using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StarGameAI.Core;

namespace StarGameAI.Memory;

public sealed class PersonaMeta
{
    public int Rev { get; set; }
    public long UpdatedAt { get; set; }
    public string By { get; set; } = "init";
    public PersonaBudget Budget { get; set; } = new();
    public Dictionary<string, string> SegmentsHash { get; set; } = new();
}

public sealed class PersonaBudget
{
    public int LimitPerDay { get; set; } = 1;
    public int UsedToday { get; set; }
    public string ResetOn { get; set; } = "";
}

public sealed class PersonaView
{
    public string Md { get; set; } = "";
    public PersonaMeta Meta { get; set; } = new();
    public List<PersonaSegment> Segments { get; set; } = new();
    public bool BudgetExhausted => Meta.Budget.UsedToday >= Meta.Budget.LimitPerDay;
}

public sealed class PersonaSaveResult
{
    public bool Ok { get; set; }
    public int Rev { get; set; }
    public string Error { get; set; } = "";
    public List<string> ChangedSegments { get; set; } = new();
}

/// <summary>
/// 自我（System Prompt.md）
/// </summary>
public sealed class Persona
{
    private const int HistoryLimit = 200;

    public static readonly Dictionary<string, (string Title, bool Editable)> Schema = new()
    {
        ["identity"] = ("身份", true),
        ["style"] = ("风格", true),
        ["interest"] = ("兴趣", true),
        ["boundaries"] = ("边界", false),
        ["output_rules"] = ("输出格式", false),
    };

    private static readonly string[] Required = { "identity", "style", "boundaries", "output_rules" };

    private static readonly Regex SegOpen = new(@"<!--\s*seg:([a-z_]+)\s*-->", RegexOptions.Compiled);
    private static readonly Regex SegClose = new(@"<!--\s*/seg:([a-z_]+)\s*-->", RegexOptions.Compiled);

    private readonly Env _env;
    private readonly DeltaLog _delta;
    private readonly object _gate = new();

    public Persona(Env env, DeltaLog delta)
    {
        _env = env;
        _delta = delta;
    }

    private string MetaFile => Path.Combine(_env.PersonaDir, "meta", "system-prompt.json");
    private string HistoryDir(int rev) => Path.Combine(_env.PersonaDir, "history", rev.ToString("D6"));

    public PersonaView Read()
    {
        lock (_gate)
        {
            var md = File.Exists(_env.PersonaFile) ? File.ReadAllText(_env.PersonaFile) : "";
            var meta = LoadMeta();

            // 全新安装时补一个 rev 0 基线，之后可以「恢复到出厂」
            var baseDir = HistoryDir(0);
            if (!Directory.Exists(baseDir) && meta.Rev == 0 && md.Length > 0)
            {
                Directory.CreateDirectory(baseDir);
                File.WriteAllText(Path.Combine(baseDir, "before.md"), md);
                File.WriteAllText(Path.Combine(baseDir, "after.md"), md);
                File.WriteAllText(Path.Combine(baseDir, "diff.txt"), "");
                File.WriteAllText(Path.Combine(baseDir, "manifest.json"),
                    JsonSerializer.Serialize(new
                    {
                        rev = 0,
                        by = "init",
                        updated_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        changed_segments = Array.Empty<string>(),
                    }, AppJson.Options));
            }

            var segments = ParseSegments(md)
                .Select(kv =>
                {
                    var (title, editable) = Schema.TryGetValue(kv.Key, out var s) ? s : (kv.Key, false);
                    if (kv.Key is "boundaries" or "output_rules") editable = false;
                    return new PersonaSegment
                    {
                        Key = kv.Key,
                        Title = title,
                        Editable = editable,
                        Hash = Hash(kv.Value),
                        Text = kv.Value,
                    };
                })
                .ToList();

            return new PersonaView { Md = md, Meta = meta, Segments = segments };
        }
    }

    public PersonaSaveResult Save(string md, string by, bool isHuman)
    {
        lock (_gate)
        {
            md ??= "";

            var (ok, error) = Validate(md);
            if (!ok) return new PersonaSaveResult { Ok = false, Error = error };

            var meta = LoadMeta();
            var old = File.Exists(_env.PersonaFile) ? File.ReadAllText(_env.PersonaFile) : "";
            var oldSeg = ParseSegments(old);
            var newSeg = ParseSegments(md);

            // 首次保存时补一个 rev 0 基线，方便「恢复到出厂」
            var baseDir = HistoryDir(0);
            if (!Directory.Exists(baseDir))
            {
                Directory.CreateDirectory(baseDir);
                File.WriteAllText(Path.Combine(baseDir, "before.md"), old);
                File.WriteAllText(Path.Combine(baseDir, "after.md"), old);
                File.WriteAllText(Path.Combine(baseDir, "diff.txt"), "");
                File.WriteAllText(Path.Combine(baseDir, "manifest.json"),
                    JsonSerializer.Serialize(new
                    {
                        rev = 0,
                        by = "init",
                        updated_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        changed_segments = Array.Empty<string>(),
                    }, AppJson.Options));
            }

            var changed = new List<string>();
            foreach (var key in Schema.Keys)
            {
                oldSeg.TryGetValue(key, out var before);
                newSeg.TryGetValue(key, out var after);
                if (Hash(before ?? "") != Hash(after ?? "")) changed.Add(key);
            }

            if (changed.Count == 0)
                return new PersonaSaveResult { Ok = true, Rev = meta.Rev, ChangedSegments = changed };

            // AI 侧不能碰只读段
            if (!isHuman && changed.Any(c => c is "boundaries" or "output_rules"))
                return new PersonaSaveResult { Ok = false, Error = "拒绝：boundaries / output_rules 只有人类可以修改" };

            // AI 侧受每日修订预算约束（上限来自 config/persona.json，0 = AI 不可写）
            if (!isHuman)
            {
                ResetBudgetIfNeeded(meta);
                if (meta.Budget.LimitPerDay <= 0)
                    return new PersonaSaveResult
                    {
                        Ok = false,
                        Error = "拒绝：当前策略不允许 AI 自行修改自我描述（revision_budget_per_day = 0）",
                    };

                if (meta.Budget.UsedToday >= meta.Budget.LimitPerDay)
                    return new PersonaSaveResult
                    {
                        Ok = false,
                        Error = $"拒绝：今日修订预算已用完（{meta.Budget.UsedToday}/{meta.Budget.LimitPerDay}），" +
                                "可在后台「人设」页调整上限",
                    };

                var gap = Math.Max(0, _env.Persona.MinMinutesBetweenRevisions);
                if (gap > 0 && meta.UpdatedAt > 0)
                {
                    var elapsedMin = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - meta.UpdatedAt) / 60.0;
                    if (elapsedMin < gap)
                        return new PersonaSaveResult
                        {
                            Ok = false,
                            Error = $"拒绝：距上次改动不足 {gap} 分钟（已过 {elapsedMin:F0} 分钟）",
                        };
                }
            }

            var rev = meta.Rev + 1;
            var snapshotDir = HistoryDir(rev);
            Directory.CreateDirectory(snapshotDir);

            // 先落快照再替换，保证任何时刻都能回到上一版
            File.WriteAllText(Path.Combine(snapshotDir, "before.md"), old);
            File.WriteAllText(Path.Combine(snapshotDir, "after.md"), md);
            File.WriteAllText(Path.Combine(snapshotDir, "diff.txt"), Diff(old, md));

            var manifest = new
            {
                rev,
                by,
                updated_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                changed_segments = changed,
            };
            File.WriteAllText(Path.Combine(snapshotDir, "manifest.json"),
                JsonSerializer.Serialize(manifest, AppJson.Options));

            Env.WriteAtomic(_env.PersonaFile, md);

            meta.Rev = rev;
            meta.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            meta.By = by;
            meta.SegmentsHash = newSeg.ToDictionary(kv => kv.Key, kv => Hash(kv.Value));
            if (!isHuman)
            {
                ResetBudgetIfNeeded(meta);
                meta.Budget.UsedToday++;
            }

            SaveMeta(meta);
            PruneHistory();

            // 变化度：记录「这一次自我被改动了多少」
            try
            {
                var (added, removed) = TextSim.LineDelta(old, md);
                _delta.Record(new DeltaRecord
                {
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Kind = "persona",
                    Target = string.Join(",", changed),
                    By = by,
                    Similarity = Math.Round(TextSim.Similarity(old, md), 3),
                    BeforeChars = old.Length,
                    AfterChars = md.Length,
                    AddedLines = added,
                    RemovedLines = removed,
                    Note = isHuman ? "人工干预" : "睡眠重写",
                });
            }
            catch { /* 记录失败不影响保存 */ }

            return new PersonaSaveResult { Ok = true, Rev = rev, ChangedSegments = changed };
        }
    }

    public List<object> History()
    {
        var root = Path.Combine(_env.PersonaDir, "history");
        if (!Directory.Exists(root)) return new List<object>();

        var items = new List<object>();
        foreach (var dir in Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.Ordinal))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var rev)) continue;

            long at = 0;
            string by = "";
            var manifest = Path.Combine(dir, "manifest.json");
            if (File.Exists(manifest))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (doc.RootElement.TryGetProperty("updated_at", out var u)) at = u.GetInt64();
                    if (doc.RootElement.TryGetProperty("by", out var b)) by = b.GetString() ?? "";
                }
                catch { /* 忽略 */ }
            }

            items.Add(new { rev, updated_at = at, by });
        }

        return items;
    }

    public string DiffFor(int rev)
    {
        var file = Path.Combine(HistoryDir(rev), "diff.txt");
        return File.Exists(file) ? File.ReadAllText(file) : "";
    }

    /// <summary>
    /// 把内容恢复到某一版（rev 0 = 出厂基线）。恢复到与当前相同的版本是无操作
    /// </summary>
    public PersonaSaveResult Rollback(int rev, string by)
    {
        var file = Path.Combine(HistoryDir(rev), "after.md");

        // rev 0 基线缺失时，退回到最早一版的 before.md（那就是出厂内容）
        if (!File.Exists(file) && rev == 0)
        {
            var root = Path.Combine(_env.PersonaDir, "history");
            if (Directory.Exists(root))
            {
                var earliest = Directory.GetDirectories(root)
                    .Select(d => (dir: d, rev: int.TryParse(Path.GetFileName(d), out var r) ? r : int.MaxValue))
                    .OrderBy(x => x.rev)
                    .FirstOrDefault();

                if (earliest.dir is not null)
                {
                    var before = Path.Combine(earliest.dir, "before.md");
                    if (File.Exists(before)) file = before;
                }
            }
        }

        if (!File.Exists(file))
            return new PersonaSaveResult { Ok = false, Error = $"rev {rev} 不存在" };

        var content = File.ReadAllText(file);
        return Save(content, by, isHuman: true);
    }

    public static Dictionary<string, string> ParseSegments(string md)
    {
        var result = new Dictionary<string, string>();
        var opens = SegOpen.Matches(md);
        foreach (Match open in opens)
        {
            var key = open.Groups[1].Value;
            var closeTag = $"<!--/seg:{key}-->";
            var closeIdx = md.IndexOf(closeTag, open.Index + open.Length, StringComparison.Ordinal);
            if (closeIdx < 0) continue;

            var text = md[(open.Index + open.Length)..closeIdx].Trim();
            result[key] = text;
        }

        return result;
    }

    private static (bool ok, string error) Validate(string md)
    {
        var opens = SegOpen.Matches(md).Select(m => m.Groups[1].Value).ToList();
        var closes = SegClose.Matches(md).Select(m => m.Groups[1].Value).ToList();

        if (opens.Count != closes.Count)
            return (false, "分段标记不完整：开标记与闭标记数量不一致");

        if (opens.Distinct().Count() != opens.Count)
            return (false, "分段标记重复：同一个段出现多次");

        foreach (var key in opens)
            if (!closes.Contains(key))
                return (false, $"分段 {key} 缺少闭标记");

        if (!opens.Distinct().OrderBy(x => x).SequenceEqual(closes.Distinct().OrderBy(x => x)))
            return (false, "分段标记不匹配");

        foreach (var key in Required)
            if (!opens.Contains(key))
                return (false, $"缺少必需的分段：{key}");

        foreach (var key in opens)
            if (!Schema.ContainsKey(key))
                return (false, $"未知分段：{key}");

        return (true, "");
    }

    private PersonaMeta LoadMeta()
    {
        PersonaMeta meta;
        try
        {
            meta = File.Exists(MetaFile)
                ? JsonSerializer.Deserialize<PersonaMeta>(File.ReadAllText(MetaFile), AppJson.Options) ?? new PersonaMeta()
                : new PersonaMeta();
        }
        catch
        {
            meta = new PersonaMeta();
        }

        ResetBudgetIfNeeded(meta);

        // 上限由 config/persona.json 控制，后台可实时改，不用重启
        var limit = Math.Max(0, _env.Persona.RevisionBudgetPerDay);
        meta.Budget.LimitPerDay = limit;

        return meta;
    }

    private void SaveMeta(PersonaMeta meta) =>
        Env.WriteAtomic(MetaFile, JsonSerializer.Serialize(meta, AppJson.Options));

    private void ResetBudgetIfNeeded(PersonaMeta meta)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (meta.Budget.ResetOn == today) return;
        meta.Budget.ResetOn = today;
        meta.Budget.UsedToday = 0;
    }

    private void PruneHistory()
    {
        var root = Path.Combine(_env.PersonaDir, "history");
        if (!Directory.Exists(root)) return;

        var dirs = Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.Ordinal).ToList();
        foreach (var dir in dirs.Skip(HistoryLimit))
        {
            try { Directory.Delete(dir, true); } catch { /* 忽略 */ }
        }
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    /// <summary>
    /// 朴素 LCS 行级 diff。行数过大时退化为整体前后对照
    /// </summary>
    public static string Diff(string before, string after)
    {
        var a = (before ?? "").Replace("\r\n", "\n").Split('\n');
        var b = (after ?? "").Replace("\r\n", "\n").Split('\n');

        if (a.Length + b.Length > 2000)
        {
            var sb0 = new StringBuilder();
            sb0.AppendLine("（内容过长，跳过逐行 diff）");
            sb0.AppendLine("--- before ---");
            sb0.AppendLine(before);
            sb0.AppendLine("--- after ---");
            sb0.AppendLine(after);
            return sb0.ToString();
        }

        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var sb = new StringBuilder();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { sb.Append(' ').AppendLine(a[x]); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { sb.Append('-').AppendLine(a[x]); x++; }
            else { sb.Append('+').AppendLine(b[y]); y++; }
        }

        while (x < a.Length) { sb.Append('-').AppendLine(a[x]); x++; }
        while (y < b.Length) { sb.Append('+').AppendLine(b[y]); y++; }

        return sb.ToString();
    }
}

using System.Text;
using System.Text.Json;
using StarGameAI.Core;

namespace StarGameAI.Memory;

public sealed class MemEntry
{
    public string Id { get; set; } = "";
    public long Ts { get; set; }
    public string Scope { get; set; } = "";
    /// <summary>
    /// identity / fact / preference / event / quote / capability / commitment
    /// </summary>
    public string Kind { get; set; } = "fact";
    public string Text { get; set; } = "";
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// 约定的生效范围（只对 kind=commitment 有意义）
    /// </summary>
    public List<string> ValidIn { get; set; } = new();

    /// <summary>
    /// hard = 越界就是错；soft = 尽量别。只有 commitment 用
    /// </summary>
    public string Strictness { get; set; } = "";

    /// <summary>
    /// 约定失效时间（0 = 不失效）
    /// </summary>
    public long ExpiresAt { get; set; }

    public int Weight { get; set; } = 50;
    /// <summary>
    /// public / member / private
    /// </summary>
    public string Level { get; set; } = "private";
    public bool Pinned { get; set; }
    public bool Tombstone { get; set; }
    public string Source { get; set; } = "";
    public double Confidence { get; set; } = 0.5;
    public int Reinforced { get; set; } = 1;
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    /// <summary>
    /// 是否已被深层画像吸收。已吸收的碎片不再参与注入，只留作溯源
    /// </summary>
    public bool Absorbed { get; set; }
    public long AbsorbedAt { get; set; }
}

/// <summary>
/// 群体里反复出现的行为模式。这是「事件画像」的结构化部分，供发言门快速匹配
/// </summary>
public sealed class GroupPattern
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "meme";
    public string Text { get; set; } = "";
    /// <summary>
    /// targeted 时的目标 QQ
    /// </summary>
    public string Target { get; set; } = "";
    public int Count { get; set; } = 1;
    public long FirstSeen { get; set; }
    public long LastSeen { get; set; }
    /// <summary>
    /// 0~1，由出现次数与时间衰减共同决定
    /// </summary>
    public double Weight { get; set; } = 0.5;
}

public sealed class GroupPatternSet
{
    public string GroupId { get; set; } = "";
    public long UpdatedAt { get; set; }
    public List<GroupPattern> Patterns { get; set; } = new();
}

/// <summary>
/// 单一真相：记忆条目按 scope 分文件、只追加
/// </summary>
public sealed class Store
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly Env _env;
    private readonly object _gate = new();

    public Store(Env env) => _env = env;

    public static readonly string[] Kinds = { "identity", "fact", "preference", "event", "quote", "capability" };
    public static readonly string[] Levels = { "public", "member", "private" };

    public static string SafeName(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value ?? "")
            sb.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_');
        var s = sb.ToString();
        return s.Length == 0 ? "default" : s.Length > 80 ? s[..80] : s;
    }

    public string FileFor(string scope) =>
        Path.Combine(_env.ShallowDir, SafeName(scope) + ".jsonl");

    public List<string> Scopes()
    {
        var dir = _env.ShallowDir;
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.GetFiles(dir, "*.jsonl")
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 把旧的 entries/ 目录迁到 shallow/（一次性）
    /// </summary>
    public int MigrateLegacyEntries()
    {
        var legacy = Path.Combine(_env.MemoryDir, "entries");
        if (!Directory.Exists(legacy)) return 0;

        var moved = 0;
        foreach (var file in Directory.GetFiles(legacy, "*.jsonl"))
        {
            var target = Path.Combine(_env.ShallowDir, Path.GetFileName(file));
            try
            {
                if (File.Exists(target))
                {
                    // 已存在同名：追加而不是覆盖
                    File.AppendAllText(target, File.ReadAllText(file));
                    File.Delete(file);
                }
                else
                {
                    File.Move(file, target);
                }

                moved++;
            }
            catch { /* 忽略单个文件失败 */ }
        }

        try
        {
            if (Directory.GetFiles(legacy).Length == 0 && Directory.GetDirectories(legacy).Length == 0)
                Directory.Delete(legacy);
        }
        catch { /* 忽略 */ }

        return moved;
    }

    public List<MemEntry> Read(string scope)
    {
        var path = FileFor(scope);
        if (!File.Exists(path)) return new List<MemEntry>();

        var list = new List<MemEntry>();
        foreach (var line in ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<MemEntry>(line, AppJson.Options);
                if (e is not null && e.Text.Length > 0) list.Add(e);
            }
            catch { /* 跳过坏行 */ }
        }

        return list;
    }

    /// <summary>
    /// 读这个场景的**归档**记忆（过保留期后被搬走的老条目）
    /// </summary>
    public List<MemEntry> ReadArchive(string scope)
    {
        var path = Path.Combine(_env.ArchiveDir, SafeName(scope) + ".jsonl");
        if (!File.Exists(path)) return new List<MemEntry>();

        var list = new List<MemEntry>();

        try
        {
            foreach (var line in ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<MemEntry>(line, AppJson.Options);
                    if (e is not null && e.Text.Length > 0) list.Add(e);
                }
                catch { /* 跳过坏行 */ }
            }
        }
        catch
        {
            return list;
        }

        return list;
    }

    public MemEntry Append(MemEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id)) entry.Id = NewId();
        if (entry.Ts == 0) entry.Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        entry.Text = entry.Text.Trim();
        entry.Weight = Math.Clamp(entry.Weight, 0, 100);
        if (entry.Text.Length > 500) entry.Text = entry.Text[..500];
        entry.Tags = entry.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Length > 16 ? t[..16] : t).Take(6).ToList();

        var line = JsonSerializer.Serialize(entry, AppJson.Options);
        lock (_gate) AppendLine(FileFor(entry.Scope), line);
        return entry;
    }

    /// <summary>
    /// 行内修改：整文件重写（原子替换）。pinned 与 tombstone 的边界由调用方把关
    /// </summary>
    public bool Patch(string scope, string id, Action<MemEntry> mutate)
    {
        lock (_gate)
        {
            var path = FileFor(scope);
            if (!File.Exists(path)) return false;

            var lines = ReadLines(path);
            var found = false;
            for (var i = 0; i < lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                MemEntry? e;
                try { e = JsonSerializer.Deserialize<MemEntry>(lines[i], AppJson.Options); }
                catch { continue; }
                if (e is null || e.Id != id) continue;

                mutate(e);
                e.Weight = Math.Clamp(e.Weight, 0, 100);
                lines[i] = JsonSerializer.Serialize(e, AppJson.Options);
                found = true;
                break;
            }

            if (!found) return false;

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, string.Join("\n", lines.Where(l => l.Length > 0)) + "\n", Utf8);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
    }

    /// <summary>
    /// 物理删除（仅后台显式调用；常规删除走 Patch 置 tombstone）
    /// </summary>
    public bool Remove(string scope, string id)
    {
        lock (_gate)
        {
            var path = FileFor(scope);
            if (!File.Exists(path)) return false;

            var lines = ReadLines(path);
            var kept = new List<string>();
            var removed = false;
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<MemEntry>(line, AppJson.Options);
                    if (e?.Id == id) { removed = true; continue; }
                }
                catch { /* 保留坏行 */ }
                kept.Add(line);
            }

            if (!removed) return false;

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, string.Join("\n", kept) + (kept.Count > 0 ? "\n" : ""), Utf8);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
    }

    public Dictionary<string, int> Stats()
    {
        var result = new Dictionary<string, int>();
        foreach (var scope in Scopes())
            result[scope] = Read(scope).Count;
        return result;
    }

    public string ProfileFile(string userId) =>
        Path.Combine(_env.DeepPersonDir, SafeName(userId) + ".md");

    public string ReadProfile(string userId)
    {
        var path = ProfileFile(userId);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public void SaveProfile(string userId, string md)
    {
        Env.WriteAtomic(ProfileFile(userId), md ?? "");
    }

    public List<string> PersonIds()
    {
        if (!Directory.Exists(_env.DeepPersonDir)) return new List<string>();
        return Directory.GetFiles(_env.DeepPersonDir, "*.md")
            .Select(p => Path.GetFileNameWithoutExtension(p) ?? "")
            .Where(x => x.Length > 0)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    public string GroupProfileFile(string groupId) =>
        Path.Combine(_env.DeepGroupDir, SafeName(groupId) + ".md");

    public string GroupPatternsFile(string groupId) =>
        Path.Combine(_env.DeepGroupDir, SafeName(groupId) + ".json");

    public string ReadGroupProfile(string groupId)
    {
        var path = GroupProfileFile(groupId);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public void SaveGroupProfile(string groupId, string md)
    {
        Env.WriteAtomic(GroupProfileFile(groupId), md ?? "");
    }

    public GroupPatternSet ReadPatterns(string groupId)
    {
        var path = GroupPatternsFile(groupId);
        if (!File.Exists(path)) return new GroupPatternSet { GroupId = groupId };
        try
        {
            var set = JsonSerializer.Deserialize<GroupPatternSet>(File.ReadAllText(path), AppJson.Config)
                      ?? new GroupPatternSet { GroupId = groupId };
            set.GroupId = groupId;
            return set;
        }
        catch
        {
            return new GroupPatternSet { GroupId = groupId };
        }
    }

    public void SavePatterns(GroupPatternSet set)
    {
        Env.WriteAtomic(GroupPatternsFile(set.GroupId),
            JsonSerializer.Serialize(set, AppJson.Config));
    }

    /// <summary>
    /// 重新计算权重并淘汰过期 pattern。这就是「事件会过期」的执行点
    /// </summary>
    public void RecomputePatterns(GroupPatternSet set)
    {
        var pol = _env.Memory;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var halfLife = Math.Max(1.0, pol.GroupHalfLifeHours) * 3600.0;

        foreach (var p in set.Patterns)
        {
            var age = Math.Max(0, now - (p.LastSeen > 0 ? p.LastSeen : p.FirstSeen));
            var recency = Math.Exp(-age / halfLife);
            // 出现次数不足「算作模式」的门槛时，权重置 0（保留计数的火种，但门控不会用它）
            var freq = p.Count < Math.Max(1, pol.GroupPatternMinCount)
                ? 0
                : Math.Min(1.0, p.Count / 5.0);
            p.Weight = Math.Round(freq * recency, 3);
        }

        var expireSeconds = Math.Max(1, pol.GroupPatternExpireDays) * 86400L;
        set.Patterns = set.Patterns
            .Where(p => p.Weight >= pol.GroupPatternMinWeight
                        || now - (p.LastSeen > 0 ? p.LastSeen : 0) < expireSeconds)
            .OrderByDescending(p => p.Weight)
            .Take(60)
            .ToList();

        set.UpdatedAt = now;
    }

    public List<string> GroupIds()
    {
        if (!Directory.Exists(_env.DeepGroupDir)) return new List<string>();
        return Directory.GetFiles(_env.DeepGroupDir, "*.md")
            .Select(p => Path.GetFileNameWithoutExtension(p) ?? "")
            .Where(x => x.Length > 0)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 把已吸收且超过保留期的浅层碎片移进 archive/，让浅层保持「短期」
    /// </summary>
    public int ArchiveExpiredShallow()
    {
        var days = Math.Max(1, _env.Memory.ShallowRetentionDays);
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - days * 86400L;
        var moved = 0;

        foreach (var scope in Scopes())
        {
            var path = FileFor(scope);
            var lines = ReadLines(path);
            if (lines.Count == 0) continue;

            var keep = new List<string>();
            var archive = new List<string>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                MemEntry? e;
                try { e = JsonSerializer.Deserialize<MemEntry>(line, AppJson.Options); }
                catch { keep.Add(line); continue; }

                if (e is not null && e.Absorbed && e.Ts > 0 && e.Ts < cutoff)
                    archive.Add(line);
                else
                    keep.Add(line);
            }

            if (archive.Count == 0) continue;

            try
            {
                Directory.CreateDirectory(_env.ArchiveDir);
                var archivePath = Path.Combine(_env.ArchiveDir, SafeName(scope) + ".jsonl");
                using (var fs = new FileStream(archivePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var sw = new StreamWriter(fs, Utf8))
                {
                    foreach (var line in archive) sw.WriteLine(line);
                }

                var tmp = path + ".tmp";
                File.WriteAllText(tmp, string.Join("\n", keep) + (keep.Count > 0 ? "\n" : ""), Utf8);
                File.Move(tmp, path, overwrite: true);
                moved += archive.Count;
            }
            catch { /* 忽略 */ }
        }

        return moved;
    }

    /// <summary>
    /// 批量把若干条浅层碎片标记为「已吸收」，只重写一次文件
    /// </summary>
    public int MarkAbsorbed(string scope, IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return 0;

        lock (_gate)
        {
            var path = FileFor(scope);
            if (!File.Exists(path)) return 0;

            var wanted = new HashSet<string>(ids, StringComparer.Ordinal);
            var lines = ReadLines(path);
            var changed = 0;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            for (var i = 0; i < lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                MemEntry? e;
                try { e = JsonSerializer.Deserialize<MemEntry>(lines[i], AppJson.Options); }
                catch { continue; }
                if (e is null || !wanted.Contains(e.Id)) continue;

                e.Absorbed = true;
                e.AbsorbedAt = now;
                lines[i] = JsonSerializer.Serialize(e, AppJson.Options);
                changed++;
            }

            if (changed == 0) return 0;

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, string.Join("\n", lines.Where(l => l.Length > 0)) + "\n", Utf8);
            File.Move(tmp, path, overwrite: true);
            return changed;
        }
    }

    public static string NewId() => "k" + Guid.NewGuid().ToString("N")[..6];

    private List<string> ReadLines(string path)
    {
        var lines = new List<string>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Utf8);
            while (sr.ReadLine() is { } line) lines.Add(line);
        }
        catch { /* 忽略 */ }

        return lines;
    }

    private static void AppendLine(string path, string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var sw = new StreamWriter(fs, Utf8);
        sw.Write(line);
        sw.Write('\n');
    }
}

using System.Text;
using System.Text.Json;
using StarGameAI.Llm;
using StarGameAI.Memory;
using StarGameAI.Pipeline;

namespace StarGameAI.Core;

/// <summary>
/// 目录里的一条：一段对话的摘要与定位锚点
/// </summary>
public sealed class RecallEntry
{
    public string Id { get; set; } = "";
    /// <summary>
    /// 这一块是哪个场景的对话
    /// </summary>
    public string Scope { get; set; } = "";
    /// <summary>
    /// 日期 yyyy-MM-dd
    /// </summary>
    public string Date { get; set; } = "";
    public long FromTs { get; set; }
    public long ToTs { get; set; }
    public int Count { get; set; }
    /// <summary>
    /// 一句话摘要。必须带具体名词，否则这一层等于没有
    /// </summary>
    public string Summary { get; set; } = "";
    /// <summary>
    /// 摘要里出现的人名、代号、约定的具体内容
    /// </summary>
    public List<string> Entities { get; set; } = new();
    public long BuiltAt { get; set; }
}

/// <summary>
/// 回溯目录：按 月 → 日 → 块 三级组织，每次只给 AI 看几十条
/// </summary>
public sealed class RecallIndex
{
    private readonly Env _env;
    private readonly object _lock = new();

    public RecallIndex(Env env) => _env = env;

    private string PathOf => Path.Combine(_env.RecallDir, "index.json");

    public List<RecallEntry> Load()
    {
        try
        {
            if (!File.Exists(PathOf)) return new List<RecallEntry>();
            var list = JsonSerializer.Deserialize<List<RecallEntry>>(
                File.ReadAllText(PathOf), AppJson.Config);
            return list ?? new List<RecallEntry>();
        }
        catch
        {
            return new List<RecallEntry>();
        }
    }

    public void Save(IEnumerable<RecallEntry> entries)
    {
        lock (_lock)
        {
            try
            {
                var list = entries
                    .OrderBy(e => e.Date, StringComparer.Ordinal)
                    .ThenBy(e => e.FromTs)
                    .ToList();

                Directory.CreateDirectory(_env.RecallDir);
                Env.WriteAtomic(PathOf, JsonSerializer.Serialize(list, AppJson.Config));
            }
            catch { /* 写不进去不影响聊天 */ }
        }
    }

    /// <summary>
    /// 写入或更新若干条目录条目。同一场景同一天同一段只留一条
    /// </summary>
    public int Upsert(IEnumerable<RecallEntry> fresh)
    {
        var all = Load();
        var added = 0;

        foreach (var e in fresh)
        {
            var hit = all.FirstOrDefault(x =>
                x.Scope == e.Scope && x.Date == e.Date && x.FromTs == e.FromTs);

            if (hit is not null)
            {
                hit.ToTs = e.ToTs;
                hit.Count = e.Count;
                hit.Summary = e.Summary;
                hit.Entities = e.Entities;
                hit.BuiltAt = e.BuiltAt;
                continue;
            }

            all.Add(e);
            added++;
        }

        Save(all);
        return added;
    }

    /// <summary>
    /// 按天数和场景取候选块
    /// </summary>
    /// <param name="days">往回看多少天</param>
    /// <param name="scope">限定场景；空表示不限（调用方应尽量避免）</param>
    public List<RecallEntry> Blocks(int days, string scope)
    {
        var since = DateOnly.FromDateTime(DateTime.Now).AddDays(-Math.Max(1, days)).ToString("yyyy-MM-dd");
        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");

        return Load()
            .Where(e => string.IsNullOrEmpty(scope) || e.Scope == scope)
            .Where(e => string.Compare(e.Date, since, StringComparison.Ordinal) >= 0)
            .Where(e => string.Compare(e.Date, today, StringComparison.Ordinal) <= 0)
            .OrderByDescending(e => e.Date, StringComparer.Ordinal)
            .ThenByDescending(e => e.FromTs)
            .ToList();
    }

    /// <summary>
    /// 把候选块排成给模型看的目录
    /// </summary>
    /// <param name="entries">候选块</param>
    /// <param name="maxChars">字数上限，超出的截断</param>
    /// <param name="truncated">是否因为超预算而截断了</param>
    public static string Render(IReadOnlyList<RecallEntry> entries, int maxChars, out bool truncated)
    {
        truncated = false;
        var sb = new StringBuilder();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var who = e.Entities.Count > 0 ? string.Join("/", e.Entities.Take(4)) : "-";
            var line = $"{i + 1}. [{e.Date}] {who}：{e.Summary}";

            if (sb.Length + line.Length > maxChars)
            {
                truncated = true;
                sb.AppendLine($"…（还有 {entries.Count - i} 条没列出）");
                break;
            }

            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 按天折叠成一行一条，用于目录超预算时先让模型挑天
    /// </summary>
    /// <param name="entries">候选块</param>
    /// <param name="maxChars">字数上限</param>
    /// <param name="truncated">是否截断</param>
    public static string RenderByDay(IReadOnlyList<RecallEntry> entries, int maxChars, out bool truncated)
    {
        truncated = false;
        var sb = new StringBuilder();

        var days = entries
            .GroupBy(e => e.Date)
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .ToList();

        for (var i = 0; i < days.Count; i++)
        {
            var g = days[i];
            var heads = string.Join("；", g.Take(3).Select(e => e.Summary).Where(s => s.Length > 0));
            if (heads.Length > 90) heads = heads[..90];

            var line = $"{i + 1}. [{g.Key}] {g.Count()} 段：{heads}";
            if (sb.Length + line.Length > maxChars)
            {
                truncated = true;
                sb.AppendLine($"…（还有 {days.Count - i} 天没列出）");
                break;
            }

            sb.AppendLine(line);
        }

        return sb.ToString();
    }
}

/// <summary>
/// 一次回溯的结果
/// </summary>
public sealed class RecallOutcome
{
    public bool Found { get; set; }
    /// <summary>
    /// 给模型看的工具结果
    /// </summary>
    public string Text { get; set; } = "";
    /// <summary>
    /// 要注入后续上下文的原文片段
    /// </summary>
    public List<RecallLine> Lines { get; set; } = new();
    public int Tokens { get; set; }
    public string Note { get; set; } = "";

    /// <summary>
    /// 记账用：这次查了什么、翻了多少、挑中哪些
    /// </summary>
    public string Query { get; set; } = "";
    public string Scope { get; set; } = "";
    public int Days { get; set; }
    public int Candidates { get; set; }
    public bool Truncated { get; set; }
    /// <summary>
    /// flat = 一次给全部目录 / byday = 先挑天再挑段
    /// </summary>
    public string Mode { get; set; } = "";
    public List<RecallEntry> Picked { get; set; } = new();
}

/// <summary>
/// 回溯记账：一次调用一行，落到 data/recall/trace.jsonl
///
/// 没有这个，出问题只能靠猜——「翻到的那段到底是我给的还是模型编的」分不出来。
/// </summary>
public sealed class RecallTrace
{
    private readonly Env _env;
    private readonly object _lock = new();

    public RecallTrace(Env env) => _env = env;

    private string PathOf => Path.Combine(_env.RecallDir, "trace.jsonl");

    public void Append(RecallOutcome o)
    {
        try
        {
            var row = new
            {
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                scope = o.Scope,
                query = o.Query.Length > 120 ? o.Query[..120] : o.Query,
                days = o.Days,
                mode = o.Mode,
                candidates = o.Candidates,
                truncated = o.Truncated,
                found = o.Found,
                note = o.Note,
                tokens = o.Tokens,
                picked = o.Picked.Select(p => new { p.Date, p.Scope, p.Count, p.Summary }),
                lines = o.Lines.Select(l => new
                {
                    label = l.Label,
                    count = l.Lines.Count,
                    ids = l.MsgIds,
                    text = string.Join(" ⏎ ", l.Lines).Trim() is var t && t.Length > 400 ? t[..400] : t,
                }),
                reply = o.Text.Length > 400 ? o.Text[..400] : o.Text,
            };

            lock (_lock)
            {
                Directory.CreateDirectory(_env.RecallDir);
                File.AppendAllText(PathOf, JsonSerializer.Serialize(row, AppJson.Options) + "\n");
            }
        }
        catch { /* 记账失败不影响主流程 */ }
    }

    /// <summary>
    /// 读最近几次
    /// </summary>
    public List<JsonElement> Recent(int limit)
    {
        var list = new List<JsonElement>();
        try
        {
            if (!File.Exists(PathOf)) return list;

            var lines = File.ReadAllLines(PathOf).Where(l => l.Trim().Length > 0).TakeLast(Math.Max(1, limit));
            foreach (var line in lines)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    list.Add(doc.RootElement.Clone());
                }
                catch { /* 跳过坏行 */ }
            }
        }
        catch { /* 读不到就当没有 */ }

        list.Reverse();
        return list;
    }
}

/// <summary>
/// 注入上下文的一小段原文
/// </summary>
public sealed class RecallLine
{
    public string Label { get; set; } = "";
    public List<string> Lines { get; set; } = new();
    public List<string> MsgIds { get; set; } = new();
}

/// <summary>
/// 回溯：AI 想不起来时，去留档里把原话找回来
///
/// 代码只管三件事——分块、锚点取原文、去重。语义判断全交给模型：
/// 挑哪一块是模型的事，代码不做任何关键词猜测。
/// </summary>
public sealed class RecallService
{
    private readonly Env _env;
    private readonly ChatLog _chatLog;
    private readonly RecallIndex _index;
    private readonly Store _store;
    private readonly IdeaStore _ideaStore;
    private readonly DeepSeekClient _llm;
    private readonly RecallTrace _trace;
    private readonly ILogger<RecallService> _log;

    public RecallService(Env env, ChatLog chatLog, RecallIndex index, Store store,
        IdeaStore ideaStore, DeepSeekClient llm, RecallTrace trace, ILogger<RecallService> log)
    {
        _env = env;
        _chatLog = chatLog;
        _index = index;
        _store = store;
        _ideaStore = ideaStore;
        _llm = llm;
        _trace = trace;
        _log = log;
    }

    /// <summary>
    /// 按用户的问话回溯一段旧对话
    /// </summary>
    /// <param name="query">用户的问话，原话最好</param>
    /// <param name="scope">只在这个会话里找，防跨会话泄露</param>
    /// <param name="days">往回看多少天</param>
    /// <param name="excludeMsgIds">已经在上下文里的消息，不再重复注入</param>
    /// <param name="ct">取消</param>
    public async Task<RecallOutcome> SearchAsync(string query, string scope, int days,
        IEnumerable<string>? excludeMsgIds, CancellationToken ct)
    {
        var outcome = await SearchCoreAsync(query, scope, days, excludeMsgIds, ct);

        outcome.Query = query ?? "";
        outcome.Scope = scope ?? "";
        outcome.Days = days;
        _trace.Append(outcome);

        return outcome;
    }

    private async Task<RecallOutcome> SearchCoreAsync(string query, string scope, int days,
        IEnumerable<string>? excludeMsgIds, CancellationToken ct)
    {
        var cfg = _env.Recall;
        var outcome = new RecallOutcome();

        if (!cfg.Enabled)
        {
            outcome.Note = "回溯没开";
            outcome.Text = "回溯功能没开。";
            return outcome;
        }

        query = (query ?? "").Trim();
        if (query.Length == 0)
        {
            outcome.Text = "缺少要查的内容。";
            return outcome;
        }

        // 1. 记忆层前置：它要是还记得，就别去翻全局记录了
        var memory = FindInMemory(query, scope);
        if (memory.Length > 0)
        {
            outcome.Found = true;
            outcome.Note = "记忆层命中";
            outcome.Text = "你的记忆里已经有这段，不用翻旧账：\n" + memory;
            return outcome;
        }

        // 2. 取目录候选
        days = Math.Clamp(days <= 0 ? 30 : days, 1, Math.Max(1, cfg.MaxLookbackDays));
        var candidates = _index.Blocks(days, scope);
        outcome.Candidates = candidates.Count;

        if (candidates.Count == 0)
        {
            outcome.Note = "目录是空的";
            outcome.Text = $"过去 {days} 天的留档目录里没有这个会话的记录。";
            return outcome;
        }

        // 3. 让模型在目录里挑（目录小就一次给完，别为了分层而分层）
        var flat = RecallIndex.Render(candidates, cfg.MaxCatalogChars, out var truncated);
        outcome.Truncated = truncated;

        var pickedBlocks = new List<RecallEntry>();

        if (!truncated)
        {
            outcome.Mode = "flat";
            var picked = await PickAsync(query, flat, candidates.Count, cfg.MaxBlocks, ct, outcome);
            foreach (var i in picked) pickedBlocks.Add(candidates[i]);
        }
        else
        {
            // 目录太大：先挑天，再在那几天里挑段
            outcome.Mode = "byday";
            var dayIndex = await PickDaysAsync(query, candidates, cfg.MaxCatalogChars, ct, outcome);
            if (dayIndex.Count == 0)
            {
                if (outcome.Note.EndsWith("失败", StringComparison.Ordinal))
                {
                    outcome.Text = "翻不了：整理日期目录那一步出错了（模型没调通）。这次没法确认有没有，别跟人说你查过了。";
                    return outcome;
                }

                outcome.Note = "目录里没挑中";
                outcome.Text = $"翻了 {candidates.Count} 段留档（折成天看的），没有跟你问的事对得上的。";
                return outcome;
            }

            var subset = dayIndex.SelectMany(i => i).ToList();
            if (subset.Count > 0)
            {
                var blockCatalog = RecallIndex.Render(subset, cfg.MaxCatalogChars, out _);
                var picked = await PickAsync(query, blockCatalog, subset.Count, cfg.MaxBlocks, ct, outcome);
                foreach (var i in picked) pickedBlocks.Add(subset[i]);
            }
        }

        if (pickedBlocks.Count == 0)
        {
            // 「没找到」和「没能去找」必须分开说。
            // 混成一句，AI 会跟用户讲「我翻过了，没有」——其实它根本没翻成
            if (outcome.Note.EndsWith("失败", StringComparison.Ordinal))
            {
                outcome.Text = "翻不了：整理目录那一步出错了（模型没调通）。这次没法确认有没有，别跟人说你查过了。";
                return outcome;
            }

            outcome.Note = outcome.Note.Length > 0 ? outcome.Note : "目录里没挑中";
            outcome.Text = $"翻了 {candidates.Count} 段留档目录，没有跟你问的事对得上的。";
            return outcome;
        }

        // 4. 按锚点取原文
        var exclude = new HashSet<string>(
            (excludeMsgIds ?? Enumerable.Empty<string>()).Where(x => x.Length > 0),
            StringComparer.Ordinal);

        var used = 0;
        foreach (var block in pickedBlocks)
        {
            if (outcome.Lines.Count >= cfg.MaxInjectEntries) break;

            var lines = ReadBlock(block, exclude, cfg.MaxInjectChars - used);
            if (lines.Lines.Count == 0) continue;

            used += lines.Lines.Sum(l => l.Length);
            outcome.Lines.Add(lines);
            outcome.Picked.Add(block);
        }

        if (outcome.Lines.Count == 0)
        {
            outcome.Note = "挑中了但原文取不到";
            outcome.Text = "目录里挑中了，但那几天的原文取不到了（可能在更早的留档里）。";
            return outcome;
        }

        outcome.Found = true;
        outcome.Note = "翻到 " + outcome.Lines.Count + " 段";
        outcome.Text = Format(outcome.Lines);

        WriteBack(pickedBlocks);
        return outcome;
    }

    /// <summary>
    /// 翻出来就记住：写进浅层碎片，标上「这是翻出来的」
    ///
    /// 用户提过一次就说明要开这个话题，下一次不该再翻一遍。
    /// 标来源是因为这不是「当时记住的」，是「事后翻出来的」——
    /// 和它自己那份片段式记忆的设定是一致的，也方便以后判断可信度。
    /// </summary>
    private void WriteBack(List<RecallEntry> blocks)
    {
        foreach (var block in blocks)
        {
            var text = (block.Summary ?? "").Trim();
            if (text.Length == 0) continue;
            if (text.Length > 80) text = text[..80];

            try
            {
                _store.Append(new MemEntry
                {
                    Scope = block.Scope,
                    Kind = "event",
                    Text = "翻旧账翻到的（" + block.Date + "）：" + text,
                    Tags = block.Entities.Take(4).ToList(),
                    Weight = 50,
                    Level = "private",
                    Source = "backtrack",
                    Confidence = 0.7,
                });
            }
            catch { /* 写不进去只影响下次还得再翻一遍 */ }
        }
    }

    /// <summary>
    /// 先在记忆层里找，命中就不用翻账
    /// </summary>
    private string FindInMemory(string query, string scope)
    {
        var sb = new StringBuilder();

        try
        {
            foreach (var e in _store.Read(scope))
            {
                if (e.Text.Length == 0) continue;
                if (!TextSim.SharesTerm(query, e.Text)) continue;
                sb.Append("- ").AppendLine(e.Text);
                if (sb.Length > 400) break;
            }
        }
        catch { /* 记忆读不到就算了 */ }

        if (sb.Length > 0) return sb.ToString().TrimEnd();

        try
        {
            // 必须按来源场景过滤：
            // 思想储存是全局的，不过滤就会把群聊里的事当成"我私下的记忆"塞进私聊
            foreach (var item in _ideaStore.Top(20, scope))
            {
                if (item.Thing.Length == 0) continue;
                if (!TextSim.SharesTerm(query, item.Thing)) continue;
                sb.Append("- ").AppendLine(item.Thing);
                if (sb.Length > 400) break;
            }
        }
        catch { /* 同上 */ }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 目录太大时先让模型挑天，返回挑中那几天的所有段
    /// </summary>
    private async Task<List<List<RecallEntry>>> PickDaysAsync(string query,
        List<RecallEntry> candidates, int budget, CancellationToken ct, RecallOutcome outcome)
    {
        var chosen = new List<List<RecallEntry>>();

        var byDay = candidates
            .GroupBy(e => e.Date)
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.ToList())
            .ToList();

        var catalog = RecallIndex.RenderByDay(candidates, budget, out _);

        try
        {
            var res = await _llm.ChatAsync("gate", new[]
            {
                ChatMsg.System(DayPickPrompt.Replace("{N}", Math.Max(1, _env.Recall.MaxBlocks).ToString())),
                ChatMsg.User($"【用户问】\n{query}\n\n【留档目录（按天）】\n{catalog}"),
            }, ct: ct);

            outcome.Tokens += res.Usage.TotalTokens;

            foreach (var i in ParseIndexes(res.Content, byDay.Count, 3))
                chosen.Add(byDay[i]);
        }
        catch (Exception ex)
        {
            _log.LogWarning("回溯挑日期失败：{Error}", ex.Message);
            outcome.Note = "挑日期失败";
        }

        return chosen;
    }

    /// <summary>
    /// 让模型在目录里挑出相关的几块
    /// </summary>
    private async Task<List<int>> PickAsync(string query, string catalog, int count,
        int maxBlocks, CancellationToken ct, RecallOutcome outcome)
    {
        var picked = new List<int>();

        try
        {
            var res = await _llm.ChatAsync("gate", new[]
            {
                ChatMsg.System(PickPrompt.Replace("{N}", Math.Max(1, maxBlocks).ToString())),
                ChatMsg.User($"【用户问】\n{query}\n\n【留档目录】\n{catalog}"),
            }, ct: ct);

            outcome.Tokens = res.Usage.TotalTokens;
            picked = ParseIndexes(res.Content, count, maxBlocks);
        }
        catch (Exception ex)
        {
            // 模型不可用时不猜。宁可说"翻不到"，也不能拿关键词乱凑一段塞回去
            _log.LogWarning("回溯挑目录失败：{Error}", ex.Message);
            outcome.Note = "挑目录失败";
        }

        return picked;
    }

    private static List<int> ParseIndexes(string raw, int count, int maxBlocks)
    {
        var list = new List<int>();

        var body = (raw ?? "").Trim();
        if (body.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = body.IndexOf('\n');
            if (nl > 0) body = body[(nl + 1)..];
            var end = body.LastIndexOf("```", StringComparison.Ordinal);
            if (end > 0) body = body[..end];
        }

        var a = body.IndexOf('[');
        var b = body.LastIndexOf(']');
        if (a < 0 || b <= a) return list;

        try
        {
            using var doc = JsonDocument.Parse(body[a..(b + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var one in doc.RootElement.EnumerateArray())
            {
                if (one.ValueKind != JsonValueKind.Number || !one.TryGetInt32(out var n)) continue;
                n--;
                if (n < 0 || n >= count || list.Contains(n)) continue;
                list.Add(n);
                if (list.Count >= Math.Max(1, maxBlocks)) break;
            }
        }
        catch { /* 解析不出来就当没挑中 */ }

        return list;
    }

    /// <summary>
    /// 按锚点把那一段原文读回来
    /// </summary>
    private RecallLine ReadBlock(RecallEntry block, HashSet<string> exclude, int budget)
    {
        var line = new RecallLine
        {
            Label = block.Date + (block.Summary.Length > 0 ? "　" + block.Summary : ""),
        };

        if (budget <= 0) return line;

        if (!DateOnly.TryParse(block.Date, out var date)) return line;

        List<LogEntry> entries;
        try
        {
            entries = _chatLog.Read(date, block.Scope)
                .Where(e => e.Ts >= block.FromTs && e.Ts <= block.ToTs)
                .Where(e => e.Text.Trim().Length > 0)
                .ToList();
        }
        catch
        {
            return line;
        }

        var used = 0;
        foreach (var e in entries)
        {
            if (e.MessageId.Length > 0 && exclude.Contains(e.MessageId)) continue;

            var who = e.IsSelf ? "我" : (e.Name.Length > 0 ? e.Name : e.UserId);
            var text = e.Text.Length > 120 ? e.Text[..120] + "…" : e.Text;
            var row = who + "：" + text;

            if (used + row.Length > budget) break;

            line.Lines.Add(row);
            if (e.MessageId.Length > 0) line.MsgIds.Add(e.MessageId);
            used += row.Length;
        }

        return line;
    }

    private static string Format(List<RecallLine> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("翻到了。这是当时的原话（不是刚刚发生的）：");
        foreach (var l in lines)
        {
            sb.Append("【").Append(l.Label).AppendLine("】");
            foreach (var row in l.Lines) sb.Append("  ").AppendLine(row);
        }

        return sb.ToString().TrimEnd();
    }

    private const string PickPrompt = """
你在帮一个 AI 从自己的旧对话留档里找回一段记忆。

给它一份「留档目录」，每条前面有编号，每条是一段旧对话的一句话摘要。
用户现在问了一句话，你要判断：**哪几条目录讲的是跟用户问话同一件事。**

规则：
- 只从编号里选，最多选 {N} 条
- 摘要里出现了用户问的那种具体东西（人名、代号、约定、事件）才选
- 只是话题相近但明显不是同一件事，不要选
- 拿不准就不要选
- 一条都对不上，输出空数组

只输出 JSON 数组，不要解释，不要代码块。例如：[3,7]
""";

    private const string DayPickPrompt = """
你在帮一个 AI 从自己的旧对话留档里找回一段记忆。

留档太多，先按天折叠了。每天一行，前面有编号。
用户现在问了一句话，你要判断：**哪一天里可能聊过这件事。**

规则：
- 只从编号里选，最多选 {N} 天
- 那一行的摘要里出现了用户问的那种具体东西（人名、代号、约定、事件）才选
- 拿不准就不要选
- 一天都对不上，输出空数组

只输出 JSON 数组，不要解释，不要代码块。例如：[2,5]
""";
}

/// <summary>
/// 已经注入过的那几段，在上下文被清空之前一直留着
///
/// 为什么要存：注入的原文如果只活一轮，下一轮 AI 又想不起来、又会去翻一次。
/// 用户提过一次就说明要开这个话题，不该让它每轮都重新找。
/// </summary>
public sealed class RecallSession
{
    private readonly Env _env;
    private readonly object _lock = new();

    public RecallSession(Env env) => _env = env;

    private string PathOf(string scope) =>
        Path.Combine(_env.RecallDir,
            "session_" + ChatLog.SafeScope(scope) + ".json");

    public List<RecallLine> Load(string scope)
    {
        try
        {
            var path = PathOf(scope);
            if (!File.Exists(path)) return new List<RecallLine>();

            var doc = JsonSerializer.Deserialize<SessionDoc>(File.ReadAllText(path), AppJson.Config);
            if (doc is null) return new List<RecallLine>();

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var ttl = Math.Max(1, _env.Recall.SessionTtlHours) * 3600L;
            if (doc.CreatedAt > 0 && now - doc.CreatedAt > ttl) return new List<RecallLine>();

            return doc.Lines ?? new List<RecallLine>();
        }
        catch
        {
            return new List<RecallLine>();
        }
    }

    /// <summary>
    /// 追加一段注入内容。同样的消息不重复写
    /// </summary>
    public void Append(string scope, IEnumerable<RecallLine> lines)
    {
        lock (_lock)
        {
            try
            {
                var current = Load(scope);
                var known = new HashSet<string>(StringComparer.Ordinal);
                foreach (var l in current) foreach (var id in l.MsgIds) known.Add(id);

                foreach (var l in lines)
                {
                    if (l.MsgIds.Count > 0 && l.MsgIds.All(known.Contains)) continue;
                    foreach (var id in l.MsgIds) known.Add(id);
                    current.Add(l);
                }

                if (current.Count > 6) current = current.Skip(current.Count - 6).ToList();

                var doc = new SessionDoc
                {
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Lines = current,
                };

                Directory.CreateDirectory(_env.RecallDir);
                Env.WriteAtomic(PathOf(scope), JsonSerializer.Serialize(doc, AppJson.Config));
            }
            catch { /* 存不下来只影响下一轮，不影响这一轮 */ }
        }
    }

    /// <summary>
    /// 拼进 system 的那一段
    /// </summary>
    public string Render(string scope)
    {
        var lines = Load(scope);
        if (lines.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("\n【你翻出来的旧对话】（是过去某天的记录，不是刚刚发生的）");
        foreach (var l in lines)
        {
            sb.Append('-').Append(l.Label).AppendLine();
            foreach (var row in l.Lines) sb.Append("  ").AppendLine(row);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 会话内已注入的所有消息 ID，用于去重
    /// </summary>
    public HashSet<string> InjectedMsgIds(string scope)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in Load(scope)) foreach (var id in l.MsgIds) set.Add(id);
        return set;
    }

    private sealed class SessionDoc
    {
        public long CreatedAt { get; set; }
        public List<RecallLine> Lines { get; set; } = new();
    }
}

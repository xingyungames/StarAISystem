using System.Text;
using System.Text.Json;
using StarGameAI.Gateway;
using StarGameAI.Llm;
using StarGameAI.Pipeline;

namespace StarGameAI.Core;

/// <summary>
/// 对话分块的观测器：判断「这条消息维护的是哪个块 / 开了新块 / 判不了」
/// </summary>
public sealed class DialogueRouter
{
    private const string JudgePrompt = """
        你在判断：这条新消息延续的是下面哪一个「话题块」。

        判断依据：
        - 是不是在接着说同一件事（同一个话题、同一群人、同一段对话）；
        - 是不是在回应某个块里的某句话（reply / 提到"这个""那个""刚才"）；
        - 是不是换了话题、换了人、隔了很久。

        **三种回答必须分清，别混：**
        - `"block":"<编号>"` —— 明确是延续某一个块；
        - `"block":"new"`     —— 明确是一个**新话题**（能说出为什么是新的）；
        - `"block":"unknown"` —— **看不出来**。只有表情/图片、没有文字线索、
                                 或者同时像好几个块的时候，就选这个。

        判不了就老实说判不了。硬选一个只会让人认错；而把"看不出来"说成"新话题"，
        会让每个不知所措的瞬间都凭空开出一个新块。

        只输出 JSON，不要解释、不要代码块：
        {"block":"编号|new|unknown","confidence":0-100,"why":"不超过20字"}
        """;

    private readonly Env _env;
    private readonly DialogueStore _store;
    private readonly Contacts _contacts;
    private readonly ChatLog _chatLog;
    private readonly DeepSeekClient _llm;
    private readonly ILogger<DialogueRouter> _log;
    private readonly object _lock = new();

    private static readonly string[] PronounHints =
    {
        "这个", "那个", "这条", "那条", "刚才", "刚说", "上面说", "它", "这事", "那事",
    };

    public DialogueRouter(Env env, DialogueStore store, Contacts contacts,
        ChatLog chatLog, DeepSeekClient llm, ILogger<DialogueRouter> log)
    {
        _env = env;
        _store = store;
        _contacts = contacts;
        _chatLog = chatLog;
        _llm = llm;
        _log = log;
    }

    private string AuditFile => Path.Combine(_env.AuditDir, "dialogue.jsonl");

    /// <summary>
    /// 对一条消息做归属判定（observe 模式下不影响行为）
    /// </summary>
    public async Task<RouteResult> ObserveAsync(ParsedMessage msg, CancellationToken ct = default)
    {
        var cfg = _env.Dialogue;
        var result = new RouteResult
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Scope = msg.Scope,
            MessageId = msg.MessageId,
            UserId = msg.UserId,
        };

        if (cfg.Mode is "off" or null) return result;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var state = _store.Load(msg.Scope);
            var (decision, blockId, confidence, by, why, candidates) =
                await DecideAsync(msg, state, cfg, ct);

            result.Decision = decision;
            result.BlockId = blockId;
            result.Confidence = Math.Round(confidence, 3);
            result.By = by;
            result.Why = why;
            result.Candidates = candidates;

            // 台账：只有非 observe 才真的改块结构；observe 只把判定写进审计，
            // 块本身也建（这样后台的甘特图才有东西看），但不参与上下文构造。
            Apply(state, msg, result, cfg);
            _store.Save(state);
        }
        catch (Exception ex)
        {
            result.Decision = "unresolved";
            result.Why = "路由失败：" + ex.Message;
            _log.LogWarning("对话分块失败：{Error}", ex.Message);
        }

        sw.Stop();
        result.Ms = (int)sw.ElapsedMilliseconds;
        WriteAudit(result);

        return result;
    }


    private async Task<(string decision, string blockId, double confidence, string by,
        string why, Dictionary<string, double> candidates)>
        DecideAsync(ParsedMessage msg, DialogueScope state, DialogueConfig cfg, CancellationToken ct)
    {
        var candidates = new Dictionary<string, double>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // ---- 规则层（零成本）
        var replyBlock = DialogueStore.FindByMessage(state, msg.InReplyTo);
        if (replyBlock is not null)
            Add(candidates, replyBlock.Id, cfg.WReplySameBlock);

        foreach (var b in state.Blocks)
        {
            var score = 0.0;

            // 同一个人接着说：最强、最便宜的信号
            if (msg.UserId.Length > 0 && b.LastUserId == msg.UserId)
                score += cfg.WSameSender;

            // 消息里出现了 @某人 / 某人名字，而这块里有这个人
            foreach (var pid in b.Participants)
            {
                var c = _contacts.ByQq(pid);
                if (c is null) continue;

                var names = new List<string> { c.Name };
                names.AddRange(c.Aliases);

                foreach (var n in names.Where(n => n.Length > 1))
                {
                    if (msg.Text.Contains("@" + n, StringComparison.Ordinal))
                        score += cfg.WAtSameEntity;
                    else if (msg.Text.Contains(n, StringComparison.Ordinal))
                        score += cfg.WEntityOverlap;
                }
            }

            // 指代前文的词
            if (PronounHints.Any(h => msg.Text.Contains(h, StringComparison.Ordinal)))
                score += cfg.WMentionPronoun;

            // 时间接近
            var gap = Math.Max(0, now - b.LastActiveAt);
            if (gap < cfg.GapSecondsNewBlock)
                score += cfg.WTimeProximity * (1 - gap / (double)Math.Max(1, cfg.GapSecondsNewBlock));

            Add(candidates, b.Id, score);
        }

        // 全新的场景 / 没有任何块
        if (state.Blocks.Count == 0)
            return ("new_block", "", 1.0, "rule", "这个会话里的第一块", candidates);

        var best = candidates.OrderByDescending(kv => kv.Value).FirstOrDefault();

        if (best.Key is not null && best.Value >= cfg.MinConfidence)
            return ("continue", best.Key, Math.Min(1.0, best.Value), "rule", "规则信号足够", candidates);

        // ---- 模糊：问模型（可关）
        if (cfg.UseModel && _llm.HasKey && state.Blocks.Count > 0)
        {
            var (blockId, conf, why) = await AskModelAsync(msg, state, candidates, ct);

            if (blockId is not null)
            {
                var ok = conf >= cfg.MinConfidence * 100;

                // "看不出"→ 直接静默，不管置信度
                if (blockId == "unknown")
                    return ("unresolved", "", conf / 100.0, "model",
                        why.Length > 0 ? why : "模型说看不出", candidates);

                // 新话题也要置信度够
                //
                // new 不受门槛约束时，每个判不了的瞬间都会凭空长出一个块
                // 某个群一天长出过 57 个块，而判不了的比例被算成了 0.7%
                if (blockId == "new")
                {
                    return ok
                        ? ("new_block", "", conf / 100.0, "model", why, candidates)
                        : ("unresolved", "", conf / 100.0, "model",
                           why.Length > 0 ? why : "模型倾向新话题但不确定", candidates);
                }

                if (ok)
                    return ("continue", blockId, conf / 100.0, "model", why, candidates);
            }

            return ("unresolved", "", conf / 100.0, "model",
                why.Length > 0 ? why : "模型也判不了", candidates);
        }

        // ---- 规则不够、又不能问模型 → 判不了（**静默**，不硬猜）
        return ("unresolved", "", best.Key is null ? 0 : best.Value, "rule",
            best.Key is null ? "没有候选块" : $"最高分 {best.Value:F2} < {cfg.MinConfidence:F2}",
            candidates);
    }

    private async Task<(string? blockId, int confidence, string why)> AskModelAsync(
        ParsedMessage msg, DialogueScope state, Dictionary<string, double> candidates, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"来源：{(msg.IsPrivate ? "私聊" : "群聊")}");
        sb.AppendLine("现在这条：" + Speaker(msg) + "：" + Trim(msg.Text, 200));
        sb.AppendLine().AppendLine("候选块：");

        var ordered = state.Blocks
            .OrderByDescending(b => b.LastActiveAt)
            .Take(6)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var b = ordered[i];
            var who = string.Join("、", b.Participants.Select(DescribeEntity).Where(x => x.Length > 0));
            sb.Append('[').Append(i).Append("] id=").Append(b.Id)
              .Append("｜state=").Append(b.State)
              .Append("｜参与者=").Append(who.Length > 0 ? who : "未知")
              .Append("｜最后活动=").Append(AgeText(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - b.LastActiveAt))
              .Append("｜消息数=").Append(b.MessageCount)
              .Append("｜最近几句：").AppendLine(RecentOf(b))
              .AppendLine();
        }

        // 给回最近几条背景，方便判断"是不是接着刚才那句"
        var scene = RecentScene(state.Scope, msg.MessageId);
        if (scene.Length > 0)
            sb.AppendLine("【刚才这里在说什么】").AppendLine(scene);

        sb.AppendLine("请判断这条落在哪个块（或 none = 新话题 / 判不了）。输出 JSON。");

        try
        {
            var res = await _llm.ChatAsync("gate", new[]
            {
                ChatMsg.System(JudgePrompt),
                ChatMsg.User(sb.ToString()),
            }, ct: ct);

            var (raw, conf, why) = Parse(res.Content);

            if (raw.Length == 0) return ("unknown", conf, why.Length > 0 ? why : "模型没给结论");

            var lowered = raw.ToLowerInvariant();

            // 三分类必须分清：看不出 ≠ 新话题。
            // 原来只有 "none" 一个兜底词，把"判不了"也当成了"开新块"。
            if (lowered is "unknown" or "none" or "?" or "-" or "null")
                return ("unknown", conf, why.Length > 0 ? why : "模型说看不出");

            if (lowered is "new" or "new_block" or "新")
                return ("new", conf, why.Length > 0 ? why : "模型判断是新话题");

            // 模型给的是编号，转回 id
            if (int.TryParse(raw, out var idx) && idx >= 0 && idx < ordered.Count)
                return (ordered[idx].Id, conf, why);

            // 万一直接给了 id
            var hit = state.Blocks.FirstOrDefault(b => b.Id == raw);
            return (hit?.Id ?? "unknown", conf, why.Length > 0 ? why : "模型给的值无法识别");
        }
        catch (LlmException ex)
        {
            _log.LogWarning("分块判定失败：{Error}", ex.Message);
            return (null, 0, "模型不可用");
        }
    }


    private void Apply(DialogueScope state, ParsedMessage msg, RouteResult result, DialogueConfig cfg)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (result.Decision == "continue")
        {
            var block = state.Blocks.FirstOrDefault(b => b.Id == result.BlockId);
            if (block is not null)
            {
                Touch(block, msg, now);
                result.BlockId = block.Id;
                return;
            }
        }

        if (result.Decision == "new_block")
        {
            var block = new DialogueBlock
            {
                Id = "b" + Guid.NewGuid().ToString("N")[..8],
                Scope = state.Scope,
                State = LooksOneToOne(msg) ? "one_to_one" : "open_topic",
                Owner = LooksOneToOne(msg) ? msg.UserId : "",
                Topic = Trim(msg.Text, 20),
                CreatedAt = now,
                LastActiveAt = now,
                MessageCount = 1,
                Participants = new List<string> { msg.UserId },
                MessageIds = new List<string> { msg.MessageId },
                LastUserId = msg.UserId,
            };

            state.Blocks.Add(block);
            result.BlockId = block.Id;

            TrimHot(state, cfg);
            return;
        }

        // unresolved：进未归属池，等事后回填
        if (!state.Unresolved.Any(u => u.MessageId == msg.MessageId))
        {
            state.Unresolved.Add(new UnresolvedMessage
            {
                MessageId = msg.MessageId,
                UserId = msg.UserId,
                Ts = now,
                Confidence = result.Confidence,
                Why = result.Why,
            });
        }
    }

    private static void Touch(DialogueBlock block, ParsedMessage msg, long now)
    {
        block.LastActiveAt = now;
        block.MessageCount++;
        block.LastUserId = msg.UserId;

        if (msg.UserId.Length > 0 && !block.Participants.Contains(msg.UserId))
            block.Participants.Add(msg.UserId);

        if (block.MessageIds.Count < 60 && !block.MessageIds.Contains(msg.MessageId))
            block.MessageIds.Add(msg.MessageId);
    }

    private static void TrimHot(DialogueScope state, DialogueConfig cfg)
    {
        // 冷却是按"最近活动"切的，不主动删块——块里有回溯价值
        var hot = state.Blocks
            .Where(b => b.State is "one_to_one" or "open_topic")
            .OrderByDescending(b => b.LastActiveAt)
            .ToList();

        for (var i = cfg.MaxHotBlocks; i < hot.Count; i++) hot[i].State = "cold";
    }

    /// <summary>
    /// 看起来是"一个人在跟我说"（@我 / 叫我名字 / 私聊）
    /// </summary>
    private bool LooksOneToOne(ParsedMessage msg)
    {
        if (msg.IsPrivate || msg.Addressed) return true;

        foreach (var n in _env.Gate.BotNames.Where(n => !string.IsNullOrWhiteSpace(n)))
            if (msg.Text.Contains(n, StringComparison.Ordinal)) return true;

        return false;
    }


    private string DescribeEntity(string userId)
    {
        var c = _contacts.ByQq(userId);
        if (c is null) return userId;
        return c.Name.Length > 0 ? c.Name + "(" + userId + ")" : userId;
    }

    private string RecentOf(DialogueBlock block)
    {
        try
        {
            var ids = block.MessageIds.TakeLast(3).ToHashSet();
            var today = DateOnly.FromDateTime(DateTime.Now);

            var lines = _chatLog.Read(today, block.Scope)
                .Concat(_chatLog.Read(today.AddDays(-1), block.Scope))
                .Where(e => ids.Contains(e.MessageId))
                .OrderBy(e => e.Ts)
                .Select(e => (e.IsSelf ? "我" : e.Name) + "：" + Trim(e.Text, 40));

            return string.Join(" / ", lines);
        }
        catch
        {
            return "";
        }
    }

    private string RecentScene(string scope, string excludeMessageId)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            return string.Join("\n", _chatLog.Read(today, scope)
                .Where(e => e.MessageId != excludeMessageId)
                .TakeLast(6)
                .Select(e => $"{(e.IsSelf ? "我" : e.Name)}：{Trim(e.Text, 60)}"));
        }
        catch
        {
            return "";
        }
    }

    private static void Add(Dictionary<string, double> map, string key, double value)
    {
        if (key.Length == 0 || value <= 0) return;
        map[key] = map.TryGetValue(key, out var old) ? old + value : value;
    }

    private static string Speaker(ParsedMessage msg) =>
        msg.Name.Length > 0 ? msg.Name : msg.UserId;

    private static string AgeText(long seconds) =>
        seconds < 60 ? Math.Max(0, seconds) + "秒前"
        : seconds < 3600 ? seconds / 60 + "分钟前"
        : seconds / 3600 + "小时前";

    private static string Trim(string? s, int n)
    {
        var v = s ?? "";
        return v.Length <= n ? v : v[..n];
    }

    private static (string raw, int confidence, string why) Parse(string text)
    {
        var body = (text ?? "").Trim();
        if (body.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = body.IndexOf('\n');
            if (nl > 0) body = body[(nl + 1)..];
            var end = body.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) body = body[..end];
        }

        var start = body.IndexOf('{');
        var stop = body.LastIndexOf('}');
        if (start < 0 || stop <= start) return ("", 0, "");

        try
        {
            using var doc = JsonDocument.Parse(body[start..(stop + 1)]);
            var root = doc.RootElement;

            var raw = "";
            if (root.TryGetProperty("block", out var b))
            {
                raw = b.ValueKind == JsonValueKind.Number
                    ? b.GetRawText()
                    : (b.GetString() ?? "").Trim();
            }

            var conf = root.TryGetProperty("confidence", out var c)
                       && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var ci) ? ci : 0;

            var why = root.TryGetProperty("why", out var w) && w.ValueKind == JsonValueKind.String
                ? (w.GetString() ?? "").Trim() : "";

            return (raw, Math.Clamp(conf, 0, 100), why);
        }
        catch
        {
            return ("", 0, "");
        }
    }

    private void WriteAudit(RouteResult r)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = r.Ts,
                scope = r.Scope,
                message_id = r.MessageId,
                user_id = r.UserId,
                decision = r.Decision,
                block_id = r.BlockId,
                confidence = r.Confidence,
                by = r.By,
                why = r.Why,
                candidates = r.Candidates,
                ms = r.Ms,
                mode = _env.Dialogue.Mode,
            }, AppJson.Options);

            lock (_lock)
            {
                Directory.CreateDirectory(_env.AuditDir);
                File.AppendAllText(AuditFile, line + "\n");
            }
        }
        catch { /* 审计失败不影响主流程 */ }
    }


    /// <summary>
    /// 读审计算指标（后台用）。unresolved_rate 是方案成立与否的第一判据
    /// </summary>
    public DialogueStats Stats(int days = 1)
    {
        var stats = new DialogueStats { WindowDays = days };

        try
        {
            if (!File.Exists(AuditFile)) return stats;

            var from = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - days * 86400L;
            var lines = File.ReadAllLines(AuditFile);
            var take = Math.Max(0, lines.Length - 5000);

            for (var i = take; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;

                try
                {
                    using var doc = JsonDocument.Parse(lines[i]);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("ts", out var tsEl) || !tsEl.TryGetInt64(out var ts)) continue;
                    if (ts < from) continue;

                    stats.Total++;

                    switch (root.TryGetProperty("decision", out var d) ? d.GetString() : "")
                    {
                        case "continue": stats.Continue++; break;
                        case "new_block": stats.NewBlock++; break;
                        default: stats.Unresolved++; break;
                    }

                    switch (root.TryGetProperty("by", out var b) ? b.GetString() : "")
                    {
                        case "rule": stats.ByRule++; break;
                        case "model": stats.ByModel++; break;
                    }
                }
                catch { /* 跳过坏行 */ }
            }
        }
        catch { /* 忽略 */ }

        return stats;
    }
}

public sealed class DialogueStats
{
    public int WindowDays { get; set; }
    public int Total { get; set; }
    public int Continue { get; set; }
    public int NewBlock { get; set; }
    public int Unresolved { get; set; }
    public int ByRule { get; set; }
    public int ByModel { get; set; }

    public double UnresolvedRate => Total == 0 ? 0 : Math.Round(Unresolved / (double)Total, 3);
    public double RuleRate => Total == 0 ? 0 : Math.Round(ByRule / (double)Total, 3);
}

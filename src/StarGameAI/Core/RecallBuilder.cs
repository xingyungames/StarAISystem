using System.Text;
using System.Text.Json;
using StarGameAI.Llm;
using StarGameAI.Pipeline;

namespace StarGameAI.Core;

/// <summary>
/// 把留档切块、写进回溯目录
///
/// 切块按「对话单元」而不是按字数：只在时间隔开很久、或者一轮完整往来之后才切。
/// 按字数切会把「几点了」和「7点了」分到两块，回溯时永远对不上。
/// </summary>
public sealed class RecallBuilder
{
    /// <summary>
    /// 隔这么久没说话，就算换了一个话题
    /// </summary>
    private const int GapSeconds = 30 * 60;

    private readonly Env _env;
    private readonly ChatLog _chatLog;
    private readonly RecallIndex _index;
    private readonly DeepSeekClient _llm;
    private readonly ILogger<RecallBuilder> _log;

    public RecallBuilder(Env env, ChatLog chatLog, RecallIndex index,
        DeepSeekClient llm, ILogger<RecallBuilder> log)
    {
        _env = env;
        _chatLog = chatLog;
        _index = index;
        _llm = llm;
        _log = log;
    }

    /// <summary>
    /// 扫一段时间的留档，切块写进目录。摘要先用代码拼，之后可以再让模型润色
    /// </summary>
    /// <param name="from">起始日期</param>
    /// <param name="to">结束日期（含）</param>
    /// <param name="onlyMissing">只处理目录里还没有的日期</param>
    public int Build(DateOnly from, DateOnly to, bool onlyMissing = true)
    {
        var existing = _index.Load();
        var have = new HashSet<string>(
            existing.Select(e => e.Scope + "@" + e.Date), StringComparer.Ordinal);

        var made = new List<RecallEntry>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var date = day.ToString("yyyy-MM-dd");

            List<LogEntry> all;
            try { all = _chatLog.Read(day); }
            catch { continue; }

            if (all.Count == 0) continue;

            foreach (var byScope in all.GroupBy(e => e.Scope))
            {
                var scope = byScope.Key;
                if (string.IsNullOrWhiteSpace(scope)) continue;
                if (onlyMissing && have.Contains(scope + "@" + date)) continue;

                foreach (var block in Split(byScope.OrderBy(e => e.Ts).ToList()))
                {
                    if (block.Count < 2) continue;

                    made.Add(new RecallEntry
                    {
                        Id = "rc" + Guid.NewGuid().ToString("N")[..8],
                        Scope = scope,
                        Date = date,
                        FromTs = block[0].Ts,
                        ToTs = block[^1].Ts,
                        Count = block.Count,
                        Summary = CheapSummary(block),
                        Entities = CheapEntities(block),
                        BuiltAt = now,
                    });
                }
            }
        }

        if (made.Count == 0) return 0;

        _index.Upsert(made);
        _log.LogInformation("回溯目录：新写 {Count} 段（{From} ~ {To}）", made.Count, from, to);
        return made.Count;
    }

    /// <summary>
    /// 按对话单元切：时间断层、或者一轮完整往来之后才断
    /// </summary>
    private static List<List<LogEntry>> Split(List<LogEntry> entries)
    {
        var blocks = new List<List<LogEntry>>();
        var current = new List<LogEntry>();

        foreach (var e in entries)
        {
            if (current.Count > 0)
            {
                var gap = e.Ts - current[^1].Ts;
                // 时间断层 → 必然换话题
                if (gap > GapSeconds)
                {
                    blocks.Add(current);
                    current = new List<LogEntry>();
                }
            }

            current.Add(e);

            // 一轮完整往来之后再考虑切（绝不在「人问」和「我答」中间切）
            if (current.Count >= 60 && e.IsSelf)
            {
                blocks.Add(current);
                current = new List<LogEntry>();
            }
        }

        if (current.Count > 0) blocks.Add(current);
        return blocks;
    }

    /// <summary>
    /// 代码版摘要：谁在场 + 开头一句 + 结尾一句
    ///
    /// 必须压成单行——目录是「一条一行」排出来的，摘要里带换行会让编号错位。
    /// </summary>
    private static string CheapSummary(List<LogEntry> block)
    {
        var first = block.FirstOrDefault(e => !e.IsSelf && e.Text.Trim().Length > 0)
                    ?? block.FirstOrDefault(e => e.Text.Trim().Length > 0);

        var last = block.LastOrDefault(e => e.Text.Trim().Length > 0);

        var head = OneLine(first?.Text, 40);
        var tail = OneLine(last?.Text, 30);

        if (head.Length == 0 && tail.Length == 0) return "";
        if (tail.Length == 0 || tail == head) return head;
        if (head.Length == 0) return tail;
        return head + "…" + tail;
    }

    /// <summary>
    /// 压成一行的短文本
    /// </summary>
    private static string OneLine(string? text, int max)
    {
        var s = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length > max ? s[..max] : s;
    }

    /// <summary>
    /// 代码版实体：在场的人 + 引号里出现的词（机械抽取，不做任何语义猜测）
    /// </summary>
    private static List<string> CheapEntities(List<LogEntry> block)
    {
        var list = new List<string>();

        foreach (var name in block
                     .Where(e => !e.IsSelf && e.Name.Trim().Length > 0)
                     .Select(e => e.Name.Trim())
                     .Distinct())
        {
            list.Add(name);
            if (list.Count >= 4) break;
        }

        foreach (var e in block)
        {
            foreach (var quoted in Quoted(e.Text))
            {
                if (list.Contains(quoted)) continue;
                list.Add(quoted);
                if (list.Count >= 6) break;
            }
        }

        return list;
    }

    private static IEnumerable<string> Quoted(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        var pairs = new (char open, char close)[] { ('「', '」'), ('『', '』'), ('“', '”'), ('"', '"') };

        foreach (var (open, close) in pairs)
        {
            var i = 0;
            while (i < text.Length)
            {
                var a = text.IndexOf(open, i);
                if (a < 0) break;
                var b = text.IndexOf(close, a + 1);
                if (b < 0 || b == a + 1) { i = a + 1; continue; }

                var word = text[(a + 1)..b].Trim();
                if (word.Length is > 0 and <= 12) yield return word;

                i = b + 1;
            }
        }
    }

    /// <summary>
    /// 让模型把代码摘要改写成一句话——**必须带具体名词**
    ///
    /// 这一层决定整个回溯能不能用：摘要磨成「聊了聊约定」，AI 读目录也挑不中。
    /// </summary>
    public async Task<int> RefineAsync(int max, CancellationToken ct)
    {
        var all = _index.Load();

        var todo = all
            .Where(e => !e.Summary.StartsWith("#", StringComparison.Ordinal))
            .OrderByDescending(e => e.Date, StringComparer.Ordinal)
            .Take(Math.Max(1, max))
            .ToList();

        if (todo.Count == 0) return 0;

        var done = 0;

        foreach (var entry in todo)
        {
            ct.ThrowIfCancellationRequested();

            if (!DateOnly.TryParse(entry.Date, out var date)) continue;

            List<LogEntry> rows;
            try
            {
                rows = _chatLog.Read(date, entry.Scope)
                    .Where(e => e.Ts >= entry.FromTs && e.Ts <= entry.ToTs)
                    .Where(e => e.Text.Trim().Length > 0)
                    .Take(40)
                    .ToList();
            }
            catch { continue; }

            if (rows.Count == 0) continue;

            var sb = new StringBuilder();
            foreach (var e in rows)
            {
                var who = e.IsSelf ? "我" : (e.Name.Length > 0 ? e.Name : e.UserId);
                var text = e.Text.Length > 80 ? e.Text[..80] : e.Text;
                sb.Append(who).Append("：").AppendLine(text);
            }

            try
            {
                var res = await _llm.ChatAsync("distill", new[]
                {
                    ChatMsg.System(RefinePrompt),
                    ChatMsg.User($"场景：{entry.Scope}\n日期：{entry.Date}\n\n对话：\n{sb}"),
                }, ct: ct);

                var (summary, entities) = ParseRefine(res.Content);
                if (summary.Length == 0) continue;

                entry.Summary = summary;
                if (entities.Count > 0) entry.Entities = entities;
                entry.BuiltAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                done++;
            }
            catch (Exception ex)
            {
                _log.LogWarning("目录摘要润色失败，保留代码摘要：{Error}", ex.Message);
                break;
            }
        }

        if (done > 0) _index.Save(all);
        return done;
    }

    private static (string summary, List<string> entities) ParseRefine(string raw)
    {
        var body = (raw ?? "").Trim();
        if (body.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = body.IndexOf('\n');
            if (nl > 0) body = body[(nl + 1)..];
            var end = body.LastIndexOf("```", StringComparison.Ordinal);
            if (end > 0) body = body[..end];
        }

        var a = body.IndexOf('{');
        var b = body.LastIndexOf('}');
        if (a < 0 || b <= a) return ("", new List<string>());

        try
        {
            using var doc = JsonDocument.Parse(body[a..(b + 1)]);
            var root = doc.RootElement;

            var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                ? (s.GetString() ?? "").Trim()
                : "";

            var entities = new List<string>();
            if (root.TryGetProperty("entities", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var one in arr.EnumerateArray())
                    if (one.ValueKind == JsonValueKind.String)
                    {
                        var v = (one.GetString() ?? "").Trim();
                        if (v.Length > 0 && v.Length <= 16) entities.Add(v);
                    }

            if (summary.Length > 60) summary = summary[..60];
            return (summary, entities);
        }
        catch
        {
            return ("", new List<string>());
        }
    }

    private const string RefinePrompt = """
你在给一段旧对话写目录卡片。以后有人问起以前的事，会先看这张卡片决定要不要翻原文。

写一句话，要求很硬：

1. **必须写出具体的东西**：人名、代号、昵称、暗号、约定的具体内容、地点、数字、事件结果。
2. 不要写「聊了聊约定」「讨论了一些事」这种没有信息量的话——那样以后永远找不到。
3. 不超过 40 字。
4. 不要评论、不要总结感受，只写「发生了什么」。

反例：和星涵聊了聊以前的约定
正例：和星涵约定暗号「诶嘿」，说好只在私下用

同时列出这条摘要里出现的关键名词（人名、代号、约定内容）作为 entities。

只输出 JSON，不要解释、不要代码块：

{"summary":"…","entities":["星涵","诶嘿"]}
""";
}

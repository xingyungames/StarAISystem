using System.Text;
using System.Text.Json;
using StarGameAI.Llm;
using StarGameAI.Memory;
using StarGameAI.Pipeline;

namespace StarGameAI.Core;

/// <summary>
/// 意图子 AI 的产出：此刻到底有没有一个具体的开口动机
/// </summary>
public sealed class SpeakIntent
{
    /// <summary>
    /// none / answer / ask / care / share / correct
    /// </summary>
    public string Kind { get; set; } = "none";
    /// <summary>
    /// 动机落在哪件具体的事上（不是抽象感受）
    /// </summary>
    public string About { get; set; } = "";
    public string Target { get; set; } = "";
    public string Why { get; set; } = "";
    /// <summary>
    /// 为什么现在还不该开口（kind=none 时填）
    /// </summary>
    public string NotYet { get; set; } = "";
    public int Confidence { get; set; }
    public int Tokens { get; set; }

    public bool HasIntent => Kind is not "" and not "none" && About.Length > 0;
}

/// <summary>
/// 意图子 AI：发散之前的一道前置判断
/// </summary>
public sealed class IntentAgent
{
    private const string Prompt = """
        你是一个只在心里做判断、不说话的自己。现在先别想说什么，只回答一个问题：
        **此刻我有没有一个具体的、说得出口的开口动机？**

        什么叫"有动机"——必须落在眼前正在发生的某件具体的事上，比如：
        - 有人在直接问我、叫我，而我还没答；
        - 有人在讨论一件我知道确切答案或确切经验的事，我能补一句真的有用的；
        - 有人说了一句需要被接住的话（在难受、在求助、在报喜），而我确实在意；
        - 有人把我之前说过的事记错了、或者把事实讲反了，该纠正一句。

        什么叫"没有"——绝大多数时候都是没有：
        - 大家在互相聊，我插进去只是打断；
        - 我不知道答案，硬猜会编；
        - 我只是"有点想说"、"想刷一下存在感"、"想把话题往我在意的事上带"；
        - 我上一轮已经说过同样的话了；
        - 现在这个话题已经被别人答完了，再说就是重复。

        **默认答案是 none。** 只有在你能指着眼前一条具体的话说出"我要回应的是这句"时，
        才给出非 none。

        输出只有一个 JSON，不要解释、不要代码块：
        {"intent":"none|answer|ask|care|share|correct",
         "about":"要回应的那件具体的事，不超过20字，kind=none 时留空",
         "target":"private:<QQ号> 或 group:<群号>",
         "why":"我为什么该开口，不超过25字",
         "confidence":0-100,
         "not_yet":"现在还不该开口的理由，不超过25字，有动机时留空"}

        confidence 参考：指不出具体那句话就不要超过 40。
        """;

    private readonly Env _env;
    private readonly DeepSeekClient _llm;
    private readonly ChatLog _chatLog;
    private readonly Identity _identity;
    private readonly ILogger<IntentAgent> _log;

    public IntentAgent(Env env, DeepSeekClient llm, ChatLog chatLog, Identity identity, ILogger<IntentAgent> log)
    {
        _env = env;
        _llm = llm;
        _chatLog = chatLog;
        _identity = identity;
        _log = log;
    }

    public async Task<SpeakIntent> ExtractAsync(
        string scope, IdeaInputs inputs, IEnumerable<string> weights,
        IEnumerable<(string text, int rounds)> matureIntents,
        string lastThought, string lastTopic, int stuckRounds, CancellationToken ct)
    {
        var cfg = _env.Idea.Intent;
        if (!cfg.Enabled) return new SpeakIntent { Kind = "skipped", NotYet = "意图层已关闭" };
        if (!_llm.HasKey) return new SpeakIntent { Kind = "skipped", NotYet = "没有可用的模型 Key" };

        var scene = Scene(scope, cfg.WindowMessages);
        if (scene.Length == 0)
            return new SpeakIntent { Kind = "none", NotYet = "这个场景今天还没有人说话" };

        var sb = new StringBuilder();

        foreach (var s in inputs.Self) sb.AppendLine(s.Replace("\n", " "));
        sb.AppendLine().AppendLine("【现在】" + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

        var roster = _identity.RosterBlock(scope, "", 5);
        if (roster.Length > 0) sb.AppendLine().Append(roster);

        sb.AppendLine().AppendLine("【刚才这里在说什么】").AppendLine(scene);

        var w = weights.ToList();
        if (w.Count > 0)
        {
            sb.AppendLine().AppendLine("【我最近更在意的事】");
            foreach (var x in w) sb.Append("- ").AppendLine(x);
        }

        // 够重但一直没做的意向：这是"开口动机"最正当的来源之一。
        //
        // 权重顶格之后就不该再加了——那时候正确的事是去做。
        // 所以把它们单独摆出来，问自己"要不要就是现在"。
        var mature = matureIntents.ToList();
        var matureMaxRounds = mature.Count == 0 ? 0 : mature.Max(x => x.rounds);
        if (mature.Count > 0)
        {
            sb.AppendLine().AppendLine("【我手上还有这些一直没做的事（已经在意到顶了）】");
            foreach (var x in mature) sb.Append("- ").AppendLine(x.text);

            // 措辞逐级硬化：惦记越久，越不该再拿没机会当借口
            //
            // 够重的意向若一直被当候选动机，就能被无限拒绝，心结永远不动
            // 按未兑现轮数换说法，到最后承认做不到、交给衰减散掉
            var tier = EscalationTier(matureMaxRounds);
            sb.AppendLine().AppendLine(tier switch
            {
                1 => "这些不是新想法，是我惦记着却没动的。不用等到完美时机——即使没想清楚，也该聊聊了。",
                2 => "**这件事你已经惦记很久了，该找个由头开口。** 不用等它自然发生。",
                3 => "**这件事真得做了，该找个由头开口。** 再拖下去它只会一直占着我的心。",
                _ => "如果现在确实没机会，那就承认做不到——别再让它一直占着我的注意力。",
            });
            sb.AppendLine();
        }

        if (inputs.Memories.Count > 0)
        {
            sb.AppendLine().AppendLine("【我记得的】");
            foreach (var m in inputs.Memories) sb.Append("- ").AppendLine(m);
        }

        if (lastThought.Length > 0)
            sb.AppendLine().AppendLine("【我上一轮的思绪】").AppendLine(lastThought);

        if (stuckRounds >= 2 && lastTopic.Length > 0)
            sb.AppendLine().AppendLine($"【提醒】我已经连续 {stuckRounds} 轮卡在「{lastTopic}」上了，" +
                                       "再想同一件事没有意义。");

        sb.AppendLine().AppendLine("请判断此刻我有没有开口动机，输出 JSON。");

        try
        {
            var res = await _llm.ChatAsync("intent", new[]
            {
                ChatMsg.System(Prompt),
                ChatMsg.User(sb.ToString()),
            }, ct: ct);

            var intent = Parse(res.Content, scope);
            intent.Tokens = res.Usage.TotalTokens;

            if (intent.Confidence < cfg.MinConfidence) 
            {
                intent.NotYet = intent.NotYet.Length > 0
                    ? intent.NotYet
                    : $"置信度 {intent.Confidence} < {cfg.MinConfidence}";
                intent.Kind = "none";
                intent.About = "";
            }

            return intent;
        }
        catch (LlmException ex)
        {
            _log.LogWarning("意图提取失败：{Error}", ex.Message);
            return new SpeakIntent { Kind = "error", NotYet = "意图提取失败：" + ex.Message };
        }
    }

    /// <summary>
    /// 措辞阶梯：按"够重但一直没兑现的轮数"返回档位（0~3）
    /// </summary>
    private int EscalationTier(int rounds)
    {
        var cfg = _env.Idea.Weight;
        if (rounds >= Math.Max(2, cfg.EscalateRound3)) return 3;
        if (rounds >= Math.Max(1, cfg.EscalateRound2)) return 2;
        if (rounds >= Math.Max(1, cfg.EscalateRound1)) return 1;
        return 0;
    }

    /// <summary>
    /// 这个场景今天最近几条真人说的话
    /// </summary>
    private string Scene(string scope, int window)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var pool = new List<LogEntry>();
            pool.AddRange(_chatLog.Read(today, scope));
            if (pool.Count < window) pool.InsertRange(0, _chatLog.Read(today.AddDays(-1), scope));

            var lines = pool
                .Where(e => !string.IsNullOrWhiteSpace(e.Text))
                .TakeLast(Math.Max(4, window))
                .Select(e => (e.IsSelf ? "我：" : (e.Name.Length > 0 ? e.Name : e.UserId) + "：")
                             + (e.Text.Length > 90 ? e.Text[..90] : e.Text));

            return string.Join("\n", lines);
        }
        catch
        {
            return "";
        }
    }

    private static SpeakIntent Parse(string raw, string fallbackTarget)
    {
        var body = (raw ?? "").Trim();
        if (body.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = body.IndexOf('\n');
            if (nl > 0) body = body[(nl + 1)..];
            var end = body.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) body = body[..end];
        }

        var start = body.IndexOf('{');
        var stop = body.LastIndexOf('}');
        if (start < 0 || stop <= start) return new SpeakIntent { Kind = "error", NotYet = "意图输出不是 JSON" };

        try
        {
            using var doc = JsonDocument.Parse(body[start..(stop + 1)]);
            var root = doc.RootElement;

            var kind = Str(root, "intent");
            if (kind.Length == 0) kind = "none";

            var about = Str(root, "about");
            var target = Str(root, "target");
            if (target.Length == 0) target = fallbackTarget;

            return new SpeakIntent
            {
                Kind = kind.Trim().ToLowerInvariant(),
                About = Cap(about, 40),
                Target = target,
                Why = Cap(Str(root, "why"), 60),
                NotYet = Cap(Str(root, "not_yet"), 60),
                Confidence = Math.Clamp(Int(root, "confidence"), 0, 100),
            };
        }
        catch
        {
            return new SpeakIntent { Kind = "error", NotYet = "意图 JSON 解析失败" };
        }
    }

    private static string Cap(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    private static string Str(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim() : "";

    private static int Int(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i : 0;
}

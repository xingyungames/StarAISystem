using System.Text;
using System.Text.Json;
using StarGameAI.Core;
using StarGameAI.Gateway;
using StarGameAI.Llm;
using StarGameAI.Memory;

namespace StarGameAI.Pipeline;

public sealed class GateDecision
{
    public bool Speak { get; set; }
    public int Score { get; set; }
    public string Reason { get; set; } = "";
    public string WhyNot { get; set; } = "";
    public string Intent { get; set; } = "ignore";
    public string Mode { get; set; } = "skip";
    public bool NeedThink { get; set; }
    public bool UsedLlm { get; set; }
    /// <summary>模型判定：这句话是不是在跟我说话（不是靠名字/问句词表猜的）。</summary>
    public bool Addressed { get; set; }
    /// <summary>命中群体模式带来的加分。</summary>
    public double EventBoost { get; set; }
    public List<string> MatchedPatterns { get; set; } = new();
    /// <summary>结果是被哪一层决定的：rule / cooldown / budget / noise / sample / llm / threshold</summary>
    public string Stage { get; set; } = "";
    public int Threshold { get; set; }
}

/// <summary>发言门：先零成本规则筛，再花一次小请求问模型要不要开口。</summary>
public sealed class Gate
{
    private sealed class ScopeState
    {
        public string Day { get; set; } = "";
        public int Spoke { get; set; }
        public int Active { get; set; }
        public int Passive { get; set; }
        public int LlmCalls { get; set; }
        public long LastSpoke { get; set; }
    }

    private const string WillingnessPrompt = """
        你在 QQ 群/私聊里扮演一个有自我的人。现在要判断两件事：
        **这句是不是在跟我说话**，以及**我该不该开口**。

        第一步，先判断"是不是在跟我说话"（addressed）：
        - 群里的 @ 只有协议层能准确给出，所以走到这一步说明没有 @ 我；
        - 别人可能直接写我的名字：「星璃，你怎么看」「星璃在吗」→ 是；
        - 也可能只是在议论我、或者教别人怎么用我：
          「末尾加上@星璃」「他刚才说的那个办法」→ 不是。这类题词表永远写不全，
          只能连上文一起看它是不是在**对谁说**；
        - 一个人自己在那儿感叹、或者在对第三个人提问 → 不是。

        第二步，判断我该不该开口（score）：
        1. 是不是在叫我、问我、需要我查东西 -> 该说；
        2. 你能不能补充真正有用的信息（而不是复读别人的话）-> 可以说；
        3. 别人正在互相聊天、你插进去只是打断 -> 不该说；
        4. 纯灌水（哈哈哈、+1、表情、打卡）-> 不该说；
        5. 你刚刚已经说过一轮了 -> 除非被点名，否则别再刷屏；
        6. 如果群里正在复读某个梗、或者在起哄某个人，而你也知道这个梗，那是可以顺着接一句的时机。

        **score 请按"主动插话的门槛"那个刻度打分**（门槛会在输入里告诉你）。
        门槛以上 = 我认为现在开口是合适的；门槛以下 = 我该闭嘴。
        不要因为"有点想说"就压线，压线的结果就是刷屏。

        同时判断这句话是否需要深度思考（need_think）：
        - 需要比较、推理、多步计算、写代码、查证 -> true
        - 寒暄、闲聊、简单确认 -> false

        输出只有一个 JSON 对象，不要解释、不要代码块：
        {"addressed":false,"score":0-100,"reason":"不超过20字","intent":"answer|add|ignore","need_think":false}

        addressed=true 表示这句话是在对我说的（我叫什么会在输入里说明）。
        """;

    private readonly Env _env;
    private readonly DeepSeekClient _llm;
    private readonly Metrics _metrics;
    private readonly ChatLog _chatLog;
    private readonly Store _store;
    private readonly Persona _persona;
    private readonly ILogger<Gate> _log;
    private readonly Dictionary<string, ScopeState> _states = new();
    private readonly object _lock = new();

    public Gate(Env env, DeepSeekClient llm, ChatLog chatLog, Store store, Persona persona,
        Metrics metrics, ILogger<Gate> log)
    {
        _env = env;
        _llm = llm;
        _chatLog = chatLog;
        _store = store;
        _persona = persona;
        _metrics = metrics;
        _log = log;
    }


    public Task<GateDecision> ConsiderAsync(ParsedMessage msg, string speaker, CancellationToken ct)
    {
        var addressed = msg.Addressed || msg.IsPrivate;
        return EvaluateAsync(msg.Scope, msg.Text, msg.MessageId, speaker, addressed, msg.IsPrivate, dry: false, ct);
    }

    /// <summary>沙盒：只算不落地，用于后台调阈值。</summary>
    public Task<GateDecision> SimulateAsync(string scope, string text, string speaker, bool addressed, CancellationToken ct)
    {
        var isPrivate = scope.StartsWith("private:", StringComparison.Ordinal);
        return EvaluateAsync(scope, text, "", speaker, addressed || isPrivate, isPrivate, dry: true, ct);
    }


    private async Task<GateDecision> EvaluateAsync(
        string scope, string text, string messageId, string speaker,
        bool addressed, bool isPrivate, bool dry, CancellationToken ct)
    {
        var cfg = Effective(scope);

        if (!cfg.Enabled)
            return new GateDecision
            {
                Speak = true, Score = 100, Reason = "主动发言已关闭，按普通回复处理",
                Mode = "passive", Stage = "disabled",
            };

        var st = StateOf(scope);

        // 真 @ 我 / 私聊 = 被动回复：一律回，不占主动额度。
        // 这是唯一还保留的零成本硬判定，因为 at 段是协议层给的，不是猜的。
        if (addressed)
        {
            return new GateDecision
            {
                Speak = true, Score = 100, Reason = "被 @ 或私聊（被动回复）",
                Intent = "answer", Mode = "passive", Addressed = true,
                NeedThink = text.Length > 60, Stage = "addressed", Threshold = 0,
            };
        }

        // 连字都没有的消息（纯表情 / 纯图片 / 空）没有判断价值。
        //
        // 注意这里**不再**用噪声词表。原来是「抹掉噪声词后剩不下两个字就算灌水」，
        // 结果把「星璃」这种两字点名判成了灌水（实盘就是这样：直接叫名字被静默）。
        // 现在只问一件事：这条消息里有没有可判断的文字。
        if (!HasJudgableText(text))
            return Skip("没有可判断的内容", "纯表情或空消息", "noise", cfg);

        if (!_llm.HasKey)
            return Skip("未配置模型", "无 API Key，保守静默", "no_key", cfg);

        // 判定调用是这一层唯一的成本，所以日调用上限才是真正的成本闸门。
        // 抽样已经去掉了：抽样是拿准确率换钱，而这里本来就该由模型逐条判断。
        if (st.LlmCalls >= cfg.MaxLlmCallsPerDay)
            return Skip("判定次数用尽", $"今日已判定 {st.LlmCalls}/{cfg.MaxLlmCallsPerDay}", "llm_quota", cfg);

        if (!dry)
        {
            st.LlmCalls++;
            _metrics.Inc("gate.llm_calls");
        }

        // 群体复读话术只当上下文交给模型，不再折算成分数。
        var (_, matched) = ComputeEventBoost(scope, text);
        var scene = RecentScene(scope, messageId);
        var patternNote = matched.Count > 0
            ? "\n群里正在被反复说的：" + string.Join("、", matched.Take(5))
            : "";

        var threshold = cfg.AmbientThreshold;

        var payload =
            // 自我描述放在最前面：放后面模型会跳过，实测就出现过"我不是星璃"这种自述。
            SelfDescribe(cfg) + "\n\n" +
            $"来源：{(isPrivate ? "私聊" : "群聊")}\n" +
            $"最近消息：\n{(scene.Length > 0 ? scene : "（暂无上下文）")}" + patternNote + "\n\n" +
            $"现在这条：{speaker}：{Trim(text, 300)}\n" +
            AtOthersNote(text, cfg) +
            $"\n主动插话的门槛是 {threshold}/100（没人叫我的时候，分数要过这条线我才会开口）。\n" +
            "请结合上面的上下文判断：这条是不是在跟我说话、我插进去有没有价值。输出 JSON。";

        GateDecision decision;
        try
        {
            var result = await _llm.ChatAsync(
                "gate",
                new[] { ChatMsg.System(WillingnessPrompt), ChatMsg.User(payload) },
                ct: ct);

            var json = ParseJson(result.Content);
            var score = Math.Clamp(IntOf(json, "score"), 0, 100);

            // 「是不是在跟我说话」交给模型判，不再靠名字位置或问句词表猜。
            // 名字只出现在正文里（别人在议论我）、或者这句话是在教别人怎么 @ 我，
            // 这些情况规则写不完，但模型连着上文一眼就能看出来。
            var toMe = BoolOf(json, "addressed");

            decision = new GateDecision
            {
                Score = score,
                Reason = Trim(StrOf(json, "reason"), 40),
                Intent = Trim(StrOf(json, "intent"), 10) is { Length: > 0 } i ? i : "ignore",
                NeedThink = BoolOf(json, "need_think"),
                UsedLlm = true,
                Addressed = toMe,
                MatchedPatterns = matched,
                Stage = toMe ? "llm_addressed" : "llm",
                Threshold = threshold,
            };

            if (toMe)
            {
                // 在跟我说话 —— 回，按被动回复算，不占主动额度、不看阈值。
                decision.Speak = true;
                decision.Mode = "passive";
                decision.Score = Math.Max(score, 80);
                decision.Intent = decision.Intent is "ignore" or "" ? "answer" : decision.Intent;
            }
            else
            {
                // 没被叫到：这才是主动插话，要过冷却、额度和阈值。
                var now = Now();

                if (st.LastSpoke > 0 && now - st.LastSpoke < cfg.CooldownSeconds)
                {
                    decision.WhyNot = $"冷却中（还剩 {cfg.CooldownSeconds - (now - st.LastSpoke)} 秒）";
                    decision.Stage = "cooldown";
                }
                else if (st.Active >= cfg.DailyBudget)
                {
                    decision.WhyNot = $"今日主动发言 {st.Active}/{cfg.DailyBudget}";
                    decision.Stage = "budget";
                }
                else if (score < threshold)
                {
                    decision.WhyNot = $"意愿 {score} < 阈值 {threshold}";
                }
                else
                {
                    decision.Speak = true;
                    decision.Mode = "active";
                }
            }
        }
        catch (LlmException ex)
        {
            // 判定失败时默认**不说话**。
            //
            // 原来是保守放行（Speak=true），但那是按"宁可多说"设计的；
            // 实盘下来代价反了：主动插嘴说错话，比沉默一次糟得多。
            // 判不成就当没动机，等下一句再说。
            _log.LogInformation("意愿判定失败，按静默处理：{Error}", ex.Message);

            if (cfg.FailOpenOnError)
                return new GateDecision
                {
                    Speak = true, Score = 55, Reason = "判定失败，保守放行",
                    Intent = "add", Mode = "active", NeedThink = false,
                    WhyNot = "llm_error", Stage = "llm_error",
                    MatchedPatterns = matched,
                };

            return new GateDecision
            {
                Speak = false, Score = 0, Reason = "判定失败，宁可不说话",
                Intent = "ignore", Mode = "skip", NeedThink = false,
                WhyNot = "判定失败，按静默处理", Stage = "llm_error",
                Threshold = cfg.AmbientThreshold,
                MatchedPatterns = matched,
            };
        }

        if (!dry) WriteAudit(scope, decision);
        return decision;
    }

    private static GateDecision Skip(string reason, string whyNot, string stage, GateConfig cfg) =>
        new()
        {
            Speak = false, Score = 0, Reason = reason, WhyNot = whyNot, Stage = stage,
            Threshold = cfg.AmbientThreshold,
        };


    /// <summary>命中群体模式时的加分。</summary>
    private (double boost, List<string> matched) ComputeEventBoost(string scope, string text)
    {
        var eb = _env.Gate.EventBoost;
        if (eb is not { Enabled: true } || eb.Scale <= 0) return (0, new List<string>());
        if (!scope.StartsWith("group:", StringComparison.Ordinal)) return (0, new List<string>());

        var gid = scope["group:".Length..];
        var query = Bigrams(text);
        if (query.Count == 0) return (0, new List<string>());

        double sum = 0;
        var matched = new List<string>();

        foreach (var p in _store.ReadPatterns(gid).Patterns)
        {
            if (p.Weight <= 0) continue;

            var pb = Bigrams(p.Text);
            if (pb.Count == 0) continue;

            var hit = pb.Count(g => query.Contains(g));
            if (hit / (double)pb.Count < 0.6) continue;

            sum += p.Weight * eb.Scale;
            matched.Add(p.Text);
        }

        var boost = matched.Count == 0 ? 0 : Math.Min(Math.Max(0, eb.Max), sum);
        return (Math.Round(boost, 1), matched);
    }


    public void NoteSpoke(string scope, bool active)
    {
        var st = StateOf(scope);
        st.Spoke++;
        if (active) st.Active++; else st.Passive++;
        st.LastSpoke = Now();
    }

    public Dictionary<string, object> Stats(string scope)
    {
        var st = StateOf(scope);
        return new Dictionary<string, object>
        {
            ["spoke"] = st.Spoke,
            ["active"] = st.Active,
            ["passive"] = st.Passive,
            ["llmCalls"] = st.LlmCalls,
            ["lastSpoke"] = st.LastSpoke,
        };
    }

    /// <summary>全局配置 + 该群的覆盖值。</summary>
    public GateConfig Effective(string scope)
    {
        var cfg = _env.Gate;
        if (!scope.StartsWith("group:", StringComparison.Ordinal)) return cfg;
        if (cfg.Groups is null) return cfg;

        var gid = scope["group:".Length..];
        if (!cfg.Groups.TryGetValue(gid, out var ov) || ov is null) return cfg;

        return new GateConfig
        {
            Enabled = ov.Enabled ?? cfg.Enabled,
            DirectThreshold = cfg.DirectThreshold,
            AmbientThreshold = ov.AmbientThreshold ?? cfg.AmbientThreshold,
            CooldownSeconds = ov.CooldownSeconds ?? cfg.CooldownSeconds,
            DailyBudget = ov.DailyBudget ?? cfg.DailyBudget,
            SampleRate = cfg.SampleRate,
            MaxLlmCallsPerDay = cfg.MaxLlmCallsPerDay,
            NoiseWords = cfg.NoiseWords,
            DirectHints = cfg.DirectHints,
            BotNames = cfg.BotNames,
            HintBonus = cfg.HintBonus,
            FailOpenOnError = cfg.FailOpenOnError,
            EventBoost = cfg.EventBoost,
            Groups = cfg.Groups,
        };
    }

    private ScopeState StateOf(string scope)
    {
        var day = DateTime.Now.ToString("yyyy-MM-dd");
        lock (_lock)
        {
            if (!_states.TryGetValue(scope, out var st) || st.Day != day)
            {
                st = new ScopeState { Day = day };
                _states[scope] = st;
            }

            return st;
        }
    }

    /// <summary>这条消息里有没有"可判断的内容"。</summary>
    private static bool HasJudgableText(string text)
    {
        var body = text ?? "";

        // 先抹掉协议占位符：[图片] [表情] [卡片] [语音] [视频] [文件] [戳一戳]…
        // 这些是 OneBot 拍平时塞进来的，本身没有语义；一整条只有它们就没有判断价值。
        var sb = new System.Text.StringBuilder(body.Length);
        var depth = 0;
        foreach (var ch in body)
        {
            if (ch == '[') { depth++; continue; }
            if (ch == ']') { if (depth > 0) depth--; continue; }
            if (depth == 0) sb.Append(ch);
        }

        foreach (var ch in sb.ToString())
        {
            if (char.IsLetterOrDigit(ch)) return true;
            if (ch >= 0x4e00 && ch <= 0x9fff) return true;
        }
        return false;
    }

    /// <summary>我是谁。</summary>
    private (string names, string identity) WhoAmI(GateConfig cfg)
    {
        var names = cfg.BotNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct()
            .ToList();

        var identity = "";

        try
        {
            var view = _persona.Read();
            var segs = Persona.ParseSegments(view.Md);

            if (segs.TryGetValue("identity", out var text) && text.Trim().Length > 0)
                identity = text.Trim().Length > 220 ? text.Trim()[..220] : text.Trim();
        }
        catch { /* 人设读不到就用配置里的名字 */ }

        // 名字没配，就从人设首句里捞一个：常见的写法是「我是星璃，……」
        if (names.Count == 0 && identity.Length > 0)
        {
            var m = System.Text.RegularExpressions.Regex.Match(identity, @"我叫?\s*([^\s，,。、；;：:（）()]{1,12})");
            if (m.Success)
            {
                var guess = m.Groups[1].Value.Trim();
                if (guess.Length > 0 && guess != "一个" && guess != "个")
                    names.Add(guess);
            }
        }

        return (string.Join("／", names), identity);
    }

    /// <summary>判定模型看到的「我是谁」原文。给后台展示用——这是那一层的地基，</summary>
    public string SelfDescription() => SelfDescribe(_env.Gate);

    /// <summary>判定模型看到的「我是谁」的一段。</summary>
    private string SelfDescribe(GateConfig cfg)
    {
        var (names, identity) = WhoAmI(cfg);

        var sb = new StringBuilder();

        if (names.Length > 0)
            sb.Append("你叫「").Append(names.Replace("／", "」，也叫「")).Append("」。");
        else
            sb.Append("你现在没有配名字，但你就是这个群里说话的那个 AI 本身。");

        if (identity.Length > 0)
            sb.Append("人设：").Append(identity);

        return sb.ToString();
    }

    /// <summary>这条消息是不是 @ 了别人。</summary>
    private string AtOthersNote(string text, GateConfig cfg)
    {
        var body = text ?? "";
        var (names, _) = WhoAmI(cfg);
        var list = names.Split('／', StringSplitOptions.RemoveEmptyEntries);

        var at = body.IndexOf('@');
        if (at < 0) return "";

        var tail = body[at..];
        if (list.Any(n => tail.StartsWith("@" + n, StringComparison.Ordinal))) return "";

        return "注意：这条消息 @ 的是别人，不是在 @ 我。\n";
    }

    private string RecentScene(string scope, string excludeMessageId)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            return string.Join("\n", _chatLog.Read(today, scope)
                .Where(e => e.MessageId != excludeMessageId)
                .TakeLast(6)
                .Select(e => $"{(e.IsSelf ? "我" : e.Name)}：{Trim(e.Text, 80)}"));
        }
        catch
        {
            return "";
        }
    }

    private void WriteAudit(string scope, GateDecision d)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = Now(),
                scope,
                speak = d.Speak,
                score = d.Score,
                addressed = d.Addressed,
                matched = d.MatchedPatterns,
                stage = d.Stage,
                reason = d.Reason,
                why_not = d.WhyNot,
                used_llm = d.UsedLlm,
            }, AppJson.Options);

            File.AppendAllText(Path.Combine(_env.AuditDir, "gate.jsonl"), line + "\n");
        }
        catch { /* 审计写失败不影响主流程 */ }
    }


    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string Trim(string? s, int n)
    {
        var v = s ?? "";
        return v.Length <= n ? v : v[..n];
    }

    private static HashSet<string> Bigrams(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in (text ?? "").ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);

        var s = sb.ToString();
        var set = new HashSet<string>();
        if (s.Length == 0) return set;
        if (s.Length == 1) { set.Add(s); return set; }
        for (var i = 0; i < s.Length - 1; i++) set.Add(s.Substring(i, 2));
        return set;
    }

    private static int IntOf(Dictionary<string, JsonElement> d, string k) =>
        d.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static string StrOf(Dictionary<string, JsonElement> d, string k) =>
        d.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool BoolOf(Dictionary<string, JsonElement> d, string k) =>
        d.TryGetValue(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    private static Dictionary<string, JsonElement> ParseJson(string text)
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
        if (start < 0) return new Dictionary<string, JsonElement>();

        var depth = 0;
        for (var i = start; i < body.Length; i++)
        {
            if (body[i] == '{') depth++;
            else if (body[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body[start..(i + 1)]);
                        return doc.RootElement.EnumerateObject()
                            .ToDictionary(p => p.Name, p => p.Value.Clone());
                    }
                    catch { return new Dictionary<string, JsonElement>(); }
                }
            }
        }

        return new Dictionary<string, JsonElement>();
    }
}

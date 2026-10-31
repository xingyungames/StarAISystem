using System.Text;
using System.Text.Json;
using StarGameAI.Llm;

namespace StarGameAI.Core;

/// <summary>
/// 一个「可以去说的地方」：私聊谁，或者在哪个群说
/// </summary>
public sealed record RouteSlot(string Channel, string Target, string Label);

/// <summary>
/// 动手之前「能不能做、在哪儿做、什么时候做」的判定结果
/// </summary>
public sealed class RouteVerdict
{
    /// <summary>
    /// 现在就能做吗。false 表示这件事要压着
    /// </summary>
    public bool Doable { get; set; }

    /// <summary>
    /// private / group / none
    /// </summary>
    public string Channel { get; set; } = "";

    /// <summary>
    /// 落到哪个号上（QQ 号或群号）
    /// </summary>
    public string Target { get; set; } = "";

    /// <summary>
    /// now / later
    /// </summary>
    public string When { get; set; } = "now";

    /// <summary>
    /// 这一下会不会把人从别的事情里拽出来
    /// </summary>
    public bool Disturb { get; set; }

    public string Why { get; set; } = "";

    public int Tokens { get; set; }

    /// <summary>
    /// 模型原始输出，排错用
    /// </summary>
    public string Raw { get; set; } = "";
}

/// <summary>
/// 「怎么发、什么时候发」子 AI。
///
/// 为什么单独一层：收敛只知道「这件事要办」，不知道「在哪儿办、这个点办会不会打扰」。
/// 实盘后果是同一件事在群里说一次、私聊又说一次，以及半夜发消息问人醒着吗。
/// 这一层只回答四件事：能不能做、在哪儿做、现在还是以后、会不会打扰。
/// </summary>
public sealed class RouteAgent
{
    private readonly DeepSeekClient _llm;
    private readonly ILogger<RouteAgent> _log;

    public RouteAgent(DeepSeekClient llm, ILogger<RouteAgent> log)
    {
        _llm = llm;
        _log = log;
    }

    /// <summary>
    /// 判一次落点与时机
    /// </summary>
    public async Task<RouteVerdict> DecideAsync(
        string action,
        string origin,
        string currentTarget,
        IReadOnlyList<RouteSlot> slots,
        string nowText,
        string presenceText,
        CancellationToken ct)
    {
        var verdict = new RouteVerdict { Doable = true, Target = currentTarget };

        // 没地方可选就没得判，照原样往下走
        if (slots.Count == 0)
        {
            verdict.Channel = ChannelOf(currentTarget);
            verdict.Why = "没有可选的落点";
            return verdict;
        }

        var sb = new StringBuilder();
        sb.AppendLine("【想做的一件事】").AppendLine(action.Trim()).AppendLine();

        sb.AppendLine("【这件事从哪儿来的】")
          .AppendLine(origin == "self"
              ? "我自己的意象。不需要谁邀请，也没有人在等我回答。"
              : "对方给的需求。是有人在等我接住什么。")
          .AppendLine();

        sb.AppendLine("【现在】").AppendLine(nowText);
        if (presenceText.Trim().Length > 0) sb.AppendLine(presenceText);
        sb.AppendLine("我正对着的落点是：" + Describe(currentTarget, slots)).AppendLine();

        sb.AppendLine("【我现在能说话的地方】（只能从这里挑）");
        foreach (var s in slots) sb.Append("- ").AppendLine(s.Label);
        sb.AppendLine();

        sb.AppendLine("【要判的四件事】");
        sb.AppendLine("1. 落点：这件事适合私聊说，还是适合在群里说？只从上面列表挑一个。");
        sb.AppendLine("2. 时机：现在这个点做合适吗？夜深了、对方在上课、刚说过要睡了，都不合适。");
        sb.AppendLine("3. 打扰：这一条发出去，会不会把人从别的事情里拽出来？会就先别发。");
        sb.AppendLine("4. 来源不同，判法不同：");
        sb.AppendLine("   - 来源是「对方的需求」：必须对方现在接得住才算能做。对方睡了、在忙、不在场，就是 later。");
        sb.AppendLine("   - 来源是「我自己的意象」：不需要谁同意，但**仍然要挑地方和时机**——");
        sb.AppendLine("     自己的事可以现在就做，只是别在群里刷存在感，别挑半夜。");
        sb.AppendLine();

        sb.AppendLine("【输出】只输出这个 JSON，不要解释，不要代码块：");
        sb.AppendLine("{\"doable\":true,\"channel\":\"private\",\"target\":\"10001\",\"when\":\"now\",\"disturb\":false,\"why\":\"一句话说清\"}");
        sb.AppendLine("channel 只能是 private 或 group 或 none；target 只能填上面列表里的号；when 只能是 now 或 later。");

        LlmResult res;
        try
        {
            res = await _llm.ChatAsync("route", new[]
            {
                ChatMsg.System(RoutePrompt),
                ChatMsg.User(sb.ToString()),
            }, ct: ct);
        }
        catch (Exception ex)
        {
            // 判不了不等于不能做——照原样放行，让后面那七道闸去兜底
            _log.LogWarning("落点判定失败，按原目标放行：{Error}", ex.Message);
            verdict.Channel = ChannelOf(currentTarget);
            verdict.Why = "落点判定失败";
            return verdict;
        }

        verdict.Tokens = res.Usage.TotalTokens;
        verdict.Raw = res.Content ?? "";

        if (!TryParse(verdict.Raw, out var doable, out var channel, out var target, out var when, out var disturb, out var why))
        {
            _log.LogWarning("落点判定解析不出来，按原目标放行：{Raw}", Trim(verdict.Raw, 160));
            verdict.Channel = ChannelOf(currentTarget);
            verdict.Why = "落点判定解析失败";
            return verdict;
        }

        verdict.Doable = doable;
        verdict.When = when;
        verdict.Disturb = disturb;
        verdict.Why = why;

        // 模型给的 target 必须落回真实存在的号上。
        // 不校验的话它会把「星涵」当号写回来，然后发送层拿着一个名字去找人。
        var slot = MatchSlot(target, channel, slots);
        if (slot is null)
        {
            slot = MatchSlot(currentTarget, "", slots);
        }

        if (slot is null)
        {
            verdict.Doable = false;
            verdict.Channel = "none";
            verdict.Why = why.Length > 0 ? why : "没有能落到的地方";
            return verdict;
        }

        verdict.Channel = slot.Channel;
        verdict.Target = slot.Target;
        if (!doable) verdict.When = "later";

        return verdict;
    }

    private static RouteSlot? MatchSlot(string want, string channel, IReadOnlyList<RouteSlot> slots)
    {
        var w = (want ?? "").Trim();
        if (w.Length == 0) return null;

        // 模型只会写号，两种写法都认：2861379621 和 private:2861379621
        var bare = BareOf(w);

        foreach (var s in slots)
            if (string.Equals(s.Target, w, StringComparison.Ordinal)) return s;

        // 同一通道下先按裸号精确比，避免把 QQ 和群号当成同一个
        if (channel.Length > 0)
            foreach (var s in slots)
                if (string.Equals(s.Channel, channel, StringComparison.Ordinal) && BareOf(s.Target) == bare) return s;

        foreach (var s in slots)
            if (BareOf(s.Target) == bare) return s;

        foreach (var s in slots)
            if (s.Label.Contains(w, StringComparison.Ordinal) || w.Contains(s.Label, StringComparison.Ordinal)) return s;

        if (channel.Length > 0)
            foreach (var s in slots)
                if (string.Equals(s.Channel, channel, StringComparison.Ordinal)) return s;

        return null;
    }

    /// <summary>去掉 private: / group: 前缀，只留号</summary>
    private static string BareOf(string target)
    {
        var t = (target ?? "").Trim();
        var i = t.IndexOf(':');
        return i >= 0 ? t[(i + 1)..] : t;
    }

    private static bool TryParse(
        string raw, out bool doable, out string channel, out string target,
        out string when, out bool disturb, out string why)
    {
        doable = true;
        channel = "";
        target = "";
        when = "now";
        disturb = false;
        why = "";

        var body = StripFences(raw);
        var a = body.IndexOf('{');
        var b = body.LastIndexOf('}');
        if (a < 0 || b <= a) return false;

        try
        {
            using var doc = JsonDocument.Parse(body[a..(b + 1)]);
            var root = doc.RootElement;

            if (root.TryGetProperty("doable", out var d))
                doable = Str(d) is "true" or "是" or "yes";

            if (root.TryGetProperty("channel", out var c)) channel = Str(c).ToLowerInvariant();
            if (root.TryGetProperty("target", out var t)) target = Str(t);
            if (root.TryGetProperty("when", out var w)) when = Str(w).ToLowerInvariant() is "later" or "later." ? "later" : "now";
            if (root.TryGetProperty("disturb", out var dd))
                disturb = Str(dd) is "true" or "是" or "yes";
            if (root.TryGetProperty("why", out var y)) why = Str(y);

            if (channel is not ("private" or "group" or "none")) channel = "";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Str(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => el.GetRawText(),
            _ => "",
        };

    private static string ChannelOf(string target) =>
        target.StartsWith("group:", StringComparison.Ordinal) ? "group" : "private";

    private static string Describe(string target, IReadOnlyList<RouteSlot> slots)
    {
        var t = (target ?? "").Trim();
        foreach (var s in slots)
            if (string.Equals(s.Target, t, StringComparison.Ordinal)) return s.Label;
        return t.Length > 0 ? t : "（不固定）";
    }

    private static string StripFences(string text)
    {
        var body = (text ?? "").Trim();
        if (!body.StartsWith("```", StringComparison.Ordinal)) return body;

        var nl = body.IndexOf('\n');
        if (nl > 0) body = body[(nl + 1)..];
        var end = body.LastIndexOf("```", StringComparison.Ordinal);
        if (end > 0) body = body[..end];
        return body.Trim();
    }

    private static string Trim(string s, int n) => s is null ? "" : s.Length > n ? s[..n] : s;

    private const string RoutePrompt = """
你是一个会自己找事做的 AI 的"出发前检查"。

你只回答一件事：**这件事，现在，在哪儿做合适。**

要判的四件事：
1. 落点——私聊某个人，还是在某个群里说。只能从它给你的列表里挑。
2. 时机——现在这个钟点、这个人的状态，做这件事合不合适。
3. 打扰——这一下会不会把人从别的事情里拽出来。
4. 来源——"我自己的意象"不需要谁同意，但仍然要挑地方和时机；"对方的需求"必须对方此刻接得住。

判法上的几条硬要求：
- 挑不出合适的地方，就说 doable=false，并写清为什么。不要为了做完而硬塞一个落点。
- 深夜（对方那边 23:00 之后到早上）、对方明确说过要睡了或在忙，一律 later。
- 同一件事别人已经问过、你已经答过，不要再挑地方重说一遍。
- target 必须填列表里的纯数字号，不要填名字。

只输出 JSON，不要解释，不要代码块：

{"doable":true,"channel":"private","target":"10001","when":"now","disturb":false,"why":"一句话说清"}
""";
}

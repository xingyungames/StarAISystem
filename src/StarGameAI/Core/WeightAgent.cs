using System.Text;
using System.Text.Json;
using StarGameAI.Llm;

namespace StarGameAI.Core;

/// <summary>
/// 加权意向的合并子 AI
/// </summary>
public sealed class WeightAgent
{
    private const string Prompt = """
        你在维护自己的「加权意向」——我打算对谁做什么。

        每条意向有一个落点（kind + target），落点下面是一串**独立的事项**，
        一件事一条，每条不超过 40 字、是下次能照着做的动作。

        **不要重写全部内容，只告诉我这次要加什么、要删什么。**
        现有事项已经好好在那儿了，没有理由的不要动它。

        输出只有一个 JSON，不要解释、不要代码块：
        {"add":["要新增的事项"],"remove":["要删掉的事项的原文（和现有条目一字不差）"]}

        规则：
        1. `add` 里只写**新出现的**事。已经存在的意思不要再写一遍，换了说法也不行；
        2. 如果新请求和现有某条是**同一件事、只是说得更准**，就在 remove 里写旧的那条、
           在 add 里写新的那条（这叫改写，不是新增两条）；
        3. 一个落点最多 6 条事项。超了就先 remove 最不重要的；
        4. 事项要写成动作，不是愿望。反例「多留意语气」；正例「她再喊困时先问一句是不是想有人陪」；
        5. 不要新增落点——落点由系统按 kind + target 定，你只维护事项；
        6. 什么都不用改就两个数组都空。
        """;

    private readonly Env _env;
    private readonly DeepSeekClient _llm;
    private readonly IdeaChainStore _chains;
    private readonly ILogger<WeightAgent> _log;

    public WeightAgent(Env env, DeepSeekClient llm, IdeaChainStore chains, ILogger<WeightAgent> log)
    {
        _env = env;
        _llm = llm;
        _chains = chains;
        _log = log;
    }

    /// <summary>
    /// 一次合并的结果，给后台和日志看
    /// </summary>
    public sealed class MergeOutcome
    {
        public string Kind { get; set; } = "group";
        public string Target { get; set; } = "";
        public string Topic { get; set; } = "";
        public string Origin { get; set; } = "context";
        public List<string> Items { get; set; } = new();
        public double Weight { get; set; }
        public bool Rejected { get; set; }
        public int Added { get; set; }
        public int Removed { get; set; }
        public string Note { get; set; } = "";
    }

    /// <summary>
    /// 把收敛得到的意向并进对应的桶
    /// </summary>
    public async Task<MergeOutcome> MergeAsync(
        string kind, string target, string who, string topic, string origin, IEnumerable<string> items, CancellationToken ct)
    {
        var outcome = new MergeOutcome { Kind = kind, Target = target, Topic = topic };

        var incoming = items
            .Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0)
            .Select(x => x.Length > 60 ? x[..60] : x)
            .Distinct()
            .ToList();

        if (incoming.Count == 0) return outcome;

        var existing = _chains.LoadWeights();
        var key = IdeaWeight.KeyOf(kind, target, topic);
        existing.TryGetValue(key, out var bucket);

        // ---- 到顶就拒绝，不做无意义的加法
        var cap = Math.Clamp(_env.Idea.Weight.ActThreshold, 0.3, 1.0);
        if (bucket is not null && bucket.Weight >= cap)
        {
            outcome.Rejected = true;
            outcome.Weight = bucket.Weight;
            outcome.Items = bucket.Items.Select(i => i.Text).ToList();
            outcome.Note = "这条已经够重了，该去做，不该再加";
            return outcome;
        }

        // 没有存量 → 不需要花一次调用
        if (bucket is null || bucket.Items.Count == 0)
        {
            var (w, rejected) = _chains.Boost(kind, target, who, topic, origin, incoming, 0.2);
            outcome.Rejected = rejected;
            outcome.Weight = w?.Weight ?? 0;
            outcome.Items = w?.Items.Select(i => i.Text).ToList() ?? new List<string>();
            outcome.Added = outcome.Items.Count;
            return outcome;
        }

        // ---- 有存量 → 让模型只给增量
        if (!_llm.HasKey)
        {
            var (w, rejected) = _chains.Boost(kind, target, who, topic, origin, incoming, 0.2);
            outcome.Rejected = rejected;
            outcome.Weight = w?.Weight ?? 0;
            outcome.Items = w?.Items.Select(i => i.Text).ToList() ?? new List<string>();
            outcome.Note = "模型不可用，按朴素方式追加";
            return outcome;
        }

        var sb = new StringBuilder();
        sb.Append("【落点】").Append(IdeaWeight.TitleOf(kind, who, target)).AppendLine();
        sb.AppendLine();
        sb.AppendLine("【现有事项】");

        for (var i = 0; i < bucket.Items.Count; i++)
            sb.Append('[').Append(i + 1).Append("] ").AppendLine(bucket.Items[i].Text);

        sb.AppendLine();
        sb.AppendLine("【这一轮新想到的】");
        foreach (var x in incoming) sb.Append("- ").AppendLine(x);

        sb.AppendLine();
        sb.AppendLine("请给出 add / remove 增量，输出 JSON。");

        string[] toAdd = Array.Empty<string>();
        string[] toRemove = Array.Empty<string>();

        try
        {
            var res = await _llm.ChatAsync("weight", new[]
            {
                ChatMsg.System(Prompt),
                ChatMsg.User(sb.ToString()),
            }, ct: ct);

            (toAdd, toRemove) = ParseDelta(res.Content, bucket.Items.Select(i => i.Text).ToList(), incoming);
        }
        catch (Exception ex)
        {
            _log.LogWarning("加权合并失败，退回朴素追加：{Error}", ex.Message);
            toAdd = incoming.ToArray();
        }

        // ---- 应用增量（复用上一次的结果，不做整体重写）
        var working = _chains.LoadWeights();
        working.TryGetValue(key, out var hit);
        if (hit is null)
        {
            var (w, rejected) = _chains.Boost(kind, target, who, topic, origin, toAdd, 0.2);
            outcome.Rejected = rejected;
            outcome.Weight = w?.Weight ?? 0;
            outcome.Items = w?.Items.Select(i => i.Text).ToList() ?? new List<string>();
            return outcome;
        }

        var before = hit.Items.Count;

        foreach (var rm in toRemove)
        {
            var idx = hit.Items.FindIndex(x =>
                string.Equals(x.Text, rm, StringComparison.Ordinal)
                || Core.TextSim.Similarity(x.Text, rm) >= 0.85);
            if (idx >= 0)
            {
                hit.Items.RemoveAt(idx);
                outcome.Removed++;
            }
        }

        IdeaChainStore.MergeItems(hit, toAdd);
        outcome.Added = hit.Items.Count - (before - outcome.Removed);

        hit.Weight = Math.Min(cap, hit.Weight + 0.2);
        hit.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        hit.Reinforced++;
        hit.Title = IdeaWeight.TitleWithTopic(hit.Kind, hit.Who, hit.Target, hit.Topic);
        if (hit.Who.Length == 0 && who.Length > 0) hit.Who = who;

        var save = _chains.LoadWeights();
        save[key] = hit;
        _chains.SaveWeights(save.Values.ToList());

        outcome.Weight = hit.Weight;
        outcome.Items = hit.Items.Select(i => i.Text).ToList();
        return outcome;
    }

    private static (string[] add, string[] remove) ParseDelta(
        string raw, List<string> current, List<string> incoming)
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
        if (start < 0 || stop <= start) return (incoming.ToArray(), Array.Empty<string>());

        try
        {
            using var doc = JsonDocument.Parse(body[start..(stop + 1)]);
            var root = doc.RootElement;

            var add = ReadArray(root, "add");
            var remove = ReadArray(root, "remove");

            // 模型什么都没说 → 用本轮的原文兜底（宁可多一条，不要丢）
            if (add.Count == 0 && remove.Count == 0) add = incoming;

            // remove 只接受确实存在的原文，防止模型删错
            remove = remove
                .Where(r => current.Any(c =>
                    string.Equals(c, r, StringComparison.Ordinal)
                    || Core.TextSim.Similarity(c, r) >= 0.85))
                .ToList();

            return (add.ToArray(), remove.ToArray());
        }
        catch
        {
            return (incoming.ToArray(), Array.Empty<string>());
        }
    }

    private static List<string> ReadArray(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => (x.GetString() ?? "").Trim())
            .Where(x => x.Length > 0)
            .Select(x => x.Length > 60 ? x[..60] : x)
            .Take(8)
            .ToList();
    }

    /// <summary>
    /// 用模型认出「同一件事的不同说法」并归并，返回并掉了几个。
    ///
    /// 字符统计只能捞到明显同形的（「X的梦想答案」和「梦想答案空着」），
    /// 换个说法就捞不到（「梦想那题没答」跟它们一个二元组都不重合）。
    /// 这是语义判断，交给模型一次最省——一个落点最多也就几行。
    /// </summary>
    public async Task<int> MergeSameTopicsAsync(CancellationToken ct)
    {
        var all = _chains.LoadWeights();

        var groups = all.Values
            .GroupBy(w => IdeaWeight.KeyOf(w.Kind, w.Target))
            .Where(g => g.Count() > 1)
            .ToList();

        if (groups.Count == 0) return 0;

        // 先把各落点的同义组都问出来，最后一次性归并。
        // 边问边并不行：每归并一次桶的 id 和顺序都会变，后面的下标全对不上。
        var same = new List<List<string>>();

        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();

            var list = g.ToList();
            if (list.Count < 2) continue;

            var sb = new StringBuilder();
            for (var i = 0; i < list.Count; i++) sb.AppendLine((i + 1) + ". " + list[i].Topic);

            string raw;
            try
            {
                var res = await _llm.ChatAsync("weight", new[]
                {
                    ChatMsg.System(TopicMergePrompt),
                    ChatMsg.User(sb.ToString()),
                }, ct: ct);

                raw = res.Content ?? "";
            }
            catch (Exception ex)
            {
                _log.LogWarning("按措辞归并失败，跳过这个落点：{Error}", ex.Message);
                continue;
            }

            foreach (var cluster in ParseGroups(raw, list.Count))
                same.Add(cluster.Select(i => list[i].Topic).ToList());
        }

        if (same.Count == 0) return 0;
        return _chains.MergeGroups(same);
    }

    /// <summary>
    /// 解析 [[1,3,4]] 这种下标分组，越界的丢掉
    /// </summary>
    private static List<List<int>> ParseGroups(string raw, int count)
    {
        var groups = new List<List<int>>();

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
        if (a < 0 || b <= a) return groups;

        try
        {
            using var doc = JsonDocument.Parse(body[a..(b + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return groups;

            foreach (var one in doc.RootElement.EnumerateArray())
            {
                if (one.ValueKind != JsonValueKind.Array) continue;

                var list = new List<int>();
                foreach (var n in one.EnumerateArray())
                {
                    if (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var idx)) continue;
                    idx--;                                   // 模型按 1 开始数
                    if (idx >= 0 && idx < count && !list.Contains(idx)) list.Add(idx);
                }

                if (list.Count >= 2) groups.Add(list);
            }
        }
        catch { /* 解析不出来就当没归并 */ }

        return groups;
    }

    private const string TopicMergePrompt = """
下面是一个落点下攒着的几条「在意的事」，每条前面有编号。

请告诉我：**哪些编号说的是同一件事。**

规则：
- 同一件事被写成几个不同的短语（「梦想的答案」「梦想那题没答」「欠她的回答」），
  它们就是同一件事，归到一组。
- 只是同一个话题下的不同动作，不算同一件事，不要归到一起。
- 拿不准就不要归——宁可漏归，也不要把两件事并成一件。

只输出 JSON 数组，每组一个数组，组内放编号。没有要并的就输出 []：

[[1,3,4]]
""";
}

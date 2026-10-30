using System.Text.Json;
using System.Text.Json.Nodes;

namespace StarGameAI.Core;

public sealed class IdeaChainVersion
{
    public int V { get; set; }
    public long Ts { get; set; }
    public List<string> Thoughts { get; set; } = new();
    /// <summary>这一版落到哪个主题上（短标签），用于判断是不是又在想同一件事。</summary>
    public string Topic { get; set; } = "";
}

//坑：固定喂同一份材料时，模型每轮都会得出同一个结论
/// <summary>一个对象的「思想链」：每一轮发散接着上一轮的想法往下走。</summary>
/// <summary>一件已经了结的事：主题、什么时候了结的、当时说了什么。</summary>
public sealed class ResolvedTopic
{
    public string Topic { get; set; } = "";
    public long Ts { get; set; }
    public string Target { get; set; } = "";
    public string Did { get; set; } = "";
    /// <summary>是否真的送达了。送达则永久不再提；没送达只是占位，允许冷却后重试。</summary>
    public bool Delivered { get; set; }
}

public sealed class IdeaChain
{
    public string Target { get; set; } = "";
    public int Version { get; set; }
    public long UpdatedAt { get; set; }
    public List<string> Thoughts { get; set; } = new();
    public string Thought { get; set; } = "";
    public string Topic { get; set; } = "";
    /// <summary>最近几版的主题标签（旧 → 新，最多 6 个）。用来判断"是不是又在想同一件事"。</summary>
    public List<string> Topics { get; set; } = new();
    /// <summary>上次消费到的输入水位（排除自己说的话）。没有新消息就不再想一遍。</summary>
    public long LastInputTs { get; set; }
    public List<IdeaChainVersion> History { get; set; } = new();
    /// <summary>已经了结的事。了结之后不再主动重提，思绪也不再带着它往下滚。</summary>
    public List<ResolvedTopic> Resolved { get; set; } = new();

    /// <summary>
    /// 停放中的事：判定现在做不了（时机不对、落点不合适），先压着不做，
    /// 也不进下一轮的任何输入。到期再拿出来。
    /// </summary>
    public List<HeldTopic> Held { get; set; } = new();
}

/// <summary>停放中的一件事。</summary>
public sealed class HeldTopic
{
    public string Topic { get; set; } = "";

    /// <summary>什么时候判它做不了的。</summary>
    public long Ts { get; set; }

    /// <summary>为什么做不了（时机不对 / 会打扰 / 没合适的地方说）。</summary>
    public string Why { get; set; } = "";

    /// <summary>到这个时刻之后可以重新拿出来。</summary>
    public long Until { get; set; }
}

/// <summary>全局了结表里的一条：这件事在某个落点已经做完了。</summary>
public sealed class DoneTopic
{
    public string Topic { get; set; } = "";
    public string Target { get; set; } = "";
    public long Ts { get; set; }
}

/// <summary>加权意向里的一条「要做的事」。</summary>
public sealed class WeightItem
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public long Ts { get; set; }

    public static string NewId() => "i" + Guid.NewGuid().ToString("N")[..6];
}

//坑：权重只加不减会卡心结——顶在 1.00 永远不动，却从不变成动作
/// <summary>一条「加权意向」：我打算对谁做什么。</summary>
public sealed class IdeaWeight
{
    public string Id { get; set; } = "";
    /// <summary>group / private / poke / file</summary>
    public string Kind { get; set; } = "group";
    /// <summary>群号或 QQ 号</summary>
    public string Target { get; set; } = "";
    /// <summary>对方的名字。**只从联系人目录取，不信模型给的名字**。</summary>
    public string Who { get; set; } = "";
    /// <summary>枚举出来的落点说明：在群里发言 / 和某某私聊</summary>
    public string Title { get; set; } = "";

    //坑：只按落点归并时，「梦想」会同时出现在 5 个桶里，每个都被顶到 1.00
    /// <summary>主题键（≤8 字），如「梦想」「喊困」「关系试探」。</summary>
    public string Topic { get; set; } = "";

    /// <summary>这条意向来自哪一路发散。</summary>
    public string Origin { get; set; } = "context";

    /// <summary>要做的事，一件事一条。</summary>
    public List<WeightItem> Items { get; set; } = new();

    /// <summary>够重了却一直没被兑现的轮数。</summary>
    public int MatureRounds { get; set; }

    /// <summary>当前权重（已经扣过衰减的**现值**，不是历史峰值）。</summary>
    public double Weight { get; set; }
    /// <summary>已经做过 / 已经在回复里体现过了。置位的条目会被移出列表。</summary>
    public bool Done { get; set; }
    public long CreatedAt { get; set; }
    /// <summary>最后一次加权或扣衰减的时间点（衰减的锚）。</summary>
    public long UpdatedAt { get; set; }
    /// <summary>被加权过几次。</summary>
    public int Reinforced { get; set; }

    /// <summary>落点标题：只由 Kind + Who + Target 推出来，不给模型自由发挥的空间。</summary>
    public static string TitleOf(string kind, string who, string target)
    {
        var k = (kind ?? "").Trim().ToLowerInvariant();
        var w = (who ?? "").Trim();
        var t = (target ?? "").Trim();

        // 群聊要把群号带上——否则后台里好几个"在群里发言"长得一模一样
        var groupTag = k == "group" && t.Length > 0 ? "·" + t : "";

        if (k == "private")
        {
            var name = w.Length > 0 ? w : (t.Length > 0 ? t : "某人");
            return "和" + name + "私聊";
        }

        if (k == "poke") return "戳一下" + (w.Length > 0 ? w : "某人") + "（提醒他）";
        if (k == "file") return "给" + (w.Length > 0 ? w : "某人") + "发文件";

        return "在群里发言" + groupTag;
    }

    /// <summary>带主题的标题：和星涵私聊·梦想 / 在群里发言·100001·喊困。</summary>
    public static string TitleWithTopic(string kind, string who, string target, string topic)
    {
        var baseTitle = TitleOf(kind, who, target);
        var t = (topic ?? "").Trim();
        return t.Length == 0 ? baseTitle : baseTitle + "·" + t;
    }

    /// <summary>累加用的键：落在同一个地方、同一个主题的意向合成一条。</summary>
    public static string KeyOf(string kind, string target) =>
        (kind ?? "").Trim().ToLowerInvariant() + "|" + (target ?? "").Trim();

    /// <summary>带主题的键。<c>(实体, 主题)</c> 才是真正的归并单位。</summary>
    public static string KeyOf(string kind, string target, string topic)
    {
        var b = KeyOf(kind, target);
        var t = (topic ?? "").Trim();
        return t.Length == 0 ? b : b + "|" + t;
    }

    /// <summary>落盘/内存里用的键。</summary>
    public static string StoreKey(IdeaWeight w) =>
        (w.Target ?? "").Trim().Length > 0
            ? IdeaWeight.KeyOf(w.Kind ?? "", w.Target ?? "", w.Topic ?? "")
            : "legacy|" + (w.Id.Length > 0 ? w.Id : Math.Abs((FirstText(w) ?? "").GetHashCode()).ToString("x8"));

    public static string FirstText(IdeaWeight w) =>
        w.Items.Count > 0 ? w.Items[0].Text : "";

    /// <summary>把老格式里被「；」缝起来的流水句拆成一件事一条。</summary>
    public static List<string> SplitLegacyBody(string body)
    {
        var result = new List<string>();
        var text = (body ?? "").Trim();
        if (text.Length == 0) return result;

        foreach (var raw in text.Split(new[] { '；', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim().Trim('。', '，', ',');
            if (part.Length == 0) continue;

            // 太长仍然要截，但至少一件一条、不再缝别人
            result.Add(part.Length > 60 ? part[..60] : part);
            if (result.Count >= 6) break;
        }

        if (result.Count == 0 && text.Length > 0)
            result.Add(text.Length > 60 ? text[..60] : text);

        return result;
    }

    /// <summary>整桶的展示文本（后台/日志用）。</summary>
    public string Summary() =>
        Items.Count == 0 ? "" : string.Join("；", Items.Select(i => i.Text));
}

/// <summary>思想链与「加权意向」的存储。</summary>
public sealed class IdeaChainStore
{
    private readonly Env _env;
    private readonly object _lock = new();

    public IdeaChainStore(Env env) => _env = env;

    private string ChainDir => Path.Combine(_env.IdeaDir, "chain");
    private string WeightsFile => Path.Combine(_env.IdeaDir, "weights.json");

    private string ChainFile(string target) =>
        Path.Combine(ChainDir, Memory.Store.SafeName(target) + ".json");

    public IdeaChain Load(string target)
    {
        var path = ChainFile(target);
        if (!File.Exists(path)) return new IdeaChain { Target = target };

        try
        {
            var chain = JsonSerializer.Deserialize<IdeaChain>(File.ReadAllText(path), AppJson.Config)
                        ?? new IdeaChain { Target = target };
            chain.Target = target;
            chain.Resolved ??= new List<ResolvedTopic>();
            chain.Topics ??= new List<string>();
            chain.Held ??= new List<HeldTopic>();
            return chain;
        }
        catch
        {
            return new IdeaChain { Target = target };
        }
    }

    public void Save(IdeaChain chain)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(ChainDir);
                Env.WriteAtomic(ChainFile(chain.Target), JsonSerializer.Serialize(chain, AppJson.Config));
            }
            catch { /* 忽略 */ }
        }
    }

    /// <summary>清掉正在带的念头，让下一轮从空白开始想。target 为空就清全部。</summary>
    public List<string> ResetCarry(string target)
    {
        var done = new List<string>();

        var one = (target ?? "").Trim();
        if (one.Length > 0)
        {
            var chain = Load(one);
            if (!Clear(chain)) return done;

            Save(chain);
            done.Add(chain.Target);
            return done;
        }

        // 全清时**不能**拿文件名当 target 再 Load：文件名是 SafeName 之后的，
        // "group:1098659169" 里的冒号已经被压成下划线，这么一 Load 就会把文件里
        // 本来正确的 target 改写成下划线版，落点从此对不上。所以直接读文件本身。
        try
        {
            if (!Directory.Exists(ChainDir)) return done;

            foreach (var p in Directory.GetFiles(ChainDir, "*.json"))
            {
                IdeaChain? chain;
                try
                {
                    chain = JsonSerializer.Deserialize<IdeaChain>(File.ReadAllText(p), AppJson.Config);
                }
                catch { continue; }

                if (chain is null || !Clear(chain)) continue;

                Env.WriteAtomic(p, JsonSerializer.Serialize(chain, AppJson.Config));
                done.Add(chain.Target.Length > 0 ? chain.Target : Path.GetFileNameWithoutExtension(p));
            }
        }
        catch { /* 目录读不到就当没有 */ }

        return done;
    }

    /// <summary>清掉一条链正在带的念头。真的清到了返回 true。</summary>
    private static bool Clear(IdeaChain chain)
    {
        chain.Thoughts ??= new List<string>();
        chain.Topics ??= new List<string>();

        var carrying = chain.Thought.Trim().Length > 0
                       || chain.Thoughts.Count > 0
                       || chain.Topic.Trim().Length > 0
                       || chain.Topics.Count > 0;
        if (!carrying) return false;

        // 只清「正在带的念头」，不动 Resolved——那是防重复提案的另一道锁。
        // 两个一起清掉的话，下一轮就会把刚了结的事原样再提一遍，等于没重置。
        chain.Thought = "";
        chain.Thoughts.Clear();
        chain.Topic = "";
        chain.Topics.Clear();
        chain.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return true;
    }

    // -- 停放 ---------------------------------------------------------

    /// <summary>把一件事停下来：现在做不了，压着，到期再说。</summary>
    public void Hold(IdeaChain chain, string topic, string why, int minutes)
    {
        var t = (topic ?? "").Trim();
        if (t.Length == 0) return;

        chain.Held ??= new List<HeldTopic>();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var until = now + Math.Max(1, minutes) * 60L;

        var hit = chain.Held.FirstOrDefault(h => SameTopic(h.Topic, t));
        if (hit is not null)
        {
            hit.Ts = now;
            hit.Until = until;
            hit.Why = why;
        }
        else
        {
            chain.Held.Add(new HeldTopic
            {
                Topic = t.Length > 24 ? t[..24] : t,
                Ts = now,
                Why = why,
                Until = until,
            });
        }

        if (chain.Held.Count > 20) chain.Held = chain.Held.Skip(chain.Held.Count - 20).ToList();

        // 停下来就要**把承载的念头一起清掉**，否则下一轮读到「上一版思绪」还是它，
        // 等于没停。这是"做不了就先不入下一轮"的关键一半。
        chain.Thought = "";
        chain.Thoughts.Clear();
        chain.Topics.RemoveAll(x => SameTopic(x, t));
        if (SameTopic(chain.Topic, t)) chain.Topic = "";

        chain.UpdatedAt = now;
        Save(chain);
    }

    /// <summary>这件事现在还在停放期里吗（顺带清掉过期的）。</summary>
    public bool IsHeld(IdeaChain chain, string topic)
    {
        var t = (topic ?? "").Trim();
        if (t.Length == 0 || chain.Held is null || chain.Held.Count == 0) return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        chain.Held.RemoveAll(h => h.Until <= now);

        return chain.Held.Any(h => SameTopic(h.Topic, t));
    }

    /// <summary>还在停放期里的事（给上下文展示与过滤用）。</summary>
    public List<string> HeldTopics(IdeaChain chain)
    {
        if (chain.Held is null || chain.Held.Count == 0) return new List<string>();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        chain.Held.RemoveAll(h => h.Until <= now);
        return chain.Held.Select(h => h.Topic).ToList();
    }

    // -- 全局了结表 ---------------------------------------------------

    private string DoneFile => Path.Combine(_env.IdeaDir, "done.json");

    /// <summary>
    /// 读全局了结表。
    ///
    /// 为什么要全局：实盘里同一件事在群里说了一次、私聊又说了一次。
    /// 每条思想链各管各的 resolved，A 落点做完了 B 落点根本不知道，
    /// 于是换个地方又提一遍。这张表是跨落点的唯一真相。
    /// </summary>
    public List<DoneTopic> LoadDone()
    {
        try
        {
            if (!File.Exists(DoneFile)) return new List<DoneTopic>();
            var list = JsonSerializer.Deserialize<List<DoneTopic>>(File.ReadAllText(DoneFile), AppJson.Config);
            return list ?? new List<DoneTopic>();
        }
        catch { return new List<DoneTopic>(); }
    }

    /// <summary>记一件事已经做完（全局，跨落点生效）。</summary>
    public void AddDone(string topic, string target)
    {
        var t = (topic ?? "").Trim();
        if (t.Length == 0) return;

        lock (_lock)
        {
            try
            {
                var list = LoadDone();
                var short26 = t.Length > 24 ? t[..24] : t;

                if (list.Any(d => SameTopic(d.Topic, short26))) return;

                list.Insert(0, new DoneTopic
                {
                    Topic = short26,
                    Target = target,
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                });

                if (list.Count > 60) list = list.Take(60).ToList();

                Directory.CreateDirectory(_env.IdeaDir);
                Env.WriteAtomic(DoneFile, JsonSerializer.Serialize(list, AppJson.Config));
            }
            catch { /* 写不进去不影响主流程 */ }
        }
    }

    /// <summary>这件事是不是已经在别处做完了。</summary>
    public bool DoneBefore(string topic)
    {
        var t = (topic ?? "").Trim();
        if (t.Length == 0) return false;
        return LoadDone().Any(d => SameTopic(d.Topic, t));
    }

    /// <summary>清空全局了结表。清完这些事就又能被重新想起来——只在确实要重来时用。</summary>
    public int ClearDone()
    {
        lock (_lock)
        {
            try
            {
                var n = LoadDone().Count;
                if (File.Exists(DoneFile)) File.Delete(DoneFile);
                return n;
            }
            catch { return 0; }
        }
    }

    /// <summary>两个主题标签是不是同一件事。</summary>
    public static bool SameTopic(string a, string b)
    {
        var na = TextSim.Normalize(a);
        var nb = TextSim.Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        if (na == nb) return true;
        if (na.Contains(nb, StringComparison.Ordinal) || nb.Contains(na, StringComparison.Ordinal)) return true;
        if (TextSim.Similarity(na, nb) >= 0.5) return true;

        // 主题标签很短，几乎每个二元组都是关键词，所以「包含度」比「相似度」更抗改写。
        //
        // 实盘:梦想那件事被拆成四个桶——欠星涵的梦想答案 / 没答的梦想 / 梦想答案空着 /
        // 梦想那题没答。两两算 Jaccard 只有 0.2~0.3，一个都并不上；
        // 但按「短的那边有多少被长的那边盖住」算，其中三对都到 0.5 以上，
        // 连起来就是一个簇，四个桶归成一个。
        return Containment(na, nb) >= 0.5;
    }

    /// <summary>短的那串二元组有多少比例出现在长的那串里。</summary>
    private static double Containment(string a, string b)
    {
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        if (shorter.Length < 2) return 0;

        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < longer.Length; i++) set.Add(longer.Substring(i, 2));

        var total = 0;
        var hit = 0;
        for (var i = 0; i + 1 < shorter.Length; i++)
        {
            total++;
            if (set.Contains(shorter.Substring(i, 2))) hit++;
        }

        return total == 0 ? 0 : (double)hit / total;
    }

    /// <summary>
    /// 把同一件事的不同措辞并成一个桶，返回并掉了几个。
    ///
    /// 为什么必须先做这一步：「从储存发散」成立的前提是储存里每条都是一件事。
    /// 同一个落点下四个措辞各成一个桶，发散每次都读到四行同一件事，只能绕着它转——
    /// 这不是输入材料不够，是同一件事被数了四遍。
    /// </summary>
    public int MergeTopics()
    {
        var all = LoadWeights();
        if (all.Count < 2) return 0;

        var merged = 0;

        foreach (var group in all.Values
                     .GroupBy(w => IdeaWeight.KeyOf(w.Kind, w.Target))
                     .Where(g => g.Count() > 1)
                     .ToList())
        {
            var list = group.ToList();
            var parent = Enumerable.Range(0, list.Count).ToArray();

            int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
            void Union(int x, int y)
            {
                x = Find(x);
                y = Find(y);
                if (x != y) parent[y] = x;
            }

            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                    if (SameTopic(list[i].Topic, list[j].Topic)) Union(i, j);

            foreach (var cluster in Enumerable.Range(0, list.Count).GroupBy(Find))
            {
                var members = cluster.Select(i => list[i]).ToList();
                if (members.Count < 2) continue;

                merged += MergeInto(all, members);
            }
        }

        if (merged > 0) SaveWeights(all.Values.ToList());
        return merged;
    }

    /// <summary>按主题名分组归并（模型给出同义分组之后调用）。</summary>
    public int MergeGroups(IEnumerable<IEnumerable<string>> groups)
    {
        var all = LoadWeights();
        if (all.Count < 2) return 0;

        var merged = 0;

        foreach (var g in groups)
        {
            var topics = g.ToList();
            if (topics.Count < 2) continue;

            var members = all.Values.Where(w => topics.Contains(w.Topic, StringComparer.Ordinal)).ToList();
            if (members.Count < 2) continue;

            merged += MergeInto(all, members);
        }

        if (merged > 0) SaveWeights(all.Values.ToList());
        return merged;
    }

    /// <summary>
    /// 把若干桶并进最重的那条，返回并掉了几个。这几个必须已经判定是同一件事。
    ///
    /// 只改传进来的这份字典，不落盘——落盘由调用方在**全部合并做完之后统一做一次**。
    /// 这里以前自己 Load 一遍再 Save，结果是：留下的是外面那份快照（还带着被并掉的桶），
    /// 而被并掉的桶在另一份字典里被删掉后又没写进去。于是每调一次，主桶权重再加一遍，
    /// 桶却一个都没少。
    /// </summary>
    private static int MergeInto(Dictionary<string, IdeaWeight> all, IReadOnlyList<IdeaWeight> members)
    {
        if (members.Count < 2) return 0;

        // 留最重的一条当主桶，其余并进去。主桶的 id 不变，页面上不会突然换条目。
        var keep = members
            .OrderByDescending(w => w.Weight)
            .ThenByDescending(w => w.UpdatedAt)
            .First();

        var rest = members.Where(w => !ReferenceEquals(w, keep)).ToList();

        // 权重取和但压在天花板下：几个桶是各自被强化的，合起来才反映真实在意程度；
        // 但不能直接冲到「够重」线上，否则刚归并完就变成一个谁也做不动的死结。
        keep.Weight = Math.Min(0.9, members.Sum(w => w.Weight));
        keep.MatureRounds = members.Max(w => w.MatureRounds);
        keep.Reinforced += rest.Sum(w => w.Reinforced);
        keep.UpdatedAt = members.Max(w => w.UpdatedAt);
        keep.Done = members.All(w => w.Done);

        MergeItems(keep, rest.SelectMany(w => w.Items.Select(i => i.Text)));
        keep.Title = IdeaWeight.TitleWithTopic(keep.Kind, keep.Who, keep.Target, keep.Topic);

        foreach (var r in rest) all.Remove(IdeaWeight.StoreKey(r));

        return rest.Count;
    }

    // -- 加权意向 -----------------------------------------------------

    /// <summary>读出所有加权意向，并**在这一步扣掉衰减、清掉遗忘的**。</summary>
    public Dictionary<string, IdeaWeight> LoadWeights()
    {
        try
        {
            if (!File.Exists(WeightsFile)) return new Dictionary<string, IdeaWeight>();

            var node = JsonNode.Parse(File.ReadAllText(WeightsFile));
            if (node is not JsonObject obj) return new Dictionary<string, IdeaWeight>();

            var cfg = _env.Idea.Weight;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var halfLifeDays = Math.Max(0.5, cfg.HalfLifeDays);
            var forgetBelow = Math.Clamp(cfg.ForgetBelow, 0.0, 0.5);

            var map = new Dictionary<string, IdeaWeight>(StringComparer.Ordinal);
            var dirty = false;

            foreach (var (key, value) in obj)
            {
                if (value is not JsonObject o) continue;

                IdeaWeight? w = null;

                if (o.ContainsKey("items"))
                {
                    w = o.Deserialize<IdeaWeight>(AppJson.Config);
                }
                else if (o.ContainsKey("body"))
                {
                    // 上一版：单个 body 字段
                    var body = o["body"]?.GetValue<string>() ?? "";
                    if (body.Length == 0) continue;

                    w = new IdeaWeight
                    {
                        Id = o["id"]?.GetValue<string>() ?? "",
                        Kind = o["kind"]?.GetValue<string>() ?? "group",
                        Target = o["target"]?.GetValue<string>() ?? "",
                        Who = o["who"]?.GetValue<string>() ?? "",
                        Weight = o["weight"]?.GetValue<double>() ?? 0,
                        Done = o["done"]?.GetValue<bool>() ?? false,
                        CreatedAt = o["created_at"]?.GetValue<long>() ?? 0,
                        UpdatedAt = o["updated_at"]?.GetValue<long>() ?? 0,
                    };
                    w.Items.Add(new WeightItem { Id = WeightItem.NewId(), Text = body, Ts = w.UpdatedAt });
                    dirty = true;

                    // **拆开**：老 body 是用「；」把好几件事缝成一句的，
                    // 原样搬过来就还是一条被截断的流水句。按分号拆成一件事一条。
                    w.Items.Clear();
                    foreach (var part in IdeaWeight.SplitLegacyBody(body))
                        w.Items.Add(new WeightItem { Id = WeightItem.NewId(), Text = part, Ts = w.UpdatedAt });
                }
                else
                {
                    // 最早的老格式：what / key / weight / updated_at
                    var what = o["what"]?.GetValue<string>() ?? "";
                    if (what.Length == 0) continue;

                    w = new IdeaWeight
                    {
                        Id = "old" + Math.Abs(what.GetHashCode()).ToString("x8"),
                        Kind = "group",
                        Target = "",
                        Weight = o["weight"]?.GetValue<double>() ?? 0,
                        UpdatedAt = o["updated_at"]?.GetValue<long>() ?? 0,
                    };
                    w.Items.Add(new WeightItem { Id = WeightItem.NewId(), Text = what, Ts = w.UpdatedAt });
                    dirty = true;
                }

                if (w is null) continue;

                w.Id = w.Id.Length > 0 ? w.Id : key;
                w.Items.RemoveAll(i => i.Text.Trim().Length == 0);
                if (w.Items.Count == 0) { dirty = true; continue; }

                if (w.Title.Length == 0) w.Title = IdeaWeight.TitleOf(w.Kind, w.Who, w.Target);

                // ---- 渐进衰减
                if (w.UpdatedAt > 0 && now > w.UpdatedAt)
                {
                    var days = (now - w.UpdatedAt) / 86400.0;
                    if (days > 0.01)
                    {
                        // 一直没被兑现的（MatureRounds 超过阈值）承认做不到，衰减加速
                        var staleAt = Math.Max(2, _env.Idea.Weight.StaleRounds);
                        var boostSpeed = w.MatureRounds >= staleAt ? 3.0 : 1.0;

                        var factor = Math.Pow(0.5, days * boostSpeed / halfLifeDays);
                        var decayed = w.Weight * factor;
                        if (Math.Abs(decayed - w.Weight) > 0.001)
                        {
                            w.Weight = Math.Round(decayed, 4);
                            dirty = true;
                        }
                        w.UpdatedAt = now;
                    }
                }

                // ---- 完全遗忘：掉到阈值以下就整条移除
                if (!w.Done && w.Weight < forgetBelow)
                {
                    dirty = true;
                    continue;
                }

                map[IdeaWeight.StoreKey(w)] = w;
            }

            if (dirty) SaveWeights(map.Values.ToList(), alreadyLocked: false);
            return map;
        }
        catch
        {
            return new Dictionary<string, IdeaWeight>();
        }
    }

    public void SaveWeights(IEnumerable<IdeaWeight> items, bool alreadyLocked = true)
    {
        lock (_lock)
        {
            var all = items
                .Where(w => w.Items.Count > 0)
                .OrderByDescending(w => w.Weight)
                .ThenByDescending(w => w.UpdatedAt)
                .Take(Math.Max(4, _env.Idea.Weight.MaxBuckets))
                .ToList();

            var map = new Dictionary<string, IdeaWeight>(StringComparer.Ordinal);
            foreach (var w in all) map[IdeaWeight.StoreKey(w)] = w;

            try
            {
                Env.WriteAtomic(WeightsFile, JsonSerializer.Serialize(map, AppJson.Config));
            }
            catch { /* 忽略 */ }
        }
    }

    /// <summary>还活着、还没做的加权意向（按权重降序）。</summary>
    public List<IdeaWeight> PendingWeights(int max = 8) =>
        LoadWeights().Values
            .Where(w => !w.Done && w.Weight > 0.05)
            .OrderByDescending(w => w.Weight)
            .Take(max)
            .ToList();

    /// <summary>够重但一直没做的意向（权重 ≥ act_threshold）。</summary>
    public List<IdeaWeight> MatureWeights(int max = 3)
    {
        var threshold = Math.Clamp(_env.Idea.Weight.ActThreshold, 0.3, 1.0);

        return LoadWeights().Values
            .Where(w => !w.Done && w.Weight >= threshold)
            .OrderByDescending(w => w.Weight)
            .Take(max)
            .ToList();
    }

    /// <summary>加权。返回 null 表示**拒绝**——桶已经够重了。</summary>
    public (IdeaWeight? weight, bool rejected) Boost(string kind, string target, string who,
        string topic, string origin, IEnumerable<string> newItems, double amount)
    {
        var k = (kind ?? "group").Trim().ToLowerInvariant();
        var t = (target ?? "").Trim();
        var tp = (topic ?? "").Trim();

        lock (_lock)
        {
            var all = LoadWeights();

            // 键是 (实体, 主题)。找不到同主题的桶时，再退一步找 (实体) 下主题为空的旧桶。
            var key = IdeaWeight.KeyOf(k, t, tp);
            if (!all.TryGetValue(key, out var hit) && tp.Length > 0)
                all.TryGetValue(IdeaWeight.KeyOf(k, t), out hit);

            var cap = Math.Clamp(_env.Idea.Weight.ActThreshold, 0.3, 1.0);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (hit is not null)
            {
                if (hit.Weight >= cap)
                    return (hit, true);   // 够重了，别再惦记

                // ---- 内容没变就不加权
                //
                // 实盘的问题：每 5 分钟都会把同一批话重新"确认"一遍，内容一个字没变，
                // 权重却一路加。而且**每加一次就重置衰减锚点**，于是权重在 0.95~1.0
                // 之间永久锯齿，衰减永远追不上。
                // 现在只有真的新增了事项才加权重、才刷新时间。
                var beforeCount = hit.Items.Count;
                MergeItems(hit, newItems);
                var changed = hit.Items.Count > beforeCount;

                if (changed)
                {
                    hit.Weight = Math.Min(cap, hit.Weight + Math.Clamp(amount, 0.01, 0.5));
                    hit.UpdatedAt = now;
                    hit.Reinforced++;
                }

                if (hit.Who.Length == 0 && who.Length > 0) hit.Who = who;
                if (hit.Topic.Length == 0 && tp.Length > 0) hit.Topic = tp;
                hit.Title = IdeaWeight.TitleWithTopic(hit.Kind, hit.Who, hit.Target, hit.Topic);

                SaveWeights(all.Values.ToList(), alreadyLocked: false);
                return (hit, false);
            }

            var created = new IdeaWeight
            {
                Id = "w" + Guid.NewGuid().ToString("N")[..8],
                Kind = k,
                Target = t,
                Who = who,
                Topic = tp,
                Origin = origin == "self" ? "self" : "context",
                Weight = Math.Min(cap, Math.Clamp(amount, 0.02, 0.5)),
                CreatedAt = now,
                UpdatedAt = now,
                Reinforced = 1,
            };
            created.Title = IdeaWeight.TitleWithTopic(created.Kind, created.Who, created.Target, created.Topic);
            MergeItems(created, newItems);

            if (created.Items.Count == 0) return (null, false);

            all[key] = created;
            SaveWeights(all.Values.ToList(), alreadyLocked: false);
            return (created, false);
        }
    }

    /// <summary>增删事项：一句话一件事，同一桶里不重复。</summary>
    public static void MergeItems(IdeaWeight bucket, IEnumerable<string> texts)
    {
        var max = 6;

        foreach (var raw in texts)
        {
            var text = (raw ?? "").Trim();
            if (text.Length == 0) continue;
            if (text.Length > 60) text = text[..60];

            // 已经说过类似的不再重复加（同桶内近似归并）
            var dup = bucket.Items.Any(i =>
                TextSim.Similarity(i.Text, text) >= 0.72
                || i.Text.Contains(text, StringComparison.Ordinal)
                || text.Contains(i.Text, StringComparison.Ordinal));

            if (dup) continue;
            if (bucket.Items.Count >= max) break;

            bucket.Items.Add(new WeightItem
            {
                Id = WeightItem.NewId(),
                Text = text,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
        }
    }

    /// <summary>消费掉一条意向：标记完成并**移出列表**。</summary>
    public bool Consume(string id, string by)
    {
        var all = LoadWeights();
        var hit = all.Values.FirstOrDefault(w => w.Id == id);
        if (hit is null) return false;

        hit.Done = true;
        hit.Weight = 0;
        SaveWeights(all.Values.Where(w => !w.Done).ToList());
        return true;
    }

    /// <summary>按落点消费整桶（act 成功发出时用）。返回消费掉了几个桶。</summary>
    public int ConsumeByTarget(string kind, string target)
    {
        var all = LoadWeights();

        // 键是 (kind, target, topic) 三段，但这里原来拿两段去查，于是永远查不到——
        // 主题为空的那条键正好等于两段，只有它能命中，而实盘每条都带主题。
        // 结果就是 idea.weight.consumed 一次都没涨过，做完的事还挂在账上继续被想起。
        var prefix = IdeaWeight.KeyOf(kind, target);

        var hits = all
            .Where(kv => kv.Key == prefix || kv.Key.StartsWith(prefix + "|", StringComparison.Ordinal))
            .Select(kv => kv.Value)
            .ToList();

        if (hits.Count == 0) return 0;

        foreach (var hit in hits)
        {
            hit.Done = true;
            hit.Weight = 0;
        }

        SaveWeights(all.Values.Where(w => !w.Done).ToList());
        return hits.Count;
    }

    /// <summary>删掉一条。</summary>
    public bool SetOrigin(string id, string origin)
    {
        var want = origin == "self" ? "self" : "context";
        var all = LoadWeights();
        var hit = all.Values.FirstOrDefault(w => w.Id == id);
        if (hit is null) return false;

        hit.Origin = want;
        // 改判成自我意象之后，之前攒的"未兑现轮数"就不再适用了（它本来就该自己做）
        if (want == "self") hit.MatureRounds = 0;
        hit.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        SaveWeights(all.Values.ToList());
        return true;
    }

    public bool Remove(string id)
    {
        var all = LoadWeights();
        var hit = all.Values.FirstOrDefault(w => w.Id == id);
        if (hit is null) return false;

        all.Remove(IdeaWeight.StoreKey(hit));
        SaveWeights(all.Values.ToList());
        return true;
    }

    /// <summary>老数据清账：把没有落点（target 为空）的条目合并成一条，合不动的丢弃。</summary>
    public int CleanupLegacy()
    {
        var all = LoadWeights();
        var legacy = all.Values.Where(w => w.Target.Trim().Length == 0).ToList();
        if (legacy.Count == 0) return 0;

        var merged = new IdeaWeight
        {
            Id = "legacyMerged",
            Kind = "group",
            Target = "",
            // 这个桶是"历史遗留"的临时收容所，没有落点、永远不会被执行。
            // 权重给低一点让它自然衰减掉，而不是继承某条已完成条目的高权重继续赖着。
            Weight = 0.10,
            CreatedAt = legacy.Min(w => w.CreatedAt),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        foreach (var w in legacy)
            foreach (var i in w.Items)
                if (merged.Items.Count < 6) merged.Items.Add(i);

        foreach (var w in legacy) all.Remove(IdeaWeight.StoreKey(w));

        if (merged.Items.Count > 0) all[IdeaWeight.StoreKey(merged)] = merged;

        SaveWeights(all.Values.ToList());
        return legacy.Count;
    }

    /// <summary>用联系人目录校正每个桶的 <c>Who</c>。</summary>
    public int FixWho(Func<string, string> resolveName)
    {
        var all = LoadWeights();
        var fixedCount = 0;

        foreach (var w in all.Values)
        {
            if (w.Target.Trim().Length == 0) continue;

            var real = (resolveName(w.Target) ?? "").Trim();
            if (string.Equals(real, w.Who, StringComparison.Ordinal)) continue;

            w.Who = real;
            w.Title = IdeaWeight.TitleOf(w.Kind, w.Who, w.Target);
            fixedCount++;
        }

        if (fixedCount > 0) SaveWeights(all.Values.ToList());
        return fixedCount;
    }

    /// <summary>一轮想完之后调用：给"够重但没被兑现"的桶加一次计数。</summary>
    public void NoteUnfulfilled()
    {
        var cap = Math.Clamp(_env.Idea.Weight.ActThreshold, 0.3, 1.0);

        lock (_lock)
        {
            var all = LoadWeights();
            var touched = false;

            foreach (var w in all.Values)
            {
                if (w.Done || w.Weight < cap) continue;
                w.MatureRounds++;
                touched = true;
            }

            if (touched) SaveWeights(all.Values.ToList(), alreadyLocked: false);
        }
    }

    /// <summary>权重最高且还新鲜的几条，喂给下一轮发散。</summary>
    public List<IdeaWeight> TopWeights(int max = 5) => PendingWeights(max);
}

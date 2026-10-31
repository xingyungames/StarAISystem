using System.Text.Json;

namespace StarGameAI.Core;

/// <summary>
/// 思想层发散用的一条储存
/// </summary>
public sealed class StoreItem
{
    public string Id { get; set; } = "";

    /// <summary>
    /// seed = 从记忆整理出来的种子；residue = 做完一件事剩下来的残渣
    /// </summary>
    public string Kind { get; set; } = "seed";

    /// <summary>
    /// 具体的一件事，一句话。不是形容词摘要
    /// </summary>
    public string Thing { get; set; } = "";

    /// <summary>
    /// 原话颗粒：撑着这件事的原始碎片，留着别磨平
    /// </summary>
    public List<string> Grain { get; set; } = new();

    /// <summary>
    /// 没做完的那部分。发散从这里接着长
    /// </summary>
    public string Open { get; set; } = "";

    /// <summary>
    /// 从哪个落点来的
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// open / done
    /// </summary>
    public string State { get; set; } = "open";

    /// <summary>
    /// 被发散取用过几次。少的先取，免得每次都从同一条长
    /// </summary>
    public int Hits { get; set; }

    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public long LastHitAt { get; set; }
}

/// <summary>
/// 思想层的储存：发散只看这里，不再直接看浅层记忆碎片。
///
/// 为什么要有这一层：碎片是原料，一段话能拆出七八条，三条说的还是同一件事
/// （实盘就是「星涵怕我被带坏」连着三条）。直接拿原料发散，长出来的必然还是那几句。
/// 先整理成「一件事一条」，发散才有东西可挑。
/// </summary>
public sealed class IdeaStore
{
    private readonly Env _env;
    private readonly object _lock = new();

    public IdeaStore(Env env) => _env = env;

    private string StoreFile => Path.Combine(_env.IdeaDir, "store.json");

    /// <summary>
    /// 读全部条目
    /// </summary>
    public List<StoreItem> Load()
    {
        try
        {
            if (!System.IO.File.Exists(StoreFile)) return new List<StoreItem>();

            var list = JsonSerializer.Deserialize<List<StoreItem>>(
                System.IO.File.ReadAllText(StoreFile), AppJson.Config);

            return list ?? new List<StoreItem>();
        }
        catch
        {
            return new List<StoreItem>();
        }
    }

    public void Save(IEnumerable<StoreItem> items)
    {
        lock (_lock)
        {
            try
            {
                var list = items.ToList();
                if (list.Count > 200) list = list.OrderByDescending(i => i.UpdatedAt).Take(200).ToList();

                Directory.CreateDirectory(_env.IdeaDir);
                Env.WriteAtomic(StoreFile, JsonSerializer.Serialize(list, AppJson.Config));
            }
            catch { /* 写不进去不影响主流程 */ }
        }
    }

    /// <summary>
    /// 取几条给发散用：没被碰过的先来，同一条别连着用
    /// </summary>
    public List<StoreItem> Top(int max = 4)
    {
        var all = Load().Where(i => i.State == "open").ToList();
        if (all.Count == 0) return all;

        return all
            .OrderBy(i => i.Hits)
            .ThenBy(i => i.LastHitAt)
            .ThenByDescending(i => i.UpdatedAt)
            .Take(Math.Max(1, max))
            .ToList();
    }

    /// <summary>
    /// 记一次取用
    /// </summary>
    public void MarkHit(IEnumerable<string> ids)
    {
        var want = new HashSet<string>(ids ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        if (want.Count == 0) return;

        var all = Load();
        var changed = false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var i in all)
        {
            if (!want.Contains(i.Id)) continue;
            i.Hits++;
            i.LastHitAt = now;
            changed = true;
        }

        if (changed) Save(all);
    }

    /// <summary>
    /// 整体替换（整理过程产出后调用）。同一件事会并进同一条，词句更新但颗粒留住
    /// </summary>
    public void Replace(IEnumerable<StoreItem> fresh)
    {
        var old = Load();
        var merged = new List<StoreItem>();

        foreach (var item in fresh)
        {
            var hit = old.FirstOrDefault(o => IdeaChainStore.SameTopic(o.Thing, item.Thing));
            if (hit is null)
            {
                merged.Add(item);
                continue;
            }

            // 留下计数与时间，免得整理一次就把"这条碰过几次"清零、发散又从头扎进同一条
            hit.Thing = item.Thing;
            hit.Open = item.Open.Length > 0 ? item.Open : hit.Open;
            hit.Source = item.Source.Length > 0 ? item.Source : hit.Source;
            hit.State = item.State;
            hit.UpdatedAt = item.UpdatedAt;

            foreach (var g in item.Grain)
                if (!hit.Grain.Contains(g, StringComparer.Ordinal)) hit.Grain.Add(g);

            if (hit.Grain.Count > 6) hit.Grain = hit.Grain.Skip(hit.Grain.Count - 6).ToList();
            merged.Add(hit);
        }

        // 老条目里还没了结的、这轮没整理到的，留着——整理不是遗忘
        foreach (var o in old)
        {
            if (o.State != "open") continue;
            if (merged.Any(m => IdeaChainStore.SameTopic(m.Thing, o.Thing))) continue;
            merged.Add(o);
        }

        Save(merged);
    }

    /// <summary>
    /// 做完一件事之后把残渣写回来。
    ///
    /// 这一步是「做完之后还能有新燃料」的唯一来源：做完就删，下一轮没有可长的地方，
    /// 只能又飘回原来那件事。所以标记必须带上「做了什么」，以及「还剩什么没做」。
    /// </summary>
    public void AddDone(string topic, string did, string open, string source)
    {
        var t = (topic ?? "").Trim();
        if (t.Length == 0) return;

        var all = Load();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var hit = all.FirstOrDefault(i => IdeaChainStore.SameTopic(i.Thing, t));
        if (hit is not null)
        {
            hit.State = "done";
            if (did.Length > 0 && !hit.Grain.Contains(did, StringComparer.Ordinal)) hit.Grain.Add(did);
            if (open.Length > 0) hit.Open = open;
            hit.UpdatedAt = now;
            Save(all);
            return;
        }

        all.Add(new StoreItem
        {
            Id = "s" + Guid.NewGuid().ToString("N")[..7],
            Kind = "residue",
            Thing = t.Length > 40 ? t[..40] : t,
            Grain = did.Length > 0 ? new List<string> { did.Length > 120 ? did[..120] : did } : new List<string>(),
            Open = open,
            Source = source,
            State = "done",
            CreatedAt = now,
            UpdatedAt = now,
        });

        Save(all);
    }

    /// <summary>
    /// 手工加一条（后台调试用）
    /// </summary>
    public StoreItem Add(string thing, string open, string source)
    {
        var all = Load();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var item = new StoreItem
        {
            Id = "s" + Guid.NewGuid().ToString("N")[..7],
            Kind = "seed",
            Thing = (thing ?? "").Trim(),
            Open = (open ?? "").Trim(),
            Source = source ?? "",
            State = "open",
            CreatedAt = now,
            UpdatedAt = now,
        };

        all.Add(item);
        Save(all);
        return item;
    }

    public int Remove(string id)
    {
        var all = Load();
        var n = all.RemoveAll(i => i.Id == id);
        if (n > 0) Save(all);
        return n;
    }
}

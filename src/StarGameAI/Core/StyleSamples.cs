using System.Text.Json;

namespace StarGameAI.Core;

/// <summary>
/// 一条「我私下里说话的样子」样本
/// </summary>
public sealed class StyleSample
{
    public string Id { get; set; } = "";
    public long Ts { get; set; }
    public string Scope { get; set; } = "";
    /// <summary>
    /// manual（人喂的）/ model（模型自评出来的）
    /// </summary>
    public string Source { get; set; } = "model";
    /// <summary>
    /// approved / rejected / pending
    /// </summary>
    public string Verdict { get; set; } = "pending";
    public string Text { get; set; } = "";
    /// <summary>
    /// 模型自评：这句像不像你平时的样子（0~100）
    /// </summary>
    public int SelfScore { get; set; }
    /// <summary>
    /// 有效权重。人工标注给满，模型自评按不确定度打折
    /// </summary>
    public double Weight { get; set; }
    public long UpdatedAt { get; set; }
}

/// <summary>
/// 样本集合的不确定度与当前准入模式
/// </summary>
public sealed class StyleState
{
    public int Approved { get; set; }
    public int Pending { get; set; }
    public int Rejected { get; set; }
    /// <summary>
    /// 已批准样本之间的一致性（0~1，越高越"像同一个人说的"）
    /// </summary>
    public double Consistency { get; set; }
    /// <summary>
    /// 覆盖度：样本数相对目标的完成比例（0~1）
    /// </summary>
    public double Coverage { get; set; }
    /// <summary>
    /// 不确定度（0~1）。越高越该听人的
    /// </summary>
    public double Uncertainty { get; set; }
    /// <summary>
    /// manual / assisted / adaptive
    /// </summary>
    public string Mode { get; set; } = "manual";
}

/// <summary>
/// 风格样本库 + 不确定性驱动的准入门
/// </summary>
public sealed class StyleStore
{
    private readonly Env _env;
    private readonly object _lock = new();

    public StyleStore(Env env) => _env = env;

    private string FilePath => Path.Combine(_env.DataDir, "style", "samples.json");

    public List<StyleSample> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<StyleSample>();
            return JsonSerializer.Deserialize<List<StyleSample>>(
                       File.ReadAllText(FilePath), AppJson.Config)
                   ?? new List<StyleSample>();
        }
        catch
        {
            return new List<StyleSample>();
        }
    }

    private void Save(List<StyleSample> list)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);

            // 上限 200 条：样本库不是流水账
            var trimmed = list
                .OrderByDescending(s => s.Verdict == "approved")
                .ThenByDescending(s => s.Weight)
                .ThenByDescending(s => s.Ts)
                .Take(200)
                .ToList();

            Env.WriteAtomic(FilePath, JsonSerializer.Serialize(trimmed, AppJson.Config));
        }
        catch { /* 忽略 */ }
    }


    public StyleState State()
    {
        var all = Load();
        var cfg = _env.Style;
        var approved = all.Where(s => s.Verdict == "approved").ToList();

        var coverage = cfg.TargetSamples <= 0
            ? 1.0
            : Math.Min(1.0, approved.Count / (double)cfg.TargetSamples);

        // 一致性：已批准样本在风格特征上的离散度。样本 < 2 时视为不知道，给 0
        //
        // 不能用文本相似度：8 条明显同语气的样本只算出 0.002
        // 风格不是词汇重叠
        double consistency = 0;
        if (approved.Count >= 2)
        {
            var vectors = approved.Select(s => StyleVector(s.Text)).ToList();
            var dims = vectors[0].Length;
            var spread = 0.0;

            for (var d = 0; d < dims; d++)
            {
                var values = vectors.Select(v => v[d]).ToList();
                var mean = values.Average();
                var std = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
                spread += std;
            }

            spread /= dims;
            consistency = Math.Clamp(1.0 - spread / 0.35, 0, 1);
        }

        // 不确定度：覆盖度不足 / 彼此不像 → 高
        var uncertainty = Math.Clamp(1.0 - coverage * (0.5 + 0.5 * consistency), 0, 1);

        var mode = uncertainty > cfg.ManualAbove ? "manual"
                 : uncertainty < cfg.AdaptiveBelow ? "adaptive"
                 : "assisted";

        return new StyleState
        {
            Approved = approved.Count,
            Pending = all.Count(s => s.Verdict == "pending"),
            Rejected = all.Count(s => s.Verdict == "rejected"),
            Consistency = Math.Round(consistency, 3),
            Coverage = Math.Round(coverage, 3),
            Uncertainty = Math.Round(uncertainty, 3),
            Mode = mode,
        };
    }


    /// <summary>
    /// 人工喂一条样本：直接批准，权重满格
    /// </summary>
    public StyleSample AddManual(string text, string scope)
    {
        var s = new StyleSample
        {
            Id = "s" + Guid.NewGuid().ToString("N")[..8],
            Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Scope = scope,
            Source = "manual",
            Verdict = "approved",
            Text = (text ?? "").Trim(),
            SelfScore = 100,
            Weight = 1.0,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        lock (_lock)
        {
            var all = Load();
            all.Add(s);
            Save(all);
        }

        return s;
    }

    /// <summary>
    /// 模型自评出来的候选。**按当前不确定度决定要不要直接收**：
    /// </summary>
    public (bool accepted, string mode) Propose(StyleSample candidate)
    {
        var cfg = _env.Style;
        var st = State();

        candidate.Id = candidate.Id.Length > 0 ? candidate.Id : "s" + Guid.NewGuid().ToString("N")[..8];
        candidate.Source = "model";
        candidate.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (candidate.Ts == 0) candidate.Ts = candidate.UpdatedAt;

        var accept = st.Mode switch
        {
            "manual" => false,                                      // 只信人
            "assisted" => candidate.SelfScore >= cfg.AssistedMinScore,
            _ => candidate.SelfScore >= cfg.AdaptiveMinScore,       // adaptive
        };

        candidate.Verdict = accept ? "approved" : "pending";
        candidate.Weight = accept
            ? Math.Clamp(candidate.SelfScore / 100.0 * cfg.ModelWeight, 0.05, 1.0)
            : 0;

        lock (_lock)
        {
            var all = Load();

            // 同一句话不重复收
            if (all.Any(x => Core.TextSim.Similarity(x.Text, candidate.Text) >= 0.92))
                return (false, st.Mode);

            all.Add(candidate);
            Save(all);
        }

        return (accept, st.Mode);
    }

    public bool SetVerdict(string id, string verdict)
    {
        if (verdict is not ("approved" or "rejected" or "pending")) return false;

        lock (_lock)
        {
            var all = Load();
            var hit = all.FirstOrDefault(s => s.Id == id);
            if (hit is null) return false;

            hit.Verdict = verdict;
            hit.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // 人工改判 → 权重按人的判断走
            hit.Weight = verdict == "approved" ? 1.0 : 0;

            Save(all);
            return true;
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var all = Load();
            var removed = all.RemoveAll(s => s.Id == id);
            if (removed == 0) return false;
            Save(all);
            return true;
        }
    }

    /// <summary>
    /// 注入用的样本：已批准、按权重取前几条
    /// </summary>
    public List<StyleSample> Top(int max = 4) =>
        Load()
            .Where(s => s.Verdict == "approved" && s.Text.Trim().Length > 0)
            .OrderByDescending(s => s.Weight)
            .ThenByDescending(s => s.Ts)
            .Take(max)
            .ToList();

    /// <summary>
    /// 风格特征向量（每维 0~1）。**只看结构，不看词汇**——
    /// </summary>
    private static double[] StyleVector(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return new double[6];

        var inBracket = 0;
        var depth = 0;

        foreach (var ch in t)
        {
            if (ch is '（' or '(') { depth++; continue; }
            if (ch is '）' or ')') { if (depth > 0) depth--; continue; }
            if (depth > 0) inBracket++;
        }

        var actionRatio = Math.Clamp(inBracket / (double)t.Length / 0.6, 0, 1);
        var lengthNorm = Math.Clamp(t.Length / 60.0, 0, 1);

        return new[]
        {
            t.StartsWith('（') || t.StartsWith('(') ? 1.0 : 0.0,      // 开口就是动作
            actionRatio,                                                // 动作占比
            lengthNorm,                                                 // 长度
            t.Contains('…') || t.Contains("...") ? 1.0 : 0.0,            // 爱用省略号
            t.Contains('？') || t.Contains('?') ? 1.0 : 0.0,             // 爱反问
            t.Contains('我') ? 1.0 : 0.0,                                // 第一人称
        };
    }
}

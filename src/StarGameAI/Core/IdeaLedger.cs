using System.Text;
using System.Text.Json;

namespace StarGameAI.Core;

/// <summary>
/// 一条「想做点什么」的提案
/// </summary>
public class IdeaProposal
{
    public string Id { get; set; } = "";
    /// <summary>
    /// speech（说话）/ poke / private / file
    /// </summary>
    public string Intent { get; set; } = "speech";
    /// <summary>
    /// 目标：private:&lt;qq&gt;、group:&lt;群号&gt; 或裸 QQ 号
    /// </summary>
    public string Target { get; set; } = "";
    public string Text { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>
/// 一次准入判定的结果，会被记进账本
/// </summary>
public sealed class IdeaRecord : IdeaProposal
{
    public long Ts { get; set; }
    /// <summary>
    /// test（后台手动投递）/ scheduler（P3 之后启用）
    /// </summary>
    public string Source { get; set; } = "test";
    public string TargetName { get; set; } = "";
    /// <summary>
    /// 判定时的在线状态快照，事后排查用
    /// </summary>
    public string Presence { get; set; } = "";
    public bool Allowed { get; set; }
    /// <summary>
    /// 被哪道闸拦下：disabled / intent / quiet / presence / cooldown / hourly / daily
    /// </summary>
    public string BlockedBy { get; set; } = "";
    public string BlockedDetail { get; set; } = "";
}

/// <summary>
/// 想法准入门槛（P2）
/// </summary>
public sealed class IdeaGate
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly Env _env;
    private readonly Presence _presence;
    private readonly object _lock = new();

    public IdeaGate(Env env, Presence presence)
    {
        _env = env;
        _presence = presence;
    }

    private string LedgerPath => Path.Combine(_env.IdeaDir, "ledger.jsonl");

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();


    public IdeaRecord Judge(IdeaProposal proposal, string source = "test")
    {
        var cfg = _env.Idea;
        var now = Now();

        var record = new IdeaRecord
        {
            Id = string.IsNullOrWhiteSpace(proposal.Id) ? "i" + Guid.NewGuid().ToString("N")[..8] : proposal.Id,
            Intent = (proposal.Intent ?? "speech").Trim(),
            Target = (proposal.Target ?? "").Trim(),
            Text = proposal.Text ?? "",
            Reason = proposal.Reason ?? "",
            Ts = now,
            Source = source,
        };

        var targetUser = UserOf(record.Target);
        record.TargetName = targetUser.Length > 0 ? _presence.Effective(targetUser).UserId : "";

        var view = targetUser.Length > 0 ? _presence.Effective(targetUser) : null;
        record.Presence = view is null ? "-" : $"{view.State}/{view.Source}";

        //总开关
        if (!cfg.Enabled)
            return Deny(record, "disabled", "思想层已关闭");

        //意图白名单
        if (!cfg.AllowedIntents.Contains(record.Intent))
            return Deny(record, "intent", $"意图 {record.Intent} 不在允许列表（{string.Join("/", cfg.AllowedIntents)}）");

        //静默时段（硬禁）
        if (cfg.QuietHours.Contains(DateTime.Now))
            return Deny(record, "quiet", $"落在静默时段 {cfg.QuietHours.Start}-{cfg.QuietHours.End}");

        //目标是否允许被打扰（只对「找某个人」的意图生效）
        if (view is not null && !view.CanDisturb)
            return Deny(record, "presence",
                $"目标当前 {view.State}（{view.Source}：{view.Note}），不允许被打扰");

        var history = ReadSince(now - 30 * 86400);

        //同一对象的冷却
        var cooldownSec = Math.Max(0, cfg.PerTargetCooldownHours) * 3600L;
        if (cooldownSec > 0 && record.Target.Length > 0)
        {
            var last = history.Where(r => r.Allowed && r.Target == record.Target)
                .OrderByDescending(r => r.Ts).FirstOrDefault();
            if (last is not null && now - last.Ts < cooldownSec)
            {
                var left = (cooldownSec - (now - last.Ts)) / 60;
                return Deny(record, "cooldown", $"距上次打扰 {record.Target} 不足 {cfg.PerTargetCooldownHours} 小时（还剩 {left} 分钟）");
            }
        }

        //每小时上限
        var hourAgo = now - 3600;
        var lastHour = history.Count(r => r.Allowed && r.Ts >= hourAgo);
        if (lastHour >= cfg.PerHourLimit)
            return Deny(record, "hourly", $"最近一小时已打扰 {lastHour} 次，上限 {cfg.PerHourLimit}");

        //每日上限
        var dayStart = new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds();
        var today = history.Count(r => r.Allowed && r.Ts >= dayStart);
        if (today >= cfg.DailyLimit)
            return Deny(record, "daily", $"今天已打扰 {today} 次，上限 {cfg.DailyLimit}");

        record.Allowed = true;
        Append(record);
        return record;
    }

    private IdeaRecord Deny(IdeaRecord record, string gate, string detail)
    {
        record.Allowed = false;
        record.BlockedBy = gate;
        record.BlockedDetail = detail;
        Append(record);
        return record;
    }

    /// <summary>
    /// 从目标字符串里取出 QQ 号；群目标返回空（群没有人的在线状态）
    /// </summary>
    public static string UserOf(string? target)
    {
        var t = (target ?? "").Trim();
        if (t.StartsWith("private:", StringComparison.Ordinal)) t = t["private:".Length..];
        else if (t.StartsWith("group:", StringComparison.Ordinal)) return "";
        return t.All(char.IsDigit) && t.Length >= 5 ? t : "";
    }


    private void Append(IdeaRecord record)
    {
        try
        {
            var line = JsonSerializer.Serialize(record, AppJson.Options);
            lock (_lock)
            {
                Directory.CreateDirectory(_env.IdeaDir);
                using var fs = new FileStream(LedgerPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var sw = new StreamWriter(fs, Utf8);
                sw.Write(line);
                sw.Write('\n');
            }
        }
        catch { /* 记账失败不影响判定结果 */ }
    }

    public List<IdeaRecord> ReadSince(long ts)
    {
        var list = new List<IdeaRecord>();
        if (!File.Exists(LedgerPath)) return list;

        var lines = new List<string>();
        try
        {
            using var fs = new FileStream(LedgerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Utf8);
            while (sr.ReadLine() is { } line) lines.Add(line);
        }
        catch
        {
            return list;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var r = JsonSerializer.Deserialize<IdeaRecord>(line, AppJson.Options);
                if (r is not null && r.Ts >= ts) list.Add(r);
            }
            catch { /* 跳过坏行 */ }
        }

        return list;
    }

    public Dictionary<string, int> TodayCounters()
    {
        var dayStart = new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds();
        var today = ReadSince(dayStart);

        var counters = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["total"] = today.Count,
            ["allowed"] = today.Count(r => r.Allowed),
            ["blocked"] = today.Count(r => !r.Allowed),
        };

        foreach (var g in today.Where(r => !r.Allowed).GroupBy(r => r.BlockedBy))
            counters["blocked:" + g.Key] = g.Count();

        return counters;
    }
}

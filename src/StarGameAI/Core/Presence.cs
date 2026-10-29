using System.Text.Json;

namespace StarGameAI.Core;

/// <summary>显式记录的一条在线状态（manual / self_report），以及最近活动的痕迹。</summary>
public sealed class PresenceEntry
{
    public string UserId { get; set; } = "";
    /// <summary>online / offline / unknown</summary>
    public string State { get; set; } = "unknown";
    /// <summary>manual / self_report —— 只有这两种会被持久化，其余靠现场计算</summary>
    public string Source { get; set; } = "manual";
    public long Since { get; set; }
    public string Note { get; set; } = "";
    /// <summary>0 表示不过期。</summary>
    public long ExpiresAt { get; set; }
    public long LastSeen { get; set; }
}

/// <summary>判定结果。</summary>
public sealed class PresenceView
{
    public string UserId { get; set; } = "";
    public string State { get; set; } = "unknown";
    public string Source { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>是否允许被主动打扰。offline=false；online=true；unknown 看允许时段。</summary>
    public bool CanDisturb { get; set; }
    public long ExpiresAt { get; set; }
    public long LastSeen { get; set; }
}

/// <summary>在线状态层（思想层的地基）。</summary>
public sealed class Presence
{
    private readonly Env _env;
    private readonly object _lock = new();
    private Dictionary<string, PresenceEntry> _map = new();
    private long _lastSave;

    public Presence(Env env)
    {
        _env = env;
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_env.PresenceFile)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, PresenceEntry>>(
                File.ReadAllText(_env.PresenceFile), AppJson.Config);
            if (data is not null) _map = data;
        }
        catch
        {
            _map = new();
        }
    }

    private void Save(bool force = false)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 每条消息都写盘太频繁，节流到 15 秒
        if (!force && now - _lastSave < 15) return;
        _lastSave = now;

        try
        {
            Env.WriteAtomic(_env.PresenceFile, JsonSerializer.Serialize(_map, AppJson.Config));
        }
        catch { /* 忽略 */ }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private PresenceEntry Ensure(string userId)
    {
        if (!_map.TryGetValue(userId, out var e))
        {
            e = new PresenceEntry { UserId = userId, Source = "activity", State = "unknown" };
            _map[userId] = e;
        }

        return e;
    }


    /// <summary>收到消息时调用：更新最近活动时间。</summary>
    public void TouchActivity(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        lock (_lock)
        {
            var e = Ensure(userId);
            e.LastSeen = Now();
            Save();
        }
    }

    /// <summary>后台手动设定（优先级最高）。ttlHours &lt;= 0 表示不过期。</summary>
    public void SetManual(string userId, string state, string note, int ttlHours)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        lock (_lock)
        {
            var e = Ensure(userId);
            e.State = Normalize(state);
            e.Source = "manual";
            e.Note = note ?? "";
            e.Since = Now();
            e.ExpiresAt = ttlHours > 0 ? Now() + ttlHours * 3600L : 0;
            Save(true);
        }
    }

    /// <summary>清掉手动设定，回到自动判定。</summary>
    public bool ClearManual(string userId)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(userId, out var e)) return false;
            if (e.Source != "manual") return false;

            e.Source = "activity";
            e.State = "unknown";
            e.Note = "";
            e.Since = Now();
            e.ExpiresAt = 0;
            Save(true);
            return true;
        }
    }

    /// <summary>用户自报在线/离线（既定事实层的第二优先级）。</summary>
    public void ReportSelf(string userId, bool online, string note, int ttlHours)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;
        lock (_lock)
        {
            var e = Ensure(userId);
            if (e.Source == "manual" && (e.ExpiresAt == 0 || e.ExpiresAt > Now()))
                return;   // 手动设定更高优先级，不覆盖

            e.State = online ? "online" : "offline";
            e.Source = "self_report";
            e.Note = note ?? "";
            e.Since = Now();
            e.ExpiresAt = Now() + Math.Max(1, ttlHours) * 3600L;
            Save(true);
        }
    }


    public PresenceView Effective(string userId)
    {
        userId ??= "";
        var cfg = _env.Idea;
        var now = Now();

        PresenceEntry? e;
        lock (_lock) _map.TryGetValue(userId, out e);

        //手动
        if (e is not null && e.Source == "manual" && (e.ExpiresAt == 0 || e.ExpiresAt > now))
            return Build(userId, e.State, "manual", e.Note, e, cfg, now);

        //自报（未过期）
        if (e is not null && e.Source == "self_report" && e.ExpiresAt > now)
            return Build(userId, e.State, "self_report", e.Note, e, cfg, now);

        //最近活动
        var lastSeen = e?.LastSeen ?? 0;
        if (lastSeen > 0 && now - lastSeen <= Math.Max(1, cfg.ActivityOnlineMinutes) * 60)
            return Build(userId, "online", "activity", "刚说过话", e, cfg, now);

        //静默时段一律离线
        if (cfg.QuietHours.Contains(DateTime.Now))
            return Build(userId, "offline", "schedule", "静默时段", e, cfg, now);

        //判不出来 → unknown，由允许时段决定能否打扰
        var note2 = lastSeen > 0
            ? $"最近一次说话 {HumanAge(now - lastSeen)}"
            : "没有活动记录";
        return Build(userId, "unknown", "activity", note2, e, cfg, now);
    }

    private static PresenceView Build(string userId, string state, string source, string note,
        PresenceEntry? e, IdeaConfig cfg, long now)
    {
        var s = Normalize(state);
        var can = s switch
        {
            "online" => true,
            "offline" => false,
            _ => cfg.AllowWindow.Contains(DateTime.Now),
        };

        return new PresenceView
        {
            UserId = userId,
            State = s,
            Source = source,
            Note = note ?? "",
            CanDisturb = can,
            ExpiresAt = e?.ExpiresAt ?? 0,
            LastSeen = e?.LastSeen ?? 0,
        };
    }

    private static string Normalize(string? state) => state switch
    {
        "online" or "offline" or "unknown" => state!,
        _ => "unknown",
    };

    private static string HumanAge(long seconds) =>
        seconds < 60 ? "刚刚"
        : seconds < 3600 ? seconds / 60 + " 分钟前"
        : seconds < 86400 ? seconds / 3600 + " 小时前"
        : seconds / 86400 + " 天前";

    /// <summary>后台列表：所有有记录的人 + 自动判定结果。</summary>
    public List<PresenceView> All(IEnumerable<string>? extraUserIds = null)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        lock (_lock)
        {
            foreach (var k in _map.Keys) ids.Add(k);
        }

        foreach (var id in extraUserIds ?? Enumerable.Empty<string>())
            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);

        return ids.Select(Effective).OrderByDescending(v => v.LastSeen).ToList();
    }


    private static readonly string[] OfflineHints =
    {
        "睡觉", "睡了", "我睡了", "要睡", "晚安", "上课", "在上课", "开会", "在开会",
        "在忙", "忙了", "考试", "出门", "下线", "先忙", "不聊了", "撤了", "走了",
    };

    private static readonly string[] OnlineHints =
    {
        "我回来了", "回来了", "下课", "醒了", "上线", "到家", "空闲了", "有空了", "忙完了",
    };

    /// <summary>从用户的话里识别「我现在在线/离线」。</summary>
    public bool TryDetectSelfReport(string text, out bool online, out string note, out int ttlHours)
    {
        online = false;
        note = "";
        ttlHours = 0;

        var body = (text ?? "").Trim();
        if (body.Length == 0 || body.Length > 40) return false;

        var cfg = _env.Idea;

        foreach (var w in OnlineHints)
        {
            if (!body.Contains(w, StringComparison.Ordinal)) continue;
            online = true;
            note = "自报：" + w;
            ttlHours = Math.Max(1, cfg.SelfReportBusyHours);
            return true;
        }

        foreach (var w in OfflineHints)
        {
            if (!body.Contains(w, StringComparison.Ordinal)) continue;
            online = false;
            note = "自报：" + w;

            var sleep = w is "睡觉" or "睡了" or "我睡了" or "要睡" or "晚安";
            ttlHours = sleep ? Math.Max(1, cfg.SelfReportSleepHours) : Math.Max(1, cfg.SelfReportBusyHours);
            return true;
        }

        return false;
    }
}

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using StarGameAI.Core;
using StarGameAI.Gateway;

namespace StarGameAI.Pipeline;

/// <summary>
/// 一条聊天记录。入站来自 NapCat 上报，出站由发送成功后回写
/// </summary>
public sealed class LogEntry
{
    /// <summary>
    /// 本地接收时间。日志分目录与水位线都用它，避免 NapCat 补投的历史时间戳把数据写进旧目录
    /// </summary>
    public long Ts { get; set; }
    /// <summary>
    /// 事件自带的时间（仅作参考）
    /// </summary>
    public long EventTs { get; set; }
    public string MessageId { get; set; } = "";
    public string TraceId { get; set; } = "";
    public string Scope { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "member";
    public bool IsSelf { get; set; }
    /// <summary>
    /// reply / ambient / recall / manual。入站为空
    /// </summary>
    public string Trigger { get; set; } = "";
    public string InReplyTo { get; set; } = "";
    public string Text { get; set; } = "";
    public List<SegBrief> Segments { get; set; } = new();
}

/// <summary>
/// 缓存日志层。纯 append、零模型调用、在消息入口同步写入
/// </summary>
public sealed class ChatLog
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly Env _env;
    private readonly object _gate = new();

    public ChatLog(Env env) => _env = env;

    public static string SafeScope(string scope) =>
        scope.Replace(':', '_').Replace('/', '_').Replace('\\', '_');

    public string FileFor(DateOnly date, string scope) =>
        Path.Combine(_env.LogDir, date.ToString("yyyy-MM-dd"), SafeScope(scope) + ".jsonl");

    public void Append(LogEntry entry)
    {
        var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(entry.Ts).LocalDateTime);
        var path = FileFor(date, entry.Scope);
        var line = JsonSerializer.Serialize(entry, AppJson.Options);

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var sw = new StreamWriter(fs, Utf8);
            sw.Write(line);
            sw.Write('\n');
        }
    }

    /// <summary>
    /// 读取某天（可限定 scope）的记录，按写入顺序
    /// </summary>
    public List<LogEntry> Read(DateOnly date, string? scope = null, string? trace = null)
    {
        var dir = Path.Combine(_env.LogDir, date.ToString("yyyy-MM-dd"));
        if (!Directory.Exists(dir)) return new List<LogEntry>();

        var files = scope is null
            ? Directory.GetFiles(dir, "*.jsonl")
            : new[] { FileFor(date, scope) };

        var result = new List<LogEntry>();
        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;
            foreach (var line in SafeReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                LogEntry? e;
                try { e = JsonSerializer.Deserialize<LogEntry>(line, AppJson.Options); }
                catch { continue; }
                if (e is null) continue;
                if (trace is not null && !string.Equals(e.TraceId, trace, StringComparison.Ordinal)) continue;
                result.Add(e);
            }
        }

        return result.OrderBy(e => e.Ts).ToList();
    }

    /// <summary>
    /// 某天全量记录数（用于睡眠阈值判断）
    /// </summary>
    public int CountOfDate(DateOnly date)
    {
        var dir = Path.Combine(_env.LogDir, date.ToString("yyyy-MM-dd"));
        if (!Directory.Exists(dir)) return 0;

        var total = 0;
        foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Utf8);
                while (sr.ReadLine() is { } line)
                    if (!string.IsNullOrWhiteSpace(line)) total++;
            }
            catch { /* 读到一半被替换，忽略 */ }
        }

        return total;
    }

    private static IEnumerable<string> SafeReadLines(string file)
    {
        var lines = new List<string>();
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Utf8);
            while (sr.ReadLine() is { } line) lines.Add(line);
        }
        catch { /* 忽略 */ }

        return lines;
    }
}

/// <summary>
/// 今日计数（进程内，重启归零；用于后台展示与发言门预算）
/// </summary>
public sealed class Metrics
{
    private readonly ConcurrentDictionary<string, long> _counters = new();

    public void Inc(string key) => _counters.AddOrUpdate(key, 1, (_, v) => v + 1);

    public long Get(string key) => _counters.TryGetValue(key, out var v) ? v : 0;

    public void Set(string key, long value) => _counters[key] = value;

    public Dictionary<string, long> Snapshot() => _counters.ToDictionary(kv => kv.Key, kv => kv.Value);
}

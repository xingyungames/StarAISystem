using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace StarGameAI.Core;

/// <summary>一次神经元调用的句柄。</summary>
public sealed class SpanHandle
{
    public string TraceId { get; init; } = "";
    public string Neuron { get; init; } = "";
    public int Seq { get; init; }
    public long StartTicks { get; init; } = Environment.TickCount64;
    public int InChars { get; init; }
}

/// <summary>神经元级别的埋点。</summary>
public sealed class Tracer
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly Env _env;
    private readonly ConcurrentDictionary<string, int> _seq = new();
    private readonly object _lock = new();

    public Tracer(Env env) => _env = env;

    public bool Enabled { get; set; } = true;

    private string Dir => Path.Combine(_env.AuditDir, "spans");

    private string FileFor(DateOnly date) =>
        Path.Combine(Dir, date.ToString("yyyy-MM-dd") + ".jsonl");

    public SpanHandle Begin(string traceId, string neuron, int inChars = 0)
    {
        var seq = _seq.AddOrUpdate(traceId, 0, (_, v) => v + 1);

        // 防御：trace 数量失控时清掉一半，避免字典无限增长
        if (_seq.Count > 5000)
        {
            foreach (var key in _seq.Keys.Take(2500).ToList()) _seq.TryRemove(key, out _);
        }

        return new SpanHandle { TraceId = traceId, Neuron = neuron, Seq = seq, InChars = inChars };
    }

    public void End(SpanHandle span, bool ok, string brief = "", string stage = "", int outChars = 0, int tokens = 0)
    {
        if (!Enabled || span is null) return;

        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var text = brief ?? "";
            var record = new
            {
                trace_id = span.TraceId,
                seq = span.Seq,
                neuron = span.Neuron,
                ts = now,
                ms = (int)(Environment.TickCount64 - span.StartTicks),
                ok,
                stage,
                in_chars = span.InChars,
                out_chars = outChars,
                tokens,
                brief = text.Length > 160 ? text[..160] : text,
            };

            var line = JsonSerializer.Serialize(record, AppJson.Options);
            var path = FileFor(DateOnly.FromDateTime(DateTime.Now));

            lock (_lock)
            {
                Directory.CreateDirectory(Dir);
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var sw = new StreamWriter(fs, Utf8);
                sw.Write(line);
                sw.Write('\n');
            }
        }
        catch
        {
            // 埋点失败绝不影响主流程
        }
    }

    /// <summary>读取某天的全部 span（按 trace 聚合交给上层）。</summary>
    public List<Dictionary<string, JsonElement>> Read(DateOnly date)
    {
        var path = FileFor(date);
        var list = new List<Dictionary<string, JsonElement>>();
        if (!File.Exists(path)) return list;

        var lines = new List<string>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
                using var doc = JsonDocument.Parse(line);
                list.Add(doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));
            }
            catch { /* 跳过坏行 */ }
        }

        return list;
    }

    /// <summary>清理超过保留期的 span 文件。</summary>
    public int Prune(int retainDays)
    {
        try
        {
            if (!Directory.Exists(Dir)) return 0;

            var cutoff = DateTime.Now.Date.AddDays(-Math.Max(1, retainDays));
            var removed = 0;

            foreach (var file in Directory.GetFiles(Dir, "*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!DateOnly.TryParse(name, out var d)) continue;
                if (d.ToDateTime(TimeOnly.MinValue) >= cutoff) continue;

                try { File.Delete(file); removed++; } catch { /* 忽略 */ }
            }

            return removed;
        }
        catch
        {
            return 0;
        }
    }
}

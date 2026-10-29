using System.Text;
using System.Text.Json;
using StarGameAI.Core;

namespace StarGameAI.Memory;

/// <summary>一次「自我被改写」的事件记录。</summary>
public sealed class DeltaRecord
{
    public long Ts { get; set; }
    /// <summary>persona（人设段）/ person（人物画像）/ group（事件画像）/ memory（碎片入库）</summary>
    public string Kind { get; set; } = "";
    public string Target { get; set; } = "";
    public string By { get; set; } = "";
    /// <summary>改写前后的文本相似度，1 = 完全没变。变化率 = 1 - Similarity。</summary>
    public double Similarity { get; set; } = 1;
    public int BeforeChars { get; set; }
    public int AfterChars { get; set; }
    public int AddedLines { get; set; }
    public int RemovedLines { get; set; }
    /// <summary>本次吸收掉的浅层碎片条数。</summary>
    public int Absorbed { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>自我变化度日志。</summary>
public sealed class DeltaLog
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly Env _env;
    private readonly object _lock = new();

    public DeltaLog(Env env) => _env = env;

    private string FilePath => Path.Combine(_env.AuditDir, "delta.jsonl");

    public void Record(DeltaRecord record)
    {
        try
        {
            var line = JsonSerializer.Serialize(record, AppJson.Options);
            lock (_lock)
            {
                Directory.CreateDirectory(_env.AuditDir);
                using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var sw = new StreamWriter(fs, Utf8);
                sw.Write(line);
                sw.Write('\n');
            }
        }
        catch { /* 记录失败不影响主流程 */ }
    }

    public List<DeltaRecord> ReadAll()
    {
        var list = new List<DeltaRecord>();
        if (!File.Exists(FilePath)) return list;

        try
        {
            using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Utf8);
            while (sr.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var r = JsonSerializer.Deserialize<DeltaRecord>(line, AppJson.Options);
                    if (r is not null) list.Add(r);
                }
                catch { /* 跳过坏行 */ }
            }
        }
        catch { /* 忽略 */ }

        return list;
    }

    public List<DeltaRecord> Since(long ts) => ReadAll().Where(r => r.Ts >= ts).ToList();
}


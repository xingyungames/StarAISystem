using System.Text.Json;

namespace StarGameAI.Core;

/// <summary>
/// 工具调用记账：一次调用一行，落到 data/audit/tool.jsonl
///
/// 为什么单独记：现有埋点里 tool 这一档几乎是空的（实盘 598 条 span 里只有 3 条），
/// 于是「它到底调没调工具、调了什么、拿回了什么」只能靠翻聊天记录猜。
/// </summary>
public sealed class ToolTrace
{
    private readonly Env _env;
    private readonly object _lock = new();

    public ToolTrace(Env env) => _env = env;

    private string PathOf => Path.Combine(_env.AuditDir, "tool.jsonl");

    /// <summary>
    /// 记一次调用
    /// </summary>
    /// <param name="name">工具名</param>
    /// <param name="args">参数原文</param>
    /// <param name="ok">成功与否</param>
    /// <param name="ms">耗时毫秒</param>
    /// <param name="result">返回内容</param>
    /// <param name="scope">在哪个会话调的</param>
    /// <param name="traceId">同一轮的追踪号</param>
    public void Append(string name, string args, bool ok, int ms, string result, string scope, string traceId)
    {
        try
        {
            var row = new
            {
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                scope,
                traceId,
                tool = name,
                ok,
                ms,
                args = Trim(args, 300),
                result = Trim(result, 600),
            };

            lock (_lock)
            {
                Directory.CreateDirectory(_env.AuditDir);
                File.AppendAllText(PathOf, JsonSerializer.Serialize(row, AppJson.Options) + "\n");
            }
        }
        catch { /* 记账失败不影响工具本身 */ }
    }

    /// <summary>
    /// 读最近几次
    /// </summary>
    /// <param name="limit">最多几条，新的在前</param>
    public List<JsonElement> Recent(int limit)
    {
        var list = new List<JsonElement>();
        try
        {
            if (!File.Exists(PathOf)) return list;

            var lines = File.ReadAllLines(PathOf).Where(l => l.Trim().Length > 0).TakeLast(Math.Max(1, limit));
            foreach (var line in lines)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    list.Add(doc.RootElement.Clone());
                }
                catch { /* 跳过坏行 */ }
            }
        }
        catch { /* 读不到就当没有 */ }

        list.Reverse();
        return list;
    }

    private static string Trim(string s, int n)
    {
        var v = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return v.Length > n ? v[..n] + "…" : v;
    }
}

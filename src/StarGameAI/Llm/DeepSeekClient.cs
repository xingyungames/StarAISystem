using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace StarGameAI.Llm;


public sealed class ChatMsg
{
    public string Role { get; set; } = "user";
    public string? Content { get; set; }
    public string? ToolCallId { get; set; }
    public List<ToolCall>? ToolCalls { get; set; }

    public static ChatMsg System(string text) => new() { Role = "system", Content = text };
    public static ChatMsg User(string text) => new() { Role = "user", Content = text };
    public static ChatMsg Assistant(string text) => new() { Role = "assistant", Content = text };
    public static ChatMsg ToolResult(string callId, string text) =>
        new() { Role = "tool", ToolCallId = callId, Content = text };
}

public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "function";
    public ToolFunctionCall Function { get; set; } = new();
}

public sealed class ToolFunctionCall
{
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "{}";
}

public sealed class ToolDef
{
    public string Type { get; set; } = "function";
    public ToolFunctionDef Function { get; set; } = new();
}

public sealed class ToolFunctionDef
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public object? Parameters { get; set; }
}

public sealed class LlmUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public int ReasoningTokens { get; set; }
}

public sealed class LlmResult
{
    public string Content { get; set; } = "";
    public List<ToolCall> ToolCalls { get; set; } = new();
    public LlmUsage Usage { get; set; } = new();
    public int LatencyMs { get; set; }
    public string Model { get; set; } = "";
}

public sealed class LlmException : Exception
{
    public LlmException(string message) : base(message) { }
}


//坑：V4 默认开思考模式，思考 token 计入 max_tokens，会出现 200 但 content 为空
/// <summary>
/// DeepSeek（OpenAI 兼容 /chat/completions）客户端
/// </summary>
public sealed class DeepSeekClient
{
    private static readonly HttpClient Http = CreateHttp();

    private readonly Core.ModelConfig _cfg;
    private readonly string _apiKey;
    private readonly ILogger<DeepSeekClient> _log;

    public DeepSeekClient(Core.ModelConfig cfg, string apiKey, ILogger<DeepSeekClient> log)
    {
        _cfg = cfg;
        _apiKey = apiKey;
        _log = log;
    }

    public bool HasKey => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<LlmResult> ChatAsync(
        string profile,
        IReadOnlyList<ChatMsg> messages,
        IReadOnlyList<ToolDef>? tools = null,
        CancellationToken ct = default)
    {
        if (!HasKey) throw new LlmException("DeepSeek API Key 为空");

        var p = _cfg.Profile(profile);
        var thinking = string.Equals(p.Thinking, "enabled", StringComparison.OrdinalIgnoreCase);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = _cfg.Model,
            ["messages"] = messages,
            ["temperature"] = p.Temperature,
            ["max_tokens"] = p.MaxTokens,
            ["stream"] = false,
            // 关键：V4 默认开思考，关掉才不会出现「token 全花在 reasoning、content 为空」
            ["thinking"] = new Dictionary<string, object>
            {
                ["type"] = thinking ? "enabled" : "disabled",
            },
        };

        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools;
            payload["tool_choice"] = "auto";
        }

        var body = JsonSerializer.Serialize(payload, Core.AppJson.Wire);
        var url = _cfg.BaseUrl.TrimEnd('/') + "/chat/completions";
        var attempts = Math.Max(0, _cfg.MaxRetries) + 1;
        string lastError = "未知错误";

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var sw = Stopwatch.StartNew();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_cfg.TimeoutSeconds));

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);

                using var resp = await Http.SendAsync(req, cts.Token);
                var text = await resp.Content.ReadAsStringAsync(cts.Token);
                sw.Stop();

                if (resp.IsSuccessStatusCode)
                    return Parse(text, (int)sw.ElapsedMilliseconds);

                lastError = $"HTTP {(int)resp.StatusCode}: {Truncate(text, 200)}";

                // 4xx（除 429）重试无意义
                if ((int)resp.StatusCode < 500 && resp.StatusCode != HttpStatusCode.TooManyRequests)
                    throw new LlmException(Sanitize(lastError));

                _log.LogWarning("DeepSeek 调用失败（{Attempt}/{Total}）：{Error}", attempt, attempts, Sanitize(lastError));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = $"请求超时（{_cfg.TimeoutSeconds}s）";
                _log.LogWarning("DeepSeek 超时（{Attempt}/{Total}）", attempt, attempts);
            }
            catch (HttpRequestException ex)
            {
                lastError = $"网络错误：{ex.Message}";
                _log.LogWarning("DeepSeek 网络错误（{Attempt}/{Total}）：{Error}", attempt, attempts, ex.Message);
            }

            if (attempt < attempts)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt - 1), 4)), ct);
        }

        throw new LlmException(Sanitize(lastError));
    }

    private LlmResult Parse(string text, int latencyMs)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new LlmException($"模型返回不是合法 JSON：{Truncate(text, 200)}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
                throw new LlmException($"模型返回缺少 choices：{Truncate(text, 200)}");

            var message = choices[0].TryGetProperty("message", out var m) ? m : default;
            var content = message.ValueKind == JsonValueKind.Object
                          && message.TryGetProperty("content", out var c)
                          && c.ValueKind == JsonValueKind.String
                ? (c.GetString() ?? "").Trim()
                : "";

            var calls = new List<ToolCall>();
            if (message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("tool_calls", out var tcs)
                && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    var fn = tc.TryGetProperty("function", out var f) ? f : default;
                    calls.Add(new ToolCall
                    {
                        Id = tc.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        Type = tc.TryGetProperty("type", out var ty) ? ty.GetString() ?? "function" : "function",
                        Function = new ToolFunctionCall
                        {
                            Name = fn.ValueKind == JsonValueKind.Object && fn.TryGetProperty("name", out var n)
                                ? n.GetString() ?? ""
                                : "",
                            Arguments = fn.ValueKind == JsonValueKind.Object
                                        && fn.TryGetProperty("arguments", out var a)
                                        && a.ValueKind == JsonValueKind.String
                                ? a.GetString() ?? "{}"
                                : "{}",
                        },
                    });
                }
            }

            var usage = new LlmUsage();
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                usage.PromptTokens = IntOf(u, "prompt_tokens");
                usage.CompletionTokens = IntOf(u, "completion_tokens");
                usage.TotalTokens = IntOf(u, "total_tokens");
                if (u.TryGetProperty("completion_tokens_details", out var d) && d.ValueKind == JsonValueKind.Object)
                    usage.ReasoningTokens = IntOf(d, "reasoning_tokens");
            }

            if (content.Length == 0 && calls.Count == 0)
            {
                if (usage.ReasoningTokens > 0)
                    throw new LlmException(
                        $"模型只产出思考内容（reasoning_tokens={usage.ReasoningTokens}），正文为空：请确认 thinking 已关闭或提高 max_tokens");
                throw new LlmException("模型返回正文为空");
            }

            return new LlmResult
            {
                Content = content,
                ToolCalls = calls,
                Usage = usage,
                LatencyMs = latencyMs,
                Model = root.TryGetProperty("model", out var mo) ? mo.GetString() ?? _cfg.Model : _cfg.Model,
            };
        }
    }

    private static int IntOf(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>
    /// 日志与异常里不出现完整密钥
    /// </summary>
    private static string Sanitize(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, "sk-[0-9a-zA-Z]{8,}", "sk-***");

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        // 超时由每次请求的 CancellationToken 控制，HttpClient 本身不设死
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}

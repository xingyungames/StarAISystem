using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using StarGameAI.Core;

namespace StarGameAI.Gateway;

public sealed class SendResult
{
    public bool Ok { get; set; }
    public string MessageId { get; set; } = "";
    public string Error { get; set; } = "";
}

/// <summary>
/// 出站：调 NapCat 的 /send_group_msg 与 /send_private_msg
/// </summary>
public sealed class Sender
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    private readonly Env _env;
    private readonly ILogger<Sender> _log;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, long> _lastSendAt = new();
    private bool _warnedNoToken;

    public Sender(Env env, ILogger<Sender> log)
    {
        _env = env;
        _log = log;
    }

    /// <summary>
    /// 按段落切分，保证单段不超长
    /// </summary>
    public List<string> Split(string text)
    {
        var max = Math.Max(80, _env.Server.Ratelimit.ReplyMaxChars);
        var maxSegments = Math.Max(1, _env.Server.Ratelimit.ReplyMaxSegments);

        var body = (text ?? "").Trim();
        if (body.Length == 0) return new List<string>();
        if (body.Length <= max) return new List<string> { body };

        var parts = new List<string>();
        var rest = body;
        while (rest.Length > 0 && parts.Count < maxSegments)
        {
            if (rest.Length <= max) { parts.Add(rest); break; }

            var cut = rest.LastIndexOfAny(new[] { '\n', '。', '！', '？', '；', '.', '!', '?' }, max - 1, max);
            if (cut < max / 3) cut = max;
            parts.Add(rest[..(cut + 1)].Trim());
            rest = rest[(cut + 1)..].TrimStart();
        }

        if (rest.Length > 0 && parts.Count > 0)
            parts[^1] += " …";

        return parts;
    }

    /// <summary>
    /// 把一段回复切成多条消息
    /// </summary>
    public List<string> Segment(string text)
    {
        var body = (text ?? "").Replace("\r\n", "\n").Trim();
        if (body.Length == 0) return new List<string>();

        var cap = Math.Max(1, _env.Server.Ratelimit.ReplyMaxSegments);

        var blocks = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(b => b.Trim())
            .Where(b => b.Length > 0)
            .ToList();

        if (blocks.Count == 0) blocks.Add(body);

        var parts = new List<string>();
        foreach (var b in blocks)
            foreach (var sub in Split(b))
                if (sub.Trim().Length > 0) parts.Add(sub.Trim());

        // 超过上限就把多余的并进最后一条，避免把一句话拆成碎片
        if (parts.Count > cap)
        {
            var head = parts.Take(cap - 1).ToList();
            head.Add(string.Join("\n", parts.Skip(cap - 1)));
            return head;
        }

        return parts;
    }

    /// <summary>
    /// 发送一段或多段文本（每段是一条独立的 QQ 消息）
    /// </summary>
    public async Task<SendResult> SendTextAsync(string scope, string text, string? replyTo, CancellationToken ct)
    {
        var results = await SendSegmentsAsync(scope, Split(text), replyTo, ct);
        return results.Count > 0 ? results[^1] : new SendResult { Ok = false, Error = "内容为空" };
    }

    /// <summary>
    /// 逐条发送多段，返回每一条的结果
    /// </summary>
    public async Task<List<SendResult>> SendSegmentsAsync(
        string scope, List<string> parts, string? replyTo, CancellationToken ct)
    {
        var results = new List<SendResult>();
        if (parts.Count == 0)
        {
            results.Add(new SendResult { Ok = false, Error = "内容为空" });
            return results;
        }

        var delay = Math.Clamp(_env.Idea.Burst.SegmentDelayMs, 0, 5000);

        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0 && delay > 0)
            {
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }
            }

            await ThrottleAsync(scope, ct);

            var segments = new List<object>();
            if (i == 0 && !string.IsNullOrEmpty(replyTo))
                segments.Add(new { type = "reply", data = new { id = replyTo } });
            segments.Add(new { type = "text", data = new { text = parts[i] } });

            var one = await SendRawAsync(scope, segments, ct);
            results.Add(one);

            // 发失败就停：后续段不再发，避免半句话 + 风控
            if (!one.Ok) break;
        }

        return results;
    }

    public async Task<SendResult> SendRawAsync(string scope, List<object> segments, CancellationToken ct)
    {
        var isPrivate = scope.StartsWith("private:", StringComparison.Ordinal);
        var isGroup = scope.StartsWith("group:", StringComparison.Ordinal);
        // 裸号不认。下面默认猜成群，于是「私聊某人」会被当成「往群号=某人QQ 的群里发」
        if (!isPrivate && !isGroup)
        {
            _log.LogError("落点缺少 private:/group: 前缀，拒绝发送：{Scope}", scope);
            return new SendResult { Ok = false, Error = "落点缺少前缀：" + scope };
        }

        var target = scope[(scope.IndexOf(':') + 1)..];
        var endpoint = isPrivate ? "/send_private_msg" : "/send_group_msg";

        var payload = isPrivate
            ? (object)new Dictionary<string, object> { ["user_id"] = target, ["message"] = segments }
            : new Dictionary<string, object> { ["group_id"] = target, ["message"] = segments };

        var url = _env.Server.Napcat.ApiBase.TrimEnd('/') + endpoint;
        var token = _env.Secrets.Napcat.Token;
        string lastError = "未知错误";

        if (string.IsNullOrEmpty(token) && !_warnedNoToken)
        {
            _warnedNoToken = true;
            _log.LogError("config/secrets.json 的 napcat.token 为空，出站请求不会带鉴权，" +
                          "NapCat 会返回 403 token verify failed");
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload, AppJson.Options),
                        Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrEmpty(token))
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

                using var resp = await Http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (resp.IsSuccessStatusCode)
                {
                    var parsed = TryParse(body);
                    if (parsed.Ok)
                    {
                        _lastSendAt[scope] = Environment.TickCount64;
                        return parsed;
                    }

                    lastError = parsed.Error;
                }
                else
                {
                    lastError = $"HTTP {(int)resp.StatusCode}: {Trim(body, 160)}";

                    // 4xx 是鉴权/参数错，重试没有意义，直接放弃
                    if ((int)resp.StatusCode < 500)
                    {
                        _log.LogError("发送被拒绝（不重试）scope={Scope}：{Error}", scope, lastError);
                        return new SendResult { Ok = false, Error = lastError };
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = "发送超时";
            }
            catch (HttpRequestException ex)
            {
                lastError = "网络错误：" + ex.Message;
            }

            _log.LogWarning("发送失败（{Attempt}/3）scope={Scope}：{Error}", attempt, scope, lastError);
            if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(attempt * 1000), ct);
        }

        // 绝不补发
        return new SendResult { Ok = false, Error = lastError };
    }

    private static SendResult TryParse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var retcode = root.TryGetProperty("retcode", out var rc) && rc.TryGetInt32(out var r) ? r : -1;
            var status = root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                ? st.GetString() ?? "" : "";

            // NapCat 有的接口返回 retcode=0，有的返回 200（HTTP 风格码），
            // 写死 retcode==0 会把成功的发送判成失败
            var ok = string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) || retcode is 0 or 200;
            if (!ok)
            {
                var msg = root.TryGetProperty("message", out var m) ? m.ToString() : "";
                return new SendResult { Ok = false, Error = $"status={status} retcode={retcode} {Trim(msg, 120)}".Trim() };
            }

            var id = "";
            if (root.TryGetProperty("data", out var data))
            {
                if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("message_id", out var mid))
                    id = OneBot.IdOf(mid);
                else
                    id = OneBot.IdOf(data);
            }

            return new SendResult { Ok = true, MessageId = id };
        }
        catch (JsonException)
        {
            return new SendResult { Ok = false, Error = "返回不是合法 JSON" };
        }
    }

    private async Task ThrottleAsync(string scope, CancellationToken ct)
    {
        var perSecond = Math.Max(1, _env.Server.Ratelimit.PerGroupPerSecond);
        var gate = _gates.GetOrAdd(scope, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_lastSendAt.TryGetValue(scope, out var last))
            {
                var elapsed = Environment.TickCount64 - last;
                var need = 1000L / perSecond - elapsed;
                if (need > 0) await Task.Delay((int)need, ct);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
}

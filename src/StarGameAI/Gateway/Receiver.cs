using System.Text.Json;
using System.Threading.Channels;
using StarGameAI.Core;
using StarGameAI.Pipeline;

namespace StarGameAI.Gateway;

public sealed class Inbound
{
    public ParsedMessage Message { get; set; } = new();
    public string Trace { get; set; } = "";
}

/// <summary>OneBot 11 事件接收。</summary>
public sealed class Receiver
{
    private readonly Env _env;
    private readonly Dedup _dedup;
    private readonly ChatLog _chatLog;
    private readonly Metrics _metrics;
    private readonly Core.Activity _activity;
    private readonly Core.Contacts _contacts;
    private readonly Core.Presence _presence;
    private readonly ILogger<Receiver> _log;
    private readonly Channel<Inbound> _queue;
    private int _pending;

    public Receiver(Env env, Dedup dedup, ChatLog chatLog, Metrics metrics,
        Core.Activity activity, Core.Contacts contacts, Core.Presence presence, ILogger<Receiver> log)
    {
        _env = env;
        _dedup = dedup;
        _chatLog = chatLog;
        _metrics = metrics;
        _activity = activity;
        _contacts = contacts;
        _presence = presence;
        _log = log;
        _queue = Channel.CreateBounded<Inbound>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public ChannelReader<Inbound> Reader => _queue.Reader;

    public int QueueDepth => Volatile.Read(ref _pending);

    /// <summary>由消费者在处理完一条后调用，仅用于后台展示队列深度。</summary>
    public void OnDequeued() => Interlocked.Decrement(ref _pending);

    /// <summary>NapCat 的 HTTP 上报**不發 Authorization**，而是带两个头：</summary>
    public bool CheckAuth(IHeaderDictionary headers, byte[] body)
    {
        var expected = _env.Secrets.Report.Token;
        if (string.IsNullOrEmpty(expected)) return true;

        var signature = headers["x-signature"].ToString();
        if (!string.IsNullOrEmpty(signature))
        {
            var hex = signature.StartsWith("sha1=", StringComparison.OrdinalIgnoreCase)
                ? signature[5..].Trim()
                : signature.Trim();

            var computed = Convert.ToHexString(
                System.Security.Cryptography.HMACSHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes(expected), body)).ToLowerInvariant();

            return string.Equals(hex.ToLowerInvariant(), computed, StringComparison.Ordinal);
        }

        var raw = headers["Authorization"].ToString();
        if (!string.IsNullOrEmpty(raw))
        {
            var token = raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? raw[7..].Trim() : raw.Trim();
            return string.Equals(token, expected, StringComparison.Ordinal);
        }

        _log.LogWarning("上报缺少 x-signature 与 Authorization，已拒绝。收到的头：{Headers}",
            string.Join(",", headers.Keys));
        return false;
    }

    /// <summary>返回 null 表示已接受；返回字符串表示应当以该原因拒绝/忽略。</summary>
    public string? Handle(string body)
    {
        OneBotPayload? payload;
        try
        {
            // OneBot 11 的事件字段是 snake_case（post_type / message_id / group_id…），
            // 必须用 snake 策略解析，用 camelCase 会全部映射不上。
            payload = JsonSerializer.Deserialize<OneBotPayload>(body, AppJson.Config);
        }
        catch (JsonException ex)
        {
            _log.LogWarning("事件 JSON 解析失败：{Error}", ex.Message);
            return "bad_json";
        }

        if (payload is null) return "empty";

        // 只处理消息事件
        if (!string.Equals(payload.PostType, "message", StringComparison.OrdinalIgnoreCase))
        {
            _metrics.Inc("event.other");
            return "not_message";
        }

        var selfId = OneBot.IdOf(payload.SelfId);
        var userId = OneBot.IdOf(payload.UserId);

        // 跳过自己（reportSelfMessage=false 时不会有，但防御）
        if (userId.Length > 0 && userId == (_env.Server.SelfId.Length > 0 ? _env.Server.SelfId : selfId))
        {
            _metrics.Inc("event.self");
            return "self";
        }

        var messageId = OneBot.IdOf(payload.MessageId);
        if (!_dedup.TryAdd(messageId))
        {
            _metrics.Inc("event.dup");
            return "dup";
        }

        var groupId = OneBot.IdOf(payload.GroupId);
        var isPrivate = string.Equals(payload.MessageType, "private", StringComparison.OrdinalIgnoreCase)
                        || groupId.Length == 0;

        var scope = isPrivate ? "private:" + userId : "group:" + groupId;

        var (text, briefs, addressed, replyTo) = OneBot.Flatten(payload.Message, selfId);
        if (text.Length == 0) text = "[非文本消息]";

        var name = payload.Sender?.Card is { Length: > 0 } card
            ? card
            : payload.Sender?.Nickname is { Length: > 0 } nick ? nick : userId;

        var entry = new LogEntry
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            EventTs = payload.Time,
            MessageId = messageId,
            TraceId = "t_" + Guid.NewGuid().ToString("N")[..12],
            Scope = scope,
            UserId = userId,
            Name = name,
            Role = payload.Sender?.Role ?? "member",
            IsSelf = false,
            Text = text,
            Segments = briefs,
            InReplyTo = replyTo,
        };

        // 日志层：不管后面怎么处理，先落盘（I11）
        try
        {
            _chatLog.Append(entry);
            _metrics.Inc("event.messages");
            _activity.TouchMessage();

            // 维护 QQ 号 ↔ 名字的映射，供工具反查；同时记下这个人出现在哪些场景
            _contacts.Touch(userId, name, scope);

            // 在线状态：先记活动，再从话里识别「我要睡了 / 在上课」这类自报
            _presence.TouchActivity(userId);
            if (_presence.TryDetectSelfReport(text, out var online, out var note, out var ttl))
            {
                _presence.ReportSelf(userId, online, note, ttl);
                _log.LogInformation("识别到在线自报 {User} → {State}（{Note}，{Ttl}h）",
                    userId, online ? "在线" : "离线", note, ttl);
                _metrics.Inc("presence.self_report");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "写日志失败 scope={Scope}", scope);
        }

        // 白名单：不在名单里的只记日志、不回复
        if (!IsWhitelisted(isPrivate, userId, groupId))
        {
            _metrics.Inc("event.filtered");
            return "filtered";
        }

        var msg = new ParsedMessage
        {
            MessageId = messageId,
            Scope = scope,
            UserId = userId,
            Name = name,
            Role = entry.Role,
            GroupId = groupId,
            Text = text,
            InReplyTo = replyTo,
            Addressed = addressed,
            Segments = briefs,
            Ts = entry.Ts,
        };

        if (!_queue.Writer.TryWrite(new Inbound { Message = msg, Trace = entry.TraceId }))
        {
            _log.LogWarning("队列已满，丢弃 {Scope}/{Id}", scope, messageId);
            _metrics.Inc("queue.dropped");
            return "queue_full";
        }

        Interlocked.Increment(ref _pending);
        return null;
    }

    private bool IsWhitelisted(bool isPrivate, string userId, string groupId)
    {
        var wl = _env.Server.Whitelist;

        // 两个名单都空 = 不设限
        if (wl.Groups.Count == 0 && wl.Private.Count == 0) return true;

        return isPrivate
            ? wl.Private.Count == 0 || wl.Private.Contains(userId)
            : wl.Groups.Count == 0 || wl.Groups.Contains(groupId);
    }
}

/// <summary>队列消费者：真正干活的地方，串行避免同会话并发抢发。</summary>
public sealed class InboundWorker : BackgroundService
{
    private readonly Receiver _receiver;
    private readonly ChatPipeline _pipeline;
    private readonly ILogger<InboundWorker> _log;
    private readonly SemaphoreSlim _perScope = new(1, 1);

    public InboundWorker(Receiver receiver, ChatPipeline pipeline, ILogger<InboundWorker> log)
    {
        _receiver = receiver;
        _pipeline = pipeline;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _receiver.Reader.ReadAllAsync(stoppingToken))
        {
            _receiver.OnDequeued();
            try
            {
                await _perScope.WaitAsync(stoppingToken);
                try
                {
                    await _pipeline.ProcessAsync(item.Message, item.Trace, stoppingToken);
                }
                finally
                {
                    _perScope.Release();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "处理消息失败 scope={Scope} id={Id}", item.Message.Scope, item.Message.MessageId);
            }
        }
    }
}

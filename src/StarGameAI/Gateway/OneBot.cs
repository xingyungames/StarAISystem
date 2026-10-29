using System.Text.Json;

namespace StarGameAI.Gateway;

/// <summary>消息段摘要，只保留类型与人可读的简写，不存二进制。</summary>
public sealed class SegBrief
{
    public string T { get; set; } = "text";
    public string? Brief { get; set; }
    public string? Url { get; set; }
}

public sealed class OneBotPayload
{
    public long Time { get; set; }
    public string? PostType { get; set; }
    public string? MessageType { get; set; }
    public string? SubType { get; set; }
    public JsonElement? SelfId { get; set; }
    public JsonElement? UserId { get; set; }
    public JsonElement? GroupId { get; set; }
    public JsonElement? MessageId { get; set; }
    public List<OneBotSegment>? Message { get; set; }
    public string? RawMessage { get; set; }
    public OneBotSender? Sender { get; set; }
}

public sealed class OneBotSegment
{
    public string? Type { get; set; }
    public JsonElement Data { get; set; }
}

public sealed class OneBotSender
{
    public JsonElement? UserId { get; set; }
    public string? Nickname { get; set; }
    public string? Card { get; set; }
    public string? Role { get; set; }
}

public sealed class ParsedMessage
{
    public string MessageId { get; set; } = "";
    public string Scope { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "member";
    public string GroupId { get; set; } = "";
    public string Text { get; set; } = "";
    public string InReplyTo { get; set; } = "";
    public bool Addressed { get; set; }
    public List<SegBrief> Segments { get; set; } = new();
    public long Ts { get; set; }
    public bool IsPrivate => Scope.StartsWith("private:", StringComparison.Ordinal);
}

public static class OneBot
{
    public static string IdOf(JsonElement? el)
    {
        if (el is not { } v) return "";
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n.ToString() : v.ToString(),
            JsonValueKind.String => v.GetString() ?? "",
            _ => "",
        };
    }

    private static string Str(JsonElement data, string name)
    {
        if (data.ValueKind != JsonValueKind.Object) return "";
        if (!data.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            _ => "",
        };
    }

    /// <summary>把消息段数组归一化成纯文本 + 摘要。at 段命中 selfId 时判定为「被叫到」。</summary>
    public static (string text, List<SegBrief> briefs, bool addressed, string replyTo) Flatten(
        List<OneBotSegment>? segments, string selfId)
    {
        var sb = new System.Text.StringBuilder();
        var briefs = new List<SegBrief>();
        var addressed = false;
        var replyTo = "";

        foreach (var seg in segments ?? new List<OneBotSegment>())
        {
            var type = seg.Type ?? "unknown";
            var data = seg.Data;

            switch (type)
            {
                case "text":
                    var t = Str(data, "text");
                    sb.Append(t);
                    briefs.Add(new SegBrief { T = "text" });
                    break;

                case "at":
                    var qq = Str(data, "qq");
                    var name = Str(data, "name");
                    if (qq == selfId) addressed = true;
                    sb.Append(qq == "all" ? "@全体成员 " : $"@{name} ");
                    briefs.Add(new SegBrief { T = "at", Brief = qq });
                    break;

                case "reply":
                    replyTo = Str(data, "id");
                    briefs.Add(new SegBrief { T = "reply", Brief = replyTo });
                    break;

                case "image":
                    sb.Append("[图片]");
                    briefs.Add(new SegBrief { T = "image", Brief = "[图片]", Url = Str(data, "url") });
                    break;

                case "face":
                case "mface":
                    sb.Append("[表情]");
                    briefs.Add(new SegBrief { T = "face", Brief = "[表情]" });
                    break;

                case "record":
                    sb.Append("[语音]");
                    briefs.Add(new SegBrief { T = "record", Brief = "[语音]", Url = Str(data, "url") });
                    break;

                case "video":
                    sb.Append("[视频]");
                    briefs.Add(new SegBrief { T = "video", Brief = "[视频]", Url = Str(data, "url") });
                    break;

                case "file":
                    sb.Append("[文件]");
                    briefs.Add(new SegBrief { T = "file", Brief = Str(data, "name") });
                    break;

                case "json":
                case "xml":
                case "markdown":
                case "miniapp":
                    sb.Append("[卡片]");
                    briefs.Add(new SegBrief { T = "card", Brief = "[卡片]" });
                    break;

                case "poke":
                    sb.Append("[戳一戳]");
                    briefs.Add(new SegBrief { T = "poke", Brief = "[戳一戳]" });
                    break;

                case "dice":
                case "rps":
                    sb.Append("[骰子]");
                    briefs.Add(new SegBrief { T = type, Brief = "[骰子]" });
                    break;

                case "forward":
                case "node":
                    sb.Append("[合并转发]");
                    briefs.Add(new SegBrief { T = "forward", Brief = "[合并转发]" });
                    break;

                default:
                    briefs.Add(new SegBrief { T = type, Brief = $"[{type}]" });
                    break;
            }
        }

        return (sb.ToString().Trim(), briefs, addressed, replyTo);
    }
}

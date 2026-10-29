using System.Text;
using System.Text.Json;
using StarGameAI.Core;
using StarGameAI.Gateway;
using StarGameAI.Llm;
using StarGameAI.Pipeline;

namespace StarGameAI.Tools;

public sealed class ToolCallContext
{
    public string Scope { get; init; } = "";
    public string UserId { get; init; } = "";
    public string TraceId { get; init; } = "";
}

/// <summary>内置工具：C# 直接实现，不走 Python 沙箱。</summary>
public sealed class BuiltinTools
{
    private readonly Env _env;
    private readonly Sandbox _sandbox;
    private readonly Sender _sender;
    private readonly ChatLog _chatLog;
    private readonly Dedup _dedup;
    private readonly Contacts _contacts;
    private readonly Presence _presence;
    private readonly ILogger<BuiltinTools> _log;

    public BuiltinTools(Env env, Sandbox sandbox, Sender sender, ChatLog chatLog,
        Dedup dedup, Contacts contacts, Presence presence, ILogger<BuiltinTools> log)
    {
        _env = env;
        _sandbox = sandbox;
        _sender = sender;
        _chatLog = chatLog;
        _dedup = dedup;
        _contacts = contacts;
        _presence = presence;
        _log = log;
        Defs = BuildDefs();
    }

    public IReadOnlyList<ToolDef> Defs { get; }

    /// <summary>判断工具输出算不算失败。</summary>
    public static bool LooksFailed(string? output)
    {
        var s = output ?? "";
        return s.StartsWith("被拒绝", StringComparison.Ordinal)
               || s.StartsWith("工具失败", StringComparison.Ordinal)
               || s.StartsWith("发送失败", StringComparison.Ordinal)
               || s.StartsWith("上传失败", StringComparison.Ordinal)
               || s.StartsWith("戳一戳失败", StringComparison.Ordinal)
               || s.StartsWith("沙箱里没有", StringComparison.Ordinal)
               || s.StartsWith("未知的内置工具", StringComparison.Ordinal);
    }

    public bool IsBuiltin(string name) =>
        name is "sb_list" or "sb_read" or "sb_write" or "sb_mkdir" or "sb_delete" or "sb_move"
            or "qq_find" or "qq_send_private" or "qq_poke"
            or "qq_send_file_private" or "qq_send_file_group";

    private static ToolDef Fn(string name, string desc, Dictionary<string, object> props, params string[] required) =>
        new()
        {
            Function = new ToolFunctionDef
            {
                Name = name,
                Description = desc,
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = props,
                    ["required"] = required,
                },
            },
        };

    private static Dictionary<string, object> P(string type, string desc) =>
        new() { ["type"] = type, ["description"] = desc };

    private static List<ToolDef> BuildDefs() => new()
    {
        Fn("sb_list", "列出沙箱目录里的文件和子目录。path 留空表示沙箱根目录。",
            new() { ["path"] = P("string", "相对沙箱的路径，留空为根目录") }),

        Fn("sb_read", "读取沙箱里的文本文件。",
            new() { ["path"] = P("string", "相对沙箱的文件路径") }, "path"),

        Fn("sb_write", "写入（覆盖）沙箱里的文本文件，父目录会自动创建。",
            new()
            {
                ["path"] = P("string", "相对沙箱的文件路径"),
                ["content"] = P("string", "要写入的完整内容"),
            }, "path", "content"),

        Fn("sb_mkdir", "在沙箱里创建目录。",
            new() { ["path"] = P("string", "相对沙箱的目录路径") }, "path"),

        Fn("sb_delete", "删除沙箱里的文件（目录必须为空）。",
            new() { ["path"] = P("string", "相对沙箱的路径") }, "path"),

        Fn("sb_move", "重命名或移动沙箱里的文件/目录。",
            new()
            {
                ["from"] = P("string", "源路径"),
                ["to"] = P("string", "目标路径"),
            }, "from", "to"),

        Fn("qq_find", "按名字或 QQ 号查人，返回对应的 QQ 号。发消息、戳一戳、发文件之前，" +
                      "先用它确认对方 QQ 号，不要凭印象编。",
            new() { ["query"] = P("string", "名字、别名或 QQ 号；留空则列出最近的联系人") }),

        Fn("qq_send_private", "给指定 QQ 号发一条私聊消息。QQ 号必须先确认过（可用 qq_find 查）。" +
                              "会受发送限速约束，失败会返回原因。",
            new()
            {
                ["user_id"] = P("string", "接收者 QQ 号"),
                ["text"] = P("string", "消息内容"),
            }, "user_id", "text"),

        Fn("qq_poke", "戳一戳指定的人。在群里用要带上 group_id。QQ 号必须先确认过。",
            new()
            {
                ["user_id"] = P("string", "被戳的人 QQ 号"),
                ["group_id"] = P("string", "群号；私聊可不填"),
            }, "user_id"),

        Fn("qq_send_file_private", "把沙箱里的文件作为私聊文件发给某人。",
            new()
            {
                ["user_id"] = P("string", "接收者 QQ 号"),
                ["path"] = P("string", "沙箱内的相对路径"),
                ["name"] = P("string", "发送时显示的文件名，留空用原文件名"),
            }, "user_id", "path"),

        Fn("qq_send_file_group", "把沙箱里的文件上传到群文件。",
            new()
            {
                ["group_id"] = P("string", "群号"),
                ["path"] = P("string", "沙箱内的相对路径"),
                ["name"] = P("string", "上传后的文件名，留空用原文件名"),
            }, "group_id", "path"),
    };

    public async Task<string> InvokeAsync(string name, JsonElement args, ToolCallContext ctx, CancellationToken ct)
    {
        try
        {
            return name switch
            {
                "sb_list" => SbList(Str(args, "path")),
                "sb_read" => SbRead(Str(args, "path")),
                "sb_write" => SbWrite(Str(args, "path"), Str(args, "content")),
                "sb_mkdir" => SbMkdir(Str(args, "path")),
                "sb_delete" => SbDelete(Str(args, "path")),
                "sb_move" => SbMove(Str(args, "from"), Str(args, "to")),
                "qq_send_private" => await QqSendPrivateAsync(Str(args, "user_id"), Str(args, "text"), ctx, ct),
                "qq_poke" => await QqPokeAsync(Str(args, "user_id"), Str(args, "group_id"), ctx, ct),
                "qq_find" => QqFind(Str(args, "query")),
                "qq_send_file_private" => await QqSendFileAsync(false, Str(args, "user_id"),
                    Str(args, "path"), Str(args, "name"), ctx, ct),
                "qq_send_file_group" => await QqSendFileAsync(true, Str(args, "group_id"),
                    Str(args, "path"), Str(args, "name"), ctx, ct),
                _ => $"未知的内置工具：{name}",
            };
        }
        catch (SandboxViolation ex)
        {
            _log.LogWarning("沙箱拒绝：{Error}", ex.Message);
            return "被拒绝：" + ex.Message;
        }
        catch (Exception ex)
        {
            _log.LogWarning("内置工具 {Name} 失败：{Error}", name, ex.Message);
            return "工具失败：" + ex.Message;
        }
    }


    private string SbList(string path)
    {
        var rel = path ?? "";
        var entries = _sandbox.List(rel);
        if (entries.Count == 0) return $"目录 {Describe(rel)} 是空的";

        var sb = new StringBuilder();
        sb.Append("目录 ").AppendLine(Describe(rel));
        foreach (var e in entries)
        {
            sb.Append(e.IsDir ? "  [目录] " : "  [文件] ")
              .Append(e.Name);
            if (!e.IsDir) sb.Append("　").Append(e.Size).Append(" 字节");
            sb.AppendLine();
        }

        sb.Append("共 ").Append(entries.Count).Append(" 项");
        return sb.ToString();
    }

    private string SbRead(string path)
    {
        var (text, truncated, size) = _sandbox.Read(path);
        if (truncated)
            return $"[文件 {size} 字节，已截断只返回前 {Sandbox.ReadLimitBytes} 字节]\n{text}";
        return text.Length == 0 ? "（空文件）" : text;
    }

    private string SbWrite(string path, string content)
    {
        var size = _sandbox.Write(path, content);
        return $"已写入 {Describe(path)}（{size} 字节）";
    }

    private string SbMkdir(string path)
    {
        _sandbox.CreateDirectory(path);
        return "已创建目录 " + Describe(path);
    }

    private string SbDelete(string path)
    {
        return _sandbox.Delete(path) ? "已删除 " + Describe(path) : "没有找到 " + Describe(path);
    }

    private string SbMove(string from, string to)
    {
        _sandbox.Move(from, to);
        return $"已把 {Describe(from)} 移到 {Describe(to)}";
    }

    private static string Describe(string rel) =>
        string.IsNullOrWhiteSpace(rel) ? "（沙箱根目录）" : rel.Replace('\\', '/');


    private async Task<string> QqSendPrivateAsync(string userId, string text, ToolCallContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId)) return "缺少 user_id";
        if (string.IsNullOrWhiteSpace(text)) return "内容为空，不发送";

        var blocked = PresenceBlock(userId.Trim(), ctx);
        if (blocked.Length > 0) return "没有发送：" + blocked + ContactHint(userId);

        var scope = "private:" + userId.Trim();
        var send = await _sender.SendTextAsync(scope, text, null, ct);
        if (!send.Ok) return "发送失败：" + send.Error + ContactHint(userId);

        // 自己发的消息必须回写日志，否则 AI 不记得自己说过什么
        _dedup.Reserve(send.MessageId);
        _chatLog.Append(new LogEntry
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageId = send.MessageId,
            TraceId = ctx.TraceId,
            Scope = scope,
            UserId = _env.Server.SelfId,
            Name = "我",
            Role = "self",
            IsSelf = true,
            Trigger = "tool",
            InReplyTo = "",
            Text = text,
        });

        _log.LogInformation("内置工具私信发出 userId={User} trace={Trace}", userId, ctx.TraceId);

        var who = _contacts.ByQq(userId);
        return $"已发送给 {(who is not null ? who.Name + "（" + userId + "）" : userId)}" + ContactHint(userId);
    }

    private async Task<string> QqPokeAsync(string userId, string groupId, ToolCallContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId)) return "缺少 user_id";

        var blocked = PresenceBlock(userId.Trim(), ctx);
        if (blocked.Length > 0) return "没有戳：" + blocked + ContactHint(userId);

        var payload = new Dictionary<string, object> { ["user_id"] = userId.Trim() };
        if (!string.IsNullOrWhiteSpace(groupId)) payload["group_id"] = groupId.Trim();

        var (ok, detail) = await NapcatActionAsync("/send_poke", payload, ct);
        if (!ok) return "戳一戳失败：" + detail + ContactHint(userId);

        _log.LogInformation("内置工具戳一戳 userId={User} group={Group}", userId, groupId);

        var who = _contacts.ByQq(userId);
        return $"已戳了 {(who is not null ? who.Name + "（" + userId + "）" : userId)}" + ContactHint(userId);
    }

    /// <summary>按名字/QQ 查人：这是「戳一戳」和「私信」的前置步骤。</summary>
    private string QqFind(string query)
    {
        var q = (query ?? "").Trim();

        if (q.Length == 0)
        {
            var all = _contacts.All().Take(15).Select(c => "  " + Contacts.Describe(c, true)).ToList();
            return all.Count == 0
                ? "还没有任何联系人记录（有人跟它说过话之后才会有）"
                : "已知联系人（最近的在前）：\n" + string.Join("\n", all);
        }

        var hits = _contacts.Search(q);
        if (hits.Count == 0)
        {
            var samples = _contacts.All().Take(5).Select(c => "  " + Contacts.Describe(c, true)).ToList();
            return $"没找到匹配「{q}」的人。\n" +
                   (samples.Count > 0 ? "已知联系人：\n" + string.Join("\n", samples) : "还没有任何联系人记录");
        }

        return "找到：\n" + string.Join("\n", hits.Select(c => "  " + Contacts.Describe(c, true)));
    }

    private async Task<string> QqSendFileAsync(bool toGroup, string target, string path, string name,
        ToolCallContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target)) return toGroup ? "缺少 group_id" : "缺少 user_id";
        if (string.IsNullOrWhiteSpace(path)) return "缺少 path（沙箱内的相对路径）";

        if (!toGroup)
        {
            var blocked = PresenceBlock(target.Trim(), ctx);
            if (blocked.Length > 0) return "没有发送：" + blocked;
        }

        // 只允许发沙箱里的文件：否则一个注入就能把 config/secrets.json 发出去
        string full;
        try
        {
            full = _sandbox.Resolve(path);
        }
        catch (SandboxViolation ex)
        {
            return "被拒绝：" + ex.Message;
        }

        if (!File.Exists(full)) return $"沙箱里没有这个文件：{path}。先用 sb_list 看看有什么。";

        var fileName = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(full) : name.Trim();
        var payload = new Dictionary<string, object>
        {
            [toGroup ? "group_id" : "user_id"] = target.Trim(),
            ["file"] = full,
            ["name"] = fileName,
            ["upload_file"] = true,
        };

        var (ok, detail) = await NapcatActionAsync(
            toGroup ? "/upload_group_file" : "/upload_private_file", payload, ct);

        if (!ok) return "上传失败：" + detail;

        _log.LogInformation("内置工具上传文件 {File} → {Target}", fileName, target);
        return $"已把 {fileName} 上传到{(toGroup ? "群 " : " ")}{target}";
    }

    //坑：NapCat 有的接口返 retcode=0、有的返 200，只看 0 会把成功判成失败
    /// <summary>调 NapCat 的 action 接口。</summary>
    private async Task<(bool ok, string detail)> NapcatActionAsync(
        string endpoint, Dictionary<string, object> payload, CancellationToken ct)
    {
        var url = _env.Server.Napcat.ApiBase.TrimEnd('/') + endpoint;

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, AppJson.Options),
                Encoding.UTF8, "application/json"),
        };

        var token = _env.Secrets.Napcat.Token;
        if (!string.IsNullOrEmpty(token))
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            return (false, $"HTTP {(int)resp.StatusCode}：{Trim(body, 120)}");

        if (string.IsNullOrWhiteSpace(body)) return (true, "");   // 空体当成功

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? "" : "";
            var retcode = root.TryGetProperty("retcode", out var rc) && rc.TryGetInt32(out var r) ? r : -1;
            var message = root.TryGetProperty("message", out var m) ? m.ToString() : "";
            var wording = root.TryGetProperty("wording", out var w) ? w.ToString() : "";

            var ok = string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                     || retcode is 0 or 200;

            if (ok) return (true, "");

            return (false, $"status={status} retcode={retcode} {Trim(message, 80)} {Trim(wording, 60)}".Trim());
        }
        catch
        {
            return (true, "");   // 不是 JSON 就当成功，别把能用的接口判死
        }
    }

    /// <summary>主动找某个人之前的在线状态检查。</summary>
    private string PresenceBlock(string userId, ToolCallContext ctx)
    {
        if (userId == ctx.UserId) return "";

        var v = _presence.Effective(userId);
        if (v.CanDisturb) return "";

        return $"对方当前判为 {v.State}（{v.Source}：{v.Note}），不允许主动打扰。"
               + "可以等他上线，或者先把想说的记下来。";
    }

    /// <summary>目标 QQ 不在联系人里时，给模型一句可自查的提示。</summary>
    private string ContactHint(string userId)    {
        if (_contacts.ByQq(userId) is not null) return "";

        var samples = _contacts.All().Take(5).Select(c => Contacts.Describe(c)).ToList();
        var hint = $"\n（注意：QQ {userId} 不在已知联系人里，可能号写错了。可以先用 qq_find 查）";
        if (samples.Count > 0) hint += "\n已知联系人：" + string.Join("；", samples);
        return hint;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];


    private static string Str(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object) return "";
        if (!args.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            _ => "",
        };
    }
}

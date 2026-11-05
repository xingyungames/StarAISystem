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

/// <summary>
/// 内置工具：C# 直接实现，不走 Python 沙箱
/// </summary>
public sealed class BuiltinTools
{
    private readonly Env _env;
    private readonly Sandbox _sandbox;
    private readonly Sender _sender;
    private readonly ChatLog _chatLog;
    private readonly Dedup _dedup;
    private readonly Contacts _contacts;
    private readonly Presence _presence;
    private readonly RecallService _recall;
    private readonly RecallSession _recallSession;
    private readonly ToolTrace _toolTrace;
    private readonly NapcatApi _napcat;
    private readonly DeepSeekClient _llm;
    private readonly ILogger<BuiltinTools> _log;

    public BuiltinTools(Env env, Sandbox sandbox, Sender sender, ChatLog chatLog,
        Dedup dedup, Contacts contacts, Presence presence,
        RecallService recall, RecallSession recallSession, ToolTrace toolTrace,
        NapcatApi napcat, DeepSeekClient llm, ILogger<BuiltinTools> log)
    {
        _env = env;
        _sandbox = sandbox;
        _sender = sender;
        _chatLog = chatLog;
        _dedup = dedup;
        _contacts = contacts;
        _presence = presence;
        _recall = recall;
        _recallSession = recallSession;
        _toolTrace = toolTrace;
        _napcat = napcat;
        _llm = llm;
        _log = log;
        Defs = BuildDefs();
    }

    public IReadOnlyList<ToolDef> Defs { get; }

    /// <summary>
    /// 判断工具输出算不算失败
    /// </summary>
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
            or "qq_send_file_private" or "qq_send_file_group"
            or "history_recall" or "qq_api" or "qq_see" or "emoji_add" or "emoji_list" or "emoji_send";

    /// <summary>
    /// qq_api 允许调的只读接口。危险的一个都不放
    /// </summary>
    private const string QqAllowList =
        "- get_image：拿图片的本地路径和下载链接（参数 file 或 file_id）\n" +
        "- fetch_ptt_text：把语音转成文字，不花模型 token（参数 message_id）\n" +
        "- ocr_image：识别图片里的文字，只认字不认画（参数 image）\n" +
        "- get_group_info：群基本信息（参数 group_id）\n" +
        "- get_group_member_info：单个群成员（参数 group_id、user_id）\n" +
        "- get_stranger_info：非好友的资料（参数 user_id）\n" +
        "- get_group_member_list：群成员列表（参数 group_id）——返回很大，别随便调\n" +
        "- _get_group_notice：群公告（参数 group_id）\n" +
        "- get_essence_msg_list：群精华（参数 group_id）\n" +
        "- get_group_honor_info：群荣誉如龙王（参数 group_id）\n" +
        "- nc_get_user_status：对方在线状态（参数 user_id）\n" +
        "- get_recent_contact：最近会话\n" +
        "- get_group_msg_history：群历史消息（参数 group_id、message_seq、count）\n" +
        "- get_friend_msg_history：私聊历史消息（参数 user_id、message_seq、count）";

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

        Fn("history_recall", "翻自己以前的聊天留档，把原话找回来。\n" +
                             "只在**你确实想不起来**的时候用：先看记忆里有没有，没有再用它。\n" +
                             "query 写用户的原话最好，比如「你还记得那个暗号吗」。\n" +
                             "翻不到就老实说不记得，不要编。",
            new()
            {
                ["query"] = P("string", "要查的那件事，尽量用用户的原话"),
                ["days"] = P("number", "往回翻多少天，留空默认 30，最多看配置上限"),
            }, "query"),

        Fn("qq_api", "查 QQ 的信息。只能调下面这些只读接口，别的都不行：\n" + QqAllowList,
            new()
            {
                ["action"] = P("string", "接口名，例如 get_image / fetch_ptt_text"),
                ["params"] = P("object", "接口参数，例如 {\"message_id\":\"123\"}"),
            }, "action"),

        Fn("qq_see", "看一张图并说出里面有什么。\n" +
                     "**对话里出现 [图片](img-xxxxxxxx) 时，把这个 img-xxxxxxxx 原样传进来就能看那一张**。\n" +
                     "只看见 [图片] 没有编号，说明那张图取不到文件，直接问对方说了什么。\n" +
                     "每张图大概 1000 token，只在真需要看的时候用。",
            new()
            {
                ["image"] = P("string", "图片编号 img-xxxxxxxx，或 http 链接，或沙箱内路径"),
                ["question"] = P("string", "想问什么，留空就让它描述图里有什么"),
                ["low"] = P("boolean", "true 表示用低分辨率（更便宜更快）"),
            }, "image"),

        Fn("emoji_add", "把一张图存进自己的表情库，并给它写一条说明，以后想发的时候能查。",
            new()
            {
                ["image"] = P("string", "图片编号 img-xxxxxxxx，或 http 链接，或沙箱内路径"),
                ["name"] = P("string", "给它起的名字，之后用它找这张图"),
                ["meaning"] = P("string", "什么时候用、什么意思；留空就自己看图写一条"),
            }, "image", "name"),

        Fn("emoji_list", "看自己的表情库里有哪些表情、各是什么意思。想发表情之前先查这个。",
            new()),

        Fn("emoji_send", "在当前会话里发一个表情库里的表情。名字要先在 emoji_list 里查准。",
            new()
            {
                ["name"] = P("string", "表情名字，必须和库里的一致"),
            }, "name"),
    };

    public async Task<string> InvokeAsync(string name, JsonElement args, ToolCallContext ctx, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string output;

        try
        {
            output = await InvokeCoreAsync(name, args, ctx, ct);
        }
        catch (SandboxViolation ex)
        {
            _log.LogWarning("沙箱拒绝：{Error}", ex.Message);
            output = "被拒绝：" + ex.Message;
        }
        catch (Exception ex)
        {
            _log.LogWarning("内置工具 {Name} 失败：{Error}", name, ex.Message);
            output = "工具失败：" + ex.Message;
        }

        sw.Stop();

        try
        {
            _toolTrace.Append(name, args.ValueKind == JsonValueKind.Undefined ? "" : args.GetRawText(),
                !LooksFailed(output), (int)sw.ElapsedMilliseconds, output, ctx.Scope, ctx.TraceId);
        }
        catch { /* 记账失败不影响结果 */ }

        return output;
    }

    private async Task<string> InvokeCoreAsync(string name, JsonElement args, ToolCallContext ctx, CancellationToken ct)
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
            "history_recall" => await HistoryRecallAsync(Str(args, "query"), Int(args, "days"), ctx, ct),
            "qq_api" => await QqApiAsync(Str(args, "action"), args, ct),
            "qq_see" => await QqSeeAsync(Str(args, "image"), Str(args, "question"),
                Bool(args, "low"), ct),
            "emoji_add" => await EmojiAddAsync(Str(args, "image"), Str(args, "name"),
                Str(args, "meaning"), ct),
            "emoji_list" => EmojiList(),
            "emoji_send" => await EmojiSendAsync(Str(args, "name"), ctx, ct),
            _ => $"未知的内置工具：{name}",
        };
    }

    private string EmojiCatalogPath => Path.Combine(_env.WorkspaceDir, "emoji", "catalog.json");

    private Dictionary<string, JsonElement> LoadCatalog()
    {
        try
        {
            if (!File.Exists(EmojiCatalogPath)) return new Dictionary<string, JsonElement>();
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(EmojiCatalogPath), Core.AppJson.Config);
            return doc ?? new Dictionary<string, JsonElement>();
        }
        catch
        {
            return new Dictionary<string, JsonElement>();
        }
    }

    /// <summary>
    /// 看库里有什么表情
    /// </summary>
    private string EmojiList()
    {
        var all = LoadCatalog();
        if (all.Count == 0) return "表情库还是空的。收到合适的图可以用 emoji_add 存进来。";

        var sb = new StringBuilder();
        sb.AppendLine("表情库（" + all.Count + " 个）：");

        foreach (var kv in all.Take(30))
        {
            var desc = kv.Value.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String
                ? (d.GetString() ?? "")
                : "";

            sb.Append("- ").Append(kv.Key);
            if (desc.Length > 0) sb.Append("：").Append(desc);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 发表情。图片段用 base64 直传，不依赖本地路径对 NapCat 可见
    /// </summary>
    private async Task<string> EmojiSendAsync(string name, ToolCallContext ctx, CancellationToken ct)
    {
        var key = (name ?? "").Trim();
        if (key.Length == 0) return "工具失败：要发哪个表情？先用 emoji_list 看有哪些";

        var scope = (ctx.Scope ?? "").Trim();
        if (scope.Length == 0) return "工具失败：不知道当前在哪个会话";

        var all = LoadCatalog();
        if (!all.TryGetValue(key, out var item))
        {
            var near = all.Keys.Where(k => k.Contains(key, StringComparison.Ordinal)
                                           || key.Contains(k, StringComparison.Ordinal)).Take(3).ToList();
            return near.Count > 0
                ? $"工具失败：没有叫「{key}」的表情。相近的有：{string.Join("、", near)}"
                : $"工具失败：没有叫「{key}」的表情。先用 emoji_list 看有哪些";
        }

        var rel = item.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String
            ? (f.GetString() ?? "")
            : "";

        if (rel.Length == 0) return "工具失败：这条表情没记文件";

        var path = Path.Combine(_env.WorkspaceDir, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return "工具失败：表情文件不在了（" + rel + "）";

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (bytes.Length > MaxImageBytes) return "工具失败：这张图太大，发不出去";

            var ext = Path.GetExtension(path).ToLowerInvariant();
            var mime = ext switch
            {
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/jpeg",
            };

            var payload = "base64://" + Convert.ToBase64String(bytes);
            var r = await _sender.SendImageAsync(scope, payload, ct);

            if (!r.Ok) return "工具失败：发不出去（" + r.Error + "）";
            return "发出去了：" + key;
        }
        catch (Exception ex)
        {
            return "工具失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 调一个 OneBot 只读接口。白名单之外一律拒绝
    /// </summary>
    private async Task<string> QqApiAsync(string action, JsonElement args, CancellationToken ct)
    {
        var name = (action ?? "").Trim().TrimStart('/');
        if (name.Length == 0) return "工具失败：没给接口名";

        if (!AllowedActions.Contains(name))
            return $"工具失败：{name} 不在允许列表里。能用的只有：" + QqAllowList;

        var payload = new Dictionary<string, object?>();
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("params", out var p)
            && p.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in p.EnumerateObject()) payload[prop.Name] = ToClr(prop.Value);
        }

        // get_image / ocr_image 的 image/file 参数也支持用编号
        foreach (var key in new[] { "image", "file" })
        {
            if (!payload.TryGetValue(key, out var v)) continue;
            var s = v?.ToString() ?? "";
            if (!Core.MediaRef.LooksLikeId(s)) continue;

            var url = Core.MediaRef.Get(_env, s);
            if (!string.IsNullOrEmpty(url)) payload[key] = url;
        }

        var (ok, data, err) = await _napcat.CallAsync(name, payload, ct);

        if (!ok) return "工具失败：" + err;
        if (data.Length == 0) return "（接口返回空）";

        // 大块返回不整包塞给模型，截断并提示
        if (data.Length > 2000)
            return data[..2000] + $"……（后面还有，共 {data.Length} 字。要看更细的，用具体接口查单个对象）";

        return data;
    }

    private static object? ToClr(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.TryGetInt64(out var n) ? n : v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => v.GetRawText(),
    };

    /// <summary>
    /// 看一张图。图片只能放 user 消息里，所以这里走一次旁路请求，只把文字带回来
    /// </summary>
    private async Task<string> QqSeeAsync(string image, string question, bool low, CancellationToken ct)
    {
        var r = await ResolveImageAsync(image, ct);
        if (r.error.Length > 0) return "工具失败：" + r.error;

        var prompt = (question ?? "").Trim();
        if (prompt.Length == 0) prompt = "用一两句话描述这张图里有什么。如果图里有文字，把文字也说出来。";

        try
        {
            var res = await _llm.VisionAsync("gate", prompt, r.url, low ? "low" : "", ct);
            var text = (res.Content ?? "").Trim();
            return text.Length > 0 ? text : "（它看了，但没说出什么）";
        }
        catch (Exception ex)
        {
            return "工具失败：看图失败（" + ex.Message + "）";
        }
    }

    /// <summary>
    /// 把图存进表情库：落盘 + 写说明 + 记进 catalog
    /// </summary>
    private async Task<string> EmojiAddAsync(string image, string name, string meaning, CancellationToken ct)
    {
        var key = (name ?? "").Trim();
        if (key.Length == 0) return "工具失败：得给这个表情起个名字，不然以后找不到";

        var r = await ResolveImageAsync(image, ct);
        if (r.error.Length > 0) return "工具失败：" + r.error;

        var dir = Path.Combine(_env.WorkspaceDir, "emoji");
        Directory.CreateDirectory(dir);

        var ext = ExtOf(r.url, r.origin);
        var fileName = ChatLog.SafeScope(key) + ext;
        var full = Path.Combine(dir, fileName);

        try
        {
            await File.WriteAllBytesAsync(full, r.bytes, ct);
        }
        catch (Exception ex)
        {
            return "工具失败：存不进去（" + ex.Message + "）";
        }

        // 没给意思就自己看一眼写一条——表情包大多没字，OCR 指望不上，只能视觉
        var desc = (meaning ?? "").Trim();
        if (desc.Length == 0)
        {
            try
            {
                var res = await _llm.VisionAsync("gate",
                    "这是一个聊天用的表情包。用一句不超过 30 字的话说清它表达什么情绪、什么场合适合发。",
                    r.url, "low", ct);
                desc = (res.Content ?? "").Trim();
            }
            catch { /* 看不了就留空，之后再补 */ }
        }

        var catalogPath = Path.Combine(dir, "catalog.json");
        var catalog = new Dictionary<string, object>();

        try
        {
            if (File.Exists(catalogPath))
            {
                var old = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    File.ReadAllText(catalogPath), Core.AppJson.Config);
                if (old is not null)
                    foreach (var kv in old) catalog[kv.Key] = kv.Value;
            }
        }
        catch { /* 目录坏了就重建 */ }

        catalog[key] = new
        {
            file = "emoji/" + fileName,
            desc,
            added = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        try
        {
            Env.WriteAtomic(catalogPath, JsonSerializer.Serialize(catalog, Core.AppJson.Config));
        }
        catch (Exception ex)
        {
            return "工具失败：catalog 写不进去（" + ex.Message + "）";
        }

        return $"收好了：{key}（{fileName}）" + (desc.Length > 0 ? "　说明：" + desc : "　说明还没写");
    }

    /// <summary>
    /// 把「链接或路径」变成可以直接喂给模型的引用，外加原始字节
    /// </summary>
    private async Task<(string url, byte[] bytes, string origin, string error)> ResolveImageAsync(
        string image, CancellationToken ct)
    {
        var src = (image ?? "").Trim();
        if (src.Length == 0) return ("", Array.Empty<byte>(), "", "没给图片");

        // 正文里的 [图片](img-xxxxxxxx)：用编号换回真 url
        if (Core.MediaRef.LooksLikeId(src))
        {
            var url = Core.MediaRef.Get(_env, src);
            if (string.IsNullOrEmpty(url))
                return ("", Array.Empty<byte>(), "", $"这个编号 {src} 找不到对应的图，可能太久了");
            src = url;
        }

        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var resp = await Net.GetAsync(src, ct);
                if (!resp.IsSuccessStatusCode)
                    return ("", Array.Empty<byte>(), "", $"下载失败 HTTP {(int)resp.StatusCode}");

                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > MaxImageBytes)
                    return ("", Array.Empty<byte>(), "", "图片太大了");

                return (src, bytes, src, "");
            }
            catch (Exception ex)
            {
                return ("", Array.Empty<byte>(), "", "下载失败：" + ex.Message);
            }
        }

        var path = src;
        if (!Path.IsPathRooted(path)) path = Path.Combine(_env.WorkspaceDir, path);
        if (!File.Exists(path))
        {
            var alt = Path.Combine(_env.Root, src);
            if (File.Exists(alt)) path = alt;
            else return ("", Array.Empty<byte>(), "", "找不到这个文件：" + src);
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (bytes.Length > MaxImageBytes)
                return ("", Array.Empty<byte>(), "", "图片太大了");

            var mime = ExtOf("", path) switch
            {
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/jpeg",
            };

            return ("data:" + mime + ";base64," + Convert.ToBase64String(bytes), bytes, path, "");
        }
        catch (Exception ex)
        {
            return ("", Array.Empty<byte>(), "", "读不出来：" + ex.Message);
        }
    }

    private static string ExtOf(string url, string path)
    {
        var probe = url.Length > 0 ? url : path;
        var q = probe.IndexOf('?');
        if (q >= 0) probe = probe[..q];

        var ext = Path.GetExtension(probe).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" ? ext : ".jpg";
    }

    private static readonly System.Net.Http.HttpClient Net = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const long MaxImageBytes = 12 * 1024 * 1024;

    /// <summary>
    /// qq_api 允许调的接口名
    /// </summary>
    private static readonly HashSet<string> AllowedActions = new(StringComparer.Ordinal)
    {
        "get_image", "fetch_ptt_text", "ocr_image",
        "get_group_info", "get_group_member_info", "get_stranger_info", "get_group_member_list",
        "_get_group_notice", "get_essence_msg_list", "get_group_honor_info",
        "nc_get_user_status", "get_recent_contact",
        "get_group_msg_history", "get_friend_msg_history",
    };

    private static bool Bool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object) return false;
        if (!args.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => (v.GetString() ?? "").ToLowerInvariant() is "true" or "1",
            _ => false,
        };
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

    /// <summary>
    /// 按名字/QQ 查人：这是「戳一戳」和「私信」的前置步骤
    /// </summary>
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
    /// <summary>
    /// 调 NapCat 的 action 接口
    /// </summary>
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

    /// <summary>
    /// 主动找某个人之前的在线状态检查
    /// </summary>
    private string PresenceBlock(string userId, ToolCallContext ctx)
    {
        if (userId == ctx.UserId) return "";

        var v = _presence.Effective(userId);
        if (v.CanDisturb) return "";

        return $"对方当前判为 {v.State}（{v.Source}：{v.Note}），不允许主动打扰。"
               + "可以等他上线，或者先把想说的记下来。";
    }

    /// <summary>
    /// 目标 QQ 不在联系人里时，给模型一句可自查的提示
    /// </summary>
    private string ContactHint(string userId)    {
        if (_contacts.ByQq(userId) is not null) return "";

        var samples = _contacts.All().Take(5).Select(c => Contacts.Describe(c)).ToList();
        var hint = $"\n（注意：QQ {userId} 不在已知联系人里，可能号写错了。可以先用 qq_find 查）";
        if (samples.Count > 0) hint += "\n已知联系人：" + string.Join("；", samples);
        return hint;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];

    private static int Int(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object) return 0;
        if (!args.TryGetProperty(name, out var v)) return 0;

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var m)) return m;
        return 0;
    }

    /// <summary>
    /// 翻旧账：把以前的原话找回来，并留在上下文里
    ///
    /// 只在「确实想不起来」时调。翻到了就注入，翻不到就老实说没有——
    /// 绝不能拿关键词凑一段看似相关的东西塞回去，那比说不知道更糟。
    /// </summary>
    private async Task<string> HistoryRecallAsync(string query, int days, ToolCallContext ctx, CancellationToken ct)
    {
        var scope = (ctx.Scope ?? "").Trim();
        if (scope.Length == 0) return "工具失败：不知道当前在哪个会话里，没法查留档";

        // 已经在上下文里的、和这一轮已经注入过的，都不再重复找
        var exclude = _recallSession.InjectedMsgIds(scope);

        var outcome = await _recall.SearchAsync(query, scope, days, exclude, ct);

        if (!outcome.Found)
        {
            _log.LogInformation("回溯没找到：{Query}（{Note}）", Trim(query, 40), outcome.Note);
            return outcome.Text.Length > 0 ? outcome.Text : "翻不到这段记录。";
        }

        // 记忆层命中：不用注入，直接告诉模型它本来就知道
        if (outcome.Lines.Count == 0) return outcome.Text;

        _recallSession.Append(scope, outcome.Lines);
        _log.LogInformation("回溯命中：{Query} → {Count} 段（{Note}）",
            Trim(query, 40), outcome.Lines.Count, outcome.Note);

        return outcome.Text;
    }


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

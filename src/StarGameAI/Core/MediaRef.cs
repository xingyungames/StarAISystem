using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StarGameAI.Core;

/// <summary>
/// 媒体引用：给图片/语音/视频分配一个短编号，让模型能拿着编号去调工具
///
/// 为什么不能直接给 url：QQ 的 CDN 链接带签名，一二百个字符，塞进每一行对话会把上下文撑爆。
/// 所以文本里只留 [图片](img-a1b2c3d4)，真要用的时候用编号换回 url。
/// </summary>
public static class MediaRef
{
    private const int MaxLines = 800;

    /// <summary>
    /// 由 url 算出稳定编号。同一个 url 永远同一个编号
    /// </summary>
    public static string IdOf(string url)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(url ?? ""));
        return "img-" + Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    public static bool LooksLikeId(string s) =>
        !string.IsNullOrEmpty(s) && s.StartsWith("img-", StringComparison.Ordinal) && s.Length <= 16;

    private static string PathOf(Env env) => Path.Combine(env.DataDir, "media.jsonl");

    /// <summary>
    /// 记一条 url ↔ 编号的对应关系。重复写同一个编号会被后面读的时候覆盖，不影响
    /// </summary>
    public static void Put(Env env, string url, string scope, string kind)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            var id = IdOf(url);
            var row = JsonSerializer.Serialize(new
            {
                id,
                url,
                scope,
                kind,
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, AppJson.Options);

            Directory.CreateDirectory(env.DataDir);
            File.AppendAllText(PathOf(env), row + "\n");
        }
        catch { /* 记不下来只影响模型能不能看到这张图 */ }
    }

    /// <summary>
    /// 用编号换回 url
    /// </summary>
    public static string? Get(Env env, string id)
    {
        if (!LooksLikeId(id)) return null;

        try
        {
            var path = PathOf(env);
            if (!File.Exists(path)) return null;

            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (!doc.RootElement.TryGetProperty("id", out var idEl)) continue;
                    if ((idEl.GetString() ?? "") != id) continue;

                    return doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
                }
                catch { /* 跳过坏行 */ }
            }
        }
        catch { /* 读不到就返回 null */ }

        return null;
    }

    /// <summary>
    /// 剪掉过长的历史，只留最近若干条
    /// </summary>
    public static void Compact(Env env)
    {
        try
        {
            var path = PathOf(env);
            if (!File.Exists(path)) return;

            var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count <= MaxLines) return;

            File.WriteAllLines(path, lines.TakeLast(MaxLines));
        }
        catch { /* 剪不掉就算了，只是文件变大 */ }
    }
}

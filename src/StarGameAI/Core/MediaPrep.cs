using System.Text;
using StarGameAI.Gateway;

namespace StarGameAI.Core;

/// <summary>
/// 入站图片预处理：把 [图片](img-x) 变成 [图片](img-x｜图中文字：…)
///
/// 为什么在网关层做而不是让模型自己调工具：模型不一定会调。
/// 实盘在私聊里发图，它有一半时候直接回「我拿不到图」，赌模型每次都记得调工具是不行的。
/// 这一步零 token（OCR 走 NapCat），做完之后发言门、记忆、思想层全都白得一份图片内容。
/// </summary>
public sealed class MediaPrep
{
    private readonly Env _env;
    private readonly NapcatApi _napcat;
    private readonly Llm.DeepSeekClient _llm;
    private readonly ILogger<MediaPrep> _log;

    public MediaPrep(Env env, NapcatApi napcat, Llm.DeepSeekClient llm, ILogger<MediaPrep> log)
    {
        _env = env;
        _napcat = napcat;
        _llm = llm;
        _log = log;
    }

    /// <summary>
    /// 给一条消息里的图片补上内容说明
    /// </summary>
    /// <param name="text">已经拍平成 [图片](img-x) 的正文</param>
    /// <param name="briefs">消息段</param>
    /// <param name="isPrivate">私聊才直接花视觉 token，群里贵不起</param>
    /// <param name="ct">取消</param>
    /// <returns>补充后的正文</returns>
    public async Task<string> EnrichAsync(string text, IEnumerable<SegBrief> briefs, bool isPrivate, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (!_env.Recall.Enabled) return text;

        var images = (briefs ?? Enumerable.Empty<SegBrief>())
            .Where(b => b.T == "image" && !string.IsNullOrWhiteSpace(b.Url))
            .Take(3)                                     // 一批图最多看三张，别把回复拖死
            .ToList();

        if (images.Count == 0) return text;

        var result = text;

        foreach (var img in images)
        {
            var marker = "[图片](" + MediaRef.IdOf(img.Url!) + ")";
            if (!result.Contains(marker, StringComparison.Ordinal)) continue;

            string note;
            try
            {
                note = await DescribeAsync(img.Url!, isPrivate, ct);
            }
            catch (Exception ex)
            {
                _log.LogDebug("图片预处理失败（不影响回话）：{Error}", ex.Message);
                continue;
            }

            if (note.Length > 0)
                result = result.Replace(marker, marker + "｜" + note, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// 一张图：先看有没有字（免费），私聊再看画（大概 1000 token）
    /// </summary>
    private async Task<string> DescribeAsync(string url, bool isPrivate, CancellationToken ct)
    {
        // 第一步：OCR，走 NapCat 不花模型 token
        var ocr = await OcrAsync(url, ct);
        if (ocr.Length > 0) return "图中文字：" + Clip(ocr, 60);

        // 第二步：私聊才直接看图。群里等模型自己调 qq_see
        if (!isPrivate) return "";

        try
        {
            var res = await _llm.VisionAsync("gate",
                "用一句不超过 30 字的话说这张图里有什么。有文字就说文字，没有就描述画面。",
                url, "low", ct);

            var desc = (res.Content ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
            return desc.Length > 0 ? "图里：" + Clip(desc, 60) : "";
        }
        catch (Exception ex)
        {
            _log.LogDebug("私聊看图失败：{Error}", ex.Message);
            return "";
        }
    }

    private async Task<string> OcrAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));

        try
        {
            var (ok, data, _) = await _napcat.CallAsync("ocr_image",
                new Dictionary<string, object?> { ["image"] = url }, cts.Token);

            if (!ok || data.Length == 0) return "";

            // 返回形如 {"texts":[{"text":"…"}],"language":"zh"}
            using var doc = System.Text.Json.JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("texts", out var arr)
                || arr.ValueKind != System.Text.Json.JsonValueKind.Array) return "";

            var sb = new StringBuilder();
            foreach (var one in arr.EnumerateArray())
            {
                if (!one.TryGetProperty("text", out var t)
                    || t.ValueKind != System.Text.Json.JsonValueKind.String) continue;

                var s = (t.GetString() ?? "").Trim();
                if (s.Length == 0) continue;

                if (sb.Length > 0) sb.Append(' ');
                sb.Append(s);
                if (sb.Length > 200) break;
            }

            return sb.ToString().Trim();
        }
        catch (OperationCanceledException)
        {
            return "";
        }
        catch (Exception ex)
        {
            _log.LogDebug("OCR 失败：{Error}", ex.Message);
            return "";
        }
    }

    private static string Clip(string s, int n)
    {
        var v = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return v.Length > n ? v[..n] + "…" : v;
    }
}

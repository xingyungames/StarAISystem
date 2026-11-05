using System.Text;
using System.Text.Json;
using StarGameAI.Core;

namespace StarGameAI.Gateway;

/// <summary>
/// NapCat 通用调用：一个口子应付所有 OneBot 11 接口
///
/// 发消息那条已经由 Sender 专门处理（限速、分段、回写日志），
/// 这里只做「查一下」这类不需要业务包装的调用。
/// </summary>
public sealed class NapcatApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Env _env;
    private readonly ILogger<NapcatApi> _log;

    public NapcatApi(Env env, ILogger<NapcatApi> log)
    {
        _env = env;
        _log = log;
    }

    /// <summary>
    /// 调用一个 OneBot 接口
    /// </summary>
    /// <param name="action">接口名，例如 get_image、fetch_ptt_text</param>
    /// <param name="payload">请求参数，序列化成 JSON</param>
    /// <param name="ct">取消</param>
    /// <returns>ok 表示 retcode=0；data 是接口返回的业务数据（紧凑 JSON）</returns>
    public async Task<(bool ok, string data, string message)> CallAsync(
        string action, object payload, CancellationToken ct)
    {
        var name = (action ?? "").Trim().TrimStart('/');
        if (name.Length == 0) return (false, "", "接口名为空");

        var url = _env.Server.Napcat.ApiBase.TrimEnd('/') + "/" + name;
        var token = _env.Secrets.Napcat.Token;

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
            var text = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                return (false, "", $"HTTP {(int)resp.StatusCode}：{Trim(text, 160)}");

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            var retcode = root.TryGetProperty("retcode", out var rc) && rc.ValueKind == JsonValueKind.Number
                ? rc.GetInt32()
                : -1;

            var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? (m.GetString() ?? "")
                : "";

            if (retcode != 0)
            {
                var wording = root.TryGetProperty("wording", out var w) && w.ValueKind == JsonValueKind.String
                    ? (w.GetString() ?? "")
                    : "";

                return (false, "", $"retcode={retcode} {message} {wording}".Trim());
            }

            var data = root.TryGetProperty("data", out var d)
                ? JsonSerializer.Serialize(d, AppJson.Options)
                : "";

            return (true, data, "");
        }
        catch (Exception ex)
        {
            _log.LogWarning("NapCat 调用 {Action} 失败：{Error}", name, ex.Message);
            return (false, "", ex.Message);
        }
    }

    private static string Trim(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : s.Length > n ? s[..n] : s;
}

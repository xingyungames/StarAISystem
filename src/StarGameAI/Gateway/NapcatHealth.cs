using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using StarGameAI.Core;

namespace StarGameAI.Gateway;

/// <summary>NapCat 在线状态探测。</summary>
public sealed class NapcatHealth : BackgroundService
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(5),
    })
    { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly Env _env;
    private readonly ILogger<NapcatHealth> _log;

    private volatile bool _online;
    private volatile string _version = "";
    private volatile string _lastError = "从未探测";
    private volatile int _latencyMs;
    private long _checkedAt;
    private long _onlineSince;
    private bool _everOnline;

    public NapcatHealth(Env env, ILogger<NapcatHealth> log)
    {
        _env = env;
        _log = log;
    }

    public bool Online => _online;
    public string Version => _version;
    public string LastError => _lastError;
    public int LatencyMs => _latencyMs;
    public long CheckedAt => Interlocked.Read(ref _checkedAt);
    public long OnlineSince => Interlocked.Read(ref _onlineSince);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 启动时先探一次，后台不用等 15 秒
        await ProbeAsync(ct);

        using var timer = new PeriodicTimer(Interval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct)) break;
            }
            catch (OperationCanceledException) { break; }

            await ProbeAsync(ct);
        }
    }

    private async Task ProbeAsync(CancellationToken ct)
    {
        var url = _env.Server.Napcat.ApiBase.TrimEnd('/') + "/get_status";
        var sw = Stopwatch.StartNew();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };

            var token = _env.Secrets.Napcat.Token;
            if (!string.IsNullOrEmpty(token))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            using var resp = await Http.SendAsync(req, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                MarkOffline($"HTTP {(int)resp.StatusCode}：{Trim(body, 120)}", (int)sw.ElapsedMilliseconds);
                return;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var retcode = root.TryGetProperty("retcode", out var rc) && rc.TryGetInt32(out var r) ? r : -1;
            if (retcode != 0)
            {
                MarkOffline($"retcode={retcode}", (int)sw.ElapsedMilliseconds);
                return;
            }

            var online = root.TryGetProperty("data", out var data)
                         && data.ValueKind == JsonValueKind.Object
                         && data.TryGetProperty("online", out var on)
                         && on.ValueKind == JsonValueKind.True;

            if (!online)
            {
                MarkOffline("接口通了，但 QQ 未登录", (int)sw.ElapsedMilliseconds);
                return;
            }

            MarkOnline((int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 关服务，不算故障
        }
        catch (Exception ex)
        {
            sw.Stop();
            MarkOffline(ex.Message, (int)sw.ElapsedMilliseconds);
        }
    }

    private void MarkOnline(int ms)
    {
        var was = _online;
        _online = true;
        _latencyMs = ms;
        _lastError = "";
        Interlocked.Exchange(ref _checkedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        if (!was && _everOnline)
            _log.LogInformation("NapCat 恢复在线（{Ms}ms）", ms);
        else if (!was)
            _log.LogInformation("NapCat 在线（{Ms}ms）", ms);

        _everOnline = true;
        if (Interlocked.Read(ref _onlineSince) == 0)
            Interlocked.Exchange(ref _onlineSince, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private void MarkOffline(string error, int ms)
    {
        var was = _online;
        _online = false;
        _latencyMs = ms;
        _lastError = Trim(error, 200);
        Interlocked.Exchange(ref _checkedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Interlocked.Exchange(ref _onlineSince, 0);

        // 只在「刚掉线」时报一次，避免每 15 秒刷屏
        if (was || !_everOnline)
            _log.LogWarning("NapCat 探测失败：{Error}", _lastError);
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
}


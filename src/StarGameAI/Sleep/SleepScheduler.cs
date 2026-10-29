using StarGameAI.Core;
using StarGameAI.Llm;
using StarGameAI.Pipeline;

namespace StarGameAI.Sleep;

/// <summary>作息调度器：让 AI「自己找时间睡」。</summary>
public sealed class SleepScheduler : BackgroundService
{
    private const int AttemptBackoffSeconds = 900;

    private readonly Env _env;
    private readonly Consolidator _consolidator;
    private readonly SleepRunner _runner;
    private readonly Activity _activity;
    private readonly ChatLog _chatLog;
    private readonly DeepSeekClient _llm;
    private readonly ILogger<SleepScheduler> _log;

    public SleepScheduler(
        Env env, Consolidator consolidator, SleepRunner runner, Activity activity,
        ChatLog chatLog, DeepSeekClient llm, ILogger<SleepScheduler> log)
    {
        _env = env;
        _consolidator = consolidator;
        _runner = runner;
        _activity = activity;
        _chatLog = chatLog;
        _llm = llm;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 让网关先稳定下来
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "作息检查失败");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(ct)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var cfg = _env.Sleep;

        if (!cfg.Enabled) return;
        if (_runner.Busy || _consolidator.IsRunning) return;
        if (!_llm.HasKey) return;

        var state = _consolidator.LoadState();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 失败退避
        if (state.LastAttemptTs > 0 && now - state.LastAttemptTs < AttemptBackoffSeconds) return;

        // 最小间隔（按上次成功算）
        if (state.LastRunTs > 0 && now - state.LastRunTs < cfg.Interval.MinHoursBetween * 3600L) return;

        // 空闲要求：对话进行中不睡
        if (_activity.IdleSeconds() < cfg.IdleRequiredSeconds) return;

        var reason = DecideReason(state, now);
        if (reason is null) return;

        _log.LogInformation("进入睡眠（{Reason}），开始巩固…", reason);
        _activity.TouchSleep();

        var outcome = await _consolidator.RunAsync("schedule", ct);

        if (outcome.Ok)
        {
            _log.LogInformation("巩固完成：分片={Chunks} 候选={Candidates} 写入={Written} rev={Rev}",
                outcome.Chunks, outcome.Candidates, outcome.Written, outcome.PersonaRev);
        }
        else
        {
            _log.LogWarning("巩固未完成：{Error}（{Backoff} 秒后才会再试）", outcome.Error, AttemptBackoffSeconds);
        }
    }

    private string? DecideReason(SleepState state, long now)
    {
        var cfg = _env.Sleep;

        //AI 自己申请过
        if (_consolidator.HasPendingSignal()) return "收到睡眠信号";

        //当日日志量
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (cfg.LogThresholdEntries > 0 && _chatLog.CountOfDate(today) >= cfg.LogThresholdEntries)
            return $"当日日志已达 {cfg.LogThresholdEntries} 条";

        //作息窗口
        if (InWindow(DateTime.Now)) return "进入作息窗口";

        //最长间隔兜底（按上次成功）
        if (state.LastRunTs > 0 && now - state.LastRunTs >= cfg.Interval.MaxHoursBetween * 3600L)
            return $"距上次巩固超过 {cfg.Interval.MaxHoursBetween} 小时";

        //从未成功巩固过
        if (state.LastRunTs == 0) return "首次巩固";

        return null;
    }

    private bool InWindow(DateTime now)
    {
        foreach (var w in _env.Sleep.Windows)
        {
            if (!TimeSpan.TryParse(w.Start, out var start)) continue;
            if (!TimeSpan.TryParse(w.End, out var end)) continue;

            var t = now.TimeOfDay;
            if (start <= end)
            {
                if (t >= start && t <= end) return true;
            }
            else
            {
                // 跨午夜，例如 23:30 - 02:00
                if (t >= start || t <= end) return true;
            }
        }

        return false;
    }
}

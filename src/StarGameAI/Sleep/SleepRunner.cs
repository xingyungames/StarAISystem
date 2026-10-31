using StarGameAI.Core;

namespace StarGameAI.Sleep;

/// <summary>
/// 把「手动触发一次巩固」包装成可取消的后台任务
/// </summary>
public sealed class SleepRunner
{
    private readonly Consolidator _consolidator;
    private readonly ILogger<SleepRunner> _log;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    public SleepRunner(Consolidator consolidator, ILogger<SleepRunner> log)
    {
        _consolidator = consolidator;
        _log = log;
    }

    public bool Busy
    {
        get { lock (_gate) return _cts is not null; }
    }

    public bool Start(string by)
    {
        lock (_gate)
        {
            if (_cts is not null) return false;
            _cts = new CancellationTokenSource();
        }

        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await _consolidator.RunAsync(by, token);
                _log.LogInformation("巩固结束 ok={Ok} 分片={Chunks} 写入={Written} rev={Rev} err={Err}",
                    outcome.Ok, outcome.Chunks, outcome.Written, outcome.PersonaRev, outcome.Error);
            }
            catch (OperationCanceledException)
            {
                _log.LogInformation("巩固被取消");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "巩固异常");
            }
            finally
            {
                lock (_gate)
                {
                    _cts?.Dispose();
                    _cts = null;
                }
            }
        });

        return true;
    }

    public bool Cancel()
    {
        lock (_gate)
        {
            if (_cts is null) return false;
            _cts.Cancel();
            return true;
        }
    }
}

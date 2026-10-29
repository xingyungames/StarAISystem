namespace StarGameAI.Core;

/// <summary>进程内的「最近活动」标记。作息调度器靠它判断「现在是不是空闲」，</summary>
public sealed class Activity
{
    private long _lastMessageTs;
    private long _lastSleepTs;

    public long LastMessageTs => Interlocked.Read(ref _lastMessageTs);

    public void TouchMessage() => Interlocked.Exchange(ref _lastMessageTs, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public long LastSleepTs => Interlocked.Read(ref _lastSleepTs);

    public void TouchSleep() => Interlocked.Exchange(ref _lastSleepTs, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>距上一条消息过去了多少秒（没有消息时返回 int.MaxValue）。</summary>
    public int IdleSeconds()
    {
        var ts = LastMessageTs;
        if (ts == 0) return int.MaxValue;
        return (int)Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts);
    }
}

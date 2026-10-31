namespace StarGameAI.Gateway;

/// <summary>
/// message_id 去重。入站（NapCat 上报）与出站（发送后回写）共用同一份表与同一套 id
/// </summary>
public sealed class Dedup
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _seen = new();
    private readonly int _capacity;
    private readonly TimeSpan _ttl;

    public Dedup(int capacity, TimeSpan ttl)
    {
        _capacity = Math.Max(100, capacity);
        _ttl = ttl;
    }

    /// <summary>
    /// 首次见到返回 true 并登记；重复返回 false
    /// </summary>
    public bool TryAdd(string key)
    {
        if (string.IsNullOrEmpty(key)) return true;

        var now = Environment.TickCount64;
        lock (_gate)
        {
            Sweep(now);

            if (_seen.TryGetValue(key, out var at) && now - at < _ttl.TotalMilliseconds)
                return false;

            _seen[key] = now;

            if (_seen.Count > _capacity)
            {
                foreach (var stale in _seen.OrderBy(kv => kv.Value)
                             .Take(_seen.Count - _capacity).Select(kv => kv.Key).ToList())
                    _seen.Remove(stale);
            }

            return true;
        }
    }

    /// <summary>
    /// 发送前预登记，避免「同一条消息正在生成时被重投」
    /// </summary>
    public void Reserve(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_gate) _seen[key] = Environment.TickCount64;
    }

    private void Sweep(long now)
    {
        if (_seen.Count < _capacity / 2) return;
        foreach (var key in _seen.Where(kv => now - kv.Value >= _ttl.TotalMilliseconds)
                     .Select(kv => kv.Key).ToList())
            _seen.Remove(key);
    }

    public int Count
    {
        get { lock (_gate) return _seen.Count; }
    }
}

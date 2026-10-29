using System.Text.Json;

namespace StarGameAI.Core;

public sealed class Contact
{
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public long FirstSeen { get; set; }
    public long LastSeen { get; set; }
    public int Messages { get; set; }

    /// <summary>这个人在哪些场景里说过话（private:QQ / group:群号）。</summary>
    public List<string> Scopes { get; set; } = new();
}

/// <summary>联系人目录：QQ 号 ↔ 名字的映射。</summary>
public sealed class Contacts
{
    private readonly Env _env;
    private readonly object _lock = new();
    private Dictionary<string, Contact> _map = new();

    public Contacts(Env env)
    {
        _env = env;
        Load();
    }

    private string FilePath => Path.Combine(_env.DataDir, "contacts.json");

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, Contact>>(
                File.ReadAllText(FilePath), AppJson.Config);
            if (data is not null) _map = data;
        }
        catch
        {
            _map = new();
        }
    }

    private void Save()
    {
        try
        {
            Env.WriteAtomic(FilePath, JsonSerializer.Serialize(_map, AppJson.Config));
        }
        catch { /* 忽略 */ }
    }

    /// <summary>收到一条消息时登记/更新联系人。</summary>
    public void Touch(string userId, string name, string scope = "")
    {
        if (string.IsNullOrWhiteSpace(userId)) return;

        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!_map.TryGetValue(userId, out var c))
            {
                c = new Contact { UserId = userId, FirstSeen = now };
                _map[userId] = c;
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                if (c.Name.Length == 0) c.Name = name;
                else if (c.Name != name && !c.Aliases.Contains(name) && c.Aliases.Count < 6)
                    c.Aliases.Add(name);   // 群名片改过、或换过昵称
            }

            if (scope.Length > 0 && !c.Scopes.Contains(scope) && c.Scopes.Count < 12)
                c.Scopes.Add(scope);

            c.LastSeen = now;
            c.Messages++;
            Save();
        }
    }

    public Contact? ByQq(string qq)
    {
        lock (_lock) return _map.TryGetValue(qq.Trim(), out var c) ? c : null;
    }

    public List<Contact> All()
    {
        lock (_lock) return _map.Values.OrderByDescending(c => c.LastSeen).ToList();
    }

    /// <summary>按名字 / 别名 / QQ 号做模糊查找，最近的排在前面。</summary>
    public List<Contact> Search(string query, int max = 8)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return new List<Contact>();

        lock (_lock)
        {
            var list = _map.Values;

            // QQ 号：先精确，再前缀
            if (q.All(char.IsDigit))
            {
                return list
                    .Where(c => c.UserId == q || c.UserId.Contains(q))
                    .OrderByDescending(c => c.UserId == q)
                    .ThenByDescending(c => c.LastSeen)
                    .Take(max)
                    .ToList();
            }

            return list
                .Where(c => Names(c).Any(n => n.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(c => Names(c).Any(n => n.Equals(q, StringComparison.OrdinalIgnoreCase)))
                .ThenByDescending(c => c.LastSeen)
                .Take(max)
                .ToList();
        }
    }

    private static IEnumerable<string> Names(Contact c)
    {
        yield return c.Name;
        foreach (var a in c.Aliases) yield return a;
    }

    /// <summary>给模型看的一行描述。</summary>
    public static string Describe(Contact c, bool verbose = false)
    {
        var aliases = c.Aliases.Count > 0 ? "（也叫：" + string.Join("、", c.Aliases) + "）" : "";
        var baseText = $"{c.Name}（QQ {c.UserId}）{aliases}";

        if (!verbose) return baseText;

        var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - c.LastSeen;
        var last = age < 3600 ? Math.Max(1, age / 60) + " 分钟前"
                 : age < 86400 ? age / 3600 + " 小时前"
                 : age / 86400 + " 天前";
        return $"{baseText}　最近说话 {last}，累计 {c.Messages} 条";
    }


    private HashSet<string>? _privatePartners;
    private long _privatePartnersAt;
    private readonly object _partnerLock = new();

    /// <summary>这个号是不是「可以私聊的人」。</summary>
    public bool IsPrivatePartner(string userId)
    {
        var uid = (userId ?? "").Trim();
        if (uid.Length == 0) return false;

        lock (_lock)
        {
            if (_map.TryGetValue(uid, out var c)
                && c.Scopes.Any(s => s.StartsWith("private:", StringComparison.Ordinal)))
                return true;
        }

        return PrivatePartnerIds().Contains(uid);
    }

    /// <summary>所有可以私聊的 QQ 号。扫盘结果缓存 60 秒，避免每次刷新都遍历目录。</summary>
    public HashSet<string> PrivatePartnerIds()
    {
        lock (_partnerLock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (_privatePartners is not null && now - _privatePartnersAt < 60)
                return _privatePartners;

            var set = new HashSet<string>(StringComparer.Ordinal);

            lock (_lock)
            {
                foreach (var c in _map.Values)
                    if (c.Scopes.Any(s => s.StartsWith("private:", StringComparison.Ordinal)))
                        set.Add(c.UserId);
            }

            // 浅层记忆：data/memory/shallow/private_<qq>.jsonl
            try
            {
                foreach (var f in Directory.EnumerateFiles(_env.ShallowDir, "private_*.jsonl"))
                    AddFromFileName(f, set);
            }
            catch { /* 目录不存在就算了 */ }

            // 聊天日志：data/log/<日期>/private_<qq>.jsonl
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(_env.LogDir))
                    foreach (var f in Directory.EnumerateFiles(dir, "private_*.jsonl"))
                        AddFromFileName(f, set);
            }
            catch { /* 同上 */ }

            _privatePartners = set;
            _privatePartnersAt = now;
            return set;
        }
    }

    private static void AddFromFileName(string path, HashSet<string> set)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        const string prefix = "private_";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return;

        var id = name[prefix.Length..];
        if (id.Length > 0 && id.All(char.IsDigit)) set.Add(id);
    }
}

using System.Text;
using StarGameAI.Memory;
using StarGameAI.Pipeline;

namespace StarGameAI.Core;

/// <summary>
/// 身份卡：把「这个 QQ 号到底是谁」翻译成模型能读的几行字
/// </summary>
public sealed class Identity
{
    private readonly Env _env;
    private readonly Contacts _contacts;
    private readonly Store _store;
    private readonly ChatLog _chatLog;

    public Identity(Env env, Contacts contacts, Store store, ChatLog chatLog)
    {
        _env = env;
        _contacts = contacts;
        _store = store;
        _chatLog = chatLog;
    }

    /// <summary>
    /// 这个人是不是"可以私聊的熟人"（转发给 Contacts 的判定）
    /// </summary>
    public bool IsPrivatePartner(string userId) => _contacts.IsPrivatePartner(userId);

    /// <summary>
    /// 场景标签翻译成人话
    /// </summary>
    public static string Label(string scope)
    {
        if (scope.StartsWith("private:", StringComparison.Ordinal)) return "私聊";
        if (scope.StartsWith("group:", StringComparison.Ordinal)) return "群 " + scope["group:".Length..];
        return scope;
    }

    /// <summary>
    /// 一个人的身份卡
    /// </summary>
    public string CardFor(string userId, string currentScope, bool verbose = false)
    {
        var uid = (userId ?? "").Trim();
        if (uid.Length == 0) return "";

        var c = _contacts.ByQq(uid);

        var sb = new StringBuilder();
        var name = c is not null && c.Name.Length > 0 ? c.Name : uid;
        sb.Append(name).Append("（QQ ").Append(uid).Append('）');

        if (c is not null)
        {
            var others = c.Aliases.Where(a => a.Length > 0 && a != c.Name).Take(4).ToList();
            if (others.Count > 0) sb.Append("，也叫 ").Append(string.Join("、", others));
        }

        // 在哪些场景见过 —— 这是跨场景认人的关键一句
        var scopes = (c?.Scopes ?? new List<string>())
            .Where(s => s != currentScope)
            .Select(Label)
            .Distinct()
            .Take(4)
            .ToList();

        if (scopes.Count > 0)
            sb.Append("；你在").Append(string.Join("、", scopes)).Append("也见过他");
        else if (c is null)
            sb.Append("；你没见过这个号");

        // 画像首句 —— 不管当前在哪个 scope，对人的印象都应该在
        var profile = "";
        try { profile = _store.ReadProfile(uid); } catch { /* 读不到就不带 */ }

        if (profile.Trim().Length > 0)
        {
            sb.Append("。你对他的印象：").Append(FirstSentence(profile, verbose ? 160 : 86));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 一句话的身份标签，用来标在每条历史消息前面
    /// </summary>
    public string SenderTag(string userId, string name, string currentScope)
    {
        var uid = (userId ?? "").Trim();
        var label = (name ?? "").Trim();
        if (uid.Length == 0) return label.Length > 0 ? label : "某人";

        var c = _contacts.ByQq(uid);
        if (label.Length == 0) label = c is not null && c.Name.Length > 0 ? c.Name : uid;
        if (c is not null && c.Name.Length > 0 && c.Name != label)
            label = c.Name;   // 群名片和私聊名不一致时，用目录里的主名，避免"换了群就像换了个人"

        var partner = _contacts.IsPrivatePartner(uid);
        return partner
            ? $"{label}（QQ {uid}｜你私聊认识的）"
            : $"{label}（QQ {uid}）";
    }

    /// <summary>
    /// 唤醒块：把这个场景里最近说过话的人，逐个对进联系人词典
    /// </summary>
    public string WakeBlock(string scope, string currentUserId, int window = 40)
    {
        var order = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var pool = new List<LogEntry>();
            pool.AddRange(_chatLog.Read(today.AddDays(-1), scope));
            pool.AddRange(_chatLog.Read(today, scope));

            foreach (var e in pool.Where(e => !e.IsSelf && e.UserId.Length > 0).TakeLast(window))
            {
                names[e.UserId] = e.Name.Length > 0 ? e.Name : names.GetValueOrDefault(e.UserId, "");
                if (!order.Contains(e.UserId)) order.Add(e.UserId);
            }
        }
        catch
        {
            return "";
        }

        if (order.Count == 0) return "";

        // 正在说话的人排最前
        order = order.OrderByDescending(u => u == currentUserId).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("【这里的人】（纯查表对出来的，同一个人换个群还是同一个号，别认错）");

        foreach (var uid in order)
        {
            var card = CardFor(uid, scope);
            sb.AppendLine("- " + (card.Length > 0 ? card : names.GetValueOrDefault(uid, uid) + "（QQ " + uid + "，没见过）"));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 群里的「在场的人」花名册（旧接口，保留给意图层用）
    /// </summary>
    public string RosterBlock(string scope, string currentUserId, int max = 5)
    {
        if (!scope.StartsWith("group:", StringComparison.Ordinal)) return "";

        var people = new List<(string userId, string name, int count)>();

        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var seen = new Dictionary<string, (string name, int count)>();

            foreach (var e in _chatLog.Read(today, scope))
            {
                if (e.IsSelf || e.UserId.Length == 0) continue;
                if (seen.TryGetValue(e.UserId, out var v))
                    seen[e.UserId] = (e.Name.Length > 0 ? e.Name : v.name, v.count + 1);
                else
                    seen[e.UserId] = (e.Name, 1);
            }

            people = seen
                .OrderByDescending(kv => kv.Value.count)
                .Take(Math.Max(1, max))
                .Select(kv => (kv.Key, kv.Value.name, kv.Value.count))
                .ToList();
        }
        catch
        {
            return "";
        }

        if (people.Count == 0) return "";

        // 当前说话的人排第一，避免被挤出 max 之外
        people = people
            .OrderByDescending(p => p.userId == currentUserId)
            .ThenByDescending(p => p.count)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("【在场的人】（同一行说的是同一个号，别认错人）");

        foreach (var (uid, name, count) in people)
        {
            sb.Append("- ");
            var card = CardFor(uid, scope);
            sb.AppendLine(card.Length > 0 ? card : name + "（QQ " + uid + "）");
        }

        return sb.ToString();
    }

    private static string FirstSentence(string text, int max)
    {
        var body = (text ?? "").Replace("\r", "").Trim();
        if (body.Length == 0) return "";

        var cut = body.IndexOfAny(new[] { '。', '\n', '；' });
        var head = cut > 0 ? body[..cut] : body;
        return head.Length > max ? head[..max] + "…" : head;
    }
}

using System.Text.Json;

namespace StarGameAI.Core;

/// <summary>一个「上下文边界」——不是一个人，而是**一件事**。</summary>
public sealed class DialogueBlock
{
    public string Id { get; set; } = "";
    public string Scope { get; set; } = "";
    /// <summary>one_to_one（一个人在跟我说）/ open_topic（多人在聊一件事，我可以插）/ cold / closed</summary>
    public string State { get; set; } = "open_topic";
    /// <summary>参与者 entity_id（QQ 号）。引用，不复制画像。</summary>
    public List<string> Participants { get; set; } = new();
    /// <summary>one_to_one 时"跟我说话的那个人"。</summary>
    public string Owner { get; set; } = "";
    /// <summary>这块里最后一条消息是谁发的。</summary>
    public string LastUserId { get; set; } = "";
    /// <summary>一句话题摘要。</summary>
    public string Topic { get; set; } = "";
    public long CreatedAt { get; set; }
    public long LastActiveAt { get; set; }
    public int MessageCount { get; set; }
    /// <summary>这块里最近的消息 id（最多留 60 个，够回溯用）。</summary>
    public List<string> MessageIds { get; set; } = new();
}

/// <summary>一个 scope 下的全部块 + 未归属池。</summary>
public sealed class DialogueScope
{
    public string Scope { get; set; } = "";
    public long UpdatedAt { get; set; }
    public List<DialogueBlock> Blocks { get; set; } = new();
    /// <summary>判不了的消息（保留一段时间，供事后回填）。</summary>
    public List<UnresolvedMessage> Unresolved { get; set; } = new();
}

public sealed class UnresolvedMessage
{
    public string MessageId { get; set; } = "";
    public string UserId { get; set; } = "";
    public long Ts { get; set; }
    public double Confidence { get; set; }
    public string Why { get; set; } = "";
}

/// <summary>一次路由判定的结果（同时也是一行审计）。</summary>
public sealed class RouteResult
{
    public long Ts { get; set; }
    public string Scope { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string UserId { get; set; } = "";
    /// <summary>continue / new_block / unresolved</summary>
    public string Decision { get; set; } = "unresolved";
    public string BlockId { get; set; } = "";
    public double Confidence { get; set; }
    /// <summary>rule / model / none</summary>
    public string By { get; set; } = "none";
    public string Why { get; set; } = "";
    /// <summary>各候选块的得分，观测用。</summary>
    public Dictionary<string, double> Candidates { get; set; } = new();
    public int Ms { get; set; }
}

/// <summary>块的存取。一个 scope 一份 <c>data/dialogue/&lt;scope&gt;.json</c>。</summary>
public sealed class DialogueStore
{
    private readonly Env _env;
    private readonly object _lock = new();

    public DialogueStore(Env env) => _env = env;

    private string Dir => Path.Combine(_env.DataDir, "dialogue");

    private string FileFor(string scope) =>
        Path.Combine(Dir, Memory.Store.SafeName(scope) + ".json");

    public DialogueScope Load(string scope)
    {
        try
        {
            var path = FileFor(scope);
            if (!File.Exists(path)) return new DialogueScope { Scope = scope };

            var s = JsonSerializer.Deserialize<DialogueScope>(File.ReadAllText(path), AppJson.Config)
                    ?? new DialogueScope { Scope = scope };
            s.Scope = scope;
            return s;
        }
        catch
        {
            return new DialogueScope { Scope = scope };
        }
    }

    public void Save(DialogueScope scope)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                scope.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                var cfg = _env.Dialogue;
                var cap = Math.Max(4, cfg.MaxBlocksPerScope);

                if (scope.Blocks.Count > cap)
                {
                    // 超量就砍最久没动静的，别让块无限增长
                    var keep = scope.Blocks
                        .OrderByDescending(b => b.LastActiveAt)
                        .Take(cap)
                        .ToList();
                    scope.Blocks = keep;
                }

                var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                             - Math.Max(1, cfg.UnresolvedKeepHours) * 3600L;
                scope.Unresolved.RemoveAll(u => u.Ts < cutoff);

                Env.WriteAtomic(FileFor(scope.Scope),
                    JsonSerializer.Serialize(scope, AppJson.Config));
            }
            catch { /* 忽略 */ }
        }
    }

    /// <summary>这条消息落在哪个块里（回流用）。</summary>
    public static DialogueBlock? FindByMessage(DialogueScope scope, string messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return null;

        foreach (var b in scope.Blocks)
            if (b.MessageIds.Contains(messageId))
                return b;

        return null;
    }

    /// <summary>所有 scope 的块总览（后台用）。</summary>
    public List<DialogueScope> LoadAll(int max = 12)
    {
        var list = new List<DialogueScope>();

        try
        {
            if (!Directory.Exists(Dir)) return list;

            foreach (var f in Directory.GetFiles(Dir, "*.json")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Take(max))
            {
                try
                {
                    var s = JsonSerializer.Deserialize<DialogueScope>(
                        File.ReadAllText(f), AppJson.Config);
                    if (s is not null && s.Blocks.Count > 0) list.Add(s);
                }
                catch { /* 跳过坏文件 */ }
            }
        }
        catch { /* 忽略 */ }

        return list;
    }
}

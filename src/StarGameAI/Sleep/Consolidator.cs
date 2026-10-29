using System.Text;
using System.Text.Json;
using StarGameAI.Core;
using StarGameAI.Llm;
using StarGameAI.Memory;
using StarGameAI.Pipeline;

namespace StarGameAI.Sleep;

public sealed class SleepWatermark
{
    public long LastConsumedLogTs { get; set; }
    public string LastConsumedMsgId { get; set; } = "";
}

public sealed class SleepSignal
{
    public long Ts { get; set; }
    public string Reason { get; set; } = "";
    public bool Consumed { get; set; }
}

/// <summary>睡眠状态：进程内可见，同时落盘以便重启后仍能读到上次结果。</summary>
public sealed class SleepState
{
    public bool Sleeping { get; set; }
    /// <summary>上一次**成功**巩固的时间。作息的最小间隔按它算。</summary>
    public long LastRunTs { get; set; }
    /// <summary>上一次尝试的时间（无论成败）。失败退避按它算。</summary>
    public long LastAttemptTs { get; set; }
    public int LastRev { get; set; }
    public bool LastOk { get; set; }
    public string LastError { get; set; } = "";
}

public sealed class Candidate
{
    public string Kind { get; set; } = "fact";
    public string Text { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public int Weight { get; set; } = 50;
    public double Confidence { get; set; } = 0.5;
    public string Scope { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public List<string> Evidence { get; set; } = new();
    /// <summary>commitment 专用：生效范围（空 = 到处都算）。</summary>
    public List<string> ValidIn { get; set; } = new();
    /// <summary>commitment 专用：hard / soft。</summary>
    public string Strictness { get; set; } = "";
}

public sealed class Chunk
{
    public string Scope { get; set; } = "";
    public List<LogEntry> Entries { get; set; } = new();
}

public sealed class ConsolidateOutcome
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public int Chunks { get; set; }
    public int Candidates { get; set; }
    public int Written { get; set; }
    public int PersonaRev { get; set; }
    /// <summary>本轮更新了几个深层画像（人 + 群）。</summary>
    public int Absorbed { get; set; }
}

/// <summary>睡眠巩固：分片 → 分流 → 归并 → 校验 → 提交。</summary>
public sealed class Consolidator
{
    private const string DistillPrompt = """
        你是一个记忆整理器。下面是一个 QQ 会话里的一段聊天记录。

        请从中抽出值得长期记住的信息，输出 JSON 数组，不要解释、不要代码块。

        每项格式：
        {"kind":"identity|fact|preference|event|capability_gap|commitment",
         "text":"不超过60字的一句陈述",
         "tags":["最多3个短标签"],
         "weight":0-100,
         "confidence":0-1,
         "strictness":"hard|soft（只有 commitment 需要）",
         "validIn":["只在哪些会话成立，空数组=到处都算；只有 commitment 需要"],
         "evidence":["依据的消息序号"]}

        kind 的用法（严格区分「我」和「对方」）：

        - identity：关于**我自己**的身份与自我认识。包括：
          对方给我起的名字或称呼、我接受的名字、我的性格、我的说话方式、我的目标。
          只要对话里出现「给你起名」「你叫」「叫你」「称呼」「你是谁」「你叫什么」这类内容，
          **必须抽成 identity**，并用第一人称写。
          例：对方说「我想给你起…星璃」，我回答「可以。我记住了」
              → {"kind":"identity","text":"我叫星璃，是对方给我起的名字"}

        - commitment：**约定与边界**。这是最重要的一类，也是最容易被漏掉的一类。
          只要对话里出现「只在我们之间」「别到处说」「别跟别人讲」「别当成常态」
          「这次算了」「下不为例」「在外面别提」这类**约束**，就必须抽成 commitment。
          必须写清三件事：约束是什么、对谁、在哪些场合成立。
          例：对方说「叫姐姐可以，但别当成常态，叫完你可别到处说」，我答「好」
              → {"kind":"commitment","text":"叫姐姐只在私下，不要在外面说出来",
                  "strictness":"hard","validIn":["<当前会话scope>"]}
          例：对方说「以后别在群里提我熬夜的事」
              → {"kind":"commitment","text":"不要在群里提对方熬夜",
                  "strictness":"hard","validIn":["group:<那个群号>"]}
          **validIn 留空数组表示到处都算**；只在当前会话成立就填当前会话的 scope。
          strictness：明确说不许 → hard；只是"尽量/最好" → soft。

        - fact：关于对方的客观事实（身份、专业、技术栈、社会关系、年龄生日等）。
        - preference：对方或我表现出的偏好、期待、对我的要求。
        - event：值得记住的、发生过的事。
        - capability_gap：明显做不到、缺少能力的需求。

        规则：
        1. identity 只写「我」，绝不能写成对方的；
        2. 同一件事不要同时写成 fact 和 identity，优先 identity（如果主语是我）；
        3. **明显的事实不要反复记**。「对方叫星涵」这种写过一次就够了，
           换个说法再记一遍只会把记忆撑满、把真正重要的约束挤掉；
        4. **宁可少记一条事实，也不要漏记一条约束**。带条件的话（如果…就…、只…才…、
           别…、不许…、下次…）优先抽成 commitment；
        5. 一次性的临时让步不要写成习惯。「这次算了」不是「以后都可以」；
        6. 寒暄、灌水、一次性的临时话题不要抽；
        7. 拿不准就不要写，宁少勿错；
        8. 最多输出 8 条。

        如果没有值得记的，输出 []。
        """;

    private readonly Env _env;
    private readonly ChatLog _chatLog;
    private readonly Store _store;
    private readonly Persona _persona;
    private readonly DeepSeekClient _llm;
    private readonly SleepState _state;
    private readonly DeltaLog _delta;
    private readonly StyleStore _style;
    private readonly ILogger<Consolidator> _log;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public Consolidator(
        Env env, ChatLog chatLog, Store store, Persona persona,
        DeepSeekClient llm, SleepState state, DeltaLog delta, StyleStore style,
        ILogger<Consolidator> log)
    {
        _env = env;
        _chatLog = chatLog;
        _store = store;
        _persona = persona;
        _llm = llm;
        _state = state;
        _delta = delta;
        _style = style;
        _log = log;
    }

    private string WatermarkFile => Path.Combine(_env.SleepDir, "watermark.json");
    private string StateFile => Path.Combine(_env.SleepDir, "state.json");
    private string SignalFile => Path.Combine(_env.SleepDir, "signals.jsonl");

    public SleepWatermark LoadWatermark() =>
        Env.ReadJson<SleepWatermark>(WatermarkFile) ?? new SleepWatermark();

    public SleepState LoadState() => Env.ReadJson<SleepState>(StateFile) ?? new SleepState();

    public void SaveState()
    {
        Env.WriteAtomic(StateFile, JsonSerializer.Serialize(_state, AppJson.Config));
    }

    public void RaiseSignal(string reason)
    {
        var line = JsonSerializer.Serialize(new SleepSignal
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Reason = reason.Length > 200 ? reason[..200] : reason,
            Consumed = false,
        }, AppJson.Config);

        Directory.CreateDirectory(_env.SleepDir);
        File.AppendAllText(SignalFile, line + "\n");
    }

    public List<SleepSignal> Signals()
    {
        var list = new List<SleepSignal>();
        if (!File.Exists(SignalFile)) return list;

        foreach (var line in File.ReadAllLines(SignalFile))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var s = JsonSerializer.Deserialize<SleepSignal>(line, AppJson.Config);
                if (s is not null) list.Add(s);
            }
            catch { /* 跳过坏行 */ }
        }

        return list.TakeLast(100).ToList();
    }

    public bool HasPendingSignal() => Signals().Any(s => !s.Consumed);

    public bool IsRunning => _state.Sleeping;

    /// <summary>一轮巩固成功后把所有睡眠信号标记为已消费（保留历史记录）。</summary>
    public void ConsumeSignals()
    {
        if (!File.Exists(SignalFile)) return;

        try
        {
            var lines = File.ReadAllLines(SignalFile);
            var rewritten = new List<string>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                SleepSignal? s;
                try { s = JsonSerializer.Deserialize<SleepSignal>(line, AppJson.Config); }
                catch { rewritten.Add(line); continue; }

                if (s is null) { rewritten.Add(line); continue; }
                s.Consumed = true;
                rewritten.Add(JsonSerializer.Serialize(s, AppJson.Config));
            }

            Env.WriteAtomic(SignalFile, string.Join("\n", rewritten) + "\n");
        }
        catch (Exception ex)
        {
            _log.LogWarning("标记睡眠信号失败：{Error}", ex.Message);
        }
    }

    public async Task<ConsolidateOutcome> RunAsync(string by, CancellationToken ct)
    {
        if (!await _mutex.WaitAsync(TimeSpan.FromSeconds(5), ct))
            return new ConsolidateOutcome { Ok = false, Error = "已有一轮巩固在进行" };

        try
        {
            _state.Sleeping = true;
            var outcome = await RunInternalAsync(by, ct);
            _state.Sleeping = false;
            _state.LastAttemptTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // 只有成功才刷新 LastRunTs：失败要保留原值，否则退避会被拉长到一整个作息周期
            if (outcome.Ok) _state.LastRunTs = _state.LastAttemptTs;
            _state.LastOk = outcome.Ok;
            _state.LastError = outcome.Error;
            if (outcome.PersonaRev > 0) _state.LastRev = outcome.PersonaRev;
            SaveState();

            // 不论手动还是定时触发，成功后统一消费掉睡眠信号，避免信号永久挂着
            if (outcome.Ok) ConsumeSignals();

            return outcome;
        }
        catch (Exception ex)
        {
            _state.Sleeping = false;
            _state.LastOk = false;
            _state.LastError = ex.Message;
            SaveState();
            _log.LogError(ex, "巩固失败");
            return new ConsolidateOutcome { Ok = false, Error = ex.Message };
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>从私聊片段里挑我自己的回复，让模型自评「像不像我平时说话的样子」，然后交给</summary>
    private async Task CollectStyleCandidatesAsync(List<Chunk> chunks, CancellationToken ct)
    {
        const string prompt = """
            下面是一个 AI 自己说过的话。请判断：**这几句像不像同一个人的说话风格**，
            给每句单独打一个 0~100 的分数。

            判断标准是"像不像一个人自然说出来的样子"，不是"内容好不好"：
            - 有具体语气、停顿、动作（括号里的动作算强特征）；
            - 长短自然，不像公文、不像客服、不像在念稿；
            - 会说半句话，会留白。

            不要因为"内容重复"扣分。只输出 JSON 数组，不要解释、不要代码块：
            [{"i":0,"score":0-100}]
            """;

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();

            if (!chunk.Scope.StartsWith("private:", StringComparison.Ordinal)) continue;

            var mine = chunk.Entries
                .Where(e => e.IsSelf && e.Text.Trim().Length >= 4 && e.Text.Trim().Length <= 120)
                .TakeLast(8)
                .ToList();

            if (mine.Count < 2) continue;

            var sb = new StringBuilder();
            for (var i = 0; i < mine.Count; i++)
                sb.Append('[').Append(i).Append("] ").AppendLine(mine[i].Text);

            var res = await _llm.ChatAsync("distill", new[]
            {
                ChatMsg.System(prompt),
                ChatMsg.User(sb.ToString()),
            }, ct: ct);

            var scores = ParseStyleScores(res.Content);

            for (var i = 0; i < mine.Count; i++)
            {
                if (!scores.TryGetValue(i, out var score)) continue;

                var (accepted, mode) = _style.Propose(new StyleSample
                {
                    Ts = mine[i].Ts,
                    Scope = chunk.Scope,
                    Text = mine[i].Text.Trim(),
                    SelfScore = score,
                });

                if (accepted) _log.LogInformation("风格样本已自动收入（{Mode}）：{Text}", mode, mine[i].Text);
            }
        }
    }

    private static Dictionary<int, int> ParseStyleScores(string raw)
    {
        var map = new Dictionary<int, int>();
        var body = (raw ?? "").Trim();

        var start = body.IndexOf('[');
        var stop = body.LastIndexOf(']');
        if (start < 0 || stop <= start) return map;

        try
        {
            using var doc = JsonDocument.Parse(body[start..(stop + 1)]);

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!el.TryGetProperty("i", out var iEl) || !iEl.TryGetInt32(out var idx)) continue;
                if (!el.TryGetProperty("score", out var sEl) || !sEl.TryGetInt32(out var score)) continue;

                map[idx] = Math.Clamp(score, 0, 100);
            }
        }
        catch { /* 解析不出来就当没采到 */ }

        return map;
    }

    private async Task<ConsolidateOutcome> RunInternalAsync(string by, CancellationToken ct)
    {
        var watermark = LoadWatermark();
        var since = watermark.LastConsumedLogTs;

        //取水位线之后的全部记录（今天与昨天两个目录，覆盖跨天）
        var today = DateOnly.FromDateTime(DateTime.Now);
        var raw = new List<LogEntry>();
        raw.AddRange(_chatLog.Read(today));
        raw.AddRange(_chatLog.Read(today.AddDays(-1)));

        var fresh = raw
            .Where(e => e.Ts > since)
            .OrderBy(e => e.Ts)
            .ToList();

        if (fresh.Count == 0)
            return new ConsolidateOutcome { Ok = true, Error = "没有新记录", Chunks = 0 };

        //分片
        var chunks = BuildChunks(fresh);
        var cap = Math.Max(1, _env.Sleep.Chunk.MaxChunksPerRun);
        var capped = chunks.Take(cap).ToList();

        //分流（map）
        var candidates = new List<Candidate>();
        var failed = 0;
        foreach (var chunk in capped)
        {
            ct.ThrowIfCancellationRequested();
            var (ok, got) = await DistillAsync(chunk, ct);
            if (!ok) failed++;
            candidates.AddRange(got);
        }

        // 3.5 风格样本：从私聊里挑我自己的回复，自评「像不像我平时的样子」
        //
        // 为什么放在巩固阶段而不是每条回复都判断：这里是一次性批处理，
        // 不占用对话延迟；而"像我吗"本来就是个慢问题，不需要实时答案。
        if (_env.Style.Enabled && failed < capped.Count)
        {
            try { await CollectStyleCandidatesAsync(capped, ct); }
            catch (Exception ex) { _log.LogWarning("风格样本采集失败：{Error}", ex.Message); }
        }

        // 只要有分片失败就**不推进水位线**：否则那批对话会被永久跳过（静默丢失记忆）。
        // 下次重跑时靠提交阶段的「已存在条目」去重，不会重复写入。
        if (failed == capped.Count)
        {
            _log.LogWarning("巩固中止：{Failed}/{Total} 个分片蒸馏失败，水位线未推进", failed, capped.Count);
            return new ConsolidateOutcome
            {
                Ok = false,
                Error = $"全部 {capped.Count} 个分片蒸馏失败（模型不可达或返回不可解析），水位线未推进",
                Chunks = capped.Count,
            };
        }

        if (failed > 0)
        {
            _log.LogWarning("巩固中止：{Failed}/{Total} 个分片失败，水位线未推进，下次重试", failed, capped.Count);
            return new ConsolidateOutcome
            {
                Ok = false,
                Error = $"{failed}/{capped.Count} 个分片失败，水位线未推进，下次重试",
                Chunks = capped.Count,
            };
        }

        //归并（reduce）：精确匹配 + 近似匹配，保留置信度更高的那条
        var merged = new List<Candidate>();
        foreach (var c in candidates.Where(c => !string.IsNullOrWhiteSpace(c.Text)))
        {
            var dup = merged.FirstOrDefault(m =>
                MapKind(m.Kind) == MapKind(c.Kind) && IsSameClaim(m.Text, c.Text));

            if (dup is null)
            {
                merged.Add(c);
            }
            else if (c.Confidence > dup.Confidence)
            {
                dup.Text = c.Text;
                dup.Confidence = c.Confidence;
                dup.Weight = c.Weight;
            }
        }

        // 4.5 与库内已有条目去重，保证重跑幂等。
        // 必须按**近似**匹配：否则措辞改几个字就会被当成新记忆重复入库
        // （实际踩过：「搭建二次元与三次元之间的桥梁」存了三遍）。
        var scopesInvolved = merged.Select(c => c.Scope)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();

        var existing = new List<(string Kind, string Text)>();
        foreach (var scope in scopesInvolved)
            foreach (var e in _store.Read(scope))
                existing.Add((e.Kind, e.Text));

        merged = merged.Where(c =>
            !existing.Any(e => e.Kind == MapKind(c.Kind) && IsSameClaim(e.Text, c.Text))).ToList();

        //校验 + 提交
        var written = 0;

        foreach (var c in merged)
        {
            if (string.IsNullOrWhiteSpace(c.Scope)) continue;

            _store.Append(new MemEntry
            {
                Scope = c.Scope,
                Kind = MapKind(c.Kind),
                Text = c.Text,
                Tags = c.Tags,
                Weight = Math.Clamp(c.Weight, 0, 100),
                Level = "private",
                Source = "sleep:" + by,
                Confidence = Math.Clamp(c.Confidence, 0, 1),
                UserId = c.UserId,
                UserName = c.UserName,
                // 约定的生效范围：模型没给就默认"只在谈成的那次会话里成立"，
                // 这是更保守的一侧——把私下让步当成到处都成立，正是实盘那次翻车的成因。
                ValidIn = c.Kind == "commitment"
                    ? (c.ValidIn.Count > 0 ? c.ValidIn : new List<string> { c.Scope })
                    : new List<string>(),
                Strictness = c.Kind == "commitment"
                    ? (c.Strictness is "hard" or "soft" ? c.Strictness : "soft")
                    : "",
            });
            written++;
        }

        //人设：只有 identity 且预算允许时才尝试合并
        var personaRev = 0;

        // 同时看「本次新抽到的」和「库里已有的」identity：
        // 否则一旦某次已经把 identity 写进库、但人设保存失败，之后就会被去重挡掉，永远补不上。
        var identityTexts = merged
            .Where(c => c.Kind == "identity" && !string.IsNullOrWhiteSpace(c.Text))
            .Select(c => c.Text.Trim())
            .Concat(
                capped.Select(c => c.Scope).Distinct()
                    .SelectMany(scope => _store.Read(scope)
                        .Where(e => e.Kind == "identity" && !e.Tombstone)
                        .OrderByDescending(e => e.Weight)
                        .Take(5)
                        .Select(e => e.Text.Trim())))
            .Where(t => t.Length > 0)
            .Distinct()
            .Take(6)
            .ToList();

        if (identityTexts.Count > 0)
        {
            var view = _persona.Read();
            var current = view.Segments.FirstOrDefault(s => s.Key == "identity")?.Text ?? "";

            // 追加会让「我还没有名字」和「我叫星璃」同时存在，所以让模型整段重写：
            // 只保留仍然成立的，删掉与新认知矛盾的旧句。
            var mergedText = await RewriteIdentityAsync(current, identityTexts, ct)
                             ?? MergeIdentity(current, string.Join("\n", identityTexts));

            if (mergedText.Trim() != current.Trim())
            {
                var saved = _persona.Save(Rebuild(view.Md, "identity", mergedText), "sleep:" + by, isHuman: false);
                if (saved.Ok)
                {
                    personaRev = saved.Rev;

                    // identity 碎片已经并进自我描述，标记吸收，
                    // 否则它们会永远留在浅层，越积越多（和私聊 event 是同一个坑）
                    foreach (var scope in capped.Select(c => c.Scope).Distinct())
                    {
                        var ids = _store.Read(scope)
                            .Where(m => !m.Absorbed && !m.Tombstone && m.Kind == "identity")
                            .Select(m => m.Id)
                            .ToList();
                        if (ids.Count > 0) _store.MarkAbsorbed(scope, ids);
                    }
                }
                else
                {
                    _log.LogInformation("人设未更新：{Error}", saved.Error);
                }
            }
        }

        //吸收：把浅层碎片收敛进深层画像（反复巩固同一份画像，而不是继续堆碎片）
        var absorbed = 0;
        try
        {
            absorbed = await AbsorbAsync(capped, ct);
            var archived = _store.ArchiveExpiredShallow();
            if (archived > 0) _log.LogInformation("浅层碎片归档 {Count} 条", archived);
        }
        catch (Exception ex)
        {
            _log.LogWarning("深层画像吸收失败（不影响本轮提交）：{Error}", ex.Message);
        }

        //推进水位线（只在成功之后）
        var maxTs = capped.SelectMany(c => c.Entries).Max(e => e.Ts);
        var maxEntry = capped.SelectMany(c => c.Entries).OrderByDescending(e => e.Ts).First();
        Env.WriteAtomic(WatermarkFile, JsonSerializer.Serialize(new SleepWatermark
        {
            LastConsumedLogTs = maxTs,
            LastConsumedMsgId = maxEntry.MessageId,
        }, AppJson.Config));

        _log.LogInformation("巩固完成 by={By} 分片={Chunks} 候选={Candidates} 写入={Written} 画像={Absorbed} rev={Rev}",
            by, capped.Count, merged.Count, written, absorbed, personaRev);

        return new ConsolidateOutcome
        {
            Ok = true,
            Chunks = capped.Count,
            Candidates = merged.Count,
            Written = written,
            PersonaRev = personaRev,
            Absorbed = absorbed,
        };
    }

    // -- 分片 -------------------------------------------------------------
    public List<Chunk> BuildChunks(List<LogEntry> entries)
    {
        var cfg = _env.Sleep.Chunk;
        var maxEntries = Math.Max(10, cfg.MaxEntries);
        var maxChars = Math.Max(1000, cfg.MaxChars);
        var overlap = Math.Clamp(cfg.OverlapEntries, 0, maxEntries / 2);

        var chunks = new List<Chunk>();
        foreach (var group in entries.GroupBy(e => e.Scope))
        {
            var list = group.OrderBy(e => e.Ts).ToList();
            var i = 0;

            while (i < list.Count)
            {
                var take = new List<LogEntry>();
                var chars = 0;

                for (var j = i; j < list.Count; j++)
                {
                    var cost = list[j].Text.Length + 40;
                    if (take.Count >= maxEntries || chars + cost > maxChars) break;
                    take.Add(list[j]);
                    chars += cost;
                }

                if (take.Count == 0) take.Add(list[i]);

                chunks.Add(new Chunk { Scope = group.Key, Entries = take });

                // 片间重叠：下一片从 (末尾 - overlap) 开始，避免话题被切断
                var advance = take.Count - overlap;
                if (advance <= 0) advance = take.Count;
                i += advance;
            }
        }

        return chunks;
    }

    // -- 分流 -------------------------------------------------------------
    /// <summary>返回 (是否成功, 候选列表)。失败必须让调用方知道，否则水位线会错误推进。</summary>
    private async Task<(bool ok, List<Candidate> list)> DistillAsync(Chunk chunk, CancellationToken ct)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < chunk.Entries.Count; i++)
        {
            var e = chunk.Entries[i];
            var who = e.IsSelf ? "我" : (e.Name.Length > 0 ? e.Name : e.UserId);
            sb.Append('[').Append(i + 1).Append("] ").Append(who).Append("：")
                .Append(e.Text.Length > 300 ? e.Text[..300] : e.Text).Append('\n');
        }

        try
        {
            var result = await _llm.ChatAsync(
                "distill",
                new[]
                {
                    ChatMsg.System(DistillPrompt),
                    ChatMsg.User("会话：" + chunk.Scope + "\n\n聊天记录：\n" + sb),
                },
                ct: ct);

            var list = ParseCandidates(result.Content);
            list = GuardIdentity(list, chunk);

            // 兜底归属：候选默认落到本片的 scope，用户信息取本片里非自己的最后发言者
            var lastUser = chunk.Entries.LastOrDefault(e => !e.IsSelf);
            foreach (var c in list)
            {
                if (string.IsNullOrWhiteSpace(c.Scope)) c.Scope = chunk.Scope;
                if (string.IsNullOrWhiteSpace(c.UserId) && lastUser is not null && c.Kind != "identity")
                {
                    c.UserId = lastUser.UserId;
                    c.UserName = lastUser.Name;
                }
            }

            return (true, list);
        }
        catch (LlmException ex)
        {
            _log.LogWarning("分片蒸馏失败 scope={Scope}：{Error}", chunk.Scope, ex.Message);
            return (false, new List<Candidate>());
        }
    }

    /// <summary>identity 候选的硬校验：**「我叫 X」里的 X 必须是我自己的名字**。</summary>
    private List<Candidate> GuardIdentity(List<Candidate> list, Chunk chunk)
    {
        var selfNames = KnownSelfNames();
        if (selfNames.Count == 0) return list;   // 连自己叫什么都不确定，就别乱改

        var fallback = chunk.Entries.LastOrDefault(e => !e.IsSelf);

        foreach (var c in list)
        {
            if (c.Kind != "identity") continue;

            var claimed = ClaimedSelfName(c.Text);
            if (claimed.Length == 0) continue;                       // 没写名字，不动
            if (selfNames.Any(n => SameName(n, claimed))) continue;   // 名字对得上，放行

            _log.LogWarning(
                "identity 里出现不属于我的名字「{Claimed}」（我叫 {Self}），已降级为对方的事实：{Text}",
                claimed, string.Join('/', selfNames), c.Text);

            c.Kind = "fact";
            if (fallback is not null)
            {
                c.UserId = fallback.UserId;
                c.UserName = fallback.Name;
            }
        }

        return list;
    }

    /// <summary>我现在确认叫哪些名字：人设【身份】里的 + 配置的 bot_names。</summary>
    private List<string> KnownSelfNames()
    {
        var names = new List<string>();

        try
        {
            var segs = Persona.ParseSegments(_persona.Read().Md);
            if (segs.TryGetValue("identity", out var text))
            {
                var m = System.Text.RegularExpressions.Regex.Match(text, @"我叫\s*([^\s，,。、；;：:（）()]{1,12})");
                if (m.Success) names.Add(m.Groups[1].Value.Trim());
            }
        }
        catch { /* 人设读不到就只用配置 */ }

        foreach (var n in _env.Gate.BotNames)
            if (!string.IsNullOrWhiteSpace(n)) names.Add(n.Trim());

        return names.Where(n => n.Length > 0).Distinct().ToList();
    }

    /// <summary>从「我叫 X / 我的名字是 X」里抠出被断言的名字。</summary>
    private static string ClaimedSelfName(string text)
    {
        var body = text ?? "";
        var m = System.Text.RegularExpressions.Regex.Match(
            body, @"我(?:的名字是|叫)\s*([^\s，,。、；;：:（）()“”「」]{1,12})");
        if (!m.Success) return "";

        var name = m.Groups[1].Value.Trim();
        if (name is "AI" or "一个" or "个") return "";
        return name;
    }

    private static bool SameName(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || a.Contains(b, StringComparison.Ordinal)
        || b.Contains(a, StringComparison.Ordinal);

    public static List<Candidate> ParseCandidates(string text)    {
        var body = (text ?? "").Trim();
        if (body.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = body.IndexOf('\n');
            if (nl > 0) body = body[(nl + 1)..];
            var end = body.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) body = body[..end];
        }

        var start = body.IndexOf('[');
        var stop = body.LastIndexOf(']');
        if (start < 0 || stop <= start) return new List<Candidate>();

        try
        {
            return JsonSerializer.Deserialize<List<Candidate>>(body[start..(stop + 1)], AppJson.Options)
                   ?? new List<Candidate>();
        }
        catch
        {
            return new List<Candidate>();
        }
    }

    // -- 深层画像吸收 -----------------------------------------------------
    private const string PersonProfilePrompt = """
        你在维护「我对某个人」的长期画像。这类画像变化很慢，追求稳定，不追求细节。

        请输出更新后的画像正文，要求：
        1. 第三人称，不超过 {N} 行；
        2. **归纳而不是罗列**：写「她习惯深夜写代码」，不要写「她说她在写代码」；
        3. 保留仍然成立的；与新信息冲突的旧行要删掉，不要两句话都留着；
        4. 不要写「用户说」「她提到过」这类来源转述；
        5. **只写关于这个人的事**。凡是描述「我」（AI 自己）的能力、限制、原则、
           想做什幺的句子，一律删掉——那些属于我自己的自我描述，不属于这个人的画像；
        6. 没有依据的不要写；不确定的不要写；
        7. 只输出正文，不要标题、不要代码块、不要解释。

        如果没什么可更新的，就原样输出当前画像。
        """;

    private const string GroupProfilePrompt = """
        你在维护「我对一个 QQ 群」的短期印象。这类印象会过期，重点是**反复出现的模式**。

        请输出一个 JSON 对象，不要解释、不要代码块：
        {"profile":"不超过 {N} 行的印象正文",
         "patterns":[{"kind":"meme|targeted|topic","text":"不超过20字，尽量用原话","target":"目标QQ，非 targeted 留空"}]}

        profile 要求：
        1. 写这个群的气质、惯常的互动方式、最近在聊什么；
        2. 只保留最近仍然活跃的，长期没再出现的要删掉；
        3. 不要逐条罗列单次聊天；
        4. 语气平实，不评价群里的人；
        5. **你自己（这个 AI）不是群成员**，不要把我写进群的成员构成里。
           写「群里有两个人在互动」这种话之前先想清楚：其中一方是谁。
           实盘出过这样的画像——「这个群目前只有两个成员在互动：一个自称星璃，一个自称星涵」，
           把我自己算成了群成员。你要描述的是**其他人**在这个群里的样子。
        6. 提到人时用「名字（QQ号）」的形式，别只写名字——不同群里的同名不是同一个人。

        patterns 要求：
        1. meme：群里反复刷的话或梗（例：「又双叒叕」「这是命运的安排」）；
        2. targeted：大家反复对某个特定的人说的固定话（把那个人 QQ 填进 target）；
        3. topic：最近持续在聊的话题；
        4. **只列真正重复出现过的**，只出现过一次的不算；
        5. 最多 10 条；没有就输出空数组。
        """;

    private sealed class GroupDraft
    {
        public string Kind { get; set; } = "meme";
        public string Text { get; set; } = "";
        public string Target { get; set; } = "";
    }

    /// <summary>把浅层碎片收敛进深层画像。</summary>
    private async Task<int> AbsorbAsync(List<Chunk> chunks, CancellationToken ct)
    {
        var pol = _env.Memory;
        var flat = chunks.SelectMany(c => c.Entries)
            .GroupBy(e => e.MessageId)
            .Select(g => g.First())
            .OrderBy(e => e.Ts)
            .ToList();

        var updated = 0;
        var budget = Math.Max(1, pol.MaxObjectsPerSleep);

        // 人 → 人物画像（面向人、跨会话、慢过期）
        foreach (var group in flat
                     .Where(e => !e.IsSelf && e.UserId.Length > 0)
                     .GroupBy(e => e.UserId)
                     .Where(g => g.Count() >= Math.Max(1, pol.MinTurnsToUpdatePerson))
                     .OrderByDescending(g => g.Count()))
        {
            if (updated >= budget) break;
            var name = group.Select(e => e.Name).FirstOrDefault(n => n.Length > 0) ?? "";
            if (await RewritePersonProfileAsync(group.Key, name, ct)) updated++;
        }

        // 群 → 事件画像（面向群、会过期）
        // 遍历「库里已有的群 scope」，而不是只看本轮有日志的群：
        // 否则画像一旦为空、而当天该群又没说话，就永远重建不出来。
        var groupScopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in flat.Where(e => e.Scope.StartsWith("group:", StringComparison.Ordinal)).Select(e => e.Scope))
            groupScopes.Add(s);
        foreach (var s in _store.Scopes().Where(x => x.StartsWith("group_", StringComparison.Ordinal)))
            groupScopes.Add("group:" + s["group_".Length..]);

        foreach (var scope in groupScopes)
        {
            if (updated >= budget) break;
            var gid = scope["group:".Length..];
            if (await RewriteGroupProfileAsync(gid, ct)) updated++;
        }

        return updated;
    }

    private async Task<bool> RewritePersonProfileAsync(string userId, string name, CancellationToken ct)
    {
        // 收集该用户的**全部**碎片（含已吸收的）作为归纳原料。
        //
        // 为什么不是只取「未吸收」：深层画像的定义是「从原料反复归纳出的稳定结论」，
        // 不是「增量摘要」。只喂增量的话，画像一旦写歪就永远纠不回来——
        // 因为证据里再也看不到相反的例子。absorbed 只是「这一轮处理过」的标记，
        // 不能当过滤条件用。
        //
        // 三条排除规则（都是实盘踩出来的）：
        //   - identity / capability 是「关于我自己」的，绝不能进别人的画像；
        //   - 群聊里的 event 归群的事件画像管，私聊里的 event 只能归这个人的画像。
        var fragments = new List<(string Scope, MemEntry Entry)>();
        var pending = 0;

        foreach (var scope in _store.Scopes())
        {
            var isPrivate = scope.StartsWith("private_", StringComparison.Ordinal);
            foreach (var e in _store.Read(scope))
            {
                if (e.UserId != userId || e.Tombstone) continue;
                if (e.Kind is "identity" or "capability") continue;
                if (e.Kind == "event" && !isPrivate) continue;

                fragments.Add((scope, e));
                if (!e.Absorbed) pending++;
            }
        }

        if (fragments.Count == 0) return false;

        var current = _store.ReadProfile(userId);

        // 没有新料、且已经有画像 → 不必白花一次模型调用
        if (pending == 0 && current.Trim().Length > 0) return false;

        var bullets = string.Join("\n", fragments
            .OrderByDescending(f => f.Entry.Ts).Take(60)
            .Select(f => "- " + f.Entry.Text));

        string rewritten;
        try
        {
            var result = await _llm.ChatAsync("merge", new[]
            {
                ChatMsg.System(PersonProfilePrompt.Replace("{N}", Math.Max(10, _env.Memory.PersonProfileMaxLines).ToString())),
                ChatMsg.User($"对象：{(name.Length > 0 ? name : "未知")}（QQ {userId}）\n\n" +
                             $"当前画像：\n{(current.Length > 0 ? current : "（还没有画像）")}\n\n" +
                             $"最近的碎片：\n{bullets}"),
            }, ct: ct);

            rewritten = StripFences(result.Content);
        }
        catch (LlmException ex)
        {
            _log.LogWarning("人物画像重写失败 {User}：{Error}", userId, ex.Message);
            return false;
        }

        if (rewritten.Length == 0) return false;
        if (rewritten.Length > 4000) rewritten = rewritten[..4000];

        _store.SaveProfile(userId, rewritten);

        // 只把「这一轮新处理到」的碎片打标记；已吸收的老原料继续留作下一轮的归纳依据
        foreach (var g in fragments.Where(f => !f.Entry.Absorbed).GroupBy(f => f.Scope))
            _store.MarkAbsorbed(g.Key, g.Select(f => f.Entry.Id).ToList());

        RecordDelta("person", userId, "sleep", current, rewritten, pending, "人物画像重写");

        _log.LogInformation("人物画像已更新 {User}（原料 {All} 条，本轮新增 {Pending} 条）",
            userId, fragments.Count, pending);
        return true;
    }

    private async Task<bool> RewriteGroupProfileAsync(string groupId, CancellationToken ct)
    {
        var scope = "group:" + groupId;

        // 和人物画像同理：原料取该群**全部**碎片，不是只取本轮新增的几条
        var stored = _store.Read(scope).Where(e => !e.Tombstone).OrderByDescending(e => e.Ts).ToList();
        if (stored.Count == 0) return false;

        var currentProfile = _store.ReadGroupProfile(groupId);
        var patternSet = _store.ReadPatterns(groupId);

        var pending = stored.Count(e => !e.Absorbed && e.Kind == "event");
        if (pending == 0 && currentProfile.Trim().Length > 0) return false;

        var scene = string.Join("\n", stored.Take(80).OrderBy(e => e.Ts).Select(e =>
            (e.UserName.Length > 0 ? e.UserName : (e.UserId.Length > 0 ? e.UserId : "群里")) + "：" +
            (e.Text.Length > 120 ? e.Text[..120] : e.Text)));

        var known = string.Join("\n", patternSet.Patterns
            .OrderByDescending(p => p.Weight).Take(20)
            .Select(p => $"- [{p.Kind}] {p.Text}（出现 {p.Count} 次，权重 {p.Weight:F2}）"));

        string raw;
        try
        {
            var result = await _llm.ChatAsync("merge", new[]
            {
                ChatMsg.System(GroupProfilePrompt.Replace("{N}", Math.Max(10, _env.Memory.GroupProfileMaxLines).ToString())),
                ChatMsg.User($"当前印象：\n{(currentProfile.Length > 0 ? currentProfile : "（还没有）")}\n\n" +
                             $"已知的重复模式：\n{(known.Length > 0 ? known : "（暂无）")}\n\n" +
                             $"最近的群聊：\n{scene}"),
            }, ct: ct);

            raw = result.Content;
        }
        catch (LlmException ex)
        {
            _log.LogWarning("事件画像重写失败 {Group}：{Error}", groupId, ex.Message);
            return false;
        }

        var (profile, drafts) = ParseGroupProfile(raw);
        if (profile.Length == 0 && drafts.Count == 0) return false;

        if (profile.Length > 0)
            _store.SaveGroupProfile(groupId, profile.Length > 8000 ? profile[..8000] : profile);

        // 合并 pattern：已有的出现次数 +1、刷新时间；新的按 count=1 加入。
        // 归并用「包含即视为同一模式」而不是精确匹配——模型常把同一句话写成
        // 「星璃你在吗」和「星璃你在吗 / 几点了」两种形式，精确匹配会存成三条。
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var distinctDrafts = new List<GroupDraft>();
        foreach (var d in drafts)
        {
            if (Core.TextSim.Normalize(d.Text).Length == 0) continue;
            var dup = distinctDrafts.FirstOrDefault(x => x.Kind == d.Kind && SamePattern(x.Text, d.Text));
            if (dup is null) distinctDrafts.Add(d);
            else if (d.Text.Length < dup.Text.Length) dup.Text = d.Text;   // 用更短的当规范写法
        }

        foreach (var d in distinctDrafts)
        {
            var hit = patternSet.Patterns.FirstOrDefault(p => p.Kind == d.Kind && SamePattern(p.Text, d.Text));

            if (hit is null)
            {
                patternSet.Patterns.Add(new GroupPattern
                {
                    Id = "p" + Guid.NewGuid().ToString("N")[..6],
                    Kind = d.Kind,
                    Text = d.Text,
                    Target = d.Target,
                    Count = 1,
                    FirstSeen = now,
                    LastSeen = now,
                    Weight = 0,
                });
            }
            else
            {
                hit.Count++;
                hit.LastSeen = now;
                if (d.Target.Length > 0) hit.Target = d.Target;
                if (d.Text.Length < hit.Text.Length) hit.Text = d.Text;
            }
        }

        _store.RecomputePatterns(patternSet);
        _store.SavePatterns(patternSet);

        // 只把「事件类」碎片标记吸收；关于人的事实留给人物画像
        var eventIds = stored
            .Where(m => !m.Absorbed && m.Kind == "event")
            .Select(m => m.Id).ToList();
        _store.MarkAbsorbed(scope, eventIds);

        RecordDelta("group", groupId, "sleep", currentProfile, profile, eventIds.Count, "事件画像重写");

        _log.LogInformation("事件画像已更新 {Group}（pattern {Count} 条，吸收事件碎片 {Ev} 条）",
            groupId, patternSet.Patterns.Count, eventIds.Count);
        return true;
    }

    /// <summary>记录一次改写的前后差异，形成「自我变化度」时间序列。</summary>
    private void RecordDelta(string kind, string target, string by, string before, string after, int absorbed, string note)
    {
        try
        {
            var (added, removed) = Core.TextSim.LineDelta(before, after);
            _delta.Record(new DeltaRecord
            {
                Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Kind = kind,
                Target = target,
                By = by,
                Similarity = Math.Round(Core.TextSim.Similarity(before, after), 3),
                BeforeChars = before?.Length ?? 0,
                AfterChars = after?.Length ?? 0,
                AddedLines = added,
                RemovedLines = removed,
                Absorbed = absorbed,
                Note = note,
            });
        }
        catch { /* 忽略 */ }
    }

    /// <summary>两条 pattern 是否算同一个模式：归一化后相等，或一方是另一方的子串。</summary>
    private static bool SamePattern(string a, string b)
    {
        var na = Core.TextSim.Normalize(a);
        var nb = Core.TextSim.Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return na == nb;
        if (na == nb) return true;
        return na.Contains(nb, StringComparison.Ordinal) || nb.Contains(na, StringComparison.Ordinal);
    }

    private static (string profile, List<GroupDraft> drafts) ParseGroupProfile(string raw)
    {
        var body = StripFences(raw ?? "");
        var start = body.IndexOf('{');
        var end = body.LastIndexOf('}');
        if (start < 0 || end <= start) return (body.Trim(), new List<GroupDraft>());

        try
        {
            using var doc = JsonDocument.Parse(body[start..(end + 1)]);
            var root = doc.RootElement;

            var profile = root.TryGetProperty("profile", out var p) && p.ValueKind == JsonValueKind.String
                ? (p.GetString() ?? "").Trim()
                : "";

            var drafts = new List<GroupDraft>();
            if (root.TryGetProperty("patterns", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in arr.EnumerateArray())
                {
                    if (it.ValueKind != JsonValueKind.Object) continue;

                    var kind = it.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                        ? k.GetString() ?? "meme" : "meme";
                    var text = it.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                        ? (t.GetString() ?? "").Trim() : "";
                    var target = it.TryGetProperty("target", out var tg) && tg.ValueKind == JsonValueKind.String
                        ? (tg.GetString() ?? "").Trim() : "";

                    if (text.Length == 0) continue;
                    if (text.Length > 30) text = text[..30];

                    drafts.Add(new GroupDraft
                    {
                        Kind = kind is "meme" or "targeted" or "topic" ? kind : "meme",
                        Text = text,
                        Target = target,
                    });
                }
            }

            return (profile, drafts);
        }
        catch
        {
            return (body.Trim(), new List<GroupDraft>());
        }
    }

    private static string StripFences(string text)
    {
        var body = (text ?? "").Trim();
        if (!body.StartsWith("```", StringComparison.Ordinal)) return body;

        var nl = body.IndexOf('\n');
        if (nl > 0) body = body[(nl + 1)..];

        var end = body.LastIndexOf("```", StringComparison.Ordinal);
        if (end >= 0) body = body[..end];

        return body.Trim();
    }

    // -- 自我描述重写 -----------------------------------------------------
    private const string IdentityPrompt = """
        你在维护「我」的自我描述。下面给出当前的自我描述，以及今天整理出的新认知。

        请输出更新后的自我描述正文，要求：
        1. 第一人称，不超过 120 字，可以分几行；
        2. 只保留仍然成立的句子，与新认知冲突的旧句必须删掉
           （例如旧句写着「我还没有名字」，而新认知里有名字，就删掉旧句）；
        3. 不要写「用户说」「他告诉我」这类第三人称转述，直接写成我自己的认知；
        4. 不要编造没有依据的内容；不确定的就不要写；
        5. 只输出正文，不要标题、不要代码块、不要解释。

        如果没有值得更新的内容，就原样输出当前的自我描述。
        """;

    /// <summary>让模型整段重写 identity 段，避免与旧内容自相矛盾。失败时返回 null（调用方退回追加）。</summary>
    private async Task<string?> RewriteIdentityAsync(string current, List<string> newCognitions, CancellationToken ct)
    {
        try
        {
            var bullets = string.Join("\n", newCognitions.Select(t => "- " + t));
            var result = await _llm.ChatAsync(
                "merge",
                new[]
                {
                    ChatMsg.System(IdentityPrompt),
                    ChatMsg.User($"当前自我描述：\n{current}\n\n今天的新认知：\n{bullets}"),
                },
                ct: ct);

            var text = result.Content.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var nl = text.IndexOf('\n');
                if (nl > 0) text = text[(nl + 1)..];
                var end = text.LastIndexOf("```", StringComparison.Ordinal);
                if (end >= 0) text = text[..end];
                text = text.Trim();
            }

            if (text.Length == 0) return null;

            // 防止模型把整段换成空泛内容或超长
            if (text.Length > 600) text = text[..600];
            return text;
        }
        catch (LlmException ex)
        {
            _log.LogWarning("自我描述重写失败，退回追加：{Error}", ex.Message);
            return null;
        }
    }

    // -- 工具 -------------------------------------------------------------
    /// <summary>能力缺口永远归到 capability，不进人设。</summary>
    private static string MapKind(string kind) => kind switch
    {
        "identity" => "identity",
        "preference" => "preference",
        "event" => "event",
        "quote" => "quote",
        "capability_gap" => "capability",
        "commitment" => "commitment",
        _ => "fact",
    };

    private static string Normalize(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    /// <summary>只保留字母与汉字并转小写，用于相似度比较（去掉标点带来的噪声）。</summary>
    private static string NormalizeAlnum(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in (text ?? "").ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    private static HashSet<string> Bigrams(string s)
    {
        var set = new HashSet<string>();
        if (s.Length == 0) return set;
        if (s.Length == 1) { set.Add(s); return set; }
        for (var i = 0; i < s.Length - 1; i++) set.Add(s.Substring(i, 2));
        return set;
    }

    /// <summary>两条陈述是否算「同一件事」。</summary>
    private static bool IsSameClaim(string a, string b)
    {
        var na = NormalizeAlnum(a);
        var nb = NormalizeAlnum(b);
        if (na.Length == 0 || nb.Length == 0) return na == nb;
        if (na == nb) return true;

        // 数字序列不同 → 事实大概率不同，绝不合并
        var da = DigitsOf(na);
        var db = DigitsOf(nb);
        if (!string.Equals(da, db, StringComparison.Ordinal)) return false;

        var sa = Bigrams(na);
        var sb = Bigrams(nb);
        var inter = 0;
        foreach (var g in sa)
            if (sb.Contains(g)) inter++;

        var union = sa.Count + sb.Count - inter;
        return union > 0 && (double)inter / union >= 0.85;
    }

    private static string DigitsOf(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
            if (char.IsDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>去掉标点与空白后的键：相同即视为同一条（安全去重用）。</summary>
    public static string SimilarityKey(string text) => Core.TextSim.Normalize(text);

    /// <summary>供后台「疑似重复」使用：0~1 的相似度（不做数字守卫，仅用于展示）。</summary>
    public static double Similarity(string a, string b) => Core.TextSim.Similarity(a, b);

    private static string MergeIdentity(string current, string addition)
    {
        var body = (current ?? "").Trim();
        if (body.Length == 0) return addition;
        if (body.Contains(addition, StringComparison.Ordinal)) return body;

        var lines = new List<string> { body, addition };
        return string.Join("\n", lines.Where(l => l.Trim().Length > 0));
    }

    /// <summary>只替换指定段的内容，其余原样保留。</summary>
    public static string Rebuild(string md, string key, string newText)
    {
        var open = $"<!--seg:{key}-->";
        var close = $"<!--/seg:{key}-->";

        var i = md.IndexOf(open, StringComparison.Ordinal);
        var j = md.IndexOf(close, StringComparison.Ordinal);
        if (i < 0 || j < 0 || j <= i) return md;

        return md[..(i + open.Length)] + "\n" + newText.Trim() + "\n" + md[j..];
    }
}

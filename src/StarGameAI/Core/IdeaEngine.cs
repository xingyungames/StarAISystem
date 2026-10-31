using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using StarGameAI.Gateway;
using StarGameAI.Llm;
using StarGameAI.Memory;
using StarGameAI.Pipeline;

namespace StarGameAI.Core;

public sealed class IdeaCandidate
{
    public string Intent { get; set; } = "speech";
    public string Target { get; set; } = "";
    public string Text { get; set; } = "";
    public string Reason { get; set; } = "";
    /// <summary>
    /// self = 自我意象；context = 对方给的需求
    /// </summary>
    public string Origin { get; set; } = "context";
}

/// <summary>
/// 一轮发散的「分层输入」，用于后台看清楚它到底看到了什么
/// </summary>
public sealed class IdeaInputs
{
    /// <summary>
    /// 人设
    /// </summary>
    public List<string> Self { get; set; } = new();
    /// <summary>
    /// 该会话最近的几条对话
    /// </summary>
    public List<string> Recent { get; set; } = new();
    /// <summary>
    /// 浅层记忆里关于这个对象的条目
    /// </summary>
    public List<string> Memories { get; set; } = new();
    /// <summary>
    /// 画像与群体模式：深层画像摘要、群里的活跃梗、在线状态
    /// </summary>
    public List<string> Profile { get; set; } = new();
    /// <summary>
    /// 最近更在意的事
    /// </summary>
    public List<string> Weights { get; set; } = new();
    /// <summary>
    /// 上一次的思绪
    /// </summary>
    public string PrevThought { get; set; } = "";
    /// <summary>
    /// 上一次想到的
    /// </summary>
    public List<string> PrevThoughts { get; set; } = new();
}

/// <summary>
/// 收敛给出的一个决策项
/// </summary>
public sealed class IdeaDecision
{
    /// <summary>
    /// speak / boost / nothing
    /// </summary>
    public string Kind { get; set; } = "";
    public string Target { get; set; } = "";
    public string Text { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>
/// 一轮「想事情」的完整记录
/// </summary>
public sealed class IdeaRound
{
    public string Id { get; set; } = "";
    public long Ts { get; set; }
    public int Ms { get; set; }
    public string Target { get; set; } = "";
    public string TargetName { get; set; } = "";
    /// <summary>
    /// 发散时喂给模型的材料原文（母题）
    /// </summary>
    public string Subject { get; set; } = "";
    /// <summary>
    /// 思想链的版本号
    /// </summary>
    public int Version { get; set; }
    /// <summary>
    /// 分层输入快照
    /// </summary>
    public IdeaInputs Inputs { get; set; } = new();
    /// <summary>
    /// 意图子 AI 的判定：这一轮到底有没有开口动机
    /// </summary>
    public SpeakIntent Intent { get; set; } = new();
    /// <summary>
    /// 发散出来的自由联想（允许无厘头）两路节点的合并结果
    /// </summary>
    public List<string> Thoughts { get; set; } = new();
    /// <summary>
    /// 用户需求：从上下文发呆——对方想让我做什么、有什么没接住
    /// </summary>
    public List<string> DivUser { get; set; } = new();
    /// <summary>
    /// 自我意图：从记忆碎片发呆——我自己在意什么、想做什么
    /// </summary>
    public List<string> DivSelf { get; set; } = new();
    /// <summary>
    /// 各阶段的耗时（毫秒），给瀑布图用
    /// </summary>
    public int MsIntent { get; set; }
    public int MsDivUser { get; set; }
    public int MsDivSelf { get; set; }
    public int MsConverge { get; set; }
    public int MsWeight { get; set; }

    /// <summary>
    /// 「怎么发、什么时候发」花了多少毫秒
    /// </summary>
    public int MsRoute { get; set; }

    /// <summary>
    /// 落点与时机的判定结果
    /// </summary>
    public RouteVerdict? Route { get; set; }

    /// <summary>
    /// 真正发到哪儿
    /// </summary>
    public string SentTo { get; set; } = "";
    /// <summary>
    /// 这一轮整理出来的加权意向（合并结果）
    /// </summary>
    public WeightAgent.MergeOutcome? Weights { get; set; }
    /// <summary>
    /// 收敛出的「思绪」：把散乱联想压成一句连贯的内在念头
    /// </summary>
    public string Thought { get; set; } = "";
    /// <summary>
    /// 这一轮落到哪个主题上（短标签）。近似归并就是按它比的，不是按句子比
    /// </summary>
    public string Topic { get; set; } = "";
    /// <summary>
    /// 这个主题已经连续想了多少轮（含本轮）1 = 新话题，≥3 = 卡住了
    /// </summary>
    public int TopicRepeat { get; set; }
    /// <summary>
    /// 决策：所以我应该干什么（可能有多条，也可能只有 nothing）
    /// </summary>
    public List<IdeaDecision> Decisions { get; set; } = new();
    /// <summary>
    /// 本轮提升权重的事项（收敛的第二档结果）
    /// </summary>
    public string Boost { get; set; } = "";
    public double BoostAmount { get; set; }
    /// <summary>
    /// 落定：sent / no_idea / weight_only / blocked / error / skipped
    /// </summary>
    public string Outcome { get; set; } = "";
    public List<IdeaCandidate> Candidates { get; set; } = new();
    /// <summary>
    /// 收敛挑中的下标，null = 决定什么都不做
    /// </summary>
    public int? Picked { get; set; }
    public string Why { get; set; } = "";
    public string BlockedBy { get; set; } = "";
    public string Detail { get; set; } = "";
    /// <summary>
    /// 真正说出去的话
    /// </summary>
    public string Spoke { get; set; } = "";
    public int Tokens { get; set; }
}

/// <summary>
/// 给后台看的运行状态
/// </summary>
public sealed class IdeaStatus
{
    public bool Enabled { get; set; }
    public bool Running { get; set; }
    /// <summary>
    /// idle / 准备 / 发散中 / 收敛中 / 发送中
    /// </summary>
    public string Phase { get; set; } = "idle";
    public string PhaseDetail { get; set; } = "";
    public long PhaseStartedAt { get; set; }
    public long NextTickAt { get; set; }
    public string LastSkip { get; set; } = "";
    public int RoundsLastHour { get; set; }
    public int RoundsToday { get; set; }
    public IdeaRound? LastRound { get; set; }
}

/// <summary>
/// 思想层 P3/P4：发散 → 收敛 → 准入 → 执行
/// </summary>
public sealed class IdeaEngine : BackgroundService
{
    private const int TickSeconds = 15;

    private readonly Env _env;
    private readonly Activity _activity;
    private readonly Presence _presence;
    private readonly Contacts _contacts;
    private readonly Store _store;
    private readonly Persona _persona;
    private readonly ChatLog _chatLog;
    private readonly Sender _sender;
    private readonly Dedup _dedup;
    private readonly IdeaGate _gate;
    private readonly IdeaChainStore _chains;
    private readonly IdeaStore _ideaStore;
    private readonly DeepSeekClient _llm;
    private readonly Tracer _tracer;
    private readonly Metrics _metrics;
    private readonly IntentAgent _intent;
    private readonly WeightAgent _weights;
    private readonly RouteAgent _route;
    private readonly ILogger<IdeaEngine> _log;

    private readonly ConcurrentQueue<IdeaRound> _rounds = new();
    private volatile IdeaRound? _lastRound;
    private volatile string _phase = "idle";
    private volatile string _phaseDetail = "";
    private long _phaseStartedAt;
    private long _nextTickAt;
    private volatile string _lastSkip = "";
    private int _busy;

    public IdeaEngine(Env env, Activity activity, Presence presence, Contacts contacts,
        Store store, Persona persona, ChatLog chatLog, Sender sender, Dedup dedup,
        IdeaGate gate, IdeaChainStore chains, IdeaStore ideaStore, DeepSeekClient llm,
        Tracer tracer, Metrics metrics,
        IntentAgent intent, WeightAgent weights, RouteAgent route, ILogger<IdeaEngine> log)
    {
        _env = env;
        _activity = activity;
        _presence = presence;
        _contacts = contacts;
        _store = store;
        _persona = persona;
        _chatLog = chatLog;
        _sender = sender;
        _dedup = dedup;
        _gate = gate;
        _chains = chains;
        _ideaStore = ideaStore;
        _llm = llm;
        _tracer = tracer;
        _metrics = metrics;
        _intent = intent;
        _weights = weights;
        _route = route;
        _log = log;
    }

    public IdeaStatus Status()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var dayStart = new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds();
        var rounds = CountedRounds();

        return new IdeaStatus
        {
            Enabled = _env.Idea.Enabled && _env.Idea.Think.Enabled,
            Running = _phase != "idle",
            Phase = _phase,
            PhaseDetail = _phaseDetail,
            PhaseStartedAt = Interlocked.Read(ref _phaseStartedAt),
            NextTickAt = Interlocked.Read(ref _nextTickAt),
            LastSkip = _lastSkip,
            RoundsLastHour = rounds.Count(r => r >= now - 3600),
            RoundsToday = rounds.Count(r => r >= dayStart),
            LastRound = _lastRound,
        };
    }

    /// <summary>
    /// 配额计数用的轮次时间戳
    /// </summary>
    private List<long> CountedRounds()
    {
        var stamps = new List<long>();
        //去重
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var r in _rounds.ToArray())
        {
            if (r.Id.Length > 0 && !seen.Add(r.Id)) continue;
            stamps.Add(r.Ts);
        }

        try
        {
            //读数据
            var path = Path.Combine(_env.IdeaDir, "rounds.jsonl");
            if (File.Exists(path))
            {
                var lines = File.ReadAllLines(path);
                var take = Math.Max(0, lines.Length - 400);

                for (var i = take; i < lines.Length; i++)
                {
                    if (lines[i].Length == 0) continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(lines[i]);
                        var root = doc.RootElement;
                        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        if (id.Length > 0 && !seen.Add(id)) continue;
                        stamps.Add(root.TryGetProperty("ts", out var tsEl) && tsEl.TryGetInt64(out var ts) ? ts : 0);
                    }
                    catch { /* 单行坏了跳过 */ }
                }
            }
        }
        catch { /* 读不到就用内存的 */ }

        return stamps;
    }

    public List<IdeaRound> RecentRounds(int max = 30) =>
        _rounds.Reverse().Take(max).ToList();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        //延迟等加载
        try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
        catch (OperationCanceledException) { return; }

        Interlocked.Exchange(ref _nextTickAt, Now() + _env.Idea.IntervalMinutes * 60L);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(TickSeconds));

        //主循环
        while (!ct.IsCancellationRequested)
        {
            try
            {
                //到时间点执行
                if (Now() >= Interlocked.Read(ref _nextTickAt))
                {
                    var cfg = _env.Idea;
                    Interlocked.Exchange(ref _nextTickAt, Now() + Math.Max(1, cfg.IntervalMinutes) * 60L);

                    if (Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
                    {
                        try { await RunRoundAsync(ct); }
                        finally { Interlocked.Exchange(ref _busy, 0); SetPhase("idle", ""); }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "思想层一轮失败"); }

            try
            {
                if (!await timer.WaitForNextTickAsync(ct)) break;
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    //坑：只查进程内 Activity 的话，重启后思想层会永远跳过
    /// <summary>
    /// 「最近有没有对话」的判断
    /// </summary>
    private int IdleSeconds()
    {
        var idle = _activity.IdleSeconds();
        if (idle != int.MaxValue) return idle;

        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var last = _chatLog.Read(today).LastOrDefault()
                       ?? _chatLog.Read(today.AddDays(-1)).LastOrDefault();

            if (last is not null && last.Ts > 0)
                return (int)Math.Max(0, Now() - last.Ts);
        }
        catch { /* 读日志失败就当没有活动 */ }

        return int.MaxValue;
    }

    /// <summary>
    /// [Debug]手动触发一轮
    /// </summary>
    public async Task<(bool ok, string message)> RunOnceAsync(string? target, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return (false, "正在想，等这一轮结束");

        try
        {
            var t = (target ?? "").Trim();
            if (t.Length == 0)
            {
                var targets = PickTargets(1);
                if (targets.Count == 0) return (false, "没有可以想的对象（白名单里没有群，也没有联系人）");
                t = targets[0];
            }

            _lastSkip = "";
            await ThinkAboutAsync(t, ct);
            return (true, "已经想了一轮：" + t);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            SetPhase("idle", "");
        }
    }

    private void SetPhase(string phase, string detail)
    {
        _phase = phase;
        _phaseDetail = detail;
        Interlocked.Exchange(ref _phaseStartedAt, Now());
    }

    /// <summary>
    /// 开始思想
    /// </summary>
    /// <param name="ct"></param>
    /// <returns></returns>
    private async Task RunRoundAsync(CancellationToken ct)
    {
        var cfg = _env.Idea;
        var think = cfg.Think;

        if (!cfg.Enabled || !think.Enabled) { _lastSkip = "思想层已关闭"; return; }
        if (!_llm.HasKey) { _lastSkip = "没有可用的模型 Key"; return; }

        var status = Status();
        if (status.RoundsLastHour >= think.MaxRoundsPerHour)
        {
            _lastSkip = $"本小时已想 {status.RoundsLastHour} 次（上限 {think.MaxRoundsPerHour}）";
            return;
        }

        if (status.RoundsToday >= think.MaxRoundsPerDay)
        {
            _lastSkip = $"今天已想 {status.RoundsToday} 次（上限 {think.MaxRoundsPerDay}）";
            return;
        }

        if (IdleSeconds() > Math.Max(1, think.ActiveWithinMinutes) * 60)
        {
            _lastSkip = $"最近 {think.ActiveWithinMinutes} 分钟没有对话，不值得想";
            return;
        }

        // 静默时段整轮跳过
        // 实盘 161 轮里 92 轮落在 01:00-08:00，烧掉 29 万 token，
        // 而静默时段一道闸就让它们全都发不出去，只剩纯消耗
        if (think.SkipQuietHours && cfg.QuietHours.Contains(DateTime.Now))
        {
            _lastSkip = $"静默时段 {cfg.QuietHours.Start}-{cfg.QuietHours.End}，不空转";
            return;
        }

        _lastSkip = "";

        var targets = PickTargets(think.MaxTargetsPerRound);
        if (targets.Count == 0) { _lastSkip = "没有可以想的对象"; return; }

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            // 没有新输入就不想
            // 一直卡在一个思绪的可能修复：群安静、消息还是那几条，每 5 分钟重新发散一遍，同一句思绪换个说法交上来
            var inputTs = LastOtherMessageTs(target);
            var chain = _chains.Load(target);
            if (chain.LastInputTs > 0 && inputTs <= chain.LastInputTs)
            {
                _lastSkip = $"{target} 没有新消息，不想";
                _log.LogInformation("跳过 {Target}：输入水位 {Input} <= 已想过 {Done}",
                    target, inputTs, chain.LastInputTs);
                continue;
            }

            await ThinkAboutAsync(target, ct);
        }
    }

    private List<string> PickTargets(int max)
    {
        var candidates = new List<(string target, long lastTs)>();

        foreach (var gid in _env.Server.Whitelist.Groups)
        {
            var t = "group:" + gid;
            candidates.Add((t, LastMessageTs(t)));
        }

        foreach (var c in _contacts.All())
        {
            // 私聊目标必须是"能私聊的人"
            if (!_contacts.IsPrivatePartner(c.UserId)) continue;

            var t = "private:" + c.UserId;
            candidates.Add((t, Math.Max(c.LastSeen, LastMessageTs(t))));
        }

        // 按‘最近有没有人在这儿说话’排序
        return candidates
            .Where(c => c.lastTs > 0)
            .OrderByDescending(c => c.lastTs)
            .Select(c => c.target)
            .Distinct()
            .Take(Math.Max(1, max))
            .ToList();
    }

    /// <summary>
    /// 现在能去说话的地方
    /// </summary>
    private List<RouteSlot> Slots()
    {
        var list = new List<RouteSlot>();

        //遍历白名单群
        //落点必须是 kind:id，裸号一进发送层就会被当成群号
        foreach (var gid in _env.Server.Whitelist.Groups)
            list.Add(new RouteSlot("group", "group:" + gid, "群（群号 " + gid + "）"));
        //遍历私聊白名单
        foreach (var c in _contacts.All())
        {
            //不能私聊的人跳过
            if (!_contacts.IsPrivatePartner(c.UserId)) continue;

            var name = c.Name.Trim();
            if (name.Length == 0) name = c.Aliases.FirstOrDefault() ?? c.UserId;
            list.Add(new RouteSlot("private", "private:" + c.UserId, "私聊 " + name + "（QQ " + c.UserId + "）"));
        }

        return list;
    }

    private static string NowText()
    {
        var now = DateTime.Now;
        return now.ToString("yyyy-MM-dd HH:mm") + "，星期" + "日一二三四五六"[(int)now.DayOfWeek];
    }

    /// <summary>
    /// 对方此刻在不在、在干嘛
    /// </summary>
    private string PresenceText(string target)
    {
        var uid = IdeaGate.UserOf(target);
        if (uid.Length == 0) return "";

        try
        {
            var pv = _presence.Effective(uid);
            if (pv is null) return "";
            return "对方现在：" + pv.State + "（" + pv.Source + (pv.Note.Length > 0 ? "：" + pv.Note : "") + "）";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 这个会话今天最后一条消息的时间；没说过话返回 0
    /// </summary>
    private long LastMessageTs(string scope)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var last = _chatLog.Read(today, scope).LastOrDefault();
            return last?.Ts ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 这个会话里别人说的最后一条消息的时间
    /// <returns>没说过话返回 0</returns>
    /// </summary>
    private long LastOtherMessageTs(string scope)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var last = _chatLog.Read(today, scope).Where(e => !e.IsSelf).LastOrDefault();
            return last?.Ts ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private async Task ThinkAboutAsync(string target, CancellationToken ct)
    {
        var think = _env.Idea.Think;
        var round = new IdeaRound
        {
            Id = "r" + Guid.NewGuid().ToString("N")[..8],
            Ts = Now(),
            Target = target,
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var userOf = IdeaGate.UserOf(target);
        round.TargetName = userOf.Length > 0 ? (_contacts.ByQq(userOf)?.Name ?? userOf) : target;

        try
        {
            var chain = _chains.Load(target);

            // 链滚太久会飘走
            // 到上限就重置，重新吃一次完整材料
            if (chain.Version >= Math.Max(2, _env.Idea.Chain.MaxVersions))
            {
                _log.LogInformation("思想链已到 {V} 版上限，重置：{Target}", chain.Version, target);
                chain = new IdeaChain { Target = target };
                _chains.Save(chain);
            }

            round.Version = chain.Version + 1;

            // 看材料，问'有没有开口动机'
            // CaptureInputs 在 BuildContext 里跑，意图子 AI 要用这份分层输入
            // 发散要等意图判定之后——没有动机就不发散，省掉两次调用
            SetPhase("判断意图中", $"第 {round.Version} 版：{round.TargetName}");

            // 把分层输入采集进 round.Inputs
            // 母题由两路节点的输入拼出来
            BuildContext(target, chain, round.Inputs);

            // 只决定「要不要开口」，不决定「要不要想」
            var selfOnly = false;

            if (_env.Idea.Intent.Enabled)
            {
                var lastTopic = chain.Topics.Count > 0 ? chain.Topics[^1] : "";
                var stuck = lastTopic.Length > 0 ? TrailingRepeat(chain.Topics, lastTopic) : 0;

                var swIntent = System.Diagnostics.Stopwatch.StartNew();

                // 够重但一直没做的意向 → 交给意图子 AI 当动机来源
                var mature = _chains.MatureWeights(3)
                    .Select(x => (
                        text: (x.Title.Length > 0 ? x.Title : IdeaWeight.TitleOf(x.Kind, x.Who, x.Target))
                              + "：" + string.Join("；", x.Items.Select(i => i.Text)),
                        rounds: x.MatureRounds))
                    .ToList();

                round.Intent = await _intent.ExtractAsync(
                    target, round.Inputs, round.Inputs.Weights, mature,
                    chain.Thought, lastTopic, stuck, ct);
                swIntent.Stop();
                round.MsIntent = (int)swIntent.ElapsedMilliseconds;

                round.Tokens += round.Intent.Tokens;
                _metrics.Inc("idea.intent");

                if (!round.Intent.HasIntent)
                {
                    // 意图判定本身失败（网络/解析）直接收尾。
                    if (round.Intent.Kind == "error")
                    {
                        round.Outcome = "error";
                        round.Why = round.Intent.NotYet.Length > 0 ? round.Intent.NotYet : "意图判定失败";
                        Finish(round, sw);
                        return;
                    }

                    selfOnly = true;
                    round.Why = round.Intent.NotYet.Length > 0
                        ? round.Intent.NotYet
                        : "此刻没有具体的开口动机";
                }
            }

            // 发散
            SetPhase("发散中", $"第 {round.Version} 版想法：{round.TargetName}");

            var subject = BuildUserContext(target, chain, round.Inputs, round.Intent);
            var selfInput = BuildSelfContext(chain, round.Inputs);
            round.Subject = TrimTo(subject + "\n----- 以下是自我意图那一路 -----\n" + selfInput, 2000);

            var maxThoughts = Math.Max(2, _env.Idea.Chain.MaxThoughts);
            var half = Math.Max(1, maxThoughts / 2);

            var userPrompt = DivergeUserPrompt.Replace("{N}", half.ToString());
            var selfPrompt = DivergeSelfPrompt.Replace("{N}", half.ToString());

            // 没有开口动机时，跑它只会凑出"硬找话说"的念头
            // 如果你喜欢让TA尴尬的唠嗑……？
            if (!selfOnly)
            {
                var swUser = System.Diagnostics.Stopwatch.StartNew();
                var userRes = await _llm.ChatAsync("diverge", new[]
                {
                    ChatMsg.System(userPrompt),
                    ChatMsg.User(subject),
                }, ct: ct);
                swUser.Stop();
                round.MsDivUser = (int)swUser.ElapsedMilliseconds;
                round.Tokens += userRes.Usage.TotalTokens;
                round.DivUser = ParseThoughts(userRes.Content);
            }

            var swSelf = System.Diagnostics.Stopwatch.StartNew();
            var selfRes = await _llm.ChatAsync("diverge", new[]
            {
                ChatMsg.System(selfPrompt),
                ChatMsg.User(selfInput),
            }, ct: ct);
            swSelf.Stop();
            round.MsDivSelf = (int)swSelf.ElapsedMilliseconds;
            round.Tokens += selfRes.Usage.TotalTokens;
            round.DivSelf = ParseThoughts(selfRes.Content);

            // 两个节点合成思想
            round.Thoughts = round.DivUser.Concat(round.DivSelf).ToList();

            // 推进思想链：v_{k+1} 就是这一轮的产物
            chain.Version = round.Version;
            chain.Thoughts = round.Thoughts.ToList();
            chain.UpdatedAt = Now();
            // 记下这一轮输入水位。下一轮如果还是这批消息，就不再想一遍 没输入再想只会产出一句和上次几乎一样的思绪
            chain.LastInputTs = LastOtherMessageTs(target);
            chain.History.Insert(0, new IdeaChainVersion
            {
                V = chain.Version,
                Ts = chain.UpdatedAt,
                Thoughts = round.Thoughts.ToList(),
            });
            var keep = Math.Max(1, _env.Idea.Chain.HistoryKeep);
            if (chain.History.Count > keep) chain.History = chain.History.Take(keep).ToList();
            _chains.Save(chain);

            if (round.Thoughts.Count == 0)
            {
                round.Outcome = "no_idea";
                round.Why = "这一轮没冒出念头";
                Finish(round, sw);
                return;
            }

            _metrics.Inc("idea.candidates");

            // 收敛：两路念头压成 [答复] + [意图]
            SetPhase("收敛中", $"从 {round.Thoughts.Count} 条想法里压出答复和意图");

            var swConv = System.Diagnostics.Stopwatch.StartNew();
            //注入提示词
            var convergeSystem = selfOnly
                ? ConvergePrompt + "\n\n【这一轮的限制】刚才没人在跟你说话，所以**不要回应任何人**。" +
                  "但**你自己想做的事可以现在就去做**：只要 intent.origin = \"self\"," +
                  "kind 允许写成 \"act\"——自我意象不需要由头，不用等谁来邀请。" +
                  "反过来，origin = \"context\" 的 act 会被直接丢掉（没那个场合）"
                : ConvergePrompt;
            var pickRes = await _llm.ChatAsync("converge", new[]
            {
                ChatMsg.System(convergeSystem),
                ChatMsg.User(BuildThoughtsText(round.DivUser, round.DivSelf)),
            }, ct: ct);
            swConv.Stop();
            round.MsConverge = (int)swConv.ElapsedMilliseconds;

            round.Tokens += pickRes.Usage.TotalTokens;
            var (thought, topic, why, boost, action) = ParseVerdict(pickRes.Content, target);
            round.Thought = thought;
            if (why.Length > 0) round.Why = why;

            // 兜底：提示词说了不许说话，但模型未必听
            if (selfOnly && action is not null && action.Origin != "self")
            {
                _log.LogInformation("没开口动机却给了动作，已丢弃：{Target} → {Text}",
                    target, action.Text.Length > 40 ? action.Text[..40] : action.Text);
                action = null;
            }

            // 判断这一轮是不是又在想上一次那件事
            // 比的是 topic 标签而不是整句
            var topicKey = topic.Length > 0 ? topic : FirstSentence(thought, 12);
            round.Topic = topicKey;
            round.TopicRepeat = 1 + TrailingRepeat(chain.Topics, topicKey);

            if (round.TopicRepeat >= 3)
                _log.LogInformation("思想链在原地踏步：{Target} 连续 {N} 轮都在「{Topic}」",
                    target, round.TopicRepeat, topicKey);

            // 这件事已经提过了，就别再想一遍。
            if (IsSuppressed(chain, topicKey))
            {
                round.Outcome = "suppressed";
                round.Why = "「" + topicKey + "」已经提过了";
                round.Detail = round.Why;
                _metrics.Inc("idea.suppressed");
                _log.LogInformation("同主题已提过，本轮丢下：{Target} / {Topic}", target, topicKey);
                Finish(round, sw);
                return;
            }

            if (topicKey.Length > 0)
            {
                chain.Topic = topicKey;
                chain.Topics.Add(topicKey);
                if (chain.Topics.Count > 6)
                    chain.Topics = chain.Topics.Skip(chain.Topics.Count - 6).ToList();
            }

            // 加权重
            // 交给 WeightAgent用增量协议把新事项并进对应落点
            if (boost is not null && boost.Items.Count > 0)
            {
                var swWeight = System.Diagnostics.Stopwatch.StartNew();
                var merged = await _weights.MergeAsync(
                    boost.Kind, boost.Target, boost.Who, boost.Topic, boost.Origin, boost.Items.Select(i => i.Text), ct);
                swWeight.Stop();
                round.MsWeight = (int)swWeight.ElapsedMilliseconds;

                round.Weights = merged;

                // 桶够重了拒绝继续加权
                if (merged.Rejected)
                {
                    // 实盘教训：91/93 轮都在给已经 1.00 的桶加权，净效果为零烧掉近 30 万 token
                    round.Boost = merged.Note;
                    round.BoostAmount = merged.Weight;
                    round.Detail = merged.Note;
                    _metrics.Inc("idea.boost.rejected");
                }
                else
                {
                    round.Boost = IdeaWeight.TitleWithTopic(merged.Kind, "", merged.Target, merged.Topic)
                                  + "｜" + string.Join("；", merged.Items);
                    round.BoostAmount = merged.Weight;
                    _metrics.Inc("idea.boost");
                }
            }

            // 思绪写回链，下一轮接着这句往下想
            chain.Thought = round.Thought;
            chain.UpdatedAt = Now();
            _chains.Save(chain);

            // 决策清单：所以我应该干什么
            if (boost is not null && round.Boost.Length > 0)
                round.Decisions.Add(new IdeaDecision
                {
                    Kind = round.Weights?.Rejected == true ? "nothing" : "boost",
                    Text = round.Boost,
                    Reason = round.Weights?.Rejected == true
                        ? "已经够重了，不再加"
                        : (IdeaWeight.TitleOf(boost.Kind, "", boost.Target) is var t && t.Length > 0
                            ? "加权重：" + t
                            : "加权重"),
                });

            if (action is null || action.Text.Trim().Length == 0)
            {
                if (round.Decisions.Count == 0)
                    round.Decisions.Add(new IdeaDecision
                    {
                        Kind = "nothing",
                        Reason = round.Why.Length > 0 ? round.Why : "这一轮不打算做什么",
                    });

                // selfOnly 保留 no_intent 标签，
                // 一眼能看出"想了但没动机"
                round.Outcome = round.Boost.Length > 0
                    ? "weight_only"
                    : (selfOnly ? "no_intent" : "no_idea");
                if (round.Why.Length == 0) round.Why = "这一轮不打算做什么";

                // 够重却没做 → 记一次未兑现。驱动措辞升级，最后承认做不到
                _chains.NoteUnfulfilled();
                Finish(round, sw);
                return;
            }

            round.Candidates = new List<IdeaCandidate> { action };
            round.Picked = 0;

            var chosen = action;
            if (chosen.Target.Length == 0) chosen.Target = target;

            // 模型经常把「发到哪儿」写进 intent 会被白名单判成「意图不在允许列表」拦掉。
            chosen.Intent = NormalizeIntent(chosen.Intent);
            // 收敛只说了「这件事要办」，没说在哪儿办、这个点办合不合适
            // 判下来做不了就先压着：到期再拿出来，那时时间跨度大了，原本做不了的往往就能做了
            if (_env.Idea.Route.Enabled)
            {
                var swRoute = System.Diagnostics.Stopwatch.StartNew();
                var verdict = await _route.DecideAsync(
                    chosen.Text, chosen.Origin, chosen.Target, Slots(), NowText(),
                    PresenceText(target), ct);
                swRoute.Stop();
                round.MsRoute = (int)swRoute.ElapsedMilliseconds;
                round.Route = verdict;
                round.Tokens += verdict.Tokens;

                if (!verdict.Doable || verdict.When != "now")
                {
                    var holdWhy = verdict.Why.Length > 0 ? verdict.Why : "现在不合适，先压着";
                    _chains.Hold(chain, round.Topic, holdWhy, _env.Idea.Route.HoldMinutes);

                    round.Outcome = "held";
                    round.Why = holdWhy;
                    round.Detail = "先不做：" + holdWhy;
                    round.Decisions.Add(new IdeaDecision
                    {
                        Kind = "nothing",
                        Target = chosen.Target,
                        Text = chosen.Text,
                        Reason = holdWhy,
                    });

                    _metrics.Inc("idea.held");
                    _log.LogInformation("这件事先压着（{Until} 分钟后再说）：{Why}",
                        _env.Idea.Route.HoldMinutes, holdWhy);

                    Finish(round, sw);
                    return;
                }

                if (!string.Equals(verdict.Target, chosen.Target, StringComparison.Ordinal))
                {
                    _log.LogInformation("落点改了：{From} → {To}（{Why}）",
                        chosen.Target, verdict.Target, verdict.Why);
                    chosen.Target = verdict.Target;
                }

                chosen.Intent = verdict.Channel == "group" ? "speech" : chosen.Intent;
                if (verdict.Why.Length > 0) chosen.Reason = verdict.Why;

                // 落点必须带前缀。裸号进到发送层会被当成群号，
                // 于是私聊内容按群发出去——这一条是真的漏到过群里
                chosen.Target = NormalizeTarget(chosen.Target, verdict.Channel);
            }

            // 最后一道校验：没有前缀的落点一律不往下走
            if (chosen.Target.Length == 0)
            {
                round.Outcome = "error";
                round.Detail = "落点为空或补不出 private:/group: 前缀";
                _log.LogError("落点不合法，本轮不发送：{Target}", chosen.Target);
                Finish(round, sw);
                return;
            }

            round.Decisions.Add(new IdeaDecision
            {
                Kind = "speak",
                Target = chosen.Target,
                Text = chosen.Text,
                Reason = chosen.Reason,
            });

            // ---- 准入
            var record = _gate.Judge(new IdeaProposal
            {
                Intent = chosen.Intent,
                Target = chosen.Target,
                Text = chosen.Text,
                Reason = chosen.Reason,
            }, "scheduler");

            if (!record.Allowed)
            {
                round.Outcome = "blocked";
                round.BlockedBy = record.BlockedBy;
                round.Detail = record.BlockedDetail;

                // 被闸拦下也是一次「提过了」：占个位，冷却期内别再想它
                // 不占位的话下一轮还会原样再提一遍，闸一直拦、token 一直烧
                MarkResolved(chain, round.Topic, chosen.Target, chosen.Text, delivered: false);

                Finish(round, sw);
                return;
            }

            // 执行
            SetPhase("发送中", chosen.Text);
            var parts = _sender.Segment(chosen.Text);
            var sends = await _sender.SendSegmentsAsync(chosen.Target, parts, null, ct);
            var ok = sends.Count > 0 && sends.All(r => r.Ok);

            if (!ok)
            {
                round.Outcome = "error";
                round.Detail = sends.LastOrDefault()?.Error ?? "发送失败";
                Finish(round, sw);
                return;
            }

            // 说出去的话 + 理由都留痕
            round.Outcome = "sent";
            round.Spoke = chosen.Text;

            // 实际送到哪儿。落点判定可能把 group:X 改成 private:Y，
            round.SentTo = chosen.Target;

            // 消费：真的做了，就把对应落点的加权意向划掉
            // 卡心结的可能性修复
            try
            {
                var (ck, ct2) = SplitTarget(chosen.Target);
                var consumed = _chains.ConsumeByTarget(ck, ct2);

                // 记下这件事做完了，把承载它的思绪清掉
                MarkResolved(chain, round.Topic, chosen.Target, chosen.Text, delivered: true);

                if (consumed > 0)
                {
                    round.Detail = "已消费 " + consumed + " 条加权意向：" + IdeaWeight.TitleOf(ck, "", ct2);
                    _metrics.Inc("idea.weight.consumed");
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug("消费加权意向失败：{Error}", ex.Message);
            }

            for (var i = 0; i < sends.Count && i < parts.Count; i++)
            {
                if (!sends[i].Ok) continue;
                _dedup.Reserve(sends[i].MessageId);
                _chatLog.Append(new LogEntry
                {
                    Ts = Now(),
                    MessageId = sends[i].MessageId,
                    TraceId = "idea_" + round.Id,
                    Scope = chosen.Target,
                    UserId = _env.Server.SelfId,
                    Name = "我",
                    Role = "self",
                    IsSelf = true,
                    Trigger = "idea",
                    Text = parts[i],
                });
            }

            _metrics.Inc("idea.sent");
            _log.LogInformation("思想层发言 → {Target}：{Text}（理由：{Reason}）",
                chosen.Target, chosen.Text, chosen.Reason);

            Finish(round, sw);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            round.Outcome = "error";
            round.Detail = ex.Message;
            Finish(round, sw);
            _log.LogWarning("思想层一轮出错：{Error}", ex.Message);
        }
    }

    private void Finish(IdeaRound round, System.Diagnostics.Stopwatch sw)
    {
        sw.Stop();
        round.Ms = (int)sw.ElapsedMilliseconds;
        _rounds.Enqueue(round);
        while (_rounds.Count > 100) _rounds.TryDequeue(out _);
        _lastRound = round;
        Persist(round);

        // 埋点：这一层以前**完全不写 span**，于是后台神经元统计里「思想」永远是 0 次，
        // 看着像没跑过。实际上它每 5 分钟都在跑，只是结论是"没什么可说的"。
        // 观测缺失和功能失效在报表上一模一样，所以这一条必须补。
        try
        {
            var handle = new SpanHandle
            {
                TraceId = round.Id,
                Neuron = "idea",
                Seq = 0,
                InChars = round.Subject.Length,
                StartTicks = Environment.TickCount64 - round.Ms,
            };

            var ok = round.Outcome is not ("error");
            var brief = string.IsNullOrEmpty(round.Outcome) ? "-" : round.Outcome
                        + (round.Why.Length > 0 ? "：" + round.Why : "");

            _tracer.End(handle, ok, brief, round.Outcome, round.Thought.Length, round.Tokens);
        }
        catch { /* 埋点失败不影响主流程 */ }

        _metrics.Inc("idea.round");
        _metrics.Inc("idea.outcome." + (string.IsNullOrEmpty(round.Outcome) ? "unknown" : round.Outcome));
    }

    private void Persist(IdeaRound round)
    {
        try
        {
            var path = Path.Combine(_env.IdeaDir, "rounds.jsonl");
            var line = JsonSerializer.Serialize(round, AppJson.Options);
            File.AppendAllText(path, line + "\n");
        }
        catch { /* 忽略 */ }
    }


    //坑：每轮喂同一份静态材料，模型每轮都得出同一个结论
    /// <summary>
    /// 给发散层看的材料。**刻意不接对话上下文**——它只读文件与最近日志
    /// </summary>
    private string BuildContext(string target, IdeaChain chain, IdeaInputs inputs)
    {
        var cfg = _env.Idea.Chain;
        CaptureInputs(target, chain, inputs);

        var sb = new StringBuilder();

        foreach (var s in inputs.Self) sb.AppendLine(s);

        sb.AppendLine().AppendLine("【现在】" + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

        if (inputs.Weights.Count > 0)
        {
            sb.AppendLine().AppendLine("【最近更在意的事】");
            foreach (var w in inputs.Weights) sb.Append("- ").AppendLine(w);
        }

        // 每轮都带的碎片锚：防止联想链脱离现实飘成纯文字游戏
        var anchor = new List<string>();
        if (inputs.Recent.Count > 0) anchor.Add("最近说：" + string.Join(" / ", inputs.Recent));
        foreach (var m in inputs.Memories) anchor.Add("记得：" + m);
        foreach (var p in inputs.Profile) anchor.Add(p);

        if (anchor.Count > 0)
        {
            sb.AppendLine().AppendLine("【眼前的一点碎片】")
              .AppendLine(TrimTo(string.Join("\n", anchor.Select(a => "- " + a)),
                                 Math.Max(60, cfg.AnchorChars)));
        }

        sb.AppendLine().AppendLine($"【这是第 {chain.Version + 1} 版想法】");

        // 卡住了就明说。不说的话它会一直把同一件事换个说法重复，而且看起来很像是"在思考"。
        if (chain.Topics.Count > 0)
        {
            var last = chain.Topics[^1];
            var stuck = TrailingRepeat(chain.Topics, last);
            if (stuck >= 2)
            {
                sb.AppendLine().AppendLine($"【你在同一个地方已经卡了 {stuck} 轮】")
                  .AppendLine($"连续 {stuck} 版都落在「{last}」上。这一版不能再说这件事——")
                  .AppendLine("换一个完全不同的角度：换个人、换个时间段、换个动作，或者干脆想点别的。")
                  .AppendLine("如果确实没别的可说，就少写几条，也不要重复上一版。");
            }
        }

        if (inputs.PrevThought.Length > 0)
            sb.AppendLine().AppendLine($"【上一次（第 {chain.Version} 版）的思绪】")
              .AppendLine(inputs.PrevThought);

        if (inputs.PrevThoughts.Count > 0)
        {
            sb.AppendLine().AppendLine("【上一次想到的（节选）】");
            foreach (var t in inputs.PrevThoughts) sb.Append("- ").AppendLine(t);
        }

        // 大块原材料只喂前几版
        if (chain.Version >= Math.Max(1, cfg.ContextRounds))
            return TrimTo(sb.ToString(), 5000);

        var material = new StringBuilder();

        if (target.StartsWith("group:", StringComparison.Ordinal))
        {
            var gid = target["group:".Length..];
            var profile = _store.ReadGroupProfile(gid);
            if (profile.Trim().Length > 0)
                material.AppendLine("【这个群】").AppendLine(profile.Trim());

            var patterns = _store.ReadPatterns(gid).Patterns
                .Where(p => p.Weight > 0).Take(5).ToList();
            if (patterns.Count > 0)
            {
                material.AppendLine("【群里反复出现的话】");
                foreach (var p in patterns)
                    material.Append("- ").Append(p.Text)
                      .Append("（权重 ").Append(p.Weight.ToString("F2")).AppendLine("）");
            }
        }
        else
        {
            var uid = IdeaGate.UserOf(target);
            var person = uid.Length > 0 ? _store.ReadProfile(uid) : "";
            if (person.Trim().Length > 0)
                material.AppendLine("【关于这个人】").AppendLine(person.Trim());

            var pv = uid.Length > 0 ? _presence.Effective(uid) : null;
            if (pv is not null)
                material.AppendLine($"【他在不在】{pv.State}（{pv.Source}：{pv.Note}）");
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var recent = _chatLog.Read(today, target).TakeLast(Math.Max(2, cfg.WindowMessages)).ToList();
        if (recent.Count > 0)
        {
            material.AppendLine("【最近说的话】");
            foreach (var e in recent)
                material.Append(e.IsSelf ? "我：" : (e.Name.Length > 0 ? e.Name : e.UserId) + "：")
                  .AppendLine(e.Text.Length > 100 ? e.Text[..100] : e.Text);
        }

        var materialText = material.ToString();
        if (materialText.Trim().Length > 0)
            sb.AppendLine().AppendLine("【眼前的情况】")
              .AppendLine(TrimTo(materialText, Math.Max(200, cfg.MaxContextChars)));

        return sb.ToString();
    }

    /// <summary>
    /// 节点一：**根据上下文发呆**（用户需求）
    /// </summary>
    private string BuildUserContext(string target, IdeaChain chain, IdeaInputs inputs, SpeakIntent? intent)
    {
        var cfg = _env.Idea.Chain;
        var sb = new StringBuilder();

        foreach (var s in inputs.Self) sb.AppendLine(s);
        sb.AppendLine().AppendLine("【现在】" + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

        if (intent is not null && intent.HasIntent)
        {
            sb.AppendLine().AppendLine("【这一轮我要回应的就是这件事】")
              .AppendLine(intent.About)
              .Append("（动机：").Append(intent.Kind)
              .Append(intent.Why.Length > 0 ? "｜" + intent.Why : "").AppendLine("）");
        }

        sb.AppendLine().AppendLine($"【这是第 {chain.Version + 1} 版想法】");

        var today = DateOnly.FromDateTime(DateTime.Now);
        var recent = _chatLog.Read(today, target).TakeLast(Math.Max(2, cfg.WindowMessages)).ToList();
        if (recent.Count > 0)
        {
            sb.AppendLine().AppendLine("【刚才这里在说什么】（标了是谁说的）");
            foreach (var e in recent)
                sb.Append(e.IsSelf ? "我：" : (e.Name.Length > 0 ? e.Name : e.UserId) + "：")
                  .AppendLine(e.Text.Length > 100 ? e.Text[..100] : e.Text);
        }

        // 大块场景材料只在前几版喂，之后靠链条自己滚
        if (chain.Version < Math.Max(1, cfg.ContextRounds))
        {
            if (target.StartsWith("group:", StringComparison.Ordinal))
            {
                var gid = target["group:".Length..];
                var profile = _store.ReadGroupProfile(gid);
                if (profile.Trim().Length > 0)
                    sb.AppendLine().AppendLine("【这个群】").AppendLine(TrimTo(profile.Trim(), cfg.MaxContextChars));

                var patterns = _store.ReadPatterns(gid).Patterns.Where(p => p.Weight > 0).Take(5).ToList();
                if (patterns.Count > 0)
                {
                    sb.AppendLine().AppendLine("【群里反复出现的话】");
                    foreach (var p in patterns) sb.Append("- ").AppendLine(p.Text);
                }
            }
            else
            {
                var uid = IdeaGate.UserOf(target);
                var pv = uid.Length > 0 ? _presence.Effective(uid) : null;
                if (pv is not null) sb.AppendLine().AppendLine($"【他在不在】{pv.State}（{pv.Source}：{pv.Note}）");
            }
        }

        return TrimTo(sb.ToString(), 5000);
    }

    /// <summary>
    /// 节点二：**根据记忆碎片发呆**（自我意图）
    /// </summary>
    /// <summary>
    /// 把一件做完的事记成"已了结"，并清掉承载它的思绪。
    ///
    /// 清思绪是关键：思想的链条会把上一版思绪喂给下一版，
    /// 于是"我还欠着"这句会自己滚下去，永远停不下来。
    /// 了结之后必须切断这条链，否则同一件事会被反复重做。
    /// </summary>
    private void MarkResolved(IdeaChain chain, string topic, string target, string did, bool delivered)
    {
        var t = (topic ?? "").Trim();
        if (t.Length == 0) return;

        // 同主题只留一条：送达就升格成永久了结，没送达只刷新占位时间
        var hit = chain.Resolved.FirstOrDefault(r => SameTopic(r.Topic, t));
        if (hit is not null)
        {
            hit.Ts = Now();
            hit.Did = did.Length > 80 ? did[..80] : did;
            hit.Target = target;
            hit.Delivered |= delivered;
        }
        else
        {
            chain.Resolved.Insert(0, new ResolvedTopic
            {
                Topic = t.Length > 24 ? t[..24] : t,
                Ts = Now(),
                Target = target,
                Did = did.Length > 80 ? did[..80] : did,
                Delivered = delivered,
            });
        }

        if (chain.Resolved.Count > 20) chain.Resolved = chain.Resolved.Take(20).ToList();

        // 只有送达才切断承载链；没送达只是占位，思绪还要留着等下一次机会
        if (delivered)
        {
            chain.Thought = "";
            chain.Thoughts.Clear();
            chain.Topics.RemoveAll(x => SameTopic(x, t));

            // 写进全局了结表：做完的事**所有落点**都不许再提。
            // 只记在本链上的话，换个地方（群里 → 私聊）又会重新提一遍。
            _chains.AddDone(t, target);

            // 同时进思想储存，带上「做了什么」。
            // 做完就删的话，下一轮没有可长的地方，只能又飘回原来那件事。
            _ideaStore.AddDone(t, did, "", target);
        }

        chain.UpdatedAt = Now();
        _chains.Save(chain);
    }

    private string BuildSelfContext(IdeaChain chain, IdeaInputs inputs)
    {
        var cfg = _env.Idea.Chain;
        var sb = new StringBuilder();

        if (inputs.Weights.Count > 0)
        {
            sb.AppendLine().AppendLine("【我最近更在意的事】");
            foreach (var w in inputs.Weights) sb.Append("- ").AppendLine(w);
        }

        if (inputs.Memories.Count > 0)
        {
            sb.AppendLine().AppendLine("【我整理出来的】");
            foreach (var m in inputs.Memories) sb.Append("- ").AppendLine(m);
        }

        if (inputs.Profile.Count > 0)
        {
            sb.AppendLine().AppendLine("【我大概知道的】");
            foreach (var p in inputs.Profile) sb.Append("- ").AppendLine(p);
        }

        if (chain.Topics.Count > 0)
        {
            var last = chain.Topics[^1];
            var stuck = TrailingRepeat(chain.Topics, last);
            if (stuck >= 2)
            {
                sb.AppendLine().AppendLine($"【你在同一个地方已经卡了 {stuck} 轮】")
                  .AppendLine($"连续 {stuck} 版都落在「{last}」上。这一版不能再说这件事——")
                  .AppendLine("换一个完全不同的角度：换个人、换个时间段、换个动作，或者干脆想点别的。");
            }
        }

        if (inputs.PrevThought.Length > 0)
            sb.AppendLine().AppendLine("【上一版的思绪】").AppendLine(inputs.PrevThought);
        // 做完的事只报个数，不复述内容
        //
        // 列出 topic 和 did 等于每轮重新念一遍，模型读到就会顺着它联想
        // 防重提已经由 resolved 和全局了结表在代码层挡住
        if (chain.Resolved.Count > 0)
            sb.AppendLine().AppendLine($"【已经了结的】{chain.Resolved.Count} 件（都不必再想，也不必再提）");


        if (inputs.PrevThoughts.Count > 0)
        {
            sb.AppendLine().AppendLine("【上一版想到的（节选）】");
            foreach (var t in inputs.PrevThoughts) sb.Append("- ").AppendLine(t);
        }

        return TrimTo(sb.ToString(), Math.Max(1200, cfg.MaxContextChars));
    }

    /// <summary>
    /// 把这一轮看到的东西**分层**采集下来，供后台展示「发散看到什么」
    /// </summary>
    private void CaptureInputs(string target, IdeaChain chain, IdeaInputs inputs)
    {
        var cfg = _env.Idea.Chain;

        // 自我
        //
        // 必须和 ChatPipeline.BuildSystem 取同一套段落
        // 漏掉 boundaries 时主动发言没有边界约束，人格比正常回复明显松
        var view = _persona.Read();
        var segs = Persona.ParseSegments(view.Md);
        foreach (var key in new[] { "identity", "style", "interest", "boundaries" })
            if (segs.TryGetValue(key, out var text) && text.Trim().Length > 0)
                inputs.Self.Add('【' + (key switch
                {
                    "identity" => "我是谁",
                    "style" => "我的说话风格",
                    "interest" => "我的兴趣",
                    _ => "我的边界（优先级最高）",
                }) + "】\n" + text.Trim());

        // 思想权重：一件事一条，别把整桶缝成一句塞进上下文
        //
        // 停放中的桶要跳过：既然判过「现在做不了」，就别再拿它喂发散，
        // 否则下一轮照样读得到、照样想一遍，压着就等于没压。
        var held = _chains.HeldTopics(chain);
        foreach (var b in _chains.TopWeights(5))
        {
            if (held.Count > 0 && held.Any(h => IdeaChainStore.SameTopic(h, b.Topic))) continue;

            var title = b.Title.Length > 0 ? b.Title : IdeaWeight.TitleOf(b.Kind, b.Who, b.Target);
            inputs.Weights.Add($"{title}（{b.Weight:F2}）：{string.Join("；", b.Items.Select(i => i.Text))}");
        }

        try
        {
            // 最近信息
            var today = DateOnly.FromDateTime(DateTime.Now);
            var recent = _chatLog.Read(today, target)
                .Where(e => e.Text.Trim().Length > 0)
                .TakeLast(3)
                .ToList();

            foreach (var e in recent)
                inputs.Recent.Add(
                    (e.IsSelf ? "我" : (e.Name.Length > 0 ? e.Name : e.UserId)) + "：" +
                    (e.Text.Length > 30 ? e.Text[..30] : e.Text));

            // 记忆碎片从思想储存取，不再直接读浅层碎片
            //
            // 碎片是原料，三条常说的是同一件事，拿它发散只能长出那几句
            // 储存里是一件事一条，没被取用过的排在前面，同一条不会连着用
            var seeds = _ideaStore.Top(4);
            foreach (var s in seeds)
            {
                inputs.Memories.Add(s.Thing
                    + (s.Open.Length > 0 ? "（还没做完：" + s.Open + "）" : ""));

                if (s.Grain.Count > 0)
                    inputs.Memories.Add("　原话：" + string.Join(" / ", s.Grain.Take(2)));
            }

            _ideaStore.MarkHit(seeds.Select(s => s.Id));

            // 画像与群体模式
            if (target.StartsWith("group:", StringComparison.Ordinal))
            {
                var gid = target["group:".Length..];

                var pattern = _store.ReadPatterns(gid).Patterns
                    .Where(p => p.Weight > 0).OrderByDescending(p => p.Weight).FirstOrDefault();
                if (pattern is not null) inputs.Profile.Add($"群里常刷：「{pattern.Text}」");

                var gprofile = _store.ReadGroupProfile(gid).Trim();
                if (gprofile.Length > 0) inputs.Profile.Add("这个群：" + FirstSentence(gprofile, 60));
            }
            else
            {
                var uid = IdeaGate.UserOf(target);
                if (uid.Length > 0)
                {
                    var person = _store.ReadProfile(uid).Trim();
                    if (person.Length > 0) inputs.Profile.Add("这个人：" + FirstSentence(person, 60));

                    var pv = _presence.Effective(uid);
                    inputs.Profile.Add($"他现在：{pv.State}");
                }
            }
        }
        catch { /* 抓不到就不带 */ }

        // 上一版
        if (chain.Version > 0)
        {
            inputs.PrevThought = chain.Thought.Trim();
            inputs.PrevThoughts = chain.Thoughts.Take(2).ToList();
        }
    }

    /// <summary>
    /// 浅层记忆里关于这个对象的最近几条碎片
    /// </summary>
    private List<string> RememberedFragments(string target, int max)
    {
        var list = new List<string>();

        try
        {
            var isPrivate = target.StartsWith("private:", StringComparison.Ordinal);
            var uid = IdeaGate.UserOf(target);

            foreach (var e in _store.Read(target))
            {
                if (e.Tombstone) continue;
                if (isPrivate && uid.Length > 0 && e.UserId.Length > 0 && e.UserId != uid) continue;
                if (e.Text.Trim().Length == 0) continue;
                list.Add(e.Text.Length > 50 ? e.Text[..50] : e.Text);
                if (list.Count >= max) break;
            }
        }
        catch { /* 读不到就不带 */ }

        return list;
    }

    /// <summary>
    /// 把模型给的 target 规范化成 private:&lt;QQ&gt; / group:&lt;群号&gt;
    /// </summary>
    private string ResolveTarget(string raw, string fallback)
    {
        var t = (raw ?? "").Trim();
        if (t.Length == 0) return fallback;

        var kind = "";
        if (t.StartsWith("private:", StringComparison.Ordinal)) { kind = "private"; t = t["private:".Length..].Trim(); }
        else if (t.StartsWith("group:", StringComparison.Ordinal)) { kind = "group"; t = t["group:".Length..].Trim(); }

        // 纯数字：够长才算 ID
        if (t.Length > 0 && t.All(char.IsDigit))
        {
            if (t.Length < 5) return fallback;

            if (kind.Length == 0)
                kind = fallback.StartsWith("group:", StringComparison.Ordinal) ? "group" : "private";

            return kind + ":" + t;
        }

        // 非数字：当名字去联系人里找
        var hit = _contacts.Search(t).FirstOrDefault();
        if (hit is not null)
        {
            // 本轮是群、模型给的是人名 → 这个动作其实是「私信这个人」
            return (kind.Length > 0 ? kind : "private") + ":" + hit.UserId;
        }

        return fallback;
    }

    private static string FirstSentence(string text, int max)
    {
        var cut = text.IndexOfAny(new[] { '。', '\n', '；', '，' });
        var head = cut > 0 ? text[..cut] : text;
        if (head.Length > max) head = head[..max];
        return head.Trim();
    }

    private static string TrimTo(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n…（截断）";

    private static string BuildThoughtsText(List<string> thoughts)
    {
        var sb = new StringBuilder("刚才脑子里冒出来的：\n");
        for (var i = 0; i < thoughts.Count; i++)
            sb.Append('[').Append(i).Append("] ").AppendLine(thoughts[i]);
        return sb.ToString();
    }

    /// <summary>
    /// 收敛层的输入：两路节点分开标注，让模型知道哪些来自上下文、哪些来自自己
    /// </summary>
    private static string BuildThoughtsText(List<string> userSide, List<string> selfSide)
    {
        var sb = new StringBuilder();

        if (userSide.Count > 0)
        {
            sb.AppendLine("【从上下文想到的——关于对方要什么】");
            for (var i = 0; i < userSide.Count; i++)
                sb.Append("  U").Append(i).Append(": ").AppendLine(userSide[i]);
        }

        if (selfSide.Count > 0)
        {
            sb.AppendLine().AppendLine("【从记忆碎片想到的——关于我自己在意什么】");
            for (var i = 0; i < selfSide.Count; i++)
                sb.Append("  S").Append(i).Append(": ").AppendLine(selfSide[i]);
        }

        return sb.ToString();
    }


    /// <summary>
    /// 发散节点一：从上下文发呆（用户需求）
    /// </summary>
    private const string DivergeUserPrompt = """
        你在发呆，而且只看眼前这一段对话。

        这一路只想一件事：**对方想让我做什么？有什么话我没接住？**
        比如「记得 XX 点叫我」这种明确的请求，或者「他刚才那句其实是在试探」这种潜台词。

        允许跳跃和联想，但落点必须是眼前的人或事：
        - 对方刚才那句话到底要什么；
        - 有什么话该接却没接；
        - 有没有人提到一件跟我有关、需要我记住的事。

        不要做这些：
        - 不要想你自己在意什么（那一路不归你管）；
        - 不要写计划口吻，不要复述原话；
        - 不要为了凑数硬写。真没东西可写就返回空数组。

        写 0~{N} 条，每条不超过 40 字。只输出 JSON，不要解释、不要代码块：
        {"thoughts":["...","..."]}
        """;

    /// <summary>
    /// 发散节点二：从记忆碎片发呆（自我意图）
    /// </summary>
    private const string DivergeSelfPrompt = """
        你在发呆，而且只看自己记着的东西。

        这一路只想一件事：**我自己在意什么、想做什么？**
        比如「她之前问过我的梦想是什么」这种还没答完的自我提问，
        或者「我最近一直在意她熬夜」这种慢慢积起来的偏向。

        允许跳跃和联想，但落点必须落在你记着的那些碎片上：
        - 哪件事我还在意着；
        - 哪个问题我问过、还没答完；
        - 我对谁的态度正在变。

        不要做这些：
        - 不要回应眼前这段对话（那一路不归你管）；
        - 不要写计划口吻，不要复述原话；
        - 不要为了凑数硬写。真没东西可写就返回空数组。

        写 0~{N} 条，每条不超过 40 字。只输出 JSON，不要解释、不要代码块：
        {"thoughts":["...","..."]}
        """;

    private const string ConvergePrompt = """
        下面是你刚才发呆冒出的两路想法：
        一路是从对方的上下文想到的（关于对方要什么），
        一路是从你自己的记忆碎片想到的（关于你自己在意什么）。

        现在要把它们收敛成**两个东西**：

        **一、答复（reply）**：一句连贯的内在念头，是你此刻沉下来的那句话。
        不是某一条想法的复述，要带自己的立场或倾向，不要写诗，不要写成计划。
        例：「她今晚又熬着，我有点想说但不确定该不该开口。」

        **二、意图（intent）**：所以我要干什么。三档，从轻到重，**大多数时候停在第 1 档**：
        1. none —— 什么都不做，思绪只是思绪；
        2. weight —— 加权重：现在不做，但这件事值得我以后更在意。
           写清落点（是"在群里发言"还是"和谁私聊"）和一句话「我要和谁做什么、为什么」；
        3. act —— 真的做一件事（说一句 / 发条私信 / 戳一下）。
           只有当答复已经成形、而且明显该开口时才选这个。

        输出 JSON，不要解释、不要代码块：
        {"reply":"那句答复，不超过40字",
         "topic":"答复落在哪件具体的事上，不超过8字",
         "why":"不超过20字",
        "intent":{"kind":"none|weight|act",
                  "target":"private:<QQ号> 或 group:<群号>",
                  "origin":"self|context —— 这条是自我意象还是对方给的需求",
                  "about":"这件事是关于谁的？填人名或QQ号，不要填代词「她」",
                  "who":"对方的名字，可空（系统会用QQ号校验覆盖）",
                  "text":"kind=act 时要直接发出去的原话，不超过60字",
                  "items":["kind=weight 时：一件事一条，每条不超过40字"],
                  "reason":"不超过20字"}}
        规则：
        - **topic 必须落在具体的事上，不是抽象感受**。
          「她喊困」「群里没人接话」「他好久没来了」是对的；
          「在意」「孤单」「人际关系」太泛，系统没法靠它判断你是不是在想同一件事；
        - **topic 是给你自己用的归并标签**：同一个念头必须沿用同一个 topic，不要每轮换说法，
          否则系统会判断不出你是不是在原地打转；
        - **origin 必须分清，这是两条完全不同的路**：
          上面那批念头分两组——「从上下文想到的」和「从记忆碎片想到的」。
          ・凡是回应**对方的需求**（他问的、他提的、他需要的）→ origin = "context"；
            这类要**等时机**，现在不是时候就先加权重；
          ・凡是**我自己的意**（我还欠着、我还没想清楚、我想问清楚、我想去做某件事）→ origin = "self"；
            这类**不需要由头**，我自己就能去做，不用等谁来邀请。
          例：「她再喊困时先问是不是想有人陪」= context（要等她喊困）；
              「我答应过的那半截梦想还没答完」= self（我想答，我自己去答）。
        - **intent.about 决定这条意向落在哪**：同一件事必须永远填同一个人，
          系统会用它做权威校验。所以写「星涵」而不是「她」——
          「她」在不同场景指不同的人，落点就会被记错；
        - **intent.items 是一件事一条**：不要把三件事用「；」缝成一句，
          那是过去的老毛病——缝长了会被长度上限截断，语义断在半句上；
        - **每条的 text 要写成下次能照做的动作**，不是愿望。
          反例「下次答这类事别只给干条」；正例「她再喊困时先接一句，别急着劝睡」；
        - 两路想法如果互相矛盾（对方要的 vs 我自己想的），在 why 里点出来；
        - **在群里发言时，text 不要点名去回应某个具体的人**，除非你确定就是那一个人。
          群里好几个人同时在说话时，一条消息只对一件事，别把两个人的话揉成一段——
          揉成一段发出去，看着就像把 A 的事按到 B 头上。宁可只说一句不点名的。
        - text 里不要提「刚才那个人说的」这类指代不清的说法，要指谁就写名字。
        """;

    private static List<string> ParseThoughts(string raw)
    {
        var body = StripFences(raw);
        var start = body.IndexOf('{');
        var stop = body.LastIndexOf('}');

        if (start >= 0 && stop > start)
        {
            try
            {
                using var doc = JsonDocument.Parse(body[start..(stop + 1)]);
                if (doc.RootElement.TryGetProperty("thoughts", out var arr)
                    && arr.ValueKind == JsonValueKind.Array)
                {
                    return arr.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => (x.GetString() ?? "").Trim())
                        .Where(x => x.Length > 0)
                        .Take(6)
                        .ToList();
                }
            }
            catch { /* 落到下面按数组解析 */ }
        }

        // 兜底：模型直接吐了一个字符串数组
        var a = body.IndexOf('[');
        var b = body.LastIndexOf(']');
        if (a < 0 || b <= a) return new List<string>();

        try
        {
            return (JsonSerializer.Deserialize<List<string>>(body[a..(b + 1)], AppJson.Options)
                    ?? new List<string>())
                .Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).Take(6).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private sealed class VerdictAction
    {
        public string Intent { get; set; } = "speech";
        public string Target { get; set; } = "";
        public string Text { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// 解析收敛层的双输出：答复（reply）+ 意图（intent）
    /// </summary>
    private (string reply, string topic, string why, IdeaWeight? weight, IdeaCandidate? action) ParseVerdict(
        string raw, string fallbackTarget)
    {
        var body = StripFences(raw);
        var start = body.IndexOf('{');
        var stop = body.LastIndexOf('}');
        if (start < 0 || stop <= start) return ("", "", "", null, null);

        try
        {
            using var doc = JsonDocument.Parse(body[start..(stop + 1)]);
            var root = doc.RootElement;

            // 答复：新字段叫 reply，老字段 thought 也认（模型偶尔会沿用旧格式）
            var reply = Str(root, "reply");
            if (reply.Length == 0) reply = Str(root, "thought");

            var topic = Str(root, "topic");
            var why = Str(root, "why");

            if (!root.TryGetProperty("intent", out var it) || it.ValueKind != JsonValueKind.Object)
                return (reply, topic, why, null, null);

            var kind = Str(it, "kind").ToLowerInvariant();
            if (kind.Length == 0)
            {
                // 老格式兼容：有 action 就当 act，有 boost 就当 weight
                if (it.TryGetProperty("text", out _)) kind = "act";
                else if (root.TryGetProperty("boost", out _)) kind = "weight";
                else kind = "none";
            }

            var targetRaw = Str(it, "target");
            if (targetRaw.Length == 0) targetRaw = fallbackTarget;

            var resolved = ResolveTarget(targetRaw, fallbackTarget);
            var (tKind, tId) = SplitTarget(resolved);

            // 落点由实体定，不由模型自由发挥
            //
            // 模型给的 about 校验成实体后覆盖落点
            // 否则同一批事会被记到几个不同落点，甚至记到别人名下
            var origin = Str(it, "origin").ToLowerInvariant() == "self" ? "self" : "context";
            var about = Str(it, "about");
            var aboutId = ResolveEntityId(about);

            if (aboutId.Length > 0 && tKind == "private" && tId != aboutId)
            {
                _log.LogInformation("落点按实体纠正：{About} → {Id}（原为 {Old}）", about, aboutId, tId);
                tId = aboutId;
            }

            var who = "";

            // **名字不可信**：只认联系人目录里登记的 QQ→名字。
            // 模型给的 who 经常是代词（"她"）或者记错的号（Liebe 两次被记成不同 QQ）。
            if (tId.Length > 0)
            {
                var c = _contacts.ByQq(tId);
                who = c is not null && c.Name.Length > 0 ? c.Name : "";
            }

            if (kind == "weight")
            {
                // 模型可以给一条 body，也可以给一个 items 数组。
                // **一件事一条**——拼成一句会被长度上限截断，语义断在半句上。
                var items = new List<string>();

                if (it.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    items.AddRange(arr.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => (x.GetString() ?? "").Trim())
                        .Where(x => x.Length > 0)
                        .Select(x => x.Length > 60 ? x[..60] : x));
                }

                if (items.Count == 0)
                {
                    var text = Str(it, "body");
                    if (text.Length == 0) text = Str(it, "text");
                    if (text.Length > 0) items.Add(text.Length > 60 ? text[..60] : text);
                }

                if (items.Count == 0) return (reply, topic, why, null, null);

                return (reply, topic, why, new IdeaWeight
                {
                    Kind = tKind,
                    Target = tId,
                    Who = who,
                    Origin = origin,
                    Topic = (Str(it, "topic") is var tq && tq.Length > 0) ? (tq.Length > 8 ? tq[..8] : tq) : (topic.Length > 8 ? topic[..8] : topic),
                    Weight = 0.2,
                    Items = items.Distinct().Take(4).Select(x => new WeightItem
                    {
                        Id = WeightItem.NewId(),
                        Text = x,
                        Ts = Now(),
                    }).ToList(),
                }, null);
            }

            if (kind == "act")
            {
                var text = Str(it, "text");
                if (text.Length == 0) return (reply, topic, why, null, null);

                var intent = tKind switch
                {
                    "private" => "private",
                    "poke" => "poke",
                    _ => "speech",
                };

                return (reply, topic, why, null, new IdeaCandidate
                {
                    Intent = intent,
                    Origin = origin,
                    Target = resolved,
                    Text = text.Length > 200 ? text[..200] : text,
                    Reason = Str(it, "reason"),
                });
            }

            return (reply, topic, why, null, null);
        }
        catch
        {
            return ("", "", "", null, null);
        }
    }

    /// <summary>
    /// 把"这件事关于谁"解析成实体 id（QQ 号）
    /// </summary>
    private string ResolveEntityId(string about)
    {
        var a = (about ?? "").Trim().Trim('@', '「', '」', '“', '”');
        if (a.Length == 0) return "";

        if (a.All(char.IsDigit) && a.Length >= 5) return a;

        var hit = _contacts.Search(a, 1).FirstOrDefault();
        return hit?.UserId ?? "";
    }

    /// <summary>
    /// 把 private:123 / group:456 拆成 (private|group, 123|456)
    /// </summary>
    private static (string kind, string id) SplitTarget(string target)
    {
        var t = (target ?? "").Trim();
        if (t.StartsWith("private:", StringComparison.Ordinal))
            return ("private", t["private:".Length..]);
        if (t.StartsWith("group:", StringComparison.Ordinal))
            return ("group", t["group:".Length..]);
        return ("group", t);
    }

    private static string Str(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim() : "";

    private static (int? pick, string why) ParsePick(string raw, int count)
    {
        var body = StripFences(raw);
        var start = body.IndexOf('{');
        var stop = body.LastIndexOf('}');
        if (start < 0 || stop <= start) return (null, "");

        try
        {
            using var doc = JsonDocument.Parse(body[start..(stop + 1)]);
            var root = doc.RootElement;

            var why = root.TryGetProperty("why", out var w) && w.ValueKind == JsonValueKind.String
                ? (w.GetString() ?? "") : "";

            if (!root.TryGetProperty("pick", out var p)) return (null, why);
            if (p.ValueKind is JsonValueKind.Null) return (null, why);
            if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out var idx)) return (null, why);
            if (idx < 0 || idx >= count) return (null, why);

            return (idx, why);
        }
        catch
        {
            return (null, "");
        }
    }


    /// <summary>
    /// 这个话题现在是不是该闭嘴了（做过了 / 停放着 / 冷却中）
    /// </summary>
    private bool IsSuppressed(IdeaChain chain, string topic)
    {
        var t = (topic ?? "").Trim();
        if (t.Length == 0) return false;

        // 已经在**任何一个**落点做完了 → 所有落点都闭嘴。
        //
        // 实盘教训：同一件事在群里说了一次、私聊又说了一次。每条链只管自己那份
        // resolved，A 落点做完 B 落点根本不知道，于是换个地方又提一遍。
        if (_chains.DoneBefore(t)) return true;

        // 判过「现在做不了」而停放中 → 先别想它
        if (_chains.IsHeld(chain, t)) return true;

        var now = Now();
        var cd = Math.Max(1, _env.Idea.Weight.ProposalCooldownMinutes) * 60L;

        foreach (var r in chain.Resolved)
        {
            if (!SameTopic(r.Topic, t)) continue;
            if (r.Delivered) return true;
            if (now - r.Ts < cd) return true;
        }

        return false;
    }


    /// <summary>
    /// 把模型写成落点的 intent 折回真正的动作类型
    /// </summary>
    private static string NormalizeIntent(string intent)
    {
        var s = (intent ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return "speech";

        return s switch
        {
            "private" or "私聊" or "group" or "群" or "群里" or "speech_group" => "speech",
            _ => s,
        };
    }

    /// <summary>
    /// 把落点补成 kind:id
    /// </summary>
    /// <param name="target">模型或路由给的落点，可能是裸号</param>
    /// <param name="channel">private / group，用来决定补哪个前缀</param>
    /// <returns>带前缀的落点；补不出来返回空串，调用方必须当作不合法</returns>
    private static string NormalizeTarget(string target, string channel)
    {
        var t = (target ?? "").Trim();
        if (t.Length == 0) return "";

        if (t.StartsWith("private:", StringComparison.Ordinal) || t.StartsWith("group:", StringComparison.Ordinal))
            return t;

        return channel switch
        {
            "private" => "private:" + t,
            "group" => "group:" + t,
            _ => "",
        };
    }


    /// <summary>
    /// 两个主题标签是不是同一件事
    /// </summary>
    private static bool SameTopic(string a, string b) => IdeaChainStore.SameTopic(a, b);

    /// <summary>
    /// 从尾部数：连续多少条和给定主题是同一件事
    /// </summary>
    private static int TrailingRepeat(List<string> topics, string topic)
    {
        var n = 0;
        for (var i = topics.Count - 1; i >= 0; i--)
        {
            if (!SameTopic(topics[i], topic)) break;
            n++;
        }
        return n;
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
}

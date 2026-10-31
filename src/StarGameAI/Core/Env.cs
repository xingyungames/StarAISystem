using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarGameAI.Core;


public sealed class ListenConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 11221;
}

public sealed class NapcatConfig
{
    public string ApiBase { get; set; } = "http://127.0.0.1:3000";
}

public sealed class ReportConfig
{
    public int MaxBodyBytes { get; set; } = 256 * 1024;
}

public sealed class DedupConfig
{
    public int Capacity { get; set; } = 5000;
    public int TtlMinutes { get; set; } = 30;
}

public sealed class RateLimitConfig
{
    public int PerGroupPerSecond { get; set; } = 1;
    public int PerUserPerMinute { get; set; } = 6;
    public int PerUserPerDay { get; set; } = 300;
    public int ReplyMaxChars { get; set; } = 1500;
    public int ReplyMaxSegments { get; set; } = 3;
}

public sealed class WhitelistConfig
{
    public List<string> Groups { get; set; } = new();
    public List<string> Private { get; set; } = new();
    public string UnknownAction { get; set; } = "silent";
}

public sealed class AdminConfig
{
    public bool LoopbackOnly { get; set; } = true;
}

public sealed class ContextConfig
{
    /// <summary>
    /// 拼进上下文的最近消息条数（按会话）
    /// </summary>
    public int MaxHistoryMessages { get; set; } = 24;
    /// <summary>
    /// 历史消息占用的字符预算；超出就只保留最近的若干条
    /// </summary>
    public int MaxContextChars { get; set; } = 4000;
}

public sealed class ServerConfig
{
    public ListenConfig Listen { get; set; } = new();
    public NapcatConfig Napcat { get; set; } = new();
    public ReportConfig Report { get; set; } = new();
    public DedupConfig Dedup { get; set; } = new();
    public RateLimitConfig Ratelimit { get; set; } = new();
    public WhitelistConfig Whitelist { get; set; } = new();
    public ContextConfig Context { get; set; } = new();
    public string SelfId { get; set; } = "";
    public int AckBudgetMs { get; set; } = 2000;
    public AdminConfig Admin { get; set; } = new();
}

public sealed class DeepSeekSecret
{
    public string ApiKey { get; set; } = "";
}

public sealed class TokenSecret
{
    public string Token { get; set; } = "";
}

public sealed class SecretBag
{
    public DeepSeekSecret Deepseek { get; set; } = new();
    public TokenSecret Napcat { get; set; } = new();
    public TokenSecret Report { get; set; } = new();
    public TokenSecret Admin { get; set; } = new();
}

public sealed class LlmProfile
{
    public double Temperature { get; set; } = 0.8;
    public int MaxTokens { get; set; } = 800;
    /// <summary>
    /// enabled / disabled。DeepSeek V4 默认开思考，必须显式关闭
    /// </summary>
    public string Thinking { get; set; } = "disabled";
}

public sealed class ModelConfig
{
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-flash";
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 2;
    public Dictionary<string, LlmProfile> Profiles { get; set; } = new();

    public LlmProfile Profile(string name) =>
        Profiles.TryGetValue(name, out var p) ? p : new LlmProfile();
}

/// <summary>
/// 事件画像命中时的发言门加成
/// </summary>
public sealed class EventBoostConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// weight = 1 的 pattern 能加多少分
    /// </summary>
    public double Scale { get; set; } = 20;
    /// <summary>
    /// 加成上限，避免单向刷分
    /// </summary>
    public int Max { get; set; } = 25;
    /// <summary>
    /// 注入 prompt 时最多列几个活跃 pattern
    /// </summary>
    public int InjectTopN { get; set; } = 5;
}

/// <summary>
/// 单个群的覆盖配置；null 表示沿用全局
/// </summary>
public sealed class GroupOverride
{
    public bool? Enabled { get; set; }
    public int? AmbientThreshold { get; set; }
    public int? DailyBudget { get; set; }
    public int? CooldownSeconds { get; set; }
}

public sealed class GateConfig
{
    public bool Enabled { get; set; } = true;
    public int DirectThreshold { get; set; } = 45;
    public int AmbientThreshold { get; set; } = 60;
    public int CooldownSeconds { get; set; } = 90;
    public int DailyBudget { get; set; } = 30;
    public double SampleRate { get; set; } = 0.4;
    public int MaxLlmCallsPerDay { get; set; } = 200;
    public List<string> NoiseWords { get; set; } = new();
    public List<string> DirectHints { get; set; } = new();

    /// <summary>
    /// 我自己在群里被叫的名字（不含 @，@ 由 at 段单独判定）
    /// </summary>
    public List<string> BotNames { get; set; } = new();

    //坑：问句词表命中就硬答时，群里任何一句带「吗」的话都会触发插嘴
    /// <summary>
    /// 问句线索（吗 / 呢 / ？/ 帮我…）
    /// </summary>
    public int HintBonus { get; set; } = 15;

    /// <summary>
    /// 判定调用失败时要不要硬着头皮说。默认 false（静默）
    /// </summary>
    public bool FailOpenOnError { get; set; }

    public EventBoostConfig EventBoost { get; set; } = new();
    public Dictionary<string, GroupOverride> Groups { get; set; } = new();
}

/// <summary>
/// 风格样本库的准入参数（卡尔曼式的"不确定度驱动信任分配"，见 Core/StyleSamples.cs）
/// </summary>
public sealed class StyleConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 覆盖度的目标样本数。到齐了才算"数据够了"
    /// </summary>
    public int TargetSamples { get; set; } = 8;

    /// <summary>
    /// 不确定度高于这个值 → manual（模型候选一律挂起等人工）
    /// </summary>
    public double ManualAbove { get; set; } = 0.65;

    /// <summary>
    /// 不确定度低于这个值 → adaptive（模型自评够高就自动收）
    /// </summary>
    public double AdaptiveBelow { get; set; } = 0.35;

    /// <summary>
    /// assisted 模式下模型自评的最低分
    /// </summary>
    public int AssistedMinScore { get; set; } = 70;

    /// <summary>
    /// adaptive 模式下模型自评的最低分
    /// </summary>
    public int AdaptiveMinScore { get; set; } = 55;

    /// <summary>
    /// 模型自评样本的权重上限（人工恒为 1.0）
    /// </summary>
    public double ModelWeight { get; set; } = 0.4;

    /// <summary>
    /// 注入几条样本给回复层参照
    /// </summary>
    public int InjectTopN { get; set; } = 4;
}

/// <summary>
/// 对话分块（Block）的路由参数
/// </summary>
public sealed class DialogueConfig
{
    /// <summary>
    /// off = 不用；observe = 只观测不决策；decide = 真正影响上下文
    /// </summary>
    public string Mode { get; set; } = "observe";

    /// <summary>
    /// 模糊时是否调模型判定归属
    /// </summary>
    public bool UseModel { get; set; } = true;

    /// <summary>
    /// 置信度低于此值 → 判不了（unresolved）
    /// </summary>
    public double MinConfidence { get; set; } = 0.60;

    /// <summary>
    /// 距该块上次活动超过这么多秒，就不倾向延续它
    /// </summary>
    public int GapSecondsNewBlock { get; set; } = 180;

    /// <summary>
    /// 同时保持活跃的块上限（超出的转冷）
    /// </summary>
    public int MaxHotBlocks { get; set; } = 6;

    public double WReplySameBlock { get; set; } = 0.40;
    public double WAtSameEntity { get; set; } = 0.50;
    public double WMentionPronoun { get; set; } = 0.30;
    public double WEntityOverlap { get; set; } = 0.25;
    public double WTimeProximity { get; set; } = 0.20;
    /// <summary>
    /// 同一个人接着说 —— 现场最强的信号，成本为零
    /// </summary>
    public double WSameSender { get; set; } = 0.45;

    /// <summary>
    /// 块多久没新消息转冷（秒）
    /// </summary>
    public int HotIdleSeconds { get; set; } = 600;

    /// <summary>
    /// 未归属消息保留多久（供事后回填）
    /// </summary>
    public int UnresolvedKeepHours { get; set; } = 24;

    /// <summary>
    /// 一个 scope 最多保留多少个块
    /// </summary>
    public int MaxBlocksPerScope { get; set; } = 60;
}

/// <summary>
/// 加权意向的生命周期参数
/// </summary>
public sealed class WeightConfig
{
    /// <summary>
    /// 到这条线就"够重了"：不再接受加权，转为意图判定的动机来源
    /// </summary>
    public double ActThreshold { get; set; } = 0.95;

    /// <summary>
    /// 衰减半衰期（天）。没人管、没被兑现的意向按这个速度变淡
    /// </summary>
    public double HalfLifeDays { get; set; } = 3.0;

    /// <summary>
    /// 掉到这个值以下就完全遗忘（整条移出列表）
    /// </summary>
    public double ForgetBelow { get; set; } = 0.08;

    /// <summary>
    /// 最多保留多少个桶
    /// </summary>
    public int MaxBuckets { get; set; } = 24;

    /// <summary>
    /// 一个桶里最多几条事项（一件事一条，防止又缝成流水句）
    /// </summary>
    public int MaxItems { get; set; } = 6;

    /// <summary>
    /// 同一主题提案后的占位冷却（分钟）。
    ///
    /// 提案一旦生成就占位，冷却期内不再重复拟同一件事。
    /// 为什么需要：实测「梦想」那一件事在 14 小时里生了 26 条提案，
    /// 其中 18 条被静默时段/冷却/白名单拦下——拦下不等于了结，
    /// 于是每 5 分钟换个措辞再拟一遍。
    /// </summary>
    public int ProposalCooldownMinutes { get; set; } = 60;

    /// <summary>
    /// 够重却一直没兑现多少轮之后，承认做不到、衰减加速
    /// </summary>
    public int StaleRounds { get; set; } = 14;

    /// <summary>
    /// 措辞硬化的阶梯（轮数）。到哪一档就用哪一档的说法
    /// </summary>
    public int EscalateRound1 { get; set; } = 2;
    public int EscalateRound2 { get; set; } = 5;
    public int EscalateRound3 { get; set; } = 9;
}

public sealed class SleepWindow{
    public string Start { get; set; } = "02:30";
    public string End { get; set; } = "05:30";
}

public sealed class SleepInterval
{
    public int MinHoursBetween { get; set; } = 8;
    public int MaxHoursBetween { get; set; } = 24;
}

public sealed class SleepChunk
{
    public int MaxEntries { get; set; } = 200;
    public int MaxChars { get; set; } = 24000;
    public int OverlapEntries { get; set; } = 20;
    public int MaxChunksPerRun { get; set; } = 40;
}

public sealed class SleepConfig
{
    public bool Enabled { get; set; } = true;
    public bool AiEditable { get; set; } = true;
    public List<SleepWindow> Windows { get; set; } = new();
    public SleepInterval Interval { get; set; } = new();
    public int IdleRequiredSeconds { get; set; } = 180;
    public bool DeferIfBusy { get; set; } = true;
    public int LogThresholdEntries { get; set; } = 800;
    /// <summary>
    /// silent / fast_reply / queue
    /// </summary>
    public string BehaviorWhileSleeping { get; set; } = "silent";
    public SleepChunk Chunk { get; set; } = new();
}

public sealed class PersonaPolicy
{
    /// <summary>
    /// 每天允许 AI 自己改写自我描述的段数上限。0 表示不允许 AI 写（只能人来改）
    /// </summary>
    public int RevisionBudgetPerDay { get; set; } = 1;
    /// <summary>
    /// 同一段两次改动之间的最小间隔（分钟），防止一次睡眠里连改多次
    /// </summary>
    public int MinMinutesBetweenRevisions { get; set; } = 0;
}

/// <summary>
/// 两层记忆的保留策略
/// </summary>
public sealed class MemoryPolicy
{
    /// <summary>
    /// 浅层碎片被吸收后保留多少天可用于溯源，之后进 archive
    /// </summary>
    public int ShallowRetentionDays { get; set; } = 7;

    /// <summary>
    /// 人物画像的时间半衰期（天）。人的身份特征变化很慢
    /// </summary>
    public double PersonHalfLifeDays { get; set; } = 90;

    /// <summary>
    /// 群体 pattern 的时间半衰期（小时）。梗会很快过气
    /// </summary>
    public double GroupHalfLifeHours { get; set; } = 72;

    /// <summary>
    /// 人物画像正文的最大行数，超了逼模型压缩
    /// </summary>
    public int PersonProfileMaxLines { get; set; } = 40;

    /// <summary>
    /// 事件画像正文的最大行数
    /// </summary>
    public int GroupProfileMaxLines { get; set; } = 60;

    /// <summary>
    /// 一个 pattern 至少出现几次才值得记（1 次是偶然，2 次开始像模式）
    /// </summary>
    public int GroupPatternMinCount { get; set; } = 2;

    /// <summary>
    /// pattern 权重低于多少就清掉
    /// </summary>
    public double GroupPatternMinWeight { get; set; } = 0.05;

    /// <summary>
    /// pattern 多少天没再出现就彻底忘掉
    /// </summary>
    public int GroupPatternExpireDays { get; set; } = 7;

    /// <summary>
    /// 一次巩固里最多更新几个对象（人 + 群），控制成本
    /// </summary>
    public int MaxObjectsPerSleep { get; set; } = 12;

    /// <summary>
    /// 一个用户在本片里至少发言几条才值得更新画像
    /// </summary>
    public int MinTurnsToUpdatePerson { get; set; } = 2;
}

/// <summary>
/// 时间窗，例如 10:00 - 20:00
/// </summary>
public sealed class TimeWindow
{
    public string Start { get; set; } = "10:00";
    public string End { get; set; } = "20:00";

    private static bool TryParse(string s, out TimeSpan t) => TimeSpan.TryParse(s, out t);

    /// <summary>
    /// 当前时间是否落在窗口内；支持跨午夜（start &gt; end）
    /// </summary>
    public bool Contains(DateTime now)
    {
        if (!TryParse(Start, out var a) || !TryParse(End, out var b)) return true;

        var t = now.TimeOfDay;
        return a <= b ? t >= a && t <= b : t >= a || t <= b;
    }
}

/// <summary>
/// 一次回复拆成多条消息的节奏控制（也可以用来做"追加"）
/// </summary>
public sealed class BurstConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// 一次连贯发言最多几条（含第一条）。硬上限，防死循环
    /// </summary>
    public int MaxMessages { get; set; } = 10;
    /// <summary>
    /// 继续追加的初始概率
    /// </summary>
    public double BaseProbability { get; set; } = 0.7;
    /// <summary>
    /// 每追加一条，下一次追加概率乘这个系数
    /// </summary>
    public double Decay { get; set; } = 0.6;
    /// <summary>
    /// 两条消息之间的间隔毫秒
    /// </summary>
    public int SegmentDelayMs { get; set; } = 450;
}

/// <summary>
/// 发散链：每一轮发散都建立在上一轮的想法上，产出 v1 → v2 → v3…
/// </summary>
public sealed class ChainConfig
{
    /// <summary>
    /// 一轮发散最多产出几条想法
    /// </summary>
    public int MaxThoughts { get; set; } = 4;
    /// <summary>
    /// 保留最近几版想法用于回看
    /// </summary>
    public int HistoryKeep { get; set; } = 5;
    /// <summary>
    /// 喂给发散的原始对话窗口（条）
    /// </summary>
    public int WindowMessages { get; set; } = 12;
    /// <summary>
    /// 前几版还带原始上下文；之后的版本只用上一版想法
    /// </summary>
    public int ContextRounds { get; set; } = 2;
    /// <summary>
    /// 原始上下文的最大字符数
    /// </summary>
    public int MaxContextChars { get; set; } = 1200;

    //坑：只喂链条不喂碎片，滚到第 20 版会飘成纯文字游戏
    /// <summary>
    /// 每轮都带的一小撮「记忆碎片」的字数上限
    /// </summary>
    public int AnchorChars { get; set; } = 220;

    /// <summary>
    /// 链最多滚多少版；超过就重置，重新吃一次完整材料
    /// </summary>
    public int MaxVersions { get; set; } = 8;
}

/// <summary>
/// 意图子 AI 的开关
/// </summary>
public sealed class IntentConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// 看最近多少条对话
    /// </summary>
    public int WindowMessages { get; set; } = 14;
    /// <summary>
    /// 低于这个置信度就当没有动机
    /// </summary>
    public int MinConfidence { get; set; } = 45;
    /// <summary>
    /// 没有动机时，同一个对象至少隔多久再看一次（分钟），避免空转
    /// </summary>
    public int RecheckMinutes { get; set; } = 20;
}

/// <summary>
/// 发散/收敛这一层的开关与预算（这一层要花模型调用，必须限额）
/// </summary>
public sealed class ThinkConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 只有最近这么多分钟内有对话活动，才值得发散
    /// </summary>
    public int ActiveWithinMinutes { get; set; } = 60;

    /// <summary>
    /// 静默时段整轮不跑，省算力。手动「立刻想一次」不受影响
    /// </summary>
    public bool SkipQuietHours { get; set; } = true;

    public int MaxRoundsPerHour { get; set; } = 6;
    public int MaxRoundsPerDay { get; set; } = 40;
    /// <summary>
    /// 一轮最多看几个对象
    /// </summary>
    public int MaxTargetsPerRound { get; set; } = 2;
    /// <summary>
    /// 发散最多产生几条念头
    /// </summary>
    public int MaxCandidates { get; set; } = 3;
}

/// <summary>
/// 思想层与在线状态的配置
/// </summary>
public sealed class IdeaConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 发散频率（分钟）
    /// </summary>
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>
    /// 允许的意图。第一版只放行「在群里说话」，私信/戳人默认关掉
    /// </summary>
    public List<string> AllowedIntents { get; set; } = new() { "speech" };

    /// <summary>
    /// 同一个对象多少小时内不重复打扰
    /// </summary>
    public int PerTargetCooldownHours { get; set; } = 6;
    public int PerHourLimit { get; set; } = 2;
    public int DailyLimit { get; set; } = 8;

    /// <summary>
    /// 绝对静默时段，任何自发行为都不许
    /// </summary>
    public TimeWindow QuietHours { get; set; } = new() { Start = "01:00", End = "08:00" };

    /// <summary>
    /// 在线状态判不出来时，落在这个时段才允许打扰
    /// </summary>
    public TimeWindow AllowWindow { get; set; } = new() { Start = "10:00", End = "20:00" };

    /// <summary>
    /// 最近多少分钟内说过话就算在线
    /// </summary>
    public int ActivityOnlineMinutes { get; set; } = 15;

    /// <summary>
    /// 超过多少小时没说话，活动线索引失效
    /// </summary>
    public int ActivityUnknownHours { get; set; } = 6;

    /// <summary>
    /// 用户自报「要睡了」之后，多久自动失效
    /// </summary>
    public int SelfReportSleepHours { get; set; } = 8;

    /// <summary>
    /// 用户自报「在上课 / 在忙」之后，多久自动失效
    /// </summary>
    public int SelfReportBusyHours { get; set; } = 2;

    public BurstConfig Burst { get; set; } = new();
    public ThinkConfig Think { get; set; } = new();
    public IntentConfig Intent { get; set; } = new();
    public WeightConfig Weight { get; set; } = new();
    public ChainConfig Chain { get; set; } = new();
    public RouteConfig Route { get; set; } = new();
}

/// <summary>
/// 动手之前的「怎么发、什么时候发」
/// </summary>
public sealed class RouteConfig
{
    /// <summary>
    /// 是否先判一次落点与时机。关掉就照收敛给的目标直接发
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 判定「现在做不了」之后停放多久（分钟）。
    ///
    /// 停放期间这件事**不进下一轮的任何输入**，所以不会被反复想起、反复重拟。
    /// 到期之后重新拿出来——那时时间跨度大了，原本做不了的事往往就能做了。
    /// </summary>
    public int HoldMinutes { get; set; } = 180;
}

public sealed class ToolsPolicy
{
    public bool AutoApprove { get; set; } = true;
    public int TimeoutDefaultS { get; set; } = 5;
    public int StdoutMaxBytes { get; set; } = 64 * 1024;
    public int GlobalConcurrency { get; set; } = 4;
    public string Python { get; set; } = "python";
    public List<string> EnvWhitelist { get; set; } = new();
    public List<string> ForbidImports { get; set; } = new();
    public List<string> ForbidBuiltins { get; set; } = new();
    public List<string> ForbidAttrs { get; set; } = new();
}

public sealed class PersonaSegment
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Editable { get; set; }
    public string Hash { get; set; } = "";
    public string Text { get; set; } = "";
}


public static class AppJson
{
    /// <summary>
    /// 入站 JSON：保持中文原样（日志与记忆文件要人可读）
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 出站到 OpenAI 兼容协议：snake_case
    /// </summary>
    public static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 配置文件与后台 API：磁盘上和协议上一律 snake_case
    /// </summary>
    public static readonly JsonSerializerOptions Config = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static string ToLine(object value) => JsonSerializer.Serialize(value, Options);
}


/// <summary>
/// 一次性加载全部配置，并解析数据目录。进程内只读
/// </summary>
public sealed class Env
{
    public string Root { get; private init; } = "";
    public ServerConfig Server { get; set; } = new();
    public SecretBag Secrets { get; private init; } = new();
    public ModelConfig Model { get; private init; } = new();
    /// <summary>
    /// 发言门配置：后台「主动说话」页可实时改写
    /// </summary>
    public GateConfig Gate { get; set; } = new();
    /// <summary>
    /// 睡眠配置可被后台实时改写，因此不是 init-only
    /// </summary>
    public SleepConfig Sleep { get; set; } = new();
    /// <summary>
    /// 人设策略（修订上限等）可在后台实时改写
    /// </summary>
    public PersonaPolicy Persona { get; set; } = new();
    /// <summary>
    /// 两层记忆的保留策略，可在后台实时改写
    /// </summary>
    public MemoryPolicy Memory { get; set; } = new();
    /// <summary>
    /// 思想层与在线状态配置，后台可实时改写
    /// </summary>
    public IdeaConfig Idea { get; set; } = new();
    /// <summary>
    /// 风格样本库的准入参数，后台可实时改写
    /// </summary>
    public StyleConfig Style { get; set; } = new();
    /// <summary>
    /// 对话分块的路由参数，后台可实时改写
    /// </summary>
    public DialogueConfig Dialogue { get; set; } = new();
    public ToolsPolicy ToolPolicy { get; private init; } = new();
    /// <summary>
    /// 神经元埋点保留天数
    /// </summary>
    public int SpanRetentionDays { get; private init; } = 7;

    /// <summary>
    /// 是否还没配置完。空白 AI 的判据：没有模型 Key、或者不知道自己的 QQ
    /// </summary>
    public bool NeedsSetup =>
        string.IsNullOrWhiteSpace(Secrets.Deepseek.ApiKey)
        || string.IsNullOrWhiteSpace(Server.SelfId);

    public string ConfigDir => Path.Combine(Root, "config");
    public string DataDir => Path.Combine(Root, "data");
    public string LogDir => Path.Combine(DataDir, "log");
    public string MemoryDir => Path.Combine(DataDir, "memory");
    /// <summary>
    /// 浅层：对话抽出的碎片，会被吸收、会过期
    /// </summary>
    public string ShallowDir => Path.Combine(MemoryDir, "shallow");
    /// <summary>
    /// 深层：人物画像（面向人，跨会话）
    /// </summary>
    public string DeepPersonDir => Path.Combine(MemoryDir, "deep", "person");
    /// <summary>
    /// 深层：事件画像（面向群，会过期）
    /// </summary>
    public string DeepGroupDir => Path.Combine(MemoryDir, "deep", "group");
    /// <summary>
    /// 已吸收且过期的浅层碎片
    /// </summary>
    public string ArchiveDir => Path.Combine(MemoryDir, "archive");
    public string PersonaDir => Path.Combine(DataDir, "persona");
    public string ToolsDir => Path.Combine(DataDir, "tools");
    public string SleepDir => Path.Combine(DataDir, "sleep");
    public string WorkspaceDir => Path.Combine(DataDir, "workspace");
    /// <summary>
    /// AI 的沙箱根目录：内置的文件读写工具只能在这里活动
    /// </summary>
    public string SandboxDir => Path.Combine(DataDir, "sandbox");
    /// <summary>
    /// 想法账本目录
    /// </summary>
    public string IdeaDir => Path.Combine(DataDir, "idea");
    /// <summary>
    /// 在线状态表
    /// </summary>
    public string PresenceFile => Path.Combine(DataDir, "presence.json");
    public string AuditDir => Path.Combine(DataDir, "audit");
    public string PersonaFile => Path.Combine(PersonaDir, "System Prompt.md");

    public static Env Load()
    {
        var root = ResolveRoot();
        var env = new Env
        {
            Root = root,
            Server = ReadJson<ServerConfig>(Path.Combine(root, "config", "server.json")) ?? new ServerConfig(),
            Secrets = EnsureSecrets(root),
            Model = ReadJson<ModelConfig>(Path.Combine(root, "config", "model.json")) ?? new ModelConfig(),
            Gate = ReadJson<GateConfig>(Path.Combine(root, "config", "gate.json")) ?? new GateConfig(),
            Sleep = ReadJson<SleepConfig>(Path.Combine(root, "config", "sleep.json")) ?? new SleepConfig(),
            Persona = ReadJson<PersonaPolicy>(Path.Combine(root, "config", "persona.json")) ?? new PersonaPolicy(),
            Memory = ReadJson<MemoryPolicy>(Path.Combine(root, "config", "memory.json")) ?? new MemoryPolicy(),
            Idea = ReadJson<IdeaConfig>(Path.Combine(root, "config", "idea.json")) ?? new IdeaConfig(),
            Style = ReadJson<StyleConfig>(Path.Combine(root, "config", "style.json")) ?? new StyleConfig(),
            Dialogue = ReadJson<DialogueConfig>(Path.Combine(root, "config", "dialogue.json")) ?? new DialogueConfig(),
            ToolPolicy = ReadJson<ToolsPolicy>(Path.Combine(root, "config", "tools.policy.json")) ?? new ToolsPolicy(),
        };

        foreach (var dir in new[]
                 {
                     env.LogDir, env.MemoryDir, env.ShallowDir, env.DeepPersonDir, env.DeepGroupDir,
                     env.ArchiveDir, env.PersonaDir, env.ToolsDir, env.SleepDir,
                     env.WorkspaceDir, env.SandboxDir, env.IdeaDir, env.AuditDir,
                     Path.Combine(env.PersonaDir, "meta"),
                     Path.Combine(env.PersonaDir, "history"),
                 })
        {
            Directory.CreateDirectory(dir);
        }

        return env;
    }

    /// <summary>
    /// 读凭据；缺了就**自动生成**那两个本地 token 并落盘
    /// </summary>
    private static SecretBag EnsureSecrets(string root)
    {
        var path = Path.Combine(root, "config", "secrets.json");
        var bag = ReadJson<SecretBag>(path) ?? new SecretBag();
        var changed = false;

        if (string.IsNullOrWhiteSpace(bag.Admin.Token))
        {
            bag.Admin.Token = "adm-" + RandomToken(28);
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(bag.Report.Token))
        {
            bag.Report.Token = "rep-" + RandomToken(20);
            changed = true;
        }

        if (changed)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                WriteAtomic(path, JsonSerializer.Serialize(bag, AppJson.Config));
            }
            catch { /* 写不了也不影响运行，只是下次还会再生成 */ }
        }

        return bag;
    }

    private static string RandomToken(int bytes)
    {
        var buf = new byte[bytes];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        return Convert.ToBase64String(buf)
            .Replace("/", "_").Replace("+", "-").Replace("=", "");
    }

    //坑：机器上存在无关的 C:\config 时，空白部署会把 C:\ 当成项目根
    /// <summary>
    /// 从程序目录向上找含 config 的根；找不到退回当前工作目录
    /// </summary>
    private static string ResolveRoot()
    {
        var forced = Environment.GetEnvironmentVariable("STARGAMEAI_ROOT");
        if (!string.IsNullOrWhiteSpace(forced) && IsUsableRoot(forced.Trim()))
            return forced.Trim();

        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && probe is not null; i++, probe = probe.Parent)
        {
            if (!IsUsableRoot(probe.FullName)) continue;
            if (LooksLikeProjectRoot(probe.FullName)) return probe.FullName;
        }

        var baseDir = TrimEnd(AppContext.BaseDirectory);
        if (IsUsableRoot(baseDir)) return baseDir;

        var cwd = TrimEnd(Directory.GetCurrentDirectory());
        if (IsUsableRoot(cwd)) return cwd;

        return string.IsNullOrWhiteSpace(baseDir) ? "." : baseDir;
    }

    private static string TrimEnd(string dir) =>
        (dir ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// 能不能当根目录：非空、且不是盘根（"C:\" 不能当根，否则会去建 C:\data）
    /// </summary>
    private static bool IsUsableRoot(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        return !string.Equals(Path.GetPathRoot(dir), dir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 像不像项目根：有 config 目录，并且还有至少一个别的结构标记
    /// </summary>
    private static bool LooksLikeProjectRoot(string dir)
    {
        if (!Directory.Exists(Path.Combine(dir, "config"))) return false;

        return Directory.Exists(Path.Combine(dir, "docs"))
               || Directory.Exists(Path.Combine(dir, "src"))
               || Directory.Exists(Path.Combine(dir, "wwwroot"));
    }

    public static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(text, AppJson.Config);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 原子写：先写临时文件再替换，避免半截文件
    /// </summary>
    public static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}

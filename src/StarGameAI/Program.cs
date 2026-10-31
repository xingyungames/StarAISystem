using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StarGameAI.Admin;
using StarGameAI.Core;
using StarGameAI.Gateway;
using StarGameAI.Llm;
using StarGameAI.Memory;
using StarGameAI.Pipeline;
using StarGameAI.Sleep;
using StarGameAI.Tools;

var env = Env.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

// 端口允许用环境变量覆盖，便于旁路起一个只做巩固/调试的实例
var bindPort = env.Server.Listen.Port;
if (int.TryParse(Environment.GetEnvironmentVariable("STARGAMEAI_PORT"), out var envPort) && envPort > 0)
    bindPort = envPort;

var bind = $"http://{env.Server.Listen.Host}:{bindPort}";
builder.WebHost.UseUrls(bind);

builder.Services.AddSingleton(env);
builder.Services.AddSingleton<Metrics>();
builder.Services.AddSingleton<Activity>();
builder.Services.AddSingleton<Tracer>();

builder.Services.AddSingleton(sp => new DeepSeekClient(
    env.Model,
    env.Secrets.Deepseek.ApiKey,
    sp.GetRequiredService<ILogger<DeepSeekClient>>()));

builder.Services.AddSingleton(_ => new Dedup(
    env.Server.Dedup.Capacity,
    TimeSpan.FromMinutes(Math.Max(1, env.Server.Dedup.TtlMinutes))));

builder.Services.AddSingleton<ChatLog>();
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<DeltaLog>();
builder.Services.AddSingleton<Sandbox>();
builder.Services.AddSingleton<Contacts>();
builder.Services.AddSingleton<Identity>();
builder.Services.AddSingleton<Presence>();
builder.Services.AddSingleton<IdeaGate>();
builder.Services.AddSingleton<IdeaChainStore>();
builder.Services.AddSingleton<IdeaStore>();
builder.Services.AddSingleton<IntentAgent>();
builder.Services.AddSingleton<WeightAgent>();
builder.Services.AddSingleton<RouteAgent>();
builder.Services.AddSingleton<StyleStore>();
builder.Services.AddSingleton<DialogueStore>();
builder.Services.AddSingleton<DialogueRouter>();
builder.Services.AddSingleton<IdeaEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IdeaEngine>());
builder.Services.AddSingleton<BuiltinTools>();
builder.Services.AddSingleton<Persona>();
builder.Services.AddSingleton<Sender>();
builder.Services.AddSingleton<Gate>();
builder.Services.AddSingleton<Tooling>();
builder.Services.AddSingleton<SleepState>();
builder.Services.AddSingleton<Consolidator>();
builder.Services.AddSingleton<SleepRunner>();
builder.Services.AddSingleton<ChatPipeline>();
builder.Services.AddSingleton<Receiver>();
builder.Services.AddHostedService<InboundWorker>();
builder.Services.AddHostedService<SleepScheduler>();
builder.Services.AddSingleton<NapcatHealth>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NapcatHealth>());

builder.Services.ConfigureHttpJsonOptions(o =>
{
    // 后台 API 与前端契约统一用 snake_case（与 docs 里的一致）
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

var app = builder.Build();


try
{
    var migrated = app.Services.GetRequiredService<Store>().MigrateLegacyEntries();
    if (migrated > 0)
        Console.WriteLine($"[启动] 已把 {migrated} 个旧 entries 文件迁到 memory/shallow/");

    // 加权意向的老条目清账：没有落点的遗留物既不累加也不会被执行，只会占上下文
    var legacyWeights = app.Services.GetRequiredService<IdeaChainStore>().CleanupLegacy();
    if (legacyWeights > 0)
        Console.WriteLine($"[启动] 已合并/清理 {legacyWeights} 条没有落点的历史加权意向");

    // 同一件事的不同措辞并成一个桶：四个「梦想」各占一个桶，发散就会每次都读到四遍
    var topicMerged = app.Services.GetRequiredService<IdeaChainStore>().MergeTopics();
    if (topicMerged > 0)
        Console.WriteLine($"[启动] 已归并 {topicMerged} 条同义的加权意向");

    // 用联系人目录校正名字：模型给的名字不可信（出现过代词、也出现过张冠李戴）
    var contactsRef = app.Services.GetRequiredService<Contacts>();
    var whoFixed = app.Services.GetRequiredService<IdeaChainStore>()
        .FixWho(uid => contactsRef.ByQq(uid)?.Name ?? "");
    if (whoFixed > 0)
        Console.WriteLine($"[启动] 已按联系人目录校正 {whoFixed} 条加权意向的名字");

    var tracer = app.Services.GetRequiredService<Tracer>();
    var pruned = tracer.Prune(env.SpanRetentionDays);
    if (pruned > 0) Console.WriteLine($"[启动] 清理了 {pruned} 个过期的埋点文件");
}
catch (Exception ex)
{
    Console.WriteLine("[启动] 记忆目录迁移失败：" + ex.Message);
}


var startupIssues = new List<string>();

if (string.IsNullOrWhiteSpace(env.Secrets.Deepseek.ApiKey))
    startupIssues.Add("config/secrets.json 缺少 deepseek.api_key，模型调用会失败");
if (string.IsNullOrWhiteSpace(env.Secrets.Napcat.Token))
    startupIssues.Add("config/secrets.json 缺少 napcat.token，发消息会被 NapCat 拒绝（403 token verify failed）");
if (string.IsNullOrWhiteSpace(env.Secrets.Report.Token))
    startupIssues.Add("config/secrets.json 缺少 report.token，上报将不做鉴权（不建议）");
if (string.IsNullOrWhiteSpace(env.Secrets.Admin.Token))
    startupIssues.Add("config/secrets.json 缺少 admin.token，后台将不做鉴权（不建议）");
if (string.IsNullOrWhiteSpace(env.Server.SelfId))
    startupIssues.Add("config/server.json 缺少 self_id，无法跳过自己发的消息");
if (!File.Exists(env.PersonaFile))
    startupIssues.Add("data/persona/System Prompt.md 不存在，自我为空");
if (env.Gate.BotNames.Count == 0)
    startupIssues.Add("config/gate.json 的 bot_names 为空：发言门只能从人设里猜自己的名字，" +
                      "叫名字时可能不认自己（建议显式填上，如 \"bot_names\": [\"星璃\"]）");


app.UseDefaultFiles();
app.UseStaticFiles();
app.MapAdmin();

// 后台首页：POST "/" 已被 OneBot 上报占用，所以 GET "/" 需要显式给一个处理端点，
// 否则路由会把它判成 405 Method Not Allowed。
app.MapGet("/", (IWebHostEnvironment hostEnv) =>
{
    var index = Path.Combine(hostEnv.WebRootPath ?? "wwwroot", "index.html");
    return File.Exists(index)
        ? Results.File(index, "text/html; charset=utf-8")
        : Results.Text("后台前端未构建：缺少 wwwroot/index.html", "text/plain; charset=utf-8");
});


/// <summary>
/// OneBot 11 上报入口。先 ACK 再处理：NapCat 约 3 秒收不到 200 就会重投
/// </summary>
app.MapPost("/", async (HttpContext ctx) =>
{
    var receiver = ctx.RequestServices.GetRequiredService<Receiver>();
    var limit = env.Server.Report.MaxBodyBytes;

    if (ctx.Request.ContentLength is long declared && declared > limit)
        return Results.Json(new { ok = false, error = "too_large" }, statusCode: 413);

    using var ms = new MemoryStream();
    var buffer = new byte[8192];
    long total = 0;
    int read;

    while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
    {
        total += read;
        if (total > limit)
            return Results.Json(new { ok = false, error = "too_large" }, statusCode: 413);
        ms.Write(buffer, 0, read);
    }

    // 先拿原始字节再验签：x-signature 是对原始 body 做的 HMAC
    var bytes = ms.ToArray();

    if (!receiver.CheckAuth(ctx.Request.Headers, bytes))
        return Results.Json(new { ok = false, error = "unauthorized" }, statusCode: 401);

    try
    {
        var reason = receiver.Handle(Encoding.UTF8.GetString(bytes));
        if (reason is not null)
            app.Logger.LogDebug("事件已忽略：{Reason}", reason);
    }
    catch (Exception ex)
    {
        // 出错也返回 200，避免 NapCat 反复重投形成风暴
        app.Logger.LogError(ex, "处理上报时异常");
    }

    // NapCat 会把响应体当作「快速操作」解析，返回空体最安全
    return Results.Empty;
});


_ = Task.Run(async () =>
{
    await Task.Delay(300);
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    log.LogInformation("StarGameAI 已启动");
    log.LogInformation("  根目录      {Root}", env.Root);
    log.LogInformation("  监听        {Bind}", bind);
    log.LogInformation("  NapCat API  {Api}", env.Server.Napcat.ApiBase);
    log.LogInformation("  机器人 QQ   {Self}", env.Server.SelfId);
    log.LogInformation("  模型        {Model} @ {Base}", env.Model.Model, env.Model.BaseUrl);
    log.LogInformation("  白名单群    {Groups}", string.Join(",", env.Server.Whitelist.Groups));
    log.LogInformation("  白名单私聊  {Private}", string.Join(",", env.Server.Whitelist.Private));
    log.LogInformation("  工具准入    {Mode}", env.ToolPolicy.AutoApprove ? "自动（契约+静态检查+试跑）" : "人工批准");
    log.LogInformation("  睡眠行为    {Mode}", env.Sleep.BehaviorWhileSleeping);
    log.LogInformation("  后台管理    http://{Host}:{Port}/", env.Server.Listen.Host, env.Server.Listen.Port);

    foreach (var issue in startupIssues)
        log.LogWarning("  配置提醒    {Issue}", issue);

    try
    {
        var tooling = app.Services.GetRequiredService<Tooling>();
        var tools = tooling.List();
        var active = tools.Count(t => t.Meta.Status == "active");
        log.LogInformation("  工具        共 {Total} 个，active {Active} 个", tools.Count, active);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "  工具目录扫描失败");
    }
});

app.Run();

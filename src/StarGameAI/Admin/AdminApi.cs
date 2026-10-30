using System.Net;
using System.Text.Json;
using StarGameAI.Core;
using StarGameAI.Gateway;
using StarGameAI.Llm;
using StarGameAI.Memory;
using StarGameAI.Pipeline;
using StarGameAI.Sleep;
using StarGameAI.Tools;

namespace StarGameAI.Admin;

public static class AdminApi
{
    private static readonly DateTime StartedAt = DateTime.UtcNow;

    public static void MapAdmin(this WebApplication app)
    {
        app.MapGet("/api/status", (HttpContext ctx, Env env, Metrics metrics, Receiver receiver,
            ChatLog chatLog, Consolidator consolidator, DeepSeekClient llm, NapcatHealth health) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var wm = consolidator.LoadWatermark();
            var state = consolidator.LoadState();
            var today = DateOnly.FromDateTime(DateTime.Now);

            return Results.Json(new
            {
                online = true,
                napcat_online = health.Online,
                napcat = new
                {
                    online = health.Online,
                    latency_ms = health.LatencyMs,
                    checked_at = health.CheckedAt,
                    online_since = health.OnlineSince,
                    last_error = health.LastError,
                },
                self_id = env.Server.SelfId,
                uptime_s = (int)(DateTime.UtcNow - StartedAt).TotalSeconds,
                today = new
                {
                    messages = metrics.Get("event.messages"),
                    replies = metrics.Get("send.ok"),
                    gate_skips = metrics.Get("gate.skips"),
                    log_entries = chatLog.CountOfDate(today),
                },
                watermark = new { last_consumed_log_ts = wm.LastConsumedLogTs },
                last_sleep = state.LastRunTs == 0
                    ? null
                    : new { at = state.LastRunTs, rev = state.LastRev, ok = state.LastOk, error = state.LastError },
                model = new
                {
                    name = env.Model.Model,
                    base_url = env.Model.BaseUrl,
                    key_present = llm.HasKey,
                },
                queues = new { inbound = receiver.QueueDepth },
            });
        });

        app.MapGet("/api/persona", (HttpContext ctx, Env env, Persona persona) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            var view = persona.Read();
            return Results.Json(new { md = view.Md, meta = view.Meta, segments = view.Segments });
        });

        app.MapPut("/api/persona", async (HttpContext ctx, Env env, Persona persona) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");
            var md = body.RootElement.TryGetProperty("md", out var m) ? m.GetString() ?? "" : "";

            var saved = persona.Save(md, "human:admin", isHuman: true);
            Audit(env, "persona.save", new { saved.Ok, saved.Rev, saved.Error, changed = saved.ChangedSegments });

            return saved.Ok
                ? Results.Json(new { ok = true, rev = saved.Rev, changed = saved.ChangedSegments })
                : Bad(saved.Error);
        });

        app.MapGet("/api/persona/history", (HttpContext ctx, Env env, Persona persona) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            return Results.Json(persona.History());
        });

        app.MapGet("/api/persona/diff", (HttpContext ctx, Env env, Persona persona, int? rev) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            var r = rev ?? 0;
            return Results.Json(new { rev = r, diff = r > 0 ? persona.DiffFor(r) : "" });
        });

        app.MapPost("/api/persona/rollback/{rev:int}", (HttpContext ctx, Env env, Persona persona, int rev) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            var result = persona.Rollback(rev, "human:rollback");
            Audit(env, "persona.rollback", new { rev, result.Ok, result.Rev, result.Error });

            return result.Ok ? Results.Json(new { ok = true, rev = result.Rev }) : Bad(result.Error);
        });

        // 修订上限：0 = 不允许 AI 自行改写；改完立即生效，不用重启
        app.MapPut("/api/persona/budget", async (HttpContext ctx, Env env, Persona persona) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            var root = body.RootElement;
            var perDay = env.Persona.RevisionBudgetPerDay;
            var minGap = env.Persona.MinMinutesBetweenRevisions;

            if (root.TryGetProperty("revision_budget_per_day", out var b) && b.TryGetInt32(out var bv))
                perDay = Math.Clamp(bv, 0, 100);
            if (root.TryGetProperty("min_minutes_between_revisions", out var m) && m.TryGetInt32(out var mv))
                minGap = Math.Clamp(mv, 0, 1440);

            var policy = new PersonaPolicy
            {
                RevisionBudgetPerDay = perDay,
                MinMinutesBetweenRevisions = minGap,
            };

            Env.WriteAtomic(Path.Combine(env.ConfigDir, "persona.json"),
                JsonSerializer.Serialize(policy, AppJson.Config));

            env.Persona = policy;   // 实时生效
            Audit(env, "persona.budget", new { perDay, minGap });

            var view = persona.Read();
            return Results.Json(new
            {
                ok = true,
                revision_budget_per_day = perDay,
                min_minutes_between_revisions = minGap,
                used_today = view.Meta.Budget.UsedToday,
            });
        });

        // 清零今日已用次数（上限不变），方便当天再放行一次
        app.MapPost("/api/persona/budget/reset-used", (HttpContext ctx, Env env, Persona persona) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var meta = persona.Read().Meta;
            meta.Budget.UsedToday = 0;
            var path = Path.Combine(env.PersonaDir, "meta", "system-prompt.json");
            Env.WriteAtomic(path, JsonSerializer.Serialize(meta, AppJson.Options));

            Audit(env, "persona.budget.reset", new { });
            return Results.Json(new { ok = true, used_today = 0 });
        });

        app.MapGet("/api/memory", (HttpContext ctx, Env env, Store store, string? scope, string? kind,
            string? q, int? page, int? size) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var p = page is null or <= 0 ? 1 : page.Value;
            var s = size is null or <= 0 or > 200 ? 50 : size.Value;

            var scopes = string.IsNullOrWhiteSpace(scope) ? store.Scopes() : new List<string> { scope! };
            var all = new List<MemEntry>();
            foreach (var sc in scopes)
            {
                foreach (var e in store.Read(sc))
                {
                    if (!string.IsNullOrWhiteSpace(kind) && e.Kind != kind) continue;
                    if (!string.IsNullOrWhiteSpace(q)
                        && !e.Text.Contains(q, StringComparison.OrdinalIgnoreCase)
                        && !e.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    all.Add(e);
                }
            }

            var total = all.Count;
            var items = all.OrderByDescending(e => e.Ts).Skip((p - 1) * s).Take(s).ToList();
            return Results.Json(new { total, page = p, size = s, scopes = store.Scopes(), items });
        });

        app.MapPatch("/api/memory/{scope}/{id}", async (HttpContext ctx, Env env, Store store,
            string scope, string id) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            var root = body.RootElement;
            var ok = store.Patch(scope, id, entry =>
            {
                if (root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    entry.Text = t.GetString() ?? entry.Text;
                if (root.TryGetProperty("weight", out var w) && w.TryGetInt32(out var wv))
                    entry.Weight = wv;
                if (root.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.String)
                {
                    var lv = l.GetString() ?? "";
                    if (Store.Levels.Contains(lv)) entry.Level = lv;
                }
                if (root.TryGetProperty("pinned", out var p)
                    && p.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    entry.Pinned = p.GetBoolean();
            });

            Audit(env, "memory.patch", new { scope, id, ok });
            return ok ? Results.Json(new { ok = true }) : NotFound("条目不存在");
        });

        app.MapDelete("/api/memory/{scope}/{id}", (HttpContext ctx, Env env, Store store,
            string scope, string id) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var entry = store.Read(scope).FirstOrDefault(e => e.Id == id);
            if (entry is null) return NotFound("条目不存在");
            if (entry.Pinned)
                return Results.Json(new { ok = false, error = "已固定的条目不可删除" }, statusCode: 409);

            // 删除 = 打 tombstone（I8），物理清理交给离线任务
            var ok = store.Patch(scope, id, e => e.Tombstone = true);
            Audit(env, "memory.tombstone", new { scope, id, ok });
            return ok ? Results.Json(new { ok = true }) : NotFound("条目不存在");
        });

        app.MapGet("/api/memory/profile/{userId}", (HttpContext ctx, Env env, Store store, string userId) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            return Results.Json(new { userId, md = store.ReadProfile(userId) });
        });

        // 深层记忆：人物画像（面向人）与事件画像（面向群）
        app.MapGet("/api/deep", (HttpContext ctx, Env env, Store store) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var persons = store.PersonIds().Select(uid => new
            {
                userId = uid,
                lines = store.ReadProfile(uid).Split('\n').Count(l => l.Trim().Length > 0),
                chars = store.ReadProfile(uid).Length,
            });

            var groups = store.GroupIds().Select(gid =>
            {
                var set = store.ReadPatterns(gid);
                return new
                {
                    groupId = gid,
                    lines = store.ReadGroupProfile(gid).Split('\n').Count(l => l.Trim().Length > 0),
                    patterns = set.Patterns.Count(p => p.Weight > 0),
                    updatedAt = set.UpdatedAt,
                };
            });

            return Results.Json(new
            {
                policy = env.Memory,
                shallow = store.Stats(),
                persons,
                groups,
            });
        });

        app.MapGet("/api/deep/person/{userId}", (HttpContext ctx, Env env, Store store, string userId) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            return Results.Json(new { userId, md = store.ReadProfile(userId) });
        });

        // 人工修正深层画像（比如画像被写歪了，或者要清空让它重新归纳）
        app.MapPut("/api/deep/person/{userId}", async (HttpContext ctx, Env env, Store store, string userId) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");
            var md = body.RootElement.TryGetProperty("md", out var m) ? m.GetString() ?? "" : "";

            store.SaveProfile(userId, md);
            Audit(env, "deep.person.put", new { userId, chars = md.Length });
            return Results.Json(new { ok = true, chars = md.Length });
        });

        app.MapPut("/api/deep/group/{groupId}", async (HttpContext ctx, Env env, Store store, string groupId) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");
            var md = body.RootElement.TryGetProperty("md", out var m) ? m.GetString() ?? "" : "";

            store.SaveGroupProfile(groupId, md);
            Audit(env, "deep.group.put", new { groupId, chars = md.Length });
            return Results.Json(new { ok = true, chars = md.Length });
        });

        app.MapGet("/api/deep/group/{groupId}", (HttpContext ctx, Env env, Store store, string groupId) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var set = store.ReadPatterns(groupId);
            store.RecomputePatterns(set);   // 展示时按当前时间重算权重

            return Results.Json(new
            {
                groupId,
                md = store.ReadGroupProfile(groupId),
                patterns = set.Patterns,
            });
        });

        // 疑似重复：0.5~0.85 的灰区不做自动合并（怕吃掉真事实），交给人来看
        app.MapGet("/api/memory/duplicates", (HttpContext ctx, Env env, Store store,
            string? scope, double? threshold) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var th = threshold is > 0 and < 1 ? threshold!.Value : 0.55;
            var scopes = string.IsNullOrWhiteSpace(scope) ? store.Scopes() : new List<string> { scope! };
            var pairs = new List<object>();

            foreach (var sc in scopes)
            {
                var items = store.Read(sc).Where(e => !e.Tombstone).ToList();
                for (var i = 0; i < items.Count; i++)
                {
                    for (var k = i + 1; k < items.Count; k++)
                    {
                        if (items[i].Kind != items[k].Kind) continue;
                        var sim = Sleep.Consolidator.Similarity(items[i].Text, items[k].Text);
                        if (sim < th) continue;

                        pairs.Add(new
                        {
                            scope = sc,
                            similarity = Math.Round(sim, 3),
                            a = new { id = items[i].Id, text = items[i].Text, weight = items[i].Weight, ts = items[i].Ts },
                            b = new { id = items[k].Id, text = items[k].Text, weight = items[k].Weight, ts = items[k].Ts },
                        });
                    }
                }
            }

            return Results.Json(pairs.OrderByDescending(p => p.GetType()
                .GetProperty("similarity")!.GetValue(p)).Take(200));
        });

        // 人工合并：保留 keep，其余打 tombstone
        app.MapPost("/api/memory/merge", async (HttpContext ctx, Env env, Store store) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            var root = body.RootElement;
            var scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() ?? "" : "";
            var keep = root.TryGetProperty("keep_id", out var kp) ? kp.GetString() ?? "" : "";
            var dropIds = new List<string>();
            if (root.TryGetProperty("drop_ids", out var ds) && ds.ValueKind == JsonValueKind.Array)
                foreach (var d in ds.EnumerateArray())
                    if (d.GetString() is { Length: > 0 } s) dropIds.Add(s);

            if (scope.Length == 0 || keep.Length == 0 || dropIds.Count == 0)
                return Bad("需要 scope / keep_id / drop_ids");

            var dropped = 0;
            foreach (var id in dropIds.Where(x => x != keep))
                if (store.Patch(scope, id, e => e.Tombstone = true)) dropped++;

            Audit(env, "memory.merge", new { scope, keep, dropped });
            return Results.Json(new { ok = true, kept = keep, dropped });
        });

        // 安全清理：只合并「去掉标点后完全相同」的条目（不涉及语义判断，不会吃事实）
        app.MapPost("/api/memory/dedupe", (HttpContext ctx, Env env, Store store, string? scope) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var scopes = string.IsNullOrWhiteSpace(scope) ? store.Scopes() : new List<string> { scope! };
            var merged = 0;

            foreach (var sc in scopes)
            {
                var groups = store.Read(sc)
                    .Where(e => !e.Tombstone)
                    .GroupBy(e => Sleep.Consolidator.SimilarityKey(e.Text));

                foreach (var group in groups)
                {
                    var items = group.OrderByDescending(e => e.Weight)
                        .ThenByDescending(e => e.Ts).ToList();
                    if (items.Count <= 1) continue;

                    var keep = items[0];
                    foreach (var dup in items.Skip(1))
                    {
                        if (dup.Pinned) continue;   // pinned 一律不动
                        if (store.Patch(sc, dup.Id, e => e.Tombstone = true)) merged++;
                    }
                }
            }

            Audit(env, "memory.dedupe", new { merged });
            return Results.Json(new { ok = true, merged });
        });

        app.MapGet("/api/tools", (HttpContext ctx, Env env, Tooling tooling, BuiltinTools builtin) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var list = new List<object>();

            foreach (var t in builtin.Defs)
                list.Add(new
                {
                    name = t.Function.Name,
                    status = "builtin",
                    description = t.Function.Description,
                    version = 0,
                    last_error = "",
                    source = "builtin",
                });

            foreach (var t in tooling.List())
                list.Add(new
                {
                    name = t.Name,
                    status = t.Meta.Status,
                    description = string.IsNullOrWhiteSpace(t.Meta.Description)
                        ? t.Contract?.Description ?? ""
                        : t.Meta.Description,
                    version = t.Meta.Version,
                    last_error = t.Meta.RejectReason.Length > 0 ? t.Meta.RejectReason : t.Meta.LastError,
                    source = "python",
                });

            return Results.Json(list);
        });

        app.MapGet("/api/tools/{name}", (HttpContext ctx, Env env, Tooling tooling, BuiltinTools builtin, string name) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var py = tooling.Load(name);
            if (py is not null)
                return Results.Json(new { name = py.Name, contract = py.ContractRaw, code = py.Code, meta = py.Meta, source = "python" });

            var def = builtin.Defs.FirstOrDefault(d => d.Function.Name == name);
            if (def is null) return NotFound("工具不存在");

            return Results.Json(new
            {
                name,
                contract = JsonSerializer.Serialize(def.Function, new JsonSerializerOptions(AppJson.Config) { WriteIndented = true }),
                code = "（内置工具：C# 直接实现，不是 Python 脚本）\n\n" + def.Function.Description,
                meta = new { name, status = "builtin", version = 0 },
                source = "builtin",
            });
        });

        app.MapPost("/api/tools/{name}/enable", async (HttpContext ctx, Env env, Tooling tooling, string name) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            // 启用前重跑门禁，不允许绕过静态检查
            var admit = await tooling.AdmitAsync(name, ctx.RequestAborted);
            Audit(env, "tool.enable", new { name, admit.Ok, admit.Reason });

            return admit.Ok
                ? Results.Json(new { ok = true, status = "active" })
                : Results.Json(new { ok = false, status = "disabled", error = admit.Reason });
        });

        app.MapPost("/api/tools/{name}/disable", (HttpContext ctx, Env env, Tooling tooling, string name) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            var ok = tooling.SetStatus(name, "disabled");
            Audit(env, "tool.disable", new { name, ok });
            return ok ? Results.Json(new { ok = true, status = "disabled" }) : NotFound("工具不存在");
        });

        app.MapPost("/api/tools/{name}/run", async (HttpContext ctx, Env env, Tooling tooling,
            BuiltinTools builtin, string name) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            var args = "{}";
            if (body is not null && body.RootElement.TryGetProperty("args", out var a))
                args = a.GetRawText();

            // 内置工具：直接在这里跑，方便后台试
            if (builtin.IsBuiltin(name))
            {
                JsonElement parsed = default;
                try
                {
                    using var doc = JsonDocument.Parse(args);
                    parsed = doc.RootElement.Clone();
                }
                catch { /* 参数坏了交给工具自己报错 */ }

                var started = Environment.TickCount64;
                var output = await builtin.InvokeAsync(name, parsed,
                    new ToolCallContext { Scope = "admin:test", UserId = "", TraceId = "admin" },
                    ctx.RequestAborted);
                var ms = (int)(Environment.TickCount64 - started);

                var ok = !BuiltinTools.LooksFailed(output);

                Audit(env, "tool.run", new { name, ok, ms, scope = "builtin" });
                return Results.Json(new { ok, data = output, error = ok ? "" : output, ms });
            }

            var run = await tooling.RunAsync(name, args, ctx.RequestAborted);
            Audit(env, "tool.run", new { name, run.Ok, run.Ms, error = run.Error });

            return Results.Json(new
            {
                ok = run.Ok,
                data = run.Data.HasValue ? (object?)run.Data.Value : null,
                error = run.Error,
                ms = run.Ms,
            });
        });

        app.MapGet("/api/sleep", (HttpContext ctx, Env env, Consolidator consolidator, SleepRunner runner) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var wm = consolidator.LoadWatermark();
            var state = consolidator.LoadState();

            return Results.Json(new
            {
                config = env.Sleep,
                state = new
                {
                    sleeping = runner.Busy || consolidator.IsRunning,
                    last_run_ts = state.LastRunTs,
                    last_attempt_ts = state.LastAttemptTs,
                    last_ok = state.LastOk,
                    last_error = state.LastError,
                    last_rev = state.LastRev,
                },
                watermark = new { last_consumed_log_ts = wm.LastConsumedLogTs },
                signals = consolidator.Signals(),
            });
        });

        app.MapPut("/api/sleep/config", async (HttpContext ctx, Env env) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            SleepConfig? cfg;
            try
            {
                cfg = body.RootElement.Deserialize<SleepConfig>(AppJson.Config);
            }
            catch (JsonException ex)
            {
                return Bad("配置格式不正确：" + ex.Message);
            }

            if (cfg is null) return Bad("配置为空");

            var modes = new[] { "silent", "fast_reply", "queue" };
            if (!modes.Contains(cfg.BehaviorWhileSleeping))
                return Bad("behavior_while_sleeping 只能是 silent / fast_reply / queue");

            Env.WriteAtomic(
                Path.Combine(env.ConfigDir, "sleep.json"),
                JsonSerializer.Serialize(cfg, AppJson.Config));

            // 实时生效，不需要重启
            env.Sleep = cfg;
            Audit(env, "sleep.config", new { behavior = cfg.BehaviorWhileSleeping });

            return Results.Json(new { ok = true });
        });

        app.MapPost("/api/sleep/start", (HttpContext ctx, Env env, SleepRunner runner) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            var started = runner.Start("manual:admin");
            Audit(env, "sleep.start", new { started });
            return Results.Json(new { ok = started, error = started ? "" : "已有一轮巩固在进行" });
        });

        app.MapPost("/api/sleep/cancel", (HttpContext ctx, Env env, SleepRunner runner) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            var cancelled = runner.Cancel();
            Audit(env, "sleep.cancel", new { cancelled });
            return Results.Json(new { ok = cancelled });
        });

        // 回退水位线：让下一轮巩固重新处理旧日志。
        // 提交阶段会与库内已有条目去重，所以重跑不会产生重复记忆。
        app.MapPost("/api/sleep/rewind", async (HttpContext ctx, Env env, Consolidator consolidator) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            if (consolidator.IsRunning)
                return Results.Json(new { ok = false, error = "正在巩固中，稍后再试" }, statusCode: 409);

            var body = await ReadJsonAsync(ctx);
            long ts = 0;
            if (body is not null && body.RootElement.TryGetProperty("ts", out var t) && t.TryGetInt64(out var v))
                ts = Math.Max(0, v);

            var path = Path.Combine(env.SleepDir, "watermark.json");
            Env.WriteAtomic(path, JsonSerializer.Serialize(
                new SleepWatermark { LastConsumedLogTs = ts, LastConsumedMsgId = "" }, AppJson.Config));

            Audit(env, "sleep.rewind", new { ts });
            return Results.Json(new { ok = true, last_consumed_log_ts = ts });
        });

        app.MapGet("/api/log", (HttpContext ctx, Env env, ChatLog chatLog,
            string? date, string? scope, string? trace, int? page, int? size) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var day = DateOnly.TryParse(date, out var d) ? d : DateOnly.FromDateTime(DateTime.Now);
            var p = page is null or <= 0 ? 1 : page.Value;
            var s = size is null or <= 0 or > 500 ? 100 : size.Value;

            var items = chatLog.Read(day, string.IsNullOrWhiteSpace(scope) ? null : scope,
                string.IsNullOrWhiteSpace(trace) ? null : trace);

            var total = items.Count;
            var paged = items.Skip((p - 1) * s).Take(s).ToList();
            return Results.Json(new { total, page = p, size = s, date = day.ToString("yyyy-MM-dd"), items = paged });
        });

        app.MapGet("/api/gate", (HttpContext ctx, Env env, Gate gate, Metrics metrics) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var activeGroups = env.Server.Whitelist.Groups
                .Select(gid => new
                {
                    groupId = gid,
                    effective = new
                    {
                        ambientThreshold = gate.Effective("group:" + gid).AmbientThreshold,
                        dailyBudget = gate.Effective("group:" + gid).DailyBudget,
                        cooldownSeconds = gate.Effective("group:" + gid).CooldownSeconds,
                        enabled = gate.Effective("group:" + gid).Enabled,
                    },
                    override_ = env.Gate.Groups.TryGetValue(gid, out var ov) ? ov : null,
                    today = gate.Stats("group:" + gid),
                })
                .ToList();

            return Results.Json(new
            {
                config = env.Gate,
                // 判定模型看到的自我描述：叫名字不认人的时候，先看这一行
                self = gate.SelfDescription(),
                groups = activeGroups,
                today = new
                {
                    replies = metrics.Get("send.ok"),
                    gate_skips = metrics.Get("gate.skips"),
                    gate_llm_calls = metrics.Get("gate.llm_calls"),
                },
            });
        });

        app.MapPut("/api/gate", async (HttpContext ctx, Env env, Gate gate) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            GateConfig? cfg;
            try
            {
                // 同样是深合并，避免「改一个阈值把词表/群体覆盖一起清空」
                cfg = MergeConfig(env.Gate, body.RootElement);
            }
            catch (JsonException ex)
            {
                return Bad("配置格式不正确：" + ex.Message);
            }

            if (cfg is null) return Bad("配置为空");

            cfg.AmbientThreshold = Math.Clamp(cfg.AmbientThreshold, 0, 100);
            cfg.DirectThreshold = Math.Clamp(cfg.DirectThreshold, 0, 100);
            cfg.SampleRate = Math.Clamp(cfg.SampleRate, 0, 1);
            cfg.CooldownSeconds = Math.Clamp(cfg.CooldownSeconds, 0, 3600);
            cfg.DailyBudget = Math.Clamp(cfg.DailyBudget, 0, 1000);
            cfg.MaxLlmCallsPerDay = Math.Clamp(cfg.MaxLlmCallsPerDay, 0, 5000);
            cfg.EventBoost ??= new EventBoostConfig();
            cfg.EventBoost.Scale = Math.Clamp(cfg.EventBoost.Scale, 0, 100);
            cfg.EventBoost.Max = Math.Clamp(cfg.EventBoost.Max, 0, 100);
            cfg.EventBoost.InjectTopN = Math.Clamp(cfg.EventBoost.InjectTopN, 0, 20);
            cfg.Groups ??= new Dictionary<string, GroupOverride>();
            cfg.BotNames ??= new List<string>();

            Env.WriteAtomic(Path.Combine(env.ConfigDir, "gate.json"),
                JsonSerializer.Serialize(cfg, AppJson.Config));

            env.Gate = cfg;   // 实时生效
            Audit(env, "gate.config", new
            {
                cfg.AmbientThreshold,
                cfg.CooldownSeconds,
                cfg.DailyBudget,
                boost = cfg.EventBoost.Enabled,
            });

            return Results.Json(new { ok = true });
        });

        // 门控沙盒：粘一段话进来，看它会怎么判，不需要真的发消息
        app.MapPost("/api/gate/simulate", async (HttpContext ctx, Env env, Gate gate) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            var root = body.RootElement;
            var scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() ?? "" : "";
            var text = root.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
            var speaker = root.TryGetProperty("speaker", out var sp) ? sp.GetString() ?? "" : "";
            var addressed = root.TryGetProperty("addressed", out var ad)
                            && ad.ValueKind is JsonValueKind.True or JsonValueKind.False
                            && ad.GetBoolean();

            if (scope.Length == 0 || text.Length == 0) return Bad("scope 与 text 都不能为空");

            var d = await gate.SimulateAsync(scope, text, speaker, addressed, ctx.RequestAborted);

            return Results.Json(new
            {
                speak = d.Speak,
                score = d.Score,
                stage = d.Stage,
                reason = d.Reason,
                why_not = d.WhyNot,
                intent = d.Intent,
                addressed = d.Addressed,
                need_think = d.NeedThink,
                used_llm = d.UsedLlm,
                threshold = d.Threshold,
                matched_patterns = d.MatchedPatterns,
            });
        });

        app.MapGet("/api/gate/audit", (HttpContext ctx, Env env, string? date) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var day = DateOnly.TryParse(date, out var d) ? d : DateOnly.FromDateTime(DateTime.Now);
            var path = Path.Combine(env.AuditDir, "gate.jsonl");
            var items = new List<object>();

            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        var ts = root.TryGetProperty("ts", out var t) ? t.GetInt64() : 0;
                        if (DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime) != day) continue;

                        items.Add(new
                        {
                            ts,
                            score = root.TryGetProperty("score", out var s) ? s.GetInt32() : 0,
                            event_boost = root.TryGetProperty("event_boost", out var eb) ? eb.GetDouble() : 0,
                            stage = root.TryGetProperty("stage", out var st) ? st.GetString() : "",
                            matched = root.TryGetProperty("matched", out var mm) && mm.ValueKind == JsonValueKind.Array
                                ? mm.EnumerateArray().Select(x => x.GetString()).ToArray()
                                : Array.Empty<string?>(),
                            reason = root.TryGetProperty("reason", out var r) ? r.GetString() : "",
                            why_not = root.TryGetProperty("why_not", out var w) ? w.GetString() : "",
                            used_llm = root.TryGetProperty("used_llm", out var u) && u.ValueKind == JsonValueKind.True,
                            speak = root.TryGetProperty("speak", out var sp) && sp.ValueKind == JsonValueKind.True,
                        });
                    }
                    catch { /* 跳过坏行 */ }
                }
            }

            var total = items.Count;
            var skips = items.Count;
            return Results.Json(new
            {
                items = items.TakeLast(500),
                total,
                skip_rate = 0.0,
                note = skips > 0 ? "漏言率需要人工抽样判定，这里只提供静默记录" : "",
            });
        });

        // 最近的消息及其经过的神经元（瀑布图数据源）
        app.MapGet("/api/trace/recent", (HttpContext ctx, Env env, Tracer tracer, ChatLog chatLog,
            string? date, int? limit) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var day = DateOnly.TryParse(date, out var d) ? d : DateOnly.FromDateTime(DateTime.Now);
            var take = limit is null or <= 0 or > 300 ? 60 : limit.Value;

            var spans = tracer.Read(day);
            var grouped = spans
                .GroupBy(s => Str(s, "trace_id"))
                .Where(g => g.Key.Length > 0)
                .Select(g =>
                {
                    var ordered = g.OrderBy(s => Int(s, "seq")).ToList();
                    return new
                    {
                        traceId = g.Key,
                        ts = ordered.Count > 0 ? Long(ordered[0], "ts") : 0,
                        spans = ordered.Select(s => new
                        {
                            seq = Int(s, "seq"),
                            neuron = Str(s, "neuron"),
                            ms = Int(s, "ms"),
                            ok = Bool(s, "ok"),
                            stage = Str(s, "stage"),
                            brief = Str(s, "brief"),
                            tokens = Int(s, "tokens"),
                        }).ToList(),
                    };
                })
                .OrderByDescending(t => t.ts)
                .Take(take)
                .ToList();

            // 用日志里的正文补上「这条是谁说的什么」
            var log = chatLog.Read(day);
            var byTrace = log.Where(e => e.TraceId.Length > 0)
                .GroupBy(e => e.TraceId)
                .ToDictionary(g => g.Key, g => g.First());

            var items = grouped.Select(t =>
            {
                byTrace.TryGetValue(t.traceId, out var entry);
                var neurons = t.spans.Select(s => s.neuron).Distinct().ToList();
                var spoke = t.spans.Any(s => s.neuron == "send" && s.ok);
                var total = t.spans.Sum(s => s.ms);

                return new
                {
                    trace_id = t.traceId,
                    ts = t.ts,
                    scope = entry?.Scope ?? "",
                    speaker = entry != null ? (entry.IsSelf ? "AI" : entry.Name) : "",
                    text = Trim(entry?.Text ?? "", 60),
                    total_ms = total,
                    spoke,
                    neurons,
                    spans = t.spans,
                };
            });

            return Results.Json(new { date = day.ToString("yyyy-MM-dd"), items });
        });

        // 单条 trace 的全部 span
        app.MapGet("/api/trace/{traceId}", (HttpContext ctx, Env env, Tracer tracer, string traceId, string? date) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var day = DateOnly.TryParse(date, out var d) ? d : DateOnly.FromDateTime(DateTime.Now);
            var spans = tracer.Read(day)
                .Where(s => Str(s, "trace_id") == traceId)
                .OrderBy(s => Int(s, "seq"))
                .Select(s => new
                {
                    seq = Int(s, "seq"),
                    neuron = Str(s, "neuron"),
                    ts = Long(s, "ts"),
                    ms = Int(s, "ms"),
                    ok = Bool(s, "ok"),
                    stage = Str(s, "stage"),
                    in_chars = Int(s, "in_chars"),
                    out_chars = Int(s, "out_chars"),
                    tokens = Int(s, "tokens"),
                    brief = Str(s, "brief"),
                })
                .ToList();

            return Results.Json(new { trace_id = traceId, date = day.ToString("yyyy-MM-dd"), spans });
        });

        // 各神经元的汇总统计（拓扑点亮用）
        app.MapGet("/api/neurons/stats", (HttpContext ctx, Env env, Tracer tracer, string? date) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var day = DateOnly.TryParse(date, out var d) ? d : DateOnly.FromDateTime(DateTime.Now);
            var spans = tracer.Read(day);

            var neurons = spans
                .GroupBy(s => Str(s, "neuron"))
                .Where(g => g.Key.Length > 0)
                .Select(g =>
                {
                    var list = g.ToList();
                    var ms = list.Select(s => Int(s, "ms")).OrderBy(x => x).ToList();
                    return new
                    {
                        neuron = g.Key,
                        calls = list.Count,
                        ok = list.Count(s => Bool(s, "ok")),
                        fail = list.Count(s => !Bool(s, "ok")),
                        avg_ms = ms.Count == 0 ? 0 : (int)ms.Average(),
                        max_ms = ms.Count == 0 ? 0 : ms[^1],
                        tokens = list.Sum(s => Int(s, "tokens")),
                    };
                })
                .OrderByDescending(n => n.calls)
                .ToList();

            var stages = spans
                .Where(s => Str(s, "neuron") == "gate" && Str(s, "stage").Length > 0)
                .GroupBy(s => Str(s, "stage"))
                .Select(g => new { stage = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count)
                .ToList();

            var traces = spans.Select(s => Str(s, "trace_id")).Where(x => x.Length > 0).Distinct().Count();

            return Results.Json(new
            {
                date = day.ToString("yyyy-MM-dd"),
                traces,
                neurons,
                gate_stages = stages,
            });
        });

        // 注意：这不是 loss。LLM 是推理时系统，没有梯度、没有参数更新，算不出 loss。
        // 这里给的是可观测代理量：每次改写人设/画像时，前后差了多少（变化率 = 1 - 相似度）。
        app.MapGet("/api/delta", (HttpContext ctx, Env env, DeltaLog delta, int? days) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var n = days is null or <= 0 or > 120 ? 14 : days.Value;
            var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - n * 86400L;
            var records = delta.Since(since).OrderByDescending(r => r.Ts).ToList();

            var daily = records
                .GroupBy(r => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(r.Ts).LocalDateTime))
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    date = g.Key.ToString("yyyy-MM-dd"),
                    count = g.Count(),
                    avg_change_rate = Math.Round(g.Average(r => 1 - r.Similarity), 3),
                    persona = g.Count(r => r.Kind == "persona"),
                    person = g.Count(r => r.Kind == "person"),
                    group = g.Count(r => r.Kind == "group"),
                    absorbed = g.Sum(r => r.Absorbed),
                })
                .ToList();

            var recent = records.Take(60).Select(r => new
            {
                ts = r.Ts,
                kind = r.Kind,
                target = r.Target,
                by = r.By,
                change_rate = Math.Round(1 - r.Similarity, 3),
                similarity = r.Similarity,
                added = r.AddedLines,
                removed = r.RemovedLines,
                absorbed = r.Absorbed,
                note = r.Note,
            }).ToList();

            return Results.Json(new
            {
                days = n,
                note = "这不是 loss。这里记的是每次改写人设/画像时前后差了多少（变化率 = 1 - 相似度）。",
                summary = new
                {
                    total = records.Count,
                    persona = records.Count(r => r.Kind == "persona"),
                    person = records.Count(r => r.Kind == "person"),
                    group = records.Count(r => r.Kind == "group"),
                    avg_change_rate = records.Count == 0
                        ? 0
                        : Math.Round(records.Average(r => 1 - r.Similarity), 3),
                    last_change_ts = records.Count > 0 ? records[0].Ts : 0,
                },
                daily,
                recent,
            });
        });

        // 行为指纹：手动改了人设之后，AI 的行为到底有没有跟着变（比文本相似度更硬）
        app.MapGet("/api/behavior", (HttpContext ctx, Env env, ChatLog chatLog, int? days) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var n = days is null or <= 0 or > 120 ? 14 : days.Value;
            var today = DateOnly.FromDateTime(DateTime.Now);
            var rows = new List<object>();

            for (var i = n - 1; i >= 0; i--)
            {
                var day = today.AddDays(-i);
                var entries = chatLog.Read(day);
                var replies = entries.Where(e => e.IsSelf).ToList();

                var avgLen = replies.Count > 0 ? (int)Math.Round(replies.Average(e => e.Text.Length)) : 0;
                var bracket = replies.Count == 0
                    ? 0
                    : Math.Round(replies.Count(e => e.Text.Contains('（') || e.Text.Contains('('))
                                 / (double)replies.Count, 3);
                var question = replies.Count == 0
                    ? 0
                    : Math.Round(replies.Count(e => e.Text.Contains('？') || e.Text.Contains('?'))
                                 / (double)replies.Count, 3);

                rows.Add(new
                {
                    date = day.ToString("yyyy-MM-dd"),
                    messages = entries.Count,
                    user_messages = entries.Count(e => !e.IsSelf),
                    replies = replies.Count,
                    avg_reply_chars = avgLen,
                    bracket_rate = bracket,
                    question_rate = question,
                });
            }

            return Results.Json(new
            {
                days = n,
                note = "行为指纹看的是「回复本身」，比人设文件变没变更能说明它有没有真的变。",
                items = rows,
            });
        });

        app.MapGet("/api/whitelist", (HttpContext ctx, Env env) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            return Results.Json(new
            {
                groups = env.Server.Whitelist.Groups,
                private_ = env.Server.Whitelist.Private,
                unknown_action = env.Server.Whitelist.UnknownAction,
                note = "两个名单都为空 = 不设限（任何群/私聊都接）",
            });
        });

        app.MapPut("/api/whitelist", async (HttpContext ctx, Env env) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            var root = body.RootElement;
            var groups = new List<string>();
            var privateList = new List<string>();

            if (root.TryGetProperty("groups", out var g) && g.ValueKind == JsonValueKind.Array)
                foreach (var x in g.EnumerateArray())
                {
                    var v = x.GetString()?.Trim() ?? "";
                    if (v.Length > 0 && v.All(char.IsDigit)) groups.Add(v);
                }

            if (root.TryGetProperty("private_", out var p) && p.ValueKind == JsonValueKind.Array)
                foreach (var x in p.EnumerateArray())
                {
                    var v = x.GetString()?.Trim() ?? "";
                    if (v.Length > 0 && v.All(char.IsDigit)) privateList.Add(v);
                }

            var action = root.TryGetProperty("unknown_action", out var a) && a.ValueKind == JsonValueKind.String
                ? (a.GetString() ?? "silent")
                : env.Server.Whitelist.UnknownAction;
            if (action is not ("silent" or "hint")) action = "silent";

            var cfg = Env.ReadJson<ServerConfig>(Path.Combine(env.ConfigDir, "server.json")) ?? env.Server;
            cfg.Whitelist = new WhitelistConfig
            {
                Groups = groups.Distinct().ToList(),
                Private = privateList.Distinct().ToList(),
                UnknownAction = action,
            };

            Env.WriteAtomic(Path.Combine(env.ConfigDir, "server.json"),
                JsonSerializer.Serialize(cfg, AppJson.Config));

            env.Server = cfg;   // 实时生效，不用重启
            Audit(env, "whitelist.save", new { groups = groups.Count, private_ = privateList.Count, action });

            return Results.Json(new { ok = true, groups = cfg.Whitelist.Groups, private_ = cfg.Whitelist.Private });
        });

        app.MapGet("/api/sandbox", (HttpContext ctx, Env env, Sandbox sandbox, string? path) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            try
            {
                var rel = path ?? "";
                var entries = sandbox.List(rel);
                return Results.Json(new
                {
                    root = sandbox.Root,
                    path = rel,
                    parent = rel.Length == 0 ? null : (rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : ""),
                    items = entries,
                });
            }
            catch (SandboxViolation ex)
            {
                return Bad(ex.Message);
            }
        });

        app.MapGet("/api/sandbox/file", (HttpContext ctx, Env env, Sandbox sandbox, string path) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            try
            {
                var (text, truncated, size) = sandbox.Read(path);
                return Results.Json(new { path, text, truncated, size });
            }
            catch (SandboxViolation ex)
            {
                return Bad(ex.Message);
            }
        });

        app.MapPut("/api/sandbox/file", async (HttpContext ctx, Env env, Sandbox sandbox) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            var path = body.RootElement.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
            var text = body.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (path.Length == 0) return Bad("path 不能为空");

            try
            {
                var size = sandbox.Write(path, text);
                Audit(env, "sandbox.write", new { path, size });
                return Results.Json(new { ok = true, size });
            }
            catch (SandboxViolation ex)
            {
                return Bad(ex.Message);
            }
        });

        app.MapDelete("/api/sandbox", (HttpContext ctx, Env env, Sandbox sandbox, string path) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            try
            {
                var ok = sandbox.Delete(path);
                Audit(env, "sandbox.delete", new { path, ok });
                return Results.Json(new { ok });
            }
            catch (SandboxViolation ex)
            {
                return Bad(ex.Message);
            }
        });

        app.MapPost("/api/sandbox/mkdir", async (HttpContext ctx, Env env, Sandbox sandbox) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");
            var path = body.RootElement.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
            if (path.Length == 0) return Bad("path 不能为空");

            try
            {
                sandbox.CreateDirectory(path);
                Audit(env, "sandbox.mkdir", new { path });
                return Results.Json(new { ok = true });
            }
            catch (SandboxViolation ex)
            {
                return Bad(ex.Message);
            }
        });

        app.MapGet("/api/presence", (HttpContext ctx, Env env, Presence presence, Contacts contacts, IdeaGate gate) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            // 「在线人」只列可以私聊的人。
            // 群里说过话的陌生号加不进这个列表——思想层的动作最后要落定成
            // 「私聊谁 / 在哪个群说话」，没私聊过的人根本发不出私信，
            // 把他们算成在线对象只会让一整轮思考注定失败。
            var partners = contacts.PrivatePartnerIds();
            var known = partners.ToList();
            var items = presence.All(known)
                .Where(v => partners.Contains(v.UserId))
                .Select(v =>
                {
                    var c = contacts.ByQq(v.UserId);
                    return new
                    {
                        user_id = v.UserId,
                        name = c?.Name ?? "",
                        state = v.State,
                        source = v.Source,
                        note = v.Note,
                        can_disturb = v.CanDisturb,
                        expires_at = v.ExpiresAt,
                        last_seen = v.LastSeen,
                        manual = v.Source == "manual",
                    };
                }).ToList();

            return Results.Json(new
            {
                config = new
                {
                    env.Idea.ActivityOnlineMinutes,
                    env.Idea.ActivityUnknownHours,
                    env.Idea.SelfReportSleepHours,
                    env.Idea.SelfReportBusyHours,
                    quiet_hours = env.Idea.QuietHours,
                    allow_window = env.Idea.AllowWindow,
                },
                counters = gate.TodayCounters(),
                items,
            });
        });

        app.MapPost("/api/presence/manual", async (HttpContext ctx, Env env, Presence presence) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            var root = body.RootElement;
            var userId = root.TryGetProperty("user_id", out var u) ? u.GetString()?.Trim() ?? "" : "";
            var state = root.TryGetProperty("state", out var s) ? s.GetString() ?? "unknown" : "unknown";
            var note = root.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "";
            var ttl = root.TryGetProperty("ttl_hours", out var t) && t.TryGetInt32(out var tv) ? Math.Clamp(tv, 0, 720) : 0;

            if (userId.Length == 0) return Bad("user_id 不能为空");
            if (state is not ("online" or "offline" or "unknown")) return Bad("state 只能是 online / offline / unknown");

            presence.SetManual(userId, state, note, ttl);
            Audit(env, "presence.manual", new { userId, state, ttl });
            return Results.Json(new { ok = true });
        });

        app.MapDelete("/api/presence/manual/{userId}", (HttpContext ctx, Env env, Presence presence, string userId) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            var cleared = presence.ClearManual(userId);
            Audit(env, "presence.clear", new { userId, cleared });
            return Results.Json(new { ok = cleared });
        });

        app.MapGet("/api/idea", (HttpContext ctx, Env env, IdeaGate gate) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7 * 86400L;
            var recent = gate.ReadSince(since)
                .OrderByDescending(r => r.Ts)
                .Take(100)
                .Select(r => new
                {
                    ts = r.Ts,
                    intent = r.Intent,
                    target = r.Target,
                    text = r.Text,
                    reason = r.Reason,
                    presence = r.Presence,
                    allowed = r.Allowed,
                    blocked_by = r.BlockedBy,
                    blocked_detail = r.BlockedDetail,
                    source = r.Source,
                })
                .ToList();

            return Results.Json(new
            {
                config = env.Idea,
                counters = gate.TodayCounters(),
                recent,
                note = "P2：这里只有准入门槛与记账，还没有发散/收敛（P3/P4）。"
                       + " 可以用下面的「投递一条想法」手动试每道闸。",
            });
        });

        app.MapPut("/api/idea", async (HttpContext ctx, Env env) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            IdeaConfig? cfg;
            try
            {
                // 深合并：只覆盖本条请求里出现的键，没发的（think / chain / burst / intent）保持原样
                cfg = MergeConfig(env.Idea, body.RootElement);
            }
            catch (JsonException ex)
            {
                return Bad("配置格式不正确：" + ex.Message);
            }

            if (cfg is null) return Bad("配置为空");

            cfg.IntervalMinutes = Math.Clamp(cfg.IntervalMinutes, 1, 1440);
            cfg.PerTargetCooldownHours = Math.Clamp(cfg.PerTargetCooldownHours, 0, 168);
            cfg.PerHourLimit = Math.Clamp(cfg.PerHourLimit, 0, 50);
            cfg.DailyLimit = Math.Clamp(cfg.DailyLimit, 0, 200);
            cfg.ActivityOnlineMinutes = Math.Clamp(cfg.ActivityOnlineMinutes, 1, 240);
            cfg.ActivityUnknownHours = Math.Clamp(cfg.ActivityUnknownHours, 1, 240);
            cfg.SelfReportSleepHours = Math.Clamp(cfg.SelfReportSleepHours, 1, 48);
            cfg.SelfReportBusyHours = Math.Clamp(cfg.SelfReportBusyHours, 1, 24);
            cfg.QuietHours ??= new TimeWindow { Start = "01:00", End = "08:00" };
            cfg.AllowWindow ??= new TimeWindow { Start = "10:00", End = "20:00" };
            cfg.AllowedIntents ??= new List<string> { "speech" };

            var known = new[] { "speech", "poke", "private", "file" };
            cfg.AllowedIntents = cfg.AllowedIntents.Where(known.Contains).Distinct().ToList();

            Env.WriteAtomic(Path.Combine(env.ConfigDir, "idea.json"),
                JsonSerializer.Serialize(cfg, AppJson.Config));

            env.Idea = cfg;   // 实时生效
            Audit(env, "idea.config", new { cfg.Enabled, cfg.IntervalMinutes, cfg.DailyLimit });

            return Results.Json(new { ok = true });
        });

        // 手动投递一条想法，走一遍六道闸（不真的执行，只判定并记账）
        app.MapPost("/api/idea/test", async (HttpContext ctx, Env env, IdeaGate gate, Contacts contacts) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            var root = body.RootElement;
            var proposal = new IdeaProposal
            {
                Intent = root.TryGetProperty("intent", out var i) ? i.GetString() ?? "speech" : "speech",
                Target = root.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "",
                Text = root.TryGetProperty("text", out var x) ? x.GetString() ?? "" : "",
                Reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "后台手动投递",
            };

            if (proposal.Target.Length == 0) return Bad("target 不能为空（private:<QQ号> 或 group:<群号>）");

            var record = gate.Judge(proposal, "test");

            var who = IdeaGate.UserOf(record.Target) is { Length: > 0 } uid ? contacts.ByQq(uid) : null;
            Audit(env, "idea.test", new { record.Intent, record.Target, record.Allowed, record.BlockedBy });

            return Results.Json(new
            {
                allowed = record.Allowed,
                blocked_by = record.BlockedBy,
                blocked_detail = record.BlockedDetail,
                presence = record.Presence,
                target = record.Target,
                target_name = who?.Name ?? "",
                counters = gate.TodayCounters(),
            });
        });

        // 运行状态：后台要看「现在是不是在想、还要多久、刚才想了什么」
        // 加权意向：还没执行的那些。数据模型改成 title + body 之后，
        // 后台不再是一堆自由文本，而是「落点 + 我要做什么」的结构化列表。
        app.MapGet("/api/idea/weights", (HttpContext ctx, Env env, IdeaChainStore chains) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var all = chains.LoadWeights().Values
                .OrderByDescending(w => w.Weight)
                .ThenByDescending(w => w.UpdatedAt)
                .ToList();

            return Results.Json(new
            {
                config = env.Idea.Weight,
                items = all.Select(w => new
                {
                    id = w.Id,
                    kind = w.Kind,
                    target = w.Target,
                    who = w.Who,
                    title = w.Title.Length > 0 ? w.Title : IdeaWeight.TitleOf(w.Kind, w.Who, w.Target),
                    items = w.Items.Select(i => new { id = i.Id, text = i.Text, ts = i.Ts }),
                    weight = Math.Round(w.Weight, 2),
                    mature = w.Weight >= env.Idea.Weight.ActThreshold,
                    rounds = w.MatureRounds,
                    topic = w.Topic,
                    origin = w.Origin,
                    reinforced = w.Reinforced,
                    created_at = w.CreatedAt,
                    updated_at = w.UpdatedAt,
                }),
                pending = all.Count(w => !w.Done),
                total = all.Count,
                legacy = all.Count(w => w.Target.Trim().Length == 0),
            });
        });

        app.MapPost("/api/idea/weights/{id}/done", (HttpContext ctx, Env env, IdeaChainStore chains, string id) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            var ok = chains.Consume(id, "admin");
            if (ok) Audit(env, "idea.weight.done", new { id });
            return Results.Json(new { ok });
        });

        // 老数据清账：把没有落点的遗留条目合并成一条，合不动的丢弃
        // 人工改判来源：自我意象（自行去做）还是对方需求（等时机）
        app.MapPost("/api/idea/weights/{id}/origin", async (HttpContext ctx, Env env, IdeaChainStore chains, string id) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            var origin = StrOf(body.RootElement, "origin");
            if (!chains.SetOrigin(id, origin)) return NotFound("没有这条加权意向");

            Audit(env, "idea.weight.origin", new { id, origin });
            return Results.Json(new { ok = true, origin = origin == "self" ? "self" : "context" });
        });

        // 重置思想链：清掉正在带的念头，下一轮不再把上一轮的内容带进去
        app.MapPost("/api/idea/chain/reset", async (HttpContext ctx, Env env, IdeaChainStore chains) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            var target = body is null ? "" : StrOf(body.RootElement, "target");

            // clearDone 是逃生口：全局了结表里的东西做完就永久不再提，
            // 万一判错了（把不该了结的记为已做完），只有这里能放回来。
            var clearDone = body is not null
                            && body.RootElement.TryGetProperty("clearDone", out var cd)
                            && cd.ValueKind == JsonValueKind.True;

            var cleared = chains.ResetCarry(target);
            var doneCleared = clearDone ? chains.ClearDone() : 0;

            Audit(env, "idea.chain.reset", new { target, cleared, doneCleared });
            return Results.Json(new { ok = true, cleared, doneCleared });
        });

        // 思想储存：发散只看这里，不再直接吃浅层碎片
        app.MapGet("/api/idea/store", (HttpContext ctx, Env env, IdeaStore store) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var all = store.Load();
            return Results.Json(new
            {
                items = all.OrderByDescending(i => i.UpdatedAt).Select(i => new
                {
                    id = i.Id,
                    kind = i.Kind,
                    thing = i.Thing,
                    grain = i.Grain,
                    open = i.Open,
                    source = i.Source,
                    state = i.State,
                    hits = i.Hits,
                    updated_at = i.UpdatedAt,
                }),
                open = all.Count(i => i.State == "open"),
                done = all.Count(i => i.State == "done"),
                seeds = all.Count(i => i.Kind == "seed"),
                residue = all.Count(i => i.Kind == "residue"),
            });
        });

        app.MapPost("/api/idea/store", async (HttpContext ctx, Env env, IdeaStore store) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            var thing = StrOf(body.RootElement, "thing");
            if (thing.Trim().Length == 0) return Bad("thing 不能为空");

            var item = store.Add(thing, StrOf(body.RootElement, "open"), StrOf(body.RootElement, "source"));
            Audit(env, "idea.store.add", new { id = item.Id, thing });
            return Results.Json(new { ok = true, id = item.Id });
        });

        app.MapDelete("/api/idea/store/{id}", (HttpContext ctx, Env env, IdeaStore store, string id) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            var n = store.Remove(id);
            Audit(env, "idea.store.remove", new { id });
            return Results.Json(new { ok = n > 0 });
        });

        // 同一件事的不同措辞并成一个桶（四个「梦想」不该各占一个）
        //
        // 先跑一遍字符统计（快、不要钱），再让模型认剩下的——换个说法的那种
        // 字符统计永远认不出来，只能靠语义。
        app.MapPost("/api/idea/weights/merge-topics", async (
            HttpContext ctx, Env env, IdeaChainStore chains, WeightAgent weights) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var merged = chains.MergeTopics();

            try
            {
                merged += await weights.MergeSameTopicsAsync(ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                Audit(env, "idea.weight.merge_topics.error", new { error = ex.Message });
            }

            Audit(env, "idea.weight.merge_topics", new { merged = merged });
            return Results.Json(new { ok = true, merged = merged });
        });

        app.MapPost("/api/idea/weights/cleanup-legacy", (HttpContext ctx, Env env, IdeaChainStore chains) =>
        {
            if (!Allowed(ctx, env)) return Deny();
            var n = chains.CleanupLegacy();
            Audit(env, "idea.weight.cleanup", new { removed = n });
            return Results.Json(new { ok = true, removed = n });
        });

        // 手动删掉一条加权意向（清账用）
        app.MapDelete("/api/idea/weights/{id}", (HttpContext ctx, Env env, IdeaChainStore chains, string id) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var map = chains.LoadWeights();
            var hit = map.Values.FirstOrDefault(w => w.Id == id);
            if (hit is null) return NotFound("没有这条加权意向");

            map.Remove(IdeaWeight.KeyOf(hit.Kind, hit.Target));
            chains.SaveWeights(map.Values.ToList());
            Audit(env, "idea.weight.delete", new { id });

            return Results.Json(new { ok = true });
        });

        //
        // 这些端点在「还没配置完」的情况下**不需要 admin token**——
        // 否则就是死锁：要先有 token 才能进后台，而 token 在后台里才能看到。
        // 安全边界靠 loopback：只接受本机请求，且配置一旦完成就立刻恢复鉴权。
        app.MapGet("/api/setup/state", (HttpContext ctx, Env env, Contacts contacts) =>
        {
            if (!IsLoopback(ctx)) return Deny();

            // 已完成配置就必须带 token，跟其它接口一致
            if (!env.NeedsSetup && !Allowed(ctx, env)) return Deny();

            return Results.Json(new
            {
                needs_setup = env.NeedsSetup,
                has_llm_key = !string.IsNullOrWhiteSpace(env.Secrets.Deepseek.ApiKey),
                has_napcat_token = !string.IsNullOrWhiteSpace(env.Secrets.Napcat.Token),
                self_id = env.Server.SelfId,
                napcat_base = env.Server.Napcat.ApiBase,
                groups = env.Server.Whitelist.Groups,
                private_list = env.Server.Whitelist.Private,
                persona_chars = File.Exists(env.PersonaFile)
                    ? new FileInfo(env.PersonaFile).Length
                    : 0,
                // 让用户能直接看到/复制后台口令，不用翻控制台
                admin_token = env.Secrets.Admin.Token,
                report_token = env.Secrets.Report.Token,
                listen_port = env.Server.Listen.Port,
                contacts = contacts.All().Count,
            });
        });

        app.MapPost("/api/setup/test-llm", async (HttpContext ctx, Env env, DeepSeekClient llm) =>
        {
            if (!IsLoopback(ctx)) return Deny();
            if (!env.NeedsSetup && !Allowed(ctx, env)) return Deny();

            if (!llm.HasKey) return Bad("还没有填 DeepSeek API Key");

            try
            {
                var res = await llm.ChatAsync("gate", new[]
                {
                    ChatMsg.System("只回两个字：可以"),
                    ChatMsg.User("连通性测试"),
                }, ct: ctx.RequestAborted);

                var text = (res.Content ?? "").Trim();
                return Results.Json(new { ok = true, reply = text.Length > 40 ? text[..40] : text });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 200);
            }
        });

        app.MapPost("/api/setup/test-napcat", async (HttpContext ctx, Env env) =>
        {
            if (!IsLoopback(ctx)) return Deny();
            if (!env.NeedsSetup && !Allowed(ctx, env)) return Deny();

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                var baseUrl = env.Server.Napcat.ApiBase.TrimEnd('/');
                if (baseUrl.Length == 0) return Bad("还没有填 NapCat API 地址");

                using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/get_login_info");
                if (!string.IsNullOrWhiteSpace(env.Secrets.Napcat.Token))
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + env.Secrets.Napcat.Token);

                using var res = await http.SendAsync(req, ctx.RequestAborted);
                var body = await res.Content.ReadAsStringAsync(ctx.RequestAborted);

                if (!res.IsSuccessStatusCode)
                    return Results.Json(new { ok = false, error = $"{(int)res.StatusCode} {res.ReasonPhrase}" }, statusCode: 200);

                return Results.Json(new { ok = true, reply = body.Length > 300 ? body[..300] : body });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: 200);
            }
        });

        // 保存引导配置：一次性写完 secrets + server +（可选）初始人设
        app.MapPost("/api/setup/apply", async (HttpContext ctx, Env env) =>
        {
            if (!IsLoopback(ctx)) return Deny();
            if (!env.NeedsSetup && !Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            var root = body.RootElement;

            // ---- secrets
            var secrets = env.Secrets;
            var llmKey = StrOf(root, "llm_api_key");
            if (llmKey.Length > 0) secrets.Deepseek.ApiKey = llmKey;

            var napcatToken = StrOf(root, "napcat_token");
            if (napcatToken.Length > 0) secrets.Napcat.Token = napcatToken;

            // ---- server
            var server = env.Server;
            var selfId = StrOf(root, "self_id").Trim();
            if (selfId.Length > 0) server.SelfId = selfId;

            var napcatBase = StrOf(root, "napcat_base").Trim();
            if (napcatBase.Length > 0) server.Napcat.ApiBase = napcatBase;

            var groups = StrListOf(root, "groups");
            if (groups is not null) server.Whitelist.Groups = groups;

            var privates = StrListOf(root, "private_list");
            if (privates is not null) server.Whitelist.Private = privates;

            var llmBase = StrOf(root, "llm_base_url").Trim();
            if (llmBase.Length > 0)
            {
                var model = env.Model;
                model.BaseUrl = llmBase;
                Env.WriteAtomic(Path.Combine(env.ConfigDir, "model.json"),
                    JsonSerializer.Serialize(model, AppJson.Config));
            }

            Env.WriteAtomic(Path.Combine(env.ConfigDir, "secrets.json"),
                JsonSerializer.Serialize(secrets, AppJson.Config));
            Env.WriteAtomic(Path.Combine(env.ConfigDir, "server.json"),
                JsonSerializer.Serialize(server, AppJson.Config));

            // ---- 初始人设（可选）
            //
            // 这一格刻意可以留空：这套系统的立场是"人格必须通过睡眠巩固长出来"。
            // 留空 = 真的从零开始；填了 = 给一个起点，之后仍会被改写。
            var persona = StrOf(root, "persona");
            var personaWritten = false;

            if (persona.Trim().Length > 0)
            {
                var segments =
                    "<!--seg:identity-->\n" + persona.Trim() + "\n<!--/seg:identity-->\n\n" +
                    "<!--seg:style-->\n说话简短，不说客套话。不确定的事就说不知道。\n<!--/seg:style-->\n\n" +
                    "<!--seg:boundaries-->\n不冒充人类去骗人。\n<!--/seg:boundaries-->\n";

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(env.PersonaFile)!);
                    Env.WriteAtomic(env.PersonaFile, segments);
                    personaWritten = true;
                }
                catch (Exception ex)
                {
                    return Bad("写人设失败：" + ex.Message);
                }
            }

            Audit(env, "setup.apply", new
            {
                has_key = !string.IsNullOrWhiteSpace(secrets.Deepseek.ApiKey),
                self_id = server.SelfId,
                groups = server.Whitelist.Groups.Count,
                persona_written = personaWritten,
            });

            return Results.Json(new
            {
                ok = true,
                needs_setup = env.NeedsSetup,
                persona_written = personaWritten,
                admin_token = secrets.Admin.Token,
                note = "配置已写入 config/，重启服务后生效（人设与白名单会在下次启动时加载）",
            });
        });

        // 「不确定度驱动」：样本少、彼此不像时只信人工；收敛后放开模型自评自动收。
        app.MapGet("/api/style", (HttpContext ctx, Env env, StyleStore style) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var state = style.State();
            var items = style.Load()
                .OrderByDescending(s => s.Verdict == "approved")
                .ThenByDescending(s => s.Weight)
                .ThenByDescending(s => s.Ts)
                .Take(100)
                .Select(s => new
                {
                    id = s.Id,
                    ts = s.Ts,
                    scope = s.Scope,
                    scope_label = Identity.Label(s.Scope),
                    source = s.Source,
                    verdict = s.Verdict,
                    text = s.Text,
                    self_score = s.SelfScore,
                    weight = Math.Round(s.Weight, 2),
                })
                .ToList();

            return Results.Json(new
            {
                config = env.Style,
                state = new
                {
                    approved = state.Approved,
                    pending = state.Pending,
                    rejected = state.Rejected,
                    consistency = state.Consistency,
                    coverage = state.Coverage,
                    uncertainty = state.Uncertainty,
                    mode = state.Mode,
                },
                items,
            });
        });

        app.MapPost("/api/style", async (HttpContext ctx, Env env, StyleStore style) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            var root = body.RootElement;
            var text = root.TryGetProperty("text", out var t) ? (t.GetString() ?? "").Trim() : "";
            var scope = root.TryGetProperty("scope", out var s) ? (s.GetString() ?? "") : "";

            if (text.Length == 0) return Bad("text 不能为空");

            var created = style.AddManual(text, scope);
            Audit(env, "style.add", new { created.Id, scope });

            return Results.Json(new { ok = true, id = created.Id });
        });

        app.MapPost("/api/style/{id}/verdict", async (HttpContext ctx, Env env, StyleStore style, string id) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            var verdict = body.RootElement.TryGetProperty("verdict", out var v)
                ? (v.GetString() ?? "") : "";

            if (!style.SetVerdict(id, verdict)) return Bad("id 或 verdict 不合法");

            Audit(env, "style.verdict", new { id, verdict });
            return Results.Json(new { ok = true, state = style.State().Mode });
        });

        app.MapDelete("/api/style/{id}", (HttpContext ctx, Env env, StyleStore style, string id) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            if (!style.Remove(id)) return NotFound("没有这条样本");
            Audit(env, "style.delete", new { id });
            return Results.Json(new { ok = true });
        });

        app.MapPut("/api/style", async (HttpContext ctx, Env env) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            StyleConfig? cfg;
            try
            {
                cfg = MergeConfig(env.Style, body.RootElement);
            }
            catch (JsonException ex)
            {
                return Bad("配置格式不正确：" + ex.Message);
            }

            if (cfg is null) return Bad("配置为空");

            cfg.TargetSamples = Math.Clamp(cfg.TargetSamples, 1, 200);
            cfg.InjectTopN = Math.Clamp(cfg.InjectTopN, 0, 12);
            cfg.ModelWeight = Math.Clamp(cfg.ModelWeight, 0, 1);
            cfg.ManualAbove = Math.Clamp(cfg.ManualAbove, 0, 1);
            cfg.AdaptiveBelow = Math.Clamp(cfg.AdaptiveBelow, 0, 1);
            if (cfg.AdaptiveBelow > cfg.ManualAbove)
                (cfg.AdaptiveBelow, cfg.ManualAbove) = (cfg.ManualAbove, cfg.AdaptiveBelow);

            Env.WriteAtomic(Path.Combine(env.ConfigDir, "style.json"),
                JsonSerializer.Serialize(cfg, AppJson.Config));

            env.Style = cfg;
            Audit(env, "style.config", new { cfg.TargetSamples, cfg.ManualAbove, cfg.AdaptiveBelow });

            return Results.Json(new { ok = true });
        });

        // 两维正交：Entity（谁，全局） / Block（上下文边界，按 scope）。
        // 现在跑 observe 模式：只记录归属与块结构，不改变行为。
        app.MapGet("/api/dialogue", (HttpContext ctx, Env env, DialogueStore store, DialogueRouter router) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var stats = router.Stats(1);
            var scopes = store.LoadAll(8).Select(s => new
            {
                scope = s.Scope,
                scope_label = Identity.Label(s.Scope),
                blocks = s.Blocks
                    .OrderByDescending(b => b.LastActiveAt)
                    .Select(b => new
                    {
                        id = b.Id,
                        state = b.State,
                        topic = b.Topic,
                        owner = b.Owner,
                        participants = b.Participants,
                        created_at = b.CreatedAt,
                        last_active_at = b.LastActiveAt,
                        message_count = b.MessageCount,
                    }),
                unresolved = s.Unresolved
                    .OrderByDescending(u => u.Ts)
                    .Take(50)
                    .Select(u => new
                    {
                        message_id = u.MessageId,
                        user_id = u.UserId,
                        ts = u.Ts,
                        confidence = Math.Round(u.Confidence, 2),
                        why = u.Why,
                    }),
            }).ToList();

            return Results.Json(new
            {
                config = env.Dialogue,
                stats = new
                {
                    window_days = stats.WindowDays,
                    total = stats.Total,
                    continue_ = stats.Continue,
                    new_block = stats.NewBlock,
                    unresolved = stats.Unresolved,
                    by_rule = stats.ByRule,
                    by_model = stats.ByModel,
                    unresolved_rate = stats.UnresolvedRate,
                    rule_rate = stats.RuleRate,
                },
                scopes,
            });
        });

        app.MapPut("/api/dialogue", async (HttpContext ctx, Env env) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            DialogueConfig? cfg;
            try
            {
                cfg = MergeConfig(env.Dialogue, body.RootElement);
            }
            catch (JsonException ex)
            {
                return Bad("配置格式不正确：" + ex.Message);
            }

            if (cfg is null) return Bad("配置为空");

            if (cfg.Mode is not ("off" or "observe" or "decide")) cfg.Mode = "observe";
            cfg.MinConfidence = Math.Clamp(cfg.MinConfidence, 0, 1);
            cfg.GapSecondsNewBlock = Math.Clamp(cfg.GapSecondsNewBlock, 10, 86400);
            cfg.MaxHotBlocks = Math.Clamp(cfg.MaxHotBlocks, 1, 32);
            cfg.HotIdleSeconds = Math.Clamp(cfg.HotIdleSeconds, 60, 86400);
            cfg.UnresolvedKeepHours = Math.Clamp(cfg.UnresolvedKeepHours, 1, 720);
            cfg.MaxBlocksPerScope = Math.Clamp(cfg.MaxBlocksPerScope, 4, 500);

            Env.WriteAtomic(Path.Combine(env.ConfigDir, "dialogue.json"),
                JsonSerializer.Serialize(cfg, AppJson.Config));

            env.Dialogue = cfg;
            Audit(env, "dialogue.config", new { cfg.Mode, cfg.MinConfidence, cfg.UseModel });

            return Results.Json(new { ok = true });
        });

        // 人工回填：把一条未归属消息挪进指定块
        app.MapPost("/api/dialogue/attach", async (HttpContext ctx, Env env, DialogueStore store) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            var root = body.RootElement;
            var scope = root.TryGetProperty("scope", out var sc) ? (sc.GetString() ?? "") : "";
            var messageId = root.TryGetProperty("message_id", out var m) ? (m.GetString() ?? "") : "";
            var blockId = root.TryGetProperty("block_id", out var b) ? (b.GetString() ?? "") : "";

            if (scope.Length == 0 || messageId.Length == 0) return Bad("scope 与 message_id 都不能为空");

            var state = store.Load(scope);

            var unresolved = state.Unresolved.FirstOrDefault(u => u.MessageId == messageId);
            if (unresolved is null) return NotFound("未归属池里没有这条消息");

            DialogueBlock? block;

            if (blockId.Length == 0)
            {
                // 新建一块来装它
                block = new DialogueBlock
                {
                    Id = "b" + Guid.NewGuid().ToString("N")[..8],
                    Scope = scope,
                    State = "open_topic",
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    LastActiveAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    MessageCount = 1,
                    Participants = new List<string> { unresolved.UserId },
                    MessageIds = new List<string> { messageId },
                    Topic = "人工归入",
                };
                state.Blocks.Add(block);
            }
            else
            {
                block = state.Blocks.FirstOrDefault(x => x.Id == blockId);
                if (block is null) return NotFound("没有这个块");

                block.MessageCount++;
                block.LastActiveAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!block.MessageIds.Contains(messageId)) block.MessageIds.Add(messageId);
                if (unresolved.UserId.Length > 0 && !block.Participants.Contains(unresolved.UserId))
                    block.Participants.Add(unresolved.UserId);
            }

            state.Unresolved.RemoveAll(u => u.MessageId == messageId);
            store.Save(state);

            Audit(env, "dialogue.attach", new { scope, messageId, block = block.Id });
            return Results.Json(new { ok = true, block_id = block.Id, by = "manual" });
        });

        app.MapGet("/api/idea/status", (HttpContext ctx, Env env, IdeaEngine engine) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var s = engine.Status();
            var rounds = engine.RecentRounds(30);

            // 重启之后内存里是空的，从文件兜底读最近几轮
            if (rounds.Count == 0)
            {
                try
                {
                    var path = Path.Combine(env.IdeaDir, "rounds.jsonl");
                    if (File.Exists(path))
                    {
                        var lines = File.ReadAllLines(path);
                        rounds = lines.Skip(Math.Max(0, lines.Length - 30))
                            .Select(l =>
                            {
                                try { return JsonSerializer.Deserialize<IdeaRound>(l, AppJson.Options); }
                                catch { return null; }
                            })
                            .Where(r => r is not null)
                            .Select(r => r!)
                            .Reverse()
                            .ToList();
                    }
                }
                catch { /* 忽略 */ }
            }

            return Results.Json(new
            {
                enabled = s.Enabled,
                running = s.Running,
                phase = s.Phase,
                phase_detail = s.PhaseDetail,
                phase_started_at = s.PhaseStartedAt,
                next_tick_at = s.NextTickAt,
                last_skip = s.LastSkip,
                rounds_last_hour = s.RoundsLastHour,
                rounds_today = s.RoundsToday,
                interval_minutes = env.Idea.IntervalMinutes,
                max_rounds_per_hour = env.Idea.Think.MaxRoundsPerHour,
                max_rounds_per_day = env.Idea.Think.MaxRoundsPerDay,
                active_within_minutes = env.Idea.Think.ActiveWithinMinutes,
                rounds = rounds.Select(r => new
                {
                    id = r.Id,
                    ts = r.Ts,
                    ms = r.Ms,
                    target = r.Target,
                    target_name = r.TargetName,
                    subject = r.Subject,
                    version = r.Version,
                    inputs = r.Inputs,
                    intent = r.Intent,
                    thoughts = r.Thoughts,
                    div_user = r.DivUser,
                    div_self = r.DivSelf,
                    ms_intent = r.MsIntent,
                    ms_div_user = r.MsDivUser,
                    ms_div_self = r.MsDivSelf,
                    ms_converge = r.MsConverge,
                    ms_weight = r.MsWeight,
                    weights = r.Weights,
                    thought = r.Thought,
                    topic = r.Topic,
                    topic_repeat = r.TopicRepeat,
                    decisions = r.Decisions,
                    boost = r.Boost,
                    boost_amount = r.BoostAmount,
                    outcome = r.Outcome,
                    picked = r.Picked,
                    why = r.Why,
                    blocked_by = r.BlockedBy,
                    detail = r.Detail,
                    spoke = r.Spoke,
                    tokens = r.Tokens,
                    candidates = r.Candidates,
                }),
            });
        });

        // 手动想一次：忽略空闲与轮次预算，用于调试
        app.MapPost("/api/idea/run-once", async (HttpContext ctx, Env env, IdeaEngine engine) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            var target = body is not null && body.RootElement.TryGetProperty("target", out var t)
                ? t.GetString() ?? "" : "";

            var (ok, message) = await engine.RunOnceAsync(
                string.IsNullOrWhiteSpace(target) ? null : target.Trim(), ctx.RequestAborted);

            Audit(env, "idea.run_once", new { target, ok });
            return Results.Json(new { ok, message });
        });

        app.MapPost("/api/napcat/send-test", async (HttpContext ctx, Env env, Sender sender) =>
        {
            if (!Allowed(ctx, env)) return Deny();

            var body = await ReadJsonAsync(ctx);
            if (body is null) return Bad("请求体不是合法 JSON");

            var scope = body.RootElement.TryGetProperty("scope", out var sc) ? sc.GetString() ?? "" : "";
            var text = body.RootElement.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";

            if (scope.Length == 0 || text.Length == 0) return Bad("scope 与 text 都不能为空");

            var result = await sender.SendTextAsync(scope, text, null, ctx.RequestAborted);
            Audit(env, "napcat.send_test", new { scope, result.Ok, result.Error });

            return Results.Json(new { ok = result.Ok, message_id = result.MessageId, error = result.Error });
        });

        app.MapGet("/health", () => Results.Json(new { ok = true }));
    }


    /// <summary>请求是不是来自本机。引导端点靠它做安全边界。</summary>
    private static bool IsLoopback(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        return ip is not null && IPAddress.IsLoopback(ip);
    }

    private static string StrOf(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "") : "";

    private static List<string>? StrListOf(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;

        return v.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => (x.GetString() ?? "").Trim())
            .Where(x => x.Length > 0)
            .Distinct()
            .ToList();
    }

    private static bool Allowed(HttpContext ctx, Env env)    {
        if (env.Server.Admin.LoopbackOnly)
        {
            var ip = ctx.Connection.RemoteIpAddress;
            if (ip is null || !IPAddress.IsLoopback(ip)) return false;
        }

        var expected = env.Secrets.Admin.Token;
        if (string.IsNullOrEmpty(expected)) return true;

        var raw = ctx.Request.Headers["Authorization"].ToString();
        var token = raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? raw[7..].Trim() : raw.Trim();
        return string.Equals(token, expected, StringComparison.Ordinal);
    }

    private static IResult Deny() =>
        Results.Json(new { ok = false, error = "unauthorized" }, statusCode: 401);

    private static IResult Bad(string error) =>
        Results.Json(new { ok = false, error }, statusCode: 400);

    private static IResult NotFound(string error) =>
        Results.Json(new { ok = false, error }, statusCode: 404);

    private static async Task<JsonDocument?> ReadJsonAsync(HttpContext ctx)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var text = await reader.ReadToEndAsync(ctx.RequestAborted);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonDocument.Parse(text);
        }
        catch
        {
            return null;
        }
    }

    private static string Str(Dictionary<string, JsonElement> d, string key) =>
        d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(Dictionary<string, JsonElement> d, string key) =>
        d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static long Long(Dictionary<string, JsonElement> d, string key) =>
        d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : 0;

    private static bool Bool(Dictionary<string, JsonElement> d, string key) =>
        d.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>把补丁**合并**进现有配置，而不是整体替换。</summary>
    private static T MergeConfig<T>(T current, JsonElement patch) where T : class
    {
        var node = JsonSerializer.SerializeToNode(current, AppJson.Config)?.AsObject()
                   ?? new System.Text.Json.Nodes.JsonObject();

        if (patch.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in patch.EnumerateObject())
                node[p.Name] = MergeValue(node[p.Name], p.Value);
        }

        return node.Deserialize<T>(AppJson.Config) ?? current;
    }

    private static System.Text.Json.Nodes.JsonNode? MergeValue(
        System.Text.Json.Nodes.JsonNode? existing, JsonElement incoming)
    {
        if (incoming.ValueKind == JsonValueKind.Object
            && existing is System.Text.Json.Nodes.JsonObject baseObj)
        {
            var merged = baseObj.DeepClone().AsObject();
            foreach (var p in incoming.EnumerateObject())
                merged[p.Name] = MergeValue(merged[p.Name], p.Value);
            return merged;
        }

        return System.Text.Json.Nodes.JsonNode.Parse(incoming.GetRawText());
    }

    private static void Audit(Env env, string action, object payload)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                action,
                payload,
            }, AppJson.Options);
            File.AppendAllText(Path.Combine(env.AuditDir, "admin.jsonl"), line + "\n");
        }
        catch { /* 审计失败不影响主流程 */ }
    }
}

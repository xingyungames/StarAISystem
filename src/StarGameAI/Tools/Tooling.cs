using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StarGameAI.Core;
using LlmToolDef = StarGameAI.Llm.ToolDef;

namespace StarGameAI.Tools;

public sealed class ToolContract
{
    public string Name { get; set; } = "";
    public int Version { get; set; } = 1;
    public string Description { get; set; } = "";
    public Dictionary<string, ToolParam> Params { get; set; } = new();
    /// <summary>返回值描述：允许写成字符串，也允许写成 {"type":..,"desc":..}。</summary>
    public JsonElement? Returns { get; set; }
    public string SideEffects { get; set; } = "none";
    public int TimeoutS { get; set; } = 5;
}

public sealed class ToolParam
{
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public JsonElement? Default { get; set; }
    public string Desc { get; set; } = "";
}

public sealed class ToolMeta
{
    public string Name { get; set; } = "";
    /// <summary>draft / pending / active / disabled</summary>
    public string Status { get; set; } = "draft";
    public int Version { get; set; } = 1;
    public string Description { get; set; } = "";
    public string LastError { get; set; } = "";
    public string RejectReason { get; set; } = "";
    public long CheckedAt { get; set; }
    public string Source { get; set; } = "manual";
}

public sealed class ToolRecord
{
    public string Name { get; set; } = "";
    public ToolMeta Meta { get; set; } = new();
    public ToolContract? Contract { get; set; }
    public string ContractRaw { get; set; } = "";
    public string Code { get; set; } = "";
    public bool HasMain => Code.Length > 0;
}

public sealed class ToolRunResult
{
    public bool Ok { get; set; }
    public JsonElement? Data { get; set; }
    public string Error { get; set; } = "";
    public int Ms { get; set; }
    public int ExitCode { get; set; }
}

/// <summary>Python 工具子系统：注册 → 自动准入门禁 → 子进程执行。</summary>
public sealed class Tooling
{
    private static readonly Regex ContractRe =
        new(@"<!--\s*contract([\s\S]*?)-->", RegexOptions.Compiled);

    private readonly Env _env;
    private readonly ILogger<Tooling> _log;
    private readonly SemaphoreSlim _concurrency;
    private readonly string _inspectorPath;

    public Tooling(Env env, ILogger<Tooling> log)
    {
        _env = env;
        _log = log;
        _concurrency = new SemaphoreSlim(Math.Max(1, env.ToolPolicy.GlobalConcurrency));
        _inspectorPath = Path.Combine(env.WorkspaceDir, "_inspector.py");
        EnsureInspector();
    }

    // -- 枚举 -------------------------------------------------------------
    public List<ToolRecord> List()
    {
        if (!Directory.Exists(_env.ToolsDir)) return new List<ToolRecord>();

        return Directory.GetDirectories(_env.ToolsDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => Load(n!))
            .Where(r => r is not null)
            .Select(r => r!)
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }

    public ToolRecord? Load(string name)
    {
        var dir = Path.Combine(_env.ToolsDir, name);
        if (!Directory.Exists(dir)) return null;

        var record = new ToolRecord { Name = name };

        var metaPath = Path.Combine(dir, "meta.json");
        if (File.Exists(metaPath))
        {
            try
            {
                record.Meta = JsonSerializer.Deserialize<ToolMeta>(File.ReadAllText(metaPath), AppJson.Options)
                              ?? new ToolMeta { Name = name };
            }
            catch { record.Meta = new ToolMeta { Name = name }; }
        }
        else
        {
            record.Meta = new ToolMeta { Name = name };
        }

        record.Meta.Name = name;

        var mdPath = Path.Combine(dir, "tool.md");
        if (File.Exists(mdPath))
        {
            record.ContractRaw = File.ReadAllText(mdPath);
            record.Contract = ParseContract(record.ContractRaw);
        }

        var pyPath = Path.Combine(dir, "main.py");
        record.Code = File.Exists(pyPath) ? File.ReadAllText(pyPath) : "";

        return record;
    }

    public static ToolContract? ParseContract(string md)
    {
        var m = ContractRe.Match(md ?? "");
        if (!m.Success) return null;

        try
        {
            return JsonSerializer.Deserialize<ToolContract>(m.Groups[1].Value, AppJson.Config);
        }
        catch
        {
            return null;
        }
    }

    // -- 准入 -------------------------------------------------------------
    public sealed class AdmitResult
    {
        public bool Ok { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>跑完三道门禁并把结果写回 meta.json。</summary>
    public async Task<AdmitResult> AdmitAsync(string name, CancellationToken ct)
    {
        var record = Load(name);
        if (record is null) return new AdmitResult { Ok = false, Reason = "工具目录不存在" };

        var meta = record.Meta;
        meta.CheckedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 门禁 1：契约
        var contract = record.Contract;
        if (contract is null)
            return await Fail(meta, name, "契约缺失或无法解析（tool.md 需要 <!--contract {...} -->）");

        if (string.IsNullOrWhiteSpace(contract.Name))
            return await Fail(meta, name, "契约缺少 name");

        if (contract.Version <= 0) contract.Version = 1;
        if (contract.TimeoutS <= 0) contract.TimeoutS = _env.ToolPolicy.TimeoutDefaultS;

        if (!record.HasMain)
            return await Fail(meta, name, "缺少 main.py");

        var py = Path.Combine(_env.ToolsDir, name, "main.py");

        // 门禁 2：静态检查
        var check = await InspectAsync(py, ct);
        if (!check.Ok)
            return await Fail(meta, name, "静态检查未通过：" + check.Reason);

        // 门禁 3：沙箱冒烟试跑
        var smoke = await RunAsync(name, "{}", ct);
        if (!smoke.Ok)
            return await Fail(meta, name, "冒烟试跑失败：" + smoke.Error);

        meta.Status = _env.ToolPolicy.AutoApprove ? "active" : "pending";
        meta.Version = contract.Version;
        meta.Description = contract.Description;
        meta.LastError = "";
        meta.RejectReason = "";
        SaveMeta(name, meta);

        _log.LogInformation("工具 {Name} 通过准入，状态 {Status}", name, meta.Status);
        return new AdmitResult { Ok = true, Reason = meta.Status };
    }

    private Task<AdmitResult> Fail(ToolMeta meta, string name, string reason)
    {
        meta.Status = "disabled";
        meta.RejectReason = reason;
        meta.LastError = reason;
        SaveMeta(name, meta);
        _log.LogWarning("工具 {Name} 未通过准入：{Reason}", name, reason);
        return Task.FromResult(new AdmitResult { Ok = false, Reason = reason });
    }

    public bool SetStatus(string name, string status)
    {
        var record = Load(name);
        if (record is null) return false;

        var meta = record.Meta;
        meta.Status = status;
        if (status == "active") meta.RejectReason = "";
        SaveMeta(name, meta);
        return true;
    }

    private void SaveMeta(string name, ToolMeta meta)
    {
        var path = Path.Combine(_env.ToolsDir, name, "meta.json");
        Env.WriteAtomic(path, JsonSerializer.Serialize(meta, AppJson.Options));
    }

    // -- 静态检查 ---------------------------------------------------------
    public async Task<AdmitResult> InspectAsync(string pyPath, CancellationToken ct)
    {
        if (!File.Exists(_inspectorPath))
            return new AdmitResult { Ok = false, Reason = "检查脚本缺失" };

        var policy = new
        {
            forbid_imports = _env.ToolPolicy.ForbidImports,
            forbid_builtins = _env.ToolPolicy.ForbidBuiltins,
            forbid_attrs = _env.ToolPolicy.ForbidAttrs,
        };
        var policyPath = Path.Combine(_env.WorkspaceDir, "_policy.json");
        File.WriteAllText(policyPath, JsonSerializer.Serialize(policy));

        var (code, stdout, stderr) = await ExecAsync(
            _env.ToolPolicy.Python,
            new[] { "-I", "-B", "-X", "utf8", _inspectorPath, pyPath, policyPath },
            _env.WorkspaceDir,
            TimeSpan.FromSeconds(20),
            null,
            ct);

        if (code != 0)
            return new AdmitResult { Ok = false, Reason = $"检查器异常（exit {code}）：{Trim(stderr, 200)}" };

        try
        {
            using var doc = JsonDocument.Parse(stdout.Trim());
            var ok = doc.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return new AdmitResult { Ok = ok, Reason = reason };
        }
        catch
        {
            return new AdmitResult { Ok = false, Reason = "检查器输出无法解析" };
        }
    }

    // -- 执行 -------------------------------------------------------------
    public async Task<ToolRunResult> RunAsync(string name, string argsJson, CancellationToken ct)
    {
        var record = Load(name);
        if (record is null) return new ToolRunResult { Ok = false, Error = "工具不存在" };
        if (!record.HasMain) return new ToolRunResult { Ok = false, Error = "缺少 main.py" };

        var timeout = Math.Clamp(
            record.Contract?.TimeoutS ?? _env.ToolPolicy.TimeoutDefaultS, 1, 120);

        await _concurrency.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        try
        {
            var sessionDir = Path.Combine(_env.WorkspaceDir, "run-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sessionDir);

            var py = Path.Combine(_env.ToolsDir, name, "main.py");
            var (code, stdout, stderr) = await ExecAsync(
                _env.ToolPolicy.Python,
                new[] { "-I", "-B", "-X", "utf8", py },
                sessionDir,
                TimeSpan.FromSeconds(timeout),
                argsJson ?? "{}",
                ct);

            sw.Stop();

            var outText = stdout.Trim();
            if (outText.Length == 0)
            {
                var err = code == 0 ? "" : $"退出码 {code}：{Trim(stderr, 300)}";
                return new ToolRunResult { Ok = code == 0, Error = err, Ms = (int)sw.ElapsedMilliseconds, ExitCode = code };
            }

            try
            {
                using var doc = JsonDocument.Parse(outText);
                var root = doc.RootElement;
                var ok = !(root.ValueKind == JsonValueKind.Object
                           && root.TryGetProperty("ok", out var o)
                           && o.ValueKind == JsonValueKind.False);

                var data = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d)
                    ? d.Clone()
                    : root.Clone();
                var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e)
                    ? e.ToString()
                    : "";

                return new ToolRunResult
                {
                    Ok = ok && code == 0,
                    Data = data,
                    Error = error,
                    Ms = (int)sw.ElapsedMilliseconds,
                    ExitCode = code,
                };
            }
            catch (JsonException)
            {
                return new ToolRunResult
                {
                    Ok = false,
                    Error = "工具输出不是合法 JSON：" + Trim(outText, 200),
                    Ms = (int)sw.ElapsedMilliseconds,
                    ExitCode = code,
                };
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new ToolRunResult { Ok = false, Error = $"执行超时（{timeout}s）", Ms = (int)sw.ElapsedMilliseconds };
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>转成模型可用的工具定义。只有 active 才出现（I7）。</summary>
    public List<LlmToolDef> ActiveDefs()
    {
        var defs = new List<LlmToolDef>();
        foreach (var record in List().Where(r => r.Meta.Status == "active" && r.Contract is not null))
        {
            var contract = record.Contract!;
            var props = new Dictionary<string, object>();
            var required = new List<string>();

            foreach (var (key, param) in contract.Params)
            {
                props[key] = new Dictionary<string, object>
                {
                    ["type"] = param.Type,
                    ["description"] = param.Desc,
                };
                if (param.Required) required.Add(key);
            }

            defs.Add(new LlmToolDef
            {
                Function = new Llm.ToolFunctionDef
                {
                    Name = contract.Name,
                    Description = contract.Description,
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = props,
                        ["required"] = required,
                    },
                },
            });
        }

        return defs;
    }

    // -- 进程执行 ---------------------------------------------------------
    private async Task<(int code, string stdout, string stderr)> ExecAsync(
        string fileName,
        IEnumerable<string> args,
        string workDir,
        TimeSpan timeout,
        string? stdin,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        // 环境变量白名单：绝不把密钥传给子进程。
        // 但 Windows 上有几个系统必需的变量（SystemRoot 等），缺了 Python 连随机数都初始化不了，
        // 所以这一小组在代码里强制保留，配置无法把它们排除掉。
        string[] essential =
        {
            "SystemRoot", "windir", "TEMP", "TMP", "PATHEXT", "COMSPEC",
            "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER",
            "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH",
        };

        psi.Environment.Clear();
        foreach (var key in essential.Concat(_env.ToolPolicy.EnvWhitelist))
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var v = Environment.GetEnvironmentVariable(key);
            if (v is not null) psi.Environment[key] = v;
        }
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        using var process = new Process { StartInfo = psi };
        process.Start();

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            await process.StandardInput.FlushAsync(ct);
        }
        process.StandardInput.Close();

        var stdoutTask = ReadCappedAsync(process.StandardOutput, _env.ToolPolicy.StdoutMaxBytes, ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, int cap, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var n = await reader.ReadAsync(buffer, ct);
            if (n <= 0) break;
            if (sb.Length + n > cap)
            {
                sb.Append(buffer, 0, Math.Max(0, cap - sb.Length));
                break;
            }
            sb.Append(buffer, 0, n);
        }

        return sb.ToString();
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];

    // -- 检查器脚本 -------------------------------------------------------
    private void EnsureInspector()
    {
        if (File.Exists(_inspectorPath)) return;

        const string script = """
            # -*- coding: utf-8 -*-
            # StarGameAI 工具静态检查器：只做语法树扫描，不执行目标代码。
            import ast, json, sys

            target = sys.argv[1]
            policy = json.load(open(sys.argv[2], encoding="utf-8"))
            forbid_imports = set(policy.get("forbid_imports") or [])
            forbid_builtins = set(policy.get("forbid_builtins") or [])
            forbid_attrs = set(policy.get("forbid_attrs") or [])

            try:
                src = open(target, encoding="utf-8").read()
            except Exception as exc:
                print(json.dumps({"ok": False, "reason": "无法读取源文件: %s" % exc}))
                sys.exit(0)

            try:
                tree = ast.parse(src)
            except SyntaxError as exc:
                print(json.dumps({"ok": False, "reason": "语法错误: %s" % exc}))
                sys.exit(0)

            hits = []
            for node in ast.walk(tree):
                if isinstance(node, ast.Import):
                    for alias in node.names:
                        root = alias.name.split(".")[0]
                        if root in forbid_imports:
                            hits.append("import " + root)
                elif isinstance(node, ast.ImportFrom):
                    if node.module:
                        root = node.module.split(".")[0]
                        if root in forbid_imports:
                            hits.append("from " + root)
                elif isinstance(node, ast.Call) and isinstance(node.func, ast.Name):
                    if node.func.id in forbid_builtins:
                        hits.append(node.func.id + "()")
                elif isinstance(node, ast.Attribute):
                    if node.attr in forbid_attrs:
                        hits.append("." + node.attr)

            print(json.dumps({"ok": not hits, "reason": ";".join(sorted(set(hits)))}, ensure_ascii=False))
            """;

        Directory.CreateDirectory(_env.WorkspaceDir);
        File.WriteAllText(_inspectorPath, script, new UTF8Encoding(false));
    }
}

using System.Text;

namespace StarGameAI.Core;

public sealed class SandboxViolation : Exception
{
    public SandboxViolation(string message) : base(message) { }
}

public sealed class SandboxEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsDir { get; set; }
    public long Size { get; set; }
    public long ModifiedAt { get; set; }
}

/// <summary>AI 的文件沙箱。内置工具与后台浏览都只能在这里活动。</summary>
public sealed class Sandbox
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly Env _env;

    public Sandbox(Env env) => _env = env;

    public string Root => _env.SandboxDir;

    public const int ReadLimitBytes = 256 * 1024;
    public const int WriteLimitBytes = 1024 * 1024;
    public const int ListLimitEntries = 200;

    /// <summary>把相对路径解析成沙箱内的绝对路径；越界抛 SandboxViolation。</summary>
    public string Resolve(string? relative)
    {
        var clean = (relative ?? "").Replace('\\', '/').Trim();
        while (clean.StartsWith("./", StringComparison.Ordinal)) clean = clean[2..];
        clean = clean.TrimStart('/');

        var rootFull = Path.GetFullPath(Root);
        var full = clean.Length == 0
            ? rootFull
            : Path.GetFullPath(Path.Combine(rootFull, clean));

        if (!full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new SandboxViolation($"路径越出沙箱范围：{relative}");

        return full;
    }

    /// <summary>转成沙箱内的相对路径（用于回显）。</summary>
    public string Relative(string fullPath)
    {
        var rootFull = Path.GetFullPath(Root);
        var full = Path.GetFullPath(fullPath);
        if (full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)) return "";
        return Path.GetRelativePath(rootFull, full).Replace('\\', '/');
    }

    public bool Exists(string relative) => File.Exists(Resolve(relative));

    public bool DirExists(string relative) => Directory.Exists(Resolve(relative));

    public List<SandboxEntry> List(string? relative)
    {
        var dir = Resolve(relative);
        if (!Directory.Exists(dir))
            throw new SandboxViolation($"目录不存在：{relative}");

        var list = new List<SandboxEntry>();

        foreach (var d in Directory.GetDirectories(dir))
        {
            var info = new DirectoryInfo(d);
            list.Add(new SandboxEntry
            {
                Name = info.Name,
                Path = Relative(d),
                IsDir = true,
                Size = 0,
                ModifiedAt = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
            });
        }

        foreach (var f in Directory.GetFiles(dir))
        {
            var info = new FileInfo(f);
            list.Add(new SandboxEntry
            {
                Name = info.Name,
                Path = Relative(f),
                IsDir = false,
                Size = info.Length,
                ModifiedAt = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
            });
        }

        return list
            .OrderByDescending(e => e.IsDir)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(ListLimitEntries)
            .ToList();
    }

    public (string text, bool truncated, long size) Read(string relative, int maxBytes = ReadLimitBytes)
    {
        var path = Resolve(relative);
        if (!File.Exists(path))
            throw new SandboxViolation($"文件不存在：{relative}");

        var info = new FileInfo(path);
        if (info.Length > maxBytes)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[maxBytes];
            var n = fs.Read(buf, 0, buf.Length);
            return (Encoding.UTF8.GetString(buf, 0, n), true, info.Length);
        }

        var text = File.ReadAllText(path, Utf8);
        return (text, false, info.Length);
    }

    public long Write(string relative, string content, int maxBytes = WriteLimitBytes)
    {
        var path = Resolve(relative);
        var bytes = Utf8.GetByteCount(content ?? "");
        if (bytes > maxBytes)
            throw new SandboxViolation($"内容过大（{bytes} 字节，上限 {maxBytes}）");

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        File.WriteAllText(path, content ?? "", Utf8);
        return new FileInfo(path).Length;
    }

    public bool Delete(string relative)
    {
        var path = Resolve(relative);
        if (Relative(path).Length == 0)
            throw new SandboxViolation("不允许删除沙箱根目录");

        if (Directory.Exists(path))
        {
            if (Directory.GetFileSystemEntries(path).Length > 0)
                throw new SandboxViolation("目录非空，拒绝删除（请先清空）");

            Directory.Delete(path);
            return true;
        }

        if (File.Exists(path))
        {
            File.Delete(path);
            return true;
        }

        return false;
    }

    public void Move(string from, string to)
    {
        var src = Resolve(from);
        var dst = Resolve(to);
        if (!File.Exists(src) && !Directory.Exists(src))
            throw new SandboxViolation($"源不存在：{from}");

        var parent = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        if (Directory.Exists(src)) Directory.Move(src, dst);
        else File.Move(src, dst, overwrite: true);
    }

    public void CreateDirectory(string relative)
    {
        Directory.CreateDirectory(Resolve(relative));
    }
}

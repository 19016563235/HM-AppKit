using System.Security.Cryptography;
using System.Text;

namespace FoxDeploy.Core;

/// <summary>
/// Opens Jingmai's official interactive installer with the requested location prefilled.
/// The caller must supply the publisher-verified payload held by IInstallerPayloadProvider.
/// The NSIS bootstrapper is extracted as data and is never executed.
/// </summary>
public sealed class JingmaiInstaller
{
    private const string ResourcePrefix = "FoxDeploy.Native.7zip.";
    private const string TargetMarker = "HM-AppKit-Install-Location";
    private static readonly (string Name, string Hash)[] ToolFiles =
    [
        ("7z.exe", "95FFF19B73EFF77C87C0D7BD32822E43FB90DD2932459F962EB532D8DFB6BA53"),
        ("7z.dll", "899CD73D7FC60E1C1C35C7DE0F5BED3DC69BFE2CECBD48014D868A347970A0B1")
    ];

    private readonly IProcessRunner _runner;
    private readonly string _workRoot;

    public JingmaiInstaller(IProcessRunner? processRunner = null, string? workRoot = null)
    {
        _runner = processRunner ?? new SystemProcessRunner();
        _workRoot = Path.GetFullPath(workRoot ?? Path.Combine(Path.GetTempPath(), "HM-AppKit-Jingmai"));
    }

    public async Task<ProcessResult> RunAsync(string verifiedInstallerPath, string targetDirectory,
        TimeSpan timeout, IProgress<string>? log, CancellationToken cancellationToken)
    {
        await using var prepared = await PrepareAsync(verifiedInstallerPath, targetDirectory, timeout, log, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // QDir::cdUp only accepts an existing parent. Create the real requested
        // application directory, never the synthetic version hint or registry state.
        Directory.CreateDirectory(prepared.TargetDirectory);
        log?.Report($"京麦需要完成官方安装向导，已预填 {prepared.TargetDirectory}。请确认位置并点击安装，完成或取消后关闭向导。");
        // Wait for the real second-stage process, rather than the short-lived NSIS bootstrapper.
        return await _runner.RunAsync(prepared.Request, log, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Extracts and validates the official container without executing vendor code.</summary>
    public async Task<JingmaiPreparedInstaller> PrepareAsync(string verifiedInstallerPath, string targetDirectory,
        TimeSpan timeout, IProgress<string>? log, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedInstallerPath);
        if (!Path.IsPathFullyQualified(verifiedInstallerPath) || !File.Exists(verifiedInstallerPath))
            throw new ArgumentException("京麦安装包必须是已校验且存在的完整文件路径。", nameof(verifiedInstallerPath));
        var target = NormalizeTarget(targetDirectory);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_workRoot);
        var workspace = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var stage = Path.Combine(workspace, "stage");
        var tools = Path.Combine(workspace, "tools");
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(tools);
        var prepared = new JingmaiPreparedInstaller(workspace, stage, target,
            CreateNativeRequest(stage, target, timeout), log);
        try
        {
            log?.Report("正在只读解包京麦官方安装包，准备预填安装位置。");
            foreach (var (name, hash) in ToolFiles)
            {
                var destination = Path.Combine(tools, name);
                await WriteResourceAsync(name, destination, cancellationToken).ConfigureAwait(false);
                await using var stream = File.OpenRead(destination);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!actual.Equals(hash, StringComparison.Ordinal))
                    throw new InvalidDataException("内置京麦解包组件完整性检查失败。");
            }
            foreach (var name in new[] { "License.txt", "NOTICE.txt" })
                await WriteResourceAsync(name, Path.Combine(tools, name), cancellationToken).ConfigureAwait(false);

            // Keep the trusted extraction helper and its DLL immutable while it is in use.
            using var executableLock = File.Open(Path.Combine(tools, "7z.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var libraryLock = File.Open(Path.Combine(tools, "7z.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
            var extractionTimeout = TimeSpan.FromMinutes(5);
            var listing = await _runner.RunAsync(new ProcessRequest(Path.Combine(tools, "7z.exe"),
                ["l", "-tNsis", "-slt", "-ba", "-sccUTF-8", "--", verifiedInstallerPath], extractionTimeout)
            { WorkingDirectory = tools }, null, cancellationToken).ConfigureAwait(false);
            EnsureExtractionFinished(listing, cancellationToken);
            ValidateArchiveListing(listing.StandardOutput);
            var extraction = await _runner.RunAsync(new ProcessRequest(Path.Combine(tools, "7z.exe"),
                ["x", "-tNsis", "-y", "-sccUTF-8", "-o" + stage, "--", verifiedInstallerPath], extractionTimeout)
            { WorkingDirectory = tools }, log, cancellationToken).ConfigureAwait(false);
            EnsureExtractionFinished(extraction, cancellationToken);
            ValidatePreparedStage(stage);
            return prepared;
        }
        catch
        {
            await prepared.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static ProcessRequest CreateNativeRequest(string stageDirectory, string targetDirectory, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageDirectory);
        if (!Path.IsPathFullyQualified(stageDirectory)) throw new ArgumentException("京麦准备目录必须是完整路径。", nameof(stageDirectory));
        var stage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stageDirectory));
        var target = NormalizeTarget(targetDirectory);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        // The vendor treats this value as a version-directory hint, then calls QDir::cdUp
        // before populating its location field. The marker is only an argument: no fake
        // installation directory, registry entry or marker file is created.
        var nativeHint = Path.Combine(target, TargetMarker);
        var encodedHint = Convert.ToBase64String(Encoding.UTF8.GetBytes(nativeHint));
        return new ProcessRequest(Path.Combine(stage, "JMInstaller.exe"),
            ["/update=install", "/installpath=" + encodedHint, "/localzippath=" + Path.Combine(stage, "install.7z")], timeout, ProtectInstaller: true)
        {
            WorkingDirectory = stage,
            ShowWindow = true
        };
    }

    public static void ValidateArchiveListing(string listing)
    {
        var paths = listing.Replace("\r", "", StringComparison.Ordinal).Split('\n')
            .Where(line => line.StartsWith("Path = ", StringComparison.Ordinal)).Select(line => line[7..]).ToArray();
        if (paths.Length == 0 || paths.Length > 5000) throw new InvalidDataException("京麦安装包文件目录不完整或格式不受支持。");
        foreach (var path in paths)
        {
            var parts = path.Replace('/', '\\').Split('\\');
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':') ||
                path.Any(char.IsControl) || parts.Any(part => part.Length == 0 || part is ".." or "." || part.EndsWith(' ') || part.EndsWith('.')))
                throw new InvalidDataException("京麦安装包包含不安全的解包路径。");
        }
        if (!paths.Contains("JMInstaller.exe", StringComparer.OrdinalIgnoreCase) || !paths.Contains("install.7z", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("京麦安装包结构发生变化，未找到官方第二阶段安装组件。");
    }

    private static void ValidatePreparedStage(string stage)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stage)) + Path.DirectorySeparatorChar;
        var pending = new Stack<string>();
        pending.Push(stage);
        var count = 0;
        long bytes = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("京麦准备目录包含异常路径或链接。");
                if (++count > 5000) throw new InvalidDataException("京麦安装包解包文件数超出预期。");
                if (entry is DirectoryInfo) pending.Push(entry.FullName);
                else
                {
                    bytes += ((FileInfo)entry).Length;
                    if (bytes > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("京麦安装包解包大小超出预期。");
                }
            }
        }
        using var native = File.OpenRead(Path.Combine(stage, "JMInstaller.exe"));
        if (native.ReadByte() != 'M' || native.ReadByte() != 'Z') throw new InvalidDataException("京麦第二阶段安装程序无效。");
        using var archive = File.OpenRead(Path.Combine(stage, "install.7z"));
        ReadOnlySpan<byte> signature = [0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c];
        Span<byte> actual = stackalloc byte[6];
        if (archive.Read(actual) != 6 || !actual.SequenceEqual(signature)) throw new InvalidDataException("京麦内层安装归档无效。");
    }

    private static string NormalizeTarget(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!Path.IsPathFullyQualified(target) || target.Length < 4 || target[1] != ':' || target.Any(char.IsControl) || target.Contains('"'))
            throw new ArgumentException("京麦安装目录必须是本机完整目录路径。", nameof(target));
        var result = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (result.Equals(Path.GetPathRoot(result), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("京麦安装目录不能是磁盘根目录。", nameof(target));
        return result;
    }

    private static async Task WriteResourceAsync(string name, string destination, CancellationToken cancellationToken)
    {
        await using var source = typeof(JingmaiInstaller).Assembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidOperationException("缺少内置京麦解包组件，请重新构建完整应用。");
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureExtractionFinished(ProcessResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.CancellationRequested) throw new OperationCanceledException("京麦解包已取消。", cancellationToken);
        if (result.TimedOut) throw new IOException("京麦安装包解包超时，未启动官方安装程序。");
        if (result.ExitCode != 0) throw new InvalidDataException($"京麦安装包无法解包（{result.ExitCode}）：{result.StandardError}");
    }
}

/// <summary>A task-owned extraction workspace. Disposing it never touches the target application directory.</summary>
public sealed class JingmaiPreparedInstaller : IAsyncDisposable
{
    private readonly string _workspace;
    private readonly IProgress<string>? _log;
    private bool _disposed;

    internal JingmaiPreparedInstaller(string workspace, string stageDirectory, string targetDirectory, ProcessRequest request, IProgress<string>? log)
    {
        _workspace = Path.GetFullPath(workspace);
        StageDirectory = stageDirectory;
        TargetDirectory = targetDirectory;
        Request = request;
        _log = log;
    }

    public string StageDirectory { get; }
    public string TargetDirectory { get; }
    public ProcessRequest Request { get; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // The native wizard can briefly leave filesystem handles open after exiting.
        // Only retry this task's cleanup; never terminate the application to release them.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (Directory.Exists(_workspace)) DeleteOwnedDirectory(_workspace, _workspace + Path.DirectorySeparatorChar);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3)
                    _log?.Report($"京麦临时准备文件仍被占用或无权清理，已保留以免影响运行中的程序：{_workspace}（{InstallerNetwork.SanitizeMessage(ex.Message)}）");
                else await Task.Delay(TimeSpan.FromMilliseconds(attempt * 300)).ConfigureAwait(false);
            }
        }
    }

    private static void DeleteOwnedDirectory(string directory, string prefix)
    {
        var absolute = Path.GetFullPath(directory);
        if (!absolute.Equals(Path.TrimEndingDirectorySeparator(prefix), StringComparison.OrdinalIgnoreCase) &&
            !absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("拒绝清理京麦准备目录以外的路径。");
        if ((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(absolute);
            return;
        }
        foreach (var entry in new DirectoryInfo(absolute).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Remove the link itself, without traversing a destination outside this workspace.
                if (entry is DirectoryInfo) Directory.Delete(entry.FullName);
                else File.Delete(entry.FullName);
            }
            else if (entry is DirectoryInfo) DeleteOwnedDirectory(entry.FullName, prefix);
            else
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
                File.Delete(entry.FullName);
            }
        }
        Directory.Delete(absolute);
    }
}

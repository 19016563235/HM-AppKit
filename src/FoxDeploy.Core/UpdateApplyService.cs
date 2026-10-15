using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FoxDeploy.Core;

/// <summary>
/// Runs in a copy of the verified replacement executable. Only declared release files are replaced;
/// application configuration, installer caches, unknown files and selected catalog overrides survive.
/// It never kills the old application or deletes the application directory.
/// </summary>
public sealed partial class UpdateApplyService
{
    private const string ManifestName = "apply.json";
    private const string OwnerName = ".hm-appkit-update.json";
    private const string JournalName = "apply-state.json";
    private const int MaxFiles = 512;
    private readonly UpdateApplyOptions _options;
    private readonly string _stagingRoot;
    private readonly string _currentExecutable;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public UpdateApplyService(UpdateApplyOptions? options = null)
    {
        _options = options ?? new();
        _stagingRoot = Full(_options.StagingRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HM AppKit", "Updates"));
        _currentExecutable = Full(_options.CurrentExecutablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序路径。"));
        if (_options.ParentExitTimeout <= TimeSpan.Zero || _options.ParentExitTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(options), "等待退出的时间无效。");
    }

    public async Task<string> CreateManifestAsync(UpdateApplyPreparation preparation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        var stage = ValidateStage(preparation.StageDirectory);
        var target = Full(preparation.TargetDirectory);
        ValidateSeparateRoots(stage, target);
        NoReparse(target);
        var executableRelative = Relative(preparation.ExecutableRelativePath);
        if (!Equal(executableRelative, "HM AppKit.exe")) throw Invalid("更新入口必须是 HM AppKit.exe。");
        var targetExecutable = Under(target, executableRelative);
        if (!Equal(_currentExecutable, targetExecutable) || !File.Exists(targetExecutable))
            throw Invalid("更新目录必须是当前运行的 HM AppKit 所在目录。");
        var version = ParseVersion(preparation.TargetVersion);
        if (version <= ParseVersion(ReadVersion(targetExecutable))) throw Invalid("更新版本必须高于当前安装版本。");
        var archiveHash = ValidHash(preparation.ArchiveSha256);
        var archive = Under(stage, "package.zip");
        RequireRegularFile(archive);
        if (!Equal(await HashAsync(archive, cancellationToken), archiveHash)) throw Invalid("更新压缩包校验失败。");
        var payload = Under(stage, "payload");
        if (!Directory.Exists(payload)) throw Invalid("更新文件目录不存在。");
        NoReparse(payload);
        var helper = Under(payload, executableRelative);
        RequireRegularFile(helper);
        if (ParseVersion(ReadVersion(helper)) != version) throw Invalid("更新程序版本与发行版本不一致。");
        if (preparation.ManagedFiles.Count is < 1 or > MaxFiles) throw Invalid("更新文件数量无效。");
        var preserve = preparation.PreserveRelativePaths.Select(Relative).ToArray();
        if (preserve.Any(path => !Equal(path, "Data/catalog.json")) || preserve.Distinct(StringComparer.OrdinalIgnoreCase).Count() != preserve.Length)
            throw Invalid("只能保留用户自定义的 Data/catalog.json。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<UpdateApplyFile>();
        long totalBytes = 0;
        foreach (var raw in preparation.ManagedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Relative(raw);
            if (!Managed(relative) || !names.Add(relative)) throw Invalid("更新清单包含不支持或重复的文件。");
            var source = Under(payload, relative);
            RequireRegularFile(source);
            var length = new FileInfo(source).Length;
            if (length <= 0 || length > 512L * 1024 * 1024 || (totalBytes += length) > 1024L * 1024 * 1024)
                throw Invalid("更新文件大小无效。");
            if (preserve.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
            var destination = Under(target, relative);
            NoReparse(destination);
            if (Directory.Exists(destination)) throw Invalid("安装目录中存在同名文件夹。");
            files.Add(new(relative, length, await HashAsync(source, cancellationToken),
                File.Exists(destination) ? await HashAsync(destination, cancellationToken) : null));
        }
        if (!files.Any(file => Equal(file.RelativePath, executableRelative))) throw Invalid("更新清单缺少主程序。");
        var id = Path.GetFileName(stage);
        var manifestPath = Under(stage, ManifestName);
        using var current = Process.GetCurrentProcess();
        if (File.Exists(manifestPath) || File.Exists(Under(stage, OwnerName)))
        {
            var existing = await ReadAndValidateAsync(manifestPath, cancellationToken);
            if (File.Exists(Under(stage, JournalName)) || !Equal(existing.TargetDirectory, target) ||
                existing.Parent.ProcessId != current.Id || existing.Parent.StartTimeUtc != current.StartTime.ToUniversalTime() ||
                ParseVersion(existing.TargetVersion) != version || !Equal(existing.ArchiveSha256, archiveHash) ||
                !existing.Files.SequenceEqual(files) || !existing.PreservedFiles.SequenceEqual(preserve, StringComparer.OrdinalIgnoreCase))
                throw Invalid("此更新暂存目录已创建过不同的安装事务，请重新下载更新。");
            return manifestPath;
        }
        if (File.Exists(Under(stage, JournalName))) throw Invalid("更新事务缺少有效清单，请重新下载更新。");
        var manifest = new UpdateApplyManifest
        {
            Id = id, Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), StageDirectory = stage,
            TargetDirectory = target, ExecutableRelativePath = executableRelative, TargetVersion = preparation.TargetVersion,
            ArchiveSha256 = archiveHash, Parent = new(current.Id, current.StartTime.ToUniversalTime(), targetExecutable),
            Files = files.ToArray(), PreservedFiles = preserve
        };
        await WriteJsonAsync(manifestPath, manifest, cancellationToken);
        await WriteJsonAsync(Under(stage, OwnerName), new UpdateApplyOwner(1, id, manifest.Nonce,
            await HashAsync(manifestPath, cancellationToken)), cancellationToken);
        return manifestPath;
    }

    public async Task<UpdateApplyResult> ApplyAsync(string manifestPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var manifest = await ReadAndValidateAsync(manifestPath, cancellationToken);
        var executable = Under(manifest.TargetDirectory, manifest.ExecutableRelativePath);
        var helper = Under(manifest.StageDirectory, "payload/" + manifest.ExecutableRelativePath);
        if (!Equal(helper, _currentExecutable)) throw Invalid("更新只能由已验证的暂存程序执行。");
        var executableFile = manifest.Files.Single(file => Equal(file.RelativePath, manifest.ExecutableRelativePath));
        if (!Equal(await HashAsync(helper, cancellationToken), executableFile.Sha256) ||
            ParseVersion(ReadVersion(helper)) != ParseVersion(manifest.TargetVersion))
            throw Invalid("更新助手已被修改。");
        progress?.Report("等待 HM AppKit 退出…");
        if (_options.WaitForParentExitAsync is { } wait) await wait(manifest.Parent, cancellationToken);
        else await WaitForParentAsync(manifest.Parent, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        NoReparse(manifest.TargetDirectory);
        var lockPath = Under(manifest.TargetDirectory, ".hm-appkit-update.lock");
        NoReparse(lockPath);
        using var applyLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        var statePath = Under(manifest.StageDirectory, JournalName);
        var work = Under(manifest.TargetDirectory, ".hm-appkit-update-" + manifest.Id);
        var journal = File.Exists(statePath) ? ReadJson<UpdateApplyJournal>(statePath) : null;
        if (journal is not null && (journal.Id != manifest.Id || journal.Nonce != manifest.Nonce)) throw Invalid("更新事务标识不一致。");
        if (journal?.State == "Applied")
        {
            await VerifyInstalledAsync(manifest, cancellationToken);
            CleanupWork(manifest, work);
            return new(true, true, true, false, executable, "此版本已经安装。");
        }
        if (journal?.State == "RolledBack")
            return new(false, false, true, false, executable, journal.Message);
        if (journal is not null && journal.State is not "Applying" and not "RollbackFailed") throw Invalid("未知的更新事务状态。");
        if (journal?.State is "Applying" or "RollbackFailed")
        {
            progress?.Report("恢复上次中断的更新…");
            var recovery = await RollbackAsync(manifest, work);
            var message = recovery ? "上次更新中断，已恢复原版本。请重新下载更新。" : "上次更新中断，部分文件未能恢复；备份已保留。";
            await WriteJsonAsync(statePath, new UpdateApplyJournal(manifest.Id, manifest.Nonce, recovery ? "RolledBack" : "RollbackFailed", message), CancellationToken.None);
            if (recovery) CleanupWork(manifest, work);
            return new(false, false, recovery, false, executable, message);
        }

        // All payloads and original versions are checked before the first installed-file mutation.
        await VerifyReadyAsync(manifest, cancellationToken);
        if (Directory.Exists(work) || File.Exists(work)) throw Invalid("更新备份目录已存在，未覆盖其中内容。");
        Directory.CreateDirectory(work);
        await WriteJsonAsync(Under(work, OwnerName), new UpdateApplyOwner(1, manifest.Id, manifest.Nonce, ""), cancellationToken);
        var applying = false;
        try
        {
            progress?.Report("准备更新文件…");
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pending = Under(work, "pending/" + file.RelativePath);
                MakeParents(pending, work);
                await CopyNewAsync(Under(manifest.StageDirectory, "payload/" + file.RelativePath), pending, cancellationToken);
                if (!Equal(await HashAsync(pending, cancellationToken), file.Sha256)) throw Invalid("复制后的更新文件校验失败。");
            }
            await VerifyOriginalsAsync(manifest, cancellationToken);
            await WriteJsonAsync(statePath, new UpdateApplyJournal(manifest.Id, manifest.Nonce, "Applying"), cancellationToken);
            applying = true;
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("更新 " + file.RelativePath + "…");
                var destination = Under(manifest.TargetDirectory, file.RelativePath);
                var pending = Under(work, "pending/" + file.RelativePath);
                var backup = Under(work, "backup/" + file.RelativePath);
                NoReparse(destination);
                MakeParents(destination, manifest.TargetDirectory);
                if (file.OriginalSha256 is not null)
                {
                    MakeParents(backup, work);
                    File.Replace(pending, destination, backup, ignoreMetadataErrors: true);
                }
                else File.Move(pending, destination);
            }
            await VerifyInstalledAsync(manifest, CancellationToken.None);
            await WriteJsonAsync(statePath, new UpdateApplyJournal(manifest.Id, manifest.Nonce, "Applied"), CancellationToken.None);
            CleanupWork(manifest, work);
            return new(true, false, true, false, executable, "更新完成。");
        }
        catch (Exception exception)
        {
            try { progress?.Report("更新未完成，正在恢复原版本…"); }
            catch (Exception) { /* A broken progress consumer must not prevent rollback. */ }
            var restored = !applying || await RollbackAsync(manifest, work);
            var canceled = exception is OperationCanceledException;
            var message = (canceled ? "更新已取消。" : "更新未完成：" + exception.Message) +
                (restored ? " 原版本已保留。" : " 部分文件无法恢复，备份已保留在 " + work + "。");
            await WriteJsonAsync(statePath, new UpdateApplyJournal(manifest.Id, manifest.Nonce, restored ? "RolledBack" : "RollbackFailed", message), CancellationToken.None);
            if (restored) CleanupWork(manifest, work);
            return new(false, false, restored, canceled, executable, message);
        }
    }

    private async Task<UpdateApplyManifest> ReadAndValidateAsync(string manifestPath, CancellationToken token)
    {
        var path = Full(manifestPath);
        var stage = ValidateStage(Path.GetDirectoryName(path) ?? "");
        if (!Equal(path, Under(stage, ManifestName))) throw Invalid("更新清单文件名无效。");
        var owner = ReadJson<UpdateApplyOwner>(Under(stage, OwnerName));
        if (owner.Schema != 1 || owner.Id != Path.GetFileName(stage) || !NonceRegex().IsMatch(owner.Nonce) ||
            !Equal(await HashAsync(path, token), ValidHash(owner.ManifestSha256))) throw Invalid("更新清单所有权校验失败。");
        var manifest = ReadJson<UpdateApplyManifest>(path);
        if (manifest.Schema != 1 || manifest.Id != owner.Id || manifest.Nonce != owner.Nonce || !Equal(Full(manifest.StageDirectory), stage))
            throw Invalid("更新清单标识无效。");
        var target = Full(manifest.TargetDirectory);
        ValidateSeparateRoots(stage, target);
        if (!Equal(target, manifest.TargetDirectory) || !Directory.Exists(target)) throw Invalid("更新目标目录无效。");
        NoReparse(target);
        if (!Equal(Relative(manifest.ExecutableRelativePath), "HM AppKit.exe") ||
            !Equal(Full(manifest.Parent.ExecutablePath), Under(target, manifest.ExecutableRelativePath)) || manifest.Parent.ProcessId <= 0 ||
            manifest.Parent.StartTimeUtc.Kind != DateTimeKind.Utc || manifest.Parent.StartTimeUtc > DateTime.UtcNow.AddMinutes(1))
            throw Invalid("原程序身份无效。");
        _ = ParseVersion(manifest.TargetVersion);
        _ = ValidHash(manifest.ArchiveSha256);
        var archive = Under(stage, "package.zip");
        RequireRegularFile(archive);
        if (!Equal(await HashAsync(archive, token), manifest.ArchiveSha256)) throw Invalid("更新压缩包已被修改。");
        if (manifest.Files.Length is < 1 or > MaxFiles || manifest.PreservedFiles.Length > 1 ||
            manifest.PreservedFiles.Any(p => !Equal(Relative(p), "Data/catalog.json"))) throw Invalid("更新文件列表无效。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var file in manifest.Files)
        {
            var relative = Relative(file.RelativePath);
            if (!Equal(relative, file.RelativePath) || !Managed(relative) || !paths.Add(relative) ||
                manifest.PreservedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase) || file.Length <= 0 ||
                file.Length > 512L * 1024 * 1024 || (bytes += file.Length) > 1024L * 1024 * 1024) throw Invalid("更新文件描述无效。");
            _ = ValidHash(file.Sha256);
            if (file.OriginalSha256 is not null) _ = ValidHash(file.OriginalSha256);
        }
        if (!paths.Contains("HM AppKit.exe")) throw Invalid("更新清单缺少主程序。");
        return manifest;
    }

    private async Task WaitForParentAsync(UpdateApplyProcessIdentity identity, CancellationToken token)
    {
        Process process;
        try { process = Process.GetProcessById(identity.ProcessId); }
        catch (ArgumentException) { return; }
        using (process)
        {
            try
            {
                if (process.HasExited) return;
                if (process.StartTime.ToUniversalTime() != identity.StartTimeUtc ||
                    !Equal(Full(process.MainModule?.FileName ?? ""), identity.ExecutablePath))
                    throw Invalid("原程序的进程身份已改变；更新已停止。");
            }
            catch (InvalidOperationException) when (process.HasExited) { return; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_options.ParentExitTimeout);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new IOException("HM AppKit 尚未退出；没有修改安装文件。"); }
        }
    }

    private static async Task VerifyReadyAsync(UpdateApplyManifest manifest, CancellationToken token)
    {
        foreach (var file in manifest.Files)
        {
            var source = Under(manifest.StageDirectory, "payload/" + file.RelativePath);
            RequireRegularFile(source);
            if (new FileInfo(source).Length != file.Length || !Equal(await HashAsync(source, token), file.Sha256))
                throw Invalid("更新文件已被修改：" + file.RelativePath);
        }
        await VerifyOriginalsAsync(manifest, token);
    }

    private static async Task VerifyOriginalsAsync(UpdateApplyManifest manifest, CancellationToken token)
    {
        foreach (var file in manifest.Files)
        {
            var path = Under(manifest.TargetDirectory, file.RelativePath);
            NoReparse(path);
            if (Directory.Exists(path) || (file.OriginalSha256 is null ? File.Exists(path) :
                !File.Exists(path) || !Equal(await HashAsync(path, token), file.OriginalSha256)))
                throw Invalid("安装目录在准备更新后发生变化：" + file.RelativePath);
        }
    }

    private static async Task VerifyInstalledAsync(UpdateApplyManifest manifest, CancellationToken token)
    {
        foreach (var file in manifest.Files)
        {
            var path = Under(manifest.TargetDirectory, file.RelativePath);
            RequireRegularFile(path);
            if (new FileInfo(path).Length != file.Length || !Equal(await HashAsync(path, token), file.Sha256))
                throw Invalid("已安装的更新文件校验失败：" + file.RelativePath);
        }
    }

    private static async Task<bool> RollbackAsync(UpdateApplyManifest manifest, string work)
    {
        try { ValidateWork(manifest, work); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
        var restored = true;
        foreach (var file in manifest.Files.Reverse())
        {
            try
            {
                var target = Under(manifest.TargetDirectory, file.RelativePath);
                var backup = Under(work, "backup/" + file.RelativePath);
                NoReparse(target); NoReparse(backup);
                if (File.Exists(backup))
                {
                    if (file.OriginalSha256 is null || !Equal(await HashAsync(backup, CancellationToken.None), file.OriginalSha256))
                        throw Invalid("备份校验失败。");
                    var current = File.Exists(target) ? await HashAsync(target, CancellationToken.None) : null;
                    if (current is null) { MakeParents(target, manifest.TargetDirectory); File.Move(backup, target); }
                    else if (Equal(current, file.Sha256)) File.Replace(backup, target, null, ignoreMetadataErrors: true);
                    else if (Equal(current, file.OriginalSha256)) File.Delete(backup);
                    else throw Invalid("更新后的目标文件又被修改，保留备份。");
                }
                else if (file.OriginalSha256 is null)
                {
                    if (!File.Exists(target)) continue;
                    if (!Equal(await HashAsync(target, CancellationToken.None), file.Sha256)) throw Invalid("新增文件已被修改。");
                    File.Delete(target);
                }
                else if (!File.Exists(target) || !Equal(await HashAsync(target, CancellationToken.None), file.OriginalSha256))
                    throw Invalid("原文件和有效备份均不可用。");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { restored = false; }
        }
        return restored;
    }

    private static void CleanupWork(UpdateApplyManifest manifest, string work)
    {
        if (!Directory.Exists(work)) return;
        try
        {
            ValidateWork(manifest, work);
            foreach (var file in manifest.Files)
                foreach (var prefix in new[] { "pending/", "backup/" })
                {
                    var path = Under(work, prefix + file.RelativePath);
                    NoReparse(path);
                    if (File.Exists(path)) File.Delete(path);
                    DeleteEmptyParents(Path.GetDirectoryName(path)!, work);
                }
            File.Delete(Under(work, OwnerName));
            Directory.Delete(work, recursive: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        { /* A locked/unknown backup is left intact. Never recursively delete an installation path. */ }
    }

    private static void ValidateWork(UpdateApplyManifest manifest, string work)
    {
        NoReparse(work);
        if (!Equal(work, Under(manifest.TargetDirectory, ".hm-appkit-update-" + manifest.Id))) throw Invalid("备份目录无效。");
        var owner = ReadJson<UpdateApplyOwner>(Under(work, OwnerName));
        if (owner.Schema != 1 || owner.Id != manifest.Id || owner.Nonce != manifest.Nonce) throw Invalid("备份目录所有权无效。");
    }

    private string ValidateStage(string path)
    {
        var stage = Full(path);
        if (!Equal(Path.GetDirectoryName(stage) ?? "", _stagingRoot) || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _))
            throw Invalid("更新暂存目录必须位于 HM AppKit 的专用 Updates 目录。");
        NoReparse(stage);
        if (!Directory.Exists(stage)) throw Invalid("更新暂存目录不存在。");
        return stage;
    }

    private static void ValidateSeparateRoots(string stage, string target)
    {
        if (Equal(target, Path.GetPathRoot(target) ?? "") || IsWithin(stage, target) || IsWithin(target, stage))
            throw Invalid("更新暂存目录和安装目录必须独立，安装目录不能是磁盘根目录。");
    }

    private string ReadVersion(string path) => _options.ExecutableVersionReader?.Invoke(path)
        ?? FileVersionInfo.GetVersionInfo(path).ProductVersion ?? throw Invalid("无法读取程序版本。");

    private static Version ParseVersion(string? text)
    {
        var value = text?.Trim().TrimStart('v', 'V').Split('+')[0];
        if (value is null || !VersionRegex().IsMatch(value) || !Version.TryParse(value, out var parsed)) throw Invalid("程序版本格式无效。");
        return new(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
    }

    private static bool Managed(string relative) => Equal(relative, "HM AppKit.exe") || Equal(relative, "README.md") ||
        Equal(relative, "Data/catalog.json") ||
        (relative.StartsWith("licenses/", StringComparison.OrdinalIgnoreCase) && relative.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) ||
        ((relative.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("catalog/", StringComparison.OrdinalIgnoreCase)) && relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) ||
        (relative.StartsWith("evidence/", StringComparison.OrdinalIgnoreCase) &&
         (relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase)));

    private static string Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Length > 240) throw Invalid("更新文件路径无效。");
        var parts = path.Replace('\\', '/').Split('/');
        foreach (var part in parts)
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':') || ReservedNameRegex().IsMatch(part))
                throw Invalid("更新文件路径包含不安全的名称。");
        return string.Join('/', parts);
    }

    private static string Under(string root, string relative)
    {
        var result = Full(Path.Combine(root, Relative(relative).Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(result, root) || Equal(result, root)) throw Invalid("更新路径超出允许目录。");
        return result;
    }
    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Equal(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool IsWithin(string path, string root) => Equal(path, root) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string ValidHash(string hash) => HashRegex().IsMatch(hash) ? hash.ToUpperInvariant() : throw Invalid("更新校验值无效。");
    private static InvalidDataException Invalid(string message) => new(message);

    private static void NoReparse(string path)
    {
        for (string? current = Full(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw Invalid("更新路径不能包含链接或重解析点。");
        }
    }

    private static void RequireRegularFile(string path)
    {
        NoReparse(path);
        if (!File.Exists(path) || Directory.Exists(path)) throw Invalid("更新文件不存在：" + Path.GetFileName(path));
    }

    private static T ReadJson<T>(string path)
    {
        RequireRegularFile(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw Invalid("更新记录过大。");
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? throw Invalid("更新记录为空。"); }
        catch (JsonException e) { throw new InvalidDataException("更新记录格式无效。", e); }
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken token)
    {
        NoReparse(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, token);
                await stream.FlushAsync(token);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        RequireRegularFile(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    private static async Task CopyNewAsync(string source, string destination, CancellationToken token)
    {
        RequireRegularFile(source); NoReparse(destination);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, token);
        await output.FlushAsync(token);
    }

    private static void MakeParents(string file, string root)
    {
        if (!IsWithin(file, root)) throw Invalid("更新目录超出范围。");
        NoReparse(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        NoReparse(file);
    }

    private static void DeleteEmptyParents(string path, string stop)
    {
        while (!Equal(path, stop) && IsWithin(path, stop))
        {
            NoReparse(path);
            if (Directory.Exists(path))
            {
                if (Directory.EnumerateFileSystemEntries(path).Any()) return;
                Directory.Delete(path, recursive: false);
            }
            path = Path.GetDirectoryName(path)!;
        }
    }

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex HashRegex();
    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex NonceRegex();
    [GeneratedRegex("^\\d+\\.\\d+(?:\\.\\d+){0,2}$", RegexOptions.CultureInvariant)] private static partial Regex VersionRegex();
    [GeneratedRegex("^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex ReservedNameRegex();
}

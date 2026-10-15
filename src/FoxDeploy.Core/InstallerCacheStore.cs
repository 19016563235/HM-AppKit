using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FoxDeploy.Core;

internal sealed record InstallerCacheEntry
{
    public int SchemaVersion { get; init; } = 1;
    public string OwnerId { get; init; } = "";
    public string AppId { get; init; } = "";
    public string SourceFingerprint { get; init; } = "";
    public string SourceProfileFingerprint { get; init; } = "";
    public string PayloadRelativePath { get; init; } = "";
    public string OriginalFileName { get; init; } = "";
    public string ResolvedUrl { get; init; } = "";
    public string FinalUrl { get; init; } = "";
    public string? ETag { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public string? MetadataETag { get; init; }
    public DateTimeOffset? MetadataLastModified { get; init; }
    public string? MetadataFinalUrl { get; init; }
    public string? ExpectedSha256 { get; init; }
    public string Sha256 { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string? Version { get; init; }
    public long Bytes { get; init; }
    public DateTimeOffset DownloadedAt { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
}

internal sealed class InstallerCacheStore(string root)
{
    private const string OwnerFile = ".hm-autosetup-owner.json";
    private const string LockFile = ".hm-autosetup.lock";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string Root { get; } = Path.GetFullPath(root);

    public bool HasEntry(string appId)
    {
        var directory = AppDirectory(appId);
        CheckNoReparsePoints(directory);
        return File.Exists(Path.Combine(directory, OwnerFile)) && File.Exists(Path.Combine(directory, "index.json"));
    }

    public async Task<Lease> AcquireAsync(string appId, CancellationToken cancellationToken)
    {
        var directory = AppDirectory(appId);
        var gate = Gates.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileStream? fileLock = null;
        try
        {
            EnsureDirectory(directory);
            var lockPath = Path.Combine(directory, LockFile);
            while (fileLock is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckNoReparsePoints(lockPath);
                try { fileLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
                { await Task.Delay(100, cancellationToken).ConfigureAwait(false); }
            }
            var ownerPath = Path.Combine(directory, OwnerFile);
            CheckNoReparsePoints(ownerPath);
            CacheOwner owner;
            if (!File.Exists(ownerPath))
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any(path => !Path.GetFileName(path).Equals(LockFile, StringComparison.Ordinal)))
                    throw new InvalidDataException($"保留目录 {directory} 包含未登记的文件，未覆盖或删除。请选择其他目录或移走这些文件。");
                owner = new CacheOwner("HM-AutoSetup.InstallerCache.v1", appId, Guid.NewGuid().ToString("N"));
                using var stream = new FileStream(ownerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(stream, owner, Json);
                stream.Flush(flushToDisk: true);
            }
            else
            {
                owner = JsonSerializer.Deserialize<CacheOwner>(ReadSmallFile(ownerPath, 8192), Json)
                    ?? throw new InvalidDataException("安装包保留目录的所有权记录为空。");
                if (owner.Kind != "HM-AutoSetup.InstallerCache.v1" || !string.Equals(owner.AppId, appId, StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParseExact(owner.OwnerId, "N", out _)) throw new InvalidDataException("安装包保留目录的所有权记录不匹配。");
            }
            return new Lease(directory, appId, owner.OwnerId, fileLock, gate);
        }
        catch { fileLock?.Dispose(); gate.Release(); throw; }
    }

    public InstallerCacheEntry? ReadEntry(Lease lease)
    {
        var path = Path.Combine(lease.Directory, "index.json");
        CheckNoReparsePoints(path);
        if (!File.Exists(path)) return null;
        var entry = JsonSerializer.Deserialize<InstallerCacheEntry>(ReadSmallFile(path, 65536), Json)
            ?? throw new InvalidDataException("安装包索引为空。");
        if (entry.SchemaVersion != 1 || entry.OwnerId != lease.OwnerId || !string.Equals(entry.AppId, lease.AppId, StringComparison.OrdinalIgnoreCase) ||
            entry.Bytes <= 0 || string.IsNullOrEmpty(entry.Sha256) || entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("安装包索引无效或不属于此应用。");
        _ = PayloadPath(lease, entry);
        return entry;
    }

    public string PayloadPath(Lease lease, InstallerCacheEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.PayloadRelativePath)) throw new InvalidDataException("安装包索引路径为空。");
        var parts = entry.PayloadRelativePath.Replace('/', '\\').Split('\\');
        if (parts.Length != 2 || !Regex.IsMatch(parts[0], "^package-[0-9a-f]{32}$", RegexOptions.CultureInvariant) || !SafeInstallerName(parts[1]))
            throw new InvalidDataException("安装包索引路径不在本应用的受管目录中。");
        var path = Path.Combine(lease.Directory, parts[0], parts[1]);
        CheckNoReparsePoints(path);
        return path;
    }

    public string CreatePendingDirectory(Lease lease)
    {
        var path = Path.Combine(lease.Directory, ".pending-" + Guid.NewGuid().ToString("N"));
        EnsureDirectory(path);
        return path;
    }

    public string Promote(Lease lease, string pendingDirectory)
    {
        CheckDirectOwnedChild(lease, pendingDirectory, ".pending-");
        var destination = Path.Combine(lease.Directory, "package-" + Guid.NewGuid().ToString("N"));
        CheckNoReparsePoints(destination);
        Directory.Move(pendingDirectory, destination);
        return destination;
    }

    public void WriteEntry(Lease lease, InstallerCacheEntry entry)
    {
        if (entry.OwnerId != lease.OwnerId || !entry.AppId.Equals(lease.AppId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("不能写入其他应用的安装包索引。");
        _ = PayloadPath(lease, entry);
        var path = Path.Combine(lease.Directory, "index.json");
        var temporary = Path.Combine(lease.Directory, ".index-" + Guid.NewGuid().ToString("N") + ".tmp");
        CheckNoReparsePoints(path);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, entry, Json); stream.Flush(flushToDisk: true); }
            CheckNoReparsePoints(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) { CheckNoReparsePoints(temporary); File.Delete(temporary); } }
    }

    public async Task DeleteRetiredAsync(Lease lease, InstallerCacheEntry retired, IProgress<string> log)
    {
        try
        {
            var path = PayloadPath(lease, retired);
            if (!File.Exists(path)) return;
            // Refuse to remove a file whose bytes were replaced outside this library.
            await using (var stream = File.OpenRead(path))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
                if (!hash.Equals(retired.Sha256, StringComparison.OrdinalIgnoreCase))
                { log.Report("旧安装包已被外部修改，未自动删除：" + path); return; }
            }
            CheckNoReparsePoints(path);
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!); // Only an empty, exact owned directory.
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { log.Report("旧安装包清理未完成：" + ex.Message); }
    }

    public static bool SafeInstallerName(string name) => !string.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.') && !name.EndsWith(' ') &&
        (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));

    public static void EnsureDirectory(string path)
    {
        CheckNoReparsePoints(path);
        Directory.CreateDirectory(path);
        CheckNoReparsePoints(path);
    }

    public static void CheckNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = Path.GetPathRoot(fullPath)!;
        foreach (var part in fullPath[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("安装包目录含链接或重解析点，已拒绝访问：" + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private string AppDirectory(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !Regex.IsMatch(appId, "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("安装包缓存应用 ID 无效。");
        return Path.Combine(Root, appId);
    }

    private static string ReadSmallFile(string path, long maximumBytes)
    {
        if (new FileInfo(path).Length > maximumBytes) throw new InvalidDataException("安装包缓存索引超过支持的大小。");
        return File.ReadAllText(path);
    }

    private static void CheckDirectOwnedChild(Lease lease, string path, string prefix)
    {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), lease.Directory, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(full), "^" + Regex.Escape(prefix) + "[0-9a-f]{32}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("安装包暂存目录不属于当前应用。");
        CheckNoReparsePoints(full);
    }

    private sealed record CacheOwner(string Kind, string AppId, string OwnerId);

    internal sealed class Lease(string directory, string appId, string ownerId, FileStream fileLock, SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;
        public string Directory { get; } = directory;
        public string AppId { get; } = appId;
        public string OwnerId { get; } = ownerId;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            fileLock.Dispose(); gate.Release();
        }
    }
}

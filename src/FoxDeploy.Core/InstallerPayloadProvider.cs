using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FoxDeploy.Core;

public interface IInstallerPayloadProvider
{
    Task<PreparedInstaller> PrepareAsync(AppDefinition app, DeploymentSettings settings, IProgress<string> log, CancellationToken cancellationToken);
}

public sealed class PreparedInstaller(string filePath, IDisposable? fileLock = null, Func<ValueTask>? cleanup = null) : IAsyncDisposable
{
    private int _disposed;
    public string FilePath { get; } = filePath;
    public string? Version { get; init; }
    public string? Sha256 { get; init; }
    public string? Publisher { get; init; }
    public bool IsCached { get; init; }
    public bool LatestConfirmed { get; init; } = true;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { fileLock?.Dispose(); }
        finally { if (cleanup is not null) await cleanup().ConfigureAwait(false); }
    }
}

public sealed class InstallerPayloadProvider : IInstallerPayloadProvider, IDisposable
{
    private const long MaximumInstallerBytes = 2L * 1024 * 1024 * 1024;
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(15) };
    private readonly InstallerCacheStore _library;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Func<string, IReadOnlyList<string>, string> _verifyPublisher;
    private readonly Func<OnlineInstallerDefinition, CancellationToken, Task<WingetResolvedInstaller>> _manifestResolver;
    private readonly Func<TimeSpan, CancellationToken, Task>? _retryDelay;
    public string InstallerLibraryRoot => _library.Root;

    public InstallerPayloadProvider(string? cacheRoot = null, string? installerLibraryRoot = null, HttpMessageHandler? httpMessageHandler = null,
        Func<string, IReadOnlyList<string>, string>? signatureVerifier = null,
        Func<OnlineInstallerDefinition, CancellationToken, Task<WingetResolvedInstaller>>? manifestResolver = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        // cacheRoot remains a constructor compatibility parameter. Installer bytes are always retained beside the app.
        _library = new InstallerCacheStore(installerLibraryRoot ?? InstallerSources.InstallerDirectory);
        _client = httpMessageHandler is null ? SharedClient : new HttpClient(httpMessageHandler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(15) };
        _ownsClient = httpMessageHandler is not null;
        _verifyPublisher = signatureVerifier ?? AuthenticodeVerification.VerifyPublisher;
        _manifestResolver = manifestResolver ?? WingetManifestResolver.ResolveAsync;
        _retryDelay = retryDelay;
    }

    public async Task CheckForUpdatesAsync(IEnumerable<AppDefinition> apps, DeploymentSettings settings, IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apps); ArgumentNullException.ThrowIfNull(settings); ArgumentNullException.ThrowIfNull(progress);
        var onlineSettings = new DeploymentSettings();
        foreach (var app in apps.Where(app => app.OnlineInstaller is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!_library.HasEntry(app.Id)) continue;
                progress.Report($"[{app.Name}] 正在检查已保留安装包的更新…");
                await using var payload = await PrepareAsync(app, onlineSettings, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (IsPreparationError(ex))
            { progress.Report($"[{app.Name}] 安装包更新检查未完成：{InstallerNetwork.SafeSummary(ex)} {InstallerNetwork.DescribeFailure(ex)}"); }
        }
    }

    public async Task<PreparedInstaller> PrepareAsync(AppDefinition app, DeploymentSettings settings, IProgress<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app); ArgumentNullException.ThrowIfNull(settings); ArgumentNullException.ThrowIfNull(log);
        using var phaseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        phaseTimeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            return await PreparePersistentAsync(app, log, phaseTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (phaseTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new IOException("安装包获取或等待缓存超过 15 分钟，已停止，请检查网络后重试。", ex); }
    }

    private async Task<PreparedInstaller> PreparePersistentAsync(AppDefinition app, IProgress<string> log, CancellationToken cancellationToken)
    {
        var online = app.OnlineInstaller;
        var profile = (InstallerProfile?)online ?? app.LocalInstaller ?? throw new NotSupportedException($"{app.Name} 未配置可核验的安装来源。");
        if (online is not null && online.AllowedPublishers.Count == 0) throw new InvalidDataException("在线安装源缺少允许的发布者，已拒绝执行。");
        InstallerCacheStore.Lease lease;
        try { lease = await _library.AcquireAsync(app.Id, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        { throw new IOException($"无法使用程序旁的安装包目录 {_library.Root}。请将 HM AppKit 放在可写入的文件夹或 U 盘中后重试。{ex.Message}", ex); }
        try
        {
            InstallerCacheEntry? oldEntry = null;
            try { oldEntry = _library.ReadEntry(lease); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            { log.Report($"[{app.Name}] 保留包索引无效，将重新下载；未删除原文件：{InstallerNetwork.SafeSummary(ex)}"); }
            var fingerprint = online is not null ? SourceFingerprint(online) : LocalFingerprint(app.LocalInstaller!);
            InstallerCacheEntry? usable = null;
            if (oldEntry is not null && (oldEntry.SourceProfileFingerprint == fingerprint ||
                string.IsNullOrEmpty(oldEntry.SourceProfileFingerprint) && oldEntry.SourceFingerprint == fingerprint))
            {
                try
                {
                    await using var verification = await OpenVerifiedCachedAsync(lease, oldEntry, profile, cancellationToken).ConfigureAwait(false);
                    usable = oldEntry;
                }
                catch (Exception ex) when (IsPreparationError(ex)) { log.Report($"[{app.Name}] 已保留安装包校验失败，不会复用：{InstallerNetwork.SafeSummary(ex)}"); }
            }
            if (usable is null && app.LocalInstaller is not null)
            {
                try
                {
                    usable = await ImportLooseInstallerAsync(app, profile, lease, fingerprint, log, cancellationToken).ConfigureAwait(false);
                    if (usable is not null && oldEntry is not null && usable.PayloadRelativePath != oldEntry.PayloadRelativePath)
                        await _library.DeleteRetiredAsync(lease, oldEntry, log).ConfigureAwait(false);
                }
                catch (Exception ex) when (online is not null && !cancellationToken.IsCancellationRequested && IsPreparationError(ex))
                { log.Report($"[{app.Name}] 程序旁的安装包未通过已核实版本校验，将从官网获取；原文件保留：{InstallerNetwork.SafeSummary(ex)}"); }
            }
            InstallerCacheEntry current;
            var latestConfirmed = true;
            try
            {
                if (online is null)
                {
                    current = usable ?? throw new FileNotFoundException($"请将已核实的 {app.LocalInstaller!.FileName} 放入程序旁 Local Installer；此应用未配置官网来源。");
                    latestConfirmed = false;
                    log.Report($"[{app.Name}] 仅配置本地安装包，已核验后使用；无法检查官网更新。");
                }
                else current = await RefreshAsync(app, online, lease, fingerprint, usable, usable ?? oldEntry, log, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (usable is not null && !cancellationToken.IsCancellationRequested && IsPreparationError(ex))
            {
                current = usable; latestConfirmed = false;
                log.Report($"[{app.Name}] 未能确认官网最新版本：{InstallerNetwork.SafeSummary(ex)}；将使用重新校验通过的已保留安装包。 {InstallerNetwork.DescribeFailure(ex)}");
            }
            var fileLock = await OpenVerifiedCachedAsync(lease, current, profile, cancellationToken).ConfigureAwait(false);
            var path = _library.PayloadPath(lease, current);
            log.Report($"[{app.Name}] 使用保留安装包：{current.OriginalFileName}" + (current.Version is { Length: > 0 } ? $"（{current.Version}）" : ""));
            return new PreparedInstaller(path, fileLock, () => { lease.Dispose(); return ValueTask.CompletedTask; })
            { Version = current.Version, Sha256 = current.Sha256, Publisher = current.Publisher, IsCached = true, LatestConfirmed = latestConfirmed };
        }
        catch { lease.Dispose(); throw; }
    }

    private async Task<InstallerCacheEntry> RefreshAsync(AppDefinition app, OnlineInstallerDefinition online, InstallerCacheStore.Lease lease,
        string fingerprint, InstallerCacheEntry? cached, InstallerCacheEntry? retired, IProgress<string> log, CancellationToken cancellationToken)
    {
        log.Report($"[{app.Name}] 正在检查官方当前版本…");
        var source = await ResolveSourceAsync(online, cached, cancellationToken, log, app.Name).ConfigureAwait(false);
        if (cached is not null && source.ExpectedSha256 is { Length: > 0 } expected && expected.Equals(cached.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            var unchanged = ApplySource(cached, source) with { CheckedAt = DateTimeOffset.Now, Version = source.Version ?? cached.Version };
            _library.WriteEntry(lease, unchanged);
            log.Report($"[{app.Name}] 官方当前版本哈希未变化，无需重新下载。");
            return unchanged;
        }
        var conditional = cached is not null && cached.ResolvedUrl == source.DownloadUri.AbsoluteUri ? cached : null;
        var download = await InstallerNetwork.ExecuteAsync(
            token => DownloadAttemptAsync(app, online, lease, source, cached, conditional, log, token),
            "安装包下载", source.DownloadUri, log, app.Name, cancellationToken, _retryDelay).ConfigureAwait(false);
        if (download.Unchanged)
        {
            var unchanged = ApplySource(cached!, source) with { CheckedAt = DateTimeOffset.Now, Version = source.Version ?? cached!.Version };
            _library.WriteEntry(lease, unchanged);
            log.Report($"[{app.Name}] {download.UnchangedMessage}");
            return unchanged;
        }
        var pending = download.PendingDirectory!;
        var path = download.FilePath!;
        string? promoted = null;
        var committed = false;
        try
        {
            // Verification and cache promotion deliberately sit outside the network retry boundary.
            var evidence = await VerifyFileAsync(path, online, source.ExpectedSha256, cancellationToken).ConfigureAwait(false);
            log.Report($"[{app.Name}] 数字签名已核对：{evidence.Publisher}");
            if (cached is not null && cached.Sha256.Equals(evidence.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                var unchanged = ApplySource(cached, source) with
                {
                    CheckedAt = DateTimeOffset.Now, ETag = download.ETag, LastModified = download.LastModified,
                    FinalUrl = download.FinalUri.AbsoluteUri, Version = source.Version ?? evidence.Version ?? cached.Version
                };
                _library.WriteEntry(lease, unchanged);
                log.Report($"[{app.Name}] 下载校验确认内容未变化，继续使用原保留包。");
                return unchanged;
            }
            promoted = _library.Promote(lease, pending);
            path = Path.Combine(promoted, Path.GetFileName(path));
            var entry = ApplySource(new InstallerCacheEntry
            {
                AppId = app.Id, OwnerId = lease.OwnerId, SourceProfileFingerprint = fingerprint,
                PayloadRelativePath = Path.GetFileName(promoted) + "\\" + Path.GetFileName(path), OriginalFileName = Path.GetFileName(path),
                FinalUrl = download.FinalUri.AbsoluteUri, ETag = download.ETag, LastModified = download.LastModified,
                Bytes = evidence.Bytes, Sha256 = evidence.Sha256, Publisher = evidence.Publisher, Version = source.Version ?? evidence.Version,
                DownloadedAt = DateTimeOffset.Now, CheckedAt = DateTimeOffset.Now
            }, source);
            _library.WriteEntry(lease, entry);
            committed = true;
            if (retired is not null && retired.PayloadRelativePath != entry.PayloadRelativePath)
                await _library.DeleteRetiredAsync(lease, retired, log).ConfigureAwait(false);
            log.Report($"[{app.Name}] 已保留当前安装包，旧版本已按记录替换。");
            return entry;
        }
        catch { if (promoted is not null && !committed) DeleteOwnedPayload(path, promoted, log); throw; }
        finally { if (Directory.Exists(pending)) DeleteOwnedPayload(Path.Combine(pending, Path.GetFileName(path)), pending, log); }
    }

    private async Task<DownloadedInstaller> DownloadAttemptAsync(AppDefinition app, OnlineInstallerDefinition online,
        InstallerCacheStore.Lease lease, ResolvedSource source, InstallerCacheEntry? cached, InstallerCacheEntry? conditional,
        IProgress<string> log, CancellationToken cancellationToken)
    {
        using var fetched = await GetAllowedAsync(source.DownloadUri, online.AllowedDownloadHosts, cancellationToken,
            conditional: conditional, stage: "安装包下载").ConfigureAwait(false);
        var response = fetched.Response;
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (cached is null || conditional is null || source.ExpectedSha256 is { Length: > 0 } hash && !hash.Equals(cached.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("官网返回未修改，但无法用当前版本哈希核对已保留安装包。");
            return new DownloadedInstaller(null, null, fetched.FinalUri, null, null, true, "官网返回 304，复用已保留安装包。");
        }
        response.EnsureSuccessStatusCode();
        if (cached is not null && conditional is not null && SameResponseIdentity(cached, fetched, source.ExpectedSha256))
            return new DownloadedInstaller(null, null, fetched.FinalUri, null, null, true, "官网版本标识未变化，无需重新下载。");
        var pending = _library.CreatePendingDirectory(lease);
        var path = Path.Combine(pending, DownloadName(app, online, fetched.FinalUri));
        try
        {
            await DownloadAsync(response, path, app.Name, fetched.FinalUri, log, cancellationToken).ConfigureAwait(false);
            return new DownloadedInstaller(pending, path, fetched.FinalUri, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
        }
        catch
        {
            DeleteOwnedPayload(path, pending, log);
            throw;
        }
    }

    private async Task<InstallerCacheEntry?> ImportLooseInstallerAsync(AppDefinition app, InstallerProfile effectiveProfile,
        InstallerCacheStore.Lease lease, string fingerprint, IProgress<string> log, CancellationToken cancellationToken)
    {
        var local = app.LocalInstaller!;
        if (string.IsNullOrWhiteSpace(local.Sha256) || local.Sha256.Length != 64 || !local.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("本地安装包缺少已核实的 SHA-256，已拒绝导入。");
        var source = InstallerSources.ResolveLocalPath(app, new DeploymentSettings(), _library.Root);
        InstallerCacheStore.CheckNoReparsePoints(source);
        if (!File.Exists(source)) return null;
        if (app.OnlineInstaller is not null && ExecutionFingerprint(local) != ExecutionFingerprint(effectiveProfile))
            throw new InvalidDataException("此旧版本地包的安装参数与当前官网版本不同，不能按当前参数执行。");
        log.Report($"[{app.Name}] 正在核验程序旁 Local Installer 中的 {local.FileName}…");
        var pending = _library.CreatePendingDirectory(lease);
        var path = Path.Combine(pending, local.FileName);
        string? promoted = null;
        var committed = false;
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (input.Length <= 0 || input.Length > MaximumInstallerBytes) throw new InvalidDataException("安装包为空或超过支持的大小。");
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            var evidence = await VerifyFileAsync(path, effectiveProfile, local.Sha256, cancellationToken, app.OnlineInstaller is null).ConfigureAwait(false);
            // The source is user-owned. Copy it into an owned package; never move or remove it.
            promoted = _library.Promote(lease, pending);
            path = Path.Combine(promoted, local.FileName);
            var entry = new InstallerCacheEntry
            {
                AppId = app.Id, OwnerId = lease.OwnerId, SourceProfileFingerprint = fingerprint, SourceFingerprint = fingerprint,
                PayloadRelativePath = Path.GetFileName(promoted) + "\\" + local.FileName, OriginalFileName = local.FileName,
                ExpectedSha256 = local.Sha256, Bytes = evidence.Bytes, Sha256 = evidence.Sha256, Publisher = evidence.Publisher,
                Version = evidence.Version, DownloadedAt = DateTimeOffset.Now, CheckedAt = DateTimeOffset.Now
            };
            _library.WriteEntry(lease, entry);
            committed = true;
            log.Report($"[{app.Name}] 本地包核验通过，已导入受管缓存；原文件保留。");
            return entry;
        }
        catch { if (promoted is not null && !committed) DeleteOwnedPayload(path, promoted, log); throw; }
        finally { if (Directory.Exists(pending)) DeleteOwnedPayload(Path.Combine(pending, local.FileName), pending, log); }
    }

    private async Task<FileStream> OpenVerifiedCachedAsync(InstallerCacheStore.Lease lease, InstallerCacheEntry entry, InstallerProfile profile, CancellationToken cancellationToken)
    {
        var path = _library.PayloadPath(lease, entry);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (stream.Length != entry.Bytes) throw new InvalidDataException("保留安装包的大小与索引不符。");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase) || entry.ExpectedSha256 is { Length: > 0 } expected && !hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("保留安装包的 SHA-256 校验失败。");
            if (profile is LocalInstallerDefinition local && !hash.Equals(local.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("本地受管安装包与当前已核实版本不一致。");
            if (profile.AllowedPublishers.Count > 0) _ = _verifyPublisher(path, profile.AllowedPublishers);
            else if (profile is not LocalInstallerDefinition) throw new InvalidDataException("受管安装包缺少可信签名发布者。");
            stream.Position = 0; return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private async Task<PayloadEvidence> VerifyFileAsync(string path, InstallerProfile profile, string? expectedSha256, CancellationToken cancellationToken, bool allowUnsignedLocal = false)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > MaximumInstallerBytes) throw new InvalidDataException("安装包为空或超过支持的大小。");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.IsNullOrWhiteSpace(expectedSha256) && !hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装包 SHA-256 与已核实版本不一致，已拒绝执行。");
        if (profile.AllowedPublishers.Count == 0 && !allowUnsignedLocal) throw new InvalidDataException("在线安装源缺少允许的发布者，已拒绝执行。");
        var publisher = profile.AllowedPublishers.Count > 0 ? _verifyPublisher(path, profile.AllowedPublishers) : "";
        var version = FileVersionInfo.GetVersionInfo(path);
        return new PayloadEvidence(stream.Length, hash, publisher, version.ProductVersion ?? version.FileVersion);
    }

    private static async Task DownloadAsync(HttpResponseMessage response, string path, string name, Uri uri, IProgress<string> log, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumInstallerBytes) throw new InvalidDataException("在线安装包超过支持的大小。");
        await using var input = await OpenNetworkStreamAsync(response.Content, "安装包下载", uri, cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[131072]; long received = 0; long lastReported = -1;
        while (await ReadNetworkAsync(input, buffer, "安装包下载", uri, cancellationToken).ConfigureAwait(false) is var read && read != 0)
        {
            received += read;
            if (received > MaximumInstallerBytes) throw new InvalidDataException("在线安装包超过支持的大小。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            var bucket = received / (10 * 1024 * 1024);
            if (bucket > lastReported)
            { lastReported = bucket; log.Report($"[{name}] 已下载 {received / 1048576d:0.0} MB" + (response.Content.Headers.ContentLength is { } total ? $" / {total / 1048576d:0.0} MB" : "")); }
        }
        if (response.Content.Headers.ContentLength is { } length && received < length)
            throw InstallerNetwork.ReadFailure(new EndOfStreamException("在线安装包下载长度不完整。"), "安装包下载", uri);
        if (response.Content.Headers.ContentLength is { } expected && received > expected)
            throw new InvalidDataException("在线安装包下载长度超过服务器声明。");
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private async Task<ResolvedSource> ResolveSourceAsync(OnlineInstallerDefinition definition, InstallerCacheEntry? cached,
        CancellationToken cancellationToken, IProgress<string>? log = null, string? appName = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            Uri? hint = !string.IsNullOrWhiteSpace(definition.WingetManifestPath) ? new Uri("https://api.github.com") :
                Uri.TryCreate(definition.MetadataUrl, UriKind.Absolute, out var metadata) ? metadata : null;
            return await InstallerNetwork.ExecuteAsync(token => ResolveSourceOnceAsync(definition, cached, token),
                "版本信息查询", hint, log, appName, timeout.Token, _retryDelay).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("官方版本信息查询超过 90 秒，暂时无法确认最新版本。", ex); }
    }

    private async Task<ResolvedSource> ResolveSourceOnceAsync(OnlineInstallerDefinition definition, InstallerCacheEntry? cached, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(definition.WingetManifestPath))
        {
            var resolved = await _manifestResolver(definition, cancellationToken).ConfigureAwait(false);
            return new ResolvedSource(ValidateAllowedUri(resolved.DownloadUri.AbsoluteUri, definition.AllowedDownloadHosts), resolved.Sha256, resolved.Version);
        }
        if (!string.IsNullOrWhiteSpace(definition.DownloadUrl)) return new ResolvedSource(ValidateAllowedUri(definition.DownloadUrl, definition.AllowedDownloadHosts));
        var metadataUri = ValidateAllowedUri(definition.MetadataUrl, definition.AllowedDownloadHosts);
        using var fetched = await GetAllowedAsync(metadataUri, definition.AllowedDownloadHosts, cancellationToken, definition.RequestHeaders,
            metadataConditional: cached, stage: "版本信息查询").ConfigureAwait(false);
        if (fetched.Response.StatusCode == HttpStatusCode.NotModified)
        {
            if (cached is null) throw new InvalidDataException("官网返回 304，但没有已保留的版本记录。");
            return new ResolvedSource(ValidateAllowedUri(cached.ResolvedUrl, definition.AllowedDownloadHosts), MetadataETag: cached.MetadataETag,
                MetadataLastModified: cached.MetadataLastModified, MetadataFinalUrl: cached.MetadataFinalUrl);
        }
        fetched.Response.EnsureSuccessStatusCode();
        var text = await ReadMetadataAsync(fetched.Response, fetched.FinalUri, cancellationToken).ConfigureAwait(false);
        return new ResolvedSource(ParseDownloadUri(definition, text, fetched.FinalUri), MetadataETag: fetched.Response.Headers.ETag?.ToString(),
            MetadataLastModified: fetched.Response.Content.Headers.LastModified, MetadataFinalUrl: fetched.FinalUri.AbsoluteUri);
    }

    public static async Task<Uri> ResolveDownloadUriAsync(OnlineInstallerDefinition definition, CancellationToken cancellationToken = default)
    {
        using var provider = new InstallerPayloadProvider();
        return (await provider.ResolveSourceAsync(definition, null, cancellationToken).ConfigureAwait(false)).DownloadUri;
    }

    private static async Task<string> ReadMetadataAsync(HttpResponseMessage response, Uri uri, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > 2097152) throw new InvalidDataException("版本元数据过大。");
        await using var stream = await OpenNetworkStreamAsync(response.Content, "版本信息查询", uri, cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream(); var buffer = new byte[8192];
        while (await ReadNetworkAsync(stream, buffer, "版本信息查询", uri, cancellationToken).ConfigureAwait(false) is var count && count != 0)
        { if (bytes.Length + count > 2097152) throw new InvalidDataException("版本元数据过大。"); bytes.Write(buffer, 0, count); }
        if (response.Content.Headers.ContentLength is { } length && bytes.Length < length)
            throw InstallerNetwork.ReadFailure(new EndOfStreamException("版本元数据下载长度不完整。"), "版本信息查询", uri);
        if (response.Content.Headers.ContentLength is { } expected && bytes.Length > expected)
            throw new InvalidDataException("版本元数据长度超过服务器声明。");
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static async Task<Stream> OpenNetworkStreamAsync(HttpContent content, string stage, Uri uri, CancellationToken token)
    {
        try { return await content.ReadAsStreamAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (token.IsCancellationRequested)
        { throw InstallerNetwork.WithContext(ex, stage, uri); }
        catch (Exception ex) when (ex is IOException or HttpRequestException || ex is OperationCanceledException && !token.IsCancellationRequested)
        { throw InstallerNetwork.ReadFailure(ex, stage, uri); }
    }

    private static async Task<int> ReadNetworkAsync(Stream input, Memory<byte> buffer, string stage, Uri uri, CancellationToken token)
    {
        try { return await input.ReadAsync(buffer, token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (token.IsCancellationRequested)
        { throw InstallerNetwork.WithContext(ex, stage, uri); }
        catch (Exception ex) when (ex is IOException or HttpRequestException || ex is OperationCanceledException && !token.IsCancellationRequested)
        { throw InstallerNetwork.ReadFailure(ex, stage, uri); }
    }

    public static Uri ParseDownloadUri(OnlineInstallerDefinition definition, string metadata, Uri? baseUri = null)
    {
        if (definition.MetadataFormat == "todesk-windows")
            return ValidateAllowedUri(ToDeskDownloadMetadata.Parse(metadata), definition.AllowedDownloadHosts);
        if (definition.MetadataFormat != "") throw new InvalidDataException("不支持的官方元数据解析配置。");
        var normalized = WebUtility.HtmlDecode(metadata).Replace("\\/", "/", StringComparison.Ordinal).Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase);
        for (var layer = 0; layer < 3; layer++) normalized = normalized.Replace("\\\"", "\"", StringComparison.Ordinal);
        var match = Regex.Match(normalized, definition.UrlPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success || !match.Groups["url"].Success) throw new InvalidDataException("官网版本信息已变化，无法确认当前下载地址；没有已核验缓存时将停止安装。");
        var value = match.Groups["url"].Value.Trim().Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase);
        var absolute = Uri.TryCreate(value, UriKind.Absolute, out var candidate) ? candidate : new Uri(baseUri ?? new Uri(definition.MetadataUrl), value);
        return ValidateAllowedUri(absolute.AbsoluteUri, definition.AllowedDownloadHosts);
    }

    public static Uri ValidateAllowedUri(string value, IReadOnlyList<string> allowedHosts)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort ||
            !allowedHosts.Any(host => uri.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("下载地址不在此软件已核实的官方 HTTPS 域名中。");
        return uri;
    }

    private async Task<FetchedResponse> GetAllowedAsync(Uri uri, IReadOnlyList<string> hosts, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null, InstallerCacheEntry? conditional = null, InstallerCacheEntry? metadataConditional = null,
        string stage = "网络请求")
    {
        for (var redirect = 0; redirect < 8; redirect++)
        {
            ValidateAllowedUri(uri.AbsoluteUri, hosts);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("HM-AppKit/0.5");
            if (headers is not null) foreach (var header in headers) request.Headers.Add(header.Key, header.Value);
            if (conditional is not null && uri.AbsoluteUri == conditional.FinalUrl) AddConditional(request, conditional.ETag, conditional.LastModified);
            if (metadataConditional is not null && uri.AbsoluteUri == metadataConditional.MetadataFinalUrl) AddConditional(request, metadataConditional.MetadataETag, metadataConditional.MetadataLastModified);
            HttpResponseMessage response;
            try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or AuthenticationException)
            { throw InstallerNetwork.WithContext(ex, stage, uri); }
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            { var location = response.Headers.Location; response.Dispose(); if (location is null) throw new InvalidDataException("官方下载跳转缺少地址。"); uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue; }
            try
            {
                if (response.StatusCode != HttpStatusCode.NotModified) response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex)
            {
                response.Dispose();
                throw InstallerNetwork.WithContext(ex, stage, uri);
            }
            return new FetchedResponse(response, uri);
        }
        throw new InvalidDataException("官方下载跳转次数过多。");
    }

    private static void AddConditional(HttpRequestMessage request, string? etag, DateTimeOffset? lastModified)
    {
        if (!string.IsNullOrWhiteSpace(etag) && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
        if (lastModified is not null) request.Headers.IfModifiedSince = lastModified;
    }
    private static bool SameResponseIdentity(InstallerCacheEntry entry, FetchedResponse fetched, string? expectedHash)
    {
        if (entry.FinalUrl != fetched.FinalUri.AbsoluteUri || expectedHash is { Length: > 0 } && !expectedHash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        var length = fetched.Response.Content.Headers.ContentLength;
        if (length is not null && length != entry.Bytes) return false;
        var etag = fetched.Response.Headers.ETag?.ToString();
        if (!string.IsNullOrWhiteSpace(entry.ETag) && etag == entry.ETag) return true;
        return entry.ETag is null && entry.LastModified is not null && entry.LastModified == fetched.Response.Content.Headers.LastModified && length == entry.Bytes;
    }
    private static string DownloadName(AppDefinition app, InstallerProfile profile, Uri uri)
    {
        var extension = profile.InstallerType.Equals("msi", StringComparison.OrdinalIgnoreCase) ? ".msi" : ".exe";
        var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        return InstallerCacheStore.SafeInstallerName(name) && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? name : app.Id + extension;
    }
    private static InstallerCacheEntry ApplySource(InstallerCacheEntry entry, ResolvedSource source) => entry with
    {
        ResolvedUrl = source.DownloadUri.AbsoluteUri, ExpectedSha256 = source.ExpectedSha256,
        SourceFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.SourceProfileFingerprint + "|" + source.ExpectedSha256))),
        MetadataETag = source.MetadataETag, MetadataLastModified = source.MetadataLastModified, MetadataFinalUrl = source.MetadataFinalUrl
    };
    private static string SourceFingerprint(OnlineInstallerDefinition profile)
    {
        // Keep existing cache identities unchanged for every source without a dedicated metadata parser.
        var value = JsonSerializer.Serialize(new
        {
            profile.DownloadUrl, profile.MetadataUrl, profile.UrlPattern, profile.WingetManifestPath, profile.InstallerType,
            Hosts = profile.AllowedDownloadHosts.Order(StringComparer.OrdinalIgnoreCase), Publishers = profile.AllowedPublishers.Order(StringComparer.OrdinalIgnoreCase),
            Headers = profile.RequestHeaders.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
        });
        if (profile.MetadataFormat != "") value += "|MetadataFormat:" + profile.MetadataFormat;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
    private static string LocalFingerprint(LocalInstallerDefinition profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile))));
    private static string ExecutionFingerprint(InstallerProfile profile) => JsonSerializer.Serialize(new
    {
        Type = profile.InstallerType.ToLowerInvariant(), profile.SilentSupported, profile.LocationSupported,
        profile.RequiresExistingTargetDirectory, profile.InteractiveFallback, profile.Adapter,
        profile.SilentArguments, profile.InteractiveArguments, profile.InstallLocationArgument
    });
    private static bool IsPreparationError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or
        InvalidOperationException or CryptographicException or RegexMatchTimeoutException or JsonException;
    private static void DeleteOwnedPayload(string path, string directory, IProgress<string> log)
    {
        try
        {
            var fullDirectory = Path.GetFullPath(directory); var fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(fullPath), fullDirectory, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包清理路径超出其独占暂存目录。");
            InstallerCacheStore.CheckNoReparsePoints(fullPath);
            if (File.Exists(fullPath)) File.Delete(fullPath);
            if (Directory.Exists(fullDirectory)) Directory.Delete(fullDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Report("安装包缓存清理未完成：" + ex.Message); }
    }
    public void Dispose() { if (_ownsClient) _client.Dispose(); }
    private sealed record PayloadEvidence(long Bytes, string Sha256, string Publisher, string? Version);
    private sealed record ResolvedSource(Uri DownloadUri, string? ExpectedSha256 = null, string? Version = null, string? MetadataETag = null,
        DateTimeOffset? MetadataLastModified = null, string? MetadataFinalUrl = null);
    private sealed record DownloadedInstaller(string? PendingDirectory, string? FilePath, Uri FinalUri, string? ETag, DateTimeOffset? LastModified,
        bool Unchanged = false, string? UnchangedMessage = null);
    private sealed record FetchedResponse(HttpResponseMessage Response, Uri FinalUri) : IDisposable { public void Dispose() => Response.Dispose(); }
}

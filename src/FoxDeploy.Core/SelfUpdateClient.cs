using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FoxDeploy.Core;

/// <summary>Checks fixed public release repositories and stages verified updates; never changes the running app or its state.</summary>
public sealed class SelfUpdateClient : IDisposable
{
    public const string Repository = "19016563235/HM-AppKit";
    public const string GiteeRepository = "huanmeng-it/HM-AppKit";
    public const string PackageAssetName = "HM-AppKit-win-x64.zip";
    public const string ChecksumAssetName = PackageAssetName + ".sha256";
    public const string ExecutableName = "HM AppKit.exe";
    private static readonly Uri LatestReleaseUri = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private static readonly Uri GiteeLatestReleaseUri = new($"https://gitee.com/api/v5/repos/{GiteeRepository}/releases/latest");
    private const int MetadataLimit = 1024 * 1024;
    private const int ChecksumLimit = 32 * 1024;
    private readonly SelfUpdateOptions _options;
    private readonly HttpClient _http;
    private readonly Action<string, string> _verifyExecutable;
    private readonly Func<TimeSpan, CancellationToken, Task>? _retryDelay;
    private readonly Dictionary<string, (string? ETag, byte[] Json)> _metadataCache = new(StringComparer.Ordinal);

    public SelfUpdateClient(SelfUpdateOptions? options = null, HttpMessageHandler? handler = null,
        Action<string, string>? executableVersionVerifier = null, Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        _options = options ?? new SelfUpdateOptions();
        if (_options.MaximumPackageBytes <= 0 || _options.MaximumExtractedBytes <= 0 || _options.MaximumArchiveEntries <= 0 ||
            _options.CheckTimeout <= TimeSpan.Zero || _options.DownloadTimeout <= TimeSpan.Zero ||
            !Enum.IsDefined(_options.PreferredSource)) throw new ArgumentException("更新限制或来源无效。", nameof(options));
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HM-AppKit-Updater/0.15");
        _verifyExecutable = executableVersionVerifier ?? VerifyExecutableVersion;
        _retryDelay = retryDelay;
    }

    public async Task<SelfUpdateRelease?> CheckForUpdateAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        if (!SelfUpdateVersion.TryParse(currentVersion, out var current)) throw new ArgumentException("当前应用版本无效。", nameof(currentVersion));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.CheckTimeout);
        if (_options.PreferredSource == SelfUpdateSource.Gitee)
        {
            // Reserve time for the fallback even if the preferred endpoint never responds.
            using var preferred = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            preferred.CancelAfter(TimeSpan.FromTicks(Math.Max(1, Math.Min(TimeSpan.FromSeconds(8).Ticks, _options.CheckTimeout.Ticks / 2))));
            try
            {
                var release = await InstallerNetwork.ExecuteAsync(token => CheckGiteeAttemptAsync(current, token),
                    "HM AppKit Gitee 版本查询", GiteeLatestReleaseUri, null, null, preferred.Token, _retryDelay).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                if (release is not null) return release;
            }
            catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException)
            { timeout.Token.ThrowIfCancellationRequested(); }
        }
        timeout.Token.ThrowIfCancellationRequested();
        return await CheckGitHubAsync(current, LatestReleaseUri, timeout.Token).ConfigureAwait(false);
    }

    private Task<SelfUpdateRelease?> CheckGitHubAsync(SelfUpdateVersion current, Uri uri, CancellationToken token) =>
        InstallerNetwork.ExecuteAsync(async ct =>
        {
            using var document = await ReadMetadataAsync(uri, SelfUpdateSource.GitHub, ct).ConfigureAwait(false);
            return document is null ? null : ParseRelease(document.RootElement, current);
        }, "HM AppKit GitHub 版本查询", uri, null, null, token, _retryDelay);

    private async Task<SelfUpdateRelease?> CheckGiteeAttemptAsync(SelfUpdateVersion current, CancellationToken token)
    {
        using var document = await ReadMetadataAsync(GiteeLatestReleaseUri, SelfUpdateSource.Gitee, token).ConfigureAwait(false);
        if (document is null || document.RootElement.ValueKind == JsonValueKind.Null) return null;
        var release = document.RootElement;
        if (release.ValueKind != JsonValueKind.Object ||
            !release.TryGetProperty("prerelease", out var preview) || preview.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetInt32(out var id) || id <= 0 ||
            !release.TryGetProperty("tag_name", out var tagValue) || tagValue.ValueKind != JsonValueKind.String) return null;
        var tag = tagValue.GetString()!;
        if (!SelfUpdateVersion.TryParse(tag, out var version) || tag.Contains('+') || version.CompareTo(current) <= 0) return null;
        var assets = new List<JsonElement>();
        for (var page = 1; page <= 10; page++)
        {
            var uri = new Uri($"https://gitee.com/api/v5/repos/{GiteeRepository}/releases/{id}/attach_files?page={page}&per_page=100");
            using var attachmentDocument = await ReadMetadataAsync(uri, SelfUpdateSource.Gitee, token).ConfigureAwait(false);
            if (attachmentDocument is null || attachmentDocument.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Gitee 更新附件列表格式无效。");
            var batch = attachmentDocument.RootElement.EnumerateArray().ToArray();
            if (batch.Length > 100 || batch.Any(item => item.ValueKind != JsonValueKind.Object))
                throw new InvalidDataException("Gitee 更新附件列表超过限制或格式无效。");
            assets.AddRange(batch.Select(item => item.Clone()));
            if (batch.Length < 100) break;
            if (page == 10) throw new InvalidDataException("Gitee 更新附件列表超过页数限制。");
        }
        var package = UniqueAsset(assets.ToArray(), PackageAssetName, requireUploaded: false);
        var checksum = UniqueAsset(assets.ToArray(), ChecksumAssetName, requireUploaded: false);
        if (!package.TryGetProperty("id", out var packageIdValue) || packageIdValue.ValueKind != JsonValueKind.Number || !packageIdValue.TryGetInt32(out var packageId) ||
            !checksum.TryGetProperty("id", out var checksumIdValue) || checksumIdValue.ValueKind != JsonValueKind.Number || !checksumIdValue.TryGetInt32(out var checksumId) ||
            !package.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number || !sizeValue.TryGetInt64(out var size) ||
            !checksum.TryGetProperty("size", out var checksumSize) || checksumSize.ValueKind != JsonValueKind.Number || !checksumSize.TryGetInt64(out var checksumBytes) ||
            checksumBytes <= 0 || checksumBytes > ChecksumLimit) throw new InvalidDataException("Gitee 更新附件信息无效。");
        var result = new SelfUpdateRelease
        {
            Source = SelfUpdateSource.Gitee, ReleaseId = id, PackageAssetId = packageId, ChecksumAssetId = checksumId,
            Version = version.ToString(), Tag = tag,
            ReleasePage = new($"https://gitee.com/{GiteeRepository}/releases/tag/{Uri.EscapeDataString(tag)}"),
            Notes = OptionalString(release, "body") ?? "", PackageUri = RequiredUri(package, "browser_download_url"),
            ChecksumUri = RequiredUri(checksum, "browser_download_url"), PackageBytes = size
        };
        ValidateRelease(result);
        return result;
    }

    private async Task<JsonDocument?> ReadMetadataAsync(Uri uri, SelfUpdateSource source, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(source == SelfUpdateSource.GitHub ? "application/vnd.github+json" : "application/json"));
        if (source == SelfUpdateSource.GitHub) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        _metadataCache.TryGetValue(uri.AbsoluteUri, out var cached);
        if (cached.Json is not null && cached.ETag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        using var response = await SendRequestAsync(request, "HM AppKit 版本查询", token).ConfigureAwait(false);
        ValidateResponseUri(response, uri);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        byte[] bytes;
        if (response.StatusCode == HttpStatusCode.NotModified)
            bytes = cached.Json ?? throw new InvalidDataException("更新源返回了无法复用的更新响应。");
        else
        {
            EnsureSuccess(response, uri);
            bytes = await ReadBoundedAsync(response, MetadataLimit, token).ConfigureAwait(false);
        }
        var document = JsonDocument.Parse(bytes);
        if (response.StatusCode != HttpStatusCode.NotModified)
            _metadataCache[uri.AbsoluteUri] = (response.Headers.ETag?.ToString(), bytes);
        return document;
    }

    public async Task<SelfUpdatePackage> DownloadAndStageAsync(SelfUpdateRelease release,
        IProgress<SelfUpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateRelease(release);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.DownloadTimeout);
        var token = timeout.Token;
        token.ThrowIfCancellationRequested();
        if (release.Source == SelfUpdateSource.Gitee)
        {
            using var preferred = CancellationTokenSource.CreateLinkedTokenSource(token);
            preferred.CancelAfter(TimeSpan.FromTicks(Math.Max(1, _options.DownloadTimeout.Ticks / 2)));
            try { return await DownloadWithRetryAsync(release, progress, preferred.Token, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (preferred.IsCancellationRequested && !token.IsCancellationRequested) { }
            catch (HttpRequestException ex) when (CanFallbackDownload(ex)) { token.ThrowIfCancellationRequested(); }
            token.ThrowIfCancellationRequested();
            progress?.Report(new(0, release.PackageBytes, "Gitee 更新下载暂不可用，正在切换 GitHub 同版本更新…"));
            using var lookup = CancellationTokenSource.CreateLinkedTokenSource(token);
            lookup.CancelAfter(_options.CheckTimeout);
            var uri = new Uri($"https://api.github.com/repos/{Repository}/releases/tags/{Uri.EscapeDataString(release.Tag)}");
            var fallback = await CheckGitHubAsync(default, uri, lookup.Token).ConfigureAwait(false);
            if (fallback is null || fallback.Tag != release.Tag || fallback.Version != release.Version)
                throw new InvalidDataException("GitHub 没有对应同版本的正式更新，已停止更新。");
            return await DownloadWithRetryAsync(fallback, progress, token, token).ConfigureAwait(false);
        }
        return await DownloadWithRetryAsync(release, progress, token, token).ConfigureAwait(false);
    }

    private Task<SelfUpdatePackage> DownloadWithRetryAsync(SelfUpdateRelease release,
        IProgress<SelfUpdateProgress>? progress, CancellationToken networkToken, CancellationToken operationToken)
    {
        var retryLog = progress is null ? null : new RetryProgress(progress, release.PackageBytes);
        return InstallerNetwork.ExecuteAsync(ct => DownloadAndStageAttemptAsync(release, progress, ct, operationToken),
            "HM AppKit 更新包准备", release.PackageUri, retryLog, null, networkToken, _retryDelay);
    }

    private static bool CanFallbackDownload(Exception exception)
    {
        for (Exception? node = exception; node is not null; node = node.InnerException)
            if (node is InvalidDataException or CryptographicException or System.Security.Authentication.AuthenticationException or
                UnauthorizedAccessException or System.Security.SecurityException) return false;
        return true;
    }

    private async Task<SelfUpdatePackage> DownloadAndStageAttemptAsync(SelfUpdateRelease release,
        IProgress<SelfUpdateProgress>? progress, CancellationToken token, CancellationToken operationToken)
    {
        var stagingRoot = Path.GetFullPath(_options.StagingRoot);
        EnsureNoReparsePoints(stagingRoot);
        Directory.CreateDirectory(stagingRoot);
        EnsureNoReparsePoints(stagingRoot);
        var stage = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            progress?.Report(new(0, release.PackageBytes, "正在验证更新校验文件…"));
            using var checksumResponse = await SendAssetAsync(release, checksum: true, token).ConfigureAwait(false);
            var checksumBytes = await ReadBoundedAsync(checksumResponse, ChecksumLimit, token).ConfigureAwait(false);
            var expectedHash = ParseChecksum(Encoding.UTF8.GetString(checksumBytes));
            if (release.ApiSha256 is not null && !string.Equals(release.ApiSha256, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("发布校验值与校验文件不一致，已停止更新。");
            var zipPath = Path.Combine(stage, "package.zip");
            using (var packageResponse = await SendAssetAsync(release, checksum: false, token).ConfigureAwait(false))
            {
                if (packageResponse.Content.Headers.ContentLength is long length && length != release.PackageBytes)
                    throw new InvalidDataException("更新包大小与发布信息不一致。");
                await using var input = await OpenBodyAsync(packageResponse, token).ConfigureAwait(false);
                await using var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                long received = 0;
                int count;
                while ((count = await ReadBodyAsync(input, buffer, packageResponse, token).ConfigureAwait(false)) != 0)
                {
                    received += count;
                    if (received > release.PackageBytes || received > _options.MaximumPackageBytes) throw new InvalidDataException("更新包超过允许的大小。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), operationToken).ConfigureAwait(false);
                    progress?.Report(new(received, release.PackageBytes, "正在下载 HM AppKit 更新…"));
                }
                if (received != release.PackageBytes) throw InstallerNetwork.ReadFailure(new EndOfStreamException("更新包下载不完整。"),
                    "HM AppKit 更新包读取", packageResponse.RequestMessage?.RequestUri ?? release.PackageUri);
                var actualHash = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包 SHA-256 校验失败，已停止更新。");
                await output.FlushAsync(operationToken).ConfigureAwait(false);
            }
            progress?.Report(new(release.PackageBytes, release.PackageBytes, "正在校验并准备更新…"));
            var payload = Path.Combine(stage, "payload");
            // Once all bytes are verified, only the caller's overall budget governs local preparation.
            // Expiring a source budget here must not restart a valid package from another server.
            var files = await ExtractArchiveAsync(zipPath, payload, operationToken).ConfigureAwait(false);
            var executable = Path.Combine(payload, ExecutableName);
            _verifyExecutable(executable, release.Version);
            operationToken.ThrowIfCancellationRequested();
            return new SelfUpdatePackage
            {
                Release = release, StageDirectory = stage, ExtractedDirectory = payload,
                ExecutablePath = executable, ZipPath = zipPath, Sha256 = expectedHash.ToLowerInvariant(), RelativeFiles = files
            };
        }
        catch
        {
            TryDeleteOwnedStage(stage, stagingRoot);
            throw;
        }
    }

    private SelfUpdateRelease? ParseRelease(JsonElement release, SelfUpdateVersion current)
    {
        if (release.ValueKind != JsonValueKind.Object) throw new InvalidDataException("GitHub 发布信息格式无效。");
        if (!release.TryGetProperty("draft", out var draft) || !release.TryGetProperty("prerelease", out var preview))
            throw new InvalidDataException("GitHub 发布信息缺少版本状态。");
        if (draft.GetBoolean() || preview.GetBoolean()) return null;
        var tag = RequiredString(release, "tag_name");
        if (!SelfUpdateVersion.TryParse(tag, out var version) || tag.Contains('+') || version.CompareTo(current) <= 0) return null;
        var page = RequiredUri(release, "html_url");
        var expectedPage = $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}";
        if (!string.Equals(page.AbsoluteUri, expectedPage, StringComparison.Ordinal)) throw new InvalidDataException("GitHub 发布页不属于 HM AppKit 仓库。");
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        var package = UniqueAsset(assets, PackageAssetName);
        var checksum = UniqueAsset(assets, ChecksumAssetName);
        var digest = OptionalString(package, "digest");
        string? apiHash = null;
        if (!string.IsNullOrEmpty(digest))
        {
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || !ValidHash(digest[7..])) throw new InvalidDataException("GitHub 更新包校验值格式无效。");
            apiHash = digest[7..].ToLowerInvariant();
        }
        var result = new SelfUpdateRelease
        {
            Version = version.ToString(), Tag = tag, ReleasePage = page,
            Notes = OptionalString(release, "body") ?? "", PackageUri = RequiredUri(package, "browser_download_url"),
            ChecksumUri = RequiredUri(checksum, "browser_download_url"), PackageBytes = package.GetProperty("size").GetInt64(), ApiSha256 = apiHash
        };
        ValidateRelease(result);
        return result;
    }

    private void ValidateRelease(SelfUpdateRelease release)
    {
        if (!SelfUpdateVersion.TryParse(release.Version, out var version) || !SelfUpdateVersion.TryParse(release.Tag, out var tagged) ||
            tagged != version || release.Tag.Contains('+') || release.PackageBytes <= 0 || release.PackageBytes > _options.MaximumPackageBytes ||
            !Enum.IsDefined(release.Source) || (release.ApiSha256 is not null && !ValidHash(release.ApiSha256))) throw new InvalidDataException("更新版本、大小或校验值无效。");
        if (release.Source == SelfUpdateSource.Gitee &&
            (release.ReleaseId is not > 0 || release.PackageAssetId is not > 0 || release.ChecksumAssetId is not > 0 ||
             release.PackageAssetId == release.ChecksumAssetId)) throw new InvalidDataException("Gitee 更新发行版或附件 ID 无效。");
        ValidateInitialAssetUri(release.PackageUri, release.Tag, PackageAssetName, release.Source);
        ValidateInitialAssetUri(release.ChecksumUri, release.Tag, ChecksumAssetName, release.Source);
        var site = release.Source == SelfUpdateSource.Gitee ? $"gitee.com/{GiteeRepository}" : $"github.com/{Repository}";
        var expectedPage = $"https://{site}/releases/tag/{Uri.EscapeDataString(release.Tag)}";
        if (release.ReleasePage.AbsoluteUri != expectedPage) throw new InvalidDataException("更新发布页无效。");
    }

    private async Task<HttpResponseMessage> SendAssetAsync(SelfUpdateRelease release, bool checksum, CancellationToken token)
    {
        var name = checksum ? ChecksumAssetName : PackageAssetName;
        var attachmentId = checksum ? release.ChecksumAssetId : release.PackageAssetId;
        var initialUri = release.Source == SelfUpdateSource.Gitee
            ? new Uri($"https://gitee.com/api/v5/repos/{GiteeRepository}/releases/{release.ReleaseId}/attach_files/{attachmentId}/download")
            : checksum ? release.ChecksumUri : release.PackageUri;
        var uri = initialUri;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            ValidateAssetRequestUri(uri, release, name, attachmentId, initialUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await SendRequestAsync(request, "HM AppKit 更新附件请求", token).ConfigureAwait(false);
            try
            {
                ValidateResponseUri(response, uri);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location ?? throw new InvalidDataException("更新下载重定向缺少地址。");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    ValidateAssetRequestUri(uri, release, name, attachmentId, initialUri);
                    if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath != initialUri.AbsolutePath)
                        throw new InvalidDataException("更新下载不能重定向到其他 GitHub 资源。");
                    response.Dispose();
                    continue;
                }
                EnsureSuccess(response, uri);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("更新下载重定向次数过多。");
    }

    private async Task<IReadOnlyList<string>> ExtractArchiveAsync(string zipPath, string payload, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count == 0 || archive.Entries.Count > _options.MaximumArchiveEntries) throw new InvalidDataException("更新压缩包文件数量无效。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/');
            var name = ValidateArchivePath(entry.FullName, directory);
            if (!names.Add(name)) throw new InvalidDataException("更新压缩包存在重名文件。");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || unixType is not (0 or 0x4000 or 0x8000) ||
                (unixType == 0x4000 && !directory) || (unixType == 0x8000 && directory)) throw new InvalidDataException("更新压缩包包含链接或特殊文件。");
            if (directory)
            {
                if (entry.Length != 0) throw new InvalidDataException("更新压缩包目录格式无效。");
                continue;
            }
            total = checked(total + entry.Length);
            if (entry.Length <= 0 || total > _options.MaximumExtractedBytes) throw new InvalidDataException("更新压缩包解压大小无效或超过限制。");
            files.Add(name);
        }
        if (!files.Contains(ExecutableName, StringComparer.Ordinal) || !files.Contains("Data/catalog.json", StringComparer.Ordinal))
            throw new InvalidDataException("更新压缩包缺少 HM AppKit 程序或应用目录。");
        Directory.CreateDirectory(payload);
        foreach (var entry in archive.Entries.Where(entry => !entry.FullName.EndsWith('/')))
        {
            token.ThrowIfCancellationRequested();
            var destination = Path.Combine(payload, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            EnsureNoReparsePoints(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
            var buffer = new byte[128 * 1024];
            long written = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                written += count;
                if (written > entry.Length) throw new InvalidDataException("更新压缩包文件大小不一致。");
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (written != entry.Length) throw new InvalidDataException("更新压缩包文件不完整。");
        }
        return files.AsReadOnly();
    }

    private static string ValidateArchivePath(string raw, bool directory)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > 240 || raw.Contains('\\') || raw.StartsWith('/') || raw.Contains(':') || raw.Contains('\0'))
            throw new InvalidDataException("更新压缩包路径无效。");
        var name = directory ? raw[..^1] : raw;
        var segments = name.Split('/');
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || segment.Any(char.IsControl) ||
                Regex.IsMatch(segment, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                segment.Equals("Local Installer", StringComparison.OrdinalIgnoreCase) || segment.Equals("settings.json", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("logs", StringComparison.OrdinalIgnoreCase) || segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新压缩包包含保留目录或无效路径。");
        }
        var allowedDirectory = segments[0] is "Data" or "docs" or "evidence" or "licenses" or "catalog";
        var allowedFile = name is ExecutableName or "README.md" or "Data/catalog.json" ||
            (segments.Length >= 2 && segments[0] == "docs" && name.EndsWith(".md", StringComparison.Ordinal)) ||
            (segments.Length >= 2 && segments[0] == "evidence" && (name.EndsWith(".md", StringComparison.Ordinal) || name.EndsWith(".json", StringComparison.Ordinal))) ||
            (segments.Length == 2 && segments[0] == "licenses" && name.EndsWith(".txt", StringComparison.Ordinal)) ||
            (segments.Length == 2 && segments[0] == "catalog" && name.EndsWith(".md", StringComparison.Ordinal));
        if (directory ? !allowedDirectory : !allowedFile) throw new InvalidDataException($"更新压缩包包含不受支持的文件：{name}");
        return name;
    }

    private static void VerifyExecutableVersion(string executable, string expectedVersion)
    {
        using (var stream = File.OpenRead(executable))
        {
            if (stream.Length < 256 || stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new InvalidDataException("更新程序不是有效的 Windows 可执行文件。");
            stream.Position = 0x3C;
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var offset = reader.ReadInt32();
            if (offset < 0x40 || offset > stream.Length - 4) throw new InvalidDataException("更新程序 PE 头无效。");
            stream.Position = offset;
            if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("更新程序 PE 头无效。");
        }
        var info = FileVersionInfo.GetVersionInfo(executable);
        if (!string.Equals(info.ProductName, "HM AppKit", StringComparison.Ordinal) ||
            !SelfUpdateVersion.TryParse(info.ProductVersion, out var actual) || !SelfUpdateVersion.TryParse(expectedVersion, out var expected) || actual != expected)
            throw new InvalidDataException("更新程序产品或版本与发布信息不一致。");
    }

    private static string ParseChecksum(string text)
    {
        var matches = new List<string>();
        foreach (var line in text.TrimStart('\uFEFF').Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Regex.Match(line, @"^([a-fA-F0-9]{64})[ \t]+\*?(.+)$", RegexOptions.CultureInvariant);
            if (!match.Success || match.Groups[2].Value != PackageAssetName) throw new InvalidDataException("更新校验文件格式或包名无效。");
            matches.Add(match.Groups[1].Value);
        }
        if (matches.Count != 1) throw new InvalidDataException("更新校验文件必须包含唯一安装包记录。");
        return matches[0].ToLowerInvariant();
    }

    private static JsonElement UniqueAsset(JsonElement[] assets, string name, bool requireUploaded = true)
    {
        var selected = assets.Where(asset => OptionalString(asset, "name") == name).ToArray();
        if (selected.Length != 1 || (requireUploaded && OptionalString(selected[0], "state") != "uploaded"))
            throw new InvalidDataException($"发布缺少唯一有效附件：{name}");
        return selected[0];
    }

    private static string RequiredString(JsonElement value, string name) => OptionalString(value, name) is { Length: > 0 } result
        ? result : throw new InvalidDataException($"GitHub 发布缺少 {name}。");
    private static string? OptionalString(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static Uri RequiredUri(JsonElement value, string name) => Uri.TryCreate(RequiredString(value, name), UriKind.Absolute, out var uri)
        ? uri : throw new InvalidDataException("GitHub 发布链接无效。");
    private static bool ValidHash(string hash) => hash.Length == 64 && hash.All(Uri.IsHexDigit);

    private static void ValidateInitialAssetUri(Uri uri, string tag, string file, SelfUpdateSource source)
    {
        ValidateAssetHost(uri, source);
        var site = source == SelfUpdateSource.Gitee ? $"gitee.com/{GiteeRepository}" : $"github.com/{Repository}";
        var expected = $"https://{site}/releases/download/{Uri.EscapeDataString(tag)}/{file}";
        if (uri.AbsoluteUri != expected) throw new InvalidDataException("更新附件不属于 HM AppKit 的指定发布。");
    }

    private static void ValidateAssetHost(Uri uri, SelfUpdateSource source)
    {
        if (!uri.IsAbsoluteUri) throw new InvalidDataException("更新下载地址必须是绝对 HTTPS 地址。");
        var allowed = source == SelfUpdateSource.Gitee
            ? uri.Host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("foruda.gitee.com", StringComparison.OrdinalIgnoreCase)
            : uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            !allowed) throw new InvalidDataException("更新下载地址不在允许的 HTTPS 域名中。");
    }

    private static void ValidateAssetRequestUri(Uri uri, SelfUpdateRelease release, string name, int? attachmentId, Uri initialUri)
    {
        ValidateAssetHost(uri, release.Source);
        if (release.Source != SelfUpdateSource.Gitee) return;
        if (uri.Host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase))
        {
            var browser = $"https://gitee.com/{GiteeRepository}/releases/download/{Uri.EscapeDataString(release.Tag)}/{name}";
            var attachment = $"https://gitee.com/{GiteeRepository}/attach_files/{attachmentId}/download/{name}";
            if (uri != initialUri && uri.AbsoluteUri != browser && uri.AbsoluteUri != attachment)
                throw new InvalidDataException("Gitee 更新下载不能跳转到其他发行版或附件。");
        }
        else
        {
            // Gitee normalizes stored CDN object names to lowercase, while API/display names preserve case.
            var storedNames = "(?:" + Regex.Escape(name) + "|" + Regex.Escape(name.ToLowerInvariant()) + ")";
            if (!Regex.IsMatch(uri.AbsolutePath, @"^/attach_file/[0-9]+/" + storedNames + "$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Gitee 更新下载 CDN 路径或附件名称无效。");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2 || parts[0] is not ("token" or "ts" or "attname") || !seen.Add(parts[0]) || string.IsNullOrEmpty(parts[1]) ||
                    (parts[0] == "attname" && Uri.UnescapeDataString(parts[1]) != name))
                    throw new InvalidDataException("Gitee 更新下载 CDN 参数无效。");
            }
        }
    }

    private static void ValidateResponseUri(HttpResponseMessage response, Uri requested)
    {
        var actual = response.RequestMessage?.RequestUri ?? requested;
        if (actual != requested) throw new InvalidDataException("更新请求发生了未经验证的自动重定向。");
    }

    private static void EnsureSuccess(HttpResponseMessage response, Uri requested)
    {
        try { response.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) { throw InstallerNetwork.WithContext(ex, "HM AppKit 更新响应", response.RequestMessage?.RequestUri ?? requested); }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is long length && length > limit) throw new InvalidDataException("更新信息超过大小限制。");
        await using var input = await OpenBodyAsync(response, token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await ReadBodyAsync(input, buffer, response, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("更新信息超过大小限制。");
            output.Write(buffer, 0, count);
        }
        if (response.Content.Headers.ContentLength is long expected)
        {
            if (output.Length > expected) throw new InvalidDataException("更新信息响应超过声明长度。");
            if (output.Length < expected)
                throw InstallerNetwork.ReadFailure(new EndOfStreamException("更新信息响应不完整。"), "HM AppKit 更新信息读取", response.RequestMessage?.RequestUri);
        }
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, string stage, CancellationToken token)
    {
        try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        { throw InstallerNetwork.WithContext(ex, stage, request.RequestUri); }
    }

    private static async Task<Stream> OpenBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        try { return await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) { throw InstallerNetwork.WithContext(ex, "HM AppKit 更新响应读取", response.RequestMessage?.RequestUri); }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        { throw InstallerNetwork.ReadFailure(ex, "HM AppKit 更新响应读取", response.RequestMessage?.RequestUri); }
    }

    private static async Task<int> ReadBodyAsync(Stream input, byte[] buffer, HttpResponseMessage response, CancellationToken token)
    {
        try { return await input.ReadAsync(buffer, token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) { throw InstallerNetwork.WithContext(ex, "HM AppKit 更新响应读取", response.RequestMessage?.RequestUri); }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        { throw InstallerNetwork.ReadFailure(ex, "HM AppKit 更新响应读取", response.RequestMessage?.RequestUri); }
    }

    private sealed class RetryProgress(IProgress<SelfUpdateProgress> progress, long total) : IProgress<string>
    {
        public void Report(string value) => progress.Report(new(0, total, value));
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新暂存目录不能位于链接或重解析点内。");
    }

    private static void TryDeleteOwnedStage(string stage, string root)
    {
        try
        {
            if (Path.GetDirectoryName(stage) != root || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _)) return;
            EnsureNoReparsePoints(stage);
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidDataException) { }
    }

    public void Dispose() => _http.Dispose();
}

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FoxDeploy.Core;

if (args.Length != 0 && args[0] == "--live-check")
{
    var currentVersion = args.Length > 1 ? args[1] : "0.6.0";
    using var client = new SelfUpdateClient();
    var release = await client.CheckForUpdateAsync(currentVersion);
    Console.WriteLine(JsonSerializer.Serialize(new { CurrentVersion = currentVersion, UpdateAvailable = release is not null, Release = release }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

if (args.Length != 0 && args[0] == "--live-stage")
{
    var root = args.Length > 1 ? Path.GetFullPath(args[1]) : new SelfUpdateOptions().StagingRoot;
    using var client = new SelfUpdateClient(new SelfUpdateOptions { StagingRoot = root });
    var release = await client.CheckForUpdateAsync("0.0.0") ?? throw new InvalidOperationException("No stable release found.");
    var package = await client.DownloadAndStageAsync(release);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        release.Version, release.Tag, release.PackageBytes, release.ReleasePage,
        package.StageDirectory, package.ExecutablePath, package.Sha256,
        FileCount = package.RelativeFiles.Count, Executed = false
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

var failures = new List<string>();
var passed = 0;
await Test("Stable version normalization preserves original v0.5 compatibility", () =>
{
    Check(SelfUpdateVersion.TryParse("v0.5", out var original), "Original version rejected.");
    Check(SelfUpdateVersion.TryParse("0.5.0.0", out var full) && original == full, "Equivalent versions differ.");
    Check(SelfUpdateVersion.TryParse("0.6.0+abc123", out var current) && current.CompareTo(original) > 0, "Informational metadata or ordering incorrect.");
    Check(SelfUpdateVersion.TryParse("v0.10", out var newer) && newer.CompareTo(new(0, 9)) > 0, "Comparison must be numeric.");
    Check(SelfUpdateVersion.TryParse("2.0.0.1", out var revision) && revision.CompareTo(new(2, 0)) > 0, "Revision ignored.");
    Check(original.ToString() == "0.5.0", "Display version not normalized.");
    foreach (var malformed in new[] { "v0.6-beta", "0.6.0-rc.1", "", "0", "0..6", "-1.2", "1.2.3.4.5", "999999999999.1", "1.2/evil" })
        Check(!SelfUpdateVersion.TryParse(malformed, out _), "Unexpected stable version: " + malformed);
    return Task.CompletedTask;
});
await Test("Latest stable release discovers exact trusted assets and API digest", async () =>
{
    using var fixture = new Fixture();
    var release = await fixture.Client.CheckForUpdateAsync("v0.5");
    Check(release is { Version: "0.6.0", Tag: "v0.6" } && release.PackageBytes == fixture.Package.Length && release.ApiSha256 == Hash(fixture.Package), "Release fields incorrect.");
    Check(fixture.Requests.Single().Uri == "https://api.github.com/repos/19016563235/HM-AppKit/releases/latest", "Wrong API repository.");
    Check(fixture.Requests[0].UserAgent.Contains("HM-AppKit"), "GitHub requires a User-Agent.");
});
await Test("Equal and older releases are never offered", async () =>
{
    using var fixture = new Fixture();
    Check(await fixture.Client.CheckForUpdateAsync("0.6.0.0") is null, "Equivalent current version offered.");
    Check(await fixture.Client.CheckForUpdateAsync("0.7.0") is null, "Downgrade offered.");
});
await Test("Draft, prerelease, and preview tags are ignored", async () =>
{
    foreach (var property in new[] { "draft", "prerelease", "tag_name" })
    {
        using var fixture = new Fixture();
        fixture.Metadata[property] = property == "tag_name" ? JsonValue.Create("v0.7-rc.1") : JsonValue.Create(true);
        Check(await fixture.Client.CheckForUpdateAsync("0.5") is null, "Draft or prerelease offered.");
    }
});
await Test("404 repository without a stable release returns no update", async () =>
{
    using var fixture = new Fixture(); fixture.ApiStatus = HttpStatusCode.NotFound;
    Check(await fixture.Client.CheckForUpdateAsync("0.5") is null, "Missing release must not block startup.");
});
await Test("Rate limits fail without retry loops", async () =>
{
    using var fixture = new Fixture(); fixture.ApiStatus = HttpStatusCode.Forbidden;
    await Throws<HttpRequestException>(() => fixture.Client.CheckForUpdateAsync("0.5"));
    Check(fixture.Requests.Count == 1, "Rate-limited request retried repeatedly.");
});
await Test("Offline network errors propagate without writing configuration or cache", async () =>
{
    using var fixture = new Fixture(); fixture.Override = (_, _) => throw new HttpRequestException("offline");
    await Throws<HttpRequestException>(() => fixture.Client.CheckForUpdateAsync("0.5"));
    fixture.AssertPreserved();
    Check(!Directory.Exists(fixture.StagingRoot), "Checking should not create a stage.");
});
await Test("Conditional API request reuses valid ETag JSON on 304", async () =>
{
    using var fixture = new Fixture();
    Check(await fixture.Client.CheckForUpdateAsync("0.5") is not null, "Initial release missing.");
    fixture.ApiStatus = HttpStatusCode.NotModified;
    Check(await fixture.Client.CheckForUpdateAsync("0.5") is not null, "304 lost known release.");
    Check(fixture.Requests[^1].ETag == "\"release-1\"", "No conditional header.");
    Check(await fixture.Client.CheckForUpdateAsync("0.6") is null, "Cached JSON bypassed version comparison.");
});
await Test("Unsolicited 304 cannot invent a cached update", async () =>
{
    using var fixture = new Fixture(); fixture.ApiStatus = HttpStatusCode.NotModified;
    await Throws<InvalidDataException>(() => fixture.Client.CheckForUpdateAsync("0.5"));
});
await Test("Metadata response is size bounded", async () =>
{
    using var fixture = new Fixture(); fixture.Metadata["body"] = new string('a', 1024 * 1024);
    await Throws<InvalidDataException>(() => fixture.Client.CheckForUpdateAsync("0.5"));
});
await Test("Check cancellation interrupts HTTP without a stage", async () =>
{
    using var fixture = new Fixture();
    fixture.Override = async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(HttpStatusCode.OK); };
    using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
    await Throws<OperationCanceledException>(() => fixture.Client.CheckForUpdateAsync("0.5", stop.Token));
    Check(!Directory.Exists(fixture.StagingRoot), "Canceled check created a stage.");
});
await Test("Foreign release page, duplicate assets and missing checksum are rejected", async () =>
{
    foreach (var variation in new[] { "foreign-page", "duplicate", "missing-checksum", "malformed-digest" })
    {
        using var fixture = new Fixture();
        if (variation == "foreign-page") fixture.Metadata["html_url"] = "https://github.com/attacker/HM-AppKit/releases/tag/v0.6";
        if (variation == "duplicate") fixture.Assets.Add(fixture.Assets[0]!.DeepClone());
        if (variation == "missing-checksum") fixture.Assets.RemoveAt(1);
        if (variation == "malformed-digest") fixture.Assets[0]!["digest"] = "md5:abc";
        await Throws<InvalidDataException>(() => fixture.Client.CheckForUpdateAsync("0.5"));
    }
});
await Test("Asset URL must exactly belong to selected repository, tag and filename", async () =>
{
    foreach (var url in new[]
    {
        "http://github.com/19016563235/HM-AppKit/releases/download/v0.6/HM-AppKit-win-x64.zip",
        "https://github.com.evil.test/19016563235/HM-AppKit/releases/download/v0.6/HM-AppKit-win-x64.zip",
        "https://github.com/attacker/HM-AppKit/releases/download/v0.6/HM-AppKit-win-x64.zip",
        "https://github.com/19016563235/HM-AppKit/releases/download/v0.5/HM-AppKit-win-x64.zip",
        "https://github.com/19016563235/HM-AppKit/releases/download/v0.6/HM-AppKit-win-x64.zip?alternate=1"
    })
    {
        using var fixture = new Fixture(); fixture.Assets[0]!["browser_download_url"] = url;
        await Throws<InvalidDataException>(() => fixture.Client.CheckForUpdateAsync("0.5"));
    }
});
await Test("Verified download stages only distribution files outside app and preserves state", async () =>
{
    using var fixture = new Fixture();
    var progress = new List<SelfUpdateProgress>();
    var package = await fixture.Client.DownloadAndStageAsync(await fixture.Release(), new InlineProgress<SelfUpdateProgress>(progress.Add));
    Check(Guid.TryParseExact(Path.GetFileName(package.StageDirectory), "N", out _) && Path.GetDirectoryName(package.StageDirectory) == fixture.StagingRoot, "Stage not owned GUID child.");
    Check(package.ExtractedDirectory == Path.Combine(package.StageDirectory, "payload") && File.Exists(package.ZipPath), "Unexpected stage layout.");
    Check(package.Sha256 == Hash(fixture.Package) && fixture.Verifications == 1, "Hash/productversion verification skipped.");
    Check(package.RelativeFiles.Count == 3 && File.Exists(package.ExecutablePath), "Payload missing files.");
    Check(progress.Any(item => item.ReceivedBytes > 0 && item.Percentage == 100), "No download progress.");
    fixture.AssertPreserved();
});
await Test("Older published packages without API digest still require checksum", async () =>
{
    using var fixture = new Fixture(); fixture.Assets[0]!["digest"] = null;
    var package = await fixture.Client.DownloadAndStageAsync(await fixture.Release());
    Check(package.Sha256 == Hash(fixture.Package), "Checksum not enforced for older GitHub release.");
});
await Test("API and checksum disagreement rejects before package download", async () =>
{
    using var fixture = new Fixture(); fixture.Checksum = new string('0', 64) + "  HM-AppKit-win-x64.zip\n";
    await Throws<InvalidDataException>(async () => await fixture.Client.DownloadAndStageAsync(await fixture.Release()));
    Check(!fixture.Requests.Any(request => request.Uri.EndsWith(".zip", StringComparison.Ordinal)), "Package downloaded despite metadata disagreement.");
    fixture.AssertNoStages();
});
await Test("Tampered package fails SHA-256 and removes only its failed stage", async () =>
{
    using var fixture = new Fixture(); var release = await fixture.Release();
    fixture.Package[^1] ^= 1;
    await Throws<InvalidDataException>(() => fixture.Client.DownloadAndStageAsync(release));
    fixture.AssertNoStages(); fixture.AssertPreserved();
});
await Test("Wrong checksum filename or duplicate checksum lines are rejected", async () =>
{
    foreach (var text in new[] { Hash(Fixture.ValidArchive()) + "  other.zip\n", new string('0', 64), new string('0', 64) + "  HM-AppKit-win-x64.zip\n" + new string('0', 64) + "  HM-AppKit-win-x64.zip\n" })
    {
        using var fixture = new Fixture(); fixture.Checksum = text;
        await Throws<InvalidDataException>(async () => await fixture.Client.DownloadAndStageAsync(await fixture.Release()));
        fixture.AssertNoStages();
    }
});
await Test("Content length mismatch rejects an incomplete package", async () =>
{
    using var fixture = new Fixture(); var release = await fixture.Release();
    fixture.Package = fixture.Package[..^1];
    await Throws<InvalidDataException>(() => fixture.Client.DownloadAndStageAsync(release)); fixture.AssertNoStages();
});
await Test("Allowlisted GitHub CDN redirects download correctly", async () =>
{
    using var fixture = new Fixture(); fixture.RedirectHost = "release-assets.githubusercontent.com";
    var package = await fixture.Client.DownloadAndStageAsync(await fixture.Release());
    Check(File.Exists(package.ExecutablePath) && fixture.Requests.Any(request => request.Uri.StartsWith("https://release-assets.githubusercontent.com/", StringComparison.Ordinal)), "CDN not followed.");
});
await Test("Untrusted redirect is rejected before sending a request to foreign host", async () =>
{
    using var fixture = new Fixture(); fixture.RedirectHost = "attacker.test";
    await Throws<InvalidDataException>(async () => await fixture.Client.DownloadAndStageAsync(await fixture.Release()));
    Check(!fixture.Requests.Any(request => request.Uri.Contains("attacker.test", StringComparison.Ordinal)), "Request escaped allowlist."); fixture.AssertNoStages();
});
await Test("Injected automatic redirects from a handler are rejected", async () =>
{
    using var fixture = new Fixture();
    fixture.Override = (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        RequestMessage = new(HttpMethod.Get, "https://attacker.test/release"), Content = new ByteArrayContent(Encoding.UTF8.GetBytes(fixture.Metadata.ToJsonString()))
    });
    await Throws<InvalidDataException>(() => fixture.Client.CheckForUpdateAsync("0.5"));
});
await Test("Download cancellation cleans stage and leaves original configuration/cache", async () =>
{
    using var fixture = new Fixture(); using var stop = new CancellationTokenSource();
    var progress = new InlineProgress<SelfUpdateProgress>(item => { if (item.ReceivedBytes > 0) stop.Cancel(); });
    await Throws<OperationCanceledException>(async () => await fixture.Client.DownloadAndStageAsync(await fixture.Release(), progress, stop.Token));
    fixture.AssertNoStages(); fixture.AssertPreserved();
});
foreach (var name in new[] { "../settings.json", "C:/escape.exe", "docs/../../evil.md", "docs\\escape.md", "docs/CON.md", "docs/foo:stream.md", "docs/evil. /file.md", "Local Installer/vendor.exe", "settings.json", "logs/session.md", ".git/config", "extra.exe", "docs/payload.dll" })
    await Test("Unsafe ZIP path rejected: " + name, async () =>
    {
        using var fixture = new Fixture(archive: Fixture.Archive((name, "bad", 0)));
        await Throws<InvalidDataException>(async () => await fixture.Client.DownloadAndStageAsync(await fixture.Release())); fixture.AssertNoStages();
    });
await Test("Case-insensitive duplicate files are rejected", async () =>
{
    using var fixture = new Fixture(archive: Fixture.Archive(("docs/README.md", "first", 0), ("docs/readme.md", "second", 0)));
    await Throws<InvalidDataException>(async () => await fixture.Client.DownloadAndStageAsync(await fixture.Release())); fixture.AssertNoStages();
});
await Test("ZIP symlinks and Windows reparse entries are rejected", async () =>
{
    foreach (var attribute in new[] { unchecked((int)0xA1FF0000), (int)FileAttributes.ReparsePoint })
    {
        using var fixture = new Fixture(archive: Fixture.Archive(("docs/link.md", "target", attribute)));
        await Throws<InvalidDataException>(async () => await fixture.Client.DownloadAndStageAsync(await fixture.Release())); fixture.AssertNoStages();
    }
});
await Test("ZIP extraction budget is enforced before writing payload", async () =>
{
    using var fixture = new Fixture(options: root => new SelfUpdateOptions { StagingRoot = root, MaximumExtractedBytes = 10 });
    await Throws<InvalidDataException>(async () => await fixture.Client.DownloadAndStageAsync(await fixture.Release())); fixture.AssertNoStages();
});
await Test("ZIP entry count budget is enforced", async () =>
{
    using var fixture = new Fixture(options: root => new SelfUpdateOptions { StagingRoot = root, MaximumArchiveEntries = 2 });
    await Throws<InvalidDataException>(async () => await fixture.Client.DownloadAndStageAsync(await fixture.Release())); fixture.AssertNoStages();
});
await Test("Production verifier rejects fake EXE even with valid archive checksum", async () =>
{
    using var fixture = new Fixture(productionVerifier: true);
    await Throws<InvalidDataException>(async () => await fixture.Client.DownloadAndStageAsync(await fixture.Release())); fixture.AssertNoStages();
});
await Test("Product version mismatch rejects validly downloaded package", async () =>
{
    using var fixture = new Fixture(); fixture.VersionMismatch = true;
    await Throws<InvalidDataException>(async () => await fixture.Client.DownloadAndStageAsync(await fixture.Release())); fixture.AssertNoStages();
});
await Test("Update metadata retries transient HTTP failure within three attempts", async () =>
{
    using var fixture = new Fixture();
    fixture.Override = (request, _) =>
    {
        fixture.Override = null;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request });
    };
    Check(await fixture.Release() is not null && fixture.Requests.Count == 2, "Metadata did not recover with one retry.");
});
await Test("Update metadata retries an incomplete response body", async () =>
{
    using var fixture = new Fixture();
    fixture.Override = (request, _) =>
    {
        fixture.Override = null;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent([123]) };
        response.Content.Headers.ContentLength = 100;
        return Task.FromResult(response);
    };
    Check(await fixture.Release() is not null && fixture.Requests.Count == 2, "Metadata EOF did not retry.");
});
await Test("Update metadata exceeding its declared length is rejected without retry", async () =>
{
    using var fixture = new Fixture();
    fixture.Override = (request, _) =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(fixture.Metadata.ToJsonString()) };
        response.Content.Headers.ContentLength = 1;
        return Task.FromResult(response);
    };
    await Throws<InvalidDataException>(() => fixture.Client.CheckForUpdateAsync("0.5"));
    Check(fixture.Requests.Count == 1, "Invalid oversized metadata was retried.");
    fixture.AssertNoStages(); fixture.AssertPreserved();
});
foreach (var failureMode in new[] { "reset", "eof" })
    await Test("Interrupted update package restarts in a clean stage: " + failureMode, async () =>
    {
        using var fixture = new Fixture(); var release = await fixture.Release();
        var downloads = 0;
        fixture.Override = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(fixture.Checksum) });
            downloads++;
            HttpContent content = downloads == 1
                ? failureMode == "reset" ? new StreamContent(new BrokenReadStream(fixture.Package)) : new ByteArrayContent(fixture.Package[..^1])
                : new ByteArrayContent(fixture.Package);
            content.Headers.ContentLength = fixture.Package.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content });
        };
        var messages = new List<SelfUpdateProgress>();
        var staged = await fixture.Client.DownloadAndStageAsync(release, new InlineProgress<SelfUpdateProgress>(messages.Add));
        Check(downloads == 2 && fixture.Verifications == 1 && staged.Sha256 == Hash(fixture.Package), "Recovery did not verify exactly one complete package.");
        Check(Directory.GetDirectories(fixture.StagingRoot).Length == 1 && messages.Any(item => item.Message.Contains("重试", StringComparison.Ordinal)), "Failed stage survived or retry not reported.");
        fixture.AssertPreserved();
    });
await Test("Update package retries stop at three and preserve configuration/cache", async () =>
{
    using var fixture = new Fixture(); var release = await fixture.Release(); var downloads = 0;
    fixture.Override = (request, _) =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(fixture.Checksum) });
        downloads++;
        throw new HttpRequestException("connection reset");
    };
    await Throws<HttpRequestException>(() => fixture.Client.DownloadAndStageAsync(release));
    Check(downloads == 3 && fixture.Verifications == 0, "Update retry was not bounded.");
    fixture.AssertNoStages(); fixture.AssertPreserved();
});
await Test("Update checksum mismatch is not retried", async () =>
{
    using var fixture = new Fixture(); var release = await fixture.Release(); fixture.Package[^1] ^= 1;
    await Throws<InvalidDataException>(() => fixture.Client.DownloadAndStageAsync(release));
    Check(fixture.Requests.Count(request => request.Uri.EndsWith(".zip", StringComparison.Ordinal)) == 1, "Invalid package was fetched again.");
    fixture.AssertNoStages(); fixture.AssertPreserved();
});
await Test("Cancellation during updater backoff stops before another request", async () =>
{
    using var stop = new CancellationTokenSource();
    using var fixture = new Fixture(retryDelay: (_, token) => { stop.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
    fixture.Override = (_, _) => throw new HttpRequestException("offline");
    await Throws<OperationCanceledException>(() => fixture.Client.CheckForUpdateAsync("0.5", stop.Token));
    Check(fixture.Requests.Count == 1, "Canceled backoff continued to send requests.");
    fixture.AssertNoStages(); fixture.AssertPreserved();
});

Console.WriteLine($"{passed} passed; {failures.Count} failed.");
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception exception) { failures.Add(name + ": " + exception); Console.WriteLine("FAIL " + name + ": " + exception.Message); }
}
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
sealed class Fixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HM-AppKit.Update.Tests-" + Guid.NewGuid().ToString("N"));
    public string StagingRoot => Path.Combine(_root, "AppData", "HM AppKit", "Updates");
    private string Config => Path.Combine(_root, "AppData", "HM AppKit", "settings.json");
    private string Cache => Path.Combine(_root, "app", "Local Installer", "vendor.exe");
    public SelfUpdateClient Client { get; }
    public byte[] Package { get; set; }
    public string Checksum { get; set; }
    public JsonObject Metadata { get; }
    public JsonArray Assets => Metadata["assets"]!.AsArray();
    public HttpStatusCode ApiStatus { get; set; } = HttpStatusCode.OK;
    public string? RedirectHost { get; set; }
    public List<RequestRecord> Requests { get; } = [];
    public int Verifications { get; private set; }
    public bool VersionMismatch { get; set; }
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Override { get; set; }

    public Fixture(byte[]? archive = null, Func<string, SelfUpdateOptions>? options = null, bool productionVerifier = false,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        Package = archive ?? ValidArchive();
        var hash = Convert.ToHexString(SHA256.HashData(Package)).ToLowerInvariant();
        Checksum = hash + "  HM-AppKit-win-x64.zip\n";
        Metadata = new JsonObject
        {
            ["draft"] = false, ["prerelease"] = false, ["tag_name"] = "v0.6",
            ["html_url"] = "https://github.com/19016563235/HM-AppKit/releases/tag/v0.6", ["body"] = "A new release",
            ["assets"] = new JsonArray(
                new JsonObject { ["name"] = SelfUpdateClient.PackageAssetName, ["state"] = "uploaded", ["size"] = Package.Length, ["digest"] = "sha256:" + hash, ["browser_download_url"] = "https://github.com/19016563235/HM-AppKit/releases/download/v0.6/" + SelfUpdateClient.PackageAssetName },
                new JsonObject { ["name"] = SelfUpdateClient.ChecksumAssetName, ["state"] = "uploaded", ["size"] = Checksum.Length, ["browser_download_url"] = "https://github.com/19016563235/HM-AppKit/releases/download/v0.6/" + SelfUpdateClient.ChecksumAssetName })
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Config)!); Directory.CreateDirectory(Path.GetDirectoryName(Cache)!);
        File.WriteAllText(Config, "original-config"); File.WriteAllText(Cache, "original-cache");
        Client = new(options?.Invoke(StagingRoot) ?? new SelfUpdateOptions { StagingRoot = StagingRoot }, new Handler(Respond),
            productionVerifier ? null : (path, version) =>
            {
                Verifications++;
                if (!File.Exists(path) || version != "0.6.0" || VersionMismatch) throw new InvalidDataException("productversion mismatch");
            }, retryDelay ?? ((_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }));
    }

    private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken token)
    {
        Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Headers.IfNoneMatch.FirstOrDefault()?.ToString(), request.Headers.UserAgent.ToString()));
        if (Override is not null) return await Override(request, token);
        if (request.RequestUri.Host == "api.github.com")
        {
            var response = new HttpResponseMessage(ApiStatus) { RequestMessage = request, Content = new StringContent(Metadata.ToJsonString()) };
            response.Headers.ETag = new EntityTagHeaderValue("\"release-1\"");
            return response;
        }
        if (RedirectHost is not null && request.RequestUri.Host == "github.com")
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
            response.Headers.Location = new Uri("https://" + RedirectHost + "/package/" + Path.GetFileName(request.RequestUri.AbsolutePath));
            return response;
        }
        return new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(Checksum) : Package)
        };
    }
    public async Task<SelfUpdateRelease> Release() => await Client.CheckForUpdateAsync("0.5") ?? throw new InvalidOperationException("Missing fixture release.");
    public void AssertPreserved()
    {
        if (File.ReadAllText(Config) != "original-config" || File.ReadAllText(Cache) != "original-cache") throw new InvalidOperationException("Existing config or cache changed.");
    }
    public void AssertNoStages()
    {
        if (Directory.Exists(StagingRoot) && Directory.EnumerateFileSystemEntries(StagingRoot).Any()) throw new InvalidOperationException("Failed update left staged files.");
    }
    public static byte[] ValidArchive() => Archive(("README.md", "readme", 0));
    public static byte[] Archive(params (string Name, string Content, int Attributes)[] extras)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add("HM AppKit.exe", "MZ-fake-test-only", 0); Add("Data/catalog.json", "{\"apps\":[]}", 0);
            foreach (var extra in extras) Add(extra.Name, extra.Content, extra.Attributes);
            void Add(string name, string content, int attributes)
            {
                var entry = zip.CreateEntry(name); entry.ExternalAttributes = attributes;
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(content);
            }
        }
        return buffer.ToArray();
    }
    public void Dispose()
    {
        Client.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
}
sealed record RequestRecord(string Uri, string? ETag, string UserAgent);

sealed class BrokenReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
{
    private bool _read;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_read) throw new IOException("connection reset", new System.Net.Sockets.SocketException(10054));
        _read = true;
        return base.ReadAsync(buffer[..Math.Min(buffer.Length, 16)], cancellationToken);
    }
}

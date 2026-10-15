using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FoxDeploy.Core;

if (args.Length > 0 && args[0] == "--live-cache") return await LiveCache(args);
if (args.Length > 0 && args[0] == "--resolve-only") return await ResolveOnly(args);

var failures = new List<string>();
var passed = 0;
await Test("Persistent package survives disposal and records verified identity", async () =>
{
    using var fixture = new Fixture();
    string path;
    await using (var payload = await fixture.Prepare())
    {
        path = payload.FilePath;
        Check(payload.IsCached && payload.LatestConfirmed && payload.Publisher == "Trusted Publisher", "Verified cache metadata missing.");
        Check(Path.GetFileName(path) == "setup.exe" && path.StartsWith(fixture.Library, StringComparison.OrdinalIgnoreCase), "Must preserve vendor filename beside the app.");
        Check(payload.Sha256 == Hash(fixture.Server.Bytes), "Hash must describe exact downloaded bytes.");
    }
    Check(File.Exists(path), "Persistent package was removed on disposal.");
    var index = fixture.Index();
    Check(index["ETag"]!.GetValue<string>() == "\"v1\"" && index["Bytes"]!.GetValue<long>() == fixture.Server.Bytes.Length, "HTTP validators or size missing.");
    Check(index["Sha256"]!.GetValue<string>() == Hash(fixture.Server.Bytes) && index["OwnerId"]!.GetValue<string>().Length == 32, "Integrity/ownership index missing.");
});
await Test("Conditional 304 reuses bytes without reading response body", async () =>
{
    using var fixture = new Fixture();
    string original;
    await using (var payload = await fixture.Prepare()) original = payload.FilePath;
    var reads = fixture.Server.BodyReads;
    await using var cached = await fixture.Prepare();
    Check(cached.FilePath == original && fixture.Server.BodyReads == reads && fixture.Server.Conditionals > 0, "Unchanged cache was downloaded again.");
});
await Test("200 with unchanged strong validators does not read large payload", async () =>
{
    using var fixture = new Fixture();
    await using (var payload = await fixture.Prepare()) { }
    fixture.Server.IgnoreConditional = true;
    var reads = fixture.Server.BodyReads;
    await using var cached = await fixture.Prepare();
    Check(fixture.Server.BodyReads == reads, "Identical ETag/size response body should not be downloaded.");
});
await Test("Metadata 304 still checks stable payload URL for new bytes", async () =>
{
    using var fixture = new Fixture(metadata: true);
    string original;
    await using (var payload = await fixture.Prepare()) original = payload.FilePath;
    fixture.Server.Tag = "\"v2\""; fixture.Server.Bytes = Encoding.UTF8.GetBytes("signed:a different current installer");
    string updated;
    await using (var payload = await fixture.Prepare()) updated = payload.FilePath;
    Check(updated != original && !File.Exists(original) && fixture.Server.Metadata304s > 0, "Stable URL update was missed or previous managed package remained.");
    Check(File.ReadAllBytes(updated).SequenceEqual(fixture.Server.Bytes), "Incorrect updated bytes.");
});
await Test("Latest redirect is followed before applying final payload validators", async () =>
{
    using var fixture = new Fixture(redirect: true);
    await using (var payload = await fixture.Prepare()) { }
    var reads = fixture.Server.BodyReads;
    await using var cached = await fixture.Prepare();
    Check(fixture.Server.Redirects == 2 && fixture.Server.Conditionals > 0 && fixture.Server.BodyReads == reads, "Redirected cache validation must avoid redownloading.");
});
await Test("Last-Modified and length validate a source without ETag", async () =>
{
    using var fixture = new Fixture();
    fixture.Server.Tag = null;
    await using (var payload = await fixture.Prepare()) { }
    var reads = fixture.Server.BodyReads;
    await using var cached = await fixture.Prepare();
    Check(fixture.Server.ModifiedConditionals > 0 && fixture.Server.BodyReads == reads, "Last-Modified conditional validation missing.");
});
await Test("No validator means content is hashed rather than trusting equal size", async () =>
{
    using var fixture = new Fixture();
    fixture.Server.Tag = null; fixture.Server.LastModified = null;
    string original;
    await using (var payload = await fixture.Prepare()) original = payload.FilePath;
    fixture.Server.Bytes = Encoding.UTF8.GetBytes("signed:version-two");
    string updated;
    await using (var payload = await fixture.Prepare()) updated = payload.FilePath;
    Check(updated != original && !File.Exists(original), "Equal-length data must not be assumed unchanged.");
});
await Test("Bad replacement preserves prior verified package and index", async () =>
{
    using var fixture = new Fixture();
    string original;
    await using (var payload = await fixture.Prepare()) original = payload.FilePath;
    var index = File.ReadAllText(fixture.IndexPath);
    fixture.Server.Bytes = Encoding.UTF8.GetBytes("unsigned:untrusted replacement"); fixture.Server.Tag = "\"bad\"";
    await using var fallback = await fixture.Prepare();
    Check(fallback.FilePath == original && !fallback.LatestConfirmed && File.ReadAllText(fixture.IndexPath) == index, "Invalid update must not replace known package or claim latest.");
    Check(!Directory.EnumerateDirectories(Path.Combine(fixture.Library, fixture.App.Id), ".pending-*").Any(), "Failed download temp was not cleaned.");
});
await Test("Offline fallback rechecks cached hash and signature", async () =>
{
    using var fixture = new Fixture();
    await using (var payload = await fixture.Prepare()) { }
    var signatures = fixture.SignatureChecks;
    fixture.Server.Offline = true;
    await using var payload2 = await fixture.Prepare();
    Check(!payload2.LatestConfirmed && fixture.SignatureChecks > signatures && fixture.Log.Lines.Any(line => line.Contains("未能确认官网最新版本")), "Offline fallback lacks verification or disclosure.");
});
await Test("Corrupted cached bytes are never used offline", async () =>
{
    using var fixture = new Fixture();
    string path;
    await using (var payload = await fixture.Prepare()) path = payload.FilePath;
    File.WriteAllText(path, "signed:tampered cached bytes"); fixture.Server.Offline = true;
    await Throws<HttpRequestException>(async () => { await using var payload = await fixture.Prepare(); });
});
await Test("KeepInstallers off uses temporary bytes and keeps historical package", async () =>
{
    using var fixture = new Fixture();
    string historical;
    await using (var payload = await fixture.Prepare()) historical = payload.FilePath;
    fixture.Server.Bytes = Encoding.UTF8.GetBytes("signed:a temporary version"); fixture.Server.Tag = "\"temp\"";
    string temporary;
    await using (var payload = await fixture.Provider.PrepareAsync(fixture.App, new DeploymentSettings { KeepInstallers = false }, fixture.Log, default))
    { temporary = payload.FilePath; Check(!payload.IsCached && temporary.StartsWith(fixture.Temporary, StringComparison.OrdinalIgnoreCase), "Temporary option wrote persistent storage."); }
    Check(!File.Exists(temporary) && File.Exists(historical), "Temporary disposal changed historical cache.");
});
await Test("Startup only maintains cached online apps and preserves caller local mode", async () =>
{
    using var fixture = new Fixture();
    var options = new DeploymentSettings { SourceMode = InstallationSourceMode.Local, LocalInstallerDirectory = @"F:\My Installers" };
    await fixture.Provider.CheckForUpdatesAsync([fixture.App], options, fixture.Log);
    Check(fixture.Server.Requests == 0 && !Directory.Exists(fixture.Library), "Startup downloaded uncached app.");
    await using (var payload = await fixture.Prepare()) { }
    await fixture.Provider.CheckForUpdatesAsync([fixture.App, fixture.App with { Id = "uncached" }], options, fixture.Log);
    Check(options.SourceMode == InstallationSourceMode.Local && options.LocalInstallerDirectory == @"F:\My Installers", "Caller settings were modified.");
    Check(!Directory.Exists(Path.Combine(fixture.Library, "uncached")), "Startup populated an unselected app.");
});
await Test("Update waits until prepared installation releases package", async () =>
{
    using var fixture = new Fixture();
    var payload = await fixture.Prepare();
    var original = payload.FilePath;
    fixture.Server.Bytes = Encoding.UTF8.GetBytes("signed:concurrent replacement"); fixture.Server.Tag = "\"concurrent\"";
    var update = fixture.Provider.CheckForUpdatesAsync([fixture.App], new DeploymentSettings(), fixture.Log);
    await Task.Delay(120);
    Check(!update.IsCompleted && File.Exists(original), "Updater raced the active installer lease.");
    await payload.DisposeAsync(); await payload.DisposeAsync();
    await update.WaitAsync(TimeSpan.FromSeconds(5));
    Check(!File.Exists(original), "Released old version was not replaced.");
});
await Test("Cancellation while waiting for active cache does not start download", async () =>
{
    using var fixture = new Fixture();
    await using var first = await fixture.Prepare();
    using var cancellation = new CancellationTokenSource(100);
    var requests = fixture.Server.Requests;
    await Throws<OperationCanceledException>(async () => { await using var next = await fixture.Provider.PrepareAsync(fixture.App, new DeploymentSettings(), fixture.Log, cancellation.Token); });
    Check(fixture.Server.Requests == requests && File.Exists(first.FilePath), "Cancelled waiter touched active package.");
});
await Test("Index path traversal never deletes or opens external source", async () =>
{
    using var fixture = new Fixture();
    await using (var payload = await fixture.Prepare()) { }
    var outside = Path.Combine(fixture.Root, "outside.exe"); File.WriteAllText(outside, "user owned archive");
    var index = fixture.Index(); index["PayloadRelativePath"] = "..\\..\\outside.exe"; File.WriteAllText(fixture.IndexPath, index.ToJsonString());
    await using var replacement = await fixture.Prepare();
    Check(File.ReadAllText(outside) == "user owned archive" && replacement.FilePath != outside, "Traversal cache entry escaped ownership.");
});
await Test("Unowned existing application folder is preserved", async () =>
{
    using var fixture = new Fixture();
    var folder = Path.Combine(fixture.Library, fixture.App.Id); Directory.CreateDirectory(folder);
    var source = Path.Combine(folder, "my-setup.exe"); File.WriteAllText(source, "my own installer");
    await Throws<InvalidDataException>(async () => { await using var payload = await fixture.Prepare(); });
    Check(File.ReadAllText(source) == "my own installer" && fixture.Server.Requests == 0, "Unowned folder was adopted or changed.");
});
await Test("Reparse application folder is rejected before writing through it", async () =>
{
    using var fixture = new Fixture();
    var outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside); Directory.CreateDirectory(fixture.Library);
    var link = Path.Combine(fixture.Library, fixture.App.Id);
    Directory.CreateSymbolicLink(link, outside);
    try
    {
        await Throws<InvalidDataException>(async () => { await using var payload = await fixture.Prepare(); });
        Check(!Directory.EnumerateFileSystemEntries(outside).Any(), "Cache followed a reparse point.");
    }
    finally { Directory.Delete(link); }
});
await Test("Expected manifest hash rejects a wrongly labelled new version", async () =>
{
    using var fixture = new Fixture(manifest: true);
    string original;
    await using (var payload = await fixture.Prepare()) original = payload.FilePath;
    fixture.ExpectedHash = Hash(Encoding.UTF8.GetBytes("signed:a newer manifest installer"));
    fixture.Server.Tag = "\"new\""; fixture.Server.Bytes = Encoding.UTF8.GetBytes("signed:wrongly served other installer");
    await using var fallback = await fixture.Prepare();
    Check(fallback.FilePath == original && !fallback.LatestConfirmed, "Wrong expected hash was accepted as current.");
});
await Test("Changed manifest hash cannot use misleading payload 304", async () =>
{
    using var fixture = new Fixture(manifest: true);
    string original;
    await using (var payload = await fixture.Prepare()) original = payload.FilePath;
    fixture.ExpectedHash = new string('A', 64);
    await using var fallback = await fixture.Prepare();
    Check(fallback.FilePath == original && !fallback.LatestConfirmed, "304 overrode a changed official expected hash.");
});
await Test("Unchanged verified manifest hash avoids payload network request", async () =>
{
    using var fixture = new Fixture(manifest: true);
    await using (var payload = await fixture.Prepare()) { }
    var requests = fixture.Server.Requests;
    await using var cached = await fixture.Prepare();
    Check(fixture.Server.Requests == requests && cached.LatestConfirmed, "Identical authoritative hash needs no payload GET.");
});
await Test("Changed source policy cannot silently reuse an old offline package", async () =>
{
    using var fixture = new Fixture();
    await using (var payload = await fixture.Prepare()) { }
    fixture.Server.Offline = true;
    var changed = fixture.App with { OnlineInstaller = fixture.App.OnlineInstaller! with { DownloadUrl = "https://official.test/other.exe" } };
    await Throws<HttpRequestException>(async () => { await using var payload = await fixture.Provider.PrepareAsync(changed, new DeploymentSettings(), fixture.Log, default); });
});
await Test("Local-source bytes remain user-owned with retention enabled", async () =>
{
    using var fixture = new Fixture();
    var source = Path.Combine(fixture.Root, "local.exe"); await File.WriteAllTextAsync(source, "signed:local source");
    var local = fixture.App with { LocalInstaller = new LocalInstallerDefinition { FileName = "local.exe", Sha256 = Hash(File.ReadAllBytes(source)), AllowedPublishers = ["Trusted Publisher"] } };
    string temporary;
    await using (var payload = await fixture.Provider.PrepareAsync(local, new DeploymentSettings { SourceMode = InstallationSourceMode.Local, LocalInstallerDirectory = fixture.Root }, fixture.Log, default)) temporary = payload.FilePath;
    Check(!File.Exists(temporary) && File.Exists(source) && !Directory.Exists(fixture.Library), "User local archive was moved or removed.");
});
await Test("Unwritable library does not silently redirect retained packages", async () =>
{
    using var fixture = new Fixture();
    File.WriteAllText(fixture.Library, "existing file prevents a directory");
    await Throws<IOException>(async () => { await using var payload = await fixture.Prepare(); });
    Check(File.ReadAllText(fixture.Library).StartsWith("existing file") && fixture.Server.Requests == 0, "Unavailable library fell back or changed user file.");
});
await Test("Official escaped Unicode slash URL parsing is supported", () =>
{
    var definition = new OnlineInstallerDefinition { MetadataUrl = "https://official.test/download", AllowedDownloadHosts = ["official.test"], UrlPattern = "win_link:\"(?<url>https[^\"]+)\"" };
    Check(InstallerPayloadProvider.ParseDownloadUri(definition, "win_link:\"https:\\u002F\\u002Fofficial.test\\u002Fsetup.exe\"").AbsoluteUri == "https://official.test/setup.exe", "Unicode-escaped official URL failed.");
    return Task.CompletedTask;
});

Console.WriteLine($"Cache tests: {passed} passed, {failures.Count} failed.");
return failures.Count == 0 ? 0 : 1;

async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex); Console.WriteLine("FAIL " + name + ": " + ex); }
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

static async Task<int> LiveCache(string[] arguments)
{
    if (arguments.Length < 4) { Console.Error.WriteLine("--live-cache CATALOG LIBRARY_ROOT APP_ID [EVIDENCE_DIRECTORY]"); return 1; }
    var evidence = Path.GetFullPath(arguments.Length > 4 ? arguments[4] : Path.Combine(arguments[2], "live-cache-evidence-" + arguments[3]));
    Directory.CreateDirectory(evidence);
    var log = new LiveLog(Path.Combine(evidence, "live-cache.log"));
    var attempts = new List<LiveCacheAttempt>();
    var reportPath = Path.Combine(evidence, "live-cache.json");
    void Save(string? error = null) => File.WriteAllText(reportPath, JsonSerializer.Serialize(new
    {
        Operation = "Production provider download/cache validation only; no installer execution", AppId = arguments[3],
        LibraryRoot = Path.GetFullPath(arguments[2]), Attempts = attempts, Error = error
    }, new JsonSerializerOptions { WriteIndented = true }));
    try
    {
        var app = AppCatalog.Load(arguments[1]).Single(app => app.Id.Equals(arguments[3], StringComparison.OrdinalIgnoreCase));
        using var provider = new InstallerPayloadProvider(Path.Combine(evidence, "temporary"), arguments[2]);
        for (var index = 0; index < 2; index++)
        {
            LiveCacheAttempt attempt;
            var started = DateTimeOffset.Now;
            log.Report($"Production cache pass {index + 1}; installer execution is disabled in this helper.");
            await using (var payload = await provider.PrepareAsync(app, new DeploymentSettings { SourceMode = InstallationSourceMode.Online, KeepInstallers = true }, log, default))
            {
                await using var stream = File.OpenRead(payload.FilePath);
                var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                if (!sha.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Independent SHA-256 disagrees with provider evidence.");
                attempt = new LiveCacheAttempt(index + 1, payload.FilePath, sha, payload.Version, payload.Publisher, payload.IsCached,
                    payload.LatestConfirmed, stream.Length, false, index == 0 || attempts[0].Path == payload.FilePath, started, DateTimeOffset.Now);
            }
            attempt = attempt with { ExistsAfterDispose = File.Exists(attempt.Path) };
            attempts.Add(attempt); Save();
            Console.WriteLine(JsonSerializer.Serialize(attempt, new JsonSerializerOptions { WriteIndented = true }));
            if (!attempt.ExistsAfterDispose) throw new IOException("Retained installer disappeared on disposal.");
        }
        Console.WriteLine("Evidence: " + reportPath);
        return attempts.All(attempt => attempt.LatestConfirmed) ? 0 : 2;
    }
    catch (Exception ex) { Save(ex.Message); Console.Error.WriteLine(ex); return 1; }
}

static async Task<int> ResolveOnly(string[] arguments)
{
    if (arguments.Length < 3) { Console.Error.WriteLine("--resolve-only CATALOG EVIDENCE_DIRECTORY [APP_ID,APP_ID]"); return 1; }
    var evidence = Path.GetFullPath(arguments[2]); Directory.CreateDirectory(evidence);
    var ids = arguments.Length > 3 ? arguments[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
    var records = new List<object>(); var failed = false;
    var path = Path.Combine(evidence, "source-resolution.json");
    foreach (var app in AppCatalog.Load(arguments[1]).Where(app => ids is null || ids.Contains(app.Id, StringComparer.OrdinalIgnoreCase)))
    {
        var started = DateTimeOffset.Now;
        try
        {
            var profile = app.OnlineInstaller ?? throw new NotSupportedException("No direct online profile.");
            Uri uri; string? hash = null; string? version = null;
            if (!string.IsNullOrWhiteSpace(profile.WingetManifestPath))
            {
                var source = await WingetManifestResolver.ResolveAsync(profile);
                uri = InstallerPayloadProvider.ValidateAllowedUri(source.DownloadUri.AbsoluteUri, profile.AllowedDownloadHosts); hash = source.Sha256; version = source.Version;
            }
            else uri = await InstallerPayloadProvider.ResolveDownloadUriAsync(profile);
            records.Add(new { app.Id, app.Name, State = "Resolved", Url = uri.AbsoluteUri, ExpectedSha256 = hash, Version = version,
                AllowedPublishers = profile.AllowedPublishers, Started = started, Finished = DateTimeOffset.Now, InstallerDownloaded = false, InstallerExecuted = false });
            Console.WriteLine(app.Id + ": " + uri.AbsoluteUri + (version is null ? "" : " / " + version));
        }
        catch (Exception ex) { failed = true; records.Add(new { app.Id, app.Name, State = "Failed", Error = ex.Message, Started = started, Finished = DateTimeOffset.Now, InstallerDownloaded = false, InstallerExecuted = false }); Console.Error.WriteLine(app.Id + ": " + ex.Message); }
        File.WriteAllText(path, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }
    Console.WriteLine("Evidence: " + path);
    return failed ? 1 : 0;
}

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "HMAppKit-CacheTests-" + Guid.NewGuid().ToString("N"));
    public string Library => Path.Combine(Root, "Local Installer");
    public string Temporary => Path.Combine(Root, "temporary");
    public string IndexPath => Path.Combine(Library, App.Id, "index.json");
    public FakeServer Server { get; } = new();
    public Log Log { get; } = new();
    public AppDefinition App { get; }
    public InstallerPayloadProvider Provider { get; }
    public int SignatureChecks;
    public string? ExpectedHash;
    public Fixture(bool metadata = false, bool redirect = false, bool manifest = false)
    {
        Directory.CreateDirectory(Root); Server.Redirect = redirect;
        App = new AppDefinition
        {
            Id = "fixture", Name = "Fixture", FolderName = "Fixture",
            OnlineInstaller = new OnlineInstallerDefinition
            {
                DownloadUrl = metadata || manifest ? "" : redirect ? "https://official.test/latest" : "https://official.test/setup.exe",
                MetadataUrl = metadata ? "https://official.test/metadata" : "", UrlPattern = "\"url\":\"(?<url>[^\"]+)\"",
                WingetManifestPath = manifest ? "manifests/f/Fixture/Fixture" : "", AllowedDownloadHosts = ["official.test"], AllowedPublishers = ["Trusted Publisher"]
            }
        };
        ExpectedHash = Convert.ToHexString(SHA256.HashData(Server.Bytes));
        Provider = new InstallerPayloadProvider(Temporary, Library, Server, (path, publishers) =>
        {
            SignatureChecks++;
            if (!File.ReadAllText(path).StartsWith("signed:", StringComparison.Ordinal) || !publishers.Contains("Trusted Publisher")) throw new InvalidDataException("Fixture publisher rejected.");
            return "Trusted Publisher";
        }, (profile, token) => Task.FromResult(new WingetResolvedInstaller(new Uri("https://official.test/setup.exe"), ExpectedHash!, "1.0.0")));
    }
    public Task<PreparedInstaller> Prepare() => Provider.PrepareAsync(App, new DeploymentSettings(), Log, default);
    public JsonObject Index() => JsonNode.Parse(File.ReadAllText(IndexPath))!.AsObject();
    public void Dispose()
    {
        Provider.Dispose(); Server.Dispose();
        var absolute = Path.GetFullPath(Root);
        if (Path.GetDirectoryName(absolute) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(absolute).StartsWith("HMAppKit-CacheTests-", StringComparison.Ordinal))
            throw new Exception("Refusing unexpected fixture cleanup path.");
        Directory.Delete(absolute, recursive: true);
    }
}
sealed class Log : IProgress<string>
{
    public List<string> Lines { get; } = [];
    public void Report(string value) { lock (Lines) Lines.Add(value); }
}
sealed class FakeServer : HttpMessageHandler
{
    public byte[] Bytes = Encoding.UTF8.GetBytes("signed:version-one");
    public string? Tag = "\"v1\"";
    public DateTimeOffset? LastModified = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    public bool Offline, IgnoreConditional, Redirect;
    public int Requests, BodyReads, Conditionals, ModifiedConditionals, Metadata304s, Redirects;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Requests++;
        if (Offline) throw new HttpRequestException("Fixture offline.");
        if (request.RequestUri!.AbsolutePath == "/latest")
        { Redirects++; var redirect = new HttpResponseMessage(HttpStatusCode.Found); redirect.Headers.Location = new Uri("https://official.test/setup.exe"); return Task.FromResult(redirect); }
        if (request.RequestUri.AbsolutePath == "/metadata")
        {
            if (request.Headers.IfNoneMatch.Any(tag => tag.Tag == "\"metadata-stable\"")) { Metadata304s++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)); }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"url\":\"https://official.test/setup.exe\"}") };
            response.Headers.ETag = new EntityTagHeaderValue("\"metadata-stable\""); return Task.FromResult(response);
        }
        if (request.Headers.IfNoneMatch.Count > 0) Conditionals++;
        if (request.Headers.IfModifiedSince is not null) ModifiedConditionals++;
        if (!IgnoreConditional && (Tag is not null && request.Headers.IfNoneMatch.Any(tag => tag.Tag == Tag) ||
            Tag is null && LastModified is not null && request.Headers.IfModifiedSince == LastModified))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        var content = new CountingContent(Bytes, () => Interlocked.Increment(ref BodyReads));
        content.Headers.LastModified = LastModified;
        var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        if (Tag is not null) result.Headers.ETag = new EntityTagHeaderValue(Tag);
        return Task.FromResult(result);
    }
}
sealed class CountingContent(byte[] bytes, Action read) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) { read(); return stream.WriteAsync(bytes).AsTask(); }
    protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
    protected override Task<Stream> CreateContentReadStreamAsync() { read(); return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)); }
}
sealed record LiveCacheAttempt(int Pass, string Path, string Sha256, string? Version, string? Publisher, bool IsCached,
    bool LatestConfirmed, long Bytes, bool ExistsAfterDispose, bool SamePathAsFirst, DateTimeOffset Started, DateTimeOffset Finished);
sealed class LiveLog(string path) : IProgress<string>
{
    public void Report(string value) { var line = DateTimeOffset.Now.ToString("O") + " " + value; Console.WriteLine(line); File.AppendAllText(path, line + Environment.NewLine); }
}

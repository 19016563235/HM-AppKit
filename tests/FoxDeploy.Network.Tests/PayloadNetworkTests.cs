using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FoxDeploy.Core;

public static class PayloadNetworkTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Mid-body connection reset starts a clean GET and verifies only the complete package", async () =>
        {
            using var fixture = new Fixture();
            fixture.Server.Respond = (_, attempt) =>
            {
                if (attempt > 1) Check(!fixture.Pending.Any(), "Partial first attempt survived until the next request.");
                return fixture.Response(interrupt: attempt == 1);
            };
            await using var payload = await fixture.Prepare();
            Check(fixture.Server.Requests == 2 && fixture.Delays == 1, "Reset did not retry exactly once.");
            Check(File.ReadAllBytes(payload.FilePath).SequenceEqual(fixture.Bytes), "Retry appended or truncated bytes.");
            Check(payload.Sha256 == Hash(fixture.Bytes) && fixture.SignatureChecks == 2, "Unfinished bytes were verified or the final package was not reverified.");
            Check(fixture.Server.DisposedResponses == 2 && !fixture.Pending.Any(), "Responses or partial payloads leaked.");
        });
        await test("HTTP 503 retries and disposes the failed response", async () =>
        {
            using var fixture = new Fixture();
            fixture.Server.Respond = (_, attempt) => attempt == 1 ? fixture.Response(status: HttpStatusCode.ServiceUnavailable) : fixture.Response();
            await using var payload = await fixture.Prepare();
            Check(fixture.Server.Requests == 2 && fixture.Server.DisposedResponses == 2 && fixture.Delays == 1, "503 retry or response disposal failed.");
        });
        await test("Premature installer EOF retries from zero", async () =>
        {
            using var fixture = new Fixture();
            fixture.Server.Respond = (_, attempt) => fixture.Response(shortBody: attempt == 1);
            await using var payload = await fixture.Prepare();
            Check(fixture.Server.Requests == 2 && File.ReadAllBytes(payload.FilePath).SequenceEqual(fixture.Bytes), "Short EOF was accepted or not retried.");
            Check(!fixture.Pending.Any(), "Short EOF left a pending directory.");
        });
        await test("Cancellation during retry delay immediately stops further GETs", async () =>
        {
            using var stop = new CancellationTokenSource();
            using var fixture = new Fixture(delay: (_, token) => { stop.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
            fixture.Server.Respond = (_, _) => fixture.Response(interrupt: true);
            await Throws<OperationCanceledException>(() => fixture.Prepare(stop.Token));
            Check(fixture.Server.Requests == 1 && !fixture.Pending.Any() && !File.Exists(fixture.IndexPath), "Cancellation downloaded again or committed a half package.");
        });
        await test("Caller cancellation while opening the body is not wrapped or retried", async () =>
        {
            using var stop = new CancellationTokenSource();
            using var fixture = new Fixture();
            fixture.Server.Respond = (_, _) => fixture.Response(onOpen: stop.Cancel);
            await Throws<OperationCanceledException>(() => fixture.Prepare(stop.Token));
            Check(fixture.Server.Requests == 1 && fixture.Delays == 0 && !fixture.Pending.Any() && !File.Exists(fixture.IndexPath),
                "Caller cancellation became a recoverable network fault.");
        });
        await test("Three interrupted replacement downloads preserve the verified package and index", async () =>
        {
            using var fixture = new Fixture();
            string original;
            await using (var payload = await fixture.Prepare()) original = payload.FilePath;
            var index = File.ReadAllText(fixture.IndexPath);
            var originalBytes = File.ReadAllBytes(original);
            fixture.Bytes = Encoding.UTF8.GetBytes("signed:replacement-version-two");
            fixture.Server.Respond = (_, _) => fixture.Response(interrupt: true);
            var before = fixture.Server.Requests;
            await using var fallback = await fixture.Prepare();
            Check(fixture.Server.Requests - before == 3 && fixture.Delays == 2, "Persistent network fault exceeded or missed the attempt limit.");
            Check(fallback.FilePath == original && !fallback.LatestConfirmed && File.ReadAllText(fixture.IndexPath) == index,
                "Network failure replaced the verified index or claimed newest.");
            Check(File.ReadAllBytes(original).SequenceEqual(originalBytes) && !fixture.Pending.Any(), "Old bytes were deleted or a partial package remained.");
        });
        await test("Expected hash mismatch is not retried or passed to signature verification", async () =>
        {
            using var fixture = new Fixture(manifest: true);
            fixture.ExpectedHash = new string('0', 64);
            await Throws<InvalidDataException>(() => fixture.Prepare());
            Check(fixture.Server.Requests == 1 && fixture.ManifestCalls == 1 && fixture.Delays == 0 && fixture.SignatureChecks == 0,
                "Hash validation was included in the retry boundary.");
            Check(!fixture.Pending.Any() && !File.Exists(fixture.IndexPath), "Rejected hash was committed.");
        });
        await test("Signature rejection is never retried", async () =>
        {
            using var fixture = new Fixture(); fixture.RejectSignature = true;
            await Throws<InvalidDataException>(() => fixture.Prepare());
            Check(fixture.Server.Requests == 1 && fixture.SignatureChecks == 1 && fixture.Delays == 0 && !fixture.Pending.Any(), "Signature rejection triggered a retry or leaked bytes.");
        });
        await test("Redirect to an unapproved host is rejected without requesting it", async () =>
        {
            using var fixture = new Fixture();
            fixture.Server.Respond = (_, _) => { var response = fixture.Response(status: HttpStatusCode.Found); response.Headers.Location = new Uri("https://unapproved.test/setup.exe"); return response; };
            await Throws<InvalidDataException>(() => fixture.Prepare());
            Check(fixture.Server.Requests == 1 && fixture.Delays == 0 && fixture.Server.DisposedResponses == 1, "Unapproved redirect was retried or requested.");
        });
        await test("Disk file creation IOException is not treated as a network interruption", async () =>
        {
            using var fixture = new Fixture();
            fixture.Server.Respond = (_, _) => fixture.Response(onOpen: () =>
            {
                var pending = fixture.Pending.Single();
                File.WriteAllText(Path.Combine(pending, "setup.exe"), "occupied");
            });
            await Throws<IOException>(() => fixture.Prepare());
            Check(fixture.Server.Requests == 1 && fixture.Delays == 0 && fixture.SignatureChecks == 0 && !fixture.Pending.Any(), "Disk failure was retried or committed.");
        });
        await test("Unmarked IOException is not retried", async () =>
        {
            using var fixture = new Fixture(); fixture.Server.Respond = (_, _) => throw new IOException("disk-like failure");
            await Throws<IOException>(() => fixture.Prepare());
            Check(fixture.Server.Requests == 1 && fixture.Delays == 0, "A raw IOException became retryable.");
        });
        await test("Metadata body reset retries resolution and downloads the installer once", async () =>
        {
            using var fixture = new Fixture(metadata: true); var metadataCalls = 0;
            fixture.Server.Respond = (request, _) => request.RequestUri!.AbsolutePath == "/metadata"
                ? fixture.Response(fixture.Metadata, interrupt: ++metadataCalls == 1) : fixture.Response();
            await using var payload = await fixture.Prepare();
            Check(metadataCalls == 2 && fixture.Server.Requests == 3 && fixture.Delays == 1, "Metadata stream reset did not restart the resolution operation.");
        });
        await test("Metadata short EOF retries before parsing", async () =>
        {
            using var fixture = new Fixture(metadata: true); var metadataCalls = 0;
            fixture.Server.Respond = (request, _) => request.RequestUri!.AbsolutePath == "/metadata"
                ? fixture.Response(fixture.Metadata, shortBody: ++metadataCalls == 1) : fixture.Response();
            await using var payload = await fixture.Prepare();
            Check(metadataCalls == 2 && fixture.Delays == 1 && fixture.Server.Requests == 3, "Short metadata was parsed or not retried.");
        });
        await test("Metadata 404 is not retried", async () =>
        {
            using var fixture = new Fixture(metadata: true); fixture.Server.Respond = (_, _) => fixture.Response(status: HttpStatusCode.NotFound);
            await Throws<InstallerNetwork.InstallerNetworkException>(() => fixture.Prepare());
            Check(fixture.Server.Requests == 1 && fixture.Delays == 0, "404 was retried.");
        });
        await test("Changed metadata format is not retried", async () =>
        {
            using var fixture = new Fixture(metadata: true); fixture.Server.Respond = (_, _) => fixture.Response(Encoding.UTF8.GetBytes("unrecognized metadata"));
            await Throws<InvalidDataException>(() => fixture.Prepare());
            Check(fixture.Server.Requests == 1 && fixture.Delays == 0, "Invalid metadata was retried.");
        });
        await test("Installer 404 is not retried", async () =>
        {
            using var fixture = new Fixture(); fixture.Server.Respond = (_, _) => fixture.Response(status: HttpStatusCode.NotFound);
            await Throws<InstallerNetwork.InstallerNetworkException>(() => fixture.Prepare());
            Check(fixture.Server.Requests == 1 && fixture.Delays == 0 && !fixture.Pending.Any(), "Installer 404 was retried.");
        });
        await test("Header connection failure is retried before reading the successful body", async () =>
        {
            using var fixture = new Fixture();
            fixture.Server.Respond = (_, attempt) => attempt == 1 ? throw new HttpRequestException("connection reset", new SocketException(10054)) : fixture.Response();
            await using var payload = await fixture.Prepare();
            Check(fixture.Server.Requests == 2 && fixture.Delays == 1 && fixture.SignatureChecks == 2, "Header failure did not recover with one retry.");
        });
        await test("Injected manifest resolution retries at one boundary rather than multiplying attempts", async () =>
        {
            using var fixture = new Fixture(manifest: true); fixture.ManifestFailures = 2;
            await using var payload = await fixture.Prepare();
            Check(fixture.ManifestCalls == 3 && fixture.Server.Requests == 1 && fixture.Delays == 2, "Manifest retry attempts multiplied or package GET repeated.");
        });
        await test("Oversized response is rejected without retry or signature check", async () =>
        {
            using var fixture = new Fixture();
            fixture.Server.Respond = (_, _) => { var response = fixture.Response(); response.Content.Headers.ContentLength = 2L * 1024 * 1024 * 1024 + 1; return response; };
            await Throws<InvalidDataException>(() => fixture.Prepare());
            Check(fixture.Server.Requests == 1 && fixture.Delays == 0 && fixture.SignatureChecks == 0 && !fixture.Pending.Any(), "Oversized response was retried or committed.");
        });
    }

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "HMAppKit-NetworkTests-" + Guid.NewGuid().ToString("N"));
        public string Library => Path.Combine(Root, "Local Installer");
        public string IndexPath => Path.Combine(Library, App.Id, "index.json");
        public IEnumerable<string> Pending => Directory.Exists(Path.Combine(Library, App.Id))
            ? Directory.EnumerateDirectories(Path.Combine(Library, App.Id), ".pending-*") : [];
        public byte[] Bytes = Encoding.UTF8.GetBytes("signed:complete-installation-package");
        public byte[] Metadata => Encoding.UTF8.GetBytes("{\"url\":\"https://official.test/setup.exe\"}");
        public string? ExpectedHash;
        public int SignatureChecks, ManifestCalls, ManifestFailures, Delays;
        public bool RejectSignature;
        public TestServer Server { get; } = new();
        public TestLog Log { get; } = new();
        public AppDefinition App { get; }
        public InstallerPayloadProvider Provider { get; }
        public Fixture(bool metadata = false, bool manifest = false, Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            Directory.CreateDirectory(Root);
            App = new AppDefinition
            {
                Id = "fixture", Name = "Fixture", FolderName = "Fixture",
                OnlineInstaller = new OnlineInstallerDefinition
                {
                    DownloadUrl = metadata || manifest ? "" : "https://official.test/setup.exe",
                    MetadataUrl = metadata ? "https://official.test/metadata" : "", UrlPattern = "\"url\":\"(?<url>[^\"]+)\"",
                    WingetManifestPath = manifest ? "f/Fixture/Package" : "", AllowedDownloadHosts = ["official.test"], AllowedPublishers = ["Trusted Publisher"]
                }
            };
            ExpectedHash = Hash(Bytes);
            Server.Respond = (_, _) => Response();
            Provider = new InstallerPayloadProvider(installerLibraryRoot: Library, httpMessageHandler: Server,
                signatureVerifier: (path, _) => { SignatureChecks++; if (RejectSignature) throw new InvalidDataException("Untrusted publisher."); return "Trusted Publisher"; },
                manifestResolver: (_, token) =>
                {
                    token.ThrowIfCancellationRequested(); ManifestCalls++;
                    if (ManifestCalls <= ManifestFailures) throw new HttpRequestException("manifest connection reset", new SocketException(10054));
                    return Task.FromResult(new WingetResolvedInstaller(new Uri("https://official.test/setup.exe"), ExpectedHash!, "1.0.0"));
                }, retryDelay: async (duration, token) =>
                {
                    Delays++; token.ThrowIfCancellationRequested();
                    if (delay is not null) await delay(duration, token);
                });
        }
        public Task<PreparedInstaller> Prepare(CancellationToken token = default) => Provider.PrepareAsync(App, new DeploymentSettings(), Log, token);
        public HttpResponseMessage Response(byte[]? bytes = null, bool interrupt = false, bool shortBody = false,
            HttpStatusCode status = HttpStatusCode.OK, Action? onOpen = null)
        {
            bytes ??= Bytes;
            var actual = shortBody ? bytes[..(bytes.Length / 2)] : bytes;
            var stream = new InterruptedStream(actual, interrupt);
            var content = new ControlledContent(stream, onOpen, () => Server.DisposedResponses++);
            content.Headers.ContentLength = bytes.Length;
            var response = new HttpResponseMessage(status) { Content = content };
            response.Headers.ETag = new EntityTagHeaderValue("\"" + Hash(bytes) + "\"");
            return response;
        }
        public void Dispose()
        {
            Provider.Dispose(); Server.Dispose();
            var absolute = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(absolute) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(absolute).StartsWith("HMAppKit-NetworkTests-", StringComparison.Ordinal))
                throw new Exception("Refusing unexpected fixture cleanup path.");
            Directory.Delete(absolute, recursive: true);
        }
    }
    private sealed class TestServer : HttpMessageHandler
    {
        public Func<HttpRequestMessage, int, HttpResponseMessage> Respond = null!;
        public int Requests, DisposedResponses;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Respond(request, ++Requests));
        }
    }
    private sealed class TestLog : IProgress<string>
    {
        public List<string> Lines { get; } = [];
        public void Report(string value) => Lines.Add(value);
    }
    private sealed class ControlledContent(Stream stream, Action? onOpen, Action disposed) : HttpContent
    {
        private bool _disposed;
        protected override Task SerializeToStreamAsync(Stream output, TransportContext? context) => stream.CopyToAsync(output);
        protected override bool TryComputeLength(out long length) { length = stream.Length; return true; }
        protected override Task<Stream> CreateContentReadStreamAsync() { onOpen?.Invoke(); return Task.FromResult(stream); }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed) { _disposed = true; disposed(); stream.Dispose(); }
            base.Dispose(disposing);
        }
    }
    private sealed class InterruptedStream(byte[] bytes, bool interrupt) : MemoryStream(bytes, writable: false)
    {
        private readonly int _breakAt = bytes.Length / 2;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (interrupt && Position >= _breakAt) throw new IOException("connection reset", new SocketException(10054));
            var limit = interrupt ? (int)(_breakAt - Position) : buffer.Length;
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, limit)], token);
        }
    }
}

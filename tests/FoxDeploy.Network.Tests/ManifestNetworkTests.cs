using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using FoxDeploy.Core;

public static class ManifestNetworkTests
{
    private const string Listing = "[{\"type\":\"dir\",\"name\":\"1.2.3\"}]";
    private static string Manifest => "PackageIdentifier: Vendor.Example\nPackageVersion: 1.2.3\nInstallerType: nullsoft\nInstallers:\n- Architecture: x64\n  InstallerUrl: https://vendor.test/setup.exe\n  InstallerSha256: " + new string('A', 64) + "\nManifestType: installer\n";
    private static OnlineInstallerDefinition Profile => new()
    {
        WingetManifestPath = "v/Vendor/Example", InstallerType = "nsis", AllowedDownloadHosts = ["vendor.test"]
    };

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Manifest listing 503 retries the entire single-attempt resolver and recovers", async () =>
        {
            using var handler = new ScriptHandler((request, call) => call == 1 ? Response(HttpStatusCode.ServiceUnavailable, "busy") : GoodResponse(request));
            using var client = new HttpClient(handler);
            var log = new Log(); var waits = new List<TimeSpan>();
            var resolved = await Resolve(client, log, waits);
            Check(resolved.Version == "1.2.3" && resolved.DownloadUri.Host == "vendor.test", "Resolver did not return the official installer.");
            Check(handler.ApiCalls == 2 && handler.RawCalls == 1 && log.Lines.Count == 1 && waits.SequenceEqual([TimeSpan.FromSeconds(1)]), "Listing recovery retried more than one resolver or lost backoff.");
        });
        await test("Raw manifest transport failure retries from listing with the real raw host", async () =>
        {
            var failed = false;
            using var handler = new ScriptHandler((request, _) =>
            {
                if (request.RequestUri!.Host == "raw.githubusercontent.com" && !failed)
                {
                    failed = true;
                    throw new HttpRequestException("secret message must not be logged", new SocketException((int)SocketError.ConnectionReset));
                }
                return GoodResponse(request);
            });
            using var client = new HttpClient(handler);
            var log = new Log(); var waits = new List<TimeSpan>();
            _ = await Resolve(client, log, waits);
            Check(handler.ApiCalls == 2 && handler.RawCalls == 2 && log.Lines.Count == 1, "A raw failure must restart exactly one listing/raw operation.");
            Check(log.Lines[0].Contains("主机=raw.githubusercontent.com") && log.Lines[0].Contains("SocketError=ConnectionReset") && !log.Lines[0].Contains("secret message"), "Retry lost actual host/socket context or exposed a remote message.");
        });
        await test("Raw response short EOF is a network read failure and can recover", async () =>
        {
            var failed = false;
            using var handler = new ScriptHandler((request, _) =>
            {
                if (request.RequestUri!.Host == "raw.githubusercontent.com" && !failed)
                {
                    failed = true;
                    var partial = Response(HttpStatusCode.OK, "short");
                    partial.Content.Headers.ContentLength = 300;
                    return partial;
                }
                return GoodResponse(request);
            });
            using var client = new HttpClient(handler);
            var log = new Log(); var waits = new List<TimeSpan>();
            _ = await Resolve(client, log, waits);
            Check(handler.ApiCalls == 2 && handler.RawCalls == 2 && log.Lines.Count == 1 && log.Lines[0].Contains("EndOfStreamException"), "Truncated metadata was parsed or not retried.");
        });
        await test("HTTP 403 is preserved and never retried", async () =>
        {
            using var handler = new ScriptHandler((_, _) => Response(HttpStatusCode.Forbidden, "forbidden"));
            using var client = new HttpClient(handler);
            var log = new Log(); var waits = new List<TimeSpan>();
            var error = await Throws<HttpRequestException>(async () => { _ = await Resolve(client, log, waits); });
            Check(error.StatusCode == HttpStatusCode.Forbidden && handler.Calls == 1 && waits.Count == 0 && log.Lines.Count == 0, "Forbidden access was retried or HTTP status lost.");
            var detail = InstallerNetwork.DescribeFailure(error);
            Check(detail.Contains("HTTP=403") && detail.Contains("api.github.com") && detail.Contains("尝试=1/3"), "Final forbidden diagnostic lacks context.");
        });
        await test("HTTP 429 is bounded to three attempts with only two retry log entries", async () =>
        {
            using var handler = new ScriptHandler((_, _) => Response(HttpStatusCode.TooManyRequests, "limited"));
            using var client = new HttpClient(handler);
            var log = new Log(); var waits = new List<TimeSpan>();
            var error = await Throws<HttpRequestException>(async () => { _ = await Resolve(client, log, waits); });
            Check(error.StatusCode == HttpStatusCode.TooManyRequests && handler.Calls == 3 && log.Lines.Count == 2 && waits.SequenceEqual([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)]), "Rate-limit handling is not bounded or logs the terminal failure twice.");
            Check(InstallerNetwork.DescribeFailure(error).Contains("尝试=3/3"), "Final retry count was lost.");
        });
        await test("Malformed raw manifest is rejected without retry", async () =>
        {
            using var handler = new ScriptHandler((request, _) => request.RequestUri!.Host == "api.github.com" ? Response(HttpStatusCode.OK, Listing) : Response(HttpStatusCode.OK, "not a manifest"));
            using var client = new HttpClient(handler);
            var log = new Log(); var waits = new List<TimeSpan>();
            await Throws<InvalidDataException>(async () => { _ = await Resolve(client, log, waits); });
            Check(handler.Calls == 2 && waits.Count == 0 && log.Lines.Count == 0, "Manifest validation failures must not be treated as transient network errors.");
        });
        await test("Malformed listing JSON is rejected without retry", async () =>
        {
            using var handler = new ScriptHandler((_, _) => Response(HttpStatusCode.OK, "not JSON"));
            using var client = new HttpClient(handler);
            var log = new Log(); var waits = new List<TimeSpan>();
            await Throws<System.Text.Json.JsonException>(async () => { _ = await Resolve(client, log, waits); });
            Check(handler.Calls == 1 && waits.Count == 0, "Invalid listing JSON was retried.");
        });
        await test("Explicit TLS authentication failure is not retried or weakened", async () =>
        {
            using var handler = new ScriptHandler((_, _) => throw new HttpRequestException("certificate secret", new AuthenticationException("untrusted certificate secret")));
            using var client = new HttpClient(handler);
            var log = new Log(); var waits = new List<TimeSpan>();
            var error = await Throws<HttpRequestException>(async () => { _ = await Resolve(client, log, waits); });
            Check(handler.Calls == 1 && waits.Count == 0 && InstallerNetwork.DescribeFailure(error).Contains("AuthenticationException"), "Authentication failure was retried or original chain lost.");
        });
        await test("Direct resolver cancellation retains OperationCanceledException and true request host", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            using var handler = new ScriptHandler((_, _) =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });
            using var client = new HttpClient(handler);
            var error = await Throws<OperationCanceledException>(async () => { _ = await WingetManifestResolver.ResolveAsync(Profile, client, cancellation.Token); });
            Check(handler.Calls == 1 && InstallerNetwork.DescribeFailure(error).Contains("主机=api.github.com"), "Public resolver cancellation lost its semantics or request context.");
        });
        await test("Caller cancellation during an HTTP failure stops before logging or retrying", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            using var handler = new ScriptHandler((_, _) => { cancellation.Cancel(); throw new HttpRequestException("failure during cancel"); });
            using var client = new HttpClient(handler);
            var log = new Log(); var waits = new List<TimeSpan>();
            var error = await Throws<OperationCanceledException>(async () => { _ = await Resolve(client, log, waits, cancellation.Token); });
            Check(handler.Calls == 1 && log.Lines.Count == 0 && waits.Count == 0 && error.InnerException is not null && InstallerNetwork.DescribeFailure(error).Contains("api.github.com"), "Cancellation was retried or original cause lost.");
        });
        await test("Direct resolver body-read cancellation remains an OperationCanceledException", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            using var handler = new ScriptHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new CancelOnReadStream(cancellation)) });
            using var client = new HttpClient(handler);
            var error = await Throws<OperationCanceledException>(async () => { _ = await WingetManifestResolver.ResolveAsync(Profile, client, cancellation.Token); });
            Check(handler.Calls == 1 && cancellation.IsCancellationRequested && InstallerNetwork.DescribeFailure(error).Contains("api.github.com"), "HTTP body cancellation was wrapped into IOException or lost request context.");
        });
        await test("Diagnostic context exposes host and error codes without signed URLs or credentials", async () =>
        {
            var address = new Uri("https://user-secret:password-secret@raw.githubusercontent.com/path?token=query-secret");
            var log = new Log(); var waits = new List<TimeSpan>(); var calls = 0;
            var error = await Throws<HttpRequestException>(async () =>
            {
                _ = await InstallerNetwork.ExecuteAsync<int>(_ =>
                {
                    calls++;
                    throw InstallerNetwork.RequestFailure(new HttpRequestException("https://user-secret:password-secret@private.test/file?sig=query-secret Bearer bearer-secret\r\nnext", new IOException("token=nested-secret", new SocketException((int)SocketError.ConnectionReset))), "清单查询", address);
                }, "版本查询", new Uri("https://api.github.com"), log, "示例应用", default,
                (pause, _) => { waits.Add(pause); return Task.CompletedTask; });
            });
            var text = string.Join(" ", log.Lines) + " " + InstallerNetwork.DescribeFailure(error) + " " + error.Message;
            foreach (var secret in new[] { "user-secret", "password-secret", "query-secret", "bearer-secret", "nested-secret", "https://", "\r", "\n" })
                Check(!text.Contains(secret, StringComparison.Ordinal), "Sensitive value leaked: " + secret);
            Check(calls == 3 && log.Lines.Count == 2 && text.Contains("主机=raw.githubusercontent.com") && text.Contains("HResult=0x") && text.Contains("NativeError="), "Safe diagnostics lost host, attempt, or nested socket details.");
        });
    }

    private static Task<WingetResolvedInstaller> Resolve(HttpClient client, Log log, List<TimeSpan> waits, CancellationToken token = default) =>
        InstallerNetwork.ExecuteAsync(cancellation => WingetManifestResolver.ResolveAsync(Profile, client, cancellation), "版本信息查询", null, log, "示例应用", token,
            (pause, _) => { waits.Add(pause); return Task.CompletedTask; });

    private static HttpResponseMessage GoodResponse(HttpRequestMessage request) => Response(HttpStatusCode.OK,
        request.RequestUri!.Host == "api.github.com" ? Listing : Manifest);
    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
    private static void Check(bool passed, string message) { if (!passed) throw new Exception(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class Log : IProgress<string>
    {
        public List<string> Lines { get; } = [];
        public void Report(string value) => Lines.Add(value);
    }
    private sealed class ScriptHandler(Func<HttpRequestMessage, int, HttpResponseMessage> script) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int ApiCalls { get; private set; }
        public int RawCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (request.RequestUri!.Host == "api.github.com") ApiCalls++;
            else if (request.RequestUri.Host == "raw.githubusercontent.com") RawCalls++;
            return Task.FromResult(script(request, Calls));
        }
    }
    private sealed class CancelOnReadStream(CancellationTokenSource cancellation) : MemoryStream(Encoding.UTF8.GetBytes(Listing))
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromException<int>(new OperationCanceledException(cancellationToken));
        }
    }
}

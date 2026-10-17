using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FoxDeploy.Core;

public static class SupportReportTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Support report offers diagnostics only for actionable outcome states", () =>
        {
            foreach (var state in Enum.GetValues<InstallState>())
            {
                var expected = state is InstallState.Failed or InstallState.Skipped or InstallState.Unverified or InstallState.LocationMismatch or InstallState.ExistingUnverified;
                Check(Report(state).HasProblems == expected, "Unexpected offer state: " + state);
            }
            Check(Report(InstallState.Succeeded, error: "本轮异常").HasProblems, "An explicit round exception needs feedback.");
            Check(SupportReport.Create("0.18", DateTimeOffset.UtcNow, [], "", cleanupWarnings: ["清理异常"]).HasProblems,
                "Cleanup warnings are independently actionable.");
            return Task.CompletedTask;
        });

        await test("Support report includes only explicit round data and anonymous OS version", () =>
        {
            var started = new DateTimeOffset(2026, 10, 17, 17, 0, 0, TimeSpan.FromHours(8));
            var report = SupportReport.Create("0.18.0", started,
                [new("one", "测试应用", InstallState.Failed, "本轮失败", @"E:\TempSoftware\One")], "本轮日志");
            using var document = JsonDocument.Parse(report.Json);
            var root = document.RootElement;
            Check(root.GetProperty("reportId").GetString() == report.ReportId && Guid.TryParseExact(report.ReportId, "N", out _), "A unique stable receipt ID is required.");
            Check(root.GetProperty("startedUtc").GetDateTimeOffset() == started && root.GetProperty("appVersion").GetString() == "0.18.0", "Round identity is required.");
            Check(root.GetProperty("applications").GetArrayLength() == 1 && root.GetProperty("roundLog").GetString() == "本轮日志", "Only supplied round data belongs in the report.");
            Check(!root.EnumerateObject().Any(property => property.Name.Contains("user", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("machine", StringComparison.OrdinalIgnoreCase)), "No user or machine identity fields may be collected.");
            Check(report.Json.Contains(@"E:\\TempSoftware\\One", StringComparison.Ordinal), "The selected install destination remains diagnostically useful.");
            return Task.CompletedTask;
        });

        await test("Support sanitization removes other-computer profiles, UNC identities, URLs and credentials", () =>
        {
            const string input = "阶段=下载 HTTP=403\nC:\\Users\\19016\\AppData\\Local\\Temp\\a.exe C:/Users/AnotherPerson/Desktop/a.exe " +
                "\\\\corporate-server\\homes\\private-user\\a.exe\n" +
                "C:\\Users\\Private Person\\Desktop\\a.exe\n\\\\corp with spaces\\home profiles\\Private Network Person\\a.exe\n" +
                "C:\\Users\\Private O'Brien\\Desktop\\a.exe\n" +
                "https://url-user:url-password@downloads.example.test/setup.exe?anything=PRIVATE_QUERY#FRAGMENT\n" +
                "Authorization: Bearer PRIVATE_BEARER password=PRIVATE_PASSWORD \"token\":\"PRIVATE_JSON_TOKEN\" cookie=PRIVATE_COOKIE\n" +
                "Cookie: first=PRIVATE_COOKIE_FIRST; second=PRIVATE_COOKIE_SECOND\n" +
                "USERNAME=PRIVATE_USERNAME COMPUTERNAME=PRIVATE_MACHINE person-private@example.test\n" +
                " at Source.Test() in C:\\lodi_build\\private-source-user\\Source.cs:line 123";
            var report = Report(InstallState.Failed, log: input, error: input);
            foreach (var secret in new[] { "19016", "AnotherPerson", "corporate-server", "private-user", "Private Person", "corp with spaces", "Private Network Person", "Private O'Brien", "url-user", "url-password", "PRIVATE_QUERY", "FRAGMENT", "PRIVATE_BEARER", "PRIVATE_PASSWORD", "PRIVATE_JSON_TOKEN", "PRIVATE_COOKIE", "PRIVATE_COOKIE_FIRST", "PRIVATE_COOKIE_SECOND", "PRIVATE_USERNAME", "PRIVATE_MACHINE", "person-private", "private-source-user" })
                Check(!report.Json.Contains(secret, StringComparison.Ordinal), "Private data escaped redaction: " + secret);
            Check(report.Json.Contains("downloads.example.test", StringComparison.Ordinal) && report.Json.Contains("HTTP=403", StringComparison.Ordinal), "Safe stage, host and error code must survive.");
            Check(report.Json.Contains("\\n", StringComparison.Ordinal), "Multiline logs must retain their structure.");
            return Task.CompletedTask;
        });

        await test("Support sanitization strips PEM keys and truncated credential prefixes", () =>
        {
            var key = "-----BEGIN OPENSSH PRIVATE KEY-----\nPRIVATE_KEY_MATERIAL\n-----END OPENSSH PRIVATE KEY-----";
            var report = Report(InstallState.Failed, log: key + "\n保留末尾诊断");
            Check(!report.Json.Contains("PRIVATE_KEY_MATERIAL", StringComparison.Ordinal) && report.Json.Contains("保留末尾诊断", StringComparison.Ordinal), "PEM material must be removed without discarding later diagnostics.");
            var hugeCredential = "token=" + new string('s', SupportReport.MaximumLogBytes + 50) + "\n诊断尾行";
            var tail = Report(InstallState.Failed, log: hugeCredential);
            Check(tail.LogTruncated && !tail.Json.Contains(new string('s', 100), StringComparison.Ordinal), "A tail boundary cannot expose an unlabelled secret fragment.");
            Check(tail.Json.Contains("诊断尾行", StringComparison.Ordinal), "The intact diagnostic tail is retained.");
            return Task.CompletedTask;
        });

        await test("Support log retains bounded Unicode tail and marks truncation", () =>
        {
            var report = Report(InstallState.Failed, log: "OLD_ROUND_PREFIX\n" + string.Concat(Enumerable.Repeat("诊断🙂\n", 110000)) + "FINAL_DIAGNOSTIC");
            using var document = JsonDocument.Parse(report.Json);
            var log = document.RootElement.GetProperty("roundLog").GetString()!;
            Check(report.LogTruncated && log.Contains("日志已截断", StringComparison.Ordinal) && log.EndsWith("FINAL_DIAGNOSTIC", StringComparison.Ordinal), "The final round lines and explicit truncation marker are required.");
            Check(!log.Contains("OLD_ROUND_PREFIX", StringComparison.Ordinal) && Encoding.UTF8.GetByteCount(log) <= SupportReport.MaximumLogBytes, "The 512 KiB log limit must apply to UTF-8 bytes.");
            Check(report.Utf8Length <= SupportReport.MaximumPayloadBytes && !log.Contains('\uFFFD'), "UTF-8 truncation must not split a rune.");
            return Task.CompletedTask;
        });

        await test("Support JSON remains bounded under escape-heavy log and large result catalog", () =>
        {
            var results = Enumerable.Range(0, 1000).Select(index => new SupportAppResult(new string('\\', 200), new string('"', 400),
                index == 999 ? InstallState.Failed : InstallState.Succeeded, new string('"', 2000), new string('\\', 1000)));
            var report = SupportReport.Create("0.18", DateTimeOffset.UtcNow, results, string.Concat(Enumerable.Repeat("\\\"\n", 200000)));
            using var document = JsonDocument.Parse(report.Json);
            Check(report.Utf8Length <= SupportReport.MaximumPayloadBytes && document.RootElement.GetProperty("omittedApplicationCount").GetInt32() == 744,
                "All limits and omitted counts must be explicit.");
            Check(document.RootElement.GetProperty("applications")[0].GetProperty("state").GetString() == "Failed", "An oversized catalog must retain its failures first.");
            Check(report.LogTruncated, "Further JSON-bound truncation must be visible.");
            return Task.CompletedTask;
        });

        await test("Support endpoint rejects non-HTTPS, credentials, query, fragment and alternate port before any request", () =>
        {
            foreach (var value in new[] { "http://support.example.test/report", "https://user:password@support.example.test/report", "https://support.example.test/report?token=secret", "https://support.example.test/report#fragment", "https://support.example.test:8443/report" })
            {
                var handler = new StubHandler((_, _) => throw new InvalidOperationException("No request expected."));
                try { using var client = new SupportUploadClient(new("测试接收端", new Uri(value)), handler); throw new InvalidOperationException("Invalid endpoint accepted."); }
                catch (ArgumentException) { }
                Check(handler.Calls == 0, "Invalid endpoint cannot receive data.");
            }
            Check(SupportUploadClient.IsValidEndpoint(new Uri("https://support.example.test/report")), "A fixed HTTPS endpoint must be accepted.");
            return Task.CompletedTask;
        });

        await test("Support upload sends exactly one JSON POST to the configured endpoint without credentials", async () =>
        {
            var report = Report();
            var endpoint = new Uri("https://support.example.test/report");
            var handler = new StubHandler(async (request, token) =>
            {
                Check(request.Method == HttpMethod.Post && request.RequestUri == endpoint, "Only the configured endpoint can receive the report.");
                Check(request.Headers.Authorization is null && !request.Headers.Contains("Cookie") && !request.Headers.Contains("Proxy-Authorization"), "No repository/OAuth/default identity may be attached.");
                Check(request.Content!.Headers.ContentType?.MediaType == "application/json" && await request.Content.ReadAsStringAsync(token) == report.Json, "Only the previewed JSON may be uploaded.");
                Check(request.Headers.GetValues("X-HM-Report-Id").Single() == report.ReportId, "The same ID must support receiver deduplication.");
                return Receipt(report);
            });
            using var client = new SupportUploadClient(new("测试接收端", endpoint), handler);
            var result = await client.UploadAsync(report);
            Check(result.Status == SupportUploadStatus.Confirmed && handler.Calls == 1, "Only an exact positive receipt can confirm success.");
            Check((await client.UploadAsync(report)).Status == SupportUploadStatus.NotConfirmed && handler.Calls == 1, "A report may never be sent twice from this dialog/client.");
        });

        await test("Support upload ignores redirect destination and never sends a second POST", async () =>
        {
            var handler = new StubHandler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                response.Headers.Location = new Uri("https://elsewhere.example.test/collect?token=REMOTE_SECRET");
                return Task.FromResult(response);
            });
            using var client = Client(handler);
            var report = Report();
            var result = await client.UploadAsync(report);
            Check(result.Status == SupportUploadStatus.NotConfirmed && handler.Calls == 1 && !result.Message.Contains("REMOTE_SECRET", StringComparison.Ordinal), "Redirects must not be followed or exposed.");
            await client.UploadAsync(report);
            Check(handler.Calls == 1, "Unknown outcomes cannot trigger a retry.");
        });

        await test("Support upload hardens an injected HttpClientHandler against cookie and redirect forwarding", () =>
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = true, UseCookies = true, UseDefaultCredentials = true, PreAuthenticate = true };
            using var client = new SupportUploadClient(new("测试接收端", new Uri("https://support.example.test/report")), handler);
            Check(!handler.AllowAutoRedirect && !handler.UseCookies && !handler.UseDefaultCredentials && !handler.PreAuthenticate && handler.Credentials is null,
                "The real HTTP handler must not silently follow cross-origin redirects or attach ambient identity.");
            return Task.CompletedTask;
        });

        await test("Support upload rejects login HTML, empty response, service error and mismatched receipt", async () =>
        {
            foreach (var body in new[] { "<html>login PRIVATE_REMOTE_BODY</html>", "", "{\"code\":999,\"msg\":\"PRIVATE_REMOTE_BODY\"}", "{\"accepted\":false,\"reportId\":\"wrong\"}", "{\"accepted\":true,\"reportId\":\"wrong\"}" })
            {
                var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
                using var client = Client(handler);
                var report = Report();
                var result = await client.UploadAsync(report);
                Check(result.Status == SupportUploadStatus.NotConfirmed && !result.Message.Contains("PRIVATE_REMOTE_BODY", StringComparison.Ordinal), "HTTP 200 alone and remote messages cannot confirm success.");
                await client.UploadAsync(report);
                Check(handler.Calls == 1, "A malformed receipt must not cause a second send.");
            }
        });

        await test("Support upload does not retry 429, 500, network failure or unexpected acknowledgement size", async () =>
        {
            foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
            {
                var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("token=PRIVATE_REMOTE_BODY") }));
                using var client = Client(handler);
                Check((await client.UploadAsync(Report())).Status == SupportUploadStatus.NotConfirmed && handler.Calls == 1, "POST failures are never automatically retried.");
            }
            var broken = new StubHandler((_, _) => throw new HttpRequestException("https://secret.example.test?password=PRIVATE_EXCEPTION"));
            using (var client = Client(broken))
            {
                var result = await client.UploadAsync(Report());
                Check(result.Status == SupportUploadStatus.NotConfirmed && broken.Calls == 1 && !result.Message.Contains("PRIVATE_EXCEPTION", StringComparison.Ordinal), "Exceptions cannot echo receiving-service secrets or trigger retries.");
            }
            var oversized = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('a', 16 * 1024 + 1)) }));
            using (var client = Client(oversized))
                Check((await client.UploadAsync(Report())).Status == SupportUploadStatus.NotConfirmed && oversized.Calls == 1, "Acknowledgement bodies must be bounded.");
        });

        await test("Support upload supports cancellation before and during the single request", async () =>
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            var never = new StubHandler((_, _) => throw new InvalidOperationException("No request expected."));
            using (var client = Client(never))
                Check((await client.UploadAsync(Report(), cancel.Token)).Status == SupportUploadStatus.Cancelled && never.Calls == 0, "Pre-cancelled upload must not send.");
            using var during = new CancellationTokenSource();
            var waiting = new StubHandler(async (_, token) =>
            {
                Check(token.CanBeCanceled, "The request timeout and caller token must reach the transport.");
                during.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("Unreachable.");
            });
            using (var client = Client(waiting))
            {
                var report = Report();
                Check((await client.UploadAsync(report, during.Token)).Status == SupportUploadStatus.Cancelled && waiting.Calls == 1, "In-flight cancellation must complete promptly without retrying.");
                await client.UploadAsync(report);
                Check(waiting.Calls == 1, "Cancellation after starting can have an unknown receipt and must not resend.");
            }
            Check(SupportUploadClient.RequestTimeout == TimeSpan.FromSeconds(30), "The request budget must remain 30 seconds.");
        });

        await test("Support upload never sends a clean or cancelled-only round", async () =>
        {
            var handler = new StubHandler((_, _) => throw new InvalidOperationException("No request expected."));
            using var client = Client(handler);
            foreach (var state in new[] { InstallState.Succeeded, InstallState.AlreadyInstalled, InstallState.Cancelled })
                Check((await client.UploadAsync(Report(state))).Status == SupportUploadStatus.Failed, "Clean rounds cannot be sent automatically.");
            Check(handler.Calls == 0, "No clean round may be transmitted.");
        });
    }

    private static SupportReport Report(InstallState state = InstallState.Failed, string log = "本轮诊断", string? error = null) =>
        SupportReport.Create("0.18.0", DateTimeOffset.UtcNow, [new("test-app", "测试应用", state, "测试结果", @"E:\TempSoftware\Test")], log, error);
    private static SupportUploadClient Client(HttpMessageHandler handler) => new(new("测试接收端", new Uri("https://support.example.test/report")), handler);
    private static HttpResponseMessage Receipt(SupportReport report) => new(HttpStatusCode.Created)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { accepted = true, reportId = report.ReportId }), Encoding.UTF8, "application/json")
    };
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}

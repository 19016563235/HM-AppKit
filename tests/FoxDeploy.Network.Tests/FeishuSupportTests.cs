using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FoxDeploy.Core;

public static class FeishuSupportTests
{
    private const string TestSecret = "HM-AppKit-test-only-secret";
    private const string TestHookId = "11111111-2222-4333-8444-555555555555";
    private static readonly Uri TestEndpoint = new("https://open.feishu.cn/open-apis/bot/v2/hook/" + TestHookId);

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Feishu HMAC uses timestamp-newline-secret as key and an empty message", () =>
        {
            // Independently calculated with Python hmac.new(key, b'', hashlib.sha256), not the implementation under test.
            Check(SupportUploadClient.CreateFeishuSignature("1599360473", TestSecret) == "QpNzGp6ZlDDhgTq/4MenBWMLPc/7SEDx3uKbEpfBgvg=", "Feishu signature differs from the independent fixed vector.");
            Check(SupportUploadClient.FeishuRequestTimeout == TimeSpan.FromSeconds(120), "A complete multipart round has a 120-second budget.");
            return Task.CompletedTask;
        });

        await test("Feishu endpoint is restricted to the official host and exact UUID webhook path", () =>
        {
            Check(SupportUploadClient.IsValidEndpoint(TestEndpoint, SupportUploadMode.FeishuWebhook), "The official fixed webhook URL must be valid.");
            foreach (var value in new[]
            {
                "https://open.feishu.cn.attacker.test/open-apis/bot/v2/hook/" + TestHookId,
                "https://attacker.test/open-apis/bot/v2/hook/" + TestHookId,
                "https://open.feishu.cn/open-apis/bot/v1/hook/" + TestHookId,
                "https://open.feishu.cn/open-apis/bot/v2/hook/not-a-uuid",
                TestEndpoint.AbsoluteUri + "/", TestEndpoint.AbsoluteUri + "?token=PRIVATE_QUERY", TestEndpoint.AbsoluteUri + "#PRIVATE_FRAGMENT",
                "https://user:password@open.feishu.cn/open-apis/bot/v2/hook/" + TestHookId,
                "https://open.feishu.cn:8443/open-apis/bot/v2/hook/" + TestHookId,
                "http://open.feishu.cn/open-apis/bot/v2/hook/" + TestHookId
            })
                Check(!SupportUploadClient.IsValidEndpoint(new Uri(value), SupportUploadMode.FeishuWebhook), "An unsupported webhook URL was accepted.");
            Check(!SupportUploadClient.IsValidEndpoint(TestEndpoint, (SupportUploadMode)999), "Unknown receiving modes must fail closed.");
            try
            {
                using var client = new SupportUploadClient(new("测试", TestEndpoint, SupportUploadMode.FeishuWebhook));
                throw new InvalidOperationException("A signing secret is required.");
            }
            catch (ArgumentException ex) { Check(!ex.Message.Contains(TestHookId, StringComparison.Ordinal), "Configuration errors cannot reveal the webhook credential."); }
            return Task.CompletedTask;
        });

        await test("Feishu options diagnostics cannot expose webhook UUID or signing secret", () =>
        {
            var options = Options();
            var diagnostic = options.ToString();
            Check(!diagnostic.Contains(TestSecret, StringComparison.Ordinal) && !diagnostic.Contains(TestHookId, StringComparison.Ordinal) &&
                !diagnostic.Contains("https://", StringComparison.Ordinal), "Options.ToString must redact all credentials.");
            return Task.CompletedTask;
        });

        await test("Feishu sends signed plain text and verifies business success rather than HTTP alone", async () =>
        {
            var report = Report("HTTP=403\n本轮下载失败\n");
            var handler = new Stub(async (request, token) =>
            {
                Check(request.Method == HttpMethod.Post && request.RequestUri == TestEndpoint, "Only the configured official webhook may receive data.");
                Check(request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "No OAuth or ambient cookie can be sent.");
                var body = await request.Content!.ReadAsStringAsync(token);
                Check(Encoding.UTF8.GetByteCount(body) < 20000 && !body.Contains(TestSecret, StringComparison.Ordinal), "The request must stay below the official size limit and cannot contain the signing secret.");
                using var parsed = JsonDocument.Parse(body);
                var root = parsed.RootElement;
                var timestamp = root.GetProperty("timestamp").GetString()!;
                Check(long.TryParse(timestamp, out var seconds) && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds) < 10, "The timestamp must be current Unix seconds as a string.");
                Check(root.GetProperty("sign").GetString() == SupportUploadClient.CreateFeishuSignature(timestamp, TestSecret), "The request needs the official HMAC signature.");
                Check(root.GetProperty("msg_type").GetString() == "text", "The chosen message format must be plain text.");
                var text = root.GetProperty("content").GetProperty("text").GetString()!;
                Check(text.Contains(report.ReportId, StringComparison.Ordinal) && text.Contains("1/1", StringComparison.Ordinal) && text.Contains("测试应用", StringComparison.Ordinal) &&
                    text.Contains("失败", StringComparison.Ordinal) && text.Contains("HTTP=403", StringComparison.Ordinal), "A readable report ID, part number, app state and round log are required.");
                return Response("{\"code\":0,\"msg\":\"success\"}");
            });
            using var client = new SupportUploadClient(Options(), handler);
            Check((await client.UploadAsync(report)).Status == SupportUploadStatus.Confirmed && handler.Calls == 1, "A successful business receipt must confirm the single message.");
            await client.UploadAsync(report);
            Check(handler.Calls == 1, "Even confirmed reports are never resent from the same client.");
        });

        await test("Feishu chunks preserve the entire preview with rune-safe text and JSON size bounds", async () =>
        {
            var report = Report(string.Concat(Enumerable.Repeat("分段诊断🙂 \\\"\n", 11000)) + "FINAL_DIAGNOSTIC");
            var count = SupportUploadClient.GetFeishuMessageCount(report);
            Check(count is > 2 and <= SupportUploadClient.MaximumFeishuMessages, "The test must exercise multiple bounded messages.");
            var reassembled = new StringBuilder();
            var part = 0;
            var handler = new Stub(async (request, token) =>
            {
                part++;
                var body = await request.Content!.ReadAsStringAsync(token);
                Check(Encoding.UTF8.GetByteCount(body) < 20000, "Every complete escaped JSON body, not only its text, must fit Feishu's limit.");
                using var parsed = JsonDocument.Parse(body);
                var text = parsed.RootElement.GetProperty("content").GetProperty("text").GetString()!;
                var boundary = text.IndexOf('\n');
                Check(boundary > 0 && text[..boundary].Contains($"{part}/{count}", StringComparison.Ordinal) && text[..boundary].Contains(report.ReportId, StringComparison.Ordinal), "Every part must identify its round and exact ordering.");
                var payload = text[(boundary + 1)..];
                Check(Encoding.UTF8.GetByteCount(payload) <= 14000 && !payload.Contains('\uFFFD'), "Each text chunk must respect UTF-8 rune boundaries.");
                reassembled.Append(payload);
                return Response("{\"code\":0}");
            });
            using var client = new SupportUploadClient(Options(), handler);
            var result = await client.UploadAsync(report);
            Check(result.Status == SupportUploadStatus.Confirmed && handler.Calls == count, "All messages need successful receipts.");
            Check(reassembled.ToString() == SupportUploadClient.PreviewText(report), "No app error, log byte, newline or delimiter may silently disappear across chunks.");
        });

        await test("Feishu stops after a later business error and reports partial delivery without retry", async () =>
        {
            var report = Report(string.Concat(Enumerable.Repeat("诊断失败\n", 10000)));
            var calls = 0;
            var handler = new Stub((_, _) => Task.FromResult(Response(++calls == 1 ? "{\"code\":0}" : "{\"code\":19021,\"msg\":\"PRIVATE_REMOTE_MESSAGE\"}")));
            using var client = new SupportUploadClient(Options(), handler);
            var result = await client.UploadAsync(report);
            Check(result.Status == SupportUploadStatus.NotConfirmed && handler.Calls == 2 && result.Message.Contains("已确认 1/", StringComparison.Ordinal) &&
                result.Message.Contains("当前条接收结果未知", StringComparison.Ordinal) && !result.Message.Contains("PRIVATE_REMOTE_MESSAGE", StringComparison.Ordinal), "Partial delivery must be explicit and receiving-service content must not be echoed.");
            await client.UploadAsync(report);
            Check(handler.Calls == 2, "Unknown multipart outcomes must not restart the round.");
        });

        await test("Feishu rejects HTTP 200 business errors, malformed success and conflicting legacy fields", async () =>
        {
            foreach (var body in new[] { "{\"code\":19021}", "{\"code\":19021,\"StatusCode\":0}", "{\"code\":\"0\"}", "{\"code\":false}", "{}", "<html>login</html>", "" })
            {
                var handler = new Stub((_, _) => Task.FromResult(Response(body)));
                using var client = new SupportUploadClient(Options(), handler);
                Check((await client.UploadAsync(Report())).Status == SupportUploadStatus.NotConfirmed && handler.Calls == 1, "HTTP success alone cannot acknowledge a Feishu message.");
            }
            var old = new Stub((_, _) => Task.FromResult(Response("{\"StatusCode\":0,\"StatusMessage\":\"success\"}")));
            using (var client = new SupportUploadClient(Options(), old))
                Check((await client.UploadAsync(Report())).Status == SupportUploadStatus.Confirmed, "The documented legacy numeric success field remains compatible when no current field exists.");
        });

        await test("Feishu cancellation between chunks stops remaining sends and retains acknowledged count", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var report = Report(string.Concat(Enumerable.Repeat("取消测试\n", 8000)));
            var handler = new Stub((_, _) =>
            {
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
                return Task.FromResult(Response("{\"code\":0}"));
            });
            using var client = new SupportUploadClient(Options(), handler);
            var result = await client.UploadAsync(report, cancellation.Token);
            Check(result.Status == SupportUploadStatus.Cancelled && handler.Calls == 1 && result.Message.Contains("已确认 1/", StringComparison.Ordinal),
                "Cancellation during inter-message throttling must retain the already acknowledged part and stop subsequent messages promptly.");
            await client.UploadAsync(report);
            Check(handler.Calls == 1 && result.Message.Contains("勿直接重复上传", StringComparison.Ordinal), "A partially sent report must not be retried.");
        });

        await test("Feishu redirects never forward a credential or report to another URL", async () =>
        {
            var handler = new Stub((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                response.Headers.Location = new Uri("https://elsewhere.example.test/PRIVATE_REDIRECT");
                return Task.FromResult(response);
            });
            using var client = new SupportUploadClient(Options(), handler);
            var result = await client.UploadAsync(Report());
            Check(handler.Calls == 1 && result.Status == SupportUploadStatus.NotConfirmed && !result.Message.Contains("PRIVATE_REDIRECT", StringComparison.Ordinal), "No redirect request or sensitive diagnostic is permitted.");
        });
    }

    private static SupportUploadOptions Options() => new("测试飞书群", TestEndpoint, SupportUploadMode.FeishuWebhook, TestSecret);
    private static SupportReport Report(string log = "本轮失败日志") => SupportReport.Create("0.18.0", DateTimeOffset.UtcNow,
        [new("test-app", "测试应用", InstallState.Failed, "合成测试失败", @"E:\TempSoftware\Test")], log);
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}

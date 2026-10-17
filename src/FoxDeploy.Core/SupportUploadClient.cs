using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FoxDeploy.Core;

public enum SupportUploadMode { PrivateEndpoint, FeishuWebhook }
public sealed class SupportUploadOptions(string displayName, Uri endpoint,
    SupportUploadMode mode = SupportUploadMode.PrivateEndpoint, string? signingSecret = null)
{
    public string DisplayName { get; } = displayName;
    public Uri Endpoint { get; } = endpoint;
    public SupportUploadMode Mode { get; } = mode;
    public string? SigningSecret { get; } = signingSecret;
    // A webhook URL contains a credential in its path. Never let generated record diagnostics expose it.
    public override string ToString() => "SupportUploadOptions { Mode = " + Mode + ", Endpoint = [隐藏], SigningSecret = [隐藏] }";
}
public enum SupportUploadStatus { Confirmed, NotConfirmed, Failed, Cancelled }
public sealed record SupportUploadResult(SupportUploadStatus Status, string Message);

/// <summary>One consent-driven POST to the configured endpoint. Never retries or follows redirects.</summary>
public sealed class SupportUploadClient : IDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FeishuRequestTimeout = TimeSpan.FromSeconds(120);
    public const int MaximumFeishuTextBytes = 14000;
    public const int MaximumFeishuRequestBytes = 20000;
    public const int MaximumFeishuMessages = 80;
    private static readonly JsonSerializerOptions FeishuJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly SupportUploadOptions _options;
    private readonly HttpClient _http;
    private readonly HashSet<string> _attemptedReports = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public SupportUploadClient(SupportUploadOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsValidEndpoint(options.Endpoint, options.Mode)) throw new ArgumentException("日志接收地址与接收模式不匹配，或不是允许的 HTTPS 地址。", nameof(options));
        if (options.Mode == SupportUploadMode.FeishuWebhook && (string.IsNullOrWhiteSpace(options.SigningSecret) || options.SigningSecret.Length > 4096))
            throw new ArgumentException("飞书日志接收端需要有效的签名密钥。", nameof(options));
        _options = options;
        if (handler is HttpClientHandler configured)
        {
            configured.AllowAutoRedirect = false;
            configured.UseCookies = false;
            configured.Credentials = null;
            configured.UseDefaultCredentials = false;
            configured.PreAuthenticate = false;
        }
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, Credentials = null
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static bool IsValidEndpoint(Uri? endpoint, SupportUploadMode mode = SupportUploadMode.PrivateEndpoint)
    {
        if (endpoint is not { IsAbsoluteUri: true, Scheme: "https" } ||
            string.IsNullOrWhiteSpace(endpoint.IdnHost) || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) || !endpoint.IsDefaultPort) return false;
        if (mode == SupportUploadMode.PrivateEndpoint) return true;
        if (mode != SupportUploadMode.FeishuWebhook || !string.Equals(endpoint.IdnHost, "open.feishu.cn", StringComparison.OrdinalIgnoreCase)) return false;
        const string prefix = "/open-apis/bot/v2/hook/";
        return endpoint.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(endpoint.AbsolutePath[prefix.Length..], "D", out _);
    }

    public async Task<SupportUploadResult> UploadAsync(SupportReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (cancellationToken.IsCancellationRequested) return new(SupportUploadStatus.Cancelled, "上传已取消，未发送。");
        if (!report.HasProblems) return new(SupportUploadStatus.Failed, "本轮没有需要反馈的问题，未发送。");
        if (report.Utf8Length > SupportReport.MaximumPayloadBytes) return new(SupportUploadStatus.Failed, "报告超过大小限制，未发送。");
        lock (_gate)
            if (!_attemptedReports.Add(report.ReportId)) return new(SupportUploadStatus.NotConfirmed, "此报告已经尝试上传，请先核实接收结果，未重复发送。");
        if (_options.Mode == SupportUploadMode.FeishuWebhook) return await UploadFeishuAsync(report, cancellationToken).ConfigureAwait(false);
        return await UploadPrivateAsync(report, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SupportUploadResult> UploadPrivateAsync(SupportReport report, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("X-HM-Report-Id", report.ReportId);
            request.Content = new StringContent(report.Json, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
                return new(SupportUploadStatus.NotConfirmed, "接收地址返回重定向，已停止；未向其他地址发送报告。");
            if (!response.IsSuccessStatusCode)
                return new(SupportUploadStatus.NotConfirmed, $"接收端返回 HTTP {(int)response.StatusCode}，未确认接收；请勿直接重复上传。");
            // A 200 login page, empty body, or service-specific error does not acknowledge this report.
            if (response.Content.Headers.ContentLength is > 16 * 1024)
                return new(SupportUploadStatus.NotConfirmed, "接收端回执过大，未确认接收；请勿直接重复上传。");
            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[1024];
            while (true)
            {
                var read = await source.ReadAsync(bytes, deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > 16 * 1024)
                    return new(SupportUploadStatus.NotConfirmed, "接收端回执过大，未确认接收；请勿直接重复上传。");
                buffer.Write(bytes, 0, read);
            }
            using var receipt = JsonDocument.Parse(buffer.ToArray());
            if (receipt.RootElement.ValueKind == JsonValueKind.Object &&
                receipt.RootElement.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.True &&
                receipt.RootElement.TryGetProperty("reportId", out var receivedId) && receivedId.ValueKind == JsonValueKind.String &&
                string.Equals(receivedId.GetString(), report.ReportId, StringComparison.Ordinal))
                return new(SupportUploadStatus.Confirmed, "日志已上传，接收端已确认。报告编号：" + report.ReportId);
            return new(SupportUploadStatus.NotConfirmed, "接收端未确认此报告编号，请先核实接收结果，勿直接重复上传。");
        }
        catch (OperationCanceledException)
        {
            return new(cancellationToken.IsCancellationRequested ? SupportUploadStatus.Cancelled : SupportUploadStatus.NotConfirmed,
                cancellationToken.IsCancellationRequested ? "上传已取消；接收结果未知，请勿直接重复上传。" : "上传超过 30 秒，接收结果未知，请勿直接重复上传。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            // Never echo remote bodies, signed URLs, or exception messages from the receiving service.
            return new(SupportUploadStatus.NotConfirmed, "未能取得有效接收回执（" + ex.GetType().Name + "），请先核实接收结果，勿直接重复上传。");
        }
    }

    /// <summary>The complete redacted plain text sent to Feishu, before per-message report/part labels.</summary>
    public static string PreviewText(SupportReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var document = JsonDocument.Parse(report.Json);
        var root = document.RootElement;
        var text = new StringBuilder();
        text.AppendLine("HM AppKit 安装诊断");
        text.AppendLine("报告编号：" + report.ReportId);
        text.AppendLine("应用版本：" + root.GetProperty("appVersion").GetString());
        text.AppendLine("开始时间：" + root.GetProperty("startedUtc").GetString());
        text.AppendLine("结束时间：" + root.GetProperty("finishedUtc").GetString());
        text.AppendLine("系统版本：" + root.GetProperty("operatingSystem").GetString());
        text.AppendLine().AppendLine("本轮应用结果：");
        foreach (var app in root.GetProperty("applications").EnumerateArray())
        {
            text.Append("• ").Append(app.GetProperty("appName").GetString()).Append(" [").Append(app.GetProperty("appId").GetString())
                .Append("]：").AppendLine(StateLabel(app.GetProperty("state").GetString()));
            text.AppendLine("  结果：" + app.GetProperty("message").GetString());
            if (app.GetProperty("targetDirectory").GetString() is { Length: > 0 } target) text.AppendLine("  目标目录：" + target);
        }
        var omittedApps = root.GetProperty("omittedApplicationCount").GetInt32();
        if (omittedApps > 0) text.AppendLine($"[报告大小限制：其余 {omittedApps} 个应用结果未包含]");
        if (root.GetProperty("roundError").GetString() is { Length: > 0 } error) text.AppendLine().AppendLine("本轮异常：").AppendLine(error);
        foreach (var warning in root.GetProperty("cleanupWarnings").EnumerateArray()) text.AppendLine("清理警告：" + warning.GetString());
        var omittedWarnings = root.GetProperty("omittedWarningCount").GetInt32();
        if (omittedWarnings > 0) text.AppendLine($"[其余 {omittedWarnings} 项清理警告未包含]");
        text.AppendLine().AppendLine("本轮日志（已脱敏）：");
        if (root.GetProperty("logTruncated").GetBoolean()) text.AppendLine("[本轮日志超过报告上限，仅保留末尾内容]");
        text.Append(root.GetProperty("roundLog").GetString());
        return text.ToString();
    }

    public static int GetFeishuMessageCount(SupportReport report) => FeishuParts(report).Count;

    /// <summary>Feishu custom-bot signature: timestamp + newline + secret is the HMAC key; message is empty.</summary>
    public static string CreateFeishuSignature(string timestamp, string signingSecret)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(signingSecret);
        return Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(timestamp + "\n" + signingSecret), Array.Empty<byte>()));
    }

    private async Task<SupportUploadResult> UploadFeishuAsync(SupportReport report, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> parts;
        try { parts = FeishuParts(report); }
        catch (InvalidDataException) { return new(SupportUploadStatus.Failed, "报告超过飞书分段上限，未发送；请保存本地报告。"); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(FeishuRequestTimeout);
        var confirmed = 0;
        var attempted = 0;
        try
        {
            for (var index = 0; index < parts.Count; index++)
            {
                if (index > 0) await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                var body = FeishuBody(parts[index], report.ReportId, index + 1, parts.Count, timestamp, _options.SigningSecret!);
                if (Encoding.UTF8.GetByteCount(body) >= MaximumFeishuRequestBytes)
                    return Partial("消息超过飞书大小限制，已停止。", SupportUploadStatus.NotConfirmed, confirmed, attempted, parts.Count);
                using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                attempted++;
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
                    return Partial("接收地址返回重定向，已停止；未向其他地址发送。", SupportUploadStatus.NotConfirmed, confirmed, attempted, parts.Count);
                if (!response.IsSuccessStatusCode)
                    return Partial($"飞书返回 HTTP {(int)response.StatusCode}，已停止。", SupportUploadStatus.NotConfirmed, confirmed, attempted, parts.Count);
                var receiptBytes = await ReadReceiptAsync(response.Content, deadline.Token).ConfigureAwait(false);
                if (receiptBytes is null) return Partial("飞书回执过大，已停止。", SupportUploadStatus.NotConfirmed, confirmed, attempted, parts.Count);
                using var receipt = JsonDocument.Parse(receiptBytes);
                if (!IsFeishuAcknowledged(receipt.RootElement))
                    return Partial("飞书未返回成功业务回执，已停止。", SupportUploadStatus.NotConfirmed, confirmed, attempted, parts.Count);
                confirmed++;
            }
            return new(SupportUploadStatus.Confirmed, $"日志已发送到飞书，{confirmed}/{parts.Count} 条均已确认。报告编号：{report.ReportId}");
        }
        catch (OperationCanceledException)
        {
            return Partial(cancellationToken.IsCancellationRequested ? "上传已取消。" : "上传超过 120 秒，已停止。",
                cancellationToken.IsCancellationRequested ? SupportUploadStatus.Cancelled : SupportUploadStatus.NotConfirmed, confirmed, attempted, parts.Count);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            return Partial("未取得有效飞书回执（" + ex.GetType().Name + "），已停止。", SupportUploadStatus.NotConfirmed, confirmed, attempted, parts.Count);
        }
    }

    private static SupportUploadResult Partial(string reason, SupportUploadStatus status, int confirmed, int attempted, int total) =>
        new(status, reason + $"已确认 {confirmed}/{total} 条；" + (attempted > confirmed ? "当前条接收结果未知；" : "剩余条未发送；") + "请核实群内报告编号，勿直接重复上传。");

    private static async Task<byte[]?> ReadReceiptAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength is > 16 * 1024) return null;
        await using var source = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        while (true)
        {
            var count = await source.ReadAsync(chunk, token).ConfigureAwait(false);
            if (count == 0) return buffer.ToArray();
            if (buffer.Length + count > 16 * 1024) return null;
            buffer.Write(chunk, 0, count);
        }
    }

    private static bool IsFeishuAcknowledged(JsonElement receipt)
    {
        if (receipt.ValueKind != JsonValueKind.Object) return false;
        // Prefer the current code field. An old success field cannot override a current business error.
        if (receipt.TryGetProperty("code", out var code)) return code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) && value == 0;
        return receipt.TryGetProperty("StatusCode", out var oldCode) && oldCode.ValueKind == JsonValueKind.Number && oldCode.TryGetInt32(out var oldValue) && oldValue == 0;
    }

    private static IReadOnlyList<string> FeishuParts(SupportReport report)
    {
        var text = PreviewText(report);
        var parts = new List<string>();
        var part = new StringBuilder();
        var textBytes = 0;
        var jsonBytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            // JSON may escape newlines, slashes, quotes or control code points to six ASCII bytes.
            var escapedBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(rune.ToString(), FeishuJson)) - 2;
            if (textBytes + rune.Utf8SequenceLength > MaximumFeishuTextBytes || jsonBytes + escapedBytes > 18000)
            {
                parts.Add(part.ToString());
                part.Clear(); textBytes = 0; jsonBytes = 0;
                if (parts.Count >= MaximumFeishuMessages) throw new InvalidDataException("飞书日志分段数超过上限。");
            }
            part.Append(rune.ToString());
            textBytes += rune.Utf8SequenceLength;
            jsonBytes += escapedBytes;
        }
        if (part.Length > 0) parts.Add(part.ToString());
        return parts;
    }

    private static string FeishuBody(string text, string reportId, int number, int total, string timestamp, string secret) =>
        JsonSerializer.Serialize(new
        {
            timestamp,
            sign = CreateFeishuSignature(timestamp, secret),
            msg_type = "text",
            content = new { text = $"[HM AppKit 日志 {reportId} · {number}/{total}]\n" + text }
        }, FeishuJson);

    private static string StateLabel(string? state) => state switch
    {
        "Failed" => "失败", "Skipped" => "已跳过（需要检查）", "Unverified" => "安装结果未验证", "LocationMismatch" => "目录不一致",
        "ExistingUnverified" => "已有安装但未验证", "Succeeded" => "已安装", "AlreadyInstalled" => "此前已安装", "Cancelled" => "已取消",
        "Pending" => "待安装", "Checking" => "检查中", "Installing" => "安装中", _ => state ?? "未知"
    };

    public void Dispose() => _http.Dispose();
}

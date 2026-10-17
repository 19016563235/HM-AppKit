using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FoxDeploy.Core;

public sealed record SupportAppResult(string AppId, string AppName, InstallState State, string Message, string? TargetDirectory = null);

/// <summary>A bounded, immutable, explicitly shareable diagnostic for one installation round.</summary>
public sealed class SupportReport
{
    public const int MaximumLogBytes = 512 * 1024;
    public const int MaximumPayloadBytes = 768 * 1024;
    private const int MaximumResults = 256;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private SupportReport(string reportId, bool hasProblems, string json, bool logTruncated)
    {
        ReportId = reportId;
        HasProblems = hasProblems;
        Json = json;
        LogTruncated = logTruncated;
        Utf8Length = Encoding.UTF8.GetByteCount(json);
        if (Utf8Length > MaximumPayloadBytes) throw new InvalidDataException("诊断报告超过大小限制，未发送。");
    }

    public string ReportId { get; }
    public bool HasProblems { get; }
    public string Json { get; }
    public bool LogTruncated { get; }
    public int Utf8Length { get; }

    public static bool IsProblemState(InstallState state) => state is InstallState.Failed or InstallState.Skipped or
        InstallState.Unverified or InstallState.LocationMismatch or InstallState.ExistingUnverified;

    public static SupportReport Create(string appVersion, DateTimeOffset startedUtc, IEnumerable<SupportAppResult> results,
        string roundLog, string? roundError = null, IEnumerable<string>? cleanupWarnings = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        var allResults = results.ToArray();
        var warnings = (cleanupWarnings ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        var hasProblems = allResults.Any(result => IsProblemState(result.State)) ||
            !string.IsNullOrWhiteSpace(roundError) || warnings.Length > 0;
        // Keep failures first when an unusually large catalog exceeds the diagnostic limit.
        var keptResults = allResults.OrderByDescending(result => IsProblemState(result.State)).Take(MaximumResults)
            .Select(result => new SupportAppResult(Safe(result.AppId, 128), Safe(result.AppName, 256), result.State,
                Safe(result.Message, 512), result.TargetDirectory is null ? null : Safe(result.TargetDirectory, 384))).ToArray();
        var log = SupportReportSanitizer.Sanitize(Tail(roundLog ?? "", MaximumLogBytes, out var initialTruncation));
        log = Tail(log, MaximumLogBytes - 128, out var sanitizedTruncation);
        var truncated = initialTruncation || sanitizedTruncation;
        if (truncated) log = "[日志已截断：仅包含本轮末尾内容]\n" + log;
        var id = Guid.NewGuid().ToString("N");
        object Envelope() => new
        {
            schemaVersion = 1,
            reportId = id,
            appVersion = Safe(appVersion, 128),
            startedUtc = startedUtc.ToUniversalTime(),
            finishedUtc = DateTimeOffset.UtcNow,
            operatingSystem = Environment.OSVersion.Platform + " " + Environment.OSVersion.Version,
            hasProblems,
            applications = keptResults,
            omittedApplicationCount = allResults.Length - keptResults.Length,
            roundError = Safe(roundError, 4096),
            cleanupWarnings = warnings.Take(16).Select(value => Safe(value, 1024)).ToArray(),
            omittedWarningCount = Math.Max(0, warnings.Length - 16),
            logTruncated = truncated,
            roundLog = log
        };
        var json = JsonSerializer.Serialize(Envelope(), JsonOptions);
        while (Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes && log.Length > 0)
        {
            log = Tail(log, Math.Max(0, Encoding.UTF8.GetByteCount(log) / 2), out _);
            truncated = true;
            if (log.Length > 0) log = "[日志已截断：仅包含本轮末尾内容]\n" + log;
            json = JsonSerializer.Serialize(Envelope(), JsonOptions);
        }
        return new SupportReport(id, hasProblems, json, truncated);
    }

    private static string Safe(string? text, int bytes) => Head(SupportReportSanitizer.Sanitize(Head(text ?? "", bytes * 4)), bytes);

    internal static string Head(string text, int bytes)
    {
        var written = 0;
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (written + rune.Utf8SequenceLength > bytes) return text[..index] + "…";
            written += rune.Utf8SequenceLength;
            index += rune.Utf16SequenceLength;
        }
        return text;
    }

    private static string Tail(string text, int bytes, out bool truncated)
    {
        // Limit the scan before encoding, so a very large original log cannot allocate a second full copy.
        if (bytes == 0) { truncated = text.Length > 0; return ""; }
        var candidate = text.Length > bytes ? text[^bytes..] : text;
        var originalShortened = candidate.Length != text.Length;
        var encoded = Encoding.UTF8.GetBytes(candidate);
        if (encoded.Length <= bytes)
        {
            truncated = originalShortened;
            return originalShortened ? RemovePartialLine(candidate) : candidate;
        }
        var index = encoded.Length - bytes;
        while (index < encoded.Length && (encoded[index] & 0xc0) == 0x80) index++;
        truncated = true;
        return RemovePartialLine(Encoding.UTF8.GetString(encoded, index, encoded.Length - index));
    }

    private static string RemovePartialLine(string value)
    {
        // Never expose an unlabelled fragment of a credential cut at the tail boundary.
        var newline = value.IndexOf('\n');
        return newline < 0 ? "" : value[(newline + 1)..];
    }
}

/// <summary>Redaction is performed before preview, saving, or upload; it collects no host/user identity.</summary>
public static class SupportReportSanitizer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = new string(value.Select(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t' ? ' ' : character).ToArray());
        try
        {
            text = Replace(text, @"(?i)\b[a-z][a-z0-9+.-]*://[^\s<>""']+", SafeHost);
            text = Replace(text, @"(?i)[a-z]:[\\/](?:users|documents and settings)[\\/][^\\/\r\n""<>]+", _ => "[用户目录]");
            // Network-share names may identify the organization or the user; no UNC path is sent.
            text = Replace(text, @"\\\\[^\r\n""<>]+", _ => "[网络目录已隐藏]");
            text = Replace(text, @"(?i)(?:\b(?:in|at)\s+|\s在\s*)[a-z]:[\\/][^\r\n]+(?::(?:line\s*)?\d+|:行号\s*\d+)", _ => " [源代码位置已隐藏]");
            text = Replace(text, @"(?i)\b(?:authorization|proxy-authorization)\s*[:=]\s*(?:(?:bearer|basic)\s+)?[^\s,;]+", _ => "[认证已隐藏]");
            text = Replace(text, @"(?i)\b(?:bearer|basic)\s+[^\s,;]+", _ => "[认证已隐藏]");
            text = Replace(text, @"(?im)\b(?:cookie|set-cookie)\s*:\s*[^\r\n]+", _ => "[Cookie 已隐藏]");
            text = Replace(text, @"(?i)[""']?\b(?:access[_-]?token|refresh[_-]?token|id[_-]?token|token|secret|password|passwd|pwd|signature|sig|credential|api[_-]?key|client[_-]?secret|cookie|set-cookie)[""']?\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s,;]+)", _ => "[凭证已隐藏]");
            text = Replace(text, @"(?i)-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----[\s\S]*?(?:-----END (?:[A-Z0-9 ]+ )?PRIVATE KEY-----|$)", _ => "[私钥已隐藏]");
            text = Replace(text, @"(?i)\A[\s\S]*?-----END (?:[A-Z0-9 ]+ )?PRIVATE KEY-----", _ => "[私钥片段已隐藏]");
            text = Replace(text, @"(?m)^\s*[A-Za-z0-9+/=]{32,}\s*$", _ => "[编码数据已隐藏]");
            text = Replace(text, @"(?i)\b(?:computername|username|userdomain)\s*[:=]\s*[^\s,;]+", _ => "[用户身份已隐藏]");
            text = Replace(text, @"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", _ => "[邮箱已隐藏]");
            return text;
        }
        catch (RegexMatchTimeoutException) { return "[诊断文本脱敏超时，原文已隐藏]"; }
    }

    private static string Replace(string value, string pattern, MatchEvaluator replacement) =>
        Regex.Replace(value, pattern, replacement, RegexOptions.CultureInvariant, Timeout);

    private static string SafeHost(Match match)
    {
        try
        {
            return Uri.TryCreate(match.Value, UriKind.Absolute, out var address) &&
                address.Scheme is "http" or "https" && !string.IsNullOrEmpty(address.IdnHost)
                    ? "[主机 " + address.IdnHost + "]" : "[地址已隐藏]";
        }
        catch (UriFormatException) { return "[地址已隐藏]"; }
    }
}

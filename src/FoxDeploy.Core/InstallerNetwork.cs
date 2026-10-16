using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace FoxDeploy.Core;

/// <summary>Bounded retries for idempotent installer HTTP operations, never for installer execution.</summary>
public static class InstallerNetwork
{
    public const int MaximumAttempts = 3;
    private const string StageKey = "NetworkStage";
    private const string HostKey = "NetworkHost";
    private const string AttemptsKey = "NetworkAttempts";
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(250);

    public static async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, string stage, Uri? uri,
        IProgress<string>? log, string? appName, CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        delay ??= Task.Delay;
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return await action(token).ConfigureAwait(false); }
            catch (Exception ex) when (HasNetworkFailure(ex))
            {
                // The caller owns the complete operation budget. Neither requests nor backoff may extend it.
                WithContext(ex, stage, uri);
                ex.Data[AttemptsKey] = attempt;
                if (token.IsCancellationRequested)
                {
                    if (ex is OperationCanceledException) throw;
                    throw WithContext(new OperationCanceledException("网络操作已取消。", ex, token), stage, uri);
                }
                if (attempt < MaximumAttempts && IsTransient(ex))
                {
                    var pause = TimeSpan.FromSeconds(attempt);
                    var prefix = string.IsNullOrWhiteSpace(appName) ? "" : "[" + SanitizeMessage(appName) + "] ";
                    log?.Report($"{prefix}{DescribeFailure(ex)}；{pause.TotalSeconds:0} 秒后重试（{attempt + 1}/{MaximumAttempts}）。");
                    try { await delay(pause, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    { throw WithContext(new OperationCanceledException("网络重试等待已取消。", ex, token), stage, uri); }
                    continue;
                }
                // Only the consumer records the final failure, so it cannot appear twice in the log.
                throw new InstallerNetworkException(stage, uri, attempt, ex);
            }
        }
    }

    /// <summary>Annotates the original exception without replacing its type, HTTP status, or inner chain.</summary>
    public static TException WithContext<TException>(TException ex, string stage, Uri? uri) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (!ex.Data.Contains(StageKey)) ex.Data[StageKey] = SafeStage(stage);
        var host = SafeHost(uri);
        if (!ex.Data.Contains(HostKey) && host is not null) ex.Data[HostKey] = host;
        return ex;
    }

    public static HttpRequestException RequestFailure(HttpRequestException ex, string stage, Uri? uri) => WithContext(ex, stage, uri);

    /// <summary>Marks a failure reading an HTTP body. Do not use this for local file writes or validation.</summary>
    public static IOException ReadFailure(Exception ex, string stage, Uri? uri)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return WithContext(new InstallerNetworkReadException(ex), stage, uri);
    }

    /// <summary>Structured diagnostic fields only; remote messages and complete URLs are deliberately excluded.</summary>
    public static string DescribeFailure(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var chain = ExceptionChain(ex).ToArray();
        var stage = FindText(chain, StageKey) ?? "未标记阶段";
        var host = FindText(chain, HostKey) ?? "未知主机";
        var attempts = FindAttempts(chain);
        var fields = new List<string> { "原因=" + SafeSummary(ex), "阶段=" + SafeStage(stage), "主机=" + NormalizeHost(host) };
        if (attempts > 0) fields.Add($"尝试={attempts}/{MaximumAttempts}");
        fields.Add("异常链=" + string.Join(" -> ", chain.Select(DescribeNode)));
        return string.Join("；", fields);
    }

    /// <summary>A short safe reason for the UI. Internal non-network validation reasons remain readable.</summary>
    public static string SafeSummary(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is InstallerNetworkException) return ex.Message; // Constructed exclusively from safe fields below.
        var chain = ExceptionChain(ex).ToArray();
        if (!HasNetworkFailure(ex) && !chain.Any(node => node is SocketException)) return SanitizeMessage(ex.Message);
        var stage = FindText(chain, StageKey) ?? "网络请求";
        var host = FindText(chain, HostKey);
        var attempts = FindAttempts(chain);
        var context = new List<string>();
        if (host is not null) context.Add(NormalizeHost(host));
        if (attempts > 0) context.Add($"已尝试 {attempts} 次");
        return SafeStage(stage) + "未完成" + (context.Count > 0 ? "（" + string.Join("，", context) + "）" : "") + "：" + NetworkReason(chain) + "。";
    }

    /// <summary>Removes full URLs, common credential forms, and line breaks from trusted local diagnostic text.</summary>
    public static string SanitizeMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "操作未完成";
        var text = new string(value.Take(4096).Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        try
        {
            text = Regex.Replace(text, @"(?i)\b[a-z][a-z0-9+.-]*://[^\s<>""']+", match =>
                Uri.TryCreate(match.Value, UriKind.Absolute, out var address) && SafeHost(address) is { } host ? "[主机 " + host + "]" : "[地址已隐藏]",
                RegexOptions.CultureInvariant, PatternTimeout);
            text = Regex.Replace(text, @"(?i)\b(?:authorization|proxy-authorization)\s*[:=]\s*(?:(?:bearer|basic)\s+)?[^\s,;]+", "[认证已隐藏]", RegexOptions.CultureInvariant, PatternTimeout);
            text = Regex.Replace(text, @"(?i)\b(?:bearer|basic)\s+[^\s,;]+", "[认证已隐藏]", RegexOptions.CultureInvariant, PatternTimeout);
            text = Regex.Replace(text, @"(?i)\b(?:access[_-]?token|refresh[_-]?token|id[_-]?token|token|secret|password|passwd|pwd|signature|sig|credential|api[_-]?key|client[_-]?secret)\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s,;]+)", "[凭证已隐藏]", RegexOptions.CultureInvariant, PatternTimeout);
            text = Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant, PatternTimeout).Trim();
            return text.Length > 768 ? text[..768] + "…" : text;
        }
        catch (RegexMatchTimeoutException) { return "诊断文本已隐藏；请查看异常类型与错误码。"; }
    }

    private static bool HasNetworkFailure(Exception ex) => ExceptionChain(ex).Any(node =>
        node is HttpRequestException or InstallerNetworkReadException or OperationCanceledException or AuthenticationException);

    private static bool IsTransient(Exception ex)
    {
        var chain = ExceptionChain(ex).ToArray();
        if (chain.Any(node => node is AuthenticationException or CryptographicException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException)) return false;
        var http = chain.OfType<HttpRequestException>().FirstOrDefault(node => node is not InstallerNetworkException);
        if (http is not null) return http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
        return chain.Any(node => node is InstallerNetworkReadException or OperationCanceledException);
    }

    private static IEnumerable<Exception> ExceptionChain(Exception ex)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Exception>();
        pending.Push(ex);
        while (pending.TryPop(out var current) && seen.Count < 24)
        {
            if (!seen.Add(current)) continue;
            yield return current;
            if (current is AggregateException aggregate)
            {
                for (var index = aggregate.InnerExceptions.Count - 1; index >= 0; index--) pending.Push(aggregate.InnerExceptions[index]);
            }
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
    }

    private static string? FindText(IReadOnlyList<Exception> chain, string key)
    {
        // The deepest annotated request is authoritative, rather than the operation's initial URL.
        for (var index = chain.Count - 1; index >= 0; index--)
            if (chain[index].Data[key] is string { Length: > 0 } value) return value;
        return null;
    }

    private static int FindAttempts(IEnumerable<Exception> chain)
    {
        foreach (var node in chain) if (node.Data[AttemptsKey] is int count) return Math.Clamp(count, 1, MaximumAttempts);
        return 0;
    }

    private static string DescribeNode(Exception ex)
    {
        var fields = new List<string> { "HResult=0x" + unchecked((uint)ex.HResult).ToString("X8", CultureInfo.InvariantCulture) };
        if (ex is HttpRequestException http)
        {
            if (http.StatusCode is { } status) fields.Add("HTTP=" + ((int)status).ToString(CultureInfo.InvariantCulture));
            if (ex is not InstallerNetworkException || http.HttpRequestError != HttpRequestError.Unknown)
                fields.Add("HttpRequestError=" + http.HttpRequestError);
        }
        if (ex is SocketException socket)
        {
            fields.Add("SocketError=" + socket.SocketErrorCode);
            fields.Add("NativeError=" + socket.NativeErrorCode.ToString(CultureInfo.InvariantCulture));
        }
        return ex.GetType().Name + "(" + string.Join(", ", fields) + ")";
    }

    private static string NetworkReason(IEnumerable<Exception> chain)
    {
        var nodes = chain.ToArray();
        if (nodes.Any(node => node is AuthenticationException or CryptographicException)) return "安全连接认证失败";
        var http = nodes.OfType<HttpRequestException>().FirstOrDefault(node => node is not InstallerNetworkException);
        if (http?.StatusCode is { } status) return "服务器返回 HTTP " + ((int)status).ToString(CultureInfo.InvariantCulture);
        var socket = nodes.OfType<SocketException>().FirstOrDefault();
        if (socket is not null) return socket.SocketErrorCode switch
        {
            SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.NetworkReset => "传输连接中断",
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "域名解析未完成",
            SocketError.TimedOut => "网络请求超时",
            _ => "网络连接未完成"
        };
        if (nodes.Any(node => node is OperationCanceledException)) return "网络请求超时";
        if (http?.HttpRequestError == HttpRequestError.NameResolutionError) return "域名解析未完成";
        return nodes.Any(node => node is InstallerNetworkReadException) ? "网络响应读取中断" : "网络请求未完成";
    }

    private static string SafeStage(string stage) => SanitizeMessage(stage).Replace('；', ' ').Replace(';', ' ');
    private static string? SafeHost(Uri? uri)
    {
        try { return uri is { IsAbsoluteUri: true } && !string.IsNullOrWhiteSpace(uri.IdnHost) ? NormalizeHost(uri.IdnHost) : null; }
        catch (UriFormatException) { return null; }
    }
    private static string NormalizeHost(string value) => value.Length <= 255 && Uri.CheckHostName(value.Trim('[', ']')) != UriHostNameType.Unknown ? value : "未知主机";

    /// <summary>A final HTTP failure with a short safe message and the untouched original exception chain.</summary>
    public sealed class InstallerNetworkException : HttpRequestException
    {
        internal InstallerNetworkException(string stage, Uri? uri, int attempts, Exception inner)
            : base(OriginalHttp(inner)?.HttpRequestError ?? HttpRequestError.Unknown, "网络操作未完成。", inner, OriginalHttp(inner)?.StatusCode)
        {
            WithContext(this, stage, uri);
            Data[AttemptsKey] = attempts;
            _message = SafeSummary(inner);
        }
        private readonly string _message;
        public override string Message => _message;
        private static HttpRequestException? OriginalHttp(Exception inner) => ExceptionChain(inner).OfType<HttpRequestException>()
            .FirstOrDefault(node => node is not InstallerNetworkException);
    }

    private sealed class InstallerNetworkReadException(Exception inner) : IOException("网络响应读取中断。", inner);
}

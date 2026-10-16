using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace FoxDeploy.Core;

public sealed record ProcessRequest(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout, bool ProtectInstaller = false)
{
    public bool UnquotedFinalArgument { get; init; }
    public string? WorkingDirectory { get; init; }
    public bool ShowWindow { get; init; }
    public int? OutputCodePage { get; init; }
}
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut = false, bool CancellationRequested = false);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<string>? output, CancellationToken cancellationToken);
}

public sealed partial class SystemProcessRunner : IProcessRunner
{
    public static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        var info = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            CreateNoWindow = !request.ShowWindow,
            WindowStyle = request.ShowWindow ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
            RedirectStandardOutput = !request.ShowWindow,
            RedirectStandardError = !request.ShowWindow
        };
        if (!request.ShowWindow)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding = request.OutputCodePage is { } codePage ? Encoding.GetEncoding(codePage) : Encoding.UTF8;
            info.StandardOutputEncoding = encoding;
            info.StandardErrorEncoding = encoding;
        }
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory)) info.WorkingDirectory = request.WorkingDirectory;
        if (request.UnquotedFinalArgument)
        {
            var final = request.Arguments.LastOrDefault();
            if (final is null || !(final.StartsWith("/D=", StringComparison.Ordinal) || final.StartsWith("/D ", StringComparison.Ordinal)) || final.Length <= 3 || final.Contains('"') || final.Any(char.IsControl))
                throw new ArgumentException("安装目录必须是最后一个 /D= 或厂商 /D 空格参数，且不能包含引号或控制字符。", nameof(request));
            // No shell participates. Quote all normal arguments using Windows rules;
            // NSIS /D= and Weixin's custom /D tail must remain unquoted, last and intact.
            info.Arguments = string.Join(" ", request.Arguments.SkipLast(1).Select(QuoteWindowsArgument).Append(final));
        }
        else foreach (var argument in request.Arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"')) return value;
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"') quoted.Append('\\', backslashes * 2 + 1).Append(character);
            else quoted.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    public async Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<string>? output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = CreateStartInfo(request) };
        if (!process.Start()) throw new InvalidOperationException("无法启动安装程序。");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        // GUI installers may start long-lived application children. Do not lend them
        // redirected pipes that would keep stream reads waiting after the GUI exits.
        var readOutput = request.ShowWindow ? Task.CompletedTask : ReadStreamAsync(process.StandardOutput, stdout, output);
        var readError = request.ShowWindow ? Task.CompletedTask : ReadStreamAsync(process.StandardError, stderr, output);
        var exit = process.WaitForExitAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        var timedOut = false;
        var cancelled = false;
        try
        {
            await exit.WaitAsync(waitCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeout.IsCancellationRequested;
            cancelled = cancellationToken.IsCancellationRequested;
            if (request.ProtectInstaller)
            {
                output?.Report(timedOut
                    ? "安装超过等待阈值；为避免损坏安装，继续等待当前安装程序退出，之后停止队列。"
                    : "收到停止请求；正在等待当前安装程序正常结束，之后停止队列。");
                // Never terminate winget or its children while an installer may be changing the system.
                await exit.ConfigureAwait(false);
            }
            else
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await exit.ConfigureAwait(false);
            }
        }
        await Task.WhenAll(readOutput, readError).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), timedOut, cancelled || cancellationToken.IsCancellationRequested);
    }

    private static async Task ReadStreamAsync(StreamReader reader, StringBuilder capture, IProgress<string>? output)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var clean = AnsiEscape().Replace(line, "").Trim();
            if (clean.Length == 0) continue;
            capture.AppendLine(clean);
            // Keep the diagnostic tail bounded even when an installer is extremely verbose.
            if (capture.Length > 65536) capture.Remove(0, capture.Length - 49152);
            output?.Report(clean);
        }
    }

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscape();
}

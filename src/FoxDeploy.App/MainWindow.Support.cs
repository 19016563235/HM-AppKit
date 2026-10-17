using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using FoxDeploy.Core;

namespace FoxDeploy.App;

public partial class MainWindow
{
    private readonly Queue<string> _supportLog = new();
    private readonly List<string> _supportWarnings = [];
    private DateTimeOffset? _supportStarted;
    private int _supportCharacters;
    private bool _supportLogTrimmed;

    private void BeginSupportReport()
    {
        _supportLog.Clear();
        _supportWarnings.Clear();
        _supportCharacters = 0;
        _supportLogTrimmed = false;
        _supportStarted = DateTimeOffset.UtcNow;
    }

    private void RecordSupportLog(string text)
    {
        if (_supportStarted is null) return;
        const int maximumCharacters = 512 * 1024;
        if (text.Contains("京麦临时准备文件仍被占用或无权清理", StringComparison.Ordinal) && _supportWarnings.Count < 8)
            _supportWarnings.Add(text);
        if (text.Length > maximumCharacters)
        {
            text = text[^maximumCharacters..];
            _supportLogTrimmed = true;
        }
        _supportLog.Enqueue(text);
        _supportCharacters += text.Length;
        while (_supportCharacters > maximumCharacters && _supportLog.Count > 1)
        {
            _supportCharacters -= _supportLog.Dequeue().Length;
            _supportLogTrimmed = true;
        }
    }

    private void OfferSupportReport(IReadOnlyList<AppRow> rows, Exception? error)
    {
        var started = _supportStarted;
        _supportStarted = null;
        if (started is null || _smokeTest) return;
        try
        {
            var log = (_supportLogTrimmed ? "[本轮早期日志已截断，仅保留末尾]\n" : "") + string.Concat(_supportLog);
            var report = SupportReport.Create(CurrentAppVersion.ToString(), started.Value,
                rows.Select(row => new SupportAppResult(row.App.Id, row.App.Name, row.State, row.StatusMessage, row.DisplayPath)),
                log, error is null ? null : InstallerNetwork.SafeSummary(error), _supportWarnings);
            if (!report.HasProblems) return;
            TrySaveSupportReport(report);
            var options = ReadSupportUploadOptions();
            var dialog = new SupportReportDialog(report, options) { Owner = this };
            dialog.ShowDialog();
            if (dialog.UploadConfirmed) AppendLog($"诊断报告已上传，报告编号：{report.ReportId}。");
            else if (dialog.UploadAttempted) AppendLog($"诊断报告上传未确认，报告编号：{report.ReportId}。请保留本地报告以便核对。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            AppendLog($"诊断报告暂未生成：{InstallerNetwork.SafeSummary(ex)}。原始安装日志仍保存在日志目录。");
        }
        finally
        {
            _supportLog.Clear();
            _supportWarnings.Clear();
            _supportCharacters = 0;
        }
    }

    private void TrySaveSupportReport(SupportReport report)
    {
        try
        {
            var directory = Path.Combine(_stateDirectory, "Reports");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"HM-AppKit-report-{report.ReportId}.json"), report.Json, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { AppendLog("诊断报告未能自动保存在本机，仍可在弹窗中另存。"); }
    }

    private SupportUploadOptions? ReadSupportUploadOptions()
    {
        try
        {
            return BuiltInSupportReceiver.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or InvalidOperationException)
        {
            AppendLog("日志接收位置尚未正确配置，可先保存诊断报告。");
            return null;
        }
    }

}

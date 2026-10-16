using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using FoxDeploy.Core;

namespace FoxDeploy.App;

public partial class MainWindow
{
    private readonly SelfUpdateClient _selfUpdater = new();
    private SelfUpdateRelease? _availableSelfUpdate;
    private SelfUpdatePackage? _preparedSelfUpdate;
    private CancellationTokenSource? _selfUpdateDownloadCancellation;
    private bool _checkingSelfUpdate;
    private bool _downloadingSelfUpdate;
    private bool _applyingSelfUpdate;
    private bool _selfUpdateDismissed;

    internal static string CurrentAppVersion
    {
        get
        {
            var version = typeof(App).Assembly.GetName().Version ?? new Version(0, 6, 0);
            return new SelfUpdateVersion(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision)).ToString();
        }
    }

    private void InitializeSelfUpdater()
    {
        SelfUpdateCheckButton.Content = $"v{CurrentAppVersion} · 检查更新";
        SelfUpdateCheckButton.ToolTip = "启动时优先检查 Gitee 发行版，必要时回退 GitHub；点击可重新检查。";
    }

    private async void SelfUpdateCheck_Click(object sender, RoutedEventArgs e) => await CheckSelfUpdateAsync(manual: true);

    private async Task CheckSelfUpdateAsync(bool manual)
    {
        if (_checkingSelfUpdate || _downloadingSelfUpdate || _applyingSelfUpdate) return;
        if (_preparedSelfUpdate is not null)
        {
            _selfUpdateDismissed = false;
            UpdateSelfUpdateControls();
            return;
        }
        _checkingSelfUpdate = true;
        SelfUpdateCheckButton.IsEnabled = false;
        SelfUpdateCheckButton.Content = "正在检查更新…";
        try
        {
            _availableSelfUpdate = await _selfUpdater.CheckForUpdateAsync(CurrentAppVersion, _startupUpdateCancellation.Token);
            _selfUpdateDismissed = false;
            if (_availableSelfUpdate is { } release) AppendLog($"HM AppKit 发现新版本 v{release.Version}，可下载更新；当前 v{CurrentAppVersion}。");
            else if (manual) AppendLog($"HM AppKit v{CurrentAppVersion} 已是当前稳定版本。");
            SelfUpdateCheckButton.ToolTip = _availableSelfUpdate is null
                ? $"v{CurrentAppVersion} 已是当前稳定版本。点击可重新检查。"
                : $"发现 HM AppKit v{_availableSelfUpdate.Version}。";
            if (manual && _availableSelfUpdate is null) SelfUpdateCheckButton.Content = $"v{CurrentAppVersion} · 已是最新版";
        }
        catch (OperationCanceledException) when (_startupUpdateCancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException or ArgumentException)
        {
            AppendLog($"HM AppKit 更新检查未完成：{InstallerNetwork.DescribeFailure(ex)}；仍可正常使用，稍后可点击“检查更新”重试。");
            SelfUpdateCheckButton.ToolTip = "更新检查暂不可用，点击可重试；不影响应用安装。";
        }
        finally
        {
            _checkingSelfUpdate = false;
            SelfUpdateCheckButton.IsEnabled = true;
            if (SelfUpdateCheckButton.Content as string == "正在检查更新…") SelfUpdateCheckButton.Content = $"v{CurrentAppVersion} · 检查更新";
            UpdateSelfUpdateControls();
        }
    }

    private async void SelfUpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_applyingSelfUpdate) return;
        if (_downloadingSelfUpdate) { _selfUpdateDownloadCancellation?.Cancel(); return; }
        if (_preparedSelfUpdate is not null) { await RestartForSelfUpdateAsync(); return; }
        if (_availableSelfUpdate is null) return;
        _downloadingSelfUpdate = true;
        _selfUpdateDownloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_startupUpdateCancellation.Token);
        UpdateSelfUpdateControls();
        try
        {
            var progress = new Progress<SelfUpdateProgress>(update =>
            {
                SelfUpdateHint.Text = update.Message;
                SelfUpdateDownloadProgress.IsIndeterminate = update.Percentage is null;
                if (update.Percentage is { } percent) SelfUpdateDownloadProgress.Value = percent;
            });
            _preparedSelfUpdate = await _selfUpdater.DownloadAndStageAsync(_availableSelfUpdate, progress, _selfUpdateDownloadCancellation.Token);
            AppendLog($"HM AppKit v{_preparedSelfUpdate.Release.Version} 更新包已通过 SHA-256 与版本核验，等待确认重启。");
        }
        catch (OperationCanceledException) when (_selfUpdateDownloadCancellation.IsCancellationRequested)
        {
            AppendLog("已取消 HM AppKit 更新包下载，当前程序、配置和缓存保持原状。");
        }
        catch (Exception ex)
        {
            AppendLog($"HM AppKit 更新包准备失败：{InstallerNetwork.DescribeFailure(ex)}；当前版本继续运行。");
            SelfUpdateHint.Text = "更新下载未完成，可点击重试；当前版本继续运行。";
            MessageBox.Show(this, $"更新包未能完成校验或下载，请稍后重试。\n\n{InstallerNetwork.SafeSummary(ex)}", "HM AppKit 更新", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            _downloadingSelfUpdate = false;
            _selfUpdateDownloadCancellation.Dispose();
            _selfUpdateDownloadCancellation = null;
            UpdateSelfUpdateControls();
        }
    }

    private async Task RestartForSelfUpdateAsync()
    {
        if (_isRunning || _preparedSelfUpdate is null || _applyingSelfUpdate) return;
        _applyingSelfUpdate = true;
        UpdateSelfUpdateControls();
        InstallButton.IsEnabled = false;
        try
        {
            if (!string.Equals(Path.GetFileName(Environment.ProcessPath), SelfUpdateClient.ExecutableName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("当前为开发调试进程，请运行发布版 HM AppKit.exe 后使用自动更新。");
            SaveState();
            var preserve = HasCustomCatalog() ? new[] { "Data/catalog.json" } : [];
            var manifest = await new UpdateApplyService().CreateManifestAsync(new UpdateApplyPreparation
            {
                StageDirectory = _preparedSelfUpdate.StageDirectory,
                TargetDirectory = AppContext.BaseDirectory,
                ManagedFiles = _preparedSelfUpdate.RelativeFiles,
                PreserveRelativePaths = preserve,
                TargetVersion = _preparedSelfUpdate.Release.Version,
                ArchiveSha256 = _preparedSelfUpdate.Sha256
            }, _startupUpdateCancellation.Token);
            var start = new ProcessStartInfo(_preparedSelfUpdate.ExecutablePath)
            {
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Normal
            };
            if (!CanWriteAppDirectory()) { start.UseShellExecute = true; start.Verb = "runas"; }
            start.ArgumentList.Add("--apply-update");
            start.ArgumentList.Add(manifest);
            using var helper = Process.Start(start) ?? throw new IOException("更新程序未能启动。");
            AppendLog($"正在重启更新到 HM AppKit v{_preparedSelfUpdate.Release.Version}，配置和安装包缓存将保留。");
            Close();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AppendLog("已取消更新的管理员授权，当前版本继续运行。");
            _applyingSelfUpdate = false;
            SetRunning(false);
        }
        catch (Exception ex)
        {
            _applyingSelfUpdate = false;
            _preparedSelfUpdate = null;
            SetRunning(false);
            AppendLog($"无法启动 HM AppKit 更新：{ex.Message}");
            MessageBox.Show(this, $"当前版本继续运行，配置和安装包缓存保持原状。\n\n{ex.Message}", "更新未开始", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static bool HasCustomCatalog()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "catalog.json");
        if (!File.Exists(path)) return false;
        using var embedded = typeof(MainWindow).Assembly.GetManifestResourceStream("FoxDeploy.catalog.json")
            ?? throw new InvalidDataException("找不到当前版本的内置应用目录。");
        using var local = File.OpenRead(path);
        return !CryptographicOperations.FixedTimeEquals(SHA256.HashData(embedded), SHA256.HashData(local));
    }

    private static bool CanWriteAppDirectory()
    {
        var probe = Path.Combine(AppContext.BaseDirectory, ".hm-appkit-update-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }

    private void SelfUpdateDismiss_Click(object sender, RoutedEventArgs e)
    {
        _selfUpdateDismissed = true;
        UpdateSelfUpdateControls();
    }

    private void UpdateSelfUpdateControls()
    {
        SelfUpdateBanner.Visibility = _availableSelfUpdate is not null && !_selfUpdateDismissed ? Visibility.Visible : Visibility.Collapsed;
        SelfUpdateCheckButton.IsEnabled = !_checkingSelfUpdate && !_downloadingSelfUpdate && !_applyingSelfUpdate;
        SelfUpdateDismissButton.IsEnabled = !_downloadingSelfUpdate && !_applyingSelfUpdate;
        SelfUpdateDownloadProgress.Visibility = _downloadingSelfUpdate ? Visibility.Visible : Visibility.Collapsed;
        if (_availableSelfUpdate is null) return;
        SelfUpdateTitle.Text = _preparedSelfUpdate is null ? $"HM AppKit v{_availableSelfUpdate.Version} 已发布" : $"HM AppKit v{_availableSelfUpdate.Version} 已准备好";
        SelfUpdateActionButton.Content = _applyingSelfUpdate ? "正在重启…" : _downloadingSelfUpdate ? "取消下载" : _preparedSelfUpdate is not null ? "重启更新" : "下载更新";
        SelfUpdateActionButton.IsEnabled = !_applyingSelfUpdate && (_preparedSelfUpdate is null || !_isRunning);
        if (!_downloadingSelfUpdate) SelfUpdateHint.Text = _preparedSelfUpdate is null
            ? "下载后确认重启，原有配置和安装包缓存会保留。"
            : _isRunning ? "安装任务结束后可重启更新，配置和缓存会保留。" : "校验完成，点击重启更新；配置和缓存会保留。";
    }
}

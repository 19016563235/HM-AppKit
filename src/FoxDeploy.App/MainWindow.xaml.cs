using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FoxDeploy.Core;
using Microsoft.Win32;

namespace FoxDeploy.App;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string _stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HM AppKit");
    private readonly ObservableCollection<AppRow> _rows = [];
    private readonly InstallerPayloadProvider _payloadProvider = new();
    private readonly DeploymentEngine _engine;
    private readonly CancellationTokenSource _startupUpdateCancellation = new();
    private readonly HashSet<string> _terminalIds = [];
    private readonly List<AppDefinition> _customApps = [];
    private readonly ICollectionView _view;
    private IReadOnlyList<DriveSnapshot> _drives = [];
    private DeploymentSettings _settings = new();
    private CancellationTokenSource? _runCancellation;
    private string _role = "公共";
    private string? _sessionLog;
    private bool _loading = true;
    private bool _isRunning;
    private bool _wingetReady;
    private bool _sourceCheckCompleted;
    private string? _explicitSelectedDrive;
    private string? _selectedDrive;
    private string _baseFolder = "Software";
    private bool _smokeTest;

    public MainWindow() : this(smokeTest: false) { }

    public MainWindow(bool smokeTest)
    {
        _smokeTest = smokeTest;
        InitializeComponent();
        _engine = new(payloadProvider: _payloadProvider);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 24);
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRow;
        AppItems.ItemsSource = _view;
        LoadState();
        _settings = ReadSettings();
        LoadCatalog();
        _loading = false;
        RefreshDrives();
        UpdateRoles();
        UpdateInstallServiceStatus();
        UpdateSummary();
        InitializeSelfUpdater();
        InstallButton.ToolTip = "点击后将接受所选应用的软件许可及 WinGet 源协议，并按计划执行安装。静默安装仍可能显示 Windows 管理员授权提示。";
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                AdminButton.Content = "✓ 管理员";
                AdminButton.IsEnabled = false;
            }
        }
        Loaded += async (_, _) =>
        {
            if (_smokeTest) return;
            await Task.WhenAll(CheckWingetAsync(), CheckSelfUpdateAsync(manual: false));
        };
        AppendLog($"HM AppKit v{CurrentAppVersion} 已就绪。安装时自动检查官方更新并校验缓存；安装包保存在程序旁 Local Installer 文件夹。");
        RunStatus.ToolTip = Path.Combine(AppContext.BaseDirectory, "Local Installer");
    }

    private async Task CheckWingetAsync()
    {
        try
        {
            var version = await _engine.GetWingetVersionAsync();
            if (!_engine.IsWingetAvailable || !System.Text.RegularExpressions.Regex.IsMatch(version.Trim(), "^v?[0-9]+\\.[0-9]+"))
                throw new InvalidOperationException(version);
            _wingetReady = true;
            WingetStatus.ToolTip = $"WinGet {version.Trim()} 已就绪。安装时检查官方更新；安装包自动缓存到程序旁 Local Installer。";
        }
        catch (Exception ex)
        {
            _wingetReady = false;
            WingetStatus.ToolTip = "自定义 WinGet 应用需要 Microsoft“应用安装程序”；官方直连及已校验缓存仍可使用。";
            AppendLog($"WinGet 检测：{ex.Message}");
            RunHint.Text = "需要 Windows 应用安装程序（WinGet）。可从 Microsoft Store 安装或更新。";
        }
        _sourceCheckCompleted = true;
        UpdateInstallServiceStatus();
    }

    private void LoadState()
    {
        var localAppData = Path.GetDirectoryName(_stateDirectory)!;
        foreach (var path in new[]
        {
            Path.Combine(_stateDirectory, "settings.json"),
            Path.Combine(localAppData, "HM-AutoSetup", "settings.json"),
            Path.Combine(localAppData, "FoxDeploy", "settings.json")
        })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var state = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(path), JsonOptions);
                if (state is null) continue;
                _settings = state.Settings ?? new DeploymentSettings();
                InstallerSources.NormalizeSettings(_settings);
                if (state.SchemaVersion < 2 && string.Equals(_settings.BaseFolder, "CompanyApps", StringComparison.OrdinalIgnoreCase))
                    _settings.BaseFolder = "Software";
                _settings.DrivePriority = new DeploymentSettings().DrivePriority;
                _baseFolder = _settings.BaseFolder;
                _explicitSelectedDrive = _settings.SelectedDrive;
                _customApps.AddRange(state.CustomApps ?? []);
                _savedPaths = state.AppPaths ?? [];
                SkipInstalledCheck.IsChecked = _settings.SkipInstalled;
                SilentCheck.IsChecked = _settings.Silent;
                if (!string.Equals(Path.GetDirectoryName(path), _stateDirectory, StringComparison.OrdinalIgnoreCase))
                    AppendLog("已读取旧版配置；后续配置将保存到 HM AppKit，旧文件保留。");
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                AppendLog($"配置未能读取，继续检查其他配置：{ex.Message}");
            }
        }
    }

    private Dictionary<string, string> _savedPaths = [];

    private void LoadCatalog()
    {
        try
        {
            var catalogPath = Path.Combine(AppContext.BaseDirectory, "Data", "catalog.json");
            string json;
            if (File.Exists(catalogPath)) json = File.ReadAllText(catalogPath);
            else
            {
                using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("FoxDeploy.catalog.json")
                    ?? throw new FileNotFoundException("找不到内置应用目录。");
                using var reader = new StreamReader(stream);
                json = reader.ReadToEnd();
            }
            var apps = AppCatalog.Parse(json);
            foreach (var app in apps.Concat(_customApps).DistinctBy(a => a.Id)) AddRow(app);
            AppendLog($"应用目录已加载：{_rows.Count} 个应用。");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException)
        {
            AppendLog($"应用目录读取失败：{ex.Message}");
            RunStatus.Text = "应用目录读取失败，可通过“自定义应用”添加安装包。";
            foreach (var app in _customApps.DistinctBy(a => a.Id)) AddRow(app);
        }
    }

    private void AddRow(AppDefinition app)
    {
        var row = new AppRow(app) { Selected = app.Category == "公共" };
        if (_savedPaths.TryGetValue(app.Id, out var path)) row.TargetPath = path;
        row.PropertyChanged += Row_PropertyChanged;
        _rows.Add(row);
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading) return;
        if (e.PropertyName == nameof(AppRow.Selected)) UpdateSummary();
        if (e.PropertyName == nameof(AppRow.TargetPath))
        {
            if (sender is AppRow row) UpdateRowPath(row);
            SaveState();
        }
    }

    private bool FilterRow(object value)
    {
        if (value is not AppRow row) return false;
        var query = SearchBox?.Text?.Trim() ?? "";
        var isRoleMatch = MatchesRole(row.App, _role);
        return isRoleMatch && (string.IsNullOrEmpty(query) || $"{row.App.Name} {row.App.Category} {string.Join(' ', row.App.Roles)} {row.App.PackageId} {row.App.Description}".Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesRole(AppDefinition app, string role) =>
        role == "全部" || app.Category == "公共" || app.Category == role || app.Roles.Contains(role, StringComparer.OrdinalIgnoreCase);

    private DeploymentSettings ReadSettings()
    {
        var settings = new DeploymentSettings
        {
            SelectedDrive = _explicitSelectedDrive,
            BaseFolder = _baseFolder,
            SkipInstalled = SkipInstalledCheck.IsChecked == true,
            Silent = SilentCheck.IsChecked == true,
            KeepInstallers = true,
            RequireCustomLocation = true,
            SourceMode = InstallationSourceMode.Auto,
            LocalInstallerDirectory = Path.Combine(AppContext.BaseDirectory, "Local Installer")
        };
        InstallerSources.NormalizeSettings(settings);
        return settings;
    }

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings = ReadSettings();
        UpdatePathHints();
        SaveState();
    }

    private void UpdateInstallServiceStatus()
    {
        var requiresWinget = _rows.Any(row => InstallerSources.UsesWinget(row.App, _settings));
        WingetStatus.Text = !requiresWinget || _sourceCheckCompleted && _wingetReady
            ? "安装时自动检查官方更新"
            : !_sourceCheckCompleted ? "正在检查安装服务…" : "部分 WinGet 应用暂不可用";
    }

    private void SaveState()
    {
        if (_loading || _smokeTest) return;
        try
        {
            _settings = ReadSettings();
            Directory.CreateDirectory(_stateDirectory);
            var state = new SavedState
            {
                SchemaVersion = 5,
                Settings = _settings,
                CustomApps = _customApps,
                AppPaths = _rows.Where(r => !string.IsNullOrWhiteSpace(r.TargetPath)).ToDictionary(r => r.App.Id, r => r.TargetPath.Trim())
            };
            var path = Path.Combine(_stateDirectory, "settings.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state, JsonOptions), Encoding.UTF8);
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLog($"保存配置失败：{ex.Message}");
        }
    }

    private void RefreshDrives()
    {
        try
        {
            _drives = DriveResolver.GetDrives();
            UpdateDriveCards();
            UpdatePathHints();
        }
        catch (Exception ex)
        {
            AppendLog($"磁盘检测失败：{ex.Message}");
        }
    }

    private void UpdateDriveCards()
    {
        _selectedDrive = string.IsNullOrWhiteSpace(_explicitSelectedDrive)
            ? DriveSelectionPolicy.ResolveDefaultDrive(_drives)
            : _explicitSelectedDrive.Trim().TrimEnd(':', '\\', '/').ToUpperInvariant();
        DriveCards.ItemsSource = DriveSelectionPolicy.OrderDrives(_drives).Select(drive =>
        {
            var letter = drive.Name.TrimEnd(':', '\\', '/').ToUpperInvariant();
            var selected = letter == _selectedDrive;
            return new DriveCard(letter, $"{letter}盘", $"{drive.AvailableBytes / 1073741824d:0.0} GB 可用",
                selected ? "#F3F5F8" : "#FAFBFD", selected ? "#B6BFD5" : "#EDF0F5",
                selected ? "#7483B6" : "#3D4D66",
                selected ? "当前安装磁盘；点击其他盘符可切换。" : $"将跟随根目录的应用安装到 {letter}: 盘。");
        }).ToList();
        AutoDriveButton.Visibility = _explicitSelectedDrive is null ? Visibility.Collapsed : Visibility.Visible;
        AutoDriveButton.ToolTip = "恢复默认磁盘：优先 D、E、F 及其他内置数据盘，最后使用 C 盘。";
    }

    private void Drive_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || sender is not Button { Tag: string letter }) return;
        _explicitSelectedDrive = letter;
        _settings = ReadSettings();
        UpdateDriveCards();
        UpdatePathHints();
        SaveState();
    }

    private void AutoDrive_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        _explicitSelectedDrive = null;
        _settings = ReadSettings();
        RefreshDrives();
        SaveState();
    }

    private void EditRoot_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        var dialog = new PathEditDialog("修改安装根目录", _baseFolder,
            $"文件夹相对于所选磁盘，例如 Software 或 Software\\Work。", value => DriveResolver.ValidateAbsolutePath("C:\\" + value), allowDefault: false) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _baseFolder = DriveResolver.ValidateAbsolutePath("C:\\" + dialog.Value)[3..];
        _settings = ReadSettings();
        UpdatePathHints();
        SaveState();
    }

    private void UpdatePathHints()
    {
        RootPathText.Text = _selectedDrive is null ? _baseFolder : $"{_selectedDrive}:\\{_baseFolder}";
        foreach (var row in _rows) UpdateRowPath(row);
    }

    private void UpdateRowPath(AppRow row)
    {
        var capabilities = InstallerSources.GetCapabilities(row.App, _settings);
        var profile = InstallerSources.GetProfile(row.App, _settings);
        var usesSilent = _settings.Silent && capabilities.SilentSupported && profile?.Adapter != "jingmai";
        row.UpdateSource(capabilities, profile?.InteractiveFallback == true, usesSilent);
        try
        {
            row.PathHint = DriveResolver.ResolveTarget(row.App, row.TargetPath, _settings, _drives);
            row.PathColor = capabilities.LocationSupported ? "#77869C" : "#AD956E";
        }
        catch (Exception ex)
        {
            row.PathHint = ex.Message;
            row.PathColor = "#B47D7D";
        }
    }

    private void RefreshDrives_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        _settings = ReadSettings();
        RefreshDrives();
        AppendLog("已重新检测本机磁盘。");
    }

    private void Role_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _isRunning) return;
        _role = button.Tag as string ?? "全部";
        if (_role != "全部")
            foreach (var row in _rows) row.Selected = MatchesRole(row.App, _role);
        UpdateRoles();
        _view.Refresh();
        UpdateSummary();
    }

    private void UpdateRoles()
    {
        if (_view is ListCollectionView list)
        {
            // Put the active team's clients first, keeping shared essentials selected below.
            list.CustomSort = Comparer<AppRow>.Create((left, right) =>
            {
                var teamView = _role is not ("公共" or "全部");
                var leftRank = teamView && left.App.Category == "公共" ? 1 : 0;
                var rightRank = teamView && right.App.Category == "公共" ? 1 : 0;
                var rank = leftRank.CompareTo(rightRank);
                return rank != 0 ? rank : _rows.IndexOf(left).CompareTo(_rows.IndexOf(right));
            });
        }
        CustomRoleButton.Visibility = _customApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in RoleButtons.Children.OfType<Button>())
        {
            var active = (string)button.Tag == _role;
            button.Background = Brush(active ? "#F3F5F8" : "#FFFFFF");
            button.Foreground = Brush(active ? "#7483B6" : "#8995A8");
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();
    private void SelectVisible_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        foreach (var row in _view.OfType<AppRow>()) row.Selected = true;
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        foreach (var row in _rows) row.Selected = false;
    }

    private void UpdateSummary()
    {
        var count = _rows.Count(r => r.Selected);
        SelectionSummary.Text = $"已选择 {count} 个应用";
        InstallButton.IsEnabled = !_isRunning && !_applyingSelfUpdate && count > 0;
    }

    private void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || sender is not Button { Tag: AppRow row }) return;
        var dialog = new PathEditDialog($"{row.App.Name} · 安装目录", row.TargetPath, row.DisplayPath, value =>
        {
            if (!string.IsNullOrWhiteSpace(value)) DriveResolver.ResolveTarget(row.App, value, ReadSettings(), _drives);
        }, allowDefault: true) { Owner = this };
        if (dialog.ShowDialog() == true) row.TargetPath = dialog.Value;
    }

    private List<AppRow> SelectedRows() => _rows.Where(r => r.Selected).ToList();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _applyingSelfUpdate) return;
        KeyboardCommit();
        var selected = SelectedRows();
        if (selected.Count == 0) { RunStatus.Text = "请先选择需要安装的应用。"; return; }
        _settings = ReadSettings();
        SaveState();
        RefreshDrives();
        _terminalIds.Clear();
        foreach (var row in selected) row.SetStatus(InstallState.Pending, "等待开始");
        _runCancellation = new CancellationTokenSource();
        SetRunning(true);
        UpdateRunProgress(selected.Count);
        RunStatus.Text = $"正在部署 {selected.Count} 个应用…";
        RunHint.Text = "已接受所选软件及 WinGet 源协议。Windows 管理员授权提示仍需在本机确认。";
        AppendLog($"── 开始部署：{selected.Count} 个应用；静默安装 {(_settings.Silent ? "开启" : "关闭")} ──");
        AppendLog($"自动缓存：{Path.Combine(AppContext.BaseDirectory, "Local Installer")}；安装时检查官方更新。");
        Exception? runError = null;
        var runCancelled = false;
        var progress = new Progress<InstallUpdate>(update =>
        {
            var row = selected.FirstOrDefault(r => r.App.Id == update.AppId);
            if (row is null) return;
            row.SetStatus(update.State, update.Message);
            if (IsTerminalState(update.State))
                _terminalIds.Add(update.AppId);
            UpdateRunProgress(selected.Count);
            RunStatus.Text = $"{row.App.Name} · {update.Message}";
            if (!update.IsLogged) AppendLog($"[{row.App.Name}] {update.Message}");
        });
        try
        {
            var requests = selected.Select(r => new InstallRequest(r.App, string.IsNullOrWhiteSpace(r.TargetPath) ? null : r.TargetPath.Trim())).ToList();
            await _engine.RunAsync(requests, _settings, progress, new Progress<string>(AppendLog), _runCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            runCancelled = true;
        }
        catch (Exception ex)
        {
            runError = ex;
        }
        finally
        {
            // Progress<T> posts UI callbacks. Apply every reported result before final counts.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            foreach (var row in selected)
            {
                if (!IsTerminalState(row.State))
                    row.SetStatus(row.State == InstallState.Installing ? InstallState.Unverified : InstallState.Cancelled,
                        row.State == InstallState.Installing ? "队列已结束，安装结果未核验，请检查应用与日志。" : "本次未执行");
                _terminalIds.Add(row.App.Id);
            }
            UpdateRunProgress(selected.Count);
            var success = selected.Count(r => r.State is InstallState.Succeeded or InstallState.AlreadyInstalled);
            var unresolved = selected.Count(r => r.State is InstallState.Failed or InstallState.Skipped or InstallState.Unverified);
            var cancelled = selected.Count(r => r.State == InstallState.Cancelled);
            var outcome = runError is not null ? $"部署异常：{runError.Message}。" : runCancelled || _runCancellation.IsCancellationRequested ? "已停止后续安装。" : "本次部署结束。";
            RunStatus.Text = $"{outcome} {success} 个已安装，{unresolved} 个需关注，{cancelled} 个未执行。";
            RunHint.Text = "总体进度按已结束项累计，包含失败、阻止和未执行；具体安装结果见应用状态与日志。";
            AppendLog(RunStatus.Text);
            _runCancellation.Dispose();
            _runCancellation = null;
            SetRunning(false);
        }
    }

    private void KeyboardCommit() => System.Windows.Input.Keyboard.ClearFocus();

    private static bool IsTerminalState(InstallState state) => state is
        InstallState.Succeeded or InstallState.AlreadyInstalled or InstallState.Failed or
        InstallState.Skipped or InstallState.Cancelled or InstallState.Unverified;

    private void UpdateRunProgress(int total)
    {
        var ended = Math.Min(_terminalIds.Count, Math.Max(0, total));
        var percentage = total > 0 ? ended * 100d / total : 0;
        RunProgress.Value = percentage;
        RunProgressSummary.Text = total > 0
            ? $"总体进度 {Math.Floor(percentage):0}% · 已结束 {ended}/{total} 项"
            : "总体进度 0%";
    }

    private void SetRunning(bool running)
    {
        _isRunning = running;
        InstallButton.IsEnabled = !running && !_applyingSelfUpdate && _rows.Any(r => r.Selected);
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = running;
        DriveCards.IsEnabled = !running;
        AutoDriveButton.IsEnabled = !running;
        RefreshDrivesButton.IsEnabled = !running;
        EditRootButton.IsEnabled = !running;
        AddCustomButton.IsEnabled = !running;
        SearchBox.IsEnabled = !running;
        SelectVisibleButton.IsEnabled = !running;
        ClearSelectionButton.IsEnabled = !running;
        SkipInstalledCheck.IsEnabled = !running;
        SilentCheck.IsEnabled = !running;
        if (AdminButton.Content is string text && !text.StartsWith('✓')) AdminButton.IsEnabled = !running;
        foreach (var row in _rows) row.IsEditable = !running;
        foreach (var button in RoleButtons.Children.OfType<Button>()) button.IsEnabled = !running;
        UpdateSelfUpdateControls();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _runCancellation?.Cancel();
        StopButton.IsEnabled = false;
        RunStatus.Text = "将在当前应用安装完成后停止，请保持窗口打开。";
        AppendLog("已请求停止后续安装，正在运行的安装器将继续完成。");
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AppendLog(message)); return; }
        var text = $"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}";
        LogBox.AppendText(text);
        LogBox.ScrollToEnd();
        try
        {
            var directory = Path.Combine(_stateDirectory, "Logs");
            Directory.CreateDirectory(directory);
            _sessionLog ??= Path.Combine(directory, $"HM-AppKit-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            File.AppendAllText(_sessionLog, text, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = Path.Combine(_stateDirectory, "Logs");
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex) { AppendLog($"日志目录未能打开：{ex.Message}"); }
    }

    private void Admin_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _applyingSelfUpdate) return;
        try
        {
            SaveState();
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory });
            Close();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { AppendLog("已取消管理员权限请求。"); }
        catch (Exception ex) { AppendLog($"无法以管理员身份重启：{ex.Message}"); }
    }

    private void AddCustom_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        var dialog = new CustomAppDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Definition is not { } app) return;
        if (_rows.Any(r => r.App.PackageId.Equals(app.PackageId, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "该 WinGet 包 ID 已经在应用清单中。请直接搜索并选择现有应用。", "应用已存在", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _customApps.Add(app);
        AddRow(app);
        var row = _rows[^1];
        row.Selected = true;
        UpdateRowPath(row);
        _role = "自定义";
        UpdateRoles();
        _view.Refresh();
        UpdateSummary();
        SaveState();
        AppendLog($"已添加自定义应用：{app.Name}（{app.PackageId}）。");
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isRunning)
        {
            e.Cancel = true;
            MessageBox.Show(this, "安装任务正在运行。请点击“完成当前后停止”，等待当前安装器结束后再关闭窗口。", "请等待当前安装完成", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _startupUpdateCancellation.Cancel();
        _selfUpdateDownloadCancellation?.Cancel();
        SaveState();
    }

    public async void RenderSmokeTest(string path, string? role = null)
    {
        _smokeTest = true;
        if (!string.IsNullOrWhiteSpace(role) && RoleButtons.Children.OfType<Button>().Any(button => button.Tag as string == role))
        {
            _role = role;
            foreach (var row in _rows) row.Selected = MatchesRole(row.App, role);
            UpdateRoles();
            _view.Refresh();
            UpdateSummary();
        }
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(250);
        UpdateLayout();
        var content = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(content);
        var margin = content.Margin;
        var size = new Size(content.ActualWidth + margin.Left + margin.Right, content.ActualHeight + margin.Top + margin.Bottom);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Background, null, new Rect(size));
            context.DrawRectangle(new VisualBrush(content), null, new Rect(margin.Left, margin.Top, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)(size.Width * dpi.DpiScaleX), (int)(size.Height * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using (var stream = File.Create(fullPath)) encoder.Save(stream);
        Close();
    }

    private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
    private sealed record DriveCard(string Letter, string Name, string FreeText, string Background, string BorderColor, string TextColor, string Tooltip);
}

public sealed class SavedState
{
    public int SchemaVersion { get; set; }
    public DeploymentSettings? Settings { get; set; }
    public Dictionary<string, string>? AppPaths { get; set; }
    public List<AppDefinition>? CustomApps { get; set; }
}

public sealed class AppRow(AppDefinition app) : INotifyPropertyChanged
{
    private bool _selected;
    private bool _isEditable = true;
    private string _targetPath = "";
    private string _pathHint = "自动选择可用磁盘";
    private string _pathColor = "#7C8CA3";
    private string _status = "待安装";
    private string _statusMessage = "";
    private string _statusDotColor = "#A7B0BE";
    private string _statusBackground = "#FFFFFF";
    private InstallerCapabilities _capabilities = new(false, false, false, "", "正在检查安装源。");
    private bool _interactiveFallback;
    private bool _usesSilent;
    public AppDefinition App { get; } = app;
    public ImageSource? Logo { get; } = AppLogoAssets.Get(app.Id);
    public string Detail => $"{App.Category} · {App.Description}";
    public string Initial => string.IsNullOrEmpty(App.Name) ? "+" : App.Name[..1].ToUpperInvariant();
    public string IconBackground => App.Category switch { "公共" => "#F1F3F7", "运营" => "#F6F3EF", "客服" => "#F0F5F2", "短视频" => "#F3F1F6", _ => "#F1F3F6" };
    public string IconColor => App.Category switch { "公共" => "#7D89AC", "运营" => "#A6927C", "客服" => "#819D8E", "短视频" => "#9C8DAA", _ => "#8995A8" };
    public string InstallModeLabel => _usesSilent ? "静默" : "常规";
    public string InstallModeColor => _usesSilent ? "#6B8F7B" : "#8490A2";
    public string InstallModeBackground => _usesSilent ? "#EFF4F1" : "#F0F2F6";
    public string SilentLabel => _usesSilent ? "静默安装" : _interactiveFallback ? "常规安装·预填目录" : "常规安装";
    public string LocationLabel => _interactiveFallback && !_usesSilent ? "向导将预填目标目录，请确认后安装" : _capabilities.LocationSupported ? "可指定目录" : "请在安装向导中选择此目标目录";
    public string SourceHint => $"{_capabilities.Message}\n{SilentLabel} · {LocationLabel}" + (App.OnlineInstaller is not null ? $"\n安装时检查官方更新，校验并复用缓存。\n{Path.Combine(AppContext.BaseDirectory, "Local Installer")}" : "");
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public bool IsEditable { get => _isEditable; set => Set(ref _isEditable, value); }
    public string TargetPath { get => _targetPath; set { Set(ref _targetPath, value); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PathModeLabel))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCustomPath))); } }
    public string PathHint { get => _pathHint; set { Set(ref _pathHint, value); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayPath))); } }
    public string DisplayPath => PathHint;
    public bool HasCustomPath => !string.IsNullOrWhiteSpace(TargetPath);
    public string PathModeLabel => string.IsNullOrWhiteSpace(TargetPath) ? "跟随安装根目录" : "自定义目录";
    public string CapabilitiesHint => $"{App.Description}\n{SourceHint}\n{App.Notes}";
    public string PathColor { get => _pathColor; set => Set(ref _pathColor, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public string StatusColor => "#8793A5";
    public string StatusDotColor { get => _statusDotColor; private set => Set(ref _statusDotColor, value); }
    public string StatusBackground { get => _statusBackground; private set => Set(ref _statusBackground, value); }
    public InstallState State { get; private set; } = InstallState.Pending;
    public void UpdateSource(InstallerCapabilities capabilities, bool interactiveFallback, bool usesSilent)
    {
        _capabilities = capabilities;
        _interactiveFallback = interactiveFallback;
        _usesSilent = usesSilent;
        foreach (var property in new[] { nameof(InstallModeLabel), nameof(InstallModeColor), nameof(InstallModeBackground), nameof(SourceHint), nameof(CapabilitiesHint), nameof(SilentLabel), nameof(LocationLabel) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
    public void SetStatus(InstallState state, string message)
    {
        State = state;
        StatusMessage = message;
        Status = state switch
        {
            InstallState.Pending => "待安装", InstallState.Checking => "安装中", InstallState.Installing => "安装中",
            InstallState.Succeeded => "已安装", InstallState.AlreadyInstalled => "已安装", InstallState.Failed => "失败",
            InstallState.Skipped => "已阻止", InstallState.Cancelled => "未执行", InstallState.Unverified => "待核验", _ => "等待"
        };
        (StatusDotColor, StatusBackground) = state switch
        {
            InstallState.Succeeded or InstallState.AlreadyInstalled => ("#6B8F7B", "#EFF4F1"),
            InstallState.Installing or InstallState.Checking => ("#B59B64", "#F6F3EC"),
            InstallState.Failed => ("#B47D7D", "#F8F0F0"),
            InstallState.Skipped or InstallState.Unverified => ("#AD956E", "#F6F3ED"),
            _ => ("#A7B0BE", "#FFFFFF")
        };
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

internal sealed class PathEditDialog : Window
{
    private readonly TextBox _path = new();
    private readonly CheckBox _useDefault = new() { Content = "跟随安装根目录", Margin = new Thickness(0, 0, 0, 14) };
    private readonly TextBlock _error = new() { Foreground = new SolidColorBrush(Color.FromRgb(180, 125, 125)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontSize = 11 };
    private readonly Action<string> _validate;
    private readonly bool _allowDefault;
    public string Value { get; private set; } = "";

    public PathEditDialog(string title, string value, string hint, Action<string> validate, bool allowDefault)
    {
        Title = title;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _validate = validate;
        _allowDefault = allowDefault;
        var body = new StackPanel { Margin = new Thickness(25, 23, 25, 23) };
        body.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = allowDefault ? "只调整此应用的安装位置。" : "确认后应用到所有跟随根目录的应用。", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(119, 133, 154)), Margin = new Thickness(0, 8, 0, 20) });
        if (allowDefault) body.Children.Add(_useDefault);
        var field = new Grid();
        field.ColumnDefinitions.Add(new ColumnDefinition());
        if (allowDefault) field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _path.Text = string.IsNullOrWhiteSpace(value) && allowDefault && hint.Length > 2 && hint[1] == ':' ? hint : value;
        field.Children.Add(_path);
        if (allowDefault)
        {
            var browse = new Button { Content = "浏览…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
            Grid.SetColumn(browse, 1);
            field.Children.Add(browse);
            browse.Click += (_, _) =>
            {
                var picker = new OpenFolderDialog { Title = title, Multiselect = false };
                if (Directory.Exists(_path.Text)) picker.InitialDirectory = _path.Text;
                if (picker.ShowDialog(this) != true) return;
                _path.Text = picker.FolderName;
                _useDefault.IsChecked = false;
                _path.IsEnabled = true;
            };
            _useDefault.IsChecked = string.IsNullOrWhiteSpace(value);
            _path.IsEnabled = _useDefault.IsChecked != true;
            _useDefault.Click += (_, _) => _path.IsEnabled = _useDefault.IsChecked != true;
        }
        body.Children.Add(field);
        body.Children.Add(new TextBlock { Text = allowDefault ? "使用完整路径，例如 D:\\Software\\WeChat；也可以恢复跟随根目录。" : hint, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(132, 144, 162)), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 19, 0, 0) };
        actions.Children.Add(new Button { Content = "取消", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 9, 0) });
        var confirm = new Button { Content = "确定", IsDefault = true, MinWidth = 88, Style = (Style)FindResource("PrimaryButton") };
        confirm.Click += (_, _) =>
        {
            var candidate = _allowDefault && _useDefault.IsChecked == true ? "" : _path.Text.Trim();
            try
            {
                if (!_allowDefault || candidate.Length > 0) _validate(candidate);
                else if (_useDefault.IsChecked != true) throw new ArgumentException("请输入安装目录，或选择跟随安装根目录。");
                Value = candidate;
                DialogResult = true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                _error.Text = ex.Message;
            }
        };
        actions.Children.Add(confirm);
        body.Children.Add(actions);
        Content = body;
        Loaded += (_, _) => { if (_path.IsEnabled) { _path.Focus(); _path.SelectAll(); } };
    }
}

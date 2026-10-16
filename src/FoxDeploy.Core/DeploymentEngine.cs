using System.ComponentModel;

namespace FoxDeploy.Core;

public sealed class DeploymentEngine
{
    private static readonly SemaphoreSlim QueueGate = new(1, 1);
    private readonly IProcessRunner _processRunner;
    private readonly IInstallationProbe _installationProbe;
    private readonly string? _wingetPath;
    private readonly Func<IReadOnlyList<DriveSnapshot>> _driveProvider;
    private readonly TimeSpan _installTimeout;
    private readonly IInstallerPayloadProvider _payloadProvider;
    public bool IsWingetAvailable => !string.IsNullOrWhiteSpace(_wingetPath);

    public DeploymentEngine(IProcessRunner? processRunner = null, IInstallationProbe? installationProbe = null,
        string? wingetPath = null, Func<IReadOnlyList<DriveSnapshot>>? driveProvider = null, TimeSpan? installTimeout = null,
        IInstallerPayloadProvider? payloadProvider = null)
    {
        _processRunner = processRunner ?? new SystemProcessRunner();
        _installationProbe = installationProbe ?? new RegistryInstallationProbe();
        _wingetPath = wingetPath ?? FindWinget();
        _driveProvider = driveProvider ?? DriveResolver.GetDrives;
        _installTimeout = installTimeout ?? TimeSpan.FromMinutes(60);
        _payloadProvider = payloadProvider ?? new InstallerPayloadProvider();
        if (_installTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(installTimeout));
    }

    public async Task<string> GetWingetVersionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsWingetAvailable) return "未检测到 WinGet，请从 Microsoft Store 安装或更新“应用安装程序”。";
        var result = await _processRunner.RunAsync(new ProcessRequest(_wingetPath!, ["--version"], TimeSpan.FromSeconds(20)), null, cancellationToken).ConfigureAwait(false);
        if (result.CancellationRequested) throw new OperationCanceledException(cancellationToken);
        if (result.TimedOut) return "WinGet 检测超时。";
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : $"WinGet 无法运行：{DescribeExitCode(result.ExitCode)}";
    }

    public static InstallPlan Plan(InstallRequest request, DeploymentSettings settings, IReadOnlyList<DriveSnapshot>? drives = null)
        => InstallerPlanFactory.Create(request, settings, drives);

    public async Task RunAsync(IReadOnlyList<InstallRequest> requests, DeploymentSettings settings,
        IProgress<InstallUpdate> progress, IProgress<string> log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(log);
        var queue = requests.ToArray();
        var snapshot = new DeploymentSettings
        {
            DrivePriority = [.. settings.DrivePriority ?? []], SelectedDrive = settings.SelectedDrive, BaseFolder = settings.BaseFolder,
            RequireCustomLocation = settings.RequireCustomLocation, Silent = settings.Silent, SkipInstalled = settings.SkipInstalled,
            SourceMode = InstallationSourceMode.Auto, LocalInstallerDirectory = InstallerSources.InstallerDirectory,
            KeepInstallers = true
        };
        if (queue.Select(r => r.App.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != queue.Length)
            throw new ArgumentException("一个安装队列中不能包含重复应用。", nameof(requests));
        foreach (var request in queue) progress.Report(new InstallUpdate(request.App.Id, InstallState.Pending, "等待安装"));
        if (cancellationToken.IsCancellationRequested)
        {
            foreach (var request in queue) progress.Report(new InstallUpdate(request.App.Id, InstallState.Cancelled, "已停止，未启动安装"));
            return;
        }
        var acquired = false;
        try
        {
            try { await QueueGate.WaitAsync(cancellationToken).ConfigureAwait(false); acquired = true; }
            catch (OperationCanceledException)
            {
                foreach (var request in queue) progress.Report(new InstallUpdate(request.App.Id, InstallState.Cancelled, "已停止，未启动安装"));
                return;
            }
            var haltedByTimeout = false;
            foreach (var request in queue)
            {
                if (cancellationToken.IsCancellationRequested || haltedByTimeout)
                {
                    progress.Report(new InstallUpdate(request.App.Id, cancellationToken.IsCancellationRequested ? InstallState.Cancelled : InstallState.Skipped,
                        haltedByTimeout ? "前一项安装超过等待阈值，队列已停止" : "已停止，未启动安装"));
                    continue;
                }
                try
                {
                    AppCatalog.Validate(request.App);
                    var usesWinget = InstallerSources.UsesWinget(request.App, snapshot);
                    progress.Report(new InstallUpdate(request.App.Id, InstallState.Checking, "检查已安装状态与目标目录"));
                    if (snapshot.SkipInstalled)
                    {
                        var installed = _installationProbe.FindInstalled(request.App);
                        if (installed.Count > 0)
                        {
                            var location = installed.Select(x => x.InstallLocation).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                            progress.Report(new InstallUpdate(request.App.Id, InstallState.AlreadyInstalled,
                                string.IsNullOrWhiteSpace(location) ? "检测到已安装，已跳过；未迁移现有安装目录" : $"已安装于 {location}；已跳过，未迁移目录", location));
                            continue;
                        }
                    }
                    if (usesWinget && !IsWingetAvailable)
                    {
                        progress.Report(new InstallUpdate(request.App.Id, InstallState.Failed, "此自定义应用需要 WinGet，请安装 Microsoft“应用安装程序”。"));
                        continue;
                    }
                    if (snapshot.SkipInstalled && usesWinget)
                    {
                        var check = await _processRunner.RunAsync(new ProcessRequest(_wingetPath!,
                            ["list", "--id", request.App.PackageId, "--exact", "--source", "winget", "--accept-source-agreements", "--disable-interactivity"],
                            TimeSpan.FromSeconds(90)), log, cancellationToken).ConfigureAwait(false);
                        if (check.CancellationRequested) { progress.Report(new InstallUpdate(request.App.Id, InstallState.Cancelled, "检查已停止，未启动安装")); continue; }
                        if (check.ExitCode == 0 && !check.TimedOut)
                        {
                            progress.Report(new InstallUpdate(request.App.Id, InstallState.AlreadyInstalled, "WinGet 检测到已安装，已跳过；未迁移现有安装目录"));
                            continue;
                        }
                        if (check.TimedOut || check.ExitCode != unchecked((int)0x8A150014))
                            log.Report($"[{request.App.Name}] 无法完成已安装检查，将由 WinGet --no-upgrade 防止重复升级。");
                    }

                    var plan = Plan(request, snapshot, _driveProvider());
                    cancellationToken.ThrowIfCancellationRequested();
                    if (plan.UsesDefaultLocation)
                        log.Report(plan.TargetDirectory is null
                            ? $"[{request.App.Name}] 已允许使用厂商默认目录；自定义目录不会传给此安装器。"
                            : $"[{request.App.Name}] 此安装器需要在向导中手动选择 {plan.TargetDirectory}；完成后将核对实际安装目录。");
                    if (!usesWinget) progress.Report(new InstallUpdate(request.App.Id, InstallState.Installing, "正在准备并校验安装包", plan.TargetDirectory));
                    await using var payload = usesWinget ? null : await _payloadProvider.PrepareAsync(request.App, snapshot, log, cancellationToken).ConfigureAwait(false);
                    if (payload is not null) plan = InstallerPlanFactory.BindPayload(plan, payload.FilePath);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (plan.RequiresExistingTargetDirectory && plan.TargetDirectory is not null)
                        Directory.CreateDirectory(plan.TargetDirectory);
                    if (plan.Adapter == "jingmai") log.Report($"[{request.App.Name}] 使用官方常规安装向导，预填目录：{plan.TargetDirectory}");
                    else log.Report($"[{request.App.Name}] {plan.DisplayCommand}");
                    progress.Report(new InstallUpdate(request.App.Id, InstallState.Installing,
                        plan.IsSilent ? "正在静默安装；管理员授权窗口仍可能出现" : "正在安装，请完成可能出现的安装向导", plan.TargetDirectory));
                    var result = plan.Adapter == "jingmai"
                        ? await new JingmaiInstaller(_processRunner).RunAsync(payload!.FilePath, plan.TargetDirectory!, _installTimeout, log, cancellationToken).ConfigureAwait(false)
                        : await _processRunner.RunAsync(new ProcessRequest(plan.IsWinget ? _wingetPath! : plan.ExecutablePath, plan.Arguments, _installTimeout, ProtectInstaller: true)
                    {
                        UnquotedFinalArgument = plan.UnquotedFinalArgument,
                        WorkingDirectory = payload is null ? null : Path.GetDirectoryName(payload.FilePath)
                    }, log, cancellationToken).ConfigureAwait(false);
                    haltedByTimeout = result.TimedOut;
                    if (IsAlreadyInstalled(result.ExitCode))
                    {
                        progress.Report(new InstallUpdate(request.App.Id, InstallState.AlreadyInstalled, "安装器报告应用已经安装，未迁移目录"));
                        continue;
                    }
                    if (IsRebootRequired(result.ExitCode))
                    {
                        progress.Report(new InstallUpdate(request.App.Id, InstallState.Unverified, "安装器要求重启；请重启后核对安装目录", plan.TargetDirectory));
                        continue;
                    }
                    if (result.ExitCode != 0)
                    {
                        progress.Report(new InstallUpdate(request.App.Id, InstallState.Failed, $"安装未完成：{DescribeExitCode(result.ExitCode)}。请查看日志。", plan.TargetDirectory));
                        continue;
                    }
                    VerifyInstallation(request.App, plan, progress, result.TimedOut);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    progress.Report(new InstallUpdate(request.App.Id, InstallState.Cancelled, "已停止，未启动安装"));
                }
                catch (NotSupportedException ex)
                {
                    log.Report($"[{request.App.Name}] {InstallerNetwork.DescribeFailure(ex)}");
                    progress.Report(new InstallUpdate(request.App.Id, InstallState.Skipped, InstallerNetwork.SafeSummary(ex)) { IsLogged = true });
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
                {
                    const string message = "此安装程序需要管理员权限，请点击右上角“管理员运行”后重试。";
                    log.Report($"[{request.App.Name}] {message} {InstallerNetwork.DescribeFailure(ex)}");
                    progress.Report(new InstallUpdate(request.App.Id, InstallState.Failed, message, request.TargetDirectory) { IsLogged = true });
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or Win32Exception or HttpRequestException or TaskCanceledException or System.Security.Cryptography.CryptographicException or System.Text.RegularExpressions.RegexMatchTimeoutException)
                {
                    log.Report($"[{request.App.Name}] {InstallerNetwork.DescribeFailure(ex)}");
                    progress.Report(new InstallUpdate(request.App.Id, InstallState.Failed, InstallerNetwork.SafeSummary(ex), request.TargetDirectory) { IsLogged = true });
                }
            }
        }
        finally { if (acquired) QueueGate.Release(); }
    }

    private void VerifyInstallation(AppDefinition app, InstallPlan plan, IProgress<InstallUpdate> progress, bool exceededTimeout)
    {
        var records = _installationProbe.FindInstalled(app);
        var locations = records.Select(r => r.InstallLocation).Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var prefix = exceededTimeout ? "安装曾超过等待阈值，现已退出；" : "";
        if (plan.TargetDirectory is not null && locations.Any(p => !InstallationLocationVerifier.IsWithinTarget(p, plan.TargetDirectory)))
        {
            progress.Report(new InstallUpdate(app.Id, InstallState.Failed, prefix + $"安装目录不符合要求。系统记录：{string.Join("；", locations)}；要求：{plan.TargetDirectory}。未自动迁移文件。", plan.TargetDirectory));
            return;
        }
        if (locations.Length > 0 && locations.All(Directory.Exists))
        {
            var target = plan.TargetDirectory ?? locations[0];
            progress.Report(new InstallUpdate(app.Id, InstallState.Succeeded, prefix + (plan.TargetDirectory is null ? $"已核对厂商默认安装目录：{target}" : $"已核对系统安装记录与目录：{target}"), target));
            return;
        }
        progress.Report(new InstallUpdate(app.Id, InstallState.Unverified,
            prefix + "安装器已正常退出，但没有可核对的 InstallLocation 或目录尚不存在；请人工检查实际安装位置。", plan.TargetDirectory));
    }

    public static string DescribeExitCode(int code) => code switch
    {
        0 => "成功 (0)",
        unchecked((int)0x8A150019) => $"需要管理员权限 ({FormatCode(code)})",
        unchecked((int)0x8A150106) => $"系统内存不足 ({FormatCode(code)})",
        unchecked((int)0x8A150105) => $"磁盘空间不足 ({FormatCode(code)})",
        unchecked((int)0x8A150107) or unchecked((int)0x8A150008) => $"网络不可用或下载失败 ({FormatCode(code)})",
        unchecked((int)0x8A150011) => $"安装包哈希校验失败，已拒绝安装 ({FormatCode(code)})",
        unchecked((int)0x8A15010C) => $"安装器被用户取消 ({FormatCode(code)})",
        unchecked((int)0x8A150102) => $"系统有其他安装正在进行 ({FormatCode(code)})",
        _ => FormatCode(code)
    };

    private static bool IsAlreadyInstalled(int code) => code is unchecked((int)0x8A150061) or unchecked((int)0x8A15010D);
    private static bool IsRebootRequired(int code) => code is 3010 or 1641 or unchecked((int)0x8A150109) or unchecked((int)0x8A15010A) or unchecked((int)0x8A15010B);
    private static string FormatCode(int code) => $"退出码 {code} / 0x{unchecked((uint)code):X8}";

    private static string? FindWinget()
    {
        var candidates = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local)) candidates.Add(Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe"));
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var directory = folder.Trim().Trim('"');
                if (Path.IsPathFullyQualified(directory)) candidates.Add(Path.Combine(directory, "winget.exe"));
            }
            catch (ArgumentException) { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}

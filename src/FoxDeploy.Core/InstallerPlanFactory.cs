namespace FoxDeploy.Core;

public static class InstallerPlanFactory
{
    private const string PayloadPlaceholder = "{InstallerPayload}";

    public static InstallPlan Create(InstallRequest request, DeploymentSettings settings, IReadOnlyList<DriveSnapshot>? drives = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        AppCatalog.Validate(request.App);
        return InstallerSources.UsesWinget(request.App, settings)
            ? CreateWinget(request, settings, drives)
            : CreateDirect(request, settings, drives);
    }

    public static InstallPlan BindPayload(InstallPlan plan, string filePath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsWinget) throw new ArgumentException("WinGet 安装计划不接收外部安装包。", nameof(plan));
        if (!Path.IsPathFullyQualified(filePath)) throw new ArgumentException("安装包必须为完整路径。", nameof(filePath));
        return plan with
        {
            ExecutablePath = plan.ExecutablePath == PayloadPlaceholder ? filePath : plan.ExecutablePath,
            Arguments = plan.Arguments.Select(value => value == PayloadPlaceholder ? filePath : value).ToArray()
        };
    }

    private static InstallPlan CreateWinget(InstallRequest request, DeploymentSettings settings, IReadOnlyList<DriveSnapshot>? drives)
    {
        var app = request.App;
        if (settings.Silent && !app.SilentSupported)
            throw new NotSupportedException($"{app.Name} 未确认支持静默安装，请取消静默选项后重试。");
        if (settings.RequireCustomLocation && !app.LocationSupported)
            throw new NotSupportedException($"{app.Name} 的安装器没有经过确认的自定义目录支持；已跳过，避免安装到默认磁盘。");
        var target = app.LocationSupported ? DriveResolver.ResolveTarget(app, request.TargetDirectory, settings, drives) : null;
        var arguments = new List<string>
        {
            "install", "--id", app.PackageId, "--exact", "--source", "winget",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
            settings.Silent ? "--silent" : "--interactive"
        };
        if (settings.SkipInstalled) arguments.Add("--no-upgrade");
        if (target is not null) { arguments.Add("--location"); arguments.Add(target); }
        return new InstallPlan(app, target, arguments.AsReadOnly(), target is null, settings.Silent);
    }

    private static InstallPlan CreateDirect(InstallRequest request, DeploymentSettings settings, IReadOnlyList<DriveSnapshot>? drives)
    {
        var app = request.App;
        var profile = InstallerSources.GetProfile(app, settings)
            ?? throw new NotSupportedException($"{app.Name} 未配置可核验的安装包来源。");
        var silent = settings.Silent && profile.SilentSupported;
        if (settings.Silent && !profile.SilentSupported && !profile.InteractiveFallback)
            throw new NotSupportedException($"{app.Name} 的此来源未确认支持静默安装，请取消静默选项后重试。");
        if (silent && !profile.LocationSupported)
            throw new NotSupportedException($"{app.Name} 的此来源未确认支持静默指定目录，请取消静默选项并在安装向导中选择目录。");

        var executable = PayloadPlaceholder;
        // Even a manual-only installer has a requested target: verify the user's final
        // choice against it after the wizard exits, rather than accepting a default drive.
        var target = DriveResolver.ResolveTarget(app, request.TargetDirectory, settings, drives);
        if (profile.Adapter == "jingmai")
            return new InstallPlan(app, target, [], false, false)
            {
                ExecutablePath = executable, IsWinget = false, Adapter = profile.Adapter
            };
        var type = profile.InstallerType.ToLowerInvariant();
        var configuredArguments = silent ? profile.SilentArguments : profile.InteractiveArguments;
        var arguments = configuredArguments.Select(argument => Expand(argument, target)).ToList();
        if (silent && arguments.Count == 0)
        {
            if (type == "nsis") arguments.Add("/S");
            else if (type == "msi") arguments.Add("/qn");
        }
        if (type == "msi")
        {
            arguments.InsertRange(0, ["/i", PayloadPlaceholder]);
            if (!arguments.Contains("/norestart", StringComparer.OrdinalIgnoreCase)) arguments.Add("/norestart");
            executable = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        }
        if (profile.LocationSupported)
        {
            var locationArgument = type == "msi" && profile.InstallLocationArgument == "/D={InstallDirectory}"
                ? "INSTALLDIR={InstallDirectory}" : profile.InstallLocationArgument;
            arguments.Add(Expand(locationArgument, target));
        }
        return new InstallPlan(app, target, arguments.AsReadOnly(), !profile.LocationSupported, silent)
        {
            ExecutablePath = executable,
            IsWinget = false,
            RequiresExistingTargetDirectory = profile.RequiresExistingTargetDirectory && profile.LocationSupported,
            // NSIS consumes the entire command-line tail after /D=, without quotes.
            UnquotedFinalArgument = type == "nsis" && profile.LocationSupported
        };
    }

    private static string Expand(string argument, string target) => argument.Replace("{InstallDirectory}", target, StringComparison.Ordinal);
}

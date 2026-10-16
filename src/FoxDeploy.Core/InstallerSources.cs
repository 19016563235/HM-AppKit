namespace FoxDeploy.Core;

// Keep the original persisted numbers readable; every mode now uses automatic sources.
public enum InstallationSourceMode { Online = 0, Local = 1, Auto = 2 }

public record InstallerProfile
{
    public string InstallerType { get; init; } = "nsis";
    public bool SilentSupported { get; init; }
    public bool LocationSupported { get; init; }
    public bool RequiresExistingTargetDirectory { get; init; }
    public bool InteractiveFallback { get; init; }
    public string Adapter { get; init; } = "";
    public List<string> SilentArguments { get; init; } = [];
    public List<string> InteractiveArguments { get; init; } = [];
    public string InstallLocationArgument { get; init; } = "/D={InstallDirectory}";
    public List<string> AllowedPublishers { get; init; } = [];
}

public sealed record LocalInstallerDefinition : InstallerProfile
{
    public string FileName { get; init; } = "";
    public string Sha256 { get; init; } = "";
}

public sealed record OnlineInstallerDefinition : InstallerProfile
{
    public string WingetManifestPath { get; init; } = "";
    // DownloadUrl may be an official redirect-to-latest endpoint.
    public string DownloadUrl { get; init; } = "";
    public string MetadataUrl { get; init; } = "";
    // A bounded regex against official metadata, with a named group "url".
    public string UrlPattern { get; init; } = "";
    public List<string> AllowedDownloadHosts { get; init; } = [];
    public Dictionary<string, string> RequestHeaders { get; init; } = [];
}

public sealed record InstallerCapabilities(bool SilentSupported, bool LocationSupported, bool IsAvailable, string SourceLabel, string Message);

public static class InstallerSources
{
    public static string InstallerDirectory => Path.Combine(AppContext.BaseDirectory, "Local Installer");

    public static void NormalizeSettings(DeploymentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.SourceMode = InstallationSourceMode.Auto;
        settings.KeepInstallers = true;
        settings.LocalInstallerDirectory = InstallerDirectory;
    }

    public static bool UsesWinget(AppDefinition app, DeploymentSettings settings) =>
        app.OnlineInstaller is null && app.LocalInstaller is null && !string.IsNullOrWhiteSpace(app.PackageId);

    public static InstallerProfile? GetProfile(AppDefinition app, DeploymentSettings settings) =>
        (InstallerProfile?)app.OnlineInstaller ?? app.LocalInstaller;

    public static string ResolveLocalPath(AppDefinition app, DeploymentSettings settings, string? installerLibraryRoot = null)
    {
        var local = app.LocalInstaller ?? throw new NotSupportedException($"{app.Name} 没有配置可核验的本地安装包。");
        if (!InstallerCacheStore.SafeInstallerName(local.FileName)) throw new InvalidDataException("本地安装包必须为单个安全的 EXE / MSI 文件名。");
        return Path.Combine(Path.GetFullPath(installerLibraryRoot ?? InstallerDirectory), local.FileName);
    }

    public static InstallerCapabilities GetCapabilities(AppDefinition app, DeploymentSettings settings)
    {
        if (UsesWinget(app, settings)) return new(app.SilentSupported, app.LocationSupported, true, "WinGet", "此自定义应用由 WinGet 管理下载与安装；如需在 Local Installer 中保留原包，请配置官方安装来源。");
        var profile = GetProfile(app, settings);
        if (profile is null) return new(false, false, false, "自动", "尚未配置可核验的安装包来源。");
        if (app.OnlineInstaller is not null)
            return new(profile.SilentSupported, profile.LocationSupported, true, "自动", "优先检查程序旁 Local Installer 中的安装包；有新版时从官网更新，无更新时复用。");
        try
        {
            var path = ResolveLocalPath(app, settings);
            var available = File.Exists(path) || new InstallerCacheStore(InstallerDirectory).HasEntry(app.Id);
            return new(profile.SilentSupported, profile.LocationSupported, available, "自动", available
                ? $"仅配置本地包 {localFileName(app)}；核验后使用，无法检查官网更新。"
                : $"请将已核验的 {localFileName(app)} 放入程序旁 Local Installer；此应用未配置官网更新。");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(profile.SilentSupported, profile.LocationSupported, false, "自动", ex.Message);
        }
    }

    private static string localFileName(AppDefinition app) => app.LocalInstaller?.FileName ?? "";
}

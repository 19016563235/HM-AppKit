namespace FoxDeploy.Core;

public enum InstallationSourceMode { Online, Local }

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
    public static bool UsesWinget(AppDefinition app, DeploymentSettings settings) =>
        settings.SourceMode == InstallationSourceMode.Online && app.OnlineInstaller is null && !string.IsNullOrWhiteSpace(app.PackageId);

    public static InstallerProfile? GetProfile(AppDefinition app, DeploymentSettings settings) =>
        settings.SourceMode == InstallationSourceMode.Local ? app.LocalInstaller : app.OnlineInstaller;

    public static string ResolveLocalPath(AppDefinition app, DeploymentSettings settings)
    {
        var local = app.LocalInstaller ?? throw new NotSupportedException($"{app.Name} 没有配置本地安装包，请切换在线下载。");
        if (string.IsNullOrWhiteSpace(settings.LocalInstallerDirectory)) throw new InvalidOperationException("请选择本地安装包目录。");
        if (!Path.IsPathFullyQualified(settings.LocalInstallerDirectory)) throw new ArgumentException("安装包目录必须是完整路径。");
        return Path.Combine(settings.LocalInstallerDirectory, local.FileName);
    }

    public static InstallerCapabilities GetCapabilities(AppDefinition app, DeploymentSettings settings)
    {
        if (UsesWinget(app, settings)) return new(app.SilentSupported, app.LocationSupported, true, "在线", "安装时查询 WinGet 源中的最新可用版本。");
        var profile = GetProfile(app, settings);
        if (profile is null) return new(false, false, false, settings.SourceMode == InstallationSourceMode.Local ? "本地" : "在线", "此来源尚未配置，请切换来源。");
        if (settings.SourceMode == InstallationSourceMode.Online)
            return new(profile.SilentSupported, profile.LocationSupported, true, "在线", "安装时从厂商官方渠道获取当前版本，并核对数字签名。");
        try
        {
            var path = ResolveLocalPath(app, settings);
            return new(profile.SilentSupported, profile.LocationSupported, File.Exists(path), "本地", File.Exists(path) ? $"本地安装包：{localFileName(app)}" : $"未找到 {localFileName(app)}，请选择安装包目录。");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return new(profile.SilentSupported, profile.LocationSupported, false, "本地", ex.Message);
        }
    }

    private static string localFileName(AppDefinition app) => app.LocalInstaller?.FileName ?? "";
}

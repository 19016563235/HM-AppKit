namespace FoxDeploy.Core;

public sealed record AppDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "公共";
    public List<string> Roles { get; init; } = [];
    public string Description { get; init; } = "";
    public string PackageId { get; init; } = "";
    public string FolderName { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public string Notes { get; init; } = "";
    public bool SilentSupported { get; init; }
    public bool LocationSupported { get; init; }
    public List<string> RegistryNames { get; init; } = [];
    public LocalInstallerDefinition? LocalInstaller { get; init; }
    public OnlineInstallerDefinition? OnlineInstaller { get; init; }
}

public sealed class DeploymentSettings
{
    public List<string> DrivePriority { get; set; } = [.. DriveSelectionPolicy.DefaultDrivePriority];
    public string? SelectedDrive { get; set; }
    public string BaseFolder { get; set; } = "Software";
    public bool SkipInstalled { get; set; } = true;
    public bool Silent { get; set; } = true;
    public bool KeepInstallers { get; set; } = true;
    public bool RequireCustomLocation { get; set; } = true;
    public InstallationSourceMode SourceMode { get; set; } = InstallationSourceMode.Auto;
    // Retained for deserializing old settings only; automatic sources always use InstallerDirectory.
    public string LocalInstallerDirectory { get; set; } = InstallerSources.InstallerDirectory;
}

public sealed record InstallRequest(AppDefinition App, string? TargetDirectory = null);
public sealed record DriveSnapshot(string Name, string Root, bool IsReady, long AvailableBytes, DriveType Type)
{
    // Explicit snapshots supplied by callers/tests retain the existing five-argument constructor.
    // Live discovery always sets these fields from the native storage query.
    public bool HardwareVerified { get; init; } = true;
    public bool IsExternal { get; init; }
    public bool IsVirtual { get; init; }
    public string BusType { get; init; } = "Unknown";
}
public enum InstallState { Pending, Checking, Installing, Succeeded, AlreadyInstalled, Failed, Skipped, Cancelled, Unverified, LocationMismatch, ExistingUnverified }
public sealed record InstallUpdate(string AppId, InstallState State, string Message, string? TargetDirectory = null)
{
    public bool IsLogged { get; init; }
}

public sealed record InstallPlan(AppDefinition App, string? TargetDirectory, IReadOnlyList<string> Arguments, bool UsesDefaultLocation, bool IsSilent)
{
    public string ExecutablePath { get; init; } = "winget";
    public bool IsWinget { get; init; } = true;
    public bool UnquotedFinalArgument { get; init; }
    public bool RequiresExistingTargetDirectory { get; init; }
    public string Adapter { get; init; } = "";
    // Display only. A shell never executes this string.
    public string DisplayCommand => Quote(ExecutablePath) + " " + string.Join(" ", Arguments.Select(Quote));
    private static string Quote(string value) => value.Any(c => char.IsWhiteSpace(c) || c is '"' or '&' or '|')
        ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;
}

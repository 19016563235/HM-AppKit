using Microsoft.Win32;
using System.Security;
using System.Text.RegularExpressions;

namespace FoxDeploy.Core;

public sealed record InstalledApplication(string DisplayName, string? InstallLocation, string RegistryKey, string LocationEvidence = "InstallLocation");

public interface IInstallationProbe
{
    IReadOnlyList<InstalledApplication> FindInstalled(AppDefinition app);
}

public sealed class RegistryInstallationProbe : IInstallationProbe
{
    public IReadOnlyList<InstalledApplication> FindInstalled(AppDefinition app)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { app.Name.Trim() };
        foreach (var alias in app.RegistryNames ?? []) if (!string.IsNullOrWhiteSpace(alias)) names.Add(alias.Trim());
        // Only exact aliases or an alias followed by a numeric version identify this product.
        var results = new List<InstalledApplication>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var keyName in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var key = uninstall.OpenSubKey(keyName);
                        if (key is null) continue;
                        var displayName = key.GetValue("DisplayName") as string;
                        var wingetId = key.GetValue("WinGetPackageIdentifier") as string;
                        var packageMatches = !string.IsNullOrWhiteSpace(app.PackageId) && string.Equals(wingetId, app.PackageId, StringComparison.OrdinalIgnoreCase);
                        if (!packageMatches && !RegistryApplicationIdentity.MatchesDisplayName(displayName, names)) continue;
                        var location = key.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(location)) location = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'));
                        var evidence = "InstallLocation";
                        if (string.IsNullOrWhiteSpace(location))
                        {
                            location = RegistryLocationEvidence.ResolveFileParent(key.GetValue("DisplayIcon") as string, isDisplayIcon: true);
                            evidence = "DisplayIcon（已核对文件存在）";
                        }
                        if (string.IsNullOrWhiteSpace(location))
                        {
                            location = RegistryLocationEvidence.ResolveFileParent(key.GetValue("UninstallString") as string, isDisplayIcon: false);
                            evidence = "UninstallString（已核对文件存在）";
                        }
                        results.Add(new InstalledApplication(displayName ?? app.Name, location, $"{hive}/{view}/{keyName}", evidence));
                    }
                    catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return results.DistinctBy(r => (r.DisplayName, r.InstallLocation)).ToArray();
    }
}

public static partial class RegistryApplicationIdentity
{
    public static bool MatchesDisplayName(string? displayName, IEnumerable<string> aliases)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        var candidate = displayName.Trim();
        foreach (var value in aliases)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var alias = value.Trim();
            if (candidate.Equals(alias, StringComparison.OrdinalIgnoreCase)) return true;
            if (candidate.StartsWith(alias + " ", StringComparison.OrdinalIgnoreCase) &&
                NumericVersionSuffix().IsMatch(candidate[(alias.Length + 1)..])) return true;
        }
        return false;
    }

    [GeneratedRegex(@"\A[vV]?[0-9]+(?:\.[0-9]+){2,}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumericVersionSuffix();
}

public static partial class RegistryLocationEvidence
{
    public static string? ResolveFileParent(string? value, bool isDisplayIcon)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        string file;
        if (expanded.StartsWith('"'))
        {
            var endQuote = expanded.IndexOf('"', 1);
            if (endQuote < 0) return null;
            file = expanded[1..endQuote];
            if (isDisplayIcon && !IconSuffix().IsMatch(expanded[(endQuote + 1)..])) return null;
        }
        else if (isDisplayIcon) file = IconIndex().Replace(expanded, "").Trim();
        else
        {
            // Only identify an existing absolute executable; never execute or tokenize this command.
            var executableEnd = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (executableEnd < 0) return null;
            file = expanded[..(executableEnd + 4)];
        }
        try
        {
            file = DriveResolver.ValidateAbsolutePath(file);
            if (!File.Exists(file)) return null;
            var extension = Path.GetExtension(file);
            if (isDisplayIcon && extension.ToLowerInvariant() is not (".exe" or ".ico" or ".dll")) return null;
            if (!isDisplayIcon && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
            if (!string.IsNullOrWhiteSpace(windows) && file.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase)) return null;
            if (file.Contains("\\Package Cache\\", StringComparison.OrdinalIgnoreCase) || file.Contains("\\Installer Cache\\", StringComparison.OrdinalIgnoreCase)) return null;
            var parent = Path.GetDirectoryName(file);
            return parent is not null && parent.Length > 3 ? parent : null;
        }
        catch (ArgumentException) { return null; }
    }

    [GeneratedRegex(@"^\s*(,\s*-?\d+)?\s*$")]
    private static partial Regex IconSuffix();
    [GeneratedRegex(@",\s*-?\d+\s*$")]
    private static partial Regex IconIndex();
}

public static class InstallationLocationVerifier
{
    public static bool IsWithinTarget(string? installedLocation, string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(installedLocation)) return false;
        try
        {
            var actual = DriveResolver.ValidateAbsolutePath(installedLocation);
            var requested = DriveResolver.ValidateAbsolutePath(targetDirectory);
            return actual.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
                actual.StartsWith(requested + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }
}

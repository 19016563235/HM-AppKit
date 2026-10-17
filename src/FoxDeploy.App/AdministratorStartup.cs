using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace FoxDeploy.App;

internal enum AdministratorStartupResult { AlreadyElevated, Relaunched, Canceled }

internal static class AdministratorStartup
{
    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static ProcessStartInfo CreateStartInfo(string processPath, string assemblyPath,
        IEnumerable<string> arguments, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(processPath) || !Path.IsPathFullyQualified(processPath))
            throw new InvalidOperationException("无法确定 HM AppKit 的启动路径。");
        var start = new ProcessStartInfo(processPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = workingDirectory,
            WindowStyle = ProcessWindowStyle.Normal
        };
        // dotnet run / dotnet app.dll must restart the same application, not an empty host.
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(assemblyPath) || !Path.IsPathFullyQualified(assemblyPath) ||
                !string.Equals(Path.GetExtension(assemblyPath), ".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("无法确定开发进程的应用程序集路径。");
            start.ArgumentList.Add(assemblyPath);
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    internal static AdministratorStartupResult RequestIfNeeded(bool isAdministrator,
        Func<ProcessStartInfo> createStartInfo, Action<ProcessStartInfo> launch)
    {
        if (isAdministrator) return AdministratorStartupResult.AlreadyElevated;
        try
        {
            launch(createStartInfo());
            return AdministratorStartupResult.Relaunched;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return AdministratorStartupResult.Canceled;
        }
    }
}

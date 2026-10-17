using System.Diagnostics;
using System.Windows;

namespace FoxDeploy.App;

public partial class App : Application
{
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        // Keep an asInvoker bootstrap so older updaters can start a staged helper.
        // No settings, caches or application windows are opened before elevation.
        try
        {
            var elevation = AdministratorStartup.RequestIfNeeded(AdministratorStartup.IsAdministrator(),
                () => AdministratorStartup.CreateStartInfo(Environment.ProcessPath ?? "", Environment.GetCommandLineArgs().FirstOrDefault() ?? "",
                    e.Args, AppContext.BaseDirectory),
                start =>
                {
                    using var process = Process.Start(start)
                        ?? throw new InvalidOperationException("管理员进程未能启动。");
                });
            if (elevation != AdministratorStartupResult.AlreadyElevated)
            {
                Shutdown(elevation == AdministratorStartupResult.Canceled ? 1 : 0);
                return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"HM AppKit 需要管理员权限，启动未完成。\n\n{ex.Message}", "HM AppKit", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }
        if (e.Args.Contains("--apply-update", StringComparer.Ordinal))
        {
            if (e.Args.Length != 2 || e.Args[0] != "--apply-update")
            {
                MessageBox.Show("更新启动参数无效，请重新打开 HM AppKit。", "HM AppKit 更新", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(1);
                return;
            }
            await ApplySelfUpdateAsync(e.Args[1]);
            return;
        }
        var smokeIndex = Array.IndexOf(e.Args, "--smoke-test");
        var window = new MainWindow(smokeTest: smokeIndex >= 0);
        MainWindow = window;
        window.Show();
        if (smokeIndex >= 0 && e.Args.Length > smokeIndex + 1)
        {
            var roleIndex = Array.IndexOf(e.Args, "--smoke-role");
            var role = roleIndex >= 0 && e.Args.Length > roleIndex + 1 ? e.Args[roleIndex + 1] : null;
            window.RenderSmokeTest(e.Args[smokeIndex + 1], role);
        }
    }
}

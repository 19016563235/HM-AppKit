using System.Windows;
using FoxDeploy.Core;

namespace FoxDeploy.App;

public partial class App : Application
{
    private async void OnStartup(object sender, StartupEventArgs e)
    {
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
            var sourceIndex = Array.IndexOf(e.Args, "--smoke-source");
            var role = roleIndex >= 0 && e.Args.Length > roleIndex + 1 ? e.Args[roleIndex + 1] : null;
            InstallationSourceMode? source = sourceIndex >= 0 && e.Args.Length > sourceIndex + 1 &&
                Enum.TryParse<InstallationSourceMode>(e.Args[sourceIndex + 1], true, out var parsedSource) ? parsedSource : null;
            window.RenderSmokeTest(e.Args[smokeIndex + 1], role, source);
        }
    }
}

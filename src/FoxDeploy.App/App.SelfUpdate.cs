using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FoxDeploy.Core;

namespace FoxDeploy.App;

public partial class App
{
    private async Task ApplySelfUpdateAsync(string manifestPath)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var finished = false;
        var status = new TextBlock
        {
            Text = "正在等待 HM AppKit 退出…",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 14)
        };
        var content = new StackPanel { Margin = new Thickness(26) };
        content.Children.Add(new TextBlock { Text = "正在更新 HM AppKit", FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        content.Children.Add(status);
        content.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4 });
        content.Children.Add(new TextBlock { Text = "原有配置和安装包缓存将保留。请等待程序重新打开。", FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) });
        var progressWindow = new Window
        {
            Title = "HM AppKit 更新",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Content = content
        };
        progressWindow.Closing += (_, e) => { if (!finished) e.Cancel = true; };
        MainWindow = progressWindow;
        progressWindow.Show();
        var exitCode = 1;
        try
        {
            var result = await new UpdateApplyService().ApplyAsync(manifestPath,
                new Progress<string>(message => status.Text = message));
            if (!result.Success)
                MessageBox.Show(progressWindow, result.Message + (result.RollbackSucceeded ? "\n\n已恢复原版本。" : "\n\n请保留更新目录，重新打开原来的 HM AppKit 检查。"), "HM AppKit 更新未完成", MessageBoxButton.OK, MessageBoxImage.Information);
            if ((result.Success || result.RollbackSucceeded) && !result.Canceled && File.Exists(result.ExecutablePath))
            {
                using var restarted = Process.Start(new ProcessStartInfo(result.ExecutablePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(result.ExecutablePath)!,
                    WindowStyle = ProcessWindowStyle.Normal
                }) ?? throw new IOException("更新后程序未能自动打开，请通过原来的桌面快捷方式打开。");
            }
            exitCode = result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            MessageBox.Show(progressWindow, $"更新未能完成，请通过原来的桌面快捷方式重新打开 HM AppKit。\n\n{ex.Message}", "HM AppKit 更新", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            finished = true;
            progressWindow.Close();
            Shutdown(exitCode);
        }
    }
}

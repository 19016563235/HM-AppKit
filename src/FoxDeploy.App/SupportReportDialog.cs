using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FoxDeploy.Core;
using Microsoft.Win32;

namespace FoxDeploy.App;

internal sealed class SupportReportDialog : Window
{
    private readonly SupportReport _report;
    private readonly SupportUploadOptions? _options;
    private readonly Button _upload;
    private readonly Button _dismiss;
    private readonly Button _save;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Func<SupportUploadClient> _createClient;
    private CancellationTokenSource? _uploadCancellation;
    public bool UploadAttempted { get; private set; }
    public bool UploadConfirmed { get; private set; }

    public SupportReportDialog(SupportReport report, SupportUploadOptions? options, Func<SupportUploadClient>? createClient = null)
    {
        _report = report;
        _options = options;
        _createClient = createClient ?? (() => new SupportUploadClient(options!));
        Title = "安装结果 · HM AppKit";
        Width = Math.Min(760, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(650, SystemParameters.WorkArea.Height - 40);
        MinWidth = 450;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(246, 247, 250));
        var body = new Grid { Margin = new Thickness(26) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            body.RowDefinitions.Add(new RowDefinition { Height = height });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "有安装任务需要关注", FontSize = 22, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock
        {
            Text = "是否上传本轮诊断报告，便于 IT 查看安装错误？",
            FontSize = 13, Margin = new Thickness(0, 9, 0, 12), TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(heading);
        var destination = new TextBlock
        {
            Text = options is null ? "接收位置尚未配置，可先保存报告。"
                : options.Mode == SupportUploadMode.FeishuWebhook
                    ? $"接收方：{SupportReportSanitizer.Sanitize(options.DisplayName)} · 飞书群机器人\n本轮报告将分 {SupportUploadClient.GetFeishuMessageCount(report)} 条消息发送，完整副本保留在本机。"
                    : $"接收方：{SupportReportSanitizer.Sanitize(options.DisplayName)}\n{options.Endpoint.AbsoluteUri}",
            FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(112, 126, 147)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14)
        };
        Grid.SetRow(destination, 1);
        body.Children.Add(destination);
        var preview = new Grid();
        preview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        preview.RowDefinitions.Add(new RowDefinition());
        preview.Children.Add(new TextBlock { Text = "报告预览 · 用户目录、下载链接及常见凭证已隐藏", FontSize = 11, Margin = new Thickness(0, 0, 0, 7) });
        var text = new TextBox
        {
            Text = options?.Mode == SupportUploadMode.FeishuWebhook ? SupportUploadClient.PreviewText(report) : report.Json,
            IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 11,
            Padding = new Thickness(12), Background = Brushes.White
        };
        Grid.SetRow(text, 1);
        preview.Children.Add(text);
        Grid.SetRow(preview, 2);
        body.Children.Add(preview);
        var statusPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _progress = new ProgressBar { Height = 3, IsIndeterminate = true, Visibility = Visibility.Collapsed };
        statusPanel.Children.Add(_progress);
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        statusPanel.Children.Add(_status);
        Grid.SetRow(statusPanel, 3);
        body.Children.Add(statusPanel);
        var actions = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 15, 0, 0) };
        _save = new Button { Content = "保存报告…", MinWidth = 104, Padding = new Thickness(14, 9, 14, 9) };
        _save.Click += SaveReport;
        DockPanel.SetDock(_save, Dock.Left);
        actions.Children.Add(_save);
        _upload = new Button
        {
            Content = "上传日志", MinWidth = 104, IsEnabled = options is not null,
            Padding = new Thickness(14, 9, 14, 9), Style = (Style)FindResource("PrimaryButton")
        };
        _upload.Click += UploadReport;
        DockPanel.SetDock(_upload, Dock.Right);
        actions.Children.Add(_upload);
        _dismiss = new Button { Content = "暂不上传", MinWidth = 94, Margin = new Thickness(0, 0, 9, 0), Padding = new Thickness(14, 9, 14, 9) };
        _dismiss.Click += (_, _) =>
        {
            if (_uploadCancellation is not null) { _uploadCancellation.Cancel(); return; }
            Close();
        };
        DockPanel.SetDock(_dismiss, Dock.Right);
        actions.Children.Add(_dismiss);
        Grid.SetRow(actions, 4);
        body.Children.Add(actions);
        Content = body;
        Closing += OnClosing;
    }

    private void SaveReport(object sender, RoutedEventArgs args)
    {
        var picker = new SaveFileDialog { Title = "保存诊断报告", Filter = "诊断报告 (*.json)|*.json", FileName = $"HM-AppKit-report-{_report.ReportId}.json", AddExtension = true, DefaultExt = ".json" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(picker.FileName, _report.Json, new UTF8Encoding(false));
            _status.Text = "诊断报告已保存。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _status.Text = "报告未能保存，请选择其他位置。"; }
    }

    private async void UploadReport(object sender, RoutedEventArgs args)
    {
        if (_options is null || UploadAttempted || _uploadCancellation is not null) return;
        UploadAttempted = true;
        _upload.IsEnabled = false;
        _save.IsEnabled = false;
        _dismiss.Content = "取消上传";
        _status.Text = "正在上传诊断报告…";
        _progress.Visibility = Visibility.Visible;
        _uploadCancellation = new CancellationTokenSource();
        try
        {
            using var client = _createClient();
            var result = await client.UploadAsync(_report, _uploadCancellation.Token);
            UploadConfirmed = result.Status == SupportUploadStatus.Confirmed;
            _status.Text = result.Message + $"\n报告编号：{_report.ReportId}";
            _upload.Content = UploadConfirmed ? "已上传" : "未确认";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ArgumentException or InvalidOperationException)
        {
            _status.Text = "上传未确认。请保存本地报告，并按报告编号联系 IT 核对。";
        }
        finally
        {
            _uploadCancellation.Dispose();
            _uploadCancellation = null;
            _progress.Visibility = Visibility.Collapsed;
            _save.IsEnabled = true;
            _dismiss.Content = "关闭";
        }
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_uploadCancellation is null) return;
        args.Cancel = true;
        _uploadCancellation.Cancel();
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FoxDeploy.Core;

namespace FoxDeploy.App;

public sealed class CustomAppDialog : Window
{
    private readonly TextBox _name = new();
    private readonly TextBox _package = new();
    private readonly TextBox _folder = new();
    private readonly ComboBox _category = new() { ItemsSource = new[] { "自定义", "公共", "运营", "客服", "短视频" }, SelectedIndex = 0, Padding = new Thickness(8), Height = 35 };
    private readonly CheckBox _silent = new() { Content = "已确认此安装包支持静默安装", Margin = new Thickness(0, 0, 0, 10) };
    private readonly CheckBox _location = new() { Content = "已确认此安装包支持 WinGet 自定义安装目录" };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    public AppDefinition? Definition { get; private set; }

    public CustomAppDialog()
    {
        Title = "添加应用 · HM AppKit";
        Width = 540;
        Height = 690;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Margin = new Thickness(28, 25, 28, 22) };
        body.Children.Add(new TextBlock { Text = "把岗位应用加入清单", FontSize = 23, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = "使用精确的 WinGet 包 ID；添加后可单独指定安装目录。", Foreground = new SolidColorBrush(Color.FromRgb(113, 128, 150)), FontSize = 12, Margin = new Thickness(0, 9, 0, 21) });
        AddField(body, "应用名称", _name);
        AddField(body, "WinGet 包 ID（例如 VideoLAN.VLC）", _package);
        AddField(body, "默认文件夹名（例如 VLC）", _folder);
        AddField(body, "岗位分类", _category);
        body.Children.Add(new TextBlock { Text = "安装能力确认", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 12) });
        body.Children.Add(_silent);
        body.Children.Add(_location);
        body.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(255, 247, 233)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 16, 0, 0),
            Child = new TextBlock { Text = "请依据安装器或 WinGet 清单确认能力。未确认目录支持的应用会被阻止安装。错误声明可能使安装器忽略目标位置，安装结果将再核验。", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(159, 117, 53)), TextWrapping = TextWrapping.Wrap }
        });
        body.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 19, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 9, 0) };
        var add = new Button { Content = "添加应用", IsDefault = true, MinWidth = 108, Style = (Style)FindResource("PrimaryButton") };
        add.Click += Add_Click;
        buttons.Children.Add(cancel);
        buttons.Children.Add(add);
        body.Children.Add(buttons);
        Content = body;
    }

    private static void AddField(Panel body, string label, Control input)
    {
        body.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(91, 107, 129)), Margin = new Thickness(0, 0, 0, 7) });
        input.Margin = new Thickness(0, 0, 0, 14);
        body.Children.Add(input);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var name = _name.Text.Trim();
        var packageId = _package.Text.Trim();
        var folder = _folder.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(folder))
        {
            _error.Text = "请填写应用名称、WinGet 包 ID 与默认文件夹名。";
            return;
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(packageId, "^[A-Za-z0-9][A-Za-z0-9._+-]*$"))
        {
            _error.Text = "包 ID 只允许字母、数字、点、下划线、加号与短横线。";
            return;
        }
        if (folder.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || folder is "." or ".." || folder.EndsWith('.') || folder.EndsWith(' '))
        {
            _error.Text = "文件夹名必须是有效的单层文件夹名称。";
            return;
        }
        Definition = new AppDefinition
        {
            Id = "custom-" + Guid.NewGuid().ToString("N"),
            Name = name,
            PackageId = packageId,
            FolderName = folder,
            Category = _category.SelectedItem as string ?? "自定义",
            Description = "自定义 WinGet 应用",
            SourceUrl = "https://github.com/microsoft/winget-pkgs",
            Notes = "安装能力由用户声明，请以实际安装器及 WinGet 清单为准。",
            SilentSupported = _silent.IsChecked == true,
            LocationSupported = _location.IsChecked == true
        };
        try { AppCatalog.Validate(Definition); }
        catch (Exception ex) when (ex is System.IO.InvalidDataException or ArgumentException)
        {
            Definition = null;
            _error.Text = ex.Message;
            return;
        }
        DialogResult = true;
    }
}

using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FoxDeploy.App;

internal static class AppLogoAssets
{
    private static readonly IReadOnlyDictionary<string, ImageSource> Logos = new[]
    {
        "feishu", "weixin", "todesk", "qianniu", "doudian", "jingmai",
        "pinduoduo", "qianfan", "kuaiyu", "dewu", "jianying"
    }.ToDictionary(id => id, Load, StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string appId) => Logos.GetValueOrDefault(appId);

    private static ImageSource Load(string appId)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/{typeof(AppLogoAssets).Assembly.GetName().Name};component/Assets/AppIcons/{appId}.png");
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

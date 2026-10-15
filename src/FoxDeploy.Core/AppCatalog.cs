using System.Text.Json;
using System.Text.RegularExpressions;

namespace FoxDeploy.Core;

public static partial class AppCatalog
{
    public static IReadOnlyList<AppDefinition> Load(string path) => Parse(File.ReadAllText(path));

    public static IReadOnlyList<AppDefinition> Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            var apps = root.EnumerateObject().FirstOrDefault(p => p.Name.Equals("apps", StringComparison.OrdinalIgnoreCase));
            if (apps.Value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("应用目录需要 apps 数组。");
            root = apps.Value;
        }
        if (root.ValueKind != JsonValueKind.Array) throw new InvalidDataException("应用目录需要 JSON 数组。");
        var result = root.Deserialize<List<AppDefinition>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("应用目录为空。");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in result)
        {
            Validate(app);
            if (!ids.Add(app.Id)) throw new InvalidDataException($"重复的应用 ID：{app.Id}");
        }
        return result.AsReadOnly();
    }

    public static void Validate(AppDefinition app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (string.IsNullOrWhiteSpace(app.Id) || !Identifier().IsMatch(app.Id)) throw new InvalidDataException("应用 ID 只能包含字母、数字、点、下划线和连字符。");
        if (string.IsNullOrWhiteSpace(app.Name)) throw new InvalidDataException($"{app.Id} 缺少应用名称。");
        if (!string.IsNullOrWhiteSpace(app.PackageId) && !PackageIdentifier().IsMatch(app.PackageId)) throw new InvalidDataException($"{app.Name} 的 WinGet 包 ID 无效。");
        if (string.IsNullOrWhiteSpace(app.PackageId) && app.LocalInstaller is null && app.OnlineInstaller is null)
            throw new InvalidDataException($"{app.Name} 缺少 WinGet 包 ID 或安装包来源。");
        DriveResolver.ValidateRelativeFolder(app.FolderName, allowNested: false);
        if (!string.IsNullOrWhiteSpace(app.SourceUrl) && (!Uri.TryCreate(app.SourceUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps))
            throw new InvalidDataException($"{app.Name} 的来源链接必须使用 HTTPS。");
        if (app.LocalInstaller is { } local)
        {
            ValidateProfile(app.Name, local);
            if (string.IsNullOrWhiteSpace(local.FileName) || local.FileName != Path.GetFileName(local.FileName) ||
                local.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || local.FileName.EndsWith('.') ||
                local.FileName.EndsWith(' ') || !ExpectedExtension(local.FileName, local.InstallerType))
                throw new InvalidDataException($"{app.Name} 的本地安装包必须是单个 EXE / MSI 文件名，不能包含路径。");
            if (local.Sha256 is null || local.Sha256.Length != 64 || !Sha256Value().IsMatch(local.Sha256))
                throw new InvalidDataException($"{app.Name} 的本地安装包需要 64 位 SHA-256 校验值。");
        }
        if (app.OnlineInstaller is { } online)
        {
            ValidateProfile(app.Name, online);
            if (online.AllowedPublishers is null || online.AllowedPublishers.Count == 0)
                throw new InvalidDataException($"{app.Name} 的在线安装包需要可信数字签名发布者。");
            if (online.AllowedDownloadHosts is null || online.AllowedDownloadHosts.Count == 0 || online.AllowedDownloadHosts.Any(host =>
                string.IsNullOrWhiteSpace(host) || host != host.Trim() || Uri.CheckHostName(host) != UriHostNameType.Dns))
                throw new InvalidDataException($"{app.Name} 的在线安装包需要明确的官方下载域名。");
            if (!string.IsNullOrWhiteSpace(online.WingetManifestPath)) WingetManifestResolver.ValidateManifestPath(online.WingetManifestPath);
            if (string.IsNullOrWhiteSpace(online.DownloadUrl) && string.IsNullOrWhiteSpace(online.MetadataUrl) && string.IsNullOrWhiteSpace(online.WingetManifestPath))
                throw new InvalidDataException($"{app.Name} 缺少官方下载地址或元数据地址。");
            foreach (var address in new[] { online.DownloadUrl, online.MetadataUrl }.Where(address => !string.IsNullOrWhiteSpace(address)))
                if (!Uri.TryCreate(address, UriKind.Absolute, out var addressUri) || addressUri.Scheme != Uri.UriSchemeHttps ||
                    !addressUri.IsDefaultPort || !string.IsNullOrEmpty(addressUri.UserInfo) || !online.AllowedDownloadHosts.Contains(addressUri.Host, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{app.Name} 的在线地址必须使用已允许域名的 HTTPS 链接。");
            if (online.RequestHeaders is null || online.RequestHeaders.Any(header => string.IsNullOrWhiteSpace(header.Key) ||
                header.Key.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') || header.Value is null || header.Value.Any(char.IsControl)))
                throw new InvalidDataException($"{app.Name} 的元数据请求头无效。");
            if (!string.IsNullOrWhiteSpace(online.MetadataUrl))
            {
                try
                {
                    var pattern = new Regex(online.UrlPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    if (!pattern.GetGroupNames().Contains("url", StringComparer.Ordinal))
                        throw new InvalidDataException($"{app.Name} 的下载匹配规则需要 url 命名组。");
                }
                catch (ArgumentException ex) { throw new InvalidDataException($"{app.Name} 的下载匹配规则无效。", ex); }
            }
        }
    }

    private static void ValidateProfile(string appName, InstallerProfile profile)
    {
        if (profile.Adapter is not ("" or "jingmai") ||
            (profile.InteractiveFallback && profile.Adapter != "jingmai") ||
            (profile.Adapter == "jingmai" && (!profile.InteractiveFallback || profile.SilentSupported || !profile.LocationSupported)))
            throw new InvalidDataException($"{appName} 的安装适配器配置无效。");
        if (profile.InstallerType is null || !new[] { "nsis", "exe", "msi" }.Contains(profile.InstallerType, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"{appName} 的安装器类型必须为 nsis、exe 或 msi。");
        if (profile.SilentArguments is null || profile.InteractiveArguments is null ||
            profile.SilentArguments.Concat(profile.InteractiveArguments).Any(argument => argument is null || argument.Any(char.IsControl)))
            throw new InvalidDataException($"{appName} 的安装参数包含无效值。");
        if (profile.AllowedPublishers is null || profile.AllowedPublishers.Any(publisher => string.IsNullOrWhiteSpace(publisher) || publisher.Any(char.IsControl)))
            throw new InvalidDataException($"{appName} 的签名发布者名称无效。");
        if (profile.SilentSupported && profile.InstallerType.Equals("exe", StringComparison.OrdinalIgnoreCase) && profile.SilentArguments.Count == 0)
            throw new InvalidDataException($"{appName} 的 EXE 静默安装需要明确的静默参数。");
        if (profile.LocationSupported && (string.IsNullOrWhiteSpace(profile.InstallLocationArgument) ||
            !profile.InstallLocationArgument.Contains("{InstallDirectory}", StringComparison.Ordinal) || profile.InstallLocationArgument.Any(char.IsControl)))
            throw new InvalidDataException($"{appName} 的安装目录参数需要 InstallDirectory 占位符。");
        if (profile.InstallerType.Equals("nsis", StringComparison.OrdinalIgnoreCase) && profile.LocationSupported && profile.InstallLocationArgument != "/D={InstallDirectory}")
            throw new InvalidDataException($"{appName} 的 NSIS 安装目录参数必须为 /D={{InstallDirectory}}。");
        if (!profile.LocationSupported && profile.SilentArguments.Concat(profile.InteractiveArguments).Any(argument => argument.Contains("{InstallDirectory}", StringComparison.Ordinal)))
            throw new InvalidDataException($"{appName} 未支持自定义目录，不能在安装参数中传入目录。");
        if (profile.InstallerType.Equals("nsis", StringComparison.OrdinalIgnoreCase) &&
            profile.SilentArguments.Concat(profile.InteractiveArguments).Any(argument => argument.StartsWith("/D=", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"{appName} 的 NSIS 目录参数由安装计划自动放在最后。");
    }

    private static bool ExpectedExtension(string fileName, string installerType) => Path.GetExtension(fileName).Equals(
        installerType.Equals("msi", StringComparison.OrdinalIgnoreCase) ? ".msi" : ".exe", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex Identifier();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]*$")]
    private static partial Regex PackageIdentifier();
    [GeneratedRegex("^[A-Fa-f0-9]{64}$")]
    private static partial Regex Sha256Value();
}

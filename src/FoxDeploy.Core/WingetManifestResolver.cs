using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FoxDeploy.Core;

public sealed record WingetResolvedInstaller(Uri DownloadUri, string Sha256, string Version);

// Resolve data from the Microsoft community catalog, then download the vendor's
// original package. Commands remain explicitly reviewed in our own catalog.
public static partial class WingetManifestResolver
{
    private static readonly HttpClient Client = CreateClient();

    public static Task<WingetResolvedInstaller> ResolveAsync(OnlineInstallerDefinition profile, CancellationToken cancellationToken = default)
        => ResolveAsync(profile, Client, cancellationToken);

    // One attempt only. InstallerPayloadProvider owns the bounded retry of the entire listing/raw operation.
    // Injection is for small fake HTTP tests; production still uses the fixed no-redirect client above.
    public static async Task<WingetResolvedInstaller> ResolveAsync(OnlineInstallerDefinition profile, HttpClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(client);
        ValidateManifestPath(profile.WingetManifestPath);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var path = profile.WingetManifestPath;
        var listing = await ReadBoundedAsync(client, new Uri("https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/" + path), "WinGet 版本列表查询", timeout.Token);
        var version = FindLatestVersion(listing);
        var packageId = string.Join('.', path.Split('/').Skip(1));
        var manifest = new Uri($"https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/{path}/{version}/{packageId}.installer.yaml");
        var yaml = await ReadBoundedAsync(client, manifest, "WinGet 安装清单查询", timeout.Token);
        return ParseManifest(yaml, packageId, version, profile, RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64");
    }

    public static void ValidateManifestPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !ManifestPath().IsMatch(path) || path.Split('/').Any(segment => segment is "." or ".."))
            throw new InvalidDataException("WinGet 清单路径无效。");
    }

    public static string FindLatestVersion(string listing)
    {
        using var document = JsonDocument.Parse(listing);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("WinGet 版本列表格式已变化。");
        var versions = new List<(Version Numeric, string Text)>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("type", out var type) || type.GetString() != "dir" || !entry.TryGetProperty("name", out var name)) continue;
            var text = name.GetString();
            if (text is not null && NumericVersion().IsMatch(text) && Version.TryParse(text, out var numeric)) versions.Add((numeric, text));
        }
        return versions.OrderByDescending(value => value.Numeric).FirstOrDefault().Text
            ?? throw new InvalidDataException("WinGet 源中没有可确认的稳定版本。");
    }

    public static WingetResolvedInstaller ParseManifest(string yaml, string packageId, string expectedVersion, OnlineInstallerDefinition profile, string architecture = "x64")
    {
        if (yaml.Length > 2 * 1024 * 1024 || Scalar(yaml, "PackageIdentifier", false) != packageId || Scalar(yaml, "PackageVersion", false) != expectedVersion)
            throw new InvalidDataException("WinGet 清单包标识或版本不符合要求。");
        var boundary = Regex.Match(yaml, @"(?m)^Installers:\s*\r?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!boundary.Success) throw new InvalidDataException("WinGet 安装器清单结构已变化。");
        var header = yaml[..boundary.Index];
        var entries = InstallerEntry().Matches(yaml[(boundary.Index + boundary.Length)..]);
        var choices = entries.Cast<Match>().Where(match => match.Groups["arch"].Value == architecture).ToArray();
        if (choices.Length == 0 && architecture == "arm64") choices = entries.Cast<Match>().Where(match => match.Groups["arch"].Value == "x64").ToArray();
        if (choices.Length == 0) choices = entries.Cast<Match>().Where(match => match.Groups["arch"].Value == "x86").ToArray();
        if (choices.Length != 1) throw new InvalidDataException("WinGet 清单中未找到唯一的适用安装器，已停止以避免安装错误版本。");
        var block = choices[0].Value;
        var type = Scalar(block, "InstallerType", true) ?? Scalar(header, "InstallerType", false);
        var expectedType = profile.InstallerType.Equals("nsis", StringComparison.OrdinalIgnoreCase) ? "nullsoft" : profile.InstallerType.ToLowerInvariant();
        if (type != expectedType) throw new InvalidDataException("WinGet 安装器类型已变化，请先核验新的安装参数。");
        var url = Scalar(block, "InstallerUrl", true) ?? throw new InvalidDataException("WinGet 清单缺少安装包链接。");
        var hash = Scalar(block, "InstallerSha256", true) ?? "";
        if (!Sha256().IsMatch(hash)) throw new InvalidDataException("WinGet 清单缺少有效的 SHA-256。");
        var uri = InstallerPayloadProvider.ValidateAllowedUri(url, profile.AllowedDownloadHosts);
        if (!uri.AbsolutePath.EndsWith(profile.InstallerType == "msi" ? ".msi" : ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("WinGet 安装包扩展名不符合已核验类型。");
        return new WingetResolvedInstaller(uri, hash.ToUpperInvariant(), expectedVersion);
    }

    private static string? Scalar(string text, string key, bool nested)
    {
        var match = Regex.Match(text, @"(?m)^" + (nested ? " +" : "") + Regex.Escape(key) + @":\s*(?<value>[^\r\n]+)\s*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success) return null;
        var value = match.Groups["value"].Value.Trim();
        // Only scalar strings are expected for these fixed fields. Do not evaluate YAML tags/aliases.
        if (value.StartsWith('!') || value.StartsWith('&') || value.StartsWith('*') || value.StartsWith('|') || value.StartsWith('>'))
            throw new InvalidDataException("WinGet 清单字段格式已变化。");
        return value.Trim('"', '\'');
    }

    private static async Task<string> ReadBoundedAsync(HttpClient client, Uri uri, string stage, CancellationToken token)
    {
        HttpResponseMessage fetched;
        try { fetched = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            InstallerNetwork.WithContext(ex, stage, uri);
            throw;
        }
        using (var response = fetched)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
                throw InstallerNetwork.RequestFailure(new HttpRequestException(
                    response.StatusCode == HttpStatusCode.Forbidden ? "WinGet 版本服务拒绝访问（HTTP 403）。" : "WinGet 版本服务暂时限流（HTTP 429）。",
                    null, response.StatusCode), stage, uri);
            try { response.EnsureSuccessStatusCode(); }
            catch (HttpRequestException ex) { InstallerNetwork.WithContext(ex, stage, uri); throw; }
            if (response.Content.Headers.ContentLength > 2097152) throw new InvalidDataException("WinGet 元数据过大。");
            Stream input;
            try { input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (token.IsCancellationRequested)
            { InstallerNetwork.WithContext(ex, stage, uri); throw; }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
            { throw InstallerNetwork.ReadFailure(ex, stage, uri); }
            await using (input)
            {
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    int size;
                    try { size = await input.ReadAsync(buffer, token).ConfigureAwait(false); }
                    catch (OperationCanceledException ex) when (token.IsCancellationRequested)
                    { InstallerNetwork.WithContext(ex, stage, uri); throw; }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
                    { throw InstallerNetwork.ReadFailure(ex, stage, uri); }
                    if (size == 0) break;
                    if (output.Length + size > 2097152) throw new InvalidDataException("WinGet 元数据过大。");
                    output.Write(buffer, 0, size);
                }
                if (response.Content.Headers.ContentLength is { } length && output.Length != length)
                    throw InstallerNetwork.ReadFailure(new EndOfStreamException("WinGet 响应未完整接收。"), stage, uri);
                return System.Text.Encoding.UTF8.GetString(output.ToArray());
            }
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HM-AppKit/0.5");
        return client;
    }

    [GeneratedRegex(@"\A[a-z]/[A-Za-z0-9_-]+(?:/[A-Za-z0-9_-]+)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ManifestPath();
    [GeneratedRegex(@"\A[0-9]+(?:\.[0-9]+){1,3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumericVersion();
    [GeneratedRegex(@"(?ms)^- Architecture: (?<arch>x86|x64|arm64)\s*\r?$.*?(?=^- Architecture:|^ManifestType:|\z)", RegexOptions.CultureInvariant)]
    private static partial Regex InstallerEntry();
    [GeneratedRegex(@"\A[A-Fa-f0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();
}

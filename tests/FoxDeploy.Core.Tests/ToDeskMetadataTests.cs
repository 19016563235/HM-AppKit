using System.Text.Json;
using FoxDeploy.Core;

internal static class ToDeskMetadataTests
{
    private const string Stable = "https://dl.todesk.com/irrigation/ToDesk_5.2.2.0.exe";
    private const string Daas = "https://dl.todesk.com/daas/ToDesk_DaaS_v1.1.1.1.exe";
    private const string Gray = "https://dl.todesk.com/irrigation/ToDesk_5.2.3.0.exe";

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        var app = AppCatalog.Load(FindCatalog()).Single(app => app.Id == "todesk");
        var profile = app.OnlineInstaller!;
        Task Case(string name, Action assertion) => test(name, () => { assertion(); return Task.CompletedTask; });
        string Parse(string html) => InstallerPayloadProvider.ParseDownloadUri(profile, html).AbsoluteUri;

        await Case("ToDesk catalog uses a dedicated stable Windows parser and the original exact publisher", () =>
        {
            Check(profile.MetadataFormat == "todesk-windows" && profile.UrlPattern == "", "Unsafe generic substring rule survived.");
            Check(profile.AllowedPublishers.SequenceEqual(["Hainan YouQu Technology Co., Ltd"]), "Do not trust the different DaaS product's publisher.");
            AppCatalog.Validate(app);
        });
        await Case("ToDesk current Nuxt argument mapping resolves personal stable instead of literal DaaS", () =>
        {
            var html = "<script>window.__NUXT__=(function(a,b,c,d,e,f,g,h,i,j){return {data:[{clientInfo:{daas_win_link:" + Quote(Daas) +
                ",lite_link_x86:i,win_link_gray:" + Quote(Gray) + ",win_version:b,win_link:j}}],state:{flag:a,empty:c,count:50}}}(false,\"5.2.2.0\",\"commas, and quotes \\\" stay literals\",\"-1\",\"2026.10.16\",null,true,1,\"https://dl.todesk.com/windows/ToDesk_Lite.exe\"," +
                Quote(Stable).Replace("/", "\\u002F", StringComparison.Ordinal) + "));</script>";
            Check(Parse(html) == Stable, "Resolved another product or gray version instead of the exact stable property.");
        });
        await Case("ToDesk previous literal stable metadata and Unicode slashes remain supported", () =>
            Check(Parse(Page("win_link:" + Quote(Stable).Replace("/", "\\u002F", StringComparison.Ordinal) + ",win_version:\"5.2.2.0\"")) == Stable, "Literal metadata failed."));
        await Case("ToDesk gray release can be newer and earlier without changing stable selection", () =>
            Check(Parse(Page("win_link_gray:" + Quote(Gray) + ",win_version_gray:\"5.2.3.0\",win_link:" + Quote(Stable) + ",win_version:\"5.2.2.0\"")) == Stable, "Gray link replaced stable."));
        await Case("ToDesk only DaaS Lite gray or downloader keys cannot stand in for stable win_link", () =>
        {
            foreach (var key in new[] { "daas_win_link", "win_link_gray", "lite_win_link", "win_downloader_link" })
                Throws(() => Parse(Page(key + ":" + Quote(Daas) + ",win_version:\"5.2.2.0\"")));
        });
        await Case("ToDesk stable property rejects other products and a mismatched version even on its official host", () =>
        {
            foreach (var address in new[] { Daas, Gray, "https://dl.todesk.com/windows/ToDesk_Lite.exe", "https://dl.todesk.com/official/ToDesk_Installer.exe", "https://dl.todesk.com/irrigation/ToDesk_5.2.2.0_x64.exe" })
                Throws(() => Parse(Page("win_link:" + Quote(address) + ",win_version:\"5.2.2.0\"")));
        });
        await Case("ToDesk dedicated parser retains strict origin and exact file validation", () =>
        {
            foreach (var address in new[] { Stable.Replace("https:", "http:", StringComparison.Ordinal), Stable.Replace("dl.todesk.com", "dl.todesk.com.evil.test", StringComparison.Ordinal),
                Stable.Replace("dl.todesk.com", "dl.todesk.com:8443", StringComparison.Ordinal), Stable.Replace("https://", "https://user:secret@", StringComparison.Ordinal), Stable + "?variant=daas", Stable + "#other" })
                Throws(() => Parse(Page("win_link:" + Quote(address) + ",win_version:\"5.2.2.0\"")));
        });
        await Case("ToDesk unknown variables duplicate parameters and missing arguments fail closed", () =>
        {
            foreach (var payload in new[]
            {
                "(function(a){return {data:[{clientInfo:{win_link:j,win_version:\"5.2.2.0\"}}]}}(" + Quote(Stable) + "));",
                "(function(a,a){return {data:[{clientInfo:{win_link:a,win_version:\"5.2.2.0\"}}]}}(" + Quote(Stable) + "," + Quote(Stable) + "));",
                "(function(a,b){return {data:[{clientInfo:{win_link:a,win_version:b}}]}}(" + Quote(Stable) + "));"
            }) Throws(() => Parse("<script>window.__NUXT__=" + payload + "</script>"));
        });
        await Case("ToDesk JavaScript calls and expressions are rejected instead of executed", () =>
        {
            foreach (var value in new[] { "alert(1)", "j.href", "j+\".exe\"", "(function(){return \"bad\"})()" })
                Throws(() => Parse(Page("win_link:" + value + ",win_version:\"5.2.2.0\"")));
            Throws(() => Parse("<script>window.__NUXT__=(function(a){return {data:[]}}(alert(1)));</script>"));
        });
        await Case("ToDesk duplicate stable keys and ambiguous client scripts are rejected", () =>
        {
            var client = "win_link:" + Quote(Stable) + ",win_version:\"5.2.2.0\"";
            Throws(() => Parse(Page(client + ",win_link:" + Quote(Daas))));
            Throws(() => Parse(Page(client) + Page(client)));
            Throws(() => Parse("<script>window.__NUXT__={data:[{clientInfo:{" + client + "}},{clientInfo:{" + client + "}}]};</script>"));
        });
        await Case("ToDesk missing malformed or non-string stable versions cannot pick an arbitrary installer", () =>
        {
            foreach (var version in new[] { "null", "1", "\"\"", "\"5.2.2\"", "\"5.2.2.0-beta\"" })
                Throws(() => Parse(Page("win_link:" + Quote(Stable) + ",win_version:" + version)));
            Throws(() => Parse(Page("win_link:" + Quote(Stable))));
        });
        await Case("ToDesk data parsing bounds metadata size nesting and node count", () =>
        {
            Throws(() => Parse(new string('x', 2097153)));
            Throws(() => Parse("<script>window.__NUXT__={data:" + new string('[', 55) + "null" + new string(']', 55) + "};</script>"));
            Throws(() => Parse("<script>window.__NUXT__={data:[" + string.Join(',', Enumerable.Repeat("null", 17000)) + "]};</script>"));
        });
        await Case("Dedicated metadata configuration rejects unknown formats and alternative origins or bypass routes", () =>
        {
            foreach (var invalid in new[]
            {
                profile with { MetadataFormat = "arbitrary-script" }, profile with { MetadataUrl = "https://dl.todesk.com/other" },
                profile with { DownloadUrl = Stable }, profile with { WingetManifestPath = "y/Youqu/ToDesk" }
            }) Throws(() => AppCatalog.Validate(app with { OnlineInstaller = invalid }));
            Throws(() => InstallerPayloadProvider.ParseDownloadUri(profile with { MetadataFormat = "arbitrary-script" }, ""));
        });
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);
    private static string Page(string client) => "<script>window.__NUXT__={data:[{clientInfo:{" + client + "}}]};</script>";
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected InvalidDataException.");
    }
    private static string FindCatalog()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "catalog", "catalog.json");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Repository catalog not found for ToDesk regression validation.");
    }
}

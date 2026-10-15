using FoxDeploy.Core;

internal static class ManifestResolverTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        var profile = new OnlineInstallerDefinition { InstallerType = "exe", AllowedDownloadHosts = ["vendor.example"] };
        var hash = new string('A', 64);
        var yaml = $"PackageIdentifier: Vendor.App\nPackageVersion: 2.10.0\nInstallerType: exe\nInstallers:\n- Architecture: x86\n  InstallerUrl: https://vendor.example/x86.exe\n  InstallerSha256: {hash}\n- Architecture: x64\n  InstallerUrl: https://vendor.example/x64.exe\n  InstallerSha256: {hash}\nManifestType: installer\n";
        await Case("Manifest versions sort numerically and exclude previews", () => Check(WingetManifestResolver.FindLatestVersion("[{\"name\":\"2.9.0\",\"type\":\"dir\"},{\"name\":\"2.10.0\",\"type\":\"dir\"},{\"name\":\"3.0-beta\",\"type\":\"dir\"}]") == "2.10.0"));
        await Case("Manifest resolver chooses native architecture and hash", () =>
        {
            var item = WingetManifestResolver.ParseManifest(yaml, "Vendor.App", "2.10.0", profile);
            Check(item.DownloadUri.AbsolutePath == "/x64.exe" && item.Sha256 == hash && item.Version == "2.10.0");
        });
        await Case("Manifest rejects changed identity, type, hash, host and ambiguous installers", () =>
        {
            foreach (var changed in new[] { yaml.Replace("Vendor.App", "Other.App"), yaml.Replace("InstallerType: exe", "InstallerType: msi"), yaml.Replace(hash, "invalid"), yaml.Replace("vendor.example", "other.example"), yaml.Replace("Architecture: x86", "Architecture: x64") })
                Reject(() => WingetManifestResolver.ParseManifest(changed, "Vendor.App", "2.10.0", profile));
        });
        await Case("Manifest path rejects traversal and unexpected repository roots", () =>
        {
            WingetManifestResolver.ValidateManifestPath("b/ByteDance/Feishu");
            foreach (var path in new[] { "../x", "b/../App", "/b/Vendor/App", "https://other/repo", "manifests/b/Vendor/App" }) Reject(() => WingetManifestResolver.ValidateManifestPath(path));
        });
        await Case("Jingmai explicitly falls back to its prefilled visible adapter in silent queues", () =>
        {
            var app = new AppDefinition { Id = "jingmai", Name = "京麦", FolderName = "Jingmai", OnlineInstaller = new OnlineInstallerDefinition
            { DownloadUrl = "https://vendor.example/app.exe", AllowedDownloadHosts = ["vendor.example"], AllowedPublishers = ["Vendor"], LocationSupported = true, InteractiveFallback = true, Adapter = "jingmai" } };
            var plan = DeploymentEngine.Plan(new InstallRequest(app, @"E:\TempSoftware\Jingmai"), new DeploymentSettings { Silent = true }, [new("E", @"E:\", true, 100_000_000_000, DriveType.Fixed)]);
            Check(!plan.IsSilent && !plan.UsesDefaultLocation && !plan.IsWinget && plan.Adapter == "jingmai" && plan.Arguments.Count == 0 && plan.TargetDirectory == @"E:\TempSoftware\Jingmai");
            Reject(() => AppCatalog.Validate(app with { OnlineInstaller = app.OnlineInstaller with { Adapter = "unknown" } }));
            Reject(() => AppCatalog.Validate(app with { OnlineInstaller = app.OnlineInstaller with { SilentSupported = true } }));
        });
        Task Case(string name, Action action) => test(name, () => { action(); return Task.CompletedTask; });
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Manifest/adapter assertion failed."); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Unsafe metadata was accepted."); }
}

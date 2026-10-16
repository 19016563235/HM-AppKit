using FoxDeploy.Core;

internal static class WeixinInstallerTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        var weixin = AppCatalog.Load(FindCatalog()).Single(app => app.Id == "weixin");
        IReadOnlyList<DriveSnapshot> drives = [new("D", @"D:\", true, 100_000_000_000, DriveType.Fixed)];
        InstallPlan Plan(AppDefinition app, string target, bool silent = true) => InstallerPlanFactory.BindPayload(
            DeploymentEngine.Plan(new InstallRequest(app, target), new DeploymentSettings { Silent = silent }, drives),
            @"Z:\USB AppKit\Local Installer\weixin\setup.exe");

        await Case("Weixin catalog explicitly selects its reviewed vendor directory adapter", () =>
        {
            var profile = weixin.OnlineInstaller ?? throw new InvalidOperationException("Missing official source.");
            Check(profile.Adapter == "weixin" && profile.InstallerType == "nsis" && profile.SilentSupported && profile.LocationSupported,
                "Weixin's reviewed NSIS plugin contract must be explicit.");
            Check(profile.InstallLocationArgument == "/D {InstallDirectory}", "Standard /D= is consumed before the Weixin plugin receives its input.");
        });

        await Case("Weixin raw command preserves the complete path after the vendor separator", () =>
        {
            foreach (var target in new[] { @"D:\Software\Weixin", @"D:\Business Apps\Weixin", @"D:\业务软件 & Tools\微信" })
            {
                var plan = Plan(weixin, target);
                Check(plan.Adapter == "weixin" && plan.IsSilent && plan.UnquotedFinalArgument && plan.TargetDirectory == target,
                    "Reviewed adapter and path must survive planning.");
                var start = SystemProcessRunner.CreateStartInfo(new ProcessRequest(plan.ExecutablePath, plan.Arguments, TimeSpan.FromMinutes(1))
                    { UnquotedFinalArgument = plan.UnquotedFinalArgument });
                Check(!start.UseShellExecute && start.ArgumentList.Count == 0 && start.Arguments == "/S /D " + target,
                    "Directory must be the unquoted final tail; no command shell may parse path characters.");
                Check(ReadReviewedVendorInput(start.Arguments) == target, "Weixin's reviewed parser must receive the complete requested path.");
            }
        });

        await Case("Weixin regression contract exposes why the former standard NSIS switch loses the path", () =>
        {
            Check(ReadReviewedVendorInput(@"/S /D=D:\Software\Weixin") is null,
                "NSIS removes /D= and its tail from $CMDLINE before the vendor plugin parses it.");
            Check(ReadReviewedVendorInput(@"/S /D D:\Software\Weixin") == @"D:\Software\Weixin",
                "The vendor separator must survive the NSIS command-line filter.");
        });

        await Case("Weixin interactive command omits the silent switch and retains the reviewed directory input", () =>
        {
            var plan = Plan(weixin, @"D:\Business Apps\Weixin", silent: false);
            var start = SystemProcessRunner.CreateStartInfo(new ProcessRequest(plan.ExecutablePath, plan.Arguments, TimeSpan.FromMinutes(1))
                { UnquotedFinalArgument = plan.UnquotedFinalArgument });
            Check(!plan.IsSilent && start.Arguments == @"/D D:\Business Apps\Weixin", "Interactive planning must not add /S.");
        });

        await Case("Unmarked NSIS profiles cannot opt into the Weixin-only directory separator", () =>
        {
            var profile = weixin.OnlineInstaller!;
            Throws<InvalidDataException>(() => AppCatalog.Validate(weixin with { OnlineInstaller = profile with { Adapter = "" } }));
            var ordinary = weixin with { OnlineInstaller = profile with { Adapter = "", InstallLocationArgument = "/D={InstallDirectory}" } };
            Check(Plan(ordinary, @"D:\Software\Weixin").Arguments.SequenceEqual(["/S", @"/D=D:\Software\Weixin"]),
                "Other NSIS profiles must keep their standard directory contract.");
        });

        await Case("Weixin adapter rejects incompatible profiles and duplicate directory switches", () =>
        {
            var profile = weixin.OnlineInstaller!;
            foreach (var invalid in new[]
            {
                profile with { InstallLocationArgument = "/D={InstallDirectory}" },
                profile with { InstallerType = "exe" },
                profile with { LocationSupported = false },
                profile with { SilentSupported = false },
                profile with { InteractiveFallback = true },
                profile with { Adapter = "unreviewed" },
                profile with { SilentArguments = ["/S", "/D"] },
                profile with { SilentArguments = ["/S", @"/D D:\Wrong"] },
                profile with { InteractiveArguments = [@"/D=D:\Wrong"] },
                profile with { InteractiveArguments = ["--unreviewed-option"] }
            })
                Throws<InvalidDataException>(() => AppCatalog.Validate(weixin with { OnlineInstaller = invalid }));
        });

        await Case("Weixin raw tail refuses quotes control characters and missing directory values", () =>
        {
            foreach (var final in new[] { "/D ", "/D \"D:\\Apps\"", "/D D:\\Apps\r\n/S", "/d D:\\Apps", "/Other D:\\Apps" })
                Throws<ArgumentException>(() => SystemProcessRunner.CreateStartInfo(new ProcessRequest(@"Z:\setup.exe", ["/S", final], TimeSpan.FromMinutes(1))
                    { UnquotedFinalArgument = true }));
        });

        async Task Case(string name, Action action) => await test(name, () => { action(); return Task.CompletedTask; });
    }

    // Contract recorded by static analysis of the reviewed 4.1.15.53 package:
    // NSIS removes its standard /D= tail from $CMDLINE. GetParameters then finds
    // /D in the remaining input and discards one separator before the full path.
    // This models only that regression boundary; it does not execute vendor code.
    private static string? ReadReviewedVendorInput(string arguments)
    {
        var filteredAt = arguments.IndexOf(" /D=", StringComparison.Ordinal);
        var remaining = filteredAt >= 0 ? arguments[..filteredAt] : arguments;
        var marker = remaining.IndexOf("/D", StringComparison.Ordinal);
        return marker < 0 || remaining.Length <= marker + 3 ? null : remaining[(marker + 3)..];
    }

    private static string FindCatalog()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "catalog", "catalog.json");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Repository catalog not found for Weixin regression validation.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name);
    }
}

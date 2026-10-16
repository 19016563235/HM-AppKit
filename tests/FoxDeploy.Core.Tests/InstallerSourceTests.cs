using FoxDeploy.Core;

internal static class InstallerSourceTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "HM-AutoSetup-SourceTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var installer = Path.Combine(fixture, "example installer.exe");
        await File.WriteAllTextAsync(installer, "Never executed: fake installer fixture.");
        var target = Path.Combine(fixture, "Installed App");
        Directory.CreateDirectory(target);
        var driveLetter = Path.GetPathRoot(fixture)![..1];
        IReadOnlyList<DriveSnapshot> drives = [new(driveLetter, driveLetter + ":\\", true, 100_000_000_000, DriveType.Fixed)];
        var localProfile = new LocalInstallerDefinition
        {
            FileName = Path.GetFileName(installer), Sha256 = new string('a', 64), SilentSupported = true, LocationSupported = true
        };
        var onlineProfile = new OnlineInstallerDefinition
        {
            DownloadUrl = "https://example.com/latest.exe", AllowedDownloadHosts = ["example.com"], AllowedPublishers = ["Example Publisher"],
            SilentSupported = true, LocationSupported = true
        };
        var app = new AppDefinition
        {
            Id = "source-example", Name = "Source Example", PackageId = "Example.Source", FolderName = "SourceExample",
            SilentSupported = true, LocationSupported = true, LocalInstaller = localProfile
        };
        DeploymentSettings Local(bool silent = true, bool skip = false) => new()
        {
            SourceMode = InstallationSourceMode.Local, LocalInstallerDirectory = fixture, Silent = silent, SkipInstalled = skip,
            SelectedDrive = driveLetter
        };
        InstallPlan Plan(AppDefinition definition, DeploymentSettings options) => DeploymentEngine.Plan(new InstallRequest(definition, target), options, drives);

        try
        {
            await Case("Legacy source settings all route to automatic configured payload", () =>
            {
                foreach (var mode in Enum.GetValues<InstallationSourceMode>())
                    Check(!Plan(app, new DeploymentSettings { SourceMode = mode }).IsWinget, "Automatic local-only profile must bypass WinGet for " + mode);
            });
            await Case("Official online profile takes precedence over a WinGet identifier", () =>
                Check(!Plan(app with { OnlineInstaller = onlineProfile }, new DeploymentSettings()).IsWinget, "Configured online payload expected"));
            await Case("Online planning does not require local files or perform a download", () =>
            {
                var definition = app with { PackageId = "", LocalInstaller = null, OnlineInstaller = onlineProfile };
                var plan = Plan(definition, new DeploymentSettings { LocalInstallerDirectory = @"Z:\Missing" });
                Check(!plan.IsWinget && plan.Arguments[^1] == "/D=" + target, "Online plan must be available before staging");
            });
            await Case("Package-only custom applications retain their explicit WinGet route", () =>
                Check(Plan(app with { LocalInstaller = null }, Local()).IsWinget, "Configured package identifier must retain WinGet fallback."));
            await Case("Preview never requires files in the retired user-selected source", () =>
                Check(!Plan(app with { LocalInstaller = localProfile with { FileName = "missing.exe" } }, Local()).IsWinget, "Local-only packages must be resolved during payload verification."));
            await Case("Old settings normalize to Auto and fixed program-relative retention", () =>
            {
                var options = Local(); options.KeepInstallers = false;
                InstallerSources.NormalizeSettings(options);
                Check(options.SourceMode == InstallationSourceMode.Auto && options.KeepInstallers && options.LocalInstallerDirectory == Path.Combine(AppContext.BaseDirectory, "Local Installer"), "Legacy source and retention settings survived normalization.");
                Check(InstallerSources.ResolveLocalPath(app, Local()) == Path.Combine(AppContext.BaseDirectory, "Local Installer", localProfile.FileName), "Retired source directory was used.");
            });
            await Case("Automatic source always uses reviewed official install arguments", () =>
            {
                var definition = app with { OnlineInstaller = onlineProfile with { InstallerType = "exe", SilentArguments = ["--quiet"], InstallLocationArgument = "--target={InstallDirectory}" } };
                foreach (var mode in Enum.GetValues<InstallationSourceMode>())
                    Check(Plan(definition, new DeploymentSettings { SourceMode = mode }).Arguments.SequenceEqual(["--quiet", "--target=" + target]), "Legacy source selected outdated local install arguments.");
            });
            await Case("USB-hosted payload can target a different internal drive", () =>
            {
                var plan = InstallerPlanFactory.BindPayload(Plan(app, Local()), @"Z:\USB AppKit\Local Installer\example\package-123\setup.exe");
                Check(plan.ExecutablePath.StartsWith(@"Z:\", StringComparison.Ordinal) && plan.TargetDirectory == target, "Payload location incorrectly restricted target drive.");
            });
            await Case("NSIS silent and custom-directory arguments preserve the complete path", () =>
            {
                var plan = Plan(app, Local());
                Check(plan.Arguments.SequenceEqual(["/S", "/D=" + target]) && plan.UnquotedFinalArgument, "NSIS /D must be last");
                var start = SystemProcessRunner.CreateStartInfo(new ProcessRequest(installer, plan.Arguments, TimeSpan.FromMinutes(1)) { UnquotedFinalArgument = plan.UnquotedFinalArgument });
                Check(!start.UseShellExecute && start.ArgumentList.Count == 0 && start.Arguments == "/S /D=" + target, "NSIS /D tail must be raw even with spaces");
            });
            await Case("Raw NSIS directory mode rejects quotes and arbitrary final switches", () =>
            {
                foreach (var value in new[] { "/other", "/D=", "/D=\"C:\\Apps\"", "/D=C:\\Apps\r\n/other" })
                    Throws<ArgumentException>(() => SystemProcessRunner.CreateStartInfo(new ProcessRequest(installer, ["/S", value], TimeSpan.FromMinutes(1)) { UnquotedFinalArgument = true }));
            });
            await Case("NSIS raw-tail construction correctly quotes earlier arguments", () =>
            {
                var start = SystemProcessRunner.CreateStartInfo(new ProcessRequest(installer, ["", "quoted\"value", @"argument with slash\", "/D=" + target], TimeSpan.FromMinutes(1)) { UnquotedFinalArgument = true });
                Check(start.Arguments.StartsWith("\"\" \"quoted\\\"value\" \"argument with slash\\\\\" ", StringComparison.Ordinal), "Windows escaping must preserve preceding arguments");
                Check(start.Arguments.EndsWith("/D=" + target, StringComparison.Ordinal), "Directory is the final raw tail");
            });
            await Case("Generic EXE receives its configured switches as separate arguments", () =>
            {
                var definition = app with { LocalInstaller = localProfile with
                {
                    InstallerType = "exe", SilentArguments = ["--quiet", "--no-restart"], InstallLocationArgument = "--directory={InstallDirectory}"
                } };
                var plan = Plan(definition, Local());
                Check(plan.Arguments.SequenceEqual(["--quiet", "--no-restart", "--directory=" + target]) && !plan.UnquotedFinalArgument, "Generic installer argument template expected");
                var start = SystemProcessRunner.CreateStartInfo(new ProcessRequest(installer, plan.Arguments, TimeSpan.FromMinutes(1)));
                Check(start.ArgumentList[^1] == "--directory=" + target, "Directory must remain a single argument");
            });
            await Case("Direct silent mode refuses unverified silent support", () =>
                Throws<NotSupportedException>(() => Plan(app with { LocalInstaller = localProfile with { SilentSupported = false } }, Local())));
            await Case("Direct silent mode refuses unsupported destination even if default permission is enabled", () =>
            {
                var options = Local(); options.RequireCustomLocation = false;
                Throws<NotSupportedException>(() => Plan(app with { LocalInstaller = localProfile with { LocationSupported = false } }, options));
            });
            await Case("Explicit manual mode keeps requested target without adding a hidden path switch", () =>
            {
                var definition = app with { LocalInstaller = localProfile with { InstallerType = "exe", SilentSupported = false, LocationSupported = false, InteractiveArguments = ["--wizard"] } };
                var plan = Plan(definition, Local(silent: false));
                Check(!plan.IsSilent && plan.UsesDefaultLocation && plan.TargetDirectory == target && plan.Arguments.SequenceEqual(["--wizard"]), "Wizard must choose and later verify the requested target");
            });
            await Case("MSI payload binding keeps msiexec and substitutes the payload argument", () =>
            {
                var definition = app with { PackageId = "", LocalInstaller = null, OnlineInstaller = onlineProfile with { InstallerType = "msi" } };
                var plan = InstallerPlanFactory.BindPayload(Plan(definition, new DeploymentSettings()), Path.Combine(fixture, "example.msi"));
                Check(Path.GetFileName(plan.ExecutablePath) == "msiexec.exe" && plan.Arguments[0] == "/i" && plan.Arguments[1] == Path.Combine(fixture, "example.msi"), "MSI execution route expected");
                Check(plan.Arguments.Contains("/qn") && plan.Arguments.Contains("/norestart") && plan.Arguments[^1] == "INSTALLDIR=" + target, "MSI arguments expected");
            });
            await Case("Catalog accepts profiles without a WinGet package ID", () => AppCatalog.Validate(app with { PackageId = "" }));
            await Case("Catalog rejects unsafe local filenames and invalid hashes", () =>
            {
                foreach (var name in new[] { @"..\setup.exe", @"F:\setup.exe", "bad?.exe", "setup.txt" })
                    Throws<InvalidDataException>(() => AppCatalog.Validate(app with { LocalInstaller = localProfile with { FileName = name } }));
                Throws<InvalidDataException>(() => AppCatalog.Validate(app with { LocalInstaller = localProfile with { Sha256 = "not-a-hash" } }));
            });
            await Case("Catalog rejects online HTTP, unapproved hosts and missing publishers", () =>
            {
                foreach (var profile in new[]
                {
                    onlineProfile with { DownloadUrl = "http://example.com/latest.exe" },
                    onlineProfile with { DownloadUrl = "https://other.example/latest.exe" },
                    onlineProfile with { AllowedPublishers = [] },
                    onlineProfile with { AllowedDownloadHosts = [] }
                }) Throws<InvalidDataException>(() => AppCatalog.Validate(app with { OnlineInstaller = profile }));
            });
            await Case("Catalog requires a named URL group for metadata download rules", () =>
            {
                var profile = onlineProfile with { DownloadUrl = "", MetadataUrl = "https://example.com/metadata", UrlPattern = "https://example.com/setup.exe" };
                Throws<InvalidDataException>(() => AppCatalog.Validate(app with { OnlineInstaller = profile }));
                AppCatalog.Validate(app with { OnlineInstaller = profile with { UrlPattern = "(?<url>https://example.com/setup.exe)" } });
            });
            await Case("Catalog rejects unsupported installer types and missing generic EXE silent flags", () =>
            {
                Throws<InvalidDataException>(() => AppCatalog.Validate(app with { LocalInstaller = localProfile with { InstallerType = "script" } }));
                Throws<InvalidDataException>(() => AppCatalog.Validate(app with { LocalInstaller = localProfile with { InstallerType = "exe" } }));
            });
            await test("Direct local installation works without WinGet and never runs winget list", async () =>
            {
                var stagedPath = Path.Combine(fixture, "staged", "verified.exe");
                var runner = new SourceRunner(); var payload = new SourcePayload(stagedPath);
                var engine = Engine(runner, new SourceProbe(), payload);
                var updates = new List<InstallUpdate>();
                await engine.RunAsync([new InstallRequest(app, target)], Local(skip: true), new SourceProgress<InstallUpdate>(updates.Add), new SourceProgress<string>(_ => { }));
                Check(runner.Requests.Count == 1 && runner.Requests[0].FileName == stagedPath && runner.Requests[0].FileName != installer && runner.Requests[0].ProtectInstaller, "Only the verified staged copy can execute, never the original source or Winget");
                Check(payload.Calls == 1 && payload.Disposals == 1 && runner.Requests[0].UnquotedFinalArgument, "Prepared payload must be used, protected and disposed");
                Check(updates[^1].State == InstallState.Unverified, "Zero exit without registry evidence remains unverified");
            });
            await test("Direct online dispatch binds the prepared official payload", async () =>
            {
                var runner = new SourceRunner(); var payload = new SourcePayload(installer);
                var updates = new List<InstallUpdate>();
                await Engine(runner, new SourceProbe(), payload).RunAsync([new InstallRequest(app with { OnlineInstaller = onlineProfile }, target)],
                    new DeploymentSettings { SkipInstalled = false }, new SourceProgress<InstallUpdate>(updates.Add), new SourceProgress<string>(_ => { }));
                Check(runner.Requests.Count == 1 && runner.Requests[0].FileName == installer && payload.Mode == InstallationSourceMode.Auto, "Automatic route must use staged payload");
            });
            await test("Local registry skip neither prepares a payload nor runs a process", async () =>
            {
                var runner = new SourceRunner(); var payload = new SourcePayload(installer); var updates = new List<InstallUpdate>();
                await Engine(runner, new SourceProbe([new(app.Name, target, "test")]), payload).RunAsync([new InstallRequest(app, target)], Local(skip: true),
                    new SourceProgress<InstallUpdate>(updates.Add), new SourceProgress<string>(_ => { }));
                Check(updates[^1].State == InstallState.AlreadyInstalled && payload.Calls == 0 && runner.Requests.Count == 0, "Existing app must be skipped before payload access");
            });
            await test("Queue normalizes legacy source and retention settings before later mutation", async () =>
            {
                var options = Local(); options.KeepInstallers = false; var payload = new SourcePayload(installer); var runner = new SourceRunner();
                await Engine(runner, new SourceProbe(), payload).RunAsync([new InstallRequest(app, target)], options,
                    new SourceProgress<InstallUpdate>(update => { if (update.State == InstallState.Pending) { options.SourceMode = InstallationSourceMode.Online; options.LocalInstallerDirectory = @"Z:\Missing"; options.KeepInstallers = true; } }),
                    new SourceProgress<string>(_ => { }));
                Check(payload.Mode == InstallationSourceMode.Auto && payload.Directory == InstallerSources.InstallerDirectory && payload.Keep && runner.Requests.Count == 1, "Queue used retired source or disabled fixed retention.");
            });
            await test("USB source uses its absolute package working directory without restricting internal target", async () =>
            {
                var source = @"Z:\USB AppKit\Local Installer\example\package-123\setup.exe";
                var runner = new SourceRunner();
                await Engine(runner, new SourceProbe(), new SourcePayload(source)).RunAsync([new InstallRequest(app, target)], Local(),
                    new SourceProgress<InstallUpdate>(_ => { }), new SourceProgress<string>(_ => { }));
                Check(runner.Requests.Single().FileName == source && runner.Requests[0].WorkingDirectory == Path.GetDirectoryName(source), "Source working directory was inherited or changed to the target drive.");
            });
            await test("Manual direct installer still fails a mismatched final directory", async () =>
            {
                var updates = new List<InstallUpdate>(); var runner = new SourceRunner();
                var manual = app with { LocalInstaller = localProfile with { SilentSupported = false, LocationSupported = false } };
                await Engine(runner, new SourceProbe([new(app.Name, Path.Combine(fixture, "Wrong"), "test")]), new SourcePayload(installer)).RunAsync(
                    [new InstallRequest(manual, target)], Local(silent: false), new SourceProgress<InstallUpdate>(updates.Add), new SourceProgress<string>(_ => { }));
                Check(updates[^1].State == InstallState.Failed && runner.Requests.Count == 1, "A manual wizard cannot bypass final directory verification");
            });
            await test("Payload preparation failure prevents execution and allows the next queue entry", async () =>
            {
                var runner = new SourceRunner(); var payload = new SourcePayload(installer) { FailFirst = true }; var updates = new List<InstallUpdate>();
                await Engine(runner, new SourceProbe(), payload).RunAsync([new InstallRequest(app, target), new InstallRequest(app with { Id = "second-source" }, target)], Local(),
                    new SourceProgress<InstallUpdate>(updates.Add), new SourceProgress<string>(_ => { }));
                Check(updates.Last(update => update.AppId == app.Id).State == InstallState.Failed && runner.Requests.Count == 1 && payload.Calls == 2, "Unverified payload must never be executed");
            });
            await test("Installer requiring an existing target gets it only after payload validation", async () =>
            {
                var newTarget = Path.Combine(fixture, "New PDD Target");
                var definition = app with { LocalInstaller = localProfile with { RequiresExistingTargetDirectory = true } };
                await Engine(new SourceRunner(), new SourceProbe(), new SourcePayload(installer) { FailFirst = true }).RunAsync(
                    [new InstallRequest(definition, newTarget)], Local(), new SourceProgress<InstallUpdate>(_ => { }), new SourceProgress<string>(_ => { }));
                Check(!Directory.Exists(newTarget), "An invalid payload must not create the installation folder");
                await Engine(new SourceRunner(), new SourceProbe(), new SourcePayload(installer)).RunAsync(
                    [new InstallRequest(definition, newTarget)], Local(), new SourceProgress<InstallUpdate>(_ => { }), new SourceProgress<string>(_ => { }));
                Check(Directory.Exists(newTarget), "PDD ignores a destination that does not already exist");
            });
        }
        finally
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(fixture).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(fixture).StartsWith("HM-AutoSetup-SourceTests-", StringComparison.Ordinal))
                Directory.Delete(fixture, recursive: true);
        }

        Task Case(string name, Action assertion) => test(name, () => { assertion(); return Task.CompletedTask; });
        DeploymentEngine Engine(SourceRunner runner, SourceProbe probe, SourcePayload payload) => new(runner, probe, "", () => drives, payloadProvider: payload);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class SourceProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private sealed class SourceRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<string>? output, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }
    private sealed class SourceProbe(IReadOnlyList<InstalledApplication>? records = null) : IInstallationProbe
    {
        public IReadOnlyList<InstalledApplication> FindInstalled(AppDefinition app) => records ?? [];
    }
    private sealed class SourcePayload(string path) : IInstallerPayloadProvider
    {
        public int Calls { get; private set; }
        public int Disposals { get; private set; }
        public bool FailFirst { get; init; }
        public InstallationSourceMode Mode { get; private set; }
        public bool Keep { get; private set; }
        public string? Directory { get; private set; }
        public Task<PreparedInstaller> PrepareAsync(AppDefinition app, DeploymentSettings settings, IProgress<string> log, CancellationToken cancellationToken)
        {
            Calls++; Mode = settings.SourceMode; Directory = settings.LocalInstallerDirectory; Keep = settings.KeepInstallers;
            if (FailFirst && Calls == 1) throw new System.Security.Cryptography.CryptographicException("Rejected test payload");
            return Task.FromResult(new PreparedInstaller(path, cleanup: () => { Disposals++; return ValueTask.CompletedTask; }));
        }
    }
}

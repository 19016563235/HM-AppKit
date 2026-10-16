using FoxDeploy.Core;

public static class DeploymentEngineBehaviorTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "HM-AppKit-EngineTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var existing = Path.Combine(fixture, "Existing");
            Directory.CreateDirectory(existing);
            var wrong = Path.Combine(fixture, "Wrong");
            Directory.CreateDirectory(wrong);
            var root = Path.GetPathRoot(fixture)!;
            var drives = new[] { new DriveSnapshot(root[..1], root, true, 100_000_000_000, DriveType.Fixed) };
            var example = Definition("example");

            await test("Silent-capable installers precede regular wizards with stable group order", async () =>
            {
                var apps = new[] { WizardDefinition("manual-first"), Definition("silent-first"), WizardDefinition("manual-second"), Definition("silent-second") };
                var result = await Run(apps.Select(app => new InstallRequest(app, existing)).ToArray(), new DeploymentSettings { SkipInstalled = false }, payload: new FakePayload { Reject = true });
                Check(result.Payload.AppIds.SequenceEqual(["silent-first", "silent-second", "manual-first", "manual-second"]), "Actual installer dispatch must keep both groups stable.");
                Check(result.Runner.Requests.Count == 0, "The fake payload provider stops before any real wizard is needed.");
            });
            await test("Disabling global silent mode preserves the original selection order", async () =>
            {
                var apps = new[] { Definition("manual-first", silent: false), Definition("silent-first"), Definition("manual-second", silent: false), Definition("silent-second") };
                var result = await Run(apps.Select(app => new InstallRequest(app, existing)).ToArray(), new DeploymentSettings { SkipInstalled = false, Silent = false });
                Check(result.Payload.AppIds.SequenceEqual(apps.Select(app => app.Id)), "All regular installers must remain in selection order.");
                Check(result.Runner.Requests.All(request => !request.Arguments.Contains("/S")), "Global manual mode must never add silent switches.");
            });
            await test("Queue ordering uses the selected source capability and keeps Jingmai last", async () =>
            {
                var manual = Definition("manual", silent: false) with { SilentSupported = true };
                var jingmai = WizardDefinition("jingmai");
                var silent = Definition("silent") with { SilentSupported = false };
                var payload = new FakePayload { Reject = true };
                var result = await Run([new(manual, existing), new(jingmai, existing), new(silent, existing)], new DeploymentSettings { SkipInstalled = false }, payload: payload);
                Check(result.Updates.Where(update => update.State == InstallState.Checking).Select(update => update.AppId).SequenceEqual(["silent", "manual", "jingmai"]),
                    "Source profile overrides catalog flags, and Jingmai stays a regular wizard.");
                Check(result.Updates.Last(update => update.AppId == "manual").State == InstallState.Skipped,
                    "Sorting must not invent an unconfirmed fallback for an unsupported generic installer.");
                Check(result.Runner.Requests.Count == 0, "The Jingmai adapter is never executed by this fake rejection test.");
            });
            await test("Already-installed directory mismatch remains visible without payload access", async () =>
            {
                var result = await Run([new(example, existing)], new DeploymentSettings { SkipInstalled = true }, _ => [new(example.Name, wrong, "test")]);
                var update = result.Updates[^1];
                Check(update.State == InstallState.LocationMismatch && update.Message.Contains(wrong, StringComparison.Ordinal)
                    && update.Message.Contains(existing, StringComparison.Ordinal) && update.TargetDirectory == existing, "Both actual and requested locations must be visible.");
                Check(result.Payload.AppIds.Count == 0 && result.Runner.Requests.Count == 0, "Existing software must not be reinstalled or downloaded.");
            });
            await test("Second run does not turn the previous mismatched installation into success", async () =>
            {
                var records = new[] { new InstalledApplication(example.Name, wrong, "test") };
                var first = await Run([new(example, existing)], new DeploymentSettings { SkipInstalled = false }, _ => records);
                var second = await Run([new(example, existing)], new DeploymentSettings { SkipInstalled = true }, _ => records);
                // A zero-exit fake install uses the real final verification path.
                Check(first.Updates[^1].State == InstallState.LocationMismatch && second.Updates[^1].State == InstallState.LocationMismatch,
                    "Directory warnings must survive the normal skip-installed path on a later run.");
                Check(first.Runner.Requests.Count == 1 && second.Runner.Requests.Count == 0, "Only the first run may dispatch an installer.");
            });
            await test("Matching existing install in a version subdirectory is skipped safely", async () =>
            {
                var version = Path.Combine(existing, "1.2.3");
                Directory.CreateDirectory(version);
                var result = await Run([new(example, existing)], new DeploymentSettings { SkipInstalled = true }, _ => [new(example.Name, version, "test")]);
                Check(result.Updates[^1].State == InstallState.AlreadyInstalled && result.Runner.Requests.Count == 0 && result.Payload.AppIds.Count == 0,
                    "A verified child of the requested directory remains an installed skip.");
            });
            await test("Existing record with no location or a missing directory is not green success", async () =>
            {
                foreach (var location in new string?[] { null, Path.Combine(existing, "missing") })
                {
                    var result = await Run([new(example, existing)], new DeploymentSettings { SkipInstalled = true }, _ => [new(example.Name, location, "test")]);
                    Check(result.Updates[^1].State == InstallState.ExistingUnverified && result.Runner.Requests.Count == 0 && result.Payload.AppIds.Count == 0,
                        "Unknown/stale installed locations must stay visible without reinstalling.");
                }
            });
            await test("Mixed existing records cannot hide one mismatched location", async () =>
            {
                var result = await Run([new(example, existing)], new DeploymentSettings { SkipInstalled = true }, _ => [new(example.Name, existing, "first"), new(example.Name, wrong, "second")]);
                Check(result.Updates[^1].State == InstallState.LocationMismatch && result.Runner.Requests.Count == 0, "All existing records must be checked.");
            });
            await test("Unavailable requested target still skips an existing app with a warning", async () =>
            {
                var result = await Run([new(example, @"Z:\Missing\Example")], new DeploymentSettings { SkipInstalled = true }, _ => [new(example.Name, existing, "test")]);
                Check(result.Updates[^1].State == InstallState.ExistingUnverified && result.Runner.Requests.Count == 0 && result.Payload.AppIds.Count == 0,
                    "Invalid current targets must not trigger reinstall or green success for an existing application.");
            });
            await test("Existing application can be checked without requiring new silent support", async () =>
            {
                var definition = example with { OnlineInstaller = example.OnlineInstaller! with { SilentSupported = false, InteractiveFallback = false } };
                var result = await Run([new(definition, existing)], new DeploymentSettings { SkipInstalled = true }, _ => [new(example.Name, existing, "test")]);
                Check(result.Updates[^1].State == InstallState.AlreadyInstalled && result.Runner.Requests.Count == 0 && result.Payload.AppIds.Count == 0,
                    "An existing installation does not need permission to run a new installer.");
            });
            await test("WinGet-only installed detection does not invent an installation location", async () =>
            {
                var winget = example with { OnlineInstaller = null, PackageId = "Example.Package", LocationSupported = true };
                var result = await Run([new(winget, existing)], new DeploymentSettings { SkipInstalled = true });
                Check(result.Updates[^1].State == InstallState.ExistingUnverified && result.Runner.Requests.Single().Arguments[0] == "list"
                    && result.Payload.AppIds.Count == 0, "WinGet presence without location evidence must skip with an honest warning.");
            });
            await test("Installer already-installed exit verifies the actual directory instead of green success", async () =>
            {
                var result = await Run([new(example, existing)], new DeploymentSettings { SkipInstalled = false }, _ => [new(example.Name, wrong, "test")],
                    processResult: new ProcessResult(unchecked((int)0x8A150061), "", ""));
                Check(result.Updates[^1].State == InstallState.LocationMismatch && result.Runner.Requests.Count == 1, "Installer-level already-present results must also verify the actual path.");
            });
            await test("Only the Qianniu direct installer requests its confirmed GBK output encoding", async () =>
            {
                var result = await Run([new(Definition("qianniu"), existing), new(Definition("other"), existing)], new DeploymentSettings { SkipInstalled = false });
                Check(result.Runner.Requests[0].OutputCodePage == 936 && result.Runner.Requests[1].OutputCodePage is null,
                    "Encoding overrides must stay limited to the verified Qianniu installer output.");
            });

            async Task<Scenario> Run(IReadOnlyList<InstallRequest> requests, DeploymentSettings settings,
                Func<AppDefinition, IReadOnlyList<InstalledApplication>>? records = null, FakePayload? payload = null, ProcessResult? processResult = null)
            {
                var updates = new List<InstallUpdate>();
                var runner = new FakeRunner(processResult ?? new ProcessResult(0, "", ""));
                payload ??= new FakePayload();
                var engine = new DeploymentEngine(runner, new FakeProbe(records ?? (_ => [])), "fake-winget.exe", () => drives, payloadProvider: payload);
                await engine.RunAsync(requests, settings, new InlineProgress<InstallUpdate>(updates.Add), new InlineProgress<string>(_ => { }));
                return new Scenario(updates, payload, runner);
            }
        }
        finally
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(fixture).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(fixture).StartsWith("HM-AppKit-EngineTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(fixture, recursive: true);
        }
    }

    private static AppDefinition Definition(string id, bool silent = true) => new()
    {
        Id = id, Name = id, FolderName = id,
        OnlineInstaller = new OnlineInstallerDefinition
        {
            DownloadUrl = "https://downloads.example.test/setup.exe", AllowedDownloadHosts = ["downloads.example.test"], AllowedPublishers = ["Test Publisher"],
            SilentSupported = silent, LocationSupported = true
        }
    };
    private static AppDefinition WizardDefinition(string id)
    {
        var app = Definition(id, silent: false);
        return app with { OnlineInstaller = app.OnlineInstaller! with { Adapter = "jingmai", InteractiveFallback = true } };
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record Scenario(List<InstallUpdate> Updates, FakePayload Payload, FakeRunner Runner);
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private sealed class FakePayload : IInstallerPayloadProvider
    {
        public List<string> AppIds { get; } = [];
        public bool Reject { get; init; }
        public Task<PreparedInstaller> PrepareAsync(AppDefinition app, DeploymentSettings settings, IProgress<string> log, CancellationToken cancellationToken)
        {
            AppIds.Add(app.Id);
            return Reject ? Task.FromException<PreparedInstaller>(new NotSupportedException("Test stops before any installer execution."))
                : Task.FromResult(new PreparedInstaller(@"C:\FakePayload\" + app.Id + ".exe"));
        }
    }
    private sealed class FakeProbe(Func<AppDefinition, IReadOnlyList<InstalledApplication>> records) : IInstallationProbe
    {
        public IReadOnlyList<InstalledApplication> FindInstalled(AppDefinition app) => records(app);
    }
    private sealed class FakeRunner(ProcessResult result) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<string>? output, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}

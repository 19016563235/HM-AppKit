using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FoxDeploy.Core;

if (args.Length > 0 && args[0] == "--list-drives")
{
    Console.WriteLine(JsonSerializer.Serialize(DriveResolver.GetDrives().Select(drive => new
    {
        drive.Name, drive.Type, drive.IsReady, drive.BusType, drive.HardwareVerified, drive.IsExternal, drive.IsVirtual,
        Eligible = DriveSelectionPolicy.IsEligibleTarget(drive)
    }), new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

if (args.Length > 0 && args[0] == "--echo-args")
{
    Console.WriteLine(JsonSerializer.Serialize(args.Skip(1).ToArray()));
    return 0;
}
if (args.Length > 0 && args[0] == "--wait-ms")
{
    Console.WriteLine("started");
    await Task.Delay(int.Parse(args[1]));
    Console.WriteLine("completed");
    return 0;
}
if (args.Length > 0 && args[0] == "--streams")
{
    for (var index = 0; index < 3000; index++)
    {
        Console.WriteLine($"out-{index}-" + new string('a', 80));
        Console.Error.WriteLine($"err-{index}-" + new string('b', 80));
    }
    return 0;
}

var failures = new List<string>();
var passed = 0;
var temporary = Path.Combine(Path.GetTempPath(), "FoxDeploy.Core.Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var app = new AppDefinition { Id = "example", Name = "Example App", PackageId = "Example.App", FolderName = "Example", SilentSupported = true, LocationSupported = true };
var drives = new[] { Drive("D", ready: false), Drive("E"), Drive("F"), Drive("C") };
var settings = new DeploymentSettings { SkipInstalled = false };

await Test("Drive priority skips missing/not-ready D and selects E before F", () =>
    Equal(@"E:\Software\Example", DriveResolver.ResolveTarget(app, null, settings, drives)));
await Test("Default root folder is Software", () => Equal("Software", new DeploymentSettings().BaseFolder));
await Test("Default drive priority places all data drives ahead of C", () =>
    Check(new DeploymentSettings().DrivePriority.SequenceEqual("DEFGHIJKLMNOPQRSTUVWXYZABC".Select(letter => letter.ToString())), "Expected D through Z, then A, B, C"));
await Test("D takes precedence over E and F regardless of drive enumeration", () =>
    Equal(@"D:\Software\Example", DriveResolver.ResolveTarget(app, null, settings, [Drive("F"), Drive("E"), Drive("D"), Drive("C")])));
await Test("F is used when D and E are missing", () =>
    Equal(@"F:\Software\Example", DriveResolver.ResolveTarget(app, null, settings, [Drive("C"), Drive("F")])));
await Test("Other data drives precede C", () =>
    Equal(@"G:\Software\Example", DriveResolver.ResolveTarget(app, null, settings, [Drive("C"), Drive("G")])));
await Test("C is available as the final default fallback", () =>
    Equal(@"C:\Software\Example", DriveResolver.ResolveTarget(app, null, settings, [Drive("C")])));
await Test("Explicit restricted drive priority does not fall back to C", () => Throws<InvalidOperationException>(() =>
    DriveResolver.ResolveTarget(app, null, new DeploymentSettings { DrivePriority = ["D", "E", "F"] }, [Drive("C")])));
await Test("No removable drive fallback", () => Throws<InvalidOperationException>(() => DriveResolver.ResolveTarget(app, null, settings, [Drive("D") with { Type = DriveType.Removable }])));
await Test("Manual C selection overrides available D", () =>
    Equal(@"C:\Software\Example", DriveResolver.ResolveTarget(app, null, new DeploymentSettings { SelectedDrive = "c:" }, [Drive("D"), Drive("C")])));
await Test("Missing selected drive fails without fallback", () => Throws<InvalidOperationException>(() =>
    DriveResolver.ResolveTarget(app, null, new DeploymentSettings { SelectedDrive = "E" }, [Drive("D"), Drive("C")])));
await Test("Unready selected drive fails without fallback", () => Throws<InvalidOperationException>(() =>
    DriveResolver.ResolveTarget(app, null, new DeploymentSettings { SelectedDrive = "D" }, [Drive("D", ready: false), Drive("C")])));
await Test("Removable selected drive fails without fallback", () => Throws<InvalidOperationException>(() =>
    DriveResolver.ResolveTarget(app, null, new DeploymentSettings { SelectedDrive = "D" }, [Drive("D") with { Type = DriveType.Removable }, Drive("C")])));
await Test("Malformed selected drive is rejected", () => Throws<ArgumentException>(() =>
    DriveResolver.ResolveTarget(app, null, new DeploymentSettings { SelectedDrive = "D;C" }, drives)));
await Test("Per-application location overrides global drive selection", () =>
    Equal(@"E:\Custom Apps\Example", DriveResolver.ResolveTarget(app, @"E:\Custom Apps\Example", new DeploymentSettings { SelectedDrive = "D" }, drives)));
await Test("No ready local disk fails", () => Throws<InvalidOperationException>(() =>
    DriveResolver.ResolveTarget(app, null, settings, [Drive("D", ready: false), Drive("C", ready: false)])));
await Test("Storage choices include ready fixed C last and exclude removable or unready disks", () =>
{
    var choices = DriveSelectionPolicy.OrderDrives([Drive("C"), Drive("F"), Drive("A"), Drive("D"), Drive("E", ready: false), Drive("G") with { Type = DriveType.Removable }, Drive("B"), Drive("Z")]);
    Check(choices.Select(drive => drive.Name).SequenceEqual(["D", "F", "Z", "A", "B", "C"]), "Only local available disks are eligible, with C last");
});
await Test("Default storage selection normalizes drive letter", () =>
    Equal("E", DriveSelectionPolicy.ResolveDefaultDrive([Drive("C"), Drive("E") with { Name = "e:\\" }])));
await Test("Default storage selection is empty without ready fixed disks", () =>
    Equal<string?>(null, DriveSelectionPolicy.ResolveDefaultDrive([Drive("C", ready: false), Drive("D") with { Type = DriveType.Removable }])));
await Test("Fixed USB hard disks and SSDs are excluded from storage choices", () =>
{
    var externalHdd = Drive("D") with { BusType = "USB", IsExternal = true };
    var externalSsd = Drive("E") with { BusType = "USB", IsExternal = true };
    Check(DriveSelectionPolicy.OrderDrives([externalHdd, externalSsd, Drive("C")]).Select(drive => drive.Name).SequenceEqual(["C"]), "DriveType.Fixed must not make USB devices eligible");
});
await Test("Hotplug NVMe is excluded even when its bus resembles an internal SSD", () =>
    Check(!DriveSelectionPolicy.IsEligibleTarget(Drive("D") with { BusType = "NVMe", IsExternal = true }), "External hotplug SSD rejected"));
await Test("Unverified fixed disk is excluded and default falls back to internal C", () =>
    Equal("C", DriveSelectionPolicy.ResolveDefaultDrive([Drive("D") with { HardwareVerified = false }, Drive("C")])));
await Test("Virtual fixed disks cannot be install targets", () =>
    Check(!DriveSelectionPolicy.IsEligibleTarget(Drive("D") with { BusType = "FileBackedVirtual", IsVirtual = true }), "Virtual disk rejected"));
await Test("External priority drive is skipped in favor of internal drive", () =>
    Equal(@"E:\Software\Example", DriveResolver.ResolveTarget(app, null, settings, [Drive("D") with { BusType = "USB", IsExternal = true }, Drive("E"), Drive("C")])));
foreach (var excluded in new[]
{
    Drive("D") with { BusType = "USB", IsExternal = true },
    Drive("D") with { BusType = "NVMe", IsExternal = true },
    Drive("D") with { IsVirtual = true },
    Drive("D") with { HardwareVerified = false }
})
{
    await Test($"Selected excluded {excluded.BusType} disk cannot bypass storage filter", () => Throws<InvalidOperationException>(() =>
        DriveResolver.ResolveTarget(app, null, new DeploymentSettings { SelectedDrive = "D" }, [excluded, Drive("C")])));
    await Test($"Per-application custom path cannot bypass excluded {excluded.BusType} disk", () => Throws<InvalidOperationException>(() =>
        DriveResolver.ResolveTarget(app, @"D:\Custom\Example", settings, [excluded, Drive("C")])));
}
await Test("Explicit existing C location remains explicit", () => Equal(@"C:\My Apps\Example", DriveResolver.ResolveTarget(app, @"C:\My Apps\Example", settings, drives)));
await Test("Missing custom drive fails", () => Throws<InvalidOperationException>(() => DriveResolver.ResolveTarget(app, @"Z:\Example", settings, drives)));
foreach (var invalid in new[] { @"D:\", @"D:Example", @"..\Example", @"\\server\share\Example", @"D:\Example\..\Windows", @"D:\Example\NUL.txt", @"D:\Example\bad?folder", @"D:\Example\trailing." })
    await Test("Invalid target rejected: " + invalid, () => Throws<ArgumentException>(() => DriveResolver.ValidateAbsolutePath(invalid)));
await Test("Malformed priority does not silently skip validation", () => Throws<ArgumentException>(() => DriveResolver.ResolveTarget(app, null, new DeploymentSettings { DrivePriority = ["D;C"] }, drives)));
await Test("Path spaces and ampersands remain one argument", () =>
{
    var plan = DeploymentEngine.Plan(new InstallRequest(app, @"E:\Company Apps\Fox & Co"), settings, drives);
    var info = SystemProcessRunner.CreateStartInfo(new ProcessRequest("winget.exe", plan.Arguments, TimeSpan.FromMinutes(1), true));
    Check(!info.UseShellExecute, "Shell must remain disabled");
    Equal(plan.Arguments.Count, info.ArgumentList.Count);
    Equal(@"E:\Company Apps\Fox & Co", info.ArgumentList[^1]);
    Check(info.ArgumentList.Contains("--exact") && info.ArgumentList.Contains("--source"), "Exact winget source required");
});
await Test("Unsupported custom location is skipped by default", () => Throws<NotSupportedException>(() => DeploymentEngine.Plan(new InstallRequest(app with { LocationSupported = false }), settings, drives)));
await Test("Explicit default-location permission omits location argument", () =>
{
    var plan = DeploymentEngine.Plan(new InstallRequest(app with { LocationSupported = false }), new DeploymentSettings { RequireCustomLocation = false }, []);
    Check(plan.UsesDefaultLocation && plan.TargetDirectory is null, "Default location must not claim a target");
    Check(!plan.Arguments.Contains("--location"), "No location flag");
});
await Test("Unsupported silent mode cannot silently fall back to interactive", () =>
    Throws<NotSupportedException>(() => DeploymentEngine.Plan(new InstallRequest(app with { SilentSupported = false }), settings, drives)));
await Test("Explicitly disabled silent mode uses interactive mode", () =>
{
    var plan = DeploymentEngine.Plan(new InstallRequest(app with { SilentSupported = false }), new DeploymentSettings { Silent = false }, drives);
    Check(!plan.IsSilent && plan.Arguments.Contains("--interactive") && !plan.Arguments.Contains("--silent"), "Interactive fallback expected");
});
await Test("Catalog supports case-insensitive fields and wrapper", () => Equal(1, AppCatalog.Parse("{\"apps\":[{\"id\":\"test\",\"name\":\"Test\",\"packageId\":\"Vendor.Test\",\"folderName\":\"Test\"}]}").Count));
await Test("Catalog rejects duplicate application identifiers", () => Throws<InvalidDataException>(() => AppCatalog.Parse(JsonSerializer.Serialize(new[] { app, app }))));
await Test("Catalog rejects package arguments disguised as identifiers", () => Throws<InvalidDataException>(() => AppCatalog.Validate(app with { PackageId = "Vendor.App --force" })));
await Test("Verification respects path segment boundaries", () =>
{
    Check(InstallationLocationVerifier.IsWithinTarget(@"D:\Apps\Example\1.2", @"D:\Apps\Example"), "Version child accepted");
    Check(!InstallationLocationVerifier.IsWithinTarget(@"D:\Apps\ExampleOther", @"D:\Apps\Example"), "Sibling rejected");
    Check(!InstallationLocationVerifier.IsWithinTarget(@"C:\Apps\Example", @"D:\Apps\Example"), "Wrong drive rejected");
});
await Test("Registry identity accepts an exact name or a numeric version suffix", () =>
{
    foreach (var displayName in new[] { "千帆客服工作台", "千帆客服工作台 1.2.6", "千帆客服工作台 v1.2.6", "千帆客服工作台 V1.2.6.1539" })
        Check(RegistryApplicationIdentity.MatchesDisplayName(displayName, ["千帆客服工作台"]), "Expected installed product: " + displayName);
});
await Test("Registry identity retains case-insensitive and trimmed exact aliases", () =>
{
    Check(RegistryApplicationIdentity.MatchesDisplayName("  QIANFAN  ", ["Other Name", " qianfan "]), "Exact alias normalization remains supported");
    Check(RegistryApplicationIdentity.MatchesDisplayName("QIANFAN v1.2.6", ["qianfan"]), "Versioned alias remains case-insensitive");
});
await Test("Registry identity rejects helpers, unrelated prefixes and version lookalikes", () =>
{
    foreach (var displayName in new[] { "千帆客服工作台 助手", "千帆客服工作台 Updater", "其他千帆客服工作台 1.2.6", "千帆客服工作台 1.2.6 助手", "千帆客服工作台1.2.6", "千帆客服工作台 1.2", "千帆客服工作台 1.2.6-beta", "千帆客服工作台  1.2.6", "千帆客服工作台 1.2.x", "千帆客服工作台 １.２.６" })
        Check(!RegistryApplicationIdentity.MatchesDisplayName(displayName, ["千帆客服工作台"]), "Unrelated product must not match: " + displayName);
    Check(!RegistryApplicationIdentity.MatchesDisplayName(null, ["千帆客服工作台"]), "Missing display name must not match");
});

var iconPath = Path.Combine(temporary, "an app, with comma.ico");
File.WriteAllText(iconPath, "evidence");
var uninstallPath = Path.Combine(temporary, "uninstall.exe");
File.WriteAllText(uninstallPath, "evidence");
await Test("Quoted DisplayIcon with index resolves existing file", () => Equal(temporary, RegistryLocationEvidence.ResolveFileParent('"' + iconPath + "\",-12", true)));
await Test("Unquoted DisplayIcon supports comma in filename", () => Equal(temporary, RegistryLocationEvidence.ResolveFileParent(iconPath + ",0", true)));
await Test("Quoted uninstall executable ignores arguments without execution", () => Equal(temporary, RegistryLocationEvidence.ResolveFileParent('"' + uninstallPath + "\" /S & arbitrary", false)));
await Test("Unquoted uninstall executable with spaces resolves safely", () => Equal(temporary, RegistryLocationEvidence.ResolveFileParent(uninstallPath + " /S", false)));
await Test("Missing icon never creates location evidence", () => Check(RegistryLocationEvidence.ResolveFileParent(Path.Combine(temporary, "missing.exe"), true) is null, "Missing file rejected"));
await Test("System executable never creates application location evidence", () => Check(RegistryLocationEvidence.ResolveFileParent(Path.Combine(Environment.SystemDirectory, "cmd.exe"), true) is null, "System path rejected"));
await Test("Icon command suffix cannot be interpreted as an index", () => Check(RegistryLocationEvidence.ResolveFileParent('"' + iconPath + "\" /other", true) is null, "Invalid suffix rejected"));

await TestAsync("Actual subprocess receives literal metacharacters and Unicode", async () =>
{
    var values = new[] { @"E:\Company Apps\Fox & Co", "$(not-a-command)", "hello|world", "中文应用", "embedded\"quote" };
    var result = await new SystemProcessRunner().RunAsync(Self(["--echo-args", .. values], TimeSpan.FromSeconds(10)), null, default);
    Equal(0, result.ExitCode);
    Check(values.SequenceEqual(JsonSerializer.Deserialize<string[]>(result.StandardOutput)!), "Arguments must round-trip exactly");
});
await TestAsync("Both redirected streams drain without pipe deadlock", async () =>
{
    var result = await new SystemProcessRunner().RunAsync(Self(["--streams"], TimeSpan.FromSeconds(15)), null, default);
    Equal(0, result.ExitCode);
    Check(result.StandardOutput.Contains("out-2999") && result.StandardError.Contains("err-2999"), "Both streams must finish");
    Check(result.StandardOutput.Length < 66000 && result.StandardError.Length < 66000, "Capture remains bounded");
});
await TestAsync("Stop request never kills protected active process", async () =>
{
    using var cancellation = new CancellationTokenSource();
    var result = await new SystemProcessRunner().RunAsync(Self(["--wait-ms", "250"], TimeSpan.FromSeconds(10), true),
        new InlineProgress<string>(line => { if (line == "started") cancellation.Cancel(); }), cancellation.Token);
    Check(result.CancellationRequested && result.StandardOutput.Contains("completed") && result.ExitCode == 0, "Protected process must complete after cancellation");
});
await TestAsync("Timeout reports and awaits protected process", async () =>
{
    var result = await new SystemProcessRunner().RunAsync(Self(["--wait-ms", "250"], TimeSpan.FromMilliseconds(60), true), null, default);
    Check(result.TimedOut && result.StandardOutput.Contains("completed") && result.ExitCode == 0, "Protected process must complete after timeout");
});
await TestAsync("Noninstall check timeout is bounded", async () =>
{
    var clock = Stopwatch.StartNew();
    var result = await new SystemProcessRunner().RunAsync(Self(["--wait-ms", "5000"], TimeSpan.FromMilliseconds(100)), null, default);
    Check(result.TimedOut && clock.Elapsed < TimeSpan.FromSeconds(3) && !result.StandardOutput.Contains("completed"), "Check should stop within timeout");
});

await TestAsync("Installer exit zero without evidence is Unverified", async () =>
{
    var runner = new FakeRunner();
    var updates = await Run(runner, new FakeProbe(), [new InstallRequest(app, temporary)]);
    Equal(InstallState.Unverified, updates[^1].State);
    Check(runner.Requests.Single().ProtectInstaller, "Active install must always be protected");
});
await TestAsync("Queued installation snapshots the selected drive", async () =>
{
    var options = new DeploymentSettings { SkipInstalled = false, SelectedDrive = "C" };
    var runner = new FakeRunner();
    var engine = new DeploymentEngine(runner, new FakeProbe(), "test-winget.exe", () => [Drive("D"), Drive("C")]);
    await engine.RunAsync([new InstallRequest(app)], options,
        new InlineProgress<InstallUpdate>(update => { if (update.State == InstallState.Pending) options.SelectedDrive = "D"; }),
        new InlineProgress<string>(_ => { }));
    Equal(@"C:\Software\Example", runner.Requests.Single().Arguments[^1]);
});
await TestAsync("Installer location mismatch is explicitly reported even with exit zero", async () =>
{
    var mismatch = Path.Combine(temporary, "wrong");
    Directory.CreateDirectory(mismatch);
    var probe = new FakeProbe { Records = [new InstalledApplication(app.Name, mismatch, "test")] };
    var updates = await Run(new FakeRunner(), probe, [new InstallRequest(app, Path.Combine(temporary, "required"))]);
    Equal(InstallState.LocationMismatch, updates[^1].State);
});
await TestAsync("Verified existing target records succeed", async () =>
{
    var updates = await Run(new FakeRunner(), new FakeProbe { Records = [new InstalledApplication(app.Name, temporary, "test")] }, [new InstallRequest(app, temporary)]);
    Equal(InstallState.Succeeded, updates[^1].State);
});
await TestAsync("Registry skip does not launch installer or relocate", async () =>
{
    var runner = new FakeRunner();
    var updates = await Run(runner, new FakeProbe { Records = [new InstalledApplication(app.Name, temporary, "test")] },
        [new InstallRequest(app, temporary)], options: new DeploymentSettings { SkipInstalled = true });
    Equal(InstallState.AlreadyInstalled, updates[^1].State);
    Equal(0, runner.Requests.Count);
});
await TestAsync("Cancellation completes current item and cancels remaining queue", async () =>
{
    using var cancellation = new CancellationTokenSource();
    var runner = new FakeRunner { Callback = async _ => { cancellation.Cancel(); await Task.Delay(30); return new ProcessResult(0, "", "", CancellationRequested: true); } };
    var updates = await Run(runner, new FakeProbe { Records = [new InstalledApplication(app.Name, temporary, "test")] },
        [new InstallRequest(app, temporary), new InstallRequest(app with { Id = "second" }, temporary)], cancellation.Token);
    Equal(1, runner.Requests.Count);
    Equal(InstallState.Succeeded, updates.Last(u => u.AppId == app.Id).State);
    Equal(InstallState.Cancelled, updates.Last(u => u.AppId == "second").State);
});
await TestAsync("Timeout stops subsequent queue entries", async () =>
{
    var runner = new FakeRunner { Callback = _ => Task.FromResult(new ProcessResult(0, "", "", TimedOut: true)) };
    var updates = await Run(runner, new FakeProbe(), [new InstallRequest(app, temporary), new InstallRequest(app with { Id = "second" }, temporary)]);
    Equal(1, runner.Requests.Count);
    Equal(InstallState.Skipped, updates.Last(u => u.AppId == "second").State);
});
await TestAsync("Unsupported location causes no process execution", async () =>
{
    var runner = new FakeRunner();
    var updates = await Run(runner, new FakeProbe(), [new InstallRequest(app with { LocationSupported = false })]);
    Equal(InstallState.Skipped, updates[^1].State);
    Equal(0, runner.Requests.Count);
});
await TestAsync("Unsupported silent mode causes no process execution", async () =>
{
    var runner = new FakeRunner();
    var updates = await Run(runner, new FakeProbe(), [new InstallRequest(app with { SilentSupported = false }, temporary)]);
    Equal(InstallState.Skipped, updates[^1].State);
    Equal(0, runner.Requests.Count);
});
await Test("Known memory and privilege codes are distinguished", () =>
{
    Check(DeploymentEngine.DescribeExitCode(unchecked((int)0x8A150106)).Contains("内存"), "Correct memory diagnosis");
    Check(DeploymentEngine.DescribeExitCode(unchecked((int)0x8A150019)).Contains("管理员"), "Correct elevation diagnosis");
});

await InstallerSourceTests.RunAsync(TestAsync);
await PayloadValidationTests.RunAsync(TestAsync);
await ManifestResolverTests.RunAsync(TestAsync);
await DeploymentEngineBehaviorTests.RunAsync(TestAsync);
await WeixinInstallerTests.RunAsync(TestAsync);

// Delete only this test-created directory after verifying it remains under the system temp root.
var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
if (Path.GetFullPath(temporary).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(temporary).StartsWith("FoxDeploy.Core.Tests-", StringComparison.Ordinal))
    Directory.Delete(temporary, recursive: true);
Console.WriteLine($"\n{passed} passed, {failures.Count} failed.");
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

DriveSnapshot Drive(string name, bool ready = true) => new(name, name + ":\\", ready, 100_000_000_000, DriveType.Fixed);

async Task<List<InstallUpdate>> Run(FakeRunner runner, FakeProbe probe, IReadOnlyList<InstallRequest> requests, CancellationToken cancellation = default, DeploymentSettings? options = null)
{
    var updates = new List<InstallUpdate>();
    var rootLetter = Path.GetPathRoot(temporary)![..1];
    var engine = new DeploymentEngine(runner, probe, "test-winget.exe", () => [Drive(rootLetter)]);
    await engine.RunAsync(requests, options ?? settings, new InlineProgress<InstallUpdate>(updates.Add), new InlineProgress<string>(_ => { }), cancellation);
    return updates;
}

Task Test(string name, Action assertion) => TestAsync(name, () => { assertion(); return Task.CompletedTask; });
async Task TestAsync(string name, Func<Task> assertion)
{
    try { await assertion(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected [{expected}], got [{actual}]"); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

ProcessRequest Self(IReadOnlyList<string> arguments, TimeSpan timeout, bool protect = false)
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing process path");
    var actual = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? new[] { Assembly.GetExecutingAssembly().Location }.Concat(arguments).ToArray() : arguments;
    return new ProcessRequest(executable, actual, timeout, protect);
}

sealed class InlineProgress<T>(Action<T> callback) : IProgress<T> { public void Report(T value) => callback(value); }
sealed class FakeRunner : IProcessRunner
{
    public List<ProcessRequest> Requests { get; } = [];
    public Func<ProcessRequest, Task<ProcessResult>>? Callback { get; init; }
    public Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<string>? output, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Callback?.Invoke(request) ?? Task.FromResult(new ProcessResult(0, "", ""));
    }
}
sealed class FakeProbe : IInstallationProbe
{
    public IReadOnlyList<InstalledApplication> Records { get; init; } = [];
    public IReadOnlyList<InstalledApplication> FindInstalled(AppDefinition app) => Records;
}

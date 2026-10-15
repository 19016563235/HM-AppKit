using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FoxDeploy.Core;

if (args.Length > 0 && args[0] == "--create-native-manifest") return await NativeManifest(args);

var passed = 0;
var failures = new List<string>();

await Test("Managed release files update atomically; caches, profiles and unknown files survive", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    var result = await fixture.Helper().ApplyAsync(manifest);
    Check(result.Success && !result.AlreadyApplied && result.RollbackSucceeded && !result.Canceled, "Incorrect result.");
    fixture.AssertNew(); fixture.AssertPreserved();
    Check(File.Exists(fixture.HelperPath) && File.Exists(fixture.Archive), "Running helper stage must not be deleted.");
    Check(!Directory.EnumerateDirectories(fixture.Target, ".hm-appkit-update-*").Any(), "Backup directory remained after success.");
    Check(!File.Exists(Path.Combine(fixture.Target, ".hm-appkit-update.lock")), "Apply lock was not closed/deleted.");
});
await Test("Repeated successful apply verifies installation and makes no further changes", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    await fixture.Helper().ApplyAsync(manifest);
    var lastWrite = File.GetLastWriteTimeUtc(fixture.TargetExe);
    var repeated = await fixture.Helper().ApplyAsync(manifest);
    Check(repeated.Success && repeated.AlreadyApplied && File.GetLastWriteTimeUtc(fixture.TargetExe) == lastWrite, "Successful retry rewrote files.");
});
await Test("Repeated pending manifest preparation supports canceled helper launch", async () =>
{
    using var fixture = new Fixture();
    var first = await fixture.Prepare();
    var contents = File.ReadAllText(first);
    var second = await fixture.Prepare();
    Check(first == second && File.ReadAllText(first) == contents, "Pending preparation was not idempotent.");
});
await Test("Unmodified distribution catalog updates with the application", async () =>
{
    using var fixture = new Fixture();
    var result = await fixture.Helper().ApplyAsync(await fixture.Prepare());
    Check(result.Success && File.ReadAllText(fixture.Catalog) == "new default catalog", "Default catalog did not update.");
});
await Test("Explicit customized catalog override survives update", async () =>
{
    using var fixture = new Fixture();
    File.WriteAllText(fixture.Catalog, "user customized catalog");
    var result = await fixture.Helper().ApplyAsync(await fixture.Prepare(preserveCatalog: true));
    Check(result.Success && File.ReadAllText(fixture.Catalog) == "user customized catalog", "Custom catalog was overwritten.");
    fixture.AssertPreserved();
});
await Test("Only catalog override may be omitted from managed update", async () =>
{
    using var fixture = new Fixture();
    await Throws(() => fixture.Current().CreateManifestAsync(fixture.Preparation() with { PreserveRelativePaths = ["HM AppKit.exe"] }));
    fixture.AssertOld();
});
await Test("Installer cache and profile paths can never be release-managed files", async () =>
{
    using var fixture = new Fixture();
    foreach (var path in new[] { "Local Installer/setup.exe", "settings.json", "Logs/current.txt", "AppData/settings.json" })
        await Throws(() => fixture.Current().CreateManifestAsync(fixture.Preparation() with { ManagedFiles = ["HM AppKit.exe", path] }));
    fixture.AssertOld(); fixture.AssertPreserved();
});
await Test("Traversal, rooted paths, streams, reserved names and duplicate files are rejected", async () =>
{
    using var fixture = new Fixture();
    foreach (var path in new[] { "../escape.exe", "docs/../../escape.md", "C:/escape.md", "docs/a.md:stream", "docs/CON.md", "docs/a. ", "docs//a.md" })
        await Throws(() => fixture.Current().CreateManifestAsync(fixture.Preparation() with { ManagedFiles = ["HM AppKit.exe", path] }));
    await Throws(() => fixture.Current().CreateManifestAsync(fixture.Preparation() with { ManagedFiles = ["HM AppKit.exe", "hm appkit.EXE"] }));
    fixture.AssertOld();
});
await Test("Preparation cannot authorize an unrelated target folder", async () =>
{
    using var fixture = new Fixture();
    await Throws(() => fixture.Current().CreateManifestAsync(fixture.Preparation() with { TargetDirectory = fixture.Root }));
    fixture.AssertOld();
});
await Test("Stage must be one owned GUID directory directly below Updates", async () =>
{
    using var fixture = new Fixture();
    await Throws(() => fixture.Current().CreateManifestAsync(fixture.Preparation() with { StageDirectory = fixture.Root }));
    await Throws(() => fixture.Current().CreateManifestAsync(fixture.Preparation() with { StageDirectory = fixture.Payload }));
    fixture.AssertOld();
});
await Test("Helper must execute from the exact staged replacement path", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    await Throws(() => fixture.Current().ApplyAsync(manifest));
    fixture.AssertOld();
});
await Test("Changed payload is rejected before any installed file changes", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    File.WriteAllText(Path.Combine(fixture.Payload, "README.md"), "corrupt staged readme");
    await Throws(() => fixture.Helper().ApplyAsync(manifest));
    fixture.AssertOld(); fixture.AssertPreserved();
});
await Test("Changed helper itself is rejected", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    File.AppendAllText(fixture.HelperPath, "corrupt");
    await Throws(() => fixture.Helper().ApplyAsync(manifest));
    fixture.AssertOld();
});
await Test("Changed release archive is rejected", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    File.AppendAllText(fixture.Archive, "corrupt");
    await Throws(() => fixture.Helper().ApplyAsync(manifest));
    fixture.AssertOld();
});
await Test("Installed changes made after preparation are preserved and stop update", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    var readme = Path.Combine(fixture.Target, "README.md");
    File.WriteAllText(readme, "edited while prompt was open");
    await Throws(() => fixture.Helper().ApplyAsync(manifest));
    Check(File.ReadAllText(fixture.TargetExe) == "old application" && File.ReadAllText(readme) == "edited while prompt was open", "Post-prompt changes were overwritten.");
});
await Test("New unexpected file at a planned new destination stops update", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    var path = Path.Combine(fixture.Target, "docs", "new.md");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "user created file");
    await Throws(() => fixture.Helper().ApplyAsync(manifest));
    Check(File.ReadAllText(path) == "user created file" && File.ReadAllText(fixture.TargetExe) == "old application", "Unexpected file was overwritten.");
});
await Test("Tampered manifest fails ownership digest validation", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    File.AppendAllText(manifest, " ");
    await Throws(() => fixture.Helper().ApplyAsync(manifest));
    fixture.AssertOld();
});
await Test("Even a rehashed manifest cannot target a different directory", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    fixture.EditManifest(node => node["TargetDirectory"] = fixture.Root);
    await Throws(() => fixture.Helper().ApplyAsync(manifest));
    fixture.AssertOld();
});
await Test("Even a rehashed manifest cannot manage traversal or cache paths", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    fixture.EditManifest(node => node["Files"]![1]!["RelativePath"] = "../escape.md");
    await Throws(() => fixture.Helper().ApplyAsync(manifest));
    fixture.AssertOld();
});
await Test("Cancellation before applying preserves all original files", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    await Throws(() => fixture.Helper().ApplyAsync(manifest, cancellationToken: canceled.Token));
    fixture.AssertOld(); fixture.AssertPreserved();
});
await Test("Cancellation during the transaction rolls back files already replaced", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    using var cancel = new CancellationTokenSource();
    var progress = new ImmediateProgress(message => { if (message == "更新 HM AppKit.exe…") cancel.Cancel(); });
    var result = await fixture.Helper().ApplyAsync(manifest, progress, cancel.Token);
    Check(!result.Success && result.Canceled && result.RollbackSucceeded, "Cancellation did not report a clean rollback.");
    fixture.AssertOld(); fixture.AssertPreserved();
    Check(!File.Exists(Path.Combine(fixture.Target, "docs", "new.md")), "Canceled update left new release file.");
});
await Test("An unexpected callback failure also rolls back a partial transaction", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    var progress = new ImmediateProgress(message => { if (message == "更新 README.md…") throw new InvalidOperationException("injected UI failure"); });
    var result = await fixture.Helper().ApplyAsync(manifest, progress);
    Check(!result.Success && result.RollbackSucceeded && result.Message.Contains("injected UI failure"), "Unexpected failure did not rollback.");
    fixture.AssertOld(); fixture.AssertPreserved();
});
await Test("A locked destination preserves originals; later retry completes rollback", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    FileStream? held = null;
    try
    {
        var progress = new ImmediateProgress(message =>
        {
            if (message == "更新 README.md…") held = new FileStream(Path.Combine(fixture.Target, "README.md"), FileMode.Open, FileAccess.Read, FileShare.None);
        });
        var result = await fixture.Helper().ApplyAsync(manifest, progress);
        Check(!result.Success && !result.RollbackSucceeded && File.ReadAllText(fixture.TargetExe) == "old application", "Locked-file failure did not preserve old executable.");
    }
    finally { held?.Dispose(); }
    var retried = await fixture.Helper().ApplyAsync(manifest);
    Check(!retried.Success && retried.RollbackSucceeded, "Rollback did not recover after lock was released.");
    fixture.AssertOld(); fixture.AssertPreserved();
});
await Test("Apply waits for exact parent identity before touching installed files", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var helper = fixture.Helper(async (identity, token) =>
    {
        Check(identity.ProcessId == Environment.ProcessId && identity.ExecutablePath == fixture.TargetExe && identity.StartTimeUtc.Kind == DateTimeKind.Utc, "Parent identity missing.");
        entered.SetResult(); await release.Task.WaitAsync(token);
    });
    var task = helper.ApplyAsync(manifest);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    fixture.AssertOld(); release.SetResult();
    Check((await task).Success, "Apply failed after parent exit.");
});
await Test("Canceled parent wait makes no filesystem changes", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    using var cancel = new CancellationTokenSource();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var helper = fixture.Helper(async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); });
    var task = helper.ApplyAsync(manifest, cancellationToken: cancel.Token);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel(); await Throws(async () => await task);
    fixture.AssertOld(); fixture.AssertPreserved();
});
await Test("Native process inspection rejects a live PID with a different executable", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    await Throws(() => fixture.NativeWaitHelper().ApplyAsync(manifest));
    fixture.AssertOld(); fixture.AssertPreserved();
});
await Test("Another update process holds an exclusive destination lock", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    using (var held = new FileStream(Path.Combine(fixture.Target, ".hm-appkit-update.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        await Throws(() => fixture.Helper().ApplyAsync(manifest));
    fixture.AssertOld();
});
await Test("An existing unrelated lock-named file is preserved", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    var path = Path.Combine(fixture.Target, ".hm-appkit-update.lock"); File.WriteAllText(path, "unknown existing file");
    await Throws(() => fixture.Helper().ApplyAsync(manifest));
    Check(File.ReadAllText(path) == "unknown existing file", "Unowned lock-named file was deleted."); fixture.AssertOld();
});
await Test("A custom unknown backup directory is never reused or removed", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    var backup = Path.Combine(fixture.Target, ".hm-appkit-update-" + Path.GetFileName(fixture.Stage));
    Directory.CreateDirectory(backup); var sentinel = Path.Combine(backup, "not-owned.txt"); File.WriteAllText(sentinel, "retain");
    await Throws(() => fixture.Helper().ApplyAsync(manifest));
    Check(File.ReadAllText(sentinel) == "retain", "Unowned backup was removed."); fixture.AssertOld();
});
await Test("Version mismatch and downgrade are rejected at preparation", async () =>
{
    using var fixture = new Fixture();
    await Throws(() => fixture.Current().CreateManifestAsync(fixture.Preparation() with { TargetVersion = "0.4.0" }));
    await Throws(() => fixture.Current().CreateManifestAsync(fixture.Preparation() with { TargetVersion = "0.7.0" }));
    fixture.AssertOld();
});
await Test("Destination reparse point introduced after preparation cannot escape target", async () =>
{
    using var fixture = new Fixture();
    var manifest = await fixture.Prepare();
    var directory = Path.Combine(fixture.Target, "docs");
    Directory.CreateSymbolicLink(directory, fixture.Outside);
    try { await Throws(() => fixture.Helper().ApplyAsync(manifest)); }
    finally { Directory.Delete(directory, recursive: false); }
    Check(!File.Exists(Path.Combine(fixture.Outside, "new.md")), "Update followed destination link."); fixture.AssertOld();
});
await Test("Staged reparse files cannot be authorized", async () =>
{
    using var fixture = new Fixture();
    var path = Path.Combine(fixture.Payload, "README.md"); File.Delete(path);
    File.CreateSymbolicLink(path, Path.Combine(fixture.Target, "README.md"));
    try { await Throws(() => fixture.Prepare()); }
    finally { File.Delete(path); }
    fixture.AssertOld();
});
await Test("Interrupted journal restores backed-up old executable before restarting", async () =>
{
    using var fixture = new Fixture();
    var manifestPath = await fixture.Prepare();
    var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
    var id = manifest["Id"]!.GetValue<string>(); var nonce = manifest["Nonce"]!.GetValue<string>();
    var work = Path.Combine(fixture.Target, ".hm-appkit-update-" + id);
    Directory.CreateDirectory(Path.Combine(work, "backup"));
    File.WriteAllText(Path.Combine(work, ".hm-appkit-update.json"), new JsonObject { ["Schema"] = 1, ["Id"] = id, ["Nonce"] = nonce, ["ManifestSha256"] = "" }.ToJsonString());
    File.Copy(fixture.TargetExe, Path.Combine(work, "backup", "HM AppKit.exe"));
    File.Copy(fixture.HelperPath, fixture.TargetExe, overwrite: true);
    File.WriteAllText(Path.Combine(fixture.Stage, "apply-state.json"), new JsonObject { ["Id"] = id, ["Nonce"] = nonce, ["State"] = "Applying" }.ToJsonString());
    var result = await fixture.Helper().ApplyAsync(manifestPath);
    Check(!result.Success && result.RollbackSucceeded, "Interrupted update was not recovered.");
    fixture.AssertOld(); fixture.AssertPreserved();
});

Console.WriteLine($"Update apply tests: {passed} passed, {failures.Count} failed.");
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception exception) { failures.Add(name + ": " + exception); Console.WriteLine("FAIL " + name + ": " + exception.Message); }
}
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task Throws(Func<Task> action)
{
    try { await action(); }
    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OperationCanceledException) { return; }
    throw new InvalidOperationException("Expected operation to reject unsafe input or fail safely.");
}

// Explicitly invoked by the release validation script. It prepares only a disposable copy of the
// application under this repository's artifacts/self-update-e2e; it does not run or alter vendor apps.
static async Task<int> NativeManifest(string[] arguments)
{
    if (arguments.Length != 6) throw new ArgumentException("Usage: --create-native-manifest STAGE TARGET VERSION SHA256 OUTPUT");
    var repository = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (repository is not null && (!File.Exists(Path.Combine(repository.FullName, "FoxDeploy.sln")) ||
        !Directory.Exists(Path.Combine(repository.FullName, ".git")))) repository = repository.Parent;
    if (repository is null) throw new InvalidOperationException("Run the native fixture from the HM AppKit repository.");
    var allowed = Path.GetFullPath(Path.Combine(repository.FullName, "artifacts", "self-update-e2e")) + Path.DirectorySeparatorChar;
    var target = Path.GetFullPath(arguments[2]);
    var output = Path.GetFullPath(arguments[5]);
    if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !output.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Native update fixture must stay below artifacts/self-update-e2e.");
    var stage = Path.GetFullPath(arguments[1]);
    var payload = Path.Combine(stage, "payload");
    var managed = Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(payload, path).Replace('\\', '/')).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    var manifest = await new UpdateApplyService(new() { CurrentExecutablePath = Path.Combine(target, "HM AppKit.exe") })
        .CreateManifestAsync(new()
        {
            StageDirectory = stage, TargetDirectory = target, TargetVersion = arguments[3], ArchiveSha256 = arguments[4], ManagedFiles = managed,
            PreserveRelativePaths = File.Exists(Path.Combine(target, "Data", "catalog.json")) ? ["Data/catalog.json"] : []
        });
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    await File.WriteAllTextAsync(output, manifest);
    Console.WriteLine(manifest);
    return 0;
}

sealed class ImmediateProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }

sealed class Fixture : IDisposable
{
    private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "HMAppKit-UpdateApplyTests");
    public string Root { get; } = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
    public string Updates => Path.Combine(Root, "profile", "HM AppKit", "Updates");
    public string Stage { get; }
    public string Target => Path.Combine(Root, "application");
    public string Payload => Path.Combine(Stage, "payload");
    public string Archive => Path.Combine(Stage, "package.zip");
    public string TargetExe => Path.Combine(Target, "HM AppKit.exe");
    public string HelperPath => Path.Combine(Payload, "HM AppKit.exe");
    public string Catalog => Path.Combine(Target, "Data", "catalog.json");
    public string Outside => Path.Combine(Root, "outside");
    private static readonly string[] Files = ["HM AppKit.exe", "README.md", "Data/catalog.json", "docs/new.md", "licenses/7zip-license.txt"];
    public Fixture()
    {
        Stage = Path.Combine(Updates, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Target); Directory.CreateDirectory(Payload); Directory.CreateDirectory(Outside);
        Write(Target, "HM AppKit.exe", "old application"); Write(Target, "README.md", "old readme");
        Write(Target, "Data/catalog.json", "old default catalog"); Write(Target, "licenses/7zip-license.txt", "old license");
        Write(Payload, "HM AppKit.exe", "new application"); Write(Payload, "README.md", "new readme");
        Write(Payload, "Data/catalog.json", "new default catalog"); Write(Payload, "docs/new.md", "new document");
        Write(Payload, "licenses/7zip-license.txt", "new license"); File.WriteAllText(Archive, "verified release archive fixture");
        Write(Target, "Local Installer/app/setup.exe", "cached installer"); Write(Target, "Local Installer/app/index.json", "cached index");
        Write(Target, "settings.json", "portable custom settings"); Write(Target, "Logs/current.log", "log sentinel");
        Write(Target, "my-own-file.txt", "unknown file"); Write(Root, "profile/HM AppKit/settings.json", "existing appdata preferences");
    }
    public UpdateApplyPreparation Preparation(bool preserveCatalog = false) => new()
    {
        StageDirectory = Stage, TargetDirectory = Target, TargetVersion = "0.6.0", ArchiveSha256 = Hash(Archive),
        ManagedFiles = Files, PreserveRelativePaths = preserveCatalog ? ["Data/catalog.json"] : []
    };
    public Task<string> Prepare(bool preserveCatalog = false) => Current().CreateManifestAsync(Preparation(preserveCatalog));
    public UpdateApplyService Current() => new(Options(TargetExe));
    public UpdateApplyService Helper(Func<UpdateApplyProcessIdentity, CancellationToken, Task>? wait = null) => new(Options(HelperPath) with
    { WaitForParentExitAsync = wait ?? ((_, _) => Task.CompletedTask) });
    public UpdateApplyService NativeWaitHelper() => new(Options(HelperPath));
    private UpdateApplyOptions Options(string executable) => new()
    {
        StagingRoot = Updates, CurrentExecutablePath = executable,
        ExecutableVersionReader = path => path.StartsWith(Payload, StringComparison.OrdinalIgnoreCase) ? "0.6.0+test" : "0.5.0"
    };
    public void EditManifest(Action<JsonNode> edit)
    {
        var manifestPath = Path.Combine(Stage, "apply.json"); var node = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        edit(node); File.WriteAllText(manifestPath, node.ToJsonString());
        var ownerPath = Path.Combine(Stage, ".hm-appkit-update.json"); var owner = JsonNode.Parse(File.ReadAllText(ownerPath))!;
        owner["ManifestSha256"] = Hash(manifestPath); File.WriteAllText(ownerPath, owner.ToJsonString());
    }
    public void AssertOld()
    {
        AssertText(TargetExe, "old application"); AssertText(Path.Combine(Target, "README.md"), "old readme");
        AssertText(Path.Combine(Target, "licenses", "7zip-license.txt"), "old license");
    }
    public void AssertNew()
    {
        AssertText(TargetExe, "new application"); AssertText(Path.Combine(Target, "README.md"), "new readme");
        AssertText(Path.Combine(Target, "licenses", "7zip-license.txt"), "new license"); AssertText(Path.Combine(Target, "docs", "new.md"), "new document");
    }
    public void AssertPreserved()
    {
        AssertText(Path.Combine(Target, "Local Installer", "app", "setup.exe"), "cached installer");
        AssertText(Path.Combine(Target, "Local Installer", "app", "index.json"), "cached index");
        AssertText(Path.Combine(Target, "settings.json"), "portable custom settings"); AssertText(Path.Combine(Target, "Logs", "current.log"), "log sentinel");
        AssertText(Path.Combine(Target, "my-own-file.txt"), "unknown file"); AssertText(Path.Combine(Root, "profile", "HM AppKit", "settings.json"), "existing appdata preferences");
    }
    public void Dispose()
    {
        var absolute = Path.GetFullPath(Root); var scope = Path.GetFullPath(TestRoot) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(scope, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(absolute), "N", out _))
            throw new InvalidOperationException("Test cleanup scope is invalid.");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, Encoding.UTF8);
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void AssertText(string path, string expected)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != expected) throw new InvalidOperationException("Unexpected content: " + path);
    }
}

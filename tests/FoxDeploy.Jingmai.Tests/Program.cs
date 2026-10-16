using System.Text;
using System.Text.Json;
using FoxDeploy.Core;

if (args is ["--prepare-only", var installerPath, var targetPath])
{
    if (!File.Exists(installerPath)) throw new FileNotFoundException("Jingmai installer payload was not found.", installerPath);
    AuthenticodeVerification.VerifyPublisher(installerPath, ["北京京东尚科信息技术有限公司"]);
    // This diagnostic mode intentionally leaves its uniquely named extraction workspace
    // for root's explicit, separate UI test. It never starts JMInstaller or the outer EXE.
    var prepared = await new JingmaiInstaller().PrepareAsync(installerPath, targetPath,
        TimeSpan.FromMinutes(30), new ImmediateProgress(Console.Error.WriteLine), CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        prepared.StageDirectory, prepared.TargetDirectory,
        Workspace = Path.GetDirectoryName(prepared.StageDirectory),
        prepared.Request.FileName, prepared.Request.Arguments,
        prepared.Request.WorkingDirectory, prepared.Request.ShowWindow, prepared.Request.ProtectInstaller
    }));
    return 0;
}

var passed = 0;
var failures = new List<string>();
var testRoot = Path.Combine(Path.GetTempPath(), "HM-AppKit-Jingmai-Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
var payload = Path.Combine(testRoot, "verified-wrapper.exe");
await File.WriteAllBytesAsync(payload, [0x4d, 0x5a]);
var target = Path.Combine(testRoot, "Target 京麦 工作台");
var stage = Path.Combine(testRoot, "Stage 中文");
try
{
    Check("native parameter uses UTF8 Base64 and one synthetic child", () =>
    {
        var request = JingmaiInstaller.CreateNativeRequest(stage, target, TimeSpan.FromMinutes(30));
        var encoded = request.Arguments.Single(argument => argument.StartsWith("/installpath=", StringComparison.Ordinal))[13..];
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        Expect(Path.GetDirectoryName(decoded) == target, "vendor cdUp must select the exact requested target");
        Expect(!Directory.Exists(decoded) && !Directory.Exists(target), "argument construction must not create fake install directories");
        Expect(request.Arguments.SequenceEqual(["/update=install", "/installpath=" + encoded, "/localzippath=" + Path.Combine(stage, "install.7z")]), "do not pass unsupported /S or encode localzippath");
    });
    Check("interactive process request opens visibly in its own stage", () =>
    {
        var request = JingmaiInstaller.CreateNativeRequest(stage, target, TimeSpan.FromMinutes(30));
        var info = SystemProcessRunner.CreateStartInfo(request);
        Expect(request.ProtectInstaller && request.ShowWindow, "protect and show the actual vendor installer");
        Expect(!info.CreateNoWindow && info.WindowStyle == System.Diagnostics.ProcessWindowStyle.Normal && !info.UseShellExecute, "show GUI without a shell");
        Expect(!info.RedirectStandardOutput && !info.RedirectStandardError, "do not make application children hold redirected GUI pipes");
        Expect(info.WorkingDirectory == stage && info.ArgumentList.Last().EndsWith("Stage 中文\\install.7z", StringComparison.Ordinal), "preserve spaces and unicode in one argument");
    });
    Check("regular process requests retain hidden default", () =>
    {
        var info = SystemProcessRunner.CreateStartInfo(new ProcessRequest("tool.exe", ["a b"], TimeSpan.FromSeconds(1)));
        Expect(info.CreateNoWindow && info.WorkingDirectory == "" && info.ArgumentList.Single() == "a b", "do not alter other process plans");
    });
    foreach (var badTarget in new[] { "relative\\Jingmai", "C:\\", "\\\\server\\share\\Jingmai", "C:\\Software\\bad\npath" })
        Check("reject target " + JsonSerializer.Serialize(badTarget), () => Throws<ArgumentException>(() => JingmaiInstaller.CreateNativeRequest(stage, badTarget, TimeSpan.FromSeconds(1))));

    var validListing = "Path = JMInstaller.exe\nSize = 661136\n\nPath = install.7z\n\nPath = platforms\\qwindows.dll\n";
    Check("valid NSIS archive listing", () => JingmaiInstaller.ValidateArchiveListing(validListing));
    foreach (var badEntry in new[] { "..\\outside.exe", "a/../../escape", "C:\\outside.exe", "\\\\server\\x", "/rooted", "file:stream", ".\\same.exe", "bad\tpath", "a\\.. \\escape", "trailing.\\file" })
        Check("reject archive path " + JsonSerializer.Serialize(badEntry), () => Throws<InvalidDataException>(() => JingmaiInstaller.ValidateArchiveListing(validListing + "Path = " + badEntry + "\n")));
    Check("reject changed package structure", () => Throws<InvalidDataException>(() => JingmaiInstaller.ValidateArchiveListing("Path = OtherInstaller.exe\n")));

    await CheckAsync("wait for real native process and clean only extraction workspace", async () =>
    {
        var workRoot = Path.Combine(testRoot, "normal-work");
        var runner = new FakeRunner(validListing, holdNative: true);
        var installation = new JingmaiInstaller(runner, workRoot).RunAsync(payload, target, TimeSpan.FromMinutes(30), null, CancellationToken.None);
        var native = await runner.NativeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Expect(!installation.IsCompleted && Directory.Exists(native.WorkingDirectory), "wait for the native GUI, retaining required stage files");
        Expect(Directory.Exists(target), "the real target must exist so QDir cdUp can prefill it");
        var sentinel = Path.Combine(target, "user-data-preserved.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        Expect(runner.Requests.Count == 3 && runner.Requests.Take(2).All(x => Path.GetFileName(x.FileName) == "7z.exe"), "never execute the NSIS bootstrapper");
        Expect(native.ProtectInstaller && native.ShowWindow && native.WorkingDirectory == Path.GetDirectoryName(native.FileName), "protect the GUI and set its dependency directory");
        runner.NativeCompleted.SetResult(new ProcessResult(0, "", ""));
        var result = await installation;
        Expect(result.ExitCode == 0 && !Directory.EnumerateFileSystemEntries(workRoot).Any(), "clean task workspace only after the actual vendor process ends");
        Expect(File.Exists(sentinel), "cleanup must preserve all target files and never claim or remove an installation");
    });
    await CheckAsync("cleanup retries a transient file lock without changing native result", async () =>
    {
        var workRoot = Path.Combine(testRoot, "transient-lock-work");
        var runner = new FakeRunner(validListing, holdNative: true);
        var installation = new JingmaiInstaller(runner, workRoot).RunAsync(payload, target, TimeSpan.FromMinutes(30), null, CancellationToken.None);
        var native = await runner.NativeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var held = File.Open(Path.Combine(native.WorkingDirectory!, "install.7z"), FileMode.Open, FileAccess.Read, FileShare.None);
        runner.NativeCompleted.SetResult(new ProcessResult(0, "", ""));
        try { await Task.Delay(150); }
        finally { held.Dispose(); }
        var result = await installation;
        Expect(result.ExitCode == 0 && !Directory.EnumerateFileSystemEntries(workRoot).Any(), "Transient lock left stage files or changed install result.");
    });
    await CheckAsync("persistent cleanup lock preserves files and reports one honest warning", async () =>
    {
        var workRoot = Path.Combine(testRoot, "persistent-lock-work");
        var runner = new FakeRunner(validListing, holdNative: true);
        var messages = new List<string>();
        var installation = new JingmaiInstaller(runner, workRoot).RunAsync(payload, target, TimeSpan.FromMinutes(30), new ImmediateProgress(messages.Add), CancellationToken.None);
        var native = await runner.NativeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var held = File.Open(Path.Combine(native.WorkingDirectory!, "install.7z"), FileMode.Open, FileAccess.Read, FileShare.None);
        runner.NativeCompleted.SetResult(new ProcessResult(0, "", ""));
        var result = await installation;
        Expect(result.ExitCode == 0 && File.Exists(held.Name) && messages.Count(message => message.Contains("已保留以免影响", StringComparison.Ordinal)) == 1,
            "Persistent lock must leave a warning and preserve the native result and locked file.");
    });
    await CheckAsync("failed extraction never launches vendor code and removes tools", async () =>
    {
        var workRoot = Path.Combine(testRoot, "failed-work");
        var runner = new FakeRunner(validListing, extractionExitCode: 2);
        await ThrowsAsync<InvalidDataException>(() => new JingmaiInstaller(runner, workRoot).RunAsync(payload, target, TimeSpan.FromMinutes(30), null, CancellationToken.None));
        Expect(runner.Requests.Count == 2 && !runner.NativeStarted.Task.IsCompleted, "do not launch the vendor after extractor failure");
        Expect(!Directory.EnumerateFileSystemEntries(workRoot).Any(), "remove failed stage and embedded tools");
    });
    await CheckAsync("invalid extracted native executable never launches", async () =>
    {
        var workRoot = Path.Combine(testRoot, "invalid-work");
        var runner = new FakeRunner(validListing, validNative: false);
        await ThrowsAsync<InvalidDataException>(() => new JingmaiInstaller(runner, workRoot).RunAsync(payload, target, TimeSpan.FromMinutes(30), null, CancellationToken.None));
        Expect(!runner.NativeStarted.Task.IsCompleted && !Directory.EnumerateFileSystemEntries(workRoot).Any(), "reject malformed native stage and clean it");
    });
    await CheckAsync("unsafe listing is rejected before extraction", async () =>
    {
        var workRoot = Path.Combine(testRoot, "unsafe-work");
        var runner = new FakeRunner(validListing + "Path = ..\\escape\n");
        await ThrowsAsync<InvalidDataException>(() => new JingmaiInstaller(runner, workRoot).RunAsync(payload, target, TimeSpan.FromMinutes(30), null, CancellationToken.None));
        Expect(runner.Requests.Count == 1 && !Directory.EnumerateFileSystemEntries(workRoot).Any(), "stop before x command and clean only task files");
    });
    await CheckAsync("pre-cancelled preparation never starts a process", async () =>
    {
        var runner = new FakeRunner(validListing);
        await ThrowsAsync<OperationCanceledException>(() => new JingmaiInstaller(runner, Path.Combine(testRoot, "cancel-work")).RunAsync(payload, target, TimeSpan.FromMinutes(30), null, new CancellationToken(true)));
        Expect(runner.Requests.Count == 0, "do not start extraction after cancellation");
    });
}
finally
{
    var absoluteTestRoot = Path.GetFullPath(testRoot);
    var tempPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
    if (!absoluteTestRoot.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absoluteTestRoot).StartsWith("HM-AppKit-Jingmai-Tests-", StringComparison.Ordinal))
        throw new IOException("Refusing to clean an unexpected test path.");
    Directory.Delete(absoluteTestRoot, recursive: true);
}
Console.WriteLine($"Jingmai adapter tests: {passed} passed, {failures.Count} failed");
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

void Check(string name, Action action)
{
    try { action(); passed++; }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); }
}
async Task CheckAsync(string name, Func<Task> action)
{
    try { await action(); passed++; }
    catch (Exception ex) { failures.Add(name + ": " + ex.Message); }
}
static void Expect(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

sealed class FakeRunner(string listing, bool holdNative = false, int extractionExitCode = 0, bool validNative = true) : IProcessRunner
{
    public List<ProcessRequest> Requests { get; } = [];
    public TaskCompletionSource<ProcessRequest> NativeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<ProcessResult> NativeCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<string>? output, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Arguments.First() == "l") return Task.FromResult(new ProcessResult(0, listing, ""));
        if (request.Arguments.First() == "x")
        {
            if (extractionExitCode != 0) return Task.FromResult(new ProcessResult(extractionExitCode, "", "test extraction failed"));
            var stage = request.Arguments.Single(argument => argument.StartsWith("-o", StringComparison.Ordinal))[2..];
            File.WriteAllBytes(Path.Combine(stage, "JMInstaller.exe"), validNative ? [0x4d, 0x5a] : [0, 0]);
            File.WriteAllBytes(Path.Combine(stage, "install.7z"), [0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c]);
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
        NativeStarted.SetResult(request);
        return holdNative ? NativeCompleted.Task : Task.FromResult(new ProcessResult(0, "", ""));
    }
}

sealed class ImmediateProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}

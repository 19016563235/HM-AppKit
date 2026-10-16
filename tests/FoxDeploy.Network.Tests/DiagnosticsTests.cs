using System.ComponentModel;
using System.Net.Http;
using System.Net.Sockets;
using FoxDeploy.Core;

public static class DiagnosticsTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("InstallUpdate logging flag defaults false and preserves the positional API", () =>
        {
            var update = new InstallUpdate("app", InstallState.Pending, "等待安装", @"C:\Diagnostics\App");
            Check(!update.IsLogged, "Ordinary progress remains eligible for UI logging.");
            var (id, state, message, target) = update;
            Check(id == "app" && state == InstallState.Pending && message == "等待安装" && target == update.TargetDirectory,
                "Adding the flag must not change existing construction or deconstruction.");
            Check((update with { IsLogged = true }).IsLogged, "The logging flag is init-only metadata.");
            return Task.CompletedTask;
        });

        await test("Engine logs a nested network failure once and continues without reinstalling", async () =>
        {
            var first = Definition("network-failure");
            var second = Definition("next-app");
            var installed = Definition("already-installed");
            const string privateUrl = "https://diagnostic-user:super-secret-password@downloads.example.test/private-installer.exe?token=signed-token-value&signature=private-signature#private-fragment";
            var exception = new HttpRequestException(HttpRequestError.ConnectionError, "下载失败 " + privateUrl,
                new IOException("Authorization: Bearer TOP_SECRET_BEARER; password=inner-password", new SocketException((int)SocketError.ConnectionReset)));
            InstallerNetwork.WithContext(exception, "安装包下载", new Uri(privateUrl));
            exception.Data["NetworkAttempts"] = 3;
            var provider = new FakePayload((app, _) => app.Id == first.Id
                ? Task.FromException<PreparedInstaller>(exception)
                : Task.FromResult(new PreparedInstaller(@"C:\Diagnostics\fake-payload.exe")));
            var runner = new FakeRunner();
            var probe = new FakeProbe(app => app.Id == installed.Id
                ? [new InstalledApplication(app.Name, @"C:\Diagnostics\Existing", "test-only-record")]
                : []);
            var result = await RunAsync([first, second, installed], provider, runner, probe, skipInstalled: true);
            var failed = FinalFor(result, first);
            Check(failed.State == InstallState.Failed && failed.IsLogged, "The failed state must declare its existing detailed log.");
            Check(failed.Message.Contains("安装包下载", StringComparison.Ordinal), "The UI retains a short readable failure stage.");
            var detail = result.EngineLogs.Single(line => line.StartsWith($"[{first.Name}]", StringComparison.Ordinal));
            Check(detail.Contains(nameof(HttpRequestException), StringComparison.Ordinal) && detail.Contains(nameof(SocketException), StringComparison.Ordinal)
                && detail.Contains("HResult=", StringComparison.Ordinal) && detail.Contains("NativeError=", StringComparison.Ordinal),
                "The diagnostic must retain the HTTP/socket exception chain and numeric codes.");
            Check(detail.Contains("安装包下载", StringComparison.Ordinal) && detail.Contains("downloads.example.test", StringComparison.Ordinal),
                "The request stage and safe host must be available for cross-machine troubleshooting.");
            Check(result.CombinedLogs.Count(line => line.Contains(nameof(SocketException), StringComparison.Ordinal)) == 1,
                "The engine and simulated UI must produce one detailed failure entry, not two.");
            Check(!result.CombinedLogs.Any(line => line == $"[{first.Name}] {failed.Message}"),
                "The UI must not append the already logged short failure a second time.");
            Check(FinalFor(result, second).State == InstallState.Unverified && runner.Requests.Count == 1,
                "The next app is executed exactly once; a fake reboot result remains unverified.");
            Check(FinalFor(result, installed).State == InstallState.LocationMismatch && !FinalFor(result, installed).IsLogged,
                "Already-installed skipping retains a visible directory mismatch as a normal loggable progress event.");
            Check(provider.AppIds.SequenceEqual([first.Id, second.Id]), "Installed apps must not download, and failed apps must not start a second installer.");
            Check(result.Updates.Where(update => update.State is InstallState.Pending or InstallState.Checking or InstallState.Installing or InstallState.AlreadyInstalled or InstallState.LocationMismatch)
                .All(update => !update.IsLogged), "Normal business progress must not be suppressed by the error deduplication flag.");
            RejectSecrets(string.Join('\n', result.CombinedLogs.Concat(result.Updates.Select(update => update.Message))));
        });

        await test("Unsupported installation retains its reason and produces one skipped diagnostic", async () =>
        {
            var app = Definition("unsupported");
            const string reason = "此安装包不支持指定安装目录，请取消静默模式后重试。";
            var provider = new FakePayload((_, _) => Task.FromException<PreparedInstaller>(new NotSupportedException(reason)));
            var runner = new FakeRunner();
            var result = await RunAsync([app], provider, runner);
            var update = FinalFor(result, app);
            Check(update.State == InstallState.Skipped && update.IsLogged && update.Message == reason,
                "Unsupported installations must remain skipped with their readable reason.");
            Check(result.EngineLogs.Count == 1 && result.EngineLogs[0].Contains(reason, StringComparison.Ordinal),
                "The detailed log must preserve the safe internal reason once.");
            Check(result.CombinedLogs.Count(line => line.Contains(reason, StringComparison.Ordinal)) == 1 && runner.Requests.Count == 0,
                "There must be no duplicate skipped message or installer invocation.");
        });

        await test("Elevation failure keeps the friendly message and is not duplicated or rerun", async () =>
        {
            var app = Definition("requires-elevation");
            var provider = new FakePayload((_, _) => Task.FromResult(new PreparedInstaller(@"C:\Diagnostics\fake-payload.exe")));
            var runner = new FakeRunner((_, _) => Task.FromException<ProcessResult>(new Win32Exception(740)));
            var result = await RunAsync([app], provider, runner);
            var update = FinalFor(result, app);
            Check(update.State == InstallState.Failed && update.IsLogged && update.Message.Contains("管理员权限", StringComparison.Ordinal),
                "UAC 740 must retain the existing failed state and friendly administrator guidance.");
            Check(result.EngineLogs.Count(line => line.Contains("管理员权限", StringComparison.Ordinal)) == 1
                && result.CombinedLogs.Count(line => line.Contains("管理员权限", StringComparison.Ordinal)) == 1,
                "The friendly elevation error must be recorded once even when the UI observes progress.");
            Check(result.EngineLogs.Any(line => line.Contains(nameof(Win32Exception), StringComparison.Ordinal) && line.Contains("HResult=", StringComparison.Ordinal)),
                "The elevation diagnostic should retain its exception code.");
            Check(runner.Requests.Count == 1 && provider.AppIds.Count == 1, "Installer execution must not be retried on elevation errors.");
        });

        await test("Local validation failures preserve safe reasons while removing credentials", async () =>
        {
            var app = Definition("invalid-payload");
            var exception = new InvalidDataException("安装包 SHA-256 校验失败，已拒绝安装。 token=signed-token-value\nhttps://diagnostic-user:super-secret-password@downloads.example.test/private-installer.exe?signature=private-signature");
            var provider = new FakePayload((_, _) => Task.FromException<PreparedInstaller>(exception));
            var runner = new FakeRunner();
            var result = await RunAsync([app], provider, runner);
            var update = FinalFor(result, app);
            Check(update.State == InstallState.Failed && update.IsLogged && update.Message.Contains("SHA-256 校验失败", StringComparison.Ordinal),
                "A security validation failure must remain failed and readable, rather than being described as a network retry.");
            Check(result.EngineLogs.Count == 1 && result.EngineLogs[0].Contains("SHA-256 校验失败", StringComparison.Ordinal),
                "One detailed failure must retain the validation reason.");
            Check(runner.Requests.Count == 0 && provider.AppIds.Count == 1, "An invalid installer must never execute or be prepared twice.");
            RejectSecrets(string.Join('\n', result.CombinedLogs.Concat(result.Updates.Select(update => update.Message))));
        });

        await test("User cancellation remains a normal cancelled event and stops later preparation", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var first = Definition("cancel-first");
            var second = Definition("cancel-next");
            var provider = new FakePayload((_, token) =>
            {
                cancellation.Cancel();
                return Task.FromException<PreparedInstaller>(new OperationCanceledException(token));
            });
            var runner = new FakeRunner();
            var result = await RunAsync([first, second], provider, runner, cancellationToken: cancellation.Token);
            Check(FinalFor(result, first).State == InstallState.Cancelled && FinalFor(result, second).State == InstallState.Cancelled,
                "Cancellation must not become a failed or skipped network diagnosis.");
            Check(result.Updates.Where(update => update.State == InstallState.Cancelled).All(update => !update.IsLogged),
                "Cancellation state messages remain eligible for the ordinary UI log.");
            Check(provider.AppIds.SequenceEqual([first.Id]) && runner.Requests.Count == 0, "Cancellation must prevent subsequent preparation and all fake installer starts.");
            Check(result.EngineLogs.Count == 0 && result.CombinedLogs.Count(line => line.Contains("已停止，未启动安装", StringComparison.Ordinal)) == 2,
                "Distinct cancellation events for the two applications must both be retained.");
        });
    }

    private static AppDefinition Definition(string id) => new()
    {
        Id = id,
        Name = "测试应用 " + id,
        FolderName = id,
        OnlineInstaller = new OnlineInstallerDefinition
        {
            DownloadUrl = "https://downloads.example.test/setup.exe",
            AllowedDownloadHosts = ["downloads.example.test"],
            AllowedPublishers = ["Test Publisher"],
            SilentSupported = true,
            LocationSupported = true
        }
    };

    private static async Task<Scenario> RunAsync(IReadOnlyList<AppDefinition> apps, FakePayload provider, FakeRunner runner,
        FakeProbe? probe = null, bool skipInstalled = false, CancellationToken cancellationToken = default)
    {
        var updates = new List<InstallUpdate>();
        var engineLogs = new List<string>();
        var combinedLogs = new List<string>();
        var engine = new DeploymentEngine(runner, probe ?? new FakeProbe(_ => []), "fake-winget.exe",
            () => [new DriveSnapshot("C", @"C:\", true, 1024L * 1024 * 1024, DriveType.Fixed)], payloadProvider: provider);
        await engine.RunAsync(apps.Select(app => new InstallRequest(app, @"C:\Diagnostics\Apps\" + app.FolderName)).ToArray(),
            new DeploymentSettings { SkipInstalled = skipInstalled },
            new ImmediateProgress<InstallUpdate>(update =>
            {
                updates.Add(update);
                // Mirror the actual UI's IsLogged contract without creating a window or executing an installer.
                if (!update.IsLogged) combinedLogs.Add($"[{apps.Single(app => app.Id == update.AppId).Name}] {update.Message}");
            }),
            new ImmediateProgress<string>(line => { engineLogs.Add(line); combinedLogs.Add(line); }), cancellationToken);
        return new Scenario(updates, engineLogs, combinedLogs);
    }

    private static InstallUpdate FinalFor(Scenario scenario, AppDefinition app) => scenario.Updates.Last(update => update.AppId == app.Id);
    private static void RejectSecrets(string text)
    {
        foreach (var secret in new[] { "diagnostic-user", "super-secret-password", "signed-token-value", "private-signature", "private-installer.exe", "private-fragment", "TOP_SECRET_BEARER", "inner-password" })
            Check(!text.Contains(secret, StringComparison.Ordinal), "Sensitive URL or credential text must not be logged: " + secret);
        Check(!text.Contains("https://", StringComparison.OrdinalIgnoreCase), "Only safe hosts, not full URLs, belong in failure diagnostics.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed record Scenario(List<InstallUpdate> Updates, List<string> EngineLogs, List<string> CombinedLogs);
    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
    private sealed class FakePayload(Func<AppDefinition, CancellationToken, Task<PreparedInstaller>> prepare) : IInstallerPayloadProvider
    {
        public List<string> AppIds { get; } = [];
        public Task<PreparedInstaller> PrepareAsync(AppDefinition app, DeploymentSettings settings, IProgress<string> log, CancellationToken cancellationToken)
        {
            AppIds.Add(app.Id);
            return prepare(app, cancellationToken);
        }
    }
    private sealed class FakeRunner(Func<ProcessRequest, CancellationToken, Task<ProcessResult>>? run = null) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<string>? output, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return run?.Invoke(request, cancellationToken) ?? Task.FromResult(new ProcessResult(3010, "", ""));
        }
    }
    private sealed class FakeProbe(Func<AppDefinition, IReadOnlyList<InstalledApplication>> find) : IInstallationProbe
    {
        public IReadOnlyList<InstalledApplication> FindInstalled(AppDefinition app) => find(app);
    }
}

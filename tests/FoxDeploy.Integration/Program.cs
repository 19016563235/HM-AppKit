using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FoxDeploy.Core;
using Microsoft.Win32;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine("HM-AutoSetup integration harness\n  --snapshot-host --catalog FILE --output DIRECTORY\n  --download-only --catalog FILE --output DIRECTORY [--host-snapshot FILE] [--apps id,id] [--retain-payloads]\n  --install --allow-isolated-install --catalog FILE --output DIRECTORY --host-snapshot FILE --guest-marker FILE [--apps id,id]\n  --install-host --allow-host-install --catalog FILE --output DIRECTORY --host-snapshot FILE --apps SINGLE_ID\n--install requires Windows Sandbox. --install-host performs a real non-isolated host installation only with its separate explicit gate. Download-only never starts an installer.");
                return 0;
            }
            var mode = args[0];
            var catalogPath = Required(args, "--catalog");
            var output = Path.GetFullPath(Required(args, "--output"));
            var catalog = AppCatalog.Load(catalogPath);
            if (mode == "--snapshot-host")
            {
                Directory.CreateDirectory(output);
                var probe = new RegistryInstallationProbe();
                var snapshot = new HostSnapshot(Environment.MachineName, Environment.UserName, DateTimeOffset.Now,
                    catalog.Select(app => new HostApp(app.Id, app.Name, probe.FindInstalled(app).ToArray())).ToArray(), ReadRegistry(), await ReadSystemInventoryAsync());
                WriteJson(Path.Combine(output, "host-installed.json"), snapshot);
                foreach (var app in snapshot.Apps) Console.WriteLine($"{app.Id}: {(app.Records.Length > 0 ? "already installed; will skip" : "not detected")}");
                return 0;
            }
            if (mode is not ("--download-only" or "--install" or "--install-host")) throw new ArgumentException("Unknown operation.");
            var hostPath = Optional(args, "--host-snapshot");
            var host = hostPath is null ? null : JsonSerializer.Deserialize<HostSnapshot>(File.ReadAllText(hostPath), Json)
                ?? throw new InvalidDataException("Host snapshot is empty.");
            if (mode == "--install") ValidateGuest(args, host);
            var requestedIds = Optional(args, "--apps")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (requestedIds is not null && requestedIds.Any(id => !catalog.Any(app => app.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
                throw new ArgumentException("Unknown app ID in --apps.");
            if (mode == "--install-host") ValidateHost(args, host, requestedIds);
            Directory.CreateDirectory(output);
            var apps = catalog.Where(app => requestedIds is null || requestedIds.Contains(app.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
            var settings = new DeploymentSettings
            {
                SourceMode = InstallationSourceMode.Online, SelectedDrive = "E", BaseFolder = "TempSoftware",
                Silent = true, SkipInstalled = true, RequireCustomLocation = true
            };
            var report = new RunReport
            {
                Mode = mode, MachineName = Environment.MachineName, UserName = Environment.UserName,
                Started = DateTimeOffset.Now, HostMachineName = host?.MachineName,
                TargetRoot = @"E:\TempSoftware", IsIsolatedGuest = mode == "--install",
                ExecutionContext = mode == "--install-host" ? "HostInstallAuthorized; NOT isolated; uninstall must be performed separately using this run's newly added official uninstall record." : mode == "--install" ? "WindowsSandbox" : "DownloadOnly; no installer execution",
                CatalogSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))),
                RegistryBefore = ReadRegistry(), SystemBefore = await ReadSystemInventoryAsync(),
                Apps = apps.Select(app => new AppResult { Id = app.Id, Name = app.Name, RequestedDirectory = @"E:\TempSoftware\" + app.FolderName }).ToList()
            };
            var sync = new object();
            void Save() { lock (sync) WriteJson(Path.Combine(output, "results.json"), report); }
            var log = new ImmediateProgress<string>(line =>
            {
                var message = $"{DateTimeOffset.Now:O} {line}";
                lock (sync) File.AppendAllText(Path.Combine(output, "integration.log"), message + Environment.NewLine);
                Console.WriteLine(message);
            });
            var probeNow = new RegistryInstallationProbe();
            var compositeProbe = new SkipHostInstallationProbe(probeNow, host);
            var provider = new RecordingPayloadProvider(new InstallerPayloadProvider(Path.Combine(Path.GetTempPath(), "HM-AutoSetup-Integration-Cache")), output,
                report, args.Contains("--retain-payloads", StringComparer.OrdinalIgnoreCase), Save);
            Save();
            if (mode == "--download-only")
            {
                foreach (var app in apps)
                {
                    var record = report.Apps.Single(item => item.Id == app.Id);
                    record.Started = DateTimeOffset.Now;
                    record.Before = compositeProbe.FindInstalled(app).ToArray();
                    if (record.Before.Length > 0)
                    {
                        record.State = InstallState.AlreadyInstalled.ToString();
                        record.Message = "Host registry detects this app; skipped before any download or installation.";
                    }
                    else if (InstallerSources.UsesWinget(app, settings))
                    {
                        record.State = InstallState.Skipped.ToString();
                        record.Message = "This entry requires WinGet; download-only validates official direct payloads only.";
                    }
                    else
                    {
                        try
                        {
                            await using var payload = await provider.PrepareAsync(app, settings, log, CancellationToken.None);
                            record.State = "DownloadVerified";
                            record.Message = "Official online payload downloaded and publisher signature verified. Installer was not executed.";
                        }
                        catch (Exception ex)
                        {
                            record.State = InstallState.Failed.ToString(); record.Message = ex.Message; log.Report($"[{app.Name}] {ex.Message}");
                        }
                    }
                    record.Finished = DateTimeOffset.Now;
                    Save();
                }
            }
            else
            {
                var currentAppId = "";
                var progress = new ImmediateProgress<InstallUpdate>(update =>
                {
                    currentAppId = update.AppId;
                    var item = report.Apps.Single(result => result.Id == update.AppId);
                    if (update.State != InstallState.Pending)
                    {
                        item.Started ??= DateTimeOffset.Now;
                        item.Before ??= compositeProbe.FindInstalled(apps.Single(app => app.Id == update.AppId)).ToArray();
                    }
                    item.State = update.State.ToString(); item.Message = update.Message;
                    item.Updates.Add(new UpdateRecord(DateTimeOffset.Now, update));
                    if (update.State is InstallState.Succeeded or InstallState.AlreadyInstalled or InstallState.Failed or InstallState.Skipped or InstallState.Cancelled or InstallState.Unverified)
                    {
                        item.Finished = DateTimeOffset.Now;
                        item.After = probeNow.FindInstalled(apps.Single(app => app.Id == update.AppId)).ToArray();
                        InspectTarget(item);
                    }
                    Save(); log.Report($"[{item.Name}] {update.State}: {update.Message}");
                });
                var runner = new RecordingProcessRunner(new SystemProcessRunner(), () => currentAppId, report, Save);
                IReadOnlyList<DriveSnapshot> GuestDrives() => [new DriveSnapshot("E", @"E:\", true, new DriveInfo(@"C:\").AvailableFreeSpace, DriveType.Fixed)
                { HardwareVerified = true, BusType = "IsolatedGuestSubst" }];
                var engine = new DeploymentEngine(runner, compositeProbe, driveProvider: mode == "--install" ? GuestDrives : null,
                    installTimeout: TimeSpan.FromMinutes(15), payloadProvider: provider);
                await engine.RunAsync(apps.Select(app => new InstallRequest(app, @"E:\TempSoftware\" + app.FolderName)).ToArray(), settings, progress, log);
            }
            report.RegistryAfter = ReadRegistry(); report.SystemAfter = await ReadSystemInventoryAsync(); report.Finished = DateTimeOffset.Now; Save();
            foreach (var app in report.Apps) Console.WriteLine($"{app.Id}: {app.State}: {app.Message}");
            return report.Apps.Any(app => app.State is "Failed" or "Unverified" or "Cancelled") ? 2 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static void ValidateGuest(string[] args, HostSnapshot? host)
    {
        if (!args.Contains("--allow-isolated-install", StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("Install requires --allow-isolated-install.");
        if (host is null || string.IsNullOrWhiteSpace(host.MachineName) || host.MachineName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Install refused: a host snapshot from a different machine is required.");
        if (!Environment.UserName.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Install refused: this is not the Windows Sandbox user.");
        var markerPath = Path.GetFullPath(Required(args, "--guest-marker"));
        if (!markerPath.Equals(@"C:\HM-IsolatedDisk\.hm-autosetup-isolated-guest.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Install refused: unexpected guest marker path.");
        var marker = JsonSerializer.Deserialize<GuestMarker>(File.ReadAllText(markerPath), Json) ?? throw new InvalidDataException("Guest marker is invalid.");
        if (!marker.MachineName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
            !marker.HostMachineName.Equals(host.MachineName, StringComparison.OrdinalIgnoreCase) ||
            marker.Isolation != "WindowsSandbox" || marker.TargetRoot != @"E:\TempSoftware" || DateTimeOffset.Now - marker.Created > TimeSpan.FromHours(2))
            throw new InvalidOperationException("Install refused: guest marker does not match this fresh session.");
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "subst.exe")) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot inspect guest drive.");
        var mapping = process.StandardOutput.ReadToEnd(); process.WaitForExit();
        var eLine = mapping.Split('\n').FirstOrDefault(line => line.StartsWith(@"E:\:", StringComparison.OrdinalIgnoreCase));
        if (process.ExitCode != 0 || eLine is null || !eLine.TrimEnd().EndsWith(@"C:\HM-IsolatedDisk", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(@"E:\.hm-autosetup-isolated-guest.json"))
            throw new InvalidOperationException("Install refused: E: is not the guest-local substitute drive.");
        Console.WriteLine("Isolation gate passed: Windows Sandbox account, different host, fresh guest-local marker and E: substitution verified.");
    }

    private static void ValidateHost(string[] args, HostSnapshot? host, string[]? requestedIds)
    {
        if (!args.Contains("--allow-host-install", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Host install requires the separate explicit --allow-host-install flag.");
        if (host is null || !host.MachineName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Host install refused: a baseline snapshot of this host is required.");
        if (requestedIds is not { Length: 1 })
            throw new InvalidOperationException("Host install refused: --apps must select exactly one application. Verify and uninstall it before testing another.");
        if (!host.Apps.Any(app => app.Id.Equals(requestedIds[0], StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Host install refused: selected application is absent from the baseline snapshot.");
        if (Environment.UserName.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Host install mode is not for Windows Sandbox.");
        if (!DriveResolver.GetDrives().Any(drive => drive.Name.Equals("E", StringComparison.OrdinalIgnoreCase) && DriveSelectionPolicy.IsEligibleTarget(drive)))
            throw new InvalidOperationException("Host install refused: E: must be an actual ready internal drive.");
        Console.WriteLine("Explicit host installation gate passed. This run is NOT isolated. Target is fixed to E:\\TempSoftware\\<app>. Baseline-installed apps will be skipped; no automatic uninstall will run.");
    }

    private static void InspectTarget(AppResult result)
    {
        result.TargetExists = Directory.Exists(result.RequestedDirectory);
        result.EffectiveLocations = (result.After ?? []).Where(record => !string.IsNullOrWhiteSpace(record.InstallLocation)).Select(record => record.InstallLocation!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        result.RegistryLocationsMatchTarget = result.EffectiveLocations.Length > 0 && result.EffectiveLocations.All(location => InstallationLocationVerifier.IsWithinTarget(location, result.RequestedDirectory));
        if (!result.TargetExists) return;
        try
        {
            var files = Directory.EnumerateFiles(result.RequestedDirectory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(10001).ToArray();
            result.FileCountAtLeast = files.Length;
            result.FilesSample = files.Take(100).Select(path => Path.GetRelativePath(result.RequestedDirectory, path)).ToArray();
            result.ExecutableFilesSample = files.Where(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Take(30)
                .Select(path => new FileEvidence(path, new FileInfo(path).Length, FileVersionInfo.GetVersionInfo(path).FileVersion)).ToArray();
        }
        catch (IOException ex) { result.FileInspectionError = ex.Message; }
        catch (UnauthorizedAccessException ex) { result.FileInspectionError = ex.Message; }
    }

    private static RegistryEntry[] ReadRegistry()
    {
        var result = new List<RegistryEntry>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var subkey in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(subkey);
                    if (key?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                    result.Add(new RegistryEntry(hive + "/" + view + "/" + subkey, name, key.GetValue("DisplayVersion")?.ToString(), key.GetValue("InstallLocation")?.ToString(), key.GetValue("DisplayIcon")?.ToString(), key.GetValue("UninstallString")?.ToString()));
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return result.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<SystemInventory> ReadSystemInventoryAsync()
    {
        // This subprocess only reads CIM and Task Scheduler. It never invokes an
        // installer, starts a scheduled task, or changes a service configuration.
        const string script = """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
            $services = @(); $drivers = @(); $tasks = @(); $inventoryErrors = New-Object 'System.Collections.Generic.List[string]'
            try { $services = @(Get-CimInstance Win32_Service | Select-Object Name, PathName, State, StartMode | Sort-Object Name) }
            catch { $inventoryErrors.Add('Services: ' + $_.Exception.Message) }
            try { $drivers = @(Get-CimInstance Win32_SystemDriver | Select-Object Name, PathName, State, StartMode | Sort-Object Name) }
            catch { $inventoryErrors.Add('Drivers: ' + $_.Exception.Message) }
            try {
                $tasks = @(Get-ScheduledTask | ForEach-Object {
                    [pscustomobject]@{ TaskName = $_.TaskName; TaskPath = $_.TaskPath; Execute = @($_.Actions | ForEach-Object { $_.Execute } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) }
                } | Sort-Object TaskPath, TaskName)
            } catch { $inventoryErrors.Add('ScheduledTasks: ' + $_.Exception.Message) }
            [ordered]@{ CapturedAt = [DateTimeOffset]::Now.ToString('o'); Services = $services; Drivers = $drivers; ScheduledTasks = $tasks; Error = ($inventoryErrors -join '; ') } | ConvertTo-Json -Depth 6 -Compress
            """;
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start read-only system inventory query.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
            catch (TimeoutException)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return new SystemInventory(DateTimeOffset.Now, [], [], [], "Read-only inventory query exceeded 45 seconds.");
            }
            var output = await stdout; var error = await stderr;
            if (process.ExitCode != 0) return new SystemInventory(DateTimeOffset.Now, [], [], [], "Read-only inventory query failed: " + error.Trim());
            return JsonSerializer.Deserialize<SystemInventory>(output, Json) ?? new SystemInventory(DateTimeOffset.Now, [], [], [], "Read-only inventory query returned empty data.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or JsonException)
        {
            return new SystemInventory(DateTimeOffset.Now, [], [], [], ex.Message);
        }
    }

    private static string Required(string[] args, string key) => Optional(args, key) ?? throw new ArgumentException($"Missing {key}.");
    private static string? Optional(string[] args, string key)
    {
        var index = Array.FindIndex(args, value => value.Equals(key, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null;
    }
    private static void WriteJson(string path, object value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, true);
    }

    private sealed class ImmediateProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
    private sealed class SkipHostInstallationProbe(IInstallationProbe inner, HostSnapshot? host) : IInstallationProbe
    {
        public IReadOnlyList<InstalledApplication> FindInstalled(AppDefinition app)
        {
            var hostRecords = host?.Apps.FirstOrDefault(entry => entry.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase))?.Records;
            return hostRecords is { Length: > 0 } ? hostRecords : inner.FindInstalled(app);
        }
    }
    private sealed class RecordingPayloadProvider(IInstallerPayloadProvider inner, string output, RunReport report, bool retain, Action save) : IInstallerPayloadProvider
    {
        public async Task<PreparedInstaller> PrepareAsync(AppDefinition app, DeploymentSettings settings, IProgress<string> log, CancellationToken cancellationToken)
        {
            var payload = await inner.PrepareAsync(app, settings, log, cancellationToken);
            try
            {
                var info = new FileInfo(payload.FilePath);
                await using var input = File.OpenRead(payload.FilePath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
                var version = FileVersionInfo.GetVersionInfo(payload.FilePath);
                string? retained = null;
                if (retain)
                {
                    var directory = Path.Combine(output, "verified-payloads", app.Id); Directory.CreateDirectory(directory);
                    retained = Path.Combine(directory, info.Name); File.Copy(payload.FilePath, retained, true);
                }
                report.Apps.Single(item => item.Id == app.Id).Payload = new PayloadEvidence(info.Name, info.Length, hash, version.FileVersion, version.ProductVersion,
                    AuthenticodeVerification.VerifyPublisher(payload.FilePath, app.OnlineInstaller!.AllowedPublishers), app.OnlineInstaller.MetadataUrl, app.OnlineInstaller.DownloadUrl, retained, DateTimeOffset.Now);
                save(); return payload;
            }
            catch { await payload.DisposeAsync(); throw; }
        }
    }
    private sealed class RecordingProcessRunner(IProcessRunner inner, Func<string> currentApp, RunReport report, Action save) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, IProgress<string>? output, CancellationToken cancellationToken)
        {
            var appId = currentApp();
            var item = report.Apps.SingleOrDefault(app => app.Id == appId);
            var evidence = new ProcessEvidence { FileName = request.FileName, Arguments = request.Arguments.ToArray(), Started = DateTimeOffset.Now, ProtectInstaller = request.ProtectInstaller, UnquotedFinalArgument = request.UnquotedFinalArgument };
            item?.Processes.Add(evidence); save();
            var result = await inner.RunAsync(request, output, cancellationToken);
            evidence.Finished = DateTimeOffset.Now; evidence.Result = result; save(); return result;
        }
    }
    private sealed record HostSnapshot(string MachineName, string UserName, DateTimeOffset Created, HostApp[] Apps, RegistryEntry[] Registry, SystemInventory? SystemInventory = null);
    private sealed record SystemInventory(DateTimeOffset CapturedAt, ServiceInventoryEntry[] Services, ServiceInventoryEntry[] Drivers, ScheduledTaskEntry[] ScheduledTasks, string? Error = null);
    private sealed record ServiceInventoryEntry(string Name, string? PathName, string? State, string? StartMode);
    private sealed record ScheduledTaskEntry(string TaskName, string TaskPath, string[] Execute);
    private sealed record HostApp(string Id, string Name, InstalledApplication[] Records);
    private sealed record GuestMarker(string MachineName, string HostMachineName, string Isolation, string TargetRoot, DateTimeOffset Created);
    private sealed record RegistryEntry(string Key, string DisplayName, string? DisplayVersion, string? InstallLocation, string? DisplayIcon, string? UninstallString);
    private sealed record UpdateRecord(DateTimeOffset Timestamp, InstallUpdate Update);
    private sealed record FileEvidence(string Path, long Bytes, string? FileVersion);
    private sealed record PayloadEvidence(string FileName, long Bytes, string Sha256, string? FileVersion, string? ProductVersion, string VerifiedPublisher, string MetadataUrl, string DownloadUrl, string? RetainedPath, DateTimeOffset VerifiedAt);
    private sealed class RunReport
    {
        public string Mode { get; init; } = "";
        public string MachineName { get; init; } = "";
        public string UserName { get; init; } = "";
        public string? HostMachineName { get; init; }
        public string TargetRoot { get; init; } = "";
        public bool IsIsolatedGuest { get; init; }
        public string ExecutionContext { get; init; } = "";
        public string CatalogSha256 { get; init; } = "";
        public DateTimeOffset Started { get; init; }
        public DateTimeOffset? Finished { get; set; }
        public RegistryEntry[] RegistryBefore { get; init; } = [];
        public RegistryEntry[] RegistryAfter { get; set; } = [];
        public SystemInventory? SystemBefore { get; init; }
        public SystemInventory? SystemAfter { get; set; }
        public List<AppResult> Apps { get; init; } = [];
    }
    private sealed class AppResult
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string RequestedDirectory { get; init; } = "";
        public string State { get; set; } = "Pending";
        public string Message { get; set; } = "";
        public DateTimeOffset? Started { get; set; }
        public DateTimeOffset? Finished { get; set; }
        public InstalledApplication[]? Before { get; set; }
        public InstalledApplication[]? After { get; set; }
        public bool TargetExists { get; set; }
        public bool RegistryLocationsMatchTarget { get; set; }
        public string[] EffectiveLocations { get; set; } = [];
        public int FileCountAtLeast { get; set; }
        public string[] FilesSample { get; set; } = [];
        public FileEvidence[] ExecutableFilesSample { get; set; } = [];
        public string? FileInspectionError { get; set; }
        public PayloadEvidence? Payload { get; set; }
        public List<UpdateRecord> Updates { get; } = [];
        public List<ProcessEvidence> Processes { get; } = [];
    }
    private sealed class ProcessEvidence
    {
        public string FileName { get; init; } = "";
        public string[] Arguments { get; init; } = [];
        public bool ProtectInstaller { get; init; }
        public bool UnquotedFinalArgument { get; init; }
        public DateTimeOffset Started { get; init; }
        public DateTimeOffset? Finished { get; set; }
        public ProcessResult? Result { get; set; }
    }
}

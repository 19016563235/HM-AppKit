using System.Security.Cryptography;
using System.Text;
using FoxDeploy.Core;

internal static class PayloadValidationTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await ToDeskMetadataTests.RunAsync(test);
        await test("Official download URLs reject insecure or lookalike hosts", () =>
        {
            foreach (var value in new[] { "http://official.example/app.exe", "https://official.example.evil.test/app.exe", "https://official.example:8443/app.exe", "https://user:pw@official.example/app.exe", "file:///C:/app.exe" })
                Throws<InvalidDataException>(() => InstallerPayloadProvider.ValidateAllowedUri(value, ["official.example"]));
            return Task.CompletedTask;
        });
        await test("Version metadata resolves escaped JSON Windows URL", () =>
        {
            var definition = new OnlineInstallerDefinition { MetadataUrl = "https://official.example/latest", AllowedDownloadHosts = ["official.example", "cdn.example"], UrlPattern = "\"windowsUrl\"\\s*:\\s*\"(?<url>[^\"]+)\"" };
            var result = InstallerPayloadProvider.ParseDownloadUri(definition, "{\"windowsUrl\":\"https:\\/\\/cdn.example\\/setup.exe\"}");
            Equal("https://cdn.example/setup.exe", result.AbsoluteUri);
            return Task.CompletedTask;
        });
        await test("Version metadata resolves relative YAML path to official host", () =>
        {
            var definition = new OnlineInstallerDefinition { MetadataUrl = "https://official.example/update/latest.yml", AllowedDownloadHosts = ["official.example"], UrlPattern = "(?m)^path:\\s*(?<url>[^\\r\\n]+)" };
            Equal("https://official.example/update/setup.exe", InstallerPayloadProvider.ParseDownloadUri(definition, "version: 2.0\npath: setup.exe\n").AbsoluteUri);
            return Task.CompletedTask;
        });
        await test("Changed or off-domain metadata does not start a download", () =>
        {
            var definition = new OnlineInstallerDefinition { MetadataUrl = "https://official.example/latest", AllowedDownloadHosts = ["official.example"], UrlPattern = "url=(?<url>https://\\S+)" };
            Throws<InvalidDataException>(() => InstallerPayloadProvider.ParseDownloadUri(definition, "no current URL"));
            Throws<InvalidDataException>(() => InstallerPayloadProvider.ParseDownloadUri(definition, "url=https://evil.test/app.exe"));
            return Task.CompletedTask;
        });
        await test("Unsigned downloaded executable cannot pass publisher verification", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "HM-Unsigned-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                File.WriteAllText(path, "This is a test fixture, never executable.");
                Throws<InvalidDataException>(() => AuthenticodeVerification.VerifyPublisher(path, ["Known Publisher"]));
            }
            finally { File.Delete(path); }
            return Task.CompletedTask;
        });
        await test("Local-only automatic payload retains verified copy beside the program and preserves original", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "HM-Payload-" + Guid.NewGuid().ToString("N"));
            var source = Path.Combine(root, "source");
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(source);
            var bytes = Encoding.UTF8.GetBytes("test data that must never be executed");
            var file = Path.Combine(source, "fixture.exe");
            await File.WriteAllBytesAsync(file, bytes);
            var app = new AppDefinition { Id = "fixture", Name = "Fixture", FolderName = "Fixture", LocalInstaller = new LocalInstallerDefinition { FileName = "fixture.exe", Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } };
            var options = new DeploymentSettings { SourceMode = InstallationSourceMode.Local, LocalInstallerDirectory = source };
            string staged;
            using var provider = new InstallerPayloadProvider(cache, source);
            await using (var payload = await provider.PrepareAsync(app, options, new QuietProgress(), default))
            {
                staged = payload.FilePath;
                if (staged == file || !File.Exists(staged)) throw new Exception("Payload must be staged separately.");
                var stagedBytes = await File.ReadAllBytesAsync(staged);
                if (!bytes.SequenceEqual(stagedBytes)) throw new Exception("Staged bytes differ.");
            }
            if (!File.Exists(staged) || !File.Exists(file) || Directory.Exists(cache)) throw new Exception("Automatic cache must persist while original remains user-owned.");
            Directory.Delete(root, recursive: true);
        });
        await test("Modified local package hash fails before execution and cache is cleaned", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "HM-Mismatch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var source = Path.Combine(root, "fixture.exe");
            await File.WriteAllTextAsync(source, "modified");
            var cache = Path.Combine(root, "cache");
            var app = new AppDefinition { Id = "fixture", Name = "Fixture", FolderName = "Fixture", LocalInstaller = new LocalInstallerDefinition { FileName = "fixture.exe", Sha256 = new string('0', 64) } };
            var thrown = false;
            using var provider = new InstallerPayloadProvider(cache, root);
            try { await using var payload = await provider.PrepareAsync(app, new DeploymentSettings { SourceMode = InstallationSourceMode.Local, LocalInstallerDirectory = @"F:\Retired Source" }, new QuietProgress(), default); }
            catch (InvalidDataException) { thrown = true; }
            if (!thrown || Directory.Exists(cache) || Directory.EnumerateDirectories(Path.Combine(root, "fixture"), ".pending-*").Any() ||
                Directory.EnumerateDirectories(Path.Combine(root, "fixture"), "package-*").Any()) throw new Exception("Changed package must fail and clean its pending cache.");
            Directory.Delete(root, recursive: true);
        });
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Equal(string expected, string actual) { if (expected != actual) throw new Exception($"Expected {expected}, got {actual}"); }
    private sealed class QuietProgress : IProgress<string> { public void Report(string value) { } }
}

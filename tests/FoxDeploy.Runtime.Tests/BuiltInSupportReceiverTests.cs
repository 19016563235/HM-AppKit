using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using FoxDeploy.App;
using FoxDeploy.Core;

internal static class BuiltInSupportReceiverTests
{
    private const string SyntheticEndpoint = "https://open.feishu.cn/open-apis/bot/v2/hook/11111111-2222-4333-8444-555555555555";
    private const string SyntheticSecret = "HM-AppKit-synthetic-only-signature-never-real";

    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        using var fixture = new Fixture();
        byte[]? generated = null;
        await test("Private build script and runtime loader round-trip a synthetic Feishu bundle", async () =>
        {
            var result = await fixture.Generate(SyntheticConfig());
            Check(result.ExitCode == 0 && File.Exists(fixture.Bundle), "Synthetic bundle generation failed.");
            generated = File.ReadAllBytes(fixture.Bundle);
            var options = BuiltInSupportReceiver.Load(new MemoryStream(generated));
            Check(options is { Mode: SupportUploadMode.FeishuWebhook, DisplayName: "IT 支持" } &&
                options.Endpoint.AbsoluteUri == SyntheticEndpoint && options.SigningSecret == SyntheticSecret,
                "The runtime receiver differs from the private build input.");
            Check(!result.Output.Contains(SyntheticEndpoint, StringComparison.Ordinal) && !result.Output.Contains(SyntheticSecret, StringComparison.Ordinal),
                "Build diagnostics exposed synthetic private input.");
        });
        await test("Encrypted bundle contains neither synthetic webhook nor signing secret in plain text", () =>
        {
            Check(generated is not null, "Round-trip fixture failed.");
            Check(generated.Length <= BuiltInSupportReceiver.MaximumBundleBytes && generated.AsSpan(0, 5).SequenceEqual("HMFB1"u8), "Bundle framing or size is incorrect.");
            Check(generated.AsSpan().IndexOf(Encoding.UTF8.GetBytes(SyntheticEndpoint)) < 0 &&
                generated.AsSpan().IndexOf(Encoding.UTF8.GetBytes(SyntheticSecret)) < 0, "Private input is present as plain text.");
            return Task.CompletedTask;
        });
        await test("Each private bundle uses fresh random encryption material", async () =>
        {
            Check(generated is not null, "Round-trip fixture failed.");
            var result = await fixture.Generate(SyntheticConfig());
            var second = File.ReadAllBytes(fixture.Bundle);
            Check(result.ExitCode == 0 && !generated.AsSpan(5, 32).SequenceEqual(second.AsSpan(5, 32)) &&
                !generated.AsSpan(37, 12).SequenceEqual(second.AsSpan(37, 12)), "A per-build key and nonce must be newly generated.");
            return;
        });
        await test("Missing built-in resource disables upload without reading external configuration", () =>
        {
            Check(BuiltInSupportReceiver.Load() is null && BuiltInSupportReceiver.Load(null) is null, "An unconfigured test assembly must have no built-in destination.");
            return Task.CompletedTask;
        });
        await test("Corrupted authentication tag rejects the bundle without private diagnostics", () =>
        {
            Check(generated is not null, "Round-trip fixture failed.");
            var corrupt = generated.ToArray(); corrupt[49] ^= 1;
            Reject(corrupt);
            return Task.CompletedTask;
        });
        await test("Truncated, unknown-format and over-limit receiver bundles fail closed", () =>
        {
            Reject([]); Reject(new byte[65]); Reject(new byte[BuiltInSupportReceiver.MaximumBundleBytes + 1]);
            Check(generated is not null, "Round-trip fixture failed.");
            var wrongHeader = generated.ToArray(); wrongHeader[0] ^= 1; Reject(wrongHeader);
            return Task.CompletedTask;
        });
        await test("Build environment input is supported without copying a configuration file", async () =>
        {
            var result = await fixture.Generate(SyntheticConfig(), fromEnvironment: true);
            Check(result.ExitCode == 0 && BuiltInSupportReceiver.Load(new MemoryStream(File.ReadAllBytes(fixture.Bundle)))?.SigningSecret == SyntheticSecret,
                "The Actions secret environment cannot generate the embedded receiver.");
        });
        await test("Invalid private input fails with fixed diagnostics and no credential echo", async () =>
        {
            foreach (var config in new[] { "{\"SigningSecret\":\"" + SyntheticSecret + "\",bad-json", SyntheticConfig("https://attacker.test/" + SyntheticSecret) })
            {
                var result = await fixture.Generate(config);
                Check(result.ExitCode != 0 && !result.Output.Contains(SyntheticSecret, StringComparison.Ordinal) &&
                    !result.Output.Contains("attacker.test", StringComparison.Ordinal), "Build errors must not echo parser input or an endpoint.");
            }
        });
        await test("Unconfigured rebuild removes an older generated receiver", async () =>
        {
            File.WriteAllBytes(fixture.Bundle, [1, 2, 3]);
            var result = await fixture.Generate(null);
            Check(result.ExitCode == 0 && !File.Exists(fixture.Bundle), "A later unconfigured build inherited an older receiver.");
        });
    }

    private static string SyntheticConfig(string endpoint = SyntheticEndpoint) => JsonSerializer.Serialize(new
    {
        Mode = "FeishuWebhook", Endpoint = endpoint, SigningSecret = SyntheticSecret
    });

    private static void Reject(byte[] bytes)
    {
        try
        {
            BuiltInSupportReceiver.Load(new MemoryStream(bytes));
            throw new InvalidOperationException("Invalid encrypted bundle was accepted.");
        }
        catch (InvalidDataException ex)
        {
            Check(ex.InnerException is null && !ex.ToString().Contains(SyntheticEndpoint, StringComparison.Ordinal) &&
                !ex.ToString().Contains(SyntheticSecret, StringComparison.Ordinal), "Failure diagnostics include private data or an unsafe inner exception.");
        }
    }

    private static void Check([DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "HM-AppKit-support-bundle-test-" + Guid.NewGuid().ToString("N"));
        private readonly string _script;
        internal string Bundle => Path.Combine(_directory, "receiver.bin");

        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "scripts", "New-SupportReceiverBundle.ps1"))) root = root.Parent;
            _script = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Cannot locate the build script."), "scripts", "New-SupportReceiverBundle.ps1");
        }

        internal async Task<(int ExitCode, string Output)> Generate(string? configuration, bool fromEnvironment = false)
        {
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment.Remove("HM_APPKIT_SUPPORT_CONFIG");
            foreach (var item in new[] { "-NoProfile", "-NonInteractive", "-File", _script, "-OutputPath", Bundle }) start.ArgumentList.Add(item);
            if (configuration is not null)
            {
                if (fromEnvironment) start.Environment["HM_APPKIT_SUPPORT_CONFIG"] = configuration;
                else
                {
                    var path = Path.Combine(_directory, "support.private.json");
                    File.WriteAllText(path, configuration, new UTF8Encoding(false));
                    start.ArgumentList.Add("-LocalConfigPath"); start.ArgumentList.Add(path);
                }
            }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the synthetic build test.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            return (process.ExitCode, await stdout + await stderr);
        }

        public void Dispose()
        {
            var full = Path.GetFullPath(_directory);
            if (Directory.Exists(full) && full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(full).StartsWith("HM-AppKit-support-bundle-test-", StringComparison.Ordinal) &&
                !new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(full, recursive: true);
        }
    }
}

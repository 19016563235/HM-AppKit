using System.ComponentModel;
using System.Text;
using FoxDeploy.App;
using FoxDeploy.Core;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
if (args is ["--emit-output", var codePage])
{
    var encoding = Encoding.GetEncoding(int.Parse(codePage));
    using var stdout = Console.OpenStandardOutput();
    using var stderr = Console.OpenStandardError();
    stdout.Write(encoding.GetBytes("开始安装中文\n"));
    stderr.Write(encoding.GetBytes("错误信息中文\n"));
    return 0;
}

var passed = 0;
var failures = new List<string>();
await Test("Legacy installer stdout and stderr decode as GBK", async () =>
{
    var result = await new SystemProcessRunner().RunAsync(new ProcessRequest(Environment.ProcessPath!,
        ["--emit-output", "936"], TimeSpan.FromSeconds(15)) { OutputCodePage = 936 }, null, CancellationToken.None);
    Expect(result.ExitCode == 0 && result.StandardOutput.Trim() == "开始安装中文" && result.StandardError.Trim() == "错误信息中文", "Legacy output was corrupted.");
});
await Test("Default UTF8 stdout and stderr remain unchanged", async () =>
{
    var result = await new SystemProcessRunner().RunAsync(new ProcessRequest(Environment.ProcessPath!,
        ["--emit-output", "65001"], TimeSpan.FromSeconds(15)), null, CancellationToken.None);
    Expect(result.ExitCode == 0 && result.StandardOutput.Trim() == "开始安装中文" && result.StandardError.Trim() == "错误信息中文", "UTF8 output regressed.");
});
await Test("Visible installer has no redirected pipes even with legacy encoding", () =>
{
    var start = SystemProcessRunner.CreateStartInfo(new ProcessRequest("unused.exe", [], TimeSpan.FromSeconds(1)) { ShowWindow = true, OutputCodePage = 936 });
    Expect(!start.RedirectStandardOutput && !start.RedirectStandardError, "GUI inherited redirected pipes.");
    return Task.CompletedTask;
});
await Test("Portable elevation uses runas and preserves update helper arguments exactly", () =>
{
    var arguments = new[] { "--apply-update", "F:\\HM AppKit 中文\\Updates\\test \"quoted\"\\apply.json", "", "--literal=\"x y\"" };
    var start = AdministratorStartup.CreateStartInfo(@"F:\HM AppKit\HM AppKit.exe", "", arguments, @"F:\HM AppKit\");
    Expect(start.UseShellExecute && start.Verb == "runas" && start.FileName == @"F:\HM AppKit\HM AppKit.exe", "Elevation must request the Windows UAC shell verb.");
    Expect(start.WorkingDirectory == @"F:\HM AppKit\" && start.ArgumentList.SequenceEqual(arguments) && start.Arguments.Length == 0,
        "Portable working directory or individual arguments were changed.");
    Expect(!start.RedirectStandardInput && !start.RedirectStandardOutput && !start.RedirectStandardError, "UAC cannot inherit redirected pipes.");
    return Task.CompletedTask;
});
await Test("Dotnet developer launch forwards the application DLL before application arguments", () =>
{
    var start = AdministratorStartup.CreateStartInfo(@"C:\dotnet\DOTNET.EXE", @"E:\开发 目录\HM AppKit.dll", ["--smoke-test", @"E:\Test Results\screen.png"], @"E:\开发 目录");
    Expect(start.ArgumentList.SequenceEqual(new[] { @"E:\开发 目录\HM AppKit.dll", "--smoke-test", @"E:\Test Results\screen.png" }), "The dotnet host would launch the wrong application.");
    return Task.CompletedTask;
});
await Test("Already elevated startup performs no second process launch", () =>
{
    var result = AdministratorStartup.RequestIfNeeded(true,
        () => throw new InvalidOperationException("Factory must not run when elevated."),
        _ => throw new InvalidOperationException("Administrator startup must not loop."));
    Expect(result == AdministratorStartupResult.AlreadyElevated, "Elevated process should proceed to normal startup.");
    return Task.CompletedTask;
});
await Test("Unelevated startup starts one elevated process and stops the bootstrap", () =>
{
    var expected = AdministratorStartup.CreateStartInfo(@"F:\App\HM AppKit.exe", "", ["--apply-update", @"F:\stage\apply.json"], @"F:\App");
    var calls = 0;
    var result = AdministratorStartup.RequestIfNeeded(false, () => expected,
        info => { calls++; Expect(ReferenceEquals(info, expected), "Restart information changed."); });
    Expect(calls == 1 && result == AdministratorStartupResult.Relaunched, "Bootstrap must stop after one UAC launch.");
    return Task.CompletedTask;
});
await Test("UAC cancellation stops startup without falling back to an unelevated UI", () =>
{
    var calls = 0;
    var result = AdministratorStartup.RequestIfNeeded(false,
        () => AdministratorStartup.CreateStartInfo(@"F:\App\HM AppKit.exe", "", [], @"F:\App"),
        _ => { calls++; throw new Win32Exception(1223); });
    Expect(calls == 1 && result == AdministratorStartupResult.Canceled, "Cancel must stop startup without a retry.");
    return Task.CompletedTask;
});
await Test("Elevation errors other than user cancellation remain visible to the caller", () =>
{
    try
    {
        AdministratorStartup.RequestIfNeeded(false,
            () => AdministratorStartup.CreateStartInfo(@"F:\App\HM AppKit.exe", "", [], @"F:\App"),
            _ => throw new Win32Exception(5));
        throw new InvalidOperationException("Access denied was swallowed.");
    }
    catch (Win32Exception ex) when (ex.NativeErrorCode == 5) { }
    return Task.CompletedTask;
});
await Test("Incomplete startup paths fail before UAC can launch a different process", () =>
{
    foreach (var paths in new[] { ("HM AppKit.exe", ""), (@"C:\dotnet\dotnet.exe", ""), (@"C:\dotnet\dotnet.exe", "relative.dll") })
    {
        try
        {
            AdministratorStartup.CreateStartInfo(paths.Item1, paths.Item2, [], @"F:\App");
            throw new InvalidOperationException("Incomplete startup paths were accepted.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("无法确定", StringComparison.Ordinal)) { }
    }
    return Task.CompletedTask;
});
await BuiltInSupportReceiverTests.RunAsync(Test);
Console.WriteLine($"Runtime tests: {passed} passed, {failures.Count} failed.");
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex); Console.WriteLine("FAIL " + name); }
}
void Expect(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

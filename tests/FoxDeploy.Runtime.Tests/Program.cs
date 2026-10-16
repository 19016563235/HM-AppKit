using System.Text;
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
Console.WriteLine($"Runtime tests: {passed} passed, {failures.Count} failed.");
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex); Console.WriteLine("FAIL " + name); }
}
void Expect(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

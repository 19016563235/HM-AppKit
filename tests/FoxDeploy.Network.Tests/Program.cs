var passed = 0;
var failures = new List<string>();
await PayloadNetworkTests.RunAsync(Test);
await ManifestNetworkTests.RunAsync(Test);
await DiagnosticsTests.RunAsync(Test);
Console.WriteLine($"Network tests: {passed} passed, {failures.Count} failed.");
return failures.Count == 0 ? 0 : 1;

async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name); Console.WriteLine("FAIL " + name + ": " + ex); }
}

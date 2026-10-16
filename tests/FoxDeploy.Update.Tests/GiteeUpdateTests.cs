using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FoxDeploy.Core;

internal static class GiteeUpdateTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Gitee is the default and exact release attachments supply size and identity", async () =>
        {
            using var f = new GiteeFixture();
            var release = await f.Release();
            Check(release.Source == SelfUpdateSource.Gitee && release.ReleaseId == 123 && release.PackageAssetId == 501 &&
                release.ChecksumAssetId == 502 && release.PackageBytes == f.Data.Package.Length && release.ApiSha256 is null, "Gitee identity or size missing.");
            Check(f.Requests.Count == 2 && !f.GitHubRequested, "Preferred source unnecessarily queried GitHub.");
        });
        foreach (var variation in new[] { "404", "null", "missing-version", "preview", "current", "incomplete", "duplicate", "invalid-id", "same-id", "foreign-url", "oversize", "wrong-size-type" })
            await test("Unusable Gitee metadata falls back to GitHub: " + variation, async () =>
            {
                using var f = new GiteeFixture();
                switch (variation)
                {
                    case "404": f.GiteeStatus = HttpStatusCode.NotFound; break;
                    case "null": f.NullGitee = true; break;
                    case "missing-version": f.Metadata.Remove("tag_name"); break;
                    case "preview": f.Metadata["prerelease"] = true; break;
                    case "current": f.Metadata["tag_name"] = "v0.5"; break;
                    case "incomplete": f.Attachments.RemoveAt(1); break;
                    case "duplicate": f.Attachments.Add(f.Attachments[0]!.DeepClone()); break;
                    case "invalid-id": f.Metadata["id"] = "not-an-id"; break;
                    case "same-id": f.Attachments[1]!["id"] = 501; break;
                    case "foreign-url": f.Attachments[0]!["browser_download_url"] = "https://gitee.com/attacker/HM-AppKit/releases/download/v0.6/" + SelfUpdateClient.PackageAssetName; break;
                    case "oversize": f.Attachments[0]!["size"] = 1024L * 1024 * 1024; break;
                    case "wrong-size-type": f.Attachments[0]!["size"] = "large"; break;
                }
                Check((await f.Release()).Source == SelfUpdateSource.GitHub && f.GitHubRequested, "Fallback did not discover GitHub release.");
                f.Data.AssertPreserved(); f.Data.AssertNoStages();
            });
        await test("Gitee metadata retries the complete listing operation before falling back", async () =>
        {
            using var f = new GiteeFixture(); var attempts = 0;
            f.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal) && ++attempts == 1
                ? new(HttpStatusCode.ServiceUnavailable) { RequestMessage = request } : null);
            Check((await f.Release()).Source == SelfUpdateSource.Gitee && attempts == 2 && !f.GitHubRequested, "Transient Gitee metadata failure did not recover.");
        });
        await test("Gitee forbidden response falls back without same-source retries", async () =>
        {
            using var f = new GiteeFixture(); f.GiteeStatus = HttpStatusCode.Forbidden;
            Check((await f.Release()).Source == SelfUpdateSource.GitHub && f.Requests.Count == 2, "403 was retried or fallback failed.");
        });
        await test("Gitee and GitHub ETags are isolated and cached JSON is compared to current version", async () =>
        {
            using var f = new GiteeFixture(); f.Metadata["tag_name"] = "v0.5";
            Check((await f.Release()).Source == SelfUpdateSource.GitHub, "Initial fallback failed.");
            f.GiteeStatus = HttpStatusCode.NotModified; f.GitHubStatus = HttpStatusCode.NotModified;
            Check((await f.Release()).Source == SelfUpdateSource.GitHub, "Conditional fallback failed.");
            Check(f.Requests[2].ETag == "\"gitee-release\"" && f.Requests[3].ETag == "\"github-release\"", "Cross-source ETag leakage.");
            Check(await f.Client.CheckForUpdateAsync("0.6") is null, "Cached release bypassed current version check.");
        });
        await test("Gitee 304 reuses release and attachment caches independently", async () =>
        {
            using var f = new GiteeFixture(); await f.Release();
            f.GiteeStatus = HttpStatusCode.NotModified; f.AttachmentsStatus = HttpStatusCode.NotModified;
            Check((await f.Release()).Source == SelfUpdateSource.Gitee && f.Requests[2].ETag == "\"gitee-release\"" &&
                f.Requests[3].ETag == "\"gitee-attachments\"", "Gitee metadata cache identity was lost.");
        });
        await test("A hung Gitee source leaves time for the GitHub fallback", async () =>
        {
            using var f = new GiteeFixture(checkTimeout: TimeSpan.FromSeconds(2));
            f.Override = async (request, token) =>
            {
                if (request.RequestUri!.Host != "gitee.com") return null;
                await Task.Delay(Timeout.InfiniteTimeSpan, token); return null;
            };
            Check((await f.Release()).Source == SelfUpdateSource.GitHub && f.Requests.Count == 2, "Preferred timeout consumed fallback budget.");
        });
        await test("User cancellation during Gitee check never starts GitHub fallback", async () =>
        {
            using var stop = new CancellationTokenSource(); using var f = new GiteeFixture();
            f.Override = (_, token) => { stop.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult<HttpResponseMessage?>(null); };
            await Throws<OperationCanceledException>(() => f.Client.CheckForUpdateAsync("0.5", stop.Token));
            Check(!f.GitHubRequested && f.Requests.Count == 1, "Cancellation triggered fallback.");
            f.Data.AssertNoStages(); f.Data.AssertPreserved();
        });
        await test("Gitee attachment pagination discovers exact assets on the next page", async () =>
        {
            using var f = new GiteeFixture(); var firstPage = new JsonArray();
            for (var i = 0; i < 100; i++) firstPage.Add(new JsonObject { ["id"] = 1000 + i, ["name"] = "unrelated-" + i });
            f.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.Query.Contains("page=1&", StringComparison.Ordinal)
                ? GiteeFixture.JsonResponse(request, firstPage.ToJsonString()) : null);
            Check((await f.Release()).Source == SelfUpdateSource.Gitee && f.Requests.Count == 3, "Attachment pagination was not followed.");
        });
        foreach (var lowercase in new[] { false, true })
            await test("Gitee attachment redirect verifies the package with normalized CDN filename: lowercase=" + lowercase, async () =>
            {
                using var f = new GiteeFixture { LowercaseCdnName = lowercase }; var staged = await f.Client.DownloadAndStageAsync(await f.Release());
                Check(staged.Release.Source == SelfUpdateSource.Gitee && staged.Sha256 == Hash(f.Data.Package) && f.Verifications == 1 &&
                    f.Requests.Any(r => r.Uri.Contains("/releases/123/attach_files/501/download", StringComparison.Ordinal)) &&
                    f.Requests.Any(r => r.Uri.StartsWith("https://foruda.gitee.com/attach_file/", StringComparison.Ordinal)) && !f.GitHubRequested,
                    "Verified Gitee CDN chain was not used.");
                f.Data.AssertPreserved();
            });
        await test("Gitee browser and repository-attachment redirect paths remain bound to the selected ID", async () =>
        {
            using var f = new GiteeFixture();
            f.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.StartsWith("/api/v5/", StringComparison.Ordinal) &&
                request.RequestUri.AbsolutePath.EndsWith("/download", StringComparison.Ordinal)
                ? GiteeFixture.Redirect(request, GiteeFixture.Browser(request.RequestUri.AbsolutePath.Contains("/502/", StringComparison.Ordinal))) : null);
            var staged = await f.Client.DownloadAndStageAsync(await f.Release());
            Check(File.Exists(staged.ExecutablePath) && f.Requests.Any(r => r.Uri.Contains("/attach_files/501/download/", StringComparison.Ordinal)), "Bound browser chain failed.");
        });
        foreach (var redirect in new[]
        {
            "https://foruda.gitee.com.attacker.test/attach_file/123/HM-AppKit-win-x64.zip",
            "http://foruda.gitee.com/attach_file/123/HM-AppKit-win-x64.zip",
            "https://foruda.gitee.com:444/attach_file/123/HM-AppKit-win-x64.zip",
            "https://user:secret@foruda.gitee.com/attach_file/123/HM-AppKit-win-x64.zip",
            "https://gitee.com/attacker/HM-AppKit/attach_files/501/download/HM-AppKit-win-x64.zip",
            "https://gitee.com/huanmeng-it/HM-AppKit/attach_files/999/download/HM-AppKit-win-x64.zip",
            "https://foruda.gitee.com/attach_file/123/other.zip",
            "https://foruda.gitee.com/attach_file/123/HM-AppKit-win-x64.zip?attname=other.zip",
            "https://github.com/19016563235/HM-AppKit/releases/download/v0.6/HM-AppKit-win-x64.zip"
        })
            await test("Unsafe Gitee CDN redirect stops before the next request: " + redirect.Replace("user:secret@", "[credentials]@"), async () =>
            {
                using var f = new GiteeFixture(); var release = await f.Release();
                f.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(GiteeFixture.Redirect(request, redirect));
                await Throws<InvalidDataException>(() => f.Client.DownloadAndStageAsync(release));
                Check(f.Requests.Count == 3 && !f.GitHubRequested, "Rejected redirect was followed or validation failure triggered fallback.");
                f.Data.AssertNoStages(); f.Data.AssertPreserved();
            });
        await test("Gitee network download failure restarts from GitHub same-tag metadata and its own checksum", async () =>
        {
            using var f = new GiteeFixture(); var release = await f.Release(); var failures = 0;
            f.Override = (request, _) =>
            {
                if (request.RequestUri!.Host == "foruda.gitee.com" && request.RequestUri.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal))
                { failures++; throw new HttpRequestException("network reset"); }
                return Task.FromResult<HttpResponseMessage?>(null);
            };
            var staged = await f.Client.DownloadAndStageAsync(release);
            Check(failures == 3 && staged.Release.Source == SelfUpdateSource.GitHub && f.Verifications == 1 &&
                f.Requests.Any(r => r.Uri == "https://api.github.com/repos/19016563235/HM-AppKit/releases/tags/v0.6") &&
                Directory.GetDirectories(f.Data.StagingRoot).Length == 1, "Same-version fallback did not use a clean independent stage.");
            f.Data.AssertPreserved();
        });
        await test("Download fallback cannot replace the requested release with a different tag", async () =>
        {
            using var f = new GiteeFixture(); var release = await f.Release();
            f.Data.Metadata["tag_name"] = "v0.7"; f.Data.Metadata["html_url"] = "https://github.com/19016563235/HM-AppKit/releases/tag/v0.7";
            foreach (var asset in f.Data.Assets) asset!["browser_download_url"] = asset["browser_download_url"]!.GetValue<string>().Replace("v0.6/", "v0.7/");
            f.Override = (request, _) => request.RequestUri!.Host == "foruda.gitee.com" ? throw new HttpRequestException("offline") : Task.FromResult<HttpResponseMessage?>(null);
            await Throws<InvalidDataException>(() => f.Client.DownloadAndStageAsync(release));
            Check(!f.Requests.Any(r => r.Uri.StartsWith("https://github.com/", StringComparison.Ordinal)) && f.Verifications == 0, "Different release payload was requested.");
            f.Data.AssertNoStages();
        });
        foreach (var failure in new[] { "hash", "checksum-format", "zip", "version", "disk", "tls" })
            await test("Gitee security and disk failures stop without download fallback: " + failure, async () =>
            {
                using var f = new GiteeFixture(archive: failure == "zip" ? Fixture.Archive(("../settings.json", "bad", 0)) : null);
                var release = await f.Release();
                if (failure == "hash") f.Data.Package[^1] ^= 1;
                if (failure == "checksum-format") f.Data.Checksum = Hash(f.Data.Package) + "  other.zip\n";
                if (failure == "version") f.VersionMismatch = true;
                if (failure == "disk") { Directory.CreateDirectory(Path.GetDirectoryName(f.Data.StagingRoot)!); File.WriteAllText(f.Data.StagingRoot, "occupied"); }
                if (failure == "tls") f.Override = (_, _) => throw new HttpRequestException("certificate invalid", new AuthenticationException("invalid certificate"));
                if (failure == "disk") await Throws<IOException>(() => f.Client.DownloadAndStageAsync(release));
                else if (failure == "tls") await Throws<HttpRequestException>(() => f.Client.DownloadAndStageAsync(release));
                else await Throws<InvalidDataException>(() => f.Client.DownloadAndStageAsync(release));
                Check(!f.GitHubRequested, "Unsafe/disk error was hidden by fallback."); f.Data.AssertNoStages(); f.Data.AssertPreserved();
            });
        await test("A hung Gitee download leaves a bounded budget for GitHub same-version fallback", async () =>
        {
            using var f = new GiteeFixture(downloadTimeout: TimeSpan.FromSeconds(2)); var release = await f.Release();
            f.Override = async (request, token) =>
            {
                if (request.RequestUri!.Host != "foruda.gitee.com") return null;
                await Task.Delay(Timeout.InfiniteTimeSpan, token); return null;
            };
            Check((await f.Client.DownloadAndStageAsync(release)).Release.Source == SelfUpdateSource.GitHub, "Preferred download timeout consumed all fallback time.");
            f.Data.AssertPreserved();
        });
        await test("User cancellation during Gitee download cleans the stage and does not fall back", async () =>
        {
            using var f = new GiteeFixture(); var release = await f.Release(); using var stop = new CancellationTokenSource();
            f.Override = (request, token) =>
            {
                stop.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult<HttpResponseMessage?>(null);
            };
            await Throws<OperationCanceledException>(() => f.Client.DownloadAndStageAsync(release, cancellationToken: stop.Token));
            Check(!f.GitHubRequested, "Canceled download started fallback."); f.Data.AssertNoStages(); f.Data.AssertPreserved();
        });
        foreach (var failsVerification in new[] { false, true })
            await test("Expiry of Gitee network budget cannot restart local verification: failure=" + failsVerification, async () =>
            {
                using var f = new GiteeFixture(downloadTimeout: TimeSpan.FromSeconds(4)); var release = await f.Release();
                f.BeforeVerify = () => { Thread.Sleep(TimeSpan.FromMilliseconds(2200)); if (failsVerification) throw new InvalidDataException("local verification failed"); };
                if (failsVerification)
                {
                    await Throws<InvalidDataException>(() => f.Client.DownloadAndStageAsync(release)); f.Data.AssertNoStages();
                }
                else Check((await f.Client.DownloadAndStageAsync(release)).Release.Source == SelfUpdateSource.Gitee && f.Verifications == 1, "Verified source changed.");
                Check(!f.GitHubRequested, "A local verification result was mistaken for a preferred network failure.");
                f.Data.AssertPreserved();
            });
        await test("Forged Gitee release identities are rejected before HTTP", async () =>
        {
            using var f = new GiteeFixture(); var release = await f.Release(); var before = f.Requests.Count;
            foreach (var forged in new[] { release with { ReleaseId = 0 }, release with { PackageAssetId = -1 }, release with { ChecksumAssetId = 501 }, release with { Source = (SelfUpdateSource)99 } })
                await Throws<InvalidDataException>(() => f.Client.DownloadAndStageAsync(forged));
            Check(f.Requests.Count == before, "Forged release sent HTTP.");
        });
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class GiteeFixture : IDisposable
    {
        public Fixture Data { get; }
        public SelfUpdateClient Client { get; }
        public JsonObject Metadata { get; }
        public JsonArray Attachments { get; }
        public List<RequestRecord> Requests { get; } = [];
        public HttpStatusCode GiteeStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode GitHubStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode AttachmentsStatus { get; set; } = HttpStatusCode.OK;
        public bool NullGitee { get; set; }
        public bool LowercaseCdnName { get; set; } = true;
        public bool VersionMismatch { get; set; }
        public Action? BeforeVerify { get; set; }
        public int Verifications { get; private set; }
        public bool GitHubRequested => Requests.Any(r => r.Uri.StartsWith("https://api.github.com/", StringComparison.Ordinal));
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Override { get; set; }
        public GiteeFixture(byte[]? archive = null, TimeSpan? checkTimeout = null, TimeSpan? downloadTimeout = null)
        {
            Data = new Fixture(archive);
            Metadata = new JsonObject { ["id"] = 123, ["tag_name"] = "v0.6", ["prerelease"] = false, ["body"] = "Gitee release",
                ["assets"] = new JsonArray(new JsonObject { ["name"] = "v0.6.zip", ["browser_download_url"] = "https://gitee.com/huanmeng-it/HM-AppKit/archive/refs/tags/v0.6.zip" }) };
            Attachments = new JsonArray(
                new JsonObject { ["id"] = 501, ["name"] = SelfUpdateClient.PackageAssetName, ["size"] = Data.Package.Length, ["browser_download_url"] = Browser(false) },
                new JsonObject { ["id"] = 502, ["name"] = SelfUpdateClient.ChecksumAssetName, ["size"] = Data.Checksum.Length, ["browser_download_url"] = Browser(true) });
            Client = new(new SelfUpdateOptions { StagingRoot = Data.StagingRoot, CheckTimeout = checkTimeout ?? TimeSpan.FromSeconds(20), DownloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(15) },
                new Handler(Respond), (path, version) =>
                {
                    BeforeVerify?.Invoke();
                    Verifications++; if (!File.Exists(path) || version != "0.6.0" || VersionMismatch) throw new InvalidDataException("product version mismatch");
                }, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        }
        public Task<SelfUpdateRelease> Release() => ReleaseCore();
        private async Task<SelfUpdateRelease> ReleaseCore() => await Client.CheckForUpdateAsync("0.5") ?? throw new InvalidOperationException("Missing fixture release.");
        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Requests.Add(new(uri.AbsoluteUri, request.Headers.IfNoneMatch.FirstOrDefault()?.ToString(), request.Headers.UserAgent.ToString()));
            if (Override is not null && await Override(request, token) is { } overridden) return overridden;
            if (uri.Host == "api.github.com") return JsonResponse(request, Data.Metadata.ToJsonString(), GitHubStatus, "github-release");
            if (uri.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)) return JsonResponse(request, NullGitee ? "null" : Metadata.ToJsonString(), GiteeStatus, "gitee-release");
            if (uri.AbsolutePath.EndsWith("/attach_files", StringComparison.Ordinal)) return JsonResponse(request, Attachments.ToJsonString(), AttachmentsStatus, "gitee-attachments");
            var checksum = uri.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) || uri.AbsolutePath.Contains("/502/", StringComparison.Ordinal);
            var name = checksum ? SelfUpdateClient.ChecksumAssetName : SelfUpdateClient.PackageAssetName;
            if (uri.Host == "gitee.com")
            {
                if (uri.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal))
                    return Redirect(request, $"https://gitee.com/huanmeng-it/HM-AppKit/attach_files/{(checksum ? 502 : 501)}/download/{name}");
                return Redirect(request, $"https://foruda.gitee.com/attach_file/1776922952076859751/{(LowercaseCdnName ? name.ToLowerInvariant() : name)}?token=test-signature&ts=1776923000&attname={name}");
            }
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(checksum ? Encoding.UTF8.GetBytes(Data.Checksum) : Data.Package) };
        }
        public static string Browser(bool checksum) => "https://gitee.com/huanmeng-it/HM-AppKit/releases/download/v0.6/" + (checksum ? SelfUpdateClient.ChecksumAssetName : SelfUpdateClient.PackageAssetName);
        public static HttpResponseMessage JsonResponse(HttpRequestMessage request, string json, HttpStatusCode status = HttpStatusCode.OK, string? etag = null)
        {
            var response = new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent(json) };
            if (etag is not null) response.Headers.ETag = new EntityTagHeaderValue("\"" + etag + "\"");
            return response;
        }
        public static HttpResponseMessage Redirect(HttpRequestMessage request, string location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
            response.Headers.Location = new Uri(location); return response;
        }
        public void Dispose() { Client.Dispose(); Data.Dispose(); }
    }
}

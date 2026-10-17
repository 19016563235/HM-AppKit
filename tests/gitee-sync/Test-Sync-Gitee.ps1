#requires -Version 7.2
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/Sync-Gitee.ps1')
$script:Passed = 0; $script:Failed = 0
function Assert-True([bool]$Condition, [string]$Message = 'Assertion failed.') { if (-not $Condition) { throw $Message } }
function Assert-Fails([scriptblock]$Action, [string]$MessagePattern) {
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_.Exception.Message }
    if (-not $caught) { throw 'Expected a failure.' }
    if ($MessagePattern -and $caught -notmatch $MessagePattern) { throw "Unexpected failure: $caught" }
}
function Test-Case([string]$Name, [scriptblock]$Action) {
    try { & $Action; $script:Passed++; Write-Output "PASS $Name" }
    catch { $script:Failed++; Write-Output "FAIL $Name : $($_.Exception.Message)" }
}
function New-TestZip([string]$Path, [string[]]$Entries = @('HM AppKit.exe', 'Data/catalog.json')) {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Entries) {
            $entry = $zip.CreateEntry($name)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('test package bytes') } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('HM-AppKit-Gitee-Tests-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
$originalHttp = (Get-Item Function:Invoke-HmHttp).ScriptBlock
$originalGitAttempt = (Get-Item Function:Invoke-HmGitAttempt).ScriptBlock
$originalSleep = Get-Item Function:Start-Sleep -ErrorAction SilentlyContinue
$originalGitHub = $script:HmGitHub; $originalGitee = $script:HmGitee
$originalToken = $env:GITEE_TOKEN
try {
    $packagePath = Join-Path $testRoot $script:HmPackageName
    New-TestZip $packagePath
    $checksumPath = $packagePath + '.sha256'
    $hash = Get-HmHash $packagePath
    [IO.File]::WriteAllText($checksumPath, "$hash  $script:HmPackageName`n", [Text.UTF8Encoding]::new($false))
    $asset = [pscustomobject]@{ name = $script:HmPackageName; size = (Get-Item $packagePath).Length; digest = "sha256:$hash"; browser_download_url = "https://github.com/19016563235/HM-AppKit/releases/download/v0.15/$script:HmPackageName" }
    $shaAsset = [pscustomobject]@{ name = $script:HmPackageName + '.sha256'; size = (Get-Item $checksumPath).Length; digest = 'sha256:' + (Get-HmHash $checksumPath); browser_download_url = "https://github.com/19016563235/HM-AppKit/releases/download/v0.15/$script:HmPackageName.sha256" }
    $sourceRelease = [pscustomobject]@{ tag_name = 'v0.15'; draft = $false; prerelease = $false; name = 'HM AppKit v0.15'; body = 'Original release notes.'; assets = @($asset, $shaAsset) }
    $package = [pscustomobject]@{ Release = $sourceRelease; Hash = $hash; Files = @{} }
    $package.Files[$script:HmPackageName] = [pscustomobject]@{ Path = $packagePath; Hash = $hash; Asset = $asset }
    $package.Files[$script:HmPackageName + '.sha256'] = [pscustomobject]@{ Path = $checksumPath; Hash = Get-HmHash $checksumPath; Asset = $shaAsset }

    Test-Case 'small metadata requests use a 60-second deadline' { Assert-True ((Get-HmHttpTimeoutSeconds -HasUpload $false -HasDownload $false) -eq 60) }
    Test-Case 'file uploads use a 60-minute deadline' { Assert-True ((Get-HmHttpTimeoutSeconds -HasUpload $true -HasDownload $false) -eq 3600) }
    Test-Case 'file downloads retain a 15-minute deadline' { Assert-True ((Get-HmHttpTimeoutSeconds -HasUpload $false -HasDownload $true) -eq 900) }
    Test-Case 'upload takes precedence over download when selecting the deadline' { Assert-True ((Get-HmHttpTimeoutSeconds -HasUpload $true -HasDownload $true) -eq 3600) }
    Test-Case 'official Bearer authentication attaches only to fixed-repository POSTs' {
        foreach ($path in @('/releases', '/releases/73/attach_files')) {
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, ($script:HmGiteeApi + $path))
            try {
                Set-HmGiteeAuthentication $request 'fake-token-for-offline-tests'
                Assert-True ($request.Headers.Authorization.Scheme -ceq 'Bearer')
                Assert-True ($request.Headers.Authorization.Parameter -ceq 'fake-token-for-offline-tests')
                Assert-True (-not $request.RequestUri.Query)
            } finally { $request.Dispose() }
        }
    }
    Test-Case 'Bearer destination guard rejects foreign, insecure, redirected, and query addresses' {
        foreach ($uri in @('https://evil.example/releases', 'https://gitee.com/api/v5/repos/other/repo/releases',
            'http://gitee.com/api/v5/repos/huanmeng-it/HM-AppKit/releases', 'https://gitee.com:8443/api/v5/repos/huanmeng-it/HM-AppKit/releases',
            'https://user@gitee.com/api/v5/repos/huanmeng-it/HM-AppKit/releases', ($script:HmGiteeApi + '/releases?access_token=not-real'),
            ($script:HmGiteeApi + '/releases#fragment'))) {
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, $uri)
            try {
                Assert-Fails { Set-HmGiteeAuthentication $request 'fake-token-for-offline-tests' } 'Credentials may only'
                Assert-True ($null -eq $request.Headers.Authorization)
            } finally { $request.Dispose() }
        }
    }
    Test-Case 'Bearer is never attached to anonymous verification GETs' {
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, ($script:HmGiteeApi + '/releases/73/attach_files/100/download'))
        try {
            Assert-Fails { Set-HmGiteeAuthentication $request 'fake-token-for-offline-tests' } 'Credentials may only'
            Assert-True ($null -eq $request.Headers.Authorization)
        } finally { $request.Dispose() }
    }
    Test-Case 'empty Bearer token fails without attaching an authorization header' {
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, ($script:HmGiteeApi + '/releases'))
        try {
            Assert-Fails { Set-HmGiteeAuthentication $request '' } 'GITEE_TOKEN'
            Assert-True ($null -eq $request.Headers.Authorization)
        } finally { $request.Dispose() }
    }
    Test-Case 'CI GitHub token authenticates only the fixed release metadata GETs' {
        foreach ($path in @('/releases/latest', '/releases/tags/v0.17')) {
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, ($script:HmGitHubApi + $path))
            try {
                Set-HmGitHubMetadataAuthentication $request 'fake-github-read-token'
                Assert-True ($request.Headers.Authorization.Scheme -ceq 'Bearer')
                Assert-True ($request.Headers.Authorization.Parameter -ceq 'fake-github-read-token')
            } finally { $request.Dispose() }
        }
    }
    Test-Case 'local metadata remains anonymous without a GitHub token' {
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, ($script:HmGitHubApi + '/releases/latest'))
        try {
            Set-HmGitHubMetadataAuthentication $request ''
            Assert-True ($null -eq $request.Headers.Authorization)
        } finally { $request.Dispose() }
    }
    Test-Case 'GitHub token is absent for Gitee CDNs other repos and unsafe addresses' {
        foreach ($uri in @('https://gitee.com/api/v5/repos/huanmeng-it/HM-AppKit/releases',
            'https://foruda.gitee.com/attach_file/1/package.zip', 'https://release-assets.githubusercontent.com/package.zip',
            'https://api.github.com/repos/other/repository/releases/latest',
            'https://api.github.com/repos/19016563235/HM-AppKit-other/releases/latest',
            'https://api.github.com/user', 'https://evil.example/repos/19016563235/HM-AppKit/releases/latest',
            'http://api.github.com/repos/19016563235/HM-AppKit/releases/latest',
            'https://api.github.com:8443/repos/19016563235/HM-AppKit/releases/latest',
            'https://user@api.github.com/repos/19016563235/HM-AppKit/releases/latest',
            ($script:HmGitHubApi + '/releases/latest?other=1'), ($script:HmGitHubApi + '/releases/latest#fragment'))) {
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $uri)
            try {
                $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', 'fake-github-read-token')
                Set-HmGitHubMetadataAuthentication $request 'fake-github-read-token'
                Assert-True ($null -eq $request.Headers.Authorization)
            } finally { $request.Dispose() }
        }
    }
    Test-Case 'GitHub token is never attached to writes or non-GET methods' {
        foreach ($method in @('POST', 'PUT', 'DELETE', 'HEAD')) {
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($method), ($script:HmGitHubApi + '/releases/latest'))
            try {
                Set-HmGitHubMetadataAuthentication $request 'fake-github-read-token'
                Assert-True ($null -eq $request.Headers.Authorization)
            } finally { $request.Dispose() }
        }
    }
    Test-Case 'GitHub authentication is removed on every redirect including the same API' {
        foreach ($uri in @(($script:HmGitHubApi + '/releases/tags/v0.17'), 'https://release-assets.githubusercontent.com/package.zip', 'https://gitee.com/')) {
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $uri)
            try {
                $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', 'fake-github-read-token')
                Set-HmGitHubMetadataAuthentication $request 'fake-github-read-token' -IsRedirect
                Assert-True ($null -eq $request.Headers.Authorization)
            } finally { $request.Dispose() }
        }
    }
    Test-Case 'GitHub and Gitee request credentials remain distinct' {
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, ($script:HmGiteeApi + '/releases/73/attach_files'))
        try {
            Set-HmGitHubMetadataAuthentication $request 'fake-github-read-token'
            Assert-True ($null -eq $request.Headers.Authorization)
            Set-HmGiteeAuthentication $request 'fake-gitee-write-token'
            Assert-True ($request.Headers.Authorization.Parameter -ceq 'fake-gitee-write-token')
        } finally { $request.Dispose() }
    }
    Test-Case 'published package downloads stay anonymous even with a CI GitHub token' {
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $asset.browser_download_url)
        try {
            Set-HmGitHubMetadataAuthentication $request 'fake-github-read-token'
            Assert-True ($null -eq $request.Headers.Authorization)
        } finally { $request.Dispose() }
    }
    Test-Case 'sync workflow injects its existing read token separately from Gitee credentials' {
        $workflow = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../.github/workflows/sync-gitee.yml'))
        Assert-True ($workflow -match '(?m)^\s+GITHUB_TOKEN:\s*\$\{\{\s*github\.token\s*\}\}\s*$')
        Assert-True ($workflow -match '(?m)^\s+GITEE_TOKEN:\s*\$\{\{\s*secrets\.GITEE_TOKEN\s*\}\}\s*$')
        Assert-True ($workflow -match '(?m)^permissions:\s*\r?\n\s+contents:\s+read\s*$')
        Assert-True ($workflow -notmatch '(?m)^\s+contents:\s+write\s*$')
    }
    foreach ($fileName in @($script:HmPackageName, ($script:HmPackageName + '.sha256'))) {
        Test-Case ("real multipart wire format for $fileName") {
            $payload = [Text.Encoding]::UTF8.GetBytes('original-test-file-bytes')
            $stream = [IO.MemoryStream]::new($payload, $false)
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, ($script:HmGiteeApi + '/releases/73/attach_files'))
            try {
                Set-HmGiteeAuthentication $request 'fake-token-for-offline-tests'
                $form = New-HmGiteeMultipart -FileStream $stream -FileName $fileName -AccessToken 'fake-token-for-offline-tests'
                Assert-True ($form -is [Net.Http.MultipartFormDataContent]) 'Multipart container was enumerated into parts.'
                $request.Content = $form
                $wire = $request.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                Assert-True ($wire.Contains('Content-Disposition: form-data; name="access_token"'))
                Assert-True ($wire.Contains('Content-Disposition: form-data; name="file"; filename="' + $fileName + '"'))
                Assert-True (-not $wire.Contains('filename*='))
                Assert-True ($wire.Contains('Content-Type: application/octet-stream'))
                Assert-True ($wire.Contains('original-test-file-bytes'))
                Assert-True ([regex]::Matches($wire, 'fake-token-for-offline-tests').Count -eq 1)
                Assert-True ($request.Content.Headers.ContentLength -eq [Text.Encoding]::UTF8.GetByteCount($wire))
                Assert-True ($request.Headers.Authorization.Parameter -ceq 'fake-token-for-offline-tests')
            } finally { $request.Dispose(); $stream.Dispose() }
        }
    }
    Test-Case 'multipart accepts only the two fixed ASCII release filenames' {
        $stream = [IO.MemoryStream]::new([byte[]](65))
        try { Assert-Fails { New-HmGiteeMultipart -FileStream $stream -FileName 'unsafe"name.zip' -AccessToken 'fake-token-for-offline-tests' } 'ValidateSet|does not belong|Cannot validate' }
        finally { $stream.Dispose() }
    }
    Test-Case 'unauthenticated multipart contains only the file part and owns its stream' {
        $stream = [IO.MemoryStream]::new([byte[]](65, 66, 67))
        $form = New-HmGiteeMultipart -FileStream $stream -FileName $script:HmPackageName
        try {
            $wire = $form.ReadAsStringAsync().GetAwaiter().GetResult()
            Assert-True (-not $wire.Contains('access_token'))
            Assert-True ($wire.Contains('ABC'))
        } finally { $form.Dispose() }
        Assert-True (-not $stream.CanRead)
    }
    Test-Case 'stable version tags accepted' { Assert-True (Test-HmReleaseTag 'v0.15'); Assert-True (Test-HmReleaseTag 'v1.2.3.4') }
    Test-Case 'path, branch, and prerelease tags rejected' { foreach ($v in @('main', '../main', 'v0.15-beta', 'v1', 'v0.15/other')) { Assert-True (-not (Test-HmReleaseTag $v)) } }
    Test-Case 'HTTPS CDN redirects restricted by publisher' {
        Assert-True (Test-HmDownloadRedirect 'https://github.com/a' 'https://release-assets.githubusercontent.com/a')
        Assert-True (Test-HmDownloadRedirect 'https://gitee.com/a' 'https://foruda.gitee.com/attach_file/1/a')
        foreach ($u in @('http://foruda.gitee.com/a', 'https://evil.example/a', 'https://foruda.gitee.com:8443/a', 'https://user@foruda.gitee.com/a')) { Assert-True (-not (Test-HmDownloadRedirect 'https://gitee.com/a' $u)) }
    }
    Test-Case 'token-bearing query addresses rejected before network' { Assert-Fails { Invoke-HmHttp 'https://gitee.com/api/v5/a?access_token=secret' } 'Unsafe API' }
    Test-Case 'credential transmission to another repository rejected before network' { Assert-Fails { Invoke-HmHttp 'https://gitee.com/api/v5/repos/other/repo/releases' -Method POST -Authenticated } 'Credentials may only' }
    Test-Case 'credentials never sent by GET' { Assert-Fails { Invoke-HmHttp "$script:HmGiteeApi/releases" -Authenticated } 'Credentials may only' }
    Test-Case 'remote refs parsed without annotating or deleting tags' {
        $refs = ConvertTo-HmRefs ((('a' * 40) + "`trefs/heads/main`n") + (('b' * 40) + "`trefs/tags/v0.15`n"))
        Assert-True ($refs.Count -eq 2 -and $refs['refs/tags/v0.15'] -eq ('b' * 40))
    }
    Test-Case 'unexpected Git refs rejected' { Assert-Fails { ConvertTo-HmRefs (('a' * 40) + "`trefs/heads/other") } 'Unexpected' }
    Test-Case 'duplicate Git refs rejected' { Assert-Fails { ConvertTo-HmRefs ((('a' * 40) + "`trefs/heads/main`n") * 2) } 'Duplicate' }
    Test-Case 'published package and checksum accepted' { Assert-True ((Assert-HmPackage $packagePath $checksumPath $asset) -eq $hash) }
    Test-Case 'GitHub API digest required and verified' {
        $wrong = [pscustomobject]@{ size = $asset.size; digest = 'sha256:' + ('0' * 64) }
        Assert-Fails { Assert-HmPackage $packagePath $checksumPath $wrong } 'API digest'
    }
    Test-Case 'package content length verified' {
        $wrong = [pscustomobject]@{ size = $asset.size + 1; digest = $asset.digest }
        Assert-Fails { Assert-HmPackage $packagePath $checksumPath $wrong } 'length mismatch'
    }
    Test-Case 'checksum sidecar is exact' {
        $bad = Join-Path $testRoot 'wrong.sha256'; [IO.File]::WriteAllText($bad, ('0' * 64) + '  HM-AppKit-win-x64.zip')
        Assert-Fails { Assert-HmPackage $packagePath $bad $asset } 'checksum mismatch'
    }
    foreach ($case in @(
        @{ Name = 'vendor executable excluded'; Entries = @('HM AppKit.exe', 'vendor.exe') },
        @{ Name = 'installer cache excluded'; Entries = @('HM AppKit.exe', 'Local Installer/cache.json') },
        @{ Name = 'backslash cache excluded'; Entries = @('HM AppKit.exe', 'Local Installer\cache.json') },
        @{ Name = 'traversal excluded'; Entries = @('HM AppKit.exe', '../settings.json') },
        @{ Name = 'absolute entry excluded'; Entries = @('HM AppKit.exe', 'C:/settings.json') },
        @{ Name = 'missing application rejected'; Entries = @('readme.txt') }
    )) {
        Test-Case $case.Name {
            $badZip = Join-Path $testRoot ([guid]::NewGuid().ToString('N') + '.zip')
            New-TestZip $badZip $case.Entries
            $badHash = Get-HmHash $badZip
            $badSha = $badZip + '.sha256'; [IO.File]::WriteAllText($badSha, "$badHash  $script:HmPackageName")
            $badAsset = [pscustomobject]@{ size = (Get-Item $badZip).Length; digest = "sha256:$badHash" }
            Assert-Fails { Assert-HmPackage $badZip $badSha $badAsset } 'Package'
        }
    }

    # A real local Git graph exercises ordinary atomic fast-forward pushes and conflicts.
    $sourceBare = Join-Path $testRoot 'github.git'; $targetBare = Join-Path $testRoot 'gitee.git'; $working = Join-Path $testRoot 'working'
    $null = Invoke-HmGit -Arguments @('init', '--bare', $sourceBare) -WorkingDirectory $testRoot
    $null = Invoke-HmGit -Arguments @('init', '--bare', $targetBare) -WorkingDirectory $testRoot
    $null = Invoke-HmGit -Arguments @('init', '-b', 'main', $working) -WorkingDirectory $testRoot
    $null = Invoke-HmGit -Arguments @('config', 'user.email', 'test@example.invalid') -WorkingDirectory $working
    $null = Invoke-HmGit -Arguments @('config', 'user.name', 'Offline Test') -WorkingDirectory $working
    [IO.File]::WriteAllText((Join-Path $working 'file.txt'), 'first')
    $null = Invoke-HmGit -Arguments @('add', 'file.txt') -WorkingDirectory $working
    $null = Invoke-HmGit -Arguments @('commit', '-m', 'first') -WorkingDirectory $working
    $first = (Invoke-HmGit -Arguments @('rev-parse', 'HEAD') -WorkingDirectory $working).Output.Trim()
    $null = Invoke-HmGit -Arguments @('tag', '-a', 'v0.15', '-m', 'annotated release') -WorkingDirectory $working
    $null = Invoke-HmGit -Arguments @('push', $sourceBare, 'main', '--tags') -WorkingDirectory $working
    $script:HmGitHub = $sourceBare; $script:HmGitee = $targetBare; $env:GITEE_TOKEN = 'offline-test-token-not-a-real-secret'
    Test-Case 'read-only plan does not push main or tags' {
        $result = Sync-HmGitCode $working -ReadOnly
        Assert-True ($result.ReferencesToUpdate -eq 2)
        Assert-True ((Invoke-HmGit -Arguments @('ls-remote', '--refs', $targetBare) -WorkingDirectory $working).Output.Length -eq 0)
    }
    Test-Case 'initial sync preserves annotated tag object and main' {
        $result = Sync-HmGitCode $working
        Assert-True ($result.ReferencesToUpdate -eq 2)
        $refs = ConvertTo-HmRefs (Invoke-HmGit -Arguments @('ls-remote', '--refs', $targetBare, 'refs/heads/main', 'refs/tags/*') -WorkingDirectory $working).Output
        Assert-True ($refs['refs/heads/main'] -eq $first)
        Assert-True ($refs['refs/tags/v0.15'] -eq (Invoke-HmGit -Arguments @('rev-parse', 'refs/tags/v0.15') -WorkingDirectory $working).Output.Trim())
    }
    Test-Case 'identical rerun does not push or duplicate refs' { Assert-True ((Sync-HmGitCode $working).ReferencesToUpdate -eq 0) }
    [IO.File]::WriteAllText((Join-Path $working 'file.txt'), 'second')
    $null = Invoke-HmGit -Arguments @('commit', '-am', 'second') -WorkingDirectory $working
    $second = (Invoke-HmGit -Arguments @('rev-parse', 'HEAD') -WorkingDirectory $working).Output.Trim()
    $null = Invoke-HmGit -Arguments @('tag', 'v0.16') -WorkingDirectory $working
    $null = Invoke-HmGit -Arguments @('push', $sourceBare, 'main', '--tags') -WorkingDirectory $working
    Test-Case 'fast-forward and new tag update together' { Assert-True ((Sync-HmGitCode $working).ReferencesToUpdate -eq 2) }
    Test-Case 'temporary fetched refs removed after success' { Assert-True ([string]::IsNullOrWhiteSpace((Invoke-HmGit -Arguments @('for-each-ref', 'refs/hm-gitee-sync') -WorkingDirectory $working).Output)) }
    $null = Invoke-HmGit -Arguments @('update-ref', 'refs/tags/v0.15', $second) -WorkingDirectory $targetBare
    Test-Case 'different same-name remote tag stops without force' { Assert-Fails { Sync-HmGitCode $working } 'tag differs' }
    $tagObject = (Invoke-HmGit -Arguments @('rev-parse', 'refs/tags/v0.15') -WorkingDirectory $working).Output.Trim()
    $null = Invoke-HmGit -Arguments @('update-ref', 'refs/tags/v0.15', $tagObject) -WorkingDirectory $targetBare
    $null = Invoke-HmGit -Arguments @('update-ref', 'refs/tags/gitee-only', $first) -WorkingDirectory $targetBare
    Test-Case 'independent remote tag stops without deleting it' { Assert-Fails { Sync-HmGitCode $working } 'independent reference' }
    $null = Invoke-HmGit -Arguments @('update-ref', '-d', 'refs/tags/gitee-only') -WorkingDirectory $targetBare
    $null = Invoke-HmGit -Arguments @('checkout', '-b', 'independent', $first) -WorkingDirectory $working
    [IO.File]::WriteAllText((Join-Path $working 'other.txt'), 'Gitee independent work')
    $null = Invoke-HmGit -Arguments @('add', 'other.txt') -WorkingDirectory $working
    $null = Invoke-HmGit -Arguments @('commit', '-m', 'independent') -WorkingDirectory $working
    $null = Invoke-HmGit -Arguments @('push', $targetBare, 'HEAD:refs/heads/independent') -WorkingDirectory $working
    $independent = (Invoke-HmGit -Arguments @('rev-parse', 'HEAD') -WorkingDirectory $working).Output.Trim()
    $null = Invoke-HmGit -Arguments @('update-ref', 'refs/heads/main', $independent) -WorkingDirectory $targetBare
    Test-Case 'independent remote main stops before any push' { Assert-Fails { Sync-HmGitCode $working } 'independent commits' }
    Test-Case 'temporary fetched refs removed after conflict' { Assert-True ([string]::IsNullOrWhiteSpace((Invoke-HmGit -Arguments @('for-each-ref', 'refs/hm-gitee-sync') -WorkingDirectory $working).Output)) }
    $script:HmGitHub = $originalGitHub; $script:HmGitee = $originalGitee

    Test-Case 'owned timeout process exits and output tasks drain before another attempt' {
        $info = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
        $info.UseShellExecute = $false; $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
        $info.ArgumentList.Add('-NoProfile'); $info.ArgumentList.Add('-Command'); $info.ArgumentList.Add('Start-Sleep -Seconds 60')
        $null = $info.Environment.Remove('GITEE_TOKEN')
        $owned = [Diagnostics.Process]::new(); $owned.StartInfo = $info
        try {
            $null = $owned.Start()
            $stdout = $owned.StandardOutput.ReadToEndAsync(); $stderr = $owned.StandardError.ReadToEndAsync()
            Stop-HmGitProcess $owned $stdout $stderr
            Assert-True ($owned.HasExited -and $stdout.IsCompletedSuccessfully -and $stderr.IsCompletedSuccessfully)
        } finally { $owned.Dispose() }
    }

    # Fault injection stays below the real retry policy; no network process is started.
    function Reset-GitMock {
        $script:GitCalls = 0
        $script:GitResults = [Collections.Generic.Queue[object]]::new()
        $script:GitSleeps = [Collections.Generic.List[double]]::new()
    }
    function Add-GitResult([int]$ExitCode, [string]$Diagnostic = '', [string]$Output = '', [bool]$TimedOut = $false) {
        $script:GitResults.Enqueue([pscustomobject]@{ ExitCode = $ExitCode; Output = $Output; TransientNetworkFailure = ($TimedOut -or (Test-HmGitTransientFailure $Diagnostic)); TimedOut = $TimedOut })
    }
    Set-Item Function:Start-Sleep -Value { param([double]$Seconds) $script:GitSleeps.Add($Seconds) }
    Set-Item Function:Invoke-HmGitAttempt -Value {
        param([string[]]$Arguments, [string]$WorkingDirectory, [switch]$Authenticate)
        $script:GitCalls++
        if ($script:GitResults.Count -eq 0) { throw 'Unexpected additional Git attempt.' }
        return $script:GitResults.Dequeue()
    }
    $readArgs = @('ls-remote', '--refs', $originalGitHub, 'refs/heads/main', 'refs/tags/*')
    $tempScope = 'refs/hm-gitee-sync/' + ('a' * 32)
    $fetchArgs = @('fetch', '--no-tags', $originalGitHub, "refs/heads/main:$tempScope/main", "refs/tags/*:$tempScope/tags/*")
    Test-Case 'only fixed public Git reads and temporary ref fetches qualify for retry' {
        Assert-True ((Get-HmGitReadOperation $readArgs) -ceq 'git-read-refs')
        Assert-True ((Get-HmGitReadOperation $fetchArgs) -ceq 'git-fetch-temp')
        Assert-True ((Get-HmGitReadOperation @('fetch', '--no-tags', $originalGitee, "refs/heads/main:$tempScope/gitee-main")) -ceq 'git-fetch-temp')
        foreach ($unsafe in @(
            @('ls-remote', '--refs', 'https://other.example/repo.git', 'refs/heads/main', 'refs/tags/*'),
            @('fetch', '--no-tags', $originalGitHub, 'refs/heads/main:refs/heads/main'),
            @('fetch', '--no-tags', $originalGitHub, "refs/heads/main:$tempScope/main", 'refs/tags/*:refs/tags/*'),
            @('fetch', '--no-tags', $originalGitHub, "refs/heads/main:$tempScope/main", "refs/tags/*:$tempScope/tags/*", '--prune'),
            @('push', '--atomic', $originalGitee, 'main')
        )) { Assert-True ([string]::IsNullOrEmpty((Get-HmGitReadOperation $unsafe))) }
    }
    Test-Case 'transient public ls-remote failure recovers with one bounded retry' {
        Reset-GitMock; Add-GitResult 128 'fatal: unable to access repository: Recv failure: Connection was reset'; Add-GitResult 0 '' 'verified refs'
        $result = Invoke-HmGit -Arguments $readArgs -WorkingDirectory $testRoot
        Assert-True ($result.ExitCode -eq 0 -and $result.Output -ceq 'verified refs' -and $script:GitCalls -eq 2)
        Assert-True ($script:GitSleeps.Count -eq 1 -and $script:GitSleeps[0] -eq 1)
    }
    Test-Case 'temporary namespace fetch recovers from a transient HTTP server error' {
        Reset-GitMock; Add-GitResult 128 'fatal: unable to access repository: The requested URL returned error: 503'; Add-GitResult 0
        Assert-True ((Invoke-HmGit -Arguments $fetchArgs -WorkingDirectory $testRoot).ExitCode -eq 0)
        Assert-True ($script:GitCalls -eq 2)
    }
    Test-Case 'transient Git reads stop after three attempts and short backoffs' {
        Reset-GitMock; 1..3 | ForEach-Object { Add-GitResult 128 'fatal: Could not resolve host: public.example' }
        Assert-Fails { Invoke-HmGit -Arguments $readArgs -WorkingDirectory $testRoot } 'Git operation failed'
        Assert-True ($script:GitCalls -eq 3 -and ($script:GitSleeps -join ',') -eq '1,2')
    }
    Test-Case 'read-only Git timeout may recover while preserving the existing attempt timeout' {
        Reset-GitMock; Add-GitResult -ExitCode 124 -TimedOut $true; Add-GitResult 0
        Assert-True ((Invoke-HmGit -Arguments $readArgs -WorkingDirectory $testRoot).ExitCode -eq 0)
        Assert-True ($script:GitCalls -eq 2)
    }
    Test-Case 'authentication and denied access failures are not retried' {
        foreach ($message in @('fatal: Authentication failed', 'fatal: HTTP Basic: Access denied', 'fatal: The requested URL returned error: 403')) {
            Reset-GitMock; Add-GitResult 128 $message; Add-GitResult 0
            Assert-Fails { Invoke-HmGit -Arguments $readArgs -WorkingDirectory $testRoot } 'Git operation failed'
            Assert-True ($script:GitCalls -eq 1 -and $script:GitSleeps.Count -eq 0)
        }
    }
    Test-Case 'history rejection is not retried even if its diagnostic mentions connection reset' {
        Reset-GitMock; Add-GitResult 1 '[rejected] main (non-fast-forward); connection reset'; Add-GitResult 0
        Assert-Fails { Invoke-HmGit -Arguments $fetchArgs -WorkingDirectory $testRoot } 'Git operation failed'
        Assert-True ($script:GitCalls -eq 1)
    }
    Test-Case 'Git push is never repeated after a transport failure' {
        Reset-GitMock; Add-GitResult 128 'fatal: Recv failure: Connection was reset'; Add-GitResult 0
        Assert-Fails { Invoke-HmGit -Arguments @('push', '--atomic', $originalGitee, 'main') -WorkingDirectory $testRoot -Authenticate } 'Git operation failed'
        Assert-True ($script:GitCalls -eq 1 -and $script:GitSleeps.Count -eq 0)
    }
    Test-Case 'non-temporary fetch and unknown remote are never retried' {
        foreach ($argsToReject in @(
            @('fetch', '--no-tags', $originalGitHub, 'refs/heads/main:refs/heads/main'),
            @('ls-remote', '--refs', 'https://other.example/repo.git', 'refs/heads/main', 'refs/tags/*')
        )) {
            Reset-GitMock; Add-GitResult 128 'fatal: Could not resolve host: public.example'; Add-GitResult 0
            Assert-Fails { Invoke-HmGit -Arguments $argsToReject -WorkingDirectory $testRoot } 'Git operation failed'
            Assert-True ($script:GitCalls -eq 1)
        }
    }
    Test-Case 'authenticated Git calls do not opt into anonymous read retries' {
        Reset-GitMock; Add-GitResult 128 'fatal: Recv failure: Connection was reset'; Add-GitResult 0
        Assert-Fails { Invoke-HmGit -Arguments $readArgs -WorkingDirectory $testRoot -Authenticate } 'Git operation failed'
        Assert-True ($script:GitCalls -eq 1)
    }
    Test-Case 'Git retry logs do not expose diagnostics stdout addresses or credentials' {
        Reset-GitMock
        1..3 | ForEach-Object { Add-GitResult 128 'fatal: connection reset https://private.example/secret-not-real' 'stdout-secret-not-real' }
        $captured = @(& { try { Invoke-HmGit -Arguments $readArgs -WorkingDirectory $testRoot | Out-Null } catch { } } 6>&1)
        $log = $captured -join ' '
        Assert-True ($script:GitCalls -eq 3 -and $log.Contains('git-read-refs') -and $log.Contains('exitcode=128'))
        Assert-True (-not $log.Contains('https://') -and -not $log.Contains('secret-not-real') -and -not $log.Contains('connection reset'))
    }
    Test-Case 'partially fetched temporary refs are really cleaned after retry exhaustion' {
        $script:PartialFetches = 0; $script:UnexpectedPushes = 0
        Set-Item Function:Invoke-HmGitAttempt -Value {
            param([string[]]$Arguments, [string]$WorkingDirectory, [switch]$Authenticate)
            if ($Arguments[0] -eq 'ls-remote') {
                $output = if ($Arguments[2] -eq $originalGitHub) { "$first`trefs/heads/main`n" } else { '' }
                return [pscustomobject]@{ ExitCode = 0; Output = $output; TransientNetworkFailure = $false; TimedOut = $false }
            }
            if ($Arguments[0] -eq 'fetch') {
                $script:PartialFetches++
                $privateRef = $Arguments[3].Substring('refs/heads/main:'.Length)
                $null = & $originalGitAttempt -Arguments @('update-ref', $privateRef, $first) -WorkingDirectory $WorkingDirectory
                return [pscustomobject]@{ ExitCode = 128; Output = ''; TransientNetworkFailure = $true; TimedOut = $false }
            }
            if ($Arguments[0] -eq 'push') { $script:UnexpectedPushes++; throw 'Unexpected push.' }
            return & $originalGitAttempt -Arguments $Arguments -WorkingDirectory $WorkingDirectory -Authenticate:$Authenticate
        }
        Assert-Fails { Sync-HmGitCode $working } 'Git operation failed'
        Assert-True ($script:PartialFetches -eq 3 -and $script:UnexpectedPushes -eq 0)
        Assert-True ([string]::IsNullOrWhiteSpace((Invoke-HmGit -Arguments @('for-each-ref', 'refs/hm-gitee-sync') -WorkingDirectory $working).Output))
    }
    $script:StopDrainFailure = $null
    Test-Case 'failed pipe drain remains unconfirmed after the owned parent has exited' {
        $info = [Diagnostics.ProcessStartInfo]::new('git')
        $info.UseShellExecute = $false; $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
        $info.ArgumentList.Add('--version')
        $null = $info.Environment.Remove('GITEE_TOKEN')
        $owned = [Diagnostics.Process]::new(); $owned.StartInfo = $info
        try {
            $null = $owned.Start()
            $stdout = $owned.StandardOutput.ReadToEndAsync(); $stderr = $owned.StandardError.ReadToEndAsync()
            Assert-True ($owned.WaitForExit(10000))
            $null = $stdout.GetAwaiter().GetResult(); $null = $stderr.GetAwaiter().GetResult()
            $faultedPipe = [Threading.Tasks.Task]::FromException([InvalidOperationException]::new('simulated pipe failure'))
            try { Stop-HmGitProcess $owned $faultedPipe ([Threading.Tasks.Task]::CompletedTask) }
            catch { $script:StopDrainFailure = $_.Exception }
            Assert-True ($owned.HasExited -and $null -ne $script:StopDrainFailure)
            Assert-True ([bool]$script:StopDrainFailure.Data['HmGitTerminationUnconfirmed'])
        } finally { $owned.Dispose() }
    }
    Test-Case 'unconfirmed pipe drain stops retries and avoids overlapping ref cleanup' {
        $script:PartialFetches = 0; $script:CleanupCalls = 0; $script:RetainedPrivateRef = ''
        Set-Item Function:Invoke-HmGitAttempt -Value {
            param([string[]]$Arguments, [string]$WorkingDirectory, [switch]$Authenticate)
            if ($Arguments[0] -eq 'ls-remote') {
                $output = if ($Arguments[2] -eq $originalGitHub) { "$first`trefs/heads/main`n" } else { '' }
                return [pscustomobject]@{ ExitCode = 0; Output = $output; TransientNetworkFailure = $false; TimedOut = $false }
            }
            if ($Arguments[0] -eq 'fetch') {
                $script:PartialFetches++
                $script:RetainedPrivateRef = $Arguments[3].Substring('refs/heads/main:'.Length)
                $null = & $originalGitAttempt -Arguments @('update-ref', $script:RetainedPrivateRef, $first) -WorkingDirectory $WorkingDirectory
                throw $script:StopDrainFailure
            }
            $script:CleanupCalls++
            return & $originalGitAttempt -Arguments $Arguments -WorkingDirectory $WorkingDirectory -Authenticate:$Authenticate
        }
        try {
            Assert-Fails { Sync-HmGitCode $working } 'Unable to confirm Git process shutdown'
            Assert-True ($script:PartialFetches -eq 1 -and $script:CleanupCalls -eq 0)
            $retained = & $originalGitAttempt -Arguments @('rev-parse', '--verify', $script:RetainedPrivateRef) -WorkingDirectory $working
            Assert-True ($retained.ExitCode -eq 0)
        } finally { $null = & $originalGitAttempt -Arguments @('update-ref', '-d', $script:RetainedPrivateRef) -WorkingDirectory $working }
    }
    Set-Item Function:Invoke-HmGitAttempt -Value $originalGitAttempt

    # Simulate only the HTTP boundary. Uploaded/downloaded bytes and real hashes remain real.
    function Reset-Server {
        $script:Server = @{ Release = $null; Attachments = [Collections.Generic.List[object]]::new(); Payloads = @{}; Posts = 0; CreatePosts = 0; UploadPosts = 0; LoseCreateResponse = $false; LoseUploadResponse = $false; RejectUploads = $false; SeenNotes = $null; ReleaseVisibilityDelay = 0; UploadVisibilityDelay = 0; HiddenReleaseGets = 0; HiddenAttachmentGets = 0; LatestUploadedId = 0 }
    }
    function Add-TestAttachment([string]$Name, [string]$Path) {
        $id = 100 + $script:Server.Attachments.Count
        $bytes = [IO.File]::ReadAllBytes($Path)
        $script:Server.Payloads[$id] = $bytes
        $script:Server.Attachments.Add([pscustomobject]@{ id = $id; name = $Name; size = $bytes.Length })
    }
    function Set-TestRelease { $script:Server.Release = [pscustomobject]@{ id = 73; tag_name = 'v0.15'; prerelease = $false } }
    Set-Item Function:Start-Sleep -Value { param([double]$Seconds) }
    Set-Item Function:Invoke-HmHttp -Value {
        param([uri]$Uri, [string]$Method = 'GET', [hashtable]$Body, [string]$UploadPath, [switch]$Authenticated, [string]$OutFile, [string]$LogFileName, [long]$MaximumBytes, [switch]$AllowNotFound)
        if ($Uri.Host -eq 'api.github.com') { return $sourceRelease }
        if ($Uri.Host -eq 'github.com') {
            $from = if ($Uri.AbsolutePath.EndsWith('.sha256')) { $checksumPath } else { $packagePath }
            Copy-Item -LiteralPath $from -Destination $OutFile
            return
        }
        if ($Method -eq 'POST') {
            Assert-True ([bool]$Authenticated)
            Assert-True (-not $Uri.Query.Contains('access_token'))
            $script:Server.Posts++
            if ($UploadPath) {
                $script:Server.UploadPosts++
                if ($script:Server.RejectUploads) { throw 'Simulated rejected upload.' }
                Add-TestAttachment ([IO.Path]::GetFileName($UploadPath)) $UploadPath
                $script:Server.LatestUploadedId = $script:Server.Attachments[$script:Server.Attachments.Count - 1].id
                $script:Server.HiddenAttachmentGets = $script:Server.UploadVisibilityDelay
                if ($script:Server.LoseUploadResponse) { throw 'Simulated lost upload response.' }
            } else {
                $script:Server.CreatePosts++
                $script:Server.SeenNotes = $Body.body
                Set-TestRelease
                $script:Server.HiddenReleaseGets = $script:Server.ReleaseVisibilityDelay
                if ($script:Server.LoseCreateResponse) { throw 'Simulated lost create response.' }
            }
            return
        }
        Assert-True (-not [bool]$Authenticated) 'Verification must be anonymous.'
        if ($Uri.AbsolutePath -like '*/releases/tags/*') {
            if ($script:Server.HiddenReleaseGets -gt 0) { $script:Server.HiddenReleaseGets--; return $null }
            return $script:Server.Release
        }
        if ($Uri.AbsolutePath -match '/attach_files/(\d+)/download$') {
            [IO.File]::WriteAllBytes($OutFile, $script:Server.Payloads[[int]$Matches[1]])
            return
        }
        if ($Uri.AbsolutePath.EndsWith('/attach_files')) {
            if ($script:Server.HiddenAttachmentGets -gt 0) {
                $script:Server.HiddenAttachmentGets--
                return $script:Server.Attachments.ToArray() | Where-Object id -NE $script:Server.LatestUploadedId
            }
            return $script:Server.Attachments.ToArray()
        }
        throw 'Unexpected mock request.'
    }

    Reset-Server
    Test-Case 'latest resolves stable published metadata and verifies downloaded original bytes' {
        $downloadStage = Join-Path $testRoot 'downloads'; $null = New-Item -ItemType Directory -Path $downloadStage
        $download = Get-HmGitHubPackage 'latest' $downloadStage
        Assert-True ($download.Hash -eq $hash -and $download.Release.tag_name -eq 'v0.15')
    }
    Test-Case 'GitHub draft refused' {
        $sourceRelease.draft = $true
        try { Assert-Fails { Get-HmGitHubPackage 'v0.15' $testRoot } 'published stable' } finally { $sourceRelease.draft = $false }
    }
    Test-Case 'GitHub prerelease refused' {
        $sourceRelease.prerelease = $true
        try { Assert-Fails { Get-HmGitHubPackage 'latest' $testRoot } 'published stable' } finally { $sourceRelease.prerelease = $false }
    }
    Test-Case 'different requested release tag refused' { Assert-Fails { Get-HmGitHubPackage 'v0.16' $testRoot } 'different release tag' }
    Test-Case 'duplicate GitHub assets refused' {
        $sourceRelease.assets = @($asset, $shaAsset, $asset)
        try { Assert-Fails { Get-HmGitHubPackage 'latest' $testRoot } 'duplicate' } finally { $sourceRelease.assets = @($asset, $shaAsset) }
    }
    Test-Case 'foreign GitHub asset URL refused' {
        $saved = $asset.browser_download_url; $asset.browser_download_url = 'https://evil.example/installer.zip'
        try { Assert-Fails { Get-HmGitHubPackage 'latest' $testRoot } 'Unexpected GitHub asset URL' } finally { $asset.browser_download_url = $saved }
    }
    Test-Case 'read-only plan with missing release performs no POST' {
        $plan = Sync-HmGiteeRelease $package $testRoot -ReadOnly
        Assert-True ($plan.CreateRelease -and $plan.Upload.Count -eq 2 -and $script:Server.Posts -eq 0)
    }
    Test-Case 'new release copies notes and both original files then anonymously verifies bytes' {
        $result = Sync-HmGiteeRelease $package $testRoot
        Assert-True ($result.Verified.Count -eq 2 -and $script:Server.Posts -eq 3 -and $script:Server.SeenNotes -ceq $sourceRelease.body)
    }
    Test-Case 'idempotent release rerun verifies without POST or overwrite' {
        $before = $script:Server.Posts
        $result = Sync-HmGiteeRelease $package $testRoot
        Assert-True ($result.Verified.Count -eq 2 -and $script:Server.Posts -eq $before)
    }
    Test-Case 'existing complete release read-only plan verifies both files' {
        $result = Sync-HmGiteeRelease $package $testRoot -ReadOnly
        Assert-True ($result.Verified.Count -eq 2 -and $result.Upload.Count -eq 0)
    }
    Reset-Server; $script:Server.LoseCreateResponse = $true; $script:Server.LoseUploadResponse = $true
    Test-Case 'unknown successful POST outcome recovered without duplicate release or uploads' {
        $result = Sync-HmGiteeRelease $package $testRoot
        Assert-True ($result.Verified.Count -eq 2 -and $script:Server.CreatePosts -eq 1 -and $script:Server.UploadPosts -eq 2)
    }
    Reset-Server; $script:Server.LoseCreateResponse = $true; $script:Server.LoseUploadResponse = $true; $script:Server.ReleaseVisibilityDelay = 2; $script:Server.UploadVisibilityDelay = 2
    Test-Case 'delayed visibility after lost responses polls GET without second POST' {
        $result = Sync-HmGiteeRelease $package $testRoot
        Assert-True ($result.Verified.Count -eq 2 -and $script:Server.CreatePosts -eq 1 -and $script:Server.UploadPosts -eq 2)
    }
    Reset-Server; $script:Server.LoseCreateResponse = $true; $script:Server.ReleaseVisibilityDelay = 99
    Test-Case 'unconfirmed release creation stops after polling without second POST' {
        Assert-Fails { Sync-HmGiteeRelease $package $testRoot } 'creation outcome could not be confirmed'
        Assert-True ($script:Server.CreatePosts -eq 1 -and $script:Server.UploadPosts -eq 0)
    }
    Reset-Server; Set-TestRelease; $script:Server.LoseUploadResponse = $true; $script:Server.UploadVisibilityDelay = 99
    Test-Case 'unconfirmed successful upload stops without second upload' {
        Assert-Fails { Sync-HmGiteeRelease $package $testRoot } 'upload outcome could not be confirmed'
        Assert-True ($script:Server.UploadPosts -eq 1 -and $script:Server.Attachments.Count -eq 1)
    }
    Reset-Server; Set-TestRelease; Add-TestAttachment $script:HmPackageName $packagePath
    Test-Case 'partial existing release uploads only missing sidecar' {
        $result = Sync-HmGiteeRelease $package $testRoot
        Assert-True ($result.Verified.Count -eq 2 -and $script:Server.UploadPosts -eq 1 -and $script:Server.CreatePosts -eq 0)
    }
    Reset-Server; Set-TestRelease; Add-TestAttachment $script:HmPackageName $packagePath
    $script:Server.Payloads[100][0] = $script:Server.Payloads[100][0] -bxor 1
    Test-Case 'same-name same-length changed attachment blocks and is never overwritten' {
        Assert-Fails { Sync-HmGiteeRelease $package $testRoot } 'differs'
        Assert-True ($script:Server.Posts -eq 0)
    }
    Reset-Server; Set-TestRelease; Add-TestAttachment $script:HmPackageName $checksumPath
    Test-Case 'same-name different-size attachment blocks without POST' {
        Assert-Fails { Sync-HmGiteeRelease $package $testRoot } 'differs'
        Assert-True ($script:Server.Posts -eq 0)
    }
    Reset-Server; Set-TestRelease; Add-TestAttachment ($script:HmPackageName + '.sha256') $packagePath
    Test-Case 'all existing attachments checked before uploading any missing file' {
        Assert-Fails { Sync-HmGiteeRelease $package $testRoot } 'differs'
        Assert-True ($script:Server.Posts -eq 0)
    }
    Reset-Server; Set-TestRelease; Add-TestAttachment $script:HmPackageName $packagePath; Add-TestAttachment $script:HmPackageName $packagePath
    Test-Case 'duplicate Gitee attachments refused without deleting either' {
        Assert-Fails { Sync-HmGiteeRelease $package $testRoot } 'Duplicate Gitee'
        Assert-True ($script:Server.Posts -eq 0 -and $script:Server.Attachments.Count -eq 2)
    }
    Reset-Server; Set-TestRelease; $script:Server.Release.prerelease = $true
    Test-Case 'existing prerelease refused without modification' { Assert-Fails { Sync-HmGiteeRelease $package $testRoot } 'requested stable' }
    Reset-Server; Set-TestRelease; $script:Server.RejectUploads = $true
    Test-Case 'rejected upload is not blindly retried and leaves no duplicate attachment' {
        Assert-Fails { Sync-HmGiteeRelease $package $testRoot } 'upload outcome could not be confirmed'
        Assert-True ($script:Server.UploadPosts -eq 1 -and $script:Server.Attachments.Count -eq 0)
    }
    Test-Case 'write mode fails immediately without token' {
        $env:GITEE_TOKEN = ''
        Assert-Fails { Invoke-HmGiteeSync 'latest' $testRoot -OnlyCode } 'GITEE_TOKEN'
    }
} finally {
    Set-Item Function:Invoke-HmHttp -Value $originalHttp
    Set-Item Function:Invoke-HmGitAttempt -Value $originalGitAttempt
    if ($originalSleep) { Set-Item Function:Start-Sleep -Value $originalSleep.ScriptBlock } else { Remove-Item Function:Start-Sleep -ErrorAction SilentlyContinue }
    $script:HmGitHub = $originalGitHub; $script:HmGitee = $originalGitee
    $env:GITEE_TOKEN = $originalToken
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ([IO.Path]::GetFileName($resolved) -notmatch '^HM-AppKit-Gitee-Tests-[0-9a-f]{32}$' -or
        [IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)) { throw 'Test cleanup path validation failed.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Output "Gitee sync offline tests: $script:Passed passed, $script:Failed failed."
if ($script:Failed -gt 0) { exit 1 }

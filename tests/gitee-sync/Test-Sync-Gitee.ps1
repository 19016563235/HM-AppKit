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
    Test-Case 'file uploads retain a 15-minute deadline' { Assert-True ((Get-HmHttpTimeoutSeconds -HasUpload $true -HasDownload $false) -eq 900) }
    Test-Case 'file downloads retain a 15-minute deadline' { Assert-True ((Get-HmHttpTimeoutSeconds -HasUpload $false -HasDownload $true) -eq 900) }
    Test-Case 'file transfer deadline is independent of upload/download precedence' { Assert-True ((Get-HmHttpTimeoutSeconds -HasUpload $true -HasDownload $true) -eq 900) }
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

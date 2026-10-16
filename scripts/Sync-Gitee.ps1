#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$Tag = 'latest',
    [switch]$CodeOnly,
    [switch]$PlanOnly,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:HmGitHub = 'https://github.com/19016563235/HM-AppKit.git'
$script:HmGitee = 'https://gitee.com/huanmeng-it/HM-AppKit.git'
$script:HmGitHubApi = 'https://api.github.com/repos/19016563235/HM-AppKit'
$script:HmGiteeApi = 'https://gitee.com/api/v5/repos/huanmeng-it/HM-AppKit'
$script:HmPackageName = 'HM-AppKit-win-x64.zip'

function Test-HmReleaseTag([string]$Value) {
    return $Value -cmatch '^v[0-9]+(?:\.[0-9]+){1,3}$'
}

function Test-HmDownloadRedirect([uri]$Initial, [uri]$Destination) {
    if ($Destination.Scheme -ne 'https' -or -not $Destination.IsDefaultPort -or
        $Destination.UserInfo -or $Destination.Fragment) { return $false }
    if ($Initial.Host -eq 'github.com') {
        return $Destination.Host -in @('github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com')
    }
    if ($Initial.Host -eq 'gitee.com') {
        return $Destination.Host -in @('gitee.com', 'foruda.gitee.com')
    }
    return $Destination.Host -eq $Initial.Host
}

function Get-HmHttpTimeoutSeconds([bool]$HasUpload, [bool]$HasDownload) {
    if ($HasUpload -or $HasDownload) { return 900 }
    return 60
}

# Only public GETs are retried here. POSTs have their own read-after-write recovery.
# Token-bearing contents and exception details never leave this function.
function Invoke-HmHttp {
    param(
        [Parameter(Mandatory)][uri]$Uri,
        [ValidateSet('GET', 'POST')][string]$Method = 'GET',
        [hashtable]$Body,
        [string]$UploadPath,
        [switch]$Authenticated,
        [string]$OutFile,
        [ValidateSet('HM-AppKit-win-x64.zip', 'HM-AppKit-win-x64.zip.sha256')][string]$LogFileName,
        [long]$MaximumBytes = 134217728,
        [switch]$AllowNotFound
    )
    if ($Uri.Scheme -ne 'https' -or $Uri.UserInfo -or -not $Uri.IsDefaultPort -or $Uri.Query -match 'access_token=') {
        throw 'Unsafe API address.'
    }
    if ($Authenticated -and ($Method -ne 'POST' -or $Uri.Host -ne 'gitee.com' -or
        -not $Uri.AbsoluteUri.StartsWith($script:HmGiteeApi + '/', [StringComparison]::Ordinal))) {
        throw 'Credentials may only be sent in a Gitee API POST body.'
    }
    if ($Authenticated -and [string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) { throw 'GITEE_TOKEN is required.' }
    $timeoutSeconds = Get-HmHttpTimeoutSeconds -HasUpload ([bool]$UploadPath) -HasDownload ([bool]$OutFile)
    $phase = if ($UploadPath) { 'upload' } elseif ($OutFile -and $Uri.Host -eq 'gitee.com') { 'verify' } elseif ($OutFile) { 'download' } elseif ($Method -eq 'POST') { 'release-create' } else { 'metadata' }
    # Only fixed phase names and the two permitted public asset names may enter logs.
    $fileLabel = '-'
    if ($LogFileName) { $fileLabel = $LogFileName }
    elseif ($UploadPath -and [IO.Path]::GetFileName($UploadPath) -in @($script:HmPackageName, ($script:HmPackageName + '.sha256'))) { $fileLabel = [IO.Path]::GetFileName($UploadPath) }
    $expectedBytes = if ($UploadPath) { (Get-Item -LiteralPath $UploadPath).Length } elseif ($OutFile) { $MaximumBytes } else { 0L }
    $attempts = if ($Method -eq 'GET') { 3 } else { 1 }
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        $handler = [System.Net.Http.HttpClientHandler]::new()
        $handler.AllowAutoRedirect = $false
        $handler.UseCookies = $false
        $client = [System.Net.Http.HttpClient]::new($handler)
        $client.Timeout = [TimeSpan]::FromSeconds($timeoutSeconds)
        $request = $null; $response = $null; $stream = $null; $file = $null
        $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($timeoutSeconds))
        $status = 0
        $readBytes = 0L; $outcome = 'failed'
        $elapsed = [Diagnostics.Stopwatch]::StartNew()
        Write-Host ('HM sync phase={0} event=start file={1} bytes={2} timeout_s={3} attempt={4}/{5}' -f $phase, $fileLabel, $expectedBytes, $timeoutSeconds, $attempt, $attempts)
        try {
            $current = $Uri
            for ($redirect = 0; $redirect -le 5; $redirect++) {
                $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $current)
                $request.Headers.UserAgent.ParseAdd('HM-AppKit-Gitee-Sync/1.0')
                $request.Headers.Accept.ParseAdd('application/json')
                if ($Method -eq 'POST') {
                    if ($UploadPath) {
                        $form = [System.Net.Http.MultipartFormDataContent]::new()
                        if ($Authenticated) { $form.Add([System.Net.Http.StringContent]::new($env:GITEE_TOKEN), 'access_token') }
                        $stream = [IO.File]::OpenRead($UploadPath)
                        $form.Add([System.Net.Http.StreamContent]::new($stream), 'file', [IO.Path]::GetFileName($UploadPath))
                        $request.Content = $form
                    } else {
                        $data = @{}
                        if ($Body) { foreach ($key in $Body.Keys) { $data[$key] = $Body[$key] } }
                        if ($Authenticated) { $data.access_token = $env:GITEE_TOKEN }
                        $json = ConvertTo-Json -InputObject $data -Depth 8 -Compress
                        $request.Content = [System.Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
                    }
                }
                $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead, $deadline.Token).GetAwaiter().GetResult()
                $status = [int]$response.StatusCode
                if ($status -in @(301, 302, 303, 307, 308)) {
                    # Never resend a POST body (and its credential) across a redirect.
                    if ($Method -ne 'GET' -or $redirect -eq 5 -or -not $response.Headers.Location) { throw 'Redirect rejected.' }
                    $next = [uri]::new($current, $response.Headers.Location)
                    if (-not (Test-HmDownloadRedirect $Uri $next)) { throw 'Redirect rejected.' }
                    $response.Dispose(); $response = $null
                    $request.Dispose(); $request = $null
                    $current = $next
                    continue
                }
                break
            }
            if ($status -eq 404 -and $AllowNotFound) { $outcome = 'not-found'; return $null }
            if ($status -lt 200 -or $status -ge 300) { throw 'Request rejected.' }
            if ($OutFile) {
                $size = $response.Content.Headers.ContentLength
                if ($null -ne $size -and $size -gt $MaximumBytes) { throw 'Download exceeds allowed size.' }
                $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                $file = [IO.File]::Create($OutFile)
                $buffer = [byte[]]::new(131072); $total = 0L
                while (($read = $stream.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token).GetAwaiter().GetResult()) -gt 0) {
                    $total += $read
                    $readBytes = $total
                    if ($total -gt $MaximumBytes) { throw 'Download exceeds allowed size.' }
                    $file.Write($buffer, 0, $read)
                }
                if ($null -ne $size -and $total -ne $size) { throw 'Incomplete download.' }
                $outcome = 'complete'
                return
            }
            $text = $response.Content.ReadAsStringAsync($deadline.Token).GetAwaiter().GetResult()
            if ($text.Length -gt 8388608) { throw 'API response exceeds allowed size.' }
            if (-not $UploadPath) { $readBytes = [Text.Encoding]::UTF8.GetByteCount($text) }
            $outcome = 'complete'
            if ([string]::IsNullOrWhiteSpace($text) -or $text.Trim() -eq 'null') { return $null }
            return ConvertFrom-Json -InputObject $text -Depth 30
        } catch {
            $outcome = if ($deadline.IsCancellationRequested) { 'timeout' } else { 'failed' }
            $retry = $Method -eq 'GET' -and ($status -eq 0 -or $status -in @(200, 408, 429) -or $status -ge 500)
            if (-not $retry -or $attempt -eq $attempts) {
                # Deliberately omit request/response bodies, signed CDN URLs, and inner exceptions.
                throw "Public API/download operation failed (HTTP $status)."
            }
        } finally {
            # Upload read_bytes measures bytes consumed from the file, not server receipt.
            if ($UploadPath -and $stream -and $stream.CanSeek) { $readBytes = $stream.Position }
            $elapsed.Stop()
            Write-Host ('HM sync phase={0} event={1} file={2} read_bytes={3} elapsed_ms={4} http={5} attempt={6}/{7}' -f $phase, $outcome, $fileLabel, $readBytes, $elapsed.ElapsedMilliseconds, $status, $attempt, $attempts)
            if ($file) { $file.Dispose() }
            if ($stream) { $stream.Dispose() }
            if ($response) { $response.Dispose() }
            if ($request) { $request.Dispose() }
            $client.Dispose()
            $deadline.Dispose()
        }
        Start-Sleep -Seconds $attempt
    }
}

function Invoke-HmGit {
    param([string[]]$Arguments, [string]$WorkingDirectory, [switch]$Authenticate, [switch]$AllowFailure)
    $info = [Diagnostics.ProcessStartInfo]::new('git')
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    foreach ($arg in $Arguments) { $info.ArgumentList.Add($arg) }
    foreach ($key in @($info.Environment.Keys)) {
        if ($key -match '^GIT_(TRACE|CONFIG_|CURL_VERBOSE)' -or $key -eq 'GITEE_TOKEN') { $null = $info.Environment.Remove($key) }
    }
    $info.Environment['GIT_TERMINAL_PROMPT'] = '0'
    $info.Environment['GCM_INTERACTIVE'] = 'Never'
    $info.Environment['GIT_ASKPASS'] = ''
    $info.Environment['GIT_CONFIG_COUNT'] = '3'
    $info.Environment['GIT_CONFIG_KEY_0'] = 'credential.helper'
    $info.Environment['GIT_CONFIG_VALUE_0'] = ''
    $info.Environment['GIT_CONFIG_KEY_1'] = 'core.askPass'
    $info.Environment['GIT_CONFIG_VALUE_1'] = ''
    $info.Environment['GIT_CONFIG_KEY_2'] = 'http.extraHeader'
    $info.Environment['GIT_CONFIG_VALUE_2'] = ''
    if ($Authenticate) {
        if ([string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) { throw 'GITEE_TOKEN is required.' }
        $basic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('huanmeng-it:' + $env:GITEE_TOKEN))
        $info.Environment['GIT_CONFIG_COUNT'] = '4'
        $info.Environment['GIT_CONFIG_KEY_3'] = 'http.https://gitee.com/.extraHeader'
        $info.Environment['GIT_CONFIG_VALUE_3'] = 'Authorization: Basic ' + $basic
    }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $info
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(180000)) {
            $process.Kill($true)
            throw 'Git operation timed out. No credentials or Git diagnostics are printed; rerun safely.'
        }
        $output = $stdout.GetAwaiter().GetResult()
        $null = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -and -not $AllowFailure) {
            throw 'Git operation failed. Check repository permissions, branch protection, and fast-forward history; no credentials or Git diagnostics are printed.'
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output }
    } finally { $process.Dispose() }
}

function ConvertTo-HmRefs([string]$Listing) {
    $refs = @{}
    foreach ($line in ($Listing -split '\r?\n')) {
        if (-not $line) { continue }
        if ($line -cnotmatch '^([0-9a-f]{40}|[0-9a-f]{64})\s+(refs/heads/main|refs/tags/[^\s^]+)$') { throw 'Unexpected remote Git reference.' }
        if ($refs.ContainsKey($Matches[2])) { throw 'Duplicate remote Git reference.' }
        $refs[$Matches[2]] = $Matches[1]
    }
    return $refs
}

function Sync-HmGitCode([string]$Root, [switch]$ReadOnly) {
    $namespace = 'refs/hm-gitee-sync/' + [guid]::NewGuid().ToString('N')
    try {
        $source = ConvertTo-HmRefs (Invoke-HmGit -Arguments @('ls-remote', '--refs', $script:HmGitHub, 'refs/heads/main', 'refs/tags/*') -WorkingDirectory $Root).Output
        $target = ConvertTo-HmRefs (Invoke-HmGit -Arguments @('ls-remote', '--refs', $script:HmGitee, 'refs/heads/main', 'refs/tags/*') -WorkingDirectory $Root).Output
        if (-not $source.ContainsKey('refs/heads/main')) { throw 'GitHub main is missing.' }
        $null = Invoke-HmGit -Arguments @('fetch', '--no-tags', $script:HmGitHub, "refs/heads/main:$namespace/main", "refs/tags/*:$namespace/tags/*") -WorkingDirectory $Root
        $fetchedMain = (Invoke-HmGit -Arguments @('rev-parse', "$namespace/main") -WorkingDirectory $Root).Output.Trim()
        if ($fetchedMain -ne $source['refs/heads/main']) { throw 'GitHub changed during synchronization; rerun the workflow.' }
        foreach ($ref in $target.Keys) {
            if (-not $source.ContainsKey($ref)) { throw "Gitee has an independent reference: $ref. No references were pushed." }
            if ($ref -like 'refs/tags/*' -and $target[$ref] -ne $source[$ref]) { throw "Gitee tag differs: $ref. No references were pushed." }
        }
        if ($target.ContainsKey('refs/heads/main')) {
            $null = Invoke-HmGit -Arguments @('fetch', '--no-tags', $script:HmGitee, "refs/heads/main:$namespace/gitee-main") -WorkingDirectory $Root
            $ancestor = Invoke-HmGit -Arguments @('merge-base', '--is-ancestor', "$namespace/gitee-main", "$namespace/main") -WorkingDirectory $Root -AllowFailure
            if ($ancestor.ExitCode -ne 0) { throw 'Gitee main contains independent commits. No references were pushed.' }
        }
        $updates = [Collections.Generic.List[string]]::new()
        if (-not $target.ContainsKey('refs/heads/main') -or $target['refs/heads/main'] -ne $fetchedMain) { $updates.Add("$namespace/main`:refs/heads/main") }
        foreach ($ref in @($source.Keys | Where-Object { $_ -like 'refs/tags/*' } | Sort-Object)) {
            $tagName = $ref.Substring('refs/tags/'.Length)
            $fetchedTag = (Invoke-HmGit -Arguments @('rev-parse', "$namespace/tags/$tagName") -WorkingDirectory $Root).Output.Trim()
            if ($fetchedTag -ne $source[$ref]) { throw 'GitHub tags changed during synchronization; rerun the workflow.' }
            if (-not $target.ContainsKey($ref)) { $updates.Add("$namespace/tags/$tagName`:$ref") }
        }
        if (-not $ReadOnly -and $updates.Count -gt 0) {
            # Atomic push is intentional. A server without atomic support must fail safely.
            $null = Invoke-HmGit -Arguments (@('push', '--atomic', $script:HmGitee) + $updates.ToArray()) -WorkingDirectory $Root -Authenticate
            $after = ConvertTo-HmRefs (Invoke-HmGit -Arguments @('ls-remote', '--refs', $script:HmGitee, 'refs/heads/main', 'refs/tags/*') -WorkingDirectory $Root).Output
            foreach ($ref in $source.Keys) {
                if (-not $after.ContainsKey($ref) -or $after[$ref] -ne $source[$ref]) { throw 'Remote Git verification failed.' }
            }
        }
        return [pscustomobject]@{ Main = $fetchedMain; Tags = @($source.Keys | Where-Object { $_ -like 'refs/tags/*' }).Count; ReferencesToUpdate = $updates.Count; PlanOnly = [bool]$ReadOnly }
    } finally {
        $local = Invoke-HmGit -Arguments @('for-each-ref', '--format=%(refname)', $namespace) -WorkingDirectory $Root -AllowFailure
        foreach ($ref in ($local.Output -split '\r?\n')) {
            if ($ref.StartsWith($namespace + '/', [StringComparison]::Ordinal)) { $null = Invoke-HmGit -Arguments @('update-ref', '-d', $ref) -WorkingDirectory $Root }
        }
    }
}

function Get-HmHash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Assert-HmPackage([string]$Package, [string]$Checksum, [object]$Asset) {
    if ((Get-Item -LiteralPath $Package).Length -ne [long]$Asset.size) { throw 'GitHub package length mismatch.' }
    $hash = Get-HmHash $Package
    $sidecar = [IO.File]::ReadAllText($Checksum).Trim()
    if ($sidecar -cne "$hash  $script:HmPackageName") { throw 'GitHub checksum mismatch.' }
    if ([string]$Asset.digest -cne "sha256:$hash") { throw 'GitHub API digest mismatch or missing.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($Package)
    try {
        $executables = @($archive.Entries | Where-Object { $_.FullName -match '\.(exe|msi)$' })
        if ($executables.Count -ne 1 -or $executables[0].FullName -cne 'HM AppKit.exe') { throw 'Package must contain only HM AppKit.exe, without vendor installers.' }
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($name -match '(^|/)Local Installer(/|$)' -or $name -match '(^|/)\.\.(/|$)' -or $name.StartsWith('/') -or $name -match '^[A-Za-z]:') {
                throw 'Package includes an installer cache or an unsafe entry.'
            }
        }
    } finally { $archive.Dispose() }
    return $hash
}

function Get-HmGitHubPackage([string]$RequestedTag, [string]$Stage) {
    if ($RequestedTag -ne 'latest' -and -not (Test-HmReleaseTag $RequestedTag)) { throw 'Use latest or a stable tag such as v0.15.' }
    $path = if ($RequestedTag -eq 'latest') { 'latest' } else { 'tags/' + $RequestedTag }
    $release = Invoke-HmHttp -Uri "$script:HmGitHubApi/releases/$path"
    if ($null -eq $release -or $release.draft -or $release.prerelease -or -not (Test-HmReleaseTag $release.tag_name)) { throw 'GitHub release is not a published stable version.' }
    if ($RequestedTag -ne 'latest' -and $release.tag_name -cne $RequestedTag) { throw 'GitHub returned a different release tag.' }
    $files = @{}
    foreach ($name in @($script:HmPackageName, ($script:HmPackageName + '.sha256'))) {
        $matches = @($release.assets | Where-Object name -CEQ $name)
        if ($matches.Count -ne 1 -or [long]$matches[0].size -le 0 -or [long]$matches[0].size -gt 104857600) { throw 'Missing, duplicate, or oversized GitHub asset.' }
        $asset = $matches[0]
        $expected = 'https://github.com/19016563235/HM-AppKit/releases/download/' + $release.tag_name + '/' + $name
        if ($asset.browser_download_url -cne $expected) { throw 'Unexpected GitHub asset URL.' }
        $file = Join-Path $Stage $name
        Invoke-HmHttp -Uri $expected -OutFile $file -LogFileName $name -MaximumBytes ([long]$asset.size)
        if ((Get-Item -LiteralPath $file).Length -ne [long]$asset.size) { throw 'GitHub asset length mismatch.' }
        $files[$name] = [pscustomobject]@{ Path = $file; Hash = Get-HmHash $file; Asset = $asset }
    }
    $hash = Assert-HmPackage $files[$script:HmPackageName].Path $files[$script:HmPackageName + '.sha256'].Path $files[$script:HmPackageName].Asset
    return [pscustomobject]@{ Release = $release; Files = $files; Hash = $hash }
}

function Get-HmGiteeRelease([string]$ReleaseTag) {
    return Invoke-HmHttp -Uri "$script:HmGiteeApi/releases/tags/$ReleaseTag" -AllowNotFound
}

function Get-HmGiteeAttachments([long]$ReleaseId) {
    $result = [Collections.Generic.List[object]]::new()
    for ($page = 1; $page -le 20; $page++) {
        $items = @(Invoke-HmHttp -Uri "$script:HmGiteeApi/releases/$ReleaseId/attach_files?per_page=100&page=$page")
        foreach ($item in $items) { if ($null -ne $item) { $result.Add($item) } }
        if ($items.Count -lt 100) { return $result.ToArray() }
    }
    throw 'Gitee attachment pagination exceeded the limit.'
}

function Assert-HmGiteeAttachment([long]$ReleaseId, [object]$Attachment, [object]$File, [string]$Stage) {
    if ([long]$Attachment.id -le 0 -or [long]$Attachment.size -ne (Get-Item -LiteralPath $File.Path).Length) { throw 'Existing Gitee attachment differs; no attachment was overwritten.' }
    $verify = Join-Path $Stage ('verify-' + [guid]::NewGuid().ToString('N'))
    try {
        Invoke-HmHttp -Uri "$script:HmGiteeApi/releases/$ReleaseId/attach_files/$($Attachment.id)/download" -OutFile $verify -LogFileName ([IO.Path]::GetFileName($File.Path)) -MaximumBytes ([long]$Attachment.size)
        if ((Get-Item -LiteralPath $verify).Length -ne [long]$Attachment.size -or (Get-HmHash $verify) -cne $File.Hash) {
            throw 'Existing Gitee attachment differs; no attachment was overwritten.'
        }
    } finally { if (Test-Path -LiteralPath $verify) { Remove-Item -LiteralPath $verify -Force } }
}

function Sync-HmGiteeRelease([object]$Package, [string]$Stage, [switch]$ReadOnly) {
    $releaseTag = [string]$Package.Release.tag_name
    $release = Get-HmGiteeRelease $releaseTag
    $created = $null -eq $release
    if ($null -eq $release -and -not $ReadOnly) {
        # Sync-HmGitCode has already verified and pushed the exact existing tag.
        # The API documents target_commitish as a branch or commit SHA, so use main.
        $body = @{ tag_name = $releaseTag; name = [string]$Package.Release.name; body = [string]$Package.Release.body; prerelease = $false; target_commitish = 'main' }
        try { $null = Invoke-HmHttp -Uri "$script:HmGiteeApi/releases" -Method POST -Body $body -Authenticated } catch { }
        for ($attempt = 1; $attempt -le 4 -and $null -eq $release; $attempt++) {
            if ($attempt -gt 1) { Start-Sleep -Seconds ([int][Math]::Pow(2, $attempt - 2)) }
            # Visibility may lag a successful POST. Poll only; never issue a second POST.
            $release = Get-HmGiteeRelease $releaseTag
        }
        if ($null -eq $release) { throw 'Gitee release creation outcome could not be confirmed. No second POST was attempted; rerun safely after checking the release.' }
    }
    if ($null -eq $release) { return [pscustomobject]@{ Tag = $releaseTag; CreateRelease = $true; Upload = @($Package.Files.Keys); Verified = @(); PlanOnly = $true } }
    if ($release.tag_name -cne $releaseTag -or $release.prerelease -or [long]$release.id -le 0) { throw 'Existing Gitee release is not the requested stable tag.' }
    $upload = [Collections.Generic.List[string]]::new(); $verified = [Collections.Generic.List[string]]::new()
    $attachments = @(Get-HmGiteeAttachments $release.id)
    # Check every existing attachment before uploading any missing attachment.
    # Transfer the small checksum first so upload permission is verified early.
    foreach ($name in @(($script:HmPackageName + '.sha256'), $script:HmPackageName)) {
        $file = $Package.Files[$name]
        $found = @($attachments | Where-Object name -CEQ $name)
        if ($found.Count -gt 1) { throw 'Duplicate Gitee asset names; no attachments were changed.' }
        if ($found.Count -eq 1) {
            Assert-HmGiteeAttachment $release.id $found[0] $file $Stage
            $verified.Add($name)
            continue
        }
        $upload.Add($name)
    }
    if (-not $ReadOnly) { foreach ($name in $upload) {
        $file = $Package.Files[$name]
        try { $null = Invoke-HmHttp -Uri "$script:HmGiteeApi/releases/$($release.id)/attach_files" -Method POST -UploadPath $file.Path -Authenticated } catch { }
        for ($attempt = 1; $attempt -le 4; $attempt++) {
            if ($attempt -gt 1) { Start-Sleep -Seconds ([int][Math]::Pow(2, $attempt - 2)) }
            $found = @(Get-HmGiteeAttachments $release.id | Where-Object name -CEQ $name)
            if ($found.Count -gt 1) { throw 'Duplicate Gitee assets detected; no attachments were deleted.' }
            if ($found.Count -eq 1) {
                Assert-HmGiteeAttachment $release.id $found[0] $file $Stage
                $verified.Add($name)
                break
            }
            if ($attempt -eq 4) { throw 'Gitee upload outcome could not be confirmed. No second upload was attempted; rerun safely after checking the release.' }
        }
    } }
    return [pscustomobject]@{ Tag = $releaseTag; CreateRelease = $created; Upload = $upload.ToArray(); Verified = $verified.ToArray(); PlanOnly = [bool]$ReadOnly }
}

function Invoke-HmGiteeSync([string]$RequestedTag, [string]$Root, [switch]$OnlyCode, [switch]$ReadOnly) {
    if (-not $ReadOnly -and [string]::IsNullOrWhiteSpace($env:GITEE_TOKEN)) { throw 'Add the GITEE_TOKEN Actions secret before running synchronization.' }
    $Root = (Resolve-Path -LiteralPath $Root).Path
    $stage = Join-Path ([IO.Path]::GetTempPath()) ('HM-AppKit-Gitee-Sync-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $stage
    try {
        # Validate the original published GitHub bytes before making any external changes.
        $package = if (-not $OnlyCode) { Get-HmGitHubPackage $RequestedTag $stage } else { $null }
        $code = Sync-HmGitCode $Root -ReadOnly:$ReadOnly
        $release = if ($package) { Sync-HmGiteeRelease $package $stage -ReadOnly:$ReadOnly } else { $null }
        return [pscustomobject]@{ GitHub = $script:HmGitHub; Gitee = $script:HmGitee; Code = $code; Release = $release; SHA256 = $(if ($package) { $package.Hash } else { $null }) }
    } finally {
        # The path is an explicitly created private temporary directory, never a user cache.
        $resolvedStage = [IO.Path]::GetFullPath($stage)
        $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if ([IO.Path]::GetDirectoryName($resolvedStage) -ne $expectedParent -or
            [IO.Path]::GetFileName($resolvedStage) -notmatch '^HM-AppKit-Gitee-Sync-[0-9a-f]{32}$') { throw 'Temporary cleanup path validation failed.' }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}

# Dot-source for deterministic offline tests; normal execution performs the requested operation.
if ($MyInvocation.InvocationName -ne '.') {
    Invoke-HmGiteeSync -RequestedTag $Tag -Root $RepositoryRoot -OnlyCode:$CodeOnly -ReadOnly:$PlanOnly | ConvertTo-Json -Depth 8
}

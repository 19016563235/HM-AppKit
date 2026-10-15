param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$release = & (Join-Path $PSScriptRoot 'Get-ReleaseMetadata.ps1')
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $projectRoot ($release.RelativeOutputRoot + '/' + $Runtime)
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$publicEvidence = @(
    'real-install-test-2026-10-15/20261015-host-dewu.json',
    'real-install-test-2026-10-15/20261015-host-doudian.json',
    'real-install-test-2026-10-15/20261015-host-jingmai-strict-skip.json',
    'real-install-test-2026-10-15/20261015-host-jingmai.json',
    'real-install-test-2026-10-15/20261015-host-kuaiyu.json',
    'real-install-test-2026-10-15/20261015-host-pinduoduo-retry.json',
    'real-install-test-2026-10-15/20261015-host-pinduoduo.json',
    'real-install-test-2026-10-15/20261015-host-qianfan-retry.json',
    'real-install-test-2026-10-15/20261015-host-qianfan-skip-recheck.json',
    'real-install-test-2026-10-15/20261015-host-qianfan.json',
    'real-install-test-2026-10-15/20261015-host-qianniu.json',
    'real-install-test-2026-10-15/cleanup.json',
    'real-install-test-2026-10-15/preserved-applications-baseline.json',
    'real-install-test-2026-10-15/summary.json',
    'v0.5-downloads/ToDesk_5.2.1.0-verification.json',
    'v0.5-downloads/jingmai-cleanup.json',
    'v0.5-cache-live/live-cache.json',
    'v0.5-source-live/source-resolution.json',
    'v0.5-ui/settings-check.json',
    'jingmai-research/cleanup.json',
    'v0.6-self-update/verification.json'
)
foreach ($evidence in $publicEvidence) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot ('evidence/' + $evidence)) -PathType Leaf)) {
        throw "缺少公开测试摘要：evidence/$evidence"
    }
}

Push-Location $projectRoot
try {
    & dotnet run --project tests/FoxDeploy.Core.Tests/FoxDeploy.Core.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw '安装引擎验证未通过。' }

    & dotnet run --project tests/FoxDeploy.Jingmai.Tests/FoxDeploy.Jingmai.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw '京麦安装适配器验证未通过。' }

    & dotnet run --project tests/FoxDeploy.Cache.Tests/FoxDeploy.Cache.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw '安装包缓存与更新验证未通过。' }

    & dotnet run --project tests/FoxDeploy.Update.Tests/FoxDeploy.Update.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw '助手更新检查与下载验证未通过。' }

    & dotnet run --project tests/FoxDeploy.UpdateApply.Tests/FoxDeploy.UpdateApply.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw '助手更新应用与配置保留验证未通过。' }

    & dotnet publish src/FoxDeploy.App/FoxDeploy.App.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw '桌面应用发布失败。' }

    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $OutputDirectory -Force
    $docsDestination = Join-Path $OutputDirectory 'docs'
    New-Item -ItemType Directory -Path $docsDestination -Force | Out-Null
    foreach ($document in @('real-install-test-2026-10-15.md', 'jingmai-research-2026-10-15.md', 'hm-appkit-v0.5-test-2026-10-15.md', 'self-update.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot ('docs\' + $document)) -Destination $docsDestination -Force
    }
    # Only sanitized, named summaries are public. Raw host snapshots, vendor
    # installers and Local Installer are never copied into the release.
    foreach ($evidence in $publicEvidence) {
        $evidenceTarget = Join-Path $OutputDirectory ('evidence/' + $evidence)
        New-Item -ItemType Directory -Path (Split-Path -Parent $evidenceTarget) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $projectRoot ('evidence/' + $evidence)) -Destination $evidenceTarget -Force
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'evidence/README.md') -Destination (Join-Path $OutputDirectory 'evidence/README.md') -Force
    $catalogDestination = Join-Path $OutputDirectory 'catalog'
    New-Item -ItemType Directory -Path $catalogDestination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'catalog\sources.md') -Destination $catalogDestination -Force
    $localInstallerNotes = Join-Path $projectRoot 'catalog\local-installers.md'
    if (Test-Path -LiteralPath $localInstallerNotes) { Copy-Item -LiteralPath $localInstallerNotes -Destination $catalogDestination -Force }

    $executable = Join-Path $OutputDirectory 'HM AppKit.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw '发布目录中缺少 HM AppKit.exe。' }
    $executableHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Output "EXE: $executable"
    Write-Output "SHA256: $executableHash"

    $archive = Join-Path (Split-Path -Parent $OutputDirectory) "HM-AppKit-$Runtime.zip"
    $archiveInputs = @(
        $executable,
        (Join-Path $OutputDirectory 'README.md'),
        (Join-Path $OutputDirectory 'Data'),
        (Join-Path $OutputDirectory 'docs'),
        (Join-Path $OutputDirectory 'evidence'),
        (Join-Path $OutputDirectory 'licenses'),
        (Join-Path $OutputDirectory 'catalog')
    )
    Compress-Archive -LiteralPath $archiveInputs -DestinationPath $archive -Force
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$archiveHash  $([System.IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath ($archive + '.sha256') -Encoding ascii
    Write-Output "ZIP: $archive"
    Write-Output "ZIP SHA256: $archiveHash"
} finally {
    Pop-Location
}

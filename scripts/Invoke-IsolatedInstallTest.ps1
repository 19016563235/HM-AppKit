param(
    [string]$OutputDirectory,
    [switch]$Launch,
    [string]$Apps
)

# Host preparation is read-only with respect to installed software. Only -Launch
# starts Windows Sandbox. No installer is ever started on the host by this script.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $projectRoot ('artifacts\Integration\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    if (@(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) {
        throw 'Use a new empty output directory so test evidence from another run is preserved.'
    }
}
$inputDirectory = Join-Path $OutputDirectory 'input'
$evidenceDirectory = Join-Path $OutputDirectory 'evidence'
New-Item -ItemType Directory -Path $inputDirectory, $evidenceDirectory -Force | Out-Null

& dotnet publish (Join-Path $projectRoot 'tests\FoxDeploy.Integration\FoxDeploy.Integration.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -o $inputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Integration harness publication failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'catalog\catalog.json') -Destination (Join-Path $inputDirectory 'catalog.json')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Invoke-IsolatedInstallGuestTest.ps1') -Destination $inputDirectory
$harness = Join-Path $inputDirectory 'HM-AutoSetup.Integration.exe'
& $harness --snapshot-host --catalog (Join-Path $inputDirectory 'catalog.json') --output $inputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Host installed software snapshot failed.' }
if ($Apps) {
    $Apps | Set-Content -LiteralPath (Join-Path $inputDirectory 'requested-apps.txt') -Encoding UTF8
}

$escape = { param([string]$value) [Security.SecurityElement]::Escape($value) }
$inputXml = & $escape $inputDirectory
$evidenceXml = & $escape $evidenceDirectory
$commandXml = & $escape 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\TestInput\Invoke-IsolatedInstallGuestTest.ps1'
$configuration = @"
<Configuration>
  <VGpu>Disable</VGpu>
  <Networking>Enable</Networking>
  <MemoryInMB>3072</MemoryInMB>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <PrinterRedirection>Disable</PrinterRedirection>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$inputXml</HostFolder>
      <SandboxFolder>C:\TestInput</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$evidenceXml</HostFolder>
      <SandboxFolder>C:\TestEvidence</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>$commandXml</Command></LogonCommand>
</Configuration>
"@
$configurationPath = Join-Path $OutputDirectory 'HM-AutoSetup-Isolated-Test.wsb'
$configuration | Set-Content -LiteralPath $configurationPath -Encoding UTF8
@"
HM-AutoSetup isolated online installation test

Prepared: $(Get-Date -Format o)
Host: $env:COMPUTERNAME
Input: $inputDirectory (read-only inside Sandbox)
Evidence: $evidenceDirectory (only writable host mapping)
Guest target: E:\TempSoftware\<app folder>

Double-click HM-AutoSetup-Isolated-Test.wsb after Windows Sandbox is enabled.
The guest creates E: by substituting its own C:\HM-IsolatedDisk directory.
The host E:\TempSoftware directory is NOT mapped to the guest.
All registry changes, services, shortcuts and application files stay in the disposable guest.
Applications already installed in the host snapshot are skipped, even in a clean guest.
Results are saved incrementally in evidence\results.json and evidence\integration.log.
Do not close the Sandbox window until evidence\guest-complete.json exists.
An exit code of zero by itself is not considered installation verification.
"@ | Set-Content -LiteralPath (Join-Path $OutputDirectory 'README.txt') -Encoding UTF8
Write-Output "Sandbox configuration: $configurationPath"
Write-Output "Evidence directory: $evidenceDirectory"
if ($Launch) {
    $sandbox = Get-Command WindowsSandbox.exe -ErrorAction SilentlyContinue
    if (-not $sandbox) { throw 'Windows Sandbox launcher is unavailable. Enable the Windows Sandbox feature and restart Windows before launching this prepared .wsb.' }
    Start-Process -FilePath $sandbox.Source -ArgumentList ('"' + $configurationPath + '"') -WindowStyle Hidden
}

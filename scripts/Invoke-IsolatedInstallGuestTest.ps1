# This script is executed only by the prepared Windows Sandbox .wsb.
$ErrorActionPreference = 'Stop'
$inputRoot = 'C:\TestInput'
$evidenceRoot = 'C:\TestEvidence'
$guestRoot = 'C:\HM-IsolatedDisk'
$transcriptStarted = $false
try {
    if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'Refusing to run: Windows Sandbox account required.' }
    $hostSnapshot = Get-Content -LiteralPath (Join-Path $inputRoot 'host-installed.json') -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($hostSnapshot.MachineName) -or $hostSnapshot.MachineName -eq $env:COMPUTERNAME) {
        throw 'Refusing to run: host identity must differ from this guest.'
    }
    if (-not (Test-Path -LiteralPath $evidenceRoot -PathType Container)) { throw 'Dedicated evidence mapping is unavailable.' }
    Start-Transcript -LiteralPath (Join-Path $evidenceRoot 'guest-powershell.log') -Force | Out-Null
    $transcriptStarted = $true
    if (Test-Path -LiteralPath 'E:\') { throw 'Refusing to replace an existing E: drive.' }
    New-Item -ItemType Directory -Path $guestRoot -Force | Out-Null
    & subst.exe E: $guestRoot
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create the guest-local E: drive.' }
    $mapping = (& subst.exe | Out-String).Trim()
    if ($mapping -notmatch '(?im)^E:\\:\s*=>\s*C:\\HM-IsolatedDisk\s*$') { throw 'Unexpected guest E: drive mapping.' }
    $guestMarker = [ordered]@{
        MachineName = $env:COMPUTERNAME
        HostMachineName = $hostSnapshot.MachineName
        Isolation = 'WindowsSandbox'
        TargetRoot = 'E:\TempSoftware'
        Created = [DateTimeOffset]::Now.ToString('o')
    }
    $markerPath = Join-Path $guestRoot '.hm-autosetup-isolated-guest.json'
    $guestMarker | ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8
    $guestMarker | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidenceRoot 'guest-isolation.json') -Encoding UTF8
    $mapping | Set-Content -LiteralPath (Join-Path $evidenceRoot 'guest-subst.txt') -Encoding UTF8
    Get-CimInstance Win32_LogicalDisk | Select-Object DeviceID, DriveType, ProviderName, VolumeName, Size, FreeSpace |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidenceRoot 'guest-disks.json') -Encoding UTF8
    Get-CimInstance Win32_Service | Select-Object Name, State, StartMode, PathName |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'guest-services-before.json') -Encoding UTF8
    Get-CimInstance Win32_StartupCommand | Select-Object Name, Command, Location, User |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'guest-startup-before.json') -Encoding UTF8
    New-Item -ItemType Directory -Path 'E:\TempSoftware' -Force | Out-Null
    $harnessArguments = @('--install', '--allow-isolated-install', '--catalog', (Join-Path $inputRoot 'catalog.json'), '--output', $evidenceRoot,
        '--host-snapshot', (Join-Path $inputRoot 'host-installed.json'), '--guest-marker', $markerPath)
    $requestedApps = Join-Path $inputRoot 'requested-apps.txt'
    if (Test-Path -LiteralPath $requestedApps -PathType Leaf) { $harnessArguments += @('--apps', (Get-Content -LiteralPath $requestedApps -Raw).Trim()) }
    & (Join-Path $inputRoot 'HM-AutoSetup.Integration.exe') @harnessArguments
    $integrationExit = $LASTEXITCODE
    Get-CimInstance Win32_Service | Select-Object Name, State, StartMode, PathName |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'guest-services-after.json') -Encoding UTF8
    Get-CimInstance Win32_StartupCommand | Select-Object Name, Command, Location, User |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'guest-startup-after.json') -Encoding UTF8
    [ordered]@{ Completed = [DateTimeOffset]::Now.ToString('o'); ExitCode = $integrationExit; GuestMachineName = $env:COMPUTERNAME; HostMachineName = $hostSnapshot.MachineName;
        Note = 'See per-app results.json; zero process exit alone is not proof of target-directory installation.' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidenceRoot 'guest-complete.json') -Encoding UTF8
    Write-Output "Finished. Integration exit code: $integrationExit. Results are in C:\TestEvidence."
} catch {
    $message = $_ | Out-String
    if (Test-Path -LiteralPath $evidenceRoot -PathType Container) {
        $message | Set-Content -LiteralPath (Join-Path $evidenceRoot 'guest-failed.txt') -Encoding UTF8
    }
    Write-Error $message
} finally {
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
}

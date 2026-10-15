param(
    [Parameter(Mandatory = $true)][string]$BaselinePath,
    [Parameter(Mandatory = $true)][string]$TargetDirectory,
    [string]$AppId,
    [string]$OutputDirectory
)

# Read-only inspection. This helper never launches, stops, uninstalls or deletes software.
$ErrorActionPreference = 'Stop'
$baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
if ($baseline.MachineName -ne $env:COMPUTERNAME) { throw 'The baseline snapshot must belong to this computer.' }
$targetPath = [IO.Path]::GetFullPath($TargetDirectory).TrimEnd('\')
if (-not $targetPath.StartsWith('E:\TempSoftware\', [StringComparison]::OrdinalIgnoreCase) -or $targetPath.Length -le 'E:\TempSoftware\'.Length) {
    throw 'Select the individual test application folder below E:\TempSoftware.'
}

function Read-UninstallEntries {
    foreach ($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryHive]::LocalMachine)) {
        foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
            $rootKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
            try {
                $uninstallKey = $rootKey.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall')
                if ($null -eq $uninstallKey) { continue }
                try {
                    foreach ($keyName in $uninstallKey.GetSubKeyNames()) {
                        $entryKey = $null
                        try {
                            $entryKey = $uninstallKey.OpenSubKey($keyName)
                            if ($null -eq $entryKey) { continue }
                            $displayName = $entryKey.GetValue('DisplayName')
                            if ([string]::IsNullOrWhiteSpace($displayName)) { continue }
                            [pscustomobject][ordered]@{
                                Key = "$hive/$view/$keyName"
                                DisplayName = [string]$displayName
                                DisplayVersion = $entryKey.GetValue('DisplayVersion')
                                InstallLocation = $entryKey.GetValue('InstallLocation')
                                DisplayIcon = $entryKey.GetValue('DisplayIcon')
                                UninstallString = $entryKey.GetValue('UninstallString')
                                QuietUninstallString = $entryKey.GetValue('QuietUninstallString')
                                Publisher = $entryKey.GetValue('Publisher')
                                InstallDate = $entryKey.GetValue('InstallDate')
                            }
                        } catch [UnauthorizedAccessException] { } catch [System.Security.SecurityException] { }
                        finally { if ($null -ne $entryKey) { $entryKey.Dispose() } }
                    }
                } finally { $uninstallKey.Dispose() }
            } finally { $rootKey.Dispose() }
        }
    }
}

$comparisonFields = @('DisplayName', 'DisplayVersion', 'InstallLocation', 'DisplayIcon', 'UninstallString')
function Get-ChangedFields($before, $after) {
    @($comparisonFields | Where-Object { [string]$before.$_ -cne [string]$after.$_ })
}
$currentEntries = @(Read-UninstallEntries | Sort-Object Key)
$beforeByKey = @{}
foreach ($entry in $baseline.Registry) { $beforeByKey[[string]$entry.Key] = $entry }
$currentByKey = @{}
foreach ($entry in $currentEntries) { $currentByKey[[string]$entry.Key] = $entry }
$added = @($currentEntries | Where-Object { -not $beforeByKey.ContainsKey([string]$_.Key) })
$removed = @($baseline.Registry | Where-Object { -not $currentByKey.ContainsKey([string]$_.Key) })
$changed = @(foreach ($entry in $currentEntries) {
    if (-not $beforeByKey.ContainsKey([string]$entry.Key)) { continue }
    $changedFields = @(Get-ChangedFields $beforeByKey[[string]$entry.Key] $entry)
    if ($changedFields.Count -gt 0) {
        [pscustomobject]@{ Key = $entry.Key; ChangedFields = $changedFields; Before = $beforeByKey[[string]$entry.Key]; After = $entry }
    }
})
$preserved = @(foreach ($app in $baseline.Apps | Where-Object { @($_.Records).Count -gt 0 }) {
    $details = @(foreach ($record in $app.Records) {
        $before = $beforeByKey[[string]$record.RegistryKey]
        $after = $currentByKey[[string]$record.RegistryKey]
        $changedFields = if ($null -eq $after) { @('UninstallEntryRemoved') } elseif ($null -eq $before) { @('BaselineRawRegistryEntryMissing') } else { @(Get-ChangedFields $before $after) }
        [pscustomobject]@{
            Key = $record.RegistryKey
            Unchanged = ($changedFields.Count -eq 0)
            ChangedFields = $changedFields
            OriginalEffectiveLocation = $record.InstallLocation
            OriginalLocationStillExists = (-not [string]::IsNullOrWhiteSpace($record.InstallLocation) -and (Test-Path -LiteralPath $record.InstallLocation -PathType Container))
            BaselineVersion = $before.DisplayVersion
            CurrentVersion = $after.DisplayVersion
            BaselineInstallLocation = $before.InstallLocation
            CurrentInstallLocation = $after.InstallLocation
            BaselineDisplayIcon = $before.DisplayIcon
            CurrentDisplayIcon = $after.DisplayIcon
        }
    })
    [pscustomobject]@{ Id = $app.Id; Name = $app.Name; SameVersionAndLocation = (@($details | Where-Object { -not $_.Unchanged -or -not $_.OriginalLocationStillExists }).Count -eq 0); Records = $details }
})
$targetExists = Test-Path -LiteralPath $targetPath -PathType Container
$files = @()
if ($targetExists) { $files = @(Get-ChildItem -LiteralPath $targetPath -File -Recurse -Force -ErrorAction SilentlyContinue | Select-Object -First 10001) }
$executables = @($files | Where-Object Extension -ieq '.exe' | Select-Object -First 50 | ForEach-Object {
    [pscustomobject]@{ Path = $_.FullName; Bytes = $_.Length; FileVersion = $_.VersionInfo.FileVersion; ProductName = $_.VersionInfo.ProductName }
})
$targetProcesses = @(Get-CimInstance Win32_Process | Where-Object {
    $_.ProcessId -ne $PID -and (([string]$_.ExecutablePath).StartsWith($targetPath + '\', [StringComparison]::OrdinalIgnoreCase) -or
    ([string]$_.CommandLine).IndexOf($targetPath, [StringComparison]::OrdinalIgnoreCase) -ge 0)
} | Select-Object ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine, CreationDate,
    @{ Name = 'ExecutableInsideTarget'; Expression = { ([string]$_.ExecutablePath).StartsWith($targetPath + '\', [StringComparison]::OrdinalIgnoreCase) } })
$result = [ordered]@{
    InspectedAt = [DateTimeOffset]::Now.ToString('o')
    MachineName = $env:COMPUTERNAME
    BaselinePath = [IO.Path]::GetFullPath($BaselinePath)
    AppId = $AppId
    TargetDirectory = $targetPath
    TargetExists = [bool]$targetExists
    TargetFileCountAtLeast = $files.Count
    TargetExecutables = $executables
    TargetProcesses = $targetProcesses
    AddedUninstallEntries = $added
    RemovedUninstallEntries = $removed
    ChangedUninstallEntries = $changed
    PreservedBaselineApplications = $preserved
    AllBaselineApplicationsUnchanged = (@($preserved | Where-Object { -not $_.SameVersionAndLocation }).Count -eq 0)
    Notes = @('Read-only inspection; no process was stopped, no uninstaller was launched and no file was deleted.', 'AddedUninstallEntries are candidates for manual review against this installation run, not automatic deletion authorization.', 'SameVersionAndLocation compares the full baseline uninstall fields plus existence of the original effective install directory.')
}
$json = $result | ConvertTo-Json -Depth 15
if ($OutputDirectory) {
    $outputPath = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
    $json | Set-Content -LiteralPath (Join-Path $outputPath 'host-diff.json') -Encoding UTF8
}
Write-Output $json

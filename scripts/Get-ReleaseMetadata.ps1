param(
    [string]$Tag
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
[xml]$properties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$versionText = [string]$properties.Project.PropertyGroup.Version

function ConvertTo-StableVersion([string]$Value) {
    if ($Value -notmatch '^v?(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?$') {
        throw "稳定版版本号格式无效：$Value"
    }
    $parts = @($Matches[1], $Matches[2], $Matches[3], $Matches[4])
    return [Version]::new([int]$parts[0], [int]$parts[1], [int]$parts[2], [int]$parts[3])
}

$version = ConvertTo-StableVersion $versionText
$directoryTag = 'v{0}.{1}' -f $version.Major, $version.Minor
if ($version.Build -ne 0 -or $version.Revision -ne 0) { $directoryTag += '.' + $version.Build }
if ($version.Revision -ne 0) { $directoryTag += '.' + $version.Revision }
if (-not $Tag) { $Tag = $directoryTag }
if ((ConvertTo-StableVersion $Tag) -ne $version) {
    throw "发布标签 $Tag 与应用版本 $versionText 不一致。"
}

[pscustomobject]@{
    Version = $versionText
    Tag = $Tag
    DirectoryTag = $directoryTag
    RelativeOutputRoot = "artifacts/HM-AppKit/$directoryTag"
    NotesFile = "RELEASE_NOTES_$Tag.md"
}

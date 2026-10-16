param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePng,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\FoxDeploy.App\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore

$source = [System.IO.Path]::GetFullPath($SourcePng)
$destination = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Source PNG does not exist: $source" }

$inputStream = [System.IO.File]::OpenRead($source)
try {
    $decoder = [System.Windows.Media.Imaging.PngBitmapDecoder]::new(
        $inputStream,
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    $original = $decoder.Frames[0]
    $original.Freeze()
} finally { $inputStream.Dispose() }
if ($original.PixelWidth -ne $original.PixelHeight) { throw 'The application icon source must have a square canvas; do not silently crop it.' }

# Preserve the user's full canvas and existing alpha. The PNG is copied unchanged;
# only ICO frames are scaled, without cropping, background removal or recoloring.
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    [System.Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [System.Windows.Media.BitmapScalingMode]::HighQuality)
    $context = $visual.RenderOpen()
    try { $context.DrawImage($original, [System.Windows.Rect]::new(0, 0, $size, $size)) }
    finally { $context.Close() }
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $buffer = [System.IO.MemoryStream]::new()
    try { $encoder.Save($buffer); $frames.Add($buffer.ToArray()) }
    finally { $buffer.Dispose() }
}

New-Item -ItemType Directory -Path $destination -Force | Out-Null
$iconPath = Join-Path $destination 'HM-AppKit.ico'
$temporaryIcon = Join-Path $destination ('HM-AppKit.' + [Guid]::NewGuid().ToString('N') + '.ico.tmp')
try {
    $stream = [System.IO.File]::Create($temporaryIcon)
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$index].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose(); $stream.Dispose() }
    Move-Item -LiteralPath $temporaryIcon -Destination $iconPath -Force
} finally {
    if (Test-Path -LiteralPath $temporaryIcon -PathType Leaf) { Remove-Item -LiteralPath $temporaryIcon -Force }
}

$pngPath = Join-Path $destination 'HM-AppKit.png'
if (-not $source.Equals($pngPath, [StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item -LiteralPath $source -Destination $pngPath -Force
}
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $pngPath -Algorithm SHA256).Hash) {
    throw 'The application PNG must remain byte-for-byte identical to the supplied artwork.'
}
Write-Output "PNG: $pngPath ($($original.PixelWidth)x$($original.PixelHeight), original bytes preserved)"
Write-Output "ICO: $iconPath ($($sizes -join ', ') px, 32-bit PNG frames)"

# Builds src/MDEditor.App/Assets/MDEditor.ico from Resources/AppIcon.png (HANDOFF §10.5):
# crops to the 824 px squircle without its shadow and writes PNG-compressed frames.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$source = Join-Path $root 'Resources\AppIcon.png'
$targetDir = Join-Path $root 'windows\src\MDEditor.App\Assets'
$target = Join-Path $targetDir 'MDEditor.ico'
New-Item -ItemType Directory -Force $targetDir | Out-Null

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$master = [System.Drawing.Bitmap]::FromFile($source)
$frames = foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CompositingMode = 'SourceCopy'
    $graphics.InterpolationMode = 'HighQualityBicubic'
    $graphics.PixelOffsetMode = 'HighQuality'
    $graphics.SmoothingMode = 'HighQuality'
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $attributes = New-Object System.Drawing.Imaging.ImageAttributes
    $attributes.SetWrapMode('TileFlipXY')
    $graphics.DrawImage($master, $rect, 100, 100, 824, 824, 'Pixel', $attributes)
    $graphics.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    , @{ Size = $size; Bytes = $stream.ToArray() }
}
$master.Dispose()

$output = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $output
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $byteSize = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
    $writer.Write([byte]$byteSize); $writer.Write([byte]$byteSize)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
    $offset += $frame.Bytes.Length
}
foreach ($frame in $frames) { $writer.Write($frame.Bytes) }
$writer.Flush()
[System.IO.File]::WriteAllBytes($target, $output.ToArray())
Write-Host "Wrote $target ($($output.Length) bytes)"

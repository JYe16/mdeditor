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

# Windows-only rim (approved for v1.1). The right half of the artwork is near-white, so on a light taskbar its outer edge
# disappears at small sizes (about 1.05:1 against #F3F3F3): the thin dark hairline macOS has does not survive the downscale.
# macOS must not change, so the fix lives here and not in scripts/make-icon.swift or Resources/AppIcon.png: on the small
# frames only, the outermost pixels of the squircle are blended towards the ink colour of its left half.
$rimMaxSize = 48
$rimInk = 0x2B, 0x2F, 0x4A
$rimStrength = 0.7

function Add-Rim([System.Drawing.Bitmap]$bitmap) {
    $n = $bitmap.Width
    $alpha = New-Object 'byte[,]' $n, $n
    for ($y = 0; $y -lt $n; $y++) { for ($x = 0; $x -lt $n; $x++) { $alpha[$x, $y] = $bitmap.GetPixel($x, $y).A } }
    for ($y = 0; $y -lt $n; $y++) {
        for ($x = 0; $x -lt $n; $x++) {
            $a = $alpha[$x, $y]
            if ($a -eq 0) { continue }
            $edge = $false
            foreach ($d in @(@(-1, 0), @(1, 0), @(0, -1), @(0, 1))) {
                $nx = $x + $d[0]; $ny = $y + $d[1]
                if ($nx -lt 0 -or $ny -lt 0 -or $nx -ge $n -or $ny -ge $n -or $alpha[$nx, $ny] -lt 128) { $edge = $true; break }
            }
            if (-not $edge) { continue }
            $p = $bitmap.GetPixel($x, $y)
            $r = [int]($p.R * (1 - $rimStrength) + $rimInk[0] * $rimStrength)
            $g = [int]($p.G * (1 - $rimStrength) + $rimInk[1] * $rimStrength)
            $b = [int]($p.B * (1 - $rimStrength) + $rimInk[2] * $rimStrength)
            $bitmap.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($a, $r, $g, $b))
        }
    }
}

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
    if ($size -le $rimMaxSize) { Add-Rim $bitmap }
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

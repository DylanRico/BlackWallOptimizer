param([Parameter(Mandatory=$true)][string]$SourcePng)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$branding = Join-Path $root 'assets\branding'
New-Item -ItemType Directory -Path $branding -Force | Out-Null
$original = Join-Path $branding 'blackwall-source.png'
if ((Resolve-Path -LiteralPath $SourcePng).Path -ne $original) {
    Copy-Item -LiteralPath $SourcePng -Destination $original -Force
}
$source = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $SourcePng).Path)
$frames = @()
try {
    foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
        $bitmap = New-Object System.Drawing.Bitmap($size, $size)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $stream = New-Object System.IO.MemoryStream
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.DrawImage($source, 0, 0, $size, $size)
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $bytes = $stream.ToArray()
            # Use classic BGRA/DIB frames at small sizes and PNG for high-resolution frames.
            if ($size -le 64) {
                $dib = New-Object System.IO.MemoryStream
                $dibWriter = New-Object System.IO.BinaryWriter($dib)
                try {
                    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
                    $dibWriter.Write([uint32]40)
                    $dibWriter.Write([int32]$size); $dibWriter.Write([int32]($size * 2))
                    $dibWriter.Write([uint16]1); $dibWriter.Write([uint16]32)
                    $dibWriter.Write([uint32]0); $dibWriter.Write([uint32]($size * $size * 4))
                    $dibWriter.Write([int32]0); $dibWriter.Write([int32]0)
                    $dibWriter.Write([uint32]0); $dibWriter.Write([uint32]0)
                    for ($row = $size - 1; $row -ge 0; $row--) {
                        for ($column = 0; $column -lt $size; $column++) {
                            $pixel = $bitmap.GetPixel($column, $row)
                            $dibWriter.Write([byte]$pixel.B); $dibWriter.Write([byte]$pixel.G)
                            $dibWriter.Write([byte]$pixel.R); $dibWriter.Write([byte]$pixel.A)
                        }
                    }
                    for ($row = $size - 1; $row -ge 0; $row--) {
                        $mask = New-Object byte[] $maskStride
                        for ($column = 0; $column -lt $size; $column++) {
                            if ($bitmap.GetPixel($column, $row).A -eq 0) {
                                $maskIndex = [int][Math]::Floor($column / 8.0)
                                $mask[$maskIndex] = $mask[$maskIndex] -bor (0x80 -shr ($column % 8))
                            }
                        }
                        $dibWriter.Write([byte[]]$mask)
                    }
                    $bytes = $dib.ToArray()
                } finally { $dibWriter.Dispose(); $dib.Dispose() }
            }
            $frames += [pscustomobject]@{ Size=$size; Bytes=$bytes }
            if ($size -eq 256) {
                foreach ($relative in @('assets\icons\blackwall-mark.png', 'src-tauri\icons\icon.png', 'src\blackwall-mark.png')) {
                    [System.IO.File]::WriteAllBytes((Join-Path $root $relative), $bytes)
                }
            }
        } finally { $graphics.Dispose(); $bitmap.Dispose(); $stream.Dispose() }
    }
} finally { $source.Dispose() }
$icoPath = Join-Path $root 'src-tauri\icons\icon.ico'
$file = [System.IO.File]::Create($icoPath)
$writer = New-Object System.IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Output "Icono RGBA: 16, 24, 32, 48, 64, 128 y 256 px. $icoPath"

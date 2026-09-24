param()
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$source = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot "..\src\YumeShelf\Assets\YumeShelfIcon.png"))
try {
    $frames = @()
    foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
        $bitmap = New-Object System.Drawing.Bitmap($size, $size)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $stream = New-Object System.IO.MemoryStream
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            # Small native frames use less padding; preserve the transparent rounded corners.
            $inset = if ($size -le 32) { $source.Width / 32 } else { 0 }
            $graphics.DrawImage($source, [System.Drawing.Rectangle]::new(0, 0, $size, $size),
                [single]$inset, [single]$inset, [single]($source.Width - 2 * $inset), [single]($source.Height - 2 * $inset), [System.Drawing.GraphicsUnit]::Pixel)
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames += [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
        } finally { $graphics.Dispose(); $bitmap.Dispose(); $stream.Dispose() }
    }
    $target = Join-Path $PSScriptRoot "..\src\YumeShelf\Assets\YumeShelf.ico"
    $file = [System.IO.File]::Create($target)
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
    Write-Output "Generated application ICO (16/20/24/32/40/48/64/128/256): $target"
} finally { $source.Dispose() }

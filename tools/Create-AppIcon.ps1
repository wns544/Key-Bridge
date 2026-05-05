param(
    [string]$Source = "Assets\keybridge-icon-source.png",
    [string]$PngOutput = "Assets\keybridge-icon.png",
    [string]$IcoOutput = "Assets\keybridge.ico"
)

Add-Type -AssemblyName System.Drawing

$projectRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $projectRoot $Source
$pngOutputPath = Join-Path $projectRoot $PngOutput
$icoOutputPath = Join-Path $projectRoot $IcoOutput

$sourceImage = [System.Drawing.Image]::FromFile($sourcePath)
try {
    $squareSize = [Math]::Min($sourceImage.Width, $sourceImage.Height)
    $sourceX = [Math]::Floor(($sourceImage.Width - $squareSize) / 2)
    $sourceY = [Math]::Floor(($sourceImage.Height - $squareSize) / 2)
    $sourceRect = [System.Drawing.Rectangle]::new($sourceX, $sourceY, $squareSize, $squareSize)

    $squareBitmap = [System.Drawing.Bitmap]::new($squareSize, $squareSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($squareBitmap)
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($sourceImage, [System.Drawing.Rectangle]::new(0, 0, $squareSize, $squareSize), $sourceRect, [System.Drawing.GraphicsUnit]::Pixel)
        }
        finally {
            $graphics.Dispose()
        }

        $squareBitmap.Save($pngOutputPath, [System.Drawing.Imaging.ImageFormat]::Png)

        $sizes = @(16, 24, 32, 48, 64, 128, 256)
        $pngImages = New-Object System.Collections.Generic.List[byte[]]

        foreach ($size in $sizes) {
            $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                    $graphics.DrawImage($squareBitmap, 0, 0, $size, $size)
                }
                finally {
                    $graphics.Dispose()
                }

                $stream = [System.IO.MemoryStream]::new()
                try {
                    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                    $pngImages.Add($stream.ToArray())
                }
                finally {
                    $stream.Dispose()
                }
            }
            finally {
                $bitmap.Dispose()
            }
        }

        $output = [System.IO.File]::Create($icoOutputPath)
        $writer = [System.IO.BinaryWriter]::new($output)
        try {
            $writer.Write([UInt16]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]$sizes.Count)

            $offset = 6 + (16 * $sizes.Count)
            for ($index = 0; $index -lt $sizes.Count; $index++) {
                $size = $sizes[$index]
                $bytes = $pngImages[$index]
                $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
                $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([UInt16]1)
                $writer.Write([UInt16]32)
                $writer.Write([UInt32]$bytes.Length)
                $writer.Write([UInt32]$offset)
                $offset += $bytes.Length
            }

            foreach ($bytes in $pngImages) {
                $writer.Write($bytes)
            }
        }
        finally {
            $writer.Dispose()
            $output.Dispose()
        }
    }
    finally {
        $squareBitmap.Dispose()
    }
}
finally {
    $sourceImage.Dispose()
}

Get-Item $pngOutputPath, $icoOutputPath | Select-Object FullName, Length

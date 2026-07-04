param(
    [string]$Source = "Assets\keybridge-icon-source.png",
    [string]$PngOutput = "Assets\keybridge-icon.png",
    [string]$IcoOutput = "Assets\keybridge.ico"
)

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class IconAlphaTools
{
    private static void AddCandidate(
        int x,
        int y,
        int width,
        int height,
        int stride,
        byte threshold,
        byte[] bytes,
        bool[] visited,
        Queue<int> queue)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return;
        }

        int index = y * width + x;
        if (visited[index])
        {
            return;
        }

        visited[index] = true;
        int offset = y * stride + (x * 4);
        byte b = bytes[offset];
        byte g = bytes[offset + 1];
        byte r = bytes[offset + 2];
        byte a = bytes[offset + 3];

        if (a > 0 && r >= threshold && g >= threshold && b >= threshold)
        {
            queue.Enqueue(index);
        }
    }

    public static void RemoveBorderConnectedWhite(Bitmap bitmap, byte threshold)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        Rectangle rect = new Rectangle(0, 0, width, height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        int byteCount = Math.Abs(stride) * height;
        byte[] bytes = new byte[byteCount];
        Marshal.Copy(data.Scan0, bytes, 0, byteCount);

        bool[] visited = new bool[width * height];
        Queue<int> queue = new Queue<int>();

        for (int index = 0; index < width; index++)
        {
            AddCandidate(index, 0, width, height, stride, threshold, bytes, visited, queue);
            AddCandidate(index, height - 1, width, height, stride, threshold, bytes, visited, queue);
        }

        for (int index = 0; index < height; index++)
        {
            AddCandidate(0, index, width, height, stride, threshold, bytes, visited, queue);
            AddCandidate(width - 1, index, width, height, stride, threshold, bytes, visited, queue);
        }

        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            int x = index % width;
            int y = index / width;
            int offset = y * stride + (x * 4);
            bytes[offset + 3] = 0;

            AddCandidate(x + 1, y, width, height, stride, threshold, bytes, visited, queue);
            AddCandidate(x - 1, y, width, height, stride, threshold, bytes, visited, queue);
            AddCandidate(x, y + 1, width, height, stride, threshold, bytes, visited, queue);
            AddCandidate(x, y - 1, width, height, stride, threshold, bytes, visited, queue);
        }

        Marshal.Copy(bytes, 0, data.Scan0, byteCount);
        bitmap.UnlockBits(data);
    }
}
"@

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

        [IconAlphaTools]::RemoveBorderConnectedWhite($squareBitmap, 238)

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

Get-Item $sourcePath, $pngOutputPath, $icoOutputPath | Select-Object FullName, Length

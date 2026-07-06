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

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Rect,
        [float]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($Rect.X, $Rect.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($Rect.Right - $diameter, $Rect.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($Rect.Right - $diameter, $Rect.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rect.X, $Rect.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()

    return $path
}

function Fill-RoundedRectangle {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Brush]$Brush,
        [System.Drawing.RectangleF]$Rect,
        [float]$Radius
    )

    $path = New-RoundedRectanglePath -Rect $Rect -Radius $Radius
    try {
        $Graphics.FillPath($Brush, $path)
    }
    finally {
        $path.Dispose()
    }
}

function Draw-RoundedRectangle {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Pen]$Pen,
        [System.Drawing.RectangleF]$Rect,
        [float]$Radius
    )

    $path = New-RoundedRectanglePath -Rect $Rect -Radius $Radius
    try {
        $Graphics.DrawPath($Pen, $path)
    }
    finally {
        $path.Dispose()
    }
}

function New-KeycapBitmap {
    param([int]$Size)

    $scale = $Size / 1024.0
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.ScaleTransform($scale, $scale)

        $shadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(48, 15, 23, 42))
        $softShadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(24, 15, 23, 42))
        $keyBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
        $topBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 248, 251, 255))
        $borderPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 216, 226, 238), 18)
        $innerPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 238, 243, 249), 10)

        try {
            Fill-RoundedRectangle $graphics $softShadowBrush ([System.Drawing.RectangleF]::new(76, 112, 872, 872)) 168
            Fill-RoundedRectangle $graphics $shadowBrush ([System.Drawing.RectangleF]::new(90, 96, 844, 844)) 154
            Fill-RoundedRectangle $graphics $keyBrush ([System.Drawing.RectangleF]::new(70, 62, 884, 884)) 160
            Fill-RoundedRectangle $graphics $topBrush ([System.Drawing.RectangleF]::new(100, 92, 824, 790)) 132
            Draw-RoundedRectangle $graphics $borderPen ([System.Drawing.RectangleF]::new(79, 71, 866, 866)) 152
            Draw-RoundedRectangle $graphics $innerPen ([System.Drawing.RectangleF]::new(130, 124, 764, 704)) 104
        }
        finally {
            $shadowBrush.Dispose()
            $softShadowBrush.Dispose()
            $keyBrush.Dispose()
            $topBrush.Dispose()
            $borderPen.Dispose()
            $innerPen.Dispose()
        }

        $fontFamily = "Segoe UI Variable Display"
        $font = [System.Drawing.Font]::new($fontFamily, 590, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $textBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 7, 27, 57))
        $format = [System.Drawing.StringFormat]::new()
        $format.Alignment = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center

        try {
            $graphics.DrawString("K", $font, $textBrush, [System.Drawing.RectangleF]::new(56, 42, 912, 900), $format)
        }
        finally {
            $format.Dispose()
            $textBrush.Dispose()
            $font.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

$sourceBitmap = New-KeycapBitmap -Size 1024
try {
    $sourceBitmap.Save($sourcePath, [System.Drawing.Imaging.ImageFormat]::Png)
    $sourceBitmap.Save($pngOutputPath, [System.Drawing.Imaging.ImageFormat]::Png)

    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $pngImages = New-Object System.Collections.Generic.List[byte[]]

    foreach ($size in $sizes) {
        $bitmap = New-KeycapBitmap -Size $size
        try {
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
    $sourceBitmap.Dispose()
}

Get-Item $sourcePath, $pngOutputPath, $icoOutputPath | Select-Object FullName, Length

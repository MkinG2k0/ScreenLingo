param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'ScreenLingo.ico')
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Rectangle,
        [float]$Radius
    )

    $diameter = $Radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($Rectangle.Left, $Rectangle.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Top, $diameter, $diameter, 270, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rectangle.Left, $Rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPngBytes {
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $margin = [Math]::Max(1, $Size * 0.047)
    $body = New-Object System.Drawing.RectangleF $margin, $margin, ($Size - 2 * $margin), ($Size - 2 * $margin)
    $path = New-RoundedRectanglePath -Rectangle $body -Radius ($Size * 0.24)
    $background = New-Object System.Drawing.Drawing2D.LinearGradientBrush $body, ([System.Drawing.Color]::FromArgb(38, 198, 218)), ([System.Drawing.Color]::FromArgb(124, 77, 255)), 45
    $graphics.FillPath($background, $path)

    $outlinePath = New-RoundedRectanglePath -Rectangle (New-Object System.Drawing.RectangleF ($margin + 1), ($margin + 1), ($Size - 2 * $margin - 2), ($Size - 2 * $margin - 2)) -Radius ($Size * 0.21)
    $outline = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(110, 255, 255, 255)), ([Math]::Max(1, $Size * 0.025))
    $graphics.DrawPath($outline, $outlinePath)

    $font = New-Object System.Drawing.Font 'Segoe UI', ($Size * 0.47), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $letterArea = New-Object System.Drawing.RectangleF ($Size * 0.10), ($Size * 0.10), ($Size * 0.58), ($Size * 0.58)
    $shadowArea = New-Object System.Drawing.RectangleF ($letterArea.X + $Size * 0.025), ($letterArea.Y + $Size * 0.035), $letterArea.Width, $letterArea.Height
    $shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(75, 0, 0, 0))
    $graphics.DrawString('A', $font, $shadow, $shadowArea, $format)
    $graphics.DrawString('A', $font, [System.Drawing.Brushes]::White, $letterArea, $format)

    $arrow = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.4, $Size * 0.07))
    $arrow.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arrow.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($arrow, $Size * 0.50, $Size * 0.72, $Size * 0.79, $Size * 0.72)
    $graphics.DrawLine($arrow, $Size * 0.79, $Size * 0.72, $Size * 0.69, $Size * 0.62)
    $graphics.DrawLine($arrow, $Size * 0.79, $Size * 0.72, $Size * 0.69, $Size * 0.82)

    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()

    $stream.Dispose()
    $arrow.Dispose()
    $shadow.Dispose()
    $format.Dispose()
    $font.Dispose()
    $outline.Dispose()
    $outlinePath.Dispose()
    $background.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()

    return ,$bytes
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($size in $sizes) {
    $images += ,(New-IconPngBytes -Size $size)
}

$file = [System.IO.File]::Create($OutputPath)
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$images.Count)

$offset = 6 + 16 * $images.Count
for ($index = 0; $index -lt $images.Count; $index++) {
    $size = $sizes[$index]
    $iconDimension = if ($size -eq 256) { 0 } else { $size }
    $writer.Write([byte]$iconDimension)
    $writer.Write([byte]$iconDimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$index].Length)
    $writer.Write([uint32]$offset)
    $offset += $images[$index].Length
}

foreach ($image in $images) {
    $writer.Write($image)
}

$writer.Dispose()
$file.Dispose()
Write-Host "Generated: $OutputPath"

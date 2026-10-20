param([string]$OutPath)
# アプリアイコン（角丸の正方形にゲージの円弧）を複数サイズの PNG を内包した .ico として生成する
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $radius = [Math]::Max(2, [int]($size * 0.22))
    $diameter = $radius * 2
    $edge = $size - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $diameter, $diameter, 180, 90)
    $path.AddArc($edge - $diameter, 0, $diameter, $diameter, 270, 90)
    $path.AddArc($edge - $diameter, $edge - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc(0, $edge - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    $background = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0xD9, 0x77, 0x57))
    $g.FillPath($background, $path)

    $margin = [single]($size * 0.24)
    $gaugeSize = [single]($size - 2 * $margin)
    $gauge = [System.Drawing.RectangleF]::new($margin, $margin, $gaugeSize, $gaugeSize)
    $penWidth = [single][Math]::Max(1.5, $size * 0.11)

    $trackPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(110, 255, 255, 255)), $penWidth
    $trackPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $trackPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($trackPen, $gauge, [single]135, [single]270)

    $valuePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $penWidth
    $valuePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $valuePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($valuePen, $gauge, [single]135, [single]170)

    $dotSize = [single]($size * 0.16)
    $dotPos = [single](($size - $dotSize) / 2)
    $g.FillEllipse([System.Drawing.Brushes]::White, $dotPos, $dotPos, $dotSize, $dotSize)

    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $stream.ToArray()
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($s in $sizes) { $images += , (New-IconPng $s) }

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$images[$i].Length); $writer.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image) }
$writer.Flush()
[System.IO.File]::WriteAllBytes($OutPath, $out.ToArray())

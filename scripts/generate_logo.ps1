Add-Type -AssemblyName System.Drawing

$width = 512
$height = 512
$bmp = New-Object System.Drawing.Bitmap $width, $height
$g = [System.Drawing.Graphics]::FromImage($bmp)

$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.Clear([System.Drawing.Color]::Transparent)

# 1. Background Rounded Badge (Primary Orange Gradient #FF7900 -> #F3781D)
$rect = New-Object System.Drawing.Rectangle 16, 16, 480, 480
$c1 = [System.Drawing.Color]::FromArgb(255, 255, 121, 0)
$c2 = [System.Drawing.Color]::FromArgb(255, 243, 120, 29)
$mode = [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal
$brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, $c1, $c2, $mode)

$radius = 100
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc(16, 16, $radius*2, $radius*2, 180, 90)
$path.AddArc(496 - $radius*2, 16, $radius*2, $radius*2, 270, 90)
$path.AddArc(496 - $radius*2, 496 - $radius*2, $radius*2, $radius*2, 0, 90)
$path.AddArc(16, 496 - $radius*2, $radius*2, $radius*2, 90, 90)
$path.CloseFigure()

$g.FillPath($brush, $path)

# Subtle inner glow / border
$borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 255, 255, 255), 4)
$g.DrawPath($borderPen, $path)

# 2. Draw Printer in Crisp White & Light Cream
$whiteBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
$creamBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 251, 248))

# Paper In Top Tray
$paperTopRect = New-Object System.Drawing.Rectangle 160, 110, 192, 110
$g.FillRectangle($creamBrush, $paperTopRect)

# Printer Main Body
$bodyRadius = 32
$bodyPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$bodyPath.AddArc(96, 200, $bodyRadius*2, $bodyRadius*2, 180, 90)
$bodyPath.AddArc(416 - $bodyRadius*2, 200, $bodyRadius*2, $bodyRadius*2, 270, 90)
$bodyPath.AddArc(416 - $bodyRadius*2, 350 - $bodyRadius*2, $bodyRadius*2, $bodyRadius*2, 0, 90)
$bodyPath.AddArc(96, 350 - $bodyRadius*2, $bodyRadius*2, $bodyRadius*2, 90, 90)
$bodyPath.CloseFigure()
$g.FillPath($whiteBrush, $bodyPath)

# Paper Slot (Horizontal slit)
$slotBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 121, 0))
$g.FillRectangle($slotBrush, 140, 270, 232, 14)

# Paper Out Bottom Tray
$paperBottomRect = New-Object System.Drawing.Rectangle 144, 278, 224, 116
$g.FillRectangle($creamBrush, $paperBottomRect)
$linePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 255, 130, 31), 8)
$linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$g.DrawLine($linePen, 180, 318, 332, 318)
$g.DrawLine($linePen, 180, 348, 290, 348)

# Printer LED indicator dot (Green #15B84A)
$ledBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 21, 184, 74))
$g.FillEllipse($ledBrush, 360, 232, 16, 16)

# 3. Wireless / Remote Network Signal Waves (Top Right)
$wavePen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 16)
$wavePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$wavePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

# Wave 1 (inner)
$g.DrawArc($wavePen, 320, 70, 90, 90, 270, 90)
# Wave 2 (middle)
$wavePen.Width = 14
$g.DrawArc($wavePen, 290, 40, 150, 150, 270, 90)
# Wave 3 (outer)
$wavePen.Width = 12
$g.DrawArc($wavePen, 260, 10, 210, 210, 270, 90)

$g.Dispose()

# Save PNG
$pngPath1 = "src/RPM.App/Assets/logo.png"
$pngPath2 = "docs/assets/logo.png"
$bmp.Save($pngPath1, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Save($pngPath2, [System.Drawing.Imaging.ImageFormat]::Png)

# Generate multi-size app.ico
$sizes = @(256, 128, 64, 48, 32, 16)
$msList = @()
foreach ($s in $sizes) {
    $icoBmp = New-Object System.Drawing.Bitmap $s, $s
    $ig = [System.Drawing.Graphics]::FromImage($icoBmp)
    $ig.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $ig.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $ig.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $ig.Clear([System.Drawing.Color]::Transparent)
    $ig.DrawImage($bmp, 0, 0, $s, $s)
    $ig.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $icoBmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $icoBmp.Dispose()
    $msList += @{ Size = $s; Stream = $ms; Bytes = $ms.ToArray() }
}
$bmp.Dispose()

$destIco = "src/RPM.App/Assets/app.ico"
$fs = [System.IO.File]::Create($destIco)
$bw = New-Object System.IO.BinaryWriter $fs

$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$msList.Count)

$offset = 6 + (16 * $msList.Count)
foreach ($item in $msList) {
    $w = if ($item.Size -ge 256) { [byte]0 } else { [byte]$item.Size }
    $h = if ($item.Size -ge 256) { [byte]0 } else { [byte]$item.Size }
    $bw.Write($w)
    $bw.Write($h)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$item.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $item.Bytes.Length
}

foreach ($item in $msList) {
    $bw.Write($item.Bytes)
    $item.Stream.Dispose()
}

$bw.Flush()
$bw.Dispose()
$fs.Dispose()

Write-Host "Tao thanh cong logo chinh thuc Remote Print Manager (Cam #FF7900)!"

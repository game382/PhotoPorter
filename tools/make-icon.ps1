# Draws the PhotoPorter icon (phone -> arrow -> photo) and writes a multi-size PNG-based .ico
param([string]$Out = "$PSScriptRoot\..\PhotoPorter.ico")
Add-Type -AssemblyName System.Drawing

function Round-Rect($g, $brush, $x, $y, $w, $h, $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $r*2, $r*2, 180, 90); $p.AddArc($x+$w-$r*2, $y, $r*2, $r*2, 270, 90)
    $p.AddArc($x+$w-$r*2, $y+$h-$r*2, $r*2, $r*2, 0, 90); $p.AddArc($x, $y+$h-$r*2, $r*2, $r*2, 90, 90)
    $p.CloseFigure(); $g.FillPath($brush, $p); $p.Dispose()
}

function Draw([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 256.0
    # background tile: blue gradient
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0,0), (New-Object System.Drawing.Point $s,$s), ([System.Drawing.Color]::FromArgb(255,40,120,230)), ([System.Drawing.Color]::FromArgb(255,10,40,110))
    Round-Rect $g $bg (4*$k) (4*$k) (248*$k) (248*$k) (48*$k)
    # phone
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $dark  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,20,50,120))
    Round-Rect $g $white (30*$k) (48*$k) (78*$k) (150*$k) (16*$k)
    Round-Rect $g $dark (38*$k) (62*$k) (62*$k) (122*$k) (8*$k)
    # arrow
    $gold = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,255,212,64))
    $pts = @(
        (New-Object System.Drawing.PointF (112*$k),(112*$k)), (New-Object System.Drawing.PointF (148*$k),(112*$k)),
        (New-Object System.Drawing.PointF (148*$k),(94*$k)),  (New-Object System.Drawing.PointF (180*$k),(126*$k)),
        (New-Object System.Drawing.PointF (148*$k),(158*$k)), (New-Object System.Drawing.PointF (148*$k),(140*$k)),
        (New-Object System.Drawing.PointF (112*$k),(140*$k)))
    $g.FillPolygon($gold, [System.Drawing.PointF[]]$pts)
    # photo card with mountain + sun
    Round-Rect $g $white (162*$k) (150*$k) (78*$k) (66*$k) (8*$k)
    $sky = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,120,190,255))
    $g.FillRectangle($sky, 168*$k, 156*$k, 66*$k, 54*$k)
    $g.FillEllipse($gold, 210*$k, 162*$k, 16*$k, 16*$k)
    $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,40,150,80))
    $m = @((New-Object System.Drawing.PointF (168*$k),(210*$k)), (New-Object System.Drawing.PointF (192*$k),(176*$k)),
           (New-Object System.Drawing.PointF (210*$k),(196*$k)), (New-Object System.Drawing.PointF (220*$k),(186*$k)),
           (New-Object System.Drawing.PointF (234*$k),(210*$k)))
    $g.FillPolygon($green, [System.Drawing.PointF[]]$m)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$imgs = $sizes | ForEach-Object { ,(Draw $_) }
$fs = [System.IO.File]::Create($Out); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $imgs[$i]
    $w.Write([byte]($(if ($s -ge 256) {0} else {$s}))); $w.Write([byte]($(if ($s -ge 256) {0} else {$s})))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$d.Length); $w.Write([uint32]$offset); $offset += $d.Length
}
foreach ($d in $imgs) { $w.Write($d) }
$w.Close()
[System.IO.File]::WriteAllBytes("$PSScriptRoot\..\icon-preview.png", (Draw 256))
"wrote $Out"

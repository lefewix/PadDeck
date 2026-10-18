# Draws assets\PadDeck.ico (teal rounded square with a 3x2 key grid), PNG-compressed entries.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\assets\PadDeck.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function Round-Rect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 45, 212, 191))
    $g.FillPath($bg, (Round-Rect 0 0 $s $s ([Math]::Max(3, $s * 0.22))))
    $fg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 15, 16, 18))
    $inner = $s * 0.70
    $gap = [Math]::Max(1, $s * 0.07)
    $kw = ($inner - 2 * $gap) / 3
    $kh = ($s * 0.50 - $gap) / 2
    $x0 = ($s - $inner) / 2
    $y0 = ($s - (2 * $kh + $gap)) / 2
    for ($r = 0; $r -lt 2; $r++) {
        for ($c = 0; $c -lt 3; $c++) {
            $x = $x0 + $c * ($kw + $gap)
            $y = $y0 + $r * ($kh + $gap)
            $g.FillPath($fg, (Round-Rect $x $y $kw $kh ([Math]::Max(0.5, $kw * 0.22))))
        }
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"wrote $out"

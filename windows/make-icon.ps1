# 앱 아이콘(사진 모양) 생성 → app.ico (크기별 PNG 를 담은 ICO)
Add-Type -AssemblyName System.Drawing
$d = $PSScriptRoot

function Draw([int]$S) {
  $bmp = New-Object System.Drawing.Bitmap $S, $S
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
  $k = $S / 256.0
  # 둥근 사각형 배경
  $r = 56 * $k; $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $m = 8 * $k; $w = $S - 2 * $m
  $p.AddArc($m, $m, $r, $r, 180, 90); $p.AddArc($m + $w - $r, $m, $r, $r, 270, 90)
  $p.AddArc($m + $w - $r, $m + $w - $r, $r, $r, 0, 90); $p.AddArc($m, $m + $w - $r, $r, $r, 90, 90); $p.CloseFigure()
  $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($S, $S)), ([System.Drawing.Color]::FromArgb(255, 255, 186, 73)), ([System.Drawing.Color]::FromArgb(255, 244, 128, 54))
  $g.FillPath($bg, $p)
  # 사진 틀
  $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
  $g.FillRectangle($white, 52 * $k, 70 * $k, 152 * $k, 116 * $k)
  $sky = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 120, 190, 245))
  $g.FillRectangle($sky, 64 * $k, 82 * $k, 128 * $k, 92 * $k)
  # 해와 산
  $sun = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 214, 64))
  $g.FillEllipse($sun, 150 * $k, 92 * $k, 26 * $k, 26 * $k)
  $hill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 76, 175, 80))
  $g.FillPolygon($hill, [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new(64 * $k, 174 * $k), [System.Drawing.PointF]::new(108 * $k, 116 * $k),
    [System.Drawing.PointF]::new(140 * $k, 152 * $k), [System.Drawing.PointF]::new(158 * $k, 134 * $k),
    [System.Drawing.PointF]::new(192 * $k, 174 * $k)))
  # 아래 화살표 (다운로드)
  $arrow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
  $g.FillRectangle($arrow, 118 * $k, 192 * $k, 20 * $k, 20 * $k)
  $g.FillPolygon($arrow, [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new(100 * $k, 208 * $k), [System.Drawing.PointF]::new(156 * $k, 208 * $k), [System.Drawing.PointF]::new(128 * $k, 236 * $k)))
  $g.Dispose()
  return $bmp
}

$sizes = 256, 48, 32, 16
$pngs = foreach ($s in $sizes) { $b = Draw $s; $ms = New-Object IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); , $ms.ToArray() }
$out = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $len = $pngs[$i].Length
  $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
  $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
  $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset); $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
[IO.File]::WriteAllBytes((Join-Path $d 'app.ico'), $out.ToArray())

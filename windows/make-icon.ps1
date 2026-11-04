# 키즈노트 다운로더 아이콘(사진 + 아래 화살표) → app.ico, icon.png(256)
# 크기마다 4배로 따로 그려 한 번만 줄이고, 작은 크기는 선을 굵게. 자세한 방식은 icon-lib.ps1
Add-Type -AssemblyName System.Drawing
$d = $PSScriptRoot

function P([double]$x, [double]$y) { [System.Drawing.PointF]::new($x, $y) }

function Draw-Icon($g, [double]$k, [int]$size) {
  $small = $size -le 24
  # 둥근 사각형 배경 (주황 그라데이션)
  $m = 8 * $k; $w = 256 * $k - 2 * $m; $r = 56 * $k
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddArc($m, $m, $r, $r, 180, 90); $p.AddArc($m + $w - $r, $m, $r, $r, 270, 90)
  $p.AddArc($m + $w - $r, $m + $w - $r, $r, $r, 0, 90); $p.AddArc($m, $m + $w - $r, $r, $r, 90, 90); $p.CloseFigure()
  $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (P 0 0), (P (256 * $k) (256 * $k)), ([System.Drawing.Color]::FromArgb(255, 186, 73)), ([System.Drawing.Color]::FromArgb(244, 128, 54))
  $g.FillPath($bg, $p)
  $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
  if ($small) {
    # 작은 크기: 디테일을 줄이고 큼직하게 (사진 틀 + 산 + 화살표)
    $g.FillRectangle($white, 44 * $k, 58 * $k, 168 * $k, 128 * $k)
    $sky = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(120, 190, 245))
    $g.FillRectangle($sky, 62 * $k, 76 * $k, 132 * $k, 92 * $k)
    $hill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(56, 160, 72))
    $g.FillPolygon($hill, [System.Drawing.PointF[]]@((P (62 * $k) (168 * $k)), (P (110 * $k) (108 * $k)), (P (150 * $k) (150 * $k)), (P (168 * $k) (132 * $k)), (P (194 * $k) (168 * $k))))
    $g.FillRectangle($white, 112 * $k, 186 * $k, 32 * $k, 24 * $k)
    $g.FillPolygon($white, [System.Drawing.PointF[]]@((P (88 * $k) (204 * $k)), (P (168 * $k) (204 * $k)), (P (128 * $k) (244 * $k))))
    return
  }
  # 사진 틀
  $g.FillRectangle($white, 52 * $k, 70 * $k, 152 * $k, 116 * $k)
  $sky = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(120, 190, 245))
  $g.FillRectangle($sky, 64 * $k, 82 * $k, 128 * $k, 92 * $k)
  # 해와 산
  $sun = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 214, 64))
  $g.FillEllipse($sun, 150 * $k, 92 * $k, 26 * $k, 26 * $k)
  $hill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(76, 175, 80))
  $g.FillPolygon($hill, [System.Drawing.PointF[]]@((P (64 * $k) (174 * $k)), (P (108 * $k) (116 * $k)), (P (140 * $k) (152 * $k)), (P (158 * $k) (134 * $k)), (P (192 * $k) (174 * $k))))
  # 아래 화살표 (다운로드)
  $g.FillRectangle($white, 118 * $k, 192 * $k, 20 * $k, 20 * $k)
  $g.FillPolygon($white, [System.Drawing.PointF[]]@((P (100 * $k) (208 * $k)), (P (156 * $k) (208 * $k)), (P (128 * $k) (236 * $k))))
}

. (Join-Path $d 'icon-lib.ps1')
Build-Ico ${function:Draw-Icon} (Join-Path $d 'app.ico') (Join-Path $d 'icon.png') $null

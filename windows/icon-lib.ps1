# 선명한 .ico 만들기 (make-icon.ps1 에서 불러 씀)
# Build-Ico <그리기 함수(g, k, size)> <ico 경로> [256 PNG 경로] [32 PNG 경로]
#   그리기 함수는 256×256 좌표계를 k 배로 그린다. size 는 최종 크기(작은 크기 보정용).
Add-Type -AssemblyName System.Drawing

function Render-Size($draw, [int]$n) {
  $ss = 4; $big = New-Object System.Drawing.Bitmap ($n * $ss), ($n * $ss)
  $g = [System.Drawing.Graphics]::FromImage($big)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
  & $draw $g ($n * $ss / 256.0) $n
  $g.Dispose()
  # 4배 → 1배 : 픽셀 4×4 평균 (가장 깨끗한 축소)
  $out = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $src = $big.LockBits((New-Object System.Drawing.Rectangle 0, 0, $big.Width, $big.Height), 'ReadOnly', ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
  $buf = New-Object byte[] ($src.Stride * $big.Height); [Runtime.InteropServices.Marshal]::Copy($src.Scan0, $buf, 0, $buf.Length); $big.UnlockBits($src)
  $dst = $out.LockBits((New-Object System.Drawing.Rectangle 0, 0, $n, $n), 'WriteOnly', ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
  $ob = New-Object byte[] ($dst.Stride * $n); $st = $src.Stride
  for ($y = 0; $y -lt $n; $y++) {
    for ($x = 0; $x -lt $n; $x++) {
      $sb = 0; $sg = 0; $sr = 0; $sa = 0
      for ($j = 0; $j -lt $ss; $j++) {
        $row = ($y * $ss + $j) * $st + $x * $ss * 4
        for ($i = 0; $i -lt $ss; $i++) {
          $p = $row + $i * 4; $a = $buf[$p + 3]
          $sb += $buf[$p] * $a; $sg += $buf[$p + 1] * $a; $sr += $buf[$p + 2] * $a; $sa += $a   # 알파 가중 평균 (테두리 번짐 방지)
        }
      }
      $o = $y * $dst.Stride + $x * 4
      if ($sa -gt 0) { $ob[$o] = [byte][Math]::Round($sb / $sa); $ob[$o + 1] = [byte][Math]::Round($sg / $sa); $ob[$o + 2] = [byte][Math]::Round($sr / $sa) }
      $ob[$o + 3] = [byte][Math]::Round($sa / ($ss * $ss))
    }
  }
  [Runtime.InteropServices.Marshal]::Copy($ob, 0, $dst.Scan0, $ob.Length); $out.UnlockBits($dst); $big.Dispose()
  return $out
}

function To-Dib($b) {
  $n = $b.Width; $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
  $w.Write([UInt32]40); $w.Write([Int32]$n); $w.Write([Int32]($n * 2)); $w.Write([UInt16]1); $w.Write([UInt16]32)
  $w.Write([UInt32]0); $w.Write([UInt32]0); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
  for ($y = $n - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $n; $x++) { $c = $b.GetPixel($x, $y); $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A) } }
  $rowBytes = [int]([Math]::Ceiling($n / 32.0) * 4)
  $w.Write((New-Object byte[] ($rowBytes * $n)))   # AND 마스크 (알파를 쓰므로 0)
  $w.Flush(); return , $ms.ToArray()
}

function Build-Ico($draw, [string]$icoPath, [string]$png256, [string]$png32) {
  # 16·24·32·48·256 의 100/125/150/200/250/300% 배율 크기
  $sizes = 16, 20, 24, 28, 30, 32, 36, 40, 42, 48, 56, 60, 64, 72, 80, 96, 128, 256
  $data = @()
  foreach ($n in $sizes) {
    $b = Render-Size $draw $n
    if ($n -le 64) { $data += , (To-Dib $b) }
    else { $ms = New-Object IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $data += , $ms.ToArray() }
    if ($n -eq 256 -and $png256) { $b.Save($png256, [System.Drawing.Imaging.ImageFormat]::Png) }
    if ($n -eq 32 -and $png32) { $b.Save($png32, [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose()
  }
  $fs = [IO.File]::Create($icoPath); $bw = New-Object IO.BinaryWriter $fs
  $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
  $offset = 6 + 16 * $sizes.Count
  for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]; $dim = if ($n -ge 256) { 0 } else { $n }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]([byte[]]$data[$i]).Length); $bw.Write([UInt32]$offset)
    $offset += $data[$i].Length
  }
  foreach ($x in $data) { $bw.Write([byte[]]$x) }
  $bw.Close()
}

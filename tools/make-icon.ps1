Add-Type -AssemblyName System.Drawing

$outPath = "C:\Users\LinOcean\Documents\deepseek-harness\default-workspace\QBank\src\QBank.App\Assets\QBank.ico"
$dir = Split-Path -Parent $outPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

# 画一条圆滚滚的鱼：渐变圆角方块底 + 白色鱼身 + 尾巴 + 眼睛
function New-FishBitmap([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.Clear([System.Drawing.Color]::Transparent)

  $s = [float]$size
  $pad = $s * 0.045
  $r = $s * 0.22
  $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($s - 2*$pad), ($s - 2*$pad))

  # 圆角矩形路径
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r * 2
  $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
  $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
  $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
  $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
  $path.CloseFigure()

  # 渐变底
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.PointF(0, 0)),
    (New-Object System.Drawing.PointF($s, $s)),
    ([System.Drawing.Color]::FromArgb(255, 124, 92, 255)),
    ([System.Drawing.Color]::FromArgb(255, 56, 160, 255)))
  $g.FillPath($brush, $path)
  $brush.Dispose()

  # 鱼身：椭圆
  $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
  $bodyW = $s * 0.46
  $bodyH = $s * 0.36
  $bodyX = $s * 0.16
  $bodyY = ($s - $bodyH) / 2
  $g.FillEllipse($white, $bodyX, $bodyY, $bodyW, $bodyH)

  # 尾巴：分叉的鱼尾（中间内凹，上下各一片）
  $tail = New-Object System.Drawing.Drawing2D.GraphicsPath
  $tx = $bodyX + $bodyW - ($s * 0.03)
  $cy = $s / 2
  $th = $bodyH * 0.80
  $tail.AddPolygon(@(
    (New-Object System.Drawing.PointF(($tx), $cy)),
    (New-Object System.Drawing.PointF(($s * 0.95), ($cy - $th))),
    (New-Object System.Drawing.PointF(($s * 0.855), $cy)),
    (New-Object System.Drawing.PointF(($s * 0.95), ($cy + $th)))
  ))
  $g.FillPath($white, $tail)
  $tail.Dispose()

  # 眼睛（挖成底色，用渐变主色补）
  $eye = $s * 0.052
  $ex = $bodyX + $bodyW * 0.24
  $ey = $s / 2 - $bodyH * 0.16
  $g.FillEllipse($white, ($ex - $eye), ($ey - $eye), ($eye*2), ($eye*2)) | Out-Null
  $eyeBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 92, 120, 255))
  $g.FillEllipse($eyeBrush, ($ex - $eye*0.62), ($ey - $eye*0.62), ($eye*1.24), ($eye*1.24))
  $eyeBrush.Dispose()

  $white.Dispose()
  $path.Dispose()
  $g.Dispose()
  return $bmp
}

# 转成 ICO 用的 DIB 帧（BITMAPINFOHEADER + BGRA 倒序像素 + AND 掩码）。
# 256 用 PNG（体积小、Vista+ 支持），其余用 DIB，保证老接口也能读。
function ConvertTo-IconDib($bmp) {
  $w = $bmp.Width
  $h = $bmp.Height
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter($ms)
  # BITMAPINFOHEADER
  $bw.Write([UInt32]40)
  $bw.Write([Int32]$w)
  $bw.Write([Int32]($h * 2))   # 高度含掩码
  $bw.Write([UInt16]1)         # planes
  $bw.Write([UInt16]32)        # bpp
  $bw.Write([UInt32]0)         # BI_RGB
  $rowBytes = $w * 4
  $bw.Write([UInt32]($rowBytes * $h))
  $bw.Write([Int32]0); $bw.Write([Int32]0)
  $bw.Write([UInt32]0); $bw.Write([UInt32]0)
  # 像素：自下而上，BGRA
  for ($y = $h - 1; $y -ge 0; $y--) {
    for ($x = 0; $x -lt $w; $x++) {
      $c = $bmp.GetPixel($x, $y)
      $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
    }
  }
  # AND 掩码：32bpp 下可全 0，行按 32bit 对齐
  $maskRow = [int][Math]::Ceiling($w / 32.0) * 4
  $zeros = New-Object 'byte[]' ($maskRow * $h)
  $bw.Write($zeros)
  $bw.Flush()
  $data = $ms.ToArray()
  $bw.Dispose(); $ms.Dispose()
  return $data
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($sz in $sizes) {
  $bmp = New-FishBitmap $sz
  $ms = New-Object System.IO.MemoryStream
  if ($sz -ge 256) {
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  } else {
    $dib = ConvertTo-IconDib $bmp
    $ms.Write($dib, 0, $dib.Length)
  }
  $frames += ,@($sz, $ms.ToArray())
  $bmp.Dispose()
  $ms.Dispose()
}

# 组装 ICO
$fs = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)             # reserved
$bw.Write([UInt16]1)             # type = icon
$bw.Write([UInt16]$frames.Count) # count
$offset = 6 + (16 * $frames.Count)
foreach ($f in $frames) {
  $sz = $f[0]; $data = $f[1]
  $dim = if ($sz -ge 256) { 0 } else { $sz }
  $bw.Write([byte]$dim)   # width
  $bw.Write([byte]$dim)   # height
  $bw.Write([byte]0)      # palette
  $bw.Write([byte]0)      # reserved
  $bw.Write([UInt16]1)    # planes
  $bw.Write([UInt16]32)   # bpp
  $bw.Write([UInt32]$data.Length)
  $bw.Write([UInt32]$offset)
  $offset += $data.Length
}
foreach ($f in $frames) { $bw.Write($f[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($outPath, $fs.ToArray())
$bw.Dispose(); $fs.Dispose()

"ICO written: {0} bytes, {1} frames" -f (Get-Item $outPath).Length, $frames.Count

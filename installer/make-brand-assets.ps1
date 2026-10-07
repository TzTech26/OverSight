# Generates Oversight's brand bitmaps from code (no external tools needed):
#   Oversight\Assets\app.ico          multi-size app/tray/installer icon (16-256 px)
#   installer\assets\wizard-*.bmp     Inno Setup wizard side + header images (100% and 200%)
#
# The mark is a scope reticle on a Windows-blue gradient tile — the same artwork as the vector
# LogoMark in Themes\Controls.xaml. Re-run after changing the design:
#   powershell -ExecutionPolicy Bypass -File installer\make-brand-assets.ps1

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$RepoRoot  = Split-Path -Parent $PSScriptRoot
$IcoPath   = Join-Path $RepoRoot "Oversight\Assets\app.ico"
$AssetsDir = Join-Path $PSScriptRoot "assets"
New-Item -ItemType Directory -Force $AssetsDir | Out-Null

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = 2 * $r
  $p.AddArc($x, $y, $d, $d, 180, 90)
  $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
  $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure()
  return $p
}

# Draws the mark into a $size × $size area at ($ox,$oy) on graphics $g.
function Draw-Mark($g, [float]$ox, [float]$oy, [float]$size) {
  $s = $size / 64.0
  $g.SmoothingMode   = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

  $inset = [Math]::Max(0.5, $size * 0.02)
  $tile  = New-RoundedRect ($ox + $inset) ($oy + $inset) ($size - 2 * $inset) ($size - 2 * $inset) (15 * $s)
  $grad  = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
             (New-Object System.Drawing.PointF($ox, $oy)),
             (New-Object System.Drawing.PointF(($ox + $size), ($oy + $size))),
             [System.Drawing.Color]::FromArgb(255, 0x60, 0xCD, 0xFF),
             [System.Drawing.Color]::FromArgb(255, 0x00, 0x5A, 0x9E))
  $blend = New-Object System.Drawing.Drawing2D.ColorBlend(3)
  $blend.Colors    = @([System.Drawing.Color]::FromArgb(255, 0x60, 0xCD, 0xFF),
                       [System.Drawing.Color]::FromArgb(255, 0x00, 0x78, 0xD4),
                       [System.Drawing.Color]::FromArgb(255, 0x00, 0x5A, 0x9E))
  $blend.Positions = @([float]0, [float]0.55, [float]1)
  $grad.InterpolationColors = $blend
  $g.FillPath($grad, $tile)

  # Reticle — thicker strokes at tiny sizes so it stays legible.
  $stroke = [Math]::Max(4.2 * $s, 1.6)
  $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $stroke)
  $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
  $cx = $ox + 32 * $s; $cy = $oy + 32 * $s; $r = 15 * $s
  $g.DrawEllipse($pen, ($cx - $r), ($cy - $r), (2 * $r), (2 * $r))
  if ($size -ge 20) {
    $g.DrawLine($pen, $cx, ($oy + 9 * $s),  $cx, ($oy + 19 * $s))
    $g.DrawLine($pen, $cx, ($oy + 45 * $s), $cx, ($oy + 55 * $s))
    $g.DrawLine($pen, ($ox + 9 * $s),  $cy, ($ox + 19 * $s), $cy)
    $g.DrawLine($pen, ($ox + 45 * $s), $cy, ($ox + 55 * $s), $cy)
  }
  $dr = [Math]::Max(3.5 * $s, 1.2)
  $dot = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
  $g.FillEllipse($dot, ($cx - $dr), ($cy - $dr), (2 * $dr), (2 * $dr))

  $pen.Dispose(); $dot.Dispose(); $grad.Dispose(); $tile.Dispose()
}

function New-MarkBitmap([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::Transparent)
  Draw-Mark $g 0 0 $size
  $g.Dispose()
  return $bmp
}

# ── app.ico: classic 32-bit DIB entries up to 48 px, PNG entries above (Vista+ format) ──
$sizes   = 16, 20, 24, 32, 40, 48, 64, 128, 256
$entries = @()
foreach ($sz in $sizes) {
  $bmp = New-MarkBitmap $sz
  if ($sz -le 48) {
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$sz); $bw.Write([int]($sz * 2))   # BITMAPINFOHEADER (height ×2: XOR+AND)
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $sz - 1; $y -ge 0; $y--) {                                   # bottom-up BGRA
      for ($x = 0; $x -lt $sz; $x++) {
        $c = $bmp.GetPixel($x, $y)
        $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
      }
    }
    $maskStride = [int]([Math]::Ceiling($sz / 32.0) * 4)                   # AND mask (all 0 — alpha wins)
    $bw.Write((New-Object byte[] ($maskStride * $sz)))
    $bw.Flush()
    $data = $ms.ToArray()
  } else {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $data = $ms.ToArray()
  }
  $bmp.Dispose()
  $entries += [pscustomobject]@{ Size = $sz; Data = $data }
}

$fs = [System.IO.File]::Create($IcoPath)
$w  = New-Object System.IO.BinaryWriter($fs)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
  $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$e.Data.Length); $w.Write([int]$offset)
  $offset += $e.Data.Length
}
foreach ($e in $entries) { $w.Write($e.Data) }
$w.Flush(); $fs.Close()
Write-Host "Wrote $IcoPath ($($entries.Count) sizes)"

# ── Inno Setup wizard images (24-bit BMP) ──
function Save-Bmp24($bmp, [string]$path) {
  $out = New-Object System.Drawing.Bitmap($bmp.Width, $bmp.Height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
  $g = [System.Drawing.Graphics]::FromImage($out); $g.DrawImage($bmp, 0, 0, $bmp.Width, $bmp.Height); $g.Dispose()
  $out.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp); $out.Dispose()
}

# Side panel on the Welcome / Finished pages: Windows dark grey with a soft blue glow, mark + wordmark.
function New-WizardImage([int]$w, [int]$h) {
  $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
  $g.Clear([System.Drawing.Color]::FromArgb(255, 0x1C, 0x1C, 0x1C))

  $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
  $glow.AddEllipse([float](-$w * 0.6), [float](-$h * 0.25), [float]($w * 2.2), [float]($h * 0.9))
  $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
  $pgb.CenterColor    = [System.Drawing.Color]::FromArgb(90, 0x00, 0x78, 0xD4)
  $pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0x00, 0x78, 0xD4))
  $g.FillPath($pgb, $glow)

  $gridPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(10, 255, 255, 255), [float]1)
  $step = [int]($w / 8)
  for ($x = 0; $x -lt $w; $x += $step) { $g.DrawLine($gridPen, $x, 0, $x, $h) }
  for ($y = 0; $y -lt $h; $y += $step) { $g.DrawLine($gridPen, 0, $y, $w, $y) }

  $mark = [float]($w * 0.46)
  Draw-Mark $g (($w - $mark) / 2) ([float]($h * 0.30)) $mark

  $font = New-Object System.Drawing.Font("Segoe UI Semibold", [float]($w * 0.12), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
  $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = [System.Drawing.StringAlignment]::Center
  $g.DrawString("Oversight", $font, [System.Drawing.Brushes]::White,
                (New-Object System.Drawing.RectangleF(0, [float]($h * 0.30 + $mark + $w * 0.07), $w, [float]($w * 0.2))), $sf)
  $font2 = New-Object System.Drawing.Font("Segoe UI", [float]($w * 0.058), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
  $muted = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0xC5, 0xC5, 0xC5))
  $g.DrawString("Gaming overlay toolkit", $font2, $muted,
                (New-Object System.Drawing.RectangleF(0, [float]($h * 0.30 + $mark + $w * 0.27), $w, [float]($w * 0.15))), $sf)

  $g.Dispose(); $pgb.Dispose(); $glow.Dispose(); $gridPen.Dispose(); $font.Dispose(); $font2.Dispose(); $muted.Dispose()
  return $bmp
}

# Small header image (top-right of inner pages): just the mark on white.
function New-SmallImage([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::White)
  Draw-Mark $g 0 0 $s
  $g.Dispose()
  return $bmp
}

foreach ($scale in 1, 2) {
  $wiz = New-WizardImage (164 * $scale) (314 * $scale)
  Save-Bmp24 $wiz (Join-Path $AssetsDir "wizard-large-$($scale * 100).bmp"); $wiz.Dispose()
  $sm = New-SmallImage (55 * $scale)
  Save-Bmp24 $sm (Join-Path $AssetsDir "wizard-small-$($scale * 100).bmp"); $sm.Dispose()
}
Write-Host "Wrote wizard images to $AssetsDir"

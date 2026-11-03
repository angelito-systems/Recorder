# Generates src/Angelito.ScreenStudio.Desktop/Assets/app.ico (PNG-compressed entries, 16-256 px). Re-run to change the design.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "..\src\Angelito.ScreenStudio.Desktop\Assets"; New-Item -ItemType Directory -Force $out | Out-Null
function Render([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
  $m = [Math]::Max(1, [int]($s * 0.04)); $w = $s - 2 * $m; $r = $w * 0.24
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc($m, $m, 2*$r, 2*$r, 180, 90); $path.AddArc($m + $w - 2*$r, $m, 2*$r, 2*$r, 270, 90)
  $path.AddArc($m + $w - 2*$r, $m + $w - 2*$r, 2*$r, 2*$r, 0, 90); $path.AddArc($m, $m + $w - 2*$r, 2*$r, 2*$r, 90, 90); $path.CloseFigure()
  $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($s, $s)), ([System.Drawing.Color]::FromArgb(255, 0x9D, 0x7B, 0xFF)), ([System.Drawing.Color]::FromArgb(255, 0x4C, 0x1D, 0xC9))
  $g.FillPath($grad, $path)
  $c = $s / 2.0
  if ($s -ge 32) {
    # soft top highlight for depth
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new(0, $s * 0.55)), ([System.Drawing.Color]::FromArgb(60, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.SetClip($path); $g.FillRectangle($hl, 0, 0, $s, $s * 0.55); $g.ResetClip()
    $ringR = $s * 0.29; $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ($s * 0.075)
    $g.DrawEllipse($pen, $c - $ringR, $c - $ringR, 2 * $ringR, 2 * $ringR)
    $dot = $s * 0.15
  } else { $dot = $s * 0.24 } # tiny sizes: a bold dot reads better than a ring
  $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xFF, 0x4D, 0x5E))), $c - $dot, $c - $dot, 2 * $dot, 2 * $dot)
  if ($s -lt 32) { $g.DrawEllipse((New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1, $s * 0.07))), $c - $dot, $c - $dot, 2 * $dot, 2 * $dot) }
  $g.Dispose(); $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); return ,$ms.ToArray()
}
$sizes = 16, 24, 32, 48, 64, 128, 256; $pngs = $sizes | % { ,(Render $_) }
$ico = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) { $s = $sizes[$i]; $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset); $offset += $pngs[$i].Length }
foreach ($p in $pngs) { $bw.Write($p) }
[System.IO.File]::WriteAllBytes((Join-Path $out "app.ico"), $ico.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $out "app-256.png"), $pngs[-1])
"app.ico: $($ico.Length) bytes, sizes $($sizes -join ',')"

# make-icon.ps1 - generate AppManager.ico (iOS-style accent rounded square with "A")
# ASCII only for PS 5.1 GBK safety
Add-Type -AssemblyName System.Drawing

$out = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) ".." | Join-Path -ChildPath "AppManager.ico"
$out = (Resolve-Path (Split-Path -Parent $out)).Path
$out = Join-Path $out "AppManager.ico"

function New-IconBitmap($size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $rect = New-Object System.Drawing.Rectangle 2, 2, ($size - 4), ($size - 4)
    $r = $size * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 122, 255))
    $g.FillPath($brush, $path)
    $brush.Dispose()
    $f = New-Object System.Drawing.Font "Segoe UI", ($size * 0.5), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $wb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.DrawString("A", $f, $wb, [System.Drawing.RectangleF]$rect, $sf)
    $wb.Dispose(); $f.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

# build multi-size ico: 16,32,48,64,128,256
$sizes = 16, 32, 48, 64, 128, 256
$ms = New-Object System.Collections.Generic.List[System.Drawing.Bitmap]
foreach ($s in $sizes) { $ms.Add((New-IconBitmap $s)) }

# write ico container manually (multi-image)
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
# count
$cnt = $ms.Count
# header: 0,0,1,0 + count
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$cnt)
# directory entries
$entryBase = 6 + ($cnt * 16)
$entries = @()
$pidx = 0
foreach ($bmp in $ms) {
    $ms2 = New-Object System.IO.MemoryStream
    $bmp.Save($ms2, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms2.ToArray()
    $ms2.Dispose()
    $w = if ($bmp.Width -ge 256) { 0 } else { $bmp.Width }
    $h = if ($bmp.Height -ge 256) { 0 } else { $bmp.Height }
    $entries += ,@{ W = $w; H = $h; Bytes = $bytes }
    $bmp.Dispose()
}
$offset = $entryBase
for ($i = 0; $i -lt $cnt; $i++) {
    $e = $entries[$i]
    $bw.Write([byte]$e.W)
    $bw.Write([byte]$e.H)
    $bw.Write([byte]0)   # color count
    $bw.Write([byte]0)   # reserved
    $bw.Write([uint16]1) # planes
    $bw.Write([uint16]32) # bpp
    $bw.Write([uint32]$e.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $bw.Write($e.Bytes) }
$bw.Flush(); $bw.Close(); $fs.Dispose()
Write-Host "Icon saved: $out ($((Get-Item $out).Length) bytes)"

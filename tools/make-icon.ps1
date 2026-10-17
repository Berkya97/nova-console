# Nova uygulama simgesini (Assets\nova.ico) üretir: koyu mavi-mor kare üzerinde yörüngeli yıldız.
# Kullanım: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "..\Assets\nova.ico"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function Render([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Yuvarlatılmış kare zemin
    $r = [single]($s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($s - $r - 1, 0, $r, $r, 270, 90)
    $path.AddArc($s - $r - 1, $s - $r - 1, $r, $r, 0, 90); $path.AddArc(0, $s - $r - 1, $r, $r, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $s, $s), ([System.Drawing.Color]::FromArgb(255, 0x1B, 0x3F, 0x8F)), ([System.Drawing.Color]::FromArgb(255, 0x3A, 0x1A, 0x6E))
    $g.FillPath($bg, $path)

    $c = $s / 2.0
    # Parıltı
    $glowR = $s * 0.30
    $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glow.AddEllipse([single]($c - $glowR), [single]($c - $glowR), [single]($glowR * 2), [single]($glowR * 2))
    $pg = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
    $pg.CenterColor = [System.Drawing.Color]::FromArgb(120, 150, 190, 255)
    $pg.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 150, 190, 255))
    $g.FillPath($pg, $glow)

    # Yörünge halkası
    $ringR = $s * 0.31
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single][Math]::Max(1.2, $s * 0.035))
    $g.DrawEllipse($pen, [single]($c - $ringR), [single]($c - $ringR), [single]($ringR * 2), [single]($ringR * 2))
    # Merkez yıldız
    $coreR = $s * 0.10
    $g.FillEllipse([System.Drawing.Brushes]::White, [single]($c - $coreR), [single]($c - $coreR), [single]($coreR * 2), [single]($coreR * 2))
    # Yörüngedeki küçük nokta
    $a = -0.8
    $dotR = [Math]::Max(1.0, $s * 0.045)
    $dx = $c + [Math]::Cos($a) * $ringR; $dy = $c + [Math]::Sin($a) * $ringR
    $g.FillEllipse([System.Drawing.Brushes]::White, [single]($dx - $dotR), [single]($dy - $dotR), [single]($dotR * 2), [single]($dotR * 2))

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        # 256 piksel: PNG sıkıştırmalı giriş (standart)
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Küçük boyutlar: klasik 32 bit DIB (tepsi simgesi ve eski API'ler için)
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32]($s * 2)); $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]0); $bw.Write([uint32]($s * $s * 4)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $s - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $s; $x++) {
                $px = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$px.B); $bw.Write([byte]$px.G); $bw.Write([byte]$px.R); $bw.Write([byte]$px.A)
            }
        }
        $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
        $bw.Write((New-Object byte[] ($maskRow * $s)))
        $bw.Flush()
    }
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 256, 64, 48, 32, 24, 16
$images = $sizes | ForEach-Object { , (Render $_) }
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
"Oluşturuldu: $out"

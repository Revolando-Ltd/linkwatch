# Builds LinkWatch.exe with the C# compiler that ships with Windows (.NET Framework 4). No third-party tools.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$src  = Join-Path $root "src"
$csc  = "$env:windir\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

# app icon: green dot, PNG images packed into an .ico (sizes 16..256)
Add-Type -AssemblyName System.Drawing
$ico = Join-Path $src "app.ico"
$sizes = 16, 24, 32, 48, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = "AntiAlias"
    $m = [math]::Max(1, $s / 16)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::LimeGreen)), $m, $m, $s - 2*$m, $s - 2*$m)
    $g.Dispose(); $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); , $ms.ToArray()
}
$fs = [IO.File]::Create($ico); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()

& $csc /nologo /target:winexe /optimize+ /platform:x64 "/out:$root\LinkWatch.exe" "/win32icon:$ico" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.ServiceProcess.dll `
    "$src\LinkWatch.cs" "$src\UsbInfo.cs" "$src\EtwUsb.cs" "$src\History.cs"
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Get-Item "$root\LinkWatch.exe" | Select-Object Name, Length, LastWriteTime


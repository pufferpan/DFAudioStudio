# Capture the DFAudioStudio window via PrintWindow (works even if the window is covered),
# then report sampled colors so the animated gradient / accent button can be verified.
# ASCII only (PS 5.1 + BOM-less UTF-8 .ps1).
param(
    [string]$OutDir = 'E:\DFAudioStudio\shots',
    [string]$Name = 'w'
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class PW {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$p = Get-Process | Where-Object { $_.ProcessName -like 'DFAudioStudio*' -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Host 'window not found'; exit 1 }

$r = New-Object PW+RECT
[void][PW]::GetWindowRect($p.MainWindowHandle, [ref]$r)
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
if ($w -le 0 -or $h -le 0) { Write-Host 'bad rect'; exit 1 }

# PW_RENDERFULLCONTENT = 2 (needed for WinUI / DirectComposition content)
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[void][PW]::PrintWindow($p.MainWindowHandle, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$file = Join-Path $OutDir ("{0}-{1:HHmmss}.png" -f $Name, (Get-Date))
$bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)

function Avg($x, $y, $sw, $sh) {
    $sr = 0; $sg = 0; $sb = 0; $n = 0
    for ($i = $x; $i -lt ($x + $sw); $i += 3) {
        for ($j = $y; $j -lt ($y + $sh); $j += 3) {
            if ($i -lt 0 -or $j -lt 0 -or $i -ge $w -or $j -ge $h) { continue }
            $c = $bmp.GetPixel($i, $j); $sr += $c.R; $sg += $c.G; $sb += $c.B; $n++
        }
    }
    if ($n -eq 0) { return '#000000' }
    return ('#{0:X2}{1:X2}{2:X2}' -f [int]($sr / $n), [int]($sg / $n), [int]($sb / $n))
}

# count accent-blue and white pixels inside a rect
function CountColors($x, $y, $sw, $sh) {
    $blue = 0; $white = 0; $n = 0
    for ($i = $x; $i -lt ($x + $sw); $i += 2) {
        for ($j = $y; $j -lt ($y + $sh); $j += 2) {
            if ($i -lt 0 -or $j -lt 0 -or $i -ge $w -or $j -ge $h) { continue }
            $c = $bmp.GetPixel($i, $j); $n++
            if ($c.B -gt 190 -and $c.R -lt 120 -and $c.G -gt 80 -and $c.G -lt 170) { $blue++ }
            if ($c.R -gt 210 -and $c.G -gt 210 -and $c.B -gt 210) { $white++ }
        }
    }
    return "$blue/$white/$n"
}

Write-Host ("file       : {0}" -f $file)
Write-Host ("size       : {0}x{1}" -f $w, $h)
Write-Host ("bg bottom  : {0}" -f (Avg ([int]($w * 0.7)) ([int]($h - 30)) 120 16))
Write-Host ("bg topright: {0}" -f (Avg ([int]($w - 200)) ([int]($h * 0.85)) 120 16))
Write-Host ("bg midleft : {0}" -f (Avg 300 ([int]($h - 30)) 120 16))
Write-Host ("primary line(blue/white/total px): {0}" -f (CountColors 500 ([int]($h * 0.70)) [int]($w - 560) 34))
$bmp.Dispose()

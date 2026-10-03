# Verify dark-mode toggle: find the "dark mode" ToggleSwitch via UI Automation, toggle it,
# and compare the window background color before/after. ASCII only.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if (-not ('TCap' -as [type])) {
    Add-Type @"
using System; using System.Runtime.InteropServices;
public class TCap {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int Left, Top, Right, Bottom; }
}
"@
}

$p = Get-Process | Where-Object { $_.ProcessName -like 'DFAudioStudio*' -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Host 'app not running'; exit 1 }
[void][TCap]::ShowWindow($p.MainWindowHandle, 9)
[void][TCap]::SetForegroundWindow($p.MainWindowHandle)
Start-Sleep -Milliseconds 1200

function Sample {
    param($label)
    $r = New-Object TCap+R
    [void][TCap]::GetWindowRect($p.MainWindowHandle, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    # average a wide strip near the bottom of the window (background area)
    $sr = 0; $sg = 0; $sb = 0; $n = 0
    for ($x = [int]($w * 0.6); $x -lt ($w - 20); $x += 5) {
        for ($y = ($h - 44); $y -lt ($h - 12); $y += 4) {
            $c = $bmp.GetPixel($x, $y); $sr += $c.R; $sg += $c.G; $sb += $c.B; $n++
        }
    }
    $bmp.Dispose()
    $hex = '#{0:X2}{1:X2}{2:X2}' -f [int]($sr / $n), [int]($sg / $n), [int]($sb / $n)
    Write-Host ("  {0,-10} background = {1}" -f $label, $hex)
    return $hex
}

Write-Host 'before toggle:'
$before = Sample 'before'

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

$switch = $null
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($el in $all) {
    $nm = $el.Current.Name
    if ($nm -and ($nm -match 'dark' -or $nm -match 'Dark' -or $nm -match [char]0x6697)) {  # 'an' = dark in CJK
        $switch = $el
        Write-Host ("  found element: type={0} name='{1}'" -f $el.Current.ControlType.ProgrammaticName, $nm)
        break
    }
}
if (-not $switch) {
    Write-Host '  dark-mode toggle not found by name; listing toggle-like elements:'
    foreach ($el in $all) {
        if ($el.Current.ControlType -eq [System.Windows.Automation.ControlType]::CheckBox -or
            $el.Current.ControlType.ProgrammaticName -match 'Button') {
            if ($el.Current.Name) { Write-Host ("    - {0}" -f $el.Current.Name) }
        }
    }
    exit 2
}

try {
    $tp = $switch.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $tp.Toggle()
    Write-Host '  toggled.'
} catch {
    try {
        $switch.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Write-Host '  invoked.'
    } catch { Write-Host ('  toggle failed: ' + $_.Exception.Message) }
}

Start-Sleep -Milliseconds 1500
Write-Host 'after toggle:'
$after = Sample 'after'
if ($before -ne $after) { Write-Host 'RESULT: background changed -> dark mode switch works' }
else { Write-Host 'RESULT: background unchanged -> check theme switching' }

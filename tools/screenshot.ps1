# Captures the Melvor Idle 2 window (even when covered by other windows) to a PNG.
# Usage: powershell -File tools\screenshot.ps1 <out.png>
param([string]$Out = "$env:TEMP\melvor.png")
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class MelvorShot {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public struct RECT { public int L, T, R, B; }
}
"@
[MelvorShot]::SetProcessDPIAware() | Out-Null
$p = Get-Process Melvor2 -ErrorAction Stop
$r = New-Object MelvorShot+RECT
[MelvorShot]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$bmp = New-Object Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
$g = [Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[MelvorShot]::PrintWindow($p.MainWindowHandle, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc)
$bmp.Save($Out)
$Out

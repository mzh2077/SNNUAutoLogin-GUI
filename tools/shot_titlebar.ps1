param([string]$Out = "$env:TEMP\snnu_titlebar.png", [int]$CropRight = 300, [int]$CropTop = 0, [int]$CropHeight = 50, [int]$Zoom = 4)

Add-Type -AssemblyName System.Drawing
# Make this process per-monitor DPI aware, otherwise window metrics come back
# in virtualized (shrunken) coordinates and the screenshot loses the right side.
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class DpiCtx {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
}
"@
try { [void][DpiCtx]::SetProcessDpiAwarenessContext([IntPtr](-4)) } catch { }
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class Win3 {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

  public static List<IntPtr> Visible(uint want) {
    var list = new List<IntPtr>();
    EnumWindows((h, p) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != want) return true;
      if (!IsWindowVisible(h)) return true;
      RECT r; if (!GetWindowRect(h, out r)) return true;
      if (r.R - r.L < 200 || r.B - r.T < 200) return true;
      list.Add(h);
      return true;
    }, IntPtr.Zero);
    return list;
  }
}
"@

$proc = Get-Process SNNUAutoLogin -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Error "no process"; exit 1 }

$handles = [Win3]::Visible([uint32]$proc.Id)
if ($handles.Count -eq 0) { Write-Error "no visible window"; exit 1 }
$hwnd = $handles[0]
$sb = New-Object System.Text.StringBuilder 256
[void][Win3]::GetWindowText($hwnd, $sb, 256)
Write-Output "hwnd = $hwnd title = $($sb.ToString())"

[void][Win3]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040)
Start-Sleep -Milliseconds 700

$r = New-Object Win3+RECT
[void][Win3]::GetWindowRect($hwnd, [ref]$r)
$w = $r.R - $r.L
$h = $r.B - $r.T
Write-Output "window = $($r.L),$($r.T) ${w}x${h}"

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
$g.Dispose()
Write-Output "mode = screen"
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)

$cw = [Math]::Min($CropRight, $w)
$ch = [Math]::Min($CropHeight, $h)
$crop = New-Object System.Drawing.Bitmap ($cw * $Zoom), ($ch * $Zoom)
$cg = [System.Drawing.Graphics]::FromImage($crop)
$cg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$cg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$cg.DrawImage($bmp, (New-Object System.Drawing.Rectangle 0, 0, ($cw * $Zoom), ($ch * $Zoom)),
    (New-Object System.Drawing.Rectangle ($w - $cw), $CropTop, $cw, $ch),
    [System.Drawing.GraphicsUnit]::Pixel)
$cg.Dispose()
$crop.Save(($Out -replace '\.png$', '_zoom.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$crop.Dispose()
$bmp.Dispose()
Write-Output "saved = $Out"

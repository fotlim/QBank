# 启动 GUI 程序 → 等窗口出现 → 用 PrintWindow 截窗口图 → 关掉，用来做无头烟测。
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Out = "shot.png",
    [int]$WaitSeconds = 6,
    [int]$ExtraWait = 4,
    [string]$AppArgs = "",
    [switch]$NoKill
)

$ErrorActionPreference = 'Stop'

# 让本进程变成 per-monitor DPI 感知：否则在 125%/150% 缩放下 GetWindowRect
# 返回的是缩放后坐标，PrintWindow 只能抓到窗口左上角一块。
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class QbDpi {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    public static void Enable() {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
    }
}
"@
[QbDpi]::Enable()

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class QBWin32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    public const int PW_RENDERFULLCONTENT = 2;
}
"@

$dir = Split-Path $Out -Parent
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$proc = if ([string]::IsNullOrWhiteSpace($AppArgs)) {
    Start-Process -FilePath $Exe -PassThru
} else {
    Start-Process -FilePath $Exe -ArgumentList $AppArgs -PassThru
}
$handle = [IntPtr]::Zero

for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 700
    if ($proc.HasExited) {
        Write-Output "PROCESS_EXITED_EARLY exitcode=$($proc.ExitCode)"
        exit 2
    }
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $handle = $proc.MainWindowHandle; break }
}

if ($handle -eq [IntPtr]::Zero) {
    Write-Output "NO_MAIN_WINDOW"
    if (-not $NoKill) { try { $proc.Kill() } catch {} }
    exit 3
}

Start-Sleep -Seconds $ExtraWait

# 尽量置前；置前失败也不影响 PrintWindow 抓取
[QBWin32]::ShowWindow($handle, 9) | Out-Null
[QBWin32]::SetWindowPos($handle, [IntPtr]::Zero, 40, 40, 0, 0, 0x0015) | Out-Null
[QBWin32]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 1000

$rect = New-Object QBWin32+RECT
[QBWin32]::GetWindowRect($handle, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
if ($w -le 0 -or $h -le 0) {
    Write-Output "BAD_RECT $w x $h"
    if (-not $NoKill) { try { $proc.Kill() } catch {} }
    exit 4
}

$bitmap = New-Object System.Drawing.Bitmap $w, $h
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
$ok = [QBWin32]::PrintWindow($handle, $hdc, [QBWin32]::PW_RENDERFULLCONTENT)
$graphics.ReleaseHdc($hdc)

if (-not $ok) {
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
}

$graphics.Dispose()
$bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

$title = $proc.MainWindowTitle
Write-Output "SHOT_OK $w x $h  title='$title'  -> $Out"

if (-not $NoKill) {
    $proc.CloseMainWindow() | Out-Null
    Start-Sleep -Milliseconds 900
    if (-not $proc.HasExited) { try { $proc.Kill() } catch {} }
    Write-Output "CLOSED"
}

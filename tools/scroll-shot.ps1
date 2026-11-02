param(
  [Parameter(Mandatory=$true)][string]$Exe,
  [Parameter(Mandatory=$true)][string]$Out,
  [string]$AppArgs = "",
  [int]$WaitSeconds = 8,
  [int]$PagesDown = 0,
  [int]$Wheel = 0
)

$ErrorActionPreference = 'Stop'

# ---- DPI 感知：必须在加载 System.Drawing 之前 ----
Add-Type -Namespace QbDpi -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
'@
try { [QbDpi.Native]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null } catch { }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -Namespace QbWin -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

$script:lastOut = $null

function Get-Shot([IntPtr]$hWnd, [string]$path) {
  $r = New-Object QbWin.Native+RECT
  [QbWin.Native]::GetWindowRect($hWnd, [ref]$r) | Out-Null
  $w = $r.Right - $r.Left
  $h = $r.Bottom - $r.Top
  if ($w -le 0 -or $h -le 0) { return $false }
  $bmp = New-Object System.Drawing.Bitmap($w, $h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  $ok = [QbWin.Native]::PrintWindow($hWnd, $hdc, 2)
  $g.ReleaseHdc($hdc)
  $g.Dispose()
  if (-not $ok) {
    $g2 = [System.Drawing.Graphics]::FromImage($bmp)
    $g2.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g2.Dispose()
  }
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  $script:lastOut = "$w x $h"
  return $true
}

$args2 = @()
if ($AppArgs) { $args2 = $AppArgs -split ' ' }

$proc = Start-Process -FilePath $Exe -ArgumentList $args2 -PassThru
$hWnd = [IntPtr]::Zero
for ($i = 0; $i -lt 20; $i++) {
  Start-Sleep -Milliseconds 700
  $proc.Refresh()
  if ($proc.HasExited) { Write-Output "PROCESS_EXITED_EARLY exitcode=$($proc.ExitCode)"; exit 2 }
  if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $hWnd = $proc.MainWindowHandle; break }
}
if ($hWnd -eq [IntPtr]::Zero) { Write-Output "NO_MAIN_WINDOW"; try { $proc.Kill() } catch {}; exit 3 }

Start-Sleep -Seconds $WaitSeconds
[QbWin.Native]::ShowWindow($hWnd, 9) | Out-Null
[QbWin.Native]::SetForegroundWindow($hWnd) | Out-Null
Start-Sleep -Milliseconds 800

# 滚轮：鼠标移到窗口中间再滚
if ($Wheel -ne 0) {
  $r = New-Object QbWin.Native+RECT
  [QbWin.Native]::GetWindowRect($hWnd, [ref]$r) | Out-Null
  $cx = [int](($r.Left + $r.Right) / 2)
  $cy = [int](($r.Top + $r.Bottom) / 2)
  [QbWin.Native]::SetCursorPos($cx, $cy) | Out-Null
  Start-Sleep -Milliseconds 200
  $notches = [Math]::Abs($Wheel)
  $flag = if ($Wheel -lt 0) { 0x0800 } else { 0x0800 }
  for ($n = 0; $n -lt $notches; $n++) {
    $delta = if ($Wheel -lt 0) { -120 } else { 120 }
    [QbWin.Native]::mouse_event($flag, 0, 0, $delta, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
  }
  Start-Sleep -Milliseconds 900
}

# PageDown：先点进窗口再翻页
if ($PagesDown -gt 0) {
  [QbWin.Native]::SetForegroundWindow($hWnd) | Out-Null
  Start-Sleep -Milliseconds 400
  for ($n = 0; $n -lt $PagesDown; $n++) {
    [System.Windows.Forms.SendKeys]::SendWait("{PGDN}")
    Start-Sleep -Milliseconds 350
  }
  Start-Sleep -Milliseconds 900
}

if (-not (Get-Shot $hWnd $Out)) {
  Write-Output "BAD_RECT"
  try { $proc.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 600; $proc.Kill() } catch {}
  exit 5
}

Write-Output "SHOT_OK $script:lastOut -> $Out"

try { $proc.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 900 } catch {}
if (-not $proc.HasExited) { try { $proc.Kill() } catch {} }
Write-Output "CLOSED"

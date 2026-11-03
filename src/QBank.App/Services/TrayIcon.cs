using System.Runtime.InteropServices;

namespace QBank.App.Services;

/// <summary>
/// 系统托盘图标。
///
/// WinUI 3 没有内置托盘支持，这里用 Shell_NotifyIcon 加 HWND 子类化实现：
/// 往托盘放一个图标，接收鼠标消息，右键弹一个系统菜单，双击把窗口叫回来。
///
/// 只做常规托盘行为 —— 菜单、双击恢复、可选气泡提示。不做任何进程监控之类的花样。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    /// <summary>托盘图标的回调消息，任意 WM_APP 之上的值即可。</summary>
    private const int WM_TRAYICON = WM_APP + 1;

    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;

    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint NIF_INFO = 0x10;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_GRAYED = 0x0001;
    private const uint MF_DEFAULT = 0x0000;

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;

    private const int GWLP_WNDPROC = -4;
    private const int WM_NULL = 0x0000;

    /// <summary>explorer.exe 重启后所有托盘图标都会消失，它广播这个消息提醒重新添加。</summary>
    private static readonly uint WM_TASKBARCREATED = RegisterWindowMessageW("TaskbarCreated");

    /// <summary>菜单项 id。</summary>
    private const int CmdShow = 1001;
    private const int CmdOpenData = 1002;
    private const int CmdExit = 1003;

    private readonly IntPtr _hwnd;
    private readonly uint _uID;
    private readonly string _tooltip;

    private IntPtr _hIcon = IntPtr.Zero;
    private IntPtr _oldWndProc = IntPtr.Zero;
    private SubclassProc? _proc;      // 必须存字段，否则回调会被 GC 回收
    private bool _added;
    private bool _disposed;

    /// <summary>双击图标或在菜单里选「显示主界面」。</summary>
    public event Action? ShowRequested;

    /// <summary>菜单里选「退出程序」。</summary>
    public event Action? ExitRequested;

    /// <summary>菜单里选「打开数据目录」。</summary>
    public event Action? OpenDataDirectoryRequested;

    /// <summary>鼠标悬停在图标上时显示的提示文字。</summary>
    public string Tooltip => _tooltip;

    /// <summary>图标是否已经真的进了托盘。</summary>
    public bool Installed => _added;

    /// <summary>安装失败时的原因，界面上显示用。成功时为 null。</summary>
    public string? LastError { get; private set; }

    public TrayIcon(IntPtr hwnd, string tooltip, string? iconPath = null, uint uID = 1)
    {
        _hwnd = hwnd;
        _uID = uID;
        _tooltip = Truncate(tooltip, 127);
        _hIcon = LoadTrayIcon(iconPath);
    }

    /// <summary>把图标塞进托盘，并开始接收它的鼠标消息。</summary>
    public bool TryInstall()
    {
        if (_disposed || _added || _hwnd == IntPtr.Zero) return _added;

        try
        {
            _proc = WndProc;
            _oldWndProc = SetWindowLongPtrW(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_proc));

            var data = BuildData();
            _added = Shell_NotifyIconW(NIM_ADD, ref data);
            if (!_added)
            {
                var code = Marshal.GetLastWin32Error();
                LastError = DescribeFailure(code);
                RestoreWndProc();
                App.Trace($"托盘图标添加失败（Win32 {code}）：{LastError}");
            }
            else
            {
                LastError = null;
                App.Trace("托盘图标已添加");
            }
        }
        catch (Exception ex)
        {
            LastError = "初始化异常：" + ex.Message;
            App.Trace("托盘图标初始化异常：" + ex.Message);
            _added = false;
        }

        return _added;
    }

    /// <summary>把 Shell_NotifyIcon 的错误码翻译成人能看懂的话。</summary>
    private static string DescribeFailure(int code) => code switch
    {
        5 => "系统拒绝了托盘登记（拒绝访问）。常见原因：进程权限或运行环境受限，"
             + "或者资源管理器不是同一个会话。关窗口会按正常退出处理，不会留下后台进程。",
        0 => "系统没有返回具体错误。",
        _ => "Win32 错误码 " + code + "。",
    };

    /// <summary>弹一个气泡提示（只在托盘图标存在时有效）。</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added || _disposed) return;

        try
        {
            var data = BuildData();
            data.uFlags = NIF_INFO;
            data.szInfoTitle = Truncate(title, 63);
            data.szInfo = Truncate(text, 255);
            data.dwInfoFlags = 0x1; // NIIF_INFO
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
        catch
        {
            // 气泡提示纯属锦上添花，失败不打扰用户
        }
    }

    /// <summary>刷新提示文字（比如显示题库条数变化）。</summary>
    public void UpdateTooltip(string tooltip)
    {
        if (!_added || _disposed) return;

        try
        {
            var data = BuildData();
            data.uFlags = NIF_TIP;
            data.szTip = Truncate(tooltip, 127);
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
        catch
        {
            // 同上
        }
    }

    private NOTIFYICONDATAW BuildData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = _uID,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = (uint)WM_TRAYICON,
        hIcon = _hIcon == IntPtr.Zero ? LoadFallbackIcon() : _hIcon,
        szTip = _tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_TRAYICON)
            {
                // 未设置 NOTIFYICON_VERSION_4 时，lParam 低位就是鼠标消息本身
                var mouse = (int)(lParam.ToInt64() & 0xFFFF);
                switch (mouse)
                {
                    case WM_LBUTTONDBLCLK:
                        ShowRequested?.Invoke();
                        return IntPtr.Zero;
                    case WM_LBUTTONUP:
                    case WM_CONTEXTMENU:
                    case WM_RBUTTONUP:
                        ShowContextMenu();
                        return IntPtr.Zero;
                }
            }
            else if (msg == WM_TASKBARCREATED && _added)
            {
                // explorer 重启了，重新注册一次
                try
                {
                    var data = BuildData();
                    Shell_NotifyIconW(NIM_ADD, ref data);
                    App.Trace("explorer 重启，托盘图标已重新添加");
                }
                catch
                {
                    // 忽略
                }
            }
        }
        catch (Exception ex)
        {
            App.Trace("托盘消息处理异常：" + ex.Message);
        }

        return CallWindowProcW(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            AppendMenuW(menu, MF_STRING | MF_DEFAULT, CmdShow, "打开主界面");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, CmdOpenData, "打开数据目录");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, CmdExit, "退出题库工坊");

            GetCursorPos(out var pt);

            // 必须先把自己设成前台窗口，否则点菜单外面菜单不会消失
            SetForegroundWindow(_hwnd);

            var cmd = (int)TrackPopupMenuEx(
                menu,
                TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD,
                pt.X, pt.Y,
                _hwnd,
                IntPtr.Zero);

            PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case CmdShow:
                    ShowRequested?.Invoke();
                    break;
                case CmdOpenData:
                    OpenDataDirectoryRequested?.Invoke();
                    break;
                case CmdExit:
                    ExitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private IntPtr LoadTrayIcon(string? iconPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
            {
                var cx = GetSystemMetrics(SM_CXSMICON);
                var cy = GetSystemMetrics(SM_CYSMICON);
                var h = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, cx, cy, LR_LOADFROMFILE);
                if (h != IntPtr.Zero) return h;
            }
        }
        catch
        {
            // 退回系统默认图标
        }

        return LoadFallbackIcon();
    }

    private static IntPtr LoadFallbackIcon() => LoadIconW(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION

    private void RestoreWndProc()
    {
        if (_oldWndProc == IntPtr.Zero || _hwnd == IntPtr.Zero) return;

        try
        {
            SetWindowLongPtrW(_hwnd, GWLP_WNDPROC, _oldWndProc);
        }
        catch
        {
            // 恢复失败也只能算了
        }

        _oldWndProc = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added)
        {
            try
            {
                var data = BuildData();
                Shell_NotifyIconW(NIM_DELETE, ref data);
            }
            catch
            {
                // 忽略
            }

            _added = false;
        }

        RestoreWndProc();

        if (_hIcon != IntPtr.Zero)
        {
            try
            {
                DestroyIcon(_hIcon);
            }
            catch
            {
                // 忽略
            }

            _hIcon = IntPtr.Zero;
        }
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text[..max];
    }

    // ─── Win32 ────────────────────────────────────────────────────────────

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, int uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

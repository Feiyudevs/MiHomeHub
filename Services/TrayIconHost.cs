using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MijiaPanel.Services;

/// <summary>
/// 自己托管 Shell_NotifyIcon：Avalonia 11.3 的 TrayIcon 只暴露左键 Clicked，
/// 而托盘需要「左键开主窗口、右键弹菜单」，所以挂窗口 WndProc 直接收托盘消息。
/// 托盘回调挂在主窗口句柄上，窗口隐藏时句柄仍在，隐藏到托盘后依旧可点。
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private const int WM_TRAYICON = 0x8000 + 2;

    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0x0;
    private const uint NIM_DELETE = 0x2;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;

    private readonly string _tooltip;
    private readonly TopLevel _owner;
    private readonly IntPtr _hwnd;
    private readonly int _taskbarCreated;
    private readonly Win32Properties.CustomWndProcHookCallback _hook;
    private readonly IntPtr _icon;

    public event Action? LeftClicked;
    public event Action? RightClicked;

    public TrayIconHost(TopLevel owner, string tooltip)
    {
        _owner = owner;
        _tooltip = tooltip;
        _hwnd = owner.TryGetPlatformHandle()?.Handle
                ?? throw new InvalidOperationException("窗口句柄不可用，托盘图标无法注册");

        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _hook = OnWndProc;
        Win32Properties.AddWndProcHookCallback(owner, _hook);

        // 直接用 exe 里嵌的图标，避免为了托盘再解一份资源
        ExtractIconEx(Environment.ProcessPath!, 0, out _icon, out var smallIcon, 1);
        if (smallIcon != IntPtr.Zero) DestroyIcon(smallIcon);
        AddIcon();
    }

    private void AddIcon()
    {
        var data = CreateData();
        Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private NOTIFYICONDATA CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_TRAYICON,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = ""
    };

    private IntPtr OnWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
            handled = true;
            // 在 WndProc 里直接弹窗会重入消息循环，推迟到本轮消息处理完
            if (mouse == WM_LBUTTONUP) Dispatcher.UIThread.Post(() => LeftClicked?.Invoke());
            else if (mouse == WM_RBUTTONUP) Dispatcher.UIThread.Post(() => RightClicked?.Invoke());
            return IntPtr.Zero;
        }

        // 资源管理器重启后托盘区清空，需要重新注册
        if (msg == _taskbarCreated) AddIcon();
        return IntPtr.Zero;
    }

    /// <summary>托盘图标在屏幕上的实际位置；图标收在溢出区且未展开时取不到，返回 null。</summary>
    public PixelRect? IconRect()
    {
        var id = new NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _hwnd,
            uID = 1
        };
        if (Shell_NotifyIconGetRect(ref id, out var rect) != 0) return null;
        return new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public void Dispose()
    {
        var data = CreateData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        Win32Properties.RemoveWndProcHookCallback(_owner, _hook);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT rect);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}

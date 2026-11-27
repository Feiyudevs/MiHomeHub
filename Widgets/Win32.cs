using System.Runtime.InteropServices;
using System.Text;

namespace MiHomeHub.Widgets;

internal static partial class Win32
{
    // 窗口消息常量
    public static readonly nint HWND_BROADCAST = new(-1);

    // 窗口样式常量
    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;
    public const int GWLP_USERDATA = -21;
    public const int GWL_HWNDPARENT = -8;

    public const uint WS_CHILD = 0x40000000;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_CLIPCHILDREN = 0x02000000;
    public const uint WS_CLIPSIBLINGS = 0x04000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_CAPTION = 0x00C00000;
    public const uint WS_THICKFRAME = 0x00040000;
    public const uint WS_SYSMENU = 0x00080000;
    public const uint WS_MINIMIZEBOX = 0x00020000;
    public const uint WS_MAXIMIZEBOX = 0x00010000;
    public const uint WS_BORDER = 0x00800000;
    public const uint WS_DLGFRAME = 0x00400000;

    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    /// <summary>窗口立体边框；去掉可削弱系统画边/阴影</summary>
    public const uint WS_EX_WINDOWEDGE = 0x00000100;
    /// <summary>对话框边框（3D）；系统窗体的一环，必须去掉</summary>
    public const uint WS_EX_CLIENTEDGE = 0x00000200;
    public const uint WS_EX_APPWINDOW = 0x00040000;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_DLGMODALFRAME = 0x00000001;
    // Avalonia 合成窗会带上；会加重 DWM 投影，NoFences 没有
    public const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

    // 窗口消息
    public const uint WM_SIZE = 0x0005;
    /// <summary>WM_SIZE 的 wParam 低字：窗口被最小化 / 还原（见 SIZE_* 常量）。</summary>
    public const int SIZE_RESTORED = 0;
    public const int SIZE_MINIMIZED = 1;
    public const int SIZE_MAXIMIZED = 2;
    public const int SIZE_MAXSHOW = 3;
    public const int SIZE_MAXHIDE = 4;
    public const uint WM_SETFOCUS = 0x0007;
    public const uint WM_KILLFOCUS = 0x0008;
    /// <summary>窗口显示/隐藏（wParam=0 隐藏）。</summary>
    public const uint WM_SHOWWINDOW = 0x0018;
    /// <summary>非客户区绘制：无边框窗直接返回 0，系统不会画出任何窗体边框。</summary>
    public const uint WM_NCPAINT = 0x0085;
    /// <summary>非客户区激活态：无边框窗直接返回 TRUE，跳过 DefWindowProc 的边框重绘。</summary>
    public const uint WM_NCACTIVATE = 0x0086;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_MOUSELEAVE = 0x02A3;
    public const uint WM_SYSCOMMAND = 0x0112;
    /// <summary>最小化系统命令。Win+D / 显示桌面就是给每个可最小化顶层窗发这条。</summary>
    public const int SC_MINIMIZE = 0xF020;
    /// <summary>SC_* 命令码在 wParam 低 4 位，高位是系统标志位（0x8000 = 系统发起）。</summary>
    public const int SC_MASK = 0xFFF0;
    public const uint WM_MOVING = 0x0216;
    public const uint WM_WINDOWPOSCHANGING = 0x0046;
    public const uint WM_WINDOWPOSCHANGED = 0x0047;
    /// <summary>非客户区尺寸计算。wParam=TRUE 时返回 0 = 客户区铺满整个窗口（无边框窗标准手法）。</summary>
    public const uint WM_NCCALCSIZE = 0x0083;

    // WM_NCHITTEST 返回值
    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;
    public const int HTNOWHERE = 0;
    public const int HTTRANSPARENT = -1;

    // 鼠标钩子
    public const int WH_MOUSE_LL = 14;
    public const int WH_KEYBOARD_LL = 13;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_MBUTTONUP = 0x0208;

    // 键盘钩子
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint VK_LEFT = 0x25;
    public const uint VK_UP = 0x26;
    public const uint VK_RIGHT = 0x27;
    public const uint VK_DOWN = 0x28;
    public const uint VK_CONTROL = 0x11;
    public const uint VK_MENU = 0x12;
    public const uint VK_TAB = 0x09;
    public const uint VK_LWIN = 0x5B;
    public const uint VK_RWIN = 0x5C;
    public const uint VK_C = 0x43;
    public const uint VK_X = 0x58;
    public const uint VK_V = 0x56;
    public const uint VK_F2 = 0x71;
    public const uint VK_F5 = 0x74;
    public const uint VK_DELETE = 0x2E;

    [LibraryImport("user32.dll")]
    public static partial uint GetDoubleClickTime();

    public const int SM_CXDOUBLECLK = 36;
    public const int SM_CYDOUBLECLK = 37;
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const int SM_CMONITORS = 80;
    public const int SM_CXDRAG = 68;
    public const int SM_CYDRAG = 69;
    /// <summary>系统正在关机/注销流程中（SM_SHUTTINGDOWN）。关机被取消后会恢复 0。</summary>
    public const int SM_SHUTTINGDOWN = 0x2000;

    // SetWindowCompositionAttribute (未文档化 API)
    public const int WCA_ACCENT_POLICY = 19;
    public const int ACCENT_ENABLE_BLURBEHIND = 3;
    public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    // 磨砂探测：DWM 合成 / 高对比度
    public const uint SPI_GETHIGHCONTRAST = 0x0042;
    public const uint HCF_HIGHCONTRASTON = 0x0001;

    // 全局钩子
    public const int GWL_WNDPROC = -4;

    // SetWindowPos 标志
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_NOSENDCHANGING = 0x0400;

    public static readonly IntPtr HWND_BOTTOM = new(1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_TOP = new(0);

    // 虚拟屏幕指标
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    // SystemParametersInfo
    public const uint SPI_GETWORKAREA = 0x0030;

    // SetWindowRgn
    public const int RGN_OR = 2;

    // Shell Hook
    public const int WM_SHELLHOOK = 0x0000D008;
    public const int HSHELL_WINDOWACTIVATED = 4;
    public const int HSHELL_WINDOWCREATED = 1;
    public const int HSHELL_WINDOWDESTROYED = 2;
    public const int HSHELL_ENDTASK = 10;

    // 神秘消息：触发 Progman 创建 WorkerW
    public const uint WM_PROGMAN = 0x052C;

    #region P/Invoke 声明

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowW(string? lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowExW(nint hWndParent, nint hWndChildAfter, string? lpszClass, string? lpszWindow);

    [LibraryImport("user32.dll")]
    public static partial nint SetParent(nint hWndChild, nint hWndNewParent);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetPropW(nint hWnd, string lpString);

    [LibraryImport("ole32.dll")]
    public static partial int RegisterDragDrop(nint hwnd, nint pDropTarget);

    [LibraryImport("ole32.dll")]
    public static partial int RevokeDragDrop(nint hwnd);

    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_VM_WRITE = 0x0020;
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint PAGE_READWRITE = 0x04;

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("user32.dll")]
    public static partial nint GetShellWindow();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    // ---- 令牌完整性级别（判断壳 explorer 是否跑在比本进程更高的 IL） ----
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;   // 别写成 0x0400（那是 QUERY_INFORMATION，跨 IL 会被 MIC 拒）
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenIntegrityLevel = 25;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint ProcessHandle, uint dwDesiredAccess, out nint TokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(nint TokenHandle, int InformationClass, nint Buffer,
        uint BufferLength, out uint ReturnLength);

    [LibraryImport("advapi32.dll")]
    public static partial nint GetSidSubAuthorityCount(nint pSid);

    [LibraryImport("advapi32.dll")]
    public static partial nint GetSidSubAuthority(nint pSid, uint nSubAuthority);

    // ---- 创建进程时重定向父进程（绕过游戏反作弊对父进程链的检测） ----
    public const uint PROCESS_CREATE_PROCESS = 0x0080;
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    public const uint PROC_THREAD_ATTRIBUTE_PARENT_PROCESS = 0x00020000;
    public const uint STARTF_USESHOWWINDOW = 0x00000001;
    /// <summary>
    /// 挂起创建新进程：主线程以挂起态启动，不执行任何 DLL 初始化。
    /// 非挂起模式下 CreateProcessW 会同步等待子进程完成初始化（静态导入 DLL 的 DllMain 等），
    /// 冷启动 .NET/Avalonia 单文件应用实测能阻塞 500~900ms；若调用点在 UI 线程上，
    /// 整个桌面层会跟着冻结（见 LaunchHelper.TryLaunchAsExplorerChild）。
    /// </summary>
    public const uint CREATE_SUSPENDED = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitializeProcThreadAttributeList(
        nint lpAttributeList, int dwAttributeCount, uint dwFlags, ref nuint lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateProcThreadAttribute(
        nint lpAttributeList, uint dwFlags, uint Attribute,
        nint lpValue, nuint cbSize, nint lpPreviousValue, nint lpReturnSize);

    [LibraryImport("kernel32.dll")]
    public static partial void DeleteProcThreadAttributeList(nint lpAttributeList);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessW(
        string? lpApplicationName,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpCommandLine,
        nint lpProcessAttributes, nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags, nint lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEXW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    /// <summary>
    /// 放行一个以 CREATE_SUSPENDED 创建的线程。
    /// 返回之前的挂起计数（正常为 1）；失败返回 (uint)-1（0xFFFFFFFF）。
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(nint hThread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll")]
    public static partial nint VirtualAllocEx(nint hProcess, nint lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualFreeEx(nint hProcess, nint lpAddress, nuint dwSize, uint dwFreeType);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteProcessMemory(nint hProcess, nint lpBaseAddress, nint lpBuffer, nuint nSize, out nint lpNumberOfBytesWritten);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadProcessMemory(nint hProcess, nint lpBaseAddress, nint lpBuffer, nuint nSize, out nint lpNumberOfBytesRead);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int X, int Y);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // ---------------------------------------------------------------- Z 序 band（方案⑤）
    // Windows 内部把 Z 序切成若干「band」：DESKTOP < BELOWDESKTOP < NORMAL < ABOVENORMAL ...
    // **跨 band 的相对顺序由 band 硬保证，系统重排（Win+D / Show Desktop / Explorer 整理桌面）
    // 只能在 band 内部动窗口，动不了跨 band 的关系。**
    //
    // 这正好替代方案⑤原本要做的「把合成层挂进分区窗自己 HWND」：
    // 玻璃层放进 ZBID_DESKTOP、分区卡留在 ZBID_NORMAL —— 那么「层永远在卡片之下」变成
    // 平台不变量，根本不存在谁盖谁，也不需要任何运行时 Z 序维护。
    //
    // ⚠️ 未文档化导出（Win8+ 稳定存在），失败无副作用，调用方按返回值决定是否继续用旧逻辑。
    public const uint ZBID_DESKTOP = 0x00000001;
    public const uint ZBID_BELOWDESKTOP = 0x00000002;
    public const uint ZBID_NORMAL = 0x00000003;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowBand(nint hWnd, nint hWndInsertAfter, uint dwBand);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowBand(nint hWnd, out uint pdwBand);

    [LibraryImport("user32.dll")]
    public static partial nint BeginDeferWindowPos(int nNumWindows);

    [LibraryImport("user32.dll")]
    public static partial nint DeferWindowPos(nint hWinPosInfo, nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndDeferWindowPos(nint hWinPosInfo);

    [LibraryImport("user32.dll")]
    public static partial int GetWindowLongW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    public static partial int SetWindowLongW(nint hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    /// <summary>WM_NCCALCSIZE 的参数：rgrc[0] = 系统提议的窗口矩形，rgrc[1] = 系统提议的客户区矩形。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NCCALCSIZE_PARAMS
    {
        public RECT rgrc0;
        public RECT rgrc1;
        public RECT rgrc2;
        public nint hwndDesktop;
    }

    [LibraryImport("user32.dll")]
    public static partial nint GetClassLongPtrW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    public static partial nint SetClassLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint hWnd, uint Msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial nint GetParent(nint hWnd);

    public const uint GA_PARENT = 1;
    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hwnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    /// <summary>窗口是否处于最小化（iconic）。此时 GetWindowRect 返回 -32000 的虚拟矩形，不能拿去对齐浮层。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetWindowTextLengthW(nint hWnd);

    // SetWindowRgn
    [LibraryImport("gdi32.dll")]
    public static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    public static partial int CombineRgn(nint hrgnDst, nint hrgnSrc1, nint hrgnSrc2, int fnCombineMode);

    [LibraryImport("user32.dll")]
    public static partial int SetWindowRgn(nint hWnd, nint hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    /// <summary>把窗口当前 Region 拷进 hRgn。返回 0=失败 1=NULLREGION(空) 2=单矩形 3=复合</summary>
    [LibraryImport("user32.dll")]
    public static partial int GetWindowRgn(nint hWnd, nint hRgn);

    [LibraryImport("gdi32.dll")]
    public static partial int GetRgnBox(nint hRgn, ref RECT lprgndata);

    /// <summary>lpRect=NULL 即整窗失效；bErase 让系统先擦背景</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(nint hWnd, nint lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint hObject);

    // Shell Hook
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterShellHookWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeregisterShellHookWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in MSG lpMsg);

    [LibraryImport("user32.dll")]
    public static partial nint DispatchMessageW(in MSG lpMsg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    public const uint WM_QUIT = 0x0012;
    public const uint WM_QUERYENDSESSION = 0x0011;
    public const uint WM_ENDSESSION = 0x0016;

    /// <summary>电源事件广播（wParam = PBT_*）</summary>
    public const uint WM_POWERBROADCAST = 0x0218;
    /// <summary>系统即将进入睡眠/休眠</summary>
    public const uint PBT_APMSUSPEND = 0x0004;
    /// <summary>从严重低电/紧急状态恢复</summary>
    public const uint PBT_APMRESUMECRITICAL = 0x0006;
    /// <summary>用户操作后恢复（秒表/定时器）</summary>
    public const uint PBT_APMRESUMESUSPEND = 0x0007;
    /// <summary>系统自动恢复（睡眠/休眠/现代待机醒来，必然触发）</summary>
    public const uint PBT_APMRESUMEAUTOMATIC = 0x0012;

    /// <summary>
    /// WM_QUERYENDSESSION 的 lParam 标志位：由 Restart Manager（安装器 CloseApplications）发出，
    /// 表示「能否关闭该应用以更新文件」，是**可取消的关闭查询**，不是真正的关机/注销。
    /// 真关机/注销 lParam 为 ENDSESSION_LOGOFF(0x80000000) 或 0，不带此位。
    /// </summary>
    public const int ENDSESSION_CLOSEAPP = 0x00000001;
    public const uint QS_ALLINPUT = 0x04FF;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x102;
    public const uint WAIT_FAILED = 0xFFFFFFFF;
    public const uint INFINITE = 0xFFFFFFFF;

    /// <summary>SHUTDOWN_NORETRY：关机时不要弹「应用未响应」重试框。</summary>
    public const uint SHUTDOWN_NORETRY = 0x1;

    /// <summary>
    /// 关机保护：创建阻止原因（系统关机界面显示文案并等我们做完）。
    /// 文档软件「保存后再关机」同款机制。
    /// </summary>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShutdownBlockReasonCreate(nint hWnd, string pwszReason);

    /// <summary>解除关机阻止，放行关机。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShutdownBlockReasonDestroy(nint hWnd);

    /// <summary>
    /// 提高本进程关机通知级别，让 WM_QUERYENDSESSION 更早到达（在 Explorer 大量关掉之前做完还原）。
    /// </summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessShutdownParameters(uint dwLevel, uint dwFlags);

    /// <summary>
    /// 同时等待句柄或线程消息队列（QS_ALLINPUT）。
    /// 空闲时阻塞，避免 PeekMessage+Sleep 空转；有消息/句柄信号即返回。
    /// 返回：WAIT_OBJECT_0+i=句柄 i；WAIT_OBJECT_0+nCount=有消息；WAIT_TIMEOUT=超时。
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial uint MsgWaitForMultipleObjects(
        uint nCount, [In] nint[] pHandles, [MarshalAs(UnmanagedType.Bool)] bool bWaitAll,
        uint dwMilliseconds, uint dwWakeMask);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint DefWindowProcW(nint hWnd, uint uMsg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint RegisterClassW(in WNDCLASSW lpWndClass);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hWnd, int nCmdShow);

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;
    public const int SW_SHOWNA = 8;
    public const int SW_SHOWNOACTIVATE = 4;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindowLongPtrW(nint hWnd, int nIndex);

    /// <summary>窗口是否处于「最大化」状态。真全屏 / 无边框全屏游戏不会走 SW_MAXIMIZE，据此可与最大化软件区分。</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(nint hWnd);

    // Monitor
    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hWnd, uint dwFlags);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(POINT pt, uint dwFlags);

    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [LibraryImport("user32.dll")]
    public static partial nint CallWindowProcW(nint lpPrevWndFunc, nint hWnd, uint Msg, nuint wParam, nint lParam);

    // CoTaskMemFree (释放 COM 分配的内存)
    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint pv);

    // GetDpiForWindow (Win10 1607+)
    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hWnd);

    // GetDpiForSystem (Win10 1607+)：主屏在登录会话里的系统 DPI，UiScale 首启采样用
    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForSystem();

    // GetDpiForMonitor (shcore, Win 8.1+)
    public const int MDT_EFFECTIVE_DPI = 0;

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    // MapWindowPoints
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MapWindowPoints(nint hWndFrom, nint hWndTo, ref POINT lpPoints, uint cPoints);

    // SetForegroundWindow（弹出 Shell 上下文菜单前设置菜单 owner 为前台）
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hWnd);

    // ListView Messages
    public const uint LVM_FIRST = 0x1000;
    public const uint LVM_GETITEMCOUNT = LVM_FIRST + 4;
    public const uint LVM_GETITEMA = LVM_FIRST + 7;
    public const uint LVM_GETITEMW = LVM_FIRST + 75;
    public const uint LVM_GETITEMPOSITION = LVM_FIRST + 16;
    public const uint LVM_SETITEMPOSITION = LVM_FIRST + 15;
    public const uint LVM_SETITEMPOSITION32 = LVM_FIRST + 49;
    public const uint LVM_HITTEST = LVM_FIRST + 18;
    public const uint LVM_GETITEMTEXTW = LVM_FIRST + 115;
    public const uint LVM_GETCOLUMNOPTIONS = LVM_FIRST + 59;
    /// <summary>取图标间距：wParam=是否小图标视图；返回 LOWORD=x, HIWORD=y；无图标时可能为 -1</summary>
    public const uint LVM_GETICONSPACING = LVM_FIRST + 60;
    /// <summary>取项矩形：wParam=index, lParam=RECT*；先填 left=LVIR_* 再发送</summary>
    public const uint LVM_GETITEMRECT = LVM_FIRST + 14;
    public const int LVIR_BOUNDS = 1;
    public const int LVIR_ICON = 0;

    // WinEvent：桌面图标列表重排 / 位置变化（改大小、排序、自动排列）
    public const uint EVENT_OBJECT_REORDER = 0x8004;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const int OBJID_CLIENT = -4;

    // LVITEM mask
    public const uint LVIF_TEXT = 0x0001;
    public const uint LVIF_PARAM = 0x0004;
    public const uint LVIF_STATE = 0x0008;

    // LVITEM state
    public const uint LVIS_FOCUSED = 0x0001;
    public const uint LVIS_SELECTED = 0x0002;

    // LVM_HITTEST flags
    public const int LVHT_NOWHERE = 0x0001;
    public const int LVHT_ONITEMICON = 0x0002;
    public const int LVHT_ONITEMLABEL = 0x0004;
    public const int LVHT_ONITEM = 0x000E;

    [StructLayout(LayoutKind.Sequential)]
    public struct LVITEMW
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public nint pszText;
        public int cchTextMax;
        public int iImage;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LVHITTESTINFO
    {
        public POINT pt;
        public uint flags;
        public int iItem;
    }

    // SendMessageTimeout (用于 0x052C)
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SendMessageTimeoutW(nint hWnd, uint Msg, nuint wParam, nint lParam, uint fuFlags, uint uTimeout, out nuint lpdwResult);

    // SendMessage (同步)
    [LibraryImport("user32.dll")]
    public static partial nint SendMessageW(nint hWnd, uint Msg, nuint wParam, nint lParam);

    public const uint SMTO_NORMAL = 0x0000;

    // SystemParametersInfoW
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    // SetWindowCompositionAttribute (未文档化)
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowCompositionAttribute(nint hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    // DWM 合成是否开启：关闭时 ACCENT 必然无效
    [LibraryImport("dwmapi.dll")]
    public static partial int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool pfEnabled);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct HIGHCONTRASTW
    {
        public uint cbSize;
        public uint dwFlags;
        public nint lpszDefaultScheme;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfoW(uint uiAction, uint uiParam, ref HIGHCONTRASTW pvParam, uint fWinIni);

    // 探针窗口（STATIC + accent）
    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hWnd, nint hDC);

    // SetWindowsHookEx / UnhookWindowsHookEx / CallNextHookEx
    [LibraryImport("user32.dll")]
    public static partial nint SetWindowsHookExW(int idHook, nint lpfn, nint hmod, uint dwThreadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

    // GetAsyncKeyState
    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    // WindowFromPoint
    [LibraryImport("user32.dll")]
    public static partial nint WindowFromPoint(POINT Point);

    // RegisterWindowMessage
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessageW(string lpString);

    // RegisterHotKey / UnregisterHotKey
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    // DwmExtendFrameIntoClientArea
    [LibraryImport("dwmapi.dll")]
    public static partial int DwmExtendFrameIntoClientArea(nint hWnd, ref MARGINS pMargins);

    // DwmSetWindowAttribute
    public const int DWMWA_NCRENDERING_POLICY = 2;
    /// <summary>DWMWA_NCRENDERING_ENABLED（=1）：**可读**，反映 DWM 当前是否真的在渲染非客户区。
    /// POLICY(2) 只能写不能读，做不了判据；用它实现「校验式自愈」。</summary>
    public const int DWMWA_NCRENDERING_ENABLED = 1;
    /// <summary>DWMWA_CLOAK：合成层隐形，不 SW_HIDE，避免显隐闪屏</summary>
    public const int DWMWA_CLOAK = 13;
    /// <summary>DWMWA_CLOAKED：读取是否被 DWM 披风（隐形但仍可被 WindowFromPoint 命中）</summary>
    public const int DWMWA_CLOAKED = 14;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
    /// <summary>DWMWA_TRANSITIONS_FORCEDISABLED：禁用窗口过渡，避免系统补画阴影</summary>
    public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    // DWMNCRENDERINGPOLICY: USEWINDOWSTYLE=0 / DISABLED=1 / ENABLED=2
    // ⚠️ DISABLED 是 1，写成 0 会变成「跟随窗口样式」= 照样画系统边框/阴影
    public const int DWMNCRP_DISABLED = 1;
    public const int DWMNCRP_ENABLED = 2;
    // Win11 系统材质：Mica=2, Acrylic=3, Tabbed=4
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    // Win11 自动圆角（与 Avalonia CornerRadius 叠会显得外框很粗）
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWCP_ROUNDSMALL = 3;
    /// <summary>DWMWA_BORDER_COLOR：Win11 窗口描边色</summary>
    public const int DWMWA_BORDER_COLOR = 34;
    /// <summary>
    /// DWMWA_COLOR_DEFAULT：让 DWM 用**系统默认**描边色（0xFFFFFFFF，即深色主题下那圈 1px 灰边）。
    /// 注意别和下面的 NONE 搞混 —— 写成 DEFAULT 等于「保留系统描边」。
    /// </summary>
    public const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);
    /// <summary>
    /// DWMWA_COLOR_NONE：**隐藏**系统描边（0xFFFFFFFE）。
    /// 分区/弹窗四边那圈「脏脏的」1px 灰边就是系统描边，必须用这个值才真正去掉。
    /// </summary>
    public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    // DwmEnableBlurBehindWindow（区域模糊；嵌入子窗口上可能无效，作尝试路径）
    public const uint DWM_BB_ENABLE = 0x00000001;
    public const uint DWM_BB_BLURREGION = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fEnable;
        public nint hRgnBlur;
        [MarshalAs(UnmanagedType.Bool)] public bool fTransitionOnMaximized;
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmEnableBlurBehindWindow(nint hWnd, ref DWM_BLURBEHIND pBlurBehind);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

    // GetForegroundWindow
    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    // 激活三件套：前台（跨进程）/ 本线程 active 窗 / 键盘焦点。
    // 只 SetForegroundWindow 时，带过 WS_EX_NOACTIVATE 的窗口会「前台但不算 active」，
    // Avalonia 的 IME 光标矩形就取不到位置（候选窗跑到屏幕右上角）。
    [LibraryImport("user32.dll")]
    public static partial nint GetActiveWindow();

    [LibraryImport("user32.dll")]
    public static partial nint SetActiveWindow(nint hWnd);

    // 让 Avalonia 把线程级 IME 重新绑定到本窗：发 WM_INPUTLANGCHANGE 会走 UpdateInputMethod
    [LibraryImport("user32.dll")]
    public static partial nint GetKeyboardLayout(uint idThread);

    public const uint WM_INPUTLANGCHANGE = 0x0051;

    // IMM32 诊断/兜底：看窗口有没有挂上输入法上下文、中/英开关是什么状态
    [LibraryImport("imm32.dll")]
    public static partial nint ImmGetContext(nint hWnd);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ImmReleaseContext(nint hWnd, nint hIMC);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ImmGetOpenStatus(nint hIMC);

    [LibraryImport("imm32.dll")]
    public static partial nint ImmCreateContext();

    [LibraryImport("imm32.dll")]
    public static partial nint ImmAssociateContext(nint hWnd, nint hIMC);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ImmSetOpenStatus(nint hIMC, [MarshalAs(UnmanagedType.Bool)] bool fOpen);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ImmNotifyIME(nint hIMC, uint dwAction, uint dwIndex, uint dwValue);

    public const uint NI_COMPOSITIONSTR = 0x0015;
    public const uint CPS_CANCEL = 0x0004;

    // AttachThreadInput / GetCurrentThreadId：前台锁死时借前台线程的输入队列抢前台（见 DesktopEmbedService.TryActivate）
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    // ===== 工作集裁剪（桌面不可见时把空页还给系统）=====
    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessWorkingSetSize(nint hProcess, nint dwMinimumWorkingSetSize, nint dwMaximumWorkingSetSize);

    /// <summary>
    /// 把本进程工作集压到最小（等价 EmptyWorkingSet）：内存不算「释放」，而是换出到 standby，
    /// 其它程序缺内存时优先从我们这里拿。回到前台会重新缺页，所以**只在桌面 UI 不可见时**调。
    /// </summary>
    public static bool TrimWorkingSet()
    {
        try { return SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1); }
        catch { return false; }
    }

    // ScreenToClient
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hWnd, ref POINT lpPoint);

    // GetWindow (按 Z-Order 遍历窗口)
    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint hWnd, uint uCmd);

    /// <summary>Z 序最顶层窗口的句柄（沿 GW_HWNDNEXT 即从最上往最下走）</summary>
    [LibraryImport("user32.dll")]
    public static partial nint GetTopWindow(nint hWnd);

    /// <summary>阻塞到 DWM 完成一次合成。不新建窗口、不动 Z 序，用来在显示后逼出一个真实合成周期。</summary>
    [LibraryImport("dwmapi.dll")]
    public static partial int DwmFlush();

    public const uint GW_HWNDFIRST = 0;
    public const uint GW_HWNDLAST = 1;
    public const uint GW_HWNDNEXT = 2;
    public const uint GW_HWNDPREV = 3;
    public const uint GW_OWNER = 4;
    public const uint GW_CHILD = 5;

    // SendInput（真实输入队列，多屏坐标按虚拟桌面归一化）
    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    public const uint WM_INITMENUPOPUP = 0x0117;
    public const uint WM_MENUCHAR = 0x0120;
    public const uint WM_MEASUREITEM = 0x002C;
    public const uint WM_DRAWITEM = 0x002B;

    [LibraryImport("user32.dll")]
    public static partial uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    /// <summary>屏幕坐标 → MOUSEEVENTF_ABSOLUTE|VIRTUALDESK 归一化坐标（多屏）</summary>
    public static (int dx, int dy) ScreenToAbsolute(int screenX, int screenY)
    {
        var vs = GetVirtualScreenRect();
        int w = Math.Max(1, vs.Right - vs.Left);
        int h = Math.Max(1, vs.Bottom - vs.Top);
        int dx = (int)Math.Round((screenX - vs.Left) * 65535.0 / (w - 1));
        int dy = (int)Math.Round((screenY - vs.Top) * 65535.0 / (h - 1));
        dx = Math.Clamp(dx, 0, 65535);
        dy = Math.Clamp(dy, 0, 65535);
        return (dx, dy);
    }

    /// <summary>
    /// 临时点击穿透：加/去 WS_EX_TRANSPARENT。
    /// 不要用空 Region——那会连绘制一起裁掉，造成壁纸/面板闪烁。
    /// </summary>
    public static void SetClickThroughStyle(nint hwnd, bool through)
    {
        if (hwnd == nint.Zero) return;
        var ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        if (through)
            ex = (nint)((nuint)ex | WS_EX_TRANSPARENT);
        else
            ex = (nint)((nuint)ex & ~WS_EX_TRANSPARENT);
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ex);
    }

    #endregion

    #region 委托和结构体

    public delegate nint LowLevelMouseProc(int nCode, nuint wParam, nint lParam);
    public delegate nint LowLevelKeyboardProc(int nCode, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    public delegate nint WndProcDelegate(nint hWnd, uint uMsg, nuint wParam, nint lParam);
    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    public delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public nint hwnd;
        public nint hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSW
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attribute;
        public nint Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MSGF = 0;

    // VK_ 键码
    public const int VK_SHIFT = 0x10;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;

    #endregion

    /// <summary>
    /// 枚举所有显示器的**物理**矩形（rcMonitor，不是工作区）。按「左→右、上→下」排序，
    /// 保证同一组显示器每次顺序一致 —— 覆盖判定用它拼 key 比较状态变化。
    /// </summary>
    public static List<RECT> GetMonitorRects()
    {
        var list = new List<RECT>(4);
        try
        {
            EnumDisplayMonitors(nint.Zero, nint.Zero,
                (nint hMon, nint hdc, ref RECT clip, nint data) =>
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    list.Add(GetMonitorInfoW(hMon, ref mi) ? mi.rcMonitor : clip);
                    return true;
                }, nint.Zero);
        }
        catch { }

        // 枚举顺序不保证稳定，统一排序后再用
        list.Sort((a, b) => a.Left != b.Left ? a.Left.CompareTo(b.Left) : a.Top.CompareTo(b.Top));
        return list;
    }

    /// <summary>获取虚拟桌面的完整矩形（覆盖所有显示器）</summary>
    public static RECT GetVirtualScreenRect()
    {
        return new RECT
        {
            Left = GetSystemMetrics(SM_XVIRTUALSCREEN),
            Top = GetSystemMetrics(SM_YVIRTUALSCREEN),
            Right = GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN),
            Bottom = GetSystemMetrics(SM_YVIRTUALSCREEN) + GetSystemMetrics(SM_CYVIRTUALSCREEN)
        };
    }

    // ───────────────────────── 系统信息（系统监控组件用） ─────────────────────────

    /// <summary>
    /// 全机 CPU 时间统计（内核时间已包含空闲时间）。两次采样做差即可算出全程 CPU 占用率，
    /// 不依赖本地化的 PDH 计数器路径（中文系统上英文计数器名不可靠）。
    /// </summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    /// <summary>物理内存状态：占用百分比 + 总量 / 可用量（字节）。失败返回 false。</summary>
    public static bool GetMemoryStatus(out uint loadPercent, out ulong totalPhys, out ulong availPhys)
    {
        var buf = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref buf))
        {
            loadPercent = 0; totalPhys = 0; availPhys = 0;
            return false;
        }
        loadPercent = buf.dwMemoryLoad;
        totalPhys = buf.ullTotalPhys;
        availPhys = buf.ullAvailPhys;
        return true;
    }

    // ───────────────────────── 提示音 ─────────────────────────

    /// <summary>MB_ICONASTERISK：系统「星号」提示音，音量跟随系统设置。</summary>
    private const uint MB_ICONASTERISK = 0x00000040;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint uType);

    /// <summary>
    /// 播一声系统提示音（计时器到点用）。非阻塞、不占用 UI 线程，
    /// 也不用 System.Media（裁剪后没有该实现）。
    /// </summary>
    public static void PlayBeep() => MessageBeep(MB_ICONASTERISK);
}


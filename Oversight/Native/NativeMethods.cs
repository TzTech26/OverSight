using System.Runtime.InteropServices;

namespace Oversight.Native;

internal static class NativeMethods
{
    // ── Magnification API (Magnification.dll) ─────────────────────────────────

    [DllImport("Magnification.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MagInitialize();

    [DllImport("Magnification.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MagUninitialize();

    /// <summary>Sets fullscreen magnification factor and source offset.</summary>
    /// <param name="magLevel">Zoom factor (1.0 = no zoom, 2.0 = 2× zoom, max 4096).</param>
    /// <param name="xOffset">Physical-pixel X of the top-left of the source region.</param>
    /// <param name="yOffset">Physical-pixel Y of the top-left of the source region.</param>
    [DllImport("Magnification.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MagSetFullscreenTransform(float magLevel, int xOffset, int yOffset);

    [DllImport("Magnification.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MagGetFullscreenTransform(out float magLevel, out int xOffset, out int yOffset);

    // ── Window magnifier (per-monitor lens) ───────────────────────────────────

    public const string WC_MAGNIFIER = "Magnifier";

    /// <summary>Sets the area of the desktop (physical pixels) shown by the control.</summary>
    [DllImport("Magnification.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MagSetWindowSource(IntPtr hwnd, RECT rect);

    /// <summary>Sets the 3×3 magnification transform for a magnifier control.</summary>
    [DllImport("Magnification.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MagSetWindowTransform(IntPtr hwnd, ref MAGTRANSFORM pTransform);

    /// <summary>Windows magnified/excluded by the control (used to exclude our own host).</summary>
    [DllImport("Magnification.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MagSetWindowFilterList(IntPtr hwnd, int dwFilterMode, int count, IntPtr[] pHWND);

    public const int MW_FILTERMODE_EXCLUDE = 0;
    public const int MW_FILTERMODE_INCLUDE = 1;

    // Magnifier control window styles
    public const int MS_SHOWMAGNIFIEDCURSOR = 0x0001;
    public const int MS_CLIPAROUNDCURSOR    = 0x0002;
    public const int MS_INVERTCOLORS        = 0x0004;

    // ── Win32 window management (for the magnifier host window) ────────────────

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint    cbSize;
        public uint    style;
        public WndProc lpfnWndProc;
        public int     cbClsExtra;
        public int     cbWndExtra;
        public IntPtr  hInstance;
        public IntPtr  hIcon;
        public IntPtr  hCursor;
        public IntPtr  hbrBackground;
        public string? lpszMenuName;
        public string  lpszClassName;
        public IntPtr  hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    public const uint WS_EX_TOPMOST     = 0x00000008;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_TOOLWINDOW  = 0x00000080;
    public const uint WS_EX_LAYERED     = 0x00080000;
    public const uint WS_EX_NOACTIVATE  = 0x08000000;

    public const uint WS_POPUP   = 0x80000000;
    public const uint WS_CHILD   = 0x40000000;
    public const uint WS_VISIBLE = 0x10000000;

    public const int SW_HIDE   = 0;
    public const int SW_SHOWNA = 8;   // show without activating

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE     = 0x0001;
    public const uint SWP_NOMOVE     = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOZORDER   = 0x0004;
    public const uint LWA_ALPHA      = 0x00000002;

    // ── Per-pixel-alpha layered window (crosshair overlay) ────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc,
        uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    public const uint ULW_ALPHA    = 0x00000002;
    public const byte AC_SRC_OVER  = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    // ── Global hotkeys ────────────────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ── WinEvent hook (foreground tracking) ───────────────────────────────────

    public delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // ── Low-level mouse hook (scroll-to-zoom) ─────────────────────────────────

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // ── Foreground / process ──────────────────────────────────────────────────

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>Non-zero <c>DWMWA_CLOAKED</c> = the window is hidden by DWM (suspended UWP hosts, other desktops).</summary>
    public const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    /// <summary>
    /// Sends a message but gives up after <paramref name="uTimeout"/> ms. The auto-clicker uses it
    /// with WM_NULL to check whether the foreground window's message pump is keeping up; a timeout
    /// (return 0) means the app is saturated, so the click loop backs off.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    public const uint WM_NULL          = 0x0000;
    public const uint SMTO_BLOCK       = 0x0001;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    // ── Window enumeration (running-apps picker) ──────────────────────────────

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    public const int  GWL_EXSTYLE = -20;

    // ── Cursor ────────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>Moves the cursor to a screen position in physical pixels (auto-clicker fixed point).</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int X, int Y);

    // ── Synthetic input (auto-clicker) ────────────────────────────────────────

    /// <summary>Injects synthesized keystrokes / mouse events into the input stream.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>Translates a virtual-key code to a scancode (auto-clicker sends keys by scancode for game compatibility).</summary>
    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    public const uint MAPVK_VK_TO_VSC = 0;

    public const int INPUT_MOUSE    = 0;
    public const int INPUT_KEYBOARD = 1;

    public const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP     = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN  = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP    = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;
    public const uint MOUSEEVENTF_XDOWN      = 0x0080;
    public const uint MOUSEEVENTF_XUP        = 0x0100;

    public const uint XBUTTON1 = 0x0001;
    public const uint XBUTTON2 = 0x0002;

    public const uint KEYEVENTF_KEYUP       = 0x0002;
    public const uint KEYEVENTF_SCANCODE    = 0x0008;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int    dx;
        public int    dy;
        public uint   mouseData;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>Union of the input payloads (mouse is the largest member; keyboard overlaps it).</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public int        type;
        public INPUTUNION u;
    }

    // ── Keyboard state (hold-to-zoom release polling) ─────────────────────────

    /// <summary>
    /// Reports whether a virtual key is currently down. The high-order bit (0x8000) of the
    /// return value is set while the key is pressed. Used to poll for release in hold-to-zoom
    /// mode, since RegisterHotKey only signals key-down.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    // GetModuleHandle lives in kernel32, not user32.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ── High-resolution timer (winmm) — used by the auto-clicker for fast intervals ──

    /// <summary>Raises the system timer resolution (ms) so Thread.Sleep is precise down to ~1 ms.</summary>
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint uMilliseconds);

    /// <summary>Restores the system timer resolution raised by <see cref="TimeBeginPeriod"/>. Must be paired.</summary>
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint uMilliseconds);

    // ── Registry (startup) via Microsoft.Win32.Registry — no P/Invoke needed ─

    // ── Constants ─────────────────────────────────────────────────────────────

    public const uint WM_HOTKEY      = 0x0312;
    public const int  WM_MOUSEWHEEL  = 0x020A;
    public const int  WM_LBUTTONDOWN = 0x0201;
    public const int  WM_RBUTTONDOWN = 0x0204;
    public const int  HC_ACTION      = 0;
    public const int  WH_MOUSE_LL    = 14;

    // RegisterHotKey modifier flags
    public const uint MOD_NONE     = 0x0000;
    public const uint MOD_ALT      = 0x0001;
    public const uint MOD_CONTROL  = 0x0002;
    public const uint MOD_SHIFT    = 0x0004;
    public const uint MOD_WIN      = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    // WinEvent hook
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT   = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    // ── Structs ───────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    /// <summary>3×3 magnification transform matrix (row-major, 9 floats).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MAGTRANSFORM
    {
        public float m00, m01, m02;
        public float m10, m11, m12;
        public float m20, m21, m22;

        public static MAGTRANSFORM Scale(float s) => new()
        {
            m00 = s, m11 = s, m22 = 1.0f,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT  pt;
        public uint   mouseData;   // HIWORD = wheel delta when wParam == WM_MOUSEWHEEL
        public uint   flags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }
}

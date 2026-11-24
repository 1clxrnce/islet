using System.Runtime.InteropServices;

namespace Islet;

// Win32 calls the island needs on its HWND: extended styles, DPI, the mouse hook and hotkeys.
internal static class Native
{
    public const int GwlStyle = -16;
    public const int GwlExStyle = -20;
    public const long WsPopup = 0x80000000L;
    // Caption, dialog frame, border, sizing frame and system menu: everything that draws a frame.
    public const long FrameStyles = 0x00C00000L | 0x00400000L | 0x00800000L | 0x00040000L | 0x00080000L;
    public const long WsExToolWindow = 0x00000080;
    public const long WsExAppWindow = 0x00040000;
    public const long WsExNoActivate = 0x08000000;
    public const long WsExTransparent = 0x00000020;
    public const long WsExLayered = 0x00080000;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpFrameChanged = 0x0020;
    public const uint SwpShowWindow = 0x0040;
    public const uint LwaAlpha = 0x2;
    public static readonly IntPtr HwndTopmost = new(-1);

    public const int WmMouseMove = 0x0200;
    public const uint WmQuit = 0x0012;
    public const int WhMouseLl = 14;

    public const int DwmwaWindowCornerPreference = 33;
    public const int DwmwaBorderColor = 34;
    public const int DwmwcpDoNotRound = 1;
    public const uint DwmwaColorNone = 0xFFFFFFFE;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Handle;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public int Time;
        public Point Point;
    }

    public delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    public static long GetExStyle(IntPtr hwnd) => GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
    public static void SetExStyle(IntPtr hwnd, long style) => SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public const uint LsfwLock = 1;
    public const uint LsfwUnlock = 2;

    [DllImport("user32.dll")]
    public static extern bool LockSetForegroundWindow(uint lockCode);

    [DllImport("user32.dll")]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int hook, LowLevelMouseProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out Msg message, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref Msg message);
}

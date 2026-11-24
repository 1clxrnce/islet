using System.Runtime.InteropServices;

namespace Islet;

// A rounded rectangle in physical screen pixels.
internal sealed record ScreenShape(double X, double Y, double Width, double Height, double Radius)
{
    public bool Contains(double px, double py)
    {
        if (px < X || py < Y || px >= X + Width || py >= Y + Height) return false;
        double r = Math.Min(Radius, Math.Min(Width, Height) / 2);
        // Inside the corner squares, the point must also be inside the corner circle.
        double cx = px < X + r ? X + r : px > X + Width - r ? X + Width - r : px;
        double cy = py < Y + r ? Y + r : py > Y + Height - r ? Y + Height - r : py;
        double dx = px - cx, dy = py - cy;
        return dx * dx + dy * dy <= r * r;
    }
}

// Low-level mouse hook on its own thread. The island never takes focus, so Windows would neither
// tell it about the cursor nor give it wheel events; the hook sees both without any polling.
// The callback runs for every mouse event in Windows: it only reads snapshots and posts to the UI.
internal static class MouseHook
{
    private static Native.LowLevelMouseProc? _proc;
    private static IntPtr _hook;
    private static uint _threadId;
    private static bool _overPill;

    // Where the pill is right now; null while it is hidden. Written by the UI thread.
    public static volatile ScreenShape? Pill;

    // Raised on the UI thread when the cursor enters or leaves the pill.
    public static event Action<bool>? PillHoverChanged;

    public static void Start()
    {
        if (_threadId != 0) return;
        _proc = Callback;
        var ready = new ManualResetEventSlim(); // Not disposed: the hook thread may set it after a timed-out wait.
        var thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            try
            {
                _hook = Native.SetWindowsHookEx(Native.WhMouseLl, _proc, Native.GetModuleHandle(null), 0);
                ready.Set();
                if (_hook == IntPtr.Zero)
                {
                    Log.Trace($"Mouse hook FAILED (error {Marshal.GetLastWin32Error()})");
                    return;
                }
                while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
            }
            finally
            {
                if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
                _threadId = 0;
            }
        })
        {
            IsBackground = true,
            Name = "islet mouse hook"
        };
        thread.Start();
        ready.Wait(2000);
    }

    public static void Stop()
    {
        Pill = null;
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    private static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam.ToInt64() == Native.WmMouseMove)
        {
            // MSLLHOOKSTRUCT: pt.x at 0, pt.y at 4.
            int x = Marshal.ReadInt32(lParam, 0);
            int y = Marshal.ReadInt32(lParam, 4);
            bool over = Pill is { } pill && pill.Contains(x, y);
            if (over != _overPill)
            {
                _overPill = over;
                Ui.Post(() => PillHoverChanged?.Invoke(over));
            }
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }
}

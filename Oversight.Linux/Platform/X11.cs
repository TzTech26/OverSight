using System.Runtime.InteropServices;
using System.Text;

namespace Oversight.Linux.Platform;

/// <summary>
/// The Xlib, XTest, XShape and XInput2 calls Oversight needs. They work on an X11 session and, under Wayland, through
/// XWayland (so for X11 and Proton games, not native Wayland windows). Event structs are read by offset: 64-bit only.
/// </summary>
public static unsafe partial class X11
{
    private const string Lib = "libX11.so.6";
    private const string Tst = "libXtst.so.6";
    private const string Ext = "libXext.so.6";
    private const string Xi = "libXi.so.6";

    public const int KeyPress = 2, KeyRelease = 3, ButtonPress = 4, ClientMessage = 33;
    public const long KeyPressMask = 1 << 0, ButtonPressMask = 1 << 2, SubstructureNotifyMask = 1 << 19;
    public const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod4Mask = 0x40;
    public const int GrabModeAsync = 1, IsViewable = 2, BadAccess = 10;
    public const int ShapeInput = 2, ShapeSet = 0;
    public const nint XA_ATOM = 4, XA_CARDINAL = 6, XA_STRING = 31, XA_WINDOW = 33;

    /// <summary>One XEvent (a 192-byte union). Fields are read by their LP64 offsets.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 192)]
    public struct XEvent
    {
        public int type;
        public int Int(int offset) { fixed (XEvent* e = &this) return *(int*)((byte*)e + offset); }
        public nint Long(int offset) { fixed (XEvent* e = &this) return *(nint*)((byte*)e + offset); }
        public uint State => (uint)Int(80);   // XKeyEvent.state / XButtonEvent.state
        public uint Detail => (uint)Int(84);  // XKeyEvent.keycode / XButtonEvent.button
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XErrorEvent
    {
        public int type;
        public nint display;
        public nint resourceid;
        public nuint serial;
        public byte error_code, request_code, minor_code;
    }

    public delegate int ErrorHandler(nint display, XErrorEvent* error);

    [LibraryImport(Lib)] public static partial int XInitThreads();
    [LibraryImport(Lib)] public static partial nint XOpenDisplay(nint name);
    [LibraryImport(Lib)] public static partial int XCloseDisplay(nint display);
    [LibraryImport(Lib)] public static partial nint XDefaultRootWindow(nint display);
    [LibraryImport(Lib)] public static partial int XConnectionNumber(nint display);
    [LibraryImport(Lib)] public static partial int XPending(nint display);
    [LibraryImport(Lib)] public static partial int XNextEvent(nint display, XEvent* ev);
    [LibraryImport(Lib)] public static partial int XSync(nint display, int discard);
    [LibraryImport(Lib)] public static partial int XFlush(nint display);
    [LibraryImport(Lib)] public static partial int XGrabKey(nint display, int keycode, uint modifiers, nint window, int ownerEvents, int pointerMode, int keyboardMode);
    [LibraryImport(Lib)] public static partial int XUngrabKey(nint display, int keycode, uint modifiers, nint window);
    [LibraryImport(Lib)] public static partial byte XKeysymToKeycode(nint display, nint keysym);
    [LibraryImport(Lib)] public static partial nint XSetErrorHandler(nint handler);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint XInternAtom(nint display, string name, int onlyIfExists);
    [LibraryImport(Lib)] public static partial int XGetWindowProperty(nint display, nint window, nint property, nint offset, nint length, int delete, nint reqType,
        out nint actualType, out int actualFormat, out nuint nitems, out nuint bytesAfter, out nint data);
    [LibraryImport(Lib)] public static partial int XFree(nint data);
    [LibraryImport(Lib)] public static partial int XQueryKeymap(nint display, byte* keys);
    [LibraryImport(Lib)] public static partial int XQueryPointer(nint display, nint window, out nint root, out nint child, out int rootX, out int rootY, out int winX, out int winY, out uint mask);
    [LibraryImport(Lib)] public static partial nint XGetModifierMapping(nint display);
    [LibraryImport(Lib)] public static partial int XFreeModifiermap(nint map);
    [LibraryImport(Lib)] public static partial int XkbSetDetectableAutoRepeat(nint display, int detectable, out int supported);
    [LibraryImport(Lib)] public static partial int XGetWindowAttributes(nint display, nint window, byte* attributes);
    [LibraryImport(Lib)] public static partial int XSelectInput(nint display, nint window, nint mask);
    [LibraryImport(Lib)] public static partial int XSendEvent(nint display, nint window, int propagate, nint mask, XEvent* ev);
    [LibraryImport(Lib)] public static partial int XGrabPointer(nint display, nint window, int ownerEvents, uint mask, int pointerMode, int keyboardMode, nint confineTo, nint cursor, nint time);
    [LibraryImport(Lib)] public static partial int XUngrabPointer(nint display, nint time);

    [LibraryImport(Tst)] public static partial int XTestFakeKeyEvent(nint display, uint keycode, int isPress, nuint delay);
    [LibraryImport(Tst)] public static partial int XTestFakeButtonEvent(nint display, uint button, int isPress, nuint delay);
    [LibraryImport(Tst)] public static partial int XTestFakeMotionEvent(nint display, int screen, int x, int y, nuint delay);

    [LibraryImport(Ext)] public static partial void XShapeCombineRectangles(nint display, nint window, int kind, int x, int y, nint rectangles, int count, int op, int ordering);
    [LibraryImport(Ext)] public static partial nint XShapeGetRectangles(nint display, nint window, int kind, out int count, out int ordering);

    [StructLayout(LayoutKind.Sequential)]
    private struct XIState { public int mask_len; public byte* mask; }

    [LibraryImport(Xi)] private static partial int XIGetClientPointer(nint display, nint window, out int deviceId);
    [LibraryImport(Xi)] private static partial int XIQueryPointer(nint display, int deviceId, nint window, out nint root, out nint child,
        out double rootX, out double rootY, out double winX, out double winY, out XIState buttons, out XIState mods, out XIState group);

    // ── Connections ──────────────────────────────────────────────────────────

    private static nint _query;
    private static readonly object QueryLock = new();

    /// <summary>
    /// A shared connection for polling keys, reading the focused window, sending XTest input and shaping windows. Zero when
    /// there's no X display (a Wayland session without XWayland). Safe from any thread: XInitThreads runs first.
    /// </summary>
    public static nint Display
    {
        get
        {
            lock (QueryLock)
            {
                if (_query == 0 && !_tried)
                {
                    _tried = true;
                    _query = Open();
                }
                return _query;
            }
        }
    }
    private static bool _tried;

    public static bool Available => Display != 0;

    /// <summary>Opens a new connection, or returns 0 when there's no display or libX11.</summary>
    public static nint Open()
    {
        try { return XOpenDisplay(0); }
        catch (DllNotFoundException) { return 0; }
    }

    /// <summary>Must run before any other Xlib call in the process (Avalonia calls it too; twice is fine).</summary>
    public static void InitThreads()
    {
        try { XInitThreads(); }
        catch (DllNotFoundException) { }
    }

    // ── Errors ───────────────────────────────────────────────────────────────

    private static ErrorHandler? _handler;
    private static nint _previous;
    private static readonly HashSet<nint> Watched = [];
    private static readonly Dictionary<nint, int> LastError = [];

    /// <summary>
    /// Records errors on <paramref name="display"/> (XGrabKey reports a taken key asynchronously as BadAccess) instead of
    /// letting Xlib's default handler exit the process. Errors on other connections go to the previous handler (Avalonia's).
    /// </summary>
    public static void WatchErrors(nint display)
    {
        lock (Watched)
        {
            Watched.Add(display);
            if (_handler is not null) return;
            _handler = OnError;
            _previous = XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(_handler));
        }
    }

    /// <summary>The last error code on <paramref name="display"/> since the previous call (0 = none).</summary>
    public static int TakeError(nint display)
    {
        lock (Watched) return LastError.Remove(display, out int code) ? code : 0;
    }

    private static int OnError(nint display, XErrorEvent* e)
    {
        lock (Watched)
        {
            if (Watched.Contains(display)) { LastError[display] = e->error_code; return 0; }
        }
        return _previous != 0 ? ((delegate* unmanaged<nint, XErrorEvent*, int>)_previous)(display, e) : 0;
    }

    // ── Properties, windows, processes ───────────────────────────────────────

    public static nint Atom(nint display, string name) => XInternAtom(display, name, 0);

    /// <summary>A format-32 property as an array of longs (atoms, windows, cardinals).</summary>
    public static nint[] Longs(nint display, nint window, string property, nint type)
    {
        if (XGetWindowProperty(display, window, Atom(display, property), 0, 1024, 0, type, out _, out int format, out nuint n, out _, out nint data) != 0 || data == 0)
            return [];
        try { return format == 32 ? new ReadOnlySpan<nint>((void*)data, (int)n).ToArray() : []; }
        finally { XFree(data); }
    }

    private static string? Text(nint display, nint window, string property)
    {
        if (XGetWindowProperty(display, window, Atom(display, property), 0, 1024, 0, XA_STRING, out _, out int format, out nuint n, out _, out nint data) != 0 || data == 0)
            return null;
        try { return format == 8 ? Encoding.UTF8.GetString((byte*)data, (int)n) : null; }
        finally { XFree(data); }
    }

    /// <summary>The window the window manager says has focus (_NET_ACTIVE_WINDOW), or 0.</summary>
    public static nint ActiveWindow(nint display) =>
        display == 0 ? 0 : Longs(display, XDefaultRootWindow(display), "_NET_ACTIVE_WINDOW", XA_WINDOW).FirstOrDefault();

    public static int WindowPid(nint display, nint window) =>
        window == 0 ? 0 : (int)Longs(display, window, "_NET_WM_PID", XA_CARDINAL).FirstOrDefault();

    /// <summary>
    /// The focused app's process name as Game Profiles match it: /proc/&lt;pid&gt;/comm without ".exe" (so Proton games read
    /// like they do on Windows), else the window's WM_CLASS instance. Null when nothing is focused or it's Oversight.
    /// </summary>
    public static (int Pid, string? Name) FocusedProcess()
    {
        nint d = Display;
        nint w = ActiveWindow(d);
        if (w == 0) return (0, null);
        int pid = WindowPid(d, w);
        string? name = pid > 0 ? ProcessName(pid) : null;
        name ??= Text(d, w, "WM_CLASS")?.Split('\0')[0] is { Length: > 0 } cls ? cls : null;
        return (pid, name);
    }

    public static string? ProcessName(int pid)
    {
        try
        {
            string comm = File.ReadAllText($"/proc/{pid}/comm").Trim();
            return comm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? comm[..^4] : comm;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static bool IsMapped(nint display, nint window)
    {
        byte* attributes = stackalloc byte[256];
        return XGetWindowAttributes(display, window, attributes) != 0 && *(int*)(attributes + 92) == IsViewable;
    }

    /// <summary>Clicks pass through the window: an empty input shape.</summary>
    public static void MakeClickThrough(nint display, nint window)
    {
        XShapeCombineRectangles(display, window, ShapeInput, 0, 0, 0, 0, ShapeSet, 0);
        XFlush(display);
    }

    public static int InputShapeRectangles(nint display, nint window)
    {
        nint rects = XShapeGetRectangles(display, window, ShapeInput, out int count, out _);
        if (rects != 0) XFree(rects);
        return count;
    }

    // ── Input state ──────────────────────────────────────────────────────────

    public static bool KeycodeDown(nint display, int keycode)
    {
        if (display == 0 || keycode <= 0) return false;
        byte* keys = stackalloc byte[32];
        XQueryKeymap(display, keys);
        return (keys[keycode >> 3] & (1 << (keycode & 7))) != 0;
    }

    private static bool? _haveXi;

    /// <summary>Whether mouse button <paramref name="button"/> (1-3, 8, 9) is down. XInput2 sees 8 and 9; the core protocol doesn't.</summary>
    public static bool ButtonDown(nint display, int button)
    {
        if (display == 0) return false;
        nint root = XDefaultRootWindow(display);
        if (_haveXi != false)
        {
            try
            {
                XIGetClientPointer(display, 0, out int device);
                if (XIQueryPointer(display, device, root, out _, out _, out _, out _, out _, out _, out var buttons, out var mods, out _) != 0)
                {
                    _haveXi = true;
                    bool down = button >> 3 < buttons.mask_len && (buttons.mask[button >> 3] & (1 << (button & 7))) != 0;
                    if (buttons.mask != null) XFree((nint)buttons.mask);
                    if (mods.mask != null) XFree((nint)mods.mask);
                    return down;
                }
            }
            catch (DllNotFoundException) { _haveXi = false; }
        }
        XQueryPointer(display, root, out _, out _, out _, out _, out _, out _, out uint mask);
        return button is >= 1 and <= 5 && (mask & (1u << (7 + button))) != 0;
    }

    public static (int X, int Y) Pointer(nint display)
    {
        if (display == 0) return (0, 0);
        XQueryPointer(display, XDefaultRootWindow(display), out _, out _, out int x, out int y, out _, out _, out _);
        return (x, y);
    }

    /// <summary>
    /// Waits for the next click anywhere: a left click returns its screen position, a right click cancels. The pointer is
    /// grabbed meanwhile, so the click doesn't land on the window underneath. Blocks; run it off the UI thread.
    /// </summary>
    public static (int X, int Y)? PickPoint()
    {
        nint d = Open();
        if (d == 0) return null;
        try
        {
            nint root = XDefaultRootWindow(d);
            if (XGrabPointer(d, root, 0, (uint)ButtonPressMask, GrabModeAsync, GrabModeAsync, 0, 0, 0) != 0) return Pointer(d);
            var ev = new XEvent();
            while (true)
            {
                XNextEvent(d, &ev);
                if (ev.type != ButtonPress) continue;
                XUngrabPointer(d, 0);
                return ev.Detail == 1 ? (ev.Int(72), ev.Int(76)) : null;
            }
        }
        finally { XCloseDisplay(d); }
    }
}

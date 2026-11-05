using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Oversight.Linux.Services;
using Oversight.Models;

namespace Oversight.Linux.Platform;

/// <summary>
/// Global hotkeys through XGrabKey on the root window, the Linux side of the Windows HotkeyManager (same groups, same
/// rules: disabled groups are ungrabbed so their keys reach other apps). The grabs live on their own X connection and
/// thread; key presses are handed to the UI thread. Each binding is grabbed with every NumLock / CapsLock / ScrollLock
/// combination so those locks don't break it.
/// </summary>
public sealed unsafe class HotkeyManager : IDisposable
{
    private const int HK_TOGGLE_ZOOM = 1001, HK_ZOOM_IN = 1002, HK_ZOOM_OUT = 1003, HK_OPEN_SETTINGS = 1004,
                      HK_TOGGLE_CROSSHAIR = 1005, HK_AUTOCLICKER_STARTSTOP = 1007;

    private static readonly Dictionary<int, string> IdToName = new()
    {
        [HK_TOGGLE_ZOOM] = "ToggleZoom",
        [HK_ZOOM_IN] = "ZoomIn",
        [HK_ZOOM_OUT] = "ZoomOut",
        [HK_OPEN_SETTINGS] = "OpenSettings",
        [HK_TOGGLE_CROSSHAIR] = "ToggleCrosshair",
        [HK_AUTOCLICKER_STARTSTOP] = "AutoClickerStartStop",
    };

    // Action names as the Settings window shows them, for the conflict notification.
    private static readonly Dictionary<int, string> IdToLabel = new()
    {
        [HK_TOGGLE_ZOOM] = "Toggle zoom",
        [HK_ZOOM_IN] = "Zoom in",
        [HK_ZOOM_OUT] = "Zoom out",
        [HK_OPEN_SETTINGS] = "Open settings",
        [HK_TOGGLE_CROSSHAIR] = "Show/hide crosshair",
        [HK_AUTOCLICKER_STARTSTOP] = "Start/stop auto clicker",
    };

    private static readonly int[] ZoomIds = [HK_TOGGLE_ZOOM, HK_ZOOM_IN, HK_ZOOM_OUT];
    // "Hide while held" isn't here: CrosshairService polls it, so the game still gets the aim key.
    private static readonly int[] CrosshairIds = [HK_TOGGLE_CROSSHAIR];
    private static readonly int[] AutoClickerIds = [HK_AUTOCLICKER_STARTSTOP];
    private static readonly int[] AlwaysIds = [HK_OPEN_SETTINGS];

    private readonly AppSettings _settings;
    private readonly Magnifier _magnifier;
    private bool _started, _zoomActive, _crosshairActive, _autoClickerActive, _disposed;
    private int _suspendCount;
    private readonly HashSet<string> _reportedConflicts = [];

    // The X thread's state.
    private readonly ConcurrentQueue<List<Grab>> _pending = new();
    private Thread? _thread;
    private volatile bool _stop;

    private sealed record Grab(int Id, string Name, HotkeyBinding Binding, bool NoRepeat);

    public event Action<string>? HotkeyConflict;
    public event Action? OpenSettingsRequested;
    public event Action? ToggleCrosshairRequested;
    public event Action? ToggleAutoClickerRequested;
    public event Action<uint>? HoldAutoClickerRequested;

    public HotkeyManager(AppSettings settings, Magnifier magnifier)
    {
        _settings = settings;
        _magnifier = magnifier;
    }

    public void Start()
    {
        if (_started || !X11.Available) return;
        _started = true;
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "Oversight-Hotkeys" };
        _thread.Start();
        Apply();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _stop = true;
        _thread?.Join(1000);
        _thread = null;
    }

    // ── Group enable / disable (called by ProcessWatcher) ────────────────────

    public void SetZoomHotkeysEnabled(bool enabled)
    {
        if (enabled == _zoomActive) return;
        _zoomActive = enabled;
        if (_suspendCount > 0) return;
        Apply();
        if (!enabled) _magnifier.Deactivate();
    }

    public void SetCrosshairHotkeyEnabled(bool enabled)
    {
        if (enabled == _crosshairActive) return;
        _crosshairActive = enabled;
        if (_suspendCount == 0) Apply();
    }

    public void SetAutoClickerHotkeyEnabled(bool enabled)
    {
        if (enabled == _autoClickerActive) return;
        _autoClickerActive = enabled;
        if (_suspendCount == 0) Apply();
    }

    // ── Suspend / resume (while a capture box is recording a key) ─────────────

    public void Suspend()
    {
        if (_suspendCount++ == 0) Apply();
    }

    public void Resume()
    {
        if (--_suspendCount == 0) Apply();
    }

    public void ReloadBindings()
    {
        if (_suspendCount == 0) Apply();
    }

    /// <summary>Hands the wanted set of grabs to the X thread, which swaps them in.</summary>
    private void Apply()
    {
        if (!_started) return;
        var grabs = new List<Grab>();
        if (_suspendCount == 0)
        {
            var ids = AlwaysIds.AsEnumerable();
            if (_zoomActive) ids = ids.Concat(ZoomIds);
            if (_crosshairActive) ids = ids.Concat(CrosshairIds);
            if (_autoClickerActive) ids = ids.Concat(AutoClickerIds);
            foreach (int id in ids)
            {
                string name = IdToName[id];
                if (!_settings.Hotkeys.TryGetValue(name, out var b) || b.IsEmpty || HotkeyBinding.IsMouseButton(b.VirtualKey)) continue;
                grabs.Add(new Grab(id, name, new HotkeyBinding { Modifiers = b.Modifiers, VirtualKey = b.VirtualKey },
                    name is "ToggleZoom" or "OpenSettings" or "ToggleCrosshair" or "AutoClickerStartStop"));
            }
        }
        _pending.Enqueue(grabs);
    }

    // ── X thread ─────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { public int fd; public short events, revents; }

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(PollFd* fds, nuint count, int timeoutMs);

    private void Run()
    {
        nint d = X11.Open();
        if (d == 0) return;
        X11.WatchErrors(d);
        X11.XkbSetDetectableAutoRepeat(d, 1, out _);   // autorepeat sends presses only, so a held key is one press
        nint root = X11.XDefaultRootWindow(d);
        var active = new List<(Grab Grab, int Keycode, uint Mods)>();
        var down = new HashSet<uint>();
        uint ignore = LockMasks(d);
        var ev = new X11.XEvent();
        var fd = new PollFd { fd = X11.XConnectionNumber(d), events = 1 };
        try
        {
            while (!_stop)
            {
                List<Grab>? wanted = null;
                while (_pending.TryDequeue(out var next)) wanted = next;
                if (wanted is not null)
                {
                    foreach (var (_, keycode, mods) in active)
                        foreach (uint extra in Combinations(ignore)) X11.XUngrabKey(d, keycode, mods | extra, root);
                    active.Clear();
                    var results = new List<(Grab, bool)>();
                    foreach (var g in wanted)
                    {
                        int keycode = KeyMap.Keycode(d, g.Binding.VirtualKey);
                        if (keycode == 0) continue;   // no such key on this keyboard layout
                        uint mods = KeyMap.Modifiers(g.Binding.Modifiers);
                        X11.TakeError(d);
                        foreach (uint extra in Combinations(ignore))
                            X11.XGrabKey(d, keycode, mods | extra, root, 0, X11.GrabModeAsync, X11.GrabModeAsync);
                        X11.XSync(d, 0);
                        bool ok = X11.TakeError(d) != X11.BadAccess;
                        if (ok) active.Add((g, keycode, mods));
                        else foreach (uint extra in Combinations(ignore)) X11.XUngrabKey(d, keycode, mods | extra, root);
                        results.Add((g, ok));
                    }
                    X11.XSync(d, 0);
                    X11.TakeError(d);
                    Dispatcher.UIThread.Post(() => Report(results));
                }

                while (X11.XPending(d) > 0)
                {
                    X11.XNextEvent(d, &ev);
                    if (ev.type is not (X11.KeyPress or X11.KeyRelease)) continue;
                    uint keycode = ev.Detail;
                    if (ev.type == X11.KeyRelease) { down.Remove(keycode); continue; }
                    bool repeat = !down.Add(keycode);
                    uint state = ev.State & (X11.ShiftMask | X11.ControlMask | X11.Mod1Mask | X11.Mod4Mask);
                    foreach (var (g, kc, mods) in active)
                        if (kc == keycode && mods == state && !(repeat && g.NoRepeat))
                        {
                            int id = g.Id;
                            Dispatcher.UIThread.Post(() => OnHotkey(id));
                        }
                }

                poll(&fd, 1, 20);
            }
        }
        finally
        {
            foreach (var (_, keycode, mods) in active)
                foreach (uint extra in Combinations(ignore)) X11.XUngrabKey(d, keycode, mods | extra, root);
            X11.XCloseDisplay(d);
        }
    }

    /// <summary>The modifier masks of CapsLock, NumLock and ScrollLock (NumLock is usually Mod2, ScrollLock varies).</summary>
    private static uint LockMasks(nint d)
    {
        uint masks = X11.LockMask;
        int num = KeyMap.Keycode(d, 0x90), scroll = KeyMap.Keycode(d, 0x91);
        nint map = X11.XGetModifierMapping(d);
        if (map == 0) return masks;
        int perMod = *(int*)map;
        byte* codes = *(byte**)((byte*)map + 8);
        for (int mod = 0; mod < 8; mod++)
            for (int i = 0; i < perMod; i++)
            {
                byte code = codes[mod * perMod + i];
                if (code != 0 && (code == num || code == scroll)) masks |= 1u << mod;
            }
        X11.XFreeModifiermap(map);
        return masks;
    }

    /// <summary>Every subset of the lock masks, so a grab works whichever locks are on.</summary>
    private static IEnumerable<uint> Combinations(uint masks)
    {
        var bits = Enumerable.Range(0, 8).Select(i => 1u << i).Where(b => (masks & b) != 0).ToArray();
        for (int n = 0; n < 1 << bits.Length; n++)
            yield return bits.Where((_, i) => (n & (1 << i)) != 0).Aggregate(0u, (a, b) => a | b);
    }

    // ── UI thread ────────────────────────────────────────────────────────────

    private void Report(List<(Grab Grab, bool Ok)> results)
    {
        foreach (var (g, ok) in results)
        {
            // Groups re-grab on every focus change, so a conflict is announced once until that key grabs again.
            string conflictKey = $"{g.Name}|{g.Binding.Modifiers}|{g.Binding.VirtualKey}";
            if (ok) _reportedConflicts.Remove(conflictKey);
            else if (_reportedConflicts.Add(conflictKey))
                HotkeyConflict?.Invoke($"{IdToLabel[g.Id]}: {g.Binding.DisplayName} is already in use by another application.");
        }
    }

    private void OnHotkey(int id)
    {
        if (!_started || _suspendCount > 0) return;
        switch (id)
        {
            case HK_TOGGLE_ZOOM: OnToggleZoomHotkey(); break;
            case HK_ZOOM_IN: _magnifier.ZoomIn(); break;
            case HK_ZOOM_OUT: _magnifier.ZoomOut(); break;
            case HK_OPEN_SETTINGS: OpenSettingsRequested?.Invoke(); break;
            case HK_TOGGLE_CROSSHAIR: ToggleCrosshairRequested?.Invoke(); break;
            case HK_AUTOCLICKER_STARTSTOP: OnAutoClickerHotkey(); break;
        }
    }

    private void OnAutoClickerHotkey()
    {
        // Hold mode: run only while the key stays down; the service polls it for release.
        if (_settings.AutoClicker.Activation == ActivationMode.Hold &&
            _settings.Hotkeys.TryGetValue("AutoClickerStartStop", out var b) && !b.IsEmpty)
            HoldAutoClickerRequested?.Invoke(b.VirtualKey);
        else
            ToggleAutoClickerRequested?.Invoke();
    }

    private void OnToggleZoomHotkey()
    {
        if (_settings.Zoom.ToggleZoomMode == ActivationMode.Hold &&
            _settings.Hotkeys.TryGetValue("ToggleZoom", out var b) && !b.IsEmpty)
            _magnifier.BeginHold(b.VirtualKey);
        else
            _magnifier.Toggle();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

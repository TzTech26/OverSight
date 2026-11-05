using System.Windows;
using System.Windows.Interop;
using Oversight.Models;
using Oversight.Native;

namespace Oversight.Services;

/// <summary>
/// Registers/unregisters global hotkeys via RegisterHotKey on a message-only window.
/// The binds fall into three groups that enable independently:
///
/// <list type="bullet">
///   <item><b>Zoom</b> (toggle / in / out) — active only where the Game Profiles rules give
///     the foreground app a zoom slot; bindings come from that slot.</item>
///   <item><b>Crosshair</b> (show/hide) — active only where the rules give
///     the app a crosshair slot; bindings come from that slot.</item>
///   <item><b>Open settings</b> — app-wide, registered whenever the manager runs, so the
///     settings window can always be reached.</item>
/// </list>
///
/// Disabled groups are truly unregistered so their keys pass through to other apps.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    // ── Hotkey ID map ─────────────────────────────────────────────────────────
    private const int HK_TOGGLE_ZOOM        = 1001;
    private const int HK_ZOOM_IN            = 1002;
    private const int HK_ZOOM_OUT           = 1003;
    private const int HK_OPEN_SETTINGS      = 1004;
    private const int HK_TOGGLE_CROSSHAIR   = 1005;
    private const int HK_AUTOCLICKER_STARTSTOP = 1007;

    private static readonly Dictionary<int, string> IdToName = new()
    {
        [HK_TOGGLE_ZOOM]            = "ToggleZoom",
        [HK_ZOOM_IN]                = "ZoomIn",
        [HK_ZOOM_OUT]               = "ZoomOut",
        [HK_OPEN_SETTINGS]          = "OpenSettings",
        [HK_TOGGLE_CROSSHAIR]       = "ToggleCrosshair",
        [HK_AUTOCLICKER_STARTSTOP]  = "AutoClickerStartStop",
    };

    // Action names as the Settings window shows them, for the conflict balloon.
    private static readonly Dictionary<int, string> IdToLabel = new()
    {
        [HK_TOGGLE_ZOOM]            = "Toggle zoom",
        [HK_ZOOM_IN]                = "Zoom in",
        [HK_ZOOM_OUT]               = "Zoom out",
        [HK_OPEN_SETTINGS]          = "Open settings",
        [HK_TOGGLE_CROSSHAIR]       = "Show/hide crosshair",
        [HK_AUTOCLICKER_STARTSTOP]  = "Start/stop auto clicker",
    };

    private static readonly int[] ZoomIds        = [HK_TOGGLE_ZOOM, HK_ZOOM_IN, HK_ZOOM_OUT];
    // "Hide while held" isn't here: CrosshairService polls it, so the game still gets the aim key.
    private static readonly int[] CrosshairIds   = [HK_TOGGLE_CROSSHAIR];
    private static readonly int[] AutoClickerIds = [HK_AUTOCLICKER_STARTSTOP];
    private static readonly int[] AlwaysIds      = [HK_OPEN_SETTINGS];

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly AppSettings      _settings;
    private readonly MagnifierService _magnifier;
    private HwndSource?               _hwndSource;
    private bool                      _zoomActive;
    private bool                      _crosshairActive;
    private bool                      _autoClickerActive;
    private int                       _suspendCount;
    private bool                      _disposed;

    public event Action<string>? HotkeyConflict;
    private readonly HashSet<string> _reportedConflicts = [];
    public event Action?         OpenSettingsRequested;
    public event Action?         ToggleCrosshairRequested;

    /// <summary>Fired when the auto-clicker start/stop key is pressed in Toggle activation mode.</summary>
    public event Action? ToggleAutoClickerRequested;

    /// <summary>
    /// Fired on key-down of the auto-clicker start/stop key in Hold activation mode, with its
    /// virtual key code so the service can poll for release (RegisterHotKey only signals key-down).
    /// </summary>
    public event Action<uint>? HoldAutoClickerRequested;

    public HotkeyManager(AppSettings settings, MagnifierService magnifier)
    {
        _settings  = settings;
        _magnifier = magnifier;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        var p = new HwndSourceParameters("Oversight_HotkeyHost")
        {
            Width = 0, Height = 0,
            WindowStyle  = 0,
            PositionX    = 0,
            PositionY    = 0,
            ParentWindow = new IntPtr(-3),  // HWND_MESSAGE
        };
        _hwndSource = new HwndSource(p);
        _hwndSource.AddHook(WndProc);

        // Open-settings is always live; the process watcher enables the zoom / crosshair
        // groups right after, once it has evaluated the current foreground window.
        Register(AlwaysIds);
    }

    public void Stop()
    {
        UnregisterAll();
        _hwndSource?.Dispose();
        _hwndSource = null;
    }

    // ── Group enable / disable (called by ProcessWatcher) ────────────────────

    public void SetZoomHotkeysEnabled(bool enabled)
    {
        if (enabled == _zoomActive) return;
        _zoomActive = enabled;

        if (_suspendCount > 0) return;   // applied on Resume

        if (enabled)
        {
            Register(ZoomIds);
        }
        else
        {
            Unregister(ZoomIds);
            _magnifier.Deactivate();
        }
    }

    public void SetCrosshairHotkeyEnabled(bool enabled)
    {
        if (enabled == _crosshairActive) return;
        _crosshairActive = enabled;

        if (_suspendCount > 0) return;

        if (enabled) Register(CrosshairIds);
        else         Unregister(CrosshairIds);
    }

    public void SetAutoClickerHotkeyEnabled(bool enabled)
    {
        if (enabled == _autoClickerActive) return;
        _autoClickerActive = enabled;

        if (_suspendCount > 0) return;

        if (enabled) Register(AutoClickerIds);
        else         Unregister(AutoClickerIds);
    }

    // ── Suspend / resume (while a capture box is recording a key) ─────────────

    public void Suspend()
    {
        if (_suspendCount++ == 0)
            UnregisterAll();
    }

    public void Resume()
    {
        if (--_suspendCount == 0)
            RegisterActive();
    }

    // ── Reload after settings change ─────────────────────────────────────────

    public void ReloadBindings()
    {
        if (_suspendCount > 0) return;
        UnregisterAll();
        RegisterActive();
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private void RegisterActive()
    {
        Register(AlwaysIds);
        if (_zoomActive)        Register(ZoomIds);
        if (_crosshairActive)   Register(CrosshairIds);
        if (_autoClickerActive) Register(AutoClickerIds);
    }

    private void Register(int[] ids)
    {
        if (_hwndSource is null) return;
        var hwnd = _hwndSource.Handle;

        foreach (var id in ids)
        {
            var name = IdToName[id];
            if (!_settings.Hotkeys.TryGetValue(name, out var b) || b.IsEmpty) continue;

            // Mouse-button binds can't go through RegisterHotKey.
            if (HotkeyBinding.IsMouseButton(b.VirtualKey)) continue;

            uint mods = b.Modifiers;
            if (name is "ToggleZoom" or "OpenSettings" or "ToggleCrosshair" or "AutoClickerStartStop")
                mods |= NativeMethods.MOD_NOREPEAT;

            // Groups re-register on every focus change, so a conflict is announced once until that key registers again.
            string conflictKey = $"{name}|{b.Modifiers}|{b.VirtualKey}";
            if (NativeMethods.RegisterHotKey(hwnd, id, mods, b.VirtualKey))
                _reportedConflicts.Remove(conflictKey);
            else if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 1409   // ERROR_HOTKEY_ALREADY_REGISTERED
                     && _reportedConflicts.Add(conflictKey))
                HotkeyConflict?.Invoke($"{IdToLabel[id]}: {b.DisplayName} is already in use by another application.");
        }
    }

    private void Unregister(int[] ids)
    {
        if (_hwndSource is null) return;
        var hwnd = _hwndSource.Handle;
        foreach (var id in ids)
            NativeMethods.UnregisterHotKey(hwnd, id);
    }

    private void UnregisterAll()
    {
        if (_hwndSource is null) return;
        var hwnd = _hwndSource.Handle;
        foreach (var id in IdToName.Keys)
            NativeMethods.UnregisterHotKey(hwnd, id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)NativeMethods.WM_HOTKEY)
        {
            OnHotkey(wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HK_TOGGLE_ZOOM:         OnToggleZoomHotkey();                break;
            case HK_ZOOM_IN:             _magnifier.ZoomIn();                 break;
            case HK_ZOOM_OUT:            _magnifier.ZoomOut();                break;
            case HK_OPEN_SETTINGS:       OpenSettingsRequested?.Invoke();     break;
            case HK_TOGGLE_CROSSHAIR:    ToggleCrosshairRequested?.Invoke();  break;
            case HK_AUTOCLICKER_STARTSTOP: OnAutoClickerHotkey();             break;
        }
    }

    private void OnAutoClickerHotkey()
    {
        // Hold mode: run only while the key stays down. RegisterHotKey signals key-down only, so
        // hand the key to the service, which polls it and stops on release. Toggle mode flips the
        // run state on each press.
        if (_settings.AutoClicker.Activation == Models.ActivationMode.Hold &&
            _settings.Hotkeys.TryGetValue("AutoClickerStartStop", out var b) && !b.IsEmpty)
        {
            HoldAutoClickerRequested?.Invoke(b.VirtualKey);
        }
        else
        {
            ToggleAutoClickerRequested?.Invoke();
        }
    }

    private void OnToggleZoomHotkey()
    {
        // Hold mode: keep zoom up only while the key stays down. RegisterHotKey only fires on
        // key-down, so hand the key to the magnifier, which polls it and turns zoom off on
        // release. Falls back to a plain toggle if the binding is somehow empty.
        if (_settings.Zoom.ToggleZoomMode == Models.ActivationMode.Hold &&
            _settings.Hotkeys.TryGetValue("ToggleZoom", out var b) && !b.IsEmpty)
        {
            _magnifier.BeginHold(b.VirtualKey);
        }
        else
        {
            _magnifier.Toggle();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

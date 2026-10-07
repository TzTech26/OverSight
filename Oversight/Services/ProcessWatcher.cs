using System.Diagnostics;
using Oversight.Native;

namespace Oversight.Services;

/// <summary>
/// Watches the foreground window (EVENT_SYSTEM_FOREGROUND) and applies the Game Profiles
/// rules in <see cref="ConfigStore"/>: the zoom slot and crosshair slot resolve independently
/// per app, so a game can have zoom without a crosshair, a crosshair without zoom, both, or
/// neither. Whatever resolves to "off" has its hotkeys unregistered / overlay hidden.
///
/// Auto-switching is suspended while the settings window is open so the user can preview and
/// edit slots without the foreground (the settings window itself) yanking them away.
/// </summary>
public sealed class ProcessWatcher : IDisposable
{
    private readonly ConfigStore        _config;
    private readonly HotkeyManager      _hotkeys;
    private readonly CrosshairService   _crosshair;
    private readonly AutoClickerService _autoClicker;
    private readonly string             _ownProcess = Process.GetCurrentProcess().ProcessName;

    // Keep the delegate alive to prevent GC collection while the hook is active
    private NativeMethods.WinEventDelegate? _delegate;
    private IntPtr _hook;
    private bool   _autoSwitchSuspended;
    private bool   _disposed;

    public ProcessWatcher(ConfigStore config, HotkeyManager hotkeys,
                          CrosshairService crosshair, AutoClickerService autoClicker)
    {
        _config      = config;
        _hotkeys     = hotkeys;
        _crosshair   = crosshair;
        _autoClicker = autoClicker;
    }

    public void Start()
    {
        _delegate = OnWinEvent;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _delegate, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        // Evaluate the current foreground window immediately
        CheckForeground();
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        _delegate = null;
    }

    /// <summary>Re-evaluates the foreground window (e.g. after the rules change).</summary>
    public void Reload() => CheckForeground();

    /// <summary>
    /// Pauses foreground-driven switching (while the settings window is open, and not
    /// minimized) and keeps the hotkeys live so the user can test zoom against the slot
    /// they're editing. The crosshair overlay is deliberately NOT forced on here — it keeps
    /// obeying the Game Profiles rules, and the settings window pushes an explicit live
    /// preview only while its Crosshair tab is being edited.
    /// </summary>
    public void SuspendAutoSwitch()
    {
        _autoSwitchSuspended = true;
        _hotkeys.SetZoomHotkeysEnabled(true);
        _hotkeys.SetCrosshairHotkeyEnabled(true);
        _hotkeys.SetAutoClickerHotkeyEnabled(true);
        _hotkeys.ReloadBindings();
    }

    /// <summary>Resumes foreground-driven switching and re-evaluates the current window.</summary>
    public void ResumeAutoSwitch()
    {
        _autoSwitchSuspended = false;
        CheckForeground();
    }

    // ── WinEvent callback ─────────────────────────────────────────────────────

    private void OnWinEvent(IntPtr hHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        CheckForeground();
    }

    private void CheckForeground()
    {
        if (_autoSwitchSuspended) return;

        var    hwnd = NativeMethods.GetForegroundWindow();
        string? proc = GetProcessName(hwnd);

        // Our own window in the foreground while auto-switch is live (e.g. the instant the
        // settings window is closing) means the user is not in any listed app — treat it as
        // an unlisted process so the defaults apply. Returning here instead used to leave the
        // previous enable-state stuck on.
        if (proc is not null && string.Equals(proc, _ownProcess, StringComparison.OrdinalIgnoreCase))
            proc = null;

        var (zoomSlot, xhSlot, acSlot) = _config.ResolveForProcess(proc);

        if (zoomSlot is not null) _config.ActivateZoomRuntime(zoomSlot);
        if (xhSlot   is not null) _config.ActivateCrosshairRuntime(xhSlot);
        if (acSlot   is not null) _config.ActivateAutoClickerRuntime(acSlot);

        _hotkeys.SetZoomHotkeysEnabled(zoomSlot is not null);
        _hotkeys.SetCrosshairHotkeyEnabled(xhSlot is not null);
        _hotkeys.SetAutoClickerHotkeyEnabled(acSlot is not null);

        // Slot switches change the live bindings, so re-register whatever is enabled.
        _hotkeys.ReloadBindings();

        // Also re-centres the overlay onto the monitor of the new foreground window.
        _crosshair.SetActivationAllowed(xhSlot is not null);

        // Leaving an app that had an auto-clicker slot halts any run in progress.
        _autoClicker.SetActivationAllowed(acSlot is not null);
    }

    private static string? GetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

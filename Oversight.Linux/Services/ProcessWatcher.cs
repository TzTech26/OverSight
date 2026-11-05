using Avalonia.Threading;
using Oversight.Linux.Platform;
using Oversight.Services;

namespace Oversight.Linux.Services;

/// <summary>
/// Applies the Game Profiles rules to the focused app, like the Windows ProcessWatcher: zoom, crosshair and auto-clicker
/// slots resolve independently, and whatever resolves to off has its hotkeys ungrabbed and its overlay hidden. The
/// focused window is read four times a second. Switching pauses while Settings is open.
/// </summary>
public sealed class ProcessWatcher : IDisposable
{
    private readonly ConfigStore _config;
    private readonly HotkeyManager _hotkeys;
    private readonly CrosshairService _crosshair;
    private readonly AutoClickerService _autoClicker;
    private readonly DispatcherTimer _timer;
    private (int Pid, string? Name)? _last;
    private bool _autoSwitchSuspended;

    public ProcessWatcher(ConfigStore config, HotkeyManager hotkeys, CrosshairService crosshair, AutoClickerService autoClicker)
    {
        _config = config;
        _hotkeys = hotkeys;
        _crosshair = crosshair;
        _autoClicker = autoClicker;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) =>
        {
            var now = X11.FocusedProcess();
            if (now != _last) CheckForeground(now);
        };
    }

    public void Start()
    {
        CheckForeground(X11.FocusedProcess());
        _timer.Start();
    }

    public void Reload() => CheckForeground(X11.FocusedProcess());

    public void SuspendAutoSwitch()
    {
        _autoSwitchSuspended = true;
        _hotkeys.SetZoomHotkeysEnabled(Magnifier.Supported);
        _hotkeys.SetCrosshairHotkeyEnabled(true);
        _hotkeys.SetAutoClickerHotkeyEnabled(true);
        _hotkeys.ReloadBindings();
    }

    public void ResumeAutoSwitch()
    {
        _autoSwitchSuspended = false;
        CheckForeground(X11.FocusedProcess());
    }

    private void CheckForeground((int Pid, string? Name) focused)
    {
        _last = focused;
        if (_autoSwitchSuspended) return;

        // Oversight's own windows count as an unlisted app, so the defaults apply.
        string? proc = focused.Pid == Environment.ProcessId ? null : focused.Name;
        var (zoomSlot, xhSlot, acSlot) = _config.ResolveForProcess(proc);

        if (zoomSlot is not null) _config.ActivateZoomRuntime(zoomSlot);
        if (xhSlot is not null) _config.ActivateCrosshairRuntime(xhSlot);
        if (acSlot is not null) _config.ActivateAutoClickerRuntime(acSlot);

        _hotkeys.SetZoomHotkeysEnabled(zoomSlot is not null && Magnifier.Supported);
        _hotkeys.SetCrosshairHotkeyEnabled(xhSlot is not null);
        _hotkeys.SetAutoClickerHotkeyEnabled(acSlot is not null);
        _hotkeys.ReloadBindings();

        _crosshair.SetActivationAllowed(xhSlot is not null);
        _autoClicker.SetActivationAllowed(acSlot is not null);
    }

    public void Dispose() => _timer.Stop();
}

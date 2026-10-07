using System.Windows.Threading;
using Oversight.Models;
using Oversight.Native;

namespace Oversight.Services;

/// <summary>
/// Owns the crosshair overlay and decides when it is on screen. Visibility is the AND of:
///
/// <list type="bullet">
///   <item><b>Rules</b> — the Game Profiles rules resolve a crosshair slot (or off) for the
///     foreground app; the process watcher relays that verdict here and switches the live
///     style to that slot.</item>
///   <item><b>Hotkey hide</b> — the "Show/hide crosshair" hotkey flips a session-local flag
///     without touching any saved slot.</item>
///   <item><b>Hold-to-hide</b> — the "Hide while held" key (e.g. aiming down sights) hides the
///     crosshair only while physically down, polled via <see cref="NativeMethods.GetAsyncKeyState"/>
///     since RegisterHotKey only signals key-down. If the crosshair is already hidden by the
///     toggle above, holding and releasing this key leaves it hidden — both flags must be clear
///     to show.</item>
/// </list>
///
/// While the settings window is editing a crosshair slot it can push a <b>preview style</b>,
/// which overrides all of the above so the edit is visible live on screen.
///
/// The crosshair sits at the centre of the monitor hosting the foreground window (games), and
/// re-renders whenever the live slot changes.
/// </summary>
public sealed class CrosshairService : IDisposable
{
    private readonly ConfigStore      _config;
    private readonly CrosshairOverlay _overlay = new();
    private readonly Dispatcher       _dispatcher;
    private readonly DispatcherTimer  _holdPollTimer;

    private bool _activationAllowed;        // the watcher's Game Profiles verdict
    private bool _userHidden;               // "Show/hide crosshair" hotkey (session only)
    private bool _holdHidden;               // "Hide while held" key is currently down
    private uint _holdHideKey;              // virtual key being polled
    private bool _watchingMouse;            // continuous watch: bind is a mouse button (no WM_HOTKEY for those)
    private CrosshairSettings? _preview;    // settings-window live preview override
    private bool _disposed;

    public CrosshairService(ConfigStore config)
    {
        _config     = config;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Live slot switched (tray, settings save, or per-app auto-switch) → restyle.
        _config.ProfilesChanged += Refresh;

        // Monitor layout changes move the centre point.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Polls the hold-hide key's live state. Two uses: a keyboard bind polls for release
        // after its WM_HOTKEY key-down (RegisterHotKey never signals key-up), and a mouse
        // bind — which RegisterHotKey can't handle at all — is watched continuously for both
        // press and release while the rules have a crosshair active here.
        _holdPollTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(15) };
        _holdPollTimer.Tick += OnHoldPollTick;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(Refresh);

    /// <summary>Called by the process watcher with the Game Profiles verdict for the foreground app.</summary>
    public void SetActivationAllowed(bool allowed)
    {
        _activationAllowed = allowed;
        if (!allowed)
        {
            // Nothing to hide-while-held for; drop any stale hold state so a leftover flag
            // can't strand the crosshair hidden the next time this app's rules re-enable it.
            _holdHidden    = false;
            _watchingMouse = false;
            _holdPollTimer.Stop();
        }
        Refresh();
    }

    /// <summary>
    /// Starts/stops the continuous watch used by mouse-button hold-hide binds (right mouse,
    /// Mouse 4/5…). RegisterHotKey can't see mouse buttons, so while the rules have a
    /// crosshair active and the live slot's bind is a mouse button, the poll timer tracks
    /// the button state for both press and release. Called on every <see cref="Refresh"/>,
    /// so slot switches re-evaluate it automatically.
    /// </summary>
    private void UpdateHoldWatch()
    {
        bool wantMouseWatch =
            _activationAllowed &&
            _config.Settings.Hotkeys.TryGetValue("HoldHideCrosshair", out var b) &&
            !b.IsEmpty && HotkeyBinding.IsMouseButton(b.VirtualKey);

        if (wantMouseWatch)
        {
            _holdHideKey   = _config.Settings.Hotkeys["HoldHideCrosshair"].VirtualKey;
            _watchingMouse = true;
            _holdPollTimer.Start();
        }
        else
        {
            _watchingMouse = false;
            if (!_holdHidden) _holdPollTimer.Stop();   // keyboard path stops itself on release
        }
    }

    /// <summary>Hotkey handler: hides/shows the crosshair without changing any saved slot.</summary>
    public void ToggleHidden()
    {
        _userHidden = !_userHidden;
        Refresh();
    }

    /// <summary>
    /// "Hide while held" key-down handler: hides the crosshair immediately and starts polling
    /// <paramref name="virtualKey"/> for release, at which point it's shown again (unless
    /// <see cref="ToggleHidden"/> already hid it — see <see cref="Refresh"/>).
    /// </summary>
    public void BeginHoldHide(uint virtualKey)
    {
        if (virtualKey == 0) return;
        _holdHideKey = virtualKey;
        _holdHidden  = true;
        Refresh();
        _holdPollTimer.Start();
    }

    private void OnHoldPollTick(object? sender, EventArgs e)
    {
        bool down = (NativeMethods.GetAsyncKeyState((int)_holdHideKey) & 0x8000) != 0;

        if (down != _holdHidden)
        {
            _holdHidden = down;
            Refresh();
        }

        // A keyboard bind only needs the timer between key-down and release; a mouse bind
        // is watched continuously (the press itself is only visible to this poll).
        if (!down && !_watchingMouse)
            _holdPollTimer.Stop();
    }

    /// <summary>
    /// Shows <paramref name="style"/> on screen regardless of the rules (settings-window live
    /// preview). Pass null to end the preview and return to rule-driven visibility.
    /// </summary>
    public void SetPreview(CrosshairSettings? style)
    {
        _preview = style?.Clone();
        Refresh();
    }

    /// <summary>Re-evaluates visibility and repaints/repositions the overlay.</summary>
    public void Refresh()
    {
        if (_disposed) return;

        UpdateHoldWatch();

        try
        {
            var style = _preview ?? _config.Settings.Crosshair;
            bool show = _preview is not null || (_activationAllowed && !_userHidden && !_holdHidden);
            if (!show)
            {
                _overlay.Hide();
                return;
            }

            var (cx, cy) = TargetCenter();
            using var bmp = CrosshairRenderer.Render(style);
            _overlay.ShowAt(bmp, cx - bmp.Width / 2, cy - bmp.Height / 2);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Crosshair] refresh failed: {ex}");
            _overlay.Hide();
        }
    }

    /// <summary>
    /// Centre of the monitor hosting the foreground window (physical pixels), falling back
    /// to the primary monitor. Games render on whichever monitor they occupy, so that is
    /// where the crosshair belongs.
    /// </summary>
    private static (int X, int Y) TargetCenter()
    {
        var hwnd   = NativeMethods.GetForegroundWindow();
        var screen = hwnd != IntPtr.Zero
            ? System.Windows.Forms.Screen.FromHandle(hwnd)
            : System.Windows.Forms.Screen.PrimaryScreen;

        var b = (screen ?? System.Windows.Forms.Screen.PrimaryScreen!).Bounds;
        return (b.Left + b.Width / 2, b.Top + b.Height / 2);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _config.ProfilesChanged -= Refresh;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _holdPollTimer.Stop();
        _holdPollTimer.Tick -= OnHoldPollTick;
        _overlay.Dispose();
    }
}

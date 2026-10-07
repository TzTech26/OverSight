using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Oversight.Models;
using Oversight.Native;

namespace Oversight.Services;

/// <summary>
/// Controls the magnifier. Supports two engines, chosen by <see cref="AppSettings.Engine"/>:
///
/// • <b>Fullscreen</b> — the Windows full-screen Magnification transform
///   (<see cref="NativeMethods.MagSetFullscreenTransform"/>). The OS scales the desktop with
///   true sub-pixel precision, so the zoom ramps animate smoothly, but it magnifies all
///   monitors at once and cannot be confined to one.
/// • <b>Windowed</b> — a per-monitor lens (<see cref="WindowMagnifier"/>) confined to the
///   monitor chosen by <see cref="AppSettings.MonitorMode"/>; only captures whole-pixel
///   rectangles, so the animation ramp judders (steady once settled).
///
/// The fullscreen engine engages a desktop-wide transform, so teardown must always reset it
/// to identity — see <see cref="EmergencyReset"/> and the process-exit / unhandled-exception
/// nets in App.
///
/// Updates at ~60 fps via a DispatcherTimer.
/// Scroll wheel adjusts the zoom level while active (via a WH_MOUSE_LL hook).
/// </summary>
public sealed class MagnifierService : IDisposable
{
    private readonly AppSettings     _settings;
    private readonly DispatcherTimer _timer;
    private readonly WindowMagnifier _lens = new();

    // Current runtime state
    private bool  _isActive;
    private float _zoom;

    // Hold-to-zoom: when active, the lens stays up only while _holdKey is held down. The
    // render timer polls the key and turns zoom off (playing the zoom-out ramp) on release,
    // since RegisterHotKey only signals key-down. Cleared for ordinary toggle activation.
    private bool _holdMode;
    private uint _holdKey;

    // Low-level mouse hook for scroll-to-zoom
    private NativeMethods.LowLevelMouseProc? _mouseHookProc; // must stay referenced
    private IntPtr                            _mouseHook;

    // Zoom ramp animation state (used for both zoom-in and zoom-out)
    private readonly System.Diagnostics.Stopwatch _animClock = new();
    private bool  _animating;
    private float _animFrom, _animTo;
    private int   _animDurationMs;
    private bool  _animZoomingIn;          // direction, for choosing the easing curve
    private bool  _animEndsDeactivate;     // when a zoom-out ramp finishes, hide the lens

    // True while a zoom-out ramp is playing: still visibly active, but on its way off.
    private bool _deactivating;

    // Smoothed lens anchor (physical pixels) for eased cursor following (fullscreen engine).
    private readonly System.Diagnostics.Stopwatch _frameClock = new();
    private double _anchorX, _anchorY;
    private bool   _anchorValid;

    // Windowed engine cross-screen fade: when the cursor changes monitors the lens fades out
    // on the old screen and fades back in on the new one, so neither screen is ever shown
    // half-magnified (which a sliding lens did). Phase 0 = steady, 1 = fading out, 2 = in.
    private (int L, int T, int W, int H) _winMon;
    private bool _winMonValid;
    private int  _fadePhase;
    private (int L, int T, int W, int H) _fadeTarget;
    private readonly System.Diagnostics.Stopwatch _fadeClock = new();

    // Last fullscreen transform applied, to skip redundant identical calls while idle.
    private (float M, int OX, int OY) _lastFs = (-1f, 0, 0);

    // Which engine drew the previous frame, so we can clean up the other one on a switch.
    private MagnifierEngine? _lastEngine;

    private bool _initialized;
    private bool _disposed;

    public bool  IsActive => _isActive;
    public float ZoomLevel => _zoom;

    public event Action? StateChanged;

    public MagnifierService(AppSettings settings)
    {
        _settings = settings;
        _zoom     = settings.Zoom.DefaultZoomLevel;

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)   // target 60 fps
        };
        _timer.Tick += OnTick;

        // A display change (resolution, monitor connect/disconnect, Win+P) while
        // active invalidates the magnifier's coordinate space and can otherwise
        // crash with the transform still engaged — drop the zoom safely instead.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!_isActive) return;
        _timer.Dispatcher.BeginInvoke(() => { if (_isActive) Deactivate(animate: false); });
    }

    /// <summary>
    /// Last-resort safety net: clear any fullscreen magnification an older build may
    /// have left engaged. The windowed lens needs no reset — the OS destroys its host
    /// window on process exit — but this stays harmless and protects against upgrades.
    /// Safe to call from any context, including exception handlers.
    /// </summary>
    public static void EmergencyReset()
    {
        try { NativeMethods.MagSetFullscreenTransform(1.0f, 0, 0); }
        catch { /* nothing more we can do during teardown */ }
    }

    // ── Initialise / dispose ─────────────────────────────────────────────────

    public bool Initialize()
    {
        if (_initialized) return true;
        _initialized = NativeMethods.MagInitialize();
        if (!_initialized)
            System.Diagnostics.Debug.WriteLine("[Magnifier] MagInitialize failed.");
        return _initialized;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Deactivate(animate: false);
        _lens.Dispose();
        if (_initialized) NativeMethods.MagUninitialize();
    }

    // ── Public controls ───────────────────────────────────────────────────────

    public void Toggle()
    {
        // A toggle-style press ends any hold session so a later held key can't strand the lens.
        _holdMode = false;

        // While a zoom-out ramp is playing the lens is technically still active; a toggle
        // there means "turn it back on", so treat the deactivating state as off.
        if (_isActive && !_deactivating) Deactivate();
        else                             Activate();
    }

    /// <summary>
    /// Hold-to-zoom activation: turns the lens on and watches <paramref name="virtualKey"/>.
    /// The render tick turns zoom off (playing the zoom-out ramp) as soon as the key is
    /// released. Re-pressing while the lens is ramping off keeps it up (see <see cref="Activate"/>).
    /// </summary>
    public void BeginHold(uint virtualKey)
    {
        _holdKey  = virtualKey;
        _holdMode = true;
        Activate();
    }

    public void Activate()
    {
        if (!_initialized) return;
        if (_isActive && !_deactivating) return;   // already fully on

        float target = _settings.Zoom.DefaultZoomLevel;

        // Interrupting a zoom-out ramp: keep the lens up and ramp back to the target zoom.
        if (_deactivating)
        {
            _deactivating = false;
            StartZoomAnim(_zoom, target, AnimDuration(_settings.Zoom.ZoomInAnimationEnabled, _settings.Zoom.ZoomAnimationMs),
                          zoomingIn: true, endsDeactivate: false);
            StateChanged?.Invoke();
            return;
        }

        // Fresh activation. Optional zoom-in ramp: animate from 1× up to the target level.
        StartZoomAnim(1.0f, target, AnimDuration(_settings.Zoom.ZoomInAnimationEnabled, _settings.Zoom.ZoomAnimationMs),
                      zoomingIn: true, endsDeactivate: false);

        _isActive    = true;
        _anchorValid = false;          // re-seed the smoothed anchor on the next tick
        _winMonValid = false;          // re-seed the windowed lens monitor / fade state
        _fadePhase   = 0;
        _frameClock.Restart();

        _timer.Start();
        InstallMouseHook();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Turn the lens off. By default this plays the optional zoom-out ramp first and hides
    /// the lens once it settles; pass <paramref name="animate"/> = false to drop it instantly
    /// (used on app shutdown / display changes where lingering frames are undesirable).
    /// </summary>
    public void Deactivate(bool animate = true)
    {
        if (!_isActive) return;

        int outMs = AnimDuration(_settings.Zoom.ZoomOutAnimationEnabled, _settings.Zoom.ZoomOutAnimationMs);
        if (animate && !_deactivating && outMs > 0 && _zoom > 1.0f)
        {
            // Ramp back down to 1×; FinishDeactivate runs when the animation settles.
            _deactivating = true;
            StartZoomAnim(_zoom, 1.0f, outMs, zoomingIn: false, endsDeactivate: true);
            StateChanged?.Invoke();
            return;
        }

        FinishDeactivate();
    }

    private void FinishDeactivate()
    {
        _isActive     = false;
        _deactivating = false;
        _holdMode     = false;
        _animating    = false;
        _animClock.Stop();
        _timer.Stop();
        RemoveMouseHook();

        // Tear down both engines regardless of which was in use: hide the windowed lens and
        // always return the desktop to 1× — leaving a fullscreen transform engaged would
        // strand the whole screen zoomed.
        _lens.Hide();
        NativeMethods.MagSetFullscreenTransform(1.0f, 0, 0);
        _lastFs     = (-1f, 0, 0);
        _lastEngine = null;

        StateChanged?.Invoke();
    }

    private static int AnimDuration(bool enabled, int ms) =>
        enabled ? Math.Clamp(ms, 0, 500) : 0;

    private void StartZoomAnim(float from, float to, int durationMs, bool zoomingIn, bool endsDeactivate)
    {
        _animDurationMs     = durationMs;
        _animZoomingIn      = zoomingIn;
        _animEndsDeactivate = endsDeactivate;
        if (durationMs > 0)
        {
            _animFrom  = from;
            _animTo    = to;
            _zoom      = from;
            _animating = true;
            _animClock.Restart();
        }
        else
        {
            _zoom      = to;
            _animating = false;
        }
    }

    public void ZoomIn()
    {
        AdjustZoom(+_settings.Zoom.ZoomStep);
    }

    public void ZoomOut()
    {
        AdjustZoom(-_settings.Zoom.ZoomStep);
    }

    // ── Timer tick (~60 fps) ─────────────────────────────────────────────────

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_isActive) return;

        // Hold-to-zoom: once the trigger key is released, ramp the lens off. Guard with
        // !_deactivating so a zoom-out ramp already in flight is left to finish, and clear
        // _holdMode so the release only fires once.
        if (_holdMode && !_deactivating &&
            (NativeMethods.GetAsyncKeyState((int)_holdKey) & 0x8000) == 0)
        {
            _holdMode = false;
            Deactivate();
            if (!_isActive) return;   // instant (non-animated) off tore the lens down already
        }

        try
        {
            // Real elapsed time since the last frame keeps the smoothing frame-rate independent.
            double dtMs = _frameClock.Elapsed.TotalMilliseconds;
            _frameClock.Restart();

            if (_animating) AdvanceAnimation();

            // A zoom-out ramp can finish inside AdvanceAnimation and tear the lens down
            // (hide it, stop the timer). If so, stop now — otherwise we'd immediately
            // re-show the lens at 1× as a frozen full-screen mirror that never updates
            // again, leaving the screen "stuck" until the app restarts.
            if (!_isActive) return;

            NativeMethods.GetCursorPos(out var cur);

            // If the engine was changed (via settings) mid-session, clear whichever one we
            // are leaving so it can't stay engaged underneath the other.
            if (_lastEngine != _settings.Zoom.Engine)
            {
                _lens.Hide();
                NativeMethods.MagSetFullscreenTransform(1.0f, 0, 0);
                _lastFs      = (-1f, 0, 0);
                _winMonValid = false;
                _fadePhase   = 0;
                _lastEngine  = _settings.Zoom.Engine;
            }

            if (_settings.Zoom.Engine == MagnifierEngine.Fullscreen)
            {
                // Fullscreen follows the cursor; smoothing eases that follow.
                var (ax, ay) = ResolveAnchor(cur.X, cur.Y, dtMs);
                UpdateFullscreen(ax, ay);
            }
            else
            {
                UpdateWindowed(cur.X, cur.Y);
            }
        }
        catch (Exception ex)
        {
            // Never let a per-frame error take down the app. Bail out of zoom cleanly.
            System.Diagnostics.Debug.WriteLine($"[Magnifier] tick error, deactivating: {ex}");
            Deactivate(animate: false);
        }
    }

    /// <summary>
    /// Resolves the anchor (cursor) point to use for this frame. With smoothing enabled the
    /// lens eases toward the real cursor — softening fast moves and the snap when the cursor
    /// crosses to another monitor — otherwise it tracks the cursor exactly.
    /// </summary>
    private (int X, int Y) ResolveAnchor(int cursorX, int cursorY, double dtMs)
    {
        int settleMs = _settings.Zoom.SmoothPanEnabled ? Math.Clamp(_settings.Zoom.PanSmoothingMs, 0, 400) : 0;

        if (settleMs <= 0 || !_anchorValid)
        {
            // Seed (or hard-track) on the real cursor position.
            _anchorX = cursorX;
            _anchorY = cursorY;
            _anchorValid = true;
            return (cursorX, cursorY);
        }

        // Exponential smoothing, framerate independent. tau ≈ settleMs/3 reaches ~95% in settleMs.
        double tau   = settleMs / 3.0;
        double alpha = 1.0 - Math.Exp(-Math.Max(dtMs, 0.0) / tau);
        _anchorX += (cursorX - _anchorX) * alpha;
        _anchorY += (cursorY - _anchorY) * alpha;

        // Snap the last sub-pixel so the view comes fully to rest when the cursor stops.
        if (Math.Abs(cursorX - _anchorX) < 0.5) _anchorX = cursorX;
        if (Math.Abs(cursorY - _anchorY) < 0.5) _anchorY = cursorY;

        return ((int)Math.Round(_anchorX), (int)Math.Round(_anchorY));
    }

    private void AdvanceAnimation()
    {
        double t = _animDurationMs > 0
            ? _animClock.Elapsed.TotalMilliseconds / _animDurationMs
            : 1.0;

        if (t >= 1.0)
        {
            _zoom = _animTo;
            _animating = false;
            _animClock.Stop();

            if (_animEndsDeactivate)
            {
                _animEndsDeactivate = false;
                FinishDeactivate();   // zoom-out ramp settled — hide the lens
                return;
            }

            StateChanged?.Invoke();   // refresh tray zoom readout once settled
        }
        else
        {
            // Zoom-in eases out (snappy start, gentle settle); zoom-out eases in (gentle
            // start, accelerates away) so the two feel like mirror images of each other.
            float e = _animZoomingIn
                ? (float)(1.0 - Math.Pow(1.0 - t, 3))   // ease-out cubic
                : (float)(t * t * t);                    // ease-in cubic
            _zoom = _animFrom + (_animTo - _animFrom) * e;
        }
    }

    /// <summary>
    /// Applies the full-screen magnification transform for the current zoom level, anchored
    /// so the content under (<paramref name="anchorX"/>, <paramref name="anchorY"/>) — the
    /// cursor — stays the fixed point of the zoom. The transform scales the entire virtual
    /// desktop, so the focus point and offset are clamped to the virtual screen (all monitors),
    /// letting the view pan all the way across every display.
    /// </summary>
    private void UpdateFullscreen(int anchorX, int anchorY)
    {
        // The fullscreen magnifier scales the whole virtual desktop. Its offset is the source
        // point shown at the desktop's top-left, in pixels relative to the primary monitor's
        // top-left -- which is the virtual-desktop origin, so cursor coordinates map directly.
        // Clamp to the *virtual screen* (every monitor), not just the primary one; clamping to
        // the primary monitor is what previously stopped the pan at its edge on multi-monitor
        // setups. The Magnification API allows offsets to the left of / above the primary
        // monitor (negative coordinates), which this preserves.
        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;

        float  m = Math.Max(1.0f, _zoom);
        double k = 1.0 - 1.0 / m;   // fraction of a coordinate that becomes the source offset

        // Offset that keeps the content under the cursor the fixed point of the zoom, clamped
        // so the magnified region never reaches past the edges of the virtual desktop.
        double ox = Math.Clamp(anchorX * k, vs.Left * k, (vs.Left + vs.Width)  * k);
        double oy = Math.Clamp(anchorY * k, vs.Top  * k, (vs.Top  + vs.Height) * k);

        var fs = (m, (int)Math.Round(ox), (int)Math.Round(oy));
        if (fs == _lastFs) return;            // nothing changed — avoid redundant churn
        _lastFs = fs;
        NativeMethods.MagSetFullscreenTransform(fs.Item1, fs.Item2, fs.Item3);
    }

    /// <summary>
    /// Windowed engine: the lens covers the monitor under the cursor at the current zoom,
    /// anchored crisply 1:1 on the cursor. Within one monitor it tracks exactly. When the
    /// cursor crosses to another monitor and "Smooth panning" is on, the lens fades out on the
    /// old screen and fades back in on the new one, so neither screen is ever shown
    /// half-magnified. With smoothing off it switches instantly (a hard snap).
    /// </summary>
    private void UpdateWindowed(int cursorX, int cursorY)
    {
        var target = GetTargetBounds(cursorX, cursorY);

        // First frame of this activation: seed on the current monitor, fully opaque.
        if (!_winMonValid)
        {
            _winMon      = target;
            _winMonValid = true;
            _fadePhase   = 0;
            RenderWindowed(_winMon, cursorX, cursorY, 255);
            return;
        }

        int fadeMs = _settings.Zoom.SmoothPanEnabled ? Math.Clamp(_settings.Zoom.PanSmoothingMs, 0, 400) : 0;
        int halfMs = fadeMs / 2;

        // Smoothing off (or zero duration): snap straight to the monitor under the cursor.
        if (halfMs <= 0)
        {
            _winMon    = target;
            _fadePhase = 0;
            RenderWindowed(_winMon, cursorX, cursorY, 255);
            return;
        }

        // Cursor moved to a different monitor → start the fade (or, if one is already
        // running, keep its destination pointed at the latest monitor).
        if (target != _winMon)
        {
            _fadeTarget = target;
            if (_fadePhase == 0)
            {
                _fadePhase = 1;            // begin fading out the old screen
                _fadeClock.Restart();
            }
        }

        switch (_fadePhase)
        {
            case 1:   // fading out on the old monitor
            {
                double t = _fadeClock.Elapsed.TotalMilliseconds / halfMs;
                if (t >= 1.0)
                {
                    _winMon    = _fadeTarget;   // hand off to the new monitor
                    _fadePhase = 2;
                    _fadeClock.Restart();
                    RenderWindowed(_winMon, cursorX, cursorY, 0);
                }
                else
                {
                    RenderWindowed(_winMon, cursorX, cursorY, (byte)(255 * (1.0 - t)));
                }
                break;
            }
            case 2:   // fading in on the new monitor
            {
                double t = _fadeClock.Elapsed.TotalMilliseconds / halfMs;
                if (t >= 1.0)
                {
                    _fadePhase = 0;
                    RenderWindowed(_winMon, cursorX, cursorY, 255);
                }
                else
                {
                    RenderWindowed(_winMon, cursorX, cursorY, (byte)(255 * t));
                }
                break;
            }
            default:  // steady
                RenderWindowed(_winMon, cursorX, cursorY, 255);
                break;
        }
    }

    private void RenderWindowed((int L, int T, int W, int H) mon, int cursorX, int cursorY, byte alpha)
    {
        int ax = Math.Clamp(cursorX, mon.L, mon.L + mon.W);
        int ay = Math.Clamp(cursorY, mon.T, mon.T + mon.H);
        _lens.Update(mon.L, mon.T, mon.W, mon.H, _zoom, ax, ay);
        _lens.SetAlpha(alpha);
    }

    /// <summary>
    /// Returns the region (physical pixels) the windowed lens should cover, based on the
    /// configured <see cref="Models.MonitorMode"/> and cursor position.
    /// </summary>
    private (int Left, int Top, int Width, int Height) GetTargetBounds(int mx, int my)
    {
        var (left, top, w, h) = GetMonitorBounds(mx, my);

        // Stay 1px shy of the full monitor height. A topmost window that covers a monitor
        // *exactly* trips the shell's fullscreen-app detection, which hides and sometimes
        // wedges the taskbar. One pixel of slack defeats that with no visible effect.
        return (left, top, w, Math.Max(1, h - 1));
    }

    private (int Left, int Top, int Width, int Height) GetMonitorBounds(int mx, int my)
    {
        switch (_settings.Zoom.MonitorMode)
        {
            case Models.MonitorMode.PrimaryOnly:
            {
                var b = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                        ?? new System.Drawing.Rectangle(0, 0,
                            (int)SystemParameters.PrimaryScreenWidth,
                            (int)SystemParameters.PrimaryScreenHeight);
                return (b.Left, b.Top, b.Width, b.Height);
            }
            case Models.MonitorMode.AllMonitors:
            {
                var v = System.Windows.Forms.SystemInformation.VirtualScreen;
                return (v.Left, v.Top, v.Width, v.Height);
            }
            case Models.MonitorMode.ActiveMonitor:
            default:
            {
                var b = System.Windows.Forms.Screen
                    .FromPoint(new System.Drawing.Point(mx, my)).Bounds;
                return (b.Left, b.Top, b.Width, b.Height);
            }
        }
    }

    // ── Zoom level ────────────────────────────────────────────────────────────

    private void AdjustZoom(float delta)
    {
        // A manual zoom / scroll cancels any in-progress ramp (intro or outro) and keeps
        // the lens up at the new level.
        _animating          = false;
        _animEndsDeactivate = false;
        _deactivating       = false;
        _zoom = Math.Clamp(_zoom + delta, _settings.Zoom.MinZoom, _settings.Zoom.MaxZoom);
        if (_isActive)
        {
            // Immediate update so the change feels responsive (dt 0 = no bounds easing step)
            NativeMethods.GetCursorPos(out var c);
            if (_settings.Zoom.Engine == MagnifierEngine.Fullscreen)
                UpdateFullscreen(c.X, c.Y);
            else
                UpdateWindowed(c.X, c.Y);
        }
        StateChanged?.Invoke();
    }

    // ── Scroll-to-zoom (low-level mouse hook) ─────────────────────────────────

    private void InstallMouseHook()
    {
        _mouseHookProc = MouseHookCallback;
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL, _mouseHookProc,
            NativeMethods.GetModuleHandle(null), 0);
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        _mouseHookProc = null;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == NativeMethods.HC_ACTION && (int)wParam == NativeMethods.WM_MOUSEWHEEL && _isActive)
        {
            try
            {
                var hs = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                short delta = (short)(hs.mouseData >> 16);
                if (delta > 0) AdjustZoom(+_settings.Zoom.ZoomStep);
                else           AdjustZoom(-_settings.Zoom.ZoomStep);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Magnifier] scroll error: {ex}");
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }
}

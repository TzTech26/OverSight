using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Oversight.Models;
using Oversight.Native;

namespace Oversight.Services;

/// <summary>
/// Repeats the active auto-clicker slot's key or mouse button while it is running. Nothing here
/// runs on its own — the run is started only by the "Start / stop auto-clicker" hotkey, which
/// <see cref="HotkeyManager"/> registers solely for apps the Game Profiles rules give an
/// auto-clicker slot. The run also stops the moment that app loses focus
/// (<see cref="SetActivationAllowed"/> → false), on app exit, and on any unhandled exception.
///
/// <list type="bullet">
///   <item><b>Toggle</b> mode — the key flips the run on / off (<see cref="ToggleRun"/>).</item>
///   <item><b>Hold</b> mode — the key runs the clicker only while physically held
///     (<see cref="BeginHoldRun"/>), polled via <see cref="NativeMethods.GetAsyncKeyState"/>
///     since RegisterHotKey only signals key-down.</item>
/// </list>
///
/// A run snapshots the slot's settings at start, so a mid-run slot switch can't change the
/// action, timing or stop-condition underneath it.
///
/// <para><b>Timing.</b> The click loop runs on its own background thread paced by a
/// <see cref="Stopwatch"/> (not a WPF timer, which only ticks at ~15 ms). While it runs the
/// system timer resolution is raised to 1 ms, so intervals down to 10 ms are honoured.</para>
/// </summary>
public sealed class AutoClickerService : IDisposable
{
    private readonly ConfigStore     _config;
    private readonly DispatcherTimer _holdPollTimer;
    private readonly DispatcherTimer _safetyTimer;
    private readonly Dispatcher      _dispatcher;

    private bool _activationAllowed;                 // Game Profiles verdict: start/stop bind registered here
    private bool _armed;                             // a run is active (counting down or clicking) — UI thread only
    private bool _holdMode;                          // started via Hold; poll the key for release
    private uint _holdKey;                           // start/stop key being polled in Hold mode
    private uint _targetPid;                         // foreground process when the run started
    private AutoClickerSettings _active = new();     // snapshot for the current run
    private Thread?                  _clickThread;   // background click loop
    private CancellationTokenSource? _runCts;        // cancels the current run's loop
    private bool _disposed;

    /// <summary>Raised when the clicker starts arming or stops, so the tray can reflect run state.</summary>
    public event Action? RunStateChanged;

    /// <summary>True while a run is active (including the safety countdown).</summary>
    public bool IsRunning => _armed;

    public AutoClickerService(ConfigStore config)
    {
        _config     = config;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Polls the Hold-mode start/stop key for release (RegisterHotKey signals key-down only).
        _holdPollTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(15) };
        _holdPollTimer.Tick += OnHoldPollTick;

        // One-shot safety countdown before the first click.
        _safetyTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _safetyTimer.Tick += OnSafetyTick;
    }

    /// <summary>Called by the process watcher with the Game Profiles verdict for the foreground app.</summary>
    public void SetActivationAllowed(bool allowed)
    {
        _activationAllowed = allowed;
        if (!allowed) StopRun();   // leaving the assigned app halts any run in progress
    }

    // ── Start / stop ──────────────────────────────────────────────────────────

    /// <summary>Toggle-mode handler: start a run if idle, stop it if one is active.</summary>
    public void ToggleRun()
    {
        if (_armed) StopRun();
        else        StartRun(holdMode: false, holdKey: 0);
    }

    /// <summary>Hold-mode handler: start a run and poll <paramref name="virtualKey"/> for release.</summary>
    public void BeginHoldRun(uint virtualKey)
    {
        if (_armed || virtualKey == 0) return;
        StartRun(holdMode: true, holdKey: virtualKey);
    }

    private void StartRun(bool holdMode, uint holdKey)
    {
        if (_disposed || !_activationAllowed) return;

        var snapshot = _config.Settings.AutoClicker.Clone();
        if (snapshot.ActionKey.IsEmpty) return;   // nothing to press

        _active   = snapshot;
        _armed    = true;
        _holdMode = holdMode;
        _holdKey  = holdKey;
        _targetPid = ForegroundPid();

        if (holdMode) _holdPollTimer.Start();
        RunStateChanged?.Invoke();

        if (_active.SafetyCountdownEnabled && _active.SafetyCountdownSeconds > 0)
        {
            _safetyTimer.Interval = TimeSpan.FromSeconds(_active.SafetyCountdownSeconds);
            _safetyTimer.Start();
        }
        else
        {
            BeginClicking();
        }
    }

    private void OnSafetyTick(object? sender, EventArgs e)
    {
        _safetyTimer.Stop();
        if (_armed) BeginClicking();
    }

    private void BeginClicking()
    {
        var cts = new CancellationTokenSource();
        _runCts = cts;
        var snapshot = _active;
        uint targetPid = _targetPid;

        _clickThread = new Thread(() => ClickLoop(snapshot, targetPid, cts.Token))
        {
            IsBackground = true,
            Name         = "Oversight-AutoClicker",
            Priority     = ThreadPriority.AboveNormal,
        };
        _clickThread.Start();
    }

    /// <summary>
    /// The click loop, on its own thread. Paces with a <see cref="Stopwatch"/> and a 1 ms system
    /// timer resolution so short intervals are honoured. Exits when the run is cancelled
    /// (stop / focus lost / hold released) or a stop-condition is met.
    /// </summary>
    private void ClickLoop(AutoClickerSettings s, uint targetPid, CancellationToken ct)
    {
        // The start/stop combo's Ctrl/Alt/Shift/Win would otherwise turn the first clicks into modified clicks.
        while (!ct.IsCancellationRequested && ModifiersHeld())
            Thread.Sleep(5);

        NativeMethods.TimeBeginPeriod(1);
        var sw       = Stopwatch.StartNew();
        var healthSw = Stopwatch.StartNew();
        try
        {
            long   count      = 0;
            double durationMs  = s.RepeatSeconds * 1000.0;
            double nextDueMs   = 0;
            bool   healthy     = true;

            while (!ct.IsCancellationRequested)
            {
                if (s.RepeatMode == RepeatMode.ForDuration && sw.Elapsed.TotalMilliseconds >= durationMs)
                    break;

                // The process watcher is paused while Settings is open, so check focus here too.
                if (ForegroundPid() != targetPid)
                    break;

                // Adaptive limiter: a few times per frame, check that the foreground window is
                // still draining its input. If it's saturated (WM_NULL times out), stop piling on
                // clicks and let it recover — this is what keeps fast rates from freezing the app.
                if (s.AdaptiveLimit && healthSw.Elapsed.TotalMilliseconds >= 8)
                {
                    healthy = TargetResponsive(timeoutMs: 30);
                    healthSw.Restart();
                }
                if (s.AdaptiveLimit && !healthy)
                {
                    Thread.Sleep(4);
                    nextDueMs = sw.Elapsed.TotalMilliseconds;   // resume cleanly, no burst catch-up
                    continue;
                }

                try { PerformAction(s); }
                catch (Exception ex) { Debug.WriteLine($"[AutoClicker] send failed: {ex.Message}"); }

                count++;
                if (s.RepeatMode == RepeatMode.ForCount && count >= Math.Max(1, s.RepeatCount))
                    break;

                // Schedule the next click relative to the running clock (drift-free).
                nextDueMs += NextIntervalMs(s);
                WaitUntil(sw, nextDueMs, ct);
            }
        }
        finally
        {
            NativeMethods.TimeEndPeriod(1);
            // If we fell out because a stop-condition was met (not an external cancel),
            // tell the UI to tear the run down and update the tray.
            if (!ct.IsCancellationRequested)
                _dispatcher.BeginInvoke(new Action(StopRun));
        }
    }

    /// <summary>
    /// True if the foreground window replies to a WM_NULL within <paramref name="timeoutMs"/> — i.e.
    /// its message pump is keeping up. A timeout means it's overwhelmed, so the loop should back off.
    /// No foreground window → treat as responsive (nothing to protect).
    /// </summary>
    private static bool TargetResponsive(uint timeoutMs)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return true;
        return NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_BLOCK, timeoutMs, out _) != IntPtr.Zero;
    }

    private static uint ForegroundPid()
    {
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);
        return pid;
    }

    private static bool ModifiersHeld() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }   // Shift, Ctrl, Alt, LWin, RWin
            .Any(vk => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0);

    /// <summary>Waits until <paramref name="dueMs"/> on <paramref name="sw"/>, cheaply then precisely.</summary>
    private static void WaitUntil(Stopwatch sw, double dueMs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            double remaining = dueMs - sw.Elapsed.TotalMilliseconds;
            if (remaining <= 0) return;
            if (remaining > 2) Thread.Sleep(1);   // ~1 ms accurate thanks to timeBeginPeriod(1)
            else Thread.SpinWait(50);             // final sub-2 ms with a tight spin
        }
    }

    /// <summary>Stops any run in progress and resets state. Safe to call when already stopped. UI thread.</summary>
    public void StopRun()
    {
        bool wasArmed = _armed;
        _runCts?.Cancel();
        _holdPollTimer.Stop();
        _safetyTimer.Stop();
        _armed = false;
        if (wasArmed) RunStateChanged?.Invoke();
    }

    private void OnHoldPollTick(object? sender, EventArgs e)
    {
        bool down = (NativeMethods.GetAsyncKeyState((int)_holdKey) & 0x8000) != 0;
        if (!down) StopRun();
    }

    /// <summary>Hard floor on the interval: 10 ms = a 100 clicks-per-second ceiling, to stay responsive.</summary>
    private const int MinIntervalMs = 10;

    private static int NextIntervalMs(AutoClickerSettings s)
    {
        int baseMs = Math.Max(MinIntervalMs, s.IntervalMs);
        if (!s.JitterEnabled || s.JitterMs <= 0) return baseMs;
        int offset = Random.Shared.Next(-s.JitterMs, s.JitterMs + 1);   // Random.Shared is thread-safe
        return Math.Max(MinIntervalMs, baseMs + offset);
    }

    // ── Input injection ───────────────────────────────────────────────────────

    private static void PerformAction(AutoClickerSettings s)
    {
        uint vk = s.ActionKey.VirtualKey;
        if (vk == 0) return;

        if (HotkeyBinding.IsMouseButton(vk))
        {
            if (s.UseFixedPosition)
                NativeMethods.SetCursorPos(s.FixedX, s.FixedY);

            SendMouseClick(vk);
            if (s.ClickType == MouseClickType.Double)
                SendMouseClick(vk);
        }
        else
        {
            SendKey(vk);
        }
    }

    private static void SendMouseClick(uint vk)
    {
        (uint down, uint up, uint data) = vk switch
        {
            0x01 => (NativeMethods.MOUSEEVENTF_LEFTDOWN,   NativeMethods.MOUSEEVENTF_LEFTUP,   0u),
            0x02 => (NativeMethods.MOUSEEVENTF_RIGHTDOWN,  NativeMethods.MOUSEEVENTF_RIGHTUP,  0u),
            0x04 => (NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP, 0u),
            0x05 => (NativeMethods.MOUSEEVENTF_XDOWN,      NativeMethods.MOUSEEVENTF_XUP,      NativeMethods.XBUTTON1),
            0x06 => (NativeMethods.MOUSEEVENTF_XDOWN,      NativeMethods.MOUSEEVENTF_XUP,      NativeMethods.XBUTTON2),
            _    => (0u, 0u, 0u),
        };
        if (down == 0) return;

        var inputs = new[]
        {
            MouseInput(down, data),
            MouseInput(up,   data),
        };
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void SendKey(uint vk)
    {
        uint scan = NativeMethods.MapVirtualKey(vk, NativeMethods.MAPVK_VK_TO_VSC);
        uint extra = IsExtendedKey(vk) ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0;

        NativeMethods.INPUT down, up;
        if (scan != 0)
        {
            // Scancode input — more games honour this than virtual-key input.
            down = KeyInput(0, (ushort)scan, NativeMethods.KEYEVENTF_SCANCODE | extra);
            up   = KeyInput(0, (ushort)scan, NativeMethods.KEYEVENTF_SCANCODE | NativeMethods.KEYEVENTF_KEYUP | extra);
        }
        else
        {
            // No scancode mapping — fall back to virtual-key input.
            down = KeyInput((ushort)vk, 0, extra);
            up   = KeyInput((ushort)vk, 0, NativeMethods.KEYEVENTF_KEYUP | extra);
        }

        var inputs = new[] { down, up };
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static NativeMethods.INPUT MouseInput(uint flags, uint data) => new()
    {
        type = NativeMethods.INPUT_MOUSE,
        u    = new NativeMethods.INPUTUNION
        {
            mi = new NativeMethods.MOUSEINPUT { dwFlags = flags, mouseData = data },
        },
    };

    private static NativeMethods.INPUT KeyInput(ushort vk, ushort scan, uint flags) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u    = new NativeMethods.INPUTUNION
        {
            ki = new NativeMethods.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags },
        },
    };

    /// <summary>Virtual keys that require the extended-key flag when sent by scancode.</summary>
    private static bool IsExtendedKey(uint vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 => true, // PgUp/Dn, End, Home, arrows
        0x2D or 0x2E => true,                                                 // Insert, Delete
        0x2C => true,                                                         // PrintScreen
        0x90 or 0x91 => true,                                                 // NumLock, ScrollLock (context)
        0xA3 or 0xA5 => true,                                                 // RControl, RMenu (right Ctrl/Alt)
        _    => false,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRun();
        _holdPollTimer.Tick -= OnHoldPollTick;
        _safetyTimer.Tick   -= OnSafetyTick;
    }
}

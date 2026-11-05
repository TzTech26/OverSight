using System.Diagnostics;
using Avalonia.Threading;
using Oversight.Linux.Platform;
using Oversight.Models;
using Oversight.Services;

namespace Oversight.Linux.Services;

/// <summary>
/// Repeats the active auto-clicker slot's key or mouse button through XTest, with the Windows app's safety rules: it
/// only starts from the start/stop hotkey (grabbed solely for apps Game Profiles give a slot), stops when the focused
/// app changes, on quit and after a crash, waits for the start combo's modifiers to be released before the first click,
/// and never clicks faster than every 10 ms. A run snapshots the slot's settings when it starts.
/// </summary>
public sealed unsafe class AutoClickerService : IDisposable
{
    private readonly ConfigStore _config;
    private readonly DispatcherTimer _holdPollTimer;
    private readonly DispatcherTimer _safetyTimer;

    private bool _activationAllowed, _armed, _disposed;
    private uint _holdKey;
    private long _target;                            // focused app when the run started (see TargetOf)
    private AutoClickerSettings _active = new();
    private CancellationTokenSource? _runCts;

    public event Action? RunStateChanged;
    public bool IsRunning => _armed;

    public AutoClickerService(ConfigStore config)
    {
        _config = config;
        _holdPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _holdPollTimer.Tick += (_, _) => { if (!KeyMap.IsDown(_holdKey)) StopRun(); };
        _safetyTimer = new DispatcherTimer();
        _safetyTimer.Tick += (_, _) =>
        {
            _safetyTimer.Stop();
            if (_armed) BeginClicking();
        };
    }

    public void SetActivationAllowed(bool allowed)
    {
        _activationAllowed = allowed;
        if (!allowed) StopRun();   // leaving the assigned app halts any run in progress
    }

    public void ToggleRun()
    {
        if (_armed) StopRun();
        else StartRun(holdKey: 0);
    }

    public void BeginHoldRun(uint virtualKey)
    {
        if (_armed || virtualKey == 0) return;
        StartRun(virtualKey);
    }

    private void StartRun(uint holdKey)
    {
        if (_disposed || !_activationAllowed || !X11.Available) return;
        var snapshot = _config.Settings.AutoClicker.Clone();
        if (snapshot.ActionKey.IsEmpty) return;

        _active = snapshot;
        _armed = true;
        _holdKey = holdKey;
        _target = TargetOf(X11.Display);
        if (holdKey != 0) _holdPollTimer.Start();
        RunStateChanged?.Invoke();

        if (_active.SafetyCountdownEnabled && _active.SafetyCountdownSeconds > 0)
        {
            _safetyTimer.Interval = TimeSpan.FromSeconds(_active.SafetyCountdownSeconds);
            _safetyTimer.Start();
        }
        else BeginClicking();
    }

    private void BeginClicking()
    {
        var cts = new CancellationTokenSource();
        _runCts = cts;
        var snapshot = _active;
        long target = _target;
        new Thread(() => ClickLoop(snapshot, target, cts.Token)) { IsBackground = true, Name = "Oversight-AutoClicker", Priority = ThreadPriority.AboveNormal }.Start();
    }

    private void ClickLoop(AutoClickerSettings s, long target, CancellationToken ct)
    {
        // The start/stop combo's Ctrl/Alt/Shift/Super would otherwise turn the first clicks into modified clicks.
        while (!ct.IsCancellationRequested && KeyMap.ModifiersHeld()) Thread.Sleep(5);

        nint d = X11.Open();   // its own connection: the ping replies arrive as events on it
        if (d == 0) { Dispatcher.UIThread.Post(StopRun); return; }
        X11.XSelectInput(d, X11.XDefaultRootWindow(d), (nint)X11.SubstructureNotifyMask);
        var sw = Stopwatch.StartNew();
        var healthSw = Stopwatch.StartNew();
        try
        {
            long count = 0;
            double durationMs = s.RepeatSeconds * 1000.0, nextDueMs = 0;
            bool healthy = true;
            while (!ct.IsCancellationRequested)
            {
                if (s.RepeatMode == RepeatMode.ForDuration && sw.Elapsed.TotalMilliseconds >= durationMs) break;

                // The process watcher is paused while Settings is open, so check focus here too.
                // No focused window for a moment during a focus change is not a reason to stop.
                if (TargetOf(d) is var now && now != 0 && now != target) break;

                // Adaptive limiter: if the focused window stops answering pings, stop piling on clicks until it recovers.
                if (s.AdaptiveLimit && healthSw.Elapsed.TotalMilliseconds >= 8)
                {
                    healthy = Responsive(d, timeoutMs: 30);
                    healthSw.Restart();
                }
                if (s.AdaptiveLimit && !healthy)
                {
                    Thread.Sleep(4);
                    nextDueMs = sw.Elapsed.TotalMilliseconds;
                    continue;
                }

                PerformAction(d, s);
                count++;
                if (s.RepeatMode == RepeatMode.ForCount && count >= Math.Max(1, s.RepeatCount)) break;

                nextDueMs += NextIntervalMs(s);
                WaitUntil(sw, nextDueMs, ct);
            }
        }
        finally
        {
            X11.XCloseDisplay(d);
            if (!ct.IsCancellationRequested) Dispatcher.UIThread.Post(StopRun);
        }
    }

    /// <summary>The focused app: its pid, or minus its window id when it has no _NET_WM_PID. 0 = nothing focused.</summary>
    private static long TargetOf(nint d)
    {
        nint w = X11.ActiveWindow(d);
        if (w == 0) return 0;
        int pid = X11.WindowPid(d, w);
        return pid > 0 ? pid : -(long)w;
    }

    private uint _ping;

    /// <summary>
    /// True if the focused window answers a _NET_WM_PING within <paramref name="timeoutMs"/>, or doesn't take pings at
    /// all. A slow answer means its event loop is swamped, so the clicker backs off.
    /// </summary>
    private bool Responsive(nint d, int timeoutMs)
    {
        nint w = X11.ActiveWindow(d), root = X11.XDefaultRootWindow(d);
        nint protocols = X11.Atom(d, "WM_PROTOCOLS"), ping = X11.Atom(d, "_NET_WM_PING");
        if (w == 0 || !X11.Longs(d, w, "WM_PROTOCOLS", X11.XA_ATOM).Contains(ping)) return true;

        uint stamp = ++_ping;
        var ev = new X11.XEvent();
        byte* p = (byte*)&ev;
        *(int*)p = X11.ClientMessage;
        *(nint*)(p + 32) = w;
        *(nint*)(p + 40) = protocols;
        *(int*)(p + 48) = 32;
        *(nint*)(p + 56) = ping;
        *(nint*)(p + 64) = (nint)stamp;
        *(nint*)(p + 72) = w;
        X11.XSendEvent(d, w, 0, 0, &ev);
        X11.XFlush(d);

        var sw = Stopwatch.StartNew();
        var reply = new X11.XEvent();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            while (X11.XPending(d) > 0)
            {
                X11.XNextEvent(d, &reply);
                if (reply.type == X11.ClientMessage && reply.Long(56) == ping && (uint)reply.Long(64) == stamp) return true;
            }
            Thread.Sleep(1);
        }
        return false;
    }

    private static void WaitUntil(Stopwatch sw, double dueMs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            double remaining = dueMs - sw.Elapsed.TotalMilliseconds;
            if (remaining <= 0) return;
            if (remaining > 2) Thread.Sleep(1);
            else Thread.SpinWait(50);
        }
    }

    public void StopRun()
    {
        bool wasArmed = _armed;
        _runCts?.Cancel();
        _holdPollTimer.Stop();
        _safetyTimer.Stop();
        _armed = false;
        if (wasArmed) RunStateChanged?.Invoke();
    }

    /// <summary>Hard floor on the interval: 10 ms = a 100 clicks-per-second ceiling, to stay responsive.</summary>
    private const int MinIntervalMs = 10;

    private static int NextIntervalMs(AutoClickerSettings s)
    {
        int baseMs = Math.Max(MinIntervalMs, s.IntervalMs);
        if (!s.JitterEnabled || s.JitterMs <= 0) return baseMs;
        return Math.Max(MinIntervalMs, baseMs + Random.Shared.Next(-s.JitterMs, s.JitterMs + 1));
    }

    /// <summary>One tick: a click (single or double, at the fixed point if set) or a key press, through XTest.</summary>
    public static void PerformAction(nint d, AutoClickerSettings s)
    {
        uint vk = s.ActionKey.VirtualKey;
        if (KeyMap.Button(vk) is int button and > 0)
        {
            if (s.UseFixedPosition) X11.XTestFakeMotionEvent(d, -1, s.FixedX, s.FixedY, 0);
            for (int i = s.ClickType == MouseClickType.Double ? 2 : 1; i > 0; i--)
            {
                X11.XTestFakeButtonEvent(d, (uint)button, 1, 0);
                X11.XTestFakeButtonEvent(d, (uint)button, 0, 0);
            }
        }
        else if (KeyMap.Keycode(d, vk) is int keycode and > 0)
        {
            X11.XTestFakeKeyEvent(d, (uint)keycode, 1, 0);
            X11.XTestFakeKeyEvent(d, (uint)keycode, 0, 0);
        }
        X11.XFlush(d);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRun();
    }
}

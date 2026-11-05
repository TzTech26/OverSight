using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Oversight.Linux.Platform;
using Oversight.Linux.Services;
using Oversight.Models;
using Oversight.Services;

namespace Oversight.Linux;

/// <summary>
/// <c>oversight --self-test</c>: checks the platform layer against the current X display (CI runs it under Xvfb with a
/// window manager) and prints one PASS/FAIL line per check. Uses a throwaway config folder. Exit code 1 on any failure.
/// </summary>
public static class SelfTest
{
    private static int _failures;

    private static void Report(bool ok, string check, string detail)
    {
        if (!ok) _failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {check}: {detail}");
    }

    public static async Task<int> RunAsync()
    {
        Console.WriteLine($"Oversight {Updater.CurrentVersion} self-test on DISPLAY={Environment.GetEnvironmentVariable("DISPLAY")}");
        try
        {
            Settings();
            nint d = X11.Display;
            if (d == 0) Report(false, "display", "no X display");
            else
            {
                await Hotkey(d);
                await Crosshair(d);
                await ClickAndFocus(d);
            }
        }
        catch (Exception ex) { Report(false, "crash", ex.ToString()); }
        Console.WriteLine(_failures == 0 ? "All checks passed." : $"{_failures} check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>A settings.json written by the Windows app loads and saves back byte for byte.</summary>
    private static void Settings()
    {
        using var stream = typeof(SelfTest).Assembly.GetManifestResourceStream("SelfTestSettings.json")!;
        string sample = new StreamReader(stream).ReadToEnd().ReplaceLineEndings("\n");
        string path = Path.Combine(ConfigStore.ConfigDirectory, "settings.json");
        Directory.CreateDirectory(ConfigStore.ConfigDirectory);
        File.WriteAllText(path, sample);

        var store = new ConfigStore();
        store.Load();
        store.Save();
        string saved = File.ReadAllText(path);
        var rule = store.AppRules.FirstOrDefault();
        bool ok = saved == sample && rule is { ProcessName: "eldenring", ZoomProfile: "Sniper", AutoClickerProfile: "Farm" } && !File.Exists(path + ".bad");
        Report(ok, "settings", ok ? $"Windows settings.json round-tripped unchanged ({sample.Length} bytes, {store.ZoomProfileNames.Count} zoom slots, {store.AppRules.Count} game rule)"
            : $"saved file differs from the original (first difference at {Enumerable.Range(0, Math.Min(saved.Length, sample.Length)).FirstOrDefault(i => saved[i] != sample[i])})");
    }

    /// <summary>A grabbed hotkey (including with NumLock on) arrives when the keys are sent with XTest.</summary>
    private static async Task Hotkey(nint d)
    {
        var settings = new AppSettings();
        settings.Hotkeys["OpenSettings"] = new HotkeyBinding { Modifiers = VirtualKeys.MOD_CONTROL | VirtualKeys.MOD_ALT, VirtualKey = 0x4F };   // Ctrl+Alt+O (Ctrl+Alt+F-keys switch VTs in XKB and never reach clients)
        using var hotkeys = new HotkeyManager(settings, new Magnifier(settings));
        int received = 0;
        string? conflict = null;
        hotkeys.OpenSettingsRequested += () => received++;
        hotkeys.HotkeyConflict += m => conflict = m;
        hotkeys.Start();
        await Task.Delay(700);   // the X thread grabs the keys

        int[] combo = [KeyMap.Keycode(d, 0xA2), KeyMap.Keycode(d, 0xA4), KeyMap.Keycode(d, 0x4F)];
        async Task Press()
        {
            foreach (int k in combo) X11.XTestFakeKeyEvent(d, (uint)k, 1, 0);
            foreach (int k in combo.Reverse()) X11.XTestFakeKeyEvent(d, (uint)k, 0, 0);
            X11.XFlush(d);
            await Task.Delay(400);
        }
        int numLock = KeyMap.Keycode(d, 0x90);
        void TapNumLock() { X11.XTestFakeKeyEvent(d, (uint)numLock, 1, 0); X11.XTestFakeKeyEvent(d, (uint)numLock, 0, 0); X11.XFlush(d); }

        await Press();
        int plain = received;
        TapNumLock();
        await Task.Delay(200);
        await Press();
        TapNumLock();
        bool ok = plain == 1 && received == 2;
        Report(ok, "hotkey", ok ? "Ctrl+Alt+O grabbed and received, also with NumLock on"
            : conflict ?? $"received {plain} of 1 presses, {received - plain} of 1 with NumLock on");
    }

    /// <summary>The overlay maps, stays above other windows, and has an empty input shape (clicks pass through).</summary>
    private static async Task Crosshair(nint d)
    {
        var store = new ConfigStore();
        store.Load();
        var crosshair = new CrosshairService(store);
        crosshair.SetPreview(new CrosshairSettings());
        nint w = crosshair.Overlay?.XWindow ?? 0;
        nint above = X11.Atom(d, "_NET_WM_STATE_ABOVE");
        bool mapped = false, top = false, shape = false;
        for (int i = 0; i < 30 && !(mapped && top && shape); i++)
        {
            await Task.Delay(100);
            mapped = w != 0 && X11.IsMapped(d, w);
            top = w != 0 && X11.Longs(d, w, "_NET_WM_STATE", X11.XA_ATOM).Contains(above);
            shape = w != 0 && X11.InputShapeRectangles(d, w) == 0;
        }
        Report(mapped && top && shape, "crosshair", $"overlay mapped: {mapped}, above others: {top}, empty input shape: {shape}");
        crosshair.Dispose();
    }

    /// <summary>An XTest click through the auto clicker's code lands on a test window, which then reads as the focused app.</summary>
    private static async Task ClickAndFocus(nint d)
    {
        var target = new Window
        {
            Title = "Oversight self-test", Width = 240, Height = 240, Background = Brushes.White,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(60, 60),
        };
        int clicks = 0;
        target.PointerPressed += (_, e) => { if (e.GetCurrentPoint(target).Properties.IsLeftButtonPressed) clicks++; };
        target.Show();
        await Task.Delay(1000);

        var center = target.PointToScreen(new Point(120, 120));
        var click = new AutoClickerSettings { ActionKey = new HotkeyBinding { VirtualKey = VirtualKeys.VK_LBUTTON }, UseFixedPosition = true, FixedX = center.X, FixedY = center.Y };
        AutoClickerService.PerformAction(d, click);
        for (int i = 0; i < 30 && clicks == 0; i++) await Task.Delay(100);
        Report(clicks == 1, "click", clicks == 1 ? $"XTest left click at {center.X},{center.Y} reached the test window" : $"test window saw {clicks} clicks");

        target.Activate();
        string? own = X11.ProcessName(Environment.ProcessId);
        (int Pid, string? Name) focused = default;
        for (int i = 0; i < 30 && focused.Name != own; i++)
        {
            await Task.Delay(100);
            focused = X11.FocusedProcess();
        }
        Report(focused.Name == own, "focus", $"active window belongs to {focused.Name ?? "nothing"} (pid {focused.Pid}), expected {own}");
        target.Close();
    }
}

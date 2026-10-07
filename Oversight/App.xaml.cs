using System.IO;
using System.Windows;
using System.Windows.Threading;
using Oversight.Services;
using Oversight.Tray;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Oversight;

public partial class App : Application
{
    private ConfigStore?        _config;
    private MagnifierService?   _magnifier;
    private CrosshairService?   _crosshair;
    private AutoClickerService? _autoClicker;
    private HotkeyManager?      _hotkeys;
    private ProcessWatcher?     _watcher;
    private TrayManager?        _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Match the Windows light/dark setting and keep following it live.
        // Must run before any window opens so the palette is in place.
        ThemeManager.Initialize();

        // Safety net: whatever happens, never leave the fullscreen magnifier
        // engaged. Without this a crash leaves the whole desktop stuck zoomed.
        DispatcherUnhandledException += (_, args) =>
        {
            MagnifierService.EmergencyReset();
            TryStopAutoClicker();          // never leave a runaway clicker going after a crash
            LogError(args.Exception);
            args.Handled = true;   // keep the tray app alive rather than bricking the screen
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { MagnifierService.EmergencyReset(); TryStopAutoClicker(); LogError(args.ExceptionObject); };
        AppDomain.CurrentDomain.ProcessExit        += (_, _) => { MagnifierService.EmergencyReset(); TryStopAutoClicker(); };

        // Single-instance guard via a named mutex. The name keeps the pre-rename "OverSight"
        // casing on purpose: mutex names are case-sensitive, and older installs use this one.
        var mutex = new System.Threading.Mutex(true, "OverSight_SingleInstance" + AppProfile.Suffix, out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("Oversight is already running.", "Oversight",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        GC.KeepAlive(mutex);

        // Rebrand migration: carry the old ScreenZoom autostart entry over to Oversight.
        if (!AppProfile.IsIsolated) StartupManager.MigrateLegacyEntry();

        _config = new ConfigStore();
        _config.Load();

        _magnifier = new MagnifierService(_config.Settings);
        if (!_magnifier.Initialize())
        {
            MessageBox.Show(
                "The Windows Magnification API could not be initialised.\n" +
                "Oversight requires Windows 8 or later.",
                "Oversight — Fatal Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _crosshair   = new CrosshairService(_config);
        _autoClicker = new AutoClickerService(_config);
        _hotkeys     = new HotkeyManager(_config.Settings, _magnifier);
        _watcher     = new ProcessWatcher(_config, _hotkeys, _crosshair, _autoClicker);
        _tray        = new TrayManager(_config, _hotkeys, _magnifier, _watcher, _crosshair, _autoClicker);

        // Wire up hotkey conflict notifications → tray balloon
        _hotkeys.HotkeyConflict += msg =>
            Dispatcher.BeginInvoke(() => _tray?.ShowBalloon("Hotkey conflict", msg));

        // "Show/hide crosshair" hotkey
        _hotkeys.ToggleCrosshairRequested += () => _crosshair?.ToggleHidden();

        // "Hide while held" crosshair hotkey (e.g. aiming down sights)
        _hotkeys.HoldHideCrosshairRequested += vk => _crosshair?.BeginHoldHide(vk);

        // Auto-clicker start/stop hotkey (Toggle vs Hold decided by the active slot)
        _hotkeys.ToggleAutoClickerRequested += () => _autoClicker?.ToggleRun();
        _hotkeys.HoldAutoClickerRequested   += vk => _autoClicker?.BeginHoldRun(vk);

        _tray.Show();

        // An isolated profile (OVERSIGHT_PROFILE, used for UI development) must not grab the
        // global hotkeys or drive slot switching — a normal install may be running beside it.
        if (AppProfile.IsIsolated)
        {
            _tray.OpenSettings();
            return;
        }

        _hotkeys.Start();
        _watcher.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _magnifier?.Deactivate(animate: false);
        _autoClicker?.StopRun();
        _hotkeys?.Stop();
        _watcher?.Dispose();
        _tray?.Dispose();
        _crosshair?.Dispose();
        _autoClicker?.Dispose();
        _magnifier?.Dispose();
        _config?.Save();
        base.OnExit(e);
    }

    /// <summary>
    /// Appends an unhandled exception to error.log in the config folder (trimmed to the last
    /// ~256 KB) so problems swallowed by the keep-alive handler can still be diagnosed. Never throws.
    /// </summary>
    private static void LogError(object? error)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[App] unhandled: {error}");
            var path = Path.Combine(ConfigStore.ConfigDirectory, "error.log");
            Directory.CreateDirectory(ConfigStore.ConfigDirectory);
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never take the app down */ }
    }

    /// <summary>Best-effort stop of the auto-clicker from a crash / exit handler; never throws.</summary>
    private void TryStopAutoClicker()
    {
        try { _autoClicker?.StopRun(); }
        catch { /* shutting down — ignore */ }
    }
}

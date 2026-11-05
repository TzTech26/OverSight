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
    private Updater?            _updater;
    private DispatcherTimer?    _updateTimer;
    private static System.Threading.Mutex? _instanceLock;
    private EventWaitHandle?    _showSignal;
    private DateTime            _lastConflictWarning = DateTime.MinValue;
    private static readonly string ShowEventName = "OverSight_ShowSettings" + AppProfile.Suffix;

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

        // After an update the new copy waits for the old one to exit, then removes the old exe.
        if (e.Args.SkipWhile(a => a != "--wait").Skip(1).FirstOrDefault() is string pidText && int.TryParse(pidText, out int pid))
        {
            try
            {
                using var old = System.Diagnostics.Process.GetProcessById(pid);
                Updater.Log($"{Updater.CurrentVersion}: waiting for {old.ProcessName} ({pid}) to exit.");
                // An old copy that didn't quit would hold the hotkeys and the tray, so close it.
                // Name check guards against a reused pid; a renamed old exe reports as "Oversight.exe.old".
                if (old.ProcessName.StartsWith("Oversight", StringComparison.OrdinalIgnoreCase) && !old.WaitForExit(15000))
                {
                    Updater.Log($"{Updater.CurrentVersion}: old copy still running after 15 s, closing it.");
                    old.Kill();
                    old.WaitForExit(5000);
                }
            }
            catch (ArgumentException) { }   // already gone
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Updater.Log($"{Updater.CurrentVersion}: couldn't close the old copy: {ex.Message}");
            }
        }
        if (Updater.CanSelfUpdate) Updater.CleanUpAfterUpdate();

        // Single-instance guard via a named mutex. The name keeps the pre-rename "OverSight"
        // casing on purpose: mutex names are case-sensitive, and older installs use this one.
        // Held in a field for the app's lifetime: a local gets garbage-collected, which releases the lock.
        _instanceLock = new System.Threading.Mutex(true, "OverSight_SingleInstance" + AppProfile.Suffix, out bool isNew);
        if (!isNew)
        {
            // Launching again opens the running copy's settings (copies older than 2.3.2 don't listen for this).
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); }
            catch (WaitHandleCannotBeOpenedException)
            {
                MessageBox.Show("Oversight is already running. Click its tray icon to open it.", "Oversight",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

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
        _updater     = new Updater(_config);
        _updater.QuitRequested += () => Dispatcher.BeginInvoke(() => { _tray?.SuppressUnsavedPrompt(); Shutdown(); });
        _tray        = new TrayManager(_config, _hotkeys, _magnifier, _watcher, _crosshair, _autoClicker, _updater);

        // Wire up hotkey conflict notifications → tray balloon
        // At most one conflict warning every 30 minutes, and none when they're turned off in Settings > General.
        _hotkeys.HotkeyConflict += msg => Dispatcher.BeginInvoke(() =>
        {
            if (_config?.ShowHotkeyWarnings != true || DateTime.Now - _lastConflictWarning < TimeSpan.FromMinutes(30)) return;
            _lastConflictWarning = DateTime.Now;
            _tray?.ShowBalloon("Hotkey conflict", msg + " Click to change it or turn these warnings off.");
        });

        // "Show/hide crosshair" hotkey
        _hotkeys.ToggleCrosshairRequested += () => _crosshair?.ToggleHidden();

        // Auto-clicker start/stop hotkey (Toggle vs Hold decided by the active slot)
        _hotkeys.ToggleAutoClickerRequested += () => _autoClicker?.ToggleRun();
        _hotkeys.HoldAutoClickerRequested   += vk => _autoClicker?.BeginHoldRun(vk);

        _tray.Show();

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() => { while (_showSignal.WaitOne()) Dispatcher.BeginInvoke(() => _tray?.OpenSettings()); }) { IsBackground = true }.Start();

        // An isolated profile (OVERSIGHT_PROFILE, used for UI development) must not grab the
        // global hotkeys or drive slot switching — a normal install may be running beside it.
        if (AppProfile.IsIsolated)
        {
            _tray.OpenSettings();
            return;
        }

        _hotkeys.Start();
        _watcher.Start();

        if (Updater.CanSelfUpdate)
        {
            // No installer: the Start menu entry is (re)written each start and follows the exe if it moves.
            try { StartMenu.Install(); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or IOException) { }

            if (e.Args.Contains("--updated")) _tray.ShowBalloon("Oversight updated", $"You're now on version {Updater.CurrentVersion}.");

            // Checks GitHub at most every 20 hours (the updater keeps track); the timer just gives it a chance to.
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _updateTimer.Tick += async (_, _) =>
            {
                _updateTimer.Interval = TimeSpan.FromMinutes(30);
                await _updater.CheckInBackgroundAsync(u =>
                    _tray?.ShowBalloon($"Oversight {u.Version} is available", "Open Settings › General to install it."));
            };
            _updateTimer.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _updateTimer?.Stop();
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
    internal static void LogError(object? error)
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

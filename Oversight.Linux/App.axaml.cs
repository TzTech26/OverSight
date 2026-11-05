using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Oversight.Linux.Platform;
using Oversight.Linux.Services;
using Oversight.Linux.Ui;
using Oversight.Services;

namespace Oversight.Linux;

public partial class App : Application
{
    private ConfigStore _config = null!;
    private Magnifier _magnifier = null!;
    private CrosshairService _crosshair = null!;
    private AutoClickerService? _autoClicker;
    private HotkeyManager _hotkeys = null!;
    private ProcessWatcher _watcher = null!;
    private Updater _updater = null!;
    private Tray? _tray;
    private SettingsWindow? _settingsWindow;
    private readonly Notifier _notifier = new();
    private DateTime _lastConflictWarning = DateTime.MinValue;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (Program.SelfTest)
        {
            Dispatcher.UIThread.Post(async () => Exit(await SelfTest.RunAsync()));
            return;
        }

        // Never leave the desktop zoomed or a clicker running after a crash; keep the tray app alive instead.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Magnifier.EmergencyReset();
            TryStopAutoClicker();
            ErrorLog.Write(e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => TryStopAutoClicker();

        _config = new ConfigStore();
        _config.Load();
        _magnifier = new Magnifier(_config.Settings);
        _crosshair = new CrosshairService(_config);
        _autoClicker = new AutoClickerService(_config);
        _hotkeys = new HotkeyManager(_config.Settings, _magnifier);
        _watcher = new ProcessWatcher(_config, _hotkeys, _crosshair, _autoClicker);
        _updater = new Updater(_config);
        _updater.QuitRequested += () => Dispatcher.UIThread.Post(Quit);

        _tray = new Tray();
        _tray.ToggleZoomRequested += _magnifier.Toggle;
        _tray.SettingsRequested += OpenSettings;
        _tray.QuitRequested += Quit;
        _magnifier.StateChanged += UpdateTray;
        _autoClicker.RunStateChanged += UpdateTray;
        _hotkeys.OpenSettingsRequested += ToggleSettings;

        // At most one conflict warning every 30 minutes, and none when they're turned off in Settings > General.
        _hotkeys.HotkeyConflict += msg =>
        {
            if (!_config.ShowHotkeyWarnings || DateTime.Now - _lastConflictWarning < TimeSpan.FromMinutes(30)) return;
            _lastConflictWarning = DateTime.Now;
            _ = _notifier.ShowAsync("Hotkey conflict", msg + " Change it or turn these warnings off in Settings.");
        };
        _hotkeys.ToggleCrosshairRequested += _crosshair.ToggleHidden;
        _hotkeys.ToggleAutoClickerRequested += _autoClicker.ToggleRun;
        _hotkeys.HoldAutoClickerRequested += _autoClicker.BeginHoldRun;
        if (Program.Instance is { } instance) instance.ShowRequested += () => Dispatcher.UIThread.Post(OpenSettings);

        // An isolated profile (OVERSIGHT_PROFILE, for UI work) must not grab the global hotkeys or drive slot switching.
        if (AppProfile.IsIsolated)
        {
            OpenSettings();
            return;
        }

        _hotkeys.Start();
        _watcher.Start();

        if (Updater.CanSelfUpdate)
        {
            AppMenu.Install(() => AssetLoader.Open(new Uri("avares://oversight/Assets/oversight.png")));
            if (Program.Args.Contains("--updated")) _ = _notifier.ShowAsync("Oversight updated", $"You're now on version {Updater.CurrentVersion}.");

            // Checks GitHub at most every 20 hours (the updater keeps track); the timer just gives it a chance to.
            var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            updateTimer.Tick += async (_, _) =>
            {
                updateTimer.Interval = TimeSpan.FromMinutes(30);
                await _updater.CheckInBackgroundAsync(u => _ = _notifier.ShowAsync($"Oversight {u.Version} is available", "Open Settings › General to install it."));
            };
            updateTimer.Start();
        }
    }

    private void UpdateTray() => _tray?.Update(_autoClicker?.IsRunning == true, _magnifier.IsActive, _magnifier.ZoomLevel);

    /// <summary>The Open settings hotkey: open it, raise it, or tuck it away if it's already the focused window.</summary>
    private void ToggleSettings()
    {
        if (_settingsWindow is { IsVisible: true, IsActive: true } w && w.WindowState != WindowState.Minimized) w.Hide();
        else OpenSettings();
    }

    private void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_config, _hotkeys, _watcher, _crosshair, _updater);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void Quit()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.SuppressUnsavedPrompt = true;
            _settingsWindow.Close();
        }
        _autoClicker?.StopRun();
        _hotkeys.Dispose();
        _watcher.Dispose();
        _tray?.Dispose();
        _crosshair.Dispose();
        _autoClicker?.Dispose();
        _magnifier.Dispose();
        _config.Save();
        Exit(0);
    }

    private void Exit(int code) => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(code);

    private void TryStopAutoClicker()
    {
        try { _autoClicker?.StopRun(); }
        catch { /* shutting down — ignore */ }
    }
}

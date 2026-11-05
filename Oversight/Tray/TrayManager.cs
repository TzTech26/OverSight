using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using Oversight.Native;
using Oversight.Services;
using Oversight.UI;
using FontStyle = System.Drawing.FontStyle;
using Point = System.Drawing.Point;

namespace Oversight.Tray;

/// <summary>
/// Manages the system-tray NotifyIcon and its context menu.
/// Uses System.Drawing to generate icons programmatically (no binary resources needed).
/// </summary>
public sealed class TrayManager : IDisposable
{
    private readonly ConfigStore        _config;
    private readonly HotkeyManager     _hotkeys;
    private readonly MagnifierService   _magnifier;
    private readonly ProcessWatcher     _watcher;
    private readonly CrosshairService   _crosshair;
    private readonly AutoClickerService _autoClicker;
    private readonly Updater            _updater;

    private readonly NotifyIcon  _tray;
    private readonly Icon        _icon;

    private SettingsWindow? _settingsWindow;
    private bool            _disposed;

    public TrayManager(ConfigStore config, HotkeyManager hotkeys,
                       MagnifierService magnifier, ProcessWatcher watcher,
                       CrosshairService crosshair, AutoClickerService autoClicker, Updater updater)
    {
        _updater     = updater;
        _config      = config;
        _hotkeys     = hotkeys;
        _magnifier   = magnifier;
        _watcher     = watcher;
        _crosshair   = crosshair;
        _autoClicker = autoClicker;

        _icon = BuildIcon();

        _tray = new NotifyIcon
        {
            Text    = AppProfile.IsIsolated ? $"Oversight ({AppProfile.Name})" : "Oversight",
            Icon    = _icon,
            Visible = false,
        };

        _tray.Click += (_, _) => ShowSettings();
        _tray.ContextMenuStrip = BuildMenu();

        _magnifier.StateChanged += OnMagnifierStateChanged;
        _autoClicker.RunStateChanged += OnAutoClickerStateChanged;
        _hotkeys.OpenSettingsRequested += ToggleSettings;

        // The WinForms menu can't observe the WPF DynamicResource palette, so rebuild
        // it with theme-matched colours whenever the system flips light/dark.
        ThemeManager.ThemeChanged += OnThemeChanged;

        // Rebuild so the slot submenus reflect added/renamed/deleted slots and the
        // active-slot check marks, whether changed from the tray or the settings window.
        _config.ProfilesChanged += OnProfilesChanged;
    }

    private void OnThemeChanged()
    {
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = BuildMenu();
        old?.Dispose();
    }

    private void OnProfilesChanged()
    {
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = BuildMenu();
        old?.Dispose();
    }

    public void Show()    => _tray.Visible = true;
    public void Hide()    => _tray.Visible = false;

    /// <summary>Shows a balloon notification from the tray icon.</summary>
    public void ShowBalloon(string title, string text)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText  = text;
        _tray.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _magnifier.StateChanged -= OnMagnifierStateChanged;
        _autoClicker.RunStateChanged -= OnAutoClickerStateChanged;
        _hotkeys.OpenSettingsRequested -= ToggleSettings;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _config.ProfilesChanged -= OnProfilesChanged;
        _tray.Visible = false;
        _tray.Dispose();
        _icon.Dispose();
        if (_settingsWindow is not null)
        {
            _settingsWindow.SuppressUnsavedPrompt = true;   // app is quitting — don't prompt
            _settingsWindow.Close();
        }
    }

    // ── Menu ──────────────────────────────────────────────────────────────────

    private ContextMenuStrip BuildMenu()
    {
        bool dark = ThemeManager.IsDarkMode;

        var menu = new ContextMenuStrip();
        menu.BackColor = dark ? Color.FromArgb(0x15, 0x14, 0x2A) : Color.FromArgb(0xFF, 0xFF, 0xFF);
        menu.ForeColor = dark ? Color.FromArgb(0xE2, 0xE8, 0xF0) : Color.FromArgb(0x1E, 0x1B, 0x2E);
        menu.Renderer  = new ThemedMenuRenderer(dark);

        var itemToggle = new ToolStripMenuItem("Toggle Zoom");
        itemToggle.Click += (_, _) => _magnifier.Toggle();

        var itemZoomSlots = BuildSlotMenu(dark, "Zoom slot",
            _config.ZoomProfileNames, _config.ActiveZoomProfileName, SwitchZoomSlot);

        var itemXhSlots = BuildSlotMenu(dark, "Crosshair",
            _config.CrosshairProfileNames, _config.ActiveCrosshairProfileName, SwitchCrosshairSlot);

        var itemAcSlots = BuildSlotMenu(dark, "Auto clicker",
            _config.AutoClickerProfileNames, _config.ActiveAutoClickerProfileName, SwitchAutoClickerSlot);

        var itemSettings = new ToolStripMenuItem("Settings…");
        itemSettings.Click += (_, _) => ShowSettings();

        var itemQuit = new ToolStripMenuItem("Quit");
        itemQuit.Click += (_, _) => QuitApp();

        menu.Items.AddRange([itemToggle, new ToolStripSeparator(),
                             itemZoomSlots, itemXhSlots, itemAcSlots,
                             new ToolStripSeparator(), itemSettings,
                             new ToolStripSeparator(), itemQuit]);
        return menu;
    }

    /// <summary>Builds a "… ▸" submenu listing save slots with a check on the active one.</summary>
    private static ToolStripMenuItem BuildSlotMenu(bool dark, string title,
        IReadOnlyList<string> names, string active, Action<string> onPick)
    {
        var root = new ToolStripMenuItem(title);

        // The submenu is a separate ToolStripDropDown, so it needs its own colours + renderer.
        root.DropDown.BackColor = dark ? Color.FromArgb(0x15, 0x14, 0x2A) : Color.FromArgb(0xFF, 0xFF, 0xFF);
        root.DropDown.ForeColor = dark ? Color.FromArgb(0xE2, 0xE8, 0xF0) : Color.FromArgb(0x1E, 0x1B, 0x2E);
        root.DropDown.Renderer  = new ThemedMenuRenderer(dark);

        foreach (var name in names)
        {
            var captured = name;
            var item = new ToolStripMenuItem(name)
            {
                Checked      = string.Equals(name, active, StringComparison.Ordinal),
                CheckOnClick = false,
            };
            item.Click += (_, _) => onPick(captured);
            root.DropDownItems.Add(item);
        }

        return root;
    }

    /// <summary>Switches the live zoom slot and refreshes the services that read settings.</summary>
    private void SwitchZoomSlot(string name)
    {
        _config.SwitchZoomProfile(name);   // raises ProfilesChanged → menu rebuild (updates the check)
        _hotkeys.ReloadBindings();
        _watcher.Reload();
    }

    /// <summary>Switches the live crosshair slot and refreshes the overlay.</summary>
    private void SwitchCrosshairSlot(string name)
    {
        _config.SwitchCrosshairProfile(name);
        _hotkeys.ReloadBindings();
        _watcher.Reload();
    }

    /// <summary>Switches the live auto-clicker slot.</summary>
    private void SwitchAutoClickerSlot(string name)
    {
        _config.SwitchAutoClickerProfile(name);
        _hotkeys.ReloadBindings();
        _watcher.Reload();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void OnMagnifierStateChanged() => UpdateTooltip();

    private void OnAutoClickerStateChanged() => UpdateTooltip();

    /// <summary>
    /// Icon stays the blue glass at all times (matches the taskbar/app icon); zoom and
    /// auto-clicker run state are reflected in the tooltip text only.
    /// </summary>
    private void UpdateTooltip()
    {
        string text = "Oversight";
        if (_autoClicker.IsRunning) text = "Oversight — Auto clicker running";
        else if (_magnifier.IsActive) text = $"Oversight — Zoom active ({_magnifier.ZoomLevel:F2}×)";

        // NotifyIcon.Text is capped at 63 chars — these are well under.
        _tray.Text = text;
    }

    /// <summary>
    /// Hotkey behaviour: open it if closed, bring it to the front if it's open
    /// but not focused (minimized or behind another window), and hide it back to
    /// the tray if it's already the foreground window.
    /// </summary>
    private void ToggleSettings()
    {
        if (_settingsWindow is null)
        {
            ShowSettings();
            return;
        }

        var hwnd = new WindowInteropHelper(_settingsWindow).Handle;
        bool isForeground = _settingsWindow.IsVisible
                         && _settingsWindow.WindowState != WindowState.Minimized
                         && NativeMethods.GetForegroundWindow() == hwnd;

        if (isForeground)
            _settingsWindow.Hide();   // tuck back into the tray
        else
            ShowSettings();           // restore / raise to top
    }

    /// <summary>Opens the settings window (creating it if needed) and forces it to the top.</summary>
    public void OpenSettings() => ShowSettings();

    private void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_config, _hotkeys, _watcher, _crosshair, _autoClicker, _updater);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            if (!_settingsWindow.IsVisible)
                _settingsWindow.Show();
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
        }

        // Reliable raise-to-front from a background (tray) process.
        _settingsWindow.Activate();
        _settingsWindow.Topmost = true;
        _settingsWindow.Topmost = false;
        _settingsWindow.Focus();
    }

    private static void QuitApp()
    {
        System.Windows.Application.Current.Shutdown();
    }

    // ── Icon generation ───────────────────────────────────────────────────────

    // Same artwork as the window / taskbar icon (Assets/app.ico), at the tray's small-icon size.
    private static Icon BuildIcon()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        using var stream = info.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    // ── Themed context menu renderer ──────────────────────────────────────────

    private sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly bool _dark;

        public ThemedMenuRenderer(bool dark) : base(new ThemedColorTable(dark)) => _dark = dark;

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var rc = new Rectangle(Point.Empty, e.Item.Size);
            Color color = e.Item.Selected
                ? (_dark ? Color.FromArgb(0x7C, 0x3A, 0xED) : Color.FromArgb(0xED, 0xE9, 0xFE))
                : (_dark ? Color.FromArgb(0x15, 0x14, 0x2A) : Color.FromArgb(0xFF, 0xFF, 0xFF));
            e.Graphics.FillRectangle(new SolidBrush(color), rc);
        }
    }

    private sealed class ThemedColorTable : ProfessionalColorTable
    {
        private readonly Color _bg;
        private readonly Color _border;
        private readonly Color _separator;

        public ThemedColorTable(bool dark)
        {
            _bg        = dark ? Color.FromArgb(0x15, 0x14, 0x2A) : Color.FromArgb(0xFF, 0xFF, 0xFF);
            _border    = dark ? Color.FromArgb(0x34, 0x31, 0x5A) : Color.FromArgb(0xD5, 0xD0, 0xE6);
            _separator = dark ? Color.FromArgb(0x24, 0x22, 0x3D) : Color.FromArgb(0xE5, 0xE2, 0xF0);
        }

        public override Color MenuBorder                 => _border;
        public override Color MenuItemBorder             => Color.Transparent;
        public override Color ToolStripDropDownBackground => _bg;
        public override Color ImageMarginGradientBegin   => _bg;
        public override Color ImageMarginGradientMiddle  => _bg;
        public override Color ImageMarginGradientEnd     => _bg;
        public override Color SeparatorDark              => _separator;
        public override Color SeparatorLight             => _separator;
    }
}

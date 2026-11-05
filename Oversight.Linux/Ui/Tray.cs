using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Oversight.Services;

namespace Oversight.Linux.Ui;

/// <summary>
/// The tray icon (StatusNotifierItem: KDE, Cinnamon, XFCE, GNOME with the AppIndicator extension Ubuntu ships), with the
/// Windows menu: Toggle Zoom, Settings, Quit. Clicking the icon opens Settings.
/// </summary>
public sealed class Tray : IDisposable
{
    private readonly TrayIcon _icon;
    private readonly string _name = AppProfile.IsIsolated ? $"Oversight ({AppProfile.Name})" : "Oversight";

    public event Action? ToggleZoomRequested;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    public Tray()
    {
        var toggle = new NativeMenuItem("Toggle Zoom");
        toggle.Click += (_, _) => ToggleZoomRequested?.Invoke();
        var settings = new NativeMenuItem("Settings…");
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => QuitRequested?.Invoke();

        _icon = new TrayIcon
        {
            ToolTipText = _name,
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://oversight/Assets/oversight.png"))),
            Menu = new NativeMenu { Items = { toggle, new NativeMenuItemSeparator(), settings, new NativeMenuItemSeparator(), quit } },
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => SettingsRequested?.Invoke();
        TrayIcon.SetIcons(Application.Current!, [_icon]);
    }

    /// <summary>Zoom and auto-clicker run state show in the tooltip, as on Windows.</summary>
    public void Update(bool autoClickerRunning, bool zoomActive, float zoomLevel) =>
        _icon.ToolTipText = autoClickerRunning ? $"{_name} — Auto clicker running"
            : zoomActive ? $"{_name} — Zoom active ({zoomLevel:F2}×)"
            : _name;

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}

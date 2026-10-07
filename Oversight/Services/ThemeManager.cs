using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace Oversight.Services;

/// <summary>
/// Keeps the app's WPF resource palette in sync with the Windows "Choose your mode"
/// (light/dark) setting and notifies listeners when it flips.
///
/// The palette lives in two swappable ResourceDictionaries (Themes/Dark.xaml,
/// Themes/Light.xaml) merged into <see cref="Application.Resources"/>; every brush is
/// referenced via DynamicResource, so swapping the merged dictionary re-themes the live
/// UI instantly. DWM title-bar colours and the WinForms tray menu can't observe
/// DynamicResource, so they subscribe to <see cref="ThemeChanged"/> and re-apply.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static ResourceDictionary? _current;
    private static Dispatcher? _dispatcher;
    private static bool _initialized;

    /// <summary>True when Windows is in dark mode (apps default to dark surfaces).</summary>
    public static bool IsDarkMode { get; private set; } = true;

    /// <summary>Raised on the UI thread after the active theme changes.</summary>
    public static event Action? ThemeChanged;

    /// <summary>
    /// Reads the current system theme, applies the matching palette, and starts
    /// listening for live changes. Call once during startup, before any window opens.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        _dispatcher = Application.Current.Dispatcher;
        IsDarkMode  = ReadSystemIsDark();
        ApplyResources();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Application.Current.Exit += (_, _) =>
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    // SystemEvents fires on its own background thread; marshal back to the UI thread.
    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // The "Choose your mode" toggle surfaces as a General preference change.
        if (e.Category != UserPreferenceCategory.General) return;

        _dispatcher?.BeginInvoke(() =>
        {
            bool dark = ReadSystemIsDark();
            if (dark == IsDarkMode) return;   // unrelated General change — ignore

            IsDarkMode = dark;
            ApplyResources();
            ThemeChanged?.Invoke();
        });
    }

    private static void ApplyResources()
    {
        var uri = new Uri(IsDarkMode
            ? "pack://application:,,,/Themes/Dark.xaml"
            : "pack://application:,,,/Themes/Light.xaml");

        var dict  = new ResourceDictionary { Source = uri };
        var dicts = Application.Current.Resources.MergedDictionaries;

        // Add the new dictionary first so DynamicResource lookups never see a gap,
        // then drop the old one.
        dicts.Add(dict);
        if (_current is not null) dicts.Remove(_current);
        _current = dict;
    }

    /// <summary>
    /// Reads HKCU…\Personalize\AppsUseLightTheme. 0 = dark, 1 = light, missing = light.
    /// </summary>
    public static bool ReadSystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue("AppsUseLightTheme") is int appsUseLight)
                return appsUseLight == 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ThemeManager] theme read failed: {ex.Message}");
        }
        return false;   // registry default when the value is absent is "light"
    }
}

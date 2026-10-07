using Microsoft.Win32;

namespace Oversight.Services;

public static class StartupManager
{
    private const string RunKey          = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName       = "Oversight";
    private const string LegacyValueName = "ScreenZoom";   // pre-rebrand entry

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            // Paths are case-insensitive on Windows, and the installer may write this value
            // with different casing (e.g. an older "OverSight" install folder).
            return key?.GetValue(ValueName) is string v &&
                   string.Equals(v, ExePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Cannot open registry Run key.");

        if (enable)
            key.SetValue(ValueName, ExePath);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// One-time rebrand migration: if the old "ScreenZoom" startup entry exists, remove it
    /// and carry its enabled-ness over to the new "Oversight" entry (pointing at this exe).
    /// Safe to call on every launch.
    /// </summary>
    public static void MigrateLegacyEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(LegacyValueName) is null) return;

            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            key.SetValue(ValueName, ExePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Startup] legacy migration failed: {ex.Message}");
        }
    }

    private static string ExePath =>
        $"\"{Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location}\"";
}

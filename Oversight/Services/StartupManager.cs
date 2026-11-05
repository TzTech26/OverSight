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
        $"\"{Environment.ProcessPath}\"";
}

/// <summary>
/// A Start menu entry, so Oversight shows up in Windows search. The app has no installer, so the shortcut is written on
/// every start and follows the exe if you move it.
/// </summary>
public static class StartMenu
{
    private static string ShortcutPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Oversight.lnk");

    public static void Install()
    {
        if (Environment.ProcessPath is not string exe) return;
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(exe);
        link.SetWorkingDirectory(System.IO.Path.GetDirectoryName(exe)!);
        link.SetDescription("Screen zoom, crosshair overlay and auto clicker");
        link.SetIconLocation(exe, 0);
        ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(ShortcutPath, true);
        System.Runtime.InteropServices.Marshal.ReleaseComObject(link);
    }

    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("000214F9-0000-0000-C000-000000000046"),
     System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder file, int max, IntPtr data, int flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder name, int max);
        void SetDescription([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int max);
        void SetWorkingDirectory([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string dir);
        void GetArguments([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder args, int max);
        void SetArguments([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] System.Text.StringBuilder path, int max, out int index);
        void SetIconLocation([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string file);
    }
}

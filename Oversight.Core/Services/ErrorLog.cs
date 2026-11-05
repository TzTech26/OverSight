namespace Oversight.Services;

public static class ErrorLog
{
    /// <summary>
    /// Appends an unhandled exception to error.log in the config folder (trimmed to the last
    /// ~256 KB) so problems swallowed by the keep-alive handler can still be diagnosed. Never throws.
    /// </summary>
    public static void Write(object? error)
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
}

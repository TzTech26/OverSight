namespace Oversight.Services;

/// <summary>
/// Optional isolated run profile, selected with the OVERSIGHT_PROFILE environment variable
/// (e.g. OVERSIGHT_PROFILE=dev). A profile gets its own config folder (%APPDATA%\Oversight-dev)
/// and its own single-instance lock, skips global hotkeys / auto-switching / registry
/// migration, and opens Settings on launch — so a development build can run beside a
/// normal install without touching its settings or fighting over its hotkeys.
/// </summary>
public static class AppProfile
{
    /// <summary>The profile name, or null for a normal run.</summary>
    public static string? Name { get; } = Normalize(Environment.GetEnvironmentVariable("OVERSIGHT_PROFILE"));

    public static bool IsIsolated => Name is not null;

    /// <summary>"" for a normal run, "-dev" for profile "dev".</summary>
    public static string Suffix => Name is null ? "" : "-" + Name;

    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = new string(raw.Trim().Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return clean.Length == 0 ? null : clean;
    }
}

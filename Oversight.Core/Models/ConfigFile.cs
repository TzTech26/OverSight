namespace Oversight.Models;

/// <summary>
/// On-disk root of settings.json (schema v5). Zoom, crosshair and auto-clicker have independent
/// save-slot lists; the Game Profiles rules pick one of each per app. The "Open settings" hotkey
/// is app-wide and always active.
///
/// Older schemas (v4 without auto-clicker, v2 profile-aware, v3 auto-switch, and the original
/// bare-AppSettings file) are detected and migrated by <see cref="Services.ConfigStore.Load"/> —
/// a v4 file deserializes straight into this shape and its auto-clicker defaults are backfilled by
/// <c>EnsureValid</c>. See also <see cref="LegacyConfigFile"/>.
/// </summary>
public class ConfigFile
{
    /// <summary>Schema version (5 = split zoom/crosshair/auto-clicker save slots).</summary>
    public int Version { get; set; } = 5;

    public List<ZoomProfile>       ZoomProfiles       { get; set; } = [];
    public List<CrosshairProfile>  CrosshairProfiles  { get; set; } = [];
    public List<AutoClickerProfile> AutoClickerProfiles { get; set; } = [];

    /// <summary>Name of the zoom slot that is currently live.</summary>
    public string ActiveZoomProfile { get; set; } = "Default";

    /// <summary>Name of the crosshair slot that is currently live.</summary>
    public string ActiveCrosshairProfile { get; set; } = string.Empty;

    /// <summary>Name of the auto-clicker slot that is currently live.</summary>
    public string ActiveAutoClickerProfile { get; set; } = string.Empty;

    /// <summary>Per-app rules: zoom slot + crosshair slot + auto-clicker slot (each empty = off) per process.</summary>
    public List<AppRule> AppRules { get; set; } = [];

    /// <summary>Zoom slot used when no listed app is focused. Null/empty = zoom off there.</summary>
    public string? DefaultZoomProfile { get; set; }

    /// <summary>Crosshair slot used when no listed app is focused. Null/empty = no crosshair there.</summary>
    public string? DefaultCrosshairProfile { get; set; }

    /// <summary>Auto-clicker slot used when no listed app is focused. Null/empty = auto-clicker off there (the default).</summary>
    public string? DefaultAutoClickerProfile { get; set; }

    /// <summary>App-wide "Open settings" hotkey — registered regardless of the per-app rules.</summary>
    public HotkeyBinding? OpenSettingsHotkey { get; set; }

    /// <summary>App-wide update preferences (General page).</summary>
    public UpdateSettings Updates { get; set; } = new();

    /// <summary>Notify when another app already uses one of the hotkeys.</summary>
    public bool ShowHotkeyWarnings { get; set; } = true;
}

public class UpdateSettings
{
    public bool CheckForUpdates { get; set; } = true;
    public bool InstallAutomatically { get; set; } = true;
    public DateTime? LastCheck { get; set; }
}

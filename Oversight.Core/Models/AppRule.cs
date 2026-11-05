using System.Text.Json.Serialization;

namespace Oversight.Models;

/// <summary>
/// A Game Profiles rule: while the given app is in the foreground, Oversight activates the
/// chosen zoom slot and crosshair slot independently. Either being empty/null means "off"
/// for that feature while the app is focused.
/// </summary>
public class AppRule
{
    /// <summary>Process name without the .exe extension (case-insensitive match key).</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>Friendly name shown in the UI (window title / Steam name); falls back to the process name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Zoom save slot to activate for this app. Empty = zoom off for this app.</summary>
    public string ZoomProfile { get; set; } = string.Empty;

    /// <summary>Crosshair save slot to show for this app. Empty = no crosshair for this app.</summary>
    public string CrosshairProfile { get; set; } = string.Empty;

    /// <summary>Auto-clicker save slot to arm for this app. Empty = auto-clicker off for this app.</summary>
    public string AutoClickerProfile { get; set; } = string.Empty;

    /// <summary>Full path of the app's executable, for its icon. Null in rules saved before 2.6.0.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExePath { get; set; }

    /// <summary>Steam app id when the rule was added from the Steam library, for Steam's cached icon.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SteamAppId { get; set; }

    public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayName) ? ProcessName : DisplayName;
}

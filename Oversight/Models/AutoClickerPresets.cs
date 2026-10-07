namespace Oversight.Models;

/// <summary>
/// The built-in auto-clicker slots, seeded on first run and during migration from an older
/// schema. They are never active until the user assigns one to an app in Game Profiles and
/// presses its start/stop bind.
/// </summary>
public static class AutoClickerPresets
{
    /// <summary>Fresh copies of the default auto-clicker save slots.</summary>
    public static List<AutoClickerProfile> DefaultProfiles() =>
    [
        new()
        {
            Name            = "Default",
            Settings        = new AutoClickerSettings(),   // left mouse, 100 ms, until stopped
            StartStopHotkey = AutoClickerProfile.DefaultStartStopHotkey(),
        },
    ];
}

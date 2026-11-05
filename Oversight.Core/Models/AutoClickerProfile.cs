namespace Oversight.Models;

/// <summary>A named auto-clicker save slot: the behaviour plus its start/stop keybind.</summary>
public class AutoClickerProfile
{
    public string Name { get; set; } = "Default";

    public AutoClickerSettings Settings { get; set; } = new();

    /// <summary>
    /// The "Start / stop auto-clicker" keybind, saved with the slot. Keyboard-only — it goes
    /// through RegisterHotKey (mouse buttons can't), and the interval / hold behaviour is
    /// decided by <see cref="AutoClickerSettings.Activation"/>.
    /// </summary>
    public HotkeyBinding StartStopHotkey { get; set; } = DefaultStartStopHotkey();

    public static HotkeyBinding DefaultStartStopHotkey() => new()
    {
        // Ctrl+Alt+C — matches the crosshair's Ctrl+Alt+X convention and avoids the
        // commonly-taken Ctrl+Shift combos.
        Modifiers  = VirtualKeys.MOD_CONTROL | VirtualKeys.MOD_ALT,
        VirtualKey = VirtualKeys.VK_C,
    };
}

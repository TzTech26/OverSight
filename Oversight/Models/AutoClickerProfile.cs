using System.Windows.Input;
using Oversight.Native;

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
        Modifiers  = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT,
        VirtualKey = (uint)KeyInterop.VirtualKeyFromKey(Key.C),
    };
}

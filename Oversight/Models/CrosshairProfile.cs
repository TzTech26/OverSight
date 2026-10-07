using System.Windows.Input;
using Oversight.Native;

namespace Oversight.Models;

/// <summary>A named crosshair save slot: the style plus its show/hide keybind.</summary>
public class CrosshairProfile
{
    public string Name { get; set; } = "Classic cross";

    public CrosshairSettings Style { get; set; } = new();

    /// <summary>The "Show/hide crosshair" keybind, saved with the slot.</summary>
    public HotkeyBinding ToggleHotkey { get; set; } = DefaultToggleHotkey();

    /// <summary>
    /// Optional "hide while held" keybind (e.g. aiming down sights with the game's own
    /// reticle) — hides the crosshair only while the key is physically down and restores it
    /// on release. Unbound by default. If the crosshair is already hidden via
    /// <see cref="ToggleHotkey"/>, pressing and releasing this key leaves it hidden — see
    /// <see cref="Services.CrosshairService"/>.
    /// </summary>
    public HotkeyBinding HoldHideHotkey { get; set; } = new();

    public static HotkeyBinding DefaultToggleHotkey() => new()
    {
        // Ctrl+Alt+X — Ctrl+Shift+X is commonly taken by other tools.
        Modifiers  = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT,
        VirtualKey = (uint)KeyInterop.VirtualKeyFromKey(Key.X),
    };
}

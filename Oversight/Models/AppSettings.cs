namespace Oversight.Models;

/// <summary>
/// The live, in-memory settings the services read every frame. Not serialized as a whole:
/// the store composes it from the active zoom slot, the active crosshair slot and the global
/// "Open settings" hotkey, and re-fills it in place whenever a slot switches.
///
/// <para><b>Live instance invariant.</b> The services (magnifier, hotkeys, crosshair,
/// auto-clicker) capture this object — and its <see cref="Zoom"/> / <see cref="Crosshair"/> /
/// <see cref="AutoClicker"/> / <see cref="Hotkeys"/> members — by reference once at startup, so
/// they must remain the <i>same objects</i> for the app's lifetime. Slot switches copy values
/// <i>into</i> them, never replace them.</para>
/// </summary>
public class AppSettings
{
    /// <summary>Live zoom behaviour (mirrors the active zoom save slot).</summary>
    public ZoomSettings Zoom { get; } = new();

    /// <summary>Live crosshair style (mirrors the active crosshair save slot).</summary>
    public CrosshairSettings Crosshair { get; } = new();

    /// <summary>Live auto-clicker behaviour (mirrors the active auto-clicker save slot).</summary>
    public AutoClickerSettings AutoClicker { get; } = new();

    /// <summary>
    /// Merged keybind view the hotkey manager registers from: ToggleZoom / ZoomIn / ZoomOut
    /// come from the active zoom slot, ToggleCrosshair from the active crosshair slot,
    /// AutoClickerStartStop from the active auto-clicker slot, and OpenSettings from the
    /// app-wide setting.
    /// </summary>
    public Dictionary<string, HotkeyBinding> Hotkeys { get; } = [];
}

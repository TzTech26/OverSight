using System.Windows.Input;
using Oversight.Native;

namespace Oversight.Models;

/// <summary>
/// One zoom save slot: everything about how the magnifier behaves, plus the zoom keybinds
/// (which travel with the slot, so each game's zoom profile can use different keys).
/// The crosshair lives in its own independent slot type — see <see cref="CrosshairSettings"/>.
/// </summary>
public class ZoomSettings
{
    // ── Zoom keybinds (saved with this slot) ──────────────────────────────────

    public Dictionary<string, HotkeyBinding> Hotkeys { get; set; } = new()
    {
        ["ToggleZoom"] = new HotkeyBinding { Modifiers = 0,                        VirtualKey = (uint)KeyInterop.VirtualKeyFromKey(Key.F12) },
        ["ZoomIn"]     = new HotkeyBinding { Modifiers = NativeMethods.MOD_CONTROL, VirtualKey = (uint)KeyInterop.VirtualKeyFromKey(Key.OemPlus) },
        ["ZoomOut"]    = new HotkeyBinding { Modifiers = NativeMethods.MOD_CONTROL, VirtualKey = (uint)KeyInterop.VirtualKeyFromKey(Key.OemMinus) },
    };

    // ── Zoom ──────────────────────────────────────────────────────────────────

    public float DefaultZoomLevel { get; set; } = 2.0f;
    public float ZoomStep        { get; set; } = 0.25f;
    public float MinZoom         { get; set; } = 1.25f;
    public float MaxZoom         { get; set; } = 16.0f;

    /// <summary>
    /// How the "Toggle zoom" hotkey behaves: <see cref="ActivationMode.Toggle"/> flips zoom
    /// on/off on each press; <see cref="ActivationMode.Hold"/> keeps zoom on only while the
    /// key is held down and turns it off on release.
    /// </summary>
    public ActivationMode ToggleZoomMode { get; set; } = ActivationMode.Toggle;

    /// <summary>Magnification engine. Only Fullscreen ships; coerced on load (see ConfigStore).</summary>
    public MagnifierEngine Engine { get; set; } = MagnifierEngine.Fullscreen;

    /// <summary>Which display(s) the windowed magnifier follows (ignored by the fullscreen engine).</summary>
    public MonitorMode MonitorMode { get; set; } = MonitorMode.ActiveMonitor;

    /// <summary>When true, turning zoom on ramps smoothly from 1× up to the target level.</summary>
    public bool ZoomInAnimationEnabled { get; set; } = true;

    /// <summary>Duration of the zoom-in ramp, in milliseconds (0 = instant). Range 0–500 in the UI.</summary>
    public int ZoomAnimationMs { get; set; } = 150;

    /// <summary>When true, turning zoom off ramps smoothly back down to 1× before hiding.</summary>
    public bool ZoomOutAnimationEnabled { get; set; } = true;

    /// <summary>Duration of the zoom-out ramp, in milliseconds (0 = instant). Range 0–500 in the UI.</summary>
    public int ZoomOutAnimationMs { get; set; } = 150;

    /// <summary>
    /// When true, the lens eases toward the cursor instead of tracking it 1:1. This smooths
    /// fast cursor movement and monitor hand-offs. When false the lens follows exactly.
    /// </summary>
    public bool SmoothPanEnabled { get; set; } = false;

    /// <summary>
    /// Lens follow smoothing strength as a settling time in milliseconds. Lower = snappier,
    /// higher = floatier. Range 0–400 in the UI. Only used when <see cref="SmoothPanEnabled"/>.
    /// </summary>
    public int PanSmoothingMs { get; set; } = 120;

    // ── Copy helpers ──────────────────────────────────────────────────────────

    public ZoomSettings Clone()
    {
        var dst = new ZoomSettings();
        dst.CopyFrom(this);
        return dst;
    }

    public void CopyFrom(ZoomSettings src)
    {
        DefaultZoomLevel        = src.DefaultZoomLevel;
        ZoomStep                = src.ZoomStep;
        MinZoom                 = src.MinZoom;
        MaxZoom                 = src.MaxZoom;
        ToggleZoomMode          = src.ToggleZoomMode;
        Engine                  = src.Engine;
        MonitorMode             = src.MonitorMode;
        ZoomInAnimationEnabled  = src.ZoomInAnimationEnabled;
        ZoomAnimationMs         = src.ZoomAnimationMs;
        ZoomOutAnimationEnabled = src.ZoomOutAnimationEnabled;
        ZoomOutAnimationMs      = src.ZoomOutAnimationMs;
        SmoothPanEnabled        = src.SmoothPanEnabled;
        PanSmoothingMs          = src.PanSmoothingMs;

        Hotkeys = src.Hotkeys.ToDictionary(
            kv => kv.Key,
            kv => new HotkeyBinding { Modifiers = kv.Value.Modifiers, VirtualKey = kv.Value.VirtualKey });
    }
}

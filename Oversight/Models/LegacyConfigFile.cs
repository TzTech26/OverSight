namespace Oversight.Models;

// DTOs used only to parse pre-v4 settings.json files so they can be migrated to the split
// zoom/crosshair slot schema. Kept intentionally loose: unknown fields are ignored and every
// property has a safe default.

/// <summary>Root of a v2/v3 config file (one combined profile list + auto-switch rules).</summary>
public class LegacyConfigFile
{
    public int Version { get; set; } = 3;
    public string ActiveProfile { get; set; } = "Default";
    public List<LegacyProfile> Profiles { get; set; } = [];
    public List<LegacyAppAssignment>? AppAssignments { get; set; }
    public string? DefaultProfile { get; set; }
}

public class LegacyProfile
{
    public string Name { get; set; } = "Default";
    public LegacyAppSettings Settings { get; set; } = new();
}

/// <summary>The old all-in-one per-profile settings blob.</summary>
public class LegacyAppSettings
{
    public Dictionary<string, HotkeyBinding> Hotkeys { get; set; } = [];

    public float DefaultZoomLevel { get; set; } = 2.0f;
    public float ZoomStep        { get; set; } = 0.25f;
    public float MinZoom         { get; set; } = 1.25f;
    public float MaxZoom         { get; set; } = 16.0f;

    public ActivationMode  ToggleZoomMode { get; set; } = ActivationMode.Toggle;
    public MagnifierEngine Engine         { get; set; } = MagnifierEngine.Fullscreen;
    public MonitorMode     MonitorMode    { get; set; } = MonitorMode.ActiveMonitor;

    public bool ZoomInAnimationEnabled  { get; set; } = true;
    public int  ZoomAnimationMs         { get; set; } = 150;
    public bool ZoomOutAnimationEnabled { get; set; } = true;
    public int  ZoomOutAnimationMs      { get; set; } = 150;
    public bool SmoothPanEnabled        { get; set; } = false;
    public int  PanSmoothingMs          { get; set; } = 120;

    public LegacyCrosshair? Crosshair { get; set; }

    public bool ProcessAwareActivation { get; set; } = false;
    public List<WhitelistEntry> Whitelist { get; set; } = [];

    /// <summary>Extracts the fields that now live in a zoom save slot.</summary>
    public ZoomSettings ToZoomSettings()
    {
        var z = new ZoomSettings
        {
            DefaultZoomLevel        = DefaultZoomLevel,
            ZoomStep                = ZoomStep,
            MinZoom                 = MinZoom,
            MaxZoom                 = MaxZoom,
            ToggleZoomMode          = ToggleZoomMode,
            Engine                  = Engine,
            MonitorMode             = MonitorMode,
            ZoomInAnimationEnabled  = ZoomInAnimationEnabled,
            ZoomAnimationMs         = ZoomAnimationMs,
            ZoomOutAnimationEnabled = ZoomOutAnimationEnabled,
            ZoomOutAnimationMs      = ZoomOutAnimationMs,
            SmoothPanEnabled        = SmoothPanEnabled,
            PanSmoothingMs          = PanSmoothingMs,
        };
        foreach (var name in new[] { "ToggleZoom", "ZoomIn", "ZoomOut" })
            if (Hotkeys.TryGetValue(name, out var b))
                z.Hotkeys[name] = new HotkeyBinding { Modifiers = b.Modifiers, VirtualKey = b.VirtualKey };
        return z;
    }
}

/// <summary>Old crosshair blob: the current style fields plus the retired Enabled flag.</summary>
public class LegacyCrosshair : CrosshairSettings
{
    public bool Enabled { get; set; } = false;
}

/// <summary>Old auto-switch rule (one combined profile per app).</summary>
public class LegacyAppAssignment
{
    public string ProcessName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
}

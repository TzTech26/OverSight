namespace Oversight.Models;

/// <summary>
/// The built-in crosshair styles. These are seeded as the default crosshair <b>save slots</b>
/// on first run (and during migration from the old schema), so the user can assign them to
/// games straight away or use one as a starting point and tweak it.
/// </summary>
public static class CrosshairPresets
{
    /// <summary>Fresh copies of the default crosshair save slots.</summary>
    public static List<CrosshairProfile> DefaultProfiles() =>
    [
        Slot("Classic cross",          new() { Shape = CrosshairShape.Cross,       Size = 28, Thickness = 2, Gap = 6,  ColorHex = "#00FF40", Opacity = 90,  Outline = true }),
        Slot("Fine cross",             new() { Shape = CrosshairShape.Cross,       Size = 18, Thickness = 1, Gap = 3,  ColorHex = "#00E5FF", Opacity = 100, Outline = true }),
        Slot("Heavy cross",            new() { Shape = CrosshairShape.Cross,       Size = 40, Thickness = 4, Gap = 8,  ColorHex = "#FFFFFF", Opacity = 85,  Outline = true }),
        Slot("Cross + dot",            new() { Shape = CrosshairShape.Cross,       Size = 28, Thickness = 2, Gap = 8,  ColorHex = "#00FF40", Opacity = 90,  Outline = true, CenterDot = true }),
        Slot("Dot",                    new() { Shape = CrosshairShape.Dot,         Size = 12, Thickness = 3, Gap = 0,  ColorHex = "#FF3030", Opacity = 100, Outline = true }),
        Slot("Small dot",              new() { Shape = CrosshairShape.Dot,         Size = 8,  Thickness = 2, Gap = 0,  ColorHex = "#00FF40", Opacity = 100, Outline = true }),
        Slot("Circle",                 new() { Shape = CrosshairShape.Circle,      Size = 30, Thickness = 2, Gap = 0,  ColorHex = "#FFEB3B", Opacity = 85,  Outline = true }),
        Slot("Circle + dot",           new() { Shape = CrosshairShape.CircleDot,   Size = 32, Thickness = 2, Gap = 0,  ColorHex = "#FF3030", Opacity = 90,  Outline = true }),
        Slot("Scope (circle + cross)", new() { Shape = CrosshairShape.CircleCross, Size = 48, Thickness = 2, Gap = 8,  ColorHex = "#00FF40", Opacity = 85,  Outline = true }),
        Slot("X cross",                new() { Shape = CrosshairShape.XCross,      Size = 28, Thickness = 2, Gap = 6,  ColorHex = "#FF00FF", Opacity = 90,  Outline = true }),
        Slot("T cross",                new() { Shape = CrosshairShape.TCross,      Size = 28, Thickness = 2, Gap = 6,  ColorHex = "#00FF40", Opacity = 90,  Outline = true }),
    ];

    private static CrosshairProfile Slot(string name, CrosshairSettings style) => new()
    {
        Name         = name,
        Style        = style,
        ToggleHotkey = CrosshairProfile.DefaultToggleHotkey(),
    };
}

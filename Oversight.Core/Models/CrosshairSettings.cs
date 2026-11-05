namespace Oversight.Models;

/// <summary>Shape drawn by the crosshair overlay.</summary>
public enum CrosshairShape
{
    Cross,        // classic "+"
    XCross,       // diagonal "×"
    TCross,       // "+" without the top arm
    Dot,          // filled centre dot
    Circle,       // ring
    CircleDot,    // ring + centre dot
    CircleCross,  // ring + "+" arms (scope style)
}

/// <summary>
/// The style stored in one crosshair save slot. Whether (and where) a crosshair actually
/// appears is decided by the Game Profiles rules, which pick a crosshair slot — or off —
/// per app; the style itself carries no enable flag.
/// </summary>
public class CrosshairSettings
{
    public CrosshairShape Shape { get; set; } = CrosshairShape.Cross;

    /// <summary>Overall span (width/height) of the crosshair in physical pixels.</summary>
    public int Size { get; set; } = 28;

    /// <summary>Line thickness in pixels. Also scales the dot shapes.</summary>
    public int Thickness { get; set; } = 2;

    /// <summary>Empty space at the centre (full width, pixels) for the cross-style shapes.</summary>
    public int Gap { get; set; } = 6;

    /// <summary>Adds a small dot at the exact centre of the cross/circle shapes.</summary>
    public bool CenterDot { get; set; } = false;

    /// <summary>Colour as "#RRGGBB".</summary>
    public string ColorHex { get; set; } = "#00FF40";

    /// <summary>Overall opacity, 10–100 %.</summary>
    public int Opacity { get; set; } = 90;

    /// <summary>Draws a thin black outline under the shape so it stays visible on any background.</summary>
    public bool Outline { get; set; } = true;

    public CrosshairSettings Clone()
    {
        var dst = new CrosshairSettings();
        dst.CopyFrom(this);
        return dst;
    }

    public void CopyFrom(CrosshairSettings src)
    {
        Shape     = src.Shape;
        Size      = src.Size;
        Thickness = src.Thickness;
        Gap       = src.Gap;
        CenterDot = src.CenterDot;
        ColorHex  = src.ColorHex;
        Opacity   = src.Opacity;
        Outline   = src.Outline;
    }
}

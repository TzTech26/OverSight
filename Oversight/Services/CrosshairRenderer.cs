using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Oversight.Models;

namespace Oversight.Services;

/// <summary>
/// Draws a <see cref="CrosshairSettings"/> style into a 32-bit ARGB bitmap (transparent
/// background, anti-aliased). Shared by the on-screen overlay and the settings-window
/// preview so both always look identical. Sizes are physical pixels.
/// </summary>
internal static class CrosshairRenderer
{
    public static Bitmap Render(CrosshairSettings s)
    {
        int size = Math.Clamp(s.Size, 4, 256);
        int th   = Math.Clamp(s.Thickness, 1, 16);
        int gap  = Math.Clamp(s.Gap, 0, size);

        // Padding leaves room for the outline pass and anti-aliased edges.
        int pad = th + 4;
        int dim = size + pad * 2;

        var bmp = new Bitmap(dim, dim, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode   = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        byte  alpha = (byte)Math.Clamp(s.Opacity * 255 / 100, 25, 255);
        Color color = ParseColor(s.ColorHex, alpha);

        float c = dim / 2f;

        // Dot diameter follows the style thickness (not the pass thickness), so the outline
        // pass yields a crisp 1px ring instead of a fat halo.
        float dotD = Math.Max(3, th * 3);

        // Outline pass: the same shape grown by 1px on every edge, in black underneath.
        if (s.Outline)
            DrawShape(g, s, c, size, th, gap, dotD, Color.FromArgb(alpha, 0, 0, 0), 1);
        DrawShape(g, s, c, size, th, gap, dotD, color, 0);

        return bmp;
    }

    /// <summary>Draws the shape from the style geometry, grown by <paramref name="grow"/> px on every edge.</summary>
    private static void DrawShape(Graphics g, CrosshairSettings s, float c,
                                  int size, int th, int gap, float dotD, Color color, int grow)
    {
        float half = size / 2f;
        float g0   = Math.Min(gap / 2f, half);   // arm inner end (gap half-width)
        dotD += 2 * grow;

        using var pen   = new Pen(color, th + 2 * grow) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
        using var brush = new SolidBrush(color);

        // Whether this shape already includes a dot of its own (the Centre-dot option is
        // then redundant and skipped).
        bool hasOwnDot = s.Shape is CrosshairShape.Dot or CrosshairShape.CircleDot;

        switch (s.Shape)
        {
            case CrosshairShape.Cross:
                DrawArm(g, pen, c, c, 0, -1, g0, half, grow);
                DrawArm(g, pen, c, c, 0, +1, g0, half, grow);
                DrawArm(g, pen, c, c, -1, 0, g0, half, grow);
                DrawArm(g, pen, c, c, +1, 0, g0, half, grow);
                break;

            case CrosshairShape.XCross:
            {
                const float d = 0.70710678f;   // cos 45°
                DrawArm(g, pen, c, c, -d, -d, g0, half, grow);
                DrawArm(g, pen, c, c, +d, -d, g0, half, grow);
                DrawArm(g, pen, c, c, -d, +d, g0, half, grow);
                DrawArm(g, pen, c, c, +d, +d, g0, half, grow);
                break;
            }

            case CrosshairShape.TCross:
                DrawArm(g, pen, c, c, 0, +1, g0, half, grow);
                DrawArm(g, pen, c, c, -1, 0, g0, half, grow);
                DrawArm(g, pen, c, c, +1, 0, g0, half, grow);
                break;

            case CrosshairShape.Dot:
            {
                float d = Math.Max(3, size / 2f + th - 2) + 2 * grow;   // Size drives the lone dot
                g.FillEllipse(brush, c - d / 2, c - d / 2, d, d);
                break;
            }

            case CrosshairShape.Circle:
                DrawRing(g, pen, c, size - th);
                break;

            case CrosshairShape.CircleDot:
                DrawRing(g, pen, c, size - th);
                g.FillEllipse(brush, c - dotD / 2, c - dotD / 2, dotD, dotD);
                break;

            case CrosshairShape.CircleCross:
                // Scope style: smaller ring, arms run from the gap out to the full size.
                DrawRing(g, pen, c, size * 2f / 3f);
                DrawArm(g, pen, c, c, 0, -1, g0, half, grow);
                DrawArm(g, pen, c, c, 0, +1, g0, half, grow);
                DrawArm(g, pen, c, c, -1, 0, g0, half, grow);
                DrawArm(g, pen, c, c, +1, 0, g0, half, grow);
                break;
        }

        // Optional centre dot for the shapes that don't already have one.
        if (s.CenterDot && !hasOwnDot)
            g.FillEllipse(brush, c - dotD / 2, c - dotD / 2, dotD, dotD);
    }

    /// <summary>Draws one arm from (cx,cy) + dir·inner to (cx,cy) + dir·outer, extended by grow at both ends.</summary>
    private static void DrawArm(Graphics g, Pen pen, float cx, float cy,
                                float dx, float dy, float inner, float outer, int grow)
    {
        if (outer - inner < 1f) return;   // gap swallowed the whole arm
        inner -= grow;
        outer += grow;
        g.DrawLine(pen, cx + dx * inner, cy + dy * inner, cx + dx * outer, cy + dy * outer);
    }

    private static void DrawRing(Graphics g, Pen pen, float c, float diameter)
    {
        float d = Math.Max(3, diameter);
        g.DrawEllipse(pen, c - d / 2, c - d / 2, d, d);
    }

    /// <summary>Parses "#RRGGBB" (leading '#' optional); falls back to green on bad input.</summary>
    public static Color ParseColor(string? hex, byte alpha)
    {
        var t = (hex ?? "").TrimStart('#');
        if (t.Length == 6 &&
            int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out int rgb))
        {
            return Color.FromArgb(alpha, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }
        return Color.FromArgb(alpha, 0x00, 0xFF, 0x40);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Oversight.Linux.Platform;
using Oversight.Models;
using Oversight.Services;

namespace Oversight.Linux.Services;

/// <summary>
/// Draws a <see cref="CrosshairSettings"/> style, the same shapes as the Windows CrosshairRenderer. Sizes are physical
/// pixels; <c>scale</c> converts them to the control's device-independent units.
/// </summary>
public static class CrosshairRenderer
{
    /// <summary>Width and height of the drawing in physical pixels, padding for the outline included.</summary>
    public static int Dim(CrosshairSettings s) => Math.Clamp(s.Size, 4, 256) + (Math.Clamp(s.Thickness, 1, 16) + 4) * 2;

    public static void Draw(DrawingContext dc, CrosshairSettings s, Point center, double scale)
    {
        int size = Math.Clamp(s.Size, 4, 256);
        int th = Math.Clamp(s.Thickness, 1, 16);
        int gap = Math.Clamp(s.Gap, 0, size);
        byte alpha = (byte)Math.Clamp(s.Opacity * 255 / 100, 25, 255);
        float dotD = Math.Max(3, th * 3);

        using (dc.PushTransform(Matrix.CreateScale(1 / scale, 1 / scale) * Matrix.CreateTranslation(center.X, center.Y)))
        {
            // Outline pass: the same shape grown by 1px on every edge, in black underneath.
            if (s.Outline) DrawShape(dc, s, size, th, gap, dotD, Color.FromArgb(alpha, 0, 0, 0), 1);
            DrawShape(dc, s, size, th, gap, dotD, ParseColor(s.ColorHex, alpha), 0);
        }
    }

    /// <summary>Draws the shape from the style geometry, grown by <paramref name="grow"/> px on every edge.</summary>
    private static void DrawShape(DrawingContext dc, CrosshairSettings s, int size, int th, int gap, float dotD, Color color, int grow)
    {
        float half = size / 2f;
        float g0 = Math.Min(gap / 2f, half);
        dotD += 2 * grow;
        var pen = new Pen(new SolidColorBrush(color), th + 2 * grow, lineCap: PenLineCap.Flat);
        var brush = new SolidColorBrush(color);
        bool hasOwnDot = s.Shape is CrosshairShape.Dot or CrosshairShape.CircleDot;
        const float d = 0.70710678f;

        switch (s.Shape)
        {
            case CrosshairShape.Cross:
                Arms(dc, pen, g0, half, grow, (0, -1), (0, 1), (-1, 0), (1, 0));
                break;
            case CrosshairShape.XCross:
                Arms(dc, pen, g0, half, grow, (-d, -d), (d, -d), (-d, d), (d, d));
                break;
            case CrosshairShape.TCross:
                Arms(dc, pen, g0, half, grow, (0, 1), (-1, 0), (1, 0));
                break;
            case CrosshairShape.Dot:
                Dot(dc, brush, Math.Max(3, size / 2f + th - 2) + 2 * grow);   // Size drives the lone dot
                break;
            case CrosshairShape.Circle:
                Ring(dc, pen, size - th);
                break;
            case CrosshairShape.CircleDot:
                Ring(dc, pen, size - th);
                Dot(dc, brush, dotD);
                break;
            case CrosshairShape.CircleCross:
                Ring(dc, pen, size * 2f / 3f);
                Arms(dc, pen, g0, half, grow, (0, -1), (0, 1), (-1, 0), (1, 0));
                break;
        }
        if (s.CenterDot && !hasOwnDot) Dot(dc, brush, dotD);
    }

    /// <summary>Draws each arm from dir·inner to dir·outer, extended by grow at both ends.</summary>
    private static void Arms(DrawingContext dc, IPen pen, float inner, float outer, int grow, params (float X, float Y)[] dirs)
    {
        if (outer - inner < 1f) return;   // gap swallowed the whole arm
        inner -= grow;
        outer += grow;
        foreach (var (x, y) in dirs) dc.DrawLine(pen, new Point(x * inner, y * inner), new Point(x * outer, y * outer));
    }

    private static void Ring(DrawingContext dc, IPen pen, float diameter)
    {
        float r = Math.Max(3, diameter) / 2;
        dc.DrawEllipse(null, pen, new Point(0, 0), r, r);
    }

    private static void Dot(DrawingContext dc, IBrush brush, float diameter) =>
        dc.DrawEllipse(brush, null, new Point(0, 0), diameter / 2, diameter / 2);

    /// <summary>Parses "#RRGGBB" (leading '#' optional); falls back to green on bad input.</summary>
    public static Color ParseColor(string? hex, byte alpha)
    {
        var t = (hex ?? "").TrimStart('#');
        return t.Length == 6 && int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out int rgb)
            ? Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : Color.FromArgb(alpha, 0x00, 0xFF, 0x40);
    }
}

/// <summary>A control that shows one crosshair style centred in its bounds (overlay and Settings preview).</summary>
public sealed class CrosshairView : Control
{
    private CrosshairSettings? _style;

    public CrosshairSettings? Style
    {
        get => _style;
        set { _style = value; InvalidateVisual(); }
    }

    public override void Render(DrawingContext dc)
    {
        if (_style is null) return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        CrosshairRenderer.Draw(dc, _style, new Point(Bounds.Width / 2, Bounds.Height / 2), scale);
    }
}

/// <summary>
/// The on-screen crosshair: a small borderless, transparent, always-on-top window the size of the crosshair, kept out
/// of the taskbar, with an empty input shape so every click goes through to the game.
/// </summary>
public sealed class CrosshairOverlay : Window
{
    private readonly CrosshairView _view = new();

    public CrosshairOverlay()
    {
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Focusable = false;
        Title = "Oversight crosshair";
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Content = _view;
        Opened += (_, _) => ClickThrough();
        ClickThrough();
    }

    /// <summary>The X11 window id (0 if Avalonia isn't on X11).</summary>
    public nint XWindow => TryGetPlatformHandle()?.Handle ?? 0;

    private void ClickThrough()
    {
        if (XWindow != 0 && X11.Available) X11.MakeClickThrough(X11.Display, XWindow);
    }

    /// <summary>Shows <paramref name="style"/> centred on (<paramref name="cx"/>, <paramref name="cy"/>) in physical pixels.</summary>
    public void ShowAt(CrosshairSettings style, int cx, int cy, double scaling)
    {
        int dim = CrosshairRenderer.Dim(style);
        Width = Height = dim / scaling;
        Position = new PixelPoint(cx - dim / 2, cy - dim / 2);
        _view.Style = style;
        if (!IsVisible) Show();
    }
}

/// <summary>
/// Decides when the crosshair is on screen, as on Windows: the Game Profiles verdict for the focused app, AND not hidden
/// by the show/hide hotkey, AND the hide-while-held key not down. A Settings preview overrides all of it. It sits at the
/// centre of the monitor the pointer is on (games keep the pointer on their own monitor).
/// </summary>
public sealed class CrosshairService : IDisposable
{
    private readonly ConfigStore _config;
    private readonly DispatcherTimer _holdPollTimer;
    private CrosshairOverlay? _overlay;
    private bool _activationAllowed, _userHidden, _holdHidden, _disposed;
    private uint _holdHideKey;
    private CrosshairSettings? _preview;

    public CrosshairService(ConfigStore config)
    {
        _config = config;
        _config.ProfilesChanged += Refresh;
        // Polls the hold-hide key (keyboard or mouse) so the key still reaches the game.
        _holdPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _holdPollTimer.Tick += (_, _) =>
        {
            bool down = KeyMap.IsDown(_holdHideKey);
            if (down != _holdHidden) { _holdHidden = down; Refresh(); }
        };
    }

    /// <summary>The overlay window, while one exists (for the self-test).</summary>
    public CrosshairOverlay? Overlay => _overlay;

    public void SetActivationAllowed(bool allowed)
    {
        _activationAllowed = allowed;
        if (!allowed)
        {
            _holdHidden = false;
            _holdPollTimer.Stop();
        }
        Refresh();
    }

    public void ToggleHidden()
    {
        _userHidden = !_userHidden;
        Refresh();
    }

    public void SetPreview(CrosshairSettings? style)
    {
        _preview = style?.Clone();
        Refresh();
    }

    private void UpdateHoldWatch()
    {
        if (_activationAllowed && _config.Settings.Hotkeys.TryGetValue("HoldHideCrosshair", out var b) && !b.IsEmpty)
        {
            _holdHideKey = b.VirtualKey;
            _holdPollTimer.Start();
        }
        else
        {
            _holdHidden = false;
            _holdPollTimer.Stop();
        }
    }

    public void Refresh()
    {
        if (_disposed) return;
        UpdateHoldWatch();
        try
        {
            var style = _preview ?? _config.Settings.Crosshair;
            bool show = _preview is not null || (_activationAllowed && !_userHidden && !_holdHidden);
            if (!show)
            {
                _overlay?.Hide();
                return;
            }
            _overlay ??= new CrosshairOverlay();
            var (px, py) = X11.Pointer(X11.Display);
            var screen = _overlay.Screens.ScreenFromPoint(new PixelPoint(px, py)) ?? _overlay.Screens.Primary;
            var b = screen?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
            _overlay.ShowAt(style.Clone(), b.X + b.Width / 2, b.Y + b.Height / 2, screen?.Scaling ?? 1);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            _overlay?.Hide();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _config.ProfilesChanged -= Refresh;
        _holdPollTimer.Stop();
        _overlay?.Close();
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace Oversight.UI;

/// <summary>
/// Attached properties consumed by the shared control templates in Themes/Controls.xaml, so
/// one template can serve every variant (accent / ghost / danger buttons, nav items with
/// icons, lists with an empty state) without a template per variant.
/// </summary>
public static class UiProps
{
    // ── Icons ────────────────────────────────────────────────────────────────

    /// <summary>A 24×24 stroke geometry drawn as the item's icon (sidebar nav items).</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(UiProps), new PropertyMetadata(null));
    public static Geometry? GetIcon(DependencyObject d) => (Geometry?)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, Geometry? v) => d.SetValue(IconProperty, v);

    /// <summary>A Segoe Fluent Icons glyph shown before a button's text.</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(UiProps), new PropertyMetadata(null));
    public static string? GetGlyph(DependencyObject d) => (string?)d.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject d, string? v) => d.SetValue(GlyphProperty, v);

    // ── State ────────────────────────────────────────────────────────────────

    /// <summary>Shows the "unsaved changes" dot on a sidebar nav item.</summary>
    public static readonly DependencyProperty IsDirtyProperty = DependencyProperty.RegisterAttached(
        "IsDirty", typeof(bool), typeof(UiProps), new PropertyMetadata(false));
    public static bool GetIsDirty(DependencyObject d) => (bool)d.GetValue(IsDirtyProperty);
    public static void SetIsDirty(DependencyObject d, bool v) => d.SetValue(IsDirtyProperty, v);

    /// <summary>Message a ListBox shows when it has no items.</summary>
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.RegisterAttached(
        "EmptyText", typeof(string), typeof(UiProps), new PropertyMetadata(null));
    public static string? GetEmptyText(DependencyObject d) => (string?)d.GetValue(EmptyTextProperty);
    public static void SetEmptyText(DependencyObject d, string? v) => d.SetValue(EmptyTextProperty, v);

    // ── Button variant colours ───────────────────────────────────────────────

    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
        "HoverBackground", typeof(Brush), typeof(UiProps), new PropertyMetadata(null));
    public static Brush? GetHoverBackground(DependencyObject d) => (Brush?)d.GetValue(HoverBackgroundProperty);
    public static void SetHoverBackground(DependencyObject d, Brush? v) => d.SetValue(HoverBackgroundProperty, v);

    public static readonly DependencyProperty HoverBorderBrushProperty = DependencyProperty.RegisterAttached(
        "HoverBorderBrush", typeof(Brush), typeof(UiProps), new PropertyMetadata(null));
    public static Brush? GetHoverBorderBrush(DependencyObject d) => (Brush?)d.GetValue(HoverBorderBrushProperty);
    public static void SetHoverBorderBrush(DependencyObject d, Brush? v) => d.SetValue(HoverBorderBrushProperty, v);

    public static readonly DependencyProperty HoverForegroundProperty = DependencyProperty.RegisterAttached(
        "HoverForeground", typeof(Brush), typeof(UiProps), new PropertyMetadata(null));
    public static Brush? GetHoverForeground(DependencyObject d) => (Brush?)d.GetValue(HoverForegroundProperty);
    public static void SetHoverForeground(DependencyObject d, Brush? v) => d.SetValue(HoverForegroundProperty, v);

    public static readonly DependencyProperty PressedBackgroundProperty = DependencyProperty.RegisterAttached(
        "PressedBackground", typeof(Brush), typeof(UiProps), new PropertyMetadata(null));
    public static Brush? GetPressedBackground(DependencyObject d) => (Brush?)d.GetValue(PressedBackgroundProperty);
    public static void SetPressedBackground(DependencyObject d, Brush? v) => d.SetValue(PressedBackgroundProperty, v);

    /// <summary>Adds a soft accent glow on hover (primary buttons).</summary>
    public static readonly DependencyProperty GlowProperty = DependencyProperty.RegisterAttached(
        "Glow", typeof(bool), typeof(UiProps), new PropertyMetadata(false));
    public static bool GetGlow(DependencyObject d) => (bool)d.GetValue(GlowProperty);
    public static void SetGlow(DependencyObject d, bool v) => d.SetValue(GlowProperty, v);
}

/// <summary>"Counter-Strike 2" → "C". Used for the letter tile beside each game profile.</summary>
public sealed class FirstLetterConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string;
        if (string.IsNullOrWhiteSpace(s)) return "?";
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) return char.ToUpper(ch, culture).ToString();
        return "?";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

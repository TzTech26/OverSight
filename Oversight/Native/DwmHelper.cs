using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Oversight.Native;

/// <summary>
/// Applies Windows 11 DWM visual effects to WPF windows.
///
/// All DwmSetWindowAttribute calls that target Windows 11-only attributes
/// (DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_CAPTION_COLOR = 35,
///  DWMWA_TEXT_COLOR = 36, DWMWA_SYSTEMBACKDROP_TYPE = 38) silently return
/// a non-zero HRESULT on older builds — they never throw, so it is safe to
/// call them unconditionally.
/// </summary>
internal static class DwmHelper
{
    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("dwmapi.dll", PreserveSig = false)]
    private static extern void DwmSetWindowAttribute(
        IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", PreserveSig = false)]
    private static extern void DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    // ── Attribute IDs ─────────────────────────────────────────────────────────

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE  = 20;   // Win10 1903+
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;   // Win11 22000+
    private const int DWMWA_CAPTION_COLOR            = 35;   // Win11 22000+
    private const int DWMWA_TEXT_COLOR               = 36;   // Win11 22000+
    private const int DWMWA_SYSTEMBACKDROP_TYPE      = 38;   // Win11 22H2 22621+

    // ── Corner preference values ──────────────────────────────────────────────

    private const int DWMWCP_ROUND      = 2;
    private const int DWMWCP_ROUNDSMALL = 3;

    // ── Backdrop type values ──────────────────────────────────────────────────

    private const int DWMSBT_AUTO            = 0;
    private const int DWMSBT_NONE            = 1;
    private const int DWMSBT_MAINWINDOW      = 2;   // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3;   // Acrylic

    // ── COLORREF helpers  (0x00BBGGRR) ───────────────────────────────────────

    private static int Colorref(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    // Caption (title-bar) + caption-text colours, matched to the WPF palette.
    private static readonly int ColCaptionDark  = Colorref(0x11, 0x11, 0x11); // #111111
    private static readonly int ColTextDark     = Colorref(0xDD, 0xDD, 0xDD); // #DDDDDD
    private static readonly int ColCaptionLight = Colorref(0xF3, 0xF3, 0xF3); // #F3F3F3
    private static readonly int ColTextLight    = Colorref(0x1B, 0x1B, 0x1B); // #1B1B1B

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies the full Windows 11 treatment to a settings-style dialog:
    ///   • Light/dark title bar + matching caption colour (per <paramref name="dark"/>)
    ///   • Rounded corners (DWMWCP_ROUND)
    ///   • Mica backdrop (Windows 11 22H2+; silently ignored on 21H2)
    /// Call this in the Window's SourceInitialized or Loaded event handler.
    /// </summary>
    public static void ApplySettingsWindow(Window window, bool dark)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            ApplyTheme(hwnd, dark);
            SetRoundCorners(hwnd, DWMWCP_ROUND);
            SetMica(hwnd);
        };
    }

    /// <summary>
    /// Re-applies the light/dark title-bar treatment to an already-created window.
    /// Safe to call when the system theme flips while the window is open.
    /// </summary>
    public static void ApplyTheme(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        SetDark(hwnd, dark);
        SetCaptionColor(hwnd, dark ? ColCaptionDark : ColCaptionLight);
        SetTextColor(hwnd, dark ? ColTextDark : ColTextLight);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void SetDark(IntPtr hwnd, bool dark)
    {
        int v = dark ? 1 : 0;
        Try(() => DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, 4));
    }

    private static void SetRoundCorners(IntPtr hwnd, int pref)
    {
        Try(() => DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4));
    }

    private static void SetCaptionColor(IntPtr hwnd, int colorRef)
    {
        Try(() => DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, 4));
    }

    private static void SetTextColor(IntPtr hwnd, int colorRef)
    {
        Try(() => DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref colorRef, 4));
    }

    private static void SetMica(IntPtr hwnd)
    {
        int v = DWMSBT_MAINWINDOW;
        Try(() => DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref v, 4));
    }

    private static void Try(Action act)
    {
        try { act(); }
        catch (COMException) { /* attribute not supported on this build — ignore */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DwmHelper] {ex.Message}");
        }
    }
}

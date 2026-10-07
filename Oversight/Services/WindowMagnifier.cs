using System.Runtime.InteropServices;
using Oversight.Native;

namespace Oversight.Services;

/// <summary>
/// A per-monitor magnifier built on the Windows Magnification window control
/// (<c>WC_MAGNIFIER</c>). A borderless, topmost, click-through layered host window
/// is positioned over the target region; the magnifier child fills it and renders
/// a magnified view of the desktop beneath it.
///
/// Unlike <see cref="NativeMethods.MagSetFullscreenTransform"/>, this only affects
/// the monitor(s) the host window covers — other displays are left untouched — and
/// it can never leave the whole desktop "stuck" zoomed, because the OS destroys the
/// window automatically when the process exits.
/// </summary>
internal sealed class WindowMagnifier : IDisposable
{
    private const string HostClassName = "OversightMagHost";

    // A WS_EX_LAYERED (alpha-blended) overlay over a fullscreen game forces the heaviest DWM
    // composition path and disqualifies the game from the low-latency flip / hardware overlay
    // planes — which makes the game feel laggy even though its FPS and the zoom view itself are
    // unchanged (the symptom seen in Rust). A plain, non-layered click-through host avoids that.
    // Trade-off: a non-layered host can't alpha-blend, so the cross-monitor "smooth pan" fade
    // becomes an instant switch. WS_EX_TRANSPARENT alone still provides click-through.
    // The WC_MAGNIFIER control only paints when hosted in a layered window — a non-layered
    // host renders blank (white). So this must stay true; it can't be dropped to dodge the
    // layered-overlay latency over fullscreen games. Kept as a flag only to document that.
    private static readonly bool UseLayeredHost = true;

    // The class WndProc delegate must stay rooted for the lifetime of the class.
    private static NativeMethods.WndProc? _wndProc;
    private static bool _classRegistered;

    private IntPtr _hwndHost = IntPtr.Zero;
    private IntPtr _hwndMag  = IntPtr.Zero;
    private bool   _visible;
    private byte   _alpha = 255;   // current layered-window opacity

    // Last applied host rect, to avoid redundant SetWindowPos churn each frame.
    private (int L, int T, int W, int H) _hostRect;

    // Last applied zoom + source rect. Re-sending an identical source/transform to the
    // magnifier control every frame makes the magnified image visibly shimmer while idle,
    // so we skip the native calls when nothing has actually changed.
    private (float Zoom, int L, int T, int W, int H) _lastSource = (-1f, 0, 0, 0, 0);

    public bool IsCreated => _hwndHost != IntPtr.Zero && _hwndMag != IntPtr.Zero;

    private bool EnsureCreated()
    {
        if (IsCreated) return true;

        var hInst = NativeMethods.GetModuleHandle(null);

        if (!_classRegistered)
        {
            _wndProc = NativeMethods.DefWindowProc;   // a plain host window needs no custom handling
            var wc = new NativeMethods.WNDCLASSEX
            {
                cbSize        = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc   = _wndProc,
                hInstance     = hInst,
                lpszClassName = HostClassName,
            };
            // 0 == failure; ERROR_CLASS_ALREADY_EXISTS is fine (e.g. re-create after dispose).
            NativeMethods.RegisterClassEx(ref wc);
            _classRegistered = true;
        }

        uint exStyle = NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TRANSPARENT |
                       NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        if (UseLayeredHost) exStyle |= NativeMethods.WS_EX_LAYERED;

        _hwndHost = NativeMethods.CreateWindowEx(
            exStyle,
            HostClassName, null,
            NativeMethods.WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (_hwndHost == IntPtr.Zero) return false;

        // Fully opaque layered window (LWA_ALPHA 255) — the magnifier paints the content.
        if (UseLayeredHost)
            NativeMethods.SetLayeredWindowAttributes(_hwndHost, 0, 255, NativeMethods.LWA_ALPHA);

        _hwndMag = NativeMethods.CreateWindowEx(
            0, NativeMethods.WC_MAGNIFIER, "OversightMagControl",
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE,
            0, 0, 0, 0, _hwndHost, IntPtr.Zero, hInst, IntPtr.Zero);
        if (_hwndMag == IntPtr.Zero) { Destroy(); return false; }

        // Exclude our own host window so the control captures the real desktop
        // beneath it instead of recursively magnifying itself.
        var exclude = new[] { _hwndHost };
        NativeMethods.MagSetWindowFilterList(
            _hwndMag, NativeMethods.MW_FILTERMODE_EXCLUDE, exclude.Length, exclude);

        return true;
    }

    /// <summary>
    /// Position the lens over <paramref name="bounds"/> (physical pixels) and render a
    /// <paramref name="zoom"/>× view anchored so the point (<paramref name="anchorX"/>,
    /// <paramref name="anchorY"/>) — the cursor — stays put under the magnified content.
    /// </summary>
    public void Update(int boundsL, int boundsT, int boundsW, int boundsH,
                       float zoom, int anchorX, int anchorY)
    {
        if (!EnsureCreated()) return;

        // Reposition / resize the host only when the target rect actually changes
        // (e.g. the cursor crossed to another monitor).
        if (_hostRect != (boundsL, boundsT, boundsW, boundsH))
        {
            NativeMethods.SetWindowPos(_hwndHost, NativeMethods.HWND_TOPMOST,
                boundsL, boundsT, boundsW, boundsH, NativeMethods.SWP_NOACTIVATE);
            NativeMethods.SetWindowPos(_hwndMag, IntPtr.Zero,
                0, 0, boundsW, boundsH, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
            _hostRect = (boundsL, boundsT, boundsW, boundsH);
        }

        // Source rectangle: bounds/zoom in size, positioned so the anchor (cursor) is a
        // fixed point of the magnification (content under the cursor stays under it).
        int srcW = Math.Max(1, (int)Math.Round(boundsW / zoom));
        int srcH = Math.Max(1, (int)Math.Round(boundsH / zoom));

        int srcL = (int)Math.Round(anchorX - (anchorX - boundsL) / zoom);
        int srcT = (int)Math.Round(anchorY - (anchorY - boundsT) / zoom);

        // Keep the source inside the target bounds.
        srcL = Math.Clamp(srcL, boundsL, boundsL + boundsW - srcW);
        srcT = Math.Clamp(srcT, boundsT, boundsT + boundsH - srcH);

        // Nothing changed since the last frame — don't re-send identical source/transform,
        // which otherwise makes the still image shimmer.
        var current = (zoom, srcL, srcT, srcW, srcH);
        if (current != _lastSource)
        {
            // Derive the scale from the *rounded* source size, not the raw float zoom. The
            // source rect is whole pixels, so a raw zoom scale leaves srcW*zoom ≠ window
            // width — a sub-pixel mismatch that wobbles every frame the zoom changes, which
            // is exactly the shake seen during the ramp. Using window/source keeps the
            // captured region and the rendered size perfectly consistent and steady.
            float scaleX = (float)boundsW / srcW;
            float scaleY = (float)boundsH / srcH;
            var transform = NativeMethods.MAGTRANSFORM.Scale(scaleX);
            transform.m11 = scaleY;
            NativeMethods.MagSetWindowTransform(_hwndMag, ref transform);

            var src = new NativeMethods.RECT
            {
                left   = srcL,
                top    = srcT,
                right  = srcL + srcW,
                bottom = srcT + srcH,
            };
            NativeMethods.MagSetWindowSource(_hwndMag, src);
            _lastSource = current;
        }

        if (!_visible)
        {
            NativeMethods.ShowWindow(_hwndHost, NativeMethods.SW_SHOWNA);
            _visible = true;
        }
    }

    /// <summary>Sets the lens opacity (0 = fully transparent, 255 = fully opaque) for fades.</summary>
    public void SetAlpha(byte alpha)
    {
        if (!UseLayeredHost) return;   // non-layered host can't alpha-blend; fades are no-ops
        if (_alpha == alpha) return;
        _alpha = alpha;
        if (_hwndHost != IntPtr.Zero)
            NativeMethods.SetLayeredWindowAttributes(_hwndHost, 0, alpha, NativeMethods.LWA_ALPHA);
    }

    public void Hide()
    {
        if (_hwndHost != IntPtr.Zero && _visible)
        {
            NativeMethods.ShowWindow(_hwndHost, NativeMethods.SW_HIDE);
            _visible = false;
        }
        // Force a fresh source/transform apply the next time we show, and restore full
        // opacity so a lens that was hidden mid-fade isn't stuck transparent on reuse.
        _lastSource = (-1f, 0, 0, 0, 0);
        if (UseLayeredHost && _hwndHost != IntPtr.Zero)
            NativeMethods.SetLayeredWindowAttributes(_hwndHost, 0, 255, NativeMethods.LWA_ALPHA);
        _alpha = 255;
    }

    public void Dispose() => Destroy();

    private void Destroy()
    {
        if (_hwndMag  != IntPtr.Zero) { NativeMethods.DestroyWindow(_hwndMag);  _hwndMag  = IntPtr.Zero; }
        if (_hwndHost != IntPtr.Zero) { NativeMethods.DestroyWindow(_hwndHost); _hwndHost = IntPtr.Zero; }
        _visible    = false;
        _hostRect   = default;
        _lastSource = (-1f, 0, 0, 0, 0);
    }
}

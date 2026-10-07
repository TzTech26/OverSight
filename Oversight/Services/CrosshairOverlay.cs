using System.Drawing;
using System.Runtime.InteropServices;
using Oversight.Native;

namespace Oversight.Services;

/// <summary>
/// The on-screen crosshair: a tiny borderless, topmost, click-through layered window whose
/// content is pushed with <see cref="NativeMethods.UpdateLayeredWindow"/> (per-pixel alpha,
/// so anti-aliased shapes blend cleanly over anything). The window is only as large as the
/// crosshair itself, which keeps its impact on fullscreen games minimal, and the OS destroys
/// it automatically if the process dies — it can never be left stranded.
///
/// Coordinates are physical pixels (the process is PerMonitorV2 DPI-aware).
/// </summary>
internal sealed class CrosshairOverlay : IDisposable
{
    private const string HostClassName = "OversightCrosshair";

    // The class WndProc delegate must stay rooted for the lifetime of the class.
    private static NativeMethods.WndProc? _wndProc;
    private static bool _classRegistered;

    private IntPtr _hwnd = IntPtr.Zero;
    private bool   _visible;

    private bool EnsureCreated()
    {
        if (_hwnd != IntPtr.Zero) return true;

        var hInst = NativeMethods.GetModuleHandle(null);

        if (!_classRegistered)
        {
            _wndProc = NativeMethods.DefWindowProc;
            var wc = new NativeMethods.WNDCLASSEX
            {
                cbSize        = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc   = _wndProc,
                hInstance     = hInst,
                lpszClassName = HostClassName,
            };
            NativeMethods.RegisterClassEx(ref wc);   // ERROR_CLASS_ALREADY_EXISTS is fine
            _classRegistered = true;
        }

        _hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TRANSPARENT |
            NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE |
            NativeMethods.WS_EX_LAYERED,
            HostClassName, null,
            NativeMethods.WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);

        return _hwnd != IntPtr.Zero;
    }

    /// <summary>
    /// Shows <paramref name="bmp"/> with its top-left at (<paramref name="x"/>, <paramref name="y"/>)
    /// physical pixels, sizing the window to the bitmap. Safe to call repeatedly.
    /// </summary>
    public void ShowAt(Bitmap bmp, int x, int y)
    {
        if (!EnsureCreated()) return;

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDc    = NativeMethods.CreateCompatibleDC(screenDc);
        IntPtr hBitmap  = IntPtr.Zero;
        IntPtr oldBmp   = IntPtr.Zero;
        try
        {
            // GetHbitmap composites onto the given background colour; transparent black
            // yields exactly the premultiplied alpha UpdateLayeredWindow expects.
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            oldBmp  = NativeMethods.SelectObject(memDc, hBitmap);

            var dst   = new NativeMethods.POINT { X = x, Y = y };
            var size  = new NativeMethods.SIZE  { cx = bmp.Width, cy = bmp.Height };
            var src   = new NativeMethods.POINT { X = 0, Y = 0 };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp             = NativeMethods.AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat         = NativeMethods.AC_SRC_ALPHA,
            };

            NativeMethods.UpdateLayeredWindow(_hwnd, screenDc, ref dst, ref size,
                memDc, ref src, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            if (oldBmp  != IntPtr.Zero) NativeMethods.SelectObject(memDc, oldBmp);
            if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }

        if (!_visible)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNA);
            _visible = true;
        }

        // Re-assert topmost: games and other overlays created after us can otherwise cover it.
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    public void Hide()
    {
        if (_hwnd != IntPtr.Zero && _visible)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
            _visible = false;
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        _visible = false;
    }
}

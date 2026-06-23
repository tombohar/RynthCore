using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace RynthCore.StatusAgent;

/// <summary>
/// Captures an AC client window's on-screen rectangle as a JPEG. Out-of-process (GDI BitBlt of the
/// composited desktop), so the game/engine is never touched. Only works on a window that's actually
/// rendering — a minimized client has no frame. Captures whatever is on top of the window too, so
/// docked overlays show; undocked panels (separate windows elsewhere) don't.
/// </summary>
internal static class ScreenCapture
{
    public enum Result { Ok, NotFound, Minimized, Error }

    private static bool _dpiSet;
    private static void EnsureDpiAware()
    {
        if (_dpiSet) return;
        _dpiSet = true;
        try { if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return; } catch { }
        try { SetProcessDPIAware(); } catch { }
    }

    /// <param name="maxWidth">If >0 and wider, downscale to this width (preserve aspect) before encoding.</param>
    /// Uses PrintWindow(PW_RENDERFULLCONTENT) so it captures the window's OWN content regardless of what's
    /// on top of it (a foreground app over the client no longer leaks into the frame). Cropped to the
    /// client area (no title bar). Separate overlay windows (Avalonia panels) are NOT included — by design.
    public static Result TryCaptureJpeg(int pid, int quality, int maxWidth, out byte[] jpeg)
    {
        jpeg = Array.Empty<byte>();
        EnsureDpiAware();
        if (!TryFindWindow(pid, out IntPtr hwnd, out bool minimized))
            return minimized ? Result.Minimized : Result.NotFound;
        try
        {
            if (!GetWindowRect(hwnd, out RECT wr)) return Result.Error;
            int ww = wr.Right - wr.Left, wh = wr.Bottom - wr.Top;
            if (ww < 8 || wh < 8) return Result.Error;

            using var full = new Bitmap(ww, wh, PixelFormat.Format24bppRgb);
            bool ok;
            using (var g = Graphics.FromImage(full))
            {
                IntPtr hdc = g.GetHdc();
                ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
            }
            if (!ok) return Result.Error;

            // Crop the title bar / borders away (client area only) for a clean view.
            Bitmap img = full;
            Rectangle crop = ClientCrop(hwnd, wr, ww, wh);
            if (crop.Width >= 8 && crop.Height >= 8 && (crop.Width < ww || crop.Height < wh))
                img = full.Clone(crop, full.PixelFormat);
            try
            {
                if (maxWidth > 0 && img.Width > maxWidth)
                {
                    int dw = maxWidth, dh = Math.Max(1, (int)Math.Round(img.Height * (maxWidth / (double)img.Width)));
                    using var scaled = new Bitmap(dw, dh, PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(scaled))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        g.DrawImage(img, 0, 0, dw, dh);
                    }
                    jpeg = EncodeJpeg(scaled, quality);
                }
                else jpeg = EncodeJpeg(img, quality);
            }
            finally { if (!ReferenceEquals(img, full)) img.Dispose(); }
            return Result.Ok;
        }
        catch { return Result.Error; }
    }

    /// The client-area rectangle within the full-window bitmap (0,0 = window top-left).
    private static Rectangle ClientCrop(IntPtr hwnd, RECT wr, int ww, int wh)
    {
        try
        {
            if (GetClientRect(hwnd, out RECT cr))
            {
                POINT origin = default;
                if (ClientToScreen(hwnd, ref origin))
                {
                    int ox = origin.X - wr.Left, oy = origin.Y - wr.Top;
                    int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
                    if (ox >= 0 && oy >= 0 && cw > 0 && ch > 0 && ox + cw <= ww && oy + ch <= wh)
                        return new Rectangle(ox, oy, cw, ch);
                }
            }
        }
        catch { }
        return new Rectangle(0, 0, ww, wh);
    }

    private static ImageCodecInfo? _jpegEncoder;
    private static byte[] EncodeJpeg(Bitmap bmp, int quality)
    {
        _jpegEncoder ??= Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid)
                         ?? throw new InvalidOperationException("no jpeg encoder");
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
        using var ms = new MemoryStream();
        bmp.Save(ms, _jpegEncoder, ep);
        return ms.ToArray();
    }

    /// Pick the game window for this pid: the largest visible, non-minimized top-level window,
    /// preferring one whose class name contains "acclient" (over the smaller Avalonia overlay windows).
    private static bool TryFindWindow(int pid, out IntPtr best, out bool minimized)
    {
        IntPtr found = IntPtr.Zero;
        long bestScore = 0;
        bool anyMinimized = false;
        var sb = new StringBuilder(64);
        EnumWindows((h, l) =>
        {
            if (GetWindowThreadProcessId(h, out uint wpid) == 0 || wpid != (uint)pid) return true;
            if (!IsWindowVisible(h)) return true;
            if (IsIconic(h)) { anyMinimized = true; return true; }
            if (!GetWindowRect(h, out RECT r)) return true;
            long area = (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
            if (area < 64) return true;
            sb.Clear();
            GetClassName(h, sb, sb.Capacity);
            bool isAc = sb.ToString().IndexOf("acclient", StringComparison.OrdinalIgnoreCase) >= 0;
            long score = isAc ? area + 4_000_000_000 : area;
            if (score > bestScore) { bestScore = score; found = h; }
            return true;
        }, IntPtr.Zero);
        best = found;
        minimized = found == IntPtr.Zero && anyMinimized;
        return found != IntPtr.Zero;
    }

    private static bool TryGetBounds(IntPtr hwnd, out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        // DWM extended frame bounds = the visible rect (excludes the invisible resize border on Win10+).
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
        {
            if (!GetWindowRect(hwnd, out r)) return false;
        }
        x = r.Left; y = r.Top; w = r.Right - r.Left; h = r.Bottom - r.Top;
        return w > 0 && h > 0;
    }

    // ── P/Invoke ────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    private const uint PW_RENDERFULLCONTENT = 0x00000002;   // capture D3D/GPU content (Win8.1+)
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT r, int size);
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
}

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace RynthCore.StatusAgent;

/// PrintWindow(PW_RENDERFULLCONTENT) capture of an AC client window as a fixed-size BGR24 frame, for
/// piping straight into ffmpeg's rawvideo input. Same occlusion-proof, D3D-capable path ScreenCapture
/// uses for the MJPEG feed (gdigrab returns black for the D3D9 client); this variant skips JPEG and
/// emits raw bytes for the H.264 encoder. ~15-20fps ceiling (PrintWindow cost) — WGC replaces it later.
internal sealed class VideoCapture
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    private IntPtr _hwnd;

    /// Resolve the window for a pid and lock in an even, capped target size (h264 needs even dims).
    public static VideoCapture? Open(int pid, int maxWidth)
    {
        EnsureDpiAware();
        if (!TryFindWindow(pid, out IntPtr hwnd)) return null;
        if (!GetClientSize(hwnd, out int cw, out int ch) || cw < 16 || ch < 16) return null;

        int w = cw, h = ch;
        if (maxWidth > 0 && w > maxWidth) { h = (int)Math.Round(h * (maxWidth / (double)w)); w = maxWidth; }
        w &= ~1; h &= ~1;
        return new VideoCapture { _hwnd = hwnd, Width = w, Height = h };
    }

    /// Capture one frame as tightly-packed BGR24 (Width*Height*3 bytes). False if unrenderable this tick.
    public bool TryGrab(out byte[] bgr)
    {
        bgr = Array.Empty<byte>();
        if (!GetWindowRect(_hwnd, out RECT wr)) return false;
        int ww = wr.Right - wr.Left, wh = wr.Bottom - wr.Top;
        if (ww < 8 || wh < 8) return false;
        try
        {
            using var full = new Bitmap(ww, wh, PixelFormat.Format24bppRgb);
            bool ok;
            using (var g = Graphics.FromImage(full))
            {
                IntPtr hdc = g.GetHdc();
                ok = PrintWindow(_hwnd, hdc, PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
            }
            if (!ok) return false;

            Rectangle crop = ClientCrop(_hwnd, wr, ww, wh);
            using var target = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(target))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(full, new Rectangle(0, 0, Width, Height), crop, GraphicsUnit.Pixel);
            }
            bgr = PackBgr(target);
            return true;
        }
        catch { return false; }
    }

    private static byte[] PackBgr(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int rowBytes = bmp.Width * 3;
            var outBuf = new byte[rowBytes * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, outBuf, y * rowBytes, rowBytes);
            return outBuf;
        }
        finally { bmp.UnlockBits(data); }
    }

    private static bool GetClientSize(IntPtr hwnd, out int w, out int h)
    {
        w = h = 0;
        if (!GetClientRect(hwnd, out RECT cr)) return false;
        w = cr.Right - cr.Left; h = cr.Bottom - cr.Top;
        return w > 0 && h > 0;
    }

    /// A pid's client area in SCREEN coords + even dims, for ffmpeg ddagrab (GPU Desktop Duplication).
    /// False if no renderable window. Used by the HD/WebRTC path; the MJPEG path stays on PrintWindow.
    public static bool TryGetClientScreenRect(int pid, out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        EnsureDpiAware();
        if (!TryFindWindow(pid, out IntPtr hwnd)) return false;
        if (!GetClientRect(hwnd, out RECT cr)) return false;
        POINT origin = default;
        if (!ClientToScreen(hwnd, ref origin)) return false;
        x = origin.X; y = origin.Y;
        w = (cr.Right - cr.Left) & ~1; h = (cr.Bottom - cr.Top) & ~1;
        return w >= 16 && h >= 16;
    }

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

    private static bool TryFindWindow(int pid, out IntPtr best)
    {
        IntPtr found = IntPtr.Zero;
        long bestScore = 0;
        var sb = new StringBuilder(64);
        EnumWindows((h, l) =>
        {
            if (GetWindowThreadProcessId(h, out uint wpid) == 0 || wpid != (uint)pid) return true;
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
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
        return found != IntPtr.Zero;
    }

    private static bool _dpiSet;
    private static void EnsureDpiAware()
    {
        if (_dpiSet) return;
        _dpiSet = true;
        try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }
        try { SetProcessDPIAware(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
}

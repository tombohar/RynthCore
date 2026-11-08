// ============================================================================
//  RynthCore.Engine - ImGui/RetailCursors.cs
//  The retail window cursors for RynthCore's windows (2026-11-07, Tom: "on retail mouse
//  cursor changes when move/resize is going to happen"). AC's cursor is a Windows cursor:
//  UIElementManager::SetCursor (0x0045A910) makes one from a 0x06 picture
//  (Device::SetCursorFromImage -> CreateIconFromRenderSurface) and sets it as the game
//  window's class cursor (SetClassLong GCL_HCURSOR) and with SetCursor. ImGui's cursor
//  shapes (ImGui.SetMouseCursor: a panel's drag band, a resize edge or grip) never reached
//  it, so the cursor stayed AC's.
//
//  Now, when the frame's ImGui cursor is a move or resize shape, the game window shows
//  retail's own window-control cursor for it (the pictures AC's windows use, read off the
//  dat as OpenAC's RetailCursorCatalog has them; 32x32, hotspot 16,16):
//    ResizeAll  -> 0x06006119 (move)       ResizeNS   -> 0x06005E66
//    ResizeEW   -> 0x06006128              ResizeNWSE -> 0x06006126   ResizeNESW -> 0x06006127
//  Win32Backend answers WM_SETCURSOR with it (AC's class cursor is left alone), and when the
//  shape goes back to the arrow AC's own cursor (its class cursor) is put back. Other shapes
//  (hand, text) keep AC's cursor, as before. The cursors are made once, from portal.dat.
//
//  AC's main thread only (the ImGui frame and the window procedure both run on it).
// ============================================================================

using System;
using System.Runtime.InteropServices;
using ImGuiNET;
using RynthCore.Engine.UI.ScriptWindows;

namespace RynthCore.Engine.ImGuiBackend;

internal static unsafe class RetailCursors
{
    private const int Hotspot = 16;
    private static readonly (ImGuiMouseCursor Shape, uint Picture)[] Map =
    {
        (ImGuiMouseCursor.ResizeAll, 0x06006119u),
        (ImGuiMouseCursor.ResizeNS, 0x06005E66u),
        (ImGuiMouseCursor.ResizeEW, 0x06006128u),
        (ImGuiMouseCursor.ResizeNWSE, 0x06006126u),
        (ImGuiMouseCursor.ResizeNESW, 0x06006127u),
    };

    private static readonly IntPtr[] Handles = new IntPtr[Map.Length];
    private static bool _made, _failedLogged;
    private static IntPtr _current;

    /// <summary>The retail cursor wanted now, or zero (AC's own). Win32Backend's WM_SETCURSOR reads it.</summary>
    public static IntPtr Current => _current;

    /// <summary>
    /// End of the ImGui frame: the shape ImGui asks for (<paramref name="shape"/>) while a RynthCore window has
    /// the mouse (<paramref name="overUi"/>). Sets the retail cursor at once when it changes, and gives AC's back.
    /// </summary>
    public static void Update(ImGuiMouseCursor shape, bool overUi)
    {
        IntPtr want = IntPtr.Zero;
        if (overUi)
        {
            int i = Array.FindIndex(Map, m => m.Shape == shape);
            if (i >= 0)
            {
                if (!_made) Make();
                want = Handles[i];
            }
        }
        if (want == _current) return;
        _current = want;
        try
        {
            if (want != IntPtr.Zero) SetCursor(want);
            else
            {
                IntPtr hwnd = Win32Backend.GameHwnd;
                IntPtr ac = hwnd != IntPtr.Zero ? (IntPtr)GetClassLongW(hwnd, GclHCursor) : IntPtr.Zero;
                if (ac != IntPtr.Zero) SetCursor(ac);
            }
        }
        catch { }
    }

    private static void Make()
    {
        _made = true;
        try
        {
            string? path = AcDatFile.FindPortalDat();
            using AcDatFile? dat = path != null ? AcDatFile.Open(path, out _) : null;
            if (dat == null) { Log("client_portal.dat not found"); return; }
            int made = 0;
            for (int i = 0; i < Map.Length; i++)
            {
                AcIconImage? img = AcIconDecoder.Decode(dat, Map[i].Picture, AcIconDecoder.MaxUiSide);
                if (img == null) continue;
                Handles[i] = CreateCursor(img);
                if (Handles[i] != IntPtr.Zero) made++;
            }
            RynthLog.UI($"RetailCursors: {made} of {Map.Length} retail window cursors made.");
        }
        catch (Exception ex) { Log($"{ex.GetType().Name}: {ex.Message}"); }
    }

    private static void Log(string why)
    {
        if (_failedLogged) return;
        _failedLogged = true;
        RynthLog.UI("RetailCursors: no retail window cursors (" + why + "); AC's cursor stays.");
    }

    /// <summary>A Windows cursor from an RGBA picture (32-bit with alpha), hotspot in the middle as retail's.</summary>
    private static IntPtr CreateCursor(AcIconImage img)
    {
        int w = img.Width, h = img.Height;
        var bi = new BITMAPV5HEADER
        {
            bV5Size = (uint)sizeof(BITMAPV5HEADER), bV5Width = w, bV5Height = -h, bV5Planes = 1, bV5BitCount = 32,
            bV5Compression = 3 /* BI_BITFIELDS */, bV5RedMask = 0x00FF0000, bV5GreenMask = 0x0000FF00,
            bV5BlueMask = 0x000000FF, bV5AlphaMask = 0xFF000000,
        };
        IntPtr dc = GetDC(IntPtr.Zero);
        IntPtr color = CreateDIBSection(dc, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, dc);
        if (color == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;
        var dst = new Span<byte>((void*)bits, w * h * 4);
        byte[] src = img.Pixels;
        for (int p = 0; p < w * h; p++)
        {
            dst[p * 4 + 0] = src[p * 4 + 2];   // B
            dst[p * 4 + 1] = src[p * 4 + 1];   // G
            dst[p * 4 + 2] = src[p * 4 + 0];   // R
            dst[p * 4 + 3] = src[p * 4 + 3];   // A
        }
        IntPtr mask = CreateBitmap(w, h, 1, 1, IntPtr.Zero);
        var info = new ICONINFO { fIcon = false, xHotspot = Math.Min(Hotspot, w - 1), yHotspot = Math.Min(Hotspot, h - 1), hbmMask = mask, hbmColor = color };
        IntPtr cursor = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return cursor;
    }

    private const int GclHCursor = -12;

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPV5HEADER
    {
        public uint bV5Size;
        public int bV5Width, bV5Height;
        public ushort bV5Planes, bV5BitCount;
        public uint bV5Compression, bV5SizeImage;
        public int bV5XPelsPerMeter, bV5YPelsPerMeter;
        public uint bV5ClrUsed, bV5ClrImportant, bV5RedMask, bV5GreenMask, bV5BlueMask, bV5AlphaMask, bV5CSType;
        public int e0, e1, e2, e3, e4, e5, e6, e7, e8;   // CIEXYZTRIPLE
        public uint bV5GammaRed, bV5GammaGreen, bV5GammaBlue, bV5Intent, bV5ProfileData, bV5ProfileSize, bV5Reserved;
    }

    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr hCursor);
    [DllImport("user32.dll")] private static extern uint GetClassLongW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPV5HEADER pbmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bitCount, IntPtr bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}

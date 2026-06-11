// ═══════════════════════════════════════════════════════════════════════════
//  RynthCore.Engine — D3D9/VitalHud.cs
//
//  "Hot HUD" — draws player Health / Stamina / Mana bars directly in the D3D9
//  EndScene path, on AC's render thread, every frame. Ported from RynthCore2's
//  overlay/hud_overlay.cpp; the D3D9 plumbing mirrors TestRenderer.cs (cached
//  vtable delegates + full render-state save/restore).
//
//  Visuals: a translucent panel with a drop shadow, three gradient-filled bars
//  (track + fill + gloss + border) with an HP/STAM/MANA label and a right-
//  aligned "cur/max" number drawn from the dependency-free BitmapFont.
//  Everything is colored 2D quads (D3DFVF_XYZRHW | D3DFVF_DIFFUSE, no texture)
//  accumulated into one static vertex batch flushed with a single
//  DrawPrimitiveUP — no per-frame heap allocation (avoids the LFH per-frame-
//  alloc AV class).
//
//  Interactive: drag the panel to move it, drag the bottom-right grip to resize.
//  Win32Backend routes mouse messages to TryHandleMouse, which consumes the ones
//  that land on the HUD so they never reach AC (no stray camera spin). Position +
//  scale persist to %APPDATA%\RynthCore\vitalhud.cfg.
//
//  Data source: Compatibility.PlayerVitalsHooks.TryGetSnapshot — the same
//  buffed-effective values AC's own bars render, maintained on AC's main thread.
//  Draw and TryHandleMouse both run on that thread (EndScene / WndProc), so the
//  shared geometry state needs no cross-thread synchronization.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.D3D9;

internal static unsafe class VitalHud
{
    // ─── D3D9 constants ───────────────────────────────────────────────
    private const uint D3DFVF_XYZRHW  = 0x0004;
    private const uint D3DFVF_DIFFUSE = 0x0040;
    private const uint HudFvf         = D3DFVF_XYZRHW | D3DFVF_DIFFUSE;

    private const uint D3DPT_TRIANGLELIST = 4;

    // D3DRENDERSTATETYPE
    private const uint D3DRS_ZENABLE           = 7;
    private const uint D3DRS_ZWRITEENABLE      = 14;
    private const uint D3DRS_ALPHATESTENABLE   = 15;
    private const uint D3DRS_SRCBLEND          = 19;
    private const uint D3DRS_DESTBLEND         = 20;
    private const uint D3DRS_CULLMODE          = 22;
    private const uint D3DRS_ALPHABLENDENABLE  = 27;
    private const uint D3DRS_FOGENABLE         = 28;
    private const uint D3DRS_STENCILENABLE     = 52;
    private const uint D3DRS_LIGHTING          = 137;
    private const uint D3DRS_COLORWRITEENABLE  = 168;
    private const uint D3DRS_BLENDOP           = 171;
    private const uint D3DRS_SCISSORTESTENABLE = 174;

    // D3DTEXTURESTAGESTATETYPE
    private const uint D3DTSS_COLOROP   = 1;
    private const uint D3DTSS_COLORARG1 = 2;
    private const uint D3DTSS_ALPHAOP   = 4;
    private const uint D3DTSS_ALPHAARG1 = 6;

    // values
    private const uint D3DBLEND_SRCALPHA    = 5;
    private const uint D3DBLEND_INVSRCALPHA = 6;
    private const uint D3DBLENDOP_ADD       = 1;
    private const uint D3DCULL_NONE         = 1;
    private const uint D3DTOP_DISABLE       = 1;
    private const uint D3DTOP_SELECTARG1    = 2;
    private const uint D3DTA_DIFFUSE        = 0;

    // ─── Win32 messages + capture P/Invoke ────────────────────────────
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP   = 0x0202;
    private const uint WM_MOUSEMOVE   = 0x0200;

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    // ─── Vertex ───────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public float X, Y, Z, Rhw;
        public uint  Color; // ARGB
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DVIEWPORT9
    {
        public uint X, Y, Width, Height;
        public float MinZ, MaxZ;
    }

    // ─── Device method delegates (COM/stdcall) ────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetRenderStateDelegate(IntPtr dev, uint state, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetRenderStateDelegate(IntPtr dev, uint state, out uint pValue);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetTextureStageStateDelegate(IntPtr dev, uint stage, uint type, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetTextureStageStateDelegate(IntPtr dev, uint stage, uint type, out uint pValue);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetTextureDelegate(IntPtr dev, uint stage, IntPtr pTexture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetTextureDelegate(IntPtr dev, uint stage, out IntPtr ppTexture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetFVFDelegate(IntPtr dev, uint fvf);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFVFDelegate(IntPtr dev, out uint fvf);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetVertexShaderDelegate(IntPtr dev, IntPtr pShader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetVertexShaderDelegate(IntPtr dev, out IntPtr ppShader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPixelShaderDelegate(IntPtr dev, IntPtr pShader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPixelShaderDelegate(IntPtr dev, out IntPtr ppShader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DrawPrimitiveUPDelegate(IntPtr dev, uint primitiveType, uint primitiveCount, IntPtr pData, uint stride);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetViewportDelegate(IntPtr dev, out D3DVIEWPORT9 vp);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseComDelegate(IntPtr pUnk);

    private static SetRenderStateDelegate?        _setRenderState;
    private static GetRenderStateDelegate?        _getRenderState;
    private static SetTextureStageStateDelegate?  _setTextureStageState;
    private static GetTextureStageStateDelegate?  _getTextureStageState;
    private static SetTextureDelegate?            _setTexture;
    private static GetTextureDelegate?            _getTexture;
    private static SetFVFDelegate?                _setFVF;
    private static GetFVFDelegate?                _getFVF;
    private static SetVertexShaderDelegate?       _setVertexShader;
    private static GetVertexShaderDelegate?       _getVertexShader;
    private static SetPixelShaderDelegate?        _setPixelShader;
    private static GetPixelShaderDelegate?        _getPixelShader;
    private static DrawPrimitiveUPDelegate?       _drawPrimitiveUP;
    private static GetViewportDelegate?           _getViewport;
    private static bool _delegatesCached;

    // ─── Geometry / interaction state (AC main thread only) ───────────
    private const float MinScale = 0.6f;
    private const float MaxScale = 2.5f;

    private static float _hudX = 16f, _hudY = 16f, _scale = 1f;
    private static float _vpW, _vpH;          // last viewport, for input clamp
    private static bool  _geomInit;
    private static bool  _firstDrawLogged;

    private static int   _dragMode;           // 0 none, 1 move, 2 resize
    private static float _grabDX, _grabDY;
    private static float _resizeStartScale = 1f, _resizeStartDist = 1f;

    /// <summary>True while the user is dragging or resizing the HUD. Win32Backend
    /// keeps routing mouse messages to <see cref="TryHandleMouse"/> while set.</summary>
    public static bool IsDragging => _dragMode != 0;

    // ─── Vertex batch (single static buffer; no per-frame heap alloc) ──
    // Text is the big consumer — the outline pass below draws each number 9×.
    private const int MaxVerts = 48000;
    private static readonly Vertex[] _batch = new Vertex[MaxVerts];
    private static int _batchCount;

    // Cached text sink + color so BitmapFont.Emit allocates nothing per call.
    private static readonly BitmapFont.CellSink _cellSink = TextCell;
    private static uint _textColor;

    // ─── Layout derived from the uniform scale ────────────────────────
    private struct Metrics
    {
        public float Scale, Pad, BarW, BarH, Gap, PanelW, PanelH, Grip;
    }

    private static Metrics Metric(float s)
    {
        if (s < MinScale) s = MinScale;
        if (s > MaxScale) s = MaxScale;
        Metrics m;
        m.Scale  = s;
        m.Pad    = 8f   * s;
        m.BarW   = 228f * s;   // wide enough for a big "cur/max" number
        m.BarH   = 26f  * s;   // taller bars → bigger, retail-legible numbers
        m.Gap    = 6f   * s;
        m.PanelW = m.Pad * 2f + m.BarW;
        m.PanelH = m.Pad * 2f + m.BarH * 3f + m.Gap * 2f;
        m.Grip   = 16f  * s;
        return m;
    }

    // ─── Color helpers ────────────────────────────────────────────────
    private static uint Argb(int a, int r, int g, int b)
        => ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;

    private static uint Lighten(uint c, int add)
    {
        static int Cl(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);
        int a = (int)((c >> 24) & 0xFF);
        int r = (int)((c >> 16) & 0xFF);
        int g = (int)((c >> 8) & 0xFF);
        int b = (int)(c & 0xFF);
        return Argb(a, Cl(r + add), Cl(g + add), Cl(b + add));
    }

    // ─── Batch builders ───────────────────────────────────────────────
    private static void BatchReset() => _batchCount = 0;

    private static void PushQuad(float x0, float y0, float x1, float y1,
        uint cTL, uint cTR, uint cBR, uint cBL)
    {
        if (_batchCount + 6 > MaxVerts) return;
        Set(ref _batch[_batchCount + 0], x0, y0, cTL);
        Set(ref _batch[_batchCount + 1], x1, y0, cTR);
        Set(ref _batch[_batchCount + 2], x1, y1, cBR);
        Set(ref _batch[_batchCount + 3], x0, y0, cTL);
        Set(ref _batch[_batchCount + 4], x1, y1, cBR);
        Set(ref _batch[_batchCount + 5], x0, y1, cBL);
        _batchCount += 6;

        static void Set(ref Vertex v, float x, float y, uint c)
        {
            v.X = x; v.Y = y; v.Z = 0f; v.Rhw = 1f; v.Color = c;
        }
    }

    private static void PushRect(float x0, float y0, float x1, float y1, uint c)
        => PushQuad(x0, y0, x1, y1, c, c, c, c);

    private static void PushVGrad(float x0, float y0, float x1, float y1, uint top, uint bot)
        => PushQuad(x0, y0, x1, y1, top, top, bot, bot);

    // ─── Text → batched quads via the bitmap font ─────────────────────
    private static void TextCell(int x0, int y0, int x1, int y1)
        => PushRect(x0, y0, x1, y1, _textColor);

    // Draws text with a full dark outline (all 8 neighbour offsets) then the
    // foreground on top, so it stays legible over any bar fill color — the
    // single drop-shadow was washed out on the light (yellow) bar.
    private static void DrawText(ReadOnlySpan<char> s, int x, int y, int cellPx, uint color)
    {
        int o = cellPx >= 4 ? 2 : 1;                       // outline thickness in px
        _textColor = Argb(235, 0, 0, 0);
        BitmapFont.Emit(s, x - o, y - o, cellPx, _cellSink);
        BitmapFont.Emit(s, x,     y - o, cellPx, _cellSink);
        BitmapFont.Emit(s, x + o, y - o, cellPx, _cellSink);
        BitmapFont.Emit(s, x - o, y,     cellPx, _cellSink);
        BitmapFont.Emit(s, x + o, y,     cellPx, _cellSink);
        BitmapFont.Emit(s, x - o, y + o, cellPx, _cellSink);
        BitmapFont.Emit(s, x,     y + o, cellPx, _cellSink);
        BitmapFont.Emit(s, x + o, y + o, cellPx, _cellSink);
        _textColor = color;
        BitmapFont.Emit(s, x, y, cellPx, _cellSink);
    }

    // "cur/max" formatted into the caller's buffer without allocating; returns
    // the number of chars written (≤ buf.Length).
    private static int FormatVital(uint cur, uint mx, Span<char> buf)
    {
        int len = 0;
        if (cur.TryFormat(buf, out int w1)) len += w1;
        if (len < buf.Length) buf[len++] = '/';
        if (mx.TryFormat(buf.Slice(len), out int w2)) len += w2;
        return len;
    }

    private static void EmitBar(float x, float y, in Metrics m,
        uint cur, uint mx, uint baseColor)
    {
        float w = m.BarW, h = m.BarH;

        PushRect(x, y, x + w, y + h, Argb(205, 18, 20, 26));     // track

        if (mx > 0)
        {
            float ratio = (float)cur / mx;
            if (ratio < 0f) ratio = 0f;
            if (ratio > 1f) ratio = 1f;
            float fw = w * ratio;
            PushVGrad(x, y, x + fw, y + h, Lighten(baseColor, 55), baseColor);  // fill
            PushRect(x, y, x + fw, y + h * 0.32f, Argb(60, 255, 255, 255));     // gloss
        }

        float bw = m.Scale >= 1.5f ? 2f : 1f;                   // border
        uint bc = Argb(180, 90, 100, 120);
        PushRect(x, y, x + w, y + bw, bc);
        PushRect(x, y + h - bw, x + w, y + h, bc);
        PushRect(x, y, x + bw, y + h, bc);
        PushRect(x + w - bw, y, x + w, y + h, bc);

        // Number only (no label — the red/yellow/blue color identifies the bar).
        // Sized as large as fits the bar, centered, with a dark outline so it
        // reads clearly over any fill color (especially white-on-yellow).
        Span<char> numBuf = stackalloc char[24];
        ReadOnlySpan<char> num = numBuf[..FormatVital(cur, mx, numBuf)];

        int innerPad = (int)(m.Pad * 0.4f);
        if (innerPad < 2) innerPad = 2;
        int availW = (int)w - 2 * innerPad;

        int cellPx = (int)(h * 0.82f / BitmapFont.GlyphH);   // tallest that fits the bar
        if (cellPx < 1) cellPx = 1;
        while (cellPx > 1 && BitmapFont.MeasureWidth(num, cellPx) > availW) cellPx--;

        int tw = BitmapFont.MeasureWidth(num, cellPx);
        int th = BitmapFont.Height(cellPx);
        int tx = (int)(x + (w - tw) * 0.5f);                 // center horizontally
        int ty = (int)(y + (h - th) * 0.5f);                 // center vertically
        DrawText(num, tx, ty, cellPx, Argb(255, 245, 250, 255));
    }

    // ─── Public API ───────────────────────────────────────────────────
    public static void Draw(IntPtr pDevice)
    {
        if (pDevice == IntPtr.Zero) return;
        if (!PlayerVitalsHooks.TryGetSnapshot(out PlayerVitalsSnapshot snap)) return;

        if (!_delegatesCached)
            CacheDelegates(pDevice);
        if (_drawPrimitiveUP == null) return; // delegate cache failed

        if (_getViewport!(pDevice, out D3DVIEWPORT9 vp) < 0) return;
        float wF = vp.Width;
        float hF = vp.Height;
        if (wF < 1f || hF < 1f) return;

        if (!_firstDrawLogged)
        {
            _firstDrawLogged = true;
            RynthLog.D3D9("VitalHud: first draw.");
        }

        // First-draw placement: prefer the saved config, else default top-right.
        if (!_geomInit)
        {
            _geomInit = true;
            if (LoadCfg(out float lx, out float ly, out float ls))
            {
                _scale = ls; _hudX = lx; _hudY = ly;
            }
            else
            {
                _scale = 1f;
                Metrics dm = Metric(1f);
                _hudX = wF - dm.PanelW - 16f;
                _hudY = 16f;
            }
        }

        // ── Save the device state we are about to clobber ──────────────
        _getRenderState!(pDevice, D3DRS_ZENABLE, out uint svZE);
        _getRenderState(pDevice, D3DRS_ZWRITEENABLE, out uint svZWE);
        _getRenderState(pDevice, D3DRS_ALPHATESTENABLE, out uint svATE);
        _getRenderState(pDevice, D3DRS_ALPHABLENDENABLE, out uint svABE);
        _getRenderState(pDevice, D3DRS_SRCBLEND, out uint svSB);
        _getRenderState(pDevice, D3DRS_DESTBLEND, out uint svDB);
        _getRenderState(pDevice, D3DRS_BLENDOP, out uint svBO);
        _getRenderState(pDevice, D3DRS_CULLMODE, out uint svCull);
        _getRenderState(pDevice, D3DRS_LIGHTING, out uint svLit);
        _getRenderState(pDevice, D3DRS_FOGENABLE, out uint svFog);
        _getRenderState(pDevice, D3DRS_STENCILENABLE, out uint svSte);
        _getRenderState(pDevice, D3DRS_SCISSORTESTENABLE, out uint svSci);
        _getRenderState(pDevice, D3DRS_COLORWRITEENABLE, out uint svCW);
        _getFVF!(pDevice, out uint svFvf);

        _getTextureStageState!(pDevice, 0, D3DTSS_COLOROP, out uint svT0CO);
        _getTextureStageState(pDevice, 0, D3DTSS_COLORARG1, out uint svT0CA1);
        _getTextureStageState(pDevice, 0, D3DTSS_ALPHAOP, out uint svT0AO);
        _getTextureStageState(pDevice, 0, D3DTSS_ALPHAARG1, out uint svT0AA1);
        _getTextureStageState(pDevice, 1, D3DTSS_COLOROP, out uint svT1CO);

        _getVertexShader!(pDevice, out IntPtr svVS);
        _getPixelShader!(pDevice, out IntPtr svPS);
        _getTexture!(pDevice, 0, out IntPtr svTex0);

        try
        {
            // ── Set HUD state: 2D, alpha-blended, colored verts, no tex/shaders ──
            _setRenderState!(pDevice, D3DRS_ZENABLE, 0);
            _setRenderState(pDevice, D3DRS_ZWRITEENABLE, 0);
            _setRenderState(pDevice, D3DRS_ALPHATESTENABLE, 0);
            _setRenderState(pDevice, D3DRS_ALPHABLENDENABLE, 1);
            _setRenderState(pDevice, D3DRS_SRCBLEND, D3DBLEND_SRCALPHA);
            _setRenderState(pDevice, D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA);
            _setRenderState(pDevice, D3DRS_BLENDOP, D3DBLENDOP_ADD);
            _setRenderState(pDevice, D3DRS_CULLMODE, D3DCULL_NONE);
            _setRenderState(pDevice, D3DRS_LIGHTING, 0);
            _setRenderState(pDevice, D3DRS_FOGENABLE, 0);
            _setRenderState(pDevice, D3DRS_STENCILENABLE, 0);
            _setRenderState(pDevice, D3DRS_SCISSORTESTENABLE, 0);
            _setRenderState(pDevice, D3DRS_COLORWRITEENABLE, 0xF);

            _setTextureStageState!(pDevice, 0, D3DTSS_COLOROP, D3DTOP_SELECTARG1);
            _setTextureStageState(pDevice, 0, D3DTSS_COLORARG1, D3DTA_DIFFUSE);
            _setTextureStageState(pDevice, 0, D3DTSS_ALPHAOP, D3DTOP_SELECTARG1);
            _setTextureStageState(pDevice, 0, D3DTSS_ALPHAARG1, D3DTA_DIFFUSE);
            _setTextureStageState(pDevice, 1, D3DTSS_COLOROP, D3DTOP_DISABLE);

            _setTexture!(pDevice, 0, IntPtr.Zero);
            _setVertexShader!(pDevice, IntPtr.Zero);
            _setPixelShader!(pDevice, IntPtr.Zero);
            _setFVF!(pDevice, HudFvf);

            BuildBatch(in snap, wF, hF);

            if (_batchCount >= 3)
            {
                fixed (Vertex* p = _batch)
                {
                    _drawPrimitiveUP(pDevice, D3DPT_TRIANGLELIST,
                        (uint)(_batchCount / 3), (IntPtr)p, (uint)sizeof(Vertex));
                }
            }

            _vpW = wF;   // for input hit-test clamping
            _vpH = hF;
        }
        finally
        {
            // ── Restore the saved state ────────────────────────────────
            _setRenderState!(pDevice, D3DRS_ZENABLE, svZE);
            _setRenderState(pDevice, D3DRS_ZWRITEENABLE, svZWE);
            _setRenderState(pDevice, D3DRS_ALPHATESTENABLE, svATE);
            _setRenderState(pDevice, D3DRS_ALPHABLENDENABLE, svABE);
            _setRenderState(pDevice, D3DRS_SRCBLEND, svSB);
            _setRenderState(pDevice, D3DRS_DESTBLEND, svDB);
            _setRenderState(pDevice, D3DRS_BLENDOP, svBO);
            _setRenderState(pDevice, D3DRS_CULLMODE, svCull);
            _setRenderState(pDevice, D3DRS_LIGHTING, svLit);
            _setRenderState(pDevice, D3DRS_FOGENABLE, svFog);
            _setRenderState(pDevice, D3DRS_STENCILENABLE, svSte);
            _setRenderState(pDevice, D3DRS_SCISSORTESTENABLE, svSci);
            _setRenderState(pDevice, D3DRS_COLORWRITEENABLE, svCW);

            _setTextureStageState!(pDevice, 0, D3DTSS_COLOROP, svT0CO);
            _setTextureStageState(pDevice, 0, D3DTSS_COLORARG1, svT0CA1);
            _setTextureStageState(pDevice, 0, D3DTSS_ALPHAOP, svT0AO);
            _setTextureStageState(pDevice, 0, D3DTSS_ALPHAARG1, svT0AA1);
            _setTextureStageState(pDevice, 1, D3DTSS_COLOROP, svT1CO);

            _setFVF!(pDevice, svFvf);
            _setVertexShader!(pDevice, svVS);
            _setPixelShader!(pDevice, svPS);
            _setTexture!(pDevice, 0, svTex0);

            // GetVertexShader / GetPixelShader / GetTexture each AddRef — release.
            ReleaseCom(svVS);
            ReleaseCom(svPS);
            ReleaseCom(svTex0);
        }
    }

    private static void BuildBatch(in PlayerVitalsSnapshot snap, float wF, float hF)
    {
        Metrics m = Metric(_scale);
        float px = _hudX;
        float py = _hudY;
        if (px > wF - m.PanelW) px = wF - m.PanelW;   // keep on-screen (draw-only)
        if (py > hF - m.PanelH) py = hF - m.PanelH;
        if (px < 0f) px = 0f;
        if (py < 0f) py = 0f;

        BatchReset();

        float so = 5f * m.Scale;                                       // shadow
        PushRect(px + so, py + so, px + m.PanelW + so, py + m.PanelH + so, Argb(110, 0, 0, 0));
        PushVGrad(px, py, px + m.PanelW, py + m.PanelH,                 // backing
            Argb(205, 28, 30, 38), Argb(205, 16, 17, 22));
        PushRect(px, py, px + m.PanelW, py + m.Pad * 0.6f, Argb(45, 255, 255, 255)); // header hint

        float pbw = m.Scale >= 1.5f ? 2f : 1f;                         // panel border
        uint pbc = Argb(200, 120, 130, 150);
        PushRect(px, py, px + m.PanelW, py + pbw, pbc);
        PushRect(px, py + m.PanelH - pbw, px + m.PanelW, py + m.PanelH, pbc);
        PushRect(px, py, px + pbw, py + m.PanelH, pbc);
        PushRect(px + m.PanelW - pbw, py, px + m.PanelW, py + m.PanelH, pbc);

        float bx = px + m.Pad;
        float by = py + m.Pad;
        EmitBar(bx, by, in m, snap.Health,  snap.MaxHealth,  Argb(235, 200, 45, 45));
        by += m.BarH + m.Gap;
        EmitBar(bx, by, in m, snap.Stamina, snap.MaxStamina, Argb(235, 205, 170, 45));
        by += m.BarH + m.Gap;
        EmitBar(bx, by, in m, snap.Mana,    snap.MaxMana,    Argb(235, 55, 115, 235));

        // resize grip (three dots) bottom-right
        float d = 3f * m.Scale;
        if (d < 2f) d = 2f;
        float gx = px + m.PanelW - m.Pad * 0.5f;
        float gy = py + m.PanelH - m.Pad * 0.5f;
        uint gc = Argb(200, 200, 205, 215);
        for (int i = 1; i <= 3; i++)
        {
            float ox = gx - i * (d + 1f);
            float oy = gy - i * (d + 1f);
            PushRect(ox, oy, ox + d, oy + d, gc);
        }
    }

    // ─── Input ────────────────────────────────────────────────────────

    /// <summary>
    /// Handles left-button drag (anywhere on the panel) + grip resize. Returns
    /// true when the message landed on the HUD and should be swallowed so it
    /// never reaches AC. Called from Win32Backend.WndProcHook on AC's main
    /// thread, before the Avalonia forward. No-op (returns false) when the HUD
    /// is not being drawn.
    /// </summary>
    public static bool TryHandleMouse(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!_geomInit || !EngineSettings.DrawCustomVitalBars)
            return false;

        switch (msg)
        {
            case WM_LBUTTONDOWN:
            {
                float mxf = (short)((long)lParam & 0xFFFF);
                float myf = (short)(((long)lParam >> 16) & 0xFFFF);
                Metrics m = Metric(_scale);

                // Clamp the same way Draw does so the hit-test matches the pixels.
                float px = _hudX, py = _hudY;
                if (_vpW > 0f && px > _vpW - m.PanelW) px = _vpW - m.PanelW;
                if (_vpH > 0f && py > _vpH - m.PanelH) py = _vpH - m.PanelH;
                if (px < 0f) px = 0f;
                if (py < 0f) py = 0f;

                bool inPanel = mxf >= px && mxf <= px + m.PanelW &&
                               myf >= py && myf <= py + m.PanelH;
                if (!inPanel) return false;

                // Pin the (clamped) top-left so move/resize anchor to the pixels.
                _hudX = px; _hudY = py;

                bool inGrip = mxf >= px + m.PanelW - m.Grip && myf >= py + m.PanelH - m.Grip;
                if (inGrip)
                {
                    float dx = mxf - px, dy = myf - py;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist < 8f) dist = 8f;
                    _resizeStartDist = dist;
                    _resizeStartScale = m.Scale;
                    _dragMode = 2;
                }
                else
                {
                    _grabDX = mxf - px;
                    _grabDY = myf - py;
                    _dragMode = 1;
                }
                SetCapture(hWnd);
                return true;
            }

            case WM_MOUSEMOVE:
            {
                if (_dragMode == 0) return false;
                // Left button no longer held (we missed the UP — e.g. capture
                // stolen by alt-tab): end the drag and let the move through so the
                // HUD can't get stuck following the cursor.
                if (((long)wParam & 0x0001 /* MK_LBUTTON */) == 0)
                {
                    _dragMode = 0;
                    ReleaseCapture();
                    SaveCfg();
                    return false;
                }
                float mxf = (short)((long)lParam & 0xFFFF);
                float myf = (short)(((long)lParam >> 16) & 0xFFFF);

                if (_dragMode == 1)                                // move
                {
                    float nx = mxf - _grabDX;
                    float ny = myf - _grabDY;
                    Metrics m = Metric(_scale);
                    if (_vpW > 0f && nx > _vpW - m.PanelW) nx = _vpW - m.PanelW;
                    if (_vpH > 0f && ny > _vpH - m.PanelH) ny = _vpH - m.PanelH;
                    if (nx < 0f) nx = 0f;
                    if (ny < 0f) ny = 0f;
                    _hudX = nx; _hudY = ny;
                }
                else                                               // resize
                {
                    float dx = mxf - _hudX, dy = myf - _hudY;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    float s = _resizeStartScale * dist / _resizeStartDist;
                    if (s < MinScale) s = MinScale;
                    if (s > MaxScale) s = MaxScale;
                    _scale = s;
                }
                return true;
            }

            case WM_LBUTTONUP:
            {
                if (_dragMode == 0) return false;
                _dragMode = 0;
                ReleaseCapture();
                SaveCfg();
                return true;
            }

            default:
                return false;
        }
    }

    // ─── Persistence: %APPDATA%\RynthCore\vitalhud.cfg ("x y scale") ──
    private static string? CfgPath()
    {
        string? appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrEmpty(appData)) return null;
        return Path.Combine(appData, "RynthCore", "vitalhud.cfg");
    }

    private static bool LoadCfg(out float x, out float y, out float s)
    {
        x = 0f; y = 0f; s = 1f;
        try
        {
            string? path = CfgPath();
            if (path == null || !File.Exists(path)) return false;
            string[] parts = File.ReadAllText(path)
                .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float fx) ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float fy) ||
                !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float fs))
                return false;
            if (fs < MinScale) fs = MinScale;
            if (fs > MaxScale) fs = MaxScale;
            if (fx < 0f) fx = 0f;
            if (fy < 0f) fy = 0f;
            x = fx; y = fy; s = fs;
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.D3D9($"VitalHud: LoadCfg failed - {ex.Message}");
            return false;
        }
    }

    private static void SaveCfg()
    {
        try
        {
            string? path = CfgPath();
            if (path == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string body = string.Format(CultureInfo.InvariantCulture, "{0:F1} {1:F1} {2:F4}", _hudX, _hudY, _scale);
            File.WriteAllText(path, body);
        }
        catch (Exception ex)
        {
            RynthLog.D3D9($"VitalHud: SaveCfg failed - {ex.Message}");
        }
    }

    // ─── Internals ────────────────────────────────────────────────────
    private static void CacheDelegates(IntPtr pDevice)
    {
        IntPtr vtable = Marshal.ReadIntPtr(pDevice);

        _setRenderState       = Get<SetRenderStateDelegate>(vtable, DeviceVTableIndex.SetRenderState);
        _getRenderState       = Get<GetRenderStateDelegate>(vtable, DeviceVTableIndex.GetRenderState);
        _setTextureStageState = Get<SetTextureStageStateDelegate>(vtable, DeviceVTableIndex.SetTextureStageState);
        _getTextureStageState = Get<GetTextureStageStateDelegate>(vtable, DeviceVTableIndex.GetTextureStageState);
        _setTexture           = Get<SetTextureDelegate>(vtable, DeviceVTableIndex.SetTexture);
        _getTexture           = Get<GetTextureDelegate>(vtable, DeviceVTableIndex.GetTexture);
        _setFVF               = Get<SetFVFDelegate>(vtable, DeviceVTableIndex.SetFVF);
        _getFVF               = Get<GetFVFDelegate>(vtable, DeviceVTableIndex.GetFVF);
        _setVertexShader      = Get<SetVertexShaderDelegate>(vtable, DeviceVTableIndex.SetVertexShader);
        _getVertexShader      = Get<GetVertexShaderDelegate>(vtable, DeviceVTableIndex.GetVertexShader);
        _setPixelShader       = Get<SetPixelShaderDelegate>(vtable, DeviceVTableIndex.SetPixelShader);
        _getPixelShader       = Get<GetPixelShaderDelegate>(vtable, DeviceVTableIndex.GetPixelShader);
        _drawPrimitiveUP      = Get<DrawPrimitiveUPDelegate>(vtable, DeviceVTableIndex.DrawPrimitiveUP);
        _getViewport          = Get<GetViewportDelegate>(vtable, DeviceVTableIndex.GetViewport);

        _delegatesCached = true;
        RynthLog.D3D9("VitalHud: D3D9 delegates cached.");
    }

    private static T Get<T>(IntPtr vtable, int index) where T : Delegate
    {
        IntPtr addr = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    private static void ReleaseCom(IntPtr pUnk)
    {
        if (pUnk == IntPtr.Zero) return;
        IntPtr vtbl = Marshal.ReadIntPtr(pUnk);
        IntPtr releaseAddr = Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size); // IUnknown::Release
        var release = Marshal.GetDelegateForFunctionPointer<ReleaseComDelegate>(releaseAddr);
        release(pUnk);
    }
}

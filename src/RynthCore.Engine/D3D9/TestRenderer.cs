// ═══════════════════════════════════════════════════════════════════════════
//  RynthCore.Engine — D3D9/TestRenderer.cs
//  Draws a small colored rectangle in the top-left corner of the game.
//  Pure D3D9 via vtable calls — no dependencies on any overlay framework.
//
//  Uses pre-transformed vertices (D3DFVF_XYZRHW | D3DFVF_DIFFUSE) so no
//  world/view/projection matrices needed. The rectangle is screen-space.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.D3D9;

internal static unsafe class TestRenderer
{
    // ─── D3D9 constants ───────────────────────────────────────────────
    private const uint D3DFVF_XYZRHW  = 0x0004;
    private const uint D3DFVF_DIFFUSE = 0x0040;
    private const uint D3DFVF_CUSTOM  = D3DFVF_XYZRHW | D3DFVF_DIFFUSE;

    private const uint D3DPT_TRIANGLESTRIP = 5;

    // D3DRENDERSTATETYPE
    private const uint D3DRS_LIGHTING              = 137;
    private const uint D3DRS_ALPHABLENDENABLE      = 27;
    private const uint D3DRS_SRCBLEND              = 19;
    private const uint D3DRS_DESTBLEND             = 20;
    private const uint D3DRS_ZENABLE               = 7;
    private const uint D3DRS_FOGENABLE             = 28;

    // D3DBLEND
    private const uint D3DBLEND_SRCALPHA           = 5;
    private const uint D3DBLEND_INVSRCALPHA        = 6;

    // D3DTRANSFORMSTATETYPE
    private const uint D3DTS_WORLD                 = 256;
    private const uint D3DTS_VIEW                  = 2;
    private const uint D3DTS_PROJECTION            = 3;

    // ─── Vertex structure ─────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public float X, Y, Z, RHW;
        public uint  Color; // ARGB

        public Vertex(float x, float y, uint color)
        {
            X = x; Y = y; Z = 0f; RHW = 1f;
            Color = color;
        }
    }

    // ─── Device method delegates (COM/stdcall) ────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetRenderStateDelegate(IntPtr pDevice, uint state, uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetRenderStateDelegate(IntPtr pDevice, uint state, out uint pValue);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetTextureDelegate(IntPtr pDevice, uint stage, IntPtr pTexture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetFVFDelegate(IntPtr pDevice, uint fvf);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DrawPrimitiveUPDelegate(
        IntPtr pDevice, uint primitiveType, uint primitiveCount,
        IntPtr pVertexStreamZeroData, uint vertexStreamZeroStride);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetVertexShaderDelegate(IntPtr pDevice, IntPtr pShader);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPixelShaderDelegate(IntPtr pDevice, IntPtr pShader);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetTextureStageStateDelegate(IntPtr pDevice, uint stage, uint type, uint value);

    // ─── Cached delegates (created once from vtable on first frame) ──
    private static SetRenderStateDelegate?       _setRenderState;
    private static GetRenderStateDelegate?       _getRenderState;
    private static SetTextureDelegate?           _setTexture;
    private static SetFVFDelegate?               _setFVF;
    private static DrawPrimitiveUPDelegate?      _drawPrimitiveUP;
    private static SetVertexShaderDelegate?      _setVertexShader;
    private static SetPixelShaderDelegate?       _setPixelShader;
    private static SetTextureStageStateDelegate? _setTextureStageState;
    private static bool _delegatesCached;

    // ─── Rectangle config ─────────────────────────────────────────────
    // Bright green semi-transparent rectangle, top-left corner
    private const float RectX = 10f;
    private const float RectY = 10f;
    private const float RectW = 120f;
    private const float RectH = 40f;
    private const uint  RectColor = 0xCC00FF00; // ARGB: green, ~80% opaque

    // ─── Public API ───────────────────────────────────────────────────

    public static void Draw(IntPtr pDevice)
    {
        if (pDevice == IntPtr.Zero) return;

        // Cache vtable delegates on first call
        if (!_delegatesCached)
            CacheDelegates(pDevice);

        // Save render states we'll modify
        _getRenderState!(pDevice, D3DRS_LIGHTING, out uint oldLighting);
        _getRenderState(pDevice, D3DRS_ALPHABLENDENABLE, out uint oldAlphaBlend);
        _getRenderState(pDevice, D3DRS_SRCBLEND, out uint oldSrcBlend);
        _getRenderState(pDevice, D3DRS_DESTBLEND, out uint oldDestBlend);
        _getRenderState(pDevice, D3DRS_ZENABLE, out uint oldZEnable);
        _getRenderState(pDevice, D3DRS_FOGENABLE, out uint oldFogEnable);

        // Set up for 2D overlay drawing
        _setRenderState!(pDevice, D3DRS_LIGHTING, 0);          // no lighting
        _setRenderState(pDevice, D3DRS_ZENABLE, 0);            // no depth test
        _setRenderState(pDevice, D3DRS_FOGENABLE, 0);          // no fog
        _setRenderState(pDevice, D3DRS_ALPHABLENDENABLE, 1);   // alpha blend on
        _setRenderState(pDevice, D3DRS_SRCBLEND, D3DBLEND_SRCALPHA);
        _setRenderState(pDevice, D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA);

        _setTexture!(pDevice, 0, IntPtr.Zero);                 // no texture
        _setVertexShader!(pDevice, IntPtr.Zero);               // fixed function
        _setPixelShader!(pDevice, IntPtr.Zero);                // fixed function
        _setFVF!(pDevice, D3DFVF_CUSTOM);

        // Build quad (triangle strip: TL, TR, BL, BR)
        var verts = stackalloc Vertex[4];
        verts[0] = new Vertex(RectX,         RectY,         RectColor);
        verts[1] = new Vertex(RectX + RectW, RectY,         RectColor);
        verts[2] = new Vertex(RectX,         RectY + RectH, RectColor);
        verts[3] = new Vertex(RectX + RectW, RectY + RectH, RectColor);

        _drawPrimitiveUP!(pDevice, D3DPT_TRIANGLESTRIP, 2, (IntPtr)verts, (uint)sizeof(Vertex));

        // Restore saved render states
        _setRenderState(pDevice, D3DRS_LIGHTING, oldLighting);
        _setRenderState(pDevice, D3DRS_ALPHABLENDENABLE, oldAlphaBlend);
        _setRenderState(pDevice, D3DRS_SRCBLEND, oldSrcBlend);
        _setRenderState(pDevice, D3DRS_DESTBLEND, oldDestBlend);
        _setRenderState(pDevice, D3DRS_ZENABLE, oldZEnable);
        _setRenderState(pDevice, D3DRS_FOGENABLE, oldFogEnable);
    }

    // ─── Internals ────────────────────────────────────────────────────

    private static void CacheDelegates(IntPtr pDevice)
    {
        IntPtr vtable = Marshal.ReadIntPtr(pDevice);

        _setRenderState       = GetMethod<SetRenderStateDelegate>(vtable, DeviceVTableIndex.SetRenderState);
        _getRenderState       = GetMethod<GetRenderStateDelegate>(vtable, DeviceVTableIndex.GetRenderState);
        _setTexture           = GetMethod<SetTextureDelegate>(vtable, DeviceVTableIndex.SetTexture);
        _setFVF               = GetMethod<SetFVFDelegate>(vtable, DeviceVTableIndex.SetFVF);
        _drawPrimitiveUP      = GetMethod<DrawPrimitiveUPDelegate>(vtable, DeviceVTableIndex.DrawPrimitiveUP);
        _setVertexShader      = GetMethod<SetVertexShaderDelegate>(vtable, DeviceVTableIndex.SetVertexShader);
        _setPixelShader       = GetMethod<SetPixelShaderDelegate>(vtable, DeviceVTableIndex.SetPixelShader);
        _setTextureStageState = GetMethod<SetTextureStageStateDelegate>(vtable, DeviceVTableIndex.SetTextureStageState);

        _delegatesCached = true;
        RynthLog.D3D9("TestRenderer: D3D9 delegates cached.");
    }

    private static T GetMethod<T>(IntPtr vtable, int index) where T : Delegate
    {
        IntPtr addr = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(addr);
    }
}

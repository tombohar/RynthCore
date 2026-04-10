// ============================================================================
//  RynthCore.Engine - D3D9/OverlayTextureRenderer.cs
//  Consumes BGRA frames from OverlayFrameBuffer and composites them onto
//  the D3D9 back buffer as a fullscreen alpha-blended quad.
//
//  Called at the end of OnEndScene, after ImGui has already rendered.
//  Uses D3DFVF_XYZRHW | D3DFVF_TEX1 (pre-transformed vertices) so no
//  projection matrix setup is needed.
//
//  Pixel format: Avalonia Bgra8888 == D3DFMT_A8R8G8B8 in memory — no swizzle.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using RynthCore.Engine.UI;

namespace RynthCore.Engine.D3D9;

internal static unsafe class OverlayTextureRenderer
{
    private static readonly Guid IID_IDirect3DDevice9Ex = new("B18B10CE-2649-405A-870F-95F777D4313A");
    // ─── D3D9 constants ───────────────────────────────────────────────────
    private const uint D3DPT_TRIANGLESTRIP = 5;
    private const uint D3DUSAGE_RENDERTARGET = 0x00000001;
    private const uint D3DFMT_A8R8G8B8    = 21;
    private const uint D3DPOOL_DEFAULT    = 0;
    private const uint D3DPOOL_MANAGED    = 1;

    private const uint D3DRS_ALPHABLENDENABLE = 27;
    private const uint D3DRS_SRCBLEND         = 19;
    private const uint D3DRS_DESTBLEND        = 20;
    private const uint D3DRS_ZENABLE          = 7;
    private const uint D3DRS_CULLMODE         = 22;
    private const uint D3DRS_LIGHTING         = 137;
    private const uint D3DRS_COLORWRITEENABLE = 168;

    private const uint D3DBLEND_SRCALPHA    = 5;
    private const uint D3DBLEND_INVSRCALPHA = 6;
    private const uint D3DCULL_NONE         = 1;

    private const uint D3DTSS_COLOROP   = 1;
    private const uint D3DTSS_COLORARG1 = 2;
    private const uint D3DTSS_COLORARG2 = 3;
    private const uint D3DTSS_ALPHAOP   = 4;
    private const uint D3DTSS_ALPHAARG1 = 5;
    private const uint D3DTSS_ALPHAARG2 = 6;

    private const uint D3DTOP_MODULATE  = 4;
    private const uint D3DTA_TEXTURE    = 2;
    private const uint D3DTA_DIFFUSE    = 0;

    private const uint D3DSAMP_MINFILTER = 5;
    private const uint D3DSAMP_MAGFILTER = 6;
    private const uint D3DTEXF_LINEAR   = 2;
    private const uint D3DTEXF_POINT    = 1;

    // D3DFVF_XYZRHW | D3DFVF_TEX1
    private const uint FVF = 0x004 | 0x100;

    // ─── Vertex (pre-transformed screen-space + UV) ────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct QuadVertex
    {
        public float X, Y, Z, RHW;
        public float U, V;
    }

    // ─── D3D9 struct ──────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DVIEWPORT9
    {
        public uint X, Y, Width, Height;
        public float MinZ, MaxZ;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DLOCKED_RECT
    {
        public int   Pitch;
        public IntPtr pBits;
    }

    // ─── Vtable delegates ─────────────────────────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTextureD(IntPtr dev, uint w, uint h, uint levels, uint usage, uint fmt, uint pool, out IntPtr ppTex, IntPtr pShared);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetTextureD(IntPtr dev, uint stage, IntPtr pTex);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetTextureStageStateD(IntPtr dev, uint stage, uint type, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetSamplerStateD(IntPtr dev, uint sampler, uint type, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetFVFD(IntPtr dev, uint fvf);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetRenderStateD(IntPtr dev, uint state, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetRenderStateD(IntPtr dev, uint state, out uint pValue);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetVertexShaderD(IntPtr dev, IntPtr pShader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetVertexShaderD(IntPtr dev, out IntPtr ppShader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPixelShaderD(IntPtr dev, IntPtr pShader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPixelShaderD(IntPtr dev, out IntPtr ppShader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetViewportD(IntPtr dev, D3DVIEWPORT9* vp);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DrawPrimitiveUPD(IntPtr dev, uint primType, uint primCount, IntPtr pVtxData, uint vtxStride);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseD(IntPtr pObj);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceD(IntPtr pObj, Guid* riid, out IntPtr ppvObject);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TexLockRectD(IntPtr pTex, uint level, D3DLOCKED_RECT* pLocked, IntPtr pRect, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TexUnlockRectD(IntPtr pTex, uint level);

    // ─── Cached delegates ─────────────────────────────────────────────────
    private static CreateTextureD?      _createTexture;
    private static SetTextureD?         _setTexture;
    private static SetTextureStageStateD? _setTexStageState;
    private static SetSamplerStateD?    _setSamplerState;
    private static SetFVFD?             _setFVF;
    private static SetRenderStateD?     _setRenderState;
    private static GetRenderStateD?     _getRenderState;
    private static SetVertexShaderD?    _setVertexShader;
    private static GetVertexShaderD?    _getVertexShader;
    private static SetPixelShaderD?     _setPixelShader;
    private static GetPixelShaderD?     _getPixelShader;
    private static GetViewportD?        _getViewport;
    private static DrawPrimitiveUPD?    _drawPrimitiveUP;
    private static QueryInterfaceD?     _queryInterface;

    // ─── State ────────────────────────────────────────────────────────────
    private static IntPtr _texture;
    private static IntPtr _sharedTextureHandle;
    private static int    _texW;
    private static int    _texH;
    private static bool   _delegatesCached;
    private static bool   _sharedInteropProbed;
    private static bool   _sharedInteropSupported;

    // ─── Public entry point ───────────────────────────────────────────────

    /// <summary>
    /// Called from EndScene (D3D9 render thread) after ImGui has rendered.
    /// Uploads any pending Avalonia frame to the texture and draws it.
    /// </summary>
    public static void Render(IntPtr pDevice)
    {
        if (pDevice == IntPtr.Zero)
            return;

        if (!_delegatesCached)
        {
            CacheDelegates(pDevice);
            _delegatesCached = true;
        }

        // Learn as early as possible whether this game device can consume shared
        // textures. That lets the producer stay on software submissions only on
        // unsupported clients instead of discovering it mid-session.
        if (OverlaySurfaceBridge.ActiveSupportsGpuInterop && !_sharedInteropProbed)
            EnsureSharedInteropSupport(pDevice);

        // Upload new frame if one is available
        if (OverlaySurfaceBridge.TryConsume(out OverlaySurfaceFrame? frame) && frame != null)
        {
            switch (frame.Kind)
            {
                case OverlaySurfaceKind.SoftwareBgra32 when frame.SoftwarePixels != null:
                    UploadFrame(pDevice, frame.SoftwarePixels, frame.Width, frame.Height);
                    break;
                case OverlaySurfaceKind.AngleSharedTexture when frame.SharedTexture != null:
                    if (!TryUseSharedTexture(pDevice, "ANGLE", frame.SharedTexture.Value))
                        LogSharedTextureStub("ANGLE", frame.SharedTexture);
                    break;
                case OverlaySurfaceKind.DxgiSharedTexture when frame.SharedTexture != null:
                    if (!TryUseSharedTexture(pDevice, "DXGI", frame.SharedTexture.Value))
                        LogSharedTextureStub("DXGI", frame.SharedTexture);
                    break;
                case OverlaySurfaceKind.D3D9SharedTexture when frame.SharedTexture != null:
                    if (!TryUseSharedTexture(pDevice, "D3D9Ex", frame.SharedTexture.Value))
                        LogSharedTextureStub("D3D9Ex", frame.SharedTexture);
                    break;
            }

        }

        if (_texture == IntPtr.Zero)
            return;

        DrawQuad(pDevice);
    }

    private static void LogSharedTextureStub(string label, OverlaySharedTextureDescriptor? descriptor)
    {
        if (descriptor == null)
        {
            RynthLog.D3D9($"OverlayTextureRenderer: {label} shared texture frame arrived without a descriptor.");
            return;
        }

        RynthLog.D3D9(
            $"OverlayTextureRenderer: {label} shared texture interop is not implemented yet " +
            $"(handle=0x{descriptor.Value.SharedHandle:X8}, size={descriptor.Value.Width}x{descriptor.Value.Height}, adapterLuid=0x{descriptor.Value.AdapterLuid:X}).");
    }

    public static void Shutdown()
    {
        ReleaseTexture();
        _delegatesCached = false;
        _sharedInteropProbed = false;
        _sharedInteropSupported = false;
    }

    // ─── Upload ───────────────────────────────────────────────────────────

    private static void UploadFrame(IntPtr pDevice, byte[] pixels, int w, int h)
    {
        // Recreate texture if dimensions changed
        if (_texture == IntPtr.Zero || w != _texW || h != _texH || _sharedTextureHandle != IntPtr.Zero)
        {
            ReleaseTexture();

            int hr = _createTexture!(pDevice, (uint)w, (uint)h, 1, 0,
                D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, out _texture, IntPtr.Zero);

            if (hr < 0 || _texture == IntPtr.Zero)
            {
                RynthLog.D3D9($"OverlayTextureRenderer: CreateTexture({w}x{h}) failed HRESULT=0x{hr:X8}");
                return;
            }

            _texW = w;
            _texH = h;
            RynthLog.D3D9($"OverlayTextureRenderer: Created {w}x{h} overlay texture.");
        }

        // Lock → copy → unlock
        var lockRect  = GetTexMethod<TexLockRectD>(_texture, TextureVTableIndex.LockRect);
        var unlockRect = GetTexMethod<TexUnlockRectD>(_texture, TextureVTableIndex.UnlockRect);

        D3DLOCKED_RECT locked;
        if (lockRect(_texture, 0, &locked, IntPtr.Zero, 0) < 0)
            return;

        // Avalonia Bgra8888 == D3DFMT_A8R8G8B8 in memory — direct copy, no swizzle
        int srcStride = w * 4;
        for (int row = 0; row < h; row++)
        {
            IntPtr dst = locked.pBits + row * locked.Pitch;
            fixed (byte* src = pixels)
                Buffer.MemoryCopy(src + row * srcStride, (void*)dst, srcStride, srcStride);
        }

        unlockRect(_texture, 0);
    }

    private static bool TryUseSharedTexture(IntPtr pDevice, string label, OverlaySharedTextureDescriptor descriptor)
    {
        if (descriptor.SharedHandle == IntPtr.Zero)
            return false;

        if (!EnsureSharedInteropSupport(pDevice))
            return false;

        if (_texture != IntPtr.Zero &&
            _sharedTextureHandle == descriptor.SharedHandle &&
            _texW == descriptor.Width &&
            _texH == descriptor.Height)
        {
            OverlaySurfaceBridge.NotifySharedTextureOpened(descriptor.SharedHandle);
            return true;
        }

        IntPtr openedTexture = IntPtr.Zero;
        IntPtr sharedHandle = descriptor.SharedHandle;
        uint usage = descriptor.Usage != 0 ? (uint)descriptor.Usage : D3DUSAGE_RENDERTARGET;
        uint format = descriptor.PixelFormat != 0 ? (uint)descriptor.PixelFormat : D3DFMT_A8R8G8B8;
        int hr = _createTexture!(
            pDevice,
            (uint)descriptor.Width,
            (uint)descriptor.Height,
            1,
            usage,
            format,
            D3DPOOL_DEFAULT,
            out openedTexture,
            new IntPtr(&sharedHandle));

        if (hr < 0 || openedTexture == IntPtr.Zero)
        {
            RynthLog.D3D9(
                $"OverlayTextureRenderer: Failed to open {label} shared texture handle 0x{descriptor.SharedHandle:X8}, HRESULT=0x{hr:X8}.");
            return false;
        }

        ReleaseTexture();
        _texture = openedTexture;
        _sharedTextureHandle = descriptor.SharedHandle;
        _texW = descriptor.Width;
        _texH = descriptor.Height;
        OverlaySurfaceBridge.NotifySharedTextureOpened(descriptor.SharedHandle);
        RynthLog.D3D9(
            $"OverlayTextureRenderer: Opened {label} shared texture handle 0x{descriptor.SharedHandle:X8} ({descriptor.Width}x{descriptor.Height}).");
        return true;
    }

    private static bool EnsureSharedInteropSupport(IntPtr pDevice)
    {
        if (_sharedInteropProbed)
            return _sharedInteropSupported;

        _sharedInteropProbed = true;

        if (_queryInterface == null)
        {
            RynthLog.D3D9("OverlayTextureRenderer: Shared-texture probe unavailable because QueryInterface is not cached.");
            OverlaySurfaceBridge.NotifySharedTextureUnavailable();
            return false;
        }

        IntPtr exDevice = IntPtr.Zero;
        Guid iid = IID_IDirect3DDevice9Ex;
        int hr = _queryInterface(pDevice, &iid, out exDevice);
        if (hr >= 0 && exDevice != IntPtr.Zero)
        {
            _sharedInteropSupported = true;
            RynthLog.D3D9("OverlayTextureRenderer: D3D9Ex shared-texture consumption is available on the active game device.");
            ReleaseComObject(exDevice);
            return true;
        }

        RynthLog.D3D9("OverlayTextureRenderer: D3D9Ex shared-texture consumption is unavailable on the active game device; software fallback will remain active.");
        OverlaySurfaceBridge.NotifySharedTextureUnavailable();
        return false;
    }

    // ─── Draw ─────────────────────────────────────────────────────────────

    private static void DrawQuad(IntPtr pDevice)
    {
        // Get current viewport to size the quad
        D3DVIEWPORT9 vp;
        if (_getViewport!(pDevice, &vp) < 0)
            return;

        float W = vp.Width;
        float H = vp.Height;

        // Tell the Avalonia thread what the current viewport size is so it can
        // keep the canvas sized correctly (prevents stretch/scale artifacts).
        int viewportWidth = (int)W;
        int viewportHeight = (int)H;
        bool viewportChanged =
            AvaloniaOverlay.ViewportWidth != viewportWidth ||
            AvaloniaOverlay.ViewportHeight != viewportHeight;
        AvaloniaOverlay.ViewportWidth = viewportWidth;
        AvaloniaOverlay.ViewportHeight = viewportHeight;
        if (viewportChanged)
            AvaloniaOverlay.NotifyGameSurfaceMetricsChanged();

        // Save render states we will change
        _getRenderState!(pDevice, D3DRS_ALPHABLENDENABLE, out uint savedAlpha);
        _getRenderState!(pDevice, D3DRS_SRCBLEND,         out uint savedSrcBlend);
        _getRenderState!(pDevice, D3DRS_DESTBLEND,        out uint savedDstBlend);
        _getRenderState!(pDevice, D3DRS_ZENABLE,          out uint savedZ);
        _getRenderState!(pDevice, D3DRS_CULLMODE,         out uint savedCull);
        _getRenderState!(pDevice, D3DRS_LIGHTING,         out uint savedLighting);
        _getRenderState!(pDevice, D3DRS_COLORWRITEENABLE, out uint savedColorWrite);
        _getVertexShader!(pDevice, out IntPtr savedVS);
        _getPixelShader!(pDevice, out IntPtr savedPS);

        try
        {
            // Set up fixed-function alpha-blended state
            _setRenderState!(pDevice, D3DRS_ALPHABLENDENABLE, 1);
            _setRenderState!(pDevice, D3DRS_SRCBLEND,         D3DBLEND_SRCALPHA);
            _setRenderState!(pDevice, D3DRS_DESTBLEND,        D3DBLEND_INVSRCALPHA);
            _setRenderState!(pDevice, D3DRS_ZENABLE,          0);
            _setRenderState!(pDevice, D3DRS_CULLMODE,         D3DCULL_NONE);
            _setRenderState!(pDevice, D3DRS_LIGHTING,         0);
            _setRenderState!(pDevice, D3DRS_COLORWRITEENABLE, 0xF);

            _setVertexShader!(pDevice, IntPtr.Zero);
            _setPixelShader!(pDevice, IntPtr.Zero);

            _setFVF!(pDevice, FVF);
            _setTexture!(pDevice, 0, _texture);

            _setTexStageState!(pDevice, 0, D3DTSS_COLOROP,   D3DTOP_MODULATE);
            _setTexStageState!(pDevice, 0, D3DTSS_COLORARG1, D3DTA_TEXTURE);
            _setTexStageState!(pDevice, 0, D3DTSS_COLORARG2, D3DTA_DIFFUSE);
            _setTexStageState!(pDevice, 0, D3DTSS_ALPHAOP,   D3DTOP_MODULATE);
            _setTexStageState!(pDevice, 0, D3DTSS_ALPHAARG1, D3DTA_TEXTURE);
            _setTexStageState!(pDevice, 0, D3DTSS_ALPHAARG2, D3DTA_DIFFUSE);
            _setSamplerState!(pDevice, 0, D3DSAMP_MINFILTER, D3DTEXF_LINEAR);
            _setSamplerState!(pDevice, 0, D3DSAMP_MAGFILTER, D3DTEXF_LINEAR);

            QuadVertex* verts = stackalloc QuadVertex[4];
            verts[0] = new QuadVertex { X = 0, Y = 0, Z = 0, RHW = 1, U = 0, V = 0 };
            verts[1] = new QuadVertex { X = W, Y = 0, Z = 0, RHW = 1, U = 1, V = 0 };
            verts[2] = new QuadVertex { X = 0, Y = H, Z = 0, RHW = 1, U = 0, V = 1 };
            verts[3] = new QuadVertex { X = W, Y = H, Z = 0, RHW = 1, U = 1, V = 1 };

            _drawPrimitiveUP!(pDevice, D3DPT_TRIANGLESTRIP, 2,
                new IntPtr(verts), (uint)sizeof(QuadVertex));
        }
        finally
        {
            // Restore changed state
            _setRenderState!(pDevice, D3DRS_ALPHABLENDENABLE, savedAlpha);
            _setRenderState!(pDevice, D3DRS_SRCBLEND,         savedSrcBlend);
            _setRenderState!(pDevice, D3DRS_DESTBLEND,        savedDstBlend);
            _setRenderState!(pDevice, D3DRS_ZENABLE,          savedZ);
            _setRenderState!(pDevice, D3DRS_CULLMODE,         savedCull);
            _setRenderState!(pDevice, D3DRS_LIGHTING,         savedLighting);
            _setRenderState!(pDevice, D3DRS_COLORWRITEENABLE, savedColorWrite);
            _setVertexShader!(pDevice, savedVS);
            _setPixelShader!(pDevice, savedPS);
            _setTexture!(pDevice, 0, IntPtr.Zero);

            if (savedVS != IntPtr.Zero) ReleaseComObject(savedVS);
            if (savedPS != IntPtr.Zero) ReleaseComObject(savedPS);
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static void ReleaseTexture()
    {
        if (_texture == IntPtr.Zero)
            return;

        var release = GetTexMethod<ReleaseD>(_texture, TextureVTableIndex.Release);
        release(_texture);
        _texture = IntPtr.Zero;
        _sharedTextureHandle = IntPtr.Zero;
        _texW = 0;
        _texH = 0;
    }

    private static void ReleaseComObject(IntPtr pObj)
    {
        if (pObj == IntPtr.Zero) return;
        IntPtr vtbl = Marshal.ReadIntPtr(pObj);
        var rel = Marshal.GetDelegateForFunctionPointer<ReleaseD>(
            Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));
        rel(pObj);
    }

    private static void CacheDelegates(IntPtr pDevice)
    {
        IntPtr vtable = Marshal.ReadIntPtr(pDevice);
        _createTexture   = Get<CreateTextureD>(vtable,      DeviceVTableIndex.CreateTexture);
        _setTexture      = Get<SetTextureD>(vtable,         DeviceVTableIndex.SetTexture);
        _setTexStageState = Get<SetTextureStageStateD>(vtable, DeviceVTableIndex.SetTextureStageState);
        _setSamplerState = Get<SetSamplerStateD>(vtable,    DeviceVTableIndex.SetSamplerState);
        _setFVF          = Get<SetFVFD>(vtable,             DeviceVTableIndex.SetFVF);
        _setRenderState  = Get<SetRenderStateD>(vtable,     DeviceVTableIndex.SetRenderState);
        _getRenderState  = Get<GetRenderStateD>(vtable,     DeviceVTableIndex.GetRenderState);
        _setVertexShader = Get<SetVertexShaderD>(vtable,    DeviceVTableIndex.SetVertexShader);
        _getVertexShader = Get<GetVertexShaderD>(vtable,    DeviceVTableIndex.GetVertexShader);
        _setPixelShader  = Get<SetPixelShaderD>(vtable,     DeviceVTableIndex.SetPixelShader);
        _getPixelShader  = Get<GetPixelShaderD>(vtable,     DeviceVTableIndex.GetPixelShader);
        _getViewport     = Get<GetViewportD>(vtable,        DeviceVTableIndex.GetViewport);
        _drawPrimitiveUP = Get<DrawPrimitiveUPD>(vtable,    DeviceVTableIndex.DrawPrimitiveUP);
        _queryInterface  = Get<QueryInterfaceD>(vtable,     0);
    }

    private static T Get<T>(IntPtr vtable, int index) where T : Delegate
    {
        IntPtr addr = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    private static T GetTexMethod<T>(IntPtr pTex, int index) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(pTex);
        IntPtr addr   = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(addr);
    }
}

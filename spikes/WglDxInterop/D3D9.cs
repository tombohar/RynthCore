// Plain D3D9 device + render-target texture. Mimics AC's setup: Direct3DCreate9
// (NOT Ex), SOFTWARE_VERTEXPROCESSING (matches what AC's actual device uses, per
// the engine's MultiClient hooks). The interop chain we're testing in this spike
// must work against a non-Ex device because that's what AC ships.

using System;
using System.Runtime.InteropServices;

namespace WglDxInterop;

internal sealed unsafe class D3D9Host : IDisposable
{
    public const uint D3D_SDK_VERSION = 32;
    public const uint D3DDEVTYPE_HAL = 1;
    public const uint D3DCREATE_FPU_PRESERVE = 0x00000002;
    public const uint D3DCREATE_MULTITHREADED = 0x00000004;
    public const uint D3DCREATE_SOFTWARE_VERTEXPROCESSING = 0x00000020;
    public const uint D3DFMT_UNKNOWN = 0;
    public const uint D3DFMT_A8R8G8B8 = 21;
    public const uint D3DSWAPEFFECT_DISCARD = 1;
    public const uint D3DPOOL_DEFAULT = 0;
    public const uint D3DUSAGE_RENDERTARGET = 0x00000001;
    public const uint D3DFVF_XYZRHW = 0x004;
    public const uint D3DFVF_TEX1 = 0x100;
    public const uint D3DPT_TRIANGLESTRIP = 5;

    public const uint D3DRS_LIGHTING = 137;
    public const uint D3DRS_ZENABLE  = 7;
    public const uint D3DRS_CULLMODE = 22;
    public const uint D3DCULL_NONE   = 1;

    public const uint D3DTSS_COLOROP   = 1;
    public const uint D3DTSS_COLORARG1 = 2;
    public const uint D3DTSS_ALPHAOP   = 4;
    public const uint D3DTSS_ALPHAARG1 = 5;
    public const uint D3DTOP_SELECTARG1 = 2;
    public const uint D3DTA_TEXTURE   = 2;
    public const uint D3DSAMP_MINFILTER = 5;
    public const uint D3DSAMP_MAGFILTER = 6;
    public const uint D3DTEXF_LINEAR   = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DPRESENT_PARAMETERS
    {
        public uint BackBufferWidth;
        public uint BackBufferHeight;
        public uint BackBufferFormat;
        public uint BackBufferCount;
        public uint MultiSampleType;
        public uint MultiSampleQuality;
        public uint SwapEffect;
        public IntPtr hDeviceWindow;
        public int Windowed;
        public int EnableAutoDepthStencil;
        public uint AutoDepthStencilFormat;
        public uint Flags;
        public uint FullScreen_RefreshRateInHz;
        public uint PresentationInterval;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DVIEWPORT9 { public uint X, Y, Width, Height; public float MinZ, MaxZ; }

    [DllImport("d3d9.dll")]
    public static extern IntPtr Direct3DCreate9(uint sdkVersion);

    [DllImport("d3d9.dll")]
    public static extern int Direct3DCreate9Ex(uint sdkVersion, out IntPtr pD3D9Ex);

    public const int IDirect3D9Ex_CreateDeviceEx = 20;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int CreateDeviceExD(IntPtr pD3D9Ex, uint adapter, uint deviceType, IntPtr hFocusWindow,
        uint behaviorFlags, IntPtr pPresentationParameters, IntPtr pFullscreenDisplayMode,
        out IntPtr ppReturnedDeviceInterface);

    // Vtable indexes for IDirect3D9 / IDirect3DDevice9 / IDirect3DTexture9.
    // Cross-checked against the engine's D3D9VTable.cs at investigation time.
    public const int IDirect3D9_CreateDevice    = 16;
    public const int IDirect3D9_Release         = 2;
    public const int IDirect3DDevice9_TestCooperativeLevel = 3;
    public const int IDirect3DDevice9_Reset                = 16;
    public const int IDirect3DDevice9_Present              = 17;
    public const int IDirect3DDevice9_BeginScene           = 41;
    public const int IDirect3DDevice9_EndScene             = 42;
    public const int IDirect3DDevice9_Clear                = 43;
    public const int IDirect3DDevice9_SetRenderState       = 57;
    public const int IDirect3DDevice9_SetTextureStageState = 60;
    public const int IDirect3DDevice9_SetSamplerState      = 62;
    public const int IDirect3DDevice9_SetTexture           = 65;
    public const int IDirect3DDevice9_SetFVF               = 89;
    public const int IDirect3DDevice9_DrawPrimitiveUP      = 83;
    public const int IDirect3DDevice9_CreateTexture        = 23;
    public const int IDirect3DDevice9_Release              = 2;
    public const int IDirect3DTexture9_Release             = 2;
    public const int IDirect3DTexture9_GetSurfaceLevel     = 18;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int CreateDeviceD(IntPtr pD3D9, uint adapter, uint deviceType, IntPtr hFocusWindow,
        uint behaviorFlags, IntPtr pPresentationParameters, out IntPtr ppReturnedDeviceInterface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint ReleaseD(IntPtr pObj);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int CreateTextureD(IntPtr dev, uint w, uint h, uint levels, uint usage,
        uint fmt, uint pool, out IntPtr ppTex, IntPtr pSharedHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int PresentD(IntPtr dev, IntPtr pSourceRect, IntPtr pDestRect,
        IntPtr hDestWindowOverride, IntPtr pDirtyRegion);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int ClearD(IntPtr dev, uint count, IntPtr pRects, uint flags, uint color, float z, uint stencil);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int BeginSceneD(IntPtr dev);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int EndSceneD(IntPtr dev);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int SetRenderStateD(IntPtr dev, uint state, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int SetTextureStageStateD(IntPtr dev, uint stage, uint type, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int SetSamplerStateD(IntPtr dev, uint stage, uint type, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int SetTextureD(IntPtr dev, uint stage, IntPtr pTex);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int SetFVFD(IntPtr dev, uint fvf);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int DrawPrimitiveUPD(IntPtr dev, uint primType, uint primCount, IntPtr pVtxData, uint vtxStride);

    public IntPtr Direct3D9 { get; private set; }
    public IntPtr Device { get; private set; }
    public uint AdapterIndex { get; private set; }
    public bool IsEx { get; private set; }

    private CreateTextureD? _createTexture;
    private PresentD? _present;
    private ClearD? _clear;
    private BeginSceneD? _beginScene;
    private EndSceneD? _endScene;
    private SetRenderStateD? _setRenderState;
    private SetTextureStageStateD? _setTexStageState;
    private SetSamplerStateD? _setSamplerState;
    private SetTextureD? _setTexture;
    private SetFVFD? _setFvf;
    private DrawPrimitiveUPD? _drawPrimitiveUp;

    public void Initialize(IntPtr hwnd, int width, int height) => InitializeInternal(hwnd, width, height, useEx: false);

    /// <summary>D3D9Ex variant — required if WGL_NV_DX_interop needs shared-handle textures.</summary>
    public void InitializeEx(IntPtr hwnd, int width, int height) => InitializeInternal(hwnd, width, height, useEx: true);

    private void InitializeInternal(IntPtr hwnd, int width, int height, bool useEx)
    {
        if (useEx)
        {
            int hrEx = Direct3DCreate9Ex(D3D_SDK_VERSION, out IntPtr pEx);
            if (hrEx < 0 || pEx == IntPtr.Zero)
                throw new InvalidOperationException($"Direct3DCreate9Ex failed, HRESULT=0x{hrEx:X8}.");
            Direct3D9 = pEx;
            IsEx = true;
        }
        else
        {
            Direct3D9 = Direct3DCreate9(D3D_SDK_VERSION);
            if (Direct3D9 == IntPtr.Zero)
                throw new InvalidOperationException("Direct3DCreate9 returned null. Ensure d3d9.dll is present.");
            IsEx = false;
        }

        var pp = new D3DPRESENT_PARAMETERS
        {
            BackBufferWidth = (uint)width,
            BackBufferHeight = (uint)height,
            BackBufferFormat = D3DFMT_A8R8G8B8,
            BackBufferCount = 1,
            SwapEffect = D3DSWAPEFFECT_DISCARD,
            hDeviceWindow = hwnd,
            Windowed = 1,
            EnableAutoDepthStencil = 0,
            AutoDepthStencilFormat = D3DFMT_UNKNOWN,
            PresentationInterval = 0
        };

        IntPtr pPP = Marshal.AllocHGlobal(Marshal.SizeOf<D3DPRESENT_PARAMETERS>());
        try
        {
            Marshal.StructureToPtr(pp, pPP, false);

            IntPtr d3dVtable = Marshal.ReadIntPtr(Direct3D9);

            int hr;
            IntPtr device;
            if (useEx)
            {
                var createDeviceEx = Marshal.GetDelegateForFunctionPointer<CreateDeviceExD>(
                    Marshal.ReadIntPtr(d3dVtable, IDirect3D9Ex_CreateDeviceEx * IntPtr.Size));
                hr = createDeviceEx(
                    Direct3D9, 0, D3DDEVTYPE_HAL, hwnd,
                    0x00000040u /*D3DCREATE_HARDWARE_VERTEXPROCESSING*/ | D3DCREATE_FPU_PRESERVE,
                    pPP, IntPtr.Zero, out device);
            }
            else
            {
                var createDevice = Marshal.GetDelegateForFunctionPointer<CreateDeviceD>(
                    Marshal.ReadIntPtr(d3dVtable, IDirect3D9_CreateDevice * IntPtr.Size));
                hr = createDevice(
                    Direct3D9, 0, D3DDEVTYPE_HAL, hwnd,
                    0x00000040u /*D3DCREATE_HARDWARE_VERTEXPROCESSING*/ | D3DCREATE_FPU_PRESERVE,
                    pPP, out device);
            }

            if (hr < 0 || device == IntPtr.Zero)
                throw new InvalidOperationException($"CreateDevice{(useEx ? "Ex" : "")} failed, HRESULT=0x{hr:X8}");

            Device = device;
            AdapterIndex = 0;

            CacheDeviceMethods();
        }
        finally
        {
            Marshal.FreeHGlobal(pPP);
        }
    }

    /// <summary>
    /// D3D9Ex-only: create an RT texture with a non-null pSharedHandle slot so
    /// the driver allocates a kernel-mode shared handle for the texture. The
    /// returned shared handle can then be passed to wglDXSetResourceShareHandleNV.
    /// On a non-Ex device this returns a texture but the share handle stays null.
    /// </summary>
    public IntPtr CreateRenderTargetTextureWithSharedHandle(int width, int height, out IntPtr sharedHandle)
    {
        if (_createTexture == null) throw new InvalidOperationException("Device not initialized.");
        sharedHandle = IntPtr.Zero;

        // CreateTexture's pSharedHandle is in/out: caller passes a pointer to
        // a HANDLE; on D3D9Ex devices the driver writes the kernel handle there.
        unsafe
        {
            IntPtr handle = IntPtr.Zero;
            int hr = _createTexture(Device, (uint)width, (uint)height, 1,
                D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT,
                out IntPtr tex, new IntPtr(&handle));
            if (hr < 0 || tex == IntPtr.Zero)
                throw new InvalidOperationException($"CreateTexture(RT shared {width}x{height}) failed, HRESULT=0x{hr:X8}");
            sharedHandle = handle;
            return tex;
        }
    }

    private void CacheDeviceMethods()
    {
        IntPtr vtbl = Marshal.ReadIntPtr(Device);
        _createTexture   = GetMethod<CreateTextureD>(vtbl, IDirect3DDevice9_CreateTexture);
        _present         = GetMethod<PresentD>(vtbl, IDirect3DDevice9_Present);
        _clear           = GetMethod<ClearD>(vtbl, IDirect3DDevice9_Clear);
        _beginScene      = GetMethod<BeginSceneD>(vtbl, IDirect3DDevice9_BeginScene);
        _endScene        = GetMethod<EndSceneD>(vtbl, IDirect3DDevice9_EndScene);
        _setRenderState  = GetMethod<SetRenderStateD>(vtbl, IDirect3DDevice9_SetRenderState);
        _setTexStageState = GetMethod<SetTextureStageStateD>(vtbl, IDirect3DDevice9_SetTextureStageState);
        _setSamplerState = GetMethod<SetSamplerStateD>(vtbl, IDirect3DDevice9_SetSamplerState);
        _setTexture      = GetMethod<SetTextureD>(vtbl, IDirect3DDevice9_SetTexture);
        _setFvf          = GetMethod<SetFVFD>(vtbl, IDirect3DDevice9_SetFVF);
        _drawPrimitiveUp = GetMethod<DrawPrimitiveUPD>(vtbl, IDirect3DDevice9_DrawPrimitiveUP);
    }

    private static T GetMethod<T>(IntPtr vtable, int index) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, index * IntPtr.Size));

    /// <summary>Creates an A8R8G8B8 texture in D3DPOOL_DEFAULT with RENDERTARGET usage.</summary>
    public IntPtr CreateRenderTargetTexture(int width, int height)
    {
        if (_createTexture == null) throw new InvalidOperationException("Device not initialized.");
        int hr = _createTexture(Device, (uint)width, (uint)height, 1,
            D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT,
            out IntPtr tex, IntPtr.Zero);
        if (hr < 0 || tex == IntPtr.Zero)
            throw new InvalidOperationException($"CreateTexture(RT {width}x{height}) failed, HRESULT=0x{hr:X8}");
        return tex;
    }

    /// <summary>Returns IDirect3DSurface9* for level 0 of the texture. Caller releases.</summary>
    public IntPtr GetTextureSurfaceLevel0(IntPtr texture)
    {
        if (texture == IntPtr.Zero) return IntPtr.Zero;
        IntPtr texVtbl = Marshal.ReadIntPtr(texture);
        var getSurfaceLevel = Marshal.GetDelegateForFunctionPointer<GetSurfaceLevelD>(
            Marshal.ReadIntPtr(texVtbl, IDirect3DTexture9_GetSurfaceLevel * IntPtr.Size));
        int hr = getSurfaceLevel(texture, 0, out IntPtr surface);
        if (hr < 0 || surface == IntPtr.Zero)
            throw new InvalidOperationException($"GetSurfaceLevel(0) failed, HRESULT=0x{hr:X8}");
        return surface;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSurfaceLevelD(IntPtr tex, uint level, out IntPtr ppSurface);

    public void ReleaseObject(IntPtr pObj)
    {
        if (pObj == IntPtr.Zero) return;
        IntPtr vtbl = Marshal.ReadIntPtr(pObj);
        var release = Marshal.GetDelegateForFunctionPointer<ReleaseD>(
            Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));
        release(pObj);
    }

    // ─── Frame helpers ────────────────────────────────────────────────────
    public int Clear(uint argbColor) => _clear!(Device, 0, IntPtr.Zero, 0x00000001 /*D3DCLEAR_TARGET*/, argbColor, 1.0f, 0);
    public int BeginScene() => _beginScene!(Device);
    public int EndScene() => _endScene!(Device);
    public int Present() => _present!(Device, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

    public void DrawTexturedQuad(IntPtr texture, float W, float H)
    {
        // Pre-transformed XYZRHW vertices; matches OverlayTextureRenderer.cs in the engine.
        // -0.5 offset is the D3D9 half-texel rule.
        Span<QuadVertex> verts = stackalloc QuadVertex[4];
        verts[0] = new QuadVertex { X = -0.5f,    Y = -0.5f,    Z = 0, RHW = 1, U = 0, V = 0 };
        verts[1] = new QuadVertex { X = W - 0.5f, Y = -0.5f,    Z = 0, RHW = 1, U = 1, V = 0 };
        verts[2] = new QuadVertex { X = -0.5f,    Y = H - 0.5f, Z = 0, RHW = 1, U = 0, V = 1 };
        verts[3] = new QuadVertex { X = W - 0.5f, Y = H - 0.5f, Z = 0, RHW = 1, U = 1, V = 1 };

        _setRenderState!(Device, D3DRS_LIGHTING, 0);
        _setRenderState!(Device, D3DRS_ZENABLE, 0);
        _setRenderState!(Device, D3DRS_CULLMODE, D3DCULL_NONE);

        _setTexStageState!(Device, 0, D3DTSS_COLOROP,   D3DTOP_SELECTARG1);
        _setTexStageState!(Device, 0, D3DTSS_COLORARG1, D3DTA_TEXTURE);
        _setTexStageState!(Device, 0, D3DTSS_ALPHAOP,   D3DTOP_SELECTARG1);
        _setTexStageState!(Device, 0, D3DTSS_ALPHAARG1, D3DTA_TEXTURE);
        _setSamplerState!(Device, 0, D3DSAMP_MINFILTER, D3DTEXF_LINEAR);
        _setSamplerState!(Device, 0, D3DSAMP_MAGFILTER, D3DTEXF_LINEAR);

        _setFvf!(Device, D3DFVF_XYZRHW | D3DFVF_TEX1);
        _setTexture!(Device, 0, texture);

        fixed (QuadVertex* p = verts)
        {
            _drawPrimitiveUp!(Device, D3DPT_TRIANGLESTRIP, 2, (IntPtr)p, (uint)sizeof(QuadVertex));
        }

        _setTexture!(Device, 0, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (Device != IntPtr.Zero)
        {
            ReleaseObject(Device);
            Device = IntPtr.Zero;
        }
        if (Direct3D9 != IntPtr.Zero)
        {
            ReleaseObject(Direct3D9);
            Direct3D9 = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QuadVertex
    {
        public float X, Y, Z, RHW;
        public float U, V;
    }
}

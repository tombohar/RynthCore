// ═══════════════════════════════════════════════════════════════════════════
//  RynthCore.Engine — ImGui/DX9Backend.cs
//  Renders ImGui draw data using D3D9 vtable calls.
//  Implements the equivalent of imgui_impl_dx9.cpp in pure C#.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using RynthCore.Engine.D3D9;

namespace RynthCore.Engine.ImGuiBackend;

internal static unsafe class DX9Backend
{
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    // Draw data is read through ImGui.NET's native structs (ImDrawData,
    // ImDrawList, ImDrawCmd). That is only valid because Native\cimgui.dll is
    // the exact 1.91.6 build ImGui.NET 1.91.6.1 was generated against;
    // ImGuiSelfTest refuses to start ImGui if the loaded DLL is anything else.

    // Converted-vertex scratch buffer, grown on demand and reused every frame
    // (no per-frame native allocation on AC's thread). Freed in Shutdown.
    private static CustomVertex* _vtxScratch;
    private static int _vtxScratchCapacity;

    private static CustomVertex* EnsureVertexScratch(int needed)
    {
        if (needed > _vtxScratchCapacity)
        {
            int cap = _vtxScratchCapacity == 0 ? 8192 : _vtxScratchCapacity;
            while (cap < needed) cap *= 2;
            if (_vtxScratch != null) NativeMemory.Free(_vtxScratch);
            _vtxScratch = (CustomVertex*)NativeMemory.Alloc((nuint)cap, (nuint)sizeof(CustomVertex));
            _vtxScratchCapacity = cap;
        }
        return _vtxScratch;
    }

    // ── Diagnostics (/rc imgui diag): what the last RenderDrawData did ──
    private static int _diagLists, _diagVtx, _diagCmds, _diagDraws, _diagSkipped, _diagFailed, _diagLastHr;
    private static long _diagFrames;

    public static string DescribeLastFrame() =>
        $"render: initialized={_initialized} frames={_diagFrames} lastFrame lists={_diagLists} vtx={_diagVtx} cmds={_diagCmds} draws={_diagDraws} skipped={_diagSkipped} failedDraws={_diagFailed} lastFailHr=0x{_diagLastHr:X8} fontTex=0x{_fontTexture:X8}";

    // ─── D3D9 constants ───────────────────────────────────────────────
    private const uint D3DPT_TRIANGLELIST = 4;
    private const uint D3DFMT_INDEX16 = 101;
    private const uint D3DFMT_A8R8G8B8 = 21;
    private const uint D3DFMT_DXT1 = 0x31545844;       // the FourCC 'DXT1'
    private const uint D3DPOOL_MANAGED = 1;
    private const uint D3DUSAGE_DYNAMIC = 0x200;
    private const uint D3DPOOL_DEFAULT = 0;
    private const uint D3DBACKBUFFER_TYPE_MONO = 0;

    // D3DRENDERSTATETYPE
    private const uint D3DRS_LIGHTING = 137;
    private const uint D3DRS_ALPHABLENDENABLE = 27;
    private const uint D3DRS_SRCBLEND = 19;
    private const uint D3DRS_DESTBLEND = 20;
    private const uint D3DRS_ZENABLE = 7;
    private const uint D3DRS_FOGENABLE = 28;
    private const uint D3DRS_CULLMODE = 22;
    private const uint D3DRS_SCISSORTESTENABLE = 174;
    private const uint D3DRS_SHADEMODE = 9;
    private const uint D3DRS_COLORWRITEENABLE = 168;
    private const uint D3DRS_STENCILENABLE = 52;
    private const uint D3DRS_MULTISAMPLEANTIALIAS = 161;
    private const uint D3DRS_BLENDOP = 171;
    private const uint D3DRS_SRGBWRITEENABLE = 194;
    private const uint D3DRS_SEPARATEALPHABLENDENABLE = 206;
    private const uint D3DRS_FILLMODE = 8;
    private const uint D3DRS_ZWRITEENABLE = 14;
    private const uint D3DRS_ZFUNC = 23;
    private const uint D3DRS_ALPHATESTENABLE = 15;
    private const uint D3DRS_CLIPPING = 136;
    private const uint D3DRS_RANGEFOGENABLE = 48;
    private const uint D3DRS_SPECULARENABLE = 29;
    private const uint D3DRS_SRCBLENDALPHA = 207;
    private const uint D3DRS_DESTBLENDALPHA = 208;
    private const uint D3DRS_SLOPESCALEDEPTHBIAS = 175;
    private const uint D3DRS_DEPTHBIAS = 195;

    // D3DBLEND
    private const uint D3DBLEND_SRCALPHA = 5;
    private const uint D3DBLEND_INVSRCALPHA = 6;
    private const uint D3DBLEND_ONE = 2;

    // D3DFILL
    private const uint D3DFILL_SOLID = 3;

    // D3DCULL
    private const uint D3DCULL_NONE = 1;

    // D3DCMPFUNC
    private const uint D3DCMP_LESSEQUAL = 4;
    private const uint D3DCMP_ALWAYS    = 8;

    // D3DSHADEMODE
    private const uint D3DSHADE_GOURAUD = 2;

    // D3DTRANSFORMSTATETYPE
    private const uint D3DTS_WORLD = 256;
    private const uint D3DTS_VIEW = 2;
    private const uint D3DTS_PROJECTION = 3;

    // D3DTEXTURESTAGESTATETYPE
    private const uint D3DTSS_COLOROP = 1;
    private const uint D3DTSS_COLORARG1 = 2;
    private const uint D3DTSS_COLORARG2 = 3;
    private const uint D3DTSS_ALPHAOP = 4;
    private const uint D3DTSS_ALPHAARG1 = 5;
    private const uint D3DTSS_ALPHAARG2 = 6;

    // D3DTEXTURESTAGESTATETYPE (continued)
    private const uint D3DTSS_TEXCOORDINDEX = 11;
    private const uint D3DTSS_TEXTURETRANSFORMFLAGS = 24;

    // D3DTEXTUREOP
    private const uint D3DTOP_MODULATE = 4;
    private const uint D3DTOP_SELECTARG1 = 2;
    private const uint D3DTOP_DISABLE = 1;

    // D3DTA
    private const uint D3DTA_TEXTURE = 2;
    private const uint D3DTA_DIFFUSE = 0;

    // D3DSAMPLERSTATETYPE
    private const uint D3DSAMP_MINFILTER = 5;
    private const uint D3DSAMP_MAGFILTER = 6;
    private const uint D3DSAMP_ADDRESSU = 1;
    private const uint D3DSAMP_ADDRESSV = 2;
    private const uint D3DSAMP_MIPFILTER = 7;
    private const uint D3DTADDRESS_CLAMP = 3;
    private const uint D3DTEXF_NONE = 0;
    private const uint D3DRS_WRAP0 = 128;

    // D3DTEXTUREFILTERTYPE
    private const uint D3DTEXF_LINEAR = 2, D3DTEXF_POINT = 1;
    // Draw-list callbacks this renderer understands besides ResetRenderState (see IconSampling): the sampler
    // for the draw commands that follow, until the next one or the end of the list.


    // FVF: xyz (3 floats) + diffuse (uint) + 1 tex coord (2 floats)
    private const uint D3DFVF_XYZ = 0x002;
    private const uint D3DFVF_DIFFUSE = 0x040;
    private const uint D3DFVF_TEX1 = 0x100;
    private const uint D3DFVF_XYZRHW = 0x004;
    private const uint D3DFVF_CUSTOM = D3DFVF_XYZRHW | D3DFVF_DIFFUSE | D3DFVF_TEX1;
    private const uint D3DRS_VERTEXBLEND = 151;
    private const uint D3DRS_CLIPPLANEENABLE = 152;

    // D3DBLENDOP
    private const uint D3DBLENDOP_ADD = 1;

    // ─── Vertex structure (D3D9 layout) ───────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    // Pre-transformed (screen-space) vertices, like VitalHud and the Avalonia
    // quad. The first in-game test drew ImGui with XYZ vertices through the
    // fixed-function transform stage and nothing appeared, although every
    // draw call succeeded: state AC leaves in that stage (user clip planes,
    // vertex blending) can discard the geometry. XYZRHW skips it entirely.
    private struct CustomVertex
    {
        public float X, Y, Z, Rhw;
        public uint Col;   // ARGB
        public float U, V;
    }

    // ─── D3D9 structs ─────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DVIEWPORT9
    {
        public uint X, Y, Width, Height;
        public float MinZ, MaxZ;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DMATRIX
    {
        public float M11, M12, M13, M14;
        public float M21, M22, M23, M24;
        public float M31, M32, M33, M34;
        public float M41, M42, M43, M44;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DLOCKED_RECT
    {
        public int Pitch;
        public IntPtr pBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DDEVICE_CREATION_PARAMETERS
    {
        public uint AdapterOrdinal;
        public uint DeviceType;
        public IntPtr hFocusWindow;
        public uint BehaviorFlags;
    }

    // ─── COM method delegates (stdcall, this as first param) ──────────
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetRenderStateD(IntPtr dev, uint state, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCreationParametersD(IntPtr dev, D3DDEVICE_CREATION_PARAMETERS* pParameters);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBackBufferD(IntPtr dev, uint swapChain, uint backBuffer, uint type, out IntPtr ppBackBuffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetRenderTargetD(IntPtr dev, uint renderTargetIndex, out IntPtr ppRenderTarget);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceD(IntPtr pObj, Guid* riid, out IntPtr ppvObject);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetTextureD(IntPtr dev, uint stage, IntPtr pTex);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetTextureStageStateD(IntPtr dev, uint stage, uint type, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetSamplerStateD(IntPtr dev, uint sampler, uint type, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetFVFD(IntPtr dev, uint fvf);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetVertexShaderD(IntPtr dev, IntPtr pShader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPixelShaderD(IntPtr dev, IntPtr pShader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetTransformD(IntPtr dev, uint state, D3DMATRIX* pMatrix);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetViewportD(IntPtr dev, D3DVIEWPORT9* pViewport);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetViewportD(IntPtr dev, D3DVIEWPORT9* pViewport);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetScissorRectD(IntPtr dev, RECT* pRect);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DrawIndexedPrimitiveUPD(
        IntPtr dev, uint primType, uint minVertIdx, uint numVerts,
        uint primCount, IntPtr pIdxData, uint idxFmt,
        IntPtr pVtxData, uint vtxStride);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DrawPrimitiveUPD(IntPtr dev, uint primType, uint primCount, IntPtr pVtxData, uint vtxStride);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTextureD(
        IntPtr dev, uint w, uint h, uint levels, uint usage,
        uint fmt, uint pool, out IntPtr ppTex, IntPtr pSharedHandle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetStreamSourceD(IntPtr dev, uint streamNum, IntPtr pStreamData, uint offsetBytes, uint stride);

    // Texture methods
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TexLockRectD(IntPtr pTex, uint level, D3DLOCKED_RECT* pLocked, IntPtr pRect, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TexUnlockRectD(IntPtr pTex, uint level);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseD(IntPtr pObj);

    // For manual state save/restore
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetRenderStateD(IntPtr dev, uint state, out uint pValue);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetTransformD(IntPtr dev, uint state, D3DMATRIX* pMatrix);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetScissorRectD(IntPtr dev, RECT* pRect);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetTextureD(IntPtr dev, uint stage, out IntPtr ppTex);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFVFD(IntPtr dev, out uint pFVF);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetVertexShaderD(IntPtr dev, out IntPtr ppShader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPixelShaderD(IntPtr dev, out IntPtr ppShader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetTextureStageStateD(IntPtr dev, uint stage, uint type, out uint pValue);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSamplerStateD(IntPtr dev, uint sampler, uint type, out uint pValue);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetStreamSourceD(IntPtr dev, uint streamNumber, out IntPtr ppStreamData, out uint pOffsetInBytes, out uint pStride);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetIndicesD(IntPtr dev, IntPtr pIndexData);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetIndicesD(IntPtr dev, out IntPtr ppIndexData);

    // ─── Cached delegates ─────────────────────────────────────────────
    private static GetCreationParametersD? _getCreationParameters;
    private static GetBackBufferD? _getBackBuffer;
    private static GetRenderTargetD? _getRenderTargetSurface;
    private static SetRenderStateD? _setRenderState;
    private static GetRenderStateD? _getRenderState;

    /// <summary>Exposes SetRenderState for Nav3DRenderInjector stencil calls.</summary>
    public static void DeviceSetRenderState(IntPtr dev, uint state, uint value)
    {
        _setRenderState?.Invoke(dev, state, value);
    }

    /// <summary>Exposes GetRenderState for Nav3DRenderInjector diagnostics.</summary>
    public static bool DeviceGetRenderState(IntPtr dev, uint state, out uint value)
    {
        value = 0;
        if (_getRenderState == null) return false;
        _getRenderState(dev, state, out value);
        return true;
    }
    private static SetTextureD? _setTexture;
    private static GetTextureD? _getTexture;
    private static SetTextureStageStateD? _setTexStageState;
    private static SetSamplerStateD? _setSamplerState;
    private static SetFVFD? _setFVF;
    private static GetFVFD? _getFVF;
    private static SetVertexShaderD? _setVertexShader;
    private static GetVertexShaderD? _getVertexShader;
    private static SetPixelShaderD? _setPixelShader;
    private static GetPixelShaderD? _getPixelShader;
    private static SetTransformD? _setTransform;
    private static GetTransformD? _getTransform;
    private static GetViewportD? _getViewport;
    private static SetViewportD? _setViewport;
    private static SetScissorRectD? _setScissorRect;
    private static GetScissorRectD? _getScissorRect;
    private static DrawIndexedPrimitiveUPD? _drawIndexedPrimUP;
    private static DrawPrimitiveUPD? _drawPrimUP;
    private static CreateTextureD? _createTexture;
    private static SetStreamSourceD? _setStreamSource;
    private static GetTextureStageStateD? _getTexStageState;
    private static GetSamplerStateD? _getSamplerState;
    private static GetStreamSourceD? _getStreamSource;
    private static SetIndicesD? _setIndices;
    private static GetIndicesD? _getIndices;
    private static IntPtr _fontTexture;
    /// <summary>
    /// True once <see cref="InitCore"/> has cached the D3D9 vtable delegates.
    /// Required by <see cref="RenderNav3D"/>, <see cref="DeviceSetRenderState"/>,
    /// and any non-ImGui render path. Independent of <see cref="_initialized"/>
    /// (which means "ImGui-specific resources also ready").
    /// </summary>
    private static bool _coreInitialized;
    /// <summary>
    /// True once <see cref="InitImGui"/> has built the font texture and the
    /// backend is fully ready for <see cref="RenderDrawData"/>. Implies
    /// <see cref="_coreInitialized"/>.
    /// </summary>
    private static bool _initialized;

    // ─── Public API ───────────────────────────────────────────────────

    /// <summary>
    /// Caches the D3D9 device function pointers that every render path here
    /// (ImGui draw, Nav3D markers, callers like Nav3DRenderInjector) relies on.
    /// Idempotent. Safe to call when ImGui is disabled — does NOT touch the
    /// font texture or any ImGui state.
    /// </summary>
    public static bool InitCore(IntPtr pDevice)
    {
        if (_coreInitialized) return true;
        if (pDevice == IntPtr.Zero) return false;

        CacheDelegates(pDevice);
        _coreInitialized = true;
        RynthLog.Render("DX9Backend: Core delegates cached.");
        return true;
    }

    /// <summary>
    /// Builds the ImGui font texture and marks the backend ready for
    /// <see cref="RenderDrawData"/>. Calls <see cref="InitCore"/> first if it
    /// hasn't run. Idempotent.
    /// </summary>
    public static bool InitImGui(IntPtr pDevice)
    {
        if (_initialized) return true;
        if (!InitCore(pDevice)) return false;

        if (!CreateFontTexture(pDevice))
            return false;

        // Draw lists may exceed 64K vertices (radar walls); with this flag ImGui
        // splits them into commands that carry a VtxOffset, which
        // RenderDrawData applies, so 16-bit indices stay valid.
        ImGuiNET.ImGui.GetIO().BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        _initialized = true;
        RynthLog.Render("DX9Backend: ImGui resources initialized.");
        return true;
    }

    /// <summary>
    /// Backward-compat wrapper — initializes both core delegates and ImGui
    /// resources. Prefer <see cref="InitCore"/> + <see cref="InitImGui"/>
    /// directly so callers can skip the ImGui half when not needed.
    /// </summary>
    public static bool Init(IntPtr pDevice) => InitImGui(pDevice);

    public static void Shutdown()
    {
        ReleaseRetiredFontTexture();
        if (_fontTexture != IntPtr.Zero)
        {
            var release = GetTexMethod<ReleaseD>(_fontTexture, TextureVTableIndex.Release);
            release(_fontTexture);
            _fontTexture = IntPtr.Zero;
        }
        if (_vtxScratch != null)
        {
            NativeMemory.Free(_vtxScratch);
            _vtxScratch = null;
            _vtxScratchCapacity = 0;
        }
        _initialized = false;
        _coreInitialized = false;
    }

    public static void NewFrame()
    {
        // Nothing needed per-frame for DX9 backend
    }

    public static void GetViewportSize(IntPtr pDevice, out int width, out int height)
    {
        width = 0; height = 0;
        if (_getViewport == null || pDevice == IntPtr.Zero) return;
        D3DVIEWPORT9 vp;
        _getViewport(pDevice, &vp);
        width = (int)vp.Width;
        height = (int)vp.Height;
    }

    public static IntPtr GetDeviceWindow(IntPtr pDevice)
    {
        if (pDevice == IntPtr.Zero) return IntPtr.Zero;

        if (_getCreationParameters == null)
        {
            IntPtr vtable = Marshal.ReadIntPtr(pDevice);
            _getCreationParameters = Get<GetCreationParametersD>(vtable, DeviceVTableIndex.GetCreationParameters);
        }

        D3DDEVICE_CREATION_PARAMETERS creationParameters = default;
        int hr = _getCreationParameters(pDevice, &creationParameters);
        if (hr < 0) return IntPtr.Zero;

        return creationParameters.hFocusWindow;
    }

    public static bool IsRenderingToBackBuffer(IntPtr pDevice)
    {
        if (pDevice == IntPtr.Zero) return true;

        if (_getBackBuffer == null || _getRenderTargetSurface == null)
        {
            IntPtr vtable = Marshal.ReadIntPtr(pDevice);
            _getBackBuffer ??= Get<GetBackBufferD>(vtable, DeviceVTableIndex.GetBackBuffer);
            _getRenderTargetSurface ??= Get<GetRenderTargetD>(vtable, DeviceVTableIndex.GetRenderTarget);
        }

        IntPtr currentRenderTarget = IntPtr.Zero;
        IntPtr backBuffer = IntPtr.Zero;
        IntPtr currentIdentity = IntPtr.Zero;
        IntPtr backBufferIdentity = IntPtr.Zero;

        try
        {
            int rtHr = _getRenderTargetSurface!(pDevice, 0, out currentRenderTarget);
            int bbHr = _getBackBuffer!(pDevice, 0, 0, D3DBACKBUFFER_TYPE_MONO, out backBuffer);
            if (rtHr < 0 || bbHr < 0 || currentRenderTarget == IntPtr.Zero || backBuffer == IntPtr.Zero)
                return true;

            if (currentRenderTarget == backBuffer)
                return true;

            currentIdentity = QueryIUnknown(currentRenderTarget);
            backBufferIdentity = QueryIUnknown(backBuffer);
            if (currentIdentity != IntPtr.Zero && backBufferIdentity != IntPtr.Zero)
                return currentIdentity == backBufferIdentity;

            return false;
        }
        finally
        {
            ReleaseComObject(currentIdentity);
            ReleaseComObject(backBufferIdentity);
            ReleaseComObject(currentRenderTarget);
            ReleaseComObject(backBuffer);
        }
    }

    private static int _logCount;
    private static void LogOnce(string where, Exception ex)
    {
        if (_logCount++ < 5)
            RynthLog.Render($"DX9Backend.{where}: {ex.GetType().Name}: {ex.Message}");
    }

    public static void RenderDrawData(ImDrawDataPtr drawData, IntPtr pDevice)
    {
        if (!_initialized) return;
        ImDrawData* dd = drawData.NativePtr;
        if (dd == null) return;
        RenderLists((ImDrawList**)dd->CmdLists.Data, dd->CmdListsCount, dd->TotalVtxCount, dd->DisplayPos, dd->DisplaySize,
            pDevice, midFrame: false);
    }

    /// <summary>
    /// Renders one draw list built outside ImGui's draw data (UnderUiLayer: the
    /// world overlays drawn at AC's 3D→UI transition, in the middle of AC's frame).
    /// Same state save/restore as <see cref="RenderDrawData"/>, plus the few
    /// states a 3D pass can leave that the UI pass at EndScene never does.
    /// Doesn't count in /rc imgui diag. AC's render thread.
    /// </summary>
    public static void RenderDrawList(ImDrawList* list, Vector2 displaySize, IntPtr pDevice)
    {
        if (!_initialized || list == null || pDevice == IntPtr.Zero) return;
        RenderLists(&list, 1, -1, Vector2.Zero, displaySize, pDevice, midFrame: true);
    }

    /// <param name="totalVtx">For the diagnostics; -1: not a frame of ImGui's own (not counted).</param>
    private static void RenderLists(ImDrawList** cmdLists, int cmdListsCount, int totalVtx, Vector2 displayPos, Vector2 displaySize,
        IntPtr pDevice, bool midFrame)
    {
        if (cmdListsCount == 0) return;
        if (displaySize.X <= 0 || displaySize.Y <= 0) return;
        if (cmdLists == null) return;

        if (totalVtx >= 0)
        {
            _diagFrames++;
            _diagLists = cmdListsCount; _diagVtx = totalVtx;
            _diagCmds = 0; _diagDraws = 0; _diagSkipped = 0; _diagFailed = 0;
        }

        // Mid-frame only (the 3D pass's leftovers): texture-coordinate wrapping,
        // and sampler 0's addressing and mip filter.
        uint oldWrap0 = 0, oldSamp0AddrU = 0, oldSamp0AddrV = 0, oldSamp0Mip = 0;
        if (midFrame)
        {
            _getRenderState!(pDevice, D3DRS_WRAP0, out oldWrap0);
            _getSamplerState!(pDevice, 0, D3DSAMP_ADDRESSU, out oldSamp0AddrU);
            _getSamplerState!(pDevice, 0, D3DSAMP_ADDRESSV, out oldSamp0AddrV);
            _getSamplerState!(pDevice, 0, D3DSAMP_MIPFILTER, out oldSamp0Mip);
            _setRenderState!(pDevice, D3DRS_WRAP0, 0);
            _setSamplerState!(pDevice, 0, D3DSAMP_ADDRESSU, D3DTADDRESS_CLAMP);
            _setSamplerState!(pDevice, 0, D3DSAMP_ADDRESSV, D3DTADDRESS_CLAMP);
            _setSamplerState!(pDevice, 0, D3DSAMP_MIPFILTER, D3DTEXF_NONE);
        }

        // ── Manual state save ────────────────────────────────────────
        D3DVIEWPORT9 oldVp = default;
        RECT oldScissor = default;
        uint oldCull = 0, oldLighting = 0, oldZEnable = 0, oldAlphaBlend = 0;
        uint oldBlendOp = 0, oldSrcBlend = 0, oldDestBlend = 0, oldScissorEnable = 0;
        uint oldShade = 0, oldFog = 0, oldStencil = 0, oldColorWrite = 0;
        uint oldSRGB = 0, oldMSAA = 0, oldSepAlpha = 0, oldFVF = 0;
        IntPtr oldTexture = IntPtr.Zero, oldVS = IntPtr.Zero, oldPS = IntPtr.Zero;
        D3DMATRIX oldWorld = default, oldView = default, oldProj = default;
        // Texture stage state (stage 0 and 1) — AC relies on these between frames
        uint oldTss0ColorOp = 0, oldTss0ColorArg1 = 0, oldTss0ColorArg2 = 0;
        uint oldTss0AlphaOp = 0, oldTss0AlphaArg1 = 0, oldTss0AlphaArg2 = 0;
        uint oldTss1ColorOp = 0, oldTss1AlphaOp = 0;
        uint oldSamp0Min = 0, oldSamp0Mag = 0;
        // Additional states from reference
        uint oldFillMode = 0, oldZWriteEnable = 0, oldAlphaTestEnable = 0;
        uint oldClipping = 0, oldRangeFog = 0, oldSpecular = 0;
        uint oldSrcBlendAlpha = 0, oldDestBlendAlpha = 0;
        uint oldVertexBlend = 0, oldClipPlanes = 0;
        uint oldTss0TexCoordIdx = 0, oldTss0TexTransFlags = 0;
        // Stream source and index buffer — DrawIndexedPrimitiveUP sets these to NULL internally
        IntPtr oldStreamData = IntPtr.Zero;
        uint oldStreamOffset = 0, oldStreamStride = 0;
        IntPtr oldIndexBuffer = IntPtr.Zero;

        _getViewport!(pDevice, &oldVp);
        _getScissorRect!(pDevice, &oldScissor);
        _getRenderState!(pDevice, D3DRS_CULLMODE, out oldCull);
        _getRenderState!(pDevice, D3DRS_LIGHTING, out oldLighting);
        _getRenderState!(pDevice, D3DRS_ZENABLE, out oldZEnable);
        _getRenderState!(pDevice, D3DRS_ALPHABLENDENABLE, out oldAlphaBlend);
        _getRenderState!(pDevice, D3DRS_BLENDOP, out oldBlendOp);
        _getRenderState!(pDevice, D3DRS_SRCBLEND, out oldSrcBlend);
        _getRenderState!(pDevice, D3DRS_DESTBLEND, out oldDestBlend);
        _getRenderState!(pDevice, D3DRS_SCISSORTESTENABLE, out oldScissorEnable);
        _getRenderState!(pDevice, D3DRS_SHADEMODE, out oldShade);
        _getRenderState!(pDevice, D3DRS_FOGENABLE, out oldFog);
        _getRenderState!(pDevice, D3DRS_STENCILENABLE, out oldStencil);
        _getRenderState!(pDevice, D3DRS_COLORWRITEENABLE, out oldColorWrite);
        _getRenderState!(pDevice, D3DRS_SRGBWRITEENABLE, out oldSRGB);
        _getRenderState!(pDevice, D3DRS_MULTISAMPLEANTIALIAS, out oldMSAA);
        _getRenderState!(pDevice, D3DRS_SEPARATEALPHABLENDENABLE, out oldSepAlpha);
        _getTexture!(pDevice, 0, out oldTexture);
        _getFVF!(pDevice, out oldFVF);
        _getVertexShader!(pDevice, out oldVS);
        _getPixelShader!(pDevice, out oldPS);
        _getTransform!(pDevice, D3DTS_WORLD, &oldWorld);
        _getTransform!(pDevice, D3DTS_VIEW, &oldView);
        _getTransform!(pDevice, D3DTS_PROJECTION, &oldProj);
        _getTexStageState!(pDevice, 0, D3DTSS_COLOROP, out oldTss0ColorOp);
        _getTexStageState!(pDevice, 0, D3DTSS_COLORARG1, out oldTss0ColorArg1);
        _getTexStageState!(pDevice, 0, D3DTSS_COLORARG2, out oldTss0ColorArg2);
        _getTexStageState!(pDevice, 0, D3DTSS_ALPHAOP, out oldTss0AlphaOp);
        _getTexStageState!(pDevice, 0, D3DTSS_ALPHAARG1, out oldTss0AlphaArg1);
        _getTexStageState!(pDevice, 0, D3DTSS_ALPHAARG2, out oldTss0AlphaArg2);
        _getTexStageState!(pDevice, 1, D3DTSS_COLOROP, out oldTss1ColorOp);
        _getTexStageState!(pDevice, 1, D3DTSS_ALPHAOP, out oldTss1AlphaOp);
        _getSamplerState!(pDevice, 0, D3DSAMP_MINFILTER, out oldSamp0Min);
        _getSamplerState!(pDevice, 0, D3DSAMP_MAGFILTER, out oldSamp0Mag);
        _getRenderState!(pDevice, D3DRS_FILLMODE, out oldFillMode);
        _getRenderState!(pDevice, D3DRS_ZWRITEENABLE, out oldZWriteEnable);
        _getRenderState!(pDevice, D3DRS_ALPHATESTENABLE, out oldAlphaTestEnable);
        _getRenderState!(pDevice, D3DRS_CLIPPING, out oldClipping);
        _getRenderState!(pDevice, D3DRS_RANGEFOGENABLE, out oldRangeFog);
        _getRenderState!(pDevice, D3DRS_SPECULARENABLE, out oldSpecular);
        _getRenderState!(pDevice, D3DRS_SRCBLENDALPHA, out oldSrcBlendAlpha);
        _getRenderState!(pDevice, D3DRS_DESTBLENDALPHA, out oldDestBlendAlpha);
        _getRenderState!(pDevice, D3DRS_VERTEXBLEND, out oldVertexBlend);
        _getRenderState!(pDevice, D3DRS_CLIPPLANEENABLE, out oldClipPlanes);
        _getTexStageState!(pDevice, 0, D3DTSS_TEXCOORDINDEX, out oldTss0TexCoordIdx);
        _getTexStageState!(pDevice, 0, D3DTSS_TEXTURETRANSFORMFLAGS, out oldTss0TexTransFlags);
        _getStreamSource!(pDevice, 0, out oldStreamData, out oldStreamOffset, out oldStreamStride);
        _getIndices!(pDevice, out oldIndexBuffer);

        try
        {
            SetupRenderStateNative(pDevice, displayPos, displaySize);

            const int ResetRenderStateSentinel = -8; // ImDrawCallback_ResetRenderState

            for (int n = 0; n < cmdListsCount; n++)
            {
                ImDrawList* list = cmdLists[n];
                if (list == null) continue;

                int vtxCount = list->VtxBuffer.Size;
                int cmdCount = list->CmdBuffer.Size;
                if (vtxCount == 0 || cmdCount == 0) continue;

                ImDrawVert* srcVtx = (ImDrawVert*)list->VtxBuffer.Data;
                ushort* idxBase = (ushort*)list->IdxBuffer.Data;
                ImDrawCmd* cmds = (ImDrawCmd*)list->CmdBuffer.Data;

                // ImGui vertices are pos/uv/RGBA; D3D9's FVF wants xyz/ARGB/uv.
                CustomVertex* vtxBuf = EnsureVertexScratch(vtxCount);
                for (int i = 0; i < vtxCount; i++)
                {
                    // Screen pixels relative to the viewport; -0.5 is D3D9's
                    // half-pixel offset (the XYZ path put it in the projection).
                    vtxBuf[i].X = srcVtx[i].pos.X - displayPos.X - 0.5f;
                    vtxBuf[i].Y = srcVtx[i].pos.Y - displayPos.Y - 0.5f;
                    vtxBuf[i].Z = 0f;
                    vtxBuf[i].Rhw = 1f;
                    uint c = srcVtx[i].col;
                    vtxBuf[i].Col = (c & 0xFF00FF00) | ((c & 0x00FF0000) >> 16) | ((c & 0x000000FF) << 16);
                    vtxBuf[i].U = srcVtx[i].uv.X;
                    vtxBuf[i].V = srcVtx[i].uv.Y;
                }

                for (int cmdI = 0; cmdI < cmdCount; cmdI++)
                {
                    ImDrawCmd* cmd = &cmds[cmdI];

                    if (cmd->UserCallback != IntPtr.Zero)
                    {
                        int cb = (int)cmd->UserCallback;
                        if (cb == ResetRenderStateSentinel)
                            SetupRenderStateNative(pDevice, displayPos, displaySize);
                        else if (cb == IconSampling.PointSentinel || cb == IconSampling.LinearSentinel)
                        {
                            uint f = cb == IconSampling.PointSentinel ? D3DTEXF_POINT : D3DTEXF_LINEAR;
                            _setSamplerState!(pDevice, 0, D3DSAMP_MINFILTER, f);
                            _setSamplerState!(pDevice, 0, D3DSAMP_MAGFILTER, f);
                        }
                        // Other callbacks are not supported (no managed callbacks
                        // from inside the render path); they carry no geometry.
                        continue;
                    }

                    uint elemCount = cmd->ElemCount;
                    _diagCmds++;
                    if (elemCount == 0) continue;

                    Vector4 clip = cmd->ClipRect;
                    RECT sr;
                    sr.Left = (int)(clip.X - displayPos.X);
                    sr.Top = (int)(clip.Y - displayPos.Y);
                    sr.Right = (int)(clip.Z - displayPos.X);
                    sr.Bottom = (int)(clip.W - displayPos.Y);
                    // Inside the viewport (0,0,displaySize; set above): a pop-out's picture is
                    // only part of its ImGui display, so a full-display clip rect reaches past
                    // it on every side. Nothing outside the viewport is drawn anyway.
                    if (sr.Left < 0) sr.Left = 0;
                    if (sr.Top < 0) sr.Top = 0;
                    if (sr.Right > (int)displaySize.X) sr.Right = (int)displaySize.X;
                    if (sr.Bottom > (int)displaySize.Y) sr.Bottom = (int)displaySize.Y;
                    if (sr.Right <= sr.Left || sr.Bottom <= sr.Top) { _diagSkipped++; continue; }
                    _setScissorRect!(pDevice, &sr);
                    _setTexture!(pDevice, 0, cmd->TextureId);

                    // DrawIndexedPrimitiveUP has no base-vertex argument, so the
                    // command's VtxOffset is applied by offsetting the vertex pointer.
                    uint vtxOffset = cmd->VtxOffset;
                    if (vtxOffset >= (uint)vtxCount) { _diagSkipped++; continue; }
                    int hr = _drawIndexedPrimUP!(pDevice,
                        D3DPT_TRIANGLELIST, 0, (uint)vtxCount - vtxOffset,
                        elemCount / 3,
                        (IntPtr)(idxBase + cmd->IdxOffset), D3DFMT_INDEX16,
                        (IntPtr)(vtxBuf + vtxOffset), (uint)sizeof(CustomVertex));
                    _diagDraws++;
                    if (hr < 0) { _diagFailed++; _diagLastHr = hr; }
                }
            }
        }
        finally
        {
            // ── Manual state restore ─────────────────────────────────
            _setRenderState!(pDevice, D3DRS_CULLMODE, oldCull);
            _setRenderState!(pDevice, D3DRS_LIGHTING, oldLighting);
            _setRenderState!(pDevice, D3DRS_ZENABLE, oldZEnable);
            _setRenderState!(pDevice, D3DRS_ALPHABLENDENABLE, oldAlphaBlend);
            _setRenderState!(pDevice, D3DRS_BLENDOP, oldBlendOp);
            _setRenderState!(pDevice, D3DRS_SRCBLEND, oldSrcBlend);
            _setRenderState!(pDevice, D3DRS_DESTBLEND, oldDestBlend);
            _setRenderState!(pDevice, D3DRS_SCISSORTESTENABLE, oldScissorEnable);
            _setRenderState!(pDevice, D3DRS_SHADEMODE, oldShade);
            _setRenderState!(pDevice, D3DRS_FOGENABLE, oldFog);
            _setRenderState!(pDevice, D3DRS_STENCILENABLE, oldStencil);
            _setRenderState!(pDevice, D3DRS_COLORWRITEENABLE, oldColorWrite);
            _setRenderState!(pDevice, D3DRS_SRGBWRITEENABLE, oldSRGB);
            _setRenderState!(pDevice, D3DRS_MULTISAMPLEANTIALIAS, oldMSAA);
            _setRenderState!(pDevice, D3DRS_SEPARATEALPHABLENDENABLE, oldSepAlpha);
            _setViewport!(pDevice, &oldVp);
            _setScissorRect!(pDevice, &oldScissor);
            _setTexture!(pDevice, 0, oldTexture);
            _setFVF!(pDevice, oldFVF);
            _setVertexShader!(pDevice, oldVS);
            _setPixelShader!(pDevice, oldPS);
            _setTransform!(pDevice, D3DTS_WORLD, &oldWorld);
            _setTransform!(pDevice, D3DTS_VIEW, &oldView);
            _setTransform!(pDevice, D3DTS_PROJECTION, &oldProj);
            _setTexStageState!(pDevice, 0, D3DTSS_COLOROP, oldTss0ColorOp);
            _setTexStageState!(pDevice, 0, D3DTSS_COLORARG1, oldTss0ColorArg1);
            _setTexStageState!(pDevice, 0, D3DTSS_COLORARG2, oldTss0ColorArg2);
            _setTexStageState!(pDevice, 0, D3DTSS_ALPHAOP, oldTss0AlphaOp);
            _setTexStageState!(pDevice, 0, D3DTSS_ALPHAARG1, oldTss0AlphaArg1);
            _setTexStageState!(pDevice, 0, D3DTSS_ALPHAARG2, oldTss0AlphaArg2);
            _setTexStageState!(pDevice, 1, D3DTSS_COLOROP, oldTss1ColorOp);
            _setTexStageState!(pDevice, 1, D3DTSS_ALPHAOP, oldTss1AlphaOp);
            _setSamplerState!(pDevice, 0, D3DSAMP_MINFILTER, oldSamp0Min);
            _setSamplerState!(pDevice, 0, D3DSAMP_MAGFILTER, oldSamp0Mag);
            _setRenderState!(pDevice, D3DRS_FILLMODE, oldFillMode);
            _setRenderState!(pDevice, D3DRS_ZWRITEENABLE, oldZWriteEnable);
            _setRenderState!(pDevice, D3DRS_ALPHATESTENABLE, oldAlphaTestEnable);
            _setRenderState!(pDevice, D3DRS_CLIPPING, oldClipping);
            _setRenderState!(pDevice, D3DRS_RANGEFOGENABLE, oldRangeFog);
            _setRenderState!(pDevice, D3DRS_SPECULARENABLE, oldSpecular);
            _setRenderState!(pDevice, D3DRS_SRCBLENDALPHA, oldSrcBlendAlpha);
            _setRenderState!(pDevice, D3DRS_DESTBLENDALPHA, oldDestBlendAlpha);
            _setRenderState!(pDevice, D3DRS_VERTEXBLEND, oldVertexBlend);
            _setRenderState!(pDevice, D3DRS_CLIPPLANEENABLE, oldClipPlanes);
            _setTexStageState!(pDevice, 0, D3DTSS_TEXCOORDINDEX, oldTss0TexCoordIdx);
            _setTexStageState!(pDevice, 0, D3DTSS_TEXTURETRANSFORMFLAGS, oldTss0TexTransFlags);
            _setStreamSource!(pDevice, 0, oldStreamData, oldStreamOffset, oldStreamStride);
            _setIndices!(pDevice, oldIndexBuffer);
            if (midFrame)
            {
                _setRenderState!(pDevice, D3DRS_WRAP0, oldWrap0);
                _setSamplerState!(pDevice, 0, D3DSAMP_ADDRESSU, oldSamp0AddrU);
                _setSamplerState!(pDevice, 0, D3DSAMP_ADDRESSV, oldSamp0AddrV);
                _setSamplerState!(pDevice, 0, D3DSAMP_MIPFILTER, oldSamp0Mip);
            }

            // Release COM refs we obtained
            if (oldTexture != IntPtr.Zero)
            {
                IntPtr vtbl = Marshal.ReadIntPtr(oldTexture);
                var rel = Marshal.GetDelegateForFunctionPointer<ReleaseD>(Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));
                rel(oldTexture);
            }
            if (oldVS != IntPtr.Zero)
            {
                IntPtr vtbl = Marshal.ReadIntPtr(oldVS);
                var rel = Marshal.GetDelegateForFunctionPointer<ReleaseD>(Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));
                rel(oldVS);
            }
            if (oldPS != IntPtr.Zero)
            {
                IntPtr vtbl = Marshal.ReadIntPtr(oldPS);
                var rel = Marshal.GetDelegateForFunctionPointer<ReleaseD>(Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));
                rel(oldPS);
            }
            if (oldStreamData != IntPtr.Zero)
            {
                IntPtr vtbl = Marshal.ReadIntPtr(oldStreamData);
                var rel = Marshal.GetDelegateForFunctionPointer<ReleaseD>(Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));
                rel(oldStreamData);
            }
            if (oldIndexBuffer != IntPtr.Zero)
            {
                IntPtr vtbl = Marshal.ReadIntPtr(oldIndexBuffer);
                var rel = Marshal.GetDelegateForFunctionPointer<ReleaseD>(Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));
                rel(oldIndexBuffer);
            }
        }
    }

    // ─── Internals ────────────────────────────────────────────────────

    private static void SetupRenderStateNative(IntPtr dev, Vector2 displayPos, Vector2 displaySize)
    {
        // Viewport
        D3DVIEWPORT9 vp;
        vp.X = 0; vp.Y = 0;
        vp.Width = (uint)displaySize.X;
        vp.Height = (uint)displaySize.Y;
        vp.MinZ = 0f; vp.MaxZ = 1f;
        _setViewport!(dev, &vp);

        // Render states (matches imgui_impl_dx9.cpp reference)
        _setPixelShader!(dev, IntPtr.Zero);
        _setVertexShader!(dev, IntPtr.Zero);
        _setRenderState!(dev, D3DRS_FILLMODE, D3DFILL_SOLID);
        _setRenderState!(dev, D3DRS_SHADEMODE, D3DSHADE_GOURAUD);
        _setRenderState!(dev, D3DRS_ZWRITEENABLE, 0);
        _setRenderState!(dev, D3DRS_ALPHATESTENABLE, 0);
        _setRenderState!(dev, D3DRS_CULLMODE, D3DCULL_NONE);
        _setRenderState!(dev, D3DRS_ZENABLE, 0);
        _setRenderState!(dev, D3DRS_ALPHABLENDENABLE, 1);
        _setRenderState!(dev, D3DRS_BLENDOP, D3DBLENDOP_ADD);
        _setRenderState!(dev, D3DRS_SRCBLEND, D3DBLEND_SRCALPHA);
        _setRenderState!(dev, D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA);
        _setRenderState!(dev, D3DRS_SEPARATEALPHABLENDENABLE, 1);
        _setRenderState!(dev, D3DRS_SRCBLENDALPHA, D3DBLEND_ONE);
        _setRenderState!(dev, D3DRS_DESTBLENDALPHA, D3DBLEND_INVSRCALPHA);
        _setRenderState!(dev, D3DRS_SCISSORTESTENABLE, 1);
        _setRenderState!(dev, D3DRS_FOGENABLE, 0);
        _setRenderState!(dev, D3DRS_RANGEFOGENABLE, 0);
        _setRenderState!(dev, D3DRS_SPECULARENABLE, 0);
        _setRenderState!(dev, D3DRS_STENCILENABLE, 0);
        _setRenderState!(dev, D3DRS_CLIPPING, 1);
        _setRenderState!(dev, D3DRS_LIGHTING, 0);
        _setRenderState!(dev, D3DRS_MULTISAMPLEANTIALIAS, 0);
        _setRenderState!(dev, D3DRS_COLORWRITEENABLE, 0xF);
        _setRenderState!(dev, D3DRS_SRGBWRITEENABLE, 0);

        // Texture stage state
        _setTexStageState!(dev, 0, D3DTSS_COLOROP, D3DTOP_MODULATE);
        _setTexStageState!(dev, 0, D3DTSS_COLORARG1, D3DTA_TEXTURE);
        _setTexStageState!(dev, 0, D3DTSS_COLORARG2, D3DTA_DIFFUSE);
        _setTexStageState!(dev, 0, D3DTSS_ALPHAOP, D3DTOP_MODULATE);
        _setTexStageState!(dev, 0, D3DTSS_ALPHAARG1, D3DTA_TEXTURE);
        _setTexStageState!(dev, 0, D3DTSS_ALPHAARG2, D3DTA_DIFFUSE);
        _setTexStageState!(dev, 0, D3DTSS_TEXCOORDINDEX, 0);
        _setTexStageState!(dev, 0, D3DTSS_TEXTURETRANSFORMFLAGS, 0); // D3DTTFF_DISABLE
        _setTexStageState!(dev, 1, D3DTSS_COLOROP, D3DTOP_DISABLE);
        _setTexStageState!(dev, 1, D3DTSS_ALPHAOP, D3DTOP_DISABLE);

        // Sampler state
        _setSamplerState!(dev, 0, D3DSAMP_MINFILTER, D3DTEXF_LINEAR);
        _setSamplerState!(dev, 0, D3DSAMP_MAGFILTER, D3DTEXF_LINEAR);

        // FVF — SetStreamSource is NOT called here; DrawIndexedPrimitiveUP uses user-memory
        // pointers directly and does not require a bound vertex stream.
        _setFVF!(dev, D3DFVF_CUSTOM);
        _setRenderState!(dev, D3DRS_VERTEXBLEND, 0);      // D3DVBF_DISABLE
        _setRenderState!(dev, D3DRS_CLIPPLANEENABLE, 0);  // no user clip planes

        // No transforms: XYZRHW vertices are already in viewport pixels (see CustomVertex).
    }

    /// <summary>
    /// After the font atlas was rebuilt: replace the font texture. Render thread.
    /// The old texture is kept one more frame: UnderUiLayer's list from the frame
    /// before may still be drawn by this frame's fallback, and it names the old one.
    /// </summary>
    public static bool RebuildFontTexture(IntPtr pDevice)
    {
        ReleaseRetiredFontTexture();
        _retiredFontTexture = _fontTexture;
        _fontTexture = IntPtr.Zero;
        return CreateFontTexture(pDevice);
    }

    // The font texture a rebuild replaced; released at the next frame's build.
    private static IntPtr _retiredFontTexture;

    /// <summary>Releases the texture the last rebuild replaced. Render thread, at the start of a frame's build.</summary>
    public static void ReleaseRetiredFontTexture()
    {
        if (_retiredFontTexture == IntPtr.Zero) return;
        var release = GetTexMethod<ReleaseD>(_retiredFontTexture, TextureVTableIndex.Release);
        release(_retiredFontTexture);
        _retiredFontTexture = IntPtr.Zero;
    }

    private static bool CreateFontTexture(IntPtr pDevice)
    {
        ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();

        // Get font atlas pixel data
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int bytesPerPixel);

        RynthLog.Render($"DX9Backend: Font texture {width}x{height}");

        // Create D3D9 texture
        int hr = _createTexture!(pDevice, (uint)width, (uint)height, 1, 0,
            D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, out _fontTexture, IntPtr.Zero);

        if (hr < 0 || _fontTexture == IntPtr.Zero)
        {
            RynthLog.Render($"DX9Backend: CreateTexture failed, HR=0x{hr:X8}");
            return false;
        }

        // Lock texture and copy pixel data
        var lockRect = GetTexMethod<TexLockRectD>(_fontTexture, TextureVTableIndex.LockRect);
        var unlockRect = GetTexMethod<TexUnlockRectD>(_fontTexture, TextureVTableIndex.UnlockRect);

        D3DLOCKED_RECT locked;
        hr = lockRect(_fontTexture, 0, &locked, IntPtr.Zero, 0);
        if (hr < 0)
        {
            RynthLog.Render($"DX9Backend: LockRect failed, HR=0x{hr:X8}");
            return false;
        }

        // Copy with RGBA → BGRA swizzle (ImGui gives RGBA, D3D9 A8R8G8B8 is BGRA in memory)
        byte* src = (byte*)pixels;
        byte* dst = (byte*)locked.pBits;

        for (int y = 0; y < height; y++)
        {
            byte* srcRow = src + y * width * 4;
            byte* dstRow = dst + y * locked.Pitch;

            for (int x = 0; x < width; x++)
            {
                int si = x * 4;
                int di = x * 4;
                dstRow[di + 0] = srcRow[si + 2]; // B ← R
                dstRow[di + 1] = srcRow[si + 1]; // G ← G
                dstRow[di + 2] = srcRow[si + 0]; // R ← B
                dstRow[di + 3] = srcRow[si + 3]; // A ← A
            }
        }

        unlockRect(_fontTexture, 0);

        // Store texture ID for ImGui
        io.Fonts.SetTexID(_fontTexture);

        // The glyphs are baked and uploaded: the atlas's input data is no longer read. ImGui
        // copies the whole TTF into the atlas for every font it adds (main font, Segoe UI
        // Symbol and Phosphor per size, ~3.8 MB each), so with the panel text sizes in use
        // that was ~400 MB kept for the life of the atlas (2026-10-15, [Mem] lines). Every
        // re-bake builds a fresh atlas from ImGuiFonts' own copies, and the pixels and TexReady
        // stay, so nothing needs it back.
        Compatibility.MemoryUse.Mark("font atlas uploaded");
        io.Fonts.ClearInputData();
        Compatibility.MemoryUse.Mark("font input data freed");
        return true;
    }

    /// <summary>
    /// A managed-pool A8R8G8B8 texture (it survives a device reset) holding RGBA pixels
    /// (script window icons). Zero on failure. AC's render thread, after <see cref="InitCore"/>.
    /// </summary>
    public static IntPtr CreateTextureRgba(IntPtr pDevice, int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (_createTexture == null || pDevice == IntPtr.Zero || width <= 0 || height <= 0
            || rgba.Length < width * height * 4)
            return IntPtr.Zero;
        int hr = _createTexture(pDevice, (uint)width, (uint)height, 1, 0,
            D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, out IntPtr tex, IntPtr.Zero);
        if (hr < 0 || tex == IntPtr.Zero)
            return IntPtr.Zero;

        var lockRect = GetTexMethod<TexLockRectD>(tex, TextureVTableIndex.LockRect);
        var unlockRect = GetTexMethod<TexUnlockRectD>(tex, TextureVTableIndex.UnlockRect);
        D3DLOCKED_RECT locked;
        if (lockRect(tex, 0, &locked, IntPtr.Zero, 0) < 0 || locked.pBits == IntPtr.Zero)
        {
            ReleaseComObject(tex);
            return IntPtr.Zero;
        }
        byte* dst = (byte*)locked.pBits;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> srcRow = rgba.Slice(y * width * 4, width * 4);
            byte* dstRow = dst + y * locked.Pitch;
            for (int x = 0; x < width; x++)
            {
                int i = x * 4;
                dstRow[i + 0] = srcRow[i + 2]; // B
                dstRow[i + 1] = srcRow[i + 1]; // G
                dstRow[i + 2] = srcRow[i + 0]; // R
                dstRow[i + 3] = srcRow[i + 3]; // A
            }
        }
        unlockRect(tex, 0);
        return tex;
    }

    /// <summary>
    /// A managed-pool DXT1 texture (4x4 blocks of 8 bytes: two RGB565 colours and sixteen 2-bit indices, a sixth
    /// of A8R8G8B8's size) holding <paramref name="blocks"/> as they are: <paramref name="width"/> x
    /// <paramref name="height"/> a multiple of 4, (w/4) * (h/4) * 8 bytes, top row of blocks first (the World
    /// Map's tiles). Zero on failure or when the device can't make DXT1. AC's render thread.
    /// </summary>
    public static IntPtr CreateTextureDxt1(IntPtr pDevice, int width, int height, ReadOnlySpan<byte> blocks)
    {
        int rowBytes = width / 4 * 8, rows = height / 4;
        if (_createTexture == null || pDevice == IntPtr.Zero || width <= 0 || height <= 0 || width % 4 != 0 || height % 4 != 0
            || blocks.Length < rowBytes * rows)
            return IntPtr.Zero;
        int hr = _createTexture(pDevice, (uint)width, (uint)height, 1, 0,
            D3DFMT_DXT1, D3DPOOL_MANAGED, out IntPtr tex, IntPtr.Zero);
        if (hr < 0 || tex == IntPtr.Zero)
            return IntPtr.Zero;

        var lockRect = GetTexMethod<TexLockRectD>(tex, TextureVTableIndex.LockRect);
        var unlockRect = GetTexMethod<TexUnlockRectD>(tex, TextureVTableIndex.UnlockRect);
        D3DLOCKED_RECT locked;
        if (lockRect(tex, 0, &locked, IntPtr.Zero, 0) < 0 || locked.pBits == IntPtr.Zero)
        {
            ReleaseComObject(tex);
            return IntPtr.Zero;
        }
        byte* dst = (byte*)locked.pBits;
        for (int y = 0; y < rows; y++)       // the pitch of a compressed surface is the bytes of one row of blocks
            blocks.Slice(y * rowBytes, rowBytes).CopyTo(new Span<byte>(dst + y * locked.Pitch, rowBytes));
        unlockRect(tex, 0);
        return tex;
    }

    /// <summary>Releases a texture made by <see cref="CreateTextureRgba"/>. AC's render thread.</summary>
    public static void ReleaseTexture(IntPtr tex) => ReleaseComObject(tex);

    private static void CacheDelegates(IntPtr pDevice)
    {
        IntPtr vtable = Marshal.ReadIntPtr(pDevice);

        _getCreationParameters = Get<GetCreationParametersD>(vtable, DeviceVTableIndex.GetCreationParameters);
        _getBackBuffer = Get<GetBackBufferD>(vtable, DeviceVTableIndex.GetBackBuffer);
        _getRenderTargetSurface = Get<GetRenderTargetD>(vtable, DeviceVTableIndex.GetRenderTarget);
        _setRenderState = Get<SetRenderStateD>(vtable, DeviceVTableIndex.SetRenderState);
        _getRenderState = Get<GetRenderStateD>(vtable, DeviceVTableIndex.GetRenderState);
        _setTexture = Get<SetTextureD>(vtable, DeviceVTableIndex.SetTexture);
        _getTexture = Get<GetTextureD>(vtable, DeviceVTableIndex.GetTexture);
        _setTexStageState = Get<SetTextureStageStateD>(vtable, DeviceVTableIndex.SetTextureStageState);
        _getTexStageState = Get<GetTextureStageStateD>(vtable, DeviceVTableIndex.GetTextureStageState);
        _setSamplerState = Get<SetSamplerStateD>(vtable, DeviceVTableIndex.SetSamplerState);
        _getSamplerState = Get<GetSamplerStateD>(vtable, DeviceVTableIndex.GetSamplerState);
        _setFVF = Get<SetFVFD>(vtable, DeviceVTableIndex.SetFVF);
        _getFVF = Get<GetFVFD>(vtable, DeviceVTableIndex.GetFVF);
        _setVertexShader = Get<SetVertexShaderD>(vtable, DeviceVTableIndex.SetVertexShader);
        _getVertexShader = Get<GetVertexShaderD>(vtable, DeviceVTableIndex.GetVertexShader);
        _setPixelShader = Get<SetPixelShaderD>(vtable, DeviceVTableIndex.SetPixelShader);
        _getPixelShader = Get<GetPixelShaderD>(vtable, DeviceVTableIndex.GetPixelShader);
        _setTransform = Get<SetTransformD>(vtable, DeviceVTableIndex.SetTransform);
        _getTransform = Get<GetTransformD>(vtable, DeviceVTableIndex.GetTransform);
        _getViewport = Get<GetViewportD>(vtable, DeviceVTableIndex.GetViewport);
        _setViewport = Get<SetViewportD>(vtable, DeviceVTableIndex.SetViewport);
        _setScissorRect = Get<SetScissorRectD>(vtable, DeviceVTableIndex.SetScissorRect);
        _getScissorRect = Get<GetScissorRectD>(vtable, DeviceVTableIndex.GetScissorRect);
        _drawIndexedPrimUP = Get<DrawIndexedPrimitiveUPD>(vtable, DeviceVTableIndex.DrawIndexedPrimitiveUP);
        _drawPrimUP = Get<DrawPrimitiveUPD>(vtable, DeviceVTableIndex.DrawPrimitiveUP);
        _createTexture = Get<CreateTextureD>(vtable, DeviceVTableIndex.CreateTexture);
        _setStreamSource = Get<SetStreamSourceD>(vtable, DeviceVTableIndex.SetStreamSource);
        _getStreamSource = Get<GetStreamSourceD>(vtable, DeviceVTableIndex.GetStreamSource);
        _setIndices = Get<SetIndicesD>(vtable, DeviceVTableIndex.SetIndices);
        _getIndices = Get<GetIndicesD>(vtable, DeviceVTableIndex.GetIndices);
    }

    private static T Get<T>(IntPtr vtable, int index) where T : Delegate
    {
        IntPtr addr = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    private static T GetTexMethod<T>(IntPtr pTexture, int index) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(pTexture);
        IntPtr addr = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    private static IntPtr QueryIUnknown(IntPtr pObject)
    {
        IntPtr vtable = Marshal.ReadIntPtr(pObject);
        var query = Marshal.GetDelegateForFunctionPointer<QueryInterfaceD>(Marshal.ReadIntPtr(vtable, 0));
        Guid iid = IID_IUnknown;
        return query(pObject, &iid, out IntPtr identity) >= 0 ? identity : IntPtr.Zero;
    }

    private static void ReleaseComObject(IntPtr pObject)
    {
        if (pObject == IntPtr.Zero)
            return;

        IntPtr vtable = Marshal.ReadIntPtr(pObject);
        var release = Marshal.GetDelegateForFunctionPointer<ReleaseD>(Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size));
        release(pObject);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  3D Nav Marker rendering — uses the already-cached D3D9 delegates
    // ═══════════════════════════════════════════════════════════════════

    // Vertex: position + diffuse color (no texture)
    private const uint D3DFVF_NAV3D = D3DFVF_XYZ | D3DFVF_DIFFUSE;
    private const uint D3DPT_TRIANGLESTRIP = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct NavVertex
    {
        public float X, Y, Z;
        public uint Col; // ARGB
    }

    private const int NavRingSegments = 32;
    private static readonly float[] _navCos = new float[NavRingSegments];
    private static readonly float[] _navSin = new float[NavRingSegments];
    private static bool _navTrigReady;
    private static int _nav3DLogCount;

    // Persistent heap buffer for batched triangle rendering. Filled once per
    // RenderNav3D call with every Nav3D triangle, then drawn in one
    // DrawPrimitiveUP — avoids the 4000+ draw-call cost of doing one call per
    // triangle (the cost showed up as motion-related flicker at high radii).
    // Grown on demand; never freed until process exit.
    private static IntPtr _navTriBatch;
    private static int _navTriBatchCapacityVerts;

    private static void EnsureNavTriBatch(int neededVerts)
    {
        if (neededVerts <= _navTriBatchCapacityVerts) return;
        int newCap = _navTriBatchCapacityVerts == 0 ? 1024 : _navTriBatchCapacityVerts;
        while (newCap < neededVerts) newCap *= 2;
        if (_navTriBatch != IntPtr.Zero) Marshal.FreeHGlobal(_navTriBatch);
        _navTriBatch = Marshal.AllocHGlobal(newCap * sizeof(NavVertex));
        _navTriBatchCapacityVerts = newCap;
    }

    private static void EnsureNavTrig()
    {
        if (_navTrigReady) return;
        for (int i = 0; i < NavRingSegments; i++)
        {
            double a = 2.0 * Math.PI * i / NavRingSegments;
            _navCos[i] = (float)Math.Cos(a);
            _navSin[i] = (float)Math.Sin(a);
        }
        _navTrigReady = true;
    }

    // AC's 3D view viewport, read at EndScene (AC has put it back by then; the EndScene
    // fallback has always drawn the markers with it). AC's render thread only.
    private static D3DVIEWPORT9 _sceneViewport;
    private static bool _hasSceneViewport;

    /// <summary>
    /// EndScene, before anything of ours touches the viewport: remembers AC's 3D view
    /// viewport for <see cref="RenderNav3D(IntPtr, bool)"/> at the UI pass, where AC has
    /// set a full-screen one. AC's render thread.
    /// </summary>
    public static void CaptureSceneViewport(IntPtr pDevice)
    {
        if (!_coreInitialized || _getViewport == null || pDevice == IntPtr.Zero) return;
        D3DVIEWPORT9 vp;
        if (_getViewport(pDevice, &vp) >= 0 && vp.Width > 0 && vp.Height > 0)
        {
            _sceneViewport = vp;
            _hasSceneViewport = true;
        }
    }

    /// <summary>
    /// <see cref="RenderNav3D(IntPtr)"/>; with <paramref name="sceneViewport"/> (AC's UI
    /// pass) the markers draw in AC's 3D view viewport (from the last EndScene) when the
    /// current one differs, and the current one is put back afterwards.
    /// </summary>
    public static void RenderNav3D(IntPtr pDevice, bool sceneViewport)
    {
        if (!sceneViewport || !_hasSceneViewport || !_coreInitialized || pDevice == IntPtr.Zero ||
            _getViewport == null || _setViewport == null)
        {
            RenderNav3D(pDevice);
            return;
        }
        D3DVIEWPORT9 current;
        if (_getViewport(pDevice, &current) < 0)
        {
            RenderNav3D(pDevice);
            return;
        }
        D3DVIEWPORT9 scene = _sceneViewport;
        bool same = current.X == scene.X && current.Y == scene.Y && current.Width == scene.Width &&
                    current.Height == scene.Height && current.MinZ == scene.MinZ && current.MaxZ == scene.MaxZ;
        if (same)
        {
            RenderNav3D(pDevice);
            return;
        }
        _setViewport(pDevice, &scene);
        try { RenderNav3D(pDevice); }
        finally { _setViewport(pDevice, &current); }
    }

    public static void RenderNav3D(IntPtr pDevice)
    {
        // Nav3D only needs the cached function pointers + nav trig + GameMatrixCapture;
        // it doesn't touch the ImGui font texture / vertex buffers. Gate on the
        // core init flag so this path runs even when EnableImGuiBackend=false.
        if (!_coreInitialized || pDevice == IntPtr.Zero) return;
        if (!D3D9.GameMatrixCapture.HasCapturedFrame) return;
        // One snapshot of the committed frame for the whole draw (the pump may
        // commit a new one meanwhile; this one stays intact for a tick).
        D3D9.Nav3DRenderer.NavBuffer frame = D3D9.Nav3DRenderer.Ready;
        int triCount = frame.TriCount, lineCount = frame.LineCount, ringCount = frame.RingCount;
        if (ringCount == 0 && lineCount == 0 && triCount == 0) return;

        EnsureNavTrig();

        // Save minimal state
        D3DMATRIX oldWorld = default, oldView = default, oldProj = default;
        uint oldFVF = 0, oldLighting = 0, oldZEnable = 0, oldZWrite = 0;
        uint oldAlphaBlend = 0, oldSrcBlend = 0, oldDestBlend = 0;
        uint oldCull = 0, oldFog = 0, oldScissor = 0, oldFill = 0;
        uint oldBlendOp = 0, oldAlphaTest = 0, oldColorWrite = 0, oldZFunc = 0;
        uint oldSlopeDepthBias = 0, oldDepthBias = 0;

        IntPtr oldTexture = IntPtr.Zero, oldVS = IntPtr.Zero, oldPS = IntPtr.Zero;
        uint oldTss0ColorOp = 0, oldTss0ColorArg1 = 0, oldTss0AlphaOp = 0, oldTss0AlphaArg1 = 0;
        uint oldTss1ColorOp = 0, oldTss1AlphaOp = 0;

        _getTransform!(pDevice, D3DTS_WORLD, &oldWorld);
        _getTransform!(pDevice, D3DTS_VIEW, &oldView);
        _getTransform!(pDevice, D3DTS_PROJECTION, &oldProj);
        _getFVF!(pDevice, out oldFVF);
        _getRenderState!(pDevice, D3DRS_LIGHTING, out oldLighting);
        _getRenderState!(pDevice, D3DRS_ZENABLE, out oldZEnable);
        _getRenderState!(pDevice, D3DRS_ZWRITEENABLE, out oldZWrite);
        _getRenderState!(pDevice, D3DRS_ZFUNC, out oldZFunc);
        _getRenderState!(pDevice, D3DRS_ALPHABLENDENABLE, out oldAlphaBlend);
        _getRenderState!(pDevice, D3DRS_SRCBLEND, out oldSrcBlend);
        _getRenderState!(pDevice, D3DRS_DESTBLEND, out oldDestBlend);
        _getRenderState!(pDevice, D3DRS_CULLMODE, out oldCull);
        _getRenderState!(pDevice, D3DRS_FOGENABLE, out oldFog);
        _getRenderState!(pDevice, D3DRS_SCISSORTESTENABLE, out oldScissor);
        _getRenderState!(pDevice, D3DRS_FILLMODE, out oldFill);
        _getRenderState!(pDevice, D3DRS_BLENDOP, out oldBlendOp);
        _getRenderState!(pDevice, D3DRS_ALPHATESTENABLE, out oldAlphaTest);
        _getRenderState!(pDevice, D3DRS_COLORWRITEENABLE, out oldColorWrite);
        _getRenderState!(pDevice, D3DRS_SLOPESCALEDEPTHBIAS, out oldSlopeDepthBias);
        _getRenderState!(pDevice, D3DRS_DEPTHBIAS, out oldDepthBias);

        _getTexture!(pDevice, 0, out oldTexture);
        _getVertexShader!(pDevice, out oldVS);
        _getPixelShader!(pDevice, out oldPS);
        _getTexStageState!(pDevice, 0, D3DTSS_COLOROP, out oldTss0ColorOp);
        _getTexStageState!(pDevice, 0, D3DTSS_COLORARG1, out oldTss0ColorArg1);
        _getTexStageState!(pDevice, 0, D3DTSS_ALPHAOP, out oldTss0AlphaOp);
        _getTexStageState!(pDevice, 0, D3DTSS_ALPHAARG1, out oldTss0AlphaArg1);
        _getTexStageState!(pDevice, 1, D3DTSS_COLOROP, out oldTss1ColorOp);
        _getTexStageState!(pDevice, 1, D3DTSS_ALPHAOP, out oldTss1AlphaOp);

        try
        {
            // Set render state for 3D colored geometry
            _setPixelShader!(pDevice, IntPtr.Zero);
            _setVertexShader!(pDevice, IntPtr.Zero);
            _setTexture!(pDevice, 0, IntPtr.Zero);
            _setFVF!(pDevice, D3DFVF_NAV3D);
            _setRenderState!(pDevice, D3DRS_FILLMODE, D3DFILL_SOLID);
            _setRenderState!(pDevice, D3DRS_CULLMODE, D3DCULL_NONE);
            _setRenderState!(pDevice, D3DRS_LIGHTING, 0);
            _setRenderState!(pDevice, D3DRS_ZENABLE, 1);
            _setRenderState!(pDevice, D3DRS_ZFUNC, D3DCMP_LESSEQUAL);
            _setRenderState!(pDevice, D3DRS_ZWRITEENABLE, 0);
            // Depth bias: push markers toward camera to prevent Z-fighting with terrain
            _setRenderState!(pDevice, D3DRS_SLOPESCALEDEPTHBIAS, BitConverter.SingleToUInt32Bits(-2.0f));
            _setRenderState!(pDevice, D3DRS_DEPTHBIAS, BitConverter.SingleToUInt32Bits(-0.0001f));
            _setRenderState!(pDevice, D3DRS_ALPHABLENDENABLE, 1);
            _setRenderState!(pDevice, D3DRS_BLENDOP, D3DBLENDOP_ADD);
            _setRenderState!(pDevice, D3DRS_SRCBLEND, D3DBLEND_SRCALPHA);
            _setRenderState!(pDevice, D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA);
            _setRenderState!(pDevice, D3DRS_ALPHATESTENABLE, 0);
            _setRenderState!(pDevice, D3DRS_FOGENABLE, 0);
            _setRenderState!(pDevice, D3DRS_SCISSORTESTENABLE, 0);
            _setRenderState!(pDevice, D3DRS_COLORWRITEENABLE, 0xF);
            // No texture — diffuse color only
            _setTexStageState!(pDevice, 0, D3DTSS_COLOROP, D3DTOP_SELECTARG1);
            _setTexStageState!(pDevice, 0, D3DTSS_COLORARG1, D3DTA_DIFFUSE);
            _setTexStageState!(pDevice, 0, D3DTSS_ALPHAOP, D3DTOP_SELECTARG1);
            _setTexStageState!(pDevice, 0, D3DTSS_ALPHAARG1, D3DTA_DIFFUSE);
            _setTexStageState!(pDevice, 1, D3DTSS_COLOROP, D3DTOP_DISABLE);
            _setTexStageState!(pDevice, 1, D3DTSS_ALPHAOP, D3DTOP_DISABLE);

            // Set transforms: World=identity, View+Proj from GameMatrixCapture
            D3DMATRIX identity = default;
            identity.M11 = 1; identity.M22 = 1; identity.M33 = 1; identity.M44 = 1;
            _setTransform!(pDevice, D3DTS_WORLD, &identity);

            D3DMATRIX viewMat = default;
            D3DMATRIX projMat = default;
            D3D9.GameMatrixCapture.GetViewMatrix(ref viewMat.M11);
            D3D9.GameMatrixCapture.GetProjectionMatrix(ref projMat.M11);
            _setTransform!(pDevice, D3DTS_VIEW, &viewMat);
            _setTransform!(pDevice, D3DTS_PROJECTION, &projMat);

            // Draw triangles first (terrain-conforming faces, sit beneath
            // everything else). All triangles are packed into one heap-backed
            // vertex buffer and submitted in a single DrawPrimitiveUP — at
            // high slope-radius settings we can have 4000+ triangles per
            // frame, and one DP-UP per triangle was visibly frame-dropping.
            if (triCount > 0)
            {
                EnsureNavTriBatch(triCount * 3);
                NavVertex* batch = (NavVertex*)_navTriBatch;
                for (int i = 0; i < triCount; i++)
                {
                    uint color = frame.TriColor[i];
                    int b = i * 3;
                    batch[b    ].X = frame.TriX1[i]; batch[b    ].Y = frame.TriY1[i]; batch[b    ].Z = frame.TriZ1[i]; batch[b    ].Col = color;
                    batch[b + 1].X = frame.TriX2[i]; batch[b + 1].Y = frame.TriY2[i]; batch[b + 1].Z = frame.TriZ2[i]; batch[b + 1].Col = color;
                    batch[b + 2].X = frame.TriX3[i]; batch[b + 2].Y = frame.TriY3[i]; batch[b + 2].Z = frame.TriZ3[i]; batch[b + 2].Col = color;
                }
                _drawPrimUP!(pDevice, D3DPT_TRIANGLELIST, (uint)triCount, (IntPtr)batch, (uint)sizeof(NavVertex));
            }

            // Draw lines (extruded XZ-plane quads — for edges/strips)
            for (int i = 0; i < lineCount; i++)
            {
                DrawNavLine(pDevice, frame.LineX1[i], frame.LineY1[i], frame.LineZ1[i],
                    frame.LineX2[i], frame.LineY2[i], frame.LineZ2[i], frame.LineThick[i], frame.LineColor[i]);
            }

            // Draw rings
            for (int i = 0; i < ringCount; i++)
            {
                DrawNavRing(pDevice, frame.RingX[i], frame.RingY[i], frame.RingZ[i],
                    frame.RingRadius[i], frame.RingThick[i], frame.RingHeight[i], frame.RingColor[i]);
            }

            if (_nav3DLogCount == 0)
                _nav3DLogCount++;
        }
        catch (Exception ex)
        {
            if (_nav3DLogCount < 5)
            {
                RynthLog.Render($"DX9Backend.RenderNav3D: {ex.GetType().Name}: {ex.Message}");
                _nav3DLogCount++;
            }
        }
        finally
        {
            _setTransform!(pDevice, D3DTS_WORLD, &oldWorld);
            _setTransform!(pDevice, D3DTS_VIEW, &oldView);
            _setTransform!(pDevice, D3DTS_PROJECTION, &oldProj);
            _setFVF!(pDevice, oldFVF);
            _setRenderState!(pDevice, D3DRS_LIGHTING, oldLighting);
            _setRenderState!(pDevice, D3DRS_ZENABLE, oldZEnable);
            _setRenderState!(pDevice, D3DRS_ZFUNC, oldZFunc);
            _setRenderState!(pDevice, D3DRS_ZWRITEENABLE, oldZWrite);
            _setRenderState!(pDevice, D3DRS_ALPHABLENDENABLE, oldAlphaBlend);
            _setRenderState!(pDevice, D3DRS_SRCBLEND, oldSrcBlend);
            _setRenderState!(pDevice, D3DRS_DESTBLEND, oldDestBlend);
            _setRenderState!(pDevice, D3DRS_CULLMODE, oldCull);
            _setRenderState!(pDevice, D3DRS_FOGENABLE, oldFog);
            _setRenderState!(pDevice, D3DRS_SCISSORTESTENABLE, oldScissor);
            _setRenderState!(pDevice, D3DRS_FILLMODE, oldFill);
            _setRenderState!(pDevice, D3DRS_BLENDOP, oldBlendOp);
            _setRenderState!(pDevice, D3DRS_ALPHATESTENABLE, oldAlphaTest);
            _setRenderState!(pDevice, D3DRS_COLORWRITEENABLE, oldColorWrite);
            _setRenderState!(pDevice, D3DRS_SLOPESCALEDEPTHBIAS, oldSlopeDepthBias);
            _setRenderState!(pDevice, D3DRS_DEPTHBIAS, oldDepthBias);

            _setTexture!(pDevice, 0, oldTexture);
            _setVertexShader!(pDevice, oldVS);
            _setPixelShader!(pDevice, oldPS);
            _setTexStageState!(pDevice, 0, D3DTSS_COLOROP, oldTss0ColorOp);
            _setTexStageState!(pDevice, 0, D3DTSS_COLORARG1, oldTss0ColorArg1);
            _setTexStageState!(pDevice, 0, D3DTSS_ALPHAOP, oldTss0AlphaOp);
            _setTexStageState!(pDevice, 0, D3DTSS_ALPHAARG1, oldTss0AlphaArg1);
            _setTexStageState!(pDevice, 1, D3DTSS_COLOROP, oldTss1ColorOp);
            _setTexStageState!(pDevice, 1, D3DTSS_ALPHAOP, oldTss1AlphaOp);
            ReleaseComObject(oldTexture);
            ReleaseComObject(oldVS);
            ReleaseComObject(oldPS);
        }
    }

    private static void DrawNavRing(IntPtr pDevice, float cx, float cy, float cz,
        float radius, float thickness, float height, uint color)
    {
        // Outer cylinder wall: triangle strip alternating top/bottom vertices.
        // Wall height comes from the per-ring Height field (set via the Ex
        // submission) so thickness and height tune independently — a thin
        // but tall ring is what makes the radar range visible at a glance.
        int wallVertCount = (NavRingSegments + 1) * 2;
        NavVertex* wallVerts = stackalloc NavVertex[wallVertCount];

        // Fade top edge to transparent for a nicer look
        uint colorTop = (color & 0x00FFFFFF) | 0x10000000; // ~6% alpha at top
        float yBottom = cy;
        float yTop = cy + MathF.Max(height, 0.1f);

        for (int i = 0; i <= NavRingSegments; i++)
        {
            int idx = i % NavRingSegments;
            float cosA = _navCos[idx];
            float sinA = _navSin[idx];
            float wx = cx + radius * cosA;
            float wz = cz + radius * sinA;

            wallVerts[i * 2].X = wx;
            wallVerts[i * 2].Y = yTop;
            wallVerts[i * 2].Z = wz;
            wallVerts[i * 2].Col = colorTop;

            wallVerts[i * 2 + 1].X = wx;
            wallVerts[i * 2 + 1].Y = yBottom;
            wallVerts[i * 2 + 1].Z = wz;
            wallVerts[i * 2 + 1].Col = color;
        }

        _drawPrimUP!(pDevice, D3DPT_TRIANGLESTRIP, (uint)(wallVertCount - 2), (IntPtr)wallVerts, (uint)sizeof(NavVertex));

        // Bottom rim: flat annulus on the ground for visibility from above
        int rimVertCount = (NavRingSegments + 1) * 2;
        NavVertex* rimVerts = stackalloc NavVertex[rimVertCount];
        float innerR = Math.Max(0.01f, radius - thickness);
        float outerR = radius + thickness;

        for (int i = 0; i <= NavRingSegments; i++)
        {
            int idx = i % NavRingSegments;
            float cosA = _navCos[idx];
            float sinA = _navSin[idx];
            rimVerts[i * 2].X = cx + outerR * cosA;
            rimVerts[i * 2].Y = yBottom;
            rimVerts[i * 2].Z = cz + outerR * sinA;
            rimVerts[i * 2].Col = color;
            rimVerts[i * 2 + 1].X = cx + innerR * cosA;
            rimVerts[i * 2 + 1].Y = yBottom;
            rimVerts[i * 2 + 1].Z = cz + innerR * sinA;
            rimVerts[i * 2 + 1].Col = color;
        }

        _drawPrimUP!(pDevice, D3DPT_TRIANGLESTRIP, (uint)(rimVertCount - 2), (IntPtr)rimVerts, (uint)sizeof(NavVertex));
    }

    private const float NavLineHeight = 0.15f; // vertical extent of connecting lines

    private static void DrawNavLine(IntPtr pDevice, float x1, float y1, float z1,
        float x2, float y2, float z2, float thickness, uint color)
    {
        float dx = x2 - x1;
        float dz = z2 - z1;
        float len = (float)Math.Sqrt(dx * dx + dz * dz);
        if (len < 0.001f) return;

        float px = -dz / len * thickness;
        float pz =  dx / len * thickness;

        // Fade top edge for visual softness
        uint colorTop = (color & 0x00FFFFFF) | 0x10000000;

        // Bottom quad (on ground, visible from above)
        NavVertex* verts = stackalloc NavVertex[4];
        verts[0].X = x1 - px; verts[0].Y = y1; verts[0].Z = z1 - pz; verts[0].Col = color;
        verts[1].X = x1 + px; verts[1].Y = y1; verts[1].Z = z1 + pz; verts[1].Col = color;
        verts[2].X = x2 - px; verts[2].Y = y2; verts[2].Z = z2 - pz; verts[2].Col = color;
        verts[3].X = x2 + px; verts[3].Y = y2; verts[3].Z = z2 + pz; verts[3].Col = color;
        _drawPrimUP!(pDevice, D3DPT_TRIANGLESTRIP, 2, (IntPtr)verts, (uint)sizeof(NavVertex));

        // Vertical wall along the line (visible from the side)
        NavVertex* wall = stackalloc NavVertex[4];
        float midPx = (px + 0) * 0.5f; // center line for vertical wall
        float midPz = (pz + 0) * 0.5f;
        wall[0].X = x1; wall[0].Y = y1 + NavLineHeight; wall[0].Z = z1; wall[0].Col = colorTop;
        wall[1].X = x1; wall[1].Y = y1;                  wall[1].Z = z1; wall[1].Col = color;
        wall[2].X = x2; wall[2].Y = y2 + NavLineHeight; wall[2].Z = z2; wall[2].Col = colorTop;
        wall[3].X = x2; wall[3].Y = y2;                  wall[3].Z = z2; wall[3].Col = color;
        _drawPrimUP!(pDevice, D3DPT_TRIANGLESTRIP, 2, (IntPtr)wall, (uint)sizeof(NavVertex));
    }

    // Filled 3D triangle in world space — vertices passed straight through to
    // the device. Cull mode is already NONE in RenderNav3D so the triangle
    // shows from either face winding, which spares the plugin from having to
    // produce CCW vertex order.
    private static void DrawNavTriangle(IntPtr pDevice,
        float x1, float y1, float z1,
        float x2, float y2, float z2,
        float x3, float y3, float z3, uint color)
    {
        NavVertex* verts = stackalloc NavVertex[3];
        verts[0].X = x1; verts[0].Y = y1; verts[0].Z = z1; verts[0].Col = color;
        verts[1].X = x2; verts[1].Y = y2; verts[1].Z = z2; verts[1].Col = color;
        verts[2].X = x3; verts[2].Y = y3; verts[2].Z = z3; verts[2].Col = color;
        _drawPrimUP!(pDevice, D3DPT_TRIANGLELIST, 1, (IntPtr)verts, (uint)sizeof(NavVertex));
    }
}

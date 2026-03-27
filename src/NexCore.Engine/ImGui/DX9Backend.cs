// ═══════════════════════════════════════════════════════════════════════════
//  NexCore.Engine — ImGui/DX9Backend.cs
//  Renders ImGui draw data using D3D9 vtable calls.
//  Implements the equivalent of imgui_impl_dx9.cpp in pure C#.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using NexCore.Engine.D3D9;

namespace NexCore.Engine.ImGuiBackend;

internal static unsafe class DX9Backend
{
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    // ─── Native ImDrawData layout (pre-1.90 cimgui, x86) ─────────────
    // Our cimgui.dll uses the old layout where CmdLists is a raw pointer (4 bytes)
    // rather than ImVector (12 bytes). All offsets confirmed by memory dump.
    private static Vector2 GetDisplayPos(IntPtr drawDataNative)
    {
        float x = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(drawDataNative, 20));
        float y = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(drawDataNative, 24));
        return new Vector2(x, y);
    }
    private static Vector2 GetDisplaySize(IntPtr drawDataNative)
    {
        float x = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(drawDataNative, 28));
        float y = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(drawDataNative, 32));
        return new Vector2(x, y);
    }
    private static int GetCmdListsCount(IntPtr drawDataNative)
    {
        return Marshal.ReadInt32(drawDataNative, 4);
    }
    private static IntPtr GetCmdListsArray(IntPtr drawDataNative)
    {
        return Marshal.ReadIntPtr(drawDataNative, 16);
    }

    // ─── D3D9 constants ───────────────────────────────────────────────
    private const uint D3DPT_TRIANGLELIST = 4;
    private const uint D3DFMT_INDEX16 = 101;
    private const uint D3DFMT_A8R8G8B8 = 21;
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
    private const uint D3DRS_ALPHATESTENABLE = 15;
    private const uint D3DRS_CLIPPING = 136;
    private const uint D3DRS_RANGEFOGENABLE = 48;
    private const uint D3DRS_SPECULARENABLE = 29;
    private const uint D3DRS_SRCBLENDALPHA = 207;
    private const uint D3DRS_DESTBLENDALPHA = 208;

    // D3DBLEND
    private const uint D3DBLEND_SRCALPHA = 5;
    private const uint D3DBLEND_INVSRCALPHA = 6;
    private const uint D3DBLEND_ONE = 2;

    // D3DFILL
    private const uint D3DFILL_SOLID = 3;

    // D3DCULL
    private const uint D3DCULL_NONE = 1;

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

    // D3DTEXTUREFILTERTYPE
    private const uint D3DTEXF_LINEAR = 2;

    // FVF: xyz (3 floats) + diffuse (uint) + 1 tex coord (2 floats)
    private const uint D3DFVF_XYZ = 0x002;
    private const uint D3DFVF_DIFFUSE = 0x040;
    private const uint D3DFVF_TEX1 = 0x100;
    private const uint D3DFVF_CUSTOM = D3DFVF_XYZ | D3DFVF_DIFFUSE | D3DFVF_TEX1;

    // D3DBLENDOP
    private const uint D3DBLENDOP_ADD = 1;

    // ─── Vertex structure (D3D9 layout) ───────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct CustomVertex
    {
        public float X, Y, Z;
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
    private static CreateTextureD? _createTexture;
    private static SetStreamSourceD? _setStreamSource;
    private static GetTextureStageStateD? _getTexStageState;
    private static GetSamplerStateD? _getSamplerState;
    private static GetStreamSourceD? _getStreamSource;
    private static SetIndicesD? _setIndices;
    private static GetIndicesD? _getIndices;
    private static IntPtr _fontTexture;
    private static bool _initialized;

    // ─── Public API ───────────────────────────────────────────────────

    public static bool Init(IntPtr pDevice)
    {
        if (_initialized) return true;

        CacheDelegates(pDevice);
        if (!CreateFontTexture(pDevice))
            return false;

        _initialized = true;
        EntryPoint.Log("DX9Backend: Initialized.");
        return true;
    }

    public static void Shutdown()
    {
        if (_fontTexture != IntPtr.Zero)
        {
            var release = GetTexMethod<ReleaseD>(_fontTexture, TextureVTableIndex.Release);
            release(_fontTexture);
            _fontTexture = IntPtr.Zero;
        }
        _initialized = false;
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

        try
        {
            int rtHr = _getRenderTargetSurface!(pDevice, 0, out currentRenderTarget);
            int bbHr = _getBackBuffer!(pDevice, 0, 0, D3DBACKBUFFER_TYPE_MONO, out backBuffer);
            if (rtHr < 0 || bbHr < 0 || currentRenderTarget == IntPtr.Zero || backBuffer == IntPtr.Zero)
                return true;

            return currentRenderTarget == backBuffer;
        }
        finally
        {
            if (currentRenderTarget != IntPtr.Zero)
            {
                IntPtr vtbl = Marshal.ReadIntPtr(currentRenderTarget);
                var rel = Marshal.GetDelegateForFunctionPointer<ReleaseD>(Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));
                rel(currentRenderTarget);
            }
            if (backBuffer != IntPtr.Zero)
            {
                IntPtr vtbl = Marshal.ReadIntPtr(backBuffer);
                var rel = Marshal.GetDelegateForFunctionPointer<ReleaseD>(Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));
                rel(backBuffer);
            }
        }
    }

    private static int _logCount;
    private static void LogOnce(string where, Exception ex)
    {
        if (_logCount++ < 5)
            EntryPoint.Log($"DX9Backend.{where}: {ex.GetType().Name}: {ex.Message}");
    }

    public static void RenderDrawData(ImDrawDataPtr drawData, IntPtr pDevice)
    {
        if (!_initialized) return;
        if (drawData.NativePtr == null) return;

        IntPtr ddNative = (IntPtr)drawData.NativePtr;
        int cmdListsCount = GetCmdListsCount(ddNative);
        Vector2 displaySize = GetDisplaySize(ddNative);
        Vector2 displayPos = GetDisplayPos(ddNative);

        if (cmdListsCount == 0) return;
        if (displaySize.X <= 0 || displaySize.Y <= 0) return;

        IntPtr cmdListsArray = GetCmdListsArray(ddNative);
        if (cmdListsArray == IntPtr.Zero) return;

        if (_logCount < 3)
            EntryPoint.Log($"DX9: Rendering {cmdListsCount} lists, display={displaySize.X}x{displaySize.Y}");

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
        _getTexStageState!(pDevice, 0, D3DTSS_TEXCOORDINDEX, out oldTss0TexCoordIdx);
        _getTexStageState!(pDevice, 0, D3DTSS_TEXTURETRANSFORMFLAGS, out oldTss0TexTransFlags);
        _getStreamSource!(pDevice, 0, out oldStreamData, out oldStreamOffset, out oldStreamStride);
        _getIndices!(pDevice, out oldIndexBuffer);

        try
        {
            // Set up render state with correct display size
            if (_logCount < 3) EntryPoint.Log("DX9: step SetupRenderState");
            SetupRenderStateNative(pDevice, displayPos, displaySize);
            if (_logCount < 3) EntryPoint.Log("DX9: step SetupRenderState done");

            // ── Native ImDrawCmd offsets (imgui.h 1.91.6, x86) ──────────
            // +0  ClipRect (4 floats, 16 bytes)
            // +16 TextureId (ptr, 4 bytes)
            // +20 VtxOffset (uint32)
            // +24 IdxOffset (uint32)
            // +28 ElemCount (uint32)
            // +32 UserCallback (ptr, 4 bytes)
            // +36 UserCallbackData (ptr, 4 bytes)
            // Total = 40 bytes. The C# ImDrawCmd wrapper adds UserCallbackDataSize
            // and UserCallbackDataOffset (8 more bytes = 48), but the native cimgui.dll
            // does NOT have those fields. We read at stride 40 to match the native layout.
            const int CmdStride = 40;
            const int ResetRenderStateSentinel = -8; // ImDrawCallback_ResetRenderState

            for (int n = 0; n < cmdListsCount; n++)
            {
                IntPtr listPtr = Marshal.ReadIntPtr(cmdListsArray, n * IntPtr.Size);
                if (listPtr == IntPtr.Zero) continue;

                // Read ImDrawList fields directly from native memory.
                // ImDrawList starts with three ImVectors (each 12 bytes: Size/Capacity/Data).
                //   [0]  CmdBuffer  — ImVector<ImDrawCmd>
                //   [12] IdxBuffer  — ImVector<ImDrawIdx>
                //   [24] VtxBuffer  — ImVector<ImDrawVert>
                int cmdCount = Marshal.ReadInt32(listPtr, 0);   // CmdBuffer.Size
                IntPtr cmdData = Marshal.ReadIntPtr(listPtr, 8);  // CmdBuffer.Data
                int idxCount = Marshal.ReadInt32(listPtr, 12);  // IdxBuffer.Size
                IntPtr idxBase = Marshal.ReadIntPtr(listPtr, 20); // IdxBuffer.Data
                int vtxCount = Marshal.ReadInt32(listPtr, 24);  // VtxBuffer.Size
                IntPtr vtxBase = Marshal.ReadIntPtr(listPtr, 32); // VtxBuffer.Data

                if (_logCount < 3)
                    EntryPoint.Log($"DX9: list[{n}] vtx={vtxCount} idx={idxCount} cmds={cmdCount}");
                if (vtxCount == 0 || cmdCount == 0) continue;

                // One-time stride diagnostic — log what each cmd stride gives for cmd[1]
                if (_logCount == 0 && n == 0 && cmdCount >= 2)
                {
                    EntryPoint.Log($"DX9: C# ImDrawCmd size = {Unsafe.SizeOf<ImDrawCmd>()}");
                    for (int ts = 32; ts <= 64; ts += 4)
                    {
                        IntPtr tp = cmdData + ts;
                        uint te = (uint)Marshal.ReadInt32(tp, 28);
                        float tx = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(tp, 0));
                        float tz = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(tp, 8));
                        uint tcb = (uint)Marshal.ReadInt32(tp, 32);
                        EntryPoint.Log($"DX9: stride={ts}: elems={te} clipX={tx} clipZ={tz} cb=0x{tcb:X8}");
                    }
                }

                // One-time vertex diagnostic — verify ImDrawVert layout
                if (_logCount == 0 && n == 0)
                {
                    EntryPoint.Log($"DX9: C# ImDrawVert size = {Unsafe.SizeOf<ImDrawVert>()}");
                    // Read first 3 vertices as raw bytes to verify layout
                    for (int vi = 0; vi < Math.Min(3, vtxCount); vi++)
                    {
                        IntPtr vp = vtxBase + vi * Unsafe.SizeOf<ImDrawVert>();
                        float px = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(vp, 0));
                        float py = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(vp, 4));
                        float ux = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(vp, 8));
                        float uy = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(vp, 12));
                        uint col = (uint)Marshal.ReadInt32(vp, 16);
                        EntryPoint.Log($"DX9: vtx[{vi}] pos=({px},{py}) uv=({ux},{uy}) col=0x{col:X8}");
                    }
                }

                CustomVertex* vtxBuf = (CustomVertex*)NativeMemory.Alloc((nuint)(vtxCount * sizeof(CustomVertex)));
                ImDrawVert* srcVtx = (ImDrawVert*)vtxBase;

                for (int i = 0; i < vtxCount; i++)
                {
                    vtxBuf[i].X = srcVtx[i].pos.X;
                    vtxBuf[i].Y = srcVtx[i].pos.Y;
                    vtxBuf[i].Z = 0f;
                    uint c = srcVtx[i].col;
                    vtxBuf[i].Col = (c & 0xFF00FF00) | ((c & 0x00FF0000) >> 16) | ((c & 0x000000FF) << 16);
                    vtxBuf[i].U = srcVtx[i].uv.X;
                    vtxBuf[i].V = srcVtx[i].uv.Y;
                }

                if (_logCount < 3) EntryPoint.Log($"DX9: list[{n}] vertex copy done");

                for (int cmdI = 0; cmdI < cmdCount; cmdI++)
                {
                    IntPtr cmdPtr = cmdData + cmdI * CmdStride;

                    // Read fields at known native offsets
                    float clipX = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(cmdPtr, 0));
                    float clipY = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(cmdPtr, 4));
                    float clipZ = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(cmdPtr, 8));
                    float clipW = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(cmdPtr, 12));
                    IntPtr textureId = Marshal.ReadIntPtr(cmdPtr, 16);
                    uint nativeIdxOff = (uint)Marshal.ReadInt32(cmdPtr, 24);
                    uint elemCount = (uint)Marshal.ReadInt32(cmdPtr, 28);
                    IntPtr userCallback = Marshal.ReadIntPtr(cmdPtr, 32);

                    if (userCallback != IntPtr.Zero)
                    {
                        if (_logCount < 3)
                            EntryPoint.Log($"DX9: cmd[{cmdI}] CALLBACK elems={elemCount} cb=0x{userCallback:X}");
                        if ((int)userCallback == ResetRenderStateSentinel)
                            SetupRenderStateNative(pDevice, displayPos, displaySize);
                        // Non-sentinel callbacks: skip (no draw data, ElemCount expected 0)
                        continue;
                    }

                    if (elemCount == 0)
                    {
                        if (_logCount < 3)
                            EntryPoint.Log($"DX9: cmd[{cmdI}] ZERO-ELEM skip tex=0x{textureId:X}");
                        continue;
                    }

                    if (_logCount < 3)
                        EntryPoint.Log($"DX9: cmd[{cmdI}] tex=0x{textureId:X} elems={elemCount} clip={clipX},{clipY},{clipZ},{clipW}");

                    RECT sr;
                    sr.Left = (int)(clipX - displayPos.X);
                    sr.Top = (int)(clipY - displayPos.Y);
                    sr.Right = (int)(clipZ - displayPos.X);
                    sr.Bottom = (int)(clipW - displayPos.Y);
                    _setScissorRect!(pDevice, &sr);
                    _setTexture!(pDevice, 0, textureId);

                    ushort* pIdxData = (ushort*)idxBase + nativeIdxOff;
                    if (_logCount < 3)
                        EntryPoint.Log($"DX9: cmd[{cmdI}] DrawIndexedPrimUP primCount={elemCount / 3}");
                    _drawIndexedPrimUP!(pDevice,
                        D3DPT_TRIANGLELIST, 0, (uint)vtxCount,
                        elemCount / 3,
                        (IntPtr)pIdxData, D3DFMT_INDEX16,
                        (IntPtr)vtxBuf, (uint)sizeof(CustomVertex));
                    if (_logCount < 3) EntryPoint.Log($"DX9: cmd[{cmdI}] draw done");
                }

                NativeMemory.Free(vtxBuf);
            }
        }
        finally
        {
            _logCount++;
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
            _setTexStageState!(pDevice, 0, D3DTSS_TEXCOORDINDEX, oldTss0TexCoordIdx);
            _setTexStageState!(pDevice, 0, D3DTSS_TEXTURETRANSFORMFLAGS, oldTss0TexTransFlags);
            _setStreamSource!(pDevice, 0, oldStreamData, oldStreamOffset, oldStreamStride);
            _setIndices!(pDevice, oldIndexBuffer);

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

        // Orthographic projection — D3D9 pixel centers are at +0.5 (not +0.0 like D3D10+)
        float L = displayPos.X + 0.5f;
        float R = displayPos.X + displaySize.X + 0.5f;
        float T = displayPos.Y + 0.5f;
        float B = displayPos.Y + displaySize.Y + 0.5f;

        D3DMATRIX identity;
        identity.M11 = 1; identity.M12 = 0; identity.M13 = 0; identity.M14 = 0;
        identity.M21 = 0; identity.M22 = 1; identity.M23 = 0; identity.M24 = 0;
        identity.M31 = 0; identity.M32 = 0; identity.M33 = 1; identity.M34 = 0;
        identity.M41 = 0; identity.M42 = 0; identity.M43 = 0; identity.M44 = 1;

        D3DMATRIX projection;
        projection.M11 = 2f / (R - L); projection.M12 = 0f; projection.M13 = 0f; projection.M14 = 0f;
        projection.M21 = 0f; projection.M22 = 2f / (T - B); projection.M23 = 0f; projection.M24 = 0f;
        projection.M31 = 0f; projection.M32 = 0f; projection.M33 = 0.5f; projection.M34 = 0f;
        projection.M41 = (L + R) / (L - R); projection.M42 = (T + B) / (B - T); projection.M43 = 0.5f; projection.M44 = 1f;

        _setTransform!(dev, D3DTS_WORLD, &identity);
        _setTransform!(dev, D3DTS_VIEW, &identity);
        _setTransform!(dev, D3DTS_PROJECTION, &projection);
    }

    private static bool CreateFontTexture(IntPtr pDevice)
    {
        ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();

        // Get font atlas pixel data
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int bytesPerPixel);

        EntryPoint.Log($"DX9Backend: Creating font texture {width}x{height} bpp={bytesPerPixel}");

        // Dump first 8 pixels of atlas source data for diagnostic
        byte* pix = (byte*)pixels;
        EntryPoint.Log($"DX9: Atlas row0 px0-3: " +
            $"[{pix[0]:X2}{pix[1]:X2}{pix[2]:X2}{pix[3]:X2}] " +
            $"[{pix[4]:X2}{pix[5]:X2}{pix[6]:X2}{pix[7]:X2}] " +
            $"[{pix[8]:X2}{pix[9]:X2}{pix[10]:X2}{pix[11]:X2}] " +
            $"[{pix[12]:X2}{pix[13]:X2}{pix[14]:X2}{pix[15]:X2}]");
        // Also check a pixel further in (where glyphs likely are, around x=66 which is near TexUvWhitePixel)
        int off66 = 66 * 4;
        EntryPoint.Log($"DX9: Atlas row0 px66-69: " +
            $"[{pix[off66]:X2}{pix[off66 + 1]:X2}{pix[off66 + 2]:X2}{pix[off66 + 3]:X2}] " +
            $"[{pix[off66 + 4]:X2}{pix[off66 + 5]:X2}{pix[off66 + 6]:X2}{pix[off66 + 7]:X2}] " +
            $"[{pix[off66 + 8]:X2}{pix[off66 + 9]:X2}{pix[off66 + 10]:X2}{pix[off66 + 11]:X2}] " +
            $"[{pix[off66 + 12]:X2}{pix[off66 + 13]:X2}{pix[off66 + 14]:X2}{pix[off66 + 15]:X2}]");
        // Check row 10 (should have glyph data for default font)
        int off_r10 = 10 * width * 4;
        EntryPoint.Log($"DX9: Atlas row10 px0-3: " +
            $"[{pix[off_r10]:X2}{pix[off_r10 + 1]:X2}{pix[off_r10 + 2]:X2}{pix[off_r10 + 3]:X2}] " +
            $"[{pix[off_r10 + 4]:X2}{pix[off_r10 + 5]:X2}{pix[off_r10 + 6]:X2}{pix[off_r10 + 7]:X2}] " +
            $"[{pix[off_r10 + 8]:X2}{pix[off_r10 + 9]:X2}{pix[off_r10 + 10]:X2}{pix[off_r10 + 11]:X2}] " +
            $"[{pix[off_r10 + 12]:X2}{pix[off_r10 + 13]:X2}{pix[off_r10 + 14]:X2}{pix[off_r10 + 15]:X2}]");

        // Create D3D9 texture
        int hr = _createTexture!(pDevice, (uint)width, (uint)height, 1, 0,
            D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, out _fontTexture, IntPtr.Zero);

        if (hr < 0 || _fontTexture == IntPtr.Zero)
        {
            EntryPoint.Log($"DX9Backend: CreateTexture failed, HR=0x{hr:X8}");
            return false;
        }

        // Lock texture and copy pixel data
        var lockRect = GetTexMethod<TexLockRectD>(_fontTexture, TextureVTableIndex.LockRect);
        var unlockRect = GetTexMethod<TexUnlockRectD>(_fontTexture, TextureVTableIndex.UnlockRect);

        D3DLOCKED_RECT locked;
        hr = lockRect(_fontTexture, 0, &locked, IntPtr.Zero, 0);
        if (hr < 0)
        {
            EntryPoint.Log($"DX9Backend: LockRect failed, HR=0x{hr:X8}");
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

        // Verify TexID was written — if zero, cimgui/ImGui.NET layout still mismatched
        nint texId = io.Fonts.TexID;
        EntryPoint.Log($"DX9Backend: TexID after SetTexID = 0x{texId:X}");
        unsafe
        {
            byte* ioPtr = (byte*)ImGuiNET.ImGui.GetIO().NativePtr;
            IntPtr fontsPtr = *(IntPtr*)(ioPtr + 36);
            EntryPoint.Log($"DX9Backend: ImFontAtlas* = 0x{fontsPtr:X}");
            *(IntPtr*)(fontsPtr + 4) = _fontTexture;
            EntryPoint.Log($"DX9Backend: TexID after native write = 0x{*(IntPtr*)(fontsPtr + 4):X}");
        }
        if (texId == 0)
        {
            EntryPoint.Log("DX9Backend: TexID is zero — managed wrapper broken, writing native.");
            // ImGuiIO x86 layout: Fonts* @ +36 / ImFontAtlas: TexID @ +4
            unsafe
            {
                byte* ioPtr = (byte*)ImGuiNET.ImGui.GetIO().NativePtr;
                IntPtr fontsPtr = *(IntPtr*)(ioPtr + 36);
                EntryPoint.Log($"DX9Backend: ImFontAtlas* = 0x{fontsPtr:X}");
                *(IntPtr*)(fontsPtr + 4) = _fontTexture;
                EntryPoint.Log($"DX9Backend: TexID after native write = 0x{*(IntPtr*)(fontsPtr + 4):X}");
            }
        }

        EntryPoint.Log("DX9Backend: Font texture created.");
        return true;
    }

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
}

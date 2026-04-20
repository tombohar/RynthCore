// ═══════════════════════════════════════════════════════════════════════════
//  RynthCore.Engine — ImGui/ViewportRendererBackend.cs
//  Per-viewport D3D9 renderer backend for ImGui multi-viewport.
//  Installs Renderer_* callbacks on ImGuiPlatformIO: for each viewport we
//  create an additional swap chain on the game's device, render the viewport's
//  ImDrawData into that swap chain's back buffer, and Present it so the
//  viewport's OS window displays the ImGui content.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ImGuiNET;
using RynthCore.Engine.D3D9;

namespace RynthCore.Engine.ImGuiBackend;

internal static unsafe class ViewportRendererBackend
{
    // ─── D3D9 constants ───────────────────────────────────────────────
    private const uint D3DFMT_UNKNOWN        = 0;
    private const uint D3DFMT_A8R8G8B8       = 21;
    private const uint D3DFMT_X8R8G8B8       = 22;
    private const uint D3DSWAPEFFECT_DISCARD = 1;
    private const uint D3DBACKBUFFER_TYPE_MONO = 0;
    private const uint D3DCLEAR_TARGET       = 0x00000001;
    private const uint D3DPRESENT_INTERVAL_IMMEDIATE = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DPRESENT_PARAMETERS
    {
        public uint BackBufferWidth;
        public uint BackBufferHeight;
        public uint BackBufferFormat;
        public uint BackBufferCount;
        public uint MultiSampleType;
        public uint MultiSampleQuality;
        public uint SwapEffect;
        public IntPtr hDeviceWindow;
        public int Windowed;                // BOOL
        public int EnableAutoDepthStencil;  // BOOL
        public uint AutoDepthStencilFormat;
        public uint Flags;
        public uint FullScreen_RefreshRateInHz;
        public uint PresentationInterval;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    // ─── Device method delegates ──────────────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateAdditionalSwapChainD(IntPtr pDevice, D3DPRESENT_PARAMETERS* pp, out IntPtr ppSwapChain);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetRenderTargetD(IntPtr pDevice, uint rtIndex, IntPtr pSurface);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetRenderTargetD(IntPtr pDevice, uint rtIndex, out IntPtr ppSurface);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ClearD(IntPtr pDevice, uint count, RECT* pRects, uint flags, uint color, float z, uint stencil);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseD(IntPtr pObj);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SwapChainGetBackBufferD(IntPtr pSwapChain, uint backBufIdx, uint type, out IntPtr ppBackBuffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SwapChainPresentD(IntPtr pSwapChain, RECT* src, RECT* dest, IntPtr hDestWnd, IntPtr dirtyRgn, uint flags);

    // ─── Callback delegates (must match imgui types) ──────────────────
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ViewportCallbackD(ImGuiViewport* vp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ViewportSizeCallbackD(ImGuiViewport* vp, System.Numerics.Vector2 size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ViewportRenderD(ImGuiViewport* vp, IntPtr renderArg);

    // ─── State ────────────────────────────────────────────────────────
    private sealed class ViewportGpuState
    {
        public IntPtr SwapChain;
        public int Width;
        public int Height;
    }

    private static IntPtr _device;
    private static CreateAdditionalSwapChainD? _createAdditionalSwapChain;
    private static SetRenderTargetD? _setRenderTarget;
    private static GetRenderTargetD? _getRenderTarget;
    private static ClearD? _clear;
    private static readonly Dictionary<IntPtr, ViewportGpuState> _viewports = new();
    private static readonly List<Delegate> _delegateRoots = new();
    private static bool _initialized;

    // Diagnostic one-shot logging counters — limited to a few calls so log doesn't explode.
    private static int _logCreate;
    private static int _logRender;
    private static int _logSwap;
    private const int MaxLog = 3;

    public static bool Init(IntPtr pDevice)
    {
        if (_initialized) return true;
        if (pDevice == IntPtr.Zero)
        {
            RynthLog.Info("ViewportRenderer: Init called with null device.");
            return false;
        }

        _device = pDevice;

        IntPtr vtable = Marshal.ReadIntPtr(pDevice);
        _createAdditionalSwapChain = GetMethod<CreateAdditionalSwapChainD>(vtable, DeviceVTableIndex.CreateAdditionalSwapChain);
        _setRenderTarget = GetMethod<SetRenderTargetD>(vtable, DeviceVTableIndex.SetRenderTarget);
        _getRenderTarget = GetMethod<GetRenderTargetD>(vtable, DeviceVTableIndex.GetRenderTarget);
        _clear = GetMethod<ClearD>(vtable, DeviceVTableIndex.Clear);

        InstallCallbacks();

        ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasViewports;

        _initialized = true;
        RynthLog.Info("ViewportRenderer: initialized.");
        return true;
    }

    public static void Shutdown()
    {
        foreach (var kv in _viewports)
        {
            if (kv.Value.SwapChain != IntPtr.Zero)
                ReleaseCom(kv.Value.SwapChain);
        }
        _viewports.Clear();
        _delegateRoots.Clear();
        _initialized = false;
    }

    private static void InstallCallbacks()
    {
        ImGuiPlatformIOPtr pio = ImGuiNET.ImGui.GetPlatformIO();
        IntPtr pioNative = (IntPtr)pio.NativePtr;

        ViewportCallbackD createFn = RendererCreateWindow;
        ViewportCallbackD destroyFn = RendererDestroyWindow;
        ViewportSizeCallbackD setSizeFn = RendererSetWindowSize;
        ViewportRenderD renderFn = RendererRenderWindow;
        ViewportRenderD swapFn = RendererSwapBuffers;

        _delegateRoots.Add(createFn);
        _delegateRoots.Add(destroyFn);
        _delegateRoots.Add(setSizeFn);
        _delegateRoots.Add(renderFn);
        _delegateRoots.Add(swapFn);

        IntPtr createPtr  = Marshal.GetFunctionPointerForDelegate(createFn);
        IntPtr destroyPtr = Marshal.GetFunctionPointerForDelegate(destroyFn);
        IntPtr setSizePtr = Marshal.GetFunctionPointerForDelegate(setSizeFn);
        IntPtr renderPtr  = Marshal.GetFunctionPointerForDelegate(renderFn);
        IntPtr swapPtr    = Marshal.GetFunctionPointerForDelegate(swapFn);

        Marshal.WriteIntPtr(pioNative, ViewportOffsets.RendererCreateWindow,  createPtr);
        Marshal.WriteIntPtr(pioNative, ViewportOffsets.RendererDestroyWindow, destroyPtr);
        Marshal.WriteIntPtr(pioNative, ViewportOffsets.RendererSetWindowSize, setSizePtr);
        Marshal.WriteIntPtr(pioNative, ViewportOffsets.RendererRenderWindow,  renderPtr);
        Marshal.WriteIntPtr(pioNative, ViewportOffsets.RendererSwapBuffers,   swapPtr);

        IntPtr verify = Marshal.ReadIntPtr(pioNative, ViewportOffsets.RendererRenderWindow);
        RynthLog.Info($"ViewportRenderer: installed handlers (RenderWindow readback=0x{verify.ToInt64():X8}, expected=0x{renderPtr.ToInt64():X8})");
    }

    // ─── Callback implementations ─────────────────────────────────────
    private static void RendererCreateWindow(ImGuiViewport* vp)
    {
        IntPtr key = (IntPtr)vp;
        if (_viewports.ContainsKey(key)) return;

        var state = new ViewportGpuState();
        _viewports[key] = state;

        int w = Math.Max(1, (int)vp->Size.X);
        int h = Math.Max(1, (int)vp->Size.Y);
        CreateSwapChainForViewport(vp, state, w, h);

        if (_logCreate++ < MaxLog)
        {
            IntPtr hwnd = (IntPtr)vp->PlatformHandleRaw;
            if (hwnd == IntPtr.Zero) hwnd = (IntPtr)vp->PlatformHandle;
            RynthLog.Info($"ViewportRenderer: CreateWindow vp=0x{key.ToInt64():X8} hwnd=0x{hwnd.ToInt64():X8} size={w}x{h} swapChain=0x{state.SwapChain.ToInt64():X8}");
        }
    }

    private static void RendererDestroyWindow(ImGuiViewport* vp)
    {
        IntPtr key = (IntPtr)vp;
        if (!_viewports.TryGetValue(key, out var state)) return;
        if (state.SwapChain != IntPtr.Zero)
        {
            ReleaseCom(state.SwapChain);
            state.SwapChain = IntPtr.Zero;
        }
        _viewports.Remove(key);
    }

    private static void RendererSetWindowSize(ImGuiViewport* vp, System.Numerics.Vector2 size)
    {
        IntPtr key = (IntPtr)vp;
        if (!_viewports.TryGetValue(key, out var state)) return;

        int w = Math.Max(1, (int)size.X);
        int h = Math.Max(1, (int)size.Y);
        if (w == state.Width && h == state.Height) return;

        if (state.SwapChain != IntPtr.Zero)
        {
            ReleaseCom(state.SwapChain);
            state.SwapChain = IntPtr.Zero;
        }
        CreateSwapChainForViewport(vp, state, w, h);
    }

    private static void RendererRenderWindow(ImGuiViewport* vp, IntPtr renderArg)
    {
        IntPtr key = (IntPtr)vp;
        bool diag = _logRender < MaxLog;
        if (!_viewports.TryGetValue(key, out var state))
        {
            if (diag) { RynthLog.Info($"ViewportRenderer: RenderWindow vp=0x{key.ToInt64():X8} NO STATE"); _logRender++; }
            return;
        }
        if (state.SwapChain == IntPtr.Zero)
        {
            if (diag) { RynthLog.Info($"ViewportRenderer: RenderWindow vp=0x{key.ToInt64():X8} NO SWAPCHAIN"); _logRender++; }
            return;
        }
        if (_setRenderTarget == null || _getRenderTarget == null || _clear == null)
        {
            if (diag) { RynthLog.Info("ViewportRenderer: RenderWindow device methods null"); _logRender++; }
            return;
        }

        // Get the swap chain's back-buffer surface
        var getBB = GetMethodFromObj<SwapChainGetBackBufferD>(state.SwapChain, SwapChain9VTableIndex.GetBackBuffer);
        int hrBB = getBB(state.SwapChain, 0, D3DBACKBUFFER_TYPE_MONO, out IntPtr backBuffer);
        if (hrBB < 0 || backBuffer == IntPtr.Zero)
        {
            if (diag) { RynthLog.Info($"ViewportRenderer: GetBackBuffer failed hr=0x{hrBB:X8}"); _logRender++; }
            return;
        }

        IntPtr oldRenderTarget = IntPtr.Zero;
        int cmdCount = 0;
        float ddW = 0, ddH = 0, ddX = 0, ddY = 0;
        try
        {
            _getRenderTarget(_device, 0, out oldRenderTarget);
            int hrSRT = _setRenderTarget(_device, 0, backBuffer);

            // Clear unless the ImGui viewport asked us not to.
            uint flags = unchecked((uint)vp->Flags);
            if ((flags & (uint)ImGuiViewportFlags.NoRendererClear) == 0)
            {
                _clear(_device, 0, null, D3DCLEAR_TARGET, 0x00000000u, 1.0f, 0);
            }

            // Render the viewport's own draw data through the main DX9 backend,
            // which already handles full state save/restore.
            ImDrawDataPtr draw = new ImDrawDataPtr((ImDrawData*)vp->DrawData);
            if ((IntPtr)draw.NativePtr != IntPtr.Zero)
            {
                IntPtr dd = (IntPtr)draw.NativePtr;
                cmdCount = Marshal.ReadInt32(dd, 4);
                ddX = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(dd, 20));
                ddY = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(dd, 24));
                ddW = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(dd, 28));
                ddH = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(dd, 32));
                DX9Backend.RenderDrawData(draw, _device);
            }

            if (diag)
            {
                RynthLog.Info($"ViewportRenderer: RenderWindow vp=0x{key.ToInt64():X8} setRT=0x{hrSRT:X8} bb=0x{backBuffer.ToInt64():X8} cmds={cmdCount} ddPos=({ddX},{ddY}) ddSize=({ddW},{ddH})");
                _logRender++;
            }
        }
        finally
        {
            if (oldRenderTarget != IntPtr.Zero)
            {
                _setRenderTarget(_device, 0, oldRenderTarget);
                ReleaseCom(oldRenderTarget);
            }
            ReleaseCom(backBuffer);
        }
    }

    private static void RendererSwapBuffers(ImGuiViewport* vp, IntPtr renderArg)
    {
        IntPtr key = (IntPtr)vp;
        if (!_viewports.TryGetValue(key, out var state)) return;
        if (state.SwapChain == IntPtr.Zero) return;

        var present = GetMethodFromObj<SwapChainPresentD>(state.SwapChain, SwapChain9VTableIndex.Present);
        int hr = present(state.SwapChain, null, null, IntPtr.Zero, IntPtr.Zero, 0);
        if (_logSwap++ < MaxLog)
            RynthLog.Info($"ViewportRenderer: SwapBuffers vp=0x{key.ToInt64():X8} Present hr=0x{hr:X8}");
    }

    // ─── Helpers ──────────────────────────────────────────────────────
    private static void CreateSwapChainForViewport(ImGuiViewport* vp, ViewportGpuState state, int w, int h)
    {
        if (_createAdditionalSwapChain == null) return;

        IntPtr hwnd = (IntPtr)vp->PlatformHandleRaw;
        if (hwnd == IntPtr.Zero) hwnd = (IntPtr)vp->PlatformHandle;
        if (hwnd == IntPtr.Zero)
        {
            RynthLog.Info("ViewportRenderer: viewport has no HWND — cannot create swap chain.");
            return;
        }

        var pp = new D3DPRESENT_PARAMETERS
        {
            BackBufferWidth  = (uint)w,
            BackBufferHeight = (uint)h,
            BackBufferFormat = D3DFMT_UNKNOWN,    // match device default
            BackBufferCount  = 1,
            SwapEffect       = D3DSWAPEFFECT_DISCARD,
            hDeviceWindow    = hwnd,
            Windowed         = 1,
            // Don't wait for vblank — each popped viewport adds another Present
            // per frame, and stacking vsyncs cuts the effective frame rate.
            PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE,
        };

        int hr = _createAdditionalSwapChain(_device, &pp, out IntPtr swapChain);
        if (hr < 0 || swapChain == IntPtr.Zero)
        {
            RynthLog.Info($"ViewportRenderer: CreateAdditionalSwapChain failed (hr=0x{hr:X8}) for {w}x{h} hwnd=0x{hwnd:X}");
            return;
        }

        state.SwapChain = swapChain;
        state.Width = w;
        state.Height = h;
    }

    private static T GetMethod<T>(IntPtr vtable, int index) where T : Delegate
    {
        IntPtr addr = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    private static T GetMethodFromObj<T>(IntPtr pObj, int index) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(pObj);
        IntPtr addr = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    private static void ReleaseCom(IntPtr pObj)
    {
        if (pObj == IntPtr.Zero) return;
        IntPtr vtable = Marshal.ReadIntPtr(pObj);
        IntPtr addr = Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size); // Release is index 2
        var release = Marshal.GetDelegateForFunctionPointer<ReleaseD>(addr);
        release(pObj);
    }
}

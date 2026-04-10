using System;
using System.Runtime.InteropServices;
using RynthCore.Engine.D3D9;

namespace RynthCore.Engine.UI;

internal sealed unsafe class OverlayD3D9SharedTexturePublisher : IDisposable
{
    private const uint D3D_SDK_VERSION = 32;
    private const uint D3DDEVTYPE_HAL = 1;
    private const uint D3DCREATE_FPU_PRESERVE = 0x00000002;
    private const uint D3DCREATE_MULTITHREADED = 0x00000004;
    private const uint D3DCREATE_SOFTWARE_VERTEXPROCESSING = 0x00000020;
    private const uint D3DFMT_UNKNOWN = 0;
    private const uint D3DFMT_A8R8G8B8 = 21;
    private const uint D3DSWAPEFFECT_DISCARD = 1;
    private const uint D3DPOOL_DEFAULT = 0;
    private const uint D3DUSAGE_DYNAMIC = 0x00000200;

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
        public int Windowed;
        public int EnableAutoDepthStencil;
        public uint AutoDepthStencilFormat;
        public uint Flags;
        public uint FullScreen_RefreshRateInHz;
        public uint PresentationInterval;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DLOCKED_RECT
    {
        public int Pitch;
        public IntPtr pBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDeviceExD(
        IntPtr pD3D9Ex,
        uint adapter,
        uint deviceType,
        IntPtr hFocusWindow,
        uint behaviorFlags,
        IntPtr pPresentationParameters,
        IntPtr pFullscreenDisplayMode,
        out IntPtr ppReturnedDeviceInterface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterLuidD(IntPtr pD3D9Ex, uint adapter, out Luid luid);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTextureD(
        IntPtr device,
        uint width,
        uint height,
        uint levels,
        uint usage,
        uint format,
        uint pool,
        out IntPtr texture,
        IntPtr sharedHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LockRectD(IntPtr texture, uint level, D3DLOCKED_RECT* lockedRect, IntPtr rect, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnlockRectD(IntPtr texture, uint level);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseD(IntPtr pObj);

    [DllImport("d3d9.dll")]
    private static extern int Direct3DCreate9Ex(uint sdkVersion, out IntPtr direct3D9Ex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowExA(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandleA(string? lpModuleName);

    private IntPtr _windowHandle;
    private IntPtr _d3d9Ex;
    private IntPtr _deviceEx;
    private IntPtr _texture;
    private IntPtr _sharedHandle;
    private int _width;
    private int _height;
    private long _adapterLuid;
    private bool _loggedAvailability;

    public bool TryUpload(
        IntPtr pixelData,
        int byteCount,
        int width,
        int height,
        int rowPitch,
        out OverlaySharedTextureDescriptor descriptor)
    {
        descriptor = default;

        if (pixelData == IntPtr.Zero || byteCount <= 0 || width <= 0 || height <= 0 || rowPitch <= 0)
            return false;

        if (!EnsureDevice())
            return false;

        if (!EnsureTexture(width, height))
            return false;

        var lockRect = GetTextureMethod<LockRectD>(_texture, TextureVTableIndex.LockRect);
        var unlockRect = GetTextureMethod<UnlockRectD>(_texture, TextureVTableIndex.UnlockRect);

        D3DLOCKED_RECT locked;
        int hr = lockRect(_texture, 0, &locked, IntPtr.Zero, 0);
        if (hr < 0 || locked.pBits == IntPtr.Zero)
        {
            RynthLog.UI($"OverlayD3D9SharedTexturePublisher: LockRect failed, HRESULT=0x{hr:X8}.");
            return false;
        }

        try
        {
            int bytesPerRow = Math.Min(rowPitch, locked.Pitch);
            for (int row = 0; row < height; row++)
            {
                IntPtr sourceRow = pixelData + (row * rowPitch);
                IntPtr targetRow = locked.pBits + (row * locked.Pitch);
                Buffer.MemoryCopy((void*)sourceRow, (void*)targetRow, bytesPerRow, bytesPerRow);
            }
        }
        finally
        {
            unlockRect(_texture, 0);
        }

        descriptor = new OverlaySharedTextureDescriptor(
            _sharedHandle,
            _adapterLuid,
            width,
            height,
            (int)D3DFMT_A8R8G8B8,
            rowPitch,
            (int)D3DUSAGE_DYNAMIC);
        return true;
    }

    public void Dispose()
    {
        ReleaseTexture();
        ReleaseComObject(ref _deviceEx);
        ReleaseComObject(ref _d3d9Ex);

        if (_windowHandle != IntPtr.Zero)
        {
            DestroyWindow(_windowHandle);
            _windowHandle = IntPtr.Zero;
        }
    }

    private bool EnsureDevice()
    {
        if (_deviceEx != IntPtr.Zero)
            return true;

        _windowHandle = CreateWindowExA(
            0,
            "STATIC",
            "RynthCore_D3D9_SharedTexture",
            0,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandleA(null),
            IntPtr.Zero);

        if (_windowHandle == IntPtr.Zero)
        {
            RynthLog.UI("OverlayD3D9SharedTexturePublisher: Failed to create helper window.");
            return false;
        }

        int hr = Direct3DCreate9Ex(D3D_SDK_VERSION, out _d3d9Ex);
        if (hr < 0 || _d3d9Ex == IntPtr.Zero)
        {
            RynthLog.UI($"OverlayD3D9SharedTexturePublisher: Direct3DCreate9Ex failed, HRESULT=0x{hr:X8}.");
            Dispose();
            return false;
        }

        IntPtr d3d9ExVTable = Marshal.ReadIntPtr(_d3d9Ex);
        var createDeviceEx = Marshal.GetDelegateForFunctionPointer<CreateDeviceExD>(
            Marshal.ReadIntPtr(d3d9ExVTable, 20 * IntPtr.Size));
        var getAdapterLuid = Marshal.GetDelegateForFunctionPointer<GetAdapterLuidD>(
            Marshal.ReadIntPtr(d3d9ExVTable, 21 * IntPtr.Size));

        var presentParameters = new D3DPRESENT_PARAMETERS
        {
            BackBufferWidth = 1,
            BackBufferHeight = 1,
            BackBufferFormat = D3DFMT_UNKNOWN,
            BackBufferCount = 1,
            SwapEffect = D3DSWAPEFFECT_DISCARD,
            hDeviceWindow = _windowHandle,
            Windowed = 1
        };

        IntPtr parametersPtr = Marshal.AllocHGlobal(Marshal.SizeOf<D3DPRESENT_PARAMETERS>());
        try
        {
            Marshal.StructureToPtr(presentParameters, parametersPtr, false);
            hr = createDeviceEx(
                _d3d9Ex,
                0,
                D3DDEVTYPE_HAL,
                _windowHandle,
                D3DCREATE_SOFTWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED | D3DCREATE_FPU_PRESERVE,
                parametersPtr,
                IntPtr.Zero,
                out _deviceEx);
        }
        finally
        {
            Marshal.FreeHGlobal(parametersPtr);
        }

        if (hr < 0 || _deviceEx == IntPtr.Zero)
        {
            RynthLog.UI($"OverlayD3D9SharedTexturePublisher: CreateDeviceEx failed, HRESULT=0x{hr:X8}.");
            Dispose();
            return false;
        }

        if (getAdapterLuid(_d3d9Ex, 0, out Luid luid) >= 0)
        {
            _adapterLuid = ((long)luid.HighPart << 32) | luid.LowPart;
        }

        if (!_loggedAvailability)
        {
            _loggedAvailability = true;
            RynthLog.UI("OverlayD3D9SharedTexturePublisher: D3D9Ex shared-texture upload path is available.");
        }

        return true;
    }

    private bool EnsureTexture(int width, int height)
    {
        if (_texture != IntPtr.Zero && _width == width && _height == height && _sharedHandle != IntPtr.Zero)
            return true;

        ReleaseTexture();

        var createTexture = GetDeviceMethod<CreateTextureD>(_deviceEx, DeviceVTableIndex.CreateTexture);
        IntPtr sharedHandle = IntPtr.Zero;
        int hr = createTexture(
            _deviceEx,
            (uint)width,
            (uint)height,
            1,
            D3DUSAGE_DYNAMIC,
            D3DFMT_A8R8G8B8,
            D3DPOOL_DEFAULT,
            out _texture,
            new IntPtr(&sharedHandle));

        if (hr < 0 || _texture == IntPtr.Zero || sharedHandle == IntPtr.Zero)
        {
            RynthLog.UI(
                $"OverlayD3D9SharedTexturePublisher: CreateTexture failed for {width}x{height}, HRESULT=0x{hr:X8}, handle=0x{sharedHandle:X8}.");
            ReleaseTexture();
            return false;
        }

        _sharedHandle = sharedHandle;
        _width = width;
        _height = height;
        RynthLog.UI(
            $"OverlayD3D9SharedTexturePublisher: Created shared upload texture {width}x{height}, handle=0x{_sharedHandle:X8}.");
        return true;
    }

    private void ReleaseTexture()
    {
        if (_texture == IntPtr.Zero)
            return;

        ReleaseComObject(ref _texture);
        _sharedHandle = IntPtr.Zero;
        _width = 0;
        _height = 0;
    }

    private static void ReleaseComObject(ref IntPtr obj)
    {
        if (obj == IntPtr.Zero)
            return;

        IntPtr vtable = Marshal.ReadIntPtr(obj);
        var release = Marshal.GetDelegateForFunctionPointer<ReleaseD>(
            Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size));
        release(obj);
        obj = IntPtr.Zero;
    }

    private static T GetDeviceMethod<T>(IntPtr device, int index) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(device);
        IntPtr address = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static T GetTextureMethod<T>(IntPtr texture, int index) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(texture);
        IntPtr address = Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }
}

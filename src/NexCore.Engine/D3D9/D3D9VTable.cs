// ═══════════════════════════════════════════════════════════════════════════
//  NexCore.Engine — D3D9/D3D9VTable.cs
//  Creates a throwaway D3D9 device to harvest vtable function pointers.
//  The vtable is shared across all IDirect3DDevice9 instances in the process,
//  so the addresses we read here apply to the game's real device.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Runtime.InteropServices;

namespace NexCore.Engine.D3D9;

/// <summary>
/// IDirect3DDevice9 vtable indices (x86, standard d3d9.h order).
/// </summary>
internal static class DeviceVTableIndex
{
    public const int Release                = 2;
    public const int GetCreationParameters  = 9;
    public const int CreateAdditionalSwapChain = 13;
    public const int Reset                  = 16;
    public const int GetBackBuffer          = 18;
    public const int CreateTexture          = 23;
    public const int BeginScene             = 41;
    public const int EndScene               = 42;
    public const int Clear                  = 43;
    public const int SetTransform           = 44;
    public const int GetTransform           = 45;
    public const int GetRenderTarget        = 38;
    public const int SetViewport            = 47;
    public const int GetViewport            = 48;
    public const int GetTexture             = 64;
    public const int SetRenderState         = 57;
    public const int GetRenderState         = 58;
    public const int SetTexture             = 65;
    public const int GetTextureStageState   = 66;
    public const int SetTextureStageState   = 67;
    public const int GetSamplerState        = 68;
    public const int SetSamplerState        = 69;
    public const int SetScissorRect         = 75;
    public const int GetScissorRect         = 76;
    public const int DrawPrimitiveUP        = 83;
    public const int DrawIndexedPrimitiveUP = 84;
    public const int SetFVF                 = 89;
    public const int GetFVF                 = 90;
    public const int SetVertexShader        = 92;
    public const int GetVertexShader        = 93;
    public const int SetStreamSource        = 100;
    public const int GetStreamSource        = 101;
    public const int SetIndices             = 104;
    public const int GetIndices             = 105;
    public const int SetPixelShader         = 107;
    public const int GetPixelShader         = 108;
    public const int CreateStateBlock       = 60;
}

/// <summary>
/// IDirect3DStateBlock9 vtable indices.
/// </summary>
internal static class StateBlockVTableIndex
{
    public const int Release    = 2;
    public const int Capture    = 4;
    public const int Apply      = 5;
}

/// <summary>
/// IDirect3DTexture9 vtable indices.
/// </summary>
internal static class TextureVTableIndex
{
    public const int Release    = 2;
    public const int LockRect   = 19;
    public const int UnlockRect = 20;
}

/// <summary>
/// IDirect3D9 vtable indices.
/// </summary>
internal static class D3D9VTableIndex
{
    public const int Release       = 2;
    public const int CreateDevice  = 16;
}

/// <summary>
/// Reads the IDirect3DDevice9 vtable by creating (and immediately releasing)
/// a temporary device + hidden window.
/// </summary>
internal static unsafe class D3D9VTable
{
    // ─── D3D9 constants ───────────────────────────────────────────────
    private const uint D3D_SDK_VERSION            = 32;
    private const uint D3DDEVTYPE_HAL             = 1;
    private const uint D3DCREATE_SOFTWARE_VERTEXPROCESSING = 0x00000020;
    private const uint D3DFMT_UNKNOWN             = 0;
    private const uint D3DSWAPEFFECT_DISCARD      = 1;

    // ─── Win32 ────────────────────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowExA(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandleA(string? lpModuleName);

    // ─── D3D9 ─────────────────────────────────────────────────────────
    [DllImport("d3d9.dll")]
    private static extern IntPtr Direct3DCreate9(uint SDKVersion);

    // ─── COM Release delegate ─────────────────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr pThis);

    // ─── IDirect3D9::CreateDevice delegate ────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDeviceDelegate(
        IntPtr pD3D9,
        uint Adapter,
        uint DeviceType,
        IntPtr hFocusWindow,
        uint BehaviorFlags,
        IntPtr pPresentationParameters,
        out IntPtr ppDevice);

    // ─── D3DPRESENT_PARAMETERS (x86 layout) ───────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DPRESENT_PARAMETERS
    {
        public uint BackBufferWidth;
        public uint BackBufferHeight;
        public uint BackBufferFormat;       // D3DFORMAT
        public uint BackBufferCount;
        public uint MultiSampleType;        // D3DMULTISAMPLE_TYPE
        public uint MultiSampleQuality;
        public uint SwapEffect;             // D3DSWAPEFFECT
        public IntPtr hDeviceWindow;
        public int Windowed;                // BOOL
        public int EnableAutoDepthStencil;  // BOOL
        public uint AutoDepthStencilFormat; // D3DFORMAT
        public uint Flags;
        public uint FullScreen_RefreshRateInHz;
        public uint PresentationInterval;
    }

    /// <summary>
    /// Creates a temp D3D9 device and returns a copy of the device vtable
    /// (array of function pointers). Returns null on failure.
    /// </summary>
    public static IntPtr[]? GetDeviceVTable(int numEntries = 119)
    {
        IntPtr hWnd = IntPtr.Zero;
        IntPtr pD3D9 = IntPtr.Zero;
        IntPtr pDevice = IntPtr.Zero;

        try
        {
            // Create a hidden window
            hWnd = CreateWindowExA(
                0, "STATIC", "NexCore_D3D9_Temp", 0,
                0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero,
                GetModuleHandleA(null), IntPtr.Zero);

            if (hWnd == IntPtr.Zero)
            {
                EntryPoint.Log("D3D9VTable: Failed to create temp window.");
                return null;
            }

            // Create IDirect3D9
            pD3D9 = Direct3DCreate9(D3D_SDK_VERSION);
            if (pD3D9 == IntPtr.Zero)
            {
                EntryPoint.Log("D3D9VTable: Direct3DCreate9 failed.");
                return null;
            }

            // Set up present parameters
            var pp = new D3DPRESENT_PARAMETERS
            {
                BackBufferWidth      = 1,
                BackBufferHeight     = 1,
                BackBufferFormat     = D3DFMT_UNKNOWN,
                BackBufferCount      = 1,
                SwapEffect           = D3DSWAPEFFECT_DISCARD,
                hDeviceWindow        = hWnd,
                Windowed             = 1, // TRUE
            };

            // Call IDirect3D9::CreateDevice via vtable
            IntPtr d3d9VTable = Marshal.ReadIntPtr(pD3D9); // pD3D9->lpVtbl
            IntPtr createDeviceAddr = Marshal.ReadIntPtr(d3d9VTable, D3D9VTableIndex.CreateDevice * IntPtr.Size);
            var createDevice = Marshal.GetDelegateForFunctionPointer<CreateDeviceDelegate>(createDeviceAddr);

            IntPtr ppAlloc = Marshal.AllocHGlobal(Marshal.SizeOf<D3DPRESENT_PARAMETERS>());
            Marshal.StructureToPtr(pp, ppAlloc, false);

            int hr = createDevice(
                pD3D9,
                0,                                      // D3DADAPTER_DEFAULT
                D3DDEVTYPE_HAL,
                hWnd,
                D3DCREATE_SOFTWARE_VERTEXPROCESSING,
                ppAlloc,
                out pDevice);

            Marshal.FreeHGlobal(ppAlloc);

            if (hr < 0 || pDevice == IntPtr.Zero)
            {
                EntryPoint.Log($"D3D9VTable: CreateDevice failed, HRESULT=0x{hr:X8}");
                return null;
            }

            // Read the device vtable
            IntPtr deviceVTable = Marshal.ReadIntPtr(pDevice); // pDevice->lpVtbl
            var vtable = new IntPtr[numEntries];
            for (int i = 0; i < numEntries; i++)
                vtable[i] = Marshal.ReadIntPtr(deviceVTable, i * IntPtr.Size);

            EntryPoint.Log($"D3D9VTable: Captured {numEntries} entries. " +
                           $"EndScene=0x{vtable[DeviceVTableIndex.EndScene]:X8}");

            return vtable;
        }
        catch (Exception ex)
        {
            EntryPoint.Log($"D3D9VTable: Exception: {ex.Message}");
            return null;
        }
        finally
        {
            // Release the temp device and D3D9 object
            if (pDevice != IntPtr.Zero)
            {
                IntPtr vtbl = Marshal.ReadIntPtr(pDevice);
                IntPtr releaseAddr = Marshal.ReadIntPtr(vtbl, DeviceVTableIndex.Release * IntPtr.Size);
                var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(releaseAddr);
                release(pDevice);
            }

            if (pD3D9 != IntPtr.Zero)
            {
                IntPtr vtbl = Marshal.ReadIntPtr(pD3D9);
                IntPtr releaseAddr = Marshal.ReadIntPtr(vtbl, D3D9VTableIndex.Release * IntPtr.Size);
                var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(releaseAddr);
                release(pD3D9);
            }

            if (hWnd != IntPtr.Zero)
                DestroyWindow(hWnd);
        }
    }
}

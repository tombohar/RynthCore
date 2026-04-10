// ═══════════════════════════════════════════════════════════════════════════
//  RynthCore.Engine — D3D9/D3D9VTable.cs
//  Creates a throwaway D3D9 device to harvest vtable function pointers.
//  The vtable is shared across all IDirect3DDevice9 instances in the process,
//  so the addresses we read here apply to the game's real device.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Runtime.InteropServices;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.D3D9;

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
    public const int DrawPrimitive           = 81;
    public const int DrawIndexedPrimitive    = 82;
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
/// Also provides <see cref="ScanModuleForDeviceVTable"/> which finds the vtable
/// by scanning d3d9.dll's module image — no device creation required.
/// </summary>
internal static unsafe class D3D9VTable
{
    // ─── D3D9 constants ───────────────────────────────────────────────
    private const uint D3D_SDK_VERSION            = 32;
    private const uint D3DDEVTYPE_HAL             = 1;
    private const uint D3DDEVTYPE_NULLREF         = 4;
    private const uint D3DCREATE_SOFTWARE_VERTEXPROCESSING = 0x00000020;
    private const uint D3DCREATE_MULTITHREADED    = 0x00000004;
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
                0, "STATIC", "RynthCore_D3D9_Temp", 0,
                0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero,
                GetModuleHandleA(null), IntPtr.Zero);

            if (hWnd == IntPtr.Zero)
            {
                RynthLog.D3D9("D3D9VTable: Failed to create temp window.");
                return null;
            }

            // Create IDirect3D9
            pD3D9 = Direct3DCreate9(D3D_SDK_VERSION);
            if (pD3D9 == IntPtr.Zero)
            {
                RynthLog.D3D9("D3D9VTable: Direct3DCreate9 failed.");
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

            // Try NULLREF device first — it doesn't touch the GPU, avoiding
            // race conditions with the game's render thread on the real HAL device.
            int hr = createDevice(
                pD3D9,
                0,                                      // D3DADAPTER_DEFAULT
                D3DDEVTYPE_NULLREF,
                hWnd,
                D3DCREATE_SOFTWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED,
                ppAlloc,
                out pDevice);

            if (hr < 0 || pDevice == IntPtr.Zero)
            {
                RynthLog.D3D9($"D3D9VTable: NULLREF device failed (hr=0x{hr:X8}), trying HAL fallback.");
                Marshal.StructureToPtr(pp, ppAlloc, false);
                hr = createDevice(
                    pD3D9,
                    0,
                    D3DDEVTYPE_HAL,
                    hWnd,
                    D3DCREATE_SOFTWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED,
                    ppAlloc,
                    out pDevice);
            }

            Marshal.FreeHGlobal(ppAlloc);

            if (hr < 0 || pDevice == IntPtr.Zero)
            {
                RynthLog.D3D9($"D3D9VTable: CreateDevice failed, HRESULT=0x{hr:X8}");
                return null;
            }

            // Read the device vtable
            IntPtr deviceVTable = Marshal.ReadIntPtr(pDevice); // pDevice->lpVtbl
            var vtable = new IntPtr[numEntries];
            for (int i = 0; i < numEntries; i++)
                vtable[i] = Marshal.ReadIntPtr(deviceVTable, i * IntPtr.Size);

            RynthLog.D3D9($"D3D9VTable: Captured {numEntries} entries. " +
                           $"EndScene=0x{vtable[DeviceVTableIndex.EndScene]:X8}");

            return vtable;
        }
        catch (Exception ex)
        {
            RynthLog.D3D9($"D3D9VTable: Exception: {ex.Message}");
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

    /// <summary>
    /// Scans the loaded d3d9.dll module image for the IDirect3DDevice9 vtable
    /// without creating any D3D9 objects.  This avoids the thread-safety hazard
    /// of creating a throwaway device while the game's real device is rendering.
    /// Returns null if the scan cannot find a valid candidate.
    /// </summary>
    public static IntPtr[]? ScanModuleForDeviceVTable(int numEntries = 119)
    {
        IntPtr hModule = GetModuleHandleA("d3d9.dll");
        if (hModule == IntPtr.Zero)
        {
            RynthLog.D3D9("D3D9VTable: d3d9.dll not loaded — module scan skipped.");
            return null;
        }

        const int MaxReasonableModuleSize = 50 * 1024 * 1024;
        if (!TryReadModuleImageSize(hModule, out int imageSize) || imageSize < 0x2000 || imageSize > MaxReasonableModuleSize)
        {
            RynthLog.D3D9($"D3D9VTable: d3d9.dll PE headers unusable (imageSize=0x{imageSize:X}) — module scan skipped.");
            return null;
        }

        int baseAddr = hModule.ToInt32();
        int vtableByteLen = numEntries * 4; // x86 pointer size

        RynthLog.D3D9($"D3D9VTable: Reading d3d9.dll (0x{baseAddr:X8}, 0x{imageSize:X} bytes)...");

        // Copy module into managed memory page-by-page, skipping any
        // uncommitted pages.  SizeOfImage can exceed committed memory
        // and NativeAOT cannot catch the resulting AccessViolationException.
        byte[] image = new byte[imageSize];
        int readableBytes = 0;
        const int PageSize = 0x1000;
        for (int off = 0; off < imageSize; off += PageSize)
        {
            int chunk = Math.Min(PageSize, imageSize - off);
            IntPtr addr = IntPtr.Add(hModule, off);
            if (SmartBoxLocator.IsMemoryReadable(addr, chunk))
            {
                Marshal.Copy(addr, image, off, chunk);
                readableBytes += chunk;
            }
        }

        if (readableBytes < vtableByteLen + 0x1000)
        {
            RynthLog.D3D9($"D3D9VTable: Only {readableBytes} readable bytes in d3d9.dll — too small for vtable scan.");
            return null;
        }

        RynthLog.D3D9($"D3D9VTable: Scanning d3d9.dll ({readableBytes} readable bytes) for {numEntries}-entry device vtable...");

        // Collect ALL candidate vtables.  d3d9.dll on Windows 11 can contain
        // multiple vtables (inner implementation + outer proxy).  The game's
        // device typically uses the LAST one (the outer proxy layer).
        IntPtr[]? bestCandidate = null;
        int bestOffset = 0;
        int candidateCount = 0;

        for (int off = 0x1000; off <= imageSize - vtableByteLen; off += 4)
        {
            // Fast reject: first 3 entries (QueryInterface, AddRef, Release) must be in-module
            int p0 = BitConverter.ToInt32(image, off);
            if ((uint)(p0 - baseAddr) >= (uint)imageSize) continue;
            int p1 = BitConverter.ToInt32(image, off + 4);
            if ((uint)(p1 - baseAddr) >= (uint)imageSize) continue;
            int p2 = BitConverter.ToInt32(image, off + 8);
            if ((uint)(p2 - baseAddr) >= (uint)imageSize) continue;

            // QI, AddRef, Release must be distinct functions
            if (p0 == p1 || p1 == p2) continue;

            // Count in-module pointers across all entries
            int inModule = 3;
            for (int i = 3; i < numEntries; i++)
            {
                int ptr = BitConverter.ToInt32(image, off + i * 4);
                if ((uint)(ptr - baseAddr) < (uint)imageSize)
                    inModule++;
            }

            // Require ≥95% in-module (113 of 119)
            if (inModule < numEntries * 95 / 100) continue;

            // EndScene (42) and BeginScene (41) must differ
            int esPtr = BitConverter.ToInt32(image, off + DeviceVTableIndex.EndScene * 4);
            int bsPtr = BitConverter.ToInt32(image, off + DeviceVTableIndex.BeginScene * 4);
            if (esPtr == bsPtr) continue;

            // Verify EndScene starts with a hookable function prologue
            int esOff = esPtr - baseAddr;
            if ((uint)esOff >= (uint)(imageSize - 3)) continue;
            if (!IsRecognizedPrologue(image, esOff)) continue;

            candidateCount++;

            // Build candidate array
            var vtable = new IntPtr[numEntries];
            for (int i = 0; i < numEntries; i++)
                vtable[i] = new IntPtr(BitConverter.ToInt32(image, off + i * 4));

            RynthLog.D3D9($"D3D9VTable: Candidate #{candidateCount} at d3d9+0x{off:X}: " +
                        $"EndScene=0x{vtable[DeviceVTableIndex.EndScene]:X8} ({inModule}/{numEntries} in-module)");

            bestCandidate = vtable;
            bestOffset = off;
        }

        if (bestCandidate != null)
        {
            RynthLog.D3D9($"D3D9VTable: Using candidate #{candidateCount} (last of {candidateCount}) at d3d9+0x{bestOffset:X}: " +
                        $"EndScene=0x{bestCandidate[DeviceVTableIndex.EndScene]:X8}");
            return bestCandidate;
        }

        RynthLog.D3D9("D3D9VTable: Module scan found no device vtable candidates.");
        return null;
    }

    private static bool IsRecognizedPrologue(byte[] image, int offset)
    {
        byte b = image[offset];
        if (b == 0x55) return true;                                         // push ebp
        if (b == 0x8B && image[offset + 1] == 0xFF) return true;            // mov edi,edi (hot-patch)
        if (b == 0x83 && image[offset + 1] == 0xEC) return true;            // sub esp, imm8
        return false;
    }

    /// <summary>
    /// Finds the game's existing IDirect3DDevice9 by scanning process heap memory
    /// for a live COM object whose vtable is in d3d9.dll with 119 entries.
    /// Completely read-only — no D3D9 API calls, no thread-safety hazards.
    /// Returns null if no device is found.
    /// </summary>
    public static IntPtr[]? FindExistingDeviceVTable(int numEntries = 119)
    {
        IntPtr d3d9Module = GetModuleHandleA("d3d9.dll");
        if (d3d9Module == IntPtr.Zero)
        {
            RynthLog.D3D9("D3D9VTable: d3d9.dll not loaded — device scan skipped.");
            return null;
        }

        if (!TryReadModuleImageSize(d3d9Module, out int d3d9Size) || d3d9Size < 0x2000)
        {
            RynthLog.D3D9("D3D9VTable: d3d9.dll PE headers unusable — device scan skipped.");
            return null;
        }

        int d3d9Base = d3d9Module.ToInt32();
        int d3d9End = d3d9Base + d3d9Size;
        int vtableByteLen = numEntries * 4;

        // Pre-read d3d9.dll image so vtable validation reads from safe managed memory
        byte[] d3d9Image = new byte[d3d9Size];
        int d3d9Readable = 0;
        const int PageSize = 0x1000;
        for (int off = 0; off < d3d9Size; off += PageSize)
        {
            int chunk = Math.Min(PageSize, d3d9Size - off);
            IntPtr addr = IntPtr.Add(d3d9Module, off);
            if (SmartBoxLocator.IsMemoryReadable(addr, chunk))
            {
                Marshal.Copy(addr, d3d9Image, off, chunk);
                d3d9Readable += chunk;
            }
        }

        if (d3d9Readable < vtableByteLen + PageSize)
        {
            RynthLog.D3D9($"D3D9VTable: Only {d3d9Readable} readable bytes in d3d9.dll — device scan skipped.");
            return null;
        }

        RynthLog.D3D9($"D3D9VTable: Scanning process heap for live D3D9 device (d3d9=0x{d3d9Base:X8}, size=0x{d3d9Size:X})...");

        int regionsScanned = 0;
        long bytesScanned = 0;
        int d3d9Pointers = 0;

        IntPtr scanAddr = new IntPtr(0x10000);

        while (scanAddr.ToInt32() > 0 && scanAddr.ToInt32() < 0x7FFE0000)
        {
            if (!VirtualQueryWrapped(scanAddr, out int regionBase, out int regionSize,
                    out uint state, out uint protect, out uint type))
                break;

            if (regionSize <= 0) break;
            IntPtr nextAddr = new IntPtr(regionBase + regionSize);

            // Only scan committed, readable, private (heap) memory
            bool committed = (state & 0x1000) != 0;   // MEM_COMMIT
            bool readable = protect != 0 && (protect & 0x01) == 0 && (protect & 0x100) == 0; // !NOACCESS, !GUARD
            bool isPrivate = (type & 0x20000) != 0;    // MEM_PRIVATE

            if (!committed || !readable || !isPrivate || regionSize < 128)
            {
                scanAddr = nextAddr;
                continue;
            }

            regionsScanned++;

            // Safe page-by-page copy of this region
            byte[] regionData = new byte[regionSize];
            int regionReadable = 0;
            for (int off = 0; off < regionSize; off += PageSize)
            {
                int chunk = Math.Min(PageSize, regionSize - off);
                IntPtr pageAddr = new IntPtr(regionBase + off);
                if (SmartBoxLocator.IsMemoryReadable(pageAddr, chunk))
                {
                    Marshal.Copy(pageAddr, regionData, off, chunk);
                    regionReadable += chunk;
                }
            }

            bytesScanned += regionReadable;

            // Scan each dword for a vtable pointer into d3d9.dll
            for (int off = 0; off <= regionReadable - 4; off += 4)
            {
                int value = BitConverter.ToInt32(regionData, off);
                if (value < d3d9Base || value >= d3d9End) continue;

                d3d9Pointers++;

                // Check if this is a 119-entry device vtable
                int vtableOffset = value - d3d9Base;
                if (vtableOffset + vtableByteLen > d3d9Readable) continue;

                int inModule = 0;
                for (int i = 0; i < numEntries; i++)
                {
                    int entry = BitConverter.ToInt32(d3d9Image, vtableOffset + i * 4);
                    if (entry >= d3d9Base && entry < d3d9End)
                        inModule++;
                }

                if (inModule < numEntries * 95 / 100) continue;

                // BeginScene and EndScene must differ
                int bs = BitConverter.ToInt32(d3d9Image, vtableOffset + DeviceVTableIndex.BeginScene * 4);
                int es = BitConverter.ToInt32(d3d9Image, vtableOffset + DeviceVTableIndex.EndScene * 4);
                if (bs == es) continue;

                // EndScene must have a hookable prologue
                int esOff = es - d3d9Base;
                if ((uint)esOff >= (uint)(d3d9Readable - 3)) continue;
                if (!IsRecognizedPrologue(d3d9Image, esOff)) continue;

                var vtable = new IntPtr[numEntries];
                for (int i = 0; i < numEntries; i++)
                    vtable[i] = new IntPtr(BitConverter.ToInt32(d3d9Image, vtableOffset + i * 4));

                int objectAddr = regionBase + off;
                RynthLog.D3D9($"D3D9VTable: Found live device at 0x{objectAddr:X8}, vtable=0x{value:X8} (d3d9+0x{vtableOffset:X}), " +
                            $"EndScene=0x{vtable[DeviceVTableIndex.EndScene]:X8} ({inModule}/{numEntries} in-module)");
                RynthLog.D3D9($"D3D9VTable: Heap scan stats: {regionsScanned} regions, {bytesScanned / 1024}KB, {d3d9Pointers} d3d9 pointers checked.");
                return vtable;
            }

            scanAddr = nextAddr;
        }

        RynthLog.D3D9($"D3D9VTable: No live device found — {regionsScanned} regions, {bytesScanned / 1024}KB, {d3d9Pointers} d3d9 pointers.");
        return null;
    }

    // ─── VirtualQuery wrapper ─────────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern int VirtualQuery(IntPtr lpAddress, byte[] lpBuffer, int dwLength);

    private static bool VirtualQueryWrapped(IntPtr address, out int regionBase, out int regionSize,
        out uint state, out uint protect, out uint type)
    {
        regionBase = 0; regionSize = 0; state = 0; protect = 0; type = 0;
        byte[] buf = new byte[28]; // MEMORY_BASIC_INFORMATION on x86
        int result = VirtualQuery(address, buf, 28);
        if (result == 0) return false;

        regionBase = BitConverter.ToInt32(buf, 0);   // BaseAddress
        regionSize = BitConverter.ToInt32(buf, 12);   // RegionSize
        state      = BitConverter.ToUInt32(buf, 16);  // State
        protect    = BitConverter.ToUInt32(buf, 20);  // Protect
        type       = BitConverter.ToUInt32(buf, 24);  // Type
        return true;
    }

    private static bool TryReadModuleImageSize(IntPtr moduleBase, out int imageSize)
    {
        imageSize = 0;
        try
        {
            if ((ushort)Marshal.ReadInt16(moduleBase) != 0x5A4D) return false;         // MZ
            int peOffset = Marshal.ReadInt32(moduleBase, 0x3C);
            if (peOffset <= 0) return false;
            IntPtr ntHeaders = IntPtr.Add(moduleBase, peOffset);
            if ((uint)Marshal.ReadInt32(ntHeaders) != 0x00004550) return false;        // PE\0\0
            imageSize = Marshal.ReadInt32(ntHeaders, 0x18 + 0x38);                     // SizeOfImage
            return imageSize > 0x1000;
        }
        catch
        {
            return false;
        }
    }
}

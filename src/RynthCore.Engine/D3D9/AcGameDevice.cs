// ============================================================================
//  RynthCore.Engine - D3D9/AcGameDevice.cs
//  AC's own IDirect3DDevice9, read from AC's memory, so the EndScene hook is
//  taken from the device AC draws with and the engine creates no D3D9 device
//  of its own (2026-11-19).
//
//  Why: the EndScene address used to come from a throwaway device (D3D9VTable
//  .GetDeviceVTable: Direct3DCreate9 + CreateDevice NULLREF, then HAL, on a
//  hidden 1x1 window) at every login and every engine hot reload. A second
//  device created while AC's device holds exclusive full screen can make AC's
//  device lost; when AC's Reset then fails, AC shows "Could not initialize
//  Direct3D. Please ensure that DirectX 9.0 is installed" and quits (full
//  screen at the monitor's native resolution, with RynthCore only).
//
//  Where AC keeps it (read offline from our client, 4,841,472 bytes; both
//  copies, C:\Turbine and C:\Games\RynthCore\AcClient):
//    - The render device global (0x00870340 here; AcCellSight reads its frame
//      stamp) points at AC's RenderDeviceD3D. PrimD3DRender::UpdateLightsInternal
//      loads it into ecx and calls RenderDeviceD3D::SetFFAmbientColor32
//      (0x005A3EB0) with it (0x0059C15E: mov ecx,[0x00870340] ... call
//      0x005A3EB0): the global's object is that function's `this`.
//    - SetFFAmbientColor32 reads the device at this+0x468 (mov eax,[esi+468h];
//      mov ecx,[eax]; push edi; push 8Bh; push eax; call [ecx+0E4h] =
//      SetRenderState(D3DRS_AMBIENT)). The constructor zeroes +0x468
//      (0x005A3395), device creation hands &this->+0x468 to IDirect3D9::
//      CreateDevice as ppReturnedDeviceInterface (0x005A3A4B; the IDirect3D9 is
//      [[0x0086734C]+0x1C]), and teardown releases it (0x005A352F).
//  Both are found by pattern (HookResolver; tools\pe_pattern.py CHECK covers
//  them), the device offset is read out of the matched code, and the global's
//  call site must call the SetFFAmbientColor32 the other pattern found.
//
//  Before a device is used: every pointer on the way is probed (MemoryProbe),
//  and its vtable's EndScene, Reset and Present slots must all be in the loaded
//  d3d9.dll (AcGameDevicePolicy.JudgeDevice). Anything else = not found: the
//  bootstrapper logs why and falls back to the throwaway-device discovery.
//  Read only: nothing here writes AC memory or calls into AC or the device.
// ============================================================================
using System;
using System.Runtime.InteropServices;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.D3D9;

internal static unsafe class AcGameDevice
{
    // RenderDeviceD3D::SetFFAmbientColor32 on the 4,841,472-byte client. Pattern is the source of truth.
    private const int SetAmbientVa = 0x005A3EB0;

    // AmbientLight's pattern for the same function, with the device offset (bytes 28-31) wildcarded
    // and read back from the match: push esi; mov esi,ecx; mov al,[esi+470h]; test al,al; push edi;
    // mov edi,[esp+0Ch]; jnz +8; cmp [esi+4A0h],edi; jz +1Bh; mov eax,[esi+DEVICE]; mov ecx,[eax];
    // push edi; push 8Bh (D3DRS_AMBIENT). Unique on both client copies (2026-11-19).
    private static readonly byte?[] SetAmbientDevicePattern =
    [
        0x56,
        0x8B, 0xF1,
        0x8A, 0x86, 0x70, 0x04, 0x00, 0x00,
        0x84, 0xC0,
        0x57,
        0x8B, 0x7C, 0x24, 0x0C,
        0x75, 0x08,
        0x39, 0xBE, 0xA0, 0x04, 0x00, 0x00,
        0x74, 0x1B,
        0x8B, 0x86, null, null, null, null,
        0x8B, 0x08,
        0x57,
        0x68, 0x8B, 0x00, 0x00, 0x00,
    ];

    // The render device global (RenderDevice* render_device) on the 4,841,472-byte client.
    private const int RenderDeviceGlobalVa = 0x00870340;

    // PrimD3DRender::UpdateLightsInternal at 0x0059C15E: mov ecx,[render device global]; shl eax,8;
    // or eax,ebx; shl eax,8; or eax,edi; shl eax,8; push ebp; or eax,esi; push eax;
    // call RenderDeviceD3D::SetFFAmbientColor32. The global (bytes 2-5) is wildcarded and read back;
    // the call (byte 23, E8 rel32) must land on the SetFFAmbientColor32 found above. Unique on both
    // client copies (2026-11-19).
    private static readonly byte?[] RenderDeviceGlobalXref =
    [
        0x8B, 0x0D, null, null, null, null,
        0xC1, 0xE0, 0x08,
        0x0B, 0xC3,
        0xC1, 0xE0, 0x08,
        0x0B, 0xC7,
        0xC1, 0xE0, 0x08,
        0x55,
        0x0B, 0xC6,
        0x50,
        0xE8,
    ];
    private const int XrefCallAt = 23;

    private static readonly object Sync = new();
    private static bool _resolveTried;
    private static IntPtr _global;        // address of the RenderDevice* global (0 = unresolved)
    private static int _deviceOffset;     // RenderDeviceD3D's IDirect3DDevice9* field
    private static string _resolveFailure = "not resolved";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);

    /// <summary>
    /// One look for AC's live device. Zero when it can't be used, with why in
    /// <paramref name="why"/>; <paramref name="worthRetry"/> says whether waiting could help (AC
    /// has no device yet) or not (the code wasn't found, the device isn't d3d9's).
    /// </summary>
    public static IntPtr TryFind(out string why, out bool worthRetry)
    {
        worthRetry = false;
        try
        {
            if (!EnsureResolved(out why))
                return IntPtr.Zero;

            IntPtr g = _global;
            if (!MemoryProbe.IsReadable(g, 4)) { why = $"the render device global 0x{g.ToInt32():X8} is unreadable"; return IntPtr.Zero; }
            IntPtr renderDevice = *(IntPtr*)g;
            if (renderDevice == IntPtr.Zero) { why = "AC has no render device yet"; worthRetry = true; return IntPtr.Zero; }

            IntPtr field = renderDevice + _deviceOffset;
            if (!MemoryProbe.IsReadable(field, 4)) { why = $"RenderDeviceD3D 0x{renderDevice.ToInt32():X8}+0x{_deviceOffset:X} is unreadable"; return IntPtr.Zero; }
            IntPtr device = *(IntPtr*)field;
            if (device == IntPtr.Zero) { why = "AC's render device has no D3D device yet"; worthRetry = true; return IntPtr.Zero; }

            if (!MemoryProbe.IsReadable(device, 4)) { why = $"device 0x{device.ToInt32():X8} is unreadable"; return IntPtr.Zero; }
            IntPtr vtable = *(IntPtr*)device;
            int slots = DeviceVTableIndex.EndScene + 1;
            if (!MemoryProbe.IsReadable(vtable, slots * IntPtr.Size)) { why = $"device 0x{device.ToInt32():X8}'s vtable 0x{vtable.ToInt32():X8} is unreadable"; return IntPtr.Zero; }
            IntPtr* vt = (IntPtr*)vtable;
            IntPtr endScene = vt[DeviceVTableIndex.EndScene];
            IntPtr reset = vt[DeviceVTableIndex.Reset];
            IntPtr present = vt[17];

            long d3d9 = GetModuleHandleW("d3d9.dll").ToInt64();
            string? bad = AcGameDevicePolicy.JudgeDevice(d3d9, ModuleOf(endScene), ModuleOf(reset), ModuleOf(present));
            string where = $"device 0x{device.ToInt32():X8} ([0x{g.ToInt32():X8}] = RenderDeviceD3D 0x{renderDevice.ToInt32():X8}, +0x{_deviceOffset:X}): " +
                           $"vtable {DecalD3D9.Describe(vtable)}, EndScene {DecalD3D9.Describe(endScene)}, Reset {DecalD3D9.Describe(reset)}, Present {DecalD3D9.Describe(present)}";
            if (bad != null)
            {
                why = $"{bad} ({where})";
                return IntPtr.Zero;
            }
            why = where;
            return device;
        }
        catch (Exception ex)
        {
            why = $"{ex.GetType().Name}: {ex.Message}";
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Looks for AC's device until <paramref name="timeoutMs"/> runs out while waiting could help
    /// (bootstrap thread; stops when the engine shuts down). Zero = not found, why in <paramref name="why"/>.
    /// </summary>
    public static IntPtr WaitFor(int timeoutMs, int pollMs, out string why)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            IntPtr device = TryFind(out why, out bool worthRetry);
            if (device != IntPtr.Zero || !worthRetry || Environment.TickCount64 >= deadline)
                return device;
            if (!EngineThreads.Sleep(pollMs))
            {
                why = "the engine is shutting down";
                return IntPtr.Zero;
            }
        }
    }

    private static long ModuleOf(IntPtr address)
    {
        const uint FROM_ADDRESS = 0x4, UNCHANGED_REFCOUNT = 0x2;
        if (address == IntPtr.Zero)
            return 0;
        return GetModuleHandleExW(FROM_ADDRESS | UNCHANGED_REFCOUNT, address, out IntPtr module) ? module.ToInt64() : 0;
    }

    private static bool EnsureResolved(out string why)
    {
        lock (Sync)
        {
            if (!_resolveTried)
            {
                _resolveTried = true;
                Resolve();
            }
            why = _resolveFailure;
            return _global != IntPtr.Zero;
        }
    }

    private static void Resolve()
    {
        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
        {
            _resolveFailure = "acclient.exe not available";
            return;
        }

        HookResolver.ResolveResult fn = HookResolver.Resolve(text, "AcGameDevice.RenderDeviceD3D::SetFFAmbientColor32", SetAmbientDevicePattern, SetAmbientVa);
        if (!fn.Success)
        {
            _resolveFailure = "RenderDeviceD3D::SetFFAmbientColor32 not found";
            return;
        }
        int offset = AcGameDevicePolicy.DeviceOffsetFromSetAmbient(text.Bytes, fn.Address.ToInt32() - text.TextBaseVa);
        if (offset < 0)
        {
            _resolveFailure = $"no device field in the code at 0x{fn.Address.ToInt32():X8} ({fn.Detail})";
            return;
        }

        HookResolver.ResolveResult g = HookResolver.ResolveData(text, "AcGameDevice.render_device", RenderDeviceGlobalXref, 2, RenderDeviceGlobalVa);
        int global = g.Address.ToInt32();
        long moduleBase = text.ModuleBase.ToInt64();
        if (global < moduleBase || global >= moduleBase + text.ImageSize)
        {
            _resolveFailure = $"the render device global 0x{global:X8} is outside acclient.exe";
            return;
        }
        if (g.Source == HookResolver.ResolveSource.PatternScan)
        {
            // The site that loads the global must call the SetFFAmbientColor32 found above: then the
            // global's object is that function's `this`, a RenderDeviceD3D.
            int site = PatternScanner.FindPattern(text.Bytes, RenderDeviceGlobalXref);
            int callAt = site + XrefCallAt;
            if (site < 0 || !AcGameDevicePolicy.CallLandsOn(text.Bytes, callAt, text.TextBaseVa + callAt, fn.Address.ToInt32()))
            {
                _resolveFailure = $"the render device global's call site doesn't call SetFFAmbientColor32 at 0x{fn.Address.ToInt32():X8}";
                return;
            }
        }

        _deviceOffset = offset;
        _global = new IntPtr(global);
        _resolveFailure = "";
        RynthLog.D3D9($"AcGameDevice: AC's device is [[0x{global:X8}]+0x{offset:X}] (render device global via {g.Detail}, " +
                      $"SetFFAmbientColor32 @ 0x{fn.Address.ToInt32():X8} via {fn.Detail}).");
    }
}

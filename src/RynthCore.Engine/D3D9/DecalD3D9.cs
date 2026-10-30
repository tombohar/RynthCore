// ============================================================================
//  RynthCore.Engine - D3D9/DecalD3D9.cs
//
//  Decal clients with the in-game renderer only (DecalInGameImGui, docs/DECAL_BRIDGE_PLAN.md).
//  Decal gives AC's device its own copy of the vtable, with Reset, BeginScene, EndScene and a
//  few more slots pointing into Inject.dll (measured 2026-09-30). The engine hooks d3d9.dll's
//  own functions (which Decal's wrappers call) and never Decal's code: HookTarget resolves a
//  device slot through d3d9's original vtable. Without Decal, Enabled stays false and
//  HookTarget is the plain vtable read it always was.
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.D3D9;

internal static unsafe class DecalD3D9
{
    /// <summary>Set by EndSceneHook.InstallWithoutDevice (Decal clients only).</summary>
    internal static bool Enabled;

    /// <summary>d3d9.dll's own EndScene for AC's device, from <see cref="TryResolve"/>.</summary>
    internal static IntPtr ResolvedEndScene;

    /// <summary>
    /// Decides, before any hook goes in, whether the in-game renderer can run in this Decal
    /// client: AC's device from the bridge (Decal's public IDecalCore.GetD3DDevice; read
    /// without waiting), and d3d9.dll's EndScene for it - directly when its vtable is d3d9's,
    /// or through the d3d9 vtable Decal's per-device copy was made from. Read-only. False =
    /// stay on the Decal-coexistence path (no D3D9). Called once per generation.
    /// </summary>
    internal static bool TryResolve()
    {
        if (ResolvedEndScene != IntPtr.Zero)
            return true;
        IntPtr device = Compatibility.DecalBridgeHost.DecalDevice;
        if (device == IntPtr.Zero)
        {
            RynthLog.D3D9("DecalD3D9: the bridge hasn't reported Decal's device - staying on the coexistence path.");
            return false;
        }
        IntPtr es = DescribeDevice(device, "Decal's device (GetD3DDevice)", out bool inD3d9);
        if (inD3d9 && es != IntPtr.Zero)
        {
            ResolvedEndScene = es;
            return true;
        }
        // Decal swapped the device's vtable for its own copy: find d3d9's original.
        IntPtr[]? original = D3D9VTable.MatchOriginalDeviceVTable(Marshal.ReadIntPtr(device));
        if (original == null)
        {
            RynthLog.D3D9("DecalD3D9: d3d9.dll's original device vtable wasn't found - staying on the coexistence path.");
            return false;
        }
        OriginalVTable = original;
        ResolvedEndScene = original[DeviceVTableIndex.EndScene];
        RynthLog.D3D9($"DecalD3D9: Decal wraps EndScene; d3d9's own EndScene is {Describe(ResolvedEndScene)} (Decal's wrapper calls it).");
        return true;
    }

    /// <summary>Decal clients: d3d9.dll's own device vtable (the one Decal's per-device copy
    /// was made from), when found. Hooks on device methods resolve through it.</summary>
    internal static IntPtr[]? OriginalVTable;
    private static int _refusedLogged;

    /// <summary>
    /// The address to hook for a device method. Without Decal: the device's vtable slot, as
    /// always. With Decal: d3d9.dll's own function for that slot, and zero (do not hook) if
    /// it isn't in d3d9.dll - the engine never hooks Decal's code.
    /// </summary>
    internal static IntPtr HookTarget(IntPtr device, int index)
    {
        IntPtr fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(device), index * IntPtr.Size);
        if (!Enabled)
            return fn;
        if (OriginalVTable != null && index < OriginalVTable.Length)
            fn = OriginalVTable[index];
        string where = Describe(fn);
        if (where.StartsWith("d3d9.dll+", StringComparison.OrdinalIgnoreCase))
            return fn;
        if (Interlocked.Increment(ref _refusedLogged) <= 8)
            RynthLog.D3D9($"DecalD3D9: device slot {index} resolves to {where}, not d3d9.dll - not hooked (Decal's code is never patched).");
        return IntPtr.Zero;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(IntPtr module, char* buffer, uint size);

    /// <summary>"module+0xRVA" for a code or data address.</summary>
    internal static string Describe(IntPtr address)
    {
        const uint FROM_ADDRESS = 0x4, UNCHANGED_REFCOUNT = 0x2;
        if (address == IntPtr.Zero)
            return "null";
        if (!GetModuleHandleExW(FROM_ADDRESS | UNCHANGED_REFCOUNT, address, out IntPtr module) || module == IntPtr.Zero)
            return $"0x{address.ToInt32():X8} (no module)";
        char* buf = stackalloc char[260];
        uint n = GetModuleFileNameW(module, buf, 260);
        return $"{System.IO.Path.GetFileName(new string(buf, 0, (int)n))}+0x{address.ToInt32() - module.ToInt32():X}";
    }

    /// <summary>Logs a device's vtable owner and EndScene/Reset/Present slots; returns its EndScene.</summary>
    internal static IntPtr DescribeDevice(IntPtr device, string what, out bool endSceneInD3d9)
    {
        endSceneInD3d9 = false;
        try
        {
            IntPtr vtable = Marshal.ReadIntPtr(device);
            IntPtr endScene = Marshal.ReadIntPtr(vtable, DeviceVTableIndex.EndScene * IntPtr.Size);
            IntPtr reset = Marshal.ReadIntPtr(vtable, DeviceVTableIndex.Reset * IntPtr.Size);
            IntPtr present = Marshal.ReadIntPtr(vtable, 17 * IntPtr.Size);
            string es = Describe(endScene);
            endSceneInD3d9 = es.StartsWith("d3d9.dll+", StringComparison.OrdinalIgnoreCase);
            RynthLog.D3D9($"DecalD3D9: {what} 0x{device.ToInt32():X8}: vtable {Describe(vtable)}, EndScene {es}, Reset {Describe(reset)}, Present {Describe(present)}.");
            return endScene;
        }
        catch (Exception ex)
        {
            RynthLog.D3D9($"DecalD3D9: reading {what} threw {ex.GetType().Name}: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    /// <summary>First backbuffer frame in our EndScene detour (AC's thread): which device our
    /// d3d9 hook is called with, and whose code its vtable points at.</summary>
    internal static void FirstFrame(IntPtr device) => DescribeDevice(device, "EndScene called with device", out _);
}

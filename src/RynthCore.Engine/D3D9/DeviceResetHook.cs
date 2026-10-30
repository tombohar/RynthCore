// ============================================================================
//  RynthCore.Engine - D3D9/DeviceResetHook.cs
//  IDirect3DDevice9::Reset: release the engine's D3DPOOL_DEFAULT resources
//  (the pop-outs' render targets) just before AC resets a lost device.
//
//  A plain D3D9 device can't be Reset while any default-pool resource is
//  alive: Reset fails, and AC shows "Could not initialize Direct3D" and stops
//  (2026-09-30 15:06, Lucy, PID 9384: the ImGui bar was popped out when the
//  device was lost — screen lock / display change). The resources are
//  recreated on the next frame, so nothing else changes.
//
//  AC's main thread (Reset is called where EndScene is). Installed from the
//  device when the frame controller first sees it; removed at shutdown.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.ImGuiBackend;

namespace RynthCore.Engine.D3D9;

internal static class DeviceResetHook
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ResetDelegate(IntPtr device, IntPtr presentationParameters);

    private static ResetDelegate? _original;
    private static ResetDelegate? _detour;
    private static IntPtr _address;
    private static bool _installed;
    private static int _resets;

    public static void InstallFromDevice(IntPtr device)
    {
        if (_installed || device == IntPtr.Zero) return;
        try
        {
            // Without Decal: the device's vtable slot, as before. With Decal: d3d9's own Reset
            // (Decal wraps Reset; its code is never hooked) - zero means not hooked.
            _address = DecalD3D9.HookTarget(device, DeviceVTableIndex.Reset);
            if (_address == IntPtr.Zero) return;
            _detour = ResetDetour;
            _original = Marshal.GetDelegateForFunctionPointer<ResetDelegate>(
                MinHook.HookCreate(_address, Marshal.GetFunctionPointerForDelegate(_detour)));
            Thread.MemoryBarrier();
            MinHook.Enable(_address);
            _installed = true;
            RynthLog.D3D9($"DeviceResetHook: installed at 0x{_address.ToInt32():X8}.");
        }
        catch (Exception ex)
        {
            RynthLog.D3D9($"DeviceResetHook: install failed - {ex.Message}");
        }
    }

    public static void Uninstall()
    {
        if (!_installed) return;
        RynthLog.D3D9($"DeviceResetHook: disable = {MinHook.StatusString(MinHook.MH_DisableHook(_address))}, remove = {MinHook.StatusString(MinHook.MH_RemoveHook(_address))}.");
        _installed = false;
        _original = null;
        _detour = null;
    }

    private static int ResetDetour(IntPtr device, IntPtr presentationParameters)
    {
        int n = Interlocked.Increment(ref _resets);
        try
        {
            int released = ImGuiPopOuts.ReleaseDeviceResources();
            if (n <= 20)
                RynthLog.D3D9($"DeviceResetHook: device Reset #{n}: released {released} pop-out surface set(s) first.");
        }
        catch (Exception ex)
        {
            try { RynthLog.D3D9($"DeviceResetHook: releasing before Reset threw {ex.GetType().Name}: {ex.Message}"); } catch { }
        }

        int hr = _original!(device, presentationParameters);
        if (n <= 20 || hr < 0)
        {
            try { RynthLog.D3D9($"DeviceResetHook: Reset #{n} returned 0x{hr:X8}."); } catch { }
        }
        return hr;
    }
}

using System;
using System.Runtime.InteropServices;
using System.Threading;
using NexCore.Engine.Hooking;

namespace NexCore.Engine.D3D9;

internal static class D3D9Bootstrapper
{
    private const uint D3DSdkVersion = 32;
    private const int PollIntervalMs = 50;
    private const int BootstrapFallbackMs = 15000;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr Direct3DCreate9Delegate(uint sdkVersion);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDeviceDelegate(
        IntPtr pD3D9,
        uint adapter,
        uint deviceType,
        IntPtr hFocusWindow,
        uint behaviorFlags,
        IntPtr pPresentationParameters,
        out IntPtr ppDevice);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetModuleHandleA(string? lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    private static int _started;
    private static bool _d3dCreateHookInstalled;
    private static bool _createDeviceHookInstalled;
    private static bool _realDeviceObserved;
    private static Direct3DCreate9Delegate? _originalDirect3DCreate9;
    private static Direct3DCreate9Delegate? _direct3DCreate9Detour;
    private static CreateDeviceDelegate? _originalCreateDevice;
    private static CreateDeviceDelegate? _createDeviceDetour;
    private static IntPtr _createDeviceTarget;
    private static IntPtr _direct3DCreate9Target;

    public static void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;

        var thread = new Thread(BootstrapWorker)
        {
            Name = "NexCore.D3D9Bootstrap",
            IsBackground = true
        };
        thread.Start();
    }

    private static void BootstrapWorker()
    {
        try
        {
            // If d3d9.dll is already loaded, the game's D3D device almost certainly
            // exists already.  Skip the Direct3DCreate9 hook path and install from
            // a throwaway vtable immediately — the vtable entries are the same.
            IntPtr earlyModule = GetModuleHandleA("d3d9.dll");
            if (earlyModule != IntPtr.Zero)
            {
                EntryPoint.Log("D3D9Bootstrapper: d3d9.dll already loaded — installing EndScene hook via vtable discovery.");
                EndSceneHook.Install();
                return;
            }

            EntryPoint.Log("D3D9Bootstrapper: Waiting for d3d9.dll so NexCore can hook the real device creation path.");
            DateTime fallbackDeadline = DateTime.UtcNow.AddMilliseconds(BootstrapFallbackMs);

            while (!EndSceneHook.IsInstalled())
            {
                IntPtr d3d9Module = GetModuleHandleA("d3d9.dll");
                if (d3d9Module != IntPtr.Zero)
                {
                    TryHookDirect3DCreate9(d3d9Module);
                    if (_realDeviceObserved || EndSceneHook.IsInstalled())
                        return;
                }

                if (DateTime.UtcNow >= fallbackDeadline)
                {
                    EntryPoint.Log("D3D9Bootstrapper: Real CreateDevice path was not observed in time. Falling back to shared vtable discovery.");
                    RemoveBootstrapHooks();
                    EndSceneHook.Install();
                    return;
                }

                Thread.Sleep(PollIntervalMs);
            }
        }
        catch (Exception ex)
        {
            EntryPoint.Log($"D3D9Bootstrapper: FATAL bootstrap error: {ex}");
            if (!EndSceneHook.IsInstalled())
            {
                EntryPoint.Log("D3D9Bootstrapper: Attempting fallback EndScene install after bootstrap failure.");
                RemoveBootstrapHooks();
                EndSceneHook.Install();
            }
        }
    }

    private static void TryHookDirect3DCreate9(IntPtr d3d9Module)
    {
        if (_d3dCreateHookInstalled)
            return;

        IntPtr direct3DCreate9Addr = GetProcAddress(d3d9Module, "Direct3DCreate9");
        if (direct3DCreate9Addr == IntPtr.Zero)
        {
            EntryPoint.Log($"D3D9Bootstrapper: Direct3DCreate9 export unavailable (error {Marshal.GetLastWin32Error()}).");
            return;
        }

        _direct3DCreate9Target = direct3DCreate9Addr;
        _direct3DCreate9Detour = Direct3DCreate9Detour;
        IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_direct3DCreate9Detour);

        IntPtr originalPtr = MinHook.Hook(direct3DCreate9Addr, detourPtr);
        _originalDirect3DCreate9 = Marshal.GetDelegateForFunctionPointer<Direct3DCreate9Delegate>(originalPtr);
        _d3dCreateHookInstalled = true;

        EntryPoint.Log($"D3D9Bootstrapper: Hooked Direct3DCreate9 @ 0x{direct3DCreate9Addr:X8}");
    }

    private static IntPtr Direct3DCreate9Detour(uint sdkVersion)
    {
        IntPtr direct3D9 = _originalDirect3DCreate9!(sdkVersion);

        if (sdkVersion != D3DSdkVersion)
            EntryPoint.Log($"D3D9Bootstrapper: Direct3DCreate9 called with SDK {sdkVersion} (expected {D3DSdkVersion}).");

        if (direct3D9 == IntPtr.Zero)
        {
            EntryPoint.Log("D3D9Bootstrapper: Direct3DCreate9 returned null.");
            return IntPtr.Zero;
        }

        TryHookCreateDevice(direct3D9);
        return direct3D9;
    }

    private static void TryHookCreateDevice(IntPtr direct3D9)
    {
        if (_createDeviceHookInstalled)
            return;

        IntPtr vtable = Marshal.ReadIntPtr(direct3D9);
        _createDeviceTarget = Marshal.ReadIntPtr(vtable, D3D9VTableIndex.CreateDevice * IntPtr.Size);
        if (_createDeviceTarget == IntPtr.Zero)
        {
            EntryPoint.Log("D3D9Bootstrapper: IDirect3D9::CreateDevice pointer was null.");
            return;
        }

        _createDeviceDetour = CreateDeviceDetour;
        IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_createDeviceDetour);

        IntPtr originalPtr = MinHook.Hook(_createDeviceTarget, detourPtr);
        _originalCreateDevice = Marshal.GetDelegateForFunctionPointer<CreateDeviceDelegate>(originalPtr);
        _createDeviceHookInstalled = true;

        EntryPoint.Log($"D3D9Bootstrapper: Hooked IDirect3D9::CreateDevice @ 0x{_createDeviceTarget:X8}");
    }

    private static void RemoveBootstrapHooks()
    {
        if (_d3dCreateHookInstalled && _direct3DCreate9Target != IntPtr.Zero)
        {
            int status = MinHook.MH_DisableHook(_direct3DCreate9Target);
            EntryPoint.Log($"D3D9Bootstrapper: Disable Direct3DCreate9 hook = {MinHook.StatusString(status)}");
            status = MinHook.MH_RemoveHook(_direct3DCreate9Target);
            EntryPoint.Log($"D3D9Bootstrapper: Remove Direct3DCreate9 hook = {MinHook.StatusString(status)}");
            _d3dCreateHookInstalled = false;
        }

        if (_createDeviceHookInstalled && _createDeviceTarget != IntPtr.Zero)
        {
            int status = MinHook.MH_DisableHook(_createDeviceTarget);
            EntryPoint.Log($"D3D9Bootstrapper: Disable CreateDevice hook = {MinHook.StatusString(status)}");
            status = MinHook.MH_RemoveHook(_createDeviceTarget);
            EntryPoint.Log($"D3D9Bootstrapper: Remove CreateDevice hook = {MinHook.StatusString(status)}");
            _createDeviceHookInstalled = false;
        }
    }

    private static int CreateDeviceDetour(
        IntPtr pD3D9,
        uint adapter,
        uint deviceType,
        IntPtr hFocusWindow,
        uint behaviorFlags,
        IntPtr pPresentationParameters,
        out IntPtr ppDevice)
    {
        int hr = _originalCreateDevice!(
            pD3D9,
            adapter,
            deviceType,
            hFocusWindow,
            behaviorFlags,
            pPresentationParameters,
            out ppDevice);

        if (hr >= 0 && ppDevice != IntPtr.Zero)
        {
            _realDeviceObserved = true;
            EntryPoint.Log($"D3D9Bootstrapper: Observed real device creation (device=0x{ppDevice:X8}, hwnd=0x{hFocusWindow:X8}, hr=0x{hr:X8}).");
            RemoveBootstrapHooks();
            EndSceneHook.InstallFromDevice(ppDevice);
        }
        else
        {
            EntryPoint.Log($"D3D9Bootstrapper: CreateDevice returned hr=0x{hr:X8}, device=0x{ppDevice:X8}");
        }

        return hr;
    }
}

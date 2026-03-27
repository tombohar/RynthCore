using System;
using System.Runtime.InteropServices;
using NexCore.Engine.Hooking;
using NexCore.Engine.ImGuiBackend;

namespace NexCore.Engine.D3D9;

internal static class EndSceneHook
{
    private const int MaxOffscreenSkipsBeforeFallback = 3;
    private const int UiInitWarmupFrames = 180;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EndSceneDelegate(IntPtr pDevice);

    private static EndSceneDelegate? _originalEndScene;
    private static EndSceneDelegate? _hookDelegate;
    private static IntPtr _endSceneAddr;
    private static bool _installed;
    private static int _frameCount;
    private static int _renderCount;
    private static int _skipCount;
    private static int _uiFrameCount;
    private static bool _offscreenFilterDisabled;
    private static bool _uiActivated;
    private static bool _warmupLogged;
    private static string _installSource = "uninitialized";

    public static void Install()
    {
        if (_installed)
            return;

        ResetInstallState("fallback-vtable");
        EntryPoint.Log("EndSceneHook: Discovering D3D9 vtable for fallback install...");

        IntPtr[]? vtable = D3D9VTable.GetDeviceVTable();
        if (vtable == null)
        {
            EntryPoint.Log("EndSceneHook: FAILED - could not read fallback vtable.");
            return;
        }

        IntPtr endSceneAddress = vtable[DeviceVTableIndex.EndScene];
        EntryPoint.Log($"EndSceneHook: Fallback EndScene @ 0x{endSceneAddress:X8}");
        InstallFromEndSceneAddress(endSceneAddress);
    }

    public static void InstallFromDevice(IntPtr pDevice)
    {
        if (_installed)
            return;

        if (pDevice == IntPtr.Zero)
        {
            EntryPoint.Log("EndSceneHook: Real-device install skipped because the device pointer was null.");
            return;
        }

        ResetInstallState("real-device");

        IntPtr deviceVTable = Marshal.ReadIntPtr(pDevice);
        IntPtr endSceneAddress = Marshal.ReadIntPtr(deviceVTable, DeviceVTableIndex.EndScene * IntPtr.Size);
        EntryPoint.Log($"EndSceneHook: Using real D3D9 device 0x{pDevice:X8}; EndScene @ 0x{endSceneAddress:X8}");
        InstallFromEndSceneAddress(endSceneAddress);
    }

    public static void InstallFromAddress(IntPtr endSceneAddress, string installSource)
    {
        if (_installed)
            return;

        if (endSceneAddress == IntPtr.Zero)
        {
            EntryPoint.Log("EndSceneHook: Address install skipped because EndScene was null.");
            return;
        }

        ResetInstallState(installSource);
        EntryPoint.Log($"EndSceneHook: Using explicit EndScene address 0x{endSceneAddress:X8} from {installSource}.");
        InstallFromEndSceneAddress(endSceneAddress);
    }

    public static bool IsInstalled()
    {
        return _installed;
    }

    public static void Uninstall()
    {
        if (!_installed)
            return;

        ImGuiController.Shutdown();

        int status = MinHook.MH_DisableHook(_endSceneAddr);
        EntryPoint.Log($"EndSceneHook: Disable = {MinHook.StatusString(status)}");

        status = MinHook.MH_RemoveHook(_endSceneAddr);
        EntryPoint.Log($"EndSceneHook: Remove = {MinHook.StatusString(status)}");

        _installed = false;
        _originalEndScene = null;
        _offscreenFilterDisabled = false;
    }

    private static void ResetInstallState(string installSource)
    {
        _installSource = installSource;
        _frameCount = 0;
        _renderCount = 0;
        _skipCount = 0;
        _uiFrameCount = 0;
        _offscreenFilterDisabled = false;
        _uiActivated = false;
        _warmupLogged = false;
    }

    private static void InstallFromEndSceneAddress(IntPtr endSceneAddress)
    {
        _endSceneAddr = endSceneAddress;

        EntryPoint.Log($"EndSceneHook: Creating detour delegate...");
        _hookDelegate = new EndSceneDelegate(EndSceneDetour);
        IntPtr hookPtr = Marshal.GetFunctionPointerForDelegate(_hookDelegate);
        EntryPoint.Log($"EndSceneHook: Detour @ 0x{hookPtr:X8}, target @ 0x{_endSceneAddr:X8}");

        try
        {
            EntryPoint.Log("EndSceneHook: Calling MH_Initialize...");
            int status = MinHook.MH_Initialize();
            EntryPoint.Log($"EndSceneHook: MH_Initialize = {status} ({MinHook.StatusString(status)})");

            if (status != MinHook.MH_OK && status != MinHook.MH_ERROR_ALREADY_INITIALIZED)
                throw new InvalidOperationException($"MH_Initialize failed: {MinHook.StatusString(status)}");

            EntryPoint.Log("EndSceneHook: Calling MH_CreateHook...");
            status = MinHook.MH_CreateHook(_endSceneAddr, hookPtr, out IntPtr originalPtr);
            EntryPoint.Log($"EndSceneHook: MH_CreateHook = {status} ({MinHook.StatusString(status)}), original @ 0x{originalPtr:X8}");

            if (status != MinHook.MH_OK)
                throw new InvalidOperationException($"MH_CreateHook failed: {MinHook.StatusString(status)}");

            EntryPoint.Log("EndSceneHook: Calling MH_EnableHook...");
            status = MinHook.MH_EnableHook(_endSceneAddr);
            EntryPoint.Log($"EndSceneHook: MH_EnableHook = {status} ({MinHook.StatusString(status)})");

            if (status != MinHook.MH_OK)
                throw new InvalidOperationException($"MH_EnableHook failed: {MinHook.StatusString(status)}");

            _originalEndScene = Marshal.GetDelegateForFunctionPointer<EndSceneDelegate>(originalPtr);
            _installed = true;

            EntryPoint.Log($"EndSceneHook: INSTALLED successfully via {_installSource}.");
        }
        catch (Exception ex)
        {
            EntryPoint.Log($"EndSceneHook: MinHook failed - {ex.Message}");
        }
    }

    private static int EndSceneDetour(IntPtr pDevice)
    {
        try
        {
            if (!_offscreenFilterDisabled && !DX9Backend.IsRenderingToBackBuffer(pDevice))
            {
                if (_skipCount < MaxOffscreenSkipsBeforeFallback)
                {
                    EntryPoint.Log("EndSceneHook: Skipping offscreen EndScene pass.");
                    _skipCount++;
                    return _originalEndScene!(pDevice);
                }

                _offscreenFilterDisabled = true;
                EntryPoint.Log($"EndSceneHook: Falling back to unfiltered EndScene after {_skipCount} skipped offscreen pass(es).");
            }

            _renderCount++;
            _frameCount++;

            if (_renderCount == 1 && _skipCount > 0)
                EntryPoint.Log($"EndSceneHook: First backbuffer frame after skipping {_skipCount} offscreen pass(es).");

            if (!_uiActivated)
            {
                if (!_warmupLogged)
                {
                    EntryPoint.Log($"EndSceneHook: Backbuffer detected. Warming up {UiInitWarmupFrames} frame(s) before UI init.");
                    _warmupLogged = true;
                }

                if (_renderCount < UiInitWarmupFrames)
                    return _originalEndScene!(pDevice);

                _uiActivated = true;
                EntryPoint.Log("EndSceneHook: Warmup complete - initializing ImGui.");
            }

            ImGuiController.OnEndScene(pDevice);
            _uiFrameCount++;

            if (_uiFrameCount == 60)
                EntryPoint.Log("EndSceneHook: 60 UI frames - ImGui is stable.");
        }
        catch (Exception ex)
        {
            if (_uiFrameCount < 30)
                EntryPoint.Log($"EndSceneHook: Frame {_frameCount} error: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        return _originalEndScene!(pDevice);
    }
}

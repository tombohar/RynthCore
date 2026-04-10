using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static class UiLifecycleHooks
{
    private static readonly byte?[] SetUiReadyPattern =
    [
        0x56, 0x8B, 0x74, 0x24, 0x08, 0x85, 0xF6, 0x75,
        0x16, 0x8B, 0x0D, null, null, null, null, 0x85,
        0xC9, 0x74, 0x0C, 0x8B, 0x41, 0x04, 0x85, 0xC0,
        0x74, 0x05, 0xE8, null, null, null, null, 0x89,
        0x35, null, null, null, null, 0x5E, 0xC3
    ];

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetUiReadyDelegate(int isReady);

    private static SetUiReadyDelegate? _originalSetUiReady;
    private static SetUiReadyDelegate? _setUiReadyDetour;
    private static IntPtr _targetAddress;
    private static IntPtr _uiReadyFlagAddress;
    private static string _statusMessage = "Not probed yet.";
    private static long _nextStatusLogTick;
    private static bool _waitingLogged;

    public static bool IsInstalled { get; private set; }
    public static bool HasObservedUiInitialized { get; private set; }
    public static string StatusMessage => _statusMessage;
    internal static IntPtr UiReadyFlagAddress => _uiReadyFlagAddress;

    public static event Action? UiInitialized;

    public static void Initialize()
    {
        if (IsInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        try
        {
            int funcOff = PatternScanner.FindPattern(textSection.Bytes, SetUiReadyPattern);
            if (funcOff < 0)
            {
                _statusMessage = "APIManager::SetUIReady pattern not found.";
                RynthLog.Compat($"Compat: UI lifecycle hook failed - {_statusMessage}");
                return;
            }

            _targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _uiReadyFlagAddress = new IntPtr(BitConverter.ToInt32(textSection.Bytes, funcOff + 33));
            _setUiReadyDetour = SetUiReadyDetour;

            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_setUiReadyDetour);
            _originalSetUiReady = Marshal.GetDelegateForFunctionPointer<SetUiReadyDelegate>(MinHook.HookCreate(_targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(_targetAddress);

            IsInstalled = true;
            _statusMessage = $"Hooked SetUIReady @ 0x{_targetAddress.ToInt32():X8}.";
            _nextStatusLogTick = Environment.TickCount64 + 3000;

            RynthLog.Compat(
                $"Compat: UI lifecycle hook ready - SetUIReady=0x{_targetAddress.ToInt32():X8}, uiReadyFlag=0x{_uiReadyFlagAddress.ToInt32():X8}");

            if (_uiReadyFlagAddress != IntPtr.Zero && Marshal.ReadInt32(_uiReadyFlagAddress) != 0)
            {
                HasObservedUiInitialized = true;
                _statusMessage = "UI ready already active (late attach).";
                RynthLog.Compat("Compat: UI lifecycle already active at inject time.");
            }
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: UI lifecycle hook failed - {ex.Message}");
        }
    }

    public static void Poll()
    {
        if (HasObservedUiInitialized || !IsInstalled)
            return;

        long now = Environment.TickCount64;
        if (now < _nextStatusLogTick)
            return;

        _nextStatusLogTick = now + 3000;
        if (!_waitingLogged)
        {
            _waitingLogged = true;
            RynthLog.Compat("Compat: UI lifecycle awaiting SetUIReady(1).");
        }
    }

    private static void SetUiReadyDetour(int isReady)
    {
        _originalSetUiReady!(isReady);
        if (isReady != 0)
            SignalUiInitialized("APIManager::SetUIReady(1)");
    }

    private static void SignalUiInitialized(string source)
    {
        if (HasObservedUiInitialized)
            return;

        HasObservedUiInitialized = true;
        _statusMessage = $"UI initialized observed via {source}.";
        RynthLog.Compat($"Compat: UI initialized observed via {source}.");
        UiInitialized?.Invoke();
    }
}

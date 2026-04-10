using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class BusyCountHooks
{
    private const int IncrementBusyCountVa = 0x00565610;
    private const int DecrementBusyCountVa = 0x00565630;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void BusyCountDelegate(IntPtr thisPtr);

    private static BusyCountDelegate? _originalIncrementBusyCount;
    private static BusyCountDelegate? _incrementBusyCountDetour;
    private static BusyCountDelegate? _originalDecrementBusyCount;
    private static BusyCountDelegate? _decrementBusyCountDetour;
    private static IntPtr _incrementTargetAddress;
    private static IntPtr _decrementTargetAddress;
    private static string _statusMessage = "Not probed yet.";
    private static int _incrementDispatchCount;
    private static int _decrementDispatchCount;

    public static bool IsInstalled { get; private set; }
    public static string StatusMessage => _statusMessage;

    public static void Initialize()
    {
        if (IsInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        int incrementOff = IncrementBusyCountVa - textSection.TextBaseVa;
        int decrementOff = DecrementBusyCountVa - textSection.TextBaseVa;
        if (!LooksHookable(textSection.Bytes, incrementOff, out byte incrementByte))
        {
            _statusMessage = $"ClientUISystem::IncrementBusyCount looks invalid @ 0x{IncrementBusyCountVa:X8}.";
            RynthLog.Compat($"Compat: busy-count hook failed - {_statusMessage}");
            return;
        }

        if (!LooksHookable(textSection.Bytes, decrementOff, out byte decrementByte))
        {
            _statusMessage = $"ClientUISystem::DecrementBusyCount looks invalid @ 0x{DecrementBusyCountVa:X8}.";
            RynthLog.Compat($"Compat: busy-count hook failed - {_statusMessage}");
            return;
        }

        try
        {
            _incrementTargetAddress = new IntPtr(textSection.TextBaseVa + incrementOff);
            _incrementBusyCountDetour = IncrementBusyCountDetour;
            IntPtr incrementDetourPtr = Marshal.GetFunctionPointerForDelegate(_incrementBusyCountDetour);
            _originalIncrementBusyCount = Marshal.GetDelegateForFunctionPointer<BusyCountDelegate>(MinHook.HookCreate(_incrementTargetAddress, incrementDetourPtr));

            _decrementTargetAddress = new IntPtr(textSection.TextBaseVa + decrementOff);
            _decrementBusyCountDetour = DecrementBusyCountDetour;
            IntPtr decrementDetourPtr = Marshal.GetFunctionPointerForDelegate(_decrementBusyCountDetour);
            _originalDecrementBusyCount = Marshal.GetDelegateForFunctionPointer<BusyCountDelegate>(MinHook.HookCreate(_decrementTargetAddress, decrementDetourPtr));

            Thread.MemoryBarrier();
            MinHook.Enable(_incrementTargetAddress);
            MinHook.Enable(_decrementTargetAddress);

            IsInstalled = true;
            _statusMessage = $"Hooked busy-count seams @ 0x{_incrementTargetAddress.ToInt32():X8}/0x{_decrementTargetAddress.ToInt32():X8}.";
            RynthLog.Compat(
                $"Compat: busy-count hooks ready - increment=0x{_incrementTargetAddress.ToInt32():X8} (0x{incrementByte:X2}), decrement=0x{_decrementTargetAddress.ToInt32():X8} (0x{decrementByte:X2})");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: busy-count hook failed - {ex.Message}");
        }
    }

    private static void IncrementBusyCountDetour(IntPtr thisPtr)
    {
        _originalIncrementBusyCount!(thisPtr);

        int count = Interlocked.Increment(ref _incrementDispatchCount);
        if (count <= 0)
            RynthLog.Compat($"Compat: busy count incremented #{count}");

        PluginManager.QueueBusyCountIncremented();
    }

    private static void DecrementBusyCountDetour(IntPtr thisPtr)
    {
        _originalDecrementBusyCount!(thisPtr);

        int count = Interlocked.Increment(ref _decrementDispatchCount);
        if (count <= 0)
            RynthLog.Compat($"Compat: busy count decremented #{count}");

        PluginManager.QueueBusyCountDecremented();
    }

    private static bool LooksHookable(byte[] textBytes, int offset, out byte firstByte)
    {
        firstByte = 0;
        if (offset < 0 || offset >= textBytes.Length)
            return false;

        firstByte = textBytes[offset];
        return firstByte is not (0x00 or 0xCC or 0xC3);
    }
}

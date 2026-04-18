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

    // ClientUISystem::UpdateCursorState — per-frame cursor evaluation
    private const int UpdateCursorStateVa = 0x005653D0;

    // ClientUISystem struct field offset for m_cBusy (confirmed via runtime dump)
    private const int OffsetMCBusy = 0x14;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void BusyCountDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void UpdateCursorStateDelegate(IntPtr thisPtr);

    private static BusyCountDelegate? _originalIncrementBusyCount;
    private static BusyCountDelegate? _incrementBusyCountDetour;
    private static BusyCountDelegate? _originalDecrementBusyCount;
    private static BusyCountDelegate? _decrementBusyCountDetour;
    private static IntPtr _incrementTargetAddress;
    private static IntPtr _decrementTargetAddress;
    private static string _statusMessage = "Not probed yet.";
    private static int _incrementDispatchCount;
    private static int _decrementDispatchCount;
    private static int _netBusyCount;
    private static IntPtr _lastThisPtr;

    public static bool IsInstalled { get; private set; }
    public static string StatusMessage => _statusMessage;

    /// <summary>Returns 0 if the character is idle, positive if a UI action is in progress.</summary>
    public static int GetBusyState() => Math.Max(0, _netBusyCount);

    /// <summary>Force-reset the client's busy count to zero and re-evaluate the cursor.
    /// Directly writes m_cBusy=0 then calls UpdateCursorState so the game
    /// switches from hourglass back to the default arrow.</summary>
    public static void ForceResetBusyCount()
    {
        if (!IsInstalled || _lastThisPtr == IntPtr.Zero)
            return;

        int was = _netBusyCount;

        // Call the original decrement a few times to let the client run
        // its own cleanup path (cursor reset, etc) for any non-zero count.
        if (_originalDecrementBusyCount != null)
        {
            int calls = Math.Max(was, 3);
            for (int i = 0; i < calls && i < 20; i++)
                _originalDecrementBusyCount(_lastThisPtr);
        }

        Interlocked.Exchange(ref _netBusyCount, 0);

        // Directly zero m_cBusy — DecrementBusyCount guards with if(m_cBusy>0)
        // so it's a no-op when our tracked count drifts from the real value.
        try { Marshal.WriteInt32(_lastThisPtr + OffsetMCBusy, 0); }
        catch { /* non-fatal */ }

        // Clear pending commands and restore client control
        CommandInterpreterHooks.ClearAllCommands();
        CommandInterpreterHooks.TakeControlFromServer();
        CommandInterpreterHooks.PlayerTeleported();

        // Force the game's own cursor evaluation with the cleared state
        try
        {
            var updateCursor = Marshal.GetDelegateForFunctionPointer<UpdateCursorStateDelegate>(
                new IntPtr(UpdateCursorStateVa));
            updateCursor(_lastThisPtr);
        }
        catch { /* non-fatal */ }

        RynthLog.Verbose($"Compat: force-reset busy count (was {was})");
    }

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
            RynthLog.Verbose($"Compat: busy-count hooks installed");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: busy-count hook failed - {ex.Message}");
        }
    }

    private static void IncrementBusyCountDetour(IntPtr thisPtr)
    {
        _lastThisPtr = thisPtr;
        _originalIncrementBusyCount!(thisPtr);
        Interlocked.Increment(ref _incrementDispatchCount);
        Interlocked.Increment(ref _netBusyCount);
        PluginManager.QueueBusyCountIncremented();
    }

    private static void DecrementBusyCountDetour(IntPtr thisPtr)
    {
        if (_lastThisPtr == IntPtr.Zero)
            _lastThisPtr = thisPtr;
        _originalDecrementBusyCount!(thisPtr);
        Interlocked.Increment(ref _decrementDispatchCount);
        Interlocked.Decrement(ref _netBusyCount);
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

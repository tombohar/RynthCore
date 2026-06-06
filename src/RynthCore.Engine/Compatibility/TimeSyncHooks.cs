using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Hooks ClientNet::HandleTimeSynch to track the server time/wall-clock relationship.
/// Used to convert Enchantment._start_time (server seconds) into remaining wall-clock seconds.
///
/// ClientNet::HandleTimeSynch signature (thiscall):
///   void __thiscall ClientNet::HandleTimeSynch(ClientNet *this, CTimeSyncHeader *pHdr, CNetLayerPacket *pkt)
///
/// COptionalHeader layout (4-byte packing, 24 bytes):
///   +0  Turbine_RefCount _ref (vfptr=4, m_cRef=4)
///   +8  UInt32 m_dwMask
///   +12 UInt32 m_Flags
///   +16 Char*  m_pData
///   +20 UInt32 m_cbData
/// CTimeSyncHeader layout:
///   +0  COptionalHeader (24 bytes)
///   +24 Double m_time    ← server time in seconds
/// </summary>
internal static class TimeSyncHooks
{
    private const int HandleTimeSynchVa = 0x005448F0;

    // Verified unique + lands exactly at HandleTimeSynchVa offline (tools/pe_pattern.py).
    private static readonly byte?[] HandleTimeSynchPattern = [ 0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8, 0x83, 0xEC, 0x10, 0x8B ];
    private const int TimeSyncHeaderMTimeOffset = 24;

    private static IntPtr _originalHandleTimeSynch;
    private static bool _initialized;

    // Server-time / wall-clock reference pair, updated each time sync arrives.
    private static double _lastServerTime;
    private static long _lastWallClockTicks;

    public static bool IsInitialized => _initialized;

    /// <summary>
    /// Returns the estimated current server time in seconds.
    /// Returns 0 if no time sync has been received yet.
    /// </summary>
    public static double GetCurrentServerTime()
    {
        long wallTicks = _lastWallClockTicks;
        if (wallTicks == 0)
            return 0;

        double elapsed = (DateTime.UtcNow.Ticks - wallTicks) / (double)TimeSpan.TicksPerSecond;
        return _lastServerTime + elapsed;
    }

    public static void Initialize()
    {
        if (_initialized)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
        {
            RynthLog.Compat("Compat: time-sync hook - acclient .text not readable.");
            return;
        }
        HookResolver.ResolveResult resolved = HookResolver.Resolve(text, "TimeSync.HandleTimeSynch", HandleTimeSynchPattern, HandleTimeSynchVa);
        if (!resolved.Success)
        {
            RynthLog.Compat($"Compat: time-sync hook unresolved (0x{HandleTimeSynchVa:X8})");
            return;
        }

        try
        {
            unsafe
            {
                delegate* unmanaged[Thiscall]<IntPtr, IntPtr, IntPtr, void> detour = &HandleTimeSynchDetour;
                MinHook.Hook(resolved.Address, (IntPtr)detour, out _originalHandleTimeSynch);
            }

            _initialized = true;
            RynthLog.Verbose($"Compat: time-sync hook ready - HandleTimeSynch=0x{resolved.Address.ToInt32():X8} ({resolved.Detail})");
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: time-sync hook failed: {ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static unsafe void HandleTimeSynchDetour(IntPtr thisPtr, IntPtr pHdr, IntPtr pkt)
    {
        try
        {
            if (pHdr != IntPtr.Zero)
            {
                long bits = Marshal.ReadInt64(pHdr + TimeSyncHeaderMTimeOffset);
                double serverTime = BitConverter.Int64BitsToDouble(bits);
                if (serverTime > 0)
                {
                    _lastServerTime = serverTime;
                    _lastWallClockTicks = DateTime.UtcNow.Ticks;
                }
            }
        }
        catch { }

        var original = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, IntPtr, void>)_originalHandleTimeSynch;
        original(thisPtr, pHdr, pkt);
    }
}

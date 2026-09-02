using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
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

    // Deep-audit finding #19 (2026-06-18): these used to be two plain fields
    // (double + long) written on the net thread and read on the pump thread
    // with no atomicity — on x86 a 64-bit load/store isn't atomic, so a
    // reader could observe a torn _lastWallClockTicks (worst case a
    // mis-timed rebuff from a wildly wrong elapsed time). Publishing both as
    // one immutable snapshot object, swapped via Volatile.Write/Read, fixes
    // that AND the subtler cross-field issue a per-field Interlocked fix
    // wouldn't: a reader can no longer see a NEW _lastServerTime paired with
    // the OLD _lastWallClockTicks (or vice versa) — the reference swap is
    // all-or-nothing.
    private sealed class TimeSyncSnapshot
    {
        public readonly double ServerTime;
        public readonly long WallClockTicks;
        public TimeSyncSnapshot(double serverTime, long wallClockTicks)
        {
            ServerTime = serverTime;
            WallClockTicks = wallClockTicks;
        }
    }

    private static TimeSyncSnapshot? _snapshot;

    public static bool IsInitialized => _initialized;

    /// <summary>
    /// Returns the estimated current server time in seconds.
    /// Returns 0 if no time sync has been received yet.
    /// </summary>
    public static double GetCurrentServerTime()
    {
        TimeSyncSnapshot? snap = Volatile.Read(ref _snapshot);
        if (snap == null || snap.WallClockTicks == 0)
            return 0;

        double elapsed = (DateTime.UtcNow.Ticks - snap.WallClockTicks) / (double)TimeSpan.TicksPerSecond;
        return snap.ServerTime + elapsed;
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
                    Volatile.Write(ref _snapshot, new TimeSyncSnapshot(serverTime, DateTime.UtcNow.Ticks));
                }
            }
        }
        catch { }

        var original = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, IntPtr, void>)_originalHandleTimeSynch;
        original(thisPtr, pHdr, pkt);
    }
}

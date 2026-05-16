using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Tracks which OS thread is AC's main thread, so RynthCore code that's about
/// to call into AC's internals (CACQualities accessors, etc.) can refuse the
/// call from any other thread.
///
/// AC's client is not thread-safe: its internal data structures (qualities
/// hash tables, enchantment lists, object tables) are mutated by the main
/// thread without locking. Reading them from another thread can observe a
/// sub-pointer in the middle of being reassigned, which appears as null and
/// AVs three frames deep inside AC (consistent crash at 0x00416C86 reading
/// [null+0x28] from the DecalCoexistence plugin-tick thread, 2026-05-14).
///
/// Capture: any of our [UnmanagedCallersOnly] detours runs on the thread that
/// called the detoured function. AC's main thread is what invokes
/// SetCombatMode, BusyCount updates, SmartBox/GameEvent dispatch, etc. The
/// first detour to fire after install latches that thread ID; everything
/// after compares against it.
/// </summary>
internal static class MainThreadGuard
{
    private static uint _acMainNativeTid;

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>
    /// Latch the current native thread ID as AC's main thread. Idempotent;
    /// only the first caller wins. Safe to call from any [UnmanagedCallersOnly]
    /// detour body — those run on whichever thread invoked the detoured AC
    /// function, which is AC's main thread for the detours we install.
    /// </summary>
    public static void RecordIfFirst()
    {
        if (_acMainNativeTid != 0) return;
        uint tid = GetCurrentThreadId();
        Interlocked.CompareExchange(ref _acMainNativeTid, tid, 0);
    }

    /// <summary>
    /// True if AC's main thread has been observed AND the calling thread is
    /// that thread. Returns false before the main thread is known, so any
    /// cross-thread caller fails closed (refuses to enter AC) during early
    /// init too.
    /// </summary>
    public static bool IsOnMainThread()
    {
        uint mainTid = _acMainNativeTid;
        if (mainTid == 0) return false;
        return GetCurrentThreadId() == mainTid;
    }

    /// <summary>
    /// Diagnostic accessor — for logging the captured thread ID at startup
    /// or in crash reports. Zero if not yet captured.
    /// </summary>
    public static uint MainThreadId => _acMainNativeTid;
}

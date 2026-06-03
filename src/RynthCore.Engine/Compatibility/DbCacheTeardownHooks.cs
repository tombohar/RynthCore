using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Hooks AC's DAT object-cache teardown <c>DBCache::DestroyObjectCaches</c>
/// (acclient.map 0x00014040 + 0x401000 = 0x00415040; ImageBase 0x00400000, ASLR off)
/// to QUIESCE off-thread enchantment-registry reads WHILE AC frees its DB objects.
///
/// Crash this addresses: the recurring <c>0x00416C86</c> (DBOCache::DestroyObj + 0x06,
/// read [null+0x28]) AV. Root cause (MainThreadGuard.cs 2026-05-14 note): the
/// off-thread plugin pump walks AC's CACQualities -> CEnchantmentRegistry linked lists
/// (EnchantmentHooks.ReadEnchantmentsFromQualities / WalkEnchantList) while AC frees
/// those nodes during a cache teardown; the pump reads a half-freed node and AC AVs in
/// DestroyObj. Decal never hits this because it drives AC only on AC's own thread.
///
/// ⚠ CRITICAL (verified 2026-06-02 from the live log): DestroyObjectCaches is NOT
/// close-only — AC calls it during world-load / zone-entry too (it fired ~5s after
/// login). So this is a SCOPED guard: raise a depth latch on entry, lower it when the
/// call returns. It must NOT stop the plugin pump or shut down the engine — an earlier
/// destructive version (RequestTickPumpStop + EngineLifecycle.Shutdown in this detour)
/// tore the engine down ~5-30s after EVERY login ("most things don't work"). The
/// engine's real teardown stays on the ExitProcess / WM_CLOSE path. While the latch is
/// set, EnchantmentHooks bails its registry walk, so the off-thread read never races
/// AC's free — at world-load, zone change, logout, AND final close alike.
///
/// Ported (and corrected for v1's in-process threading) from RynthCore2's
/// DestroyObjectCaches guard (RynthSuite2 f8a291e).
/// </summary>
internal static class DbCacheTeardownHooks
{
    // DBCache::DestroyObjectCaches. Fallback VA; pattern-scan is the source of truth.
    private const int DestroyObjectCachesFallbackVa = 0x00415040;

    // 8B 01 / 83 EC 0C / 53 56 57 / 6A 01 / FF 50 38 / 8D 4C 24 .. — unique in .text,
    // lands exactly at 0x00415040 (byte-verified against this client).
    private static readonly byte?[] DestroyObjectCachesPattern =
    [
        0x8B, 0x01, 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57,
        0x6A, 0x01, 0xFF, 0x50, 0x38, 0x8D, 0x4C, 0x24,
    ];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void ThisCallVoidDelegate(IntPtr thisPtr);

    private static ThisCallVoidDelegate? _originalDestroyObjectCaches;
    private static ThisCallVoidDelegate? _destroyObjectCachesDetour;
    private static IntPtr _targetAddress;

    private static int _teardownDepth;
    private static int _installed;
    private static int _enterLogCount;

    public static bool IsInstalled => Volatile.Read(ref _installed) != 0;

    /// <summary>
    /// True only WHILE AC is inside DBCache::DestroyObjectCaches (freeing/rebuilding its
    /// DB object caches — world-load, zone change, logout, or final close). Off-thread
    /// AC object/registry walkers (the plugin-pump enchantment reader) must bail while
    /// this is set so they never deref a node AC is concurrently freeing. SCOPED —
    /// cleared when the call returns; NOT a one-way latch.
    /// </summary>
    public static bool TeardownActive => Volatile.Read(ref _teardownDepth) > 0;

    public static void Initialize()
    {
        if (Interlocked.CompareExchange(ref _installed, 1, 0) != 0)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            Volatile.Write(ref _installed, 0);
            RynthLog.Compat("DbCacheTeardownHooks: acclient.exe not available.");
            return;
        }

        var resolved = HookResolver.Resolve(
            textSection, "DbCacheTeardown.DestroyObjectCaches",
            DestroyObjectCachesPattern, DestroyObjectCachesFallbackVa);
        if (!resolved.Success)
        {
            Volatile.Write(ref _installed, 0);
            return;
        }

        try
        {
            _targetAddress = resolved.Address;
            _destroyObjectCachesDetour = DestroyObjectCachesDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_destroyObjectCachesDetour);
            _originalDestroyObjectCaches = Marshal.GetDelegateForFunctionPointer<ThisCallVoidDelegate>(
                MinHook.HookCreate(_targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(_targetAddress);
            RynthLog.Compat($"DbCacheTeardownHooks: ready (DestroyObjectCaches=0x{_targetAddress.ToInt32():X8}).");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _installed, 0);
            RynthLog.Compat($"DbCacheTeardownHooks: install threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void DestroyObjectCachesDetour(IntPtr thisPtr)
    {
        // SCOPED read-quiesce. Raise the latch BEFORE AC frees anything; lower it when
        // AC's teardown loop returns. While raised, EnchantmentHooks (the off-thread
        // registry walker) bails, so the pump can't read a node AC is concurrently
        // freeing -> kills the 0x00416C86 (DBOCache::DestroyObj) AV. NO pump-stop / NO
        // engine shutdown here: DestroyObjectCaches ALSO fires at world-load/zone, and
        // destructive work tore the engine down mid-play (2026-06-02).
        Interlocked.Increment(ref _teardownDepth);
        try
        {
            if (Interlocked.Increment(ref _enterLogCount) <= 3)
                RynthLog.Compat("DbCacheTeardownHooks: DestroyObjectCaches entry — off-thread registry reads quiesced for this teardown.");
            _originalDestroyObjectCaches!(thisPtr);
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"DbCacheTeardownHooks: original threw {ex.GetType().Name}: {ex.Message}"); } catch { }
        }
        finally
        {
            Interlocked.Decrement(ref _teardownDepth);
        }
    }
}

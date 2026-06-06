using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Hooks AC's top-level per-frame game-logic tick, <c>Client::UseTime</c>
/// (acclient.map 0x00010FA0 + 0x401000 = 0x00411FA0; ImageBase 0x00400000, ASLR
/// off), to drain the marshalled CAST queue on AC's MAIN thread IN THE GAME-LOGIC
/// PHASE.
///
/// Why this tick (not EndScene): a cast must run where AC processes player actions
/// and advances its cast state machine. EndScene is the RENDER phase — marshalling
/// the cast there fired IncrementBusyCount but never completed the cast (proven
/// dead-end, reverted 2026-05-19: no projectile/damage, busy leaked, cast slot
/// jammed). Client::UseTime IS the game-logic phase, so the cast completes there.
///
/// Why marshal casts at all: CastSpell is the LAST off-thread AC mutator. Run off
/// the pump thread it mutates AC's single-threaded object/animation/enchant graph
/// and corrupts it; AC then AVs LATER in its own per-tick code — CSequence::
/// update_internal (animation, captured 2026-06-03), 0x67E779 (classify), 0x0055FA24
/// (range-list). Running the cast here, on AC's own game thread, removes that race
/// at the source — the root fix for the off-thread object-graph corruption class.
///
/// Thiscall detour, same proven pattern as DbCacheTeardownHooks / LogoutLifecycleHooks.
/// DrainCasts early-returns when no cast is pending, so the detour is alloc-free in
/// the common per-frame case — the same safe profile as the existing EndScene detour.
/// </summary>
internal static class GameTickHooks
{
    private const int UseTimeVa = 0x00411FA0;   // Client::UseTime — thiscall, returns bool in AL

    // Client::UseTime returns a bool (AL): AC's main loop drives it as
    //   do { } while ( UseTime() != 0 );
    // so the return value MUST be propagated. A void delegate discards AC's AL
    // and the loop then reads whatever the managed epilogue left in EAX — when
    // that is nonzero the loop never exits and the client hard-freezes (main
    // thread spins in UseTime pumping messages but never rendering; bot threads
    // keep running). Dump-confirmed root cause, 2026-06-05.
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int ThisCallBoolDelegate(IntPtr thisPtr);

    private static ThisCallBoolDelegate? _originalUseTime;
    private static ThisCallBoolDelegate? _useTimeDetour;
    private static IntPtr _targetAddress;
    private static int _installed;

    // Verified unique + lands exactly at UseTimeVa offline (tools/pe_pattern.py).
    // Replaces the prior warn-only prologue check; null = wildcard (rel32 call operand).
    private static readonly byte?[] UseTimePattern = [ 0x56, 0x8B, 0xF1, 0xE8, null, null, null, null, 0xE8, null, null, null, null, 0x84, 0xC0, 0x74 ];

    public static bool IsInstalled => Volatile.Read(ref _installed) != 0;

    public static void Initialize()
    {
        if (Interlocked.CompareExchange(ref _installed, 1, 0) != 0)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            Volatile.Write(ref _installed, 0);
            RynthLog.Compat("GameTickHooks: acclient.exe not available.");
            return;
        }

        HookResolver.ResolveResult resolved = HookResolver.Resolve(textSection, "GameTick.UseTime", UseTimePattern, UseTimeVa);
        if (!resolved.Success)
        {
            Volatile.Write(ref _installed, 0);
            RynthLog.Compat($"GameTickHooks: Client::UseTime unresolved (VA 0x{UseTimeVa:X8}) — cast marshalling NOT installed.");
            return;
        }

        try
        {
            _targetAddress = resolved.Address;
            _useTimeDetour = UseTimeDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_useTimeDetour);
            _originalUseTime = Marshal.GetDelegateForFunctionPointer<ThisCallBoolDelegate>(
                MinHook.HookCreate(_targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(_targetAddress);
            Volatile.Write(ref _installed, 1);
            RynthLog.Compat($"GameTickHooks: Client::UseTime hooked @ 0x{_targetAddress.ToInt32():X8} ({resolved.Detail}) — cast drain on game-logic tick.");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _installed, 0);
            RynthLog.Compat($"GameTickHooks: install threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int UseTimeDetour(IntPtr thisPtr)
    {
        // Drain any queued cast BEFORE AC's game-logic tick: SelectItem sets AC's
        // selection and the cast initiates, then THIS SAME UseTime call processes it to
        // completion — mimicking AC's natural input -> UseTime flow. (Draining AFTER the
        // tick landed self-buffs but not targeted casts: the selection set at the end of
        // tick N was clobbered before tick N+1 read it.) Alloc-free when no cast is
        // pending. Must never throw into AC's native frame.
        try { AcMainThreadQueue.DrainCasts(); }
        catch { }
        // MUST return Client::UseTime's own bool — AC loops do/while on it.
        try { return _originalUseTime!(thisPtr); }
        catch { return 0; }
    }
}

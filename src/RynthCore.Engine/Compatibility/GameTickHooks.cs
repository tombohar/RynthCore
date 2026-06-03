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
    private const int UseTimeVa = 0x00411FA0;   // Client::UseTime(void) — thiscall

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void ThisCallVoidDelegate(IntPtr thisPtr);

    private static ThisCallVoidDelegate? _originalUseTime;
    private static ThisCallVoidDelegate? _useTimeDetour;
    private static IntPtr _targetAddress;
    private static int _installed;

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

        int funcOff = UseTimeVa - textSection.TextBaseVa;
        if (funcOff < 0 || funcOff + 16 > textSection.Bytes.Length)
        {
            Volatile.Write(ref _installed, 0);
            RynthLog.Compat($"GameTickHooks: Client::UseTime VA 0x{UseTimeVa:X8} out of .text range — not installed.");
            return;
        }

        // Plausibility check (warn-only, like CastSpell): byte-identical binary + ASLR
        // off, so the map VA is valid; log the prologue for post-hoc verification.
        byte b0 = textSection.Bytes[funcOff];
        byte b1 = textSection.Bytes[funcOff + 1];
        bool plausible = b0 == 0x55 || b0 == 0x53 || b0 == 0x56 || b0 == 0x57 ||
                         b0 == 0x6A || b0 == 0x68 || b0 == 0xA1 || b0 == 0x8B ||
                         (b0 == 0x83 && b1 == 0xEC) || (b0 == 0x81 && b1 == 0xEC);
        string prologue = Convert.ToHexString(textSection.Bytes, funcOff, 16);

        try
        {
            _targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _useTimeDetour = UseTimeDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_useTimeDetour);
            _originalUseTime = Marshal.GetDelegateForFunctionPointer<ThisCallVoidDelegate>(
                MinHook.HookCreate(_targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(_targetAddress);
            Volatile.Write(ref _installed, 1);
            RynthLog.Compat($"GameTickHooks: Client::UseTime hooked @ 0x{_targetAddress.ToInt32():X8} " +
                            $"(prologue {prologue}, plausible={plausible}) — cast drain on game-logic tick.");
            if (!plausible)
                RynthLog.Compat("GameTickHooks: WARNING Client::UseTime prologue atypical — verify the VA before trusting cast marshalling.");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _installed, 0);
            RynthLog.Compat($"GameTickHooks: install threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void UseTimeDetour(IntPtr thisPtr)
    {
        // Drain any queued cast BEFORE AC's game-logic tick: SelectItem sets AC's
        // selection and the cast initiates, then THIS SAME UseTime call processes it to
        // completion — mimicking AC's natural input -> UseTime flow. (Draining AFTER the
        // tick landed self-buffs but not targeted casts: the selection set at the end of
        // tick N was clobbered before tick N+1 read it.) Alloc-free when no cast is
        // pending. Must never throw into AC's native frame.
        try { AcMainThreadQueue.DrainCasts(); }
        catch { }
        try { _originalUseTime!(thisPtr); }
        catch { }
    }
}

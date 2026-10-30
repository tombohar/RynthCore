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
    /// <summary>Set once the Decal-coexistence plugin pump starts (no EndScene hook in that
    /// client): the UseTime detour then also runs EngineFrameController.RunHeadlessPrefetch.
    /// Never set in a client without Decal.</summary>
    internal static volatile bool HeadlessPrefetch;

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
    private static IntPtr _originalUseTimePtr;
    private static IntPtr _targetAddress;
    private static int _installed;
    private static int _exitLatch;

    // Native SEH guard around AC's tick (SehTrampoline v4+). A native AV that unwinds into
    // this managed detour is fatal under CoreCLR; caught natively, it drops one tick instead.
    // More than MaxTickFaults within TickFaultWindowMs means AC's state is broken rather than
    // one bad message, so the guard turns itself off and the next fault ends the client.
    private const int MaxTickFaults = 5;
    private const long TickFaultWindowMs = 30_000;
    private static bool _tickGuard;
    private static int _tickFaults;
    private static long _tickFaultWindowStart;

    // Verified unique + lands exactly at UseTimeVa offline (tools/pe_pattern.py).
    // Replaces the prior warn-only prologue check; null = wildcard (rel32 call operand).
    private static readonly byte?[] UseTimePattern = [ 0x56, 0x8B, 0xF1, 0xE8, null, null, null, null, 0xE8, null, null, null, null, 0x84, 0xC0, 0x74 ];

    public static bool IsInstalled => Volatile.Read(ref _installed) != 0;

    /// <summary>Client::UseTime returned 0: AC's game loop is exiting (any exit path).</summary>
    public static bool GameLoopExiting => Volatile.Read(ref _exitLatch) != 0;

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
            _originalUseTimePtr = MinHook.HookCreate(_targetAddress, detourPtr);
            _originalUseTime = Marshal.GetDelegateForFunctionPointer<ThisCallBoolDelegate>(_originalUseTimePtr);
            _tickGuard = SehTrampoline.IsAvailable && SehTrampoline.NativeVersion >= 4;
            Thread.MemoryBarrier();
            MinHook.Enable(_targetAddress);
            Volatile.Write(ref _installed, 1);
            RynthLog.Compat($"GameTickHooks: Client::UseTime hooked @ 0x{_targetAddress.ToInt32():X8} ({resolved.Detail}) — cast drain on game-logic tick; " +
                (_tickGuard ? "native AV guard on." : $"native AV guard OFF (SehTrampoline v{SehTrampoline.NativeVersion}, needs v4)."));
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _installed, 0);
            RynthLog.Compat($"GameTickHooks: install threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int UseTimeDetour(IntPtr thisPtr)
    {
        // Hang-watchdog beat from the game-logic tick (no frame count): the
        // EndScene beat alone left the watchdog blind to a main thread that
        // wedges before the new generation's EndScene hook installs.
        try { MainThreadHangWatchdog.MainThreadBeatNoFrame(); }
        catch { }
        // Diag (diag/logoff-origin): 1 Hz sample of the client's own input-idle clock.
        try { LogoffOriginProbe.SampleIdleOnGameThread(); }
        catch { }
        // Refresh TeleportStateHooks' main-thread cache every real game tick. This
        // detour (unlike EndScene) fires on AC's true main thread in EVERY mode
        // including Decal-coexistence (no D3D9 hook there) — without this,
        // off-thread callers of IsPortaling in coexistence mode would see a
        // perpetually stale cache since nothing else ever reads it on-thread.
        // The getter itself does the guarded read + cache write; discard the value.
        bool portaling = false;
        try { portaling = TeleportStateHooks.IsPortaling; }
        catch { }
        // Death / portal space / teleport lines, for lining crashes up with what
        // the character was doing (PlayerLifecycleLog). Also the world-transition state the
        // AcMainThreadQueue drains gate on: sampled BEFORE the cast drain below, so a cast queued
        // as the player dies is held in this same tick, not run into the death.
        try { PlayerLifecycleLog.SampleOnGameThread(portaling); }
        catch { }

        // Drain any queued cast BEFORE AC's game-logic tick: SelectItem sets AC's
        // selection and the cast initiates, then THIS SAME UseTime call processes it to
        // completion — mimicking AC's natural input -> UseTime flow. (Draining AFTER the
        // tick landed self-buffs but not targeted casts: the selection set at the end of
        // tick N was clobbered before tick N+1 read it.) Alloc-free when no cast is
        // pending. Must never throw into AC's native frame.
        try { AcMainThreadQueue.DrainCasts(); }
        catch { }
        // Shortcut slots used or changed from RynthCore (AcShortcuts), the same moment AC's own
        // keys reach the toolbar: before this tick. Alloc-free when nothing is queued.
        try { AcShortcuts.Drain(); }
        catch { }

        // Run AC's own game-logic + physics + animation pass.
        int result;
        if (_tickGuard)
        {
            result = SehTrampoline.GameTickThiscall(_originalUseTimePtr, thisPtr, out bool avCaught,
                out uint exAddr, out uint dataAddr, out string module);
            // 0 would end AC's game loop; a dropped tick must keep it running.
            if (avCaught)
                result = OnTickFault(exAddr, dataAddr, module);
        }
        else
        {
            try { result = _originalUseTime!(thisPtr); }
            catch { result = 0; }
        }

        // ── Game loop is exiting — quiesce BEFORE AC's teardown ──────────────
        // AC's main loop is `do { } while (UseTime() != 0)`; a 0 return means
        // this was the LAST tick and AC tears its world down next (DBCache::
        // DestroyObjectCaches frees every DBObj). This is the only close signal
        // that covers ALL exit paths: the in-game exit / logout-quit flow never
        // delivers WM_CLOSE, so the Win32Backend/AvaloniaOverlay WM_CLOSE
        // quiesce never ran and the ~63 Hz pump kept firing Move/SetAutoRun/
        // object reads into freed objects -> the on-close AV at acclient
        // 0x00416C86 (DBOCache::DestroyObj reading [null+0x28]). We are still
        // INSIDE the final UseTime call here — AC objects are all valid — so
        // the pump's in-flight frame finishes safely during the bounded join.
        if (result == 0 && Interlocked.Exchange(ref _exitLatch, 1) == 0)
        {
            try { RynthLog.Compat("GameTickHooks: Client::UseTime returned 0 — game loop exiting; quiescing plugin pump + late input before AC teardown."); }
            catch { }
            // Drop the busy watchdog's cached ClientUISystem ptr (freed next).
            try { BusyCountHooks.ResetSession(); }
            catch { }
            // Arm the late mouse/cursor swallow in BOTH game-window subclasses
            // (the 0x0056547B UpdateCursorState close-AV trigger) — WM_CLOSE
            // never fired on this path, so neither latch is set yet.
            try { ImGuiBackend.Win32Backend.MarkCloseInFlight(); }
            catch { }
            try { UI.RynthOverlayWindow.MarkCloseInFlight(); }
            catch { }
            try { EntryPoint.StopTickPumpAndJoin(); }
            catch { }
            // Skip the post-tick drains/watchdog below — no engine-issued
            // mutation may run once the loop has decided to exit.
            return 0;
        }

        // Drain the marshalled-action ring AFTER AC's tick, NOT before.
        // ⚠ Issuing a UseObject (corpse open) / movement / stance change starts
        // or REPLACES the player's CSequence motion — and AC's animation update
        // (CPhysicsObj::update_object -> CSequence::update_internal ->
        // AnimSequenceNode::get_high_frame) runs inside _originalUseTime above.
        // Draining BEFORE that call meant the SAME tick walked the sequence we
        // had just perturbed, hitting a freed/null anim node -> hard AV at
        // acclient 0x5263xx/0x5264xx (null+0xC). Live-diagnosed 2026-06-11
        // during a rapid corpse-open grind (AC-only crash stack, no engine
        // frames). Draining after lets AC install the new gesture's node and
        // walk it cleanly on its NEXT tick. The EndScene drain (the other call
        // site) is already post-physics in the frame, so it was never the
        // trigger. (Casts stay PRE-tick above — their selection-timing
        // requirement, and they go through the separate cast slot.)
        // Re-sample the world-transition state first: a death, a portal entry or a
        // landblock jump can begin INSIDE AC's tick above, and the pre-tick sample would
        // let this drain run queued work into it. Edges are logged once whichever sample
        // sees them.
        try
        {
            bool portalingAfter = false;
            try { portalingAfter = TeleportStateHooks.IsPortaling; }
            catch { }
            PlayerLifecycleLog.SampleOnGameThread(portalingAfter);
        }
        catch { }
        try { AcMainThreadQueue.Drain(); }
        catch { }
        // Main-thread snapshots the pump reads instead of AC memory (player id,
        // cast gate, pose, ...). Post-tick, so they reflect this tick's physics.
        try { MainThreadSnapshots.Tick(); }
        catch { }
        try { AcShortcuts.ReadTick(); }
        catch { }
        try { AcKeymapLive.Tick(); }
        catch { }
        try { AcPanels.Drain(); }
        catch { }
        // Decal clients only (no EndScene hook): the prefetches EngineFrameController
        // runs per frame. False for every other client - one flag check.
        if (HeadlessPrefetch)
        {
            try { ImGuiBackend.EngineFrameController.RunHeadlessPrefetch(); }
            catch { }
        }
        // Busy watchdog: also post-tick now. Its decrement / cursor-refresh
        // touch m_cBusy, not the motion sequence, so order is immaterial here;
        // keeping all engine-issued mutations after AC's pass is the invariant.
        try { BusyCountHooks.CheckWatchdog(); }
        catch { }
        // Flight recorder: per-second summary, stall detectors, scheduled automatic dump.
        try { Net.FlightRecorder.MainTick(); }
        catch { }
        // AC's "server has disconnected" screen: close the client after a short wait so a
        // launcher can relaunch it (EngineSettings.CloseOnServerDisconnect).
        try { ServerDisconnectClose.MainTick(); }
        catch { }

        // MUST return Client::UseTime's own bool — AC loops do/while on it.
        return result;
    }

    /// <summary>
    /// Logs a native AV caught inside Client::UseTime and returns the value the detour hands
    /// back to AC's loop (always 1: keep running). Turns the guard off once faults exceed
    /// the budget, so a client with corrupted state still dies instead of spinning.
    /// </summary>
    private static int OnTickFault(uint exAddr, uint dataAddr, string module)
    {
        long now = Environment.TickCount64;
        if (_tickFaults == 0 || now - _tickFaultWindowStart > TickFaultWindowMs)
        {
            _tickFaultWindowStart = now;
            _tickFaults = 0;
        }
        _tickFaults++;

        string where = module.Length > 0 ? $"{module}+0x{exAddr:X}" : $"0x{exAddr:X8}";
        try
        {
            RynthLog.Error($"GameTickHooks: access violation inside Client::UseTime at {where} (data 0x{dataAddr:X8}); " +
                $"tick dropped, client kept running (fault {_tickFaults}/{MaxTickFaults} in {TickFaultWindowMs / 1000}s). Full record in native-crash.log.");
            if (_tickFaults >= MaxTickFaults)
            {
                _tickGuard = false;
                RynthLog.Error("GameTickHooks: Client::UseTime fault budget exhausted; native AV guard OFF for this session.");
            }
        }
        catch { }
        return 1;
    }
}

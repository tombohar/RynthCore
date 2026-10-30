using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class BusyCountHooks
{
    // Fallback VAs (4,841,472-byte client). Pattern-scan is now the source of truth.
    private const int IncrementBusyCountFallbackVa = 0x00565610;
    private const int DecrementBusyCountFallbackVa = 0x00565630;
    private const int UpdateCursorStateFallbackVa  = 0x005653D0;

    // ClientUISystem struct field offset for m_cBusy (confirmed via runtime dump)
    private const int OffsetMCBusy = 0x14;

    // ClientUISystem::IncrementBusyCount — entire function is short:
    //   mov edx,[ecx+14]; inc edx; mov eax,edx; cmp eax,1; mov [ecx+14],edx;
    //   jne +5; jmp UpdateCursorState; ret
    private static readonly byte?[] IncrementBusyCountPattern =
    [
        0x8B, 0x51, 0x14, 0x42, 0x8B, 0xC2, 0x83, 0xF8,
        0x01, 0x89, 0x51, 0x14, 0x75, 0x05,
        0xE9, null, null, null, null,        // jmp rel32 -> UpdateCursorState
        0xC3
    ];

    // ClientUISystem::DecrementBusyCount — also short:
    //   dec [ecx+14]; jne +5; jmp UpdateCursorState; ret
    private static readonly byte?[] DecrementBusyCountPattern =
    [
        0xFF, 0x49, 0x14, 0x75, 0x05,
        0xE9, null, null, null, null,        // jmp rel32 -> UpdateCursorState
        0xC3
    ];

    // ClientUISystem::UpdateCursorState — large prologue with cmp/setcc pair.
    private static readonly byte?[] UpdateCursorStatePattern =
    [
        0x83, 0xEC, 0x08, 0x53, 0x55, 0x56, 0x57, 0x89,
        0x4C, 0x24, 0x10,
        0xE8, null, null, null, null,        // call rel32
        0x85, 0xC0, 0x0F, 0x95, 0xC3, 0x33, 0xC0, 0x84,
        0xDB
    ];

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
    private static IntPtr _updateCursorStateAddress;
    private static string _statusMessage = "Not probed yet.";
    private static int _incrementDispatchCount;
    private static int _decrementDispatchCount;
    private static int _netBusyCount;
    private static IntPtr _lastThisPtr;

    public static bool IsInstalled { get; private set; }
    public static string StatusMessage => _statusMessage;

    /// <summary>Returns 0 if the character is idle, positive if a UI action is in progress.</summary>
    public static int GetBusyState() => Math.Max(0, _netBusyCount);

    /// <summary>
    /// Force-reset the client's busy count to zero and re-evaluate the cursor.
    /// <paramref name="clearMotion"/> additionally flushes the command
    /// interpreter (ClearAllCommands/TakeControlFromServer/PlayerTeleported) —
    /// ⚠ DANGEROUS and OFF by default: AC only runs that triplet inside full
    /// teleport flows that also reset the physics sequence. Fired standalone
    /// around a live gesture it can leave the player's CSequence with a null
    /// current anim node → AC's next animation tick AVs at acclient 0x5264A9
    /// reading null+0xC (CSequence::update_internal). Diagnosed live
    /// 2026-06-11: the loot busy-watchdog called this every ~2s for an hour
    /// (every corpse open leaks +1 m_cBusy) and one clear landed in the
    /// gesture window. The busy UNSTICK itself only needs the decrement loop +
    /// cursor refresh below. Even when requested, the motion flush is skipped
    /// while a cast/action gesture is animating.
    /// </summary>
    // Session counters surfaced in the heartbeat so a soak shows the busy-fix
    // machinery working: fcl = force-clears performed, rec = cast/item-action
    // reconciles performed. A healthy post-fix soak should see rec climb during
    // combat/looting and fcl stay near-flat (force-clear is now just a backstop).
    private static long _forceClearCount;
    private static long _reconcileCount;
    public static long ForceClearCount => Interlocked.Read(ref _forceClearCount);
    public static long ReconcileCount => Interlocked.Read(ref _reconcileCount);

    // Off-thread force-reset requests latch here and are consumed by
    // CheckWatchdog on the main thread (post-tick). ≤1 tick latency —
    // irrelevant for a 2-5s watchdog; coalesces bursts for free.
    private static int _forceResetRequested;
    private static string _forceResetRequestedWhy = "";

    /// <summary>
    /// The engine count to 0, and the plugins told: one busy-decremented event per unit dropped
    /// (2026-10-06). ForceResetBusyCount's catch-up decrements go through the trampoline (no
    /// detour, no event) and the resyncs below wrote the count straight, so the plugins' mirrors
    /// (RynthAi's _busyCount, which gates buffs, combat and corpse opens) stayed up and their own
    /// watchdogs fired a second reset 5-10 s later (Silentkelpie's analysis; verified).
    /// </summary>
    private static void DropShadowTo0()
    {
        int was = Interlocked.Exchange(ref _netBusyCount, 0);
        for (int i = 0; i < was && i < 64; i++)
            PluginManager.QueueBusyCountDecremented();
    }

    private static int ReadRealBusySigned() => TryReadRealBusy(out int v) ? v : int.MinValue;

    public static void ForceResetBusyCount(bool clearMotion = false, string why = "plugin request (host ForceResetBusyCount)")
    {
        // ⚠ MAIN THREAD ONLY (2026-06-12 cross-incident forensics, top-ranked
        // corruption writer): the plugin host API called this raw from the
        // pump thread 284-471×/session — up to 20 native DecrementBusyCount
        // calls (each falling through into UpdateCursorState's UI/object-graph
        // walk) plus a raw m_cBusy write, concurrent with AC's main thread.
        // Leading suspect for the LFH heap detonation (ntdll+0x87D99, free()
        // during object teardown, last off-thread clear ≤1s before the AV).
        // Off-thread callers latch a request consumed post-tick instead.
        if (!MainThreadGuard.IsOnMainThread())
        {
            _forceResetRequestedWhy = why;
            Interlocked.Exchange(ref _forceResetRequested, 1);
            Net.FlightRecorder.Mark(Net.FlightMark.BusyForceClearAsk, text: "off the game thread: latched for the next tick");
            return;
        }

        if (!IsInstalled || _lastThisPtr == IntPtr.Zero)
            return;
        Interlocked.Increment(ref _forceClearCount);

        // Never mutate through a stale pointer: after logout/close AC frees the
        // ClientUISystem singleton, and the decrement/cursor calls below would
        // run native code against freed memory (uncatchable under NativeAOT).
        if (!ClientObjectHooks.IsReadablePointer(_lastThisPtr + OffsetMCBusy))
        {
            RynthLog.Compat("BusyCountHooks: ForceResetBusyCount skipped — cached ClientUISystem pointer is no longer readable.");
            _lastThisPtr = IntPtr.Zero;
            return;
        }

        int was = _netBusyCount;
        // Every reset is logged with who asked (2026-10-06; Silentkelpie's analysis counted 38
        // silent "(was real 0, shadow 0)" resets in 15 minutes).
        try { RynthLog.Compat($"BusyCountHooks: force-reset by {why}: real {ReadRealBusySigned()}, engine count {was} -> 0."); } catch { }

        // Decrement exactly what the REAL field holds, never past zero. This used
        // to call the original decrement max(shadow, 3) times: with the real field
        // already 0 (AC's UseDone decrements it inline, which the shadow never
        // sees) that walked it to -1..-3 before the write below put it back.
        // m_cBusy only drives the cursor (UpdateCursorState: != 0 = busy cursor),
        // so this was never the item-action lock, but there is no reason to
        // write below zero. See ClientActionGates for the gates AC really checks.
        int real = Marshal.ReadInt32(_lastThisPtr + OffsetMCBusy);
        Net.FlightRecorder.Mark(Net.FlightMark.BusyForceClear, unchecked((uint)real), unchecked((uint)was),
            text: clearMotion ? "with the motion flush" : "");
        if (_originalDecrementBusyCount != null && real > 0)
        {
            for (int i = 0; i < real && i < 20; i++)
                _originalDecrementBusyCount(_lastThisPtr);
        }

        DropShadowTo0();
        // Nothing of the old count is left for the reconciler (a late UseDone just finds 0).
        lock (_directLock)
        {
            _directPending.Clear();
            _directLeak = 0;
            _pendingCastBusyDelta = 0;
        }

        try { Marshal.WriteInt32(_lastThisPtr + OffsetMCBusy, 0); }
        catch { /* non-fatal */ }

        if (clearMotion)
        {
            bool gesture = false;
            try { if (!PlayerPhysicsHooks.TryGetCastGestureInProgress(out gesture)) gesture = false; }
            catch { gesture = false; }
            if (gesture)
            {
                RynthLog.Compat("BusyCountHooks: motion flush SKIPPED — a gesture is animating (the CSequence-corruption window).");
            }
            else
            {
                CommandInterpreterHooks.ClearAllCommands();
                CommandInterpreterHooks.TakeControlFromServer();
                CommandInterpreterHooks.PlayerTeleported();
            }
        }

        if (_updateCursorStateAddress != IntPtr.Zero)
        {
            try
            {
                var updateCursor = Marshal.GetDelegateForFunctionPointer<UpdateCursorStateDelegate>(_updateCursorStateAddress);
                updateCursor(_lastThisPtr);
            }
            catch { /* non-fatal */ }
        }

        RynthLog.Verbose($"Compat: force-reset busy count (was real {real}, shadow {was})");
    }

    // ── Negative m_cBusy repair ─────────────────────────────────────────────
    // AC never takes m_cBusy below what it added; only a zeroing of ours can:
    // a force-clear (or the direct-action reconciler) zeroes a count whose
    // action is still in flight, then the server's UseDone decrements it
    // inline (Handle_Item::UseDone, not through DecrementBusyCount) to -1.
    // The real-field watchdog only looks at > 0, so the negative value stayed
    // for the session: a permanent busy cursor. Repair it after a short grace.
    private static long _realBusyNegativeSince;
    private static long _negativeRepairCount;
    private const long NegativeBusyRepairMs = 2_000;
    public static long NegativeRepairCount => Interlocked.Read(ref _negativeRepairCount);
    public static int ShadowBusyCount => Volatile.Read(ref _netBusyCount);
    public static int PendingReconcileDelta => _pendingCastBusyDelta;

    /// <summary>Read AC's real m_cBusy. False when the ClientUISystem isn't known/readable.</summary>
    public static bool TryReadRealBusy(out int value)
    {
        value = 0;
        IntPtr p = _lastThisPtr;
        if (p == IntPtr.Zero || !ClientObjectHooks.IsReadablePointer(p + OffsetMCBusy))
            return false;
        try { value = Marshal.ReadInt32(p + OffsetMCBusy); return true; }
        catch { return false; }
    }

    /// <summary>
    /// If AC's m_cBusy is negative, set it to 0 and refresh the cursor. Main
    /// thread only (returns false elsewhere). <paramref name="was"/> is the
    /// value found.
    /// </summary>
    public static bool RepairNegativeBusy(out int was)
    {
        was = 0;
        if (!MainThreadGuard.IsOnMainThread())
            return false;
        IntPtr p = _lastThisPtr;
        if (!TryReadRealBusy(out was) || was >= 0)
            return false;
        if (!ClientObjectHooks.IsWritablePointer(p + OffsetMCBusy))
            return false;

        Marshal.WriteInt32(p + OffsetMCBusy, 0);
        DropShadowTo0();
        Interlocked.Increment(ref _negativeRepairCount);
        Net.FlightRecorder.Mark(Net.FlightMark.BusyNegativeRepair, unchecked((uint)was));
        if (_updateCursorStateAddress != IntPtr.Zero)
        {
            try
            {
                var updateCursor = Marshal.GetDelegateForFunctionPointer<UpdateCursorStateDelegate>(_updateCursorStateAddress);
                updateCursor(p);
            }
            catch { /* non-fatal */ }
        }
        return true;
    }

    public static void Initialize()
    {
        // The item-action gates (pending request / attacking flag) resolve on
        // their own; they don't need the busy-count hooks below.
        try { ClientActionGates.Initialize(); }
        catch (Exception ex) { RynthLog.Compat($"ClientActionGates: init threw {ex.GetType().Name}: {ex.Message}"); }

        if (IsInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        var inc = HookResolver.Resolve(textSection, "BusyCountHooks.IncrementBusyCount",
            IncrementBusyCountPattern, IncrementBusyCountFallbackVa);
        var dec = HookResolver.Resolve(textSection, "BusyCountHooks.DecrementBusyCount",
            DecrementBusyCountPattern, DecrementBusyCountFallbackVa);
        var cursor = HookResolver.Resolve(textSection, "BusyCountHooks.UpdateCursorState",
            UpdateCursorStatePattern, UpdateCursorStateFallbackVa);

        if (cursor.Success) _updateCursorStateAddress = cursor.Address;

        if (!inc.Success || !dec.Success)
        {
            _statusMessage = $"Resolve failed — increment={inc.Detail}, decrement={dec.Detail}.";
            RynthLog.Compat($"BusyCountHooks: {_statusMessage}");
            return;
        }

        try
        {
            _incrementTargetAddress = inc.Address;
            _incrementBusyCountDetour = IncrementBusyCountDetour;
            IntPtr incrementDetourPtr = Marshal.GetFunctionPointerForDelegate(_incrementBusyCountDetour);
            _originalIncrementBusyCount = Marshal.GetDelegateForFunctionPointer<BusyCountDelegate>(
                MinHook.HookCreate(_incrementTargetAddress, incrementDetourPtr));

            _decrementTargetAddress = dec.Address;
            _decrementBusyCountDetour = DecrementBusyCountDetour;
            IntPtr decrementDetourPtr = Marshal.GetFunctionPointerForDelegate(_decrementBusyCountDetour);
            _originalDecrementBusyCount = Marshal.GetDelegateForFunctionPointer<BusyCountDelegate>(
                MinHook.HookCreate(_decrementTargetAddress, decrementDetourPtr));

            Thread.MemoryBarrier();
            MinHook.Enable(_incrementTargetAddress);
            MinHook.Enable(_decrementTargetAddress);

            IsInstalled = true;
            _statusMessage = $"Hooked busy-count seams (inc=0x{_incrementTargetAddress.ToInt32():X8}, dec=0x{_decrementTargetAddress.ToInt32():X8}).";
            RynthLog.Compat($"BusyCountHooks: hooks installed.");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"BusyCountHooks: install threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int _incFires, _decFires;

    // ── Auto-watchdog: force-clear busy if it stays >0 too long ──────────
    // Engine-side safety net so the bot self-heals when its busy tracking
    // desyncs from AC's m_cBusy (e.g., a hook miss, RDP-eaten chat input
    // preventing the user from running /ra clearbusy manually, or AC's
    // own busy state going stuck after a server-rejected action).
    //
    // Trigger: any of our main-thread detours can call CheckWatchdog().
    // SmartBoxHooks.DispatchGameEventDetour wires this in — it fires
    // frequently on AC's main thread while in-world, so the watchdog
    // gets a tick at least every server event.
    //
    // Heuristic: if busy has been positive for > BusyWatchdogTimeoutMs
    // with no transitions back to zero, fire ForceResetBusyCount. We
    // do this regardless of why it got stuck — combat-urgent or not —
    // because the alternative is an inert bot.
    private static long _busyBecamePositiveTickMs;
    private static long _lastWatchdogFireTickMs;
    private const long BusyWatchdogTimeoutMs = 5_000;
    private const long BusyWatchdogCooldownMs = 2_000;

    // Real-field watchdog. The shadow counter (_netBusyCount) DESYNCS from AC's
    // real m_cBusy whenever AC mutates the field through a path we don't hook (or
    // a detour is missed): the counter drifts to ~0 while the real field stays
    // pinned > 0. A pinned real m_cBusy makes AC LOCALLY refuse combat-mode
    // changes (its SetCombatMode never even fires) and casts ("You're too
    // busy!") — wedging the bot in NonCombat (can't re-enter Magic to buff/fight)
    // with NEITHER counter-based watchdog firing, because the counter says idle.
    // So the watchdog must also trust the REAL field, read straight from
    // [ClientUISystem + OffsetMCBusy]. (Diagnosed 2026-06-09: SetCombatMode
    // stopped firing for 30+ min while ChangeCombatMode was re-sent and 191
    // "too busy" refusals piled up, yet zero force-clears — classic desync.)
    private static long _realBusyPositiveTickMs;
    // 2026-10-06: was 4 s with no other condition, which zeroed the count of actions the
    // server had not answered yet (a corpse walk, a 2.4 s kit, a 3.5 s vital cast) many times
    // an hour. m_cBusy only drives the cursor (see ClientActionGates), so this is a cursor
    // clean-up and runs only once the server has demonstrably gone quiet: the count positive
    // for RealBusyWatchdogTimeoutMs AND no UseDone at all for RealBusyServerSilentMs AND no
    // cast gesture animating.
    private const long RealBusyWatchdogTimeoutMs = 12_000;
    private const long RealBusyServerSilentMs = 12_000;

    private static int ReadRealBusy()
    {
        IntPtr p = _lastThisPtr;
        if (p == IntPtr.Zero) return -1;
        // The try/catch below cannot catch a non-null-page AV under NativeAOT
        // (only null-page faults map to NullReferenceException). If AC freed
        // the ClientUISystem singleton (logout/close teardown) a stale pointer
        // read is fail-fast — probe the page first. TOCTOU-narrow but this is
        // a watchdog: skipping one beat is free.
        if (!ClientObjectHooks.IsReadablePointer(p + OffsetMCBusy)) return -1;
        try { return Marshal.ReadInt32(p + OffsetMCBusy); }
        catch { return -1; }
    }

    /// <summary>
    /// Drop the cached ClientUISystem pointer at logout / client-close so the
    /// watchdog and ForceResetBusyCount can't touch a freed singleton (the
    /// 0x0056547B close-AV class). The increment/decrement detours re-capture
    /// it on the next legit AC call.
    /// </summary>
    public static void ResetSession()
    {
        _lastThisPtr = IntPtr.Zero;
        // The next session's ClientUISystem starts at 0, and so does our copy (2026-10-28). Kept,
        // a count left up by a logout mid-action stayed up for good: the watchdog can't clear it
        // without the pointer, switches itself off, and is armed again only by a 0 -> 1 step, so
        // GetBusyState answered "busy" until the client restarted (the item bar refused every use
        // after a relog; RynthAi's waiting-on-the-server test reads the same count).
        Interlocked.Exchange(ref _netBusyCount, 0);
        Volatile.Write(ref _busyBecamePositiveTickMs, 0);
        Volatile.Write(ref _realBusyPositiveTickMs, 0);
        _realBusyNegativeSince = 0;
        lock (_directLock)
        {
            _pendingCastBusyDelta = 0;
            _directPending.Clear();
            _directLeak = 0;
        }
        Volatile.Write(ref _lastUseDoneTickMs, 0);
        try { ClientActionGates.ResetTracking(); } catch { }
    }

    // ── UseDone: AC's own decrement (2026-10-06) ─────────────────────────────
    // AC pairs every client action with the server's answer: sending a use or a cast
    // increments m_cBusy (ClientUISystem::IncrementBusyCount, e.g. after
    // CM_Magic::Event_CastUntargetedSpell @0x00567CB0), and the server's UseDone
    // (GameEvent 0x01C7) decrements it INLINE in ClientUISystem::Handle_Item::UseDone
    // (@0x005656A0: [ui+0x14]-1, UpdateCursorState at 0) - not through
    // DecrementBusyCount, so neither this file's shadow count nor the plugins' mirrors
    // ever saw it. That blind spot is where "busy count stuck" came from: the shadow
    // climbed after every use and cast, and the plugin and the watchdogs below then
    // force-cleared AC's REAL count while the server still owed answers (each later
    // UseDone drove it to -1: ~190 negative repairs an hour on 10-05/10-06). The engine's
    // direct UseObject / UseObjectOn / CastSpell go through the same client functions,
    // so their busy is answered the same way: the reconciler below must not decrement it
    // a second time.
    // SmartBoxHooks calls this for every inbound UseDone, on AC's main thread, before AC
    // handles it: the shadow and the plugin mirrors take the same -1 AC is about to take,
    // and one unit of direct-action reconcile is dropped (AC pairs it itself).
    private static long _lastUseDoneTickMs;
    private static int _lateUseDone;
    private static long _useDoneMirrored;
    public static long UseDoneMirrorCount => Interlocked.Read(ref _useDoneMirrored);

    public static void OnUseDone()
    {
        Volatile.Write(ref _lastUseDoneTickMs, Environment.TickCount64);
        // The flight recorder shows AC's inline -1 as an ordinary decrement: real after it, shadow after ours.
        if (TryReadRealBusy(out int realBefore))
        {
            Net.FlightRecorder.Mark(Net.FlightMark.BusyDecrement, unchecked((uint)(realBefore - 1)),
                unchecked((uint)Math.Max(0, Volatile.Read(ref _netBusyCount) - 1)));
            // Nothing is counted for this answer (its action was reconciled or reset already, or the
            // count was never raised): AC's -1 makes the count negative. Swallow it at the next tick
            // instead of after the 2 s grace below.
            if (realBefore <= 0)
                Volatile.Write(ref _lateUseDone, 1);
        }
        // AC pairs this UseDone with the oldest direct action still waiting for one: drop one
        // unit of its delta from the reconciler (a FreeHandsAndCastSpell that added 2 keeps
        // its unanswered second unit, which is a real leak and is reconciled).
        lock (_directLock)
        {
            if (_directPending.Count > 0)
            {
                var e = _directPending.Dequeue();
                _pendingCastBusyDelta = Math.Max(0, _pendingCastBusyDelta - 1);
                if (e.Delta > 1)
                    _directLeak += e.Delta - 1;
            }
        }
        int now;
        int was;
        do
        {
            was = Volatile.Read(ref _netBusyCount);
            if (was <= 0) return;   // nothing of ours to pair (a player action we never counted, or already 0)
            now = was - 1;
        }
        while (Interlocked.CompareExchange(ref _netBusyCount, now, was) != was);
        Interlocked.Increment(ref _useDoneMirrored);
        if (now <= 0)
            Volatile.Write(ref _busyBecamePositiveTickMs, 0);
        PluginManager.QueueBusyCountDecremented();
    }

    // ── Direct-action busy reconciliation ─────────────────────────────────────
    // The engine's direct AC calls (casts via FreeHandsAndCastSpell / CastSpell, and
    // UseObject / UseObjectOn) raise m_cBusy synchronously; the delta is measured around
    // the call (both on AC's main thread, so it is exactly ours).
    // 2026-10-06: this used to decrement the whole delta as soon as the gesture ended or
    // 4 s passed, on the belief that nothing else would - but AC's UseDone handler does
    // (see OnUseDone), so every answered action was decremented twice and the real count
    // went to -1 until the negative repair below (~190 an hour). Now each direct action
    // waits in _directPending for ITS UseDone (OnUseDone takes one unit off), and only
    // what no UseDone will answer is decremented here:
    //   * the part of a delta above 1 (FreeHandsAndCastSpell readies the wand +1 and casts
    //     +1; the server answers the cast only), once no gesture animates;
    //   * an action with no UseDone after DirectActionAnswerMs (a wield gets none; a
    //     refusal the client never heard about), once no gesture animates.
    // A late UseDone after that still drives the count to -1; the negative repair stays.
    private readonly struct DirectAction
    {
        public readonly long IssuedAtMs;
        public readonly int Delta;
        public DirectAction(long at, int delta) { IssuedAtMs = at; Delta = delta; }
    }
    private static readonly System.Collections.Generic.Queue<DirectAction> _directPending = new();
    private static readonly object _directLock = new();   // ResetSession can come from another thread
    private static int _directLeak;
    private static int _pendingCastBusyDelta;   // what the reconciler may still decrement (display + clamp)
    private static int _reconcileLogCount;
    // The longest answer in 3.7 h of healthy logs was 5.0 s (a vital cast; corpse walks 3.7 s).
    private const long DirectActionAnswerMs = 6_000;

    /// <summary>Snapshot the real m_cBusy immediately before a direct cast/item-action call (-1 = unreadable).</summary>
    public static int CaptureRealBusyForCast() => ReadRealBusy();

    /// <summary>
    /// Arm reconciliation right after a direct cast/item-action call, given the
    /// pre-call snapshot. Main thread only (these calls already run there).
    /// </summary>
    public static void NoteDirectCastIssued(int busyBefore)
    {
        if (busyBefore < 0) return;
        int after = ReadRealBusy();
        if (after < 0) return;
        int delta = after - busyBefore;
        if (delta <= 0) return;
        lock (_directLock)
        {
            _pendingCastBusyDelta += delta;
            _directPending.Enqueue(new DirectAction(Environment.TickCount64, delta));
            while (_directPending.Count > 64) _directLeak += _directPending.Dequeue().Delta;   // never unbounded
        }
    }

    private static bool CastGestureAnimating()
    {
        try { return PlayerPhysicsHooks.TryGetCastGestureInProgress(out bool g) && g; }
        catch { return false; }
    }

    private static void ReconcileCastBusy(long now)
    {
        int pending;
        lock (_directLock)
        {
            // Actions the server never answered in time join the unanswered part.
            while (_directPending.Count > 0 && now - _directPending.Peek().IssuedAtMs > DirectActionAnswerMs)
                _directLeak += _directPending.Dequeue().Delta;
            pending = _directLeak;
        }
        if (pending <= 0) return;

        // Never mid-gesture: the action's own animation is what it is still doing.
        if (CastGestureAnimating())
            return;

        lock (_directLock)
        {
            _directLeak = Math.Max(0, _directLeak - pending);
            _pendingCastBusyDelta = Math.Max(0, _pendingCastBusyDelta - pending);
        }

        IntPtr p = _lastThisPtr;
        if (p == IntPtr.Zero || _decrementTargetAddress == IntPtr.Zero) return;
        if (!ClientObjectHooks.IsReadablePointer(p + OffsetMCBusy)) return;

        // Clamp to the live field — if AC (or a watchdog) already cleared part
        // of it, never push m_cBusy negative.
        int real = ReadRealBusy();
        int n = Math.Min(pending, real);
        if (n <= 0) return;

        try
        {
            // Call the HOOKED decrement entry (not the trampoline): our detour
            // fires, so _netBusyCount and the plugin busy-decremented events
            // stay consistent for free.
            var dec = Marshal.GetDelegateForFunctionPointer<BusyCountDelegate>(_decrementTargetAddress);
            for (int i = 0; i < n; i++) dec(p);
            Interlocked.Increment(ref _reconcileCount);
            Net.FlightRecorder.Mark(Net.FlightMark.BusyReconcile, (uint)n, unchecked((uint)ReadRealBusy()));
            if (_reconcileLogCount < 50 || _reconcileLogCount % 100 == 0)
                RynthLog.Compat($"BusyCountHooks: direct-action busy reconciled — decremented {n} (real now {ReadRealBusy()}, total reconciles {_reconcileLogCount + 1}).");
            _reconcileLogCount++;
        }
        catch { }
    }

    /// <summary>
    /// Auto-clear busy state if it has been positive for too long.
    /// Safe to call from any thread; only performs work on AC's main
    /// thread (via MainThreadGuard) since ForceResetBusyCount writes
    /// AC memory and calls AC functions that aren't thread-safe.
    /// </summary>
    public static void CheckWatchdog()
    {
        long now = Environment.TickCount64;

        // The gates AC actually checks before an item use/move/equip (pending
        // inventory request, attacking flag). Runs on every call — before each
        // inbound game event and after each tick — so the attacking flag's
        // zero window between AttackDone and the next CommenceAttack is seen.
        if (MainThreadGuard.IsOnMainThread())
        {
            try { ClientActionGates.Tick(now, PlayerLifecycleLog.InWorldTransition); }
            catch { }
        }

        // Dead or portaling (and just after): the busy count moves on its own
        // while AC rebuilds the world, and force-clearing it then writes into
        // AC mid-transition — every death today crashed inside AC during the
        // death portal (2026-09-28). Restart the timers so a genuinely stuck
        // count is judged afresh once the world has settled.
        if (PlayerLifecycleLog.InWorldTransition)
        {
            Volatile.Write(ref _realBusyPositiveTickMs, 0);
            if (Volatile.Read(ref _busyBecamePositiveTickMs) != 0)
                Volatile.Write(ref _busyBecamePositiveTickMs, now);
            return;
        }

        // ── Real-field watchdog (catches counter desync) ─────────────────────
        // Trust AC's REAL m_cBusy over our shadow counter: when they diverge a
        // pinned real field is what actually wedges the client (mode-change /
        // cast refusal) while _netBusyCount reads ~0 and the counter watchdog
        // below never fires. A legit action returns the field to 0 within ~2-3s
        // (resetting the timer); only a genuinely stuck field reaches the 4s
        // timeout. Field read is harmless off-thread, but ForceResetBusyCount
        // mutates AC, so only clear on the main thread.
        if (MainThreadGuard.IsOnMainThread())
        {
            // Reconcile our own cast increments FIRST — a clean deterministic
            // decrement here means the stuck-field watchdog below (and the
            // plugin-side clears) have nothing left to catch.
            ReconcileCastBusy(now);

            // Consume any off-thread force-reset request (plugin watchdogs):
            // the actual clear runs HERE, on the main thread.
            if (Interlocked.Exchange(ref _forceResetRequested, 0) == 1)
                ForceResetBusyCount(why: _forceResetRequestedWhy + " (latched from off the game thread)");

            int realBusy = ReadRealBusy();
            if (realBusy > 0)
            {
                long sinceR = Volatile.Read(ref _realBusyPositiveTickMs);
                if (sinceR == 0)
                {
                    Volatile.Write(ref _realBusyPositiveTickMs, now);
                }
                else if (now - sinceR > RealBusyWatchdogTimeoutMs
                         && now - Volatile.Read(ref _lastUseDoneTickMs) > RealBusyServerSilentMs
                         && !CastGestureAnimating()
                         && now - Volatile.Read(ref _lastWatchdogFireTickMs) > BusyWatchdogCooldownMs)
                {
                    Volatile.Write(ref _lastWatchdogFireTickMs, now);
                    long silent = now - Volatile.Read(ref _lastUseDoneTickMs);
                    RynthLog.Compat($"BusyCountHooks: REAL m_cBusy at {realBusy} for {now - sinceR}ms with no UseDone from the server for {(silent > 3_600_000 ? "ever" : silent + "ms")} (counter={_netBusyCount}) — resetting the cursor count (the server is quiet; nothing is waiting on it).");
                    Net.FlightRecorder.Mark(Net.FlightMark.BusyWatchdog, (uint)realBusy, unchecked((uint)_netBusyCount), (uint)(now - sinceR), "real m_cBusy positive, server quiet");
                    ForceResetBusyCount(why: "the engine's real-count watchdog (server quiet)");
                    Volatile.Write(ref _realBusyPositiveTickMs, 0);
                    Volatile.Write(ref _busyBecamePositiveTickMs, 0);
                    return;
                }
            }
            else
            {
                // 0 = idle; -1 = unreadable (no pointer / freed page). Either
                // way disarm the timer — leaving it armed across an unreadable
                // stretch caused an instant force-clear when reads resumed
                // (e.g. first beat after the next login).
                Volatile.Write(ref _realBusyPositiveTickMs, 0);
            }

            // Negative real field (ReadRealBusy folds every negative into its -1
            // "unreadable" sentinel, so read it directly). See RepairNegativeBusy.
            bool haveReal = TryReadRealBusy(out int signedBusy);
            bool lateUseDone = Interlocked.Exchange(ref _lateUseDone, 0) == 1;
            if (haveReal && signedBusy < 0 && lateUseDone && RepairNegativeBusy(out int lateNeg))
            {
                _realBusyNegativeSince = 0;
                RynthLog.Compat($"BusyCountHooks: a UseDone came with nothing counted for it; m_cBusy {lateNeg} -> 0 at once.");
            }
            else if (haveReal && signedBusy < 0)
            {
                if (_realBusyNegativeSince == 0)
                    _realBusyNegativeSince = now;
                else if (now - _realBusyNegativeSince >= NegativeBusyRepairMs && RepairNegativeBusy(out int neg))
                {
                    _realBusyNegativeSince = 0;
                    RynthLog.Compat($"BusyCountHooks: REAL m_cBusy was negative ({neg}) — reset to 0 (a zeroed count's own UseDone arrived later).");
                }
            }
            else
            {
                _realBusyNegativeSince = 0;
            }

            // Counter-based watchdog below: the shadow counter never sees AC's
            // inline UseDone decrement, so it reads "stuck" after nearly every use
            // and cast even though the real field is back at 0. When the real
            // field is readable and idle, just resync the shadow — force-clearing
            // there only zeroed the next in-flight action (thousands of needless
            // force-clears a session). A real field > 0 is the real-field
            // watchdog's job above.
            long shadowSince = Volatile.Read(ref _busyBecamePositiveTickMs);
            if (shadowSince != 0 && haveReal && signedBusy <= 0
                && now - shadowSince >= BusyWatchdogTimeoutMs)
            {
                DropShadowTo0();
                Volatile.Write(ref _busyBecamePositiveTickMs, 0);
                return;
            }
        }

        // ── Counter-based watchdog (original) ────────────────────────────────
        long stuckSince = Volatile.Read(ref _busyBecamePositiveTickMs);
        if (stuckSince == 0)
            return;

        long elapsed = now - stuckSince;
        if (elapsed < BusyWatchdogTimeoutMs)
            return;

        if (!MainThreadGuard.IsOnMainThread())
            return;

        long lastFire = Volatile.Read(ref _lastWatchdogFireTickMs);
        if (now - lastFire < BusyWatchdogCooldownMs)
            return;

        Volatile.Write(ref _lastWatchdogFireTickMs, now);
        RynthLog.Compat($"BusyCountHooks: watchdog auto-resetting — busy stuck at {_netBusyCount} for {elapsed}ms.");
        Net.FlightRecorder.Mark(Net.FlightMark.BusyWatchdog, unchecked((uint)ReadRealBusy()), unchecked((uint)_netBusyCount), (uint)elapsed, "engine count stuck");
        ForceResetBusyCount(why: "the engine's count watchdog (real count unreadable)");
        Volatile.Write(ref _busyBecamePositiveTickMs, 0);
    }

    private static void IncrementBusyCountDetour(IntPtr thisPtr)
    {
        RecursionGuard.Tick("BusyCountHooks.Increment");
        _lastThisPtr = thisPtr;
        if (++_incFires <= 3)
            RynthLog.Compat($"BusyCountHooks: Increment fired #{_incFires} this=0x{thisPtr.ToInt32():X8}");
        try { _originalIncrementBusyCount!(thisPtr); }
        catch (Exception ex) { try { RynthLog.Compat($"BusyCountHooks: Increment original threw {ex.GetType().Name}: {ex.Message}"); } catch { } throw; }
        Interlocked.Increment(ref _incrementDispatchCount);
        int after = Interlocked.Increment(ref _netBusyCount);
        Net.FlightRecorder.Mark(Net.FlightMark.BusyIncrement, unchecked((uint)Marshal.ReadInt32(thisPtr + OffsetMCBusy)), unchecked((uint)after));
        if (after == 1)
            Volatile.Write(ref _busyBecamePositiveTickMs, Environment.TickCount64);
        PluginManager.QueueBusyCountIncremented();
    }

    private static void DecrementBusyCountDetour(IntPtr thisPtr)
    {
        RecursionGuard.Tick("BusyCountHooks.Decrement");
        if (_lastThisPtr == IntPtr.Zero)
            _lastThisPtr = thisPtr;
        if (++_decFires <= 3)
            RynthLog.Compat($"BusyCountHooks: Decrement fired #{_decFires} this=0x{thisPtr.ToInt32():X8}");
        try { _originalDecrementBusyCount!(thisPtr); }
        catch (Exception ex) { try { RynthLog.Compat($"BusyCountHooks: Decrement original threw {ex.GetType().Name}: {ex.Message}"); } catch { } throw; }
        Interlocked.Increment(ref _decrementDispatchCount);
        int after = Interlocked.Decrement(ref _netBusyCount);
        Net.FlightRecorder.Mark(Net.FlightMark.BusyDecrement, unchecked((uint)Marshal.ReadInt32(thisPtr + OffsetMCBusy)), unchecked((uint)after));
        if (after <= 0)
            Volatile.Write(ref _busyBecamePositiveTickMs, 0);
        PluginManager.QueueBusyCountDecremented();
    }
}

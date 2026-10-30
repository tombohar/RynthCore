// ============================================================================
//  RynthCore.Engine - Compatibility/ClientActionGates.cs
//
//  The acclient's own item-action gates, which lock the player out of using,
//  equipping and moving items while chat, clicks and the inventory still work.
//
//  Every item action the client makes (double-click use, equip, drag to a pack,
//  pick up, drop, give, split, merge, vendor buy/sell) first asks
//  ACCWeenieObject::IsPlayerReadyToMakeInventoryRequest. That refuses when
//  either of two globals is set, and prints a line in chat:
//
//    1. The PENDING INVENTORY REQUEST slot (4 dwords in .data, 0x00871ED0..DC on
//       the 4,841,472-byte client): request type, object id and the time it was
//       sent. ACCWeenieObject::RecordRequest (and the UIAttempt* callers, inline)
//       fill it when the client sends an inventory request. Only the server's
//       answer ABOUT THAT SAME OBJECT empties it: ServerSaysMoveItem,
//       ServerSaysSetStackSize, ServerSaysAttemptFailed (0x00A0), the ground
//       container view/close (RecordResponse) or the vendor list re-send. Those
//       are ACCWeenieObject methods, so when the object is no longer in the
//       client's object table (the corpse decayed, a later corpse open queued the
//       previous corpse's contents for destruction, a stack merged away) the
//       answer finds nothing and the slot is never emptied. The time field is
//       written and never read: there is no timeout. Logging off does not empty
//       it either, and CPlayerSystem::LogOffCharacter/UseTime wait for it, so the
//       logoff sits at "Logging off..." forever. Refusal text:
//       "You can only move or use one item at a time".
//
//    2. The ATTACKING flag (0x00871EE0): set by
//       ClientCombatSystem::HandleCommenceAttackEvent (0x01B8), cleared only by
//       HandleAttackDoneEvent (0x01A7). A lost AttackDone leaves it set for the
//       rest of the process. Refusal text:
//       "You cannot move or use an item while attacking".
//
//  Neither is the busy count (ClientUISystem m_cBusy): AC only reads m_cBusy to
//  pick the cursor (UpdateCursorState: != 0 shows the busy cursor), so a zeroed
//  or negative count changes the cursor and nothing else. That is why every
//  busy-count reset (/ra clearbusy, /ra panic, the watchdogs) left this lock
//  in place.
//
//  This file:
//    * resolves both gates by pattern (two independent code sites each, which
//      must agree, or the gate is left alone);
//    * reads them for /rc actionstate and the heartbeat (read-only, any thread);
//    * on AC's main thread, empties a pending request whose object has been
//      gone from the client for MissingObjectGraceMs, or that has had no answer
//      for PendingTimeoutMs, and clears an attacking flag that has stayed set
//      with no AttackDone for AttackStaleMs. Emptying the slot is exactly what
//      AC's own RecordResponse does when an answer arrives; a late real answer
//      after that finds nothing pending and changes nothing;
//    * does the same on demand for /rc unlockactions (latched when off-thread).
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal static class ClientActionGates
{
    // ACCWeenieObject::RecordRequest(objectId, type) — the whole function:
    //   mov eax,[esp+4]; mov ecx,[esp+8]; mov edx,[TimerCurTime]; mov [reqObj],eax;
    //   mov eax,[TimerCurTime+4]; mov [reqType],ecx; mov [reqTime],edx; mov [reqTime+4],eax; ret
    private static readonly byte?[] RecordRequestPattern =
    [
        0x8B, 0x44, 0x24, 0x04,
        0x8B, 0x4C, 0x24, 0x08,
        0x8B, 0x15, null, null, null, null,   // +10 TimerCurTime (low dword)
        0xA3, null, null, null, null,         // +15 reqObj
        0xA1, null, null, null, null,         // +20 TimerCurTime (high dword)
        0x89, 0x0D, null, null, null, null,   // +26 reqType
        0x89, 0x15, null, null, null, null,   // +32 reqTime (low)
        0xA3, null, null, null, null,         // +37 reqTime (high)
        0xC3
    ];

    // ACCWeenieObject::RecordResponse(objectId) — clears the slot when the id matches:
    //   mov eax,[esp+4]; cmp eax,[reqObj]; jne +16h; xor eax,eax;
    //   mov [reqObj],eax; mov [reqType],eax; mov [reqTime],eax; mov [reqTime+4],eax; ret
    private static readonly byte?[] RecordResponsePattern =
    [
        0x8B, 0x44, 0x24, 0x04,
        0x3B, 0x05, null, null, null, null,   // +6  reqObj
        0x75, 0x16,
        0x33, 0xC0,
        0xA3, null, null, null, null,         // +15 reqObj
        0xA3, null, null, null, null,         // +20 reqType
        0xA3, null, null, null, null,         // +25 reqTime (low)
        0xA3, null, null, null, null,         // +30 reqTime (high)
        0xC3
    ];

    // ClientCombatSystem::HandleAttackDoneEvent: mov [esi+3C],bl; cmp [esp+0C],ebx;
    // mov [esi+3D],bl; mov [attacking],ebx
    private static readonly byte?[] AttackDonePattern =
    [
        0x88, 0x5E, 0x3C, 0x39, 0x5C, 0x24, 0x0C, 0x88, 0x5E, 0x3D,
        0x89, 0x1D, null, null, null, null    // +12 attacking
    ];

    // ClientCombatSystem::HandleCommenceAttackEvent tail: mov [attacking],1;
    // call GetUISystem; mov ecx,eax; pop esi; add esp,4; jmp IncrementBusyCount
    private static readonly byte?[] CommenceAttackPattern =
    [
        0xC7, 0x05, null, null, null, null,   // +2 attacking
        0x01, 0x00, 0x00, 0x00,
        0xE8, null, null, null, null,
        0x8B, 0xC8, 0x5E, 0x83, 0xC4, 0x04, 0xE9
    ];

    // Resolved addresses (0 = not resolved; the gate is then read-only-unavailable).
    private static int _reqTypeVa, _reqObjVa, _reqTimeVa, _timerVa, _attackVa;
    private static bool _requestReady, _attackReady, _initialized;
    private static string _status = "Not initialized.";

    // Repair policy. A normal request is answered within a network round trip
    // (a ground pickup adds the walk to the item); a single attack swing lasts a
    // few seconds and every AttackDone empties the flag between swings. CheckWatchdog
    // runs before every inbound game event and after every game tick, so the
    // flag's zero window between AttackDone and the next CommenceAttack is always
    // sampled at least once.
    // 10 s (was 60): a real answer comes within about a second, and with healing interrupting
    // melee/looting (2026.10.4.2) a lost answer locked every item action for a full minute.
    private const long PendingTimeoutMs = 10_000;
    private const long MissingObjectGraceMs = 5_000;
    // 12 s in melee/missile (was 30); outside them no attack can be running, so 1.5 s.
    private const long AttackStaleMs = 12_000;
    private const long AttackStaleOutOfStanceMs = 1_500;
    private const long PendingCheckIntervalMs = 500;
    private const long ChatNoticeCooldownMs = 10 * 60_000;

    // Main-thread tracking state.
    private static long _trackedKey;
    private static long _pendingSeenAt;
    private static long _objectMissingSince;
    private static long _lastPendingCheck;
    private static long _attackSetSince;
    private static long _lastChatNoticeAt = long.MinValue / 2;

    private static long _pendingClears;
    private static long _attackClears;
    private static int _unlockRequested;

    public static bool IsRequestGateAvailable => _requestReady;
    public static bool IsAttackGateAvailable => _attackReady;
    public static string StatusMessage => _status;
    public static long PendingClearCount => Interlocked.Read(ref _pendingClears);
    public static long AttackClearCount => Interlocked.Read(ref _attackClears);

    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
        {
            _status = "acclient .text not readable.";
            RynthLog.Compat($"ClientActionGates: {_status}");
            return;
        }

        string req = ResolveRequestSlot(text);
        string atk = ResolveAttackFlag(text);
        _status = $"request slot: {req}; attack flag: {atk}.";
        RynthLog.Compat($"ClientActionGates: {_status}");
    }

    private static string ResolveRequestSlot(AcClientTextSection text)
    {
        if (!TryFindUnique(text, RecordRequestPattern, out int a))
            return "RecordRequest pattern not found (unique)";
        if (!TryFindUnique(text, RecordResponsePattern, out int b))
            return "RecordResponse pattern not found (unique)";

        byte[] t = text.Bytes;
        int obj = BitConverter.ToInt32(t, a + 15);
        int type = BitConverter.ToInt32(t, a + 26);
        int time = BitConverter.ToInt32(t, a + 32);
        int timeHi = BitConverter.ToInt32(t, a + 37);
        int timerLo = BitConverter.ToInt32(t, a + 10);
        int timerHi = BitConverter.ToInt32(t, a + 20);

        bool consistent =
            type == obj - 4 && time == obj + 4 && timeHi == obj + 8 && timerHi == timerLo + 4
            && BitConverter.ToInt32(t, b + 6) == obj && BitConverter.ToInt32(t, b + 15) == obj
            && BitConverter.ToInt32(t, b + 20) == type && BitConverter.ToInt32(t, b + 25) == time
            && BitConverter.ToInt32(t, b + 30) == timeHi;
        if (!consistent)
            return $"code sites disagree (obj=0x{obj:X8} type=0x{type:X8})";
        if (!ClientObjectHooks.IsWritablePointer(new IntPtr(type))
            || !ClientObjectHooks.IsWritablePointer(new IntPtr(timeHi))
            || !ClientObjectHooks.IsReadablePointer(new IntPtr(timerLo)))
            return $"globals not mapped (type=0x{type:X8})";

        _reqTypeVa = type;
        _reqObjVa = obj;
        _reqTimeVa = time;
        _timerVa = timerLo;
        _requestReady = true;
        return $"0x{type:X8} (type) / 0x{obj:X8} (object)";
    }

    private static string ResolveAttackFlag(AcClientTextSection text)
    {
        if (!TryFindUnique(text, AttackDonePattern, out int a))
            return "HandleAttackDoneEvent pattern not found (unique)";
        if (!TryFindUnique(text, CommenceAttackPattern, out int b))
            return "HandleCommenceAttackEvent pattern not found (unique)";
        int flagA = BitConverter.ToInt32(text.Bytes, a + 12);
        int flagB = BitConverter.ToInt32(text.Bytes, b + 2);
        if (flagA != flagB)
            return $"code sites disagree (0x{flagA:X8} vs 0x{flagB:X8})";
        if (!ClientObjectHooks.IsWritablePointer(new IntPtr(flagA)))
            return $"global 0x{flagA:X8} not mapped";
        _attackVa = flagA;
        _attackReady = true;
        return $"0x{flagA:X8}";
    }

    private static bool TryFindUnique(AcClientTextSection text, byte?[] pattern, out int offset)
    {
        offset = PatternScanner.FindPattern(text.Bytes, pattern);
        if (offset < 0)
            return false;
        return PatternScanner.FindPatternInRegion(text.Bytes, pattern, offset + 1, text.Bytes.Length) < 0;
    }

    // ── Reads (any thread: plain loads from acclient .data, which never unmaps) ──

    public static bool TryReadPending(out uint type, out uint objectId, out double ageSeconds)
    {
        type = 0;
        objectId = 0;
        ageSeconds = -1;
        if (!_requestReady)
            return false;
        type = unchecked((uint)Marshal.ReadInt32(new IntPtr(_reqTypeVa)));
        objectId = unchecked((uint)Marshal.ReadInt32(new IntPtr(_reqObjVa)));
        double issued = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(new IntPtr(_reqTimeVa)));
        double now = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(new IntPtr(_timerVa)));
        if (type != 0 && issued > 0 && now >= issued && now - issued < 1e7)
            ageSeconds = now - issued;
        return true;
    }

    public static bool TryReadAttacking(out bool attacking)
    {
        attacking = false;
        if (!_attackReady)
            return false;
        attacking = Marshal.ReadInt32(new IntPtr(_attackVa)) != 0;
        return true;
    }

    public static string RequestTypeName(uint type) => type switch
    {
        0 => "none",
        1 => "merge",
        2 => "split",
        3 => "move",
        4 => "pick up",
        5 => "put in container",
        6 => "drop",
        7 => "wield",
        9 => "give",
        10 => "vendor buy/sell",
        _ => $"request {type}",
    };

    /// <summary>Short heartbeat field, empty when both gates are open.</summary>
    public static string HeartbeatField()
    {
        try
        {
            string s = string.Empty;
            if (TryReadPending(out uint type, out _, out double age) && type != 0)
                s = age >= 0 ? $" req={type}/{age:0}s" : $" req={type}";
            if (TryReadAttacking(out bool attacking) && attacking)
                s += " atk=1";
            return s;
        }
        catch { return string.Empty; }
    }

    // ── Main-thread watchdog ─────────────────────────────────────────────────

    /// <summary>Drop the tracking state at logout / client close (the slot itself is AC's).</summary>
    public static void ResetTracking()
    {
        _trackedKey = 0;
        _pendingSeenAt = 0;
        _objectMissingSince = 0;
        _attackSetSince = 0;
    }

    /// <summary>
    /// Called from BusyCountHooks.CheckWatchdog on every game tick and before every
    /// inbound game event. Main thread only; any other thread returns at once.
    /// </summary>
    public static void Tick(long now, bool inWorldTransition)
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;

        if (Interlocked.Exchange(ref _unlockRequested, 0) == 1)
        {
            foreach (string line in UnlockNow("manual (/rc unlockactions)"))
                RynthLog.Compat($"ClientActionGates: {line}");
        }

        TickAttackFlag(now, inWorldTransition);

        if (now - _lastPendingCheck < PendingCheckIntervalMs)
            return;
        _lastPendingCheck = now;
        TickPendingRequest(now, inWorldTransition);
    }

    private static void TickAttackFlag(long now, bool inWorldTransition)
    {
        if (!_attackReady)
            return;
        bool attacking = Marshal.ReadInt32(new IntPtr(_attackVa)) != 0;
        if (!attacking || inWorldTransition)
        {
            _attackSetSince = 0;
            return;
        }
        if (_attackSetSince == 0)
        {
            _attackSetSince = now;
            return;
        }
        // Peace or Magic mode (kits, looting, casting happen here): no melee/missile attack
        // can be in progress, so a set flag is a lost AttackDone. Clear it quickly.
        int mode = CombatModeHooks.ReadCurrentCombatMode();
        bool attackStance = mode == CombatActionHooks.CombatModeMelee || mode == CombatActionHooks.CombatModeMissile;
        if (now - _attackSetSince < (attackStance ? AttackStaleMs : AttackStaleOutOfStanceMs))
            return;

        long heldMs = now - _attackSetSince;
        _attackSetSince = 0;
        if (ClearAttackFlag($"watchdog: held {heldMs / 1000}s with no AttackDone"))
        {
            RynthLog.Compat($"ClientActionGates: attacking flag held {heldMs / 1000}s with no AttackDone — cleared. AC was refusing every item use/move ('while attacking').");
            NotifyChat("Item actions were locked (the client never got the end of an attack). Unlocked.");
        }
    }

    private static void TickPendingRequest(long now, bool inWorldTransition)
    {
        if (!_requestReady)
            return;
        TryReadPending(out uint type, out uint objectId, out double acAge);
        if (type == 0)
        {
            _trackedKey = 0;
            _objectMissingSince = 0;
            return;
        }

        long issuedBits = Marshal.ReadInt64(new IntPtr(_reqTimeVa));
        long key = unchecked(((long)type << 56) ^ ((long)objectId << 24) ^ issuedBits);
        if (key != _trackedKey || _pendingSeenAt == 0)
        {
            _trackedKey = key;
            _pendingSeenAt = now;
            _objectMissingSince = 0;
        }

        // Portal / death: objects leave and re-enter the client wholesale and
        // answers can arrive after the world settles. Judge afresh afterwards.
        if (inWorldTransition)
        {
            _pendingSeenAt = now;
            _objectMissingSince = 0;
            return;
        }

        string? reason = null;
        if (LoginLifecycleHooks.HasObservedLoginComplete)
        {
            bool exists = objectId != 0 && ClientObjectHooks.TryGetWeenieObjectPtr(objectId, out _);
            if (exists)
                _objectMissingSince = 0;
            else if (_objectMissingSince == 0)
                _objectMissingSince = now;
            else if (now - _objectMissingSince >= MissingObjectGraceMs)
                reason = "its object is no longer in the client, so no answer can clear it";
        }

        if (reason == null && now - _pendingSeenAt >= PendingTimeoutMs)
            reason = $"no answer for {(now - _pendingSeenAt) / 1000}s";

        if (reason == null)
            return;

        if (ClearPendingRequest("watchdog: " + reason))
        {
            RynthLog.Compat(
                $"ClientActionGates: cleared a stuck item request — {RequestTypeName(type)} (type {type}) on 0x{objectId:X8}" +
                $"{(acAge >= 0 ? $", sent {acAge:0}s ago" : string.Empty)}: {reason}. AC was refusing every item use/move/equip ('one item at a time').");
            NotifyChat("Item actions were locked (the client was still waiting on an old item request). Unlocked.");
        }
        _trackedKey = 0;
        _objectMissingSince = 0;
    }

    private static void NotifyChat(string text)
    {
        // Called from the main-thread watchdog only; the chat line itself is
        // written by the queue's drain.
        if (!MainThreadGuard.IsOnMainThread())
            return;
        long now = Environment.TickCount64;
        if (now - _lastChatNoticeAt < ChatNoticeCooldownMs)
            return;
        _lastChatNoticeAt = now;
        AcMainThreadQueue.EnqueueWriteToChat("[RynthCore] " + text, 1);
    }

    /// <summary>
    /// Empty AC's pending-inventory-request slot, the same four stores
    /// RecordResponse makes when the server answers. Main thread only.
    /// </summary>
    private static bool ClearPendingRequest(string why)
    {
        if (!MainThreadGuard.IsOnMainThread() || !_requestReady)
            return false;
        Net.FlightRecorder.Mark(Net.FlightMark.GateCleared,
            unchecked((uint)Marshal.ReadInt32(new IntPtr(_reqTypeVa))), unchecked((uint)Marshal.ReadInt32(new IntPtr(_reqObjVa))), text: why);
        Marshal.WriteInt32(new IntPtr(_reqObjVa), 0);
        Marshal.WriteInt32(new IntPtr(_reqTypeVa), 0);
        Marshal.WriteInt32(new IntPtr(_reqTimeVa), 0);
        Marshal.WriteInt32(new IntPtr(_reqTimeVa + 4), 0);
        Interlocked.Increment(ref _pendingClears);
        return true;
    }

    /// <summary>Clear AC's attacking flag, as HandleAttackDoneEvent does. Main thread only.</summary>
    private static bool ClearAttackFlag(string why)
    {
        if (!MainThreadGuard.IsOnMainThread() || !_attackReady)
            return false;
        Net.FlightRecorder.Mark(Net.FlightMark.GateCleared, 0, 0, text: why);
        Marshal.WriteInt32(new IntPtr(_attackVa), 0);
        Interlocked.Increment(ref _attackClears);
        return true;
    }

    /// <summary>
    /// /rc unlockactions: open both gates now and repair a negative busy count.
    /// On AC's main thread it runs at once and returns what it did; elsewhere it
    /// latches for the next watchdog tick and says so.
    /// </summary>
    public static string[] RequestUnlock()
    {
        if (!MainThreadGuard.IsOnMainThread())
        {
            Interlocked.Exchange(ref _unlockRequested, 1);
            return ["Unlock queued for the next game tick."];
        }
        return UnlockNow("manual (/rc unlockactions)");
    }

    private static string[] UnlockNow(string why)
    {
        if (!MainThreadGuard.IsOnMainThread())
            return ["Unlock skipped: not on the game thread."];

        var done = new System.Collections.Generic.List<string>();
        if (TryReadPending(out uint type, out uint objectId, out double age) && type != 0)
        {
            if (ClearPendingRequest(why))
                done.Add($"cleared the pending item request ({RequestTypeName(type)} on 0x{objectId:X8}{(age >= 0 ? $", {age:0}s old" : string.Empty)})");
        }
        if (TryReadAttacking(out bool attacking) && attacking)
        {
            if (ClearAttackFlag(why))
                done.Add("cleared the attacking flag");
        }
        if (BusyCountHooks.RepairNegativeBusy(out int was))
            done.Add($"reset a negative busy count ({was} -> 0)");

        ResetTracking();
        string summary = done.Count == 0
            ? "Nothing to unlock: no pending item request, no attacking flag, busy count not negative."
            : "Unlocked: " + string.Join("; ", done) + ".";
        RynthLog.Compat($"ClientActionGates: {why}: {summary}");
        return [summary];
    }

    // ── /rc actionstate ─────────────────────────────────────────────────────

    public static string[] DescribeLines()
    {
        var lines = new System.Collections.Generic.List<string>();
        lines.Add("Action state (what the client checks before a use, equip or move):");

        // 1. Pending inventory request.
        if (!_requestReady)
        {
            lines.Add($"  Item request: unavailable ({_status})");
        }
        else
        {
            TryReadPending(out uint type, out uint objectId, out double age);
            if (type == 0)
            {
                lines.Add("  Item request: none (open)");
            }
            else
            {
                string name = string.Empty;
                bool exists = false;
                if (MainThreadGuard.IsOnMainThread() && objectId != 0)
                {
                    exists = ClientObjectHooks.TryGetWeenieObjectPtr(objectId, out _);
                    if (exists) ClientObjectHooks.TryGetObjectName(objectId, out name);
                }
                lines.Add($"  Item request: WAITING - {RequestTypeName(type)} (type {type}) on 0x{objectId:X8}" +
                          $"{(name.Length > 0 ? $" '{name}'" : string.Empty)}" +
                          $"{(age >= 0 ? $", sent {age:0.0}s ago" : string.Empty)}, object {(exists ? "still in client" : "GONE from client")}.");
                lines.Add("    While this is set the client refuses every use/equip/move ('You can only move or use one item at a time') and a logoff waits forever.");
            }
        }

        // 2. Attacking flag.
        if (!_attackReady)
            lines.Add("  Attacking flag: unavailable");
        else
        {
            TryReadAttacking(out bool attacking);
            long held = _attackSetSince == 0 ? 0 : (Environment.TickCount64 - _attackSetSince) / 1000;
            lines.Add(attacking
                ? $"  Attacking flag: SET{(held > 0 ? $" ({held}s)" : string.Empty)} - item use/move refused ('while attacking') until the server's AttackDone."
                : "  Attacking flag: clear");
        }

        // 3. Busy count (cursor only).
        string busy = BusyCountHooks.TryReadRealBusy(out int real) ? real.ToString() : "unreadable";
        lines.Add($"  Busy count m_cBusy: {busy} (engine shadow {BusyCountHooks.ShadowBusyCount}, pending reconcile {BusyCountHooks.PendingReconcileDelta}, force-clears {BusyCountHooks.ForceClearCount}, reconciles {BusyCountHooks.ReconcileCount}, negative repairs {BusyCountHooks.NegativeRepairCount}). Non-zero only shows the busy cursor.");

        // 4. ClientUISystem: target mode / open container / vendor.
        IntPtr ui = ClientHelperHooks.ReadUiSystemPtr();
        if (ui != IntPtr.Zero && ClientObjectHooks.IsReadableSpan(ui, 0x40))
        {
            uint ground = unchecked((uint)Marshal.ReadInt32(ui + 0x18));
            uint vendor = unchecked((uint)Marshal.ReadInt32(ui + 0x20));
            int targetMode = Marshal.ReadInt32(ui + 0x2C);
            string tm = targetMode switch { 0 => "none", 1 => "1 (use: pick an object)", 3 => "3 (choose a target for an item: next click is the target, Esc cancels)", _ => targetMode.ToString() };
            lines.Add($"  Target mode: {tm}; open container 0x{ground:X8}; vendor 0x{vendor:X8}");
        }
        else
        {
            lines.Add("  UI system: not available");
        }

        // 5. Combat system.
        IntPtr combat = CombatModeHooks.ReadCombatSystemPtr();
        if (combat != IntPtr.Zero && ClientObjectHooks.IsReadableSpan(combat, 0x48))
        {
            int mode = Marshal.ReadInt32(combat + 0x1C);
            string modeName = mode switch { 1 => "NonCombat", 2 => "Melee", 4 => "Missile", 8 => "Magic", _ => mode.ToString() };
            byte busyHeld = Marshal.ReadByte(combat + 0x3C);
            byte inAttack = Marshal.ReadByte(combat + 0x3D);
            byte autoRepeat = Marshal.ReadByte(combat + 0x44);
            lines.Add($"  Combat: mode {modeName}; attack holds busy {busyHeld}; attack in progress {inAttack}; auto-repeat {autoRepeat}");
        }

        lines.Add($"  World transition (portal/death): {(PlayerLifecycleLog.InWorldTransition ? "yes" : "no")}; logged in: {(LoginLifecycleHooks.HasObservedLoginComplete ? "yes" : "no")}");
        lines.Add($"  Auto-unlocks this session: item request {PendingClearCount}, attacking flag {AttackClearCount}. Unlock now: /rc unlockactions");
        return lines.ToArray();
    }
}

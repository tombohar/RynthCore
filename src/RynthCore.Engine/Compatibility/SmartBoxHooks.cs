using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class SmartBoxHooks
{
    private const int NetBlobBufPtrOffset = 0x2C;
    private const int NetBlobBufSizeOffset = 0x30;
    private const uint PositionUpdateOpcode = 0x0000F74C;
    private const uint PlayerPositionUpdateOpcode = 0x0000F74B;
    private const uint VectorUpdateOpcode = 0x0000F74E;
    private const uint UpdateObjectOpcode = 0x0000F7DB;
    private const uint ServerObjectDeleteOpcode = 0x0000F747;
    private const uint PlayerTeleportOpcode = 0x0000F751;

    // ACSmartBox::DispatchSmartBoxEvent
    // 83 EC 08 53 8B 5C 24 10 8B 53 30 83 FA 04 8B 43 2C 56 8B F1 89 44 24 14 89 54 24 08 72 ?? 8B 08
    private static readonly byte?[] DispatchSmartBoxEventPattern =
    [
        0x83, 0xEC, 0x08, 0x53, 0x8B, 0x5C, 0x24, 0x10,
        0x8B, 0x53, 0x30, 0x83, 0xFA, 0x04, 0x8B, 0x43,
        0x2C, 0x56, 0x8B, 0xF1, 0x89, 0x44, 0x24, 0x14,
        0x89, 0x54, 0x24, 0x08, 0x72, null, 0x8B, 0x08
    ];

    // UIQueueManager::ProcessNetBlobData @ 0x0055BCD0 — the real inbound GameEvent
    // dispatcher (switches on inner event type: 0x1B1/0x1B2 attack, 0x1AC/0x1AD
    // victim/killer, 0x1C0 health, …). thiscall(this, uint* data, int end);
    // the inner event type is *data and the payload follows at data+4.
    // ⚠ This MUST differ from DispatchSmartBoxEventPattern — they were once an
    // identical copy-paste, which deduped this hook away (it never installed, so
    // combat events were never seen). Verified unique vs acclient.exe.
    private static readonly byte?[] DispatchGameEventPattern =
    [
        0x81, 0xEC, 0xC0, 0x01, 0x00, 0x00, 0x53, 0x55,
        0x8B, 0xAC, 0x24, 0xD0, 0x01, 0x00, 0x00, 0x56,
        0x57, 0x8B, 0xF9, 0x8B, 0x8C, 0x24, 0xD4, 0x01,
        0x00, 0x00, 0x8B, 0x11, 0x8B, 0xC1, 0x8D, 0x34,
        0x29, 0x83, 0xC1, 0x04
    ];

    private static IntPtr _originalDispatchSmartBoxEventPtr;
    private static IntPtr _originalDispatchGameEventPtr;
    private static string _statusMessage = "Not probed yet.";

    public static bool IsInstalled { get; private set; }
    public static string StatusMessage => _statusMessage;

    private static readonly object _initLock = new();
    public static void Initialize()
    {
        lock (_initLock)
        {
            if (IsInstalled)
                return;

            if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
            {
                _statusMessage = "acclient.exe not available.";
                return;
            }

            try
            {
                unsafe
                {
                    var hookedAddresses = new HashSet<IntPtr>();

                    // Hook SmartBox Event
                    int sbOff = PatternScanner.FindPattern(textSection.Bytes, DispatchSmartBoxEventPattern);
                    if (sbOff >= 0)
                    {
                        IntPtr sbAddr = new IntPtr(textSection.TextBaseVa + sbOff);
                        if (hookedAddresses.Add(sbAddr))
                        {
                            delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint> pSbDetour = &DispatchSmartBoxEventDetour;
                            MinHook.Hook(sbAddr, (IntPtr)pSbDetour, out _originalDispatchSmartBoxEventPtr);
                            RynthLog.Verbose($"Compat: smartbox hook ready @ 0x{sbAddr.ToInt32():X8}");
                        }
                    }
                    else RynthLog.Compat("Compat: smartbox pattern not found.");

                    // Hook Game Event
                    int geOff = PatternScanner.FindPattern(textSection.Bytes, DispatchGameEventPattern);
                    if (geOff >= 0)
                    {
                        IntPtr geAddr = new IntPtr(textSection.TextBaseVa + geOff);
                        if (hookedAddresses.Add(geAddr))
                        {
                            delegate* unmanaged[Thiscall]<IntPtr, IntPtr, int, uint> pGeDetour = &DispatchGameEventDetour;
                            MinHook.Hook(geAddr, (IntPtr)pGeDetour, out _originalDispatchGameEventPtr);
                            _gameEventHookInstalled = true;
                            RynthLog.Verbose($"Compat: game-event hook ready @ 0x{geAddr.ToInt32():X8}");
                        }
                        else RynthLog.Compat("Compat: game-event pattern matched already-hooked address.");
                    }
                    else RynthLog.Compat("Compat: game-event pattern not found.");
                }

                IsInstalled = true;
                _statusMessage = "Hooks installed.";
            }
            catch (Exception ex)
            {
                _statusMessage = ex.Message;
                RynthLog.Compat($"Compat: hooks failed - {ex.Message}");
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static unsafe uint DispatchSmartBoxEventDetour(IntPtr thisPtr, IntPtr blob)
    {
        MainThreadGuard.RecordIfFirst();
        RecursionGuard.Tick("SmartBoxHooks.DispatchSmartBoxEvent");
        var pOriginal = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint>)_originalDispatchSmartBoxEventPtr;

        if (!LoginLifecycleHooks.HasObservedLoginComplete)
        {
            try { CharacterCaptureHooks.ProcessPotentialCharacterMessage(blob, isGameEvent: false); } catch { }
            return pOriginal(thisPtr, blob);
        }

        SmartBoxEventInfo info = ReadSmartBoxEventInfo(blob);
        DiagEvent("SB", false, blob, info);
        uint status = pOriginal(thisPtr, blob);

        try
        {
            ObjectFreshness.Note(info.Opcode, info.RawObjectId, Environment.TickCount64);   // ghost check (RemoveClientObject)
            if (info.Opcode == PositionUpdateOpcode && info.RawObjectId != 0)
                NotePlayerMotionEcho(blob, info);
            TryQueueHealthUpdate(blob, info);
            TryQueueDamageEvent(blob, info);

            // Always on (no server-message capture needed), after AC handled the message:
            //  0xF747 ObjectDelete [opcode][objectId]: the SERVER deleted it (AutoId re-arms it
            //   and its kept identify data goes), unlike the client's own view releases;
            //  0xF751 PlayerTeleport: a teleport starts, so queued game actions hold for the
            //   settle window from this moment (WorldTransitionState), whatever the per-tick
            //   teleport_in_progress sample catches.
            if (info.Opcode == ServerObjectDeleteOpcode && info.RawObjectId != 0)
                AutoIdService.OnServerDelete(info.RawObjectId);
            else if (info.Opcode == PlayerTeleportOpcode)
                PlayerLifecycleLog.OnTeleportMessage();

            if (info.RawObjectId != 0 &&
                (info.Opcode == PositionUpdateOpcode ||
                 info.Opcode == PlayerPositionUpdateOpcode ||
                 info.Opcode == VectorUpdateOpcode ||
                 info.Opcode == UpdateObjectOpcode))
            {
                PluginManager.QueueUpdateObject(info.RawObjectId);
            }
        }
        catch { }

        return status;
    }

    // UIQueueManager::ProcessNetBlobData(this, uint* data, int size). thiscall:
    // `this` in ECX (thisPtr), then the two stack args (data, size). The inner
    // GameEvent type is *data; the event payload follows at data+4. `size` is the
    // payload byte LENGTH — verified against the live binary: the hooked fn's
    // prologue computes its own bound as `lea esi,[data+size]`. NOTE: this is a
    // DIFFERENT function/shape than DispatchSmartBoxEvent — must keep the 2nd
    // stack arg or the thiscall callee-cleanup imbalances the stack and crashes AC.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static unsafe uint DispatchGameEventDetour(IntPtr thisPtr, IntPtr data, int size)
    {
        MainThreadGuard.RecordIfFirst();
        // Engine-side BusyCount watchdog: fires on every inbound game event
        // while in-world (AC's main thread). Self-heals busy-count desync.
        try { BusyCountHooks.CheckWatchdog(); } catch { }
        RecursionGuard.Tick("SmartBoxHooks.DispatchGameEvent");
        var pOriginal = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, int, uint>)_originalDispatchGameEventPtr;

        EnchantmentChange? enchantment = null;
        bool answered = false;
        try
        {
            // Reject undersized payloads instead of clamping up: an inflated
            // bound would let parsers read past the real payload (uncatchable
            // NativeAOT AV on a page boundary).
            if (data != IntPtr.Zero && size >= 4)
            {
                uint eventType = unchecked((uint)Marshal.ReadInt32(data));
                int len = size > 0x4000 ? 0x4000 : size;
                // Property messages (2026-09-30): the identify reply, PlayerDescription and every
                // UpdateProperty* message pass through here, from their type on. `size` is the
                // real length (the function bounds itself with data+size), so the two large ones
                // get all of it (PlayerDescription runs to ~29 KB) instead of the 16 KB clamp.
                ParsePropertyMessage(eventType, data, size > 0x10000 ? 0x10000 : size);
                ParseGameEvent(eventType, data, (uint)len);
                if ((eventType >= 0x02C2 && eventType <= 0x02C8) || eventType == 0x0312)
                    enchantment = CaptureEnchantmentEvent(eventType, data, (uint)len);
                // v85: a prompt a plugin armed an answer for is answered here instead of
                // opening AC's dialog.
                if (eventType == ConfirmationResponder.EventConfirmationRequest)
                    answered = ConfirmationResponder.OnRequest(data, (uint)len);
            }
        }
        catch { }

        // AC's ConfirmationRequest handler returns 0 on every path, so an answered prompt
        // returns the same without reaching it.
        if (answered)
            return 0;

        uint status = pOriginal(thisPtr, data, size);

        // Enchantment events are queued AFTER the original ran: by then AC's
        // CEnchantmentRegistry holds the change (ClientMagicSystem::Handle_Magic__*
        // update it synchronously), so a plugin that re-reads the registry when the
        // event reaches it on the pump sees the new state. Queue only, never call
        // plugins from here (AC's main thread).
        if (enchantment != null)
        {
            try { FlushEnchantmentEvent(enchantment); } catch { }
        }

        return status;
    }

    // spike/decal-bridge: set while a game event from the Decal bridge is parsed on the
    // plugin pump thread, where the main-thread-only live reads must not run.
    [ThreadStatic] private static bool _fromBridge;

    /// <summary>
    /// spike/decal-bridge: a game event taken from Decal's raw server message instead of
    /// the ProcessNetBlobData hook. <paramref name="data"/> is [eventType][payload], as the
    /// hook sees it. Runs on the pump thread after AC has already applied the event, so an
    /// enchantment is queued at once, and a purge (no ids on the wire) can't be diffed.
    /// </summary>
    internal static unsafe void ProcessBridgeGameEvent(byte* data, int size)
    {
        if (data == null || size < 4)
            return;
        uint eventType = *(uint*)data;
        int len = size > 0x4000 ? 0x4000 : size;
        _fromBridge = true;
        try
        {
            ParseGameEvent(eventType, (IntPtr)data, (uint)len);
            if ((eventType >= 0x02C2 && eventType <= 0x02C8) && eventType != 0x02C6)
            {
                EnchantmentChange? c = CaptureEnchantmentEvent(eventType, (IntPtr)data, (uint)len);
                if (c != null)
                    FlushEnchantmentEvent(c);
            }
        }
        catch { }
        finally { _fromBridge = false; }
    }

    private static int _geEventLogCount;
    private static int _hpReadLogCount;

    // Feeds the property caches (AppraisalHooks, PropertyUpdateHooks) from the messages that
    // carry properties. Parsing only: nothing here touches AC state. Each parser is
    // bounds-checked against `size` and ignores a message it can't read.
    private static void ParsePropertyMessage(uint eventType, IntPtr data, int size)
    {
        try
        {
            if (eventType == PropertyWire.EventIdentifyObjectResponse)
                AppraisalHooks.OnIdentifyWire(data, size);
            else if (eventType == PropertyWire.EventPlayerDescription)
                PropertyUpdateHooks.OnPlayerDescriptionWire(data, size);
            else if (PropertyWire.IsPropertyUpdateOpcode(eventType))
                PropertyUpdateHooks.OnUpdateWire(data, size);
        }
        catch { }
    }

    // Parse an inbound GameEvent for the events combat cares about. `data` points
    // at the event blob: inner type @ +0, payload @ +4. `size` bounds reads.
    private static void ParseGameEvent(uint eventType, IntPtr data, uint size)
    {
        // Diagnostic: surface the first handful of combat-range events so the hook
        // can be verified live and any offset corrected from real bytes. Capped.
        if (eventType >= 0x0180 && eventType <= 0x01E0 && _geEventLogCount < 40)
        {
            _geEventLogCount++;
            uint d1 = size >= 8  ? unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, 4)))  : 0;
            uint d2 = size >= 12 ? unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, 8)))  : 0;
            uint d3 = size >= 16 ? unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, 12))) : 0;
            RynthLog.Compat($"GE evt=0x{eventType:X4} len={size} p=[{d1:X8} {d2:X8} {d3:X8}]");
        }

        // v75: the character's titles (0x0029 at login, 0x002B per new title; see CharacterTitles).
        if (CharacterTitles.IsTitleEvent(eventType))
        {
            try { CharacterTitles.OnGameEvent(eventType, data, size, ClientHelperHooks.GetPlayerId()); } catch { }
            return;
        }

        // v72: the player-to-player trade events (0x01FD-0x0208; see PlayerTrade).
        if (PlayerTrade.IsTradeEvent(eventType))
        {
            PlayerTrade.OnGameEvent(eventType, data, size);
            return;
        }

        switch (eventType)
        {
            case 0x01C0: // UpdateHealth: [targetId u32][ratio f32]
                if (size >= 12)
                {
                    uint targetId = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, 4)));
                    float ratio = Marshal.PtrToStructure<float>(IntPtr.Add(data, 8));
                    uint maxHealth = 0, currentHealth = 0;
                    // We're on AC's main thread here, so read the creature's REAL
                    // MaxHealth straight from its qualities (SEH-guarded, appraisal-
                    // free) — fixes the bogus 50 stub for mobs the char can't assess.
                    // The plugin QueryHealth()s its combat target on lock, which is
                    // what makes this fire for the mobs it's actually fighting.
                    // Falls back to the wire-parsed appraisal cache if the read fails.
                    uint realMax = 0;
                    bool gotReal = !_fromBridge && ClientObjectHooks.TryReadCreatureMaxHealth(targetId, out realMax) && realMax > 0;
                    if (gotReal)
                        maxHealth = realMax;
                    else if (ObjectQualityCache.TryGetCreatureVitals(targetId, out CreatureVitals exact) && exact.MaxHealth > 0)
                        maxHealth = exact.MaxHealth;
                    if (maxHealth > 0)
                        currentHealth = (uint)Math.Round(maxHealth * Math.Clamp(ratio, 0f, 1f));
                    if (_hpReadLogCount < 25)
                    {
                        _hpReadLogCount++;
                        RynthLog.Compat($"Creature HP id=0x{targetId:X8}: sehRead={(gotReal ? realMax.ToString() : "none")} used={maxHealth} ratio={ratio:0.00}");
                    }
                    PluginManager.QueueUpdateHealth(targetId, ratio, currentHealth, maxHealth);
                }
                break;

            case 0x01B1: // AttackerNotification (we hit)
            case 0x01B2: // DefenderNotification (we're hit)
                ParseGameEventDamage(eventType, data, size);
                break;

            case 0x01AD: // KillerNotification — death message string (we got a kill)
            {
                string? msg = ReadString16Latin1(data, 4, size);
                if (!string.IsNullOrEmpty(msg))
                {
                    if (_killNotifyLogCount < 12)
                    {
                        _killNotifyLogCount++;
                        RynthLog.Compat($"Combat: kill notify '{msg}'");
                    }
                    PluginManager.QueueKillNotification(msg);
                    ImGuiBackend.Hud.HudFeed.OnKillMessage(msg);   // kill burst
                }
                break;
            }

            case 0x01C7: // UseDone: [errorType u32] — server finished an action
                         // (cast/use) with WeenieError.None(0) = completed, or an
                         // error (0x1D = YoureTooBusy). Read-only observation: bump
                         // a monotonic counter so the plugin can pace combat casts
                         // on real server completion instead of a blind interval.
                         // This is the authoritative ACE Player.IsBusy lifecycle
                         // signal — it never touches client m_cBusy.
            {
                uint err = size >= 8 ? unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, 4))) : 0;
                int seq = System.Threading.Interlocked.Increment(ref _useDoneSeq);
                // v70: the sequence and its error code as one 64-bit value, so a reader
                // on the plugin pump never pairs one UseDone's number with another's code.
                System.Threading.Interlocked.Exchange(ref _useDoneLast, ((long)(uint)seq << 32) | err);
                // AC is about to take one off m_cBusy inline (Handle_Item::UseDone): the engine's
                // shadow and the plugins' mirrors take it too (2026-10-06, BusyCountHooks.OnUseDone).
                try { BusyCountHooks.OnUseDone(); } catch { }
                // Every one while RynthLog.ActionDoneTraceEnabled (the default; 2026-10-05: the
                // first 25 of a session couldn't show which completion a held cast was waiting on).
                if (RynthLog.ActionDoneTraceEnabled || _useDoneLogCount < 25)
                {
                    _useDoneLogCount++;
                    RynthLog.Compat($"UseDone seq={seq} err=0x{err:X}{(err == 0x1D ? " (too busy)" : err == 0 ? " (done)" : "")}");
                }
                break;
            }

            case 0x028A: // WeenieError: [errorType u32]. "Your spell fizzled",
                         // "You don't know that spell", ... — AC's own refusal lines.
                         // A cast that fizzles ends with this AND a UseDone(None).
                if (size >= 8)
                {
                    uint code = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, 4)));
                    RecordWeenieError(eventType, code, 0);
                    if (code == 0x0529) // TradeComplete (v72 trade state)
                        PlayerTrade.OnTradeComplete();
                }
                break;

            case 0x028B: // WeenieErrorWithString: [errorType u32][string16 text]
                if (size >= 8)
                    RecordWeenieError(eventType, unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, 4))), 0);
                break;

            case 0x0022: // InventoryPutObjInContainer: [item u32][container u32][placement][type].
                         // AutoId (Limited): an item that landed in the player's inventory with
                         // nothing cached is a candidate. Here, not on the server message stream,
                         // so it works with that stream off (engine.json / /rc netmsg off).
                if (size >= 12)
                {
                    uint putItem = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, 4)));
                    uint putContainer = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, 8)));
                    AutoIdService.OnPutInContainer(putItem, putContainer);
                    EquipmentTracker.OnContained(putItem, putContainer);
                }
                break;
            case 0x0023: // WieldObject: [item u32][EquipMask u32] (ACE GameEventWieldItem).
                if (size >= 12)
                    EquipmentTracker.OnWield(
                        unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, 4))),
                        unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, 8))));
                break;
            case 0x019A: // InventoryPutObjectIn3D: [item u32] (ACE GameEventItemServerSaysMoveItem): dropped.
                if (size >= 8)
                    EquipmentTracker.OnDropped(unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, 4))));
                break;
            case 0x00A0: // InventoryServerSaveFailed: [objectId u32][errorType u32] —
                         // the server refused an inventory change (wield, move, ...).
                         // Layout from UIQueueManager::ProcessNetBlobData's inline case
                         // @0x0055C012 (edx=[p], esi=[p+4]) and ACE's writer.
                if (size >= 12)
                    RecordWeenieError(eventType,
                        unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, 8))),
                        unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, 4))));
                break;
        }
    }

    // Monotonic count of inbound UseDone (0x1C7) events. The plugin records this
    // at cast time and re-reads it to detect the server finishing the cast.
    private static int _useDoneSeq;
    private static int _useDoneLogCount;
    // (seq << 32) | errorType of the most recent UseDone; 0 until the first one.
    private static long _useDoneLast;
    private static bool _gameEventHookInstalled;
    /// <summary>True once the game-event detour (UIQueueManager::ProcessNetBlobData) is installed.</summary>
    public static bool IsGameEventHookInstalled => _gameEventHookInstalled;
    public static int GetUseDoneSeq() => System.Threading.Volatile.Read(ref _useDoneSeq);

    /// <summary>
    /// v70: the most recent UseDone's sequence number (as GetUseDoneSeq counts them) and its
    /// WeenieError code (0 = the action completed). Any thread. False when the game-event
    /// hook isn't installed; seq 0 = no UseDone yet this session.
    /// </summary>
    public static bool TryGetLastUseDone(out int seq, out uint error)
    {
        long v = System.Threading.Interlocked.Read(ref _useDoneLast);
        seq = unchecked((int)(v >> 32));
        error = unchecked((uint)v);
        return _gameEventHookInstalled;
    }

    // ── Weenie errors (v70) ─────────────────────────────────────────────
    // The last refusal the server sent: WeenieError (0x028A), WeenieErrorWithString
    // (0x028B) or InventoryServerSaveFailed (0x00A0, with the item id). Written on AC's
    // main thread, read from the plugin pump: four fields, so a lock (never contended).
    private static readonly object _weenieErrorLock = new();
    private static int _weenieErrorSeq;
    private static uint _weenieErrorCode, _weenieErrorEvent, _weenieErrorObject;
    private static int _weenieErrorLogCount;

    private static void RecordWeenieError(uint eventType, uint code, uint objectId)
    {
        int seq;
        lock (_weenieErrorLock)
        {
            seq = ++_weenieErrorSeq;
            _weenieErrorCode = code;
            _weenieErrorEvent = eventType;
            _weenieErrorObject = objectId;
        }
        if (_weenieErrorLogCount < 25)
        {
            _weenieErrorLogCount++;
            RynthLog.Compat($"WeenieError seq={seq} evt=0x{eventType:X3} err=0x{code:X4} obj=0x{objectId:X8}");
        }
    }

    /// <summary>
    /// v70: the most recent server refusal (see RecordWeenieError). seq counts them (0 = none
    /// yet); eventType says which message carried it; objectId is the item for 0x00A0, else 0.
    /// Any thread. False when the game-event hook isn't installed.
    /// </summary>
    public static bool TryGetLastWeenieError(out int seq, out uint error, out uint eventType, out uint objectId)
    {
        lock (_weenieErrorLock)
        {
            seq = _weenieErrorSeq;
            error = _weenieErrorCode;
            eventType = _weenieErrorEvent;
            objectId = _weenieErrorObject;
        }
        return _gameEventHookInstalled;
    }

    // ── Enchantment events (v70) ────────────────────────────────────────
    // The player's enchantment changes arrive as these GameEvents (all dispatched by
    // UIQueueManager::ProcessNetBlobData, the function this file hooks; the case targets
    // were traced to CM_Magic::DispatchUI_* in the Chorizite map):
    //   0x02C2 UpdateEnchantment            Enchantment
    //   0x02C4 UpdateMultipleEnchantments   u32 count + Enchantment[]
    //   0x02C3 RemoveEnchantment            LayeredSpellId (u16 spell, u16 layer)
    //   0x02C5 RemoveMultipleEnchantments   u32 count + LayeredSpellId[]
    //   0x02C7 DispelEnchantment            LayeredSpellId (silent removal)
    //   0x02C8 DispelMultipleEnchantments   u32 count + LayeredSpellId[]
    //   0x02C6 PurgeEnchantments            (none: all but vitae go, e.g. on death)
    //   0x0312 PurgeBadEnchantments         (none: the harmful ones go)
    // The purges carry no ids, so the player's registry is read just before and just after
    // AC applies them and the spells that disappeared are reported. Removals report the spell
    // id (layer stripped), which is what OnEnchantmentRemoved's plugins key on.
    //
    // Enchantment on the wire (ACE's writer, 60 bytes, +4 when HasSpellSetId != 0):
    //   +0 u16 spellId  +2 u16 layer  +4 u16 category  +6 u16 hasSpellSetId  +8 u32 power
    //   +12 f64 startTime  +20 f64 duration  +28 u32 caster  +32 f32 degradeMod
    //   +36 f32 degradeLimit  +40 f64 lastDegraded  +48 u32 statModType  +52 u32 key
    //   +56 f32 value  [+60 u32 spellSetId]
    private const int EnchantmentWireSize = 60;
    private const int MaxEnchantmentsPerEvent = 256;
    private static int _enchantmentEventLogCount;

    private sealed class EnchantmentChange
    {
        public uint EventType;
        public bool Added;
        public int Count;
        public uint[] SpellIds = Array.Empty<uint>();
        public double[] Durations = Array.Empty<double>();
        /// <summary>Purges: the player's spell ids before AC applied it (null = unreadable).</summary>
        public uint[]? Before;
    }

    private static EnchantmentChange? CaptureEnchantmentEvent(uint eventType, IntPtr data, uint size)
    {
        // Payload starts at data+4; `size` counts the 4-byte event type too.
        switch (eventType)
        {
            case 0x02C2: // UpdateEnchantment
            {
                if (size < 4 + EnchantmentWireSize) return null;
                var c = new EnchantmentChange { EventType = eventType, Added = true, Count = 1, SpellIds = new uint[1], Durations = new double[1] };
                ReadWireEnchantment(data, 4, out c.SpellIds[0], out c.Durations[0]);
                return c.SpellIds[0] != 0 ? c : null;
            }

            case 0x02C4: // UpdateMultipleEnchantments
            {
                if (size < 8) return null;
                uint count = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, 4)));
                if (count == 0 || count > MaxEnchantmentsPerEvent) return null;
                var c = new EnchantmentChange { EventType = eventType, Added = true, SpellIds = new uint[count], Durations = new double[count] };
                uint off = 8;
                for (uint i = 0; i < count; i++)
                {
                    if (off + EnchantmentWireSize > size) break;
                    ushort hasSet = unchecked((ushort)Marshal.ReadInt16(IntPtr.Add(data, (int)off + 6)));
                    ReadWireEnchantment(data, (int)off, out uint spellId, out double duration);
                    off += EnchantmentWireSize + (hasSet != 0 ? 4u : 0u);
                    if (spellId == 0) continue;
                    c.SpellIds[c.Count] = spellId;
                    c.Durations[c.Count] = duration;
                    c.Count++;
                }
                return c.Count > 0 ? c : null;
            }

            case 0x02C3: // RemoveEnchantment
            case 0x02C7: // DispelEnchantment
            {
                if (size < 8) return null;
                uint spellId = (uint)(ushort)Marshal.ReadInt16(IntPtr.Add(data, 4));
                if (spellId == 0) return null;
                return new EnchantmentChange { EventType = eventType, Count = 1, SpellIds = new[] { spellId } };
            }

            case 0x02C5: // RemoveMultipleEnchantments
            case 0x02C8: // DispelMultipleEnchantments
            {
                if (size < 8) return null;
                uint count = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, 4)));
                if (count == 0 || count > MaxEnchantmentsPerEvent) return null;
                var c = new EnchantmentChange { EventType = eventType, SpellIds = new uint[count] };
                for (uint i = 0; i < count && 8 + i * 4 + 4 <= size; i++)
                {
                    uint spellId = (uint)(ushort)Marshal.ReadInt16(IntPtr.Add(data, (int)(8 + i * 4)));
                    if (spellId != 0) c.SpellIds[c.Count++] = spellId;
                }
                return c.Count > 0 ? c : null;
            }

            case 0x02C6: // PurgeEnchantments
            case 0x0312: // PurgeBadEnchantments
                return new EnchantmentChange { EventType = eventType, Before = SnapshotPlayerSpellIds() };
        }
        return null;
    }

    private static void ReadWireEnchantment(IntPtr data, int off, out uint spellId, out double duration)
    {
        spellId = (uint)(ushort)Marshal.ReadInt16(IntPtr.Add(data, off));
        duration = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(IntPtr.Add(data, off + 20)));
    }

    private static void FlushEnchantmentEvent(EnchantmentChange c)
    {
        // The registry just changed: refresh the off-thread player snapshot NOW (we
        // are on the main thread, after AC applied the change), before the event is
        // queued, so a plugin re-reading on the event sees the new state as before.
        EnchantmentHooks.RefreshPlayerSnapshotNow();

        if (c.Before != null)
        {
            // A purge: whatever left the registry.
            uint[]? after = SnapshotPlayerSpellIds();
            if (after == null) return;
            var remaining = new HashSet<uint>(after);
            var reported = new HashSet<uint>();
            foreach (uint id in c.Before)
                if (!remaining.Contains(id) && reported.Add(id))
                    PluginManager.QueueEnchantmentRemoved(id);
            LogEnchantmentEvent(c.EventType, reported.Count, 0);
            return;
        }

        for (int i = 0; i < c.Count; i++)
        {
            if (c.Added) PluginManager.QueueEnchantmentAdded(c.SpellIds[i], c.Durations[i]);
            else PluginManager.QueueEnchantmentRemoved(c.SpellIds[i]);
        }
        LogEnchantmentEvent(c.EventType, c.Count, c.SpellIds[0]);
    }

    private static void LogEnchantmentEvent(uint eventType, int count, uint firstSpellId)
    {
        if (_enchantmentEventLogCount >= 25) return;
        _enchantmentEventLogCount++;
        RynthLog.Compat($"Enchantment evt=0x{eventType:X3} count={count} first={firstSpellId}");
    }

    /// <summary>The player's enchantment spell ids (main thread here), or null when unreadable.</summary>
    private static unsafe uint[]? SnapshotPlayerSpellIds()
    {
        const int Max = 512;
        uint* ids = stackalloc uint[Max];
        double* expiry = stackalloc double[Max];
        int n = EnchantmentHooks.ReadPlayerEnchantments(ids, expiry, Max);
        if (n < 0) return null;
        var result = new uint[n];
        for (int i = 0; i < n; i++) result[i] = ids[i];
        return result;
    }

    // AttackerNotification (0x01B1) / DefenderNotification (0x01B2). Payload at
    // data+4: string16 name, u32 damageType, f64 percent, u32 damage,
    // [u32 damageLocation (defender only),] u32 crit, …. Read-only byte math.
    private static void ParseGameEventDamage(uint eventType, IntPtr data, uint size)
    {
        if (size < 24) return;
        bool attacker = eventType == 0x01B1;
        // "X hits you": the nameplates' engaged filter keys attackers by name (no id on the wire).
        if (!attacker)
            ImGuiBackend.Hud.MonsterHudData.RecordAttacker(ReadString16Latin1(data, 4, size));

        int nameLen = (ushort)Marshal.ReadInt16(IntPtr.Add(data, 4));
        int strBlock = ((2 + nameLen + 3) / 4) * 4;       // u16 len + chars, padded to a multiple of 4
        int off = 4 + strBlock;                            // → damageType
        int critOff = off + (attacker ? 16 : 20);          // type(4)+percent(8)+damage(4)[+dmgLoc(4)]
        if (critOff + 4 > size) return;

        uint damageType = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, off)));
        double share = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(IntPtr.Add(data, off + 4)));   // damage / the monster's max health
        uint damage = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, off + 12)));
        uint crit = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, critOff)));
        if (damage == 0 || damage > 1_000_000) return;

        if (_damageEventLogCount < 12)
        {
            _damageEventLogCount++;
            RynthLog.Compat($"Combat: damage evt 0x{eventType:X3} dmg={damage} crit={crit} atk={attacker} len={size}");
        }
        PluginManager.QueueCombatDamage(damage, damageType, crit != 0, attacker);
        // RynthVision combat text (and the plate of a monster with no health feed): the attacker event names the monster hit.
        ImGuiBackend.Hud.HudFeed.OnDamageEvent(attacker, attacker ? ReadString16Latin1(data, 4, size) : null, damage, crit != 0, attacker ? share : 0);
    }

    private static SmartBoxEventInfo ReadSmartBoxEventInfo(IntPtr blob)
    {
        if (blob == IntPtr.Zero)
            return default;

        try
        {
            uint blobSize = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(blob, NetBlobBufSizeOffset)));
            if (blobSize < sizeof(uint))
                return new SmartBoxEventInfo(0, 0, blobSize);

            IntPtr payloadPtr = Marshal.ReadIntPtr(IntPtr.Add(blob, NetBlobBufPtrOffset));
            if (payloadPtr == IntPtr.Zero)
                return new SmartBoxEventInfo(0, 0, blobSize);

            uint opcode = unchecked((uint)Marshal.ReadInt32(payloadPtr));
            uint rawObjectId = blobSize >= 8
                ? unchecked((uint)Marshal.ReadInt32(IntPtr.Add(payloadPtr, sizeof(uint))))
                : 0;
            return new SmartBoxEventInfo(opcode, rawObjectId, blobSize);
        }
        catch
        {
            return default;
        }
    }

    // UpdateMotion (0xF74C) for the player with the autonomous flag set: the server's answer to a
    // MoveToState from this client (layout: opcode, guid, u16 instance, u16 movement seq, u16
    // server-control seq, u8 autonomous at +14). Counted for the pre-cast stop (PreCastStopRules).
    private static void NotePlayerMotionEcho(IntPtr blob, SmartBoxEventInfo info)
    {
        if (info.BlobSize < 16) return;
        uint player = ClientHelperHooks.CachedPlayerId;
        if (player == 0 || info.RawObjectId != player) return;
        try
        {
            IntPtr payloadPtr = Marshal.ReadIntPtr(IntPtr.Add(blob, NetBlobBufPtrOffset));
            if (payloadPtr == IntPtr.Zero) return;
            if (Marshal.ReadByte(IntPtr.Add(payloadPtr, 14)) != 0)
                AcMainThreadQueue.NotePlayerMotionEcho();
        }
        catch { }
    }

    private static void TryQueueHealthUpdate(IntPtr blob, SmartBoxEventInfo info)
    {
        if (info.Opcode != 0x01C0 || info.BlobSize < 12 || blob == IntPtr.Zero)
            return;

        try
        {
            IntPtr payloadPtr = Marshal.ReadIntPtr(IntPtr.Add(blob, NetBlobBufPtrOffset));
            if (payloadPtr == IntPtr.Zero)
                return;

            uint targetId = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(payloadPtr, 4)));
            float ratio = Marshal.PtrToStructure<float>(IntPtr.Add(payloadPtr, 8));

            uint maxHealth = 0;
            uint currentHealth = 0;
            // Appraisal-only: emit an absolute health pair ONLY from a real
            // appraisal's wire-parsed CreatureProfile (id-keyed). With no
            // appraisal, leave max=0 so the UI falls back to a % — the
            // pointer-cache and creature Inq maxes are unreliable on ACE and
            // fabricated wildly wrong values (e.g. 50/50 for a 190-hp mob).
            if (ObjectQualityCache.TryGetCreatureVitals(targetId, out CreatureVitals exact) && exact.MaxHealth > 0)
            {
                maxHealth = exact.MaxHealth;
                currentHealth = (uint)Math.Round(maxHealth * Math.Clamp(ratio, 0f, 1f));
            }

            PluginManager.QueueUpdateHealth(targetId, ratio, currentHealth, maxHealth);
        }
        catch
        {
        }
    }

    private static int _damageEventLogCount;

    // AttackerNotification (0x01B1, you hit) / DefenderNotification (0x01B2, you're
    // hit) carry the EXACT per-hit damage + crit — the only client-side source of
    // real damage numbers (chat is flavor text; health deltas miss one-shots).
    // These arrive as GameEvents whose blob envelope is
    // [recipientId][sequence][eventType][payload] (eventType at +8), while
    // SmartBox-dispatched events put eventType at +0. Detect the eventType at
    // either offset, then parse the payload that follows:
    //   string16 name (u16 charCount + cp1252 1-byte chars, padded so (2+len)%4==0)
    //   u32 damageType, f64 percent, u32 damage, [u32 damageLocation (defender),]
    //   u32 crit, u64 attackConditions
    // Pure read-only byte math (no allocation) to stay detour-safe.
    private static void TryQueueDamageEvent(IntPtr blob, SmartBoxEventInfo info)
    {
        if (blob == IntPtr.Zero || info.BlobSize < 24)
            return;

        try
        {
            IntPtr payloadPtr = Marshal.ReadIntPtr(IntPtr.Add(blob, NetBlobBufPtrOffset));
            if (payloadPtr == IntPtr.Zero)
                return;

            uint et0 = unchecked((uint)Marshal.ReadInt32(payloadPtr));
            uint et8 = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(payloadPtr, 8)));

            int nameOff;
            uint eventType;
            if (et0 == 0x01B1 || et0 == 0x01B2) { eventType = et0; nameOff = 4; }      // SmartBox-style
            else if (et8 == 0x01B1 || et8 == 0x01B2) { eventType = et8; nameOff = 12; } // GameEvent envelope
            else return;

            bool attacker = eventType == 0x01B1;
            int nameLen = (ushort)Marshal.ReadInt16(IntPtr.Add(payloadPtr, nameOff));
            int strBlock = ((2 + nameLen + 3) / 4) * 4;     // u16 len + chars, padded to a multiple of 4
            int off = nameOff + strBlock;                    // → damageType
            int critOff = off + (attacker ? 16 : 20);        // type(4)+percent(8)+damage(4)[+dmgLoc(4)]
            if (critOff + 4 > info.BlobSize)
                return;

            uint damageType = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(payloadPtr, off)));
            double share = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(IntPtr.Add(payloadPtr, off + 4)));   // damage / the monster's max health
            uint damage = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(payloadPtr, off + 12)));
            uint crit = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(payloadPtr, critOff)));
            if (damage == 0 || damage > 1_000_000)
                return;

            if (_damageEventLogCount < 12)
            {
                _damageEventLogCount++;
                RynthLog.Compat($"Combat: damage evt 0x{eventType:X3} nameOff={nameOff} dmg={damage} crit={crit} atk={attacker} blob={info.BlobSize}");
            }

            PluginManager.QueueCombatDamage(damage, damageType, crit != 0, attacker);
            ImGuiBackend.Hud.HudFeed.OnDamageEvent(attacker, attacker ? ReadString16Latin1(payloadPtr, nameOff, info.BlobSize) : null, damage, crit != 0, attacker ? share : 0);
        }
        catch
        {
        }
    }

    private static int _killNotifyLogCount;

    // KillerNotification (0x01AD) is sent to the player ONLY when they kill
    // something, at the lethal hit — before the death animation plays out and
    // the creature reclassifies to a corpse. That makes it the EARLIEST
    // reliable "target dead" signal: the health=0 update and the Monster->Corpse
    // flip both land at/after the corpse swap (ACE delays CreateCorpse by the
    // full death-animation length), so combat that paces on those would burn a
    // second full cast at an already-dead mob. Payload is a single string16 —
    // the formatted death message with the victim name embedded; there is NO
    // object id (GameEventKillerNotification just WriteString16L's the text).
    // We forward the string so the plugin can match it against its one active
    // target by name. Same envelope ambiguity as the damage events: eventType
    // at +0 (SmartBox-style) or +8 (GameEvent envelope); the string16 follows
    // immediately after the eventType u32.
    private static void TryQueueKillNotification(IntPtr blob, SmartBoxEventInfo info)
    {
        if (blob == IntPtr.Zero || info.BlobSize < 6)
            return;

        try
        {
            IntPtr payloadPtr = Marshal.ReadIntPtr(IntPtr.Add(blob, NetBlobBufPtrOffset));
            if (payloadPtr == IntPtr.Zero)
                return;

            uint et0 = unchecked((uint)Marshal.ReadInt32(payloadPtr));
            uint et8 = info.BlobSize >= 12 ? unchecked((uint)Marshal.ReadInt32(IntPtr.Add(payloadPtr, 8))) : 0;

            int strOff;
            if (et0 == 0x01AD) strOff = 4;       // SmartBox-style: eventType at +0
            else if (et8 == 0x01AD) strOff = 12; // GameEvent envelope: eventType at +8
            else return;

            string? deathMessage = ReadString16Latin1(payloadPtr, strOff, info.BlobSize);
            if (string.IsNullOrEmpty(deathMessage))
                return;

            if (_killNotifyLogCount < 12)
            {
                _killNotifyLogCount++;
                RynthLog.Compat($"Combat: kill notify strOff={strOff} blob={info.BlobSize} msg='{deathMessage}'");
            }

            PluginManager.QueueKillNotification(deathMessage);
        }
        catch
        {
        }
    }

    // Reads an AC string16 (u16 char count + that many 1-byte cp1252 chars) at
    // the given payload offset, bounded by the blob size. Returns null on a
    // missing/oversize length so a corrupt field can't allocate wildly inside
    // this main-thread detour. High bytes are taken as Latin-1 (monster names
    // are ASCII in practice). One small string alloc per kill — the same
    // pattern ChatCallbackHooks already uses for inbound chat capture.
    private static string? ReadString16Latin1(IntPtr payloadPtr, int off, uint blobSize)
    {
        if (off < 0 || off + 2 > blobSize) return null;
        int len = (ushort)Marshal.ReadInt16(IntPtr.Add(payloadPtr, off));
        if (len <= 0 || len > 256) return null;
        if (off + 2 + len > blobSize) return null;

        Span<char> chars = stackalloc char[len];
        for (int i = 0; i < len; i++)
            chars[i] = (char)Marshal.ReadByte(IntPtr.Add(payloadPtr, off + 2 + i));
        return new string(chars);
    }

    // ── RE instrumentation (TEMPORARY) ───────────────────────────────────
    // Diagnose why combat GameEvents (damage 0x1B1/2, kill 0x1AC/AD, health
    // 0x1C0) don't reach the parsers on the user's Decal-coexistence client.
    // Logs three things, all capped: (1) detour ALIVE + busyness — proves the
    // hook fires and isn't on the wrong function; (2) any combat-range event's
    // first dwords (COMBAT) so we see the opcode + envelope offset; (3) for the
    // game-event detour, the first 30 events unfiltered (GE-ANY) so structure is
    // visible even if the opcode sits at an unexpected offset. Remove once fixed.
    private static int _geFire, _sbFire, _geCombat, _sbCombat, _geAny;

    private static unsafe void DiagEvent(string tag, bool isGe, IntPtr blob, SmartBoxEventInfo info)
    {
        int fired = isGe ? ++_geFire : ++_sbFire;
        if (fired == 1 || fired == 50 || fired % 500 == 0)
            RynthLog.Compat($"{tag}-ALIVE fired={fired} lastOp=0x{info.Opcode:X4} blob={info.BlobSize}");

        if (blob == IntPtr.Zero || info.BlobSize < 4)
            return;

        try
        {
            IntPtr p = Marshal.ReadIntPtr(IntPtr.Add(blob, NetBlobBufPtrOffset));
            if (p == IntPtr.Zero)
                return;

            uint sz = info.BlobSize;
            uint et0 = unchecked((uint)Marshal.ReadInt32(p));
            uint w1  = sz >= 8  ? unchecked((uint)Marshal.ReadInt32(IntPtr.Add(p, 4)))  : 0;
            uint et8 = sz >= 12 ? unchecked((uint)Marshal.ReadInt32(IntPtr.Add(p, 8)))  : 0;
            uint w3  = sz >= 16 ? unchecked((uint)Marshal.ReadInt32(IntPtr.Add(p, 12))) : 0;
            uint w4  = sz >= 20 ? unchecked((uint)Marshal.ReadInt32(IntPtr.Add(p, 16))) : 0;

            bool combat = (et0 >= 0x0180 && et0 <= 0x01E0) || (et8 >= 0x0180 && et8 <= 0x01E0);
            if (combat)
            {
                ref int c = ref (isGe ? ref _geCombat : ref _sbCombat);
                if (c < 50)
                {
                    c++;
                    RynthLog.Compat($"{tag}-COMBAT sz={sz} w=[{et0:X8} {w1:X8} {et8:X8} {w3:X8} {w4:X8}]");
                }
            }
            else if (isGe && _geAny < 30)
            {
                _geAny++;
                RynthLog.Compat($"GE-ANY sz={sz} w=[{et0:X8} {w1:X8} {et8:X8} {w3:X8} {w4:X8}]");
            }
        }
        catch { }
    }



    // WireParseIdentifyFromBlob / TryCacheVitalsFromIdentify removed 2026-05-18:
    // calling CombatActionHooks.TryParseIdentifyResponse from an [UnmanagedCallersOnly]
    // detour on AC's main thread triggers ObjectQualityCache dictionary growth
    // (allocation) for each newly-identified mob → GC during the reverse-P/Invoke
    // transition → RhpReversePInvokeAttachOrTrapThread2 STATUS_FAIL_FAST (silent
    // process death, no dialog). Mob health display falls back to ratio (%) mode;
    // absolute values can be restored via a pre-allocated pump-thread ring buffer.

    private readonly record struct SmartBoxEventInfo(uint Opcode, uint RawObjectId, uint BlobSize);
}

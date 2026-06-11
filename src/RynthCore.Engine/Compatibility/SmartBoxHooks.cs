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
            TryQueueHealthUpdate(blob, info);
            TryQueueDamageEvent(blob, info);

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

        try
        {
            // Reject undersized payloads instead of clamping up: an inflated
            // bound would let parsers read past the real payload (uncatchable
            // NativeAOT AV on a page boundary).
            if (data != IntPtr.Zero && size >= 4)
            {
                uint eventType = unchecked((uint)Marshal.ReadInt32(data));
                int len = size > 0x4000 ? 0x4000 : size;
                ParseGameEvent(eventType, data, (uint)len);
            }
        }
        catch { }

        return pOriginal(thisPtr, data, size);
    }

    private static int _geEventLogCount;
    private static int _hpReadLogCount;

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
                    bool gotReal = ClientObjectHooks.TryReadCreatureMaxHealth(targetId, out uint realMax) && realMax > 0;
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
                }
                break;
            }
        }
    }

    // AttackerNotification (0x01B1) / DefenderNotification (0x01B2). Payload at
    // data+4: string16 name, u32 damageType, f64 percent, u32 damage,
    // [u32 damageLocation (defender only),] u32 crit, …. Read-only byte math.
    private static void ParseGameEventDamage(uint eventType, IntPtr data, uint size)
    {
        if (size < 24) return;
        bool attacker = eventType == 0x01B1;

        int nameLen = (ushort)Marshal.ReadInt16(IntPtr.Add(data, 4));
        int strBlock = ((2 + nameLen + 3) / 4) * 4;       // u16 len + chars, padded to a multiple of 4
        int off = 4 + strBlock;                            // → damageType
        int critOff = off + (attacker ? 16 : 20);          // type(4)+percent(8)+damage(4)[+dmgLoc(4)]
        if (critOff + 4 > size) return;

        uint damageType = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, off)));
        uint damage = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, off + 12)));
        uint crit = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, critOff)));
        if (damage == 0 || damage > 1_000_000) return;

        if (_damageEventLogCount < 12)
        {
            _damageEventLogCount++;
            RynthLog.Compat($"Combat: damage evt 0x{eventType:X3} dmg={damage} crit={crit} atk={attacker} len={size}");
        }
        PluginManager.QueueCombatDamage(damage, damageType, crit != 0, attacker);
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

// ============================================================================
//  RynthCore.Engine - Compatibility/PlayerTrade.cs
//
//  Player-to-player trade (plugin API v72): the state of the trade window, read
//  from the server's trade GameEvents, and the trade actions, sent through the
//  retail client's own CM_Trade / ClientTradeSystem functions.
//
//  STATE  UIQueueManager::ProcessNetBlobData (SmartBoxHooks' existing game-event
//         detour, AC's main thread) hands every trade GameEvent to OnGameEvent
//         before AC handles it. No new hook. Payloads (event type at +0, payload
//         from +4), checked against ACE's writers (GameEvent*Trade.cs) and the
//         client's CM_Trade::DispatchUI_Recv_* readers:
//           0x01FD RegisterTrade        u32 initiator, u32 partner, f64 stamp
//           0x01FE OpenTrade            u32 objectId (ACE never sends it)
//           0x01FF CloseTrade           u32 EndTradeReason (1 normal, 2 combat, 0x51 cancelled)
//           0x0200 AddToTrade           u32 item, u32 side (1 you, 2 partner), u32 slot
//           0x0201 RemoveFromTrade      u32 item, u32 side (ACE never sends it)
//           0x0202 AcceptTrade          u32 who accepted
//           0x0203 DeclineTrade         u32 who declined
//           0x0205 ResetTrade           u32 who reset (both windows empty; also sent
//                                       to both sides after a completed trade)
//           0x0207 TradeFailure         u32 item, u32 WeenieError
//           0x0208 ClearTradeAcceptance (none)
//         plus WeenieError 0x0529 TradeComplete (counted). ACE sends the second
//         party RegisterTrade(initiator, initiator), so the partner is "the
//         initiator unless that's you, else the partner field".
//
//         The state lives in fixed arrays under one lock: the detour never
//         allocates and never calls a plugin. Plugins read a copy on their pump
//         (GetTradeState / GetTradeItems) and compare Generation / Sequence /
//         counters between reads; nothing is queued for them.
//
//  ACTIONS  Bound by byte pattern (tools/pe_pattern.py CHECK), VAs from the
//         Chorizite acclient map (+0x401000), verified on both acclient copies:
//           CM_Trade::Event_OpenTradeNegotiations(ulong)   0x006AE260  0x01F6
//           CM_Trade::Event_AddToTrade(ulong item, ulong loc) 0x006AE030 0x01F8
//           CM_Trade::Event_ResetTrade()                   0x006AE330  0x0204
//           CM_Trade::Event_CloseTradeNegotiations()       0x006AE140  0x01F7
//           ClientTradeSystem::AcceptTrade()   (thiscall)  0x0056E7A0  0x01FA
//           ClientTradeSystem::DeclineTrade()  (thiscall)  0x0056E7C0  0x01FB
//         The Event_* functions are cdecl, return bool, and only build and send
//         the game action (the same generated shape as Event_GetAndWieldItem).
//         Accept goes through ClientTradeSystem because CM_Trade::Event_AcceptTrade
//         packs the client's own Trade object (ClientTradeSystem+0x10); the wrapper
//         also sets that object's accepted flag (+0x38) the way the trade window's
//         Accept button does. Decline clears it the same way. The ClientTradeSystem
//         instance is the global ClientTradeSystem::GetTradeSystem returns
//         (0x0087174C), resolved by code xref.
//
//         Every action runs on AC's main thread: from any other thread it is queued
//         on AcMainThreadQueue (ActionKind.Trade) and the drain calls back in here.
// ============================================================================

using System;
using System.Runtime.InteropServices;

using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static unsafe class PlayerTrade
{
    // ── Actions ─────────────────────────────────────────────────────────────

    internal enum TradeOp : uint
    {
        Open = 1,
        Add = 2,
        Reset = 3,
        Accept = 4,
        Decline = 5,
        Close = 6,
    }

    private const int EventOpenTradeNegotiationsVa = 0x006AE260;
    private const int EventAddToTradeVa = 0x006AE030;
    private const int EventResetTradeVa = 0x006AE330;
    private const int EventCloseTradeNegotiationsVa = 0x006AE140;
    private const int TradeSystemAcceptTradeVa = 0x0056E7A0;
    private const int TradeSystemDeclineTradeVa = 0x0056E7C0;
    private const int TradeSystemGlobalVa = 0x0087174C;

    // The four CM_Trade events share one generated template (see Event_GiveObjectRequest in
    // ClientHelperHooks); what tells them apart is the arg-block size after the first inner
    // call (+0x08 one arg, +0x0C two, +0x04 none) and the opcode immediate at the end, so the
    // patterns run to that byte. Generated + verified unique via pe_pattern.py GEN against both
    // acclient copies (C:\Turbine and C:\Games\RynthCore\AcClient).
    private static readonly byte?[] PatEventOpenTradeNegotiations = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC6, 0x08, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x54, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x1C, 0xC7, 0x02, 0xF6 ];
    private static readonly byte?[] PatEventAddToTrade = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC6, 0x0C, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x54, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x1C, 0xC7, 0x02, 0xF8 ];
    private static readonly byte?[] PatEventResetTrade = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC6, 0x04, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x54, 0x24, 0x0C, 0xC7, 0x02, 0x04 ];
    private static readonly byte?[] PatEventCloseTradeNegotiations = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC6, 0x04, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x54, 0x24, 0x0C, 0xC7, 0x02, 0xF7 ];
    // ClientTradeSystem::AcceptTrade: mov eax,[ecx+10h]; mov dword [eax+38h],1; mov ecx,[ecx+10h];
    // push ecx; call Event_AcceptTrade; pop ecx; ret. DeclineTrade: the same flag write with 0,
    // then jmp Event_DeclineTrade. Unique at 8 bytes; run on to the call/jmp for margin.
    private static readonly byte?[] PatTradeSystemAcceptTrade = [ 0x8B, 0x41, 0x10, 0xC7, 0x40, 0x38, 0x01, 0x00, 0x00, 0x00, 0x8B, 0x49, 0x10, 0x51, 0xE8, null, null, null, null, 0x59, 0xC3 ];
    private static readonly byte?[] PatTradeSystemDeclineTrade = [ 0x8B, 0x41, 0x10, 0xC7, 0x40, 0x38, 0x00, 0x00, 0x00, 0x00, 0xE9 ];
    // mov [ClientTradeSystem::s_pTradeSystem], esi; call [import]; xor edi, edi (operand at +2).
    private static readonly byte?[] PatXrefTradeSystem = [ 0x89, 0x35, null, null, null, null, 0xFF, 0x15, 0xFC, 0x31, 0x79, 0x00, 0x33, 0xFF ];

    private static delegate* unmanaged[Cdecl]<uint, byte> _eventOpenTradeNegotiations;
    private static delegate* unmanaged[Cdecl]<uint, uint, byte> _eventAddToTrade;
    private static delegate* unmanaged[Cdecl]<byte> _eventResetTrade;
    private static delegate* unmanaged[Cdecl]<byte> _eventCloseTradeNegotiations;
    private static delegate* unmanaged[Thiscall]<IntPtr, void> _tradeSystemAcceptTrade;
    private static delegate* unmanaged[Thiscall]<IntPtr, void> _tradeSystemDeclineTrade;
    private static int _tradeSystemAddr;

    public static bool HasOpen => _eventOpenTradeNegotiations != null;
    public static bool HasAdd => _eventAddToTrade != null;
    public static bool HasReset => _eventResetTrade != null;
    public static bool HasClose => _eventCloseTradeNegotiations != null;
    public static bool HasAccept => _tradeSystemAcceptTrade != null;
    public static bool HasDecline => _tradeSystemDeclineTrade != null;
    public static bool ActionsAvailable => HasOpen && HasAdd && HasReset && HasClose && HasAccept && HasDecline;

    /// <summary>Bind the trade functions. Called from ClientHelperHooks.Probe (engine init).</summary>
    public static void Probe(AcClientTextSection text)
    {
        _eventOpenTradeNegotiations = (delegate* unmanaged[Cdecl]<uint, byte>)ResolveFn(HookResolver.Resolve(text, "PlayerTrade.Event_OpenTradeNegotiations", PatEventOpenTradeNegotiations, EventOpenTradeNegotiationsVa));
        _eventAddToTrade = (delegate* unmanaged[Cdecl]<uint, uint, byte>)ResolveFn(HookResolver.Resolve(text, "PlayerTrade.Event_AddToTrade", PatEventAddToTrade, EventAddToTradeVa));
        _eventResetTrade = (delegate* unmanaged[Cdecl]<byte>)ResolveFn(HookResolver.Resolve(text, "PlayerTrade.Event_ResetTrade", PatEventResetTrade, EventResetTradeVa));
        _eventCloseTradeNegotiations = (delegate* unmanaged[Cdecl]<byte>)ResolveFn(HookResolver.Resolve(text, "PlayerTrade.Event_CloseTradeNegotiations", PatEventCloseTradeNegotiations, EventCloseTradeNegotiationsVa));
        _tradeSystemAcceptTrade = (delegate* unmanaged[Thiscall]<IntPtr, void>)ResolveFn(HookResolver.Resolve(text, "PlayerTrade.ClientTradeSystem_AcceptTrade", PatTradeSystemAcceptTrade, TradeSystemAcceptTradeVa));
        _tradeSystemDeclineTrade = (delegate* unmanaged[Thiscall]<IntPtr, void>)ResolveFn(HookResolver.Resolve(text, "PlayerTrade.ClientTradeSystem_DeclineTrade", PatTradeSystemDeclineTrade, TradeSystemDeclineTradeVa));
        HookResolver.ResolveResult system = HookResolver.ResolveData(text, "PlayerTrade.ClientTradeSystem", PatXrefTradeSystem, 2, TradeSystemGlobalVa);
        _tradeSystemAddr = system.Source == HookResolver.ResolveSource.PatternScan ? system.Address.ToInt32() : 0;
        if (_tradeSystemAddr == 0)
        {
            // Without the instance Accept / Decline can't run; don't offer them.
            _tradeSystemAcceptTrade = null;
            _tradeSystemDeclineTrade = null;
        }
        RynthLog.Verbose($"Compat: player trade {(ActionsAvailable ? "ready" : "partly bound")} (open={HasOpen} add={HasAdd} reset={HasReset} close={HasClose} accept={HasAccept} decline={HasDecline}).");
    }

    // Fail closed: these send game actions, so only a unique pattern match binds. The resolver's
    // fixed-VA fallback (a pattern miss on some other client build) leaves the action unavailable.
    private static void* ResolveFn(HookResolver.ResolveResult r) =>
        r.Success && r.Source == HookResolver.ResolveSource.PatternScan ? (void*)r.Address : null;

    /// <summary>Ask <paramref name="targetId"/> to trade (0x01F6 OpenTradeNegotiations).</summary>
    public static bool Open(uint targetId)
    {
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return _eventOpenTradeNegotiations != null && targetId != 0
                && AcMainThreadQueue.EnqueueTrade((uint)TradeOp.Open, targetId, 0);
        if (_eventOpenTradeNegotiations == null || targetId == 0) return false;
        byte rv = _eventOpenTradeNegotiations(targetId);
        LogAction($"Event_OpenTradeNegotiations target=0x{targetId:X8} rv={rv}");
        return rv != 0;
    }

    /// <summary>Put one of your items in the trade window (0x01F8 AddToTrade). slot 0 = next free.</summary>
    public static bool Add(uint itemId, uint slot)
    {
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return _eventAddToTrade != null && itemId != 0
                && AcMainThreadQueue.EnqueueTrade((uint)TradeOp.Add, itemId, slot);
        if (_eventAddToTrade == null || itemId == 0) return false;
        byte rv = _eventAddToTrade(itemId, slot);
        LogAction($"Event_AddToTrade item=0x{itemId:X8} slot={slot} rv={rv}");
        return rv != 0;
    }

    /// <summary>Empty both sides of the window (0x0204 ResetTrade).</summary>
    public static bool Reset()
    {
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return _eventResetTrade != null && AcMainThreadQueue.EnqueueTrade((uint)TradeOp.Reset, 0, 0);
        if (_eventResetTrade == null) return false;
        byte rv = _eventResetTrade();
        LogAction($"Event_ResetTrade rv={rv}");
        return rv != 0;
    }

    /// <summary>Close the trade (0x01F7 CloseTradeNegotiations).</summary>
    public static bool Close()
    {
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return _eventCloseTradeNegotiations != null && AcMainThreadQueue.EnqueueTrade((uint)TradeOp.Close, 0, 0);
        if (_eventCloseTradeNegotiations == null) return false;
        byte rv = _eventCloseTradeNegotiations();
        LogAction($"Event_CloseTradeNegotiations rv={rv}");
        return rv != 0;
    }

    /// <summary>Accept the trade as it stands (the window's Accept button, 0x01FA AcceptTrade).</summary>
    public static bool Accept()
    {
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return _tradeSystemAcceptTrade != null && AcMainThreadQueue.EnqueueTrade((uint)TradeOp.Accept, 0, 0);
        if (_tradeSystemAcceptTrade == null) return false;
        IntPtr system = TradeSystemWithTrade();
        if (system == IntPtr.Zero) { LogAction("AcceptTrade skipped: no ClientTradeSystem / Trade object"); return false; }
        _tradeSystemAcceptTrade(system);
        LogAction($"ClientTradeSystem::AcceptTrade this=0x{system.ToInt32():X8}");
        return true;
    }

    /// <summary>Withdraw your acceptance (the window's Decline button, 0x01FB DeclineTrade).</summary>
    public static bool Decline()
    {
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return _tradeSystemDeclineTrade != null && AcMainThreadQueue.EnqueueTrade((uint)TradeOp.Decline, 0, 0);
        if (_tradeSystemDeclineTrade == null) return false;
        IntPtr system = TradeSystemWithTrade();
        if (system == IntPtr.Zero) { LogAction("DeclineTrade skipped: no ClientTradeSystem / Trade object"); return false; }
        _tradeSystemDeclineTrade(system);
        LogAction($"ClientTradeSystem::DeclineTrade this=0x{system.ToInt32():X8}");
        return true;
    }

    /// <summary>AcMainThreadQueue drain (main thread): run a queued trade action.</summary>
    public static void RunQueued(uint op, uint a, uint b)
    {
        switch ((TradeOp)op)
        {
            case TradeOp.Open: Open(a); break;
            case TradeOp.Add: Add(a, b); break;
            case TradeOp.Reset: Reset(); break;
            case TradeOp.Accept: Accept(); break;
            case TradeOp.Decline: Decline(); break;
            case TradeOp.Close: Close(); break;
        }
    }

    /// <summary>The ClientTradeSystem, when it and its Trade object (+0x10) exist; else zero.</summary>
    private static IntPtr TradeSystemWithTrade()
    {
        if (_tradeSystemAddr == 0) return IntPtr.Zero;
        IntPtr system = Marshal.ReadIntPtr(new IntPtr(_tradeSystemAddr));
        if (system == IntPtr.Zero) return IntPtr.Zero;
        IntPtr trade = Marshal.ReadIntPtr(IntPtr.Add(system, 0x10));
        return trade == IntPtr.Zero ? IntPtr.Zero : system;
    }

    private static int _actionLogCount;

    private static void LogAction(string message)
    {
        if (_actionLogCount >= 200) return;
        _actionLogCount++;
        RynthLog.Compat($"Trade: {message}");
    }

    // ── State ───────────────────────────────────────────────────────────────

    private const int MaxItemsPerSide = 256;
    private const uint TradeSideSelf = 1;
    private const uint TradeSidePartner = 2;

    private static readonly object _lock = new();
    private static readonly uint[] _selfItems = new uint[MaxItemsPerSide];
    private static readonly uint[] _partnerItems = new uint[MaxItemsPerSide];
    private static int _selfCount, _partnerCount;
    private static bool _open, _selfAccepted, _partnerAccepted;
    private static uint _generation, _sequence, _partnerId, _initiatorId, _lastEventType;
    private static uint _failureCount, _failureItem, _failureReason;
    private static uint _closeReason, _acceptedBy, _declinedBy, _resetBy;
    private static uint _completedCount, _partnerAcceptCount;
    private static int _eventLogCount;

    /// <summary>True for the GameEvent types this file tracks.</summary>
    public static bool IsTradeEvent(uint eventType) => eventType >= 0x01FD && eventType <= 0x0208;

    /// <summary>
    /// A trade GameEvent (SmartBoxHooks.ParseGameEvent, AC's main thread, before AC handles it).
    /// <paramref name="size"/> counts the 4-byte event type. No allocation on the state path.
    /// </summary>
    public static void OnGameEvent(uint eventType, IntPtr data, uint size)
    {
        uint a = size >= 8 ? Rd(data, 4) : 0;
        uint b = size >= 12 ? Rd(data, 8) : 0;
        uint self = ClientHelperHooks.GetPlayerId();

        lock (_lock)
        {
            switch (eventType)
            {
                case 0x01FD: // RegisterTrade: initiator, partner, stamp
                    if (size < 12) return;
                    _open = true;
                    _generation++;
                    _initiatorId = a;
                    _partnerId = a != 0 && a != self ? a : b;
                    ClearWindow();
                    _closeReason = 0;
                    break;

                case 0x01FE: // OpenTrade: objectId
                    if (!_open)
                    {
                        _open = true;
                        _generation++;
                        ClearWindow();
                        _closeReason = 0;
                    }
                    if (_partnerId == 0 && a != 0 && a != self)
                        _partnerId = a;
                    break;

                case 0x01FF: // CloseTrade: EndTradeReason
                    _open = false;
                    _closeReason = a;
                    _partnerId = 0;
                    _initiatorId = 0;
                    ClearWindow();
                    break;

                case 0x0200: // AddToTrade: item, side, slot
                    if (size < 12 || a == 0) return;
                    if (b == TradeSidePartner) AddUnique(_partnerItems, ref _partnerCount, a);
                    else AddUnique(_selfItems, ref _selfCount, a);
                    // The server clears both acceptances whenever the window changes.
                    _selfAccepted = _partnerAccepted = false;
                    break;

                case 0x0201: // RemoveFromTrade: item, side
                    if (a == 0) return;
                    if (b != TradeSideSelf) RemoveItem(_partnerItems, ref _partnerCount, a);
                    if (b != TradeSidePartner) RemoveItem(_selfItems, ref _selfCount, a);
                    _selfAccepted = _partnerAccepted = false;
                    break;

                case 0x0202: // AcceptTrade: who
                    _acceptedBy = a;
                    // Who is "you": the player id when known, else "not the partner".
                    if (self != 0 ? a != self : (a != 0 && a == _partnerId))
                    {
                        _partnerAccepted = true;
                        _partnerAcceptCount++;
                    }
                    else
                    {
                        _selfAccepted = true;
                    }
                    break;

                case 0x0203: // DeclineTrade: who
                    _declinedBy = a;
                    _selfAccepted = _partnerAccepted = false;
                    break;

                case 0x0205: // ResetTrade: who
                    _resetBy = a;
                    ClearWindow();
                    break;

                case 0x0207: // TradeFailure: item, WeenieError
                    _failureCount++;
                    _failureItem = a;
                    _failureReason = b;
                    if (a != 0) RemoveItem(_selfItems, ref _selfCount, a);
                    break;

                case 0x0208: // ClearTradeAcceptance
                    _selfAccepted = _partnerAccepted = false;
                    break;

                default:
                    return;
            }
            _sequence++;
            _lastEventType = eventType;
        }

        if (_eventLogCount < 200)
        {
            _eventLogCount++;
            RynthLog.Compat($"Trade: evt=0x{eventType:X4} a=0x{a:X8} b=0x{b:X8} len={size} partner=0x{_partnerId:X8} you={_selfCount} them={_partnerCount}");
        }
    }

    /// <summary>WeenieError 0x0529 TradeComplete (AC's main thread).</summary>
    public static void OnTradeComplete()
    {
        lock (_lock)
        {
            _completedCount++;
            _sequence++;
        }
        if (_eventLogCount < 200)
        {
            _eventLogCount++;
            RynthLog.Compat("Trade: complete (WeenieError 0x0529)");
        }
    }

    private static void ClearWindow()
    {
        _selfCount = 0;
        _partnerCount = 0;
        _selfAccepted = false;
        _partnerAccepted = false;
    }

    private static void AddUnique(uint[] list, ref int count, uint id)
    {
        for (int i = 0; i < count; i++)
            if (list[i] == id) return;
        if (count < list.Length) list[count++] = id;
    }

    private static void RemoveItem(uint[] list, ref int count, uint id)
    {
        for (int i = 0; i < count; i++)
        {
            if (list[i] != id) continue;
            for (int j = i + 1; j < count; j++) list[j - 1] = list[j];
            count--;
            return;
        }
    }

    private static uint Rd(IntPtr data, int offset) => unchecked((uint)Marshal.ReadInt32(IntPtr.Add(data, offset)));

    /// <summary>
    /// v72 GetTradeState: copy the state into <paramref name="state"/> (up to state->Size bytes).
    /// Returns the bytes written, 0 on a bad argument. Any thread.
    /// </summary>
    public static int GetState(TradeStateNative* state)
    {
        if (state == null) return 0;
        uint want = state->Size;
        if (want < 8) return 0;

        TradeStateNative s = default;
        lock (_lock)
        {
            uint flags = 0;
            if (_open) flags |= TradeStateFlags.Open;
            if (_selfAccepted) flags |= TradeStateFlags.YouAccepted;
            if (_partnerAccepted) flags |= TradeStateFlags.PartnerAccepted;
            s.Flags = flags;
            s.Generation = _generation;
            s.Sequence = _sequence;
            s.PartnerId = _partnerId;
            s.InitiatorId = _initiatorId;
            s.SelfItemCount = _selfCount;
            s.PartnerItemCount = _partnerCount;
            s.LastEventType = _lastEventType;
            s.FailureCount = _failureCount;
            s.LastFailureItemId = _failureItem;
            s.LastFailureReason = _failureReason;
            s.LastCloseReason = _closeReason;
            s.LastAcceptedBy = _acceptedBy;
            s.LastDeclinedBy = _declinedBy;
            s.LastResetBy = _resetBy;
            s.CompletedCount = _completedCount;
            s.PartnerAcceptCount = _partnerAcceptCount;
        }
        if (SmartBoxHooks.IsGameEventHookInstalled) s.Flags |= TradeStateFlags.Watching;
        if (ActionsAvailable) s.Flags |= TradeStateFlags.ActionsAvailable;

        uint n = Math.Min(want, (uint)sizeof(TradeStateNative));
        s.Size = n;
        Buffer.MemoryCopy(&s, state, want, n);
        return (int)n;
    }

    /// <summary>
    /// v72 GetTradeItems: copy up to <paramref name="capacity"/> item ids from one side
    /// (1 = yours, 2 = the partner's) and return that side's total count; -1 for a bad side.
    /// </summary>
    public static int GetItems(int side, uint* buffer, int capacity)
    {
        if (side != (int)TradeSideSelf && side != (int)TradeSidePartner) return -1;
        lock (_lock)
        {
            uint[] list = side == (int)TradeSideSelf ? _selfItems : _partnerItems;
            int count = side == (int)TradeSideSelf ? _selfCount : _partnerCount;
            if (buffer != null && capacity > 0)
            {
                int n = Math.Min(capacity, count);
                for (int i = 0; i < n; i++) buffer[i] = list[i];
            }
            return count;
        }
    }
}

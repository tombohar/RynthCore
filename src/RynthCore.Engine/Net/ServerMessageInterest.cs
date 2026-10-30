// ============================================================================
//  RynthCore.Engine - Net/ServerMessageInterest.cs
//  Which server messages one plugin asked for (PluginContract.SetServerMessageInterestFn).
//
//  Opcodes and game event types are 16-bit on the wire in practice; each set is a
//  65536-bit map (8 KB), so a check is one load. Special entries:
//    opcode 0xFFFFFFFF  every message;
//    opcode 0xF7B0      every game event (otherwise only the listed event types).
//  Pure code; compiled into the NetMessageTests runner.
// ============================================================================

using System;

namespace RynthCore.Engine.Net;

internal sealed class ServerMessageInterest
{
    public const uint GameEventOpcode = 0xF7B0;
    public const uint AllMessages = 0xFFFFFFFF;
    public const int MaxEntries = 4096;

    private readonly ulong[] _opcodes = new ulong[65536 / 64];
    private readonly ulong[] _events = new ulong[65536 / 64];

    public bool All { get; private set; }
    public bool AllGameEvents { get; private set; }
    public int OpcodeCount { get; private set; }
    public int EventCount { get; private set; }
    public bool IsEmpty => !All && OpcodeCount == 0 && EventCount == 0;

    /// <summary>Replaces the set. False (set left empty) for an entry out of range or too many entries.</summary>
    public unsafe bool Set(uint* opcodes, int opcodeCount, uint* events, int eventCount)
    {
        Clear();
        if (opcodeCount < 0 || eventCount < 0 || opcodeCount > MaxEntries || eventCount > MaxEntries)
            return false;
        if ((opcodeCount > 0 && opcodes == null) || (eventCount > 0 && events == null))
            return false;

        for (int i = 0; i < opcodeCount; i++)
        {
            uint op = opcodes[i];
            if (op == AllMessages) { All = true; continue; }
            if (op > 0xFFFF) { Clear(); return false; }
            if (op == GameEventOpcode) AllGameEvents = true;
            if (Add(_opcodes, op)) OpcodeCount++;
        }
        for (int i = 0; i < eventCount; i++)
        {
            uint ev = events[i];
            if (ev > 0xFFFF) { Clear(); return false; }
            if (Add(_events, ev)) EventCount++;
        }
        return true;
    }

    public void Clear()
    {
        Array.Clear(_opcodes);
        Array.Clear(_events);
        All = AllGameEvents = false;
        OpcodeCount = EventCount = 0;
    }

    /// <summary><paramref name="eventType"/> is only looked at for opcode 0xF7B0.</summary>
    public bool Wants(uint opcode, uint eventType)
    {
        if (All) return true;
        if (opcode > 0xFFFF) return false;
        if (opcode == GameEventOpcode)
            return AllGameEvents || (eventType <= 0xFFFF && Has(_events, eventType));
        return Has(_opcodes, opcode);
    }

    private static bool Has(ulong[] map, uint v) => (map[v >> 6] & (1UL << (int)(v & 63))) != 0;

    private static bool Add(ulong[] map, uint v)
    {
        ulong bit = 1UL << (int)(v & 63);
        if ((map[v >> 6] & bit) != 0) return false;
        map[v >> 6] |= bit;
        return true;
    }
}

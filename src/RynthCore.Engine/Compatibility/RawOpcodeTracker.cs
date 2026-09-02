using System;
using System.Collections.Generic;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Thread-safe store for raw opcode observations captured by RawPacketHooks.
/// Populated on the network receive thread; read on the ImGui render thread.
/// </summary>
internal static class RawOpcodeTracker
{
    public struct OpcodeEntry
    {
        public int Count;
        public int LastPayloadLen;
        public byte[] LastSample; // up to 16 bytes of the most recent payload
    }

    // Opcodes already handled by SmartBoxHooks — shown only when ShowKnown is true.
    private static readonly HashSet<ushort> KnownOpcodes =
    [
        0xF74B, 0xF74C, 0xF74E, 0xF7DB, 0xF7B0, 0xF658
    ];

    private static readonly object _lock = new();
    private static readonly Dictionary<ushort, OpcodeEntry> _entries = new();

    public static bool Frozen;
    public static bool ShowKnown = false;
    public static bool ShowUnknown = true;

    // Deep-audit finding #25 (2026-06-18): Track allocates a fresh byte[] for
    // every non-zero-opcode blob of every inbound packet, called from
    // RecvFromDetour (a reverse-P/Invoke detour) with no gate on whether the
    // Packet Sniffer panel is even open — only Frozen. Sustained per-packet
    // managed allocation on a reverse-P/Invoke transition is the documented
    // RhpReversePInvokeAttachOrTrapThread2 fail-fast hazard; on a busy
    // multibox it's constant GC churn for a diagnostic feature nobody is
    // looking at. RynthCoreShell sets this to the panel's actual visibility
    // every frame (open OR closed) — Track short-circuits before any
    // allocation while it's false, which is the overwhelming common case.
    public static volatile bool Active;

    public static bool IsKnown(ushort opcode) => KnownOpcodes.Contains(opcode);

    public static unsafe void Track(ushort opcode, byte* payload, int payloadLen)
    {
        if (Frozen || !Active) return;

        int sampleLen = payloadLen < 16 ? payloadLen : 16;
        byte[] sample = new byte[sampleLen];
        for (int i = 0; i < sampleLen; i++)
            sample[i] = payload[i];

        lock (_lock)
        {
            if (_entries.TryGetValue(opcode, out OpcodeEntry existing))
            {
                existing.Count++;
                existing.LastPayloadLen = payloadLen;
                existing.LastSample = sample;
                _entries[opcode] = existing;
            }
            else
            {
                _entries[opcode] = new OpcodeEntry
                {
                    Count = 1,
                    LastPayloadLen = payloadLen,
                    LastSample = sample
                };
            }
        }
    }

    public static void Clear()
    {
        lock (_lock)
            _entries.Clear();
    }

    /// <summary>Returns a snapshot of all tracked entries sorted by opcode for rendering.</summary>
    public static (ushort Opcode, OpcodeEntry Entry)[] GetSnapshot()
    {
        lock (_lock)
        {
            var result = new (ushort, OpcodeEntry)[_entries.Count];
            int i = 0;
            foreach (var kv in _entries)
                result[i++] = (kv.Key, kv.Value);
            Array.Sort(result, (a, b) => a.Item1.CompareTo(b.Item1));
            return result;
        }
    }
}

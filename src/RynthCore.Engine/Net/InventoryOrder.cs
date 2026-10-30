// ============================================================================
//  RynthCore.Engine - Net/InventoryOrder.cs
//  The order of the items in each of the player's containers, as the SERVER keeps it.
//
//  Why: retail lets a drop name a slot in a pack (PutItemInContainer carries a placement), and
//  the Inventory panel's grid is that order. The client's PWD snapshot only knows which
//  container an item is in, so the panel had the items by id: a drop on cell k could not be
//  turned into a placement. The server's order is sent in three places, and all three are
//  read here from the reassembled message stream (ServerMessageStream), read-only:
//
//    PlayerDescription (event 0x0013)   the main pack, in PlacementPosition order: first the
//                                       ordinary items, then the side packs and foci. At login.
//    ViewContents      (event 0x0196)   a pack the player opened, same order.
//    InventoryPutObjInContainer (0x0022) an item landed in a container at a placement.
//    InventoryRemoveObject (0x0024) / DeleteObject (0xF747): the item left.
//
//  ACE keeps the positions contiguous (0..n-1, renumbered at load, shifted down on a removal and
//  up on an add), and orders items and "pack-slot" things (side packs, foci) separately, so a
//  list here is one container's one kind and an add is "remove from wherever it was, insert at
//  the placement". Sending placement = the index the item should end at (counted without it)
//  therefore lands it exactly there.
//
//  A container is only "known" once a full list for it was seen: a drop on a container this
//  has not seen a list for falls back to the first-empty-slot move, never to a guess.
//
//  Pure code (no engine state); compiled into tools/NetMessageTests. Any thread.
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;

namespace RynthCore.Engine.Net;

internal static class InventoryOrder
{
    public const uint GameEventOpcode = 0xF7B0, DeleteObjectOpcode = 0xF747, InventoryRemoveOpcode = 0x0024;
    public const uint EventPlayerDescription = 0x0013, EventPutObjInContainer = 0x0022, EventViewContents = 0x0196;

    /// <summary>ContainerType on the wire: 0 ordinary item, 1 side pack, 2 foci. 1 and 2 are the "pack slot" kind.</summary>
    private static bool SlotKind(uint containerType) => containerType != 0;

    /// <summary>Object ids are far above any count, type or mask; it keeps those from passing for ids.</summary>
    private const uint MinId = 0x1000;

    private static readonly object Gate = new();
    // (container, slotKind) -> ids in server order. A key is present only for a list seen in full.
    private static readonly Dictionary<(uint, bool), List<uint>> Lists = new();
    private static int _version;
    // Containers other than the player's own (corpses, chests, packs once opened) are kept least-recently-seen out.
    private const int MaxLists = 64;
    private static uint _player;
    private static long _clock;
    private static readonly Dictionary<uint, long> Touched = new();

    /// <summary>Changes whenever any list changed (a cheap "rebuild your view" check).</summary>
    public static int Version => Volatile.Read(ref _version);

    public static void Reset()
    {
        lock (Gate) { Lists.Clear(); Touched.Clear(); _player = 0; _version++; }
    }

    /// <summary>
    /// The server's order of <paramref name="container"/>'s ordinary items (or, with
    /// <paramref name="slotKind"/>, its side packs and foci). False when no full list was seen.
    /// </summary>
    public static bool TryGetOrder(uint container, bool slotKind, out uint[] ids)
    {
        lock (Gate)
        {
            if (Lists.TryGetValue((container, slotKind), out List<uint>? l)) { ids = l.ToArray(); return true; }
        }
        ids = Array.Empty<uint>();
        return false;
    }

    /// <summary>
    /// One whole server message, starting at its opcode (the way ServerMessageStream delivers it).
    /// Anything else is ignored; a malformed one is dropped.
    /// </summary>
    public static void Observe(ReadOnlySpan<byte> msg)
    {
        if (msg.Length < 8) return;
        uint opcode = U32(msg, 0);
        try
        {
            if (opcode == GameEventOpcode)
            {
                if (msg.Length < 16) return;
                uint ev = U32(msg, 12);
                if (ev == EventPlayerDescription) OnPlayerDescription(U32(msg, 4), msg.Slice(16));
                else if (ev == EventViewContents) OnViewContents(msg.Slice(16));
                else if (ev == EventPutObjInContainer) OnPut(msg.Slice(16));
            }
            else if (opcode == InventoryRemoveOpcode || opcode == DeleteObjectOpcode)
                OnGone(U32(msg, 4));
        }
        catch (ArgumentOutOfRangeException) { }
    }

    // ── Keeping it across an engine reload ──────────────────────────────────
    // The login message is the only place the main pack's order arrives, and a hot reload of the engine
    // (every deploy, and any crash recovery) would lose it until the next login. So the lists are
    // saved as they change and read back when the engine starts. Layout: 'IORD', version u32, list count
    // u32, then per list: container u32, slot-kind u8, count u32, ids u32 each.

    private const uint Magic = 0x44524F49;   // "IORD"

    public static byte[] Serialize()
    {
        lock (Gate)
        {
            var ms = new System.IO.MemoryStream();
            var w = new System.IO.BinaryWriter(ms);
            w.Write(Magic); w.Write(1u); w.Write((uint)Lists.Count);
            foreach (KeyValuePair<(uint, bool), List<uint>> kv in Lists)
            {
                w.Write(kv.Key.Item1); w.Write(kv.Key.Item2 ? (byte)1 : (byte)0); w.Write((uint)kv.Value.Count);
                foreach (uint id in kv.Value) w.Write(id);
            }
            w.Flush();
            return ms.ToArray();
        }
    }

    /// <summary>Replaces the lists with a saved set. False (nothing changed) when the data is not one.</summary>
    public static bool Restore(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length < 12 || U32(data, 0) != Magic || U32(data, 4) != 1) return false;
            uint lists = U32(data, 8);
            if (lists > 4096) return false;
            int at = 12;
            var loaded = new Dictionary<(uint, bool), List<uint>>();
            for (uint i = 0; i < lists; i++)
            {
                if (at + 9 > data.Length) return false;
                uint container = U32(data, at); bool slot = data[at + 4] != 0; uint n = U32(data, at + 5);
                at += 9;
                if (n > 100000 || at + (long)n * 4 > data.Length) return false;
                var l = new List<uint>((int)n);
                for (uint k = 0; k < n; k++) { l.Add(U32(data, at)); at += 4; }
                loaded[(container, slot)] = l;
            }
            lock (Gate)
            {
                Lists.Clear();
                Touched.Clear();
                foreach (KeyValuePair<(uint, bool), List<uint>> kv in loaded) { Lists[kv.Key] = kv.Value; Touched[kv.Key.Item1] = ++_clock; }
                Evict();
                _version++;
            }
            return true;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    // ── ViewContents: container, count, count x (id, type) ──────────────────

    private static void OnViewContents(ReadOnlySpan<byte> p)
    {
        if (p.Length < 8) return;
        uint container = U32(p, 0);
        uint count = U32(p, 4);
        if (count > 4096 || p.Length < 8 + (long)count * 8) return;
        var items = new List<uint>((int)count);
        var slots = new List<uint>();
        for (int i = 0; i < count; i++)
        {
            uint id = U32(p, 8 + i * 8), type = U32(p, 12 + i * 8);
            if (id == 0 || type > 2) return;
            (SlotKind(type) ? slots : items).Add(id);
        }
        lock (Gate)
        {
            RemoveEverywhere(container, items, slots);
            Lists[(container, false)] = items;
            Lists[(container, true)] = slots;
            Touched[container] = ++_clock;
            Evict();
            _version++;
        }
    }

    // ── PlayerDescription: ... inventory count, n x (id, type), worn count, e x (id, loc, priority) ──

    private static void OnPlayerDescription(uint player, ReadOnlySpan<byte> p)
    {
        if (!TryFindContents(p, out int invAt, out int n)) return;
        var items = new List<uint>(n);
        var slots = new List<uint>();
        for (int i = 0; i < n; i++)
        {
            uint id = U32(p, invAt + 4 + i * 8), type = U32(p, invAt + 8 + i * 8);
            (SlotKind(type) ? slots : items).Add(id);
        }
        lock (Gate)
        {
            Lists.Clear();                       // a login: nothing seen before it counts
            Touched.Clear();
            _player = player;
            Lists[(player, false)] = items;
            Lists[(player, true)] = slots;
            Touched[player] = ++_clock;
            _version++;
        }
    }

    /// <summary>
    /// The inventory list sits at the END of PlayerDescription, before the worn list that closes it;
    /// everything before it is a long run of options and tables that is not worth parsing for this.
    /// So the tail is read backwards: a worn count e with e x 12 bytes after it, and before that an
    /// inventory count n with n x 8 bytes, every id non-zero and every type 0..2. Both counts being
    /// where they must be and every entry being sane is not something noise does by accident.
    /// </summary>
    internal static bool TryFindContents(ReadOnlySpan<byte> p, out int invAt, out int n)
    {
        invAt = 0; n = 0;
        for (int e = 0; e <= 256; e++)
        {
            int wornAt = p.Length - 12 * e - 4;
            if (wornAt < 4) break;
            if (U32(p, wornAt) != (uint)e) continue;
            if (!WornSane(p, wornAt, e)) continue;
            int maxN = (wornAt - 4) / 8;
            // Largest first: a shorter list can match too (a pack-type word of 1 reads as a count of 1).
            for (int cand = Math.Min(maxN, 4096); cand >= 0; cand--)
            {
                int at = wornAt - 8 * cand - 4;
                if (U32(p, at) != (uint)cand) continue;
                if (!InventorySane(p, at, cand)) continue;
                invAt = at; n = cand;
                return true;
            }
        }
        return false;
    }

    private static bool WornSane(ReadOnlySpan<byte> p, int wornAt, int e)
    {
        for (int i = 0; i < e; i++)
            if (U32(p, wornAt + 4 + i * 12) < MinId || U32(p, wornAt + 8 + i * 12) == 0) return false;
        return true;
    }

    private static bool InventorySane(ReadOnlySpan<byte> p, int at, int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (U32(p, at + 4 + i * 8) < MinId || U32(p, at + 8 + i * 8) > 2) return false;
        }
        return true;
    }

    // ── Moves ───────────────────────────────────────────────────────────────

    private static void OnPut(ReadOnlySpan<byte> p)
    {
        if (p.Length < 16) return;
        uint item = U32(p, 0), container = U32(p, 4), type = U32(p, 12);
        int placement = unchecked((int)U32(p, 8));
        bool slot = SlotKind(type);
        lock (Gate)
        {
            RemoveItem(item);
            if (Lists.TryGetValue((container, slot), out List<uint>? l))
                l.Insert(Math.Clamp(placement, 0, l.Count), item);
            _version++;
        }
    }

    private static void OnGone(uint id)
    {
        if (id == 0) return;
        lock (Gate) { if (RemoveItem(id)) _version++; }
    }

    /// <summary>Drops the least recently seen containers beyond <see cref="MaxLists"/>; never the player's own.</summary>
    private static void Evict()
    {
        while (Touched.Count > MaxLists)
        {
            uint oldest = 0; long at = long.MaxValue;
            foreach (KeyValuePair<uint, long> kv in Touched)
                if (kv.Key != _player && kv.Value < at) { at = kv.Value; oldest = kv.Key; }
            if (oldest == 0) return;
            Touched.Remove(oldest);
            Lists.Remove((oldest, false));
            Lists.Remove((oldest, true));
        }
    }

    private static bool RemoveItem(uint id)
    {
        bool any = false;
        foreach (List<uint> l in Lists.Values)
        {
            int i = l.IndexOf(id);
            if (i < 0) continue;
            l.RemoveAt(i);
            any = true;
        }
        return any;
    }

    // A full list for a container replaces the old one; its items must not also sit in another list.
    private static void RemoveEverywhere(uint container, List<uint> items, List<uint> slots)
    {
        foreach (KeyValuePair<(uint, bool), List<uint>> kv in Lists)
        {
            if (kv.Key.Item1 == container) continue;
            kv.Value.RemoveAll(id => items.Contains(id) || slots.Contains(id));
        }
    }

    private static uint U32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(at, 4));
}

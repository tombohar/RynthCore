// ============================================================================
//  RynthCore.Engine - UI/Data/ItemHudModel.cs
//  The pure half of the Item Count and Mini Remote panels (ItemCountFace,
//  MiniRemoteFace): what one character keeps, the item_huds.txt format, the
//  carried count and which carried item a remote slot means. No engine calls,
//  no I/O (ItemHudStore does that), so tools/ItemHudTests compiles this file.
//
//  item_huds.txt (one section per character, "<server>/<character>"):
//    # comment lines
//    [Aelrynth/Drakkon]
//    count=Mana Scarab
//    count=Prismatic Taper
//    slots=8
//    slotsize=44
//    slot=0|0x80123456|Healing Kit
//    slot=3|0x8012ABCD|Stamina Elixir
//  slotsize (the remote's box edge in pixels) is written only when it isn't
//  the default. Lines this version doesn't know are kept where they were.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RynthCore.Engine.UI.Data;

/// <summary>An item a Mini Remote slot uses: its id when set, and its name to find a new stack by.</summary>
internal sealed record RemoteSlot(uint Id, string Name);

/// <summary>A carried item as the panels see it (from InventoryModel's view).</summary>
internal readonly record struct HudItem(uint Id, string Name, int Stack);

/// <summary>One character's Item Count list and Mini Remote slots. Immutable.</summary>
internal sealed class ItemHudCharacter
{
    public const int MaxCounts = 30;
    public const int MinSlots = 4, MaxSlots = 24, DefaultSlots = 8;
    /// <summary>Mini Remote box edge in pixels (before UI scaling): the grow / shrink range.</summary>
    public const int MinSlotSize = 24, MaxSlotSize = 72, DefaultSlotSize = 36;
    public const int MaxNameLength = 64;

    public static readonly ItemHudCharacter Empty = new(Array.Empty<string>(), new RemoteSlot?[DefaultSlots], Array.Empty<string>());

    public ItemHudCharacter(string[] counts, RemoteSlot?[] slots, string[] other, int slotSize = DefaultSlotSize)
    {
        Counts = counts;
        Slots = slots;
        Other = other;
        SlotSize = Math.Clamp(slotSize, MinSlotSize, MaxSlotSize);
    }

    /// <summary>Item names on the Item Count list, in the order added.</summary>
    public string[] Counts { get; }
    /// <summary>The remote's slots (null = empty); the length is the slot count.</summary>
    public RemoteSlot?[] Slots { get; }
    /// <summary>Lines of this section the file had that this version doesn't know.</summary>
    public string[] Other { get; }
    /// <summary>The remote's box edge in pixels, <see cref="MinSlotSize"/>..<see cref="MaxSlotSize"/>.</summary>
    public int SlotSize { get; }

    public bool HasCount(string name) => IndexOfCount(name) >= 0;

    public int IndexOfCount(string name)
    {
        for (int i = 0; i < Counts.Length; i++)
            if (string.Equals(Counts[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public static string Clean(string? name)
    {
        string s = (name ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Replace('|', ' ').Trim();
        return s.Length > MaxNameLength ? s[..MaxNameLength] : s;
    }

    /// <summary>Adds a name (trimmed; no duplicates, any case). Same instance when nothing changes.</summary>
    public ItemHudCharacter WithCount(string name, out string message)
    {
        name = Clean(name);
        if (name.Length == 0) { message = "Type an item name first."; return this; }
        if (HasCount(name)) { message = $"\"{name}\" is already on the list."; return this; }
        if (Counts.Length >= MaxCounts) { message = $"The list is full ({MaxCounts} items)."; return this; }
        var next = new string[Counts.Length + 1];
        Array.Copy(Counts, next, Counts.Length);
        next[^1] = name;
        message = $"Counting \"{name}\".";
        return new ItemHudCharacter(next, Slots, Other, SlotSize);
    }

    public ItemHudCharacter WithoutCount(int index)
    {
        if (index < 0 || index >= Counts.Length) return this;
        var next = new string[Counts.Length - 1];
        Array.Copy(Counts, 0, next, 0, index);
        Array.Copy(Counts, index + 1, next, index, Counts.Length - index - 1);
        return new ItemHudCharacter(next, Slots, Other, SlotSize);
    }

    public ItemHudCharacter WithSlot(int index, RemoteSlot? slot)
    {
        if (index < 0 || index >= Slots.Length) return this;
        var next = (RemoteSlot?[])Slots.Clone();
        next[index] = slot == null ? null : slot with { Name = Clean(slot.Name) };
        return new ItemHudCharacter(Counts, next, Other, SlotSize);
    }

    /// <summary>More or fewer slots (clamped); slots past the new end are dropped.</summary>
    public ItemHudCharacter WithSlotCount(int count)
    {
        count = Math.Clamp(count, MinSlots, MaxSlots);
        if (count == Slots.Length) return this;
        var next = new RemoteSlot?[count];
        Array.Copy(Slots, next, Math.Min(count, Slots.Length));
        return new ItemHudCharacter(Counts, next, Other, SlotSize);
    }

    /// <summary>Bigger or smaller remote boxes (clamped). Same instance when nothing changes.</summary>
    public ItemHudCharacter WithSlotSize(int size)
    {
        size = Math.Clamp(size, MinSlotSize, MaxSlotSize);
        return size == SlotSize ? this : new ItemHudCharacter(Counts, Slots, Other, size);
    }
}

/// <summary>Counting and slot lookup over the carried items.</summary>
internal static class ItemHudMath
{
    /// <summary>
    /// How many of <paramref name="name"/> the character carries: every stack with that name
    /// (any case), whole stacks summed; a single item counts 1.
    /// </summary>
    public static long Count(IReadOnlyList<HudItem> items, string name)
    {
        long total = 0;
        foreach (HudItem it in items)
            if (string.Equals(it.Name, name, StringComparison.OrdinalIgnoreCase))
                total += Math.Max(1, it.Stack);
        return total;
    }

    /// <summary>
    /// The carried item a slot uses: the same object when it is still carried, else a stack
    /// with the slot's name (the largest; a stack that merged or split gets a new id). 0 when none.
    /// </summary>
    public static uint Resolve(IReadOnlyList<HudItem> items, RemoteSlot slot)
    {
        uint byName = 0;
        int best = -1;
        foreach (HudItem it in items)
        {
            if (slot.Id != 0 && it.Id == slot.Id) return it.Id;
            if (slot.Name.Length > 0 && string.Equals(it.Name, slot.Name, StringComparison.OrdinalIgnoreCase) && it.Stack > best)
            {
                best = it.Stack;
                byName = it.Id;
            }
        }
        return byName;
    }

    /// <summary>The carried item with this id, if any.</summary>
    public static bool TryFind(IReadOnlyList<HudItem> items, uint id, out HudItem item)
    {
        foreach (HudItem it in items)
            if (it.Id == id) { item = it; return true; }
        item = default;
        return false;
    }
}

/// <summary>The item_huds.txt format: sections per character key, unknown lines kept.</summary>
internal static class ItemHudFile
{
    public const string Header = "# RynthCore Item Count and Mini Remote - auto-generated, hand-edits OK. /rc itemcount, /rc remote in game.";

    public sealed class Contents
    {
        /// <summary>Per character key (case-insensitive), in file order.</summary>
        public readonly Dictionary<string, ItemHudCharacter> Characters = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Order = new();
        /// <summary>Lines before the first section that aren't the header or comments we write.</summary>
        public readonly List<string> Preamble = new();

        public ItemHudCharacter For(string key) => Characters.TryGetValue(key, out ItemHudCharacter? c) ? c : ItemHudCharacter.Empty;

        public void Set(string key, ItemHudCharacter c)
        {
            if (!Characters.ContainsKey(key)) Order.Add(key);
            Characters[key] = c;
        }
    }

    public static Contents Parse(IEnumerable<string> lines)
    {
        var result = new Contents();
        string? key = null;
        List<string>? counts = null, other = null;
        RemoteSlot?[]? slots = null;
        int slotSize = ItemHudCharacter.DefaultSlotSize;

        void Flush()
        {
            if (key == null) return;
            result.Set(key, new ItemHudCharacter(counts!.ToArray(), slots!, other!.ToArray(), slotSize));
        }

        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r', '\n');
            string t = line.Trim();
            if (t.Length > 2 && t[0] == '[' && t[^1] == ']')
            {
                Flush();
                key = t[1..^1].Trim();
                counts = new List<string>();
                other = new List<string>();
                slots = new RemoteSlot?[ItemHudCharacter.DefaultSlots];
                slotSize = ItemHudCharacter.DefaultSlotSize;
                continue;
            }
            if (key == null)
            {
                if (t.Length > 0 && !t.StartsWith('#')) result.Preamble.Add(line);
                continue;
            }
            int eq = t.IndexOf('=');
            string name = eq > 0 ? t[..eq].Trim() : string.Empty;
            string val = eq > 0 ? t[(eq + 1)..] : string.Empty;
            switch (name)
            {
                case "count":
                {
                    string c = ItemHudCharacter.Clean(val);
                    if (c.Length > 0 && counts!.Count < ItemHudCharacter.MaxCounts && !counts.Exists(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase)))
                        counts.Add(c);
                    break;
                }
                case "slots":
                    if (int.TryParse(val.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    {
                        n = Math.Clamp(n, ItemHudCharacter.MinSlots, ItemHudCharacter.MaxSlots);
                        var resized = new RemoteSlot?[n];
                        Array.Copy(slots!, resized, Math.Min(n, slots!.Length));
                        slots = resized;
                    }
                    break;
                case "slotsize":
                    // Clamped by the constructor; an unreadable value keeps the default.
                    if (int.TryParse(val.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int px))
                        slotSize = px;
                    break;
                case "slot":
                {
                    // slot=<index>|<0xid>|<name>
                    string[] f = val.Split('|', 3);
                    if (f.Length == 3 && int.TryParse(f[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)
                        && i >= 0 && i < ItemHudCharacter.MaxSlots && TryParseId(f[1], out uint id))
                    {
                        if (i >= slots!.Length)
                        {
                            var grown = new RemoteSlot?[i + 1];
                            Array.Copy(slots, grown, slots.Length);
                            slots = grown;
                        }
                        string sn = ItemHudCharacter.Clean(f[2]);
                        if (id != 0 || sn.Length > 0) slots[i] = new RemoteSlot(id, sn);
                    }
                    else other!.Add(line);
                    break;
                }
                default:
                    if (t.Length > 0 && !t.StartsWith('#')) other!.Add(line);
                    break;
            }
        }
        Flush();
        return result;
    }

    private static bool TryParseId(string s, out uint id)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
        return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    public static string Write(Contents c)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        foreach (string p in c.Preamble) sb.Append(p).Append('\n');
        foreach (string key in c.Order)
        {
            ItemHudCharacter ch = c.Characters[key];
            sb.Append('\n').Append('[').Append(key).Append("]\n");
            foreach (string n in ch.Counts) sb.Append("count=").Append(n).Append('\n');
            sb.Append("slots=").Append(ch.Slots.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (ch.SlotSize != ItemHudCharacter.DefaultSlotSize)
                sb.Append("slotsize=").Append(ch.SlotSize.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (int i = 0; i < ch.Slots.Length; i++)
            {
                RemoteSlot? s = ch.Slots[i];
                if (s == null) continue;
                sb.Append("slot=").Append(i.ToString(CultureInfo.InvariantCulture)).Append("|0x")
                  .Append(s.Id.ToString("X8", CultureInfo.InvariantCulture)).Append('|').Append(s.Name).Append('\n');
            }
            foreach (string o in ch.Other) sb.Append(o).Append('\n');
        }
        return sb.ToString();
    }
}

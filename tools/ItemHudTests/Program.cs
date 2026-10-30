using RynthCore.Engine.UI.Data;

namespace ItemHudTests;

/// <summary>The Item Count / Mini Remote model: counting, slot lookup, edits and item_huds.txt.</summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        CountSumsStacks();
        ResolveSlots();
        CountListEdits();
        SlotEdits();
        FileRoundTrip();
        PerCharacterSections();
        UnknownLinesKept();
        BadFileValues();
        EditsAreImmutable();
        SlotSizes();

        Console.WriteLine(_failed == 0 ? $"PASS ({_checks} assertions)" : $"FAIL ({_failed} of {_checks} assertions)");
        return _failed == 0 ? 0 : 1;
    }

    private static void Case(string name) { _case = name; Console.WriteLine("ok   " + name); }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"FAIL {_case}: {what}");
    }

    private static readonly HudItem[] Pack =
    {
        new(0x80000001, "Mana Scarab", 25),
        new(0x80000002, "Mana Scarab", 100),
        new(0x80000003, "mana scarab", 3),        // any case
        new(0x80000004, "Prismatic Taper", 0),    // a single item can report 0
        new(0x80000005, "Healing Kit", 1),
        new(0x80000006, "Greater Mana Scarab", 7),// a different item
        new(0x80000007, "Deadly Arrow", 250),     // worn (wielded ammo)
    };

    private static void CountSumsStacks()
    {
        Case("CountSumsStacks");
        Check(ItemHudMath.Count(Pack, "Mana Scarab") == 128, "every Mana Scarab stack summed, any case");
        Check(ItemHudMath.Count(Pack, "Prismatic Taper") == 1, "an item without a stack size counts 1");
        Check(ItemHudMath.Count(Pack, "Deadly Arrow") == 250, "worn ammo counts");
        Check(ItemHudMath.Count(Pack, "Lockpick") == 0, "none carried is 0");
        Check(ItemHudMath.Count(Pack, "Scarab") == 0, "the name must match, not just contain");
        Check(ItemHudMath.Count(Array.Empty<HudItem>(), "Mana Scarab") == 0, "no items");
    }

    private static void ResolveSlots()
    {
        Case("ResolveSlots");
        Check(ItemHudMath.Resolve(Pack, new RemoteSlot(0x80000001, "Mana Scarab")) == 0x80000001, "the same object while carried");
        Check(ItemHudMath.Resolve(Pack, new RemoteSlot(0x8000FFFF, "Mana Scarab")) == 0x80000002, "gone (merged/used up): the largest stack with its name");
        Check(ItemHudMath.Resolve(Pack, new RemoteSlot(0x8000FFFF, "Lockpick")) == 0, "nothing with that name: 0");
        Check(ItemHudMath.Resolve(Pack, new RemoteSlot(0, "healing kit")) == 0x80000005, "no id: by name, any case");
        Check(ItemHudMath.Resolve(Pack, new RemoteSlot(0x80000005, "")) == 0x80000005, "no name: by id");
        Check(ItemHudMath.TryFind(Pack, 0x80000004, out HudItem t) && t.Name == "Prismatic Taper", "TryFind by id");
        Check(!ItemHudMath.TryFind(Pack, 0x12345678, out _), "TryFind misses");
    }

    private static void CountListEdits()
    {
        Case("CountListEdits");
        var c = ItemHudCharacter.Empty;
        c = c.WithCount("  Mana Scarab ", out string m1);
        Check(c.Counts.SequenceEqual(new[] { "Mana Scarab" }) && m1.StartsWith("Counting", StringComparison.Ordinal), "added, trimmed");
        var same = c.WithCount("MANA SCARAB", out string m2);
        Check(ReferenceEquals(same, c) && m2.Contains("already"), "a duplicate in another case is refused");
        Check(ReferenceEquals(c.WithCount("   ", out string m3), c) && m3.Length > 0, "an empty name is refused");
        c = c.WithCount("Healing Kit", out _).WithCount("Lock|pick", out _);
        Check(c.Counts.Length == 3 && c.Counts[2] == "Lock pick", "a bar can't break the file format");
        c = c.WithoutCount(c.IndexOfCount("healing kit"));
        Check(c.Counts.SequenceEqual(new[] { "Mana Scarab", "Lock pick" }), "removed by name, any case");
        Check(ReferenceEquals(c.WithoutCount(9), c), "a bad index changes nothing");
        var full = ItemHudCharacter.Empty;
        for (int i = 0; i < ItemHudCharacter.MaxCounts + 3; i++) full = full.WithCount("Item " + i, out _);
        Check(full.Counts.Length == ItemHudCharacter.MaxCounts, "capped");
        string longName = new('x', 200);
        Check(ItemHudCharacter.Empty.WithCount(longName, out _).Counts[0].Length == ItemHudCharacter.MaxNameLength, "long names cut");
    }

    private static void SlotEdits()
    {
        Case("SlotEdits");
        var c = ItemHudCharacter.Empty;
        Check(c.Slots.Length == ItemHudCharacter.DefaultSlots && c.Slots.All(s => s == null), "8 empty slots by default");
        c = c.WithSlot(2, new RemoteSlot(0x80000005, "Healing Kit"));
        Check(c.Slots[2] == new RemoteSlot(0x80000005, "Healing Kit"), "slot set");
        Check(ReferenceEquals(c.WithSlot(99, new RemoteSlot(1, "x")), c), "out of range: no change");
        c = c.WithSlotCount(12);
        Check(c.Slots.Length == 12 && c.Slots[2]?.Name == "Healing Kit", "more slots keep the filled ones");
        c = c.WithSlotCount(1);
        Check(c.Slots.Length == ItemHudCharacter.MinSlots && c.Slots[2]?.Name == "Healing Kit", "at least 4 slots");
        c = c.WithSlotCount(1000);
        Check(c.Slots.Length == ItemHudCharacter.MaxSlots, "at most 24");
        c = c.WithSlot(2, null);
        Check(c.Slots[2] == null, "cleared");
    }

    private static ItemHudFile.Contents RoundTrip(ItemHudFile.Contents c) =>
        ItemHudFile.Parse(ItemHudFile.Write(c).Split('\n'));

    private static void FileRoundTrip()
    {
        Case("FileRoundTrip");
        var c = new ItemHudFile.Contents();
        var d = ItemHudCharacter.Empty.WithCount("Mana Scarab", out _).WithCount("Deadly Arrow", out _)
            .WithSlotCount(12).WithSlot(0, new RemoteSlot(0x80123456, "Healing Kit")).WithSlot(11, new RemoteSlot(0x8012ABCD, "Stamina Elixir"));
        c.Set("Aelrynth/Drakkon", d);
        string text = ItemHudFile.Write(c);
        Check(text.StartsWith(ItemHudFile.Header, StringComparison.Ordinal), "header first");
        Check(text.Contains("[Aelrynth/Drakkon]\n") && text.Contains("count=Mana Scarab\n") && text.Contains("slots=12\n")
              && text.Contains("slot=0|0x80123456|Healing Kit\n") && text.Contains("slot=11|0x8012ABCD|Stamina Elixir\n"), "readable lines");
        var back = RoundTrip(c).For("aelrynth/drakkon");
        Check(back.Counts.SequenceEqual(d.Counts), "counts back, in order (key any case)");
        Check(back.Slots.Length == 12 && back.Slots[0] == d.Slots[0] && back.Slots[11] == d.Slots[11] && back.Slots[5] == null, "slots back");
        Check(ItemHudFile.Write(RoundTrip(c)) == text, "a second save is identical");
    }

    private static void PerCharacterSections()
    {
        Case("PerCharacterSections");
        var c = new ItemHudFile.Contents();
        c.Set("Aelrynth/One", ItemHudCharacter.Empty.WithCount("Mana Scarab", out _));
        c.Set("Aelrynth/Two", ItemHudCharacter.Empty.WithCount("Healing Kit", out _));
        c.Set("Other/One", ItemHudCharacter.Empty.WithSlot(1, new RemoteSlot(5, "Pyreal")));
        var back = RoundTrip(c);
        Check(back.For("Aelrynth/One").Counts.SequenceEqual(new[] { "Mana Scarab" }), "first character");
        Check(back.For("Aelrynth/Two").Counts.SequenceEqual(new[] { "Healing Kit" }), "second character apart");
        Check(back.For("Other/One").Counts.Length == 0 && back.For("Other/One").Slots[1]?.Name == "Pyreal", "same name on another server apart");
        Check(ReferenceEquals(back.For("Nobody/Here"), ItemHudCharacter.Empty), "an unknown character: empty");
        Check(back.Order.SequenceEqual(new[] { "Aelrynth/One", "Aelrynth/Two", "Other/One" }), "file order kept");
    }

    private static void UnknownLinesKept()
    {
        Case("UnknownLinesKept");
        string[] file =
        {
            "# a comment",
            "future=1",
            "[A/B]",
            "count=Mana Scarab",
            "iconSize=24",
            "slot=0|0x00000005|Pyreal",
        };
        var c = ItemHudFile.Parse(file);
        Check(c.Preamble.SequenceEqual(new[] { "future=1" }), "a line before any section kept");
        Check(c.For("A/B").Other.SequenceEqual(new[] { "iconSize=24" }), "an unknown line in a section kept");
        string text = ItemHudFile.Write(c);
        Check(text.Contains("future=1\n") && text.Contains("iconSize=24\n"), "and written back");
        Check(!text.Contains("# a comment"), "comments are ours to write");
    }

    private static void BadFileValues()
    {
        Case("BadFileValues");
        string[] file =
        {
            "[A/B]",
            "count=",
            "count=Mana Scarab",
            "count=MANA SCARAB",
            "slots=banana",
            "slots=2",
            "slot=x|0x5|Pyreal",
            "slot=3|zzz|Pyreal",
            "slot=30|0x5|Pyreal",
            "slot=1|0x0|",
            "slot=2|17|Decimal Id",
        };
        var d = ItemHudFile.Parse(file).For("A/B");
        Check(d.Counts.SequenceEqual(new[] { "Mana Scarab" }), "empty and duplicate counts dropped");
        Check(d.Slots.Length == ItemHudCharacter.MinSlots, "slots clamped to the minimum");
        Check(d.Slots[1] == null, "a slot with no id and no name stays empty");
        Check(d.Slots[2] == new RemoteSlot(17, "Decimal Id"), "a decimal id reads");
        Check(d.Other.Count(o => o.StartsWith("slot=", StringComparison.Ordinal)) == 3, "unreadable slot lines kept as they were");
        Check(ItemHudFile.Parse(Array.Empty<string>()).Order.Count == 0, "an empty file");
    }

    private static void EditsAreImmutable()
    {
        Case("EditsAreImmutable");
        var a = ItemHudCharacter.Empty.WithCount("Mana Scarab", out _).WithSlot(0, new RemoteSlot(1, "Pyreal"));
        string[] counts = a.Counts;
        var slots = a.Slots;
        var b = a.WithCount("Healing Kit", out _).WithSlot(0, null).WithSlotCount(12);
        Check(counts.Length == 1 && a.Counts.Length == 1, "the old lists are unchanged");
        Check(slots[0]?.Name == "Pyreal" && a.Slots.Length == ItemHudCharacter.DefaultSlots, "the old slots are unchanged");
        Check(b.Counts.Length == 2 && b.Slots[0] == null && b.Slots.Length == 12, "the new ones have the edits");
        Check(ItemHudCharacter.Empty.Counts.Length == 0 && ItemHudCharacter.Empty.Slots.All(s => s == null), "Empty stays empty");
    }

    /// <summary>The Mini Remote's box size: clamped, kept through edits, and saved only when not the default.</summary>
    private static void SlotSizes()
    {
        Case("SlotSizes");
        var c = ItemHudCharacter.Empty;
        Check(c.SlotSize == ItemHudCharacter.DefaultSlotSize, "default size");
        Check(ReferenceEquals(c.WithSlotSize(ItemHudCharacter.DefaultSlotSize), c), "the same size: no change");
        Check(c.WithSlotSize(1).SlotSize == ItemHudCharacter.MinSlotSize, "clamped to the minimum");
        Check(c.WithSlotSize(1000).SlotSize == ItemHudCharacter.MaxSlotSize, "clamped to the maximum");
        var big = c.WithSlotSize(48);
        var edited = big.WithCount("Mana Scarab", out _).WithSlot(0, new RemoteSlot(1, "Pyreal")).WithSlotCount(12);
        Check(edited.SlotSize == 48, "other edits keep the size");

        var file = new ItemHudFile.Contents();
        file.Set("A/Big", big);
        file.Set("A/Plain", c.WithCount("Healing Kit", out _));
        string text = ItemHudFile.Write(file);
        Check(text.Contains("slotsize=48\n"), "a non-default size is written");
        Check(text.Split("slotsize=").Length == 2, "the default size isn't written");
        var back = RoundTrip(file);
        Check(back.For("A/Big").SlotSize == 48 && back.For("A/Plain").SlotSize == ItemHudCharacter.DefaultSlotSize,
            "sizes read back per character");

        string[] bad = { "[A/B]", "slotsize=banana", "[A/C]", "slotsize=5", "[A/D]", "slotsize=999" };
        var parsed = ItemHudFile.Parse(bad);
        Check(parsed.For("A/B").SlotSize == ItemHudCharacter.DefaultSlotSize, "an unreadable size keeps the default");
        Check(parsed.For("A/C").SlotSize == ItemHudCharacter.MinSlotSize, "a small size clamps up");
        Check(parsed.For("A/D").SlotSize == ItemHudCharacter.MaxSlotSize, "a large size clamps down");
    }
}

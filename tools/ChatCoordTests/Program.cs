using RynthCore.Engine.UI.Data;

namespace ChatCoordTests;

/// <summary>
/// The chat window's coordinate finder (ChatCoords): the forms players and the game write
/// coordinates in, and ordinary chat that must not become coordinates.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;

    private static int Main()
    {
        One("Meet me at 42.1N, 33.6E", 42.1, 33.6, "42.1N, 33.6E");
        One("42.1N 33.6E", 42.1, 33.6, "42.1N, 33.6E");
        One("42.1N33.6E", 42.1, 33.6, "42.1N, 33.6E");
        One("loc 21.5S, 1.8W please", -21.5, -1.8, "21.5S, 1.8W");
        One("33.6E, 42.1N", 42.1, 33.6, "42.1N, 33.6E");
        One("at 42.1 N, 33.6 E now", 42.1, 33.6, "42.1N, 33.6E");
        One("(12.5n, 0.4w)", 12.5, -0.4, "12.5N, 0.4W");
        One("Your location is: 0xA9B40019 [84.000 7.100 42.005] 0.0 0.0 0.0 1.0 = 42.1N, 33.6E", 42.1, 33.6, "42.1N, 33.6E");
        One("drudges at 42N 33E", 42, 33, "42.0N, 33.0E");
        One("0.0N, 101.9W", 0, -101.9, "0.0N, 101.9W");
        // Pasted the way players often type them (lowercase, no space): RynthChat makes these links.
        One("82.3n,34.2e", 82.3, 34.2, "82.3N, 34.2E");
        One("Bob says, \"meet at 82.3n, 34.2e\"", 82.3, 34.2, "82.3N, 34.2E");

        // Two in one line, in order.
        var two = ChatCoords.Find("from 42.1N, 33.6E to 21.5S, 1.8W");
        Check(two.Count == 2, "two pairs");
        Check(two.Count == 2 && two[0].Ns == 42.1 && two[1].Ns == -21.5, "in order");
        Check(two.Count == 2 && two[0].Start < two[1].Start, "start positions");
        var first = two.Count > 0 ? two[0] : default;
        Check(first.Start == 5 && "from 42.1N, 33.6E to".Substring(first.Start, first.Length) == "42.1N, 33.6E", "the span covers the pair");

        // Not coordinates.
        None("give me 5 s, 2 e");
        None("lvl 120N 33E");                // off the map
        None("Holtburg is nice");
        None("brb 5 min");
        None("42.1N");                       // half a pair
        None("42.1N 33.6N");                 // two N/S
        None("v1.2.3n 4.5e");                // part of a word / version
        None("");
        None(null);

        PreCheck();
        LinkSpans();

        Console.WriteLine(_failed == 0 && _checks > 0 ? $"PASS ({_checks} checks)" : $"FAIL ({_failed} of {_checks})");
        return _failed == 0 && _checks > 0 ? 0 : 1;
    }

    /// <summary>The cheap pre-check ChatFace runs before the regex must never skip a line Find would link.</summary>
    private static void PreCheck()
    {
        foreach (string t in new[] { "42.1N, 33.6E", "42N 33E", "33.6E 42.1N", "at 42.1 N, 33.6 E now", "82.3n,34.2e", "0.5S, 101.2W", "x1	n 2e" })
            Check(ChatCoords.MayHaveCoords(t), $"pre-check lets \"{t}\" through");
        foreach (string t in new[] { "", "hello world", "no digits here N E", "N5", "12  N (two spaces)", "forty two north" })
            Check(!ChatCoords.MayHaveCoords(t), $"pre-check skips \"{t}\"");

        // Soundness: over random lines built from coordinate-ish pieces, Find never finds a pair the pre-check skipped.
        var rng = new Random(20261019);
        string[] pieces = { "42.1", "33.6", "N", "S", "E", "W", "n", "e", " ", ", ", " ", "x", "	", "/", "101.9", "7.5", "5", "120" };
        int found = 0;
        for (int k = 0; k < 60000; k++)
        {
            var sb = new System.Text.StringBuilder();
            for (int n = rng.Next(1, 9); n > 0; n--) sb.Append(pieces[rng.Next(pieces.Length)]);
            string line = sb.ToString();
            if (ChatCoords.Find(line).Count > 0)
            {
                found++;
                if (!ChatCoords.MayHaveCoords(line)) Check(false, $"pre-check skipped \"{line}\", which holds a coordinate");
            }
        }
        Check(found > 100, $"the random lines held coordinates to test with ({found})");
        Check(true, "pre-check never skipped a line with a coordinate");
    }

    private const uint LinkCol = 0xFF5ECFE0;

    /// <summary>The coordinate links laid over a rule's highlight spans.</summary>
    private static void LinkSpans()
    {
        // No coordinates: the highlights come back as they were.
        var plain = new[] { new ChatSpan(2, 3, 0xFF112233), new ChatSpan(8, 2, 0xFF445566) };
        Same(ChatCoords.LinkSpans(plain, new List<ChatCoord>(), LinkCol), plain, "no coordinates: highlights unchanged");
        Check(ChatCoords.LinkSpans(null, new List<ChatCoord>(), LinkCol).Length == 0, "no spans, no coordinates: nothing");

        // A line with only a coordinate (no rule highlight): one link span over it.
        string text = "meet at 42.1N, 33.6E soon";
        var coords = ChatCoords.Find(text);
        var only = ChatCoords.LinkSpans(null, coords, LinkCol);
        Check(only.Length == 1 && only[0].Start == coords[0].Start && only[0].Length == coords[0].Length && only[0].Argb == LinkCol,
            "a coordinate alone becomes one link span");

        // A highlight across the whole line is cut around the coordinate: before, link, after.
        var whole = new[] { new ChatSpan(0, text.Length, 0xFFAA0000) };
        var cut = ChatCoords.LinkSpans(whole, coords, LinkCol);
        Check(cut.Length == 3, $"a highlight over the coordinate is cut in two around it (got {cut.Length})");
        if (cut.Length == 3)
        {
            Check(cut[0].Start == 0 && cut[0].Length == coords[0].Start && cut[0].Argb == 0xFFAA0000, "the part before keeps the highlight");
            Check(cut[1].Start == coords[0].Start && cut[1].Length == coords[0].Length && cut[1].Argb == LinkCol, "the coordinate keeps the link colour");
            int end = coords[0].Start + coords[0].Length;
            Check(cut[2].Start == end && cut[2].Start + cut[2].Length == text.Length && cut[2].Argb == 0xFFAA0000, "the part after keeps the highlight");
        }

        // A highlight that only covers part of the coordinate loses that part and keeps the rest.
        var half = new[] { new ChatSpan(coords[0].Start - 3, 8, 0xFF00AA00) };   // ends inside the coordinate
        var halfCut = ChatCoords.LinkSpans(half, coords, LinkCol);
        Check(halfCut.Length == 2 && halfCut[0].Argb == 0xFF00AA00 && halfCut[0].Length == 3 && halfCut[1].Argb == LinkCol,
            "a highlight ending inside the coordinate is cut at it");

        // A highlight beside the coordinate (no overlap) is untouched; the result is sorted and never overlaps.
        var beside = new[] { new ChatSpan(0, 4, 0xFF0000AA), new ChatSpan(coords[0].Start + coords[0].Length + 1, 4, 0xFF0000BB) };
        var res = ChatCoords.LinkSpans(beside, coords, LinkCol);
        Check(res.Length == 3 && res[0].Argb == 0xFF0000AA && res[1].Argb == LinkCol && res[2].Argb == 0xFF0000BB, "highlights beside it stay, in order");
        NoOverlap(res, "beside");

        // Two coordinates and several highlights: sorted, no overlap, every coordinate present exactly once.
        string two = "from 42.1N, 33.6E to 21.5S, 1.8W now";
        var cs = ChatCoords.Find(two);
        var hl = new[] { new ChatSpan(0, 9, 0xFF010101), new ChatSpan(10, 20, 0xFF020202) };
        var r2 = ChatCoords.LinkSpans(hl, cs, LinkCol);
        NoOverlap(r2, "two coordinates");
        Check(r2.Count(s => s.Argb == LinkCol) == 2, "both coordinates are links");
        foreach (ChatCoord c in cs)
            Check(r2.Any(s => s.Start == c.Start && s.Length == c.Length && s.Argb == LinkCol), $"link span for {c.Text}");
    }

    private static void NoOverlap(ChatSpan[] spans, string what)
    {
        bool ok = true;
        for (int i = 1; i < spans.Length; i++)
            if (spans[i].Start < spans[i - 1].Start + spans[i - 1].Length) ok = false;
        Check(ok, $"{what}: sorted and non-overlapping");
    }

    private static void Same(ChatSpan[] got, ChatSpan[] want, string what) =>
        Check(got.Length == want.Length && got.Zip(want).All(p => p.First == p.Second), what);

    private static void One(string text, double ns, double ew, string formatted)
    {
        var found = ChatCoords.Find(text);
        Check(found.Count == 1, $"one pair in \"{text}\" (found {found.Count})");
        if (found.Count != 1) return;
        Check(Math.Abs(found[0].Ns - ns) < 1e-9 && Math.Abs(found[0].Ew - ew) < 1e-9,
            $"\"{text}\" -> {ns}, {ew} (got {found[0].Ns}, {found[0].Ew})");
        Check(found[0].Text == formatted, $"\"{text}\" formats as {formatted} (got {found[0].Text})");
    }

    private static void None(string? text)
    {
        var found = ChatCoords.Find(text);
        Check(found.Count == 0, $"no pair in \"{text}\" (found {found.Count}: {string.Join("; ", found.Select(f => f.Text))})");
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine("  [FAIL] " + what);
    }
}

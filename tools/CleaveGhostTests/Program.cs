using RynthCore.Engine.Compatibility;
using RynthCore.Engine.ImGuiBackend.Hud;

namespace CleaveGhostTests;

internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        Run("cleave: the second mob a Tetsubo hits loses its share", CleaveOntoUnfedPlate);
        Run("cleave: a hit the server already reported (queried target) changes nothing", ServerAlreadyReported);
        Run("cleave: a plate with a live server feed is left to the server", LiveFeedLeftAlone);
        Run("cleave: never a kill, never a bad share, nothing to match", Bounds);
        Run("cleave: an earlier estimate counts down further", EstimateChains);
        Run("freshness: object messages are noted, a delete forgets, others are ignored", Freshness);
        Console.WriteLine($"{_checks} check(s), {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        _case = name;
        int before = _failed;
        try { test(); }
        catch (Exception ex) { Fail($"threw {ex.GetType().Name}: {ex.Message}"); }
        Console.WriteLine($"[{(_failed == before ? "PASS" : "FAIL")}]  {name}");
    }

    private static void Check(bool ok, string what) { _checks++; if (!ok) Fail(what); }
    private static void Fail(string what) { _failed++; Console.WriteLine($"   FAIL {_case}: {what}"); }
    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.0005f;

    private const float Share204 = 204f / 2300f;   // 204 on an Olthoi Slasher (2300 hp): 0.089

    private static void CleaveOntoUnfedPlate()
    {
        // The queried target (0x10) got its 0x01C0 for its own hit; the second mob (0x20, 1.6 m)
        // and a third further off (0x30, 4 m) have no feed.
        CleaveHealth.Candidate[] c =
        [
            new(0x10, 1.4f, true, 0.60f, 0.70f, false, -0.05),   // queried target: its own 0x01C0 for its own (bigger) hit
            new(0x20, 1.6f, false, 1f, -1f, false, 0),
            new(0x30, 4.0f, true, 0.80f, -1f, true, 3.0),         // an older estimate
        ];
        uint id = CleaveHealth.Pick(c, Share204, out float ratio);
        Check(id == 0x20, $"nearest unfed plate takes it (got 0x{id:X})");
        Check(Near(ratio, 1f - Share204), $"full -> {1f - Share204:0.000} (got {ratio:0.000})");
    }

    private static void ServerAlreadyReported()
    {
        // Same name, the queried target's 0x01C0 dropped it by the same share 0.1 s after the hit.
        CleaveHealth.Candidate[] c =
        [
            new(0x10, 1.4f, true, 0.70f - Share204, 0.70f, false, -0.1),
            new(0x20, 1.6f, false, 1f, -1f, false, 0),
        ];
        Check(CleaveHealth.Pick(c, Share204, out _) == 0, "the server's own update was this hit: no estimate anywhere");
    }

    private static void LiveFeedLeftAlone()
    {
        // The only plate with the name has a live feed (updated 0.5 s before) but the drop doesn't match.
        CleaveHealth.Candidate[] c = [new(0x10, 1.4f, true, 0.5f, 0.52f, false, 0.5)];
        Check(CleaveHealth.Pick(c, Share204, out _) == 0, "live-fed plate: no estimate");
        // An old feed (20 s) is not live: the plate takes the hit.
        CleaveHealth.Candidate[] old = [new(0x10, 1.4f, true, 0.5f, 0.52f, false, 20)];
        Check(CleaveHealth.Pick(old, Share204, out float r) == 0x10 && Near(r, 0.5f - Share204), "stale feed: estimate from its last ratio");
    }

    private static void Bounds()
    {
        CleaveHealth.Candidate[] c = [new(0x20, 1.6f, true, 0.05f, -1f, true, 2)];
        Check(CleaveHealth.Pick(c, 0.25f, out float r) == 0x20 && Near(r, CleaveHealth.MinRatio), "never below MinRatio (a kill comes from the kill message)");
        Check(CleaveHealth.Pick(c, 0f, out _) == 0, "no share: nothing");
        Check(CleaveHealth.Pick(c, float.NaN, out _) == 0, "NaN share: nothing");
        Check(CleaveHealth.Pick(c, 3f, out _) == 0, "an impossible share: nothing");
        Check(CleaveHealth.Pick(ReadOnlySpan<CleaveHealth.Candidate>.Empty, Share204, out _) == 0, "no plate with the name: nothing");
    }

    private static void EstimateChains()
    {
        // Two hits on the same unfed mob: the second starts from the first estimate (estimates are never "live feed").
        CleaveHealth.Candidate[] first = [new(0x20, 1.6f, false, 1f, -1f, false, 0)];
        CleaveHealth.Pick(first, Share204, out float r1);
        CleaveHealth.Candidate[] second = [new(0x20, 1.6f, true, r1, 1f, true, 1.2)];
        Check(CleaveHealth.Pick(second, Share204, out float r2) == 0x20 && Near(r2, 1f - 2 * Share204), $"second hit: {r2:0.000}");
    }

    private static void Freshness()
    {
        ObjectFreshness.Clear();
        Check(ObjectFreshness.AgeMs(0x800B87E3, 1000) == -1, "never heard of: -1");
        ObjectFreshness.Note(0xF74C, 0x800B87E3, 1000);                 // UpdateMotion
        Check(ObjectFreshness.AgeMs(0x800B87E3, 4000) == 3000, "3 s since its motion update");
        ObjectFreshness.Note(0xF748, 0x800B87E3, 5000);                 // UpdatePosition
        Check(ObjectFreshness.AgeMs(0x800B87E3, 5200) == 200, "position update refreshes it");
        ObjectFreshness.Note(0xF7B0, 0x800B87E3, 9000);                 // a game event: not about the object
        Check(ObjectFreshness.AgeMs(0x800B87E3, 9000) == 4000, "game events don't count");
        ObjectFreshness.Note(0xF747, 0x800B87E3, 9500);                 // DeleteObject
        Check(ObjectFreshness.AgeMs(0x800B87E3, 9600) == -1, "deleted: forgotten");
        ObjectFreshness.Note(0xF745, 0x800B87E3, 10000);                // CreateObject again
        Check(ObjectFreshness.AgeMs(0x800B87E3, 10000) == 0, "created again: fresh");
        Check(ObjectFreshness.IsObjectMessage(0xF74E) && ObjectFreshness.IsObjectMessage(0xF7DB) && !ObjectFreshness.IsObjectMessage(0xF7E0),
            "vector/update-object count, server text doesn't");
        ObjectFreshness.Note(0xF74C, 0, 1);
        Check(ObjectFreshness.AgeMs(0, 2) == -1, "id 0 ignored");
    }
}

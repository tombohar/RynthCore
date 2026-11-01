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
        Run("health probe: fight plates first, then never-read, then old quiet ones; never the stream's owner", ProbeFightFirst);
        Run("health probe: the stalest fight plate first, never-read plates nearest first", ProbeStalestAndNearest);
        Run("health probe: nothing due (fresh, dead, no id) sends nothing", ProbeNothingDue);
        Run("health probe: a probe's replies are the HUD's; the owner's and plugins' are theirs", ProbeReplies);
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

    // ── HealthProbe: the unselected plates' health ──────────────────────

    private static void ProbeFightFirst()
    {
        // 0x10 is the selection (streamed). 0x20 is in the fight, last read 3 s ago; 0x30 never read;
        // 0x40 quiet at full health, read 20 s ago.
        HealthProbe.Candidate[] c =
        [
            new(0x10, 1.0f, true, 5.0, true),
            new(0x20, 4.0f, true, 3.0, true),
            new(0x30, 2.0f, false, 0, false),
            new(0x40, 1.5f, true, 20.0, false),
        ];
        Check(HealthProbe.Pick(c, owner: 0x10) == 0x20, "a fight plate the server isn't streaming goes first");
        Check(HealthProbe.Pick(c, owner: 0x20) == 0x10, "the stream's owner is never probed (the selection is, once it isn't the owner)");
        HealthProbe.Candidate[] noFight = [c[2], c[3]];
        Check(HealthProbe.Pick(noFight, owner: 0x10) == 0x30, "then a plate never read");
        HealthProbe.Candidate[] quiet = [c[3]];
        Check(HealthProbe.Pick(quiet, owner: 0x10) == 0x40, "then a quiet plate whose reading is old");
    }

    private static void ProbeStalestAndNearest()
    {
        HealthProbe.Candidate[] fight =
        [
            new(0x20, 1.0f, true, 2.5, true),
            new(0x21, 9.0f, true, 6.0, true),
            new(0x22, 3.0f, false, 0, true),   // hitting you, never read: before any reading
        ];
        Check(HealthProbe.Pick(fight, 0) == 0x22, "a fight plate never read beats every read one");
        Check(HealthProbe.Pick(fight[..2], 0) == 0x21, "the stalest fight plate first");
        HealthProbe.Candidate[] unseen =
        [
            new(0x30, 7.0f, false, 0, false),
            new(0x31, 2.0f, false, 0, false),
            new(0x32, 5.0f, false, 0, false),
        ];
        Check(HealthProbe.Pick(unseen, 0) == 0x31, "never-read plates: the nearest first");
    }

    private static void ProbeNothingDue()
    {
        HealthProbe.Candidate[] c =
        [
            new(0x20, 1.0f, true, HealthProbe.ActiveRefreshSec - 0.1, true),   // fresh fight plate
            new(0x40, 1.0f, true, HealthProbe.IdleRefreshSec - 0.1, false),    // quiet, read recently
            new(0x50, 1.0f, true, 30.0, true, Dead: true),                      // dead
            new(0, 1.0f, false, 0, true),                                       // no id
        ];
        Check(HealthProbe.Pick(c, 0) == 0, "nothing due: no query (no traffic)");
        Check(HealthProbe.Pick(ReadOnlySpan<HealthProbe.Candidate>.Empty, 0) == 0, "no plates: nothing");
        HealthProbe.Candidate[] nan = [new(0x20, 1.0f, true, double.NaN, true)];
        Check(HealthProbe.Pick(nan, 0) == 0, "a NaN age is never due");
        HealthProbe.Candidate[] due = [new(0x20, 1.0f, true, HealthProbe.ActiveRefreshSec, true)];
        Check(HealthProbe.Pick(due, 0) == 0x20, "a fight plate exactly ActiveRefreshSec old is due");
    }

    private static void ProbeReplies()
    {
        HealthProbe.Reset();
        Check(!HealthProbe.TryGetOwner(out _), "after a reset the stream's owner is unknown");
        HealthProbe.NoteQuery(0x10);                      // the selection / a plugin asked for 0x10
        Check(HealthProbe.TryGetOwner(out uint o) && o == 0x10, "the owner is the last queried id");

        HealthProbe.NoteProbe(0x20, 1000);
        Check(HealthProbe.IsProbeReply(0x20, 1150), "the reply to a probe is the HUD's");
        Check(HealthProbe.IsProbeReply(0x20, 1300), "... every path that reports it (window, not consume-once)");
        Check(!HealthProbe.IsProbeReply(0x10, 1150), "the owner's replies go to plugins");
        Check(!HealthProbe.IsProbeReply(0x30, 1150), "an id nobody probed goes to plugins");
        Check(!HealthProbe.IsProbeReply(0x20, 1000 + HealthProbe.ReplyWindowMs + 1), "after the window the id's replies go to plugins again");
        Check(!HealthProbe.IsProbeReply(0x20, 1200), "... and the expired entry is gone");

        HealthProbe.NoteProbe(0x21, 2000);
        HealthProbe.NoteQuery(0x21);                      // a plugin asked for the same monster right after
        Check(!HealthProbe.IsProbeReply(0x21, 2100), "a plugin's own query for a probed id takes its replies back");
        Check(HealthProbe.TryGetOwner(out o) && o == 0x21, "... and owns the stream");

        // No owner to hand the stream back to: the probed id keeps streaming to us, all of it the HUD's.
        HealthProbe.NoteQuery(0);
        HealthProbe.NoteProbe(0x22, 3000, sticky: true);
        Check(HealthProbe.IsProbeReply(0x22, 3000 + 60_000), "sticky probe: still the HUD's a minute later");
        HealthProbe.NoteQuery(0x10);
        Check(!HealthProbe.IsProbeReply(0x22, 3000 + 60_001), "the next query moves the stream: sticky entry gone");

        for (uint i = 1; i <= 20; i++) HealthProbe.NoteProbe(0x100 + i, 5000);   // more than the ledger holds
        Check(HealthProbe.IsProbeReply(0x114, 5100), "the newest probes are remembered when the ledger wraps");
        Check(!HealthProbe.IsProbeReply(0x101, 5100), "the oldest are dropped (bounded)");
        HealthProbe.NoteProbe(0, 5000);
        Check(!HealthProbe.IsProbeReply(0, 5000), "id 0 never");
        long before = HealthProbe.ProbeCount;
        HealthProbe.NoteProbe(0x200, 6000);
        Check(HealthProbe.ProbeCount == before + 1, "probes are counted");
        HealthProbe.Reset();
        Check(!HealthProbe.IsProbeReply(0x114, 5100) && !HealthProbe.TryGetOwner(out _), "reset (logout) forgets everything");
    }
}

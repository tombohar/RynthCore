// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/HealthProbe.cs
//  Fresh health for the monster plates that are NOT the selected / queried
//  target (2026-10-31: "only the selected monster's plate keeps updating; the
//  others show a number that stops changing while they take damage").
//
//  Why they froze: ACE sends a creature's health (0x01C0 UpdateHealth) only
//  to players whose current health-query target it is (Player.HandleAction-
//  QueryHealth -> UpdateSelectedTarget; Creature.OnHealthUpdate walks that
//  creature's selectedTargets). One target per player: every QueryHealth
//  moves the stream. So a plate got one value when it was selected / queried /
//  appraised and then nothing, and CleaveHealth estimates only cover the
//  player's own weapon hits (no other players, pets, procs or DoTs).
//
//  The fix: the HUD asks for them, slowly. At most one probe per interval
//  (setting, default 1 s), and each probe is immediately followed by a query
//  for the stream's owner (the last target a plugin or the selection asked
//  for), so the selected / fight target keeps its live stream. Cost per probe:
//  two 0x01BF game actions up, two 0x01C0 replies down (well under 100 bytes).
//
//  Who is probed (Pick): plates on screen, not the stream owner, not dead.
//    1. "active" plates (hurt, hit by you, or hitting you) whose last server
//       reading is ActiveRefreshSec old or more, the stalest first;
//    2. plates never read by the server, nearest first (one probe each);
//    3. quiet plates whose reading is IdleRefreshSec old or more.
//  Nothing due: no traffic.
//
//  A probe's reply is the HUD's: PluginManager records it for the plates but
//  does not hand it to plugins (IsProbeReply), so a bot never reads another
//  player's damage on a monster it isn't fighting as its own hit.
//
//  Pick and the ledger are pure (time is passed in); compiled into
//  tools/CleaveGhostTests. Engine-owned statics only.
// ============================================================================

using System;
using System.Threading;

namespace RynthCore.Engine.ImGuiBackend.Hud;

internal static class HealthProbe
{
    /// <summary>A plate in the fight is re-read when its last server reading is this old (s).</summary>
    public const double ActiveRefreshSec = 2.0;
    /// <summary>A quiet plate (full health, nobody fighting it) is re-read this rarely (s).</summary>
    public const double IdleRefreshSec = 12.0;
    /// <summary>A 0x01C0 for a probed id this soon after the probe (ms) is the probe's reply.</summary>
    public const long ReplyWindowMs = 2000;

    /// <summary>The setting's range and default: seconds between two probes.</summary>
    public const float DefaultIntervalSec = 1.0f, MinIntervalSec = 0.5f, MaxIntervalSec = 5.0f;

    /// <summary>
    /// One plate. <paramref name="Seen"/>: the server has sent its health at least once;
    /// <paramref name="ServerAgeSec"/>: seconds since that last server reading (estimates don't count).
    /// <paramref name="Active"/>: hurt, hit by the player or hitting the player recently.
    /// <paramref name="Dead"/>: its last reading was 0.
    /// </summary>
    public readonly record struct Candidate(uint Id, float Distance, bool Seen, double ServerAgeSec, bool Active, bool Dead = false);

    /// <summary>The plate to probe now, or 0 when none is due. <paramref name="owner"/> is never picked (it is streamed).</summary>
    public static uint Pick(ReadOnlySpan<Candidate> candidates, uint owner)
    {
        uint best = 0;
        int bestTier = -1;
        double bestScore = double.NegativeInfinity;
        float bestD = float.MaxValue;
        foreach (Candidate c in candidates)
        {
            if (c.Id == 0 || c.Id == owner || c.Dead) continue;
            int tier;
            double score;
            if (c.Active)
            {
                if (c.Seen && !(c.ServerAgeSec >= ActiveRefreshSec)) continue;
                tier = 2;
                score = c.Seen ? c.ServerAgeSec / ActiveRefreshSec : double.MaxValue;
            }
            else if (!c.Seen)
            {
                tier = 1;
                score = 0;   // all equal: the nearest wins
            }
            else
            {
                if (!(c.ServerAgeSec >= IdleRefreshSec)) continue;
                tier = 0;
                score = c.ServerAgeSec / IdleRefreshSec;
            }
            bool better = tier > bestTier ||
                          (tier == bestTier && (score > bestScore || (score == bestScore && c.Distance < bestD)));
            if (!better) continue;
            best = c.Id; bestTier = tier; bestScore = score; bestD = c.Distance;
        }
        return best;
    }

    // ── The server's health stream: whose it is, and which replies are the HUD's ──

    private static readonly object Sync = new();
    private static uint _owner;
    private static bool _ownerKnown;
    private const int MaxPending = 8;
    private static readonly uint[] PendingId = new uint[MaxPending];
    private static readonly long[] PendingAt = new long[MaxPending];
    private static readonly bool[] PendingSticky = new bool[MaxPending];
    private static int _pendingNext;
    private static long _probes;

    /// <summary>Probes sent since the engine started (status line).</summary>
    public static long ProbeCount => Interlocked.Read(ref _probes);

    /// <summary>
    /// A health query that is not the HUD's went out for <paramref name="id"/> (a plugin's QueryHealth,
    /// or the client's own on a selection change): the server streams that id now, and its replies
    /// belong to whoever asked. Any thread.
    /// </summary>
    public static void NoteQuery(uint id)
    {
        lock (Sync)
        {
            _owner = id;
            _ownerKnown = true;
            // The stream moved: a sticky probe (sent with no owner to hand back to) no longer streams,
            // and the newly queried id's replies are the asker's.
            for (int i = 0; i < MaxPending; i++)
                if (PendingId[i] == id || PendingSticky[i]) { PendingId[i] = 0; PendingSticky[i] = false; }
        }
    }

    /// <summary>The id the server streams health for (see NoteQuery); false before anything was noted.</summary>
    public static bool TryGetOwner(out uint owner)
    {
        lock (Sync)
        {
            owner = _owner;
            return _ownerKnown;
        }
    }

    /// <summary>
    /// The HUD probed <paramref name="id"/> at <paramref name="nowMs"/>. <paramref name="sticky"/>: no owner was
    /// queried after it, so the server keeps streaming this id to us: every reply for it is the HUD's until
    /// the next NoteQuery (no ReplyWindowMs).
    /// </summary>
    public static void NoteProbe(uint id, long nowMs, bool sticky = false)
    {
        if (id == 0) return;
        lock (Sync)
        {
            int slot = -1;
            for (int i = 0; i < MaxPending; i++)
                if (PendingId[i] == id) { slot = i; break; }
            if (slot < 0) { slot = _pendingNext; _pendingNext = (_pendingNext + 1) % MaxPending; }
            PendingId[slot] = id;
            PendingAt[slot] = nowMs;
            PendingSticky[slot] = sticky;
            _probes++;
        }
    }

    /// <summary>True when a health reply for <paramref name="id"/> answers a HUD probe (sent within ReplyWindowMs).</summary>
    public static bool IsProbeReply(uint id, long nowMs)
    {
        if (id == 0) return false;
        lock (Sync)
        {
            if (_ownerKnown && id == _owner) return false;   // someone else asked for it too
            for (int i = 0; i < MaxPending; i++)
                if (PendingId[i] == id)
                {
                    if (PendingSticky[i]) return true;
                    long age = nowMs - PendingAt[i];
                    if (age >= 0 && age <= ReplyWindowMs) return true;
                    PendingId[i] = 0;   // expired
                    return false;
                }
            return false;
        }
    }

    /// <summary>Logout / character switch: nothing carries over.</summary>
    public static void Reset()
    {
        lock (Sync)
        {
            _owner = 0;
            _ownerKnown = false;
            Array.Clear(PendingId);
            Array.Clear(PendingSticky);
            _pendingNext = 0;
        }
    }
}

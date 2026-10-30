// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/CleaveHealth.cs
//  Health for monsters the player hits but the server doesn't stream health
//  for (2026-10-12, Drakkon with a Tetsubo: "the HUD health for monsters only
//  changes if they are targeted. It doesn't change for AoE monsters").
//
//  ACE pushes UpdateHealth (0x01C0) only for the object a player has selected
//  or queried (RynthAi queries its combat target), so the second and third mob
//  a cleave hits kept their old bar. Every hit does send an AttackerNotification
//  (0x01B1) naming the monster - no id - with the damage and its share of the
//  monster's max health (damage / max: 204 on an Olthoi Slasher's 2300 reads
//  0.089). This turns that share into a bar estimate on the plate it belongs
//  to. Pure: no engine state; MonsterHud passes the candidates in.
//
//  Rules, for the plates carrying the hit's name:
//    * one whose real (server) health dropped by this share around the hit
//      already got it from 0x01C0: nothing to do;
//    * a plate with a live server feed (a real update in the last
//      LiveFeedSec) is left to the server;
//    * otherwise the nearest one takes the share off its last ratio (full when
//      never seen), never below MinRatio - a kill comes from the kill message
//      and the object's deletion, not from an estimate.
// ============================================================================

using System;

namespace RynthCore.Engine.ImGuiBackend.Hud;

internal static class CleaveHealth
{
    /// <summary>A real update this close to the hit (s) with the same drop is the same hit.</summary>
    public const double SameHitSec = 0.6;
    /// <summary>Drop-vs-share tolerance for "the same hit".</summary>
    public const float MatchTolerance = 0.006f;   // both sides are the server's exact numbers; a different hit is rarely this close
    /// <summary>A real update this recent (s) means the server is feeding this plate.</summary>
    public const double LiveFeedSec = 1.5;
    /// <summary>An estimate never takes a bar lower than this.</summary>
    public const float MinRatio = 0.01f;

    /// <summary>One plate with the hit's name. <paramref name="ObsAgeSec"/>: seconds between its
    /// last health observation and the hit (negative = the observation came after it).</summary>
    public readonly record struct Candidate(uint Id, float Distance, bool HasObs, float Ratio, float Prev, bool Estimated, double ObsAgeSec);

    /// <summary>
    /// The plate a hit of <paramref name="share"/> (damage / max health) belongs to and its new
    /// ratio, or 0 when no plate should change.
    /// </summary>
    public static uint Pick(ReadOnlySpan<Candidate> candidates, float share, out float newRatio)
    {
        newRatio = 0f;
        if (candidates.Length == 0 || !(share > 0f) || share > 1.5f) return 0;

        foreach (Candidate c in candidates)
        {
            if (!c.HasObs || c.Estimated || c.Prev < 0f) continue;
            if (Math.Abs(c.ObsAgeSec) <= SameHitSec && Math.Abs((c.Prev - c.Ratio) - share) <= MatchTolerance)
                return 0;   // the server reported this hit itself
        }

        uint best = 0;
        float bestD = float.MaxValue, bestRatio = 1f;
        foreach (Candidate c in candidates)
        {
            if (c.HasObs && !c.Estimated && c.ObsAgeSec >= -SameHitSec && c.ObsAgeSec <= LiveFeedSec) continue;   // server-fed
            if (c.Distance < bestD)
            {
                best = c.Id;
                bestD = c.Distance;
                bestRatio = c.HasObs ? c.Ratio : 1f;
            }
        }
        if (best == 0) return 0;
        newRatio = Math.Max(MinRatio, bestRatio - share);
        return best;
    }
}

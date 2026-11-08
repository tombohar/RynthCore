// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/MonsterHudData.cs
//  What the nameplates know about a monster beyond the client's object
//  tables: its last health ratio (and absolute HP when a max is known) and
//  whether it hit the player recently.
//
//  Fed by two existing chokepoints (one line each, no new hooks); each health
//  change is passed on to HudFeed (combat text kills / heals):
//    PluginManager.QueueUpdateHealth - every health observation the engine
//      makes (0x01C0 UpdateHealth, QueryHealthResponse, 0xC9 appraisal
//      CreatureProfile, player vitals); runs whether or not plugins load.
//    SmartBoxHooks.ParseGameEventDamage - DefenderNotification (0x01B2,
//      "X hits you"), which names the attacker (no id on the wire).
//  Both run on AC's main thread today; the lock keeps them safe if a caller
//  ever moves. Bounded: a full table is cleared wholesale (advisory data).
//  Engine-owned statics only (collectible with the engine generation).
//
//  Plugin API v93 (2026-11-07) reads it from plugin threads too:
//  TryGetServerHealth hands out the last SERVER reading (probe replies
//  included, CleaveHealth estimates never) and its age, under the same lock.
//  Compiled into tools/CleaveGhostTests (with a stand-in HudFeed).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RynthCore.Engine.ImGuiBackend.Hud;

/// <summary>Last health observation for one object. <paramref name="Prev"/>: the ratio before it
/// (-1 = first). <paramref name="Estimated"/>: worked out from a hit (CleaveHealth), not sent by the server.
/// <paramref name="ServerTicks"/>: when the server last sent this object's health (0 = never; estimates keep it).
/// <paramref name="ServerRatio"/>: the ratio the server last sent (-1 = never; an estimate keeps it, so it stays
/// the server's number while <paramref name="Ratio"/> shows the estimate).</summary>
internal readonly record struct HealthObservation(float Ratio, uint Current, uint Max, long Ticks, float Prev = -1f, bool Estimated = false,
    long ServerTicks = 0, float ServerRatio = -1f);

internal static class MonsterHudData
{
    private static readonly object Sync = new();
    private static readonly Dictionary<uint, HealthObservation> Health = new(256);
    private static readonly Dictionary<string, long> Attackers = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxHealthEntries = 2048;
    private const int MaxAttackerEntries = 64;

    /// <summary>
    /// A health update for <paramref name="id"/>. A max of 0 (ratio-only update)
    /// keeps the last known max. Any thread; never blocks for long.
    /// <paramref name="estimated"/>: from a hit's share of max health (CleaveHealth), not the server.
    /// </summary>
    public static void RecordHealth(uint id, float ratio, uint current, uint max, bool estimated = false)
    {
        if (id == 0 || float.IsNaN(ratio)) return;
        ratio = Math.Clamp(ratio, 0f, 1f);
        long now = Stopwatch.GetTimestamp();
        float before = -1f;
        bool prevWasEstimate = false;
        lock (Sync)
        {
            bool had = Health.TryGetValue(id, out HealthObservation prev);
            if (had) before = prev.Ratio;
            prevWasEstimate = had && prev.Estimated;
            if (max == 0 && had && prev.Max > 0)
            {
                max = prev.Max;
                current = (uint)Math.Round(max * ratio);
            }
            if (Health.Count >= MaxHealthEntries && !Health.ContainsKey(id))
                Health.Clear();
            long serverTicks = estimated ? (had ? prev.ServerTicks : 0) : now;
            float serverRatio = estimated ? (had ? prev.ServerRatio : -1f) : ratio;
            Health[id] = new HealthObservation(ratio, current, max, now, before, estimated, serverTicks, serverRatio);
        }
        // An estimate never kills (CleaveHealth keeps it above 0) and the server correcting one
        // isn't a heal: the combat text sees an estimate's successor as a first observation.
        if (estimated) return;
        // Kills and heals for the combat text; a death also drops the monster's debuffs.
        HudFeed.OnHealth(id, prevWasEstimate ? -1f : before, ratio, max);
    }

    public static bool TryGetHealth(uint id, out HealthObservation h)
    {
        lock (Sync)
            return Health.TryGetValue(id, out h);
    }

    /// <summary>
    /// Plugin API v93 GetMonsterHealth: the last health ratio the SERVER sent for <paramref name="id"/>
    /// (0x01C0 UpdateHealth, the HUD's own probe replies included; QueryHealthResponse; appraisal) and
    /// how many ms ago (saturates at uint.MaxValue). Never an estimate (CleaveHealth): an id the server
    /// never sent health for is false. Read-only, any thread (the table's lock), no game calls.
    /// </summary>
    public static bool TryGetServerHealth(uint id, out float ratio, out uint ageMs)
        => TryGetServerHealth(id, Stopwatch.GetTimestamp(), out ratio, out ageMs);

    /// <summary><see cref="TryGetServerHealth(uint, out float, out uint)"/> at <paramref name="nowTicks"/>
    /// (Stopwatch ticks; tests pass their own).</summary>
    public static bool TryGetServerHealth(uint id, long nowTicks, out float ratio, out uint ageMs)
    {
        ratio = -1f;
        ageMs = uint.MaxValue;
        if (id == 0) return false;
        HealthObservation h;
        lock (Sync)
            if (!Health.TryGetValue(id, out h)) return false;
        if (h.ServerTicks == 0 || h.ServerRatio < 0f) return false;
        long ticks = Math.Max(0, nowTicks - h.ServerTicks);
        double ms = ticks * 1000.0 / Stopwatch.Frequency;
        ratio = h.ServerRatio;
        ageMs = ms >= uint.MaxValue ? uint.MaxValue : (uint)ms;
        return true;
    }

    /// <summary>A monster named <paramref name="name"/> hit the player. Any thread.</summary>
    public static void RecordAttacker(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        long now = Stopwatch.GetTimestamp();
        lock (Sync)
        {
            if (Attackers.Count >= MaxAttackerEntries && !Attackers.ContainsKey(name))
                Attackers.Clear();
            Attackers[name] = now;
        }
    }

    /// <summary>True when a monster with this name hit the player within <paramref name="seconds"/>.</summary>
    public static bool AttackedRecently(string name, double seconds)
    {
        if (name.Length == 0) return false;
        lock (Sync)
        {
            if (!Attackers.TryGetValue(name, out long t)) return false;
            return (Stopwatch.GetTimestamp() - t) < (long)(seconds * Stopwatch.Frequency);
        }
    }

    /// <summary>Logout / character switch: ids don't survive the session.</summary>
    public static void Clear()
    {
        lock (Sync)
        {
            Health.Clear();
            Attackers.Clear();
        }
    }
}

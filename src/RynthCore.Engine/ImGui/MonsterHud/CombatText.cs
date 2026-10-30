// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/CombatText.cs
//  RynthVision combat text, the ImGui port of the RynthJuice plugin (so it
//  also works in dungeons), plus the gain text:
//    - damage you deal: a number over the monster, white -> yellow -> orange
//      -> red by size, GOLD, bigger and with "!" on a crit;
//    - damage you take: red numbers over your head;
//    - heals: green "+N" over you or a monster;
//    - kill burst: expanding rings where a monster you damaged dies;
//    - gains: "+12,345 XP", "+500 Lum", "+10 Radiance" rising over your head.
//  Numbers pop from the head with a little sideways jitter, drift up and fade
//  (~1.2 s; crits pop larger). At most MaxNumbers live at once.
//
//  Sources (engine-side events, no polling of monsters):
//    AttackerNotification / DefenderNotification (SmartBoxHooks, exact melee
//      and missile damage + crit; the target is named, not id'd);
//    ACE's spell lines "[Critical hit! ]You <verb> <target> for N points
//      with <spell>." and "<source> <verbs> you for N points with <spell>.",
//      and damage over time ("You <verb> <target> for N points of periodic
//      ... damage!", "You receive N points of periodic harm.");
//    health observations (MonsterHudData): a monster reaching 0 (kill) or
//      rising (heal); the player's own vitals snapshot for heals;
//    KillerNotification (the death message names the victim) and object
//      deletes after a recent hit (kill fallback);
//    XP / Luminance: TotalExperience (Int64 1) and AvailableLuminance
//      (Int64 6) read at 4 Hz on AC's thread, rises only (spending never
//      shows); Radiance (Aelrynth): "You gain N Radiance." in chat.
//  ACE sends no damage notification for a killing blow, so the last hit's
//  number is estimated from the last health ratio x max HP when the max is
//  known (appraised), else only the burst shows.
//
//  Hooks run on AC's main thread and only queue (a lock, a struct); the
//  render thread resolves names to monsters and draws. Engine-owned statics.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.ImGuiBackend.Hud;

internal static class CombatText
{
    private const int MaxNumbers = 30;
    private const int MaxBursts = 8;
    private const int MaxGains = 6;
    private const int MaxEvents = 64;
    // Base lifetimes; MonsterHudSettings.NumTime scales them.
    private const float NumberLife = 2.0f, CritLife = 2.5f, HealLife = 1.8f;
    private const float GainLife = 1.8f, GainMergeSec = 0.3f;
    private const float BurstLife = 1.1f;
    private const double KillAfterHitSec = 10.0;    // a death counts as "yours" this long after your last hit
    private const double DeleteKillSec = 8.0;       // a delete counts as a death this soon after your last hit

    private static uint C(uint argb) => MonsterHud.C(argb);
    private static readonly uint StaminaAmber = C(0xFFFFC040), ManaBlue = C(0xFF5AA8FF);
    private static readonly uint Gold = C(0xFFFFD700), HealGreen = C(0xFF50FF50), TakenRed = C(0xFFFF2828),
        White = C(0xFFFFFFFF), PaleYellow = C(0xFFFFF080), Orange = C(0xFFFFA030), Red = C(0xFFFF3828),
        BurstCyan = C(0xFF60FFFF), BurstGold = C(0xFFFFD24A),
        XpGold = C(0xFFFFC83C), LumViolet = C(0xFFC08CFF), RadianceTeal = C(0xFF7FF0E0);

    // ── Events (AC's main thread -> render thread) ──────────────────────
    private enum EvKind : byte { Dealt, Taken, Health, KillMsg, Deleted, Radiance, Test, ChatXp, ChatLum }
    private readonly record struct Ev(EvKind Kind, uint Id, string? Text, int Amount, bool Crit, float Prev, float Ratio, uint Max);

    private static readonly object Sync = new();
    private static readonly List<Ev> Pending = new(MaxEvents);
    private static readonly List<Ev> Work = new(MaxEvents);

    // ── Live effects (render thread) ────────────────────────────────────
    private enum NumKind : byte { Dealt, Taken, Heal, Stamina, Mana }

    private sealed class Number
    {
        public bool Active;
        public NumKind Kind;
        public string Text = "";
        public uint Color;
        public bool Crit;
        public long Born;
        public float Life;
        public bool OnPlayer;
        public uint Cell;
        public float X, Y, Z;
        public float Jitter;
        public uint Target;
    }

    private sealed class Burst
    {
        public bool Active;
        public bool Crit;
        public long Born;
        public uint Cell;
        public float X, Y, Z;    // feet
    }

    private enum GainKind : byte { Xp, Lum, Radiance }

    private sealed class Gain
    {
        public GainKind Kind;
        public long Amount;
        public long Born;
        public long Merged;
        public string Text = "";
    }

    private sealed class Hit
    {
        public long Ticks;
        public uint Cell;
        public float X, Y, Z;    // head
        public float FeetZ;
        public long BurstAt;
    }

    private static readonly Number[] Numbers = CreatePool<Number>(MaxNumbers);
    private static readonly Burst[] Bursts = CreatePool<Burst>(MaxBursts);
    private static readonly List<Gain> Gains = new(MaxGains);
    private static readonly Dictionary<uint, Hit> Hits = new(64);
    private static readonly List<uint> HitEvict = new(16);
    private static int _liveNumbers, _liveBursts;
    private static uint _rng = 0x9E3779B9u;
    private static long _nextHitPrune;

    // Player vitals (heals) and the gain baselines.
    private static uint _prevHealth, _prevMaxHealth, _prevStamina, _prevMaxStamina, _prevMana, _prevMaxMana;
    private static uint _gainOwner;
    private static long _xpBase = -1, _lumBase = -1;
    // A chat line already showed this rise (Aelrynth's kill summary): the property poll skips it.
    private static long _chatXpUntil, _chatLumUntil;
    private static string _lastRadianceLine = "";
    private static long _lastRadianceTicks;
    private static int _statNumbers, _statBursts, _statGains;

    private static T[] CreatePool<T>(int n) where T : new()
    {
        var a = new T[n];
        for (int i = 0; i < n; i++) a[i] = new T();
        return a;
    }

    /// <summary>True while anything is on screen or waiting to be drawn.</summary>
    public static bool HasContent
    {
        get
        {
            if (_liveNumbers > 0 || _liveBursts > 0 || Gains.Count > 0) return true;
            lock (Sync) return Pending.Count > 0;
        }
    }

    public static void Clear()
    {
        lock (Sync) Pending.Clear();
        foreach (Number n in Numbers) n.Active = false;
        foreach (Burst b in Bursts) b.Active = false;
        Gains.Clear();
        Hits.Clear();
        _liveNumbers = _liveBursts = 0;
        _prevHealth = _prevMaxHealth = 0;
        _gainOwner = 0;
        _xpBase = _lumBase = -1;
    }

    /// <summary>When the player last hit this monster (Stopwatch ticks), long.MinValue if never. Render thread.</summary>
    public static long LastHitTicks(uint id) => Hits.TryGetValue(id, out Hit? h) ? h.Ticks : long.MinValue;

    // ════════════════════════════════════════════════════════════════════
    //  Feeds (AC's main thread; queue only)
    // ════════════════════════════════════════════════════════════════════

    private static void Push(in Ev e)
    {
        lock (Sync)
        {
            if (Pending.Count >= MaxEvents) Pending.RemoveAt(0);
            Pending.Add(e);
        }
    }

    private static bool NumbersOn => MonsterHudSettings.Numbers;

    private static long _lastDamageKey, _lastDamageTicks;
    private static string? _lastDamageName;

    /// <summary>AttackerNotification (you hit <paramref name="name"/>) / DefenderNotification (you were hit).</summary>
    public static void OnDamageEvent(bool attacker, string? name, uint damage, bool crit)
    {
        if (!NumbersOn || damage == 0) return;
        // The same event seen by both of SmartBoxHooks' paths within 50 ms is one hit.
        long key = ((long)damage << 2) | (attacker ? 2L : 0L) | (crit ? 1L : 0L);
        long now = Stopwatch.GetTimestamp();
        if (key == _lastDamageKey && name == _lastDamageName && now - _lastDamageTicks < Stopwatch.Frequency / 20) return;
        _lastDamageKey = key; _lastDamageName = name; _lastDamageTicks = now;
        if (attacker)
        {
            if (!string.IsNullOrEmpty(name)) Push(new Ev(EvKind.Dealt, 0, name, (int)damage, crit, 0, 0, 0));
        }
        else if (MonsterHudSettings.NumTaken)
        {
            Push(new Ev(EvKind.Taken, 0, null, (int)damage, crit, 0, 0, 0));
        }
    }

    /// <summary>A health observation for a creature (not the player): kills and heals.</summary>
    public static void OnHealth(uint id, float prev, float ratio, uint max)
    {
        if (!NumbersOn || prev < 0f) return;
        if (ratio <= 0.001f && prev > 0.001f)
            Push(new Ev(EvKind.Health, id, null, 0, false, prev, ratio, max));
        else if (ratio > prev + 0.001f && MonsterHudSettings.NumHeals)
            Push(new Ev(EvKind.Health, id, null, 0, false, prev, ratio, max));
    }

    /// <summary>KillerNotification: the player killed something; the message names it.</summary>
    public static void OnKillMessage(string? message)
    {
        if (!NumbersOn || string.IsNullOrEmpty(message)) return;
        Push(new Ev(EvKind.KillMsg, 0, message, 0, false, 0, 0, 0));
    }

    /// <summary>"/rv plates test": a spread of sample numbers, a burst and gains around the player.</summary>
    public static void SpawnTest() => Push(new Ev(EvKind.Test, 0, null, 0, false, 0, 0, 0));

    public static void OnDeleted(uint id)
    {
        if (!NumbersOn || !MonsterHudSettings.NumKills) return;
        // Only creatures can be kill-burst candidates; items and corpses come and go all the time.
        if (!ClientObjectHooks.TryGetSnapshotItemType(id, out uint type) || (type & 0x10) == 0) return;
        Push(new Ev(EvKind.Deleted, id, null, 0, false, 0, 0, 0));
    }

    private static int _radianceDiag, _gainDiag;

    private static readonly string[] AttackPrefixes = { "Critical hit! ", "Overpower! ", "Sneak Attack! " };

    /// <summary>A chat line: spell damage (dealt / taken) and Radiance gains. Cheap for anything else.</summary>
    public static void OnChat(string line)
    {
        if (line.Length < 14) return;

        // Diagnostics: the first lines mentioning Radiance (every kill line), and whether they parse.
        if (_radianceDiag < 60 && line.Contains("Radiance", StringComparison.Ordinal))
        {
            _radianceDiag++;
            bool shape = line.StartsWith("You gain ", StringComparison.Ordinal) && line.EndsWith(" Radiance.", StringComparison.Ordinal);
            RynthLog.Compat($"Nameplates: radiance line '{line}' (len {line.Length}, matches {shape}, gains {MonsterHudSettings.Gains}/{MonsterHudSettings.GainRadiance})");
        }

        // Aelrynth's kill summary: "Kill: +5,000 experience, +50 Radiance." (parts optional,
        // "Luminance" too). It reports XP even at max level, where TotalExperience no longer rises.
        if (line.StartsWith("Kill: +", StringComparison.Ordinal))
        {
            if (!MonsterHudSettings.Gains) return;
            long now = Stopwatch.GetTimestamp();
            if (line == _lastRadianceLine && now - _lastRadianceTicks < Stopwatch.Frequency / 10) return;
            _lastRadianceLine = line;
            _lastRadianceTicks = now;
            // Parts are separated by ", "; a bare ',' is a thousands separator ("+10,500 experience"),
            // which the old Split(',') cut in two, dropping every XP amount of 1,000 or more.
            foreach (string raw in line.Substring(6).TrimEnd('.').Split(", "))
            {
                string part = raw.Trim();
                if (part.Length < 3 || part[0] != '+') continue;
                int sp = part.IndexOf(' ');
                if (sp < 2 || !TryParseNumber(part.AsSpan(1, sp - 1), out long amount) || amount <= 0) continue;
                string what = part.Substring(sp + 1);
                int amt = (int)Math.Min(amount, int.MaxValue);
                if (what.StartsWith("experience", StringComparison.OrdinalIgnoreCase))
                {
                    if (!MonsterHudSettings.GainXp) continue;
                    _chatXpUntil = now + Stopwatch.Frequency * 2;   // the property poll skips this rise
                    Push(new Ev(EvKind.ChatXp, 0, null, amt, false, 0, 0, 0));
                }
                else if (what.StartsWith("Luminance", StringComparison.OrdinalIgnoreCase))
                {
                    if (!MonsterHudSettings.GainLum) continue;
                    _chatLumUntil = now + Stopwatch.Frequency * 2;
                    Push(new Ev(EvKind.ChatLum, 0, null, amt, false, 0, 0, 0));
                }
                else if (what.StartsWith("Radiance", StringComparison.OrdinalIgnoreCase))
                {
                    if (MonsterHudSettings.GainRadiance) Push(new Ev(EvKind.Radiance, 0, null, amt, false, 0, 0, 0));
                }
            }
            return;
        }

        // "You gain 1,234 Radiance." (Aelrynth; may be batched)
        if (line.StartsWith("You gain ", StringComparison.Ordinal) && line.EndsWith(" Radiance.", StringComparison.Ordinal))
        {
            if (!MonsterHudSettings.Gains || !MonsterHudSettings.GainRadiance) return;
            long now = Stopwatch.GetTimestamp();
            // The same line twice within 100 ms is one line seen by two chat paths.
            if (line == _lastRadianceLine && now - _lastRadianceTicks < Stopwatch.Frequency / 10) return;
            _lastRadianceLine = line;
            _lastRadianceTicks = now;
            if (TryParseNumber(line.AsSpan(9, line.Length - 9 - " Radiance.".Length), out long amount) && amount > 0)
                Push(new Ev(EvKind.Radiance, 0, null, (int)Math.Min(amount, int.MaxValue), false, 0, 0, 0));
            return;
        }

        if (!NumbersOn) return;

        // Damage over time on you: "You receive N points of periodic harm."
        if (line.StartsWith("You receive ", StringComparison.Ordinal) && line.EndsWith(" points of periodic harm.", StringComparison.Ordinal))
        {
            if (MonsterHudSettings.NumTaken &&
                TryParseNumber(line.AsSpan(12, line.Length - 12 - " points of periodic harm.".Length), out long harm) && harm > 0 && harm < 1_000_000)
                Push(new Ev(EvKind.Taken, 0, null, (int)harm, false, 0, 0, 0));
            return;
        }

        // Spells: "... for N points with <spell>."; your damage over time: "You <verb> <target> for N points of periodic <type> damage!"
        int ptsWith = line.IndexOf(" points with ", StringComparison.Ordinal);
        if (ptsWith < 0) ptsWith = line.IndexOf(" points of periodic ", StringComparison.Ordinal);
        if (ptsWith < 0) return;

        bool crit = false;
        int start = 0;
        for (bool again = true; again;)
        {
            again = false;
            foreach (string pre in AttackPrefixes)
                if (string.CompareOrdinal(line, start, pre, 0, pre.Length) == 0)
                {
                    if (pre[0] == 'C') crit = true;
                    start += pre.Length;
                    again = true;
                }
        }

        int forAt = line.LastIndexOf(" for ", ptsWith, StringComparison.Ordinal);
        if (forAt <= start || !TryParseNumber(line.AsSpan(forAt + 5, ptsWith - forAt - 5), out long n) || n <= 0 || n > 1_000_000)
            return;

        if (string.CompareOrdinal(line, start, "You ", 0, 4) == 0)
        {
            // "You <verb> <target> for N points with <spell>."
            int verbEnd = line.IndexOf(' ', start + 4);
            if (verbEnd < 0 || verbEnd >= forAt) return;
            string target = line[(verbEnd + 1)..forAt];
            if (target.Length > 0) Push(new Ev(EvKind.Dealt, 0, target, (int)n, crit, 0, 0, 0));
        }
        else if (MonsterHudSettings.NumTaken && forAt >= 4 && string.CompareOrdinal(line, forAt - 4, " you", 0, 4) == 0)
        {
            // "<source> <verbs> you for N points with <spell>."
            Push(new Ev(EvKind.Taken, 0, null, (int)n, crit, 0, 0, 0));
        }
    }

    /// <summary>Digits with thousands separators ("12,345").</summary>
    private static bool TryParseNumber(ReadOnlySpan<char> s, out long value)
    {
        value = 0;
        bool any = false;
        foreach (char ch in s)
        {
            if (ch == ',' || ch == '.' || ch == ' ') continue;
            if (ch < '0' || ch > '9') return false;
            value = value * 10 + (ch - '0');
            any = true;
            if (value > 1_000_000_000_000L) return false;
        }
        return any;
    }

    /// <summary>XP / Luminance rises since the last poll (4 Hz, AC's thread, from MonsterHud.Update).</summary>
    public static void PollGains(uint playerId)
    {
        if (playerId == 0) return;
        if (playerId != _gainOwner) { _gainOwner = playerId; _xpBase = _lumBase = -1; }
        bool gains = MonsterHudSettings.Gains;
        if (!ClientObjectHooks.TryGetObjectQuadProperty(playerId, 1u, out long xp) || xp <= 0) return;   // TotalExperience
        long nowTicks = Stopwatch.GetTimestamp();
        if (_xpBase >= 0 && xp > _xpBase && gains && MonsterHudSettings.GainXp && nowTicks > _chatXpUntil) AddGain(GainKind.Xp, xp - _xpBase);
        _xpBase = xp;
        // AvailableLuminance: absent means 0 once the player's qualities are readable (the XP read was).
        long lum = ClientObjectHooks.TryGetObjectQuadProperty(playerId, 6u, out long l) ? l : 0;
        if (_lumBase >= 0 && lum > _lumBase && gains && MonsterHudSettings.GainLum && nowTicks > _chatLumUntil) AddGain(GainKind.Lum, lum - _lumBase);
        _lumBase = lum;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Draw (render thread, inside the ImGui frame)
    // ════════════════════════════════════════════════════════════════════

    public static void Draw(ref MonsterHud.Frame f)
    {
        ProcessEvents(ref f);
        if (NumbersOn && MonsterHudSettings.NumHeals) WatchPlayerHeals();
        PruneHits(f.Now);

        float k = MonsterHudSettings.Scale * f.UiScale;
        if (_liveBursts > 0) DrawBursts(ref f, k);
        if (_liveNumbers > 0) DrawNumbers(ref f, k);
        if (Gains.Count > 0) DrawGains(ref f, k);
    }

    private static void ProcessEvents(ref MonsterHud.Frame f)
    {
        Work.Clear();
        lock (Sync)
        {
            if (Pending.Count == 0) return;
            Work.AddRange(Pending);
            Pending.Clear();
        }
        uint player = ClientHelperHooks.GetPlayerId();
        foreach (Ev e in Work)
        {
            switch (e.Kind)
            {
                case EvKind.Dealt:
                {
                    uint id = MonsterHud.FindPlateByName(e.Text!);
                    if (id == 0) id = PlateDebuffs.UniqueNearby(e.Text!);
                    if (id == 0 || !NoteHit(id, f.Now, out Hit h)) break;
                    if (MonsterHudSettings.NumDealt)
                        SpawnNumber(NumKind.Dealt, e.Amount, e.Crit, false, h.Cell, h.X, h.Y, h.Z, id, f.Now);
                    break;
                }
                case EvKind.Taken:
                    SpawnNumber(NumKind.Taken, e.Amount, e.Crit, true, 0, 0, 0, 0, 0, f.Now);
                    break;
                case EvKind.Health:
                    if (e.Id == player) break;
                    if (e.Ratio <= 0.001f)
                    {
                        Kill(e.Id, e.Prev, e.Max, f.Now, requireHit: true);
                    }
                    else
                    {
                        uint max = e.Max;
                        if (max == 0 && ObjectQualityCache.TryGetCreatureVitals(e.Id, out CreatureVitals v)) max = v.MaxHealth;
                        int delta = max > 0 ? (int)MathF.Round((e.Ratio - e.Prev) * max) : 0;
                        if (delta >= Math.Max(8, (int)(max * 0.05f)) && MonsterHud.TryGetHead(e.Id, out uint c, out float x, out float y, out float z, out _))
                            SpawnNumber(NumKind.Heal, delta, false, false, c, x, y, z, e.Id, f.Now);
                    }
                    break;
                case EvKind.KillMsg:
                {
                    uint id = MonsterHud.FindPlateNamedIn(e.Text!);
                    if (id == 0) break;
                    float prev = MonsterHudData.TryGetHealth(id, out HealthObservation ho) ? ho.Ratio : 0f;
                    uint max = ho.Max;
                    Kill(id, prev, max, f.Now, requireHit: false);
                    break;
                }
                case EvKind.Deleted:
                    if (Hits.TryGetValue(e.Id, out Hit? dh))
                    {
                        if (dh.BurstAt == 0 && f.Now - dh.Ticks < (long)(DeleteKillSec * Stopwatch.Frequency) &&
                            MonsterHudSettings.NumKills)
                            SpawnBurst(dh.Cell, dh.X, dh.Y, dh.FeetZ, false, f.Now);
                        Hits.Remove(e.Id);
                    }
                    break;
                // Chat-reported gains show one-for-one with the chat line: merging two kills
                // inside GainMergeSec showed a sum that matched no line in chat (2026-09-30).
                case EvKind.Radiance:
                    AddGain(GainKind.Radiance, e.Amount, merge: false);
                    break;
                case EvKind.ChatXp:
                    AddGain(GainKind.Xp, e.Amount, merge: false);
                    break;
                case EvKind.ChatLum:
                    AddGain(GainKind.Lum, e.Amount, merge: false);
                    break;
                case EvKind.Test:
                {
                    uint c = f.PlayerCell;
                    float x = f.Px, y = f.Py, z = f.Pz + 2f;
                    SpawnNumber(NumKind.Dealt, 18, false, false, c, x + 2f, y + 2f, z, 0, f.Now);
                    SpawnNumber(NumKind.Dealt, 73, false, false, c, x - 2f, y + 1f, z, 0, f.Now);
                    SpawnNumber(NumKind.Dealt, 142, false, false, c, x + 1f, y - 2f, z, 0, f.Now);
                    SpawnNumber(NumKind.Dealt, 891, true, false, c, x - 1f, y - 1f, z, 0, f.Now);
                    SpawnNumber(NumKind.Heal, 64, false, false, c, x + 3f, y, z, 0, f.Now);
                    SpawnNumber(NumKind.Taken, 37, false, true, 0, 0, 0, 0, 0, f.Now);
                    SpawnBurst(c, x, y + 3f, f.Pz, false, f.Now);
                    AddGain(GainKind.Xp, 12345);
                    AddGain(GainKind.Lum, 500);
                    AddGain(GainKind.Radiance, 10);
                    break;
                }
            }
        }
        Work.Clear();
    }

    /// <summary>Records a hit on a monster (for kill attribution) with its head position. False without a position.</summary>
    private static bool NoteHit(uint id, long now, out Hit hit)
    {
        if (!Hits.TryGetValue(id, out Hit? h))
        {
            if (Hits.Count >= 128) Hits.Clear();
            h = new Hit();
            Hits[id] = h;
        }
        hit = h;
        h.Ticks = now;
        if (MonsterHud.TryGetHead(id, out uint cell, out float x, out float y, out float z, out float feet))
        {
            h.Cell = cell; h.X = x; h.Y = y; h.Z = z; h.FeetZ = feet;
        }
        return h.Cell != 0;
    }

    /// <summary>A monster died: the burst, the estimated killing blow, and its debuffs go.</summary>
    private static void Kill(uint id, float prevRatio, uint max, long now, bool requireHit)
    {
        PlateDebuffs.Remove(id);
        Hits.TryGetValue(id, out Hit? h);
        bool recent = h != null && now - h.Ticks < (long)(KillAfterHitSec * Stopwatch.Frequency);
        if (requireHit && !recent) return;
        if (h != null && h.BurstAt != 0 && now - h.BurstAt < 5 * Stopwatch.Frequency) return;   // already shown

        if (!MonsterHud.TryGetHead(id, out uint cell, out float x, out float y, out float z, out float feet))
        {
            if (h == null || h.Cell == 0) return;
            cell = h.Cell; x = h.X; y = h.Y; z = h.Z; feet = h.FeetZ;
        }
        if (h == null)
        {
            if (Hits.Count >= 128) Hits.Clear();
            h = new Hit { Ticks = long.MinValue / 2, Cell = cell, X = x, Y = y, Z = z, FeetZ = feet };
            Hits[id] = h;
        }
        h.BurstAt = now;

        // ACE sends no notification for the killing blow: estimate it from the last health seen.
        if (MonsterHudSettings.NumDealt && recent)
        {
            if (max == 0 && ObjectQualityCache.TryGetCreatureVitals(id, out CreatureVitals v)) max = v.MaxHealth;
            int est = max > 0 ? (int)MathF.Round(Math.Clamp(prevRatio, 0f, 1f) * max) : 0;
            if (est > 0 && !RecentNumberOn(id, now)) SpawnNumber(NumKind.Dealt, est, false, false, cell, x, y, z, id, now);
        }
        if (MonsterHudSettings.NumKills)
            SpawnBurst(cell, x, y, feet, false, now);
    }

    private static bool RecentNumberOn(uint id, long now)
    {
        foreach (Number n in Numbers)
            if (n.Active && n.Target == id && now - n.Born < Stopwatch.Frequency * 3 / 10) return true;
        return false;
    }

    private static void PruneHits(long now)
    {
        if (now < _nextHitPrune) return;
        _nextHitPrune = now + Stopwatch.Frequency * 5;
        HitEvict.Clear();
        foreach (KeyValuePair<uint, Hit> kv in Hits)
            if (now - kv.Value.Ticks > 60 * Stopwatch.Frequency && (kv.Value.BurstAt == 0 || now - kv.Value.BurstAt > 60 * Stopwatch.Frequency))
                HitEvict.Add(kv.Key);
        foreach (uint id in HitEvict) Hits.Remove(id);
    }

    /// <summary>The player's health rising past regen size: a green number over the head.</summary>
    private static void WatchPlayerHeals()
    {
        if (!PlayerVitalsHooks.TryGetSnapshot(out PlayerVitalsSnapshot v) || v.MaxHealth == 0) return;
        long now = Stopwatch.GetTimestamp();
        Restore(NumKind.Heal, v.Health, v.MaxHealth, ref _prevHealth, ref _prevMaxHealth, now);
        if (MonsterHudSettings.NumRestores)
        {
            Restore(NumKind.Stamina, v.Stamina, v.MaxStamina, ref _prevStamina, ref _prevMaxStamina, now);
            Restore(NumKind.Mana, v.Mana, v.MaxMana, ref _prevMana, ref _prevMaxMana, now);
        }
    }

    /// <summary>A rise bigger than a regen tick (spells, potions, kits): a number over the player.</summary>
    private static void Restore(NumKind kind, uint cur, uint max, ref uint prevCur, ref uint prevMax, long now)
    {
        uint prev = prevCur, pm = prevMax;
        prevCur = cur;
        prevMax = max;
        if (max == 0 || prev == 0 || pm != max || cur <= prev) return;
        int delta = (int)(cur - prev);
        if (delta >= Math.Max(10, (int)(max * 0.05f)))
            SpawnNumber(kind, delta, false, true, 0, 0, 0, 0, 0, now);
    }

    // ── Spawning ─────────────────────────────────────────────────────────

    private static void SpawnNumber(NumKind kind, int amount, bool crit, bool onPlayer, uint cell, float x, float y, float z, uint target, long now)
    {
        if (amount <= 0) return;
        Number? n = null;
        foreach (Number c in Numbers)
            if (!c.Active) { n = c; break; }
        if (n == null)
        {
            // Full: reuse the oldest.
            n = Numbers[0];
            foreach (Number c in Numbers) if (c.Born < n.Born) n = c;
            _liveNumbers--;
        }
        n.Active = true;
        n.Kind = kind;
        n.Crit = crit;
        n.Born = now;
        n.OnPlayer = onPlayer;
        n.Cell = cell; n.X = x; n.Y = y; n.Z = z;
        n.Target = target;
        n.Jitter = (NextRandom() * 2f - 1f) * 14f;
        string s = amount.ToString(CultureInfo.InvariantCulture);
        switch (kind)
        {
            case NumKind.Heal:
                n.Text = "+" + s; n.Color = HealGreen; n.Life = HealLife;
                break;
            case NumKind.Stamina:
                n.Text = "+" + s + " stam"; n.Color = StaminaAmber; n.Life = HealLife;
                break;
            case NumKind.Mana:
                n.Text = "+" + s + " mana"; n.Color = ManaBlue; n.Life = HealLife;
                break;
            case NumKind.Taken:
                n.Text = crit ? s + "!" : s; n.Color = crit ? Gold : TakenRed; n.Life = crit ? CritLife : NumberLife;
                break;
            default:
                n.Text = crit ? s + "!" : s; n.Color = crit ? Gold : DamageColor(amount); n.Life = crit ? CritLife : NumberLife;
                break;
        }
        _liveNumbers++;
        _statNumbers++;
    }

    private static void SpawnBurst(uint cell, float x, float y, float z, bool crit, long now)
    {
        Burst? b = null;
        foreach (Burst c in Bursts)
            if (!c.Active) { b = c; break; }
        if (b == null)
        {
            b = Bursts[0];
            foreach (Burst c in Bursts) if (c.Born < b.Born) b = c;
            _liveBursts--;
        }
        b.Active = true;
        b.Crit = crit;
        b.Born = now;
        b.Cell = cell; b.X = x; b.Y = y; b.Z = z;
        _liveBursts++;
        _statBursts++;
    }

    /// <summary>A gain; with <paramref name="merge"/>, the same kind within GainMergeSec adds to the newest one.</summary>
    private static void AddGain(GainKind kind, long amount, bool merge = true)
    {
        if (amount <= 0) return;
        // Diagnostics (2026-09-30: popups read half the chat's "Kill:" XP): the first 60 gains
        // with their source, beside the kill lines logged in OnChat.
        if (_gainDiag < 60)
        {
            _gainDiag++;
            RynthLog.Compat($"Nameplates: gain {kind} +{amount} ({(merge ? "property rise" : "chat line")})");
        }
        long now = Stopwatch.GetTimestamp();
        for (int i = merge ? Gains.Count - 1 : -1; i >= 0; i--)
        {
            Gain g = Gains[i];
            if (g.Kind != kind) continue;
            if (now - g.Born < (long)(GainMergeSec * Stopwatch.Frequency))
            {
                g.Amount += amount;
                g.Merged = now;
                g.Text = GainText(kind, g.Amount);
                return;
            }
            break;
        }
        if (Gains.Count >= MaxGains) Gains.RemoveAt(0);
        Gains.Add(new Gain { Kind = kind, Amount = amount, Born = now, Merged = now, Text = GainText(kind, amount) });
        _statGains++;
    }

    private static string GainText(GainKind kind, long amount)
    {
        string n = amount.ToString("N0", CultureInfo.InvariantCulture);
        return kind switch
        {
            GainKind.Xp => "+" + n + " XP",
            GainKind.Lum => "+" + n + " Lum",
            _ => "+" + n + " Radiance",
        };
    }

    private static float NextRandom()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return (_rng & 0xFFFFFF) / (float)0x1000000;
    }

    private static uint DamageColor(int amount) =>
        amount < 25 ? White : amount < 60 ? PaleYellow : amount < 120 ? Orange : Red;

    // ── Painting ─────────────────────────────────────────────────────────

    private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);

    /// <summary>Where "over the player" is on screen: the head, or the upper middle in first person.</summary>
    private static Vector2 PlayerAnchor(ref MonsterHud.Frame f, bool aboveplate, float k)
    {
        if (PlayerPlate.HeadOnScreen && !PlayerPlate.FirstPerson)
            return new Vector2(PlayerPlate.HeadScreen.X, (aboveplate ? PlayerPlate.PlateTop : PlayerPlate.HeadScreen.Y) - 6f * k);
        return new Vector2(MathF.Round(f.Display.X * 0.5f), MathF.Round(f.Display.Y * (aboveplate ? 0.28f : 0.42f)));
    }

    private static void DrawNumbers(ref MonsterHud.Frame f, float k)
    {
        float baseSize = ImGuiFonts.Get(UiFont.NumBold24).FontSize * MonsterHudSettings.NumSize * MonsterHudSettings.Scale;
        float timeScale = MonsterHudSettings.NumTime;
        Vector2 playerAt = PlayerAnchor(ref f, false, k);
        int live = 0;
        foreach (Number n in Numbers)
        {
            if (!n.Active) continue;
            float age = (f.Now - n.Born) / (float)Stopwatch.Frequency / timeScale;
            if (age >= n.Life) { n.Active = false; continue; }
            live++;

            Vector2 at;
            if (n.OnPlayer) at = playerAt;
            else if (!MonsterHud.Project(ref f, n.Cell, n.X, n.Y, n.Z, out at, keepOnScreen: true)) continue;

            float t = age / n.Life;
            float rise = 70f * k * MonsterHudSettings.NumSize * EaseOut(t);
            float pop = 1f + (n.Crit ? 0.7f : 0.35f) * MathF.Max(0f, 1f - age / (n.Crit ? 0.16f : 0.12f));
            float size = MathF.Round(baseSize * pop * (n.Crit ? 1.35f : 1f) * (n.Kind == NumKind.Taken ? 1.1f : 1f));
            ImFontPtr font = ImGuiFonts.Sharp(size, bold: true);   // shrink a bigger bake, never stretch
            float alpha = MathF.Min(1f, age / 0.06f);
            float fadeStart = 0.55f * n.Life;
            if (age > fadeStart) alpha *= MathF.Max(0f, 1f - (age - fadeStart) / (n.Life - fadeStart));

            float w = MonsterHud.TextWidth(font, size, n.Text);
            var pos = new Vector2(MathF.Round(at.X + n.Jitter * k - w * 0.5f), MathF.Round(at.Y - 10f * k - rise - size));
            MonsterHud.OutlinedText(f.Dl, font, size, pos, alpha, n.Text);
            if (n.Crit) MonsterHud.OutlinedText(f.Dl, font, size, pos + new Vector2(1, 0), alpha * 0.6f, n.Text);
            f.Dl.AddText(font, size, pos, MonsterHud.Mul(n.Color, alpha), n.Text);
        }
        _liveNumbers = live;
    }

    private static void DrawGains(ref MonsterHud.Frame f, float k)
    {
        // 24 px bake at GainSize 1 would be big; 0.75 keeps the old look at the default.
        float baseSize = MathF.Round(ImGuiFonts.Get(UiFont.NumBold24).FontSize * 0.75f * MonsterHudSettings.GainSize * MonsterHudSettings.Scale);
        float life = GainLife * MonsterHudSettings.GainTime;
        Vector2 at = PlayerAnchor(ref f, true, k);
        float lineH = baseSize + 3f * k;
        int slot = 0;
        for (int i = Gains.Count - 1; i >= 0; i--)
        {
            Gain g = Gains[i];
            float age = (f.Now - g.Born) / (float)Stopwatch.Frequency;
            if (age >= life) { Gains.RemoveAt(i); continue; }
            float t = age / life;
            float drift = 34f * k * MonsterHudSettings.GainSize * EaseOut(t);
            float sinceMerge = (f.Now - g.Merged) / (float)Stopwatch.Frequency;
            float pop = 1f + 0.25f * MathF.Max(0f, 1f - sinceMerge / 0.15f);
            float size = MathF.Round(baseSize * pop);
            ImFontPtr font = ImGuiFonts.Sharp(size, bold: true);
            float alpha = MathF.Min(1f, age / 0.08f);
            if (t > 0.6f) alpha *= MathF.Max(0f, 1f - (t - 0.6f) / 0.4f);
            uint col = g.Kind == GainKind.Xp ? XpGold : g.Kind == GainKind.Lum ? LumViolet : RadianceTeal;

            float w = MonsterHud.TextWidth(font, size, g.Text);
            // Newest at the bottom; each older line sits one line higher.
            var pos = new Vector2(MathF.Round(at.X - w * 0.5f), MathF.Round(at.Y - size - drift - slot * lineH));
            MonsterHud.OutlinedText(f.Dl, font, size, pos, alpha, g.Text);
            f.Dl.AddText(font, size, pos, MonsterHud.Mul(col, alpha), g.Text);
            slot++;
        }
    }

    /// <summary>Expanding ground rings plus a short fading pillar of rings where the monster fell.</summary>
    private static void DrawBursts(ref MonsterHud.Frame f, float k)
    {
        int live = 0;
        foreach (Burst b in Bursts)
        {
            if (!b.Active) continue;
            float age = (f.Now - b.Born) / (float)Stopwatch.Frequency;
            if (age >= BurstLife) { b.Active = false; continue; }
            live++;
            float t = age / BurstLife;
            float fade = 1f - t;
            uint main = b.Crit ? BurstGold : BurstCyan;
            float thick = MathF.Max(1f, 3f * k * fade + 1f);

            Ring(ref f, b, 0.5f + 5.5f * EaseOut(t), 0.15f, MonsterHud.Mul(main, fade), thick);
            float t2 = Math.Clamp((age - 0.12f) / (BurstLife - 0.12f), 0f, 1f);
            if (t2 > 0f) Ring(ref f, b, 0.3f + 3.2f * EaseOut(t2), 0.15f, MonsterHud.Mul(White, (1f - t2) * 0.8f), MathF.Max(1f, thick * 0.7f));
            for (int i = 0; i < 4; i++)
            {
                float h = 0.6f + i * 0.7f + 1.2f * t;
                float r = (1.3f - i * 0.22f) * (0.6f + 0.6f * EaseOut(t));
                Ring(ref f, b, r, h, MonsterHud.Mul(main, fade * (0.75f - i * 0.12f)), MathF.Max(1f, thick * 0.6f));
            }
        }
        _liveBursts = live;
    }

    private static void Ring(ref MonsterHud.Frame f, Burst b, float radius, float height, uint col, float thick)
    {
        const int Segs = 28;
        bool havePrev = false;
        Vector2 prev = default;
        for (int i = 0; i <= Segs; i++)
        {
            float ang = i * (MathF.PI * 2f / Segs);
            bool ok = MonsterHud.Project(ref f, b.Cell, b.X + radius * MathF.Cos(ang), b.Y + radius * MathF.Sin(ang), b.Z + height, out Vector2 p);
            if (ok && havePrev) f.Dl.AddLine(prev, p, col, thick);
            havePrev = ok;
            prev = p;
        }
    }

    public static string Describe() =>
        $"Combat text {(MonsterHudSettings.Numbers ? "on" : "off")}, gains {(MonsterHudSettings.Gains ? "on" : "off")}: " +
        $"{_liveNumbers} number(s), {_liveBursts} burst(s), {Gains.Count} gain(s) live; " +
        $"{_statNumbers} numbers, {_statBursts} kill bursts, {_statGains} gains this session.";
}

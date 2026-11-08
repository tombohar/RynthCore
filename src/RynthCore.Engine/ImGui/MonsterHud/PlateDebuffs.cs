// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/PlateDebuffs.cs
//  Which debuffs are on which monster, for the icon row on each nameplate.
//
//  Source 1, the player's own casts (primary). ACE tells the caster
//    "You cast {spell} on {target}{suffix}"         (ChatMessageType.Magic)
//  with suffix ", surpassing X" | ", refreshing X" | ", but it is surpassed by X"
//  (WorldObject_Magic.cs, from AddEnchantmentResult.StackType), and
//    "{target} resists your spell", "You fail to affect {target} with {spell}".
//  The line names the target, not its id, and several monsters share names,
//  so the line is matched to an object id:
//    1. a pending engine cast (CombatActionHooks.CastSpell: RynthAi and every
//       other engine-issued cast) of the same spell on a monster of that name,
//       within PendingCastSec;
//    2. else the selected target, or a target selected within PendingCastSec,
//       with that name (manual casts go at the selection);
//    3. else the only creature of that name within FallbackRangeM;
//    4. else nothing is recorded (ambiguous).
//  The spell name gives id / category / power / duration through the client's
//  spell table (PortalSpellTable). Stacking follows AC: one entry per spell
//  category; a surpassing or refreshing cast replaces it; a surpassed cast
//  leaves the stronger one and at least extends how long the category lasts.
//  Duration: the table's, times 1 + 0.2 x the player's
//  AugmentationIncreasedSpellDuration (PropertyInt 238), not for DoTs (ACE
//  EnchantmentManager.BuildEntry).
//
//  Source 2, appraisal highlights (secondary, generic). The 0xC9 appraisal's
//  CreatureAppraisalProfile keeps enchantment_bitfield: low 9 bits = attribute
//  or vital modified, bits 16..24 = modified upward (green). Red bits that none
//  of the player's own debuffs explained at that moment raise one "debuffed by
//  others" marker, kept for OthersMemorySec or until the next appraisal. It
//  only refreshes on appraisal (the combat target is re-appraised often).
//
//  Entries drop at expiry, when the monster dies or is released (delete), and
//  on logout. Fed on AC's main thread (chat, cast, selection, appraisal,
//  delete hooks); read by the plates on the render thread. A lock keeps it
//  safe if a caller moves. Bounded. Engine-owned statics only.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.ImGuiBackend.Hud;

// DebuffKind and DebuffIcon (what an icon stands for) are in DebuffIcon.cs, shared with your own plate's row.

internal static class PlateDebuffs
{
    private const double PendingCastSec = 8.0;
    private const float FallbackRangeM = 60f;
    private const double OthersMemorySec = 180.0;
    private const int MaxMonsters = 512;
    private const int MaxPending = 16;
    private const uint TypeCreature = 0x10;

    private sealed class Entry
    {
        public uint Category;
        public DebuffKind Kind;
        public uint SpellId;
        public uint Power;
        public long Expiry;
        public float Duration;
    }

    private readonly record struct PendingCast(uint Target, uint SpellId, long Ticks);
    private readonly record struct Selection(uint Id, long Ticks);
    private readonly record struct OthersMark(ushort Mask, long Ticks);

    private static readonly object Sync = new();
    private static readonly Dictionary<uint, List<Entry>> ByMonster = new(64);
    private static readonly Dictionary<uint, OthersMark> Others = new(64);
    private static readonly List<PendingCast> Pending = new(MaxPending);
    private static readonly List<Selection> Selections = new(8);
    private static float _durationMult = 1f;
    private static int _recorded, _ambiguous, _resisted;
    private static string _lastEvent = "none yet";

    /// <summary>Caster duration multiplier from AugmentationIncreasedSpellDuration (MonsterHud refreshes it).</summary>
    public static void SetDurationAugmentation(int augs) =>
        _durationMult = 1f + Math.Clamp(augs, 0, 5) * 0.2f;

    // ── Feeds (AC's main thread) ─────────────────────────────────────────

    /// <summary>
    /// An engine-issued cast (targetId 0 = self: ignored). A repeat of the same
    /// spell on the same target (a retry, or the off-thread enqueue and its
    /// main-thread run) refreshes the one entry instead of adding another.
    /// </summary>
    public static void NoteCast(uint targetId, int spellId)
    {
        if (targetId == 0 || spellId <= 0) return;
        long now = Stopwatch.GetTimestamp();
        lock (Sync)
        {
            for (int i = 0; i < Pending.Count; i++)
                if (Pending[i].Target == targetId && Pending[i].SpellId == (uint)spellId)
                {
                    Pending.RemoveAt(i);
                    break;
                }
            if (Pending.Count >= MaxPending) Pending.RemoveAt(0);
            Pending.Add(new PendingCast(targetId, (uint)spellId, now));
        }
    }

    /// <summary>The client's selection changed.</summary>
    public static void NoteSelection(uint id)
    {
        if (id == 0) return;
        long now = Stopwatch.GetTimestamp();
        lock (Sync)
        {
            if (Selections.Count >= 8) Selections.RemoveAt(0);
            Selections.Add(new Selection(id, now));
        }
    }

    /// <summary>A monster left the client (died, despawned, out of range): its debuffs go with it.</summary>
    public static void Remove(uint id)
    {
        lock (Sync)
        {
            ByMonster.Remove(id);
            Others.Remove(id);
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            ByMonster.Clear();
            Others.Clear();
            Pending.Clear();
            Selections.Clear();
        }
    }

    /// <summary>
    /// The appraisal's enchantment bitfield for a creature (successful roll only).
    /// Keeps the red bits that the player's own tracked debuffs don't explain.
    /// </summary>
    public static void OnAppraisal(uint id, uint bitfield)
    {
        if (id == 0 || (bitfield & 0xFE00FE00u) != 0) return;   // bits outside the 9 + 9 known: not trusted
        ushort red = (ushort)((bitfield & 0x1FF) & ~((bitfield >> 16) & 0x1FF));
        long now = Stopwatch.GetTimestamp();
        lock (Sync)
        {
            ushort explained = 0;
            if (ByMonster.TryGetValue(id, out List<Entry>? list))
                foreach (Entry e in list)
                    if (e.Expiry > now) explained |= ExplainedBits(e.Category);
            ushort unexplained = (ushort)(red & ~explained);
            if (unexplained == 0) { Others.Remove(id); return; }
            if (Others.Count >= MaxMonsters && !Others.ContainsKey(id)) Others.Clear();
            Others[id] = new OthersMark(unexplained, now);
        }
    }

    /// <summary>
    /// A chat line. Cheap unless it is one of the caster's magic lines. Needs
    /// the spell table; before it has loaded, cast lines are ignored.
    /// </summary>
    public static void OnChat(string line)
    {
        if (line.Length < 12) return;
        if (line.StartsWith("You cast ", StringComparison.Ordinal))
        {
            if (!PortalSpellTable.Ready) { PortalSpellTable.EnsureLoadQueued(); return; }
            OnYouCast(line);
            return;
        }
        if (line.EndsWith(" resists your spell", StringComparison.Ordinal))
        {
            string name = line[..^" resists your spell".Length];
            Consume(name, 0);
            lock (Sync) { _resisted++; _lastEvent = $"{name} resisted"; }
            return;
        }
        if (line.StartsWith("You fail to affect ", StringComparison.Ordinal))
        {
            int with = line.LastIndexOf(" with ", StringComparison.Ordinal);
            if (with > 19) Consume(line[19..with], 0);
            return;
        }
        if (line.StartsWith("Your spell fizzled", StringComparison.Ordinal))
        {
            lock (Sync) if (Pending.Count > 0) Pending.RemoveAt(Pending.Count - 1);
        }
    }

    private static readonly string[] SuffixMarks = { ", surpassing ", ", refreshing ", ", but it is surpassed by " };

    private static void OnYouCast(string line)
    {
        // "You cast <spell> on <target>[<suffix>]"
        string body = line[9..].TrimEnd('.', ' ', '\r', '\n');
        int suffixAt = -1, suffixKind = -1;
        for (int k = 0; k < SuffixMarks.Length; k++)
        {
            int i = body.IndexOf(SuffixMarks[k], StringComparison.Ordinal);
            if (i > 0 && (suffixAt < 0 || i < suffixAt)) { suffixAt = i; suffixKind = k; }
        }
        string surpassedBy = suffixKind == 2 ? body[(suffixAt + SuffixMarks[2].Length)..] : "";
        if (suffixAt > 0) body = body[..suffixAt];

        // The spell name may itself contain " on ": try each split, left to right.
        DebuffSpell? spell = null;
        string target = "";
        for (int at = body.IndexOf(" on ", StringComparison.Ordinal); at > 0; at = body.IndexOf(" on ", at + 1, StringComparison.Ordinal))
        {
            if (PortalSpellTable.TryGetByName(body[..at], out DebuffSpell s))
            {
                spell = s;
                target = body[(at + 4)..];
                break;
            }
        }
        if (spell == null || target.Length == 0 || target == "yourself" || target == "you") return;   // a buff, or not ours

        long now = Stopwatch.GetTimestamp();
        uint id = ResolveTarget(spell, target, now, out string how);
        lock (Sync)
        {
            if (id == 0)
            {
                _ambiguous++;
                _lastEvent = $"{spell.Name} on {target}: target ambiguous, not shown";
                return;
            }
            Apply(id, spell, suffixKind, surpassedBy, now);
            _recorded++;
            _lastEvent = $"{spell.Name} on {target} (0x{id:X8}, {how})";
        }
    }

    /// <summary>Pending engine cast, then the selection, then a unique name nearby; 0 if ambiguous.</summary>
    private static uint ResolveTarget(DebuffSpell spell, string name, long now, out string how)
    {
        long window = (long)(PendingCastSec * Stopwatch.Frequency);
        lock (Sync)
        {
            // 1. The newest engine cast of this spell on a monster so named.
            int best = -1;
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                PendingCast p = Pending[i];
                if (now - p.Ticks > window) continue;
                if (p.SpellId != spell.Id && !SameName(p.SpellId, spell.Name)) continue;
                if (!NameIs(p.Target, name)) continue;
                best = i;
                break;
            }
            if (best >= 0)
            {
                uint t = Pending[best].Target;
                Pending.RemoveAt(best);
                how = "engine cast";
                return t;
            }
        }

        // 2. The selection (manual casts go at the selected target).
        uint sel = ClientHelperHooks.GetSelectedItemId();
        if (sel != 0 && NameIs(sel, name)) { how = "selected"; return sel; }
        lock (Sync)
        {
            for (int i = Selections.Count - 1; i >= 0; i--)
            {
                Selection s = Selections[i];
                if (now - s.Ticks > window) break;
                if (NameIs(s.Id, name)) { how = "recent selection"; return s.Id; }
            }
        }

        // 3. The only creature of that name nearby.
        uint only = UniqueNearby(name);
        how = only != 0 ? "unique name" : "ambiguous";
        return only;
    }

    private static bool SameName(uint spellId, string name) =>
        PortalSpellTable.TryGetById(spellId, out DebuffSpell s) && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool NameIs(uint id, string name) =>
        ClientObjectHooks.TryGetSnapshotName(id, out string n) && n.Equals(name, StringComparison.Ordinal);

    /// <summary>The only creature so named within FallbackRangeM of the player; 0 when none or several.</summary>
    internal static uint UniqueNearby(string name)
    {
        if (!PlayerPhysicsHooks.TryGetPlayerPose(out uint pCell, out float px, out float py, out float pz, out _, out _, out _, out _))
            return 0;
        uint found = 0;
        uint[] ids = ClientObjectHooks.LiveObjectIds;
        for (int i = 0; i < ids.Length; i++)
        {
            uint id = ids[i];
            if (!NameIs(id, name)) continue;
            if (!ClientObjectHooks.TryGetSnapshotItemType(id, out uint type) || (type & TypeCreature) == 0) continue;
            if (!ClientObjectHooks.TryGetSnapshotPosition(id, out uint cell, out float x, out float y, out float z)) continue;
            int dx = (int)((cell >> 24) & 0xFF) - (int)((pCell >> 24) & 0xFF);
            int dy = (int)((cell >> 16) & 0xFF) - (int)((pCell >> 16) & 0xFF);
            if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1) continue;
            float ex = x + dx * 192f - px, ey = y + dy * 192f - py, ez = z - pz;
            if (ex * ex + ey * ey + ez * ez > FallbackRangeM * FallbackRangeM) continue;
            if (found != 0) return 0;   // two of them: ambiguous
            found = id;
        }
        return found;
    }

    /// <summary>A resist or failure: drop the newest pending cast on a monster so named.</summary>
    private static void Consume(string name, uint spellId)
    {
        lock (Sync)
        {
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                if ((spellId == 0 || Pending[i].SpellId == spellId) && NameIs(Pending[i].Target, name))
                {
                    Pending.RemoveAt(i);
                    return;
                }
            }
        }
    }

    /// <summary>AC-style stacking, one entry per category. Caller holds Sync.</summary>
    private static void Apply(uint id, DebuffSpell spell, int suffixKind, string surpassedBy, long now)
    {
        float dur = (float)spell.Duration * (spell.IsDot ? 1f : _durationMult);
        if (dur <= 0) return;
        long expiry = now + (long)(dur * Stopwatch.Frequency);

        if (!ByMonster.TryGetValue(id, out List<Entry>? list))
        {
            if (ByMonster.Count >= MaxMonsters) ByMonster.Clear();
            list = new List<Entry>(4);
            ByMonster[id] = list;
        }
        Entry? e = null;
        foreach (Entry x in list)
            if (x.Category == spell.Category) { e = x; break; }

        if (suffixKind == 2)
        {
            // Surpassed: the stronger spell stays; ours sits under it, so the category lasts at least as long as ours.
            if (e != null)
            {
                if (expiry > e.Expiry) { e.Expiry = expiry; e.Duration = dur; }
                return;
            }
            DebuffSpell shown = PortalSpellTable.TryGetByName(surpassedBy, out DebuffSpell y) && y.Category == spell.Category ? y : spell;
            list.Add(new Entry { Category = spell.Category, Kind = KindOf(shown), SpellId = shown.Id, Power = shown.Power, Expiry = expiry, Duration = dur });
            return;
        }

        // Initial, surpassing, refreshing: this spell now heads the category.
        if (e == null)
        {
            e = new Entry { Category = spell.Category };
            list.Add(e);
        }
        e.Kind = KindOf(spell);
        e.SpellId = spell.Id;
        e.Power = spell.Power;
        e.Expiry = expiry;
        e.Duration = dur;
    }

    // ── Read (render thread) ─────────────────────────────────────────────

    /// <summary>
    /// The icons for one monster, merged by kind, soonest expiry first within a
    /// kind; expired entries are dropped here. Returns the icon count (may
    /// exceed dst.Length: the rest are the overflow).
    /// </summary>
    public static int GetIcons(uint id, DebuffIcon[] dst, out bool others)
    {
        others = false;
        long now = Stopwatch.GetTimestamp();
        lock (Sync)
        {
            if (Others.TryGetValue(id, out OthersMark m))
            {
                if (now - m.Ticks < (long)(OthersMemorySec * Stopwatch.Frequency)) others = true;
                else Others.Remove(id);
            }
            if (!ByMonster.TryGetValue(id, out List<Entry>? list)) return 0;
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].Expiry <= now) list.RemoveAt(i);
            if (list.Count == 0) { ByMonster.Remove(id); return 0; }

            int n = 0;
            ulong seen = 0;
            foreach (Entry e in list)
            {
                int k = (int)e.Kind;
                if ((seen & (1UL << k)) != 0)
                {
                    for (int i = 0; i < Math.Min(n, dst.Length); i++)
                    {
                        if (dst[i].Kind != e.Kind) continue;
                        dst[i].Stack++;
                        dst[i].AddSpell(e.SpellId);
                        if (e.Expiry < dst[i].Expiry) { dst[i].Expiry = e.Expiry; dst[i].Duration = e.Duration; }
                        break;
                    }
                    continue;
                }
                seen |= 1UL << k;
                if (n < dst.Length)
                {
                    dst[n] = new DebuffIcon { Kind = e.Kind, Expiry = e.Expiry, Duration = e.Duration, Stack = 1 };
                    dst[n].AddSpell(e.SpellId);
                }
                n++;
            }
            // Stable order on the plate: by kind.
            int shown = Math.Min(n, dst.Length);
            for (int i = 1; i < shown; i++)
            {
                DebuffIcon t = dst[i];
                int j = i - 1;
                while (j >= 0 && dst[j].Kind > t.Kind) { dst[j + 1] = dst[j]; j--; }
                dst[j + 1] = t;
            }
            return n;
        }
    }

    /// <summary>Tracked debuff entries (all monsters) and monsters carrying the "others" marker.</summary>
    public static void Count(out int entries, out int monsters, out int othersMarked)
    {
        long now = Stopwatch.GetTimestamp();
        entries = monsters = 0;
        lock (Sync)
        {
            foreach (List<Entry> list in ByMonster.Values)
            {
                int live = 0;
                foreach (Entry e in list) if (e.Expiry > now) live++;
                entries += live;
                if (live > 0) monsters++;
            }
            othersMarked = Others.Count;
        }
    }

    public static string Describe()
    {
        Count(out int entries, out int monsters, out int others);
        lock (Sync)
            return $"Debuffs: {entries} tracked on {monsters} monster(s), {others} with others' marker; " +
                   $"{_recorded} recorded, {_resisted} resisted, {_ambiguous} ambiguous this session; last: {_lastEvent}. " +
                   $"Spell table: {PortalSpellTable.Status}.";
    }

    // ── Categories ───────────────────────────────────────────────────────

    /// <summary>AC spell category (ACE SpellCategory) to the icon it shows.</summary>
    internal static DebuffKind KindOf(DebuffSpell s)
    {
        switch (s.Category)
        {
            case 110: return DebuffKind.VulnFire;
            case 106: return DebuffKind.VulnCold;
            case 108: return DebuffKind.VulnLightning;
            case 102: return DebuffKind.VulnAcid;
            case 114: return DebuffKind.VulnSlash;
            case 112: return DebuffKind.VulnPierce;
            case 104: return DebuffKind.VulnBludgeon;
            case 116: return DebuffKind.Imperil;                     // ArmorLowering (Imperil)
            case 42: return DebuffKind.MagicYield;                   // MagicDefenseLowering (Magic Yield)
            case 38: case 40: return DebuffKind.Defense;             // Melee / Missile defense (Vulnerability, Defenselessness)
            case 2: case 4: case 6: case 8: case 10: case 12: return DebuffKind.Attributes;
            case 80: case 82: case 84: case 94: case 96: case 98: return DebuffKind.Vitals;   // max vitals, regen (Fester...)
            case 78: return DebuffKind.Movement;                     // RunLowering (Leaden Feet)
        }
        if (s.IsDot) return DebuffKind.Dot;
        uint c = s.Category;
        bool skill = (c >= 18 && c <= 76 && (c & 1) == 0) || c == 206 || c == 217 || c == 219 || c == 220 || c == 594 ||
                     c == 644 || c == 664 || c == 667 || c == 670 || c == 673 || c == 676 || c == 697;
        if (skill) return DebuffKind.Skills;
        if (c == 636 || c == 637 || c == 642 || c == 643 || (c >= 410 && c <= 412) || c == 530 || c == 536 || c == 537 ||
            c == 618 || c == 619 || c == 631 || (c >= 684 && c <= 687))
            return DebuffKind.Dot;
        return DebuffKind.Other;
    }

    /// <summary>Appraisal bits (attribute / vital highlight) a category of the player's own would turn red.</summary>
    private static ushort ExplainedBits(uint category) => category switch
    {
        2 => 0x001,                       // Strength
        4 => 0x002 | 0x040 | 0x080,       // Endurance (and the max health / stamina it feeds)
        6 => 0x004,                       // Quickness
        8 => 0x008,                       // Coordination
        10 => 0x010,                      // Focus
        12 => 0x020 | 0x100,              // Self (and max mana)
        80 => 0x040,
        82 => 0x080,
        84 => 0x100,
        _ => 0,
    };
}

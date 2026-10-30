// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/SelfDebuffs.cs
//  "Debuffs on you" (2026-10-27): which harmful spells on the player get an
//  icon in the row under your plate's mana bar. A pure function over the
//  engine's player enchantment snapshot (EnchantmentHooks.CopyPlayerStatMods:
//  spell id, expiry in server seconds, spell category); PlayerPlate reads the
//  snapshot four times a second and hands it in with the server time and a
//  lookup (PortalSpellTable.TryGetById, which only holds non-beneficial
//  enchantments, so a hit means harmful; PlateDebuffs.KindOf gives the icon).
//
//  Rules, the monster row's where they apply:
//    - only harmful spells; cooldowns (ids 0x8000+) and buffs never show;
//    - an entry already past its expiry is dropped, and so is one that never
//      ends (vitae, spells from items: a state, not a fight debuff; the Buffs
//      tab still lists them);
//    - one entry per spell category: AC runs the strongest of a category and
//      the rest wait under it, so the category lasts until its last entry ends;
//    - one icon per kind (DebuffKind); a kind merging several categories shows
//      their count and the soonest end, like the monster row;
//    - icons in kind order (the monster row's), as many as fit in dst; the
//      count returned includes the rest (the "+N").
//  No engine dependencies beyond DebuffIcon.cs (the offline tests compile it).
// ============================================================================

using System;

namespace RynthCore.Engine.ImGuiBackend.Hud;

/// <summary>A harmful spell's icon kind and full length (s); false for a spell that isn't harmful.</summary>
internal delegate bool HarmfulSpellLookup(uint spellId, out DebuffKind kind, out float duration);

internal static class SelfDebuffs
{
    /// <summary>Expiries at or past this (server seconds) never end; the snapshot writes double.MaxValue for them.</summary>
    internal const double NoEnd = 1e12;
    /// <summary>Snapshot ids from here up are cooldowns, not spells.</summary>
    internal const uint FirstCooldownId = 0x8000;
    /// <summary>Snapshot entries looked at (the player rarely carries more than ~100).</summary>
    internal const int MaxEntries = 256;
    private const int KindCount = (int)DebuffKind.Other + 1;
    private const double MaxRemainingSec = 1e6;   // keeps the tick conversion far from overflow

    /// <summary>
    /// Fills <paramref name="dst"/> with the icons for the first <paramref name="n"/> snapshot entries
    /// (<paramref name="ids"/>, <paramref name="expiry"/> in server seconds, <paramref name="categories"/>)
    /// at server time <paramref name="serverNow"/>; icon expiries come out in Stopwatch ticks from
    /// <paramref name="nowTicks"/>. Returns the icon count (may exceed dst.Length: the overflow).
    /// 0 when the server time isn't known yet.
    /// </summary>
    internal static int Select(uint[] ids, double[] expiry, uint[] categories, int n, double serverNow,
        long nowTicks, long ticksPerSecond, HarmfulSpellLookup harmful, DebuffIcon[] dst)
    {
        if (serverNow <= 0 || n <= 0) return 0;
        n = Math.Min(Math.Min(n, MaxEntries), Math.Min(ids.Length, Math.Min(expiry.Length, categories.Length)));

        // 1. Live harmful entries, one per spell category (the one that ends last).
        Span<uint> catKey = stackalloc uint[MaxEntries];
        Span<double> catEnd = stackalloc double[MaxEntries];
        Span<float> catDur = stackalloc float[MaxEntries];
        Span<byte> catKind = stackalloc byte[MaxEntries];
        int cats = 0;
        for (int i = 0; i < n; i++)
        {
            uint id = ids[i];
            if (id == 0 || id >= FirstCooldownId) continue;
            double end = expiry[i];
            if (double.IsNaN(end) || end >= NoEnd || end <= serverNow) continue;
            if (!harmful(id, out DebuffKind kind, out float dur)) continue;
            // No category recorded: the spell stands alone (ids are below 0x8000, so no clash).
            uint key = categories[i] != 0 ? categories[i] : 0x80000000u | id;
            int c = 0;
            while (c < cats && catKey[c] != key) c++;
            if (c == cats)
            {
                catKey[c] = key;
                cats++;
            }
            else if (end <= catEnd[c])
            {
                continue;
            }
            catEnd[c] = end;
            catDur[c] = dur;
            catKind[c] = (byte)Math.Min((int)kind, KindCount - 1);
        }
        if (cats == 0) return 0;

        // 2. One icon per kind: the categories it merges and the soonest end among them.
        Span<long> kindEnd = stackalloc long[KindCount];
        Span<float> kindDur = stackalloc float[KindCount];
        Span<int> kindStack = stackalloc int[KindCount];
        kindEnd.Clear();
        kindDur.Clear();
        kindStack.Clear();
        for (int c = 0; c < cats; c++)
        {
            int k = catKind[c];
            double remaining = Math.Min(catEnd[c] - serverNow, MaxRemainingSec);
            long end = nowTicks + (long)(remaining * ticksPerSecond);
            if (kindStack[k] == 0 || end < kindEnd[k])
            {
                kindEnd[k] = end;
                kindDur[k] = catDur[c];
            }
            kindStack[k]++;
        }

        // 3. In kind order, as many as fit; the count includes the rest.
        int count = 0;
        for (int k = 0; k < KindCount; k++)
        {
            if (kindStack[k] == 0) continue;
            if (count < dst.Length)
                dst[count] = new DebuffIcon { Kind = (DebuffKind)k, Expiry = kindEnd[k], Duration = kindDur[k], Stack = kindStack[k] };
            count++;
        }
        return count;
    }
}

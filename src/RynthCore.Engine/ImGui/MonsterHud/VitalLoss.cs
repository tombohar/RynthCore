// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/VitalLoss.cs
//  "Grey out what debuffs take" (2026-11-02, owner: "instead of re-scaling the
//  bar to 100% with the debuff, show a greyed out area of what is lost"): what
//  your health, stamina and mana maximums would be without the harmful spells
//  on you. Your plate's bar then spans that maximum, and the part the debuffs
//  take is painted grey at its right end.
//
//  A pure function over the engine's player enchantment snapshot
//  (EnchantmentHooks.CopyPlayerStatMods: StatMod type, key, value, spell
//  category), which PlayerPlate reads four times a second. Only the shown
//  (debuffed) maximum comes from AC; the rest is worked back from it, so a
//  rounding step or a rule this misses can only move the grey edge a point or
//  two, never the fill.
//
//  AC's rules, as far as they matter here:
//    - a vital's maximum is (its attribute part + ranks + start) x the vital's
//      multipliers + its additive mods; health's attribute part is Endurance/2,
//      stamina's Endurance, mana's Self;
//    - only the strongest enchantment of a spell category counts (the others
//      wait under it); category 0 entries each count;
//    - harmful = an additive mod below 0 or a multiplier below 1 (attribute
//      debuffs are additive; a vital's can be either);
//    - vitae is left out: it is the death penalty, not a debuff, and the
//      snapshot doesn't carry it; cooldowns (ids 0x8000+) carry no StatMod.
//  No engine dependencies (the offline tests compile it).
// ============================================================================

using System;

namespace RynthCore.Engine.ImGuiBackend.Hud;

/// <summary>One vital's enchantment totals: buffs and debuffs apart.</summary>
internal struct VitalMods
{
    public float BuffMult, DebuffMult, BuffAdd, DebuffAdd;
    /// <summary>What attribute debuffs take off the vital's attribute part (health: half the Endurance lost).</summary>
    public float AttrLoss;

    public static VitalMods None => new() { BuffMult = 1f, DebuffMult = 1f };

    /// <summary>A harmful spell lowers this vital's maximum.</summary>
    public readonly bool AnyDebuff => DebuffMult < 0.9999f || DebuffAdd < 0f || AttrLoss > 0f;
}

internal static class VitalLoss
{
    // StatMod type bits (PlayerProgressHooks.Mod*).
    internal const uint ModAttribute = 0x1, ModSecondAttribute = 0x2, ModMultiplicative = 0x4000, ModAdditive = 0x8000, ModVitae = 0x800000;
    // Attribute keys; vital keys come in pairs (max, current): health 1-2, stamina 3-4, mana 5-6.
    internal const uint Endurance = 2, Self = 6;
    internal const int Health = 0, Stamina = 1, Mana = 2;
    internal const uint FirstCooldownId = 0x8000;
    private const int MaxEntries = 256;
    /// <summary>A worked-back maximum never goes past this many times the shown one (a bad multiplier).</summary>
    private const float MaxGrowth = 10f;

    /// <summary>
    /// The totals for health, stamina and mana (<paramref name="dst"/>[Health..Mana]) from the first
    /// <paramref name="n"/> snapshot entries. Entries of a spell category past its strongest are skipped.
    /// </summary>
    internal static void Collect(uint[] ids, uint[] types, uint[] keys, float[] vals, uint[] categories, int n, Span<VitalMods> dst)
    {
        for (int v = 0; v < dst.Length; v++) dst[v] = VitalMods.None;
        if (dst.Length < 3 || n <= 0) return;
        n = Math.Min(Math.Min(n, MaxEntries),
            Math.Min(Math.Min(ids.Length, types.Length), Math.Min(keys.Length, Math.Min(vals.Length, categories.Length))));

        for (int i = 0; i < n; i++)
        {
            uint type = types[i];
            if (ids[i] == 0 || ids[i] >= FirstCooldownId || (type & ModVitae) != 0) continue;
            if ((type & (ModAttribute | ModSecondAttribute)) == 0) continue;
            bool mult = (type & ModMultiplicative) != 0;
            if (!mult && (type & ModAdditive) == 0) continue;
            float val = vals[i];
            if (float.IsNaN(val) || float.IsInfinity(val)) continue;
            if (!Strongest(i, types, keys, vals, categories, n)) continue;

            if ((type & ModSecondAttribute) != 0)
            {
                int v = keys[i] switch { 1 or 2 => Health, 3 or 4 => Stamina, 5 or 6 => Mana, _ => -1 };
                if (v < 0) continue;
                ref VitalMods m = ref dst[v];
                if (mult)
                {
                    if (val <= 0f) continue;   // never a real multiplier
                    if (val < 1f) m.DebuffMult *= val; else m.BuffMult *= val;
                }
                else if (val < 0f) m.DebuffAdd += val;
                else m.BuffAdd += val;
            }
            else if (!mult && val < 0f)
            {
                // An attribute debuff: Endurance feeds health (half) and stamina, Self feeds mana.
                float lost = -val;
                if (keys[i] == Endurance)
                {
                    dst[Health].AttrLoss += lost * 0.5f;
                    dst[Stamina].AttrLoss += lost;
                }
                else if (keys[i] == Self)
                    dst[Mana].AttrLoss += lost;
            }
        }
    }

    /// <summary>
    /// Entry <paramref name="i"/> is its spell category's strongest on the same stat (ties: the first).
    /// Category 0 entries always count.
    /// </summary>
    private static bool Strongest(int i, uint[] types, uint[] keys, float[] vals, uint[] categories, int n)
    {
        uint cat = categories[i];
        if (cat == 0) return true;
        float mine = Strength(types[i], vals[i]);
        for (int j = 0; j < n; j++)
        {
            if (j == i || categories[j] != cat || keys[j] != keys[i]) continue;
            if ((types[j] & (ModAttribute | ModSecondAttribute | ModMultiplicative | ModAdditive)) !=
                (types[i] & (ModAttribute | ModSecondAttribute | ModMultiplicative | ModAdditive))) continue;
            float other = Strength(types[j], vals[j]);
            if (other > mine || (other == mine && j < i)) return false;
        }
        return true;
    }

    private static float Strength(uint type, float val) =>
        float.IsFinite(val) ? ((type & ModMultiplicative) != 0 ? MathF.Abs(val - 1f) : MathF.Abs(val)) : 0f;

    /// <summary>
    /// The maximum without the debuffs, worked back from the <paramref name="shown"/> (debuffed) one:
    /// shown = part x buffMult x debuffMult + buffAdd + debuffAdd, where part already lost AttrLoss.
    /// Never below <paramref name="shown"/>; equal to it with no debuff.
    /// </summary>
    internal static uint FullMax(uint shown, in VitalMods m)
    {
        if (shown == 0 || !m.AnyDebuff) return shown;
        double scaled = shown - (double)m.BuffAdd - m.DebuffAdd;   // part x buffMult x debuffMult
        if (scaled < 0) scaled = 0;
        double debuffMult = Math.Max(m.DebuffMult, 0.01f);
        double full = scaled / debuffMult + m.AttrLoss * (double)m.BuffMult + m.BuffAdd;
        full = Math.Min(full, shown * (double)MaxGrowth);
        return full <= shown ? shown : (uint)Math.Round(full);
    }
}

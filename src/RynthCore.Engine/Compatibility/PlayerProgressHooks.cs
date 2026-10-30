// ============================================================================
//  RynthCore.Engine - Compatibility/PlayerProgressHooks.cs
//
//  The Skills panel's data (docs/IMGUI_SKILLS.md): the player's skills,
//  attributes and vitals with the numbers a raise needs (ranks, XP spent,
//  innate level, buffed and base values), level, XP, skill credits, and the
//  buffs on each stat (the enchantment snapshot's StatMods).
//
//  Threads:
//    Prefetch   AC's main thread (MainThreadSnapshots.Tick, player live). All the
//               AC reads are in ClientObjectHooks.ReadPlayerProgressLive.
//    Want / Current / RefreshSoon   any thread (the panel: AC's render thread).
//
//  Demand-driven: nothing is read unless a reader called Want in the last 2 s,
//  then at most every 500 ms (WantSlow, the plugin API's GetTrainingInfo: in the
//  last 5 s, then at most every 2 s). Reads go into one preallocated work buffer; a new
//  snapshot object is published (and allocated) only when something changed,
//  so an idle open panel costs a compare every half second.
// ============================================================================

using System;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// One read of the player's progress. Published copies are never written again
/// (readers may keep them); the work buffer is private to the prefetch.
/// Index conventions: skills by STypeSkill (1..MaxSkill), attributes by
/// STypeAttribute (1..6), vitals 1 = health, 2 = stamina, 3 = mana.
/// </summary>
internal sealed class PlayerProgress
{
    public const int MaxSkill = 54;
    public const int MaxBuffs = 256;

    public int Version;
    public uint PlayerId;

    // Skills: the qualities' skill table (class 0 when the skill isn't in it) and InqSkill.
    public bool SkillTableRead;
    public readonly bool[] SkillInTable = new bool[MaxSkill + 1];
    public readonly uint[] SkillClass = new uint[MaxSkill + 1];      // 1 untrained, 2 trained, 3 specialized
    public readonly uint[] SkillXpSpent = new uint[MaxSkill + 1];
    public readonly uint[] SkillRanks = new uint[MaxSkill + 1];
    public readonly uint[] SkillInit = new uint[MaxSkill + 1];
    public readonly bool[] SkillLevelKnown = new bool[MaxSkill + 1];
    public readonly int[] SkillBuffed = new int[MaxSkill + 1];
    public readonly int[] SkillBase = new int[MaxSkill + 1];

    // Attributes 1..6 and vitals 1..3 (health, stamina, mana): the AttributeCache and Inq*.
    public bool AttributeCacheRead;
    public readonly uint[] AttrRanks = new uint[7];
    public readonly uint[] AttrInit = new uint[7];
    public readonly uint[] AttrXpSpent = new uint[7];
    public readonly uint[] AttrBuffed = new uint[7];
    public readonly uint[] AttrBase = new uint[7];
    public readonly uint[] VitalRanks = new uint[4];
    public readonly uint[] VitalInit = new uint[4];
    public readonly uint[] VitalXpSpent = new uint[4];
    public readonly uint[] VitalBuffed = new uint[4];
    public readonly uint[] VitalBase = new uint[4];

    public int Level;
    public int SkillCredits;
    public long TotalXp;
    public long UnassignedXp;
    public long Luminance;
    public long MaxLuminance;
    public float Vitae = 1f;

    // Buffs that change a skill, attribute or vital (StatMod type has 0x1, 0x2 or 0x10).
    public int BuffCount;
    public readonly uint[] BuffSpell = new uint[MaxBuffs];
    public readonly double[] BuffExpiry = new double[MaxBuffs];       // server time; double.MaxValue = no end
    public readonly uint[] BuffType = new uint[MaxBuffs];
    public readonly uint[] BuffKey = new uint[MaxBuffs];
    public readonly float[] BuffVal = new float[MaxBuffs];
    public readonly uint[] BuffCategory = new uint[MaxBuffs];

    public void Clear()
    {
        PlayerId = 0;
        SkillTableRead = false;
        Array.Clear(SkillInTable);
        Array.Clear(SkillClass);
        Array.Clear(SkillXpSpent);
        Array.Clear(SkillRanks);
        Array.Clear(SkillInit);
        Array.Clear(SkillLevelKnown);
        Array.Clear(SkillBuffed);
        Array.Clear(SkillBase);
        AttributeCacheRead = false;
        Array.Clear(AttrRanks);
        Array.Clear(AttrInit);
        Array.Clear(AttrXpSpent);
        Array.Clear(AttrBuffed);
        Array.Clear(AttrBase);
        Array.Clear(VitalRanks);
        Array.Clear(VitalInit);
        Array.Clear(VitalXpSpent);
        Array.Clear(VitalBuffed);
        Array.Clear(VitalBase);
        Level = SkillCredits = 0;
        TotalXp = UnassignedXp = Luminance = MaxLuminance = 0;
        Vitae = 1f;
        BuffCount = 0;
    }

    public void CopyFrom(PlayerProgress o)
    {
        PlayerId = o.PlayerId;
        SkillTableRead = o.SkillTableRead;
        o.SkillInTable.CopyTo(SkillInTable, 0);
        o.SkillClass.CopyTo(SkillClass, 0);
        o.SkillXpSpent.CopyTo(SkillXpSpent, 0);
        o.SkillRanks.CopyTo(SkillRanks, 0);
        o.SkillInit.CopyTo(SkillInit, 0);
        o.SkillLevelKnown.CopyTo(SkillLevelKnown, 0);
        o.SkillBuffed.CopyTo(SkillBuffed, 0);
        o.SkillBase.CopyTo(SkillBase, 0);
        AttributeCacheRead = o.AttributeCacheRead;
        o.AttrRanks.CopyTo(AttrRanks, 0);
        o.AttrInit.CopyTo(AttrInit, 0);
        o.AttrXpSpent.CopyTo(AttrXpSpent, 0);
        o.AttrBuffed.CopyTo(AttrBuffed, 0);
        o.AttrBase.CopyTo(AttrBase, 0);
        o.VitalRanks.CopyTo(VitalRanks, 0);
        o.VitalInit.CopyTo(VitalInit, 0);
        o.VitalXpSpent.CopyTo(VitalXpSpent, 0);
        o.VitalBuffed.CopyTo(VitalBuffed, 0);
        o.VitalBase.CopyTo(VitalBase, 0);
        Level = o.Level;
        SkillCredits = o.SkillCredits;
        TotalXp = o.TotalXp;
        UnassignedXp = o.UnassignedXp;
        Luminance = o.Luminance;
        MaxLuminance = o.MaxLuminance;
        Vitae = o.Vitae;
        BuffCount = o.BuffCount;
        Array.Copy(o.BuffSpell, BuffSpell, o.BuffCount);
        Array.Copy(o.BuffExpiry, BuffExpiry, o.BuffCount);
        Array.Copy(o.BuffType, BuffType, o.BuffCount);
        Array.Copy(o.BuffKey, BuffKey, o.BuffCount);
        Array.Copy(o.BuffVal, BuffVal, o.BuffCount);
        Array.Copy(o.BuffCategory, BuffCategory, o.BuffCount);
    }

    /// <summary>True when every value equals <paramref name="o"/>'s (Version aside). No allocation.</summary>
    public bool SameAs(PlayerProgress o)
    {
        if (PlayerId != o.PlayerId || SkillTableRead != o.SkillTableRead || AttributeCacheRead != o.AttributeCacheRead
            || Level != o.Level || SkillCredits != o.SkillCredits || TotalXp != o.TotalXp || UnassignedXp != o.UnassignedXp
            || Luminance != o.Luminance || MaxLuminance != o.MaxLuminance || Vitae != o.Vitae || BuffCount != o.BuffCount)
            return false;
        return Eq(SkillInTable, o.SkillInTable) && Eq(SkillClass, o.SkillClass) && Eq(SkillXpSpent, o.SkillXpSpent)
            && Eq(SkillRanks, o.SkillRanks) && Eq(SkillInit, o.SkillInit) && Eq(SkillLevelKnown, o.SkillLevelKnown)
            && Eq(SkillBuffed, o.SkillBuffed) && Eq(SkillBase, o.SkillBase)
            && Eq(AttrRanks, o.AttrRanks) && Eq(AttrInit, o.AttrInit) && Eq(AttrXpSpent, o.AttrXpSpent)
            && Eq(AttrBuffed, o.AttrBuffed) && Eq(AttrBase, o.AttrBase)
            && Eq(VitalRanks, o.VitalRanks) && Eq(VitalInit, o.VitalInit) && Eq(VitalXpSpent, o.VitalXpSpent)
            && Eq(VitalBuffed, o.VitalBuffed) && Eq(VitalBase, o.VitalBase)
            && EqN(BuffSpell, o.BuffSpell, BuffCount) && EqN(BuffExpiry, o.BuffExpiry, BuffCount)
            && EqN(BuffType, o.BuffType, BuffCount) && EqN(BuffKey, o.BuffKey, BuffCount)
            && EqN(BuffVal, o.BuffVal, BuffCount) && EqN(BuffCategory, o.BuffCategory, BuffCount);
    }

    private static bool Eq<T>(T[] a, T[] b) where T : IEquatable<T> => a.AsSpan().SequenceEqual(b);

    private static bool EqN<T>(T[] a, T[] b, int n) where T : IEquatable<T> => a.AsSpan(0, n).SequenceEqual(b.AsSpan(0, n));
}

internal static class PlayerProgressHooks
{
    private const int RefreshMs = 500;
    private const int WantWindowMs = 2000;
    private const int SlowRefreshMs = 2000;
    private const int SlowWantWindowMs = 5000;

    // StatMod type flags (ACE EnchantmentTypeFlags) that mark a buff on a stat.
    internal const uint ModAttribute = 0x1, ModSecondAttribute = 0x2, ModSkill = 0x10;
    internal const uint ModMultiplicative = 0x4000, ModAdditive = 0x8000, ModVitae = 0x800000;
    private const uint ModAnyStat = ModAttribute | ModSecondAttribute | ModSkill;

    private static long _wantedUntilMs;
    private static long _slowWantedUntilMs;
    private static long _nextReadMs;
    private static long _lastReadMs;
    private static int _version;
    private static PlayerProgress? _published;

    // Main thread only: the work buffer and the enchantment copy's scratch.
    private static readonly PlayerProgress Work = new();
    private const int EnchScratch = 1024;
    private static readonly uint[] ScratchSpell = new uint[EnchScratch];
    private static readonly double[] ScratchExpiry = new double[EnchScratch];
    private static readonly uint[] ScratchType = new uint[EnchScratch];
    private static readonly uint[] ScratchKey = new uint[EnchScratch];
    private static readonly float[] ScratchVal = new float[EnchScratch];
    private static readonly uint[] ScratchCategory = new uint[EnchScratch];

    /// <summary>A reader wants fresh data for the next 2 s. Any thread; call it each frame the panel draws.</summary>
    public static void Want() =>
        Volatile.Write(ref _wantedUntilMs, Environment.TickCount64 + WantWindowMs);

    /// <summary>
    /// A background reader (the plugin API, TrainingApi) wants data for the next 5 s, read at
    /// most every 2 s, so a plugin polling every few seconds doesn't keep the 2 Hz panel rate up.
    /// Any thread.
    /// </summary>
    public static void WantSlow() =>
        Volatile.Write(ref _slowWantedUntilMs, Environment.TickCount64 + SlowWantWindowMs);

    /// <summary>Read again at the next main-thread tick (after a raise). Any thread.</summary>
    public static void RefreshSoon() => Volatile.Write(ref _nextReadMs, 0);

    /// <summary>
    /// Environment.TickCount64 of the last successful read, changed or not (0 = never), so a
    /// reader can tell how old <see cref="Current"/>'s numbers are. Any thread.
    /// </summary>
    public static long LastReadMs => Volatile.Read(ref _lastReadMs);

    /// <summary>The last published read, or null. Never written again once published. Any thread.</summary>
    public static PlayerProgress? Current => Volatile.Read(ref _published);

    /// <summary>
    /// AC's main thread (MainThreadSnapshots.Tick, only while the player is live).
    /// Reads when wanted and due; publishes only a changed result.
    /// </summary>
    internal static void Prefetch()
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;
        long now = Environment.TickCount64;
        bool panel = now <= Volatile.Read(ref _wantedUntilMs);
        if ((!panel && now > Volatile.Read(ref _slowWantedUntilMs)) || now < Volatile.Read(ref _nextReadMs))
            return;
        Volatile.Write(ref _nextReadMs, now + (panel ? RefreshMs : SlowRefreshMs));

        if (!ClientObjectHooks.ReadPlayerProgressLive(Work))
            return;
        CopyStatBuffs(Work);

        PlayerProgress? current = Volatile.Read(ref _published);
        if (current == null || !current.SameAs(Work))
        {
            var snapshot = new PlayerProgress();
            snapshot.CopyFrom(Work);
            snapshot.Version = ++_version;
            Volatile.Write(ref _published, snapshot);
        }
        // After publishing: a reader that sees this time sees this read's numbers.
        Volatile.Write(ref _lastReadMs, Environment.TickCount64);
    }

    /// <summary>The player's enchantments that change a skill, attribute or vital (not vitae).</summary>
    private static void CopyStatBuffs(PlayerProgress dst)
    {
        dst.BuffCount = 0;
        int n = EnchantmentHooks.CopyPlayerStatMods(ScratchSpell, ScratchExpiry, ScratchType, ScratchKey, ScratchVal, ScratchCategory);
        for (int i = 0; i < n && dst.BuffCount < PlayerProgress.MaxBuffs; i++)
        {
            uint type = ScratchType[i];
            if ((type & ModAnyStat) == 0 || (type & ModVitae) != 0)
                continue;
            int k = dst.BuffCount++;
            dst.BuffSpell[k] = ScratchSpell[i];
            dst.BuffExpiry[k] = ScratchExpiry[i];
            dst.BuffType[k] = type;
            dst.BuffKey[k] = ScratchKey[i];
            dst.BuffVal[k] = ScratchVal[i];
            dst.BuffCategory[k] = ScratchCategory[i];
        }
    }
}

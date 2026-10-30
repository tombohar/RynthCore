// ============================================================================
//  RynthCore.Engine - Compatibility/TrainingCosts.cs
//
//  The XP arithmetic behind raising an attribute, a vital or a skill, and the
//  checks a raise from the plugin API passes before anything is sent. Pure code
//  (no AC memory, no engine state), so tools/TrainingCostTests compiles it in and
//  runs it against the XpTable of a real client_portal.dat.
//
//  XpTable 0x0E000018 (ACE.DatLoader XpTable; the server raises against the same
//  table): file id u32; counts i32 for attributes, vitals, trained skills,
//  specialized skills, then the level count u32; then count+1 u32 each for
//  attributes, vitals, trained and specialized skills, count+1 u64 levels and
//  count+1 u32 skill credits per level. Entry n is the total XP spent at rank n.
//
//  Cost of n raises: table[ranks + n] - xpSpent (ACE Player.SpendAttributeXp /
//  SpendSkillXp take an XP amount and raise as many ranks as it covers; the retail
//  character window sends exactly this for "+1" and "+10": gmAttributeUI /
//  gmSkillUI GetCostToRaise / GetCostToRaise10 -> CM_Train::Event_Train*).
// ============================================================================

using System;
using System.Buffers.Binary;

namespace RynthCore.Engine.Compatibility;

/// <summary>The four raise tables of the portal XpTable (index = rank, value = total XP at that rank).</summary>
internal sealed class XpTableData
{
    public uint[] AttributeXp = Array.Empty<uint>();
    public uint[] VitalXp = Array.Empty<uint>();
    public uint[] TrainedXp = Array.Empty<uint>();
    public uint[] SpecializedXp = Array.Empty<uint>();
    public ulong[] LevelXp = Array.Empty<ulong>();
}

/// <summary>The plugin API's raise and train results (RynthCoreAPI.RaiseFn / TrainSkillFn, v79).</summary>
internal enum RaiseStatus
{
    Sent = 1,
    /// <summary>The client's raise senders aren't bound on this build.</summary>
    Unavailable = 0,
    NotInWorld = -1,
    BadArguments = -2,
    /// <summary>No player snapshot or no XP table yet (both are being fetched; try again in a second).</summary>
    NotReady = -3,
    /// <summary>The numbers are older than the freshness limit; a new read was asked for. Try again in a second.</summary>
    Stale = -4,
    /// <summary>The cost the caller expected isn't the cost now (something changed: refresh and ask again).</summary>
    CostChanged = -5,
    /// <summary>Not enough unassigned XP (or skill credits for a train).</summary>
    NotEnough = -6,
    /// <summary>Can't be raised: at or past the top rank, an untrained skill, or a skill that can't be trained.</summary>
    NotRaisable = -7,
    /// <summary>An earlier raise or train hasn't shown up in the numbers yet (5 s at most).</summary>
    Busy = -8,
    /// <summary>The send failed or the action queue was full.</summary>
    SendFailed = -9,
}

internal static class TrainingCosts
{
    /// <summary>Most ranks one API raise may buy (the retail window offers 1 and 10).</summary>
    public const int MaxRanksPerRaise = 100;

    // ── the XpTable ──────────────────────────────────────────────────────

    /// <summary>Reads the portal XpTable file (0x0E000018). False when it is short or the counts are absurd.</summary>
    public static bool TryParseXpTable(ReadOnlySpan<byte> raw, out XpTableData table)
    {
        table = new XpTableData();
        int p = 0;
        if (!U32(raw, ref p, out _)) return false;                                   // file id
        if (!U32(raw, ref p, out uint attrs) || !U32(raw, ref p, out uint vitals)
            || !U32(raw, ref p, out uint trained) || !U32(raw, ref p, out uint spec)
            || !U32(raw, ref p, out uint levels))
            return false;
        if (attrs > 10000 || vitals > 10000 || trained > 10000 || spec > 10000 || levels > 10000)
            return false;
        if (!U32s(raw, ref p, (int)attrs + 1, out table.AttributeXp)
            || !U32s(raw, ref p, (int)vitals + 1, out table.VitalXp)
            || !U32s(raw, ref p, (int)trained + 1, out table.TrainedXp)
            || !U32s(raw, ref p, (int)spec + 1, out table.SpecializedXp))
            return false;
        var lv = new ulong[levels + 1];
        for (int i = 0; i <= levels; i++)
        {
            if (p + 8 > raw.Length) return false;
            lv[i] = BinaryPrimitives.ReadUInt64LittleEndian(raw.Slice(p, 8));
            p += 8;
        }
        table.LevelXp = lv;
        return true;
    }

    private static bool U32(ReadOnlySpan<byte> b, ref int p, out uint v)
    {
        v = 0;
        if (p + 4 > b.Length) return false;
        v = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(p, 4));
        p += 4;
        return true;
    }

    private static bool U32s(ReadOnlySpan<byte> b, ref int p, int n, out uint[] a)
    {
        a = Array.Empty<uint>();
        if (n < 0 || p + (long)n * 4 > b.Length) return false;
        a = new uint[n];
        for (int i = 0; i < n; i++) { a[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(p, 4)); p += 4; }
        return true;
    }

    // ── arithmetic (the server's: Player.SpendAttributeXp / SpendSkillXp) ──

    /// <summary>The table a stat raises against: attributes, vitals, trained (2) or specialized (3+) skills;
    /// null for an untrained or unknown skill (those aren't raised with XP).</summary>
    public static uint[]? TableFor(XpTableData t, uint kind, uint skillClass) => kind switch
    {
        1 => t.AttributeXp,
        2 => t.VitalXp,
        3 => skillClass >= 3 ? t.SpecializedXp : skillClass == 2 ? t.TrainedXp : null,
        _ => null,
    };

    /// <summary>True when <paramref name="ranks"/> is the table's top rank (nothing left to buy).</summary>
    public static bool IsMax(uint[] table, uint ranks) => table.Length == 0 || ranks + 1L >= table.Length;

    /// <summary>Ranks left to the top of the table.</summary>
    public static int RanksLeft(uint[] table, uint ranks) => (int)Math.Max(0L, table.Length - 1L - ranks);

    /// <summary>XP to buy <paramref name="n"/> more ranks from <paramref name="ranks"/> with
    /// <paramref name="spent"/> already spent; -1 past the top; 0 for n &lt; 1.</summary>
    public static long CostOf(uint[] table, uint ranks, uint spent, int n)
    {
        if (n < 1) return 0;
        long target = (long)ranks + n;
        if (target >= table.Length) return -1;
        return Math.Max(0L, (long)table[target] - spent);
    }

    /// <summary>How many ranks in a row <paramref name="available"/> XP buys (0 when not even one), up to the top.</summary>
    public static int Affordable(uint[] table, uint ranks, uint spent, long available)
    {
        int lo = 0, hi = RanksLeft(table, ranks);
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            long c = CostOf(table, ranks, spent, mid);
            if (c >= 0 && c <= available) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    // ── the checks before a raise is sent ──────────────────────────────────

    /// <summary>
    /// Everything a raise of <paramref name="count"/> ranks must pass, given the numbers now:
    /// a table (null = untrained/unknown skill), not past the top, a cost that equals what the
    /// caller confirmed (<paramref name="expectedXp"/> &gt; 0; 0 = don't compare), enough
    /// unassigned XP, and an amount the 32-bit game action can carry.
    /// <paramref name="cost"/> is the XP to send when the result is <see cref="RaiseStatus.Sent"/>.
    /// </summary>
    public static RaiseStatus CheckRaise(uint[]? table, uint ranks, uint spent, long unassignedXp,
                                         int count, long expectedXp, out long cost)
    {
        cost = -1;
        if (count < 1 || count > MaxRanksPerRaise || expectedXp < 0) return RaiseStatus.BadArguments;
        if (table == null || table.Length == 0) return RaiseStatus.NotRaisable;
        cost = CostOf(table, ranks, spent, count);
        if (cost <= 0) return RaiseStatus.NotRaisable;          // past the top (-1) or nothing to buy (0)
        if (expectedXp != 0 && expectedXp != cost) return RaiseStatus.CostChanged;
        if (cost > unassignedXp) return RaiseStatus.NotEnough;
        if (cost > uint.MaxValue) return RaiseStatus.NotRaisable;  // more than one game action can carry
        return RaiseStatus.Sent;
    }

    /// <summary>
    /// The checks for training an untrained skill with credits: the skill must be untrained
    /// (class 1, or 0 = not in the table), the dat must price it (&gt; 0), the price must be what
    /// the caller confirmed (<paramref name="expectedCredits"/> &gt; 0; 0 = don't compare) and
    /// the player must have the credits.
    /// </summary>
    public static RaiseStatus CheckTrain(uint skillClass, int trainedCost, int credits, int expectedCredits)
    {
        if (expectedCredits < 0) return RaiseStatus.BadArguments;
        if (skillClass >= 2 || trainedCost <= 0) return RaiseStatus.NotRaisable;
        if (expectedCredits != 0 && expectedCredits != trainedCost) return RaiseStatus.CostChanged;
        if (trainedCost > credits) return RaiseStatus.NotEnough;
        return RaiseStatus.Sent;
    }
}

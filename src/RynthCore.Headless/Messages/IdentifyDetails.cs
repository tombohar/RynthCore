// ============================================================================
//  RynthCore.Headless - Messages/IdentifyDetails.cs
//  What an identify reply (game event 0xC9, SetAppraiseInfo) says beyond its property
//  tables, which PropertyWire.TryParseIdentify reads but only counts or partly keeps:
//  the spell book's ids and the whole creature profile (health, stamina, mana and
//  their maxima).
//
//  Layout (ACE AppraiseInfo.Write), from the event type on: [type][guid][flags][success],
//  then per flag in this order: int table (0x1), int64 (0x2000), bool (0x2), float (0x4),
//  string (0x8), data id (0x1000) - each a PackableHashTable (u16 count, u16 buckets,
//  entries) - then the spell book (0x10: u32 count, u32 ids), the armour profile (0x80:
//  8 f32), the creature profile (0x100: u32 flags, health, health max; flag 0x8 adds
//  Str End Quick Coord Focus Self, Stamina Mana StaminaMax ManaMax; flag 0x1 adds u16
//  highlights + u16 colors). The weapon and hook profiles that may follow are not needed.
// ============================================================================

using RynthCore.PluginSdk.Net;

namespace RynthCore.Headless.Messages;

public sealed record IdentifyDetails(uint ObjectId, uint Flags, bool Success, uint[] Spells, bool HasCreatureProfile,
    uint Health, uint HealthMax, uint Stamina, uint StaminaMax, uint Mana, uint ManaMax)
{
    public bool HasSpellBook => (Flags & 0x10) != 0;
}

public static class IdentifyDetailsReader
{
    private const uint IdInt = 0x0001, IdBool = 0x0002, IdFloat = 0x0004, IdString = 0x0008, IdSpellBook = 0x0010,
        IdArmor = 0x0080, IdCreature = 0x0100, IdDid = 0x1000, IdInt64 = 0x2000;

    /// <summary>
    /// Reads an identify reply starting at its event type (0xC9). A section that runs past the
    /// end stops the read; what came before it is kept (spells read in full or not at all).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> msg, out IdentifyDetails details)
    {
        details = new IdentifyDetails(0, 0, false, Array.Empty<uint>(), false, 0, 0, 0, 0, 0, 0);
        var r = new NetReader(msg);
        if (!r.U32(out uint type) || type != 0xC9) return false;
        if (!r.U32(out uint id) || !r.U32(out uint f) || !r.U32(out uint success) || id == 0) return false;
        details = details with { ObjectId = id, Flags = f, Success = success != 0 };

        if ((f & IdInt) != 0 && !SkipTable(ref r, 8)) return true;
        if ((f & IdInt64) != 0 && !SkipTable(ref r, 12)) return true;
        if ((f & IdBool) != 0 && !SkipTable(ref r, 8)) return true;
        if ((f & IdFloat) != 0 && !SkipTable(ref r, 12)) return true;
        if ((f & IdString) != 0 && !SkipStringTable(ref r)) return true;
        if ((f & IdDid) != 0 && !SkipTable(ref r, 8)) return true;

        if ((f & IdSpellBook) != 0)
        {
            if (!r.ListCount(out int n, 4)) return true;
            var spells = new uint[n];
            for (int i = 0; i < n; i++)
                if (!r.U32(out spells[i])) return true;
            details = details with { Spells = spells };
        }

        if ((f & IdArmor) != 0 && !r.Skip(32)) return true;

        if ((f & IdCreature) != 0)
        {
            if (!r.U32(out uint cp) || !r.U32(out uint hp) || !r.U32(out uint hpMax)) return true;
            uint st = 0, stMax = 0, mp = 0, mpMax = 0;
            if ((cp & 0x8) != 0)
            {
                if (!r.Skip(24) || !r.U32(out st) || !r.U32(out mp) || !r.U32(out stMax) || !r.U32(out mpMax)) return true;
            }
            details = details with
            {
                HasCreatureProfile = true, Health = hp, HealthMax = hpMax,
                Stamina = st, StaminaMax = stMax, Mana = mp, ManaMax = mpMax,
            };
        }
        return true;
    }

    private static bool SkipTable(ref NetReader r, int entrySize)
        => r.TableCount(out int n, entrySize) && r.Skip(n * entrySize);

    private static bool SkipStringTable(ref NetReader r)
    {
        if (!r.TableCount(out int n, 6)) return false;
        for (int i = 0; i < n; i++)
            if (!r.U32(out _) || !r.String16L(out _)) return false;
        return true;
    }
}

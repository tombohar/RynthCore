using System.Text;

namespace PropertyWireTests;

/// <summary>
/// Builds messages byte for byte the way ACE writes them, from the part the client's
/// UIQueueManager::ProcessNetBlobData hands on: a game event from its event type (the 0xF7B0
/// header - guid, sequence - is already stripped), a top-level message from its opcode.
/// Each writer below mirrors the ACE.Server code named in its comment.
/// </summary>
internal sealed class AceMessage
{
    private readonly MemoryStream _ms = new();
    public readonly BinaryWriter W;

    private AceMessage(uint first)
    {
        W = new BinaryWriter(_ms);
        W.Write(first);
    }

    /// <summary>GameEventMessage: [F7B0][guid][seq][event type] - the client sees the type on.</summary>
    public static AceMessage GameEvent(uint eventType) => new(eventType);

    /// <summary>GameMessage: the opcode, then the body.</summary>
    public static AceMessage Message(uint opcode) => new(opcode);

    public byte[] ToArray() => _ms.ToArray();

    private static uint PadMultiple(uint length, uint multiple) => multiple * ((length + multiple - 1u) / multiple) - length;

    /// <summary>ACE Extensions.WriteString16L: u16 length, cp1252 bytes, pad (2 + length) to 4.</summary>
    public void String16L(string data)
    {
        W.Write((ushort)data.Length);
        W.Write(Encoding.Latin1.GetBytes(data));
        W.Write(new byte[PadMultiple(2u + (uint)data.Length, 4u)]);
    }

    /// <summary>ACE Extensions.Align: pad the stream to a multiple of 4.</summary>
    public void Align() => W.Write(new byte[PadMultiple((uint)_ms.Length, 4u)]);

    /// <summary>ACE PackableHashTable.WriteHeader: u16 count, u16 bucket count.</summary>
    public void HashHeader(int count, int buckets)
    {
        W.Write((ushort)count);
        W.Write((ushort)buckets);
    }

    public void IntTable(Dictionary<uint, int> t) { HashHeader(t.Count, 16); foreach (var kv in t) { W.Write(kv.Key); W.Write(kv.Value); } }
    public void Int64Table(Dictionary<uint, long> t) { HashHeader(t.Count, 8); foreach (var kv in t) { W.Write(kv.Key); W.Write(kv.Value); } }
    public void BoolTable(Dictionary<uint, bool> t) { HashHeader(t.Count, 8); foreach (var kv in t) { W.Write(kv.Key); W.Write(Convert.ToUInt32(kv.Value)); } }
    public void FloatTable(Dictionary<uint, double> t) { HashHeader(t.Count, 8); foreach (var kv in t) { W.Write(kv.Key); W.Write(kv.Value); } }
    public void StringTable(Dictionary<uint, string> t) { HashHeader(t.Count, 8); foreach (var kv in t) { W.Write(kv.Key); String16L(kv.Value); } }
    public void UIntTable(Dictionary<uint, uint> t) { HashHeader(t.Count, 8); foreach (var kv in t) { W.Write(kv.Key); W.Write(kv.Value); } }
}

/// <summary>ACE ArmorProfile: slash, pierce, bludgeon, cold, fire, acid, nether, lightning.</summary>
internal sealed record ArmorProfile(float Slash, float Pierce, float Bludgeon, float Cold, float Fire, float Acid, float Nether, float Lightning);

/// <summary>ACE CreatureProfile (Flags: HasBuffsDebuffs 0x1, ShowAttributes 0x8).</summary>
internal sealed record CreatureProfile(uint Flags, uint Health, uint HealthMax, uint[]? Attributes = null, ushort Highlights = 0, ushort Colors = 0);

/// <summary>ACE WeaponProfile.</summary>
internal sealed record WeaponProfile(uint DamageType, uint WeaponTime, uint WeaponSkill, uint Damage,
    double DamageVariance, double DamageMod, double WeaponLength, double MaxVelocity, double WeaponOffense, uint MaxVelocityEstimated = 0);

/// <summary>ACE HookProfile (Flags: Inscribable 1, IsHealer 2, IsFood 4, IsLockpick 8).</summary>
internal sealed record HookProfile(uint Flags, uint ValidLocations, uint AmmoType);

/// <summary>ACE AppraiseInfo: what BuildProfile leaves in it; Write() below mirrors AppraiseInfoExtensions.Write.</summary>
internal sealed class AppraiseInfo
{
    public bool Success = true;
    public Dictionary<uint, int> Ints = new();
    public Dictionary<uint, long> Int64s = new();
    public Dictionary<uint, bool> Bools = new();
    public Dictionary<uint, double> Floats = new();
    public Dictionary<uint, string> Strings = new();
    public Dictionary<uint, uint> Dids = new();
    public List<uint> SpellBook = new();
    public ArmorProfile? Armor;
    public CreatureProfile? Creature;
    public WeaponProfile? Weapon;
    public HookProfile? Hook;
    public ushort ArmorHighlight, ArmorColor, WeaponHighlight, WeaponColor, ResistHighlight, ResistColor;
    public uint[]? ArmorLevels;   // 9 values

    // ACE IdentifyResponseFlags
    private const uint IntStatsTable = 0x0001, BoolStatsTable = 0x0002, FloatStatsTable = 0x0004, StringStatsTable = 0x0008,
        SpellBookF = 0x0010, WeaponProfileF = 0x0020, HookProfileF = 0x0040, ArmorProfileF = 0x0080, CreatureProfileF = 0x0100,
        ArmorEnchantmentBitfield = 0x0200, ResistEnchantmentBitfield = 0x0400, WeaponEnchantmentBitfield = 0x0800,
        DidStatsTable = 0x1000, Int64StatsTable = 0x2000, ArmorLevelsF = 0x4000;

    /// <summary>ACE AppraiseInfo.BuildFlags.</summary>
    public uint Flags
    {
        get
        {
            uint f = 0;
            if (Ints.Count > 0) f |= IntStatsTable;
            if (Int64s.Count > 0) f |= Int64StatsTable;
            if (Bools.Count > 0) f |= BoolStatsTable;
            if (Floats.Count > 0) f |= FloatStatsTable;
            if (Strings.Count > 0) f |= StringStatsTable;
            if (Dids.Count > 0) f |= DidStatsTable;
            if (SpellBook.Count > 0) f |= SpellBookF;
            if (ResistHighlight != 0) f |= ResistEnchantmentBitfield;
            if (Armor != null) f |= ArmorProfileF;
            if (Creature != null) f |= CreatureProfileF;
            if (Weapon != null) f |= WeaponProfileF;
            if (Hook != null) f |= HookProfileF;
            if (ArmorHighlight != 0) f |= ArmorEnchantmentBitfield;
            if (WeaponHighlight != 0) f |= WeaponEnchantmentBitfield;
            if (ArmorLevels != null) f |= ArmorLevelsF;
            return f;
        }
    }

    /// <summary>GameEventIdentifyObjectResponse: the object guid, then AppraiseInfoExtensions.Write.</summary>
    public byte[] ToIdentifyMessage(uint guid)
    {
        var m = AceMessage.GameEvent(0x00C9);
        var w = m.W;
        w.Write(guid);
        uint flags = Flags;
        w.Write(flags);
        w.Write(Convert.ToUInt32(Success));
        if ((flags & IntStatsTable) != 0) m.IntTable(Ints);
        if ((flags & Int64StatsTable) != 0) m.Int64Table(Int64s);
        if ((flags & BoolStatsTable) != 0) m.BoolTable(Bools);
        if ((flags & FloatStatsTable) != 0) m.FloatTable(Floats);
        if ((flags & StringStatsTable) != 0) m.StringTable(Strings);
        if ((flags & DidStatsTable) != 0) m.UIntTable(Dids);
        if ((flags & SpellBookF) != 0) { w.Write(SpellBook.Count); foreach (uint s in SpellBook) w.Write(s); }   // PackableList
        if (Armor is { } a)
        {
            w.Write(a.Slash); w.Write(a.Pierce); w.Write(a.Bludgeon); w.Write(a.Cold);
            w.Write(a.Fire); w.Write(a.Acid); w.Write(a.Nether); w.Write(a.Lightning);
        }
        if (Creature is { } c)
        {
            w.Write(c.Flags); w.Write(c.Health); w.Write(c.HealthMax);
            if ((c.Flags & 0x8) != 0)
                foreach (uint v in c.Attributes ?? new uint[10]) w.Write(v);
            if ((c.Flags & 0x1) != 0) { w.Write(c.Highlights); w.Write(c.Colors); }
        }
        if (Weapon is { } p)
        {
            w.Write(p.DamageType); w.Write(p.WeaponTime); w.Write(p.WeaponSkill); w.Write(p.Damage);
            w.Write(p.DamageVariance); w.Write(p.DamageMod); w.Write(p.WeaponLength); w.Write(p.MaxVelocity);
            w.Write(p.WeaponOffense); w.Write(p.MaxVelocityEstimated);
        }
        if (Hook is { } h) { w.Write(h.Flags); w.Write(h.ValidLocations); w.Write(h.AmmoType); }
        if ((flags & ArmorEnchantmentBitfield) != 0) { w.Write(ArmorHighlight); w.Write(ArmorColor); }
        if ((flags & WeaponEnchantmentBitfield) != 0) { w.Write(WeaponHighlight); w.Write(WeaponColor); }
        if ((flags & ResistEnchantmentBitfield) != 0) { w.Write(ResistHighlight); w.Write(ResistColor); }
        if (ArmorLevels != null) foreach (uint v in ArmorLevels) w.Write(v);
        return m.ToArray();
    }
}

/// <summary>The property part of ACE GameEventPlayerDescription.WriteEventBody (and a tail after it).</summary>
internal sealed class PlayerDescription
{
    public Dictionary<uint, int> Ints = new();
    public Dictionary<uint, long> Int64s = new();
    public Dictionary<uint, bool> Bools = new();
    public Dictionary<uint, double> Floats = new();
    public Dictionary<uint, string> Strings = new();
    public Dictionary<uint, uint> Dids = new();
    public Dictionary<uint, uint> Iids = new();

    public byte[] ToMessage()
    {
        var m = AceMessage.GameEvent(0x0013);
        var w = m.W;
        uint f = 0;
        if (Ints.Count > 0) f |= 0x0001;
        if (Int64s.Count > 0) f |= 0x0080;
        if (Bools.Count > 0) f |= 0x0002;
        if (Floats.Count > 0) f |= 0x0004;
        if (Strings.Count > 0) f |= 0x0010;
        if (Dids.Count > 0) f |= 0x0008;
        if (Iids.Count > 0) f |= 0x0040;
        f |= 0x0020;   // Position (LastOutsideDeath): follows the tables, not read
        w.Write(f);
        w.Write(1u);   // WeenieType.Creature... (Player = 43, the value is skipped)
        if (Ints.Count > 0) m.IntTable(Ints);
        if (Int64s.Count > 0) m.Int64Table(Int64s);
        if (Bools.Count > 0) m.BoolTable(Bools);
        if (Floats.Count > 0) m.FloatTable(Floats);
        if (Strings.Count > 0) m.StringTable(Strings);
        if (Dids.Count > 0) m.UIntTable(Dids);
        if (Iids.Count > 0) m.UIntTable(Iids);
        // Position table (1 entry, 16 buckets) + the vectors, options and inventory that follow.
        m.HashHeader(1, 16);
        w.Write(14u);                        // PositionType.LastOutsideDeath
        w.Write(0x7D640013u); w.Write(10f); w.Write(20f); w.Write(30f); w.Write(1f); w.Write(0f); w.Write(0f); w.Write(0f);
        w.Write(0x00000303u);                // vector flags ... (garbage to the parser)
        return m.ToArray();
    }
}

/// <summary>ACE GameMessage{Private,Public}UpdateProperty* / UpdateDataID / UpdateInstanceID.</summary>
internal static class Updates
{
    private const byte Seq = 0x5A;

    public static byte[] PrivateInt(uint key, int v) { var m = AceMessage.Message(0x02CD); m.W.Write(Seq); m.W.Write(key); m.W.Write(v); return m.ToArray(); }
    public static byte[] PublicInt(uint guid, uint key, int v) { var m = AceMessage.Message(0x02CE); m.W.Write(Seq); m.W.Write(guid); m.W.Write(key); m.W.Write(v); return m.ToArray(); }
    public static byte[] PrivateInt64(uint key, long v) { var m = AceMessage.Message(0x02CF); m.W.Write(Seq); m.W.Write(key); m.W.Write(v); return m.ToArray(); }
    public static byte[] PublicInt64(uint guid, uint key, long v) { var m = AceMessage.Message(0x02D0); m.W.Write(Seq); m.W.Write(guid); m.W.Write(key); m.W.Write(v); return m.ToArray(); }
    public static byte[] PrivateBool(uint key, bool v) { var m = AceMessage.Message(0x02D1); m.W.Write(Seq); m.W.Write(key); m.W.Write(Convert.ToUInt32(v)); return m.ToArray(); }
    public static byte[] PublicBool(uint guid, uint key, bool v) { var m = AceMessage.Message(0x02D2); m.W.Write(Seq); m.W.Write(guid); m.W.Write(key); m.W.Write(Convert.ToUInt32(v)); return m.ToArray(); }
    public static byte[] PrivateFloat(uint key, double v) { var m = AceMessage.Message(0x02D3); m.W.Write(Seq); m.W.Write(key); m.W.Write(v); return m.ToArray(); }
    public static byte[] PublicFloat(uint guid, uint key, double v) { var m = AceMessage.Message(0x02D4); m.W.Write(Seq); m.W.Write(guid); m.W.Write(key); m.W.Write(v); return m.ToArray(); }

    /// <summary>GameMessagePrivateUpdatePropertyString: seq, key, Align(), WriteString16L.</summary>
    public static byte[] PrivateString(uint key, string v) { var m = AceMessage.Message(0x02D5); m.W.Write(Seq); m.W.Write(key); m.Align(); m.String16L(v); return m.ToArray(); }

    /// <summary>GameMessagePublicUpdatePropertyString: seq, key, THEN guid, Align(), WriteString16L.</summary>
    public static byte[] PublicString(uint guid, uint key, string v) { var m = AceMessage.Message(0x02D6); m.W.Write(Seq); m.W.Write(key); m.W.Write(guid); m.Align(); m.String16L(v); return m.ToArray(); }

    public static byte[] PrivateDataId(uint key, uint v) { var m = AceMessage.Message(0x02D7); m.W.Write(Seq); m.W.Write(key); m.W.Write(v); return m.ToArray(); }
    public static byte[] PublicDataId(uint guid, uint key, uint v) { var m = AceMessage.Message(0x02D8); m.W.Write(Seq); m.W.Write(guid); m.W.Write(key); m.W.Write(v); return m.ToArray(); }
    public static byte[] PrivateInstanceId(uint key, uint v) { var m = AceMessage.Message(0x02D9); m.W.Write(Seq); m.W.Write(key); m.W.Write(v); return m.ToArray(); }
    public static byte[] PublicInstanceId(uint guid, uint key, uint v) { var m = AceMessage.Message(0x02DA); m.W.Write(Seq); m.W.Write(guid); m.W.Write(key); m.W.Write(v); return m.ToArray(); }
}

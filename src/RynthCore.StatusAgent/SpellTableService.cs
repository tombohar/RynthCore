using RynthCore2.TerrainData;

namespace RynthCore.StatusAgent;

/// <summary>
/// The client's spell table (portal.dat file 0x0E00000E), read once in the background so the agent can
/// turn the plugin's bare enchantment ids into what the phone's Buffs tab shows: name, family (category),
/// tier, school, beneficial or not, and the spell's icon. Same layout and parse as the engine's
/// RynthCore.Engine/ImGui/MonsterHud/PortalSpellTable.cs (ACE.DatLoader SpellTable/SpellBase); this
/// copy keeps every spell, not just the debuffs. Until it is loaded (or if portal.dat is missing) a
/// lookup misses and the agent writes "Spell N" with no icon - the countdown still works.
/// </summary>
internal sealed class SpellTableService
{
    private const uint SpellTableFileId = 0x0E00000E;
    private const uint FlagBeneficial = 0x4;
    private const uint MetaEnchantment = 1, MetaPortalSummon = 7, MetaFellowEnchantment = 12;

    internal readonly record struct SpellInfo(uint Id, string Name, uint School, uint Icon, uint Category, uint Flags)
    {
        public bool Beneficial => (Flags & FlagBeneficial) != 0;
    }

    private readonly string _datPath;
    private volatile Dictionary<uint, SpellInfo>? _table;
    private int _queued;

    public SpellTableService(string portalDatPath) => _datPath = portalDatPath;

    public bool Ready => _table != null;

    /// <summary>A spell by id, once the table is in; queues the one-time load on first use. Any thread.</summary>
    public bool TryGet(uint spellId, out SpellInfo info)
    {
        var t = _table;
        if (t == null)
        {
            EnsureLoadQueued();
            info = default;
            return false;
        }
        return t.TryGetValue(spellId, out info);
    }

    public void EnsureLoadQueued()
    {
        if (_table != null || Interlocked.Exchange(ref _queued, 1) == 1) return;
        _ = Task.Run(Load);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_datPath)) { AgentLog.Warn($"[spells] portal.dat not found at '{_datPath}'; buff names fall back to spell ids."); return; }
            using var db = new DatDatabase();
            if (!db.Open(_datPath)) { AgentLog.Warn($"[spells] could not open '{_datPath}'."); return; }
            byte[]? raw = db.GetFileData(SpellTableFileId);
            if (raw == null) { AgentLog.Warn("[spells] spell table 0x0E00000E not in portal.dat."); return; }
            var t = Parse(raw, out int total);
            _table = t;
            AgentLog.Info($"[spells] spell table read: {t.Count:N0} of {total:N0} spells.");
        }
        catch (Exception ex) { AgentLog.Warn($"[spells] spell table read failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Parse the SpellTable file. Public for the agent tests (a hand-built table).</summary>
    internal static Dictionary<uint, SpellInfo> Parse(byte[] raw, out int total)
    {
        var t = new Dictionary<uint, SpellInfo>();
        var r = new SpanReader(raw);
        r.U32();                         // file id
        total = r.U16();
        r.U16();                         // bucket count
        for (int i = 0; i < total && r.Ok; i++)
        {
            uint id = r.U32();
            string name = r.ObfuscatedString();
            r.ObfuscatedString();        // description
            uint school = r.U32();
            uint icon = r.U32();
            uint category = r.U32();
            uint flags = r.U32();
            r.U32();                     // base mana
            r.Skip(8);                   // range constant, range mod
            r.U32();                     // power
            r.Skip(12);                  // economy mod, formula version, component loss
            uint meta = r.U32();
            r.U32();                     // meta spell id
            if (meta == MetaEnchantment || meta == MetaFellowEnchantment) r.Skip(16);   // duration f64, degrade mod, degrade limit
            else if (meta == MetaPortalSummon) r.Skip(8);                                // portal lifetime
            r.Skip(8 * 4 + 12 + 8 + 4 + 12);   // components, effects, recovery, display order, target type, mana mod
            if (!r.Ok) break;
            t[id] = new SpellInfo(id, name, school, icon, category, flags);
        }
        return t;
    }

    /// <summary>The spell's level 1-8 from its name: a Roman suffix ("... VII"), "Incantation of" = 8,
    /// otherwise 0 (unknown). Matches RynthAi's SpellInfo.ComputeLevel closely enough for display.</summary>
    internal static int TierFromName(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        if (name.StartsWith("Incantation of ", StringComparison.OrdinalIgnoreCase)) return 8;
        int sp = name.LastIndexOf(' ');
        string last = sp >= 0 ? name[(sp + 1)..] : name;
        return last switch
        {
            "I" => 1, "II" => 2, "III" => 3, "IV" => 4, "V" => 5, "VI" => 6, "VII" => 7, "VIII" => 8,
            _ => 0,
        };
    }

    /// <summary>Bounds-checked little-endian reader; Ok turns false on overrun.</summary>
    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _b;
        private int _p;
        public bool Ok;

        public SpanReader(byte[] b) { _b = b; _p = 0; Ok = true; }

        private bool Has(int n)
        {
            if (Ok && _p + n <= _b.Length) return true;
            Ok = false;
            return false;
        }

        public uint U32() { if (!Has(4)) return 0; uint v = BitConverter.ToUInt32(_b.Slice(_p, 4)); _p += 4; return v; }
        public ushort U16() { if (!Has(2)) return 0; ushort v = BitConverter.ToUInt16(_b.Slice(_p, 2)); _p += 2; return v; }
        public void Skip(int n) { if (Has(n)) _p += n; }

        /// <summary>u16 length + bytes with nibbles swapped, then aligned to 4 from the file start (as ACE does).</summary>
        public string ObfuscatedString()
        {
            int n = U16();
            if (!Has(n)) return "";
            var chars = new char[n];
            for (int i = 0; i < n; i++)
            {
                byte x = _b[_p + i];
                chars[i] = (char)(byte)((x >> 4) | (x << 4));   // Latin-1: byte == code point
            }
            _p += n;
            _p = (_p + 3) & ~3;
            return new string(chars);
        }
    }
}

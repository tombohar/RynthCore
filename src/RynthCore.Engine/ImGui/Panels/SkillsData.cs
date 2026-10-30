// ============================================================================
//  RynthCore.Engine - ImGui/Panels/SkillsData.cs
//  The Skills panel's static data from client_portal.dat, read once in the
//  background (UiBackgroundWriter, never AC's thread):
//
//    SkillTable 0x0E000004  id u32, PackedHashTable (u16 count, u16 buckets) of
//      u32 skill id + SkillBase: description, name (u16 length + bytes, each
//      padded to 4 from the file start), icon u32, trained cost i32,
//      specialized cost i32 (total), category u32, chargen use u32,
//      min level u32 (1 usable untrained, 2 needs training), formula
//      (W, X, Y, Z, attr1, attr2 u32), upper, lower, learn mod f64.
//    XpTable 0x0E000018     id u32, counts (attribute, vital, trained,
//      specialized i32, level u32), then count+1 u32 each for attributes,
//      vitals, trained and specialized skills, count+1 u64 levels, count+1 u32
//      skill credits per level.
//
//  Layouts: ACE.DatLoader SkillTable / SkillBase / SkillFormula / XpTable (the
//  server raises against the same XpTable). Readers take the published
//  immutable tables; any thread.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.ScriptWindows;

namespace RynthCore.Engine.ImGuiBackend.Panels;

/// <summary>One skill from the portal SkillTable.</summary>
internal sealed class SkillDatEntry
{
    public uint Id;
    public string Name = "";
    public string Description = "";
    public uint Icon;
    public int TrainedCost;
    public int SpecializedCost;
    public uint Category;       // 1 combat, 2 other, 3 magic
    public uint MinLevel;       // 1 usable untrained, 2 trained or better
    public uint Attr1, Attr2, Divisor;
    public bool UsableUntrained => MinLevel <= 1;
}

internal sealed class SkillDatTables
{
    public readonly Dictionary<uint, SkillDatEntry> Skills = new();
    public uint[] AttributeXp = Array.Empty<uint>();
    public uint[] VitalXp = Array.Empty<uint>();
    public uint[] TrainedXp = Array.Empty<uint>();
    public uint[] SpecializedXp = Array.Empty<uint>();
    public ulong[] LevelXp = Array.Empty<ulong>();
    /// <summary>The same arrays as one object (Compatibility/TrainingCosts).</summary>
    public XpTableData Xp = new();
}

internal static class SkillDat
{
    private const uint SkillTableFileId = 0x0E000004;
    private const uint XpTableFileId = 0x0E000018;
    private const int MaxFile = 1024 * 1024;

    private static volatile SkillDatTables? _tables;
    private static int _queued;
    private static volatile string _status = "not loaded";

    public static SkillDatTables? Tables => _tables;

    /// <summary>"38 skills, 190 attribute ranks" or why not. Any thread.</summary>
    public static string Status => _status;

    /// <summary>Queues the one-time read (again after a failure). Cheap once loaded.</summary>
    public static void EnsureLoadQueued()
    {
        if (_tables != null || Interlocked.Exchange(ref _queued, 1) == 1) return;
        _status = "loading";
        UiBackgroundWriter.Enqueue("skills dat", Load);
    }

    private static void Load()
    {
        try
        {
            string? path = AcDatFile.FindPortalDat();
            if (path == null) { Fail("client_portal.dat not found next to acclient.exe"); return; }
            using AcDatFile? dat = AcDatFile.Open(path, out string why);
            if (dat == null) { Fail($"{Path.GetFileName(path)}: {why}"); return; }
            byte[]? skills = dat.Read(SkillTableFileId, MaxFile);
            byte[]? xp = dat.Read(XpTableFileId, MaxFile);
            if (skills == null || xp == null) { Fail($"{Path.GetFileName(path)}: skill or XP table missing"); return; }
            var t = new SkillDatTables();
            if (!ParseSkills(skills, t) || !ParseXp(xp, t)) { Fail($"{Path.GetFileName(path)}: skill or XP table unreadable"); return; }
            _tables = t;
            _status = string.Format(CultureInfo.InvariantCulture, "{0} skills, {1} attribute and {2} skill ranks from {3}",
                t.Skills.Count, t.AttributeXp.Length - 1, t.TrainedXp.Length - 1, Path.GetFileName(path));
            RynthLog.UI($"Skills: {_status}");
        }
        catch (Exception ex)
        {
            Fail($"read failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Fail(string why)
    {
        _status = why;
        RynthLog.UI($"Skills: {why}");
        Volatile.Write(ref _queued, 0);   // the panel's next open tries again
    }

    private static bool ParseSkills(byte[] raw, SkillDatTables t)
    {
        var r = new Reader(raw);
        r.U32();                               // file id
        int count = r.U16();
        r.U16();                               // buckets
        for (int i = 0; i < count && r.Ok; i++)
        {
            var s = new SkillDatEntry { Id = r.U32() };
            s.Description = r.PString();
            s.Name = r.PString();
            s.Icon = r.U32();
            s.TrainedCost = (int)r.U32();
            s.SpecializedCost = (int)r.U32();
            s.Category = r.U32();
            r.U32();                           // chargen use
            s.MinLevel = r.U32();
            r.U32(); r.U32(); r.U32();         // formula W, X, Y
            s.Divisor = r.U32();               // Z
            s.Attr1 = r.U32();
            s.Attr2 = r.U32();
            r.Skip(24);                        // upper, lower bound, learn mod
            if (!r.Ok) break;
            if (s.Name.Length > 0) t.Skills[s.Id] = s;
        }
        return r.Ok && t.Skills.Count > 0;
    }

    // The XpTable layout and the cost arithmetic live in Compatibility/TrainingCosts.cs (shared
    // with the plugin API's raise calls and tested against a real portal.dat).
    private static bool ParseXp(byte[] raw, SkillDatTables t)
    {
        if (!TrainingCosts.TryParseXpTable(raw, out XpTableData xp)) return false;
        t.Xp = xp;
        t.AttributeXp = xp.AttributeXp;
        t.VitalXp = xp.VitalXp;
        t.TrainedXp = xp.TrainedXp;
        t.SpecializedXp = xp.SpecializedXp;
        t.LevelXp = xp.LevelXp;
        return true;
    }

    // ── XP arithmetic (the server's: Player.SpendAttributeXp / SpendSkillXp) ──

    /// <summary>True when <paramref name="ranks"/> is the table's top rank (nothing left to buy).</summary>
    public static bool IsMax(uint[] table, uint ranks) => TrainingCosts.IsMax(table, ranks);

    /// <summary>XP to buy <paramref name="n"/> more ranks from <paramref name="ranks"/> with <paramref name="spent"/> already spent; -1 past the top.</summary>
    public static long CostOf(uint[] table, uint ranks, uint spent, int n) => TrainingCosts.CostOf(table, ranks, spent, n);

    /// <summary>How many ranks in a row <paramref name="available"/> XP buys (0 when not even one), up to the top rank.</summary>
    public static int Affordable(uint[] table, uint ranks, uint spent, long available) =>
        TrainingCosts.Affordable(table, ranks, spent, available);

    /// <summary>Bounds-checked little-endian reader; Ok turns false on overrun.</summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _b;
        private int _p;
        public bool Ok;

        public Reader(byte[] b) { _b = b; _p = 0; Ok = true; }

        private bool Has(int n)
        {
            if (Ok && n >= 0 && _p + n <= _b.Length) return true;
            Ok = false;
            return false;
        }

        public uint U32() { if (!Has(4)) return 0; uint v = BitConverter.ToUInt32(_b.Slice(_p, 4)); _p += 4; return v; }
        public ushort U16() { if (!Has(2)) return 0; ushort v = BitConverter.ToUInt16(_b.Slice(_p, 2)); _p += 2; return v; }
        public ulong U64() { if (!Has(8)) return 0; ulong v = BitConverter.ToUInt64(_b.Slice(_p, 8)); _p += 8; return v; }
        public void Skip(int n) { if (Has(n)) _p += n; }

        public uint[] U32s(int n)
        {
            if (!Has(n * 4)) return Array.Empty<uint>();
            var a = new uint[n];
            for (int i = 0; i < n; i++) a[i] = U32();
            return a;
        }

        /// <summary>u16 length + Latin-1 bytes, then aligned to 4 from the file start (ACE AlignBoundary).</summary>
        public string PString()
        {
            int n = U16();
            if (!Has(n)) return "";
            string s = Encoding.Latin1.GetString(_b.Slice(_p, n));
            _p += n;
            _p = (_p + 3) & ~3;
            return s;
        }
    }
}

// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/PortalSpellTable.cs
//  The client's spell table (portal dat file 0x0E00000E), read once in the
//  background so the nameplates can turn "You cast <spell> on <monster>" into
//  a spell id, category, power and duration.
//
//  Layout (ACE.DatLoader SpellTable / SpellBase): file id u32, then a
//  PackedHashTable (u16 count, u16 buckets) of u32 spell id + SpellBase:
//    name, desc: u16 length + nibble-swapped bytes, each padded to 4
//    school, icon, category, bitfield, baseMana u32; rangeConst, rangeMod f32;
//    power u32; economyMod f32; formulaVersion u32; componentLoss f32;
//    metaSpellType u32, metaSpellId u32;
//    Enchantment (1) / FellowEnchantment (12): duration f64, degradeMod f32,
//      degradeLimit f32;  PortalSummon (7): portalLifetime f64;
//    8 x u32 components, casterEffect, targetEffect, fizzleEffect u32,
//    recoveryInterval f64, recoveryAmount f32, displayOrder,
//    nonComponentTargetType, manaMod u32.
//  Bitfield 0x4 = Beneficial, 0x10000 = DamageOverTime (ACE SpellFlags).
//
//  Only debuffs are kept (non-beneficial enchantments, ~1,500 of ~6,300),
//  plus every spell's icon id (script windows draw spell icons) and name
//  (the Inventory panel's item tooltips and the Skills panel's buff lists).
//  The dat reader is a minimal port of the on-demand reader in
//  RynthSuite/Shared/RynthCore.TerrainData/DatDatabase.cs (header at 0x140,
//  B-tree search by id, block chain whose first dword is the next block).
//
//  Threads: the load runs on UiBackgroundWriter (never AC's thread); readers
//  take a published immutable snapshot. Engine-owned statics only.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RynthCore.Engine.UI;

namespace RynthCore.Engine.ImGuiBackend.Hud;

/// <summary>One debuff spell from the client's spell table.</summary>
internal sealed class DebuffSpell
{
    public uint Id;
    public string Name = "";
    public uint Category;
    public uint Power;
    public uint Flags;
    public double Duration;     // seconds, before caster augmentations
    public bool IsDot => (Flags & 0x10000) != 0;
}

internal static class PortalSpellTable
{
    private const uint SpellTableFileId = 0x0E00000E;
    private const uint FlagBeneficial = 0x4;
    private const uint MetaEnchantment = 1, MetaPortalSummon = 7, MetaFellowEnchantment = 12;

    private sealed class Table
    {
        public readonly Dictionary<uint, DebuffSpell> ById = new();
        public readonly Dictionary<string, DebuffSpell> ByName = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Every spell's icon (0x06 texture id), for script windows' spell icons.</summary>
        public readonly Dictionary<uint, uint> IconById = new();
        /// <summary>Every spell's name, for item tooltips (Inventory panel) and buff lists (Skills panel).</summary>
        public readonly Dictionary<uint, string> NameById = new();
    }

    private static readonly object Sync = new();
    private static volatile Table? _table;
    private static bool _queued;
    private static volatile string _status = "not loaded";

    /// <summary>True once the table is readable.</summary>
    public static bool Ready => _table != null;

    public static int Count => _table?.ById.Count ?? 0;

    /// <summary>"1,512 debuffs from client_portal.dat" or why not. Any thread.</summary>
    public static string Status => _status;

    /// <summary>Queues the one-time read off the calling thread. Cheap after the first call.</summary>
    public static void EnsureLoadQueued()
    {
        if (_table != null) return;
        lock (Sync)
        {
            if (_queued) return;
            _queued = true;
        }
        _status = "loading";
        UiBackgroundWriter.Enqueue("portal spell table", LoadNow);
    }

    public static bool TryGetById(uint id, out DebuffSpell spell)
    {
        Table? t = _table;
        if (t != null && t.ById.TryGetValue(id, out DebuffSpell? s)) { spell = s; return true; }
        spell = null!;
        return false;
    }

    /// <summary>Any spell's icon (0x06 texture id) once the table is loaded. Any thread.</summary>
    public static bool TryGetIcon(uint spellId, out uint icon)
    {
        Table? t = _table;
        if (t != null && t.IconById.TryGetValue(spellId, out icon)) return true;
        icon = 0;
        return false;
    }

    /// <summary>Any spell's name once the table is loaded. Any thread.</summary>
    public static bool TryGetName(uint spellId, out string name)
    {
        Table? t = _table;
        if (t != null && t.NameById.TryGetValue(spellId, out string? n)) { name = n; return true; }
        name = string.Empty;
        return false;
    }

    /// <summary>
    /// A debuff by its exact name. Several spells can share a name (monster
    /// versions of "Poison", "Slowness"...): the strongest non-self one wins.
    /// </summary>
    public static bool TryGetByName(string name, out DebuffSpell spell)
    {
        Table? t = _table;
        if (t != null && t.ByName.TryGetValue(name, out DebuffSpell? s)) { spell = s; return true; }
        spell = null!;
        return false;
    }

    // ── Load (background) ────────────────────────────────────────────────

    private static void LoadNow()
    {
        try
        {
            string? path = FindPortalDat();
            if (path == null) { _status = "client_portal.dat not found next to acclient.exe"; return; }
            byte[]? raw = DatReader.ReadFile(path, SpellTableFileId, out string why);
            if (raw == null) { _status = $"{Path.GetFileName(path)}: {why}"; return; }
            Table t = Parse(raw, out int total);
            _table = t;
            _status = $"{t.ById.Count:N0} debuffs of {total:N0} spells from {Path.GetFileName(path)}";
            RynthLog.UI($"Nameplates: spell table - {_status}");
        }
        catch (Exception ex)
        {
            _status = $"spell table read failed: {ex.GetType().Name}: {ex.Message}";
            RynthLog.UI($"Nameplates: {_status}");
        }
    }

    private static string? FindPortalDat()
    {
        var dirs = new List<string>(2);
        try
        {
            string? exe = Environment.ProcessPath;
            string? dir = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir)) dirs.Add(dir);
        }
        catch { }
        try { dirs.Add(Environment.CurrentDirectory); } catch { }
        foreach (string dir in dirs)
            foreach (string name in new[] { "client_portal.dat", "portal.dat" })
            {
                string full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
        return null;
    }

    private static Table Parse(byte[] raw, out int total)
    {
        var t = new Table();
        var r = new SpanReader(raw);
        r.U32();                         // file id
        total = r.U16();
        r.U16();                         // bucket count
        for (int i = 0; i < total && r.Ok; i++)
        {
            uint id = r.U32();
            string name = r.ObfuscatedString();
            r.ObfuscatedString();        // description
            r.U32();                     // school
            uint icon = r.U32();
            uint category = r.U32();
            uint flags = r.U32();
            r.U32();                     // base mana
            r.Skip(8);                   // range constant, range mod
            uint power = r.U32();
            r.Skip(12);                  // economy mod, formula version, component loss
            uint meta = r.U32();
            r.U32();                     // meta spell id
            double duration = 0;
            if (meta == MetaEnchantment || meta == MetaFellowEnchantment)
            {
                duration = r.F64();
                r.Skip(8);               // degrade modifier, degrade limit
            }
            else if (meta == MetaPortalSummon)
            {
                r.Skip(8);
            }
            r.Skip(8 * 4 + 12 + 8 + 4 + 12);   // components, effects, recovery, display order, target type, mana mod
            if (!r.Ok) break;

            if (icon != 0) t.IconById[id] = icon;
            if (name.Length > 0) t.NameById[id] = name;
            if (meta != MetaEnchantment || (flags & FlagBeneficial) != 0 || name.Length == 0) continue;
            var s = new DebuffSpell { Id = id, Name = name, Category = category, Power = power, Flags = flags, Duration = duration };
            t.ById[id] = s;
            if (!t.ByName.TryGetValue(name, out DebuffSpell? prev) || Better(s, prev))
                t.ByName[name] = s;
        }
        return t;
    }

    /// <summary>Name collisions: prefer a spell cast on others (not SelfTargeted 0x8), then the stronger.</summary>
    private static bool Better(DebuffSpell a, DebuffSpell b)
    {
        bool aSelf = (a.Flags & 0x8) != 0, bSelf = (b.Flags & 0x8) != 0;
        if (aSelf != bSelf) return !aSelf;
        return a.Power > b.Power;
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
        public double F64() { if (!Has(8)) return 0; double v = BitConverter.ToDouble(_b.Slice(_p, 8)); _p += 8; return v; }
        public void Skip(int n) { if (Has(n)) _p += n; }

        /// <summary>u16 length + bytes with nibbles swapped, then aligned to 4 (from the file start, as ACE does).</summary>
        public string ObfuscatedString()
        {
            int n = U16();
            if (!Has(n)) return "";
            Span<char> chars = n <= 256 ? stackalloc char[n] : new char[n];
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

    /// <summary>Just enough of the dat format to read one file by id.</summary>
    private static class DatReader
    {
        private const int HeaderOffset = 0x140;
        private const int Branches = 62, MaxEntries = 61, EntrySize = 24;
        private const int NodeSize = 4 * Branches + 4 + EntrySize * MaxEntries;   // 1716
        private const int MaxDepth = 16;

        public static byte[]? ReadFile(string path, uint fileId, out string why)
        {
            // Read-only, and share everything: AC holds its own handles on the dats.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            Span<byte> hdr = stackalloc byte[36];
            fs.Seek(HeaderOffset, SeekOrigin.Begin);
            if (!ReadFully(fs, hdr)) { why = "short header"; return null; }
            uint blockSize = BitConverter.ToUInt32(hdr.Slice(4, 4));
            uint root = BitConverter.ToUInt32(hdr.Slice(32, 4));
            if (blockSize <= 4 || blockSize > 65536 || root == 0 || root >= fs.Length) { why = "bad header"; return null; }

            uint offset = root;
            for (int depth = 0; depth < MaxDepth && offset != 0; depth++)
            {
                byte[]? node = ReadChain(fs, blockSize, offset, NodeSize);
                if (node == null) { why = "unreadable B-tree node"; return null; }
                uint count = BitConverter.ToUInt32(node, 4 * Branches);
                if (count > MaxEntries) { why = "corrupt B-tree node"; return null; }
                int i = 0;
                while (i < count && BitConverter.ToUInt32(node, 4 * Branches + 4 + i * EntrySize + 4) < fileId) i++;
                if (i < count && BitConverter.ToUInt32(node, 4 * Branches + 4 + i * EntrySize + 4) == fileId)
                {
                    int e = 4 * Branches + 4 + i * EntrySize;
                    uint fileOffset = BitConverter.ToUInt32(node, e + 8);
                    uint fileSize = BitConverter.ToUInt32(node, e + 12);
                    if (fileSize == 0 || fileSize > 64 * 1024 * 1024) { why = "implausible file size"; return null; }
                    byte[]? data = ReadChain(fs, blockSize, fileOffset, (int)fileSize);
                    why = data == null ? "unreadable file blocks" : "";
                    return data;
                }
                bool leaf = BitConverter.ToUInt32(node, 0) == 0;
                if (leaf || i >= Branches) break;
                offset = BitConverter.ToUInt32(node, i * 4);
            }
            why = $"file 0x{fileId:X8} not in the dat";
            return null;
        }

        /// <summary>A block chain: each block starts with the next block's offset (0 = last).</summary>
        private static byte[]? ReadChain(FileStream fs, uint blockSize, uint offset, int size)
        {
            if ((long)offset + 4 > fs.Length) return null;
            var buffer = new byte[size];
            Span<byte> next = stackalloc byte[4];
            fs.Seek(offset, SeekOrigin.Begin);
            if (!ReadFully(fs, next)) return null;
            uint nextAddr = BitConverter.ToUInt32(next);
            int done = 0;
            int guard = 0;
            while (done < size)
            {
                int take = Math.Min((int)blockSize - 4, size - done);
                if (!ReadFully(fs, buffer.AsSpan(done, take))) return null;
                done += take;
                if (done >= size) break;
                if (nextAddr == 0 || nextAddr >= fs.Length || ++guard > 1_000_000) return null;
                fs.Seek(nextAddr, SeekOrigin.Begin);
                if (!ReadFully(fs, next)) return null;
                nextAddr = BitConverter.ToUInt32(next);
            }
            return buffer;
        }

        private static bool ReadFully(FileStream fs, Span<byte> dest)
        {
            while (dest.Length > 0)
            {
                int n = fs.Read(dest);
                if (n <= 0) return false;
                dest = dest.Slice(n);
            }
            return true;
        }
    }
}

// ============================================================================
//  RynthCore.PluginSdk - Manifest/PluginManifestReader.cs
//  Reads a plugin's embedded manifest straight from the DLL file: the PE
//  resource directory, RCDATA "RYNTH_PLUGIN_MANIFEST". Nothing is loaded or run,
//  so the engine can refuse a plugin before LoadLibrary (a NativeAOT plugin
//  can never be unloaded) and the launcher can read one it would never load.
//
//  Works on NativeAOT and managed (IL) plugin DLLs, PE32 and PE32+.
// ============================================================================

using System;
using System.IO;
using System.Text;

namespace RynthCore.PluginSdk.Manifest;

public static class PluginManifestReader
{
    private const int MaxManifestBytes = 64 * 1024;

    /// <summary>
    /// The plugin's manifest; null when the DLL has none (a plugin from before manifests: it
    /// keeps working as before). Throws <see cref="InvalidDataException"/> when there is one but
    /// it is broken, and IO exceptions when the file can't be read.
    /// </summary>
    public static RynthPluginManifest? Read(string dllPath)
    {
        string? json = ReadJson(dllPath);
        return json == null ? null : RynthPluginManifest.Parse(json);
    }

    /// <summary>The embedded manifest's text, or null when the file has none.</summary>
    public static string? ReadJson(string dllPath)
    {
        using var fs = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ReadJson(fs);
    }

    /// <summary>The embedded manifest's text, or null when the image has none.</summary>
    public static string? ReadJson(Stream pe)
    {
        byte[]? data = PeResources.Find(pe, RynthPluginManifest.ResourceType, RynthPluginManifest.ResourceName, MaxManifestBytes);
        if (data == null) return null;
        int start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        int end = data.Length;
        while (end > start && data[end - 1] == 0) end--;   // UpdateResource data is exact, but tolerate padding
        return Encoding.UTF8.GetString(data, start, end - start);
    }
}

/// <summary>A minimal reader for a PE file's resource directory (no Win32 calls).</summary>
public static class PeResources
{
    /// <summary>
    /// The data of resource <paramref name="type"/>/<paramref name="name"/> (first language), or null
    /// when the file has no such resource. Throws <see cref="InvalidDataException"/> on a malformed image.
    /// </summary>
    public static byte[]? Find(Stream pe, int type, string name, int maxBytes)
    {
        var r = new BinaryReader(pe, Encoding.Unicode, leaveOpen: true);
        long len = pe.Length;
        if (len < 0x40) throw Bad("too small for a PE file");
        pe.Position = 0;
        if (r.ReadUInt16() != 0x5A4D) throw Bad("no MZ header");
        pe.Position = 0x3C;
        uint peOff = r.ReadUInt32();
        if (peOff > len - 24) throw Bad("PE header offset out of range");
        pe.Position = peOff;
        if (r.ReadUInt32() != 0x00004550) throw Bad("no PE signature");
        r.ReadUInt16();                          // machine
        ushort sections = r.ReadUInt16();
        pe.Position += 12;                       // timestamp, symbol table, symbol count
        ushort optSize = r.ReadUInt16();
        r.ReadUInt16();                          // characteristics
        long opt = pe.Position;
        ushort magic = r.ReadUInt16();
        long dirs = magic switch
        {
            0x10B => opt + 96,                   // PE32
            0x20B => opt + 112,                  // PE32+
            _ => throw Bad($"unknown optional header magic 0x{magic:X}"),
        };
        pe.Position = dirs - 4;
        uint dirCount = r.ReadUInt32();
        if (dirCount < 3 || dirs + 3 * 8 > opt + optSize) return null;
        pe.Position = dirs + 2 * 8;              // IMAGE_DIRECTORY_ENTRY_RESOURCE
        uint rsrcRva = r.ReadUInt32();
        uint rsrcSize = r.ReadUInt32();
        if (rsrcRva == 0 || rsrcSize == 0) return null;

        var table = new (uint Va, uint VSize, uint Raw, uint RawSize)[sections];
        pe.Position = opt + optSize;
        for (int i = 0; i < sections; i++)
        {
            pe.Position += 8;                    // name
            uint vsize = r.ReadUInt32();
            uint va = r.ReadUInt32();
            uint rawSize = r.ReadUInt32();
            uint raw = r.ReadUInt32();
            pe.Position += 16;
            table[i] = (va, vsize, raw, rawSize);
        }

        long ToOffset(uint rva, uint size)
        {
            foreach (var s in table)
            {
                uint span = Math.Max(s.VSize, s.RawSize);
                if (rva >= s.Va && rva < s.Va + span)
                {
                    long off = s.Raw + (long)(rva - s.Va);
                    if (rva - s.Va + (long)size > s.RawSize || off + size > len) throw Bad("resource data outside its section");
                    return off;
                }
            }
            throw Bad($"RVA 0x{rva:X} is in no section");
        }

        long root = ToOffset(rsrcRva, 16);
        long rootEnd = root + rsrcSize;

        // Level 1: type (by id), level 2: name (by string), level 3: language (first).
        long? typeDir = FindEntry(r, root, rootEnd, root, id: type, name: null);
        if (typeDir is not { } td || td >= 0) { if (typeDir == null) return null; throw Bad("resource type entry is not a directory"); }
        long? nameDir = FindEntry(r, root, rootEnd, root + (~td), id: -1, name: name);
        if (nameDir is not { } nd) return null;
        long langDir = nd < 0 ? root + (~nd) : throw Bad("resource name entry is not a directory");
        long dataEntry = FirstEntry(r, root, rootEnd, langDir);
        if (dataEntry < 0) throw Bad("resource language entry is a directory");
        pe.Position = root + dataEntry;
        uint dataRva = r.ReadUInt32();
        uint dataSize = r.ReadUInt32();
        if (dataSize > maxBytes) throw Bad($"resource is {dataSize} bytes (at most {maxBytes})");
        pe.Position = ToOffset(dataRva, dataSize);
        byte[] data = r.ReadBytes((int)dataSize);
        if (data.Length != dataSize) throw Bad("resource data truncated");
        return data;
    }

    /// <summary>
    /// Finds an entry in the directory at <paramref name="dir"/> by id (or, with <paramref name="name"/>,
    /// by name, case-insensitive). Returns the entry's OffsetToData relative to the resource root:
    /// a subdirectory comes back as ~offset (negative), a data entry as the offset; null if absent.
    /// </summary>
    private static long? FindEntry(BinaryReader r, long root, long rootEnd, long dir, int id, string? name)
    {
        Stream s = r.BaseStream;
        if (dir + 16 > rootEnd) throw Bad("resource directory out of range");
        s.Position = dir + 12;
        int named = r.ReadUInt16();
        int ids = r.ReadUInt16();
        if (dir + 16 + (long)(named + ids) * 8 > rootEnd) throw Bad("resource directory entries out of range");
        for (int i = 0; i < named + ids; i++)
        {
            s.Position = dir + 16 + i * 8L;
            uint nameField = r.ReadUInt32();
            uint dataField = r.ReadUInt32();
            bool isNamed = (nameField & 0x80000000) != 0;
            bool match;
            if (name != null)
            {
                if (!isNamed) continue;
                long strAt = root + (nameField & 0x7FFFFFFF);
                if (strAt + 2 > rootEnd) throw Bad("resource name out of range");
                s.Position = strAt;
                int chars = r.ReadUInt16();
                if (strAt + 2 + chars * 2L > rootEnd) throw Bad("resource name out of range");
                string entryName = Encoding.Unicode.GetString(r.ReadBytes(chars * 2));
                match = string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                match = !isNamed && (nameField & 0xFFFF) == id;
            }
            if (!match) continue;
            long off = dataField & 0x7FFFFFFF;
            return (dataField & 0x80000000) != 0 ? ~off : off;
        }
        return null;
    }

    private static long FirstEntry(BinaryReader r, long root, long rootEnd, long dir)
    {
        Stream s = r.BaseStream;
        if (dir + 16 > rootEnd) throw Bad("resource directory out of range");
        s.Position = dir + 12;
        int count = r.ReadUInt16() + r.ReadUInt16();
        if (count == 0) throw Bad("empty resource language directory");
        s.Position = dir + 16 + 4;
        uint dataField = r.ReadUInt32();
        long off = dataField & 0x7FFFFFFF;
        return (dataField & 0x80000000) != 0 ? ~off : off;
    }

    private static InvalidDataException Bad(string why) => new("not a readable PE file: " + why);
}

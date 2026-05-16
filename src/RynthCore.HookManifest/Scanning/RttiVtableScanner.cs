using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.HookManifest.Scanning;

/// <summary>
/// MSVC 32-bit RTTI walker. Given a class name like "gmRadarUI", walks:
///   1. Type descriptor   (.rdata: ".?AV<Class>@@" string, header lives 8 bytes before)
///   2. Complete Object Locator(s) that reference the type descriptor (+0xC field)
///   3. vtables that the COL precedes (vtable starts at COL_ref_addr + 4)
///   4. virtual method slots (4-byte code pointers into .text)
/// </summary>
public static class RttiVtableScanner
{
    public readonly record struct Vtable(int VtableVa, int ColVa);
    public readonly record struct VtableMethod(int SlotIndex, int Va);

    public sealed record VtableEnumeration(
        string ClassName,
        int? TypeDescriptorVa,
        IReadOnlyList<int> ColVas,
        IReadOnlyList<Vtable> Vtables,
        IReadOnlyList<VtableMethod> Methods);

    /// <summary>
    /// Returns the VA of the type descriptor for <paramref name="className"/>,
    /// or 0 if not present in the image.
    /// The descriptor name string in .rdata is preceded by an 8-byte header
    /// (vftable ptr + spare); the descriptor's VA is string_va - 8.
    /// </summary>
    public static int FindTypeDescriptorVa(AcClientImageView image, string className)
    {
        string mangled = $".?AV{className}@@";
        int stringVa = StringRefScanner.FindStringVa(image, mangled);
        if (stringVa == 0) return 0;
        return stringVa - 8;
    }

    /// <summary>
    /// Find every 32-bit little-endian occurrence of <paramref name="targetVa"/>
    /// in .rdata. Returns the VA of each occurrence.
    /// </summary>
    public static List<int> FindPointersTo(AcClientImageView image, int targetVa)
    {
        var hits = new List<int>();
        if (!image.HasRdata) return hits;
        byte[] data = image.RdataBytes;
        byte b0 = (byte)(targetVa & 0xFF);
        byte b1 = (byte)((targetVa >> 8) & 0xFF);
        byte b2 = (byte)((targetVa >> 16) & 0xFF);
        byte b3 = (byte)((targetVa >> 24) & 0xFF);
        int max = data.Length - 4;
        // Pointer fields are 4-byte aligned in MSVC-emitted RTTI tables.
        for (int i = 0; i <= max; i += 4)
        {
            if (data[i] == b0 && data[i + 1] == b1 && data[i + 2] == b2 && data[i + 3] == b3)
                hits.Add(image.RdataBaseVa + i);
        }
        return hits;
    }

    /// <summary>
    /// Locate every Complete Object Locator that references
    /// <paramref name="typeDescriptorVa"/>. The COL's pTypeDescriptor field
    /// is at COL+0xC, so the COL starts 0xC bytes before each reference.
    /// Validates signature == 0 (32-bit).
    /// </summary>
    public static List<int> FindColsForTypeDescriptor(AcClientImageView image, int typeDescriptorVa)
    {
        var cols = new List<int>();
        if (!image.HasRdata) return cols;
        foreach (int refVa in FindPointersTo(image, typeDescriptorVa))
        {
            int colVa = refVa - 0xC;
            if (!TryReadInt32(image, colVa, out int signature)) continue;
            if (signature != 0) continue; // 32-bit COL signature
            if (!TryReadInt32(image, colVa + 0x10, out int classHierarchyVa)) continue;
            if (!IsInRdata(image, classHierarchyVa)) continue;
            cols.Add(colVa);
        }
        return cols;
    }

    /// <summary>
    /// Find every vtable preceded by a pointer to the given COL.
    /// The COL pointer slot lives 4 bytes before the vtable proper.
    /// </summary>
    public static List<Vtable> FindVtablesForCol(AcClientImageView image, int colVa)
    {
        var tables = new List<Vtable>();
        foreach (int refVa in FindPointersTo(image, colVa))
        {
            int vtableVa = refVa + 4;
            tables.Add(new Vtable(vtableVa, colVa));
        }
        return tables;
    }

    /// <summary>
    /// Read consecutive 4-byte code pointers from a vtable. Stops at the first
    /// entry that is not a valid .text pointer (or after <paramref name="maxSlots"/>).
    /// </summary>
    public static List<VtableMethod> EnumerateVtable(AcClientImageView image, int vtableVa, int maxSlots = 256)
    {
        var methods = new List<VtableMethod>();
        for (int slot = 0; slot < maxSlots; slot++)
        {
            int entryVa = vtableVa + slot * 4;
            if (!TryReadInt32(image, entryVa, out int ptr)) break;
            if (!IsInText(image, ptr)) break;
            methods.Add(new VtableMethod(slot, ptr));
        }
        return methods;
    }

    /// <summary>
    /// End-to-end: class name → every method address across every vtable.
    /// </summary>
    public static VtableEnumeration EnumerateClass(AcClientImageView image, string className)
    {
        int tdVa = FindTypeDescriptorVa(image, className);
        if (tdVa == 0)
            return new VtableEnumeration(className, null, [], [], []);

        var cols = FindColsForTypeDescriptor(image, tdVa);
        var allTables = new List<Vtable>();
        var allMethods = new List<VtableMethod>();
        var seen = new HashSet<int>();

        foreach (int colVa in cols)
        {
            foreach (var vt in FindVtablesForCol(image, colVa))
            {
                allTables.Add(vt);
                foreach (var m in EnumerateVtable(image, vt.VtableVa))
                {
                    if (seen.Add(m.Va))
                        allMethods.Add(m);
                }
            }
        }

        return new VtableEnumeration(className, tdVa, cols, allTables, allMethods);
    }

    private static bool TryReadInt32(AcClientImageView image, int va, out int value)
    {
        value = 0;
        if (image.HasRdata && va >= image.RdataBaseVa && va + 4 <= image.RdataBaseVa + image.RdataBytes.Length)
        {
            int off = va - image.RdataBaseVa;
            value = image.RdataBytes[off]
                  | (image.RdataBytes[off + 1] << 8)
                  | (image.RdataBytes[off + 2] << 16)
                  | (image.RdataBytes[off + 3] << 24);
            return true;
        }
        return false;
    }

    private static bool IsInRdata(AcClientImageView image, int va)
        => image.HasRdata
           && va >= image.RdataBaseVa
           && va < image.RdataBaseVa + image.RdataBytes.Length;

    private static bool IsInText(AcClientImageView image, int va)
        => va >= image.TextBaseVa
           && va < image.TextBaseVa + image.TextBytes.Length;
}

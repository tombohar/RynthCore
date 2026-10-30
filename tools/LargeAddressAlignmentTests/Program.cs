using System.Buffers.Binary;
using RynthCore.Engine.Compatibility;

namespace LargeAddressAlignmentTests;

/// <summary>
/// The large-address alignment fix, offline: which acclient code the scan picks, that a patched
/// image rescans as fully patched, that look-alike bytes are refused, and that the patched
/// remainder pads a cursor above 2 GB the same as below it.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;

    private static int Main(string[] args)
    {
        // Without an argument (scripts\Run-AllTests.ps1): the launcher's own client, or the old
        // C:\Turbine\Asheron's Call-ryn, which the build PC no longer has (2026-10-22).
        string exe = FindExe(args.Length > 0 ? args[0]
            : new[] { @"C:\Games\RynthCore\AcClient", @"C:\Turbine\Asheron's Call-ryn" }
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "acclient.exe"))) ?? @"C:\Games\RynthCore\AcClient");
        RealClient(exe);
        SyntheticPatterns();
        PaddingArithmetic();
        Console.WriteLine();
        Console.WriteLine(_failed == 0 && _checks > 0 ? $"PASS ({_checks} checks)" : $"FAIL ({_failed} of {_checks} checks failed)");
        return _failed == 0 && _checks > 0 ? 0 : 1;
    }

    private static string FindExe(string arg) =>
        File.Exists(arg) ? arg : Path.Combine(arg, "acclient.exe");

    private static void RealClient(string path)
    {
        Section("acclient .text from " + path);
        if (!File.Exists(path))
        {
            Check(false, "acclient.exe exists");
            return;
        }

        byte[] image = File.ReadAllBytes(path);
        Check(TryReadText(image, out int textVa, out byte[] text), ".text section read");
        if (text.Length == 0) return;

        List<AlignmentSite> sites = LargeAddressAlignmentScan.Find(text, textVa);
        Console.WriteLine($"     {sites.Count} site(s), first 0x{sites.FirstOrDefault().AndVa:X8}, last 0x{sites.LastOrDefault().AndVa:X8}");
        Check(sites.Count == 190, "retail client has 190 padding sites");
        Check(sites.All(s => !s.AlreadyPatched), "none patched on disk");

        // MotionData::UnPack (kelpie's 0x09000009 corruption) and the setup unpack under the
        // 0x539153 crashes, plus the one site with a pop before its je.
        foreach (int va in new[] { 0x005271A6, 0x00534B48, 0x00534C96, 0x00532E78, 0x004FD1B6, 0x006B394E })
            Check(sites.Any(s => s.AndVa == va), $"0x{va:X8} found");
        // A 16-bit ring index (movzx, never negative), not cursor padding: no trailing je.
        Check(sites.All(s => s.AndVa != 0x00547029), "0x00547029 (ring index) left alone");

        foreach (AlignmentSite s in sites)
            text[s.ImmHighByteVa - textVa] = LargeAddressAlignmentScan.PatchedHighByte;
        List<AlignmentSite> again = LargeAddressAlignmentScan.Find(text, textVa);
        Check(again.Count == sites.Count && again.All(s => s.AlreadyPatched), "patched image rescans as all already patched");
        Check(again.Select(s => s.AndVa).SequenceEqual(sites.Select(s => s.AndVa)), "same sites after patching");
    }

    private static void SyntheticPatterns()
    {
        Section("synthetic byte patterns");
        // and eax, 0x80000003 ; jns +5 ; dec eax ; or eax, -4 ; inc eax ; je +0x18
        byte[] eax = { 0x25, 0x03, 0x00, 0x00, 0x80, 0x79, 0x05, 0x48, 0x83, 0xC8, 0xFC, 0x40, 0x74, 0x18 };
        Check(Scan(eax).Count == 1, "eax short form matches");
        Check(Scan(eax)[0].ImmHighByteVa == 0x1000 + 4, "immediate high byte located");

        // and ecx, 0x80000003 ; mov [esp+0xC], eax ; jns ; dec ecx ; or ecx, -4 ; inc ecx ; pop ebx ; je
        byte[] ecxGap = { 0x81, 0xE1, 0x03, 0x00, 0x00, 0x80, 0x89, 0x44, 0x24, 0x0C, 0x79, 0x05, 0x49, 0x83, 0xC9, 0xFC, 0x41, 0x5B, 0x74, 0x10 };
        Check(Scan(ecxGap).Count == 1 && Scan(ecxGap)[0].Register == 1, "ecx long form with gap and pop matches");

        byte[] noJe = (byte[])eax.Clone();
        noJe[12] = 0x66;
        Check(Scan(noJe).Count == 0, "no trailing je: refused");

        byte[] wrongReg = (byte[])eax.Clone();
        wrongReg[7] = 0x49;   // dec ecx after and eax
        Check(Scan(wrongReg).Count == 0, "fix-up on another register: refused");

        byte[] otherMask = (byte[])eax.Clone();
        otherMask[1] = 0x07;  // % 8
        Check(Scan(otherMask).Count == 0, "and 0x80000007: refused");

        byte[] patched = (byte[])eax.Clone();
        patched[4] = 0x00;
        Check(Scan(patched) is [{ AlreadyPatched: true }], "and eax, 3 reported as already patched");
    }

    private static void PaddingArithmetic()
    {
        Section("padding above and below 2 GB");
        foreach (uint cursor in new uint[] { 0x2FDC98E1, 0x2FDC98E3, 0x80438035, 0x80438036, 0x8043A4C3, 0xFFFFFFFD })
        {
            int stock = StockPad(cursor), fixedPad = FixedPad(cursor);
            Check((cursor + (uint)fixedPad) % 4 == 0 && fixedPad < 4, $"0x{cursor:X8}: patched pad {fixedPad} aligns");
            if (cursor < 0x80000000)
                Check(stock == fixedPad, $"0x{cursor:X8}: below 2 GB the patch changes nothing");
            else
                Check(stock == fixedPad + 4, $"0x{cursor:X8}: stock pad {stock} overshoots by 4");
        }
    }

    // AC's code: r = and(ptr, 0x80000003) with the sign fix-up, pad = r == 0 ? 0 : 4 - r.
    private static int StockPad(uint cursor)
    {
        int r = (int)cursor % 4;
        return r == 0 ? 0 : 4 - r;
    }

    private static int FixedPad(uint cursor)
    {
        int r = (int)(cursor & 3);
        return r == 0 ? 0 : 4 - r;
    }

    private static List<AlignmentSite> Scan(byte[] code)
    {
        byte[] padded = new byte[code.Length + 64];
        code.CopyTo(padded, 0);
        return LargeAddressAlignmentScan.Find(padded, 0x1000);
    }

    private static bool TryReadText(byte[] image, out int textVa, out byte[] text)
    {
        textVa = 0;
        text = Array.Empty<byte>();
        int pe = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C));
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 6));
        int optSize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 20));
        int imageBase = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(pe + 24 + 28));
        for (int i = 0; i < sections; i++)
        {
            int s = pe + 24 + optSize + i * 40;
            if (System.Text.Encoding.ASCII.GetString(image, s, 8).TrimEnd('\0') != ".text") continue;
            int virtualSize = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(s + 8));
            int rva = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(s + 12));
            int rawOffset = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(s + 20));
            textVa = imageBase + rva;
            text = image.AsSpan(rawOffset, virtualSize).ToArray();
            return true;
        }
        return false;
    }

    private static void Section(string name) => Console.WriteLine($"\n== {name}");

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (!ok) _failed++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
    }
}

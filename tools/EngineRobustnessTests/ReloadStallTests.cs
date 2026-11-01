using RynthCore.Engine;
using RynthCore.Engine.ImGuiBackend;

namespace EngineRobustnessTests;

/// <summary>
/// The pure parts of the 2026-11-01 reload stall fix: the font atlas's RGBA->BGRA rows made by
/// the bake worker (FontPixels), and the hang dump's rundll32 command line (HangDumpCommand).
/// </summary>
internal static partial class Program
{
    private static void RunReloadStallTests()
    {
        FontPixelsSwapRedAndBlue();
        HangDumpCommandLine();
    }

    private static void FontPixelsSwapRedAndBlue()
    {
        _case = "font pixels";
        // Little-endian RGBA words: R is the low byte. A8R8G8B8 in memory is B, G, R, A.
        uint[] rgba = { 0xFF0000FFu /* opaque red */, 0x80FF0000u /* half blue */, 0x4000FF00u /* green */, 0x00000000u, 0x12345678u };
        var bgra = new uint[rgba.Length];
        FontPixels.RgbaToBgra(rgba, bgra);
        Check(bgra[0] == 0xFFFF0000u, $"red lands in the R byte of A8R8G8B8 (0x{bgra[0]:X8})");
        Check(bgra[1] == 0x800000FFu, $"blue lands in the low byte, alpha kept (0x{bgra[1]:X8})");
        Check(bgra[2] == 0x4000FF00u, "green and alpha stay");
        Check(bgra[3] == 0, "transparent black stays");
        Check(bgra[4] == 0x12785634u, $"R and B swap, G and A stay (0x{bgra[4]:X8})");

        // The bytes the old per-byte loop wrote: dst = { src[2], src[1], src[0], src[3] }.
        var rnd = new System.Random(11);
        var src = new uint[4096];
        for (int i = 0; i < src.Length; i++) src[i] = (uint)rnd.Next() ^ ((uint)rnd.Next() << 16);
        var dst = new uint[src.Length];
        FontPixels.RgbaToBgra(src, dst);
        bool same = true;
        for (int i = 0; i < src.Length && same; i++)
        {
            byte[] s = System.BitConverter.GetBytes(src[i]), d = System.BitConverter.GetBytes(dst[i]);
            same = d[0] == s[2] && d[1] == s[1] && d[2] == s[0] && d[3] == s[3];
        }
        Check(same, "the same bytes as the per-byte swizzle it replaces, for 4096 random pixels");

        bool threw = false;
        try { FontPixels.RgbaToBgra(new uint[4], new uint[3]); }
        catch (System.ArgumentException) { threw = true; }
        Check(threw, "a destination smaller than the source is refused");
    }

    private static void HangDumpCommandLine()
    {
        _case = "hang dump command";
        string a = HangDumpCommand.Arguments(@"C:\Games\RynthCore\Runtime\RynthCore.SehTrampoline.dll",
            @"C:\Games\RynthCore\Logs\dumps\hang_17132_20261031_211714.dmp", 17132);
        Check(a == "\"C:\\Games\\RynthCore\\Runtime\\RynthCore.SehTrampoline.dll\",RC_DumpProcess 17132 C:\\Games\\RynthCore\\Logs\\dumps\\hang_17132_20261031_211714.dmp",
            $"dll quoted, entry, pid, then the path as the rest of the line: {a}");
        string spaced = HangDumpCommand.Arguments(@"C:\a b\x.dll", @"C:\Users\Some One's PC\d.dmp", 42);
        Check(spaced.EndsWith(" 42 C:\\Users\\Some One's PC\\d.dmp", System.StringComparison.Ordinal),
            "a path with spaces and an apostrophe is passed as it is (DumpArgs.h takes the rest of the line)");
        Check(HangDumpCommand.DescribeExit(0) == "written", "0 = written");
        Check(HangDumpCommand.DescribeExit(5) == "MiniDumpWriteDump failed", "5 = MiniDumpWriteDump failed");
        Check(HangDumpCommand.DescribeExit(-1).Contains("rundll32"), "anything else is rundll32's own failure");
    }
}

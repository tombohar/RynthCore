using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Fixes the end-of-retail client's palette memory leak (PaletteLeakSites has the two sites and the byte
/// rules). Every custom palette (the colour swaps of creatures, armour and items) is 8 KB that is never
/// freed: about 3,000 an hour on a character hunting outdoors, ~460 MB on a client up 20 hours. Found and
/// published by eriknihlen (github.com/eriknihlen/ac-eor-palette-leak-fix, MIT); here each inc
/// [reg+0x24] becomes test [reg+0x24], reg by changing one byte (FF -> 85), after the bytes round it
/// are checked.
///
/// Off unless the launcher's "Fix AC's palette memory leak" is checked (appsettings.json
/// FixPaletteLeak; a missing key is off, since many clients already have the fix from a patched
/// acclient or the original patch). Read once at engine load. Off, nothing is touched. On, a site
/// already fixed (85 xx 24) or NOP'd (90 90 90) is left as it is and counted as fixed; a site whose
/// bytes are anything else is left alone and said once. Palettes that leaked before the fix stay
/// leaked until the client restarts.
/// </summary>
internal static class PaletteLeakFix
{
    private const uint PAGE_EXECUTE_READWRITE = 0x40;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

    public static string StatusMessage { get; private set; } = "Not applied yet.";

    public static void Initialize()
    {
        if (!LauncherSettings.FixPaletteLeakEnabled)
        {
            StatusMessage = "off (the launcher's \"Fix AC's palette memory leak\" is unchecked).";
            return;
        }

        int patched = 0, fixedBefore = 0, leftAlone = 0;
        foreach (PaletteLeakSites.Site s in PaletteLeakSites.Sites)
        {
            byte[]? bytes = ReadBytes(s.Start, s.Context.Length);
            PaletteLeakSites.SiteState state = bytes == null
                ? PaletteLeakSites.SiteState.Unknown
                : PaletteLeakSites.Classify(s, bytes);
            switch (state)
            {
                case PaletteLeakSites.SiteState.AlreadyFixed:
                case PaletteLeakSites.SiteState.AlreadyNopped:
                    fixedBefore++;
                    RynthLog.Compat($"Compat: palette leak fix - {s.Name} @ 0x{s.Va:X8} already fixed " +
                                    $"({(state == PaletteLeakSites.SiteState.AlreadyNopped ? "NOP'd" : "test")}); left as it is.");
                    break;
                case PaletteLeakSites.SiteState.Unknown:
                    leftAlone++;
                    RynthLog.Compat($"Compat: palette leak fix - {s.Name} @ 0x{s.Va:X8} does not look like the end-of-retail client; left alone.");
                    break;
                default:
                    if (WriteByte(s.Va, PaletteLeakSites.FixedByte)) patched++;
                    else leftAlone++;
                    break;
            }
        }

        StatusMessage = PaletteLeakSites.Summary(patched, fixedBefore, leftAlone);
        RynthLog.Info(StatusMessage);
    }

    private static byte[]? ReadBytes(int start, int count)
    {
        try
        {
            var b = new byte[count];
            for (int i = 0; i < count; i++) b[i] = Marshal.ReadByte(new IntPtr(start + i));
            return b;
        }
        catch { return null; }
    }

    private static bool WriteByte(int va, byte value)
    {
        IntPtr at = new(va);
        if (!VirtualProtect(at, (UIntPtr)1u, PAGE_EXECUTE_READWRITE, out uint old))
        {
            RynthLog.Compat($"Compat: palette leak fix - VirtualProtect 0x{va:X8} failed (error {Marshal.GetLastWin32Error()}); left alone.");
            return false;
        }
        try { Marshal.WriteByte(at, value); }
        finally { VirtualProtect(at, (UIntPtr)1u, old, out _); }
        FlushInstructionCache(GetCurrentProcess(), at, (UIntPtr)1u);
        return true;
    }
}

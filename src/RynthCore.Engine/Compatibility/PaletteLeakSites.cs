using System;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// The two places in the end-of-retail client that PaletteLeakFix changes, and how the bytes there are
/// read. Pure (no memory access), so tools\PaletteLeakFixTests can check it on fake bytes.
///
/// Palette::makeModifiedPalette adds a reference (inc [reg+0x24]) to a palette whose count DBObj's
/// constructor already set to 1, so every custom palette starts at 2 and is never freed. Found and
/// published by eriknihlen (github.com/eriknihlen/ac-eor-palette-leak-fix, MIT), who NOPs both
/// increments. RynthCore changes ONE byte a site, FF -> 85 (inc -> test [reg+0x24], reg): a one-byte
/// write is atomic, and the flags it sets are never read (a ret follows one site, a call the other).
///   0x0053EFFE  Palette::makeModifiedPalette()                    FF 40 24  inc [eax+0x24]
///   0x0053F19C  Palette::makeModifiedPalette(DataID, Subpalette*) FF 46 24  inc [esi+0x24]
/// </summary>
internal static class PaletteLeakSites
{
    public const byte StockByte = 0xFF, FixedByte = 0x85, Nop = 0x90;

    /// <summary>What the bytes at a site say.</summary>
    public enum SiteState
    {
        /// <summary>The stock inc: the leak is there, and the fix may change the byte.</summary>
        Stock,
        /// <summary>Already test [reg+0x24] (85 xx 24): fixed before, by this engine or another patch.</summary>
        AlreadyFixed,
        /// <summary>The inc NOP'd out (90 90 90), as eriknihlen's patch does: fixed by something else.</summary>
        AlreadyNopped,
        /// <summary>Not the end-of-retail client's code: left alone.</summary>
        Unknown,
    }

    /// <param name="Va">The address of the inc's first byte (the one changed).</param>
    /// <param name="Context">The bytes around it, from <see cref="Offset"/> back; null = any (a call's rel32).
    /// The three bytes from <see cref="Offset"/> are the inc itself (FF, then its ModRM and disp8).</param>
    public readonly record struct Site(int Va, string Name, byte?[] Context, int Offset)
    {
        public int Start => Va - Offset;
    }

    public static readonly Site[] Sites =
    [
        // call Palette::Palette; test eax,eax; je +3; [inc dword ptr [eax+24h]]; ret
        new(0x0053EFFE, "Palette::makeModifiedPalette()",
            [0xE8, null, null, null, null, 0x85, 0xC0, 0x74, 0x03, 0xFF, 0x40, 0x24, 0xC3], 9),
        // call Palette::Palette; mov esi,eax; test esi,esi; je +3Ch; [inc dword ptr [esi+24h]]; mov eax,[esi]
        new(0x0053F19C, "Palette::makeModifiedPalette(DataID, Subpalette*)",
            [0xE8, null, null, null, null, 0x8B, 0xF0, 0x85, 0xF6, 0x74, 0x3C, 0xFF, 0x46, 0x24, 0x8B, 0x06], 11),
    ];

    /// <summary>
    /// Reads <paramref name="bytes"/> (the client's bytes from <see cref="Site.Start"/>, at least the
    /// context's length). Everything outside the inc must match; the inc is stock (FF xx 24), already
    /// fixed (85 xx 24) or NOP'd (90 90 90). Anything else is Unknown.
    /// </summary>
    public static SiteState Classify(Site site, ReadOnlySpan<byte> bytes)
    {
        byte?[] ctx = site.Context;
        if (bytes.Length < ctx.Length) return SiteState.Unknown;
        int op = site.Offset;
        for (int i = 0; i < ctx.Length; i++)
        {
            if (i >= op && i < op + 3) continue;   // the inc, read below
            if (ctx[i] is byte want && bytes[i] != want) return SiteState.Unknown;
        }
        byte modrm = ctx[op + 1]!.Value, disp = ctx[op + 2]!.Value;
        if (bytes[op + 1] == modrm && bytes[op + 2] == disp)
        {
            if (bytes[op] == StockByte) return SiteState.Stock;
            if (bytes[op] == FixedByte) return SiteState.AlreadyFixed;
            return SiteState.Unknown;
        }
        if (bytes[op] == Nop && bytes[op + 1] == Nop && bytes[op + 2] == Nop) return SiteState.AlreadyNopped;
        return SiteState.Unknown;
    }

    /// <summary>The one log line: on, and what was done at each site.</summary>
    public static string Summary(int patched, int alreadyFixed, int leftAlone) =>
        $"Compat: palette leak fix on - {patched} site(s) patched, {alreadyFixed} already fixed, " +
        $"{leftAlone} left alone (of {Sites.Length}).";
}

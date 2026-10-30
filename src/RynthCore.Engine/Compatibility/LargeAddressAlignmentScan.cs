using System;
using System.Collections.Generic;

namespace RynthCore.Engine.Compatibility;

/// <summary>One signed "pointer % 4" padding site in acclient's code.</summary>
/// <param name="AndVa">VA of the <c>and r32, imm32</c> instruction.</param>
/// <param name="ImmHighByteVa">VA of the immediate's top byte (0x80 stock, 0x00 once patched).</param>
/// <param name="Register">x86 register number (0 = eax ... 7 = edi).</param>
/// <param name="AlreadyPatched">True when the immediate is already 0x00000003.</param>
internal readonly record struct AlignmentSite(int AndVa, int ImmHighByteVa, int Register, bool AlreadyPatched);

/// <summary>
/// Finds AC's 4-byte pack/unpack padding code, which MSVC compiled from a signed
/// <c>(int)ptr % 4</c>:
/// <code>
///   and  r, 0x80000003      ; keep the sign bit
///   ...  (0-12 bytes, no flag writes)
///   jns  +5
///   dec  r / or r, -4 / inc r   ; negative remainder fix-up
///   [pop r32]
///   je   skip               ; remainder 0: already aligned
///   ... pad 4 - r bytes
/// </code>
/// With large-address-aware acclient a buffer above 2 GB is a negative int, the remainder
/// comes out -3..-1, and AC pads 4 - r = 5..7 bytes instead of 1..3: the cursor runs 4 bytes
/// past the field, zeroes live data and parses garbage counts (kelpie's motion-table crashes).
/// Clearing the immediate's sign bit (<c>and r, 3</c>) makes the remainder 0..3 at any address.
/// Requiring the trailing <c>je</c> limits the patch to padding code: a signed modulo of a value
/// that can really be negative is left alone.
/// </summary>
internal static class LargeAddressAlignmentScan
{
    private const byte StockImmHighByte = 0x80;
    private const byte PatchedImmHighByte = 0x00;

    // and r32, imm32 to the jns: MSVC interleaves at most a few movs; the largest gap in
    // the retail client is 5 bytes.
    private const int MaxGapBeforeJns = 12;

    /// <summary>Scans <paramref name="text"/> (the bytes of .text, which starts at
    /// <paramref name="textVa"/>) for stock and already-patched padding sites.</summary>
    public static List<AlignmentSite> Find(ReadOnlySpan<byte> text, int textVa)
    {
        var sites = new List<AlignmentSite>();
        // Leave room for the longest match: 6 (and) + 12 (gap) + 7 (fix-up) + 1 (pop) + 2 (je).
        int last = text.Length - 28;
        for (int i = 0; i < last; i++)
        {
            int reg, immOffset;
            if (text[i] == 0x25)
            {
                reg = 0;          // and eax, imm32 (short form)
                immOffset = 1;
            }
            else if (text[i] == 0x81 && (text[i + 1] & 0xF8) == 0xE0)
            {
                reg = text[i + 1] & 0x07;   // and r32, imm32 (81 /4, mod=11)
                immOffset = 2;
            }
            else
            {
                continue;
            }

            if (text[i + immOffset] != 0x03 || text[i + immOffset + 1] != 0x00 || text[i + immOffset + 2] != 0x00)
                continue;
            byte high = text[i + immOffset + 3];
            if (high != StockImmHighByte && high != PatchedImmHighByte)
                continue;

            int andEnd = i + immOffset + 4;
            if (!HasSignedFixupThenJe(text, andEnd, reg))
                continue;

            sites.Add(new AlignmentSite(textVa + i, textVa + i + immOffset + 3, reg, high == PatchedImmHighByte));
        }
        return sites;
    }

    /// <summary>The immediate's top byte value a site must hold before it is patched.</summary>
    public static byte StockHighByte => StockImmHighByte;

    /// <summary>The immediate's top byte value after the patch (<c>and r, 3</c>).</summary>
    public static byte PatchedHighByte => PatchedImmHighByte;

    // jns +5 ; dec r ; or r, 0xFC ; inc r ; [pop r32] ; je rel8|rel32
    private static bool HasSignedFixupThenJe(ReadOnlySpan<byte> text, int from, int reg)
    {
        for (int k = from; k <= from + MaxGapBeforeJns; k++)
        {
            if (text[k] != 0x79 || text[k + 1] != 0x05 ||
                text[k + 2] != 0x48 + reg ||
                text[k + 3] != 0x83 || text[k + 4] != 0xC8 + reg || text[k + 5] != 0xFC ||
                text[k + 6] != 0x40 + reg)
            {
                continue;
            }

            int m = k + 7;
            if (text[m] >= 0x58 && text[m] <= 0x5F)
                m++;
            return text[m] == 0x74 || (text[m] == 0x0F && text[m + 1] == 0x84);
        }
        return false;
    }
}

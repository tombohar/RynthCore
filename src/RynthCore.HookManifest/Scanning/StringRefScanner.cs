using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.HookManifest.Scanning;

/// <summary>
/// Locate AC-client functions by anchoring on string literals they push to
/// the stack (debug/trace/error messages). String refs are far more
/// discriminating than the generic 6-byte prologues that match dozens of
/// functions.
/// </summary>
public static class StringRefScanner
{
    /// <summary>
    /// Returns the absolute VA of the first NUL-terminated occurrence of
    /// <paramref name="literal"/> in .rdata (and as a fallback, .text), or 0
    /// if not present.
    /// </summary>
    public static int FindStringVa(AcClientImageView image, string literal, bool requireNul = true)
    {
        if (string.IsNullOrEmpty(literal)) return 0;
        byte[] needle = Encoding.ASCII.GetBytes(literal);

        if (image.HasRdata)
        {
            int rd = FindAscii(image.RdataBytes, needle, requireNul);
            if (rd >= 0) return image.RdataBaseVa + rd;
        }

        int tx = FindAscii(image.TextBytes, needle, requireNul);
        if (tx >= 0) return image.TextBaseVa + tx;

        return 0;
    }

    /// <summary>
    /// Find every <c>push imm32</c> in .text whose immediate equals
    /// <paramref name="targetVa"/>. Returns offsets into TextBytes.
    /// </summary>
    public static List<int> FindPushImm32Sites(AcClientImageView image, int targetVa)
    {
        var sites = new List<int>();
        byte b0 = (byte)(targetVa & 0xFF);
        byte b1 = (byte)((targetVa >> 8) & 0xFF);
        byte b2 = (byte)((targetVa >> 16) & 0xFF);
        byte b3 = (byte)((targetVa >> 24) & 0xFF);
        byte[] bytes = image.TextBytes;
        int max = bytes.Length - 5;
        for (int i = 0; i < max; i++)
        {
            if (bytes[i] != 0x68) continue;
            if (bytes[i + 1] != b0) continue;
            if (bytes[i + 2] != b1) continue;
            if (bytes[i + 3] != b2) continue;
            if (bytes[i + 4] != b3) continue;
            sites.Add(i);
        }
        return sites;
    }

    /// <summary>
    /// Find every 4-byte little-endian occurrence of <paramref name="targetVa"/>
    /// anywhere in .text — catches mov reg,imm32 / lea / cmp / push-mem etc.,
    /// not just `push imm32`. Returns .text offsets of the immediate (the
    /// instruction starts 1-2 bytes earlier, but walk-back-to-prologue does
    /// not need exact instruction boundaries).
    /// </summary>
    public static List<int> FindImm32SitesInText(AcClientImageView image, int targetVa)
    {
        var sites = new List<int>();
        byte b0 = (byte)(targetVa & 0xFF);
        byte b1 = (byte)((targetVa >> 8) & 0xFF);
        byte b2 = (byte)((targetVa >> 16) & 0xFF);
        byte b3 = (byte)((targetVa >> 24) & 0xFF);
        byte[] bytes = image.TextBytes;
        int max = bytes.Length - 4;
        for (int i = 0; i <= max; i++)
        {
            if (bytes[i] == b0 && bytes[i + 1] == b1 && bytes[i + 2] == b2 && bytes[i + 3] == b3)
                sites.Add(i);
        }
        return sites;
    }

    /// <summary>
    /// Find every 4-byte little-endian occurrence of <paramref name="targetVa"/>
    /// in .rdata — i.e. the string used as a data-table pointer. Returns the
    /// VA of each pointer slot. There is no enclosing function to walk back to;
    /// this just reports that the string is table-referenced.
    /// </summary>
    public static List<int> FindDataPointerSites(AcClientImageView image, int targetVa)
    {
        var sites = new List<int>();
        if (!image.HasRdata) return sites;
        byte[] data = image.RdataBytes;
        byte b0 = (byte)(targetVa & 0xFF);
        byte b1 = (byte)((targetVa >> 8) & 0xFF);
        byte b2 = (byte)((targetVa >> 16) & 0xFF);
        byte b3 = (byte)((targetVa >> 24) & 0xFF);
        int max = data.Length - 4;
        for (int i = 0; i <= max; i += 4)
        {
            if (data[i] == b0 && data[i + 1] == b1 && data[i + 2] == b2 && data[i + 3] == b3)
                sites.Add(image.RdataBaseVa + i);
        }
        return sites;
    }

    /// <summary>
    /// Walk backward from <paramref name="textOffset"/> in TextBytes for the
    /// nearest occurrence of <paramref name="prologue"/>. Returns the offset
    /// of the matched prologue, or -1.
    /// </summary>
    public static int FindFunctionEntryBefore(
        AcClientImageView image, int textOffset, byte[] prologue, int maxBackward = 0x2000)
    {
        if (prologue.Length == 0) return -1;
        byte[] bytes = image.TextBytes;
        int floor = Math.Max(0, textOffset - maxBackward);
        for (int i = textOffset; i >= floor; i--)
        {
            if (i + prologue.Length > bytes.Length) continue;
            bool match = true;
            for (int j = 0; j < prologue.Length; j++)
            {
                if (bytes[i + j] != prologue[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    /// <summary>
    /// End-to-end: find every function that pushes <paramref name="literal"/>
    /// and walk back to a prologue. Single hit = strong anchor; multi-hit =
    /// needs disambiguation; zero = string isn't in the image.
    /// </summary>
    public static List<int> FindFunctionsByStringRef(
        AcClientImageView image, string literal, byte[] prologue,
        int maxBackward = 0x2000, bool requireNul = true)
    {
        var hits = new HashSet<int>();
        int stringVa = FindStringVa(image, literal, requireNul);
        if (stringVa == 0) return [];

        foreach (int site in FindPushImm32Sites(image, stringVa))
        {
            int funcOff = FindFunctionEntryBefore(image, site, prologue, maxBackward);
            if (funcOff >= 0) hits.Add(image.TextBaseVa + funcOff);
        }
        return new List<int>(hits);
    }

    private static int FindAscii(byte[] haystack, byte[] needle, bool requireNul)
    {
        int max = haystack.Length - needle.Length - (requireNul ? 1 : 0);
        for (int i = 0; i <= max; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (!match) continue;
            if (requireNul && haystack[i + needle.Length] != 0) continue;
            return i;
        }
        return -1;
    }
}

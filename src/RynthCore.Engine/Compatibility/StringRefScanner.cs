using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Finds AC client functions by anchoring on string literals that those
/// functions push onto the stack (for printf/debug-trace/error messages).
///
/// Why this exists: the existing PatternScanner.FindPattern + FindPrologueBefore
/// approach matches on a generic 6-byte prologue (e.g. `83 EC 0C 53 56 57 E8`)
/// that dozens of functions share. On retail AC the body opcodes we anchor on
/// (`mov [edx], imm32`) happen to make the first match correct, but on ACE
/// rebuilds the same prologue matches a *different* function — leading to
/// `_castSpell` and `InstallInnerDispatcherHook` calling/hooking the wrong
/// function and AVing AC.
///
/// String references are far more discriminating: a string like
/// "Combat::CastSpell" is referenced by exactly one function (the one that
/// logs it on error), so finding the `push &string` site and walking back to
/// the function prologue gives a verifiable anchor.
///
/// Workflow:
///   1. Identify a string that AC's target function pushes (run strings.exe
///      on acclient.exe, look for self-naming debug/error messages).
///   2. Call FindFunctionsByStringRef("Combat::CastSpell", prologue).
///   3. If exactly one match, that's the function. If multiple, narrow with
///      a second string or fall back to a body-pattern check.
///   4. If zero matches, the string isn't present in this binary — the
///      caller should fall back to a pattern scan or refuse to bind.
/// </summary>
internal static class StringRefScanner
{
    /// <summary>
    /// Find the absolute VA of a NUL-terminated ASCII string literal inside
    /// the loaded acclient.exe image window. Returns 0 if not present.
    ///
    /// Note: <see cref="AcClientModule.TryReadTextSection"/> reads from
    /// moduleBase+0x1000 to end-of-image, so the byte window covers .text
    /// AND .rdata AND .data. String literals live in .rdata, which is
    /// included.
    /// </summary>
    public static int FindStringVa(AcClientTextSection module, string literal)
    {
        if (string.IsNullOrEmpty(literal))
            return 0;

        byte[] needle = Encoding.ASCII.GetBytes(literal);
        byte[] haystack = module.Bytes;
        int max = haystack.Length - needle.Length - 1;

        for (int i = 0; i <= max; i++)
        {
            // Match the literal exactly...
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (!match) continue;

            // ...and require a NUL byte after it so we don't match a prefix
            // of a longer string ("CastSpell" inside "CastSpellOnTarget").
            if (haystack[i + needle.Length] != 0)
                continue;

            return module.TextBaseVa + i;
        }
        return 0;
    }

    /// <summary>
    /// Find every `push imm32` instruction whose immediate equals
    /// <paramref name="targetVa"/>. Opcode 0x68 + 4 little-endian bytes.
    /// This is how MSVC-emitted C++ pushes a string literal address before
    /// a printf-family call. Returns file offsets relative to
    /// <c>module.TextBaseVa</c>.
    /// </summary>
    public static List<int> FindPushImm32Sites(AcClientTextSection module, int targetVa)
    {
        var sites = new List<int>();
        byte b0 = (byte)(targetVa & 0xFF);
        byte b1 = (byte)((targetVa >> 8) & 0xFF);
        byte b2 = (byte)((targetVa >> 16) & 0xFF);
        byte b3 = (byte)((targetVa >> 24) & 0xFF);
        byte[] bytes = module.Bytes;
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
    /// Walk backward from <paramref name="siteOff"/> looking for the nearest
    /// occurrence of <paramref name="prologue"/>. Returns the offset of the
    /// matched prologue, or -1 if none found within <paramref name="maxBackward"/>.
    ///
    /// Unlike PatternScanner.FindPrologueBefore which has its own search loop,
    /// this version is intentionally local to a string-ref site so the
    /// match window stays small and a global pattern can't accidentally
    /// match a function far away.
    /// </summary>
    public static int FindFunctionEntryBefore(AcClientTextSection module, int siteOff, byte[] prologue, int maxBackward = 0x2000)
    {
        if (prologue.Length == 0)
            return -1;

        byte[] bytes = module.Bytes;
        int floor = Math.Max(0, siteOff - maxBackward);
        for (int i = siteOff; i >= floor; i--)
        {
            if (i + prologue.Length > bytes.Length)
                continue;

            bool match = true;
            for (int j = 0; j < prologue.Length; j++)
            {
                if (bytes[i + j] != prologue[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// One-call helper: locate every function whose body pushes
    /// <paramref name="literal"/>, by finding the string in the image,
    /// finding every <c>push string_va</c> site, and walking back to the
    /// nearest matching prologue.
    ///
    /// Returns the set of absolute VAs of candidate function entries.
    /// Caller should treat a single-result list as a hard match, and a
    /// multi-result list as "needs more disambiguation."
    /// </summary>
    public static List<int> FindFunctionsByStringRef(
        AcClientTextSection module,
        string literal,
        byte[] prologue,
        int maxBackward = 0x2000)
    {
        var hits = new HashSet<int>();
        int stringVa = FindStringVa(module, literal);
        if (stringVa == 0)
            return new List<int>();

        foreach (int site in FindPushImm32Sites(module, stringVa))
        {
            int funcOff = FindFunctionEntryBefore(module, site, prologue, maxBackward);
            if (funcOff >= 0)
                hits.Add(module.TextBaseVa + funcOff);
        }
        return new List<int>(hits);
    }

    /// <summary>
    /// Walks the loaded image and writes every printable ASCII run of at
    /// least <paramref name="minLength"/> bytes (terminated by NUL or any
    /// non-printable byte) to <paramref name="outputPath"/>, one per line,
    /// in the form: <c>0xVA\ttext</c>.
    ///
    /// This is the catalog the user (or a future automated anchor picker)
    /// greps for real anchors. AC client release builds tend to strip
    /// __FUNCTION__-style debug names but keep user-facing strings, format
    /// strings, RTTI type descriptors (".?AVClassName@@"), and file paths
    /// (when assertions or trace macros remained) — those are the real
    /// hooks for string anchoring.
    /// </summary>
    public static void DumpAllStrings(AcClientTextSection module, string outputPath, int minLength = 8)
    {
        byte[] bytes = module.Bytes;
        int baseVa = module.TextBaseVa;
        using var sw = new System.IO.StreamWriter(outputPath, append: false, encoding: System.Text.Encoding.UTF8);
        sw.WriteLine($"# acclient.exe ASCII string dump — base VA 0x{baseVa:X8}, size 0x{bytes.Length:X}, minLength={minLength}");
        sw.WriteLine("# Each line: <hex VA><TAB><string>");

        int i = 0;
        int written = 0;
        while (i < bytes.Length)
        {
            byte b = bytes[i];
            if (b < 0x20 || b > 0x7E)
            {
                i++;
                continue;
            }

            int start = i;
            while (i < bytes.Length)
            {
                byte c = bytes[i];
                if (c < 0x20 || c > 0x7E)
                    break;
                i++;
            }

            int len = i - start;
            if (len >= minLength)
            {
                int va = baseVa + start;
                // ASCII-only payload, safe to convert via Encoding.ASCII.
                string text = System.Text.Encoding.ASCII.GetString(bytes, start, len);
                sw.Write("0x");
                sw.Write(va.ToString("X8"));
                sw.Write('\t');
                sw.WriteLine(text);
                written++;
            }
        }

        RynthLog.Compat($"StringRef: dumped {written} string(s) of length>={minLength} to '{outputPath}'.");
    }

    /// <summary>
    /// Diagnostic: log every string in <paramref name="literals"/> with the
    /// VA we found it at, plus every push site for that string, plus the
    /// nearest enclosing function entry. Useful for hand-calibration: run
    /// this once with a list of candidate strings and read the log to see
    /// which strings uniquely identify which functions.
    /// </summary>
    public static void DumpStringAnchors(AcClientTextSection module, IEnumerable<string> literals, byte[] prologue)
    {
        foreach (string literal in literals)
        {
            int va = FindStringVa(module, literal);
            if (va == 0)
            {
                RynthLog.Compat($"StringRef: '{literal}' — NOT FOUND in image.");
                continue;
            }
            List<int> sites = FindPushImm32Sites(module, va);
            if (sites.Count == 0)
            {
                RynthLog.Compat($"StringRef: '{literal}' @ 0x{va:X8} — string exists but no `push imm32` site references it (may be loaded by `mov reg, imm32` or stored in vtable).");
                continue;
            }
            var funcs = new HashSet<int>();
            foreach (int site in sites)
            {
                int funcOff = FindFunctionEntryBefore(module, site, prologue);
                if (funcOff >= 0)
                    funcs.Add(module.TextBaseVa + funcOff);
            }
            string fnList;
            if (funcs.Count == 0)
            {
                fnList = "(no enclosing prologue match)";
            }
            else
            {
                var parts = new List<string>(funcs.Count);
                foreach (int v in funcs) parts.Add($"0x{v:X8}");
                fnList = string.Join(", ", parts);
            }
            RynthLog.Compat($"StringRef: '{literal}' @ 0x{va:X8} — pushed by {sites.Count} site(s); function entries: {fnList}");
        }
    }
}

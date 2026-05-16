using System.Collections.Generic;
using System.Linq;
using RynthCore.HookManifest.Scanning;
using RynthCore.HookManifest.Schema;
using RynthCore.HookResolver.Core.Disassembly;
using RynthCore.HookResolver.Core.Offline;

namespace RynthCore.HookResolver.Core.Discovery;

public enum StringReferenceMode
{
    PushImm32,      // 0x68 + va  (printf-style string arg; precise)
    AnyImm32InText, // any mov/lea/cmp immediate in .text (broad)
    DataPointer     // va appears as a pointer in .rdata table (no function)
}

public sealed record StringDiscoveryRequest(
    string Literal,
    byte[] Prologue,
    int MaxBackward = 0x2000,
    bool LooseMatch = false,
    StringReferenceMode ReferenceMode = StringReferenceMode.PushImm32);

public sealed record StringDiscoveryCandidate(
    int Va,
    int Rva,
    int PushSiteVa,
    FunctionSnapshot Snapshot);

public sealed record StringDiscoveryResult(
    string Literal,
    int? StringVa,
    int PushSiteCount,
    IReadOnlyList<StringDiscoveryCandidate> Candidates,
    string Summary);

public static class StringDiscoveryService
{
    public static StringDiscoveryResult Discover(AcClientImage image, StringDiscoveryRequest req)
    {
        bool requireNul = !req.LooseMatch;
        int stringVa = StringRefScanner.FindStringVa(image.View, req.Literal, requireNul);
        if (stringVa == 0)
        {
            string hint = requireNul
                ? " (try Loose match — it may be embedded in a longer string)"
                : "";
            return new StringDiscoveryResult(req.Literal, null, 0, [],
                $"'{req.Literal}' not present in image{hint}.");
        }

        // DataPointer mode: the string is a table entry, not a code reference.
        // We can report where the table is but there's no function to walk to.
        if (req.ReferenceMode == StringReferenceMode.DataPointer)
        {
            var ptrs = StringRefScanner.FindDataPointerSites(image.View, stringVa);
            string msg = ptrs.Count == 0
                ? $"'{req.Literal}' @ 0x{stringVa:X8} — not referenced as a .rdata data pointer either."
                : $"'{req.Literal}' @ 0x{stringVa:X8} — referenced by {ptrs.Count} data-table " +
                  $"pointer(s): {string.Join(", ", ptrs.ConvertAll(p => $"0x{p:X8}"))}. " +
                  "Table-driven — no function to walk back to (inspect the table layout manually).";
            return new StringDiscoveryResult(req.Literal, stringVa, ptrs.Count, [], msg);
        }

        var sites = req.ReferenceMode == StringReferenceMode.AnyImm32InText
            ? StringRefScanner.FindImm32SitesInText(image.View, stringVa)
            : StringRefScanner.FindPushImm32Sites(image.View, stringVa);

        string refKind = req.ReferenceMode == StringReferenceMode.AnyImm32InText
            ? "imm32" : "push imm32";

        if (sites.Count == 0)
        {
            return new StringDiscoveryResult(req.Literal, stringVa, 0, [],
                $"'{req.Literal}' @ 0x{stringVa:X8} — no `{refKind}` site references it " +
                "(try AnyImm32 or DataPointer reference mode).");
        }

        var byFunc = new Dictionary<int, int>();
        foreach (int site in sites)
        {
            int funcOff = StringRefScanner.FindFunctionEntryBefore(
                image.View, site, req.Prologue, req.MaxBackward);
            if (funcOff < 0) continue;
            int funcVa = image.View.TextBaseVa + funcOff;
            int siteVa = image.View.TextBaseVa + site;
            if (!byFunc.ContainsKey(funcVa))
                byFunc[funcVa] = siteVa;
        }

        var candidates = byFunc
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                int funcVa = kv.Key;
                int rva = funcVa - image.View.ImageBase;
                var snap = IcedDisassembler.Snapshot(image.View, rva);
                return new StringDiscoveryCandidate(funcVa, rva, kv.Value, snap);
            })
            .ToList();

        string summary = candidates.Count switch
        {
            0 => $"'{req.Literal}' @ 0x{stringVa:X8} — {sites.Count} {refKind} site(s), " +
                 "but no enclosing prologue match (try a different prologue or larger walk-back).",
            1 => $"'{req.Literal}' @ 0x{stringVa:X8} — single function candidate via {refKind} (strong anchor).",
            _ => $"'{req.Literal}' @ 0x{stringVa:X8} — {candidates.Count} candidates via {refKind} " +
                 $"({sites.Count} site(s); disambiguate with the snapshot)."
        };

        return new StringDiscoveryResult(req.Literal, stringVa, sites.Count, candidates, summary);
    }

    public static IReadOnlyList<ProloguePreset> Presets { get; } =
    [
        new("thiscall (push esi; mov esi, ecx)", [0x56, 0x8B, 0xF1]),
        new("cdecl (push ebp; mov ebp, esp)", [0x55, 0x8B, 0xEC]),
        new("combat-action (sub esp,0xC; push regs; call)", [0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8]),
        new("static (sub esp,N)", [0x83, 0xEC]),
    ];
}

public sealed record ProloguePreset(string Name, byte[] Bytes);

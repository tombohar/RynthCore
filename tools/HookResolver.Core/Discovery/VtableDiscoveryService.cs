using System.Collections.Generic;
using System.Linq;
using RynthCore.HookManifest.Scanning;
using RynthCore.HookManifest.Schema;
using RynthCore.HookResolver.Core.Disassembly;
using RynthCore.HookResolver.Core.Offline;

namespace RynthCore.HookResolver.Core.Discovery;

public sealed record VtableMethodCandidate(
    int SlotIndex,
    int Va,
    int VtableVa,
    FunctionSnapshot Snapshot);

public sealed record VtableDiscoveryResult(
    string ClassName,
    int? TypeDescriptorVa,
    int ColCount,
    int VtableCount,
    IReadOnlyList<VtableMethodCandidate> Methods,
    string Summary);

public static class VtableDiscoveryService
{
    public static VtableDiscoveryResult Discover(AcClientImage image, string className)
    {
        var view = image.View;
        var enumeration = RttiVtableScanner.EnumerateClass(view, className);

        if (enumeration.TypeDescriptorVa is null)
        {
            return new VtableDiscoveryResult(className, null, 0, 0, [],
                $"Type descriptor '.?AV{className}@@' not present in .rdata.");
        }

        if (enumeration.Vtables.Count == 0)
        {
            return new VtableDiscoveryResult(className,
                enumeration.TypeDescriptorVa, enumeration.ColVas.Count, 0, [],
                $"Type descriptor found @ 0x{enumeration.TypeDescriptorVa:X8} with " +
                $"{enumeration.ColVas.Count} COL(s) but no vtable preceded by a COL pointer.");
        }

        var methods = enumeration.Methods
            .OrderBy(m => m.SlotIndex)
            .Select(m =>
            {
                int rva = m.Va - view.ImageBase;
                var snap = IcedDisassembler.Snapshot(view, rva);
                int vtableVa = enumeration.Vtables[0].VtableVa;
                return new VtableMethodCandidate(m.SlotIndex, m.Va, vtableVa, snap);
            })
            .ToList();

        string summary =
            $"'{className}' → TypeDesc 0x{enumeration.TypeDescriptorVa:X8}, " +
            $"{enumeration.ColVas.Count} COL(s), {enumeration.Vtables.Count} vtable(s), " +
            $"{methods.Count} unique method(s).";

        return new VtableDiscoveryResult(
            className,
            enumeration.TypeDescriptorVa,
            enumeration.ColVas.Count,
            enumeration.Vtables.Count,
            methods,
            summary);
    }
}

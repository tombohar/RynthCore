using System.Globalization;
using RynthCore.HookManifest.Schema;
using RynthCore.HookResolver.Core.Offline;
using RynthCore.HookResolver.Core.Resolution;
using RynthCore.HookResolver.Core.Symbols;

string[] candidatePaths =
[
    @"C:\Games\RynthCore\AcClient\acclient.exe",
    @"C:\Turbine\Asheron's Call\acclient.exe",
];

string? path = candidatePaths.FirstOrDefault(File.Exists);
if (path is null)
{
    Console.WriteLine("ERROR: no acclient.exe found at any candidate path:");
    foreach (var p in candidatePaths) Console.WriteLine("  - " + p);
    return 2;
}

Console.WriteLine("=== HookResolver smoke test ===");
Console.WriteLine("acclient.exe path: " + path);

AcClientImage image = await AcClientImageLoader.LoadAsync(path);
var input = image.Input;

Console.WriteLine("SHA256          : " + input.Sha256);
Console.WriteLine("FileSizeBytes   : " + input.FileSizeBytes);
Console.WriteLine("Machine         : " + input.Machine);
Console.WriteLine($"ImageBase       : 0x{input.ImageBase:X8}");
Console.WriteLine($".text RVA       : 0x{input.TextRva:X8}");
Console.WriteLine($".text BaseVA    : 0x{image.View.TextBaseVa:X8}");
Console.WriteLine($".text bytes     : {image.View.TextBytes.Length}");
Console.WriteLine();

var symbols = AllSymbols.Default;
var fallbackById = symbols.ToDictionary(s => s.Id, s => s.FallbackVa);

var entries = new List<HookManifestEntry>();
await foreach (var e in HookResolveOrchestrator.ResolveAsync(image, symbols))
    entries.Add(e);

int total = entries.Count;
int resolved = entries.Count(e => e.Status == ResolutionStatus.Resolved);
int ambiguous = entries.Count(e => e.Status == ResolutionStatus.Ambiguous);
int unresolved = entries.Count(e => e.Status == ResolutionStatus.Unresolved);
int notApplicable = entries.Count(e => e.Status == ResolutionStatus.NotApplicable);

Console.WriteLine("=== BREAKDOWN ===");
Console.WriteLine($"Total symbols  : {total}");
Console.WriteLine($"Resolved       : {resolved}");
Console.WriteLine($"Ambiguous      : {ambiguous}");
Console.WriteLine($"Unresolved     : {unresolved}");
Console.WriteLine($"NotApplicable  : {notApplicable}");
Console.WriteLine();

Console.WriteLine("=== NON-RESOLVED (Ambiguous / Unresolved / NotApplicable) ===");
var bad = entries.Where(e => e.Status != ResolutionStatus.Resolved).ToList();
if (bad.Count == 0)
{
    Console.WriteLine("(none)");
}
else
{
    foreach (var e in bad)
    {
        Console.WriteLine($"- {e.SymbolId}");
        Console.WriteLine($"    Status            : {e.Status}");
        Console.WriteLine($"    MatchedCandidates : {e.MatchedCandidates}");
        Console.WriteLine($"    PatternId         : {e.PatternId}");
        Console.WriteLine($"    Reason            : {e.Reason ?? "(null)"}");
        if (e.ChosenCandidateReason is { } ccr)
            Console.WriteLine($"    ChosenReason      : {ccr}");
        if (e.Address is { } a)
            Console.WriteLine($"    (partial)Address  : 0x{a.Va:X8}");
        foreach (var w in e.Warnings)
            Console.WriteLine($"    Warning           : [{w.RuleId}] passed={w.Passed} {w.Detail}");
    }
}
Console.WriteLine();

Console.WriteLine("=== RESOLVED (SymbolId -> VA) ===");
foreach (var e in entries.Where(e => e.Status == ResolutionStatus.Resolved))
{
    int? va = e.Address?.Va;
    string vaStr = va is { } v ? $"0x{v:X8}" : "(no addr)";
    string line = $"  {e.SymbolId,-52} {vaStr}";

    fallbackById.TryGetValue(e.SymbolId, out int? fb);
    if (fb is { } fbv)
    {
        if (va is { } v2 && v2 != fbv)
            line += $"   [DELTA fallback=0x{fbv:X8} diff={(v2 - fbv):+#;-#;0}]";
        else if (va is { } v3 && v3 == fbv)
            line += "   [==fallback]";
    }
    else
    {
        line += "   [no-fallback]";
    }

    // surface any advisory warnings even on resolved entries
    if (e.Warnings.Count > 0)
        line += $"   (warnings={e.Warnings.Count})";

    Console.WriteLine(line);
}
Console.WriteLine();
Console.WriteLine("=== DONE ===");
return 0;

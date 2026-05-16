using System.Linq;
using System.Text;
using Avalonia.Media;
using RynthCore.HookManifest.Schema;
using RynthCore.HookResolver.Core.Diff;

namespace RynthCore.HookResolver.UI;

public sealed class HookRow
{
    public HookRow(HookManifestEntry entry, EngineResolution? engine)
    {
        Entry = entry;
        SymbolId = entry.SymbolId;
        Status = entry.Status.ToString();
        Va = entry.Address?.Hex ?? "—";
        Detail = entry.Reason
            ?? entry.ChosenCandidateReason
            ?? (entry.Validations.Count > 0 ? entry.Validations[0].Detail : "");

        StatusBrush = entry.Status switch
        {
            ResolutionStatus.Resolved => new SolidColorBrush(Color.Parse("#26C1A6")),
            ResolutionStatus.Ambiguous => new SolidColorBrush(Color.Parse("#E0B040")),
            ResolutionStatus.Unresolved => new SolidColorBrush(Color.Parse("#D24F4F")),
            _ => new SolidColorBrush(Color.Parse("#8896A0")),
        };

        if (engine is null)
        {
            EngineVa = "—";
            DiffSymbol = "·";
            DiffBrush = new SolidColorBrush(Color.Parse("#56646D"));
            DiffTooltip = "no engine-log entry for this symbol";
        }
        else if (engine.Va is null)
        {
            EngineVa = "unavailable";
            DiffSymbol = "⊘";
            DiffBrush = new SolidColorBrush(Color.Parse("#D24F4F"));
            DiffTooltip = "engine logged UNAVAILABLE";
        }
        else
        {
            int engineVa = engine.Va.Value;
            EngineVa = $"0x{engineVa:X8}";

            if (entry.Address is { } toolAddr && toolAddr.Va == engineVa)
            {
                DiffSymbol = "✓";
                DiffBrush = new SolidColorBrush(Color.Parse("#26C1A6"));
                DiffTooltip = $"match ({engine.Kind})";
            }
            else if (entry.Address is null)
            {
                DiffSymbol = "?";
                DiffBrush = new SolidColorBrush(Color.Parse("#E0B040"));
                DiffTooltip = "tool unresolved, engine has VA";
            }
            else
            {
                DiffSymbol = "✗";
                DiffBrush = new SolidColorBrush(Color.Parse("#D24F4F"));
                int delta = entry.Address.Va - engineVa;
                DiffTooltip = $"mismatch — tool/engine delta {delta:+#;-#;0}";
            }
        }
    }

    public HookManifestEntry Entry { get; }
    public string SymbolId { get; }
    public string Status { get; }
    public string Va { get; }
    public string EngineVa { get; }
    public string DiffSymbol { get; }
    public string DiffTooltip { get; }
    public string Detail { get; }
    public IBrush StatusBrush { get; }
    public IBrush DiffBrush { get; }

    public bool HasSnapshot => Entry.Snapshot is not null;

    public string SnapshotSummary
    {
        get
        {
            var s = Entry.Snapshot;
            if (s is null) return "(no snapshot — symbol unresolved)";
            return $"{s.LengthBytes} bytes • {s.InstructionCount} instructions • " +
                   (s.EndsAtReturn ? "ends at RET" : "no RET in range");
        }
    }

    public string SnapshotPrologue =>
        Entry.Snapshot is { PrologueHex: var hex } ? FormatHex(hex) : "—";

    public string SnapshotCallTargets
    {
        get
        {
            var s = Entry.Snapshot;
            if (s is null || s.CallTargets.Count == 0) return "(none captured)";
            var sb = new StringBuilder();
            foreach (var ct in s.CallTargets.Take(8))
                sb.AppendLine($"  +0x{ct.Offset:X3}  →  0x{ct.Va:X8}");
            return sb.ToString().TrimEnd();
        }
    }

    public string SnapshotNote => Entry.Snapshot?.Note ?? "";
    public bool HasSnapshotNote => !string.IsNullOrEmpty(Entry.Snapshot?.Note);

    private static string FormatHex(string raw)
    {
        var sb = new StringBuilder(raw.Length + raw.Length / 2);
        for (int i = 0; i < raw.Length; i += 2)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(raw, i, 2);
        }
        return sb.ToString();
    }
}

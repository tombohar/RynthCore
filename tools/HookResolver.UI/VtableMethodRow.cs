using System.Linq;
using System.Text;
using Avalonia.Media;
using RynthCore.HookManifest.Schema;
using RynthCore.HookResolver.Core.Discovery;

namespace RynthCore.HookResolver.UI;

public sealed class VtableMethodRow
{
    public VtableMethodRow(VtableMethodCandidate candidate)
    {
        Candidate = candidate;
        Slot = $"[{candidate.SlotIndex:D2}]";
        Va = $"0x{candidate.Va:X8}";

        var s = candidate.Snapshot;
        Summary = $"{s.LengthBytes} bytes • {s.InstructionCount} instr • " +
                  (s.EndsAtReturn ? "ends RET" : "no RET");
        PrologueDisplay = FormatHex(s.PrologueHex);
        CallTargetsDisplay = s.CallTargets.Count == 0
            ? "(none)"
            : string.Join("  ", s.CallTargets.Take(5).Select(ct => $"+0x{ct.Offset:X3}→0x{ct.Va:X8}"));

        AccentBrush = s.EndsAtReturn && s.LengthBytes > 8
            ? new SolidColorBrush(Color.Parse("#26C1A6"))
            : new SolidColorBrush(Color.Parse("#E0B040"));
    }

    public VtableMethodCandidate Candidate { get; }
    public FunctionSnapshot Snapshot => Candidate.Snapshot;
    public string Slot { get; }
    public string Va { get; }
    public string Summary { get; }
    public string PrologueDisplay { get; }
    public string CallTargetsDisplay { get; }
    public IBrush AccentBrush { get; }

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

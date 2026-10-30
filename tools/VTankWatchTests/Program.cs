using RynthCore.Engine.Compatibility;

namespace VTankWatchTests;

/// <summary>VTankSignal (the Decal bridge's VTank signal) without AC, Decal or VTank.</summary>
internal static class Program
{
    private static int _checks, _failed;

    private static int Main()
    {
        // VTank's documented commands, in the shapes a chat bar or a meta sends them.
        Parse("/vt start", VTankSignal.Start);
        Parse("/vt stop", VTankSignal.Stop);
        Parse("/VT START", VTankSignal.Start);
        Parse("/Vt Stop", VTankSignal.Stop);
        Parse("  /vt   start  ", VTankSignal.Start);
        Parse("/vt\tstop", VTankSignal.Stop);
        Parse("/vt start\r\n", VTankSignal.Start);
        Parse("/vt stop now", VTankSignal.Stop);      // VTank's own parser decides; the verb matches

        // Not VTank start/stop.
        Parse(null, VTankSignal.None);
        Parse("", VTankSignal.None);
        Parse("/vt", VTankSignal.None);
        Parse("/vt ", VTankSignal.None);
        Parse("/vt startx", VTankSignal.None);
        Parse("/vt starting", VTankSignal.None);
        Parse("/vt stopped", VTankSignal.None);
        Parse("/vt opt set enablecombat true", VTankSignal.None);
        Parse("/vt forcebuff", VTankSignal.None);
        Parse("/vt cancelforcebuff", VTankSignal.None);
        Parse("/vt meta load start", VTankSignal.None);
        Parse("/vt nav load stop", VTankSignal.None);
        Parse("/vtank start", VTankSignal.None);
        Parse("/vtstart", VTankSignal.None);
        Parse("vt start", VTankSignal.None);
        Parse("/ra start", VTankSignal.None);
        Parse("/ra stop", VTankSignal.None);
        Parse("/ub vt start", VTankSignal.None);
        Parse("/tell Someone, /vt start", VTankSignal.None);
        Parse("/f type /vt stop to stop", VTankSignal.None);
        Parse("hello /vt start", VTankSignal.None);

        // Diagnostic chat filter: VTank-looking lines yes, other players' lines no.
        Chat("[VTank] Something VTank printed", true);
        Chat("Virindi Tank v.1.0.0.0 loaded", true);
        Chat("You have entered the General channel.", false);
        Chat("Bob tells you, \"is VTank on?\"", false);
        Chat("Bob says, \"VTank\"", false);
        Chat("[RynthAi] Macro STARTED.", false);
        Chat(null, false);

        Console.WriteLine(_failed == 0 ? $"PASS ({_checks} checks)" : $"FAIL ({_failed} of {_checks} checks)");
        return _failed == 0 ? 0 : 1;
    }

    private static void Parse(string? line, int expected)
    {
        _checks++;
        int got = VTankSignal.ParseCommand(line);
        if (got != expected)
        {
            _failed++;
            Console.WriteLine($"FAIL ParseCommand({Show(line)}) = {got}, expected {expected}");
        }
    }

    private static void Chat(string? text, bool expected)
    {
        _checks++;
        bool got = VTankSignal.LooksLikeVTankChat(text);
        if (got != expected)
        {
            _failed++;
            Console.WriteLine($"FAIL LooksLikeVTankChat({Show(text)}) = {got}, expected {expected}");
        }
    }

    private static string Show(string? s) => s == null ? "null" : "\"" + s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
}

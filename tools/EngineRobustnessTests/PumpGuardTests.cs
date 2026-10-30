using System.Text.RegularExpressions;
using RynthCore.Engine.Plugins;

namespace EngineRobustnessTests;

/// <summary>
/// TickAll's step guards and the pump-dispatch line for the hang watchdog (Plugins/PumpGuards.cs),
/// plus a source scan that TickAll catches every one of its own steps.
/// </summary>
internal static partial class Program
{
    private static void RunPumpGuardTests()
    {
        StepFaultLogCadence();
        StepFaultLogText();
        PumpDispatchLine();
        TickAllGuardsEveryStep();
    }

    private static void StepFaultLogCadence()
    {
        _case = "step fault log cadence";
        var log = new StepFaultLog((int)TickStep.Count);
        Check(log.Record((int)TickStep.Nav3DCommit, out int c1) && c1 == 1, "the first throw is logged");
        int logged = 0;
        for (int i = 2; i <= 2500; i++)
            if (log.Record((int)TickStep.Nav3DCommit, out _)) logged++;
        Check(logged == 2, $"then only every {StepFaultLog.RepeatEvery}th (1000, 2000): got {logged}");
        Check(log.CountOf((int)TickStep.Nav3DCommit) == 2500, "every throw counted");
        Check(log.Record((int)TickStep.FreezeCheck, out int other) && other == 1, "steps are counted apart");
    }

    private static void StepFaultLogText()
    {
        _case = "step fault log text";
        string first = StepFaultLog.Describe(TickStep.DropFailedOwners, "InvalidOperationException", "Collection was modified", 1);
        Check(first.Contains("DropFailedOwners threw InvalidOperationException: Collection was modified"), "names the step and the exception");
        Check(first.Contains("the pump carries on"), "says the pump carries on");
        string later = StepFaultLog.Describe(TickStep.DropFailedOwners, "InvalidOperationException", "x", 3000);
        Check(later.Contains("3000 times"), "a repeat carries the count");
    }

    private static void PumpDispatchLine()
    {
        _case = "pump dispatch line";
        Check(PumpDispatchInfo.Describe("RynthAi", "Tick", 1_000, 13_375) == "inside RynthAi Tick for 12375 ms",
            "names plugin, export and how long");
        Check(PumpDispatchInfo.Describe(null, "Tick", 1_000, 13_375) == null, "no plugin call in progress: nothing");
        Check(PumpDispatchInfo.Describe("RynthAi", "Tick", 0, 13_375) == null, "no start time: nothing");
        Check(PumpDispatchInfo.Describe("RynthAi", "Tick", 13_000, 13_375) == null, "a call under a second old is normal: nothing");
        Check(PumpDispatchInfo.Describe("RynthNav", null, 1_000, 3_000) == "inside RynthNav call for 2000 ms", "unnamed export");
    }

    /// <summary>
    /// TickAll may let nothing of its own escape: each step is wrapped and reported through
    /// TickStepFailed, every TickStep is used, and the plugin Tick is dispatched as "Tick".
    /// </summary>
    private static void TickAllGuardsEveryStep()
    {
        _case = "TickAll guards";
        string? plugins = FindRepoDir(Path.Combine("src", "RynthCore.Engine", "Plugins"));
        Check(plugins != null, "found src\\RynthCore.Engine\\Plugins by walking up");
        if (plugins == null) return;
        string src = File.ReadAllText(Path.Combine(plugins, "PluginManager.cs")).Replace("\r\n", "\n");
        int start = src.IndexOf("public static void TickAll()", StringComparison.Ordinal);
        Check(start >= 0, "TickAll found");
        if (start < 0) return;
        int end = src.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        string body = end > start ? src[start..end] : src[start..];

        foreach (TickStep step in Enum.GetValues<TickStep>())
        {
            if (step == TickStep.Count) continue;
            Check(body.Contains($"TickStepFailed(TickStep.{step}, ex)"), $"{step} is caught and reported");
        }
        Check(body.Contains("EnterDispatch(plugin, \"Tick\")"), "the plugin Tick is dispatched as \"Tick\" (the hang watchdog's line)");
        Check(!Regex.IsMatch(body, @"catch\s*\{\s*\}"), "no silent catch {} left in TickAll");
        // The engine calls outside the plugin loop each sit inside a try.
        foreach (string call in new[] { "MainThreadFreeze.ShouldPausePluginTick()", "D3D9.Nav3DRenderer.ClearFrame()",
                                        "ScriptWindowRegistry.DropFailedOwners()", "D3D9.Nav3DRenderer.CommitFrame()" })
        {
            int at = body.IndexOf(call, StringComparison.Ordinal);
            string line = at < 0 ? "" : body[body.LastIndexOf('\n', at)..at];
            Check(at >= 0 && line.Contains("try {"), $"{call} is called inside try {{ }}");
        }
        string watchdog = File.ReadAllText(Path.Combine(Path.GetDirectoryName(plugins)!, "MainThreadHangWatchdog.cs"));
        Check(watchdog.Contains("PluginManager.DescribePumpDispatch()"), "the hang report names the plugin call the pump is inside");
    }
}

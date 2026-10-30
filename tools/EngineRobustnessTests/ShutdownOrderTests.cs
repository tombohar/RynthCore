using System.Text.RegularExpressions;
using RynthCore.Engine.Plugins;

namespace EngineRobustnessTests;

/// <summary>
/// Plugins are shut down only once the plugin pump has stopped (audit 2026-10-21, 01 #1):
/// PluginShutdownGate's verdicts (Plugins/PumpGuards.cs), and source scans that
/// EngineLifecycle.Shutdown stops the pump before any step that shuts a plugin down, and that
/// PluginManager.ShutdownAll asks the gate before it calls a plugin.
/// </summary>
internal static partial class Program
{
    private static void RunShutdownOrderTests()
    {
        ShutdownGateVerdicts();
        ShutdownGateRefusalLine();
        LifecycleStopsPumpBeforePlugins();
        ShutdownAllAsksTheGateFirst();
    }

    private static void ShutdownGateVerdicts()
    {
        _case = "shutdown gate";
        Check(PluginShutdownGate.Decide(pumpStarted: false, pumpExited: false, onPumpThread: false) == PluginShutdownGate.Verdict.NoPump,
            "no pump started: plugins may be shut down");
        Check(PluginShutdownGate.Decide(true, true, false) == PluginShutdownGate.Verdict.PumpStopped, "pump left its loop: stopped");
        Check(PluginShutdownGate.Decide(true, false, true) == PluginShutdownGate.Verdict.OnPumpThread, "on the pump thread itself");
        Check(PluginShutdownGate.Decide(true, false, false) == PluginShutdownGate.Verdict.PumpStillRunning,
            "pump still running on another thread");
        Check(PluginShutdownGate.MayShutDownPlugins(PluginShutdownGate.Verdict.NoPump), "NoPump allows");
        Check(PluginShutdownGate.MayShutDownPlugins(PluginShutdownGate.Verdict.PumpStopped), "PumpStopped allows");
        Check(PluginShutdownGate.MayShutDownPlugins(PluginShutdownGate.Verdict.OnPumpThread), "OnPumpThread allows (no frame alongside)");
        Check(!PluginShutdownGate.MayShutDownPlugins(PluginShutdownGate.Verdict.PumpStillRunning),
            "a pump still in a frame refuses: no Shutdown alongside a Tick");
        int wait = PluginShutdownGate.ShutdownWaitMs;
        Check(wait >= 1000 && wait <= 10000, $"the extra wait is bounded (1-10 s), got {wait} ms");
    }

    private static void ShutdownGateRefusalLine()
    {
        _case = "shutdown gate line";
        string line = PluginShutdownGate.DescribeRefusal(3, "inside RynthAi Tick for 4200 ms");
        Check(line.Contains("inside RynthAi Tick for 4200 ms"), "names what the pump is inside");
        Check(line.Contains("3 plugin(s) NOT shut down"), "says how many plugins were left alone");
        Check(PluginShutdownGate.DescribeRefusal(1, null).Contains("no plugin call named"), "no dispatch: says so");
    }

    /// <summary>
    /// The pump stops (TickPump.StopAndJoin) once, after the queues are disarmed and before
    /// every step that can shut a plugin down or tear down what its tick reads.
    /// </summary>
    private static void LifecycleStopsPumpBeforePlugins()
    {
        _case = "shutdown order";
        string? engine = FindRepoDir(Path.Combine("src", "RynthCore.Engine"));
        Check(engine != null, "found src\\RynthCore.Engine by walking up");
        if (engine == null) return;
        string src = File.ReadAllText(Path.Combine(engine, "EngineLifecycle.cs"));
        int start = src.IndexOf("public static void Shutdown()", StringComparison.Ordinal);
        Check(start >= 0, "EngineLifecycle.Shutdown found");
        if (start < 0) return;
        var steps = new List<string>();
        foreach (Match m in Regex.Matches(src[start..], "Step\\(\"([^\"]+)\""))
            steps.Add(m.Groups[1].Value);

        int pump = steps.IndexOf("TickPump.StopAndJoin");
        Check(pump >= 0, "the pump is stopped");
        Check(steps.FindAll(s => s == "TickPump.StopAndJoin").Count == 1, "exactly once");
        foreach (string before in new[] { "AcMainThreadQueue.Disarm", "UiDataHub.Disarm" })
        {
            int at = steps.IndexOf(before);
            Check(at >= 0 && at < pump, $"{before} still comes first");
        }
        foreach (string after in new[] { "AcUiPassHook.Uninstall", "VideoPatch.Uninstall", "EndSceneHook.Uninstall",
                                         "DecalBridgeHost.Shutdown", "PluginManager.ShutdownAll (defensive)",
                                         "EngineThreads.StopAndJoinAll", "ManagedPlugins.UnloadRetired" })
        {
            int at = steps.IndexOf(after);
            Check(at > pump, $"the pump stops before {after} (pump at {pump}, {after} at {at})");
        }
    }

    /// <summary>ShutdownAll asks PluginShutdownGate (through EntryPoint) before UnloadAllPlugins, and returns on a refusal.</summary>
    private static void ShutdownAllAsksTheGateFirst()
    {
        _case = "ShutdownAll gate";
        string? plugins = FindRepoDir(Path.Combine("src", "RynthCore.Engine", "Plugins"));
        Check(plugins != null, "found src\\RynthCore.Engine\\Plugins by walking up");
        if (plugins == null) return;
        string src = File.ReadAllText(Path.Combine(plugins, "PluginManager.cs")).Replace("\r\n", "\n");
        int start = src.IndexOf("public static void ShutdownAll()", StringComparison.Ordinal);
        Check(start >= 0, "ShutdownAll found");
        if (start < 0) return;
        int end = src.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        string body = end > start ? src[start..end] : src[start..];
        int gate = body.IndexOf("EntryPoint.StopTickPumpForPluginShutdown(", StringComparison.Ordinal);
        int refuse = body.IndexOf("PluginShutdownGate.MayShutDownPlugins(gate)", StringComparison.Ordinal);
        int unload = body.IndexOf("UnloadAllPlugins();", StringComparison.Ordinal);
        Check(gate >= 0 && refuse > gate, "asks the gate");
        Check(unload > refuse, "before any plugin is called (UnloadAllPlugins)");
        int ret = refuse < 0 ? -1 : body.IndexOf("return;", refuse, StringComparison.Ordinal);
        Check(ret > refuse && ret < unload, "returns without calling the plugins on a refusal");

        string? engine = Path.GetDirectoryName(plugins);
        string entry = File.ReadAllText(Path.Combine(engine!, "EntryPoint.cs")).Replace("\r\n", "\n");
        int stop = entry.IndexOf("internal static bool StopTickPumpAndJoin(", StringComparison.Ordinal);
        Check(stop >= 0, "StopTickPumpAndJoin found");
        if (stop < 0) return;
        int flag = entry.IndexOf("Interlocked.Exchange(ref _tickPumpStopRequested, 1);", stop, StringComparison.Ordinal);
        int nullCheck = entry.IndexOf("if (_tickPumpThread == null)", stop, StringComparison.Ordinal);
        Check(flag > stop && flag < nullCheck, "the stop flag is set before the no-pump early return (a late pump exits at once)");
    }
}

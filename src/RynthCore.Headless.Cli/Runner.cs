// ============================================================================
//  rynth-headless - Runner.cs
//  `rynth-headless run --config bots.json`: every configured character in this one
//  process, ticked round-robin on one thread (each bot's session, bridge and plugins
//  only ever run on it, as plugins expect). The thread sleeps until any bot's socket has
//  data or 15 ms pass. Ctrl+C stops every bot cleanly (end commands, logout); a second
//  Ctrl+C closes the sessions at once.
// ============================================================================

using System.Diagnostics;
using RynthCore.Headless.Dat;
using RynthCore.Headless.Movement;
using RynthCore.Headless.Net;

namespace RynthCore.Headless.Cli;

internal static class Runner
{
    public static int Run(string configPath, bool launcher = false, string? acFolder = null)
    {
        RunConfig cfg;
        try { cfg = RunConfig.Load(configPath); }
        catch (Exception ex) { Console.Error.WriteLine($"config: {ex.Message}"); return 2; }
        if (launcher && cfg.Sessions.Count != 1)
        {
            Console.Error.WriteLine("config: --launcher runs exactly one character (the launcher starts one process per account)");
            return 2;
        }

        var clock = Stopwatch.StartNew();
        string T() => $"[{clock.Elapsed.TotalSeconds,8:F2}]";
        object consoleGate = new();
        void Out(string s) { lock (consoleGate) Console.WriteLine($"{T()} {s}"); }

        // Plugin files go under the suite dir (RynthAi reads RYNTHSUITE_DIR once per load context).
        Directory.CreateDirectory(cfg.SuiteDir);
        Environment.SetEnvironmentVariable("RYNTHSUITE_DIR", cfg.SuiteDir);

        string? ac = DatFile.FindAcFolder(acFolder);
        using GeometryWalk? geo = ac != null ? GeometryWalk.Open(ac) : null;
        SpellTable? spells = ac != null ? SpellTable.Load(ac, out _) : null;
        Out($"{cfg.Sessions.Count} character(s) on {string.Join(", ", cfg.Sessions.Select(s => $"{s.Host}:{s.ServerPort}").Distinct())}; {(geo != null ? "walking on the dats in " + ac : "no dats: flat walking")}");

        var bots = cfg.Sessions.Select((s, i) => new Bot(cfg, s, i, geo, spells, Out)).ToList();
        if (cfg.StatusDir.Length > 0)
            foreach (Bot b in bots) Out($"[{b.Label}] status files: {Path.Combine(cfg.StatusDir, "RynthCore." + b.ClientId + ".*")}");

        LauncherSession? record = launcher ? new LauncherSession(cfg, cfg.Sessions[0]) : null;
        using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset,
            RynthCore.App.HeadlessLaunch.StopEventName(Environment.ProcessId));   // the launcher sets it (Ctrl+C can't reach a hidden console)
        bool stopAsked = false;

        int ctrlC = 0;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            if (++ctrlC == 1)
            {
                Out("stopping every character (Ctrl+C again to close at once)");
                foreach (Bot b in bots) b.Stop();
            }
        };

        // Logins are staggered 3 s apart, as the launcher staggers clients.
        long start = clock.ElapsedMilliseconds;
        var startAt = bots.Select((b, i) => start + i * 3000L).ToArray();
        var links = new List<UdpLink>();
        while (bots.Any(b => !b.Done) && ctrlC < 2)
        {
            links.Clear();
            foreach (Bot b in bots)
                if (b.Link != null) links.Add(b.Link);
            UdpLink.WaitAny(links, 15);
            long now = clock.ElapsedMilliseconds;
            for (int i = 0; i < bots.Count; i++)
            {
                if (now < startAt[i]) continue;
                try { bots[i].Tick(now); }
                catch (Exception ex) { Out($"[{bots[i].Label}] tick failed: {ex.GetType().Name}: {ex.Message}"); }
            }
            record?.Tick(bots[0]);
            if (!stopAsked && stopEvent.WaitOne(0))
            {
                stopAsked = true;
                Out("stop asked (stop event): stopping every character");
                foreach (Bot b in bots) b.Stop();
            }
        }

        foreach (Bot b in bots) b.Dispose();
        Out("all characters done");
        return 0;
    }
}

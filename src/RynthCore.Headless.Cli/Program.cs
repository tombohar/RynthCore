// ============================================================================
//  rynth-headless - P0 protocol spike.
//  Connects, logs in, enters the world as one character, prints chat and objects as
//  they appear, says one line in local chat, then logs out cleanly.
//
//    rynth-headless --host 127.0.0.1 --port 9000
//                   (--account NAME --password-env VAR | --launcher-account NAME)
//                   [--character NAME] [--say TEXT] [--stay SECONDS] [--objects] [--capture FILE]
//
//  The password comes from the environment variable named by --password-env, or from the
//  launcher's saved (DPAPI-encrypted) account; it is never printed, logged or written.
// ============================================================================

using RynthCore.Headless.Net;
using RynthCore.Headless.Session;

namespace RynthCore.Headless.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Several characters in one process: rynth-headless run --config bots.json
        if (args.Length >= 1 && args[0] == "run")
        {
            int i = Array.IndexOf(args, "--config");
            if (i < 0 || i + 1 >= args.Length) { Console.Error.WriteLine("usage: rynth-headless run --config FILE [--launcher] [--ac-folder DIR]"); return 2; }
            int ac = Array.IndexOf(args, "--ac-folder");
            return Runner.Run(args[i + 1], launcher: args.Contains("--launcher"), acFolder: ac >= 0 && ac + 1 < args.Length ? args[ac + 1] : null);
        }
        var a = ParseArgs(args);
        if (a == null) return Usage();

        // ACE boots a session already using the account and refuses the new login (character
        // error 0x1) until that is done, so an account-in-use failure is retried.
        for (int attempt = 1; ; attempt++)
        {
            int rc = RunOnce(a, out bool accountInUse);
            if (!accountInUse || attempt == 3) return rc;
            Console.WriteLine("account was in use; the server is booting the other session - retrying in 8 s");
            Thread.Sleep(8000);
        }
    }

    private static string? GetPassword(Args a)
    {
        if (a.PasswordEnv.Length > 0)
        {
            string? pw = Environment.GetEnvironmentVariable(a.PasswordEnv);
            if (string.IsNullOrEmpty(pw)) Console.Error.WriteLine($"environment variable {a.PasswordEnv} is not set");
            return string.IsNullOrEmpty(pw) ? null : pw;
        }
        string? saved = LauncherAccounts.TryGetPassword(a.Account, a.Host, a.Port, out string problem);
        if (saved == null) Console.Error.WriteLine($"launcher account: {problem}");
        return saved;
    }

    private static int RunOnce(Args a, out bool accountInUse)
    {
        accountInUse = false;
        string? password = GetPassword(a);
        if (password == null) return 2;

        using UdpLink link = UdpLink.Open(a.Host, a.Port);
        using StreamWriter? capture = a.CapturePath != null ? new StreamWriter(a.CapturePath) : null;
        var captureClock = System.Diagnostics.Stopwatch.StartNew();
        if (capture != null)
        {
            capture.WriteLine("# rynth-headless capture: <ms> <C|S> <hex datagram>. The LoginRequest (it holds the password) is left out.");
            link.Tap = (outbound, d) =>
            {
                if (outbound && d.Length >= 8 && (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4)) & 0x00010000) != 0)
                    return;
                capture.WriteLine($"{captureClock.ElapsedMilliseconds} {(outbound ? 'C' : 'S')} {Convert.ToHexString(d)}");
            };
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string T() => $"[{clock.Elapsed.TotalSeconds,7:F2}]";
        var session = ClientSession.Connect(link, new SessionOptions { Account = a.Account, Character = a.Character, PickFirstCharacter = a.Character == null });
        string? acFolder = RynthCore.Headless.Dat.DatFile.FindAcFolder();
        using var geometry = acFolder != null ? RynthCore.Headless.Movement.GeometryWalk.Open(acFolder) : null;
        if (geometry != null)
        {
            session.Walker = geometry;
            geometry.TrackDoors(session.World);
        }

        // Plugins (P3): RynthCore plugins hosted headless, their data under --suite-dir.
        RynthCore.Headless.Host.PluginHost? plugins = null;
        RynthCore.Headless.Host.HostBridge? bridge = null;
        StreamWriter? pluginLog = null;
        if (a.Plugins.Count > 0)
        {
            Directory.CreateDirectory(a.SuiteDir);
            Environment.SetEnvironmentVariable("RYNTHSUITE_DIR", a.SuiteDir);
            pluginLog = new StreamWriter(Path.Combine(a.SuiteDir, "headless-plugins.log"), append: true) { AutoFlush = true };
            RynthCore.Headless.Dat.SpellTable? spells = acFolder != null ? RynthCore.Headless.Dat.SpellTable.Load(acFolder, out _) : null;
            var ctx = new RynthCore.Headless.Host.HostContext(session, a.Account, geometry, spells);
            ctx.LogSink = line =>
            {
                pluginLog.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
                if (line.StartsWith("[host]") || a.ShowPluginLog) Console.WriteLine($"{T()} {line}");
            };
            ctx.TraceActions = true;
            session.ActionSent += (type, arg) => { if (type != 0xF753) ctx.Log($"[act] sent 0x{type:X4} 0x{arg:X8}"); };
            session.World.Error += (code, text) => ctx.Log($"[act] WeenieError 0x{code:X} {text}");
            session.World.InventoryFailed += (item, err) => ctx.Log($"[act] InventoryServerSaveFailed 0x{item:X8} 0x{err:X}");
            session.World.ContainerOpened += id => ctx.Log($"[act] ViewContents 0x{id:X8}: {session.World.Contents(id).Count()} items ({string.Join(", ", session.World.Contents(id).Take(6).Select(o => o.Name.Length > 0 ? o.Name : $"0x{o.Id:X8}"))})");
            plugins = new RynthCore.Headless.Host.PluginHost(ctx);
            foreach (string pl in a.Plugins)
                Console.WriteLine($"{T()} plugin {Path.GetFileName(pl)}: {(plugins.Load(pl) ? "loaded" : "FAILED")}");
            bridge = new RynthCore.Headless.Host.HostBridge(ctx, plugins);
                    }
        long nextCommand = -1, nextEnd = 0;
        int endIndex = 0;
        int commandIndex = 0;
        Console.WriteLine(geometry != null ? $"walking on the dats in {acFolder}" : "no dats found: walking flat (no terrain, walls or cells)");

        session.Log += s => Console.WriteLine($"{T()} {s}");
        session.StateChanged += s => Console.WriteLine($"{T()} state: {s}");
        session.Chat += c => Console.WriteLine($"{T()} chat[{c.ChatType:X2}] {(c.Sender.Length > 0 ? c.Sender + ": " : "")}{c.Text}");
        int objects = 0;
        session.ObjectCreated += o =>
        {
            objects++;
            if (a.PrintObjects || objects <= 25)
                Console.WriteLine($"{T()} + 0x{o.Id:X8} {o.Name} (wcid {o.WeenieClassId}, cell 0x{o.Cell:X8})");
            else if (objects == 26)
                Console.WriteLine($"{T()} ... (more objects; --objects prints all)");
        };

        bool stop = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop = true; };

        Console.WriteLine($"{T()} connecting to {a.Host}:{a.Port} as {a.Account}");
        session.Start(password);
        password = null;

        long inWorldAt = -1;
        bool said = false, loggingOut = false, scenarioOk = true;
        while (!session.Finished)
        {
            link.Wait(15);
            session.Tick();
            bridge?.Tick();
            // --command lines go to the plugins (as if typed in the chat bar) once in the world, 2 s apart.
            if (plugins != null && session.State == SessionState.InWorld && commandIndex < a.Commands.Count)
            {
                if (nextCommand < 0) nextCommand = clock.ElapsedMilliseconds + 4000;
                if (clock.ElapsedMilliseconds >= nextCommand)
                {
                    string cmd = a.Commands[commandIndex++];
                    // As if typed: plugins first, then on to the server (same route as the API's InvokeChatParser).
                    bool sent = plugins.Context.ActInvokeChatParser(cmd);
                    Console.WriteLine($"{T()} command {cmd}: {(sent ? "handled" : "refused")}");
                    nextCommand = clock.ElapsedMilliseconds + 2000;
                }
            }

            if (session.State == SessionState.InWorld && inWorldAt < 0)
                inWorldAt = clock.ElapsedMilliseconds;
            if (a.Scenario != null && inWorldAt >= 0 && !said && clock.ElapsedMilliseconds - inWorldAt > 2000 && session.State == SessionState.InWorld)
            {
                said = true;
                if (a.Scenario == "probe")
                {
                    // Debug: say each --say line and list what the server sends for 6 s after it.
                    foreach (string line in a.Say.Split('|'))
                    {
                        var ops = new List<string>();
                        Action<uint, ReadOnlyMemory<byte>> tap = (op, m) =>
                        {
                            uint ev = op == 0xF7B0 && m.Length >= 16 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(m.Span.Slice(12)) : 0;
                            if (ev != 0x1B4) ops.Add(op == 0xF7B0 ? $"ev{ev:X}" : $"{op:X}" + (m.Length >= 8 && op is 0xF745 or 0xF748 or 0xF74C or 0xF747 ? $"[{System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(m.Span.Slice(4)):X8}]" : ""));
                        };
                        session.RawMessage += tap;
                        Console.WriteLine($"{T()} probe: {line}");
                        if (line == "!pos" && session.World.PlayerObject?.Position is { } here) session.SendPosition(here);
                        else if (line.StartsWith("!drop ")) session.Drop(Convert.ToUInt32(line.Substring(6), 16));
                        else if (line == "!equipped")
                            foreach (var (eid, loc) in session.World.Player.EquippedItems)
                                Console.WriteLine($"{T()} equipped 0x{eid:X8} {session.World.Get(eid)?.Name} at 0x{loc:X}");
                        else if (line.StartsWith("!unwield ")) session.MoveItem(Convert.ToUInt32(line.Substring(9), 16), session.PlayerId, 0);
                        else if (line.StartsWith("!wield "))
                        {
                            string[] w = line.Split(' ');
                            session.Wield(Convert.ToUInt32(w[1], 16), Convert.ToUInt32(w[2], 16));
                        }
                        else if (line.StartsWith("!walk ") && session.World.PlayerObject?.Position is { } start)
                        {
                            // "!walk north east": metres relative to where we stand.
                            string[] d = line.Split(' ');
                            float n = float.Parse(d[1]), e = float.Parse(d[2]);
                            session.MoveFinished += (ok, why) => Console.WriteLine($"{T()} walk finished: {ok} ({why}), run rate {session.World.PlayerRunRate?.ToString("F2") ?? "?"}");
                            session.WalkTo(RynthCore.Headless.Session.ClientSession.Relocate(start, start.X + e, start.Y + n));
                        }
                        else session.Say(line);
                        long until = clock.ElapsedMilliseconds + 6000;
                        long nextTrace = 0;
                        while (clock.ElapsedMilliseconds < until)
                        {
                            link.Wait(15);
                            session.Tick();
                            if (session.Moving && clock.ElapsedMilliseconds >= nextTrace && session.World.PlayerObject?.Position is { } tp)
                            {
                                nextTrace = clock.ElapsedMilliseconds + 500;
                                Console.WriteLine($"{T()}   at 0x{tp.Cell:X8} {tp.X:F1} {tp.Y:F1} {tp.Z:F1} speed {session.MoveSpeed:F1}");
                            }
                        }
                        session.RawMessage -= tap;
                        Console.WriteLine($"{T()} probe got: {string.Join(" ", ops)}");
                    }
                }
                else if (a.Scenario == "p2")
                {
                    if (geometry == null) { Console.WriteLine("p2 needs the AC dats"); scenarioOk = false; }
                    else scenarioOk = new ScenarioP2(session, link, geometry, s => Console.WriteLine($"{T()} {s}")).Run();
                }
                else if (a.Scenario != "p1") { Console.WriteLine($"unknown scenario {a.Scenario}"); scenarioOk = false; }
                else scenarioOk = new ScenarioP1(session, link, s => Console.WriteLine($"{T()} {s}")).Run();
                loggingOut = true;
                Console.WriteLine($"{T()} logging out");
                session.Logout();
            }
            if (inWorldAt >= 0 && !said && clock.ElapsedMilliseconds - inWorldAt > 3000 && session.State == SessionState.InWorld)
            {
                if (a.Say.Length > 0)
                {
                    Console.WriteLine($"{T()} saying: {a.Say}");
                    session.Say(a.Say);
                }
                said = true;
            }
            bool timeUp = inWorldAt >= 0 && clock.ElapsedMilliseconds - inWorldAt > a.StaySeconds * 1000L;
            if ((timeUp || stop) && !loggingOut && plugins != null && endIndex < a.EndCommands.Count && session.State == SessionState.InWorld)
            {
                // --end-command lines (e.g. "/ra stop", a teleport home) go out first, 3 s apart.
                if (clock.ElapsedMilliseconds >= nextEnd)
                {
                    string cmd = a.EndCommands[endIndex++];
                    Console.WriteLine($"{T()} end command {cmd}: {(plugins.Context.ActInvokeChatParser(cmd) ? "handled" : "refused")}");
                    nextEnd = clock.ElapsedMilliseconds + 3000;
                }
                continue;
            }
            if ((timeUp || stop) && !loggingOut && clock.ElapsedMilliseconds >= nextEnd)
            {
                loggingOut = true;
                Console.WriteLine($"{T()} logging out");
                session.Logout();
            }
        }

        Console.WriteLine($"{T()} done: {session.State}{(session.FailReason != null ? " - " + session.FailReason : "")}");
        Console.WriteLine($"{T()} objects seen: {session.ObjectsSeen}; {session.Stats}");
        plugins?.Dispose();
        pluginLog?.Dispose();
        accountInUse = session.FailReason == "character error 0x1";
        return session.State == SessionState.Closed && session.FailReason == null && scenarioOk ? 0 : 1;
    }

    private sealed class Args
    {
        public string Host = "", Account = "", PasswordEnv = "", Say = "Hello from RynthCore.Headless";
        public string? Character, CapturePath, Scenario;
        public readonly List<string> Plugins = new(), Commands = new(), EndCommands = new();
        public string SuiteDir = HeadlessDefaults.SuiteDir;
        public bool ShowPluginLog, ShowChat;
        public int Port = 9000, StaySeconds = 15;
        public bool PrintObjects;
    }

    private static Args? ParseArgs(string[] args)
    {
        var a = new Args();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--host": a.Host = Next(); break;
                case "--port": a.Port = int.Parse(Next()); break;
                case "--account": a.Account = Next(); break;
                case "--password-env": a.PasswordEnv = Next(); break;
                case "--launcher-account": a.Account = Next(); a.PasswordEnv = ""; break;
                case "--character": a.Character = Next(); break;
                case "--say": a.Say = Next(); break;
                case "--stay": a.StaySeconds = int.Parse(Next()); break;
                case "--objects": a.PrintObjects = true; break;
                case "--capture": a.CapturePath = Next(); break;
                case "--scenario": a.Scenario = Next(); break;
                case "--plugin": a.Plugins.Add(Next()); break;
                case "--suite-dir": a.SuiteDir = Next(); break;
                case "--command": a.Commands.Add(Next()); break;
                case "--end-command": a.EndCommands.Add(Next()); break;
                case "--plugin-log": a.ShowPluginLog = true; break;
                default: return null;
            }
        }
        return a.Host.Length > 0 && a.Account.Length > 0 ? a : null;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("       rynth-headless run --config FILE   (several characters, see RunConfig.cs)");
        Console.Error.WriteLine("usage: rynth-headless --host HOST [--port 9000] (--account NAME --password-env VAR | --launcher-account NAME)");
        Console.Error.WriteLine("                      [--character NAME] [--say TEXT] [--stay SECONDS] [--objects] [--capture FILE] [--scenario p1|p2|probe]");
        Console.Error.WriteLine("  --launcher-account uses the password saved in the RynthCore launcher for that account on HOST:PORT.");
        Console.Error.WriteLine("  Without --character the first character on the account is used.");
        return 2;
    }
}

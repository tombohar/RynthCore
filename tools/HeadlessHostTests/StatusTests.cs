// ============================================================================
//  StatusTests - the per-character status files and command inbox (StatusExporter) and
//  the phone's hold-to-move rules (RemoteMove), in a temp folder.
// ============================================================================

using System.Text.Json;
using RynthCore.Headless.Host.Status;

namespace HeadlessHostTests;

internal static class StatusTests
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() => Suites.Add("status", Run, order: 50);

    private static void Run()
    {
        Move();
        Exporter();
    }

    private static void Move()
    {
        var m = new RemoteMove();
        T.Check(RemoteMove.TryParseStart("forward", out string d, out long? dm) && d == "forward" && dm == null, "legacy start: a bare direction");
        T.Check(RemoteMove.TryParseStart("{\"dir\":\"Right\",\"deadmanMs\":800}", out d, out dm) && d == "right" && dm == 800, "protocol 2 start");
        T.Check(RemoteMove.TryParseStart("{\"dir\":\"left\"}", out _, out dm) && dm == RemoteMove.DefaultDeadMs, "protocol 2 start: default dead-man");
        T.Check(!RemoteMove.TryParseStart("{\"dir\":\"up\"}", out _, out _), "an unknown direction is refused");
        T.Check(RemoteMove.TryParseKeepAlive("{\"dirs\":[\"forward\",\"jump\",\"FORWARD\"]}", out var dirs) && dirs.Count == 1, "keep-alive: unknown and repeated names dropped");
        T.Eq(RemoteMove.ParseStop("garbage"), "stop", "an unreadable stop stops everything");

        // Legacy: 2 s window, re-armed by a repeated start.
        T.Eq(m.Start("forward", null, 0).Count, 1, "start presses once");
        T.Eq(m.Start("forward", null, 1500).Count, 0, "a repeated start doesn't press again");
        m.Tick(3000, out bool fired);
        T.Check(!fired, "re-armed: still held at 3.0 s");
        m.Tick(3600, out fired);
        T.Check(fired && m.Held.Count == 0, "legacy dead-man at 2 s after the last start");

        // Protocol 2: keep-alives hold it, a keep-alive naming less lets go, the dead-man fires, resume.
        m.Start("forward", 600, 10_000);
        m.Start("left", 600, 10_000);
        m.KeepAlive(new[] { "forward", "left" }, 10_500);
        m.Tick(11_000, out fired);
        T.Check(!fired && m.Held.Count == 2, "keep-alive refreshes the dead-man");
        var acts = m.KeepAlive(new[] { "forward" }, 11_100);
        T.Check(acts.Count == 1 && acts[0] == ("left", false), "a keep-alive that no longer names a direction lets go of it");
        m.Tick(11_800, out fired);
        T.Check(fired && m.Held.Count == 0 && m.KeepAliveMode, "no keep-alive for 600 ms: dead-man");
        acts = m.KeepAlive(new[] { "forward" }, 12_500);
        T.Check(acts.Count == 1 && acts[0] == ("forward", true), "a keep-alive soon after resumes what the dead-man stopped");
        m.Stop("stop");
        T.Eq(m.KeepAlive(new[] { "forward" }, 12_600).Count, 0, "but never after an explicit stop");
    }

    private static void Exporter()
    {
        Fixture f = Fixture.Shared;
        string dir = Path.Combine(Path.GetTempPath(), "rynth-headless-status-" + Guid.NewGuid().ToString("N"));
        try
        {
            int id = StatusExporter.MakeClientId(3);
            T.Check(id > 1_000_000_000 && id % 2 == 1, "client id: odd and above a billion (never a real pid)");
            T.Check(StatusExporter.MakeClientId(4) != id, "a different slot, a different id");
            var x = new StatusExporter(dir, id, Fixture.Account) { ServerName = "ACEmulator", LastCharacter = "+Test Character" };

            x.Tick();
            using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, $"RynthCore.{id}.status.json"))))
            {
                JsonElement r = doc.RootElement;
                T.Check(!r.GetProperty("inWorld").GetBoolean() && r.GetProperty("fps").GetInt32() == 0, "no session yet: not in the world (reads loading)");
                T.Check(r.GetProperty("headless").GetBoolean() && r.GetProperty("hostPid").GetInt32() == Environment.ProcessId, "headless + hostPid");
                T.Eq(r.GetProperty("character").GetString(), "+Test Character", "the configured character until one is seen");
            }

            // A command waiting before the first in-world tick is drained (oldest first) and deleted.
            Directory.CreateDirectory(Path.Combine(dir, "commands"));
            string cmd = Path.Combine(dir, "commands", $"RynthCore.{id}.0000000000000000001-a.cmd.json");
            File.WriteAllText(cmd, "{\"action\":\"moveStart\",\"value\":{\"dir\":\"forward\",\"deadmanMs\":600}}");
            string other = Path.Combine(dir, "commands", "RynthCore.12345.0000000000000000001-a.cmd.json");
            File.WriteAllText(other, "{\"action\":\"moveStart\",\"value\":\"forward\"}");
            x.Context = f.Context;
            x.Tick();
            T.Check(!File.Exists(cmd), "its command file is drained");
            T.Check(File.Exists(other), "another client's command file is left alone");
            T.Check(f.Session.DriveMotion.Moving, "moveStart forward: autorun on");
            File.WriteAllText(cmd, "{\"action\":\"moveStop\",\"value\":\"stop\"}");
            Thread.Sleep(250);
            x.Tick();
            T.Check(!f.Session.DriveMotion.Moving, "moveStop: stopped");

            using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, $"RynthCore.{id}.status.json"))))
            {
                JsonElement r = doc.RootElement;
                T.Eq(r.GetProperty("schema").GetString(), "rynthcore.client-status/1", "status schema");
                T.Eq(r.GetProperty("pid").GetInt32(), id, "pid = the client id");
                T.Eq(r.GetProperty("account").GetString(), Fixture.Account, "account");
                T.Eq(r.GetProperty("server").GetString(), "ACEmulator", "server (the configured name)");
                T.Check(r.GetProperty("inWorld").GetBoolean(), "in the world");
                T.Check(r.TryGetProperty("opos", out JsonElement opos) && opos.GetProperty("cell").GetString()!.Length == 8, "opos with the cell");
                T.Check(r.GetProperty("enchantments").ValueKind == JsonValueKind.Array, "enchantments listed");
                T.Eq(r.GetProperty("moveProto").GetInt32(), 2, "move protocol 2");
            }
            string sheet = Path.Combine(dir, $"RynthCore.{id}.character.json");
            T.Check(File.Exists(sheet), "character sheet written");
            using (var doc = JsonDocument.Parse(File.ReadAllText(sheet)))
            {
                JsonElement r = doc.RootElement;
                T.Eq(r.GetProperty("schema").GetString(), "rynthcore.character/1", "sheet schema");
                T.Eq(r.GetProperty("attributes").GetArrayLength(), 6, "six attributes");
                T.Eq(r.GetProperty("vitals").GetArrayLength(), 3, "three vitals");
                T.Check(r.GetProperty("skills").GetArrayLength() > 0, "trained skills listed");
                T.Check(r.GetProperty("level").GetInt32() > 0, "level");
            }

            x.Retire();
            T.Check(!File.Exists(Path.Combine(dir, $"RynthCore.{id}.status.json")) && !File.Exists(sheet), "retire removes its files");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}

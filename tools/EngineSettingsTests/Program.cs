using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RynthCore.Engine.Plugins;
using static RynthCore.Engine.UI.AcWindowChromePolicy;

namespace RynthCore.Engine
{
    /// <summary>Stub of the engine's log: the lines go to stdout, where the parent test reads them.</summary>
    internal static class RynthLog
    {
        internal static void Plugin(string msg) => Console.WriteLine("[PLG] " + msg);
        internal static void Info(string msg) => Console.WriteLine("[INF] " + msg);
        internal static void Warn(string msg) => Console.WriteLine("[WRN] " + msg);
    }
}

namespace EngineSettingsTests
{
    // Offline tests for saving engine.json. See the csproj.
    internal static class Program
    {
        private static int _fails, _asserts;
        private static void Check(bool cond, string msg)
        {
            _asserts++;
            if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
        }
        private static void Eq<T>(T actual, T expected, string msg) =>
            Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{msg}: expected '{expected}', got '{actual}'");

        private static string _root = "";

        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--child")
                return Child(args.Skip(1).ToArray());

            _root = Path.Combine(Path.GetTempPath(), "rynth-engine-settings-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            try
            {
                Run(nameof(MergeKeepsForeignKeys), MergeKeepsForeignKeys);
                Run(nameof(MergeEmptyOrMissingFile), MergeEmptyOrMissingFile);
                Run(nameof(MergeRefusesUnreadableFile), MergeRefusesUnreadableFile);
                Run(nameof(WriteAtomicCreatesAndReplaces), WriteAtomicCreatesAndReplaces);
                Run(nameof(OwnedKeysMatchEngineSettings), OwnedKeysMatchEngineSettings);
                Run(nameof(SaveKeepsLauncherKeys), SaveKeepsLauncherKeys);
                Run(nameof(SaveRoundTripsOwnedKeys), SaveRoundTripsOwnedKeys);
                Run(nameof(MergeChangedKeysOnly), MergeChangedKeysOnly);
                Run(nameof(StaleClientDoesNotOverwriteOtherClientsKeys), StaleClientDoesNotOverwriteOtherClientsKeys);
                Run(nameof(EverySetterWritesOnlyItsOwnKey), EverySetterWritesOnlyItsOwnKey);
                Run(nameof(NoSaveWithoutAKey), NoSaveWithoutAKey);
                Run(nameof(AutoIdModeRoundTrips), AutoIdModeRoundTrips);
                Run(nameof(SaveWithoutFileWritesOwnedKeys), SaveWithoutFileWritesOwnedKeys);
                Run(nameof(SaveLeavesMalformedFileAlone), SaveLeavesMalformedFileAlone);
                Run(nameof(MaxHotReloadsReadAndKept), MaxHotReloadsReadAndKept);
                Run(nameof(CloseOnServerDisconnectDefaultOnAndSaved), CloseOnServerDisconnectDefaultOnAndSaved);
                Run(nameof(TransitionGateDefaultOnAndSaved), TransitionGateDefaultOnAndSaved);
                Run(nameof(HoldReloadInWorldOptInAndSaved), HoldReloadInWorldOptInAndSaved);
                Run(nameof(IltServerNamesEdit), IltServerNamesEdit);
                Run(nameof(DisplaySettingsDefaultsAndSaved), DisplaySettingsDefaultsAndSaved);
                Run(nameof(BorderlessDecisions), BorderlessDecisions);
                Run(nameof(PlacementDecisions), PlacementDecisions);
                Run(nameof(PlacementFilesArePerClient), PlacementFilesArePerClient);
                Run(nameof(PlacementFileRoundTrips), PlacementFileRoundTrips);
            }
            finally
            {
                try { Directory.Delete(_root, true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine(_fails == 0 && _asserts > 0 ? $"PASS ({_asserts} assertions)" : $"FAIL ({_fails} of {_asserts} assertions failed)");
            return _fails == 0 && _asserts > 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            int before = _fails;
            try { test(); }
            catch (Exception ex) { _fails++; _asserts++; Console.WriteLine($"  [FAIL] {name} threw {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine($"{(_fails == before ? "ok  " : "FAIL")} {name}");
        }

        // ── The real EngineSettings, in a child process ─────────────────────────

        /// <summary>Runs in the child: one EngineSettings call against RYNTHCORE_ENGINE_SETTINGS.</summary>
        private static int Child(string[] args)
        {
            try
            {
                switch (args[0])
                {
                    case "hook-off": EngineSettings.SetUiHookEnabled(args[1], false); break;
                    case "add-plugin": EngineSettings.AddPluginPath(args[1]); break;
                    case "imgui-off": EngineSettings.EnableImGuiBackend = false; break;
                    case "insertrel-on": EngineSettings.InsertReleasesUi = true; break;
                    case "remove-plugin": EngineSettings.RemovePluginPath(int.Parse(args[1])); break;
                    case "stale":
                        // A stale client: it has read the file (EnsureLoaded), another client then edits
                        // the file (args[1] = a JSON object merged over it), and only then does this client
                        // change one setting (args[2..] = a child command) and save.
                        {
                            _ = EngineSettings.TransitionGate;
                            string file = Environment.GetEnvironmentVariable("RYNTHCORE_ENGINE_SETTINGS")!;
                            var theirs = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                            foreach (var kv in System.Text.Json.Nodes.JsonNode.Parse(args[1])!.AsObject().ToList())
                            {
                                theirs.Remove(kv.Key);
                                theirs[kv.Key] = kv.Value?.DeepClone();
                            }
                            File.WriteAllText(file, theirs.ToJsonString());
                            return Child(args.Skip(2).ToArray());
                        }
                    case "dcclose-off": EngineSettings.CloseOnServerDisconnect = false; break;
                    case "dcclose-on": EngineSettings.CloseOnServerDisconnect = true; break;
                    case "tgate-off": EngineSettings.TransitionGate = false; break;
                    case "tgate-on": EngineSettings.TransitionGate = true; break;
                    case "borderless-on": EngineSettings.BorderlessWindowed = true; break;
                    case "borderless-off": EngineSettings.BorderlessWindowed = false; break;
                    case "layout-on": EngineSettings.RememberWindowLayout = true; break;
                    case "layout-off": EngineSettings.RememberWindowLayout = false; break;
                    case "acpos-on": EngineSettings.RememberAcWindowPosition = true; break;
                    case "acpos-off": EngineSettings.RememberAcWindowPosition = false; break;
                    case "hold-on": EngineSettings.HoldReloadInWorld = true; break;
                    case "hold-off": EngineSettings.HoldReloadInWorld = false; break;
                    case "hold-minutes": EngineSettings.HoldReloadInWorldMaxMinutes = int.Parse(args[1]); break;
                    case "autoid":
                        if (!RynthCore.Engine.Compatibility.AutoIdPolicy.TryParseMode(args[1], out var aim)) throw new ArgumentException(args[1]);
                        EngineSettings.AutoIdMode = aim;
                        break;
                    case "ilt-add": Console.WriteLine("Changed=" + EngineSettings.EditIltServerName(args[1], add: true)); break;
                    case "ilt-remove": Console.WriteLine("Changed=" + EngineSettings.EditIltServerName(args[1], add: false)); break;
                    case "dump":
                        Console.WriteLine("AutoIdMode=" + EngineSettings.AutoIdMode);
                        Console.WriteLine("PluginPaths=" + string.Join("|", EngineSettings.PluginPaths));
                        Console.WriteLine("StartTooltip=" + EngineSettings.IsUiHookEnabled("StartTooltip"));
                        Console.WriteLine("DecalBridge=" + EngineSettings.DecalBridgeMode);
                        Console.WriteLine("ImGuiBackend=" + EngineSettings.EnableImGuiBackend);
                        Console.WriteLine("ServerMessageStream=" + EngineSettings.ServerMessageStream);
                        Console.WriteLine("MaxHotReloads=" + EngineSettings.MaxHotReloads);
                        Console.WriteLine("CloseOnServerDisconnect=" + EngineSettings.CloseOnServerDisconnect);
                        Console.WriteLine("TransitionGate=" + EngineSettings.TransitionGate);
                        Console.WriteLine("BorderlessWindowed=" + EngineSettings.BorderlessWindowed);
                        Console.WriteLine("RememberWindowLayout=" + EngineSettings.RememberWindowLayout);
                        Console.WriteLine("RememberAcWindowPosition=" + EngineSettings.RememberAcWindowPosition);
                        Console.WriteLine("HoldReloadInWorld=" + EngineSettings.HoldReloadInWorld);
                        Console.WriteLine("HoldReloadInWorldMaxMinutes=" + EngineSettings.HoldReloadInWorldMaxMinutes);
                        Console.WriteLine("IltServerNames=" + (EngineSettings.IltServerNames is { } n ? string.Join("|", n) : "(none)"));
                        break;
                    default: throw new ArgumentException(args[0]);
                }
                Console.WriteLine("CHILD-OK");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CHILD-THREW {ex.GetType().Name}: {ex.Message}");
                return 2;
            }
        }

        private static (int Exit, string Out) RunChild(string settingsPath, params string[] args)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--child");
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.Environment["RYNTHCORE_ENGINE_SETTINGS"] = settingsPath;
            using Process p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output);
        }

        private static string NewDir(string name)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static JsonElement ReadJson(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.Clone();
        }

        private static string Raw(JsonElement el) => el.GetRawText().Replace(" ", "").Replace("\r", "").Replace("\n", "");

        private static void NoTempFilesLeft(string dir) =>
            Eq(string.Join(",", Directory.GetFiles(dir).Select(Path.GetFileName).Where(n => n != "engine.json")), "", "only engine.json in the folder (no temp file left)");

        // A launcher-written engine.json: the engine's keys plus the launcher's (and an unknown tool's).
        private const string LauncherFile = """
            {
              "PluginPaths": [ "C:\\Games\\RynthSuite\\Plugins\\RynthCore.Plugin.RynthAi.dll" ],
              "EnableImGuiShell": false,
              "EnableImGuiBackend": true,
              "EnableDcompOverlay": true,
              "DecalBridge": "On",
              "DecalInGameImGui": true,
              "LoggingLevel": "Debug",
              "LauncherWindow": { "X": 10, "Y": -4, "Docked": [ "Plugins", { "Tab": 2 } ], "Note": null },
              "SomeNumber": 1.50,
              "Unicode": "Dereth \u00e9 \u2014"
            }
            """;

        private static void SaveKeepsLauncherKeys()
        {
            string dir = NewDir("keeps");
            string path = Path.Combine(dir, "engine.json");
            File.WriteAllText(path, LauncherFile);
            JsonElement before = ReadJson(path);

            var (exit, output) = RunChild(path, "hook-off", "StartTooltip");
            Eq(exit, 0, "child exit code (" + output.Trim() + ")");

            JsonElement after = ReadJson(path);
            foreach (string key in new[] { "EnableDcompOverlay", "LoggingLevel", "LauncherWindow", "SomeNumber", "Unicode" })
            {
                Check(after.TryGetProperty(key, out JsonElement v), $"launcher key {key} kept");
                if (after.TryGetProperty(key, out v))
                    Check(JsonElement.DeepEquals(v, before.GetProperty(key)), $"launcher key {key} value unchanged: {Raw(v)}");
            }
            Eq(after.GetProperty("SomeNumber").GetRawText(), "1.50", "a number keeps its exact text");
            Eq(after.GetProperty("Unicode").GetString(), "Dereth \u00e9 \u2014", "non-ASCII text intact");

            // The engine's own keys: only the one changed (DisabledUiHooks) is written; every other
            // key keeps exactly what the file had (another client's change to those must not be
            // overwritten with this client's stale copy).
            Eq(string.Join(",", after.GetProperty("DisabledUiHooks").EnumerateArray().Select(e => e.GetString())), "StartTooltip", "DisabledUiHooks written");
            Eq(Raw(after.GetProperty("PluginPaths")), Raw(before.GetProperty("PluginPaths")), "PluginPaths kept");
            Eq(after.GetProperty("EnableImGuiShell").GetBoolean(), false, "EnableImGuiShell kept");
            Eq(after.GetProperty("DecalBridge").GetString(), "On", "DecalBridge On kept");
            foreach (JsonProperty p in before.EnumerateObject())
                Check(after.TryGetProperty(p.Name, out JsonElement kept) && JsonElement.DeepEquals(kept, p.Value), $"unchanged key {p.Name} keeps the file's value");
            foreach (string key in new[] { "EnablePlugins", "EnableDatShareHook", "EnableAvaloniaOverlay", "EnableD3D9Hook", "EnableEngine", "EngineHookCount", "EnableHangMinidump", "PreventIdleLogoff" })
                Check(after.TryGetProperty(key, out _) == before.TryGetProperty(key, out _), $"owned key {key} not added or dropped by an unrelated save");

            // No key twice.
            var names = after.EnumerateObject().Select(p => p.Name).ToList();
            Eq(names.Count, names.Distinct(StringComparer.Ordinal).Count(), "no duplicate keys");
            NoTempFilesLeft(dir);

            // A second save (another owned key) still keeps them.
            (exit, output) = RunChild(path, "imgui-off");
            Eq(exit, 0, "second child exit code (" + output.Trim() + ")");
            after = ReadJson(path);
            Eq(after.GetProperty("EnableImGuiBackend").GetBoolean(), false, "EnableImGuiBackend saved");
            Check(after.TryGetProperty("EnableDcompOverlay", out _) && after.TryGetProperty("LauncherWindow", out _), "launcher keys survive a second save");
            Eq(after.GetProperty("DisabledUiHooks").GetArrayLength(), 1, "first save's DisabledUiHooks kept");
        }

        private static void SaveRoundTripsOwnedKeys()
        {
            string dir = NewDir("roundtrip");
            string path = Path.Combine(dir, "engine.json");
            File.WriteAllText(path, LauncherFile.Replace("\"LoggingLevel\"", "\"ServerMessageStream\": false, \"LoggingLevel\""));
            string plugin = Path.Combine(dir, "RynthCore.Plugin.RynthNav.dll");
            Eq(RunChild(path, "add-plugin", plugin).Exit, 0, "add-plugin");
            Eq(RunChild(path, "hook-off", "StartTooltip").Exit, 0, "hook-off");

            var (exit, output) = RunChild(path, "dump");
            Eq(exit, 0, "dump");
            Check(output.Contains("PluginPaths=C:\\Games\\RynthSuite\\Plugins\\RynthCore.Plugin.RynthAi.dll|" + plugin), "plugin paths read back in order: " + output);
            Check(output.Contains("StartTooltip=False"), "the hook is off after a reload");
            Check(output.Contains("DecalBridge=On"), "DecalBridge read back");
            Check(output.Contains("ServerMessageStream=False"), "ServerMessageStream false read back");
            Check(ReadJson(path).TryGetProperty("EnableDcompOverlay", out _), "launcher key still there");
        }

        // EngineSettingsFile.Merge with changedKeys: only the changed keys come from the engine, a changed
        // key at its default (left out of ownedJson) is removed, everything else keeps the file's value.
        private static void MergeChangedKeysOnly()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal) { "A", "B", "C", "D" };
            string existing = """{ "A": 1, "B": 2, "C": 3, "D": 4, "Launcher": { "x": 1 }, "Mystery": [1, 2] }""";

            byte[]? merged = EngineSettingsFile.Merge(existing, Owned("""{ "A": 10, "B": 20, "C": 30 }"""), keys, out string? err, new[] { "B" });
            Check(merged != null && err == null, "merged");
            JsonElement r = JsonDocument.Parse(merged!).RootElement;
            Eq(r.GetProperty("B").GetInt32(), 20, "the changed key B is taken from the engine");
            Eq(r.GetProperty("A").GetInt32(), 1, "A (owned, unchanged) keeps the file's value");
            Eq(r.GetProperty("C").GetInt32(), 3, "C (owned, unchanged) keeps the file's value");
            Eq(r.GetProperty("D").GetInt32(), 4, "D (owned, not written by the engine, unchanged) is kept, not dropped");
            Eq(Raw(r.GetProperty("Launcher")), """{"x":1}""", "an unowned key is kept");
            Eq(Raw(r.GetProperty("Mystery")), "[1,2]", "an unknown key is kept");
            Eq(r.EnumerateObject().Count(), 6, "no key lost or duplicated");

            // A changed key the engine left out (back at its default) is removed.
            merged = EngineSettingsFile.Merge(existing, Owned("""{ "A": 10 }"""), keys, out _, new[] { "D" });
            r = JsonDocument.Parse(merged!).RootElement;
            Check(!r.TryGetProperty("D", out _), "a changed key at its default is removed");
            Eq(r.GetProperty("A").GetInt32(), 1, "...and A still keeps the file's value");

            // A changed key the file doesn't have yet is added.
            merged = EngineSettingsFile.Merge("""{ "A": 1 }""", Owned("""{ "A": 10, "B": 20 }"""), keys, out _, new[] { "B" });
            r = JsonDocument.Parse(merged!).RootElement;
            Eq(r.GetProperty("B").GetInt32(), 20, "a changed key not in the file is added");
            Eq(r.GetProperty("A").GetInt32(), 1, "A untouched");

            // No file (or an empty one): nothing to keep, everything the engine writes goes in.
            foreach (string? none in new[] { null, "", "  " })
            {
                merged = EngineSettingsFile.Merge(none, Owned("""{ "A": 10, "B": 20 }"""), keys, out _, new[] { "B" });
                Eq(Raw(JsonDocument.Parse(merged!).RootElement), """{"A":10,"B":20}""", $"'{none}': the whole settings are written when there is no file");
            }

            // No changedKeys (null): the old behaviour, every owned key rewritten from the engine.
            merged = EngineSettingsFile.Merge(existing, Owned("""{ "A": 10, "B": 20 }"""), keys, out _);
            r = JsonDocument.Parse(merged!).RootElement;
            Eq(r.GetProperty("A").GetInt32(), 10, "without changedKeys A is rewritten");
            Check(!r.TryGetProperty("C", out _), "without changedKeys an owned key the engine left out is dropped");

            // An unreadable file is still refused with changedKeys.
            byte[]? refused = EngineSettingsFile.Merge("{ \"A\": ", Owned("""{ "A": 1 }"""), keys, out string? bad, new[] { "A" });
            Check(refused == null && !string.IsNullOrEmpty(bad), "an unreadable file is refused");
        }

        // The 2026-10-16 bug: a client that never touched a setting wrote its stale copy over another
        // client's change. "stale" = the child has loaded the file, another client edits it, then the child
        // saves a different setting.
        private static void StaleClientDoesNotOverwriteOtherClientsKeys()
        {
            string dir = NewDir("stale");
            string path = Path.Combine(dir, "engine.json");
            File.WriteAllText(path, LauncherFile);

            // The other client turned several settings away from the default (this client holds the defaults).
            const string other = """
                { "TransitionGate": false, "BorderlessWindowed": true, "RememberWindowLayout": false,
                  "RememberAcWindowPosition": true, "HoldReloadInWorld": true, "HoldReloadInWorldMaxMinutes": 45,
                  "CloseOnServerDisconnect": false, "EnableImGuiBackend": false, "InsertReleasesUi": true,
                  "AutoIdMode": "All", "EnableImGuiShell": true, "IltServerNames": [ "Other" ],
                  "FromNewerBuild": { "k": [ 1, 2 ] } }
                """;
            var (exit, output) = RunChild(path, "stale", other, "hook-off", "StartTooltip");
            Eq(exit, 0, "stale child exit code (" + output.Trim() + ")");
            JsonElement after = ReadJson(path);
            using JsonDocument theirsDoc = JsonDocument.Parse(other);
            foreach (JsonProperty p in theirsDoc.RootElement.EnumerateObject())
                Check(after.TryGetProperty(p.Name, out JsonElement v) && JsonElement.DeepEquals(v, p.Value), $"the other client's {p.Name} survives this client's save");

            // The key this client changed is written.
            Eq(string.Join(",", after.GetProperty("DisabledUiHooks").EnumerateArray().Select(e => e.GetString())), "StartTooltip", "this client's own change is written");
            // The launcher's / unknown keys are still there.
            Check(after.TryGetProperty("LauncherWindow", out _) && after.TryGetProperty("EnableDcompOverlay", out _), "launcher keys kept");

            // A stale client changing a key back to its default removes just that key.
            (exit, output) = RunChild(path, "stale", """{ "HoldReloadInWorld": false }""", "tgate-on");
            Eq(exit, 0, "second stale child exit code (" + output.Trim() + ")");
            after = ReadJson(path);
            Check(!after.TryGetProperty("TransitionGate", out _), "TransitionGate changed back to its default is dropped");
            Eq(after.GetProperty("HoldReloadInWorld").GetBoolean(), false, "...and the other client's later HoldReloadInWorld=false is not reset");
            Eq(after.GetProperty("BorderlessWindowed").GetBoolean(), true, "...nor BorderlessWindowed");

            // Both clients change the same key: the later save is the one on disk (nothing to merge).
            (exit, output) = RunChild(path, "stale", """{ "HoldReloadInWorldMaxMinutes": 60 }""", "hold-minutes", "90");
            Eq(exit, 0, "same-key child exit code (" + output.Trim() + ")");
            Eq(ReadJson(path).GetProperty("HoldReloadInWorldMaxMinutes").GetInt32(), 90, "the same key changed in both: the later save wins");

            Eq(DumpValue(RunChild(path, "dump").Out, "BorderlessWindowed"), "True", "a later start reads the other client's value");
            var keys = ReadJson(path).EnumerateObject().Select(p => p.Name).ToList();
            Eq(keys.Count, keys.Distinct(StringComparer.Ordinal).Count(), "no duplicate keys");
            NoTempFilesLeft(dir);
        }

        // Each public setter's Save("Key") must name the key it changed: a wrong or missing name would
        // silently not persist. For every setter: a file with a non-default value for the other keys,
        // the setter run, and only that one key may differ afterwards.
        private static void EverySetterWritesOnlyItsOwnKey()
        {
            string dir = NewDir("setters");
            string path = Path.Combine(dir, "engine.json");
            string plugin = Path.Combine(dir, "RynthCore.Plugin.RynthNav.dll");
            // Sentinels away from every default, so a key written from memory shows up as a difference.
            string seed = LauncherFile.Replace("\"LoggingLevel\"", """
                "EnablePlugins": false, "EnableDatShareHook": false, "EnableAvaloniaOverlay": false, "EnableD3D9Hook": false,
                "EnableEngine": false, "EngineHookCount": 7, "EnableHangMinidump": false, "PreventIdleLogoff": false,
                "DecalStandDown": false, "ServerMessageStream": false, "FlightRecorder": false,
                "EnableLargeAddressAlignmentFix": false, "MaxHotReloads": 7, "ReportEndpoint": "http://x.invalid/",
                "EnablePluginOverlayWindows": false, "LoadAllServerPlugins": true, "IltHosts": [ "h.invalid" ],
                "DisabledUiHooks": [ "Seed" ], "HoldReloadInWorldMaxMinutes": 33, "LoggingLevel"
                """);

            // (child args, the engine.json key that setter owns, expected raw JSON afterwards or null = just "changed")
            var cases = new (string[] Args, string Key, string? Raw)[]
            {
                (new[] { "imgui-off" },                  "EnableImGuiBackend",          "false"),
                (new[] { "dcclose-off" },                "CloseOnServerDisconnect",     "false"),
                (new[] { "hook-off", "StartTooltip" },   "DisabledUiHooks",             "[\"Seed\",\"StartTooltip\"]"),
                (new[] { "insertrel-on" },               "InsertReleasesUi",            "true"),
                (new[] { "borderless-on" },              "BorderlessWindowed",          "true"),
                (new[] { "layout-off" },                 "RememberWindowLayout",        "false"),
                (new[] { "acpos-on" },                   "RememberAcWindowPosition",    "true"),
                (new[] { "hold-on" },                    "HoldReloadInWorld",           "true"),
                (new[] { "hold-minutes", "90" },         "HoldReloadInWorldMaxMinutes", "90"),
                (new[] { "ilt-add", "Frosthaven" },      "IltServerNames",              null),
                (new[] { "autoid", "all" },              "AutoIdMode",                  "\"All\""),
                (new[] { "tgate-off" },                  "TransitionGate",              "false"),
                (new[] { "add-plugin", plugin },         "PluginPaths",                 null),
                (new[] { "remove-plugin", "0" },         "PluginPaths",                 null),
            };
            foreach (var (args, key, raw) in cases)
            {
                File.WriteAllText(path, seed);
                JsonElement before = ReadJson(path);
                var (exit, output) = RunChild(path, args);
                Eq(exit, 0, $"{args[0]}: exit code ({output.Trim()})");
                JsonElement after = ReadJson(path);
                Check(after.TryGetProperty(key, out JsonElement v), $"{args[0]}: {key} written");
                if (raw != null && after.TryGetProperty(key, out v))
                    Eq(Raw(v), raw, $"{args[0]}: {key} value");
                if (after.TryGetProperty(key, out v) && before.TryGetProperty(key, out JsonElement was))
                    Check(!JsonElement.DeepEquals(v, was), $"{args[0]}: {key} actually changed");
                foreach (JsonProperty p in before.EnumerateObject())
                    if (p.Name != key)
                        Check(after.TryGetProperty(p.Name, out JsonElement kept) && JsonElement.DeepEquals(kept, p.Value), $"{args[0]}: other key {p.Name} keeps the file's value");
                foreach (JsonProperty p in after.EnumerateObject())
                    if (p.Name != key)
                        Check(before.TryGetProperty(p.Name, out _), $"{args[0]}: no other key ({p.Name}) added");
            }
        }

        // Source guard: the only argument-less Save() call is the one inside Save(string key). A new
        // setter written as plain Save() would write nothing under the changed-keys rule.
        private static void NoSaveWithoutAKey()
        {
            string source = File.ReadAllText(FindUp(Path.Combine("src", "RynthCore.Engine", "Plugins", "EngineSettings.cs")));
            int bare = Regex.Matches(source, @"^\s*Save\(\);", RegexOptions.Multiline).Count;
            Eq(bare, 1, "EngineSettings.cs: Save() with no key is called only from Save(string key)");
            int keyed = 0;
            foreach (Match m in Regex.Matches(source, @"^\s*Save\(""(\w+)""\);", RegexOptions.Multiline))
            {
                keyed++;
                Check(EngineSettings.OwnedKeys.Contains(m.Groups[1].Value), $"Save(\"{m.Groups[1].Value}\") names an owned key");
            }
            Check(keyed >= 14, $"found the keyed Save calls ({keyed})");
        }

        private static void AutoIdModeRoundTrips()
        {
            string dir = NewDir("autoid");
            string path = Path.Combine(dir, "engine.json");
            File.WriteAllText(path, LauncherFile);
            var (exit, output) = RunChild(path, "dump");
            Eq(exit, 0, "dump");
            Check(output.Contains("AutoIdMode=Limited"), "default is Limited: " + output);

            Eq(RunChild(path, "autoid", "off").Exit, 0, "set off");
            Eq(ReadJson(path).GetProperty("AutoIdMode").GetString(), "Off", "saved as \"Off\"");
            (exit, output) = RunChild(path, "dump");
            Check(output.Contains("AutoIdMode=Off"), "Off read back: " + output);

            Eq(RunChild(path, "autoid", "all").Exit, 0, "set all");
            (exit, output) = RunChild(path, "dump");
            Check(output.Contains("AutoIdMode=All"), "All read back: " + output);

            Eq(RunChild(path, "autoid", "limited").Exit, 0, "back to limited");
            Check(!ReadJson(path).TryGetProperty("AutoIdMode", out _), "the default leaves the key out");
            Check(ReadJson(path).TryGetProperty("EnableDcompOverlay", out _), "launcher key still there");

            File.WriteAllText(path, LauncherFile.Replace("\"LoggingLevel\"", "\"AutoIdMode\": \"bogus\", \"LoggingLevel\""));
            (exit, output) = RunChild(path, "dump");
            Check(output.Contains("AutoIdMode=Limited"), "an unknown value reads as the default: " + output);
        }

        private static void SaveWithoutFileWritesOwnedKeys()
        {
            string dir = NewDir("nofile");
            string path = Path.Combine(dir, "engine.json");
            var (exit, output) = RunChild(path, "hook-off", "CheckTooltip");
            Eq(exit, 0, "child exit code (" + output.Trim() + ")");
            Check(File.Exists(path), "engine.json created");
            JsonElement after = ReadJson(path);
            Eq(after.GetProperty("DisabledUiHooks")[0].GetString(), "CheckTooltip", "the change saved");
            Check(after.EnumerateObject().All(p => EngineSettings.OwnedKeys.Contains(p.Name)), "only engine keys in a new file");
            NoTempFilesLeft(dir);
        }

        private static void SaveLeavesMalformedFileAlone()
        {
            foreach ((string name, string content) in new[]
            {
                ("truncated", "{ \"PluginPaths\": [ \"C:\\\\Games\\\\a.dll\" ], \"EnableDcompOverlay\": tr"),
                ("not-json", "this is not json"),
                ("array-root", "[ 1, 2, 3 ]"),
            })
            {
                string dir = NewDir("bad-" + name);
                string path = Path.Combine(dir, "engine.json");
                File.WriteAllText(path, content);
                byte[] before = File.ReadAllBytes(path);

                var (exit, output) = RunChild(path, "hook-off", "StartTooltip");
                Eq(exit, 0, $"{name}: no crash ({output.Trim()})");
                Check(output.Contains("[WRN] EngineSettings: not saving"), $"{name}: says why it didn't save");
                Check(File.ReadAllBytes(path).SequenceEqual(before), $"{name}: the file is left exactly as it was");
                NoTempFilesLeft(dir);
            }

            // An empty file (an old non-atomic save cut short) holds nothing to lose: it is rewritten.
            string emptyDir = NewDir("bad-empty");
            string emptyPath = Path.Combine(emptyDir, "engine.json");
            File.WriteAllText(emptyPath, "");
            Eq(RunChild(emptyPath, "hook-off", "StartTooltip").Exit, 0, "empty: no crash");
            Eq(ReadJson(emptyPath).GetProperty("DisabledUiHooks")[0].GetString(), "StartTooltip", "empty: rewritten with the change");
        }

        // engine.json "MaxHotReloads" (the loader's hot-reload cap): default 20, a hand-set value
        // (including -1 = no cap) is read and survives the engine's own saves, the default is
        // never written, and a value that isn't a whole number is ignored.
        private static void MaxHotReloadsReadAndKept()
        {
            string dir = NewDir("maxreloads");
            string path = Path.Combine(dir, "engine.json");

            File.WriteAllText(path, LauncherFile);
            var (exit, output) = RunChild(path, "dump");
            Eq(exit, 0, "dump (missing)");
            Eq(DumpValue(output, "MaxHotReloads"), "20", "missing: the default");
            Eq(RunChild(path, "hook-off", "StartTooltip").Exit, 0, "save with the default");
            Check(!ReadJson(path).TryGetProperty("MaxHotReloads", out _), "the default isn't written to the file");

            foreach ((string json, string expected) in new[] { ("5", "5"), ("-1", "-1"), ("0", "0") })
            {
                File.WriteAllText(path, LauncherFile.Replace("\"LoggingLevel\"", $"\"MaxHotReloads\": {json}, \"LoggingLevel\""));
                (exit, output) = RunChild(path, "dump");
                Eq(exit, 0, $"{json}: dump");
                Eq(DumpValue(output, "MaxHotReloads"), expected, $"{json}: read back");
                Eq(RunChild(path, "hook-off", "CheckTooltip").Exit, 0, $"{json}: save");
                Eq(ReadJson(path).GetProperty("MaxHotReloads").GetRawText(), json, $"{json}: kept by the engine's save");
                Eq(DumpValue(RunChild(path, "dump").Out, "MaxHotReloads"), expected, $"{json}: still the same after the save");
            }

            foreach (string bad in new[] { "\"lots\"", "2.5", "true", "null" })
            {
                File.WriteAllText(path, LauncherFile.Replace("\"LoggingLevel\"", $"\"MaxHotReloads\": {bad}, \"LoggingLevel\""));
                (exit, output) = RunChild(path, "dump");
                Eq(exit, 0, $"{bad}: dump");
                Eq(DumpValue(output, "MaxHotReloads"), "20", $"{bad}: ignored, the default");
            }
        }

        // engine.json "CloseOnServerDisconnect" (close the client on AC's Disconnected screen):
        // default on and not written while on; the Settings checkbox turning it off writes false,
        // which a later start reads back; turning it on again drops it from the file; a value
        // that isn't true/false is ignored.
        private static void CloseOnServerDisconnectDefaultOnAndSaved()
        {
            string dir = NewDir("dcclose");
            string path = Path.Combine(dir, "engine.json");

            File.WriteAllText(path, LauncherFile);
            Eq(DumpValue(RunChild(path, "dump").Out, "CloseOnServerDisconnect"), "True", "missing: on");
            Eq(RunChild(path, "hook-off", "StartTooltip").Exit, 0, "save with the default");
            Check(!ReadJson(path).TryGetProperty("CloseOnServerDisconnect", out _), "the default isn't written to the file");

            var (exit, output) = RunChild(path, "dcclose-off");
            Eq(exit, 0, "turn off (" + output.Trim() + ")");
            Eq(ReadJson(path).GetProperty("CloseOnServerDisconnect").GetRawText(), "false", "off is saved");
            Check(ReadJson(path).TryGetProperty("LauncherWindow", out _), "launcher keys kept");
            Eq(DumpValue(RunChild(path, "dump").Out, "CloseOnServerDisconnect"), "False", "off read back at the next start");
            Eq(RunChild(path, "hook-off", "CheckTooltip").Exit, 0, "another save");
            Eq(ReadJson(path).GetProperty("CloseOnServerDisconnect").GetRawText(), "false", "off survives other saves");

            Eq(RunChild(path, "dcclose-on").Exit, 0, "turn on");
            Check(!ReadJson(path).TryGetProperty("CloseOnServerDisconnect", out _), "on again: dropped from the file");
            Eq(DumpValue(RunChild(path, "dump").Out, "CloseOnServerDisconnect"), "True", "on read back");

            foreach (string bad in new[] { "\"no\"", "0", "null" })
            {
                File.WriteAllText(path, LauncherFile.Replace("\"LoggingLevel\"", $"\"CloseOnServerDisconnect\": {bad}, \"LoggingLevel\""));
                Eq(DumpValue(RunChild(path, "dump").Out, "CloseOnServerDisconnect"), "True", $"{bad}: ignored, on");
            }
        }

        // engine.json TransitionGate (2026-10-18): default on, only a hand-set false is written, other saves keep
        // it, a value that is not a boolean is ignored.
        private static void TransitionGateDefaultOnAndSaved()
        {
            string dir = NewDir("tgate");
            string path = Path.Combine(dir, "engine.json");

            File.WriteAllText(path, LauncherFile);
            Eq(DumpValue(RunChild(path, "dump").Out, "TransitionGate"), "True", "missing: on");
            Eq(RunChild(path, "hook-off", "StartTooltip").Exit, 0, "save with the default");
            Check(!ReadJson(path).TryGetProperty("TransitionGate", out _), "the default isn't written to the file");

            var (exit, output) = RunChild(path, "tgate-off");
            Eq(exit, 0, "turn off (" + output.Trim() + ")");
            Eq(ReadJson(path).GetProperty("TransitionGate").GetRawText(), "false", "off is saved");
            Check(ReadJson(path).TryGetProperty("LauncherWindow", out _), "launcher keys kept");
            Eq(DumpValue(RunChild(path, "dump").Out, "TransitionGate"), "False", "off read back at the next start");
            Eq(RunChild(path, "hook-off", "CheckTooltip").Exit, 0, "another save");
            Eq(ReadJson(path).GetProperty("TransitionGate").GetRawText(), "false", "off survives other saves");

            Eq(RunChild(path, "tgate-on").Exit, 0, "turn on");
            Check(!ReadJson(path).TryGetProperty("TransitionGate", out _), "on again: dropped from the file");

            foreach (string bad in new[] { "\"no\"", "0", "null" })
            {
                File.WriteAllText(path, LauncherFile.Replace("\"LoggingLevel\"", $"\"TransitionGate\": {bad}, \"LoggingLevel\""));
                Eq(DumpValue(RunChild(path, "dump").Out, "TransitionGate"), "True", $"{bad}: ignored, on");
            }
        }

        // Settings > Display (2026-10-18): BorderlessWindowed off and RememberAcWindowPosition off by default,
        // RememberWindowLayout on (panels have always been remembered). Only the non-default is written;
        // other saves keep it; a value that is not a boolean is ignored.
        private static void DisplaySettingsDefaultsAndSaved()
        {
            string dir = NewDir("display");
            string path = Path.Combine(dir, "engine.json");
            File.WriteAllText(path, LauncherFile);

            string dump = RunChild(path, "dump").Out;
            Eq(DumpValue(dump, "BorderlessWindowed"), "False", "borderless defaults off");
            Eq(DumpValue(dump, "RememberWindowLayout"), "True", "panel remembering defaults on (unchanged behaviour)");
            Eq(DumpValue(dump, "RememberAcWindowPosition"), "False", "game window position is opt-in");

            Eq(RunChild(path, "hook-off", "StartTooltip").Exit, 0, "save with the defaults");
            foreach (string key in new[] { "BorderlessWindowed", "RememberWindowLayout", "RememberAcWindowPosition" })
                Check(!ReadJson(path).TryGetProperty(key, out _), key + ": the default isn't written to the file");

            (string cmd, string key, string raw, string value)[] cases =
            {
                ("borderless-on", "BorderlessWindowed", "true", "True"),
                ("layout-off", "RememberWindowLayout", "false", "False"),
                ("acpos-on", "RememberAcWindowPosition", "true", "True"),
            };
            foreach (var (cmd, key, raw, value) in cases)
            {
                var (exit, output) = RunChild(path, cmd);
                Eq(exit, 0, cmd + " (" + output.Trim() + ")");
                Eq(ReadJson(path).GetProperty(key).GetRawText(), raw, key + " is saved");
                Check(ReadJson(path).TryGetProperty("LauncherWindow", out _), key + ": launcher keys kept");
                Eq(DumpValue(RunChild(path, "dump").Out, key), value, key + " read back at the next start");
            }
            Eq(RunChild(path, "hook-off", "CheckTooltip").Exit, 0, "another save");
            foreach (var (_, key, raw, _) in cases)
                Eq(ReadJson(path).GetProperty(key).GetRawText(), raw, key + " survives other saves");

            Eq(RunChild(path, "borderless-off").Exit, 0, "borderless back off");
            Eq(RunChild(path, "layout-on").Exit, 0, "layout back on");
            Eq(RunChild(path, "acpos-off").Exit, 0, "position back off");
            foreach (var (_, key, _, _) in cases)
                Check(!ReadJson(path).TryGetProperty(key, out _), key + ": back at the default, dropped from the file");

            foreach (string key in new[] { "BorderlessWindowed", "RememberWindowLayout", "RememberAcWindowPosition" })
                foreach (string bad in new[] { "\"no\"", "0", "null" })
                {
                    File.WriteAllText(path, LauncherFile.Replace("\"LoggingLevel\"", $"\"{key}\": {bad}, \"LoggingLevel\""));
                    string d = RunChild(path, "dump").Out;
                    Eq(DumpValue(d, "BorderlessWindowed"), "False", $"{key}={bad}: borderless stays off");
                    Eq(DumpValue(d, "RememberWindowLayout"), "True", $"{key}={bad}: layout stays on");
                    Eq(DumpValue(d, "RememberAcWindowPosition"), "False", $"{key}={bad}: position stays off");
                }
        }

        // ── AcWindowChromePolicy: borderless windowed ───────────────────────

        private const int Windowed = 0x14CF0000 | WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox;
        private const int Popup = unchecked((int)0x90000000);   // WS_POPUP | WS_VISIBLE: no chrome at all

        private static void BorderlessDecisions()
        {
            // Setting off, a chrome-less window, no marker: it is not ours - no change at all.
            foreach (bool fullscreenLooking in new[] { false, true })
            {
                var d = DecideBorderless(want: false, marker: 0, style: Popup, looksExclusiveFullscreen: fullscreenLooking);
                Eq(d.Action, ChromeAction.None, $"off + chrome-less + no marker (fullscreen-looking={fullscreenLooking}): no change");
            }
            // Setting off, a window with chrome, no marker: nothing either.
            Eq(DecideBorderless(false, 0, Windowed, false).Action, ChromeAction.None, "off + chrome + no marker: no change");

            // Setting off, our marker, chrome still gone: restore, even when the window fills the monitor.
            foreach (bool fullscreenLooking in new[] { false, true })
            {
                int stripped = Windowed & ~BorderChromeMask;
                var d = DecideBorderless(false, marker: Windowed, style: stripped, looksExclusiveFullscreen: fullscreenLooking);
                Eq(d.Action, ChromeAction.Restore, $"off + our marker + stripped (fullscreen-looking={fullscreenLooking}): restore");
                Eq(d.NewStyle & BorderChromeMask, Windowed & BorderChromeMask, "all the chrome bits come back");
                Eq(d.NewStyle & ~BorderChromeMask, stripped & ~BorderChromeMask, "the other style bits stay as they are now");
            }
            // Our marker, but someone already gave the chrome back (or the marker is junk): only drop it.
            Eq(DecideBorderless(false, Windowed, Windowed, false).Action, ChromeAction.ClearMarker, "off + marker + chrome back: marker dropped");
            Eq(DecideBorderless(false, Popup, Popup, false).Action, ChromeAction.ClearMarker, "off + marker without a caption: dropped, not restored");

            // Setting on: strip once and set the marker.
            var strip = DecideBorderless(true, 0, Windowed, false);
            Eq(strip.Action, ChromeAction.Strip, "on + chrome + no marker: strip");
            Eq(strip.NewStyle & BorderChromeMask, 0, "the chrome bits are gone");
            Eq(strip.NewMarker, Windowed, "the marker holds the style from before");
            Check(strip.NewMarker != 0, "the marker is never 0 (0 means 'not ours')");
            // ...and the next tick, with the marker and no chrome: nothing more to do.
            Eq(DecideBorderless(true, strip.NewMarker, strip.NewStyle, false).Action, ChromeAction.None, "on + marker + stripped: nothing more");
            // Chrome that is not ours and not there (external tool, fullscreen): leave it, no marker.
            Eq(DecideBorderless(true, 0, Popup, false).Action, ChromeAction.None, "on + already chrome-less, no marker: leave it");
            Eq(DecideBorderless(true, 0, Popup, true).Action, ChromeAction.None, "on + exclusive fullscreen, no marker: leave it");
            // The game put the chrome back after we stripped: strip again.
            Eq(DecideBorderless(true, Windowed, Windowed, false).Action, ChromeAction.Strip, "on + marker + chrome came back: strip again");
            // Flagged as fullscreen-looking without our marker: left alone.
            Eq(DecideBorderless(true, 0, Windowed, true).Action, ChromeAction.None, "on + no marker + flagged fullscreen: left alone");
        }

        // ── AcWindowChromePolicy: window position ───────────────────────────

        private static void PlacementDecisions()
        {
            var main = new Rect(0, 0, 1920, 1080);
            var second = new Rect(1920, 0, 3840, 1080);
            var one = new[] { main };
            var two = new[] { main, second };

            Eq(EvaluatePlacement(true, false, new Rect(100, 100, 1100, 800), one), PlacementVerdict.Ok, "on-screen rect is fine");
            Eq(EvaluatePlacement(false, false, new Rect(100, 100, 1100, 800), one), PlacementVerdict.OptedOut, "opted out: never");
            Eq(EvaluatePlacement(true, true, new Rect(100, 100, 1100, 800), one), PlacementVerdict.ExclusiveFullscreen, "exclusive fullscreen: skipped");

            // Off-screen: a monitor that is gone, or a title bar above the top edge.
            Eq(EvaluatePlacement(true, false, new Rect(2000, 100, 3000, 800), one), PlacementVerdict.OffScreen, "saved on the second monitor, which is gone: ignored");
            Eq(EvaluatePlacement(true, false, new Rect(2000, 100, 3000, 800), two), PlacementVerdict.Ok, "...but fine while that monitor exists");
            Eq(EvaluatePlacement(true, false, new Rect(-5000, -5000, -4000, -4300), two), PlacementVerdict.OffScreen, "far off every monitor: ignored");
            Eq(EvaluatePlacement(true, false, new Rect(100, -900, 1100, 200), one), PlacementVerdict.OffScreen, "title bar above the screen: ignored");
            Eq(EvaluatePlacement(true, false, new Rect(-32000, -32000, -31840, -31972), two), PlacementVerdict.TooSmall, "a minimised window's rect is refused");

            // Sanity limits.
            Eq(EvaluatePlacement(true, false, new Rect(0, 0, MinWidth - 1, 600), one), PlacementVerdict.TooSmall, "narrower than the minimum");
            Eq(EvaluatePlacement(true, false, new Rect(0, 0, 800, MinHeight - 1), one), PlacementVerdict.TooSmall, "shorter than the minimum");
            Eq(EvaluatePlacement(true, false, new Rect(0, 0, MinWidth, MinHeight), one), PlacementVerdict.Ok, "exactly the minimum is fine");
            Eq(EvaluatePlacement(true, false, new Rect(0, 0, MaxExtent + 1, 600), one), PlacementVerdict.TooLarge, "absurdly wide");

            // Writes only when changed.
            var r = new Rect(10, 20, 810, 620);
            Check(ShouldWritePlacement(PlacementVerdict.Ok, r, null), "first rect is written");
            Check(!ShouldWritePlacement(PlacementVerdict.Ok, r, r), "unchanged rect: no write");
            Check(ShouldWritePlacement(PlacementVerdict.Ok, r with { Right = 900 }, r), "changed rect: written");
            Check(!ShouldWritePlacement(PlacementVerdict.OffScreen, r, null), "a refused rect is never written");
        }

        private static void PlacementFilesArePerClient()
        {
            Eq(ClientKey("DreamWeave", "Drakkon"), "DreamWeave/Drakkon", "key = server/character");
            Eq(ClientKey("", "Drakkon"), "Drakkon", "no server: character alone");
            Eq(ClientKey("DreamWeave", ""), (string?)null, "no character: no key");
            Eq(ClientKey(null, null), (string?)null, "nothing known: no key");

            string a = PlacementFileName("DreamWeave/Drakkon");
            string b = PlacementFileName("DreamWeave/Drakkon1");
            string c = PlacementFileName("Aelrynth/Lucy McJuicy");
            Check(a != b && a != c && b != c, "different clients get different files");
            Eq(PlacementFileName("dreamweave/drakkon"), a, "stable (and case-insensitive) for one client");
            Eq(PlacementFileName(null), SharedFileName, "no key: the shared file");
            Eq(PlacementFileName("  "), SharedFileName, "blank key: the shared file");
            Check(a != SharedFileName && a.StartsWith("ac_window.") && a.EndsWith(".txt"), "per-client name shape: " + a);
            foreach (string name in new[] { a, b, c, PlacementFileName("we/ird:na*me?<>|\"x"), PlacementFileName(new string('x', 500)) })
            {
                Check(name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, "valid file name: " + name);
                Check(name.Length < 90, "short file name: " + name.Length);
            }
            // Names that sanitise to the same letters still differ (the hash).
            Check(PlacementFileName("S/a b") != PlacementFileName("S/a_b"), "'a b' and 'a_b' stay apart");

            // The key is only usable after login; the shared file only after a long wait without one.
            Check(!TryResolveKey("S", "", 1000, 90000, out _), "no character yet, just started: not ready");
            Check(TryResolveKey("S", "", 91000, 90000, out string? fallback) && fallback == null, "no character after the wait: shared file");
            Check(TryResolveKey("S", "Char", 0, 90000, out string? k) && k == "S/Char", "character known: its key at once");
        }

        private static void PlacementFileRoundTrips()
        {
            var r = new Rect(-1280, 40, -280, 840);
            Check(TryParsePlacement(FormatPlacement(r).Split('\n'), out Rect back), "own output parses");
            Eq(back, r, "round trip (negative coordinates, secondary monitor)");
            Check(!TryParsePlacement(new[] { "left=1", "top=2", "width=0", "height=5" }, out _), "zero width: refused");
            Check(!TryParsePlacement(new[] { "width=500", "height=500" }, out _), "no position: refused");
            Check(!TryParsePlacement(new[] { "garbage", "left=x", "" }, out _), "garbage: refused");
            Check(TryParsePlacement(new[] { "# c", "LEFT = 5", "Top=6", "width=300", "height=200", "extra=1" }, out Rect loose) && loose == new Rect(5, 6, 305, 206),
                "case, spaces, comments and unknown keys are tolerated");
        }

        // engine.json HoldReloadInWorld (2026-10-18, opt-in from SilentKelpie's PR 6): default off, only a true
        // is written, other saves keep it, a non-boolean is ignored. HoldReloadInWorldMaxMinutes: default 10,
        // clamped to 1-240, only a non-default value is written, a non-number is ignored.
        private static void HoldReloadInWorldOptInAndSaved()
        {
            string dir = NewDir("holdreload");
            string path = Path.Combine(dir, "engine.json");

            File.WriteAllText(path, LauncherFile);
            string dump = RunChild(path, "dump").Out;
            Eq(DumpValue(dump, "HoldReloadInWorld"), "False", "missing: off");
            Eq(DumpValue(dump, "HoldReloadInWorldMaxMinutes"), "10", "missing: 10 minutes");
            Eq(RunChild(path, "hook-off", "StartTooltip").Exit, 0, "save with the defaults");
            Check(!ReadJson(path).TryGetProperty("HoldReloadInWorld", out _), "off isn't written to the file");
            Check(!ReadJson(path).TryGetProperty("HoldReloadInWorldMaxMinutes", out _), "10 isn't written to the file");

            var (exit, output) = RunChild(path, "hold-on");
            Eq(exit, 0, "turn on (" + output.Trim() + ")");
            Eq(ReadJson(path).GetProperty("HoldReloadInWorld").GetRawText(), "true", "on is saved");
            Check(ReadJson(path).TryGetProperty("LauncherWindow", out _), "launcher keys kept");
            Eq(DumpValue(RunChild(path, "dump").Out, "HoldReloadInWorld"), "True", "on read back at the next start");
            Eq(RunChild(path, "hook-off", "CheckTooltip").Exit, 0, "another save");
            Eq(ReadJson(path).GetProperty("HoldReloadInWorld").GetRawText(), "true", "on survives other saves");

            Eq(RunChild(path, "hold-minutes", "30").Exit, 0, "set 30 minutes");
            Eq(ReadJson(path).GetProperty("HoldReloadInWorldMaxMinutes").GetRawText(), "30", "30 is saved");
            Eq(DumpValue(RunChild(path, "dump").Out, "HoldReloadInWorldMaxMinutes"), "30", "30 read back");
            Eq(RunChild(path, "hold-minutes", "10").Exit, 0, "back to 10");
            Check(!ReadJson(path).TryGetProperty("HoldReloadInWorldMaxMinutes", out _), "10 again: dropped from the file");

            Eq(RunChild(path, "hold-off").Exit, 0, "turn off");
            Check(!ReadJson(path).TryGetProperty("HoldReloadInWorld", out _), "off again: dropped from the file");

            foreach (string bad in new[] { "\"yes\"", "1", "null" })
            {
                File.WriteAllText(path, LauncherFile.Replace("\"LoggingLevel\"", $"\"HoldReloadInWorld\": {bad}, \"LoggingLevel\""));
                Eq(DumpValue(RunChild(path, "dump").Out, "HoldReloadInWorld"), "False", $"{bad}: ignored, off");
            }

            // Hand-set minutes: in range is kept, out of range is clamped to 1-240, junk is the default.
            foreach (var (json, expected) in new[] { ("1", "1"), ("240", "240"), ("0", "1"), ("-5", "1"), ("241", "240"), ("100000", "240"), ("45", "45"), ("\"x\"", "10"), ("12.5", "10"), ("null", "10") })
            {
                File.WriteAllText(path, LauncherFile.Replace("\"LoggingLevel\"", $"\"HoldReloadInWorldMaxMinutes\": {json}, \"LoggingLevel\""));
                Eq(DumpValue(RunChild(path, "dump").Out, "HoldReloadInWorldMaxMinutes"), expected, $"{json}: read as {expected}");
            }
        }

        // /rc server names ilt add|remove (2026.10.5.46): the first edit starts from the defaults,
        // the edit is saved with the launcher's keys kept, and repeats, unknown, empty and overlong
        // patterns change nothing.
        private static void IltServerNamesEdit()
        {
            string dir = NewDir("iltnames");
            string path = Path.Combine(dir, "engine.json");
            File.WriteAllText(path, LauncherFile);
            JsonElement before = ReadJson(path);

            Eq(DumpValue(RunChild(path, "dump").Out, "IltServerNames"), "(none)", "no list in the file");
            Eq(DumpValue(RunChild(path, "ilt-add", "Frosthaven").Out, "Changed"), "True", "add");
            string defaults = string.Join("|", RynthCore.Engine.Compatibility.IltDetect.DefaultServerNames);
            Eq(DumpValue(RunChild(path, "dump").Out, "IltServerNames"), defaults + "|Frosthaven", "the defaults kept, the new one added");
            Eq(Raw(ReadJson(path).GetProperty("LauncherWindow")), Raw(before.GetProperty("LauncherWindow")), "the launcher's keys kept");
            NoTempFilesLeft(dir);

            Eq(DumpValue(RunChild(path, "ilt-add", "FROSTHAVEN").Out, "Changed"), "False", "adding again (any case) changes nothing");
            Eq(DumpValue(RunChild(path, "ilt-remove", "nope").Out, "Changed"), "False", "removing an unknown pattern changes nothing");
            Eq(DumpValue(RunChild(path, "ilt-add", " ").Out, "Changed"), "False", "an empty pattern is refused");
            Eq(DumpValue(RunChild(path, "ilt-add", new string('x', EngineSettings.MaxIltServerNameLength + 1)).Out, "Changed"), "False",
                "an overlong pattern is refused");
            Eq(DumpValue(RunChild(path, "ilt-remove", "frosthaven").Out, "Changed"), "True", "remove (any case)");
            Eq(DumpValue(RunChild(path, "dump").Out, "IltServerNames"), defaults, "back to the defaults");
        }

        /// <summary>The value of a "Name=value" line of the child's dump output ("" if none).</summary>
        private static string DumpValue(string output, string name)
        {
            foreach (string line in output.Split('\n'))
            {
                string l = line.TrimEnd('\r');
                if (l.StartsWith(name + "=", StringComparison.Ordinal)) return l.Substring(name.Length + 1);
            }
            return "";
        }

        // ── EngineSettingsFile, direct ──────────────────────────────────────────

        private static byte[] Owned(string json) => Encoding.UTF8.GetBytes(json);
        private static readonly HashSet<string> Keys = new(StringComparer.Ordinal) { "A", "B", "Optional" };

        private static void MergeKeepsForeignKeys()
        {
            string existing = """{ "A": 1, "Foreign": { "x": [1, {"y": null}] }, "Optional": true, "B": "old", "Other": "kept", "Foreign": 99 }""";
            byte[]? merged = EngineSettingsFile.Merge(existing, Owned("""{ "A": 2, "B": "new" }"""), Keys, out string? err);
            Check(merged != null && err == null, "merged");
            JsonElement r = JsonDocument.Parse(merged!).RootElement;
            Eq(string.Join(",", r.EnumerateObject().Select(p => p.Name)), "A,B,Foreign,Other", "ours first in our order, then the others in file order, each once");
            Eq(r.GetProperty("A").GetInt32(), 2, "owned A updated");
            Eq(r.GetProperty("B").GetString(), "new", "owned B updated");
            Eq(Raw(r.GetProperty("Foreign")), """{"x":[1,{"y":null}]}""", "nested foreign object kept whole (first of a duplicate key)");
            Check(!r.TryGetProperty("Optional", out _), "an owned key not written this time is left out");

            // A key we write but forgot to list as owned is still written once (ours wins).
            byte[]? m2 = EngineSettingsFile.Merge("""{ "New": "file" }""", Owned("""{ "New": "ours" }"""), Keys, out _);
            JsonElement r2 = JsonDocument.Parse(m2!).RootElement;
            Eq(r2.EnumerateObject().Count(), 1, "unlisted key written once");
            Eq(r2.GetProperty("New").GetString(), "ours", "ours wins");

            // Keys are case-sensitive, like the engine's reads: "a" is not "A".
            byte[]? m3 = EngineSettingsFile.Merge("""{ "a": 5 }""", Owned("""{ "A": 1 }"""), Keys, out _);
            Eq(JsonDocument.Parse(m3!).RootElement.GetProperty("a").GetInt32(), 5, "a differently-cased key is someone else's");
        }

        private static void MergeEmptyOrMissingFile()
        {
            foreach (string? existing in new[] { null, "", "  \r\n " })
            {
                byte[]? merged = EngineSettingsFile.Merge(existing, Owned("""{ "A": 1 }"""), Keys, out string? err);
                Check(merged != null && err == null, $"'{existing}': merged");
                Eq(Raw(JsonDocument.Parse(merged!).RootElement), """{"A":1}""", $"'{existing}': only ours");
            }
        }

        private static void MergeRefusesUnreadableFile()
        {
            foreach (string bad in new[] { "{ \"A\": ", "nope", "[1]", "\"text\"", "null", "{ \"A\": 1 } trailing" })
            {
                byte[]? merged = EngineSettingsFile.Merge(bad, Owned("""{ "A": 1 }"""), Keys, out string? err);
                Check(merged == null && !string.IsNullOrEmpty(err), $"'{bad}': refused with a reason");
            }
        }

        private static void WriteAtomicCreatesAndReplaces()
        {
            string dir = NewDir("atomic");
            string path = Path.Combine(dir, "engine.json");
            EngineSettingsFile.WriteAtomic(path, Encoding.UTF8.GetBytes("{\"v\":1}"));
            Eq(File.ReadAllText(path), "{\"v\":1}", "created");
            EngineSettingsFile.WriteAtomic(path, Encoding.UTF8.GetBytes("{\"v\":2}"));
            Eq(File.ReadAllText(path), "{\"v\":2}", "replaced");
            NoTempFilesLeft(dir);

            // A file another process holds open without delete sharing: the swap fails after its
            // retries, the old content stays whole, and the temp file is cleaned up.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool threw = false;
                try { EngineSettingsFile.WriteAtomic(path, Encoding.UTF8.GetBytes("{\"v\":3}")); }
                catch (IOException) { threw = true; }
                catch (UnauthorizedAccessException) { threw = true; }
                Check(threw, "a locked file makes the save fail (the caller logs it)");
            }
            Eq(File.ReadAllText(path), "{\"v\":2}", "locked: old content intact");
            NoTempFilesLeft(dir);
        }

        // Every key EngineSettings.cs reads or writes must be in OwnedKeys (and the other way round):
        // a new setting left off the list would be kept from the old file instead of saved.
        private static void OwnedKeysMatchEngineSettings()
        {
            string source = File.ReadAllText(FindUp(Path.Combine("src", "RynthCore.Engine", "Plugins", "EngineSettings.cs")));
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(source, @"TryGetProperty\(""(\w+)"""))
                used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(source, @"\bw\.Write\w*\(""(\w+)"""))
                used.Add(m.Groups[1].Value);
            Check(used.Count >= 16, $"found the keys in EngineSettings.cs ({used.Count})");
            foreach (string key in used)
                Check(EngineSettings.OwnedKeys.Contains(key), $"EngineSettings.OwnedKeys lists \"{key}\"");
            foreach (string key in EngineSettings.OwnedKeys)
                Check(used.Contains(key), $"OwnedKeys \"{key}\" is read or written by EngineSettings.cs");
        }

        private static string FindUp(string relative)
        {
            for (DirectoryInfo? d = new(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                string p = Path.Combine(d.FullName, relative);
                if (File.Exists(p)) return p;
            }
            throw new FileNotFoundException(relative);
        }
    }
}

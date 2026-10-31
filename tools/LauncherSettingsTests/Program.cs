using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RynthCore.App;

namespace LauncherSettingsTests;

/// <summary>
/// The launcher's shared settings with "Decal + RynthCore" accounts, in a temp folder.
/// "Old launcher" = a launcher released before the Decal bridge: its InjectionMode has only
/// RynthCore and Decal, read with JsonStringEnumConverter, so "DecalBridge" makes the whole
/// appsettings.json unreadable for it.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _dir = "";

    private static string Main_ => Path.Combine(_dir, "appsettings.json");
    private static string Bak => Main_ + ".bak";
    private static string Sidecar => Path.Combine(_dir, DecalAccountModes.FileName);

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "rc-launcher-settings-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Scenario("negative control: the old launcher can't read DecalBridge", root, () =>
            {
                File.WriteAllText(Main_, """{ "AccountProfiles": [ { "Id": "b", "InjectionMode": "DecalBridge" } ] }""");
                Check(!OldLauncherCanRead(Main_), "old reader rejects \"DecalBridge\" (so the checks below mean something)");
            });

            Scenario("save and load", root, () =>
            {
                AppSettings s = Sample();
                AppSettingsStore.Save(s);
                AppSettingsStore.Save(s);   // second save: a .bak exists too
                CheckSharedFilesSafe();
                Check(SharedMode("a") == "RynthCore", "a is RynthCore in appsettings.json");
                Check(SharedMode("b") == "RynthCore", "b (Decal + RynthCore) is written as RynthCore");
                Check(SharedMode("c") == "Decal", "c is Decal in appsettings.json");
                Check(SidecarIds().SequenceEqual(new[] { "b" }), "decal-accounts.json lists only b");
                Check(s.AccountProfiles[1].InjectionMode == InjectionMode.DecalBridge, "save leaves the in-memory mode alone");

                AppSettings loaded = AppSettingsStore.Load();
                Check(Mode(loaded, "a") == InjectionMode.RynthCore, "load: a RynthCore");
                Check(Mode(loaded, "b") == InjectionMode.DecalBridge, "load: b Decal + RynthCore");
                Check(Mode(loaded, "c") == InjectionMode.Decal, "load: c Decal");
                Check(AppSettingsStore.LastLoadDiagnostic == null && AppSettingsStore.LastMigrationNote == null, "load: no diagnostics");
            });

            Scenario("switch back to RynthCore", root, () =>
            {
                AppSettings s = Sample();
                AppSettingsStore.Save(s);
                s.AccountProfiles[1].InjectionMode = InjectionMode.RynthCore;
                AppSettingsStore.Save(s);
                Check(!File.Exists(Sidecar), "decal-accounts.json removed when no account uses the mode");
                Check(Mode(AppSettingsStore.Load(), "b") == InjectionMode.RynthCore, "load: b RynthCore");
            });

            Scenario("migrate a file written by a pre-release bridge build", root, () =>
            {
                string legacy = """
                { "EnginePath": "X", "AccountProfiles": [
                  { "Id": "a", "AccountName": "A", "InjectionMode": "RynthCore" },
                  { "Id": "b", "AccountName": "B", "InjectionMode": "DecalBridge" },
                  { "Id": "d", "AccountName": "D", "InjectionMode": 2 } ] }
                """;
                File.WriteAllText(Main_, legacy);
                File.WriteAllText(Bak, legacy);
                AppSettings loaded = AppSettingsStore.Load();
                Check(Mode(loaded, "b") == InjectionMode.DecalBridge, "b kept as Decal + RynthCore");
                Check(Mode(loaded, "d") == InjectionMode.DecalBridge, "d (numeric 2) kept as Decal + RynthCore");
                Check(loaded.EnginePath == "X", "other settings kept");
                Check(AppSettingsStore.LastMigrationNote?.Contains("Moved 2") == true, "migration note: " + AppSettingsStore.LastMigrationNote);
                CheckSharedFilesSafe();
                Check(SidecarIds().SequenceEqual(new[] { "b", "d" }), "decal-accounts.json lists b and d");

                AppSettingsStore.Load();
                Check(AppSettingsStore.LastMigrationNote == null, "second load: nothing left to migrate");
            });

            Scenario("an old launcher switches the account to Decal", root, () =>
            {
                AppSettingsStore.Save(Sample());
                OldLauncherEdit(accounts => SetMode(accounts, "b", "Decal"));
                AppSettings loaded = AppSettingsStore.Load();
                Check(Mode(loaded, "b") == InjectionMode.Decal, "the old launcher's choice wins");
                AppSettingsStore.Save(loaded);
                Check(!File.Exists(Sidecar), "the stale entry is dropped at the next save");
            });

            Scenario("an old launcher deletes the account", root, () =>
            {
                AppSettingsStore.Save(Sample());
                OldLauncherEdit(accounts =>
                {
                    for (int i = accounts.Count - 1; i >= 0; i--)
                        if ((string?)accounts[i]!["Id"] == "b") accounts.RemoveAt(i);
                });
                AppSettings loaded = AppSettingsStore.Load();
                Check(loaded.AccountProfiles.All(a => a.Id != "b"), "b is gone");
                AppSettingsStore.Save(loaded);
                Check(!File.Exists(Sidecar), "its entry is dropped at the next save");
            });

            Scenario("an old launcher saves without changes", root, () =>
            {
                AppSettingsStore.Save(Sample());
                OldLauncherEdit(_ => { });
                Check(Mode(AppSettingsStore.Load(), "b") == InjectionMode.DecalBridge, "b is still Decal + RynthCore");
            });

            Scenario("unreadable decal-accounts.json", root, () =>
            {
                AppSettingsStore.Save(Sample());
                File.WriteAllText(Sidecar, "{ not json");
                AppSettings loaded = AppSettingsStore.Load();
                Check(Mode(loaded, "b") == InjectionMode.RynthCore, "b runs as RynthCore");
                Check(Mode(loaded, "c") == InjectionMode.Decal, "other accounts unchanged");
                Check(AppSettingsStore.LastLoadDiagnostic == null, "appsettings.json not taken for corrupt");
                Check(AppSettingsStore.LastMigrationNote?.Contains("could not be read") == true, "note: " + AppSettingsStore.LastMigrationNote);
                loaded.AccountProfiles.First(a => a.Id == "b").InjectionMode = InjectionMode.DecalBridge;
                AppSettingsStore.Save(loaded);
                Check(SidecarIds().SequenceEqual(new[] { "b" }), "picking the mode again rewrites the file");
            });

            Scenario("a mode name this build doesn't know", root, () =>
            {
                File.WriteAllText(Main_, """{ "EnginePath": "Y", "AccountProfiles": [ { "Id": "z", "InjectionMode": "SomethingNew" }, { "Id": "y", "InjectionMode": 9 } ] }""");
                AppSettings loaded = AppSettingsStore.Load();
                Check(AppSettingsStore.LastLoadDiagnostic == null && loaded.EnginePath == "Y", "file still loads (not quarantined)");
                Check(Mode(loaded, "z") == InjectionMode.RynthCore && Mode(loaded, "y") == InjectionMode.RynthCore, "unknown modes read as RynthCore");
            });

            Scenario("unchanged list: decal-accounts.json is not rewritten", root, () =>
            {
                AppSettings s = Sample();
                AppSettingsStore.Save(s);
                DateTime old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(Sidecar, old);
                AppSettingsStore.Save(s);
                Check(File.GetLastWriteTimeUtc(Sidecar) == old, "no rewrite when nothing changed");
                s.AccountProfiles[0].InjectionMode = InjectionMode.DecalBridge;
                AppSettingsStore.Save(s);
                Check(SidecarIds().SequenceEqual(new[] { "a", "b" }), "rewritten when the list changed");
                CheckSharedFilesSafe();
            });

            // ── Headless accounts (headless-accounts.json) ──────────────────────
            // Same arrangement as "Decal + RynthCore": appsettings.json says RynthCore, the sidecar
            // says which are Headless. "Bridge launcher" = one that knows DecalBridge but not Headless.

            Scenario("headless: written as RynthCore, listed in headless-accounts.json", root, () =>
            {
                AppSettings s = SampleWithHeadless();
                AppSettingsStore.Save(s);
                AppSettingsStore.Save(s);   // second save: a .bak exists too
                CheckSharedFilesSafe();
                Check(SharedMode("h") == "RynthCore", "h (Headless) is written as RynthCore");
                Check(SharedMode("b") == "RynthCore" && SharedMode("c") == "Decal", "the others as before");
                Check(HeadlessIds().SequenceEqual(new[] { "h" }), "headless-accounts.json lists only h");
                Check(SidecarIds().SequenceEqual(new[] { "b" }), "decal-accounts.json still lists only b");
                Check(s.AccountProfiles.First(a => a.Id == "h").InjectionMode == InjectionMode.Headless, "save leaves the in-memory mode alone");
                Check(BridgeLauncherCanRead(Main_) && BridgeLauncherCanRead(Bak), "a bridge launcher can read appsettings.json and its .bak");
            });

            Scenario("headless: round trip", root, () =>
            {
                AppSettingsStore.Save(SampleWithHeadless());
                AppSettings loaded = AppSettingsStore.Load();
                Check(Mode(loaded, "h") == InjectionMode.Headless, "load: h Headless");
                Check(Mode(loaded, "a") == InjectionMode.RynthCore && Mode(loaded, "b") == InjectionMode.DecalBridge &&
                      Mode(loaded, "c") == InjectionMode.Decal, "load: the others as saved");
                Check(AppSettingsStore.LastLoadDiagnostic == null && AppSettingsStore.LastMigrationNote == null, "load: no diagnostics");

                loaded.AccountProfiles.First(a => a.Id == "h").InjectionMode = InjectionMode.RynthCore;
                AppSettingsStore.Save(loaded);
                Check(!File.Exists(HeadlessSidecar), "headless-accounts.json removed when no account uses the mode");
                Check(SidecarIds().SequenceEqual(new[] { "b" }), "decal-accounts.json untouched");
                Check(Mode(AppSettingsStore.Load(), "h") == InjectionMode.RynthCore, "load: h RynthCore");
            });

            Scenario("headless: older launchers still read the file, and their edits win", root, () =>
            {
                AppSettingsStore.Save(SampleWithHeadless());
                Check(OldLauncherCanRead(Main_), "a launcher from before the Decal bridge can read it");
                Check(BridgeLauncherCanRead(Main_), "a launcher with the Decal bridge but no Headless can read it");

                OldLauncherEdit(_ => { });   // saves without changes (drops what it doesn't know, but not the sidecar)
                Check(Mode(AppSettingsStore.Load(), "h") == InjectionMode.Headless, "an old launcher's save keeps h Headless");

                OldLauncherEdit(accounts => SetMode(accounts, "h", "Decal"));
                AppSettings loaded = AppSettingsStore.Load();
                Check(Mode(loaded, "h") == InjectionMode.Decal, "an old launcher switching h to Decal wins");
                AppSettingsStore.Save(loaded);
                Check(!File.Exists(HeadlessSidecar), "the stale entry is dropped at the next save");
            });

            Scenario("headless: migrate a file that says Headless itself", root, () =>
            {
                string legacy = """
                { "EnginePath": "X", "AccountProfiles": [
                  { "Id": "a", "AccountName": "A", "InjectionMode": "RynthCore" },
                  { "Id": "h", "AccountName": "H", "InjectionMode": "Headless" },
                  { "Id": "n", "AccountName": "N", "InjectionMode": 3 },
                  { "Id": "b", "AccountName": "B", "InjectionMode": "DecalBridge" } ] }
                """;
                File.WriteAllText(Main_, legacy);
                File.WriteAllText(Bak, legacy);
                Check(!BridgeLauncherCanRead(Main_), "negative control: a bridge launcher can't read \"Headless\"");
                AppSettings loaded = AppSettingsStore.Load();
                Check(Mode(loaded, "h") == InjectionMode.Headless && Mode(loaded, "n") == InjectionMode.Headless, "h and n (numeric 3) kept as Headless");
                Check(Mode(loaded, "b") == InjectionMode.DecalBridge, "b kept as Decal + RynthCore");
                Check(AppSettingsStore.LastMigrationNote?.Contains("2 Headless account(s)") == true &&
                      AppSettingsStore.LastMigrationNote.Contains("1 \"Decal + RynthCore\""), "migration note: " + AppSettingsStore.LastMigrationNote);
                CheckSharedFilesSafe();
                Check(HeadlessIds().SequenceEqual(new[] { "h", "n" }) && SidecarIds().SequenceEqual(new[] { "b" }), "both sidecars written");
                AppSettingsStore.Load();
                Check(AppSettingsStore.LastMigrationNote == null, "second load: nothing left to migrate");
            });

            Scenario("headless: unreadable headless-accounts.json, and an id in both sidecars", root, () =>
            {
                AppSettingsStore.Save(SampleWithHeadless());
                File.WriteAllText(HeadlessSidecar, """{ "HeadlessAccountIds": [ 7 ] }""");
                AppSettings loaded = AppSettingsStore.Load();
                Check(Mode(loaded, "h") == InjectionMode.RynthCore, "h runs as RynthCore");
                Check(Mode(loaded, "b") == InjectionMode.DecalBridge, "decal-accounts.json still applies");
                Check(AppSettingsStore.LastLoadDiagnostic == null, "appsettings.json not taken for corrupt");
                Check(AppSettingsStore.LastMigrationNote?.Contains("headless-accounts.json could not be read") == true, "note: " + AppSettingsStore.LastMigrationNote);

                AppSettingsStore.Save(SampleWithHeadless());
                File.WriteAllText(HeadlessSidecar, """{ "Version": 1, "HeadlessAccountIds": [ "h", "b" ] }""");
                AppSettings both = AppSettingsStore.Load();
                Check(Mode(both, "b") == InjectionMode.DecalBridge && Mode(both, "h") == InjectionMode.Headless, "an id in both: Decal + RynthCore wins");
            });

            Scenario("headless: Clone and CopyFrom keep every field", root, () =>
            {
                var full = new LaunchAccountProfile
                {
                    Id = "id1", AccountName = "Acct", PasswordProtected = "blob", LegacyPlainPassword = "legacy",
                    PasswordNeedsReentry = true, CharacterName = "Char", Alias = "Al", ServerId = "srv",
                    WindowX = 1, WindowY = 2, WindowWidth = 300, WindowHeight = 400, UserPrefsPath = @"C:\p.ini",
                    InjectionMode = InjectionMode.Headless,
                    OnLoginCommandsByCharacter = new(StringComparer.OrdinalIgnoreCase) { ["Char"] = ["/say hi"] },
                    OnLoginWaitMs = 1234,
                };
                // Every public read/write property must be set to something other than its default here,
                // so a field added later without Clone/CopyFrom support fails this check.
                var blank = new LaunchAccountProfile();
                foreach (var prop in typeof(LaunchAccountProfile).GetProperties().Where(p => p.CanRead && p.CanWrite))
                    Check(!Equals(Json(prop.GetValue(full)), Json(prop.GetValue(blank))), $"test sets {prop.Name}");

                LaunchAccountProfile clone = full.Clone();
                var copied = new LaunchAccountProfile();
                copied.CopyFrom(full);
                foreach (var prop in typeof(LaunchAccountProfile).GetProperties().Where(p => p.CanRead && p.CanWrite))
                {
                    string want = Json(prop.GetValue(full));
                    Check(Json(prop.GetValue(clone)) == want, $"Clone keeps {prop.Name}");
                    Check(Json(prop.GetValue(copied)) == want, $"CopyFrom keeps {prop.Name}");
                }
                Check(clone.InjectionMode == InjectionMode.Headless && copied.InjectionMode == InjectionMode.Headless, "Headless kept");
            });

            Scenario("headless: the new settings round trip, and default for older files", root, () =>
            {
                AppSettings fresh = new();
                Check(fresh.HeadlessExePath == "" && fresh.HeadlessPluginPaths.Count == 0 &&
                      fresh.HeadlessSuiteDir == "" && fresh.HeadlessStatusDir == "", "defaults: empty");

                File.WriteAllText(Main_, """{ "EnginePath": "Z", "AccountProfiles": [] }""");
                AppSettings older = AppSettingsStore.Load();
                Check(older.HeadlessExePath == "" && older.HeadlessPluginPaths is { Count: 0 } &&
                      older.HeadlessSuiteDir == "" && older.HeadlessStatusDir == "", "a file without them reads the defaults");

                AppSettings s = SampleWithHeadless();
                s.HeadlessExePath = @"D:\Rynth\Headless\rynth-headless.exe";
                s.HeadlessPluginPaths = [@"D:\x64\RynthCore.Plugin.RynthAi.dll", @"D:\x64\RynthCore.Plugin.RynthNav.dll"];
                s.HeadlessSuiteDir = @"D:\Suite";
                s.HeadlessStatusDir = @"D:\Status";
                AppSettingsStore.Save(s);
                AppSettings loaded = AppSettingsStore.Load();
                Check(loaded.HeadlessExePath == s.HeadlessExePath, "HeadlessExePath");
                Check(loaded.HeadlessPluginPaths.SequenceEqual(s.HeadlessPluginPaths), "HeadlessPluginPaths");
                Check(loaded.HeadlessSuiteDir == s.HeadlessSuiteDir && loaded.HeadlessStatusDir == s.HeadlessStatusDir, "HeadlessSuiteDir, HeadlessStatusDir");
                Check(OldLauncherCanRead(Main_) && BridgeLauncherCanRead(Main_), "older launchers still read the file");
            });

            Scenario("headless window: its size and place round trip", root, () =>
            {
                AppSettings fresh = new();
                Check(fresh.HeadlessWindowX == null && fresh.HeadlessWindowY == null &&
                      fresh.HeadlessWindowWidth == null && fresh.HeadlessWindowHeight == null, "defaults: none (centred)");
                AppSettings s = SampleWithHeadless();
                s.HeadlessWindowX = 120; s.HeadlessWindowY = -40; s.HeadlessWindowWidth = 1180; s.HeadlessWindowHeight = 790;
                AppSettingsStore.Save(s);
                AppSettings loaded = AppSettingsStore.Load();
                Check(loaded.HeadlessWindowX == 120 && loaded.HeadlessWindowY == -40 &&
                      loaded.HeadlessWindowWidth == 1180 && loaded.HeadlessWindowHeight == 790, "saved and loaded");
                Check(loaded.WindowX == s.WindowX && loaded.WindowWidth == s.WindowWidth, "the launcher's own place untouched");
                Check(OldLauncherCanRead(Main_) && BridgeLauncherCanRead(Main_), "older launchers still read the file");
            });

            Scenario("headless: run config, arguments, exe path, stop event", root, () =>
            {
                const string HeadlessPw = "Headless-Test-Pw-7 \"q\"";
                LaunchAccountProfile account = new() { Id = "0123abcd", AccountName = "TestAccount", InjectionMode = InjectionMode.Headless };
                account.SetPassword(HeadlessPw);
                var spec = new HeadlessRunSpec
                {
                    Host = "127.0.0.1", Port = 9000, ServerName = "Local ACE",
                    Plugins = [@"C:\x64\RynthCore.Plugin.RynthAi.dll", "  ", @"C:\x64\Other.dll"],
                    Sessions =
                    {
                        new HeadlessSessionSpec { LauncherProfileId = account.Id, Account = account.AccountName, Character = "+Test Character",
                                                  Commands = ["/ra start", ""], StaySeconds = 0, Reconnect = true },
                    },
                };
                string json = HeadlessLaunch.BuildConfigJson(spec);
                Check(!json.Contains(HeadlessPw) && !json.Contains(account.PasswordProtected), "no password (plain or encrypted) in the config");
                Check(!json.Contains("password", StringComparison.OrdinalIgnoreCase), "no password field at all");

                JsonNode cfg = JsonNode.Parse(json)!;
                Check((string?)cfg["host"] == "127.0.0.1" && (int?)cfg["port"] == 9000 && (string?)cfg["serverName"] == "Local ACE", "host, port, serverName");
                Check(cfg["suiteDir"] == null && cfg["statusDir"] == null, "empty suiteDir/statusDir left out (the CLI's defaults)");
                Check(cfg["plugins"]!.AsArray().Select(n => (string)n!).SequenceEqual(new[] { @"C:\x64\RynthCore.Plugin.RynthAi.dll", @"C:\x64\Other.dll" }), "plugins (blank dropped)");
                JsonNode session = cfg["sessions"]!.AsArray().Single()!;
                Check((string?)session["launcherProfileId"] == "0123abcd" && (string?)session["account"] == "TestAccount" && (string?)session["character"] == "+Test Character",
                    "session: launcherProfileId, account, character");
                Check(session["commands"]!.AsArray().Select(n => (string)n!).SequenceEqual(new[] { "/ra start" }) &&
                      (int?)session["staySeconds"] == 0 && (bool?)session["reconnect"] == true, "session: commands, staySeconds, reconnect");

                // As rynth-headless reads it (RunConfig: case-insensitive).
                CliRunConfig? read = JsonSerializer.Deserialize<CliRunConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                Check(read?.Host == "127.0.0.1" && read.Port == 9000 && read.Sessions.Single().LauncherProfileId == "0123abcd" &&
                      read.Sessions.Single().Character == "+Test Character", "the CLI's shape reads it");

                spec.SuiteDir = @"D:\Suite"; spec.StatusDir = @"D:\Status";
                spec.Sessions[0].Character = "";
                JsonNode cfg2 = JsonNode.Parse(HeadlessLaunch.BuildConfigJson(spec))!;
                Check((string?)cfg2["suiteDir"] == @"D:\Suite" && (string?)cfg2["statusDir"] == @"D:\Status", "suiteDir/statusDir when set");
                Check(cfg2["sessions"]![0]!.AsObject().ContainsKey("character") && cfg2["sessions"]![0]!["character"] == null, "no character: null");

                string path = HeadlessLaunch.ConfigPath(_dir, account.Id);
                Check(path == Path.Combine(_dir, "headless", "launch_0123abcd.json"), "config path: " + path);
                Check(Path.GetFileName(HeadlessLaunch.ConfigPath(_dir, "a/b:c")) == "launch_a_b_c.json", "unsafe id characters replaced");
                HeadlessLaunch.WriteConfig(path, json);
                Check(File.ReadAllText(path) == json && !File.Exists(path + ".tmp"), "config written (no temp file left)");
                CheckNoPlainText(HeadlessPw);

                Check(Same(HeadlessLaunch.BuildArguments(path, @"C:\Turbine\Asheron's Call"),
                           "run", "--config", path, "--launcher", "--ac-folder", @"C:\Turbine\Asheron's Call"), "arguments with the AC folder");
                Check(Same(HeadlessLaunch.BuildArguments(path, null), "run", "--config", path, "--launcher"), "arguments without");
                Check(HeadlessLaunch.AcFolderFor(@"C:\Turbine\Asheron's Call\acclient.exe") == @"C:\Turbine\Asheron's Call", "AC folder from the client path");
                Check(HeadlessLaunch.AcFolderFor("  ") == null && HeadlessLaunch.AcFolderFor(null) == null, "no client path: no AC folder");

                Check(HeadlessLaunch.ResolveExePath("", @"C:\Games\RynthCore") == @"C:\Games\RynthCore\Headless\rynth-headless.exe", "default exe beside the launcher");
                Check(HeadlessLaunch.ResolveExePath(" \"D:\\h\\rynth-headless.exe\" ", @"C:\x") == @"D:\h\rynth-headless.exe", "set exe (trimmed, unquoted)");

                // HeadlessPluginPaths empty = the x64 plugins the installer ships in <launcher>\Headless\plugins\<Name>\.
                string install = Path.Combine(_dir, "install");
                Check(HeadlessLaunch.DefaultPluginPaths(install).Count == 0, "no Headless\\plugins folder: no default plugins");
                string shipped = Path.Combine(install, "Headless", "plugins");
                Directory.CreateDirectory(Path.Combine(shipped, "RynthAi"));
                Directory.CreateDirectory(Path.Combine(shipped, "Empty"));
                File.WriteAllText(Path.Combine(shipped, "RynthAi", "RynthCore.Plugin.RynthAi.dll"), "x");
                File.WriteAllText(Path.Combine(shipped, "RynthAi", "RynthCore.PluginSdk.dll"), "x");
                string shippedAi = Path.Combine(shipped, "RynthAi", "RynthCore.Plugin.RynthAi.dll");
                Check(Same(HeadlessLaunch.DefaultPluginPaths(install), shippedAi), "default plugins: each folder's RynthCore.Plugin.<folder>.dll, not its dependencies");
                Check(Same(HeadlessLaunch.ResolvePluginPaths([], install), shippedAi) &&
                      Same(HeadlessLaunch.ResolvePluginPaths(null, install), shippedAi) &&
                      Same(HeadlessLaunch.ResolvePluginPaths(["  ", "\"\""], install), shippedAi), "an empty list uses the shipped plugins");
                Check(Same(HeadlessLaunch.ResolvePluginPaths([" \"D:\\x64\\RynthCore.Plugin.RynthAi.dll\" "], install), @"D:\x64\RynthCore.Plugin.RynthAi.dll"),
                      "a set list is used as it is (and nothing shipped is added)");

                int pid = Environment.ProcessId;
                Check(HeadlessLaunch.StopEventName(pid) == $@"Local\RynthHeadless.Stop.{pid}", "stop event name");
                Check(!HeadlessLaunch.TrySignalStop(pid), "no stop event: TrySignalStop is false (the launcher kills instead)");
                using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, HeadlessLaunch.StopEventName(pid)))
                {
                    Check(HeadlessLaunch.TrySignalStop(pid) && stop.WaitOne(0), "TrySignalStop sets the process's stop event");
                }
            });

            // ── saved account passwords (DPAPI) ─────────────────────────────────
            // Test-only passwords; nothing here touches the real settings file.
            const string Pw = "t3st pw \"quoted\" ü";
            const string Pw2 = "Second-Test-Pw";

            Scenario("password round trip", root, () =>
            {
                AppSettings s = Sample();
                s.AccountProfiles[0].SetPassword(Pw);
                AppSettingsStore.Save(s);
                AppSettingsStore.Save(s);   // .bak too
                CheckNoPlainText(Pw);
                CheckNoPlainText("t3st pw");
                Check(!RawAccount("a").ContainsKey("Password"), "no plain \"Password\" field written");
                Check(!string.IsNullOrEmpty((string?)RawAccount("a")["PasswordProtected"]), "PasswordProtected written");
                Check(OldLauncherCanRead(Main_), "the old launcher can still read the file");

                AppSettings loaded = AppSettingsStore.Load();
                LaunchAccountProfile a = loaded.AccountProfiles.First(x => x.Id == "a");
                Check(a.TryGetPasswordForLaunch(out string got, out _) && got == Pw, "decrypts to the saved password");
                Check(!a.PasswordNeedsReentry && a.HasSavedPassword, "saved, readable");
                Check(AppSettingsStore.LastPasswordMigrationNote == null && AppSettingsStore.LastPasswordProblemNote == null, "no password notes");

                var builder = new AcLaunchArgumentBuilder();
                LaunchServerProfile ace = Server(AcEmulatorKind.Ace), gdle = Server(AcEmulatorKind.Gdle);
                // Read back the way acclient reads its command line (AcClientCommandLine.Split): the password
                // as typed, less its quote marks (the client can't receive one), and the host after it intact.
                List<string>? aceArgs = AcClientCommandLine.Split(builder.BuildArguments(ace, a));
                Check(ArgAfter(aceArgs, "-v") == "t3st pw quoted ü" && ArgAfter(aceArgs, "-h") == "127.0.0.1:9000",
                    "ACE: the client reads the password (-v) and the host after it");
                List<string>? gdleArgs = AcClientCommandLine.Split(builder.BuildArguments(gdle, a));
                Check(ArgAfter(gdleArgs, "-a") == "A:t3st pw quoted ü" && ArgAfter(gdleArgs, "-p") == "9000",
                    "GDLE: the client reads account:password (-a) and the port");
                string preview = builder.BuildMaskedArguments(ace, a);
                Check(!preview.Contains("t3st") && preview.Contains("-v " + AcLaunchArgumentBuilder.PasswordMask), "preview masked: " + preview);
                LaunchAccountProfile noPw = loaded.AccountProfiles.First(x => x.Id == "b");
                Check(AcLaunchArgumentBuilder.NeedsPassword(noPw) && !AcLaunchArgumentBuilder.NeedsPassword(a), "NeedsPassword: only the account without one");
                Check(builder.BuildMaskedArguments(ace, noPw).StartsWith("No password is saved for account 'B'"),
                    "preview of an account without a password says so: " + builder.BuildMaskedArguments(ace, noPw));
                // The client exits with "You can only run the game from the launcher program" on an
                // empty password (Conquest, 2026-10-13), so the launch is refused instead.
                string refusal = Throws(() => builder.BuildArguments(ace, noPw));
                Check(refusal.StartsWith("No password is saved for account 'B'"), "no saved password: launch refused: " + refusal);
                Check(Throws(() => builder.BuildArguments(gdle, noPw)).StartsWith("No password"), "GDLE refused too");

                // Account names: ACE's 50-character limit; GDLE's "account:password" argument.
                Check(AcLaunchArgumentBuilder.AccountNameProblem("SK", AcEmulatorKind.Ace) == null, "a short name is fine");
                Check(AcLaunchArgumentBuilder.AccountNameProblem(new string('x', 51), AcEmulatorKind.Ace)?.Contains("50") == true, "51 characters: too long");
                Check(AcLaunchArgumentBuilder.AccountNameProblem("a:b", AcEmulatorKind.Ace) == null, "a colon is fine on ACE");
                Check(AcLaunchArgumentBuilder.AccountNameProblem("a:b", AcEmulatorKind.Gdle) != null, "a colon is refused on GDLE");
                Check(AcLaunchArgumentBuilder.AccountNameProblem("  ", AcEmulatorKind.Ace) != null, "blank is refused");

                LaunchAccountProfile copy = a.Clone();
                Check(copy.PasswordProtected == a.PasswordProtected, "Clone keeps the encrypted password");
                copy.SetPassword(Pw2);
                Check(copy.TryGetPasswordForLaunch(out string got2, out _) && got2 == Pw2, "a new password replaces it");
                copy.ClearPassword();
                Check(!copy.HasSavedPassword && copy.TryGetPasswordForLaunch(out string none, out _) && none.Length == 0, "clear");
                Check(AccountPasswordProtection.Protect(Pw) != AccountPasswordProtection.Protect(Pw), "each encryption differs (salted blobs)");
            });

            Scenario("migrate plain-text passwords from an older file", root, () =>
            {
                string legacy = """
                { "EnginePath": "X", "AccountProfiles": [
                  { "Id": "a", "AccountName": "A", "Password": "PLAIN-PW-ONE", "InjectionMode": "RynthCore" },
                  { "Id": "b", "AccountName": "B", "Password": "PLAIN-PW-TWO", "InjectionMode": "RynthCore" },
                  { "Id": "c", "AccountName": "C", "Password": "", "InjectionMode": "Decal" },
                  { "Id": "d", "AccountName": "D" } ] }
                """;
                File.WriteAllText(Main_, legacy);
                File.WriteAllText(Bak, legacy);
                File.WriteAllText(Sidecar, """{ "DecalBridgeAccountIds": [ "b" ] }""");
                File.WriteAllText(Main_ + ".corrupt-20260101-000000", "{ old quarantined file");

                AppSettings loaded = AppSettingsStore.Load();
                Check(AppSettingsStore.LastPasswordMigrationNote?.StartsWith("migrated 2 account password(s) to encrypted storage") == true,
                    "one note: " + AppSettingsStore.LastPasswordMigrationNote);
                Check(AppSettingsStore.LastPasswordMigrationNote?.Contains("corrupt-") == true, "note points at the quarantined file");
                Check(PwOf(loaded, "a") == "PLAIN-PW-ONE" && PwOf(loaded, "b") == "PLAIN-PW-TWO", "passwords kept");
                Check(!loaded.AccountProfiles.First(x => x.Id == "c").HasSavedPassword, "empty plain password = none saved");
                Check(loaded.AccountProfiles.All(x => x.LegacyPlainPassword == null), "no plain text left on the profiles");
                CheckNoPlainText("PLAIN-PW-ONE");
                CheckNoPlainText("PLAIN-PW-TWO");
                Check(!File.ReadAllText(Main_).Contains("\"Password\"") && !File.ReadAllText(Bak).Contains("\"Password\""),
                    "no \"Password\" field in appsettings.json or .bak");
                Check(Mode(loaded, "b") == InjectionMode.DecalBridge && SidecarIds().SequenceEqual(new[] { "b" }),
                    "decal-accounts.json survives the migration save");
                Check(loaded.EnginePath == "X", "other settings kept");
                CheckNotesClean("PLAIN-PW-ONE", "PLAIN-PW-TWO");

                AppSettings again = AppSettingsStore.Load();
                Check(AppSettingsStore.LastPasswordMigrationNote == null, "second load: nothing left to migrate");
                Check(PwOf(again, "a") == "PLAIN-PW-ONE", "second load decrypts");
            });

            Scenario("undecryptable password (copied from another Windows user or PC)", root, () =>
            {
                AppSettings s = Sample();
                s.AccountProfiles[0].SetPassword(Pw);
                s.AccountProfiles[1].SetPassword(Pw2);
                s.AccountProfiles[2].SetPassword(Pw2);
                AppSettingsStore.Save(s);
                // Blobs this user can't open: a valid one with a byte changed (DPAPI's integrity
                // check fails as it does with another user's key), random bytes, and non-base64.
                byte[] blob = Convert.FromBase64String(s.AccountProfiles[0].PasswordProtected);
                blob[^5] ^= 0x5A;
                SetRaw("a", Convert.ToBase64String(blob));
                SetRaw("b", Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
                SetRaw("c", "not base64 !!");

                AppSettings loaded = AppSettingsStore.Load();   // must not throw
                Check(AppSettingsStore.LastLoadDiagnostic == null, "the file is not taken for corrupt");
                Check(loaded.AccountProfiles.All(x => x.PasswordNeedsReentry), "all three marked \"password needs re-entering\"");
                Check(AppSettingsStore.LastPasswordProblemNote?.Contains("3 saved account password(s)") == true &&
                      AppSettingsStore.LastPasswordProblemNote.Contains("need re-entering"),
                    "problem note: " + AppSettingsStore.LastPasswordProblemNote);

                LaunchAccountProfile a = loaded.AccountProfiles.First(x => x.Id == "a");
                var builder = new AcLaunchArgumentBuilder();
                LaunchServerProfile ace = Server(AcEmulatorKind.Ace);
                string? error = null;
                try { builder.BuildArguments(ace, a); }
                catch (InvalidOperationException ex) { error = ex.Message; }
                Check(error?.Contains("needs re-entering") == true, "launch refused, not launched with an empty password: " + error);
                Check(builder.BuildMaskedArguments(ace, a).Contains("needs re-entering"), "preview says so");
                Check(!a.TryGetPasswordForLaunch(out string empty, out _) && empty.Length == 0, "TryGetPasswordForLaunch false");

                a.SetPassword(Pw2);   // re-entered in the account editor
                Check(!a.PasswordNeedsReentry && a.TryGetPasswordForLaunch(out string got, out _) && got == Pw2, "re-entering fixes it");
                AppSettingsStore.Save(loaded);
                AppSettings reloaded = AppSettingsStore.Load();
                Check(!reloaded.AccountProfiles.First(x => x.Id == "a").PasswordNeedsReentry, "fixed after reload");
                Check(reloaded.AccountProfiles.First(x => x.Id == "b").PasswordNeedsReentry, "the others still need re-entering");
                CheckNoPlainText(Pw2);
            });

            Scenario("nothing leaks into notes, previews or other files", root, () =>
            {
                File.WriteAllText(Main_, """{ "AccountProfiles": [ { "Id": "a", "AccountName": "A", "Password": "LEAK-CHECK-PW" } ] }""");
                AppSettings loaded = AppSettingsStore.Load();
                CheckNotesClean("LEAK-CHECK-PW");
                Check(!loaded.AccountProfiles[0].ToString().Contains("LEAK-CHECK-PW"), "display name has no password");
                Check(!new AcLaunchArgumentBuilder().BuildMaskedArguments(Server(AcEmulatorKind.Gdle), loaded.AccountProfiles[0]).Contains("LEAK-CHECK-PW"),
                    "GDLE preview masked");
                AppSettingsStore.Save(loaded);
                CheckNoPlainText("LEAK-CHECK-PW");
            });

            Scenario("known servers: picking one fills the profile, saved profiles find theirs", root, () =>
            {
                var all = KnownServerCatalog.All;
                Check(all.Count == 17, $"17 known servers (16 + ILT), got {all.Count}");
                Check(all.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == all.Count, "ids unique");
                Check(all.Select(s => s.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() == all.Count, "hosts unique");
                Check(all.All(s => s.Port is > 0 and < 65536 && s.PluginName.StartsWith("Rynth", StringComparison.Ordinal)),
                    "every entry has a port and a Rynth plugin");
                Check(all.Select(s => s.Name).SequenceEqual(all.Select(s => s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)),
                    "listed by name");

                KnownServer? reef = KnownServerCatalog.FindById("REEFCULL");
                Check(reef?.Emulator == AcEmulatorKind.Gdle, "Reefcull is GDLE (id lookup ignores case)");
                Check(KnownServerCatalog.FindById("portalstorm")?.Port == 9020, "PortalStorm uses 9020");

                var p = new LaunchServerProfile { Id = "keep", Alias = "Main", RodatEnabled = true, Host = "x", Port = 1 };
                KnownServerCatalog.ApplyTo(KnownServerCatalog.FindById("ilt")!, p);
                Check(p.Name == "Infinite Leaftide" && p.ConnectionString == "game.infiniteleaftide.online:9000" && p.Emulator == AcEmulatorKind.Ace,
                    "ILT fills name, host, port, emulator: " + p.ConnectionString);
                Check(p.Id == "keep" && p.Alias == "Main" && p.RodatEnabled, "id, alias and Rodat kept");

                Check(KnownServerCatalog.FindByHost("PLAY.COLDEVE.AC.")?.Id == "coldeve", "exact host (case, trailing dot)");
                Check(KnownServerCatalog.FindByHost("infiniteleaftide.online")?.Id == "ilt", "ILT's parent domain finds ILT");
                Check(KnownServerCatalog.FindByHost("aelrynth.com")?.Id == "aelrynth", "Aelrynth");
                Check(KnownServerCatalog.FindByHost("other.ddns.net") == null, "a shared dynamic-DNS domain matches nothing");
                Check(KnownServerCatalog.FindByHost("harvestbud.gdleac.com") == null, "Reefcull's shared host domain isn't Reefcull");
                Check(KnownServerCatalog.FindByHost("172.111.230.12") == null, "an IP matches only itself");
                Check(KnownServerCatalog.FindByHost("172.111.230.127")?.Id == "frostace", "FrostACE by IP");
                Check(KnownServerCatalog.FindByHost("") == null && KnownServerCatalog.FindByHost(null) == null, "empty host");
            });

            // ── First Login dialog rules (a new server: no password, no characters yet) ──
            Scenario("first login on a new server", root, () =>
            {
                var withPw = new LaunchAccountProfile { Id = "ilt", AccountName = "silentkelpie" };
                withPw.SetPassword(Pw);
                var noPwSame = new LaunchAccountProfile { Id = "other", AccountName = "SilentKelpie" };
                var conquest = new LaunchAccountProfile { Id = "new", AccountName = "SK", CharacterName = LaunchAccountProfile.NoneOption };
                var all = new List<LaunchAccountProfile> { withPw, noPwSame, conquest };

                Check(FirstLoginRules.KnownAccountNames(all, "new").SequenceEqual(new[] { "silentkelpie" }),
                    "known names: the others, distinct ignoring case, not this profile's own");

                Check(FirstLoginRules.SavedPasswordSource(all, "SILENTKELPIE", "new")?.Id == "ilt",
                    "the same name with a saved password can be reused (case ignored)");
                Check(FirstLoginRules.SavedPasswordSource(all, "SK", "new") == null, "a new name has none to reuse");
                Check(FirstLoginRules.SavedPasswordSource(all, "silentkelpie", "ilt") == null, "never the profile itself");

                var copy = new LaunchAccountProfile { AccountName = "silentkelpie" };
                copy.CopyPasswordFrom(withPw);
                Check(copy.PasswordProtected == withPw.PasswordProtected && copy.TryGetPasswordForLaunch(out string reused, out _) && reused == Pw,
                    "reuse copies the encrypted blob, and it decrypts");

                var none = new List<string>();
                List<string> firstChoices = FirstLoginRules.LoginChoices(none, conquest.CharacterName);
                Check(firstChoices.SequenceEqual(new[] { FirstLoginRules.StopAtCharacterSelect }),
                    "no characters yet: only stop at character select (the old (None) isn't listed)");
                Check(FirstLoginRules.CharacterNameFor(FirstLoginRules.StopAtCharacterSelect, none) == "",
                    "stop with none known saves \"\" so the first character created is picked next time");

                var chars = new List<string> { "Alpha", "Beta" };
                Check(FirstLoginRules.LoginChoices(chars, "Gamma").SequenceEqual(new[] { FirstLoginRules.StopAtCharacterSelect, "Gamma", "Alpha", "Beta" }),
                    "several characters: all listed, the saved one kept even if not seen yet");
                Check(FirstLoginRules.CharacterNameFor("Beta", chars) == "Beta", "picking a character saves it");
                Check(FirstLoginRules.CharacterNameFor(FirstLoginRules.StopAtCharacterSelect, chars) == LaunchAccountProfile.NoneOption,
                    "stop with characters known saves the explicit (None)");

                Check(FirstLoginRules.Problem("SK", "", "", false, AcEmulatorKind.Ace)?.StartsWith("Enter the password") == true, "a password is required");
                Check(FirstLoginRules.Problem("SK", "a", "b", false, AcEmulatorKind.Ace)?.Contains("don't match") == true, "the confirmation must match");
                Check(FirstLoginRules.Problem("SK", "pw", "pw", false, AcEmulatorKind.Ace) == null, "name + matching password: ready");
                Check(FirstLoginRules.Problem("silentkelpie", "", "", true, AcEmulatorKind.Ace) == null, "reusing a saved password needs no typing");
                Check(FirstLoginRules.Problem("a:b", "pw", "pw", false, AcEmulatorKind.Gdle) != null, "GDLE name with a colon refused");
                Check(FirstLoginRules.Problem("", "pw", "pw", false, AcEmulatorKind.Ace) != null, "a name is required");
            });

            // Deleting a server profile re-pointed its accounts at the first remaining server, so the
            // next launch sent one server's password to another (audit 2026-10-21 F2).
            Scenario("server profiles: an account logs on only to its own server", root, () =>
            {
                var conquest = new LaunchServerProfile { Id = "conq", Name = "Conquest", Host = "conquest.example", Port = 9000 };
                var ilt = new LaunchServerProfile { Id = "ilt", Name = "Infinite Leaftide", Host = "ilt.example", Port = 9000 };
                var servers = new List<LaunchServerProfile> { conquest, ilt };
                var onConquest = new LaunchAccountProfile { Id = "a", AccountName = "SK", ServerId = "conq" };
                var onConquest2 = new LaunchAccountProfile { Id = "b", AccountName = "Buffi", Alias = "Buffer", ServerId = "CONQ" };
                var onIlt = new LaunchAccountProfile { Id = "c", AccountName = "SK", ServerId = "ilt" };
                var never = new LaunchAccountProfile { Id = "d", AccountName = "New" };
                var dangling = new LaunchAccountProfile { Id = "e", AccountName = "Old", ServerId = "deleted-elsewhere" };
                var accounts = new List<LaunchAccountProfile> { onConquest, onConquest2, onIlt, never, dangling };

                Check(AccountServerRules.ServerFor(onConquest, servers) == conquest, "its own server");
                Check(AccountServerRules.ServerFor(onConquest2, servers) == conquest, "the id is matched ignoring case");
                Check(AccountServerRules.ServerFor(onIlt, servers) == ilt, "the same name on another server: that server");
                Check(AccountServerRules.ServerFor(never, servers) == null, "no server set: none (not the first or the selected one)");
                Check(AccountServerRules.ServerFor(dangling, servers) == null, "a server that is gone: none, never another server");
                Check(AccountServerRules.ServerFor(dangling, new List<LaunchServerProfile> { ilt }) == null, "not even when only one server is left");
                Check(AccountServerRules.ServerFor(onConquest, new List<LaunchServerProfile>()) == null, "no servers at all: none");

                Check(AccountServerRules.AccountsOn(conquest, accounts).Select(a => a.Id).SequenceEqual(new[] { "a", "b" }), "Conquest's accounts: a and b");
                string? blocker = AccountServerRules.DeleteBlocker(conquest, accounts);
                Check(blocker != null && blocker.Contains("2 account(s)") && blocker.Contains("SK") && blocker.Contains("Buffer (Buffi)"),
                    "deleting a server with accounts is refused, naming them: " + blocker);
                Check(blocker != null && !blocker.Contains("Old") && !blocker.Contains("New"), "only that server's accounts are named");
                var unused = new LaunchServerProfile { Id = "spare", Name = "Spare", Host = "h", Port = 1 };
                Check(AccountServerRules.DeleteBlocker(unused, accounts) == null, "a server no account uses can be deleted");

                // The refusal leaves every account as it was (nothing re-pointed).
                Check(accounts.Select(a => a.ServerId).SequenceEqual(new[] { "conq", "CONQ", "ilt", "", "deleted-elsewhere" }), "no account was changed");

                var many = Enumerable.Range(1, 12).Select(i => new LaunchAccountProfile { AccountName = "Acc" + i, ServerId = "ilt" }).ToList();
                string? manyBlocker = AccountServerRules.DeleteBlocker(ilt, many);
                Check(manyBlocker != null && manyBlocker.Contains("12 account(s)") && manyBlocker.Contains("4 more") && !manyBlocker.Contains("Acc9"),
                    "a long list names 8 and counts the rest");

                Check(AccountServerRules.NoServerMessage(dangling, anyServerProfiles: true).Contains("pick the server"), "no-server launch message says what to do");
                Check(AccountServerRules.NoServerMessage(never, anyServerProfiles: false).Contains("Add a server profile"), "with no servers at all: add one");
            });

            // A saved UserPreferences.ini whose display mode the PC can't use made every launch
            // fail with "Could not initialize Direct3D"; a quick exit must undo that swap.
            Scenario("prefs swap: did the swap change the live file", root, () =>
            {
                byte[] a = "[Display]\nResolution=2560x1440\n"u8.ToArray();
                byte[] b = "[Display]\nResolution=1920x1080\n"u8.ToArray();
                Check(!UserPrefsSwapRecovery.SwapChangedPrefs(new UserPrefsSwap("s", "l", a, (byte[])a.Clone())), "same bytes: not the cause");
                Check(UserPrefsSwapRecovery.SwapChangedPrefs(new UserPrefsSwap("s", "l", a, b)), "different bytes: changed");
                Check(UserPrefsSwapRecovery.SwapChangedPrefs(new UserPrefsSwap("s", "l", a, null)), "no live file before: changed");
            });

            Scenario("prefs swap: failed stash set aside, live file put back", root, () =>
            {
                var (swap, legacy) = PrefsSwapFiles(legacyMatches: true);
                var now = new DateTime(2026, 10, 14, 16, 30, 5);
                UserPrefsRecoveryResult r = UserPrefsSwapRecovery.Recover(swap, quarantineStash: true, restoreLive: true, legacy, now);
                Check(!File.Exists(swap.StashPath), "stash no longer swappable");
                Check(r.QuarantinedStashPath == swap.StashPath + ".failed-20261014-163005" && File.Exists(r.QuarantinedStashPath), "stash kept as .failed-<time>");
                Check(!File.Exists(legacy) && r.QuarantinedLegacyPath != null, "identical name-only stash set aside too (it would re-seed)");
                Check(r.LiveRestored && File.ReadAllBytes(swap.LivePath).SequenceEqual(swap.PreviousLiveBytes!), "live file back to its pre-swap bytes");
            });

            Scenario("prefs swap: different name-only stash kept", root, () =>
            {
                var (swap, legacy) = PrefsSwapFiles(legacyMatches: false);
                UserPrefsRecoveryResult r = UserPrefsSwapRecovery.Recover(swap, true, true, legacy, DateTime.Now);
                Check(File.Exists(legacy) && r.QuarantinedLegacyPath == null, "name-only stash with other settings left alone");
            });

            Scenario("prefs swap: later swap or profile override", root, () =>
            {
                var (swap, legacy) = PrefsSwapFiles(legacyMatches: true);
                File.WriteAllText(swap.LivePath, "another account's settings");
                UserPrefsRecoveryResult r = UserPrefsSwapRecovery.Recover(swap, quarantineStash: false, restoreLive: false, legacy, DateTime.Now);
                Check(File.Exists(swap.StashPath) && File.Exists(legacy), "override: the user's file is never renamed");
                Check(!r.LiveRestored && File.ReadAllText(swap.LivePath) == "another account's settings", "later swap: live file untouched");
            });

            Scenario("prefs swap: no live file before", root, () =>
            {
                var (swap, _) = PrefsSwapFiles(legacyMatches: false);
                swap = swap with { PreviousLiveBytes = null };
                UserPrefsSwapRecovery.Recover(swap, true, true, null, DateTime.Now);
                Check(!File.Exists(swap.LivePath), "live file removed so the game writes its defaults");
            });

            // A kill while createdump.exe / WerFault.exe writes a client's crash dump cuts the dump
            // short; the launcher's kills wait for the writer, then Grace more, at most MaxHold.
            Scenario("crash dump writers: which client a command line names", root, () =>
            {
                Check(CrashDumpWriterRule.TargetPid("createdump",
                    "\"C:\\Games\\RynthCore\\Runtime\\dotnet\\createdump.exe\" -f \"C:\\Games\\RynthCore\\Logs\\dumps\\clr_29196_%t.dmp\" --withheap 29196") == 29196,
                    "createdump: the positional pid after the options");
                Check(CrashDumpWriterRule.TargetPid("createdump.exe", "createdump.exe 4321 -h") == 4321, "createdump: pid first, \".exe\" in the name");
                Check(CrashDumpWriterRule.TargetPid("createdump", "createdump --crashthread 777 --signal 6 -f 1234.dmp 4321") == 4321,
                    "createdump: numeric option values (--crashthread, --signal) aren't the pid");
                Check(CrashDumpWriterRule.TargetPid("createdump", "createdump -f x.dmp --withheap") == null, "createdump without a pid: none");
                Check(CrashDumpWriterRule.TargetPid("WerFault", @"C:\WINDOWS\SysWOW64\WerFault.exe -u -p 29196 -s 568") == 29196, "WerFault: the value after -p");
                Check(CrashDumpWriterRule.TargetPid("WerFault", @"C:\WINDOWS\system32\WerFault.exe -pss -s 1234 -r 5678 -p 29196 -ip 4000") == 29196,
                    "WerFault: -s/-r/-ip values aren't the pid");
                Check(CrashDumpWriterRule.TargetPid("werfault", "WerFault.exe /p 99 /s 1") == 99, "WerFault: /p, any case");
                Check(CrashDumpWriterRule.TargetPid("WerFault", "WerFault.exe -s 1234") == null, "WerFault without -p: none");
                Check(CrashDumpWriterRule.TargetPid("notepad", "notepad.exe 1234") == null, "not a dump writer: none");
                Check(CrashDumpWriterRule.TargetPid("createdump", null) == null && CrashDumpWriterRule.TargetPid("createdump", "") == null,
                    "no command line: none");
            });

            Scenario("crash dump writers: how long the kills wait", root, () =>
            {
                var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
                var H = CrashDumpWriterRule.Hold.Holding;
                Check(CrashDumpWriterRule.Decide(false, null, null, now) == CrashDumpWriterRule.Hold.None, "no writer ever: kill checks run");
                Check(CrashDumpWriterRule.Decide(true, null, null, now) == H, "a writer names the client: hold");
                Check(CrashDumpWriterRule.Decide(true, now, now.AddMinutes(-5), now) == H, "still writing after 5 min: hold");
                Check(CrashDumpWriterRule.Decide(false, now.AddSeconds(-59), now.AddMinutes(-1), now) == H, "writer gone 59 s ago: still held (60 s more)");
                Check(CrashDumpWriterRule.Decide(false, now.AddSeconds(-60), now.AddMinutes(-1), now) == CrashDumpWriterRule.Hold.None,
                    "writer gone 60 s ago: kill checks resume");
                Check(CrashDumpWriterRule.Decide(true, now, now.AddMinutes(-15), now) == CrashDumpWriterRule.Hold.Expired,
                    "held 15 min: expired, a writer that never ends can't shield a dead client");
                Check(CrashDumpWriterRule.Decide(false, now.AddSeconds(-10), now.AddMinutes(-16), now) == CrashDumpWriterRule.Hold.Expired,
                    "expired stays expired through the grace");
                Check(CrashDumpWriterRule.Grace == TimeSpan.FromSeconds(60), "the grace is 60 s");
            });

            // Drakkon 2026-10-25 23:04: the saved window was put back 3 s after the login, AC shrank it to its
            // login size 3 s later and drew the game at 1600x900 into 806x629 all night.
            Scenario("window restore: AC's shrink right after the restore is put back", root, () =>
            {
                var at = new DateTime(2026, 10, 26, 3, 4, 48, DateTimeKind.Utc);
                bool Re(double sec, DateTime? last = null, int count = 0, bool loggedOut = false, int w = 806, int h = 629) =>
                    AcWindowRestoreRule.ShouldReapply(at.AddSeconds(sec), at, last, count, loggedOut, w, h, 1606, 929);
                Check(Re(3), "806x629 three seconds after the restore: put back");
                Check(!Re(3, w: 1606, h: 929), "the saved size: nothing to do");
                Check(!Re(3, w: 1300, h: 800), "a moderate resize (the player's): left alone");
                Check(!Re(3, loggedOut: true), "logged out (the real character select): left alone");
                Check(!Re(61), "past the minute after the restore: left alone (the tracker only refuses to save it)");
                Check(!Re(5, last: at.AddSeconds(3), count: 1), "two seconds after a put-back: wait (AC may still be resizing)");
                Check(Re(6, last: at.AddSeconds(3), count: 1), "three seconds after: again");
                Check(!Re(30, last: at.AddSeconds(20), count: AcWindowRestoreRule.MaxReapplies), "after MaxReapplies: no more");
                Check(AcWindowRestoreRule.IsLoginShrink(806, 629, 1606, 929) && !AcWindowRestoreRule.IsLoginShrink(1200, 700, 1606, 929),
                    "login size is under half the saved area");
                Check(!AcWindowRestoreRule.IsLoginShrink(806, 629, 0, 0), "no saved size: never");
            });
        }
        finally
        {
            AppSettingsStore.DirectoryOverride = null;
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        ClientCommandLineChecks();
        ExtraLaunchArgumentChecks();
        PasswordEyeChecks();

        Console.WriteLine(_failed == 0
            ? $"PASS: {_checks} checks"
            : $"FAIL: {_failed} of {_checks} checks failed");
        return _failed == 0 ? 0 : 1;
    }

    // ── acclient's command line ──────────────────────────────────────────────
    // Passwords with special characters failed to log in (2026-10-21, Conquest; ThwargLauncher, which
    // passes the password as it is, worked): the launcher wrote the C runtime's escapes (\\ and \"),
    // which acclient's own splitter keeps as typed. AcClientCommandLine.Split is that splitter
    // (ConvertCommandLineToArgList, 0x004098F0); everything the launcher writes is read back with it.
    private static void ClientCommandLineChecks()
    {
        Console.WriteLine("-- acclient's command line");

        // The splitter itself, as decompiled: spaces split outside quotes, a quote toggles and is dropped,
        // a backslash is ordinary, an open quote refuses the line.
        Check(Same(AcClientCommandLine.Split("\"C:\\Turbine\\Asheron's Call\\acclient.exe\" -a x  -v y"),
            "C:\\Turbine\\Asheron's Call\\acclient.exe", "-a", "x", "-v", "y"), "the exe path in quotes, double spaces");
        Check(Same(AcClientCommandLine.Split("a\"\"b c\\\"d e\""), "ab", "c\\d e"), "quotes dropped, a backslash kept as it is");
        Check(AcClientCommandLine.Split("-v \"a b") == null, "an open quote refuses the line");
        Check(Same(AcClientCommandLine.Split("-v a\tb"), "-v", "a\tb"), "only a space splits (a tab is part of the argument)");

        // A long password with nearly every special character and no space (the report): written as it is,
        // exactly as ThwargLauncher writes it.
        string special = "Aa1!@#$%^&*()_+-=[]{}|\\:;'<>,.?/~`" + "Zz9\\\\end\\";
        Check(AcClientCommandLine.Token(special) == special, "no space, no quote: the password as it is");
        // The saved form (DPAPI over UTF-8, base64) gives every one of these characters back.
        foreach (string pw in new[] { special, "Aa1!@#$%^&*(_+-=[]{}|\\:;'<>,.?/~`*Zz9" + new string('(', 3) + "**", "€£§°´¨ü x" })
            Check(AccountPasswordProtection.TryUnprotect(AccountPasswordProtection.Protect(pw), out string back) && back == pw,
                "saved and read back unchanged: " + pw.Length + " characters");
        Check(ReadsBack(special, special), "and the client reads it back");

        // Quote marks: the client drops them whatever the launcher does; with them left out the rest
        // reaches the server as typed, and the line can't end on an open quote.
        // Negative control: the old C-runtime form reads back wrong, so the checks here mean something.
        static string OldQuote(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        Check(ArgAfter(AcClientCommandLine.Split("-v " + OldQuote("x\"y\"z\\") + " -h h"), "-v") is string old && old != "xyz\\",
            "negative control: the old escapes reach the client changed");
        Check(ReadsBack("p\"ss\\word", "pss\\word"), "one quote mark: left out, the backslash kept (was \\\\ before)");
        Check(ReadsBack("x\"y\"z\\", "xyz\\"), "two quote marks");
        Check(AcClientCommandLine.LosesCharacters("a\"b") && !AcClientCommandLine.LosesCharacters(special), "LosesCharacters: only a quote mark");
        Check(ReadsBack("pa ss \\ word\\", "pa ss \\ word\\"), "spaces: in quotes, a trailing backslash kept");
        Check(ReadsBack("ü€£§°´ ä", "ü€£§°´ ä"), "non-ASCII and a space");

        // Every password ThwargLauncher can log in with (no space: it doesn't quote) reads back the same
        // through ours; and ours reads back every password, quotes left out.
        const string Alphabet = "ab\\\" !@#$%^&*()-_=+[]{};:',.<>/?|`~ü";
        var rng = new Random(20261021);
        int thwargSame = 0, ours = 0;
        for (int n = 0; n < 4000; n++)
        {
            var sb = new System.Text.StringBuilder();
            int len = 1 + rng.Next(40);
            for (int i = 0; i < len; i++) sb.Append(Alphabet[rng.Next(Alphabet.Length)]);
            string pw = sb.ToString();
            string expected = AcClientCommandLine.ClientValue(pw);
            if (expected.Length == 0) continue;
            if (ReadsBack(pw, expected)) ours++;
            else { Check(false, "reads back: " + pw); break; }
            List<string>? thwarg = AcClientCommandLine.Split("-a acct -v " + pw + " -h host:9000");
            if (pw.IndexOf(' ') < 0 && thwarg != null && thwarg.Count == 6)
            {
                if (ArgAfter(thwarg, "-v") == expected) thwargSame++;
                else { Check(false, "same as ThwargLauncher: " + pw); break; }
            }
        }
        Check(ours > 3500 && thwargSame > 100, $"random passwords: {ours} read back, {thwargSame} the same as ThwargLauncher's");
    }

    // ── Extra Launch Arguments ───────────────────────────────────────────────
    // Labelled "appended", the box replaced the whole command line (account, password and host);
    // it now appends to every launch, so it may not carry the login switches (audit 2026-10-21 F10).
    private static void ExtraLaunchArgumentChecks()
    {
        Console.WriteLine("-- extra launch arguments");

        var account = new LaunchAccountProfile { AccountName = "SK" };
        account.SetPassword("pw x");
        string built = new AcLaunchArgumentBuilder().BuildArguments(Server(AcEmulatorKind.Ace), account);

        Check(AcLaunchArgumentBuilder.ExtraArgumentsProblem(null, out string none) == null && none == "", "none: fine, nothing added");
        Check(AcLaunchArgumentBuilder.ExtraArgumentsProblem("  \r\n\t ", out string blank) == null && blank == "", "only blanks: fine, nothing added");
        Check(AcLaunchArgumentBuilder.WithExtraArguments(built, "") == built, "nothing added: the account's line unchanged");

        Check(AcLaunchArgumentBuilder.ExtraArgumentsProblem("-nointro\r\n-w \"My Window\"", out string extra) == null && extra == "-nointro -w \"My Window\"",
            "line breaks become spaces: " + extra);
        string line = AcLaunchArgumentBuilder.WithExtraArguments(built, extra);
        List<string>? args = AcClientCommandLine.Split(line);
        Check(line.StartsWith(built + " ", StringComparison.Ordinal), "appended after the account's own arguments, not instead of them");
        Check(args != null && ArgAfter(args, "-a") == "SK" && ArgAfter(args, "-v") == "pw x" && ArgAfter(args, "-h") == "127.0.0.1:9000",
            "the client still reads this account, its password and its server");
        Check(args != null && args.Contains("-nointro") && ArgAfter(args, "-w") == "My Window", "and the extra arguments");

        // An old whole-line override saved in the box: refused rather than appended to every account.
        foreach (string sw in new[] { "-a", "-v", "-h", "-p", "-rodat", "-A", "-RODAT" })
        {
            string? problem = AcLaunchArgumentBuilder.ExtraArgumentsProblem($"-nointro {sw} other", out string n);
            Check(problem != null && n == "" && problem.Contains(sw, StringComparison.OrdinalIgnoreCase), $"{sw} refused: {problem}");
        }
        Check(AcLaunchArgumentBuilder.ExtraArgumentsProblem("-a acct -v secret -h other.example:9000", out _) is string old && !old.Contains("secret"),
            "a saved old override is refused, and the message doesn't repeat its password");
        Check(AcLaunchArgumentBuilder.ExtraArgumentsProblem("-host -apple \"-a b\"", out string lookalike) == null && lookalike.Length > 0,
            "switches that only start like a login switch, or a quoted value with one inside, are fine");
        Check(AcLaunchArgumentBuilder.ExtraArgumentsProblem("-w \"open", out _)?.Contains("quote") == true,
            "a quote left open is refused (the client would drop the whole line, login included)");
    }

    // ── The "show password" eye ──────────────────────────────────────────────
    // Player request (2026-10-21): the account editor's box starts empty ("leave blank to keep the
    // saved password") and the saved password was never shown. SavedPasswordReveal: the eye fills an
    // empty box with the saved password and empties it again if unchanged; saving it unchanged keeps
    // the saved one as it is.
    private static void PasswordEyeChecks()
    {
        Console.WriteLine("-- the show-password eye");
        const string Saved = "s3cret pw ü\\\"";

        var account = new LaunchAccountProfile { AccountName = "A" };
        account.SetPassword(Saved);
        string blob = account.PasswordProtected;
        var eye = SavedPasswordReveal.For(account);

        Check(eye.EyeOn("") == Saved && eye.FilledFromSaved, "eye on, box empty: the saved password, decrypted now");
        Check(eye.PasswordToSave(Saved) == null, "saved while shown and unchanged: the saved password is kept");
        Check(eye.EyeOff(Saved) == "" && !eye.FilledFromSaved, "eye off, unchanged: the box is empty again (blank = keep)");
        Check(eye.PasswordToSave("") == null && eye.PasswordToSave(null) == null, "blank box: kept");
        ApplySave(account, eye, "");
        Check(account.PasswordProtected == blob, "Save with a blank box: the blob is the same");
        Check(eye.EyeOn("") == Saved, "fill, then Save while it's shown");
        ApplySave(account, eye, Saved);
        Check(account.PasswordProtected == blob, "Save with the shown password unchanged: nothing re-encrypted, the blob is the same");
        Check(account.TryGetPasswordForLaunch(out string still, out _) && still == Saved, "and it still decrypts to the saved password");

        Check(eye.EyeOn("") == Saved, "fill again");
        Check(eye.EyeOff(Saved + "x") == Saved + "x" && eye.PasswordToSave(Saved + "x") == Saved + "x", "edited: stays in the box and is saved as the new password");
        Check(eye.EyeOn("") == Saved && eye.EyeOff("") == "", "filled, then emptied by the player: stays empty");
        Check(eye.EyeOn("") == Saved && eye.PasswordToSave("other") == "other", "filled, replaced while shown: the new one is saved");
        eye.EyeOff("other");

        Check(eye.EyeOn("typed") == "typed" && !eye.FilledFromSaved, "eye on with something typed: only reveals it");
        Check(eye.EyeOff("typed") == "typed" && eye.PasswordToSave("typed") == "typed", "eye off: what was typed stays and is saved");
        Check(eye.EyeOn(Saved) == Saved && eye.EyeOff(Saved) == Saved, "the saved password typed by hand is not taken for the eye's fill");

        eye.EyeOn("");
        eye.Reset();
        Check(!eye.FilledFromSaved && eye.PasswordToSave(Saved) == Saved, "after Reset the fill is forgotten (at most re-encrypts the same text)");

        ApplySave(account, eye, "new pw");
        Check(account.TryGetPasswordForLaunch(out string got, out _) && got == "new pw", "a new password typed: saved");

        var none = new LaunchAccountProfile { AccountName = "B" };
        var noneEye = SavedPasswordReveal.For(none);
        Check(noneEye.EyeOn("") == "" && !noneEye.FilledFromSaved, "no saved password: nothing to fill");
        Check(noneEye.EyeOn("x") == "x", "no saved password: reveals what's typed");

        // Settings copied from another Windows user or PC: the blob doesn't decrypt here.
        var copied = new LaunchAccountProfile { AccountName = "C", PasswordProtected = Convert.ToBase64String(new byte[64]) };
        var copiedEye = SavedPasswordReveal.For(copied);
        Check(copiedEye.EyeOn("") == "" && !copiedEye.FilledFromSaved, "unreadable saved password: nothing filled in");
        Check(copied.PasswordNeedsReentry, "and the profile says it needs re-entering");
        Check(copiedEye.EyeOn("") == "" && copiedEye.EyeOn("retyped") == "retyped", "needs re-entering: the eye only reveals what's typed");
        Check(copiedEye.PasswordToSave("retyped") == "retyped", "the retyped password is saved");

        var legacy = new LaunchAccountProfile { AccountName = "D", LegacyPlainPassword = "legacy pw" };
        Check(SavedPasswordReveal.For(legacy).EyeOn("") == "legacy pw", "a not-yet-migrated plain password is shown too");

        // The eye keeps no copy of the password: no string field, only the "filled" flag.
        Check(typeof(SavedPasswordReveal).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                .All(f => f.FieldType != typeof(string)), "SavedPasswordReveal holds no string");
        Check(SavedPasswordReveal.ShowTip.Length < 20 && SavedPasswordReveal.HideTip.Length < 20, "short tooltips");
    }

    /// <summary>The account editor's Save for the password box (AccountProfileDialog.SaveAndClose).</summary>
    private static void ApplySave(LaunchAccountProfile profile, SavedPasswordReveal eye, string? boxText)
    {
        if (eye.PasswordToSave(boxText) is string newPassword)
            profile.SetPassword(newPassword);
        eye.Reset();
    }

    /// <summary>The launcher's ACE line for <paramref name="password"/>, read back by acclient's rules.</summary>
    private static bool ReadsBack(string password, string expected)
    {
        string line = "-a " + AcClientCommandLine.Token("acct") + " -v " + AcClientCommandLine.Token(password)
                      + " -h " + AcClientCommandLine.Token("host:9000") + " -rodat off";
        List<string>? args = AcClientCommandLine.Split(line);
        return args != null && args.Count == 8 && ArgAfter(args, "-v") == expected && ArgAfter(args, "-h") == "host:9000";
    }

    private static string? ArgAfter(List<string>? args, string name)
    {
        if (args == null) return null;
        int i = args.IndexOf(name);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    private static bool Same(List<string>? got, params string[] want) => got != null && got.SequenceEqual(want);

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void Scenario(string name, string root, Action body)
    {
        Console.WriteLine($"-- {name}");
        _dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        AppSettingsStore.DirectoryOverride = _dir;
        try { body(); }
        catch (Exception ex) { Check(false, $"threw {ex.GetType().Name}: {ex.Message}"); }
    }

    private static AppSettings Sample() => new()
    {
        EnginePath = @"C:\Games\RynthCore\Runtime\RynthCore.Loader.dll",
        AccountProfiles =
        [
            new LaunchAccountProfile { Id = "a", AccountName = "A", InjectionMode = InjectionMode.RynthCore },
            new LaunchAccountProfile { Id = "b", AccountName = "B", InjectionMode = InjectionMode.DecalBridge },
            new LaunchAccountProfile { Id = "c", AccountName = "C", InjectionMode = InjectionMode.Decal },
        ],
    };

    private static InjectionMode Mode(AppSettings s, string id) => s.AccountProfiles.First(a => a.Id == id).InjectionMode;

    /// <summary><see cref="Sample"/> plus "h", a Headless account.</summary>
    private static AppSettings SampleWithHeadless()
    {
        AppSettings s = Sample();
        s.AccountProfiles.Add(new LaunchAccountProfile { Id = "h", AccountName = "H", InjectionMode = InjectionMode.Headless });
        return s;
    }

    private static string HeadlessSidecar => Path.Combine(_dir, HeadlessAccountModes.FileName);

    private static List<string> HeadlessIds()
    {
        if (!File.Exists(HeadlessSidecar)) return new();
        JsonNode node = JsonNode.Parse(File.ReadAllText(HeadlessSidecar))!;
        return node["HeadlessAccountIds"]!.AsArray().Select(n => (string)n!).ToList();
    }

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    /// <summary>The run config's shape as rynth-headless declares it (src\RynthCore.Headless.Cli\RunConfig.cs).</summary>
    private sealed class CliRunConfig
    {
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string ServerName { get; set; } = "";
        public List<string> Plugins { get; set; } = new();
        public List<CliSession> Sessions { get; set; } = new();
    }

    private sealed class CliSession
    {
        public string LauncherProfileId { get; set; } = "";
        public string Account { get; set; } = "";
        public string? Character { get; set; }
        public List<string> Commands { get; set; } = new();
        public int StaySeconds { get; set; }
        public bool Reconnect { get; set; }
    }

    /// <summary>
    /// A swap as the launcher leaves it: the failed per-server stash copied over the live file,
    /// plus the account's name-only stash (same bytes when <paramref name="legacyMatches"/>).
    /// </summary>
    private static (UserPrefsSwap Swap, string LegacyPath) PrefsSwapFiles(bool legacyMatches)
    {
        string stash = Path.Combine(_dir, "SK@Conquest.ini");
        string legacy = Path.Combine(_dir, "SK.ini");
        string live = Path.Combine(_dir, "UserPreferences.ini");
        byte[] bad = "[Display]\nResolution=2560x1440\nWindowed=False\n"u8.ToArray();
        byte[] good = "[Display]\nResolution=1920x1080\nWindowed=True\n"u8.ToArray();
        File.WriteAllBytes(stash, bad);
        File.WriteAllBytes(legacy, legacyMatches ? bad : good);
        File.WriteAllBytes(live, bad);
        return (new UserPrefsSwap(stash, live, bad, good), legacy);
    }

    private static void CheckSharedFilesSafe()
    {
        foreach (string path in new[] { Main_, Bak })
        {
            if (!File.Exists(path)) continue;
            string name = Path.GetFileName(path);
            Check(!File.ReadAllText(path).Contains("DecalBridge"), $"{name} has no \"DecalBridge\"");
            Check(OldLauncherCanRead(path), $"the old launcher can read {name}");
            // Every InjectionMode written is one the oldest launcher knows (never Headless or 3).
            foreach (JsonNode? a in JsonNode.Parse(File.ReadAllText(path))!["AccountProfiles"]?.AsArray() ?? new JsonArray())
            {
                string? mode = a?["InjectionMode"]?.ToString();
                Check(mode is null or "RynthCore" or "Decal", $"{name}: account {a?["Id"]} InjectionMode is {mode}");
            }
        }
    }

    private static string? SharedMode(string id)
    {
        JsonArray accounts = JsonNode.Parse(File.ReadAllText(Main_))!["AccountProfiles"]!.AsArray();
        return accounts.FirstOrDefault(a => (string?)a!["Id"] == id)?["InjectionMode"]?.ToString();
    }

    private static List<string> SidecarIds()
    {
        if (!File.Exists(Sidecar)) return new();
        JsonNode node = JsonNode.Parse(File.ReadAllText(Sidecar))!;
        return node["DecalBridgeAccountIds"]!.AsArray().Select(n => (string)n!).ToList();
    }

    /// <summary>An old launcher's save: the JSON round-trips with its edit applied.</summary>
    private static void OldLauncherEdit(Action<JsonArray> edit)
    {
        Check(OldLauncherCanRead(Main_), "old launcher loads the file before its edit");
        JsonNode root = JsonNode.Parse(File.ReadAllText(Main_))!;
        edit(root["AccountProfiles"]!.AsArray());
        File.Copy(Main_, Bak, overwrite: true);
        File.WriteAllText(Main_, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void SetMode(JsonArray accounts, string id, string mode)
    {
        foreach (JsonNode? a in accounts)
            if ((string?)a!["Id"] == id) a["InjectionMode"] = mode;
    }

    private enum OldInjectionMode { RynthCore = 0, Decal = 1 }

    private sealed class OldAccount
    {
        public string Id { get; set; } = "";
        public OldInjectionMode InjectionMode { get; set; }
    }

    private sealed class OldSettings
    {
        public List<OldAccount> AccountProfiles { get; set; } = new();
    }

    private static readonly JsonSerializerOptions OldOptions = new() { Converters = { new JsonStringEnumConverter() } };

    private static bool OldLauncherCanRead(string path)
    {
        try { JsonSerializer.Deserialize<OldSettings>(File.ReadAllText(path), OldOptions); return true; }
        catch (JsonException) { return false; }
    }

    // A launcher with the Decal bridge but before Headless (its InjectionMode stops at DecalBridge).
    private enum BridgeInjectionMode { RynthCore = 0, Decal = 1, DecalBridge = 2 }

    private sealed class BridgeAccount
    {
        public string Id { get; set; } = "";
        public BridgeInjectionMode InjectionMode { get; set; }
    }

    private sealed class BridgeSettings
    {
        public List<BridgeAccount> AccountProfiles { get; set; } = new();
    }

    private static bool BridgeLauncherCanRead(string path)
    {
        try
        {
            BridgeSettings? s = JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(path), OldOptions);
            // Strict like the old enum converter: a value it has no name for is "unreadable" too.
            return s != null && s.AccountProfiles.All(a => Enum.IsDefined(a.InjectionMode));
        }
        catch (JsonException) { return false; }
    }

    private static LaunchServerProfile Server(AcEmulatorKind kind) => new()
    {
        Id = "s", Name = "Test", Host = "127.0.0.1", Port = 9000, Emulator = kind
    };

    /// <summary>The message <paramref name="action"/> throws ("" when it doesn't throw).</summary>
    private static string Throws(Action action)
    {
        try { action(); return string.Empty; }
        catch (Exception ex) { return ex.Message; }
    }

    private static string PwOf(AppSettings s, string id) =>
        s.AccountProfiles.First(a => a.Id == id).TryGetPasswordForLaunch(out string pw, out _) ? pw : "<unreadable>";

    private static JsonObject RawAccount(string id) =>
        JsonNode.Parse(File.ReadAllText(Main_))!["AccountProfiles"]!.AsArray()
            .First(a => (string?)a!["Id"] == id)!.AsObject();

    private static void SetRaw(string id, string passwordProtected)
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(Main_))!;
        foreach (JsonNode? a in root["AccountProfiles"]!.AsArray())
            if ((string?)a!["Id"] == id) a["PasswordProtected"] = passwordProtected;
        File.WriteAllText(Main_, root.ToJsonString());
    }

    /// <summary>No file the store wrote in the test folder (settings, .bak, .tmp, decal-accounts) holds the password.</summary>
    private static void CheckNoPlainText(string password)
    {
        foreach (string file in Directory.GetFiles(_dir))
        {
            if (file.Contains(".corrupt-")) continue;   // planted by the test itself
            Check(!File.ReadAllText(file).Contains(password), $"{Path.GetFileName(file)} has no plain-text password");
        }
    }

    /// <summary>The store's notes (what the launcher logs and shows in the activity panel) hold no password.</summary>
    private static void CheckNotesClean(params string[] passwords)
    {
        foreach (string? note in new[] { AppSettingsStore.LastLoadDiagnostic, AppSettingsStore.LastMigrationNote,
                     AppSettingsStore.LastPasswordMigrationNote, AppSettingsStore.LastPasswordProblemNote })
            foreach (string pw in passwords)
                Check(note == null || !note.Contains(pw), "note has no password: " + note);
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"   FAIL: {what}");
    }
}

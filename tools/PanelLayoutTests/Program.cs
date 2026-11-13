using System.Diagnostics;
using System.Text;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.UI;

namespace RynthCore.Engine
{
    /// <summary>Stub of the engine's log (PanelStateStore writes load/save problems to it).</summary>
    internal static class RynthLog
    {
        internal static readonly List<string> Lines = new();
        internal static void UI(string msg) { lock (Lines) Lines.Add(msg); }
    }
}

namespace RynthCore.Engine.Plugins
{
    /// <summary>Stub of the one engine.json setting PanelStateStore reads.</summary>
    internal static class EngineSettings
    {
        public static bool RememberWindowLayout { get; set; } = true;
    }
}

namespace PanelLayoutTests
{
    /// <summary>
    /// Panel layout per character without AC: the key and file names, seeding a new character from the
    /// default, the switch at login and back at logout, a hot reload using the character's files from
    /// the first frame (the decision logic), two clients never writing each other's files, the setting
    /// off giving today's paths, and the /rc layout replies.
    /// </summary>
    internal static class Program
    {
        private static int _checks, _failed;
        private static string _case = "";
        private static string _root = "";

        // Placeholders only: no real server or character names.
        private const string ServerOne = "Server One", ServerTwo = "ServerTwo";
        private const string CharA = "Character A", CharB = "Character B";

        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--client")
                return Client(args.Skip(1).ToArray());

            _root = Path.Combine(Path.GetTempPath(), "rynth-panel-layout-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            try
            {
                Run(KeyAndFileNames);
                Run(SettingOffGivesTodaysPaths);
                Run(SeedingFromTheDefault);
                Run(ResetAndMakeDefaultFiles);
                Run(LatestTextWritesKeepTheNewest);
                Run(StoreSwitchesItsFile);
                Run(SwitchAtLoginAndBackAtLogout);
                Run(HotReloadUsesTheCharactersFilesFromTheFirstFrame);
                Run(IdentityThatNeverComes);
                Run(OneSwitchAtATimeAndReset);
                Run(TwoSessionsNeverShareAFile);
                Run(TwoClientsNeverWriteEachOthersFiles);
                Run(AutoSaveRules);
                Run(IdentityFromThePlayerWhenTheLaunchHasNone);
                Run(HotReloadWithTheNameNotKnownYet);
                Run(TheDefaultIsOnlyReadInUse);
                Run(CommandReplies);
                Run(PulledOnScreenNeverMovesTheSavedSpot);
                Run(TheBarKeepsItsHome);
                Run(HomeSurvivesAReloadThroughTheStore);
            }
            finally
            {
                try { Directory.Delete(_root, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
            Console.WriteLine(_failed == 0 ? "ALL PANEL LAYOUT TESTS PASSED." : $"{_failed} FAILED.");
            return _failed == 0 && _checks > 0 ? 0 : 1;
        }

        private static void Run(Action test)
        {
            try { test(); }
            catch (Exception ex) { _checks++; _failed++; Console.WriteLine($"  FAIL [{_case}] threw {ex.GetType().Name}: {ex.Message}"); }
        }

        private static string NewDir(string name)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string DefaultIni(string root) => Path.Combine(root, "suite", "RynthAi", "imgui.ini");

        // ── the key and the file names ───────────────────────────────────────

        private static void KeyAndFileNames()
        {
            Case("the key: server and character, the file names");
            Check(PanelLayoutPolicy.Owner(ServerOne, "") == null, "no character name: no owner (the default is used)");
            Check(PanelLayoutPolicy.Owner(ServerOne, "   ") == null, "a blank name is no name");
            Check(PanelLayoutPolicy.Owner(null, null) == null, "nothing known: no owner");
            LayoutOwner a = PanelLayoutPolicy.Owner("  " + ServerOne + " ", " " + CharA + "  ")!;
            Check(a.Server == ServerOne && a.Character == CharA, "names are trimmed");
            Check(a.Equals(PanelLayoutPolicy.Owner(ServerOne.ToUpperInvariant(), CharA.ToLowerInvariant())), "the same character in another case is the same owner");
            Check(a.GetHashCode() == PanelLayoutPolicy.Owner(ServerOne.ToUpperInvariant(), CharA.ToLowerInvariant())!.GetHashCode(), "...with the same hash");
            Check(!a.Equals(PanelLayoutPolicy.Owner(ServerTwo, CharA)), "the same name on another server is another owner");
            Check(!a.Equals(PanelLayoutPolicy.Owner(ServerOne, CharB)), "another character is another owner");
            Eq(PanelLayoutPolicy.Describe(a), CharA + " on " + ServerOne, "described as 'name on server'");
            Eq(PanelLayoutPolicy.Describe(PanelLayoutPolicy.Owner("", CharA)!), CharA, "no server: just the name");

            string root = @"C:\Users\Someone\AppData\Local\RynthCore";
            LayoutFiles f = PanelLayoutPolicy.CharacterFiles(root, a);
            Eq(f.PanelState, Path.Combine(root, "layouts", ServerOne, CharA, "panel_state.txt"), "panel_state.txt in layouts\\<server>\\<character>");
            Eq(f.ImGuiIni, Path.Combine(root, "layouts", ServerOne, CharA, "imgui.ini"), "imgui.ini beside it");
            Eq(f.Folder, Path.Combine(root, "layouts", ServerOne, CharA), "the layout's folder");
            Eq(PanelLayoutPolicy.CharacterFiles(root, PanelLayoutPolicy.Owner("", CharA)!).PanelState,
                Path.Combine(root, "layouts", "_", CharA, "panel_state.txt"), "no server: the '_' folder");
            Check(PanelLayoutPolicy.CharacterFiles(root, a).Folder != PanelLayoutPolicy.CharacterFiles(root, PanelLayoutPolicy.Owner(ServerTwo, CharA)!).Folder,
                "the same name on two servers: two folders");

            Case("safe file names");
            Eq(PanelLayoutPolicy.SafeSegment(CharA), CharA, "a plain name (letters, a space) stays as it is");
            Eq(PanelLayoutPolicy.SafeSegment("Some-Name'Here"), "Some-Name'Here", "hyphens and apostrophes are fine");
            Eq(PanelLayoutPolicy.SafeSegment(""), "_", "empty: '_'");
            Eq(PanelLayoutPolicy.SafeSegment(null), "_", "null: '_'");
            string colon = PanelLayoutPolicy.SafeSegment("A:B"), question = PanelLayoutPolicy.SafeSegment("A?B");
            Check(colon.StartsWith("A_B-", StringComparison.Ordinal) && question.StartsWith("A_B-", StringComparison.Ordinal), $"invalid characters become '_' ({colon}, {question})");
            Check(colon != question, "two names that clean up alike stay apart (a hash of the original)");
            Eq(PanelLayoutPolicy.SafeSegment("A:B"), colon, "the same name always gives the same folder");
            foreach (string bad in new[] { "a/b", "a\\b", "a*b", "a|b", "a<b>", "a\"b", "tab\there", "dots..." , "space ", "CON", "nul", "com1.txt" })
            {
                string s = PanelLayoutPolicy.SafeSegment(bad);
                Check(s.Length > 0 && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, $"'{bad}' -> '{s}' has no invalid characters");
                Check(!s.EndsWith('.') && !s.EndsWith(' '), $"'{bad}' -> '{s}' doesn't end with a dot or space");
                string stem = s.Split('.')[0].Split('-')[0];
                Check(!new[] { "CON", "NUL", "COM1" }.Contains(stem, StringComparer.OrdinalIgnoreCase), $"'{bad}' -> '{s}' isn't a device name");
                string dir = Path.Combine(_root, "names", s);
                Directory.CreateDirectory(dir);
                Check(Directory.Exists(dir), $"'{s}' can be made as a folder");
            }
            string longName = new string('x', 200);
            Check(PanelLayoutPolicy.SafeSegment(longName).Length <= 64 + 9, "a very long name is cut (plus its hash)");
        }

        private static void SettingOffGivesTodaysPaths()
        {
            Case("the setting off: today's files");
            string root = @"C:\Users\Someone\AppData\Local\RynthCore";
            string ini = @"C:\Games\RynthSuite\RynthAi\imgui.ini";
            LayoutOwner who = PanelLayoutPolicy.Owner(ServerOne, CharA)!;
            LayoutFiles today = PanelLayoutPolicy.DefaultFiles(root, ini);
            Eq(today.PanelState, Path.Combine(root, "panel_state.txt"), "the default panel_state.txt is the file every client used");
            Eq(today.ImGuiIni, ini, "the default imgui.ini is the file every client used");
            Check(PanelLayoutPolicy.Choose(perCharacter: false, inWorld: true, who) == null, "setting off, in the world: the default");
            Eq(PanelLayoutPolicy.FilesFor(root, ini, PanelLayoutPolicy.Choose(false, true, who)), today, "setting off: today's two files");
            Check(PanelLayoutPolicy.Choose(true, inWorld: false, who) == null, "setting on, at character select: the default");
            Check(PanelLayoutPolicy.Choose(true, true, null) == null, "setting on, in the world, no character name: the default");
            Eq(PanelLayoutPolicy.Choose(true, true, who), who, "setting on, in the world with a name: the character's");
            Eq(PanelLayoutPolicy.FilesFor(root, ini, who), PanelLayoutPolicy.CharacterFiles(root, who), "...and its files");

            string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "panel_state.txt");
            Eq(PanelStateStore.DefaultPath, expected, "PanelStateStore's default is today's %LOCALAPPDATA%\\RynthCore\\panel_state.txt (path only, not opened)");

            Case("the setting off: a session never leaves the default");
            var s = new PanelLayoutSession();
            LayoutSwitch? first = s.Tick(0, perCharacter: false, inWorld: false, 0, false, null);
            Check(first is { To: null }, "first frame: the default");
            s.MarkApplied(first!.Value.Gen);
            Check(s.Tick(10, false, inWorld: true, 10, identityRecorded: true, who) == null, "login with the setting off: no switch");
            Check(s.ReadyToDraw(11, true, 10), "and the panels draw at once (the default is in place)");
            Check(s.Tick(20, false, inWorld: false, 0, true, who) == null, "logout: still nothing to switch");
        }

        // ── seeding a new character from the default ─────────────────────────

        private static void SeedingFromTheDefault()
        {
            Case("seed plan");
            Eq(PanelLayoutPolicy.SeedPlan(false, false, true, true), (true, true), "nothing yet, default has both: copy both");
            Eq(PanelLayoutPolicy.SeedPlan(true, true, true, true), (false, false), "the character has both: copy nothing");
            Eq(PanelLayoutPolicy.SeedPlan(true, false, true, true), (false, true), "only the ini missing: copy the ini");
            Eq(PanelLayoutPolicy.SeedPlan(false, true, true, true), (true, false), "only panel_state missing: copy it");
            Eq(PanelLayoutPolicy.SeedPlan(false, false, false, false), (false, false), "no default (a fresh install): nothing to copy");
            Eq(PanelLayoutPolicy.SeedPlan(false, false, true, false), (true, false), "the default has no ini: only panel_state");

            Case("seeding files");
            string root = NewDir("seed");
            LayoutFiles defaults = PanelLayoutPolicy.DefaultFiles(root, DefaultIni(root));
            PanelLayoutFiles.WriteText(defaults.PanelState, "# default\r\npanel.Chat=open,10,20,300,200,docked,0,0\r\n");
            PanelLayoutFiles.WriteText(defaults.ImGuiIni, "[Window][Plugin Window##x]\nPos=5,6\nSize=100,100\n");
            LayoutFiles a = PanelLayoutPolicy.CharacterFiles(root, PanelLayoutPolicy.Owner(ServerOne, CharA)!);
            Eq(PanelLayoutFiles.SeedMissing(defaults, a), (true, true), "a new character: both copied");
            Eq(File.ReadAllText(a.PanelState), File.ReadAllText(defaults.PanelState), "panel_state.txt is the default's (nothing moves the first time)");
            Eq(File.ReadAllText(a.ImGuiIni), File.ReadAllText(defaults.ImGuiIni), "imgui.ini is the default's");
            File.WriteAllText(a.PanelState, "# mine\r\n");
            Eq(PanelLayoutFiles.SeedMissing(defaults, a), (false, false), "seeded once: the next login copies nothing");
            Eq(File.ReadAllText(a.PanelState), "# mine\r\n", "the character's own places are kept");
            File.Delete(a.ImGuiIni);
            Eq(PanelLayoutFiles.SeedMissing(defaults, a), (false, true), "one file gone: only that one is copied");
            Eq(File.ReadAllText(a.PanelState), "# mine\r\n", "...and the other is kept");

            string bare = NewDir("seed-nodefault");
            LayoutFiles noDefaults = PanelLayoutPolicy.DefaultFiles(bare, DefaultIni(bare));
            LayoutFiles b = PanelLayoutPolicy.CharacterFiles(bare, PanelLayoutPolicy.Owner(ServerOne, CharB)!);
            Eq(PanelLayoutFiles.SeedMissing(noDefaults, b), (false, false), "no default yet: nothing copied");
            Check(!File.Exists(b.PanelState) && !File.Exists(b.ImGuiIni), "...and nothing made up");
            Check(!File.Exists(noDefaults.PanelState) && !File.Exists(noDefaults.ImGuiIni), "the default is never written by seeding");
            NoTempFiles(root);
        }

        private static void ResetAndMakeDefaultFiles()
        {
            Case("reset: the default copied over the character's");
            string root = NewDir("reset");
            LayoutFiles defaults = PanelLayoutPolicy.DefaultFiles(root, DefaultIni(root));
            LayoutFiles a = PanelLayoutPolicy.CharacterFiles(root, PanelLayoutPolicy.Owner(ServerOne, CharA)!);
            PanelLayoutFiles.WriteText(defaults.PanelState, "# default\r\n");
            PanelLayoutFiles.WriteText(defaults.ImGuiIni, "[Window][D]\n");
            PanelLayoutFiles.WriteText(a.PanelState, "# old A\r\n");
            PanelLayoutFiles.WriteText(a.ImGuiIni, "[Window][OldA]\n");
            PanelLayoutFiles.CopyOver(defaults, a);
            Eq(File.ReadAllText(a.PanelState), "# default\r\n", "panel_state.txt is the default's");
            Eq(File.ReadAllText(a.ImGuiIni), "[Window][D]\n", "imgui.ini is the default's");
            File.Delete(defaults.ImGuiIni);
            PanelLayoutFiles.WriteText(a.ImGuiIni, "[Window][OldA]\n");
            PanelLayoutFiles.CopyOver(defaults, a);
            Eq(File.ReadAllText(a.ImGuiIni), "", "the default has no imgui.ini: the character's is emptied, not kept");

            Case("make default: the character's layout written as the default");
            PanelLayoutFiles.WriteText(a.ImGuiIni, "[Window][A]\n");
            PanelLayoutFiles.MakeDefault(a, defaults, "# from A\r\n", "[Window][A now]\n");
            Eq(File.ReadAllText(defaults.PanelState), "# from A\r\n", "the default panel_state.txt is the store's text");
            Eq(File.ReadAllText(defaults.ImGuiIni), "[Window][A now]\n", "the default imgui.ini is the context's windows");
            PanelLayoutFiles.MakeDefault(a, defaults, "# from A again\r\n", null);
            Eq(File.ReadAllText(defaults.ImGuiIni), "[Window][A]\n", "no context (ImGui off): the character's imgui.ini as saved");
            Eq(File.ReadAllText(a.PanelState), "# default\r\n", "the character's own files are not changed by it");
            NoTempFiles(root);
        }

        private static void LatestTextWritesKeepTheNewest()
        {
            Case("imgui.ini saves: the newest text wins, never an older one after it");
            var written = new List<(string, string)>();
            var w = new LatestTextWrites((p, t) => written.Add((p, t)));
            Check(!w.Flush("x"), "nothing queued: nothing written");
            w.Queue("x", "one");
            w.Queue("x", "two");
            Check(w.HasPending("x"), "queued");
            Check(w.Flush("x"), "flushed");
            Check(written.Count == 1 && written[0] == ("x", "two"), "only the newest is written, once");
            Check(!w.Flush("x"), "a later flush (the writer's queued job after shutdown wrote it) writes nothing");
            w.Queue("x", "three");
            w.Queue("y", "other");
            w.Flush("y");
            Check(written[^1] == ("y", "other") && w.HasPending("x"), "files are separate");

            string root = NewDir("latest");
            string path = Path.Combine(root, "a", "imgui.ini");
            var real = new LatestTextWrites(PanelLayoutFiles.WriteText);
            var threads = new List<Thread>();
            for (int t = 0; t < 4; t++)
            {
                int id = t;
                var th = new Thread(() => { for (int i = 0; i < 50; i++) { real.Queue(path, $"t{id} i{i}"); real.Flush(path); } });
                threads.Add(th);
                th.Start();
            }
            foreach (var th in threads) th.Join();
            real.Queue(path, "last");
            real.Flush(path);
            Eq(File.ReadAllText(path), "last", "concurrent saves: the file holds the last one, whole");
            NoTempFiles(root);
        }

        private static void StoreSwitchesItsFile()
        {
            Case("PanelStateStore: switching its file, atomic saves");
            string root = NewDir("store");
            string def = Path.Combine(root, "panel_state.txt");
            File.WriteAllText(def, "# default\r\nbar=1,2\r\npanel.Chat=open,10,20,300,200,docked,0,0\r\n");
            byte[] defBytes = File.ReadAllBytes(def);
            PanelStateStore.SwitchTo(def, flushCurrent: false);
            Eq(PanelStateStore.CurrentPath, def, "the store reads the file it was switched to");
            Check(PanelStateStore.TryGetPanel("Chat", out var chat) && chat.Left == 10, "its panels are read");

            string a = Path.Combine(root, "layouts", "s", "a", "panel_state.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(a)!);
            File.WriteAllText(a, "# A\r\npanel.Radar=open,500,40,200,200,docked,0,0\r\nbarlayout=2,0\r\n");
            PanelStateStore.SwitchTo(a, flushCurrent: false);
            Check(!PanelStateStore.TryGetPanel("Chat", out _), "nothing of the old layout is left in memory");
            Check(PanelStateStore.TryGetPanel("Radar", out var radar) && radar.Left == 500, "the new layout's panels");
            Eq(PanelStateStore.BarRows, (2, 0), "the new layout's bar rows");
            Check(PanelStateStore.BarPosition == null, "no bar row: no bar position (not the old one)");
            Eq(File.ReadAllBytes(def), defBytes, "switching away from the default never writes it");

            PanelStateStore.SetPanel("Radar", new PanelStateStore.PanelEntry(true, 600, 50, 200, 200));
            Check(File.ReadAllText(a).Contains("panel.Radar=open,600,50", StringComparison.Ordinal), "a save goes to the character's file");
            Eq(File.ReadAllBytes(def), defBytes, "...and not the default");
            Check(PanelStateStore.Snapshot().Contains("panel.Radar=open,600,50", StringComparison.Ordinal), "Snapshot is the text of what is in memory");
            NoTempFiles(root);

            string b = Path.Combine(root, "layouts", "s", "b", "panel_state.txt");
            PanelStateStore.SwitchTo(b, flushCurrent: true);
            Check(!File.Exists(b), "a character without a file starts empty (no file made by reading)");
            Check(!PanelStateStore.TryGetPanel("Radar", out _), "and empty in memory");
            PanelStateStore.SetBarPosition(7, 8);
            Check(File.Exists(b) && File.ReadAllText(b).Contains("bar=7,8", StringComparison.Ordinal), "its first save makes it");
            Check(!File.ReadAllText(a).Contains("bar=7,8", StringComparison.Ordinal), "the other character's file isn't touched");

            Case("PanelStateStore: Remember Open Windows off");
            RynthCore.Engine.Plugins.EngineSettings.RememberWindowLayout = false;
            try
            {
                string before = File.ReadAllText(b);
                PanelStateStore.SetBarPosition(9, 9);
                Eq(File.ReadAllText(b), before, "off: a change isn't saved");
                PanelStateStore.SwitchTo(a, flushCurrent: true);
                Eq(File.ReadAllText(b), before, "off: a switch doesn't write the file it leaves either");
                PanelStateStore.Flush();
                Check(File.ReadAllText(a).Contains("panel.Radar", StringComparison.Ordinal), "Save Layout Now still writes (the file in use)");
            }
            finally
            {
                RynthCore.Engine.Plugins.EngineSettings.RememberWindowLayout = true;
            }
            NoTempFiles(root);
        }

        // ── where a docked window draws versus its home (UI/PanelPlacement, 2026-11-12) ──
        //
        // 2026-11-12, reported: "a few of the panels still move around on reload and login (shortcuts, our own
        // toolbar)". The Shortcuts bar sits in the bottom-right corner (a fit-height plugin window: its
        // height follows its content), the toolbar's size follows its icons, rows and text size. Before,
        // a placement pulled onto a smaller screen, or for a taller first frame, was what the open saved
        // (ImGuiPanelHost.Apply -> Persist(entry.Pos)) and what the bar's size step saved, and only a
        // display change ever undid it. Numbers are the live layout's (1600x900 client).

        private static readonly System.Numerics.Vector2 Full = new(1600, 900);

        private static System.Numerics.Vector2 V(float x, float y) => new(x, y);

        private static void PulledOnScreenNeverMovesTheSavedSpot()
        {
            Case("placement: a pull onto the screen is drawn, never the home");
            Eq(HomePlace.OnScreen(V(1301, 811), V(299, 89), Full), V(1301, 811), "flush in the bottom-right corner fits as saved");
            Eq(HomePlace.OnScreen(V(1301, 811), V(299, 120), Full), V(1301, 780), "taller than saved: pulled up by the difference");
            Eq(HomePlace.OnScreen(V(1700, -5), V(100, 50), Full), V(1500, 0), "off the right and top: onto the screen");
            Eq(HomePlace.OnScreen(V(1301, 811), V(299, 89), V(0, 0)), V(1301, 811), "the display not known yet: drawn at its home");
            Eq(HomePlace.OnScreen(V(50, 50), V(2000, 1000), Full), V(0, 0), "bigger than the display: its top-left corner");
            Check(HomePlace.Near(V(1319.6f, 811), V(1320, 811)) && !HomePlace.Near(V(1319, 811), V(1320, 811)), "Near is half a pixel");

            Case("placement: login on a client smaller for a moment");
            var p = new HomePlace();
            p.Set(V(1301, 811), V(299, 89), V(800, 600));
            Eq(p.Drawn, V(501, 511), "on 800x600 it draws inside the screen");
            Check(p.Pulled, "...and knows it was pulled");
            Eq(p.Home, V(1301, 811), "the home (what the open saves) is still the saved spot");
            Check(p.Fit(V(299, 89), Full), "the client grows: drawn again");
            Eq(p.Drawn, V(1301, 811), "back in the corner");
            Check(!p.Pulled, "...not pulled any more");
            Check(!p.Fit(V(299, 89), Full), "nothing changed: nothing to do");

            Case("placement: a fit-height window, a taller then a shorter first frame");
            p = new HomePlace();
            p.Set(V(1301, 811), V(299, 89), Full);
            Check(!p.Pulled, "saved flush in the corner at its height: as saved");
            Check(p.Fit(V(299, 120), Full), "a taller frame (a key row, a bigger draw size) at the bottom edge");
            Eq(p.Drawn, V(1301, 780), "pulled up while it is taller");
            Eq(p.Home, V(1301, 811), "the home doesn't move");
            Check(p.Fit(V(299, 50), Full), "one line tall ('Your shortcuts show once you're in the world.')");
            Eq(p.Drawn, V(1301, 811), "back at its home");
            p.Fit(V(299, 89), Full);
            Eq(p.Drawn, V(1301, 811), "its real height: where the player left it");

            Case("placement: the saved row's height is stale (fit heights aren't saved)");
            p = new HomePlace();
            p.Set(V(1301, 811), V(299, 120), Full);   // the row says 120, the content is 89
            Eq(p.Drawn, V(1301, 780), "the first frame at the saved 120 is pulled up");
            p.Fit(V(299, 89), Full);
            Eq(p.Drawn, V(1301, 811), "fitted to 89 a frame later: back in the corner");
            Eq(p.Home, V(1301, 811), "the home was never the pulled spot");

            Case("placement: the player's drag is the new home");
            p.MoveTo(V(900, 700), V(299, 89), Full);
            Eq(p.Home, V(900, 700), "dragged: that is the home");
            Eq(p.Drawn, V(900, 700), "...and where it draws");
            p.MoveTo(V(1301, 811), V(299, 89), Full);
            p.Fit(V(299, 89), V(1280, 720));
            Eq(p.Drawn, V(981, 631), "a smaller client later pulls it in");
            Eq(p.Home, V(1301, 811), "...and keeps the player's spot");

            Case("placement: the load line says where and why");
            p = new HomePlace();
            p.Set(V(1301, 811), V(299, 89), V(800, 600));
            string line = p.Describe("Shortcuts", "its saved place");
            Check(line.Contains("Shortcuts placed at (501,511) 299x89 from its saved place (1301,811)", StringComparison.Ordinal)
                && line.Contains("pulled onto the 800x600 display", StringComparison.Ordinal), $"a pulled placement: {line}");
            p = new HomePlace();
            p.Set(V(1301, 811), V(299, 89), Full);
            line = p.Describe("Shortcuts", "its saved place");
            Check(line.EndsWith("on the 1600x900 display: as saved.", StringComparison.Ordinal), $"as saved: {line}");
            p = new HomePlace();
            p.Set(V(148, 0), V(0, 0), V(0, 0));
            line = p.Describe("the bar", "its saved bar row");
            Check(line.StartsWith("the bar placed at (148,0) from its saved bar row (148,0)", StringComparison.Ordinal)
                && line.Contains("isn't known yet", StringComparison.Ordinal), $"no size, no display yet: {line}");
            p = new HomePlace();
            p.Set(V(1301, 811), V(299, 89), Full);
            p.Fit(V(299, 120), Full);
            line = p.DescribePull("Shortcuts");
            Check(line.Contains("(1301,780)", StringComparison.Ordinal) && line.Contains("home (1301,811) at 299x120", StringComparison.Ordinal)
                && line.Contains("Not saved", StringComparison.Ordinal), $"the pull line: {line}");
            Check(!new HomePlace().Fit(V(10, 10), Full), "a window never placed has nothing to fit");
        }

        private static void TheBarKeepsItsHome()
        {
            Case("the toolbar: icons that come in as plugins load near the right edge");
            var bar = new HomePlace();
            bar.Set(V(1450, 0), V(0, 0), Full);   // the size isn't known before its first frame
            Eq(bar.Drawn, V(1450, 0), "first frame at its bar row");
            bar.Fit(V(120, 53), Full);
            Eq(bar.Drawn, V(1450, 0), "the engine's icons fit");
            Check(bar.Fit(V(260, 53), Full), "plugin windows add icons: wider than the room left");
            Eq(bar.Drawn, V(1340, 0), "pulled left while it is that wide");
            Eq(bar.Home, V(1450, 0), "the bar row's spot is kept (the old _sizeWatch saved the pull)");
            bar.Fit(V(140, 79), Full);
            Eq(bar.Drawn, V(1450, 0), "fewer icons a row (three rows): back where the player left it");

            Case("the toolbar: a text size step and a smaller client");
            bar.Fit(V(150, 400), V(1600, 300));
            Eq(bar.Drawn, V(1450, 0), "taller than a short client: its top stays at 0");
            bar.Fit(V(150, 60), V(1024, 768));
            Eq(bar.Drawn, V(874, 0), "a narrower client: pulled in");
            bar.Fit(V(150, 60), Full);
            Eq(bar.Drawn, V(1450, 0), "the client back to full size: back home");
        }

        private static void HomeSurvivesAReloadThroughTheStore()
        {
            Case("placement through panel_state.txt: what an open saves, read back by the next load");
            string root = NewDir("placement");
            string file = Path.Combine(root, "layouts", "s", "a", "panel_state.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "bar=1450,0\r\npanel.Shortcuts=open,1301,811,299,89,docked,0,0\r\n");
            PanelStateStore.SwitchTo(file, flushCurrent: false);

            // The load: a client 800x600 for a moment (or a first frame taller than saved).
            Check(PanelStateStore.TryGetPanel("Shortcuts", out var saved), "the row is read");
            var place = new HomePlace();
            place.Set(V((float)saved.Left, (float)saved.Top), V((float)saved.Width, (float)saved.Height), V(800, 600));
            Check(place.Pulled, "drawn pulled in");
            // ImGuiPanelHost.Persist (the open's save) writes the home:
            PanelStateStore.SetPanel("Shortcuts", new PanelStateStore.PanelEntry(true, place.Home.X, place.Home.Y, saved.Width, saved.Height));
            Check(File.ReadAllText(file).Contains("panel.Shortcuts=open,1301,811,299,89", StringComparison.Ordinal), "the file keeps the corner");
            Check(place.Drawn != place.Home, "(the drawn spot, which the open wrote before, is somewhere else)");

            // The bar's pull isn't written at all; only a drag is.
            var bar = new HomePlace();
            var row = PanelStateStore.BarPosition!.Value;
            bar.Set(V((float)row.Left, (float)row.Top), V(0, 0), Full);
            bar.Fit(V(260, 53), Full);
            Check(bar.Pulled, "the bar is pulled left");

            // The next load (a hot reload): the same file, read again.
            PanelStateStore.SwitchTo(file, flushCurrent: false);
            Check(PanelStateStore.TryGetPanel("Shortcuts", out var again) && again.Left == 1301 && again.Top == 811, "reloaded: Shortcuts in the corner");
            Eq(PanelStateStore.BarPosition, ((double)1450, (double)0), "reloaded: the bar where its row says");
            var next = new HomePlace();
            next.Set(V((float)again.Left, (float)again.Top), V((float)again.Width, (float)again.Height), Full);
            Eq(next.Drawn, V(1301, 811), "and drawn there");
            NoTempFiles(root);
        }

        // ── the session: when to switch, when panels may draw ───────────────

        private static readonly LayoutOwner WhoA = PanelLayoutPolicy.Owner(ServerOne, CharA)!;
        private static readonly LayoutOwner WhoB = PanelLayoutPolicy.Owner(ServerOne, CharB)!;

        private static void SwitchAtLoginAndBackAtLogout()
        {
            Case("the switch at login and back at logout");
            var s = new PanelLayoutSession();
            LayoutSwitch? sw = s.Tick(0, true, inWorld: false, 0, false, null);
            Check(sw is { To: null, FromKnown: false } && !sw.Value.FlushFrom, "first frame at character select: the default, nothing written out");
            Check(s.ReadyToDraw(1, false, 0), "nothing draws at character select anyway: never held");
            Check(s.Tick(5, true, false, 0, false, null) == null, "while it loads: no second switch");
            s.MarkApplied(sw!.Value.Gen);
            Check(s.Tick(10, true, false, 0, false, null) == null, "the default in place: nothing to do");

            const long login = 1000;
            Check(s.Tick(1000, true, inWorld: true, login, identityRecorded: false, null) == null, "login, the name not recorded yet: no switch");
            Check(!s.ReadyToDraw(1000, true, login), "...and panels wait");
            sw = s.Tick(1016, true, true, login, identityRecorded: true, WhoA);
            Check(sw is { } x1 && Equals(x1.To, WhoA) && x1.From == null && x1.FromKnown && !x1.FlushFrom,
                "the name is in: switch to the character's layout; the default is not written out");
            Check(!s.ReadyToDraw(1020, true, login), "panels still wait while it loads");
            s.MarkApplied(sw!.Value.Gen);
            Check(s.ReadyToDraw(1040, true, login), "applied: panels draw, in the character's places");
            Eq(s.AppliedLayout, WhoA, "the character's layout is the one in use");
            Check(s.Tick(1050, true, true, login, true, WhoA) == null, "in the world: nothing more to switch");

            sw = s.Tick(5000, true, inWorld: false, 0, true, WhoA);
            Check(sw is { } x2 && x2.To == null && Equals(x2.From, WhoA) && x2.FlushFrom,
                "logout: back to the default, the character's files written out first");
            Check(s.ReadyToDraw(5001, false, 0), "character select: not held");
            s.MarkApplied(sw!.Value.Gen);

            const long relogin = 9000;
            sw = s.Tick(9000, true, true, relogin, identityRecorded: true, WhoA);
            Check(sw is { } x3 && Equals(x3.To, WhoA) && x3.From == null && !x3.FlushFrom, "logging back in: the character's layout again");
            Check(!s.ReadyToDraw(9001, true, relogin), "...held until it is in place");
            s.MarkApplied(sw!.Value.Gen);
            Check(s.ReadyToDraw(9002, true, relogin), "...then drawn");

            Case("the setting changed in the world: switch, without hiding the panels");
            sw = s.Tick(9100, perCharacter: false, true, relogin, true, WhoA);
            Check(sw is { } x4 && x4.To == null && x4.FlushFrom, "off: to the default (the character's written out)");
            Check(s.ReadyToDraw(9101, true, relogin), "the panels keep drawing while it loads");
            s.MarkApplied(sw!.Value.Gen);
            sw = s.Tick(9200, perCharacter: true, true, relogin, true, WhoA);
            Check(sw is { } x5 && Equals(x5.To, WhoA) && !x5.FlushFrom, "on again: to the character's (the default not written)");
            s.MarkApplied(sw!.Value.Gen);
        }

        private static void HotReloadUsesTheCharactersFilesFromTheFirstFrame()
        {
            Case("hot reload in the world: the character's files from the first frame");
            // The new engine marks the login at init (MarkAlreadyComplete) and records the name; its
            // session is new. Simulated frame by frame: drawing is allowed only with the character's
            // layout applied, never with the default while in the world.
            foreach (bool frameBeforeLoginMark in new[] { false, true })
            {
                var s = new PanelLayoutSession();
                long login = 50_000;
                LayoutOwner? applied = null;
                bool anyApplied = false, drewWithDefault = false, drewWithCharacter = false;
                LayoutSwitch? inFlight = null;
                int switchesToCharacter = 0;
                for (long t = 49_990; t < 50_200; t += 16)
                {
                    bool inWorld = !frameBeforeLoginMark || t >= login;
                    bool recorded = inWorld && t >= login + 16;   // the name lands a frame after the mark
                    // A switch asked for one frame is loaded by the writer and applied the next.
                    if (inFlight is { } done)
                    {
                        s.MarkApplied(done.Gen);
                        applied = done.To;
                        anyApplied = true;
                        inFlight = null;
                    }
                    LayoutSwitch? sw = s.Tick(t, true, inWorld, inWorld ? login : 0, recorded, recorded ? WhoA : null);
                    if (sw is { } x)
                    {
                        inFlight = x;
                        if (Equals(x.To, WhoA)) switchesToCharacter++;
                    }
                    if (inWorld && s.ReadyToDraw(t, true, login))
                    {
                        if (!anyApplied || applied == null) drewWithDefault = true;
                        else if (Equals(applied, WhoA)) drewWithCharacter = true;
                    }
                }
                string how = frameBeforeLoginMark ? " (a frame ran before the login was marked)" : "";
                Check(!drewWithDefault, "panels never drew with the default layout in the world" + how);
                Check(drewWithCharacter, "panels drew with the character's layout" + how);
                Eq(switchesToCharacter, 1, "one switch to the character" + how);
            }

            Case("hot reload: the first switch goes straight to the character");
            var direct = new PanelLayoutSession();
            Check(direct.Tick(100, true, true, 100, identityRecorded: false, null) == null, "login marked, name not yet: no switch to the default first");
            LayoutSwitch? first = direct.Tick(116, true, true, 100, true, WhoA);
            Check(first is { } f && Equals(f.To, WhoA) && !f.FromKnown && !f.FlushFrom, "the first switch is the character's, nothing written out");
            Check(!direct.ReadyToDraw(117, true, 100), "no frame draws before it is applied");
            direct.MarkApplied(first!.Value.Gen);
            Check(direct.ReadyToDraw(132, true, 100), "applied: the first frame that draws uses it");

            Case("a login time not set yet in the world: wait for it");
            var early = new PanelLayoutSession();
            Check(early.Tick(10_000, true, inWorld: true, loginAtMs: 0, identityRecorded: true, WhoA) == null, "the flag without its time: no decision");
            Check(!early.ReadyToDraw(10_000, true, 0), "...and no drawing");
        }

        private static void IdentityThatNeverComes()
        {
            Case("a name that never comes: the default after the wait");
            var s = new PanelLayoutSession();
            s.MarkApplied(s.Tick(0, true, false, 0, false, null)!.Value.Gen);
            const long login = 2000;
            Check(s.Tick(2000 + PanelLayoutPolicy.IdentityWaitMs - 1, true, true, login, false, null) == null, "just before the wait ends: still waiting");
            Check(!s.ReadyToDraw(2000 + PanelLayoutPolicy.IdentityWaitMs - 1, true, login), "...not drawn");
            Check(s.Tick(2000 + PanelLayoutPolicy.IdentityWaitMs, true, true, login, false, null) == null, "the wait over, no name: the default already in use, no switch");
            Check(s.ReadyToDraw(2000 + PanelLayoutPolicy.IdentityWaitMs, true, login), "...and the panels draw with it");

            var t = new PanelLayoutSession();
            t.MarkApplied(t.Tick(0, true, false, 0, false, null)!.Value.Gen);
            LayoutSwitch? late = t.Tick(login + PanelLayoutPolicy.IdentityWaitMs, true, true, login, false, WhoB);
            Check(late is { } l && Equals(l.To, WhoB), "the wait over, a name known but never recorded: that name's layout");
        }

        private static void OneSwitchAtATimeAndReset()
        {
            Case("one switch at a time");
            var s = new PanelLayoutSession();
            LayoutSwitch first = s.Tick(0, true, false, 0, false, null)!.Value;
            Check(s.InFlight, "in flight");
            Check(s.Tick(1, true, true, 1, true, WhoA) == null, "the login during it: no second switch yet");
            Check(!s.ReadyToDraw(2, true, 1), "...and nothing draws (the default isn't the login's layout)");
            s.MarkApplied(first.Gen);
            Check(!s.ReadyToDraw(3, true, 1), "the default applied: still not the login's layout");
            LayoutSwitch next = s.Tick(4, true, true, 1, true, WhoA)!.Value;
            Check(Equals(next.To, WhoA) && next.Gen == first.Gen + 1, "after it: the character's");
            s.MarkApplied(first.Gen);
            Check(s.InFlight, "an old switch marked again doesn't count");
            s.MarkApplied(next.Gen);
            Check(!s.InFlight && s.ReadyToDraw(5, true, 1), "done");

            Case("a stuck switch stops holding the panels");
            var stuck = new PanelLayoutSession();
            stuck.Tick(10, true, true, 10, true, WhoA);
            Check(!stuck.ReadyToDraw(10 + PanelLayoutPolicy.ApplyGiveUpMs - 1, true, 10), "held while it may still come");
            Check(stuck.ReadyToDraw(10 + PanelLayoutPolicy.ApplyGiveUpMs, true, 10), "drawn after the give-up time");

            Case("reset: the character's files reseeded from the default");
            s.WantReseed();
            LayoutSwitch? reset = s.Tick(6, true, true, 1, true, WhoA);
            Check(reset is { } r && r.Reseed && Equals(r.To, WhoA) && Equals(r.From, WhoA) && !r.FlushFrom,
                "a reseed switch to the same character, its current files not written out (they're being replaced)");
            Check(s.ReadyToDraw(7, true, 1), "the panels keep drawing while it loads");
            s.MarkApplied(reset!.Value.Gen);
            Check(s.Tick(8, true, true, 1, true, WhoA) == null, "once: no second reset");
            var none = new PanelLayoutSession();
            none.MarkApplied(none.Tick(0, true, false, 0, false, null)!.Value.Gen);
            none.WantReseed();
            Check(none.Tick(1, true, false, 0, false, null) == null, "no character layout in use: a reset does nothing");
        }

        private static void TwoSessionsNeverShareAFile()
        {
            Case("two clients' sessions: the files each one writes are its own");
            string root = @"C:\Users\Someone\AppData\Local\RynthCore";
            string ini = @"C:\Games\RynthSuite\RynthAi\imgui.ini";
            LayoutFiles defaults = PanelLayoutPolicy.DefaultFiles(root, ini);
            var written = new Dictionary<LayoutOwner, HashSet<string>> { [WhoA] = new(), [WhoB] = new() };
            foreach (LayoutOwner who in new[] { WhoA, WhoB })
            {
                var s = new PanelLayoutSession();
                long t = 0;
                void Step(bool inWorld, long login)
                {
                    LayoutSwitch? sw = s.Tick(t += 16, true, inWorld, login, true, who);
                    if (sw is not { } x) return;
                    // What a switch writes: the layout left (when FlushFrom) and the one it goes to (seeding).
                    if (x.FlushFrom) { LayoutFiles f = PanelLayoutPolicy.FilesFor(root, ini, x.From); written[who].Add(f.PanelState); written[who].Add(f.ImGuiIni); }
                    if (x.To != null) { LayoutFiles f = PanelLayoutPolicy.FilesFor(root, ini, x.To); written[who].Add(f.PanelState); written[who].Add(f.ImGuiIni); }
                    s.MarkApplied(x.Gen);
                    // In the world the saves go to the layout in use.
                    if (inWorld && s.AppliedLayout is { } inUse) { LayoutFiles f = PanelLayoutPolicy.FilesFor(root, ini, inUse); written[who].Add(f.PanelState); written[who].Add(f.ImGuiIni); }
                }
                Step(false, 0); Step(true, 100); Step(true, 100); Step(false, 0); Step(true, 900); Step(true, 900);
            }
            Check(written[WhoA].Count > 0 && written[WhoB].Count > 0, "both wrote their layouts");
            Check(!written[WhoA].Overlaps(written[WhoB]), "client A and client B never write the same file");
            Check(!written[WhoA].Contains(defaults.PanelState) && !written[WhoA].Contains(defaults.ImGuiIni)
                  && !written[WhoB].Contains(defaults.PanelState) && !written[WhoB].Contains(defaults.ImGuiIni),
                "neither writes the default (in the world, at a switch or a logout)");
        }

        // Two clients at once: child processes of this test, each a client of one character.
        private static void TwoClientsNeverWriteEachOthersFiles()
        {
            Case("two clients at once, two characters: each writes only its own files");
            string root = NewDir("clients");
            string ini = DefaultIni(root);
            LayoutFiles defaults = PanelLayoutPolicy.DefaultFiles(root, ini);
            PanelLayoutFiles.WriteText(defaults.PanelState, "# default\r\npanel.Shared=open,1,2,300,200,docked,0,0\r\n");
            PanelLayoutFiles.WriteText(defaults.ImGuiIni, "[Window][Shared]\nPos=1,2\n");
            byte[] defPanel = File.ReadAllBytes(defaults.PanelState), defIni = File.ReadAllBytes(defaults.ImGuiIni);

            var clients = new[] { (ServerOne, CharA), (ServerOne, CharB), (ServerTwo, CharA) };
            var procs = new List<Process>();
            foreach (var (server, character) in clients)
            {
                string self = Environment.ProcessPath!;
                var psi = new ProcessStartInfo(self) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                // Run as "dotnet PanelLayoutTests.dll": the child is the same dll.
                if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    psi.ArgumentList.Add(typeof(Program).Assembly.Location);
                foreach (string a in new[] { "--client", root, ini, server, character, "150" }) psi.ArgumentList.Add(a);
                procs.Add(Process.Start(psi)!);
            }
            for (int i = 0; i < procs.Count; i++)
            {
                string output = procs[i].StandardOutput.ReadToEnd() + procs[i].StandardError.ReadToEnd();
                procs[i].WaitForExit();
                Check(procs[i].ExitCode == 0, $"client {i + 1} ran ({output.Trim()})");
            }

            Eq(File.ReadAllBytes(defaults.PanelState), defPanel, "the default panel_state.txt is unchanged");
            Eq(File.ReadAllBytes(defaults.ImGuiIni), defIni, "the default imgui.ini is unchanged");
            foreach (var (server, character) in clients)
            {
                LayoutOwner who = PanelLayoutPolicy.Owner(server, character)!;
                LayoutFiles f = PanelLayoutPolicy.CharacterFiles(root, who);
                string panel = File.Exists(f.PanelState) ? File.ReadAllText(f.PanelState) : "";
                string iniText = File.Exists(f.ImGuiIni) ? File.ReadAllText(f.ImGuiIni) : "";
                string mark = Marker(server, character);
                Check(panel.Contains("panel." + mark + "=", StringComparison.Ordinal), $"{PanelLayoutPolicy.Describe(who)}: its own panels are in its file");
                Check(panel.Contains("panel.Shared=", StringComparison.Ordinal), $"{PanelLayoutPolicy.Describe(who)}: seeded from the default");
                Check(panel.Contains("panel.Last=open,149,", StringComparison.Ordinal), $"{PanelLayoutPolicy.Describe(who)}: the last save is the one on disk");
                Check(iniText.Contains("[Window][" + mark + "]", StringComparison.Ordinal) && iniText.Contains("Pos=149,149", StringComparison.Ordinal),
                    $"{PanelLayoutPolicy.Describe(who)}: its imgui.ini holds its own last save");
                foreach (var (os, oc) in clients)
                {
                    if (os == server && oc == character) continue;
                    string other = Marker(os, oc);
                    Check(!panel.Contains(other, StringComparison.Ordinal) && !iniText.Contains(other, StringComparison.Ordinal),
                        $"{PanelLayoutPolicy.Describe(who)}'s files have nothing of {os}/{oc}");
                }
            }
            NoTempFiles(root);
        }

        private static string Marker(string server, string character) => ("M_" + server + "_" + character).Replace(' ', '_');

        /// <summary>A client (child process): character select on the default, a login to its character, saves, a logout.</summary>
        private static int Client(string[] a)
        {
            try
            {
                string root = a[0], ini = a[1], server = a[2], character = a[3];
                int saves = int.Parse(a[4]);
                LayoutFiles defaults = PanelLayoutPolicy.DefaultFiles(root, ini);
                PanelStateStore.SwitchTo(defaults.PanelState, flushCurrent: false);   // character select: the default
                LayoutOwner who = PanelLayoutPolicy.Owner(server, character)!;
                LayoutFiles mine = PanelLayoutPolicy.CharacterFiles(root, who);
                // The login: seed, switch (the default is never written out).
                PanelLayoutFiles.SeedMissing(defaults, mine);
                PanelStateStore.SwitchTo(mine.PanelState, flushCurrent: false);
                var iniWrites = new LatestTextWrites(PanelLayoutFiles.WriteText);
                string mark = Marker(server, character);
                for (int i = 0; i < saves; i++)
                {
                    PanelStateStore.SetPanel(mark, new PanelStateStore.PanelEntry(true, i, i, 200, 100));
                    PanelStateStore.SetPanel("Last", new PanelStateStore.PanelEntry(true, i, i, 200, 100));
                    PanelStateStore.SetBarPosition(i, i);
                    iniWrites.Queue(mine.ImGuiIni, $"[Window][{mark}]\nPos={i},{i}\nSize=100,100\n");
                    iniWrites.Flush(mine.ImGuiIni);
                }
                // The logout: the character's written out, back to the default (not written).
                PanelStateStore.SwitchTo(defaults.PanelState, flushCurrent: true);
                Console.WriteLine("client ok");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"client threw {ex.GetType().Name}: {ex.Message}");
                return 2;
            }
        }

        // ── saving rules and the commands ─────────────────────────────────

        private static void AutoSaveRules()
        {
            Case("saves of the layout in use (imgui.ini and panel_state.txt)");
            Check(PanelLayoutPolicy.MaySaveLayoutInUse(characterLayout: true, perCharacter: true), "a character's layout: always saved");
            Check(PanelLayoutPolicy.MaySaveLayoutInUse(false, perCharacter: false), "the setting off: the shared default is saved, as before");
            Check(!PanelLayoutPolicy.MaySaveLayoutInUse(false, perCharacter: true),
                "the setting on: the default is never saved (character select, a client that doesn't know its character yet, Save Layout Now)");
            string why = PanelLayoutPolicy.DefaultReadOnlyText(inWorld: true);
            Check(why.Contains(PanelLayoutPolicy.ReasonText(DefaultReason.NoCharacterName), StringComparison.Ordinal) && why.Contains("/rc layout default", StringComparison.Ordinal),
                "Save Layout Now on the default says why and what writes it");
            Check(PanelLayoutPolicy.DefaultReadOnlyText(false).Contains(PanelLayoutPolicy.ReasonText(DefaultReason.NotInWorld), StringComparison.Ordinal), "...at character select too");
        }

        // ── 2026-11-05: a client launched with no character picked ─────────────

        private static void IdentityFromThePlayerWhenTheLaunchHasNone()
        {
            Case("the key: the launch context's name, else the player's own");
            Eq(PanelLayoutPolicy.Identity(ServerOne, CharA, CharB), WhoA, "the launch context names one: that one (the key the other per-character files use)");
            Eq(PanelLayoutPolicy.Identity(ServerOne, "", CharB), WhoB, "the launch context names none: the player in the world");
            Eq(PanelLayoutPolicy.Identity(ServerOne, "  ", " " + CharB + " "), WhoB, "a blank launch name is none; the player's name is trimmed");
            Check(PanelLayoutPolicy.Identity(ServerOne, "", "") == null, "neither known: no owner");
            Check(PanelLayoutPolicy.Identity(ServerOne, null, null) == null, "nothing at all: no owner");
        }

        private static void HotReloadWithTheNameNotKnownYet()
        {
            // The live log (2026-11-05): "SessionState: recorded login session ... character=''" at the hot
            // reload, then "switched to the PC-wide default layout" and nothing later; that client saved
            // its places into the shared files all session.
            Case("hot reload, recorded with no name: waits for the player's name, never the default first");
            var s = new PanelLayoutSession();
            const long login = 70_000;
            Check(s.Tick(login, true, true, login, identityRecorded: true, null) == null, "recorded without a name: no switch yet (the player's name is on its way)");
            Check(!s.ReadyToDraw(login + 16, true, login), "...and the panels wait");
            Check(s.Tick(login + 500, true, true, login, true, null) == null, "still waiting half a second in");
            LayoutSwitch? sw = s.Tick(login + 800, true, true, login, true, WhoA);
            Check(sw is { } a && Equals(a.To, WhoA) && !a.FlushFrom, "the player's name read: straight to the character's layout, nothing written out");
            s.MarkApplied(sw!.Value.Gen);
            Check(s.ReadyToDraw(login + 816, true, login), "applied: the panels draw, in the character's places");

            Case("hot reload, the name later than the wait: the default only read, then the character's");
            var late = new PanelLayoutSession();
            Check(late.Tick(login, true, true, login, true, null) == null, "recorded without a name: waiting");
            sw = late.Tick(login + PanelLayoutPolicy.IdentityWaitMs, true, true, login, true, null);
            Check(sw is { } d && d.To == null && !d.FlushFrom, "the wait over, no name: the default, so the panels can draw");
            Check(!PanelLayoutPolicy.MaySaveLayoutInUse(characterLayout: sw!.Value.To != null, perCharacter: true),
                "...but nothing auto-saves into it (no default auto-save from a client in the world)");
            late.MarkApplied(sw.Value.Gen);
            Check(late.ReadyToDraw(login + PanelLayoutPolicy.IdentityWaitMs + 1, true, login), "the panels draw with it meanwhile");
            Check(late.Tick(login + PanelLayoutPolicy.IdentityWaitMs + 16, true, true, login, true, null) == null, "no name yet: nothing more");
            sw = late.Tick(login + PanelLayoutPolicy.IdentityWaitMs + 2000, true, true, login, true, WhoA);
            Check(sw is { } c && Equals(c.To, WhoA) && c.From == null && !c.FlushFrom,
                "the name known later: the switch to the character's layout, the default not written out");
            Check(late.ReadyToDraw(login + PanelLayoutPolicy.IdentityWaitMs + 2001, true, login), "the panels keep drawing while it loads");
            late.MarkApplied(sw!.Value.Gen);
            Eq(late.AppliedLayout, WhoA, "the character's layout is in use for the rest of the session");
            Check(PanelLayoutPolicy.MaySaveLayoutInUse(late.AppliedLayout != null, true), "...and its saves go to the character's files");
            Check(late.Tick(login + PanelLayoutPolicy.IdentityWaitMs + 3000, true, true, login, true, WhoA) == null, "no switch back");

            Case("a relogin in the same client: the new login waits for its own name again");
            sw = late.Tick(200_000, true, false, 0, true, null);
            Check(sw is { } o && o.To == null && o.FlushFrom, "logout: the character's written out, the default (read only) at character select");
            late.MarkApplied(sw!.Value.Gen);
            const long relogin = 300_000;
            Check(late.Tick(relogin, true, true, relogin, true, null) == null, "the new login, no name yet: waiting");
            sw = late.Tick(relogin + 400, true, true, relogin, true, WhoB);
            Check(sw is { } r && Equals(r.To, WhoB), "the other character's name: its layout");
        }

        private static void TheDefaultIsOnlyReadInUse()
        {
            Case("PanelStateStore: the PC-wide default is only read with the setting on");
            string root = NewDir("readonly");
            string def = Path.Combine(root, "panel_state.txt");
            File.WriteAllText(def, "# default\r\nbar=1,2\r\npanel.Shortcuts=open,10,20,300,120,docked,0,0\r\n");
            byte[] defBytes = File.ReadAllBytes(def);
            string a = Path.Combine(root, "layouts", "s", "a", "panel_state.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(a)!);
            File.WriteAllText(a, "# A\r\npanel.Shortcuts=open,900,700,300,120,docked,0,0\r\n");
            PanelStateStore.ReadOnlyPath = def;
            try
            {
                PanelStateStore.SwitchTo(def, flushCurrent: false);
                PanelStateStore.SetPanel("Shortcuts", new PanelStateStore.PanelEntry(true, 55, 66, 300, 120));
                PanelStateStore.SetBarPosition(3, 4);
                Eq(File.ReadAllBytes(def), defBytes, "a moved window and the bar on the default: not written");
                Check(PanelStateStore.TryGetPanel("Shortcuts", out var moved) && moved.Left == 55, "...kept in memory while it is in use");
                PanelStateStore.Flush();
                Eq(File.ReadAllBytes(def), defBytes, "Save Layout Now doesn't write it either");
                PanelStateStore.SwitchTo(a, flushCurrent: true);
                Eq(File.ReadAllBytes(def), defBytes, "switching away (even asked to write out what it leaves) doesn't write it");
                Check(PanelStateStore.TryGetPanel("Shortcuts", out var mine) && mine.Left == 900, "the character's layout: its own place, not the moves made on the default");

                Case("a plugin window registered after the switch gets its saved place, and its moves are saved");
                // PluginWindows registers its windows seconds after the login (the manifests); the store
                // holds every row of the file whether a window is registered or not.
                Check(PanelStateStore.TryGetPanel("Late Window", out _) == false, "not in the file: nothing saved yet");
                File.WriteAllText(a, "# A\r\npanel.Shortcuts=open,900,700,300,120,docked,0,0\r\npanel.Toolbar=open,400,10,500,40,docked,0,0\r\n");
                PanelStateStore.SwitchTo(a, flushCurrent: false);
                Check(PanelStateStore.TryGetPanel("Toolbar", out var tb) && tb.Open && tb.Left == 400 && tb.Top == 10,
                    "a window registered after the switch reads its saved place and open state from the character's layout");
                PanelStateStore.SetPanel("Toolbar", tb with { Left = 420, Top = 12 });
                Check(File.ReadAllText(a).Contains("panel.Toolbar=open,420,12,", StringComparison.Ordinal), "its move is saved to the character's file");
                Check(File.ReadAllText(a).Contains("panel.Shortcuts=open,900,700,", StringComparison.Ordinal), "...with the other windows' rows kept");
                Eq(File.ReadAllBytes(def), defBytes, "...and the default untouched");

                Case("a layout switch and plugin windows: they follow the new layout like engine panels");
                const WindowKind plugin = WindowKind.PluginWindow, engine = WindowKind.EnginePanel, script = WindowKind.ScriptWindow;
                Eq(PanelLayoutPolicy.StepAtSwitch(plugin, shown: false, remember: true, saved: true, savedOpen: true), SwitchStep.Open,
                    "registered (on the old layout) but not open, the new layout has it open: it opens there");
                Eq(PanelLayoutPolicy.StepAtSwitch(plugin, shown: true, remember: true, saved: true, savedOpen: false), SwitchStep.Close,
                    "opened from the old layout, the new one has it closed: it closes (not saved over)");
                Eq(PanelLayoutPolicy.StepAtSwitch(plugin, true, true, saved: true, savedOpen: true), SwitchStep.Place, "open in both: it moves to the new layout's place");
                Eq(PanelLayoutPolicy.StepAtSwitch(plugin, true, true, saved: false, savedOpen: false), SwitchStep.PlaceAndSave,
                    "new to the layout: placed, and saved there so it comes back there");
                Eq(PanelLayoutPolicy.StepAtSwitch(plugin, false, true, saved: false, savedOpen: false), SwitchStep.Leave, "not open, not in the layout: left alone");
                Eq(PanelLayoutPolicy.StepAtSwitch(plugin, false, remember: false, true, true), SwitchStep.Leave, "Remember Open Windows off: not opened");
                Eq(PanelLayoutPolicy.StepAtSwitch(plugin, true, remember: false, true, false), SwitchStep.Place, "...nor closed");
                Eq(PanelLayoutPolicy.StepAtSwitch(engine, false, true, true, true), SwitchStep.Open, "an engine panel: the same");
                Eq(PanelLayoutPolicy.StepAtSwitch(engine, true, true, true, false), SwitchStep.Close, "...both ways");
                Eq(PanelLayoutPolicy.StepAtSwitch(script, false, true, true, true), SwitchStep.Leave, "a script window opens when its script says, not from the layout");
                Eq(PanelLayoutPolicy.StepAtSwitch(script, true, true, true, false), SwitchStep.Place, "...and stays open (it moves)");

                Case("the setting off: the default is saved, as before");
                PanelStateStore.ReadOnlyPath = null;
                PanelStateStore.SwitchTo(def, flushCurrent: false);
                PanelStateStore.SetBarPosition(5, 6);
                Check(File.ReadAllText(def).Contains("bar=5,6", StringComparison.Ordinal), "no read-only mark: the shared file is written");
            }
            finally
            {
                PanelStateStore.ReadOnlyPath = null;
            }
            NoTempFiles(root);
        }

        private static void CommandReplies()
        {
            Case("/rc layout: parsing");
            Eq(PanelLayoutPolicy.ParseCommand(""), LayoutCommand.Status, "alone: the status");
            Eq(PanelLayoutPolicy.ParseCommand(null), LayoutCommand.Status, "null: the status");
            Eq(PanelLayoutPolicy.ParseCommand(" STATUS "), LayoutCommand.Status, "status, any case");
            Eq(PanelLayoutPolicy.ParseCommand("default"), LayoutCommand.MakeDefault, "default");
            Eq(PanelLayoutPolicy.ParseCommand("reset"), LayoutCommand.ResetAsk, "reset alone asks");
            Eq(PanelLayoutPolicy.ParseCommand("reset   Confirm"), LayoutCommand.ResetConfirm, "reset confirm, any spacing and case");
            foreach (string bad in new[] { "frob", "default now", "reset now", "confirm", "reset confirm please" })
                Eq(PanelLayoutPolicy.ParseCommand(bad), LayoutCommand.Invalid, $"'{bad}' is not a form");

            Case("/rc layout: the check before it runs (ChatCommandReplies)");
            foreach (string ok in new[] { "layout", "layout status", "layout default", "layout reset", "layout reset confirm", "LAYOUT  Reset  confirm" })
            {
                ChatVerbCheck c = ChatCommandReplies.Check(ok);
                Check(c.Problem == null && c.Verb?.Name == "layout", $"'/rc {ok}' runs");
            }
            foreach (string bad in new[] { "layout frob", "layout default now", "layout reset please" })
            {
                ChatVerbCheck c = ChatCommandReplies.Check(bad);
                Check(c.Verb?.Name == "layout" && c.Problem == PanelLayoutPolicy.Usage, $"'/rc {bad}' answers with the usage");
            }
            Eq(ChatCommandReplies.Usage("layout"), PanelLayoutPolicy.Usage, "the catalog's usage is the policy's");
            Check(ChatCommandReplies.CommandList().Contains("layout", StringComparison.Ordinal), "/rc lists layout");
            Check(ChatCommandReplies.KindOfLine(PanelLayoutPolicy.Usage) == ChatReplyKind.Fail, "the usage is a failure (red)");

            Case("/rc layout: the status");
            string root = @"C:\L", ini = @"C:\G\imgui.ini";
            LayoutFiles defaults = PanelLayoutPolicy.DefaultFiles(root, ini);
            LayoutFiles mine = PanelLayoutPolicy.CharacterFiles(root, WhoA);
            string[] own = PanelLayoutPolicy.StatusLines(WhoA, mine, DefaultReason.None, defaults, true);
            Check(own[0].Contains(PanelLayoutPolicy.Describe(WhoA), StringComparison.Ordinal) && own[0].Contains(mine.PanelState, StringComparison.Ordinal)
                  && own[0].Contains(mine.ImGuiIni, StringComparison.Ordinal), "a character's: whose, and both files");
            Check(own.Length == 2 && own[1].Contains(defaults.PanelState, StringComparison.Ordinal) && own[1].Contains("/rc layout default", StringComparison.Ordinal)
                  && own[1].Contains("/rc layout reset", StringComparison.Ordinal), "...and the default's files and the two commands");
            Check(ChatCommandReplies.KindOfLines(own) == ChatReplyKind.Ok, "a state, not a failure");
            foreach (DefaultReason why in new[] { DefaultReason.SettingOff, DefaultReason.NotInWorld, DefaultReason.NoCharacterName, DefaultReason.WaitingForName })
            {
                string[] d = PanelLayoutPolicy.StatusLines(null, defaults, why, defaults, perCharacter: why != DefaultReason.SettingOff);
                Check(d[0].StartsWith("Panel layout: the PC-wide default (", StringComparison.Ordinal) && d[0].Contains(PanelLayoutPolicy.ReasonText(why), StringComparison.Ordinal)
                      && d[0].Contains(defaults.PanelState, StringComparison.Ordinal) && d[0].Contains(defaults.ImGuiIni, StringComparison.Ordinal),
                    $"the default ({why}): why, and both files");
                Check(ChatCommandReplies.KindOfLines(d) == ChatReplyKind.Ok, $"the default ({why}): a state");
            }
            string[] off = PanelLayoutPolicy.StatusLines(null, defaults, DefaultReason.NotRunning, defaults, false);
            Check(off.Length == 1 && ChatCommandReplies.KindOfLine(off[0]) == ChatReplyKind.Fail, "not running: a failure, says why");

            Case("/rc layout default and reset: why they can't run");
            Check(PanelLayoutPolicy.CharacterLayoutProblem("x", true, true, true, WhoA, false) == null, "a character's layout in use: they run");
            (bool running, bool per, bool inWorld, LayoutOwner? inUse, bool switching, string want)[] cases =
            {
                (false, true, true, WhoA, false, PanelLayoutPolicy.ReasonText(DefaultReason.NotRunning)),
                (true, false, true, WhoA, false, PanelLayoutPolicy.ReasonText(DefaultReason.SettingOff)),
                (true, true, false, null, false, PanelLayoutPolicy.ReasonText(DefaultReason.NotInWorld)),
                (true, true, true, WhoA, true, "still switching"),
                (true, true, true, null, false, PanelLayoutPolicy.ReasonText(DefaultReason.NoCharacterName)),
            };
            foreach (var c in cases)
            {
                string? p = PanelLayoutPolicy.CharacterLayoutProblem("make a default from this client", c.running, c.per, c.inWorld, c.inUse, c.switching);
                Check(p != null && p.StartsWith("Can't make a default from this client: ", StringComparison.Ordinal) && p.Contains(c.want, StringComparison.Ordinal)
                      && p.EndsWith(PanelLayoutPolicy.Usage, StringComparison.Ordinal), $"why not ({c.want}), with the usage: {p}");
                Check(ChatCommandReplies.KindOfLine(p ?? "") == ChatReplyKind.Fail, $"a failure (red): {c.want}");
            }

            Case("/rc layout default and reset: the answers");
            string ask = PanelLayoutPolicy.ResetAskReply(WhoA, defaults);
            Check(ask.Contains(PanelLayoutPolicy.Describe(WhoA), StringComparison.Ordinal) && ask.Contains("/rc layout reset confirm", StringComparison.Ordinal)
                  && ask.Contains(defaults.PanelState, StringComparison.Ordinal), "reset asks first: whose, from which file, and the confirm");
            Check(ChatCommandReplies.KindOfLine(ask) == ChatReplyKind.Ok, "...as a state (nothing failed)");
            string done = PanelLayoutPolicy.ResetDoneReply(WhoA);
            Check(done.StartsWith(PanelLayoutPolicy.Describe(WhoA), StringComparison.Ordinal) && ChatCommandReplies.KindOfLine(done) == ChatReplyKind.Ok, "reset done");
            string made = PanelLayoutPolicy.MakeDefaultDoneReply(WhoA, defaults);
            Check(made.Contains(defaults.PanelState, StringComparison.Ordinal) && made.Contains(defaults.ImGuiIni, StringComparison.Ordinal)
                  && ChatCommandReplies.KindOfLine(made) == ChatReplyKind.Ok, "make default done: the default's files");
            string failed = PanelLayoutPolicy.FailedReply("make the default", "access denied");
            Eq(failed, "Can't make the default: access denied.", "a failed file job says what and why");
            Check(ChatCommandReplies.KindOfLine(failed) == ChatReplyKind.Fail, "...as a failure");
            Check(ChatCommandReplies.KindOfLine(PanelLayoutPolicy.BusyReply) == ChatReplyKind.Fail && PanelLayoutPolicy.BusyReply.EndsWith(PanelLayoutPolicy.Usage, StringComparison.Ordinal),
                "busy: a failure, with the usage");

            Case("Settings > Display: the layout in use and the reset button");
            string line = PanelLayoutPolicy.SettingsLine(WhoA, mine, DefaultReason.None);
            Check(line.StartsWith("In use: ", StringComparison.Ordinal) && line.Contains(PanelLayoutPolicy.Describe(WhoA), StringComparison.Ordinal)
                  && line.Contains(mine.Folder, StringComparison.Ordinal), $"a character's: whose, and its folder ({line})");
            Check(!line.Contains(defaults.PanelState, StringComparison.Ordinal), "...not the default's file");
            foreach (DefaultReason why in new[] { DefaultReason.SettingOff, DefaultReason.NotInWorld, DefaultReason.NoCharacterName, DefaultReason.WaitingForName })
            {
                string d = PanelLayoutPolicy.SettingsLine(null, defaults, why);
                Check(d.StartsWith("In use: the PC-wide default (", StringComparison.Ordinal) && d.Contains(PanelLayoutPolicy.ReasonText(why), StringComparison.Ordinal)
                      && d.Contains(defaults.PanelState, StringComparison.Ordinal), $"the default ({why}): why, and its file");
            }
            Check(PanelLayoutPolicy.SettingsLine(null, defaults, DefaultReason.NotRunning).Contains(PanelLayoutPolicy.ReasonText(DefaultReason.NotRunning), StringComparison.Ordinal),
                "not running: says so");
            string confirm = PanelLayoutPolicy.ResetConfirmText(WhoA);
            Check(confirm.Contains(PanelLayoutPolicy.Describe(WhoA), StringComparison.Ordinal) && confirm.EndsWith("can't be undone.", StringComparison.Ordinal),
                "the reset asks first: whose layout, and that it can't be undone");
            Check(PanelLayoutPolicy.ResetConfirmText(null).Contains("nothing to reset", StringComparison.Ordinal), "no character's layout: nothing to reset");
            // The button runs /rc layout reset confirm, the form the command takes.
            Eq(PanelLayoutPolicy.ParseCommand("reset confirm"), LayoutCommand.ResetConfirm, "the button's command is reset confirm");
        }

        // ── helpers ────────────────────────────────────────────────────────

        private static void NoTempFiles(string dir)
        {
            string[] left = Directory.GetFiles(dir, "*.tmp", SearchOption.AllDirectories);
            Check(left.Length == 0, "no temp file left behind" + (left.Length > 0 ? ": " + string.Join(", ", left) : ""));
        }

        private static void Case(string name)
        {
            _case = name;
            Console.WriteLine($"-- {name}");
        }

        private static void Check(bool ok, string what)
        {
            _checks++;
            if (ok) return;
            _failed++;
            Console.WriteLine($"  FAIL [{_case}] {what}");
        }

        private static void Eq<T>(T actual, T expected, string what) =>
            Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{what}: expected '{Show(expected)}', got '{Show(actual)}'");

        private static void Eq(byte[] actual, byte[] expected, string what) =>
            Check(actual.AsSpan().SequenceEqual(expected), what);

        private static string Show<T>(T v) => v switch
        {
            null => "null",
            string s => s,
            _ => v.ToString() ?? "",
        };
    }
}

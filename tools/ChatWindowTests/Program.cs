using System.Text.RegularExpressions;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;

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

namespace ChatWindowTests
{
    /// <summary>
    /// The extra chat windows without AC: their keys, the first free one, the tab each shows, the
    /// /rc chatwindow parser, and the panel layout values that keep each window's tab.
    /// </summary>
    internal static class Program
    {
        private static int _fails, _checks;
        private static string _case = "";

        private static int Main()
        {
            Keys();
            FirstFree();
            Tabs();
            Parser();
            StoreValues();
            Sources();
            Console.WriteLine(_fails == 0 ? $"PASS ({_checks} checks)" : $"FAIL: {_fails} of {_checks} checks");
            return _fails == 0 ? 0 : 1;
        }

        private static void Case(string name) { _case = name; Console.WriteLine("-- " + name); }

        private static void Check(bool ok, string what)
        {
            _checks++;
            if (ok) return;
            _fails++;
            Console.WriteLine($"   FAIL [{_case}] {what}");
        }

        private static void Eq<T>(T got, T want, string what) =>
            Check(EqualityComparer<T>.Default.Equals(got, want), $"{what} (got '{got}', want '{want}')");

        private static readonly string[] AllTabs =
            { "All", "Chat", "Channels", "System", "Combat", "Rynth", "Items", "Other", "Loot Log" };

        private static void Keys()
        {
            Case("window keys and numbers");
            Eq(ChatWindows.MaxWindows, 6, "six extra windows");
            Eq(ChatWindows.Key(2), "Chat 2", "the first extra window is Chat 2 (the main chat is Chat)");
            Eq(string.Join("|", ChatWindows.Keys()), "Chat 2|Chat 3|Chat 4|Chat 5|Chat 6|Chat 7", "every key, in order");
            Check(ChatWindows.TryNumber("Chat 4", out int n) && n == 4, "Chat 4 is window 4");
            Check(ChatWindows.TryNumber("chat 7", out n) && n == 7, "any case");
            Check(!ChatWindows.TryNumber("Chat", out _), "the main chat isn't an extra window");
            Check(!ChatWindows.TryNumber("Chat 1", out _), "nor window 1");
            Check(!ChatWindows.TryNumber("Chat 8", out _), "nor past the last");
            Check(!ChatWindows.TryNumber("Chat 2x", out _), "nor a longer word");
            Check(!ChatWindows.TryNumber("ChatFilters", out _), "nor the Filters panel");
            Check(!ChatWindows.TryNumber(null, out _), "nor nothing");
            Eq(ChatWindows.TabValueKey(3), "chatwin.3.tab", "where window 3's tab is saved");
            Eq(ChatWindows.HeaderText(3, "Combat"), "Chat 3 · Combat", "its header");
        }

        private static void FirstFree()
        {
            Case("the first free window");
            Eq(ChatWindows.FirstFree(_ => false), 2, "none open: Chat 2");
            var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Chat 2", "Chat 4" };
            Eq(ChatWindows.FirstFree(open.Contains), 3, "a gap is filled first");
            Eq(ChatWindows.FirstFree(k => k != "Chat 7"), 7, "the last one");
            Eq(ChatWindows.FirstFree(_ => true), 0, "all open: none (0)");
        }

        private static void Tabs()
        {
            Case("the tab a window shows");
            Eq(ChatWindows.EffectiveTab("Combat", AllTabs), "Combat", "its own");
            Eq(ChatWindows.EffectiveTab("combat", AllTabs), "Combat", "spelled as the tab is now");
            Eq(ChatWindows.EffectiveTab("loot log", AllTabs), "Loot Log", "a created tab, with a space");
            Eq(ChatWindows.EffectiveTab("Gone", AllTabs), "All", "a deleted tab: All (like the main chat)");
            Eq(ChatWindows.EffectiveTab("", AllTabs), "All", "nothing saved: All");
            Eq(ChatWindows.EffectiveTab(null, AllTabs), "All", "nothing saved (null): All");
            Eq(ChatWindows.Resolve("  Items ", AllTabs), "Items", "typed with spaces around it");
            Eq(ChatWindows.Resolve("Itemz", AllTabs), null, "no such tab: null");

            Case("a rename followed");
            Eq(ChatWindows.FollowRename("Loot Log", "loot log", "Drops"), "Drops", "the window showing it follows");
            Eq(ChatWindows.FollowRename("Combat", "Loot Log", "Drops"), null, "another window: no change");
            Eq(ChatWindows.FollowRename(null, "Loot Log", "Drops"), null, "nothing saved: no change");
            Eq(ChatWindows.FollowRename("Loot Log", "Loot Log", "  "), null, "a blank new name: no change");
        }

        private static void Parser()
        {
            Case("/rc chatwindow parsing");
            Eq(ChatWindows.Parse("").Verb, ChatWindowVerb.List, "alone: the list");
            Eq(ChatWindows.Parse(" list ").Verb, ChatWindowVerb.List, "list");
            Eq(ChatWindows.Parse("status").Verb, ChatWindowVerb.List, "status");
            var open = ChatWindows.Parse("open Loot Log");
            Check(open.Verb == ChatWindowVerb.Open && open.Tab == "Loot Log", "open <tab with a space>");
            var bare = ChatWindows.Parse("open");
            Check(bare.Verb == ChatWindowVerb.Open && bare.Tab == "", "open alone: the main chat's tab");
            Eq(ChatWindows.Parse("new Combat").Verb, ChatWindowVerb.Open, "new = open");
            var close = ChatWindows.Parse("close 3");
            Check(close.Verb == ChatWindowVerb.Close && close.Number == 3, "close 3");
            close = ChatWindows.Parse("close chat 3");
            Check(close.Verb == ChatWindowVerb.Close && close.Number == 3, "close chat 3");
            Eq(ChatWindows.Parse("CLOSE ALL").Verb, ChatWindowVerb.CloseAll, "close all, any case");
            var tab = ChatWindows.Parse("tab 5 Loot Log");
            Check(tab.Verb == ChatWindowVerb.Tab && tab.Number == 5 && tab.Tab == "Loot Log", "tab 5 <tab>");
            tab = ChatWindows.Parse("tab chat 5 Combat");
            Check(tab.Verb == ChatWindowVerb.Tab && tab.Number == 5 && tab.Tab == "Combat", "tab chat 5 <tab>");

            foreach (string bad in new[] { "frob", "close", "close 9", "close 1", "close 3 x", "close x", "tab", "tab 3", "tab 9 Combat", "tab Combat" })
            {
                var c = ChatWindows.Parse(bad);
                Check(c.Verb == ChatWindowVerb.Error && c.Error.EndsWith(ChatWindows.Usage, StringComparison.Ordinal),
                    $"'/rc chatwindow {bad}': why, then the usage (got {c.Verb}: {c.Error})");
                Check(!c.Error.StartsWith("Usage", StringComparison.Ordinal) || c.Error == ChatWindows.Usage, $"'{bad}': one usage line");
            }
            Check(ChatWindows.Parse("frob").Error.StartsWith("Unknown", StringComparison.Ordinal), "an unknown verb starts 'Unknown' (a failure in red)");
            Check(ChatWindows.Usage.StartsWith("Usage: /rc chatwindow", StringComparison.Ordinal), "the usage names the command");
        }

        private static string NewDir(string name)
        {
            string d = Path.Combine(Path.GetTempPath(), "rynth-chatwindowtests-" + Environment.ProcessId, name);
            Directory.CreateDirectory(d);
            return d;
        }

        private static void StoreValues()
        {
            Case("a window's tab in the panel layout (PanelStateStore values)");
            string root = NewDir("store");
            try
            {
                string a = Path.Combine(root, "a", "panel_state.txt");
                string b = Path.Combine(root, "b", "panel_state.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(a)!);
                const string aText = "# A\r\npanel.Chat=open,10,20,300,200,docked,0,0\r\npanel.Chat 2=open,400,20,300,200,floating,900,100\r\n";
                File.WriteAllText(a, aText);
                PanelStateStore.SwitchTo(a, flushCurrent: false);
                int gen = PanelStateStore.Generation;
                Check(!PanelStateStore.TryGetValue("chatwin.2.tab", out string v) && v == "", "nothing saved: no value");
                Check(PanelStateStore.TryGetPanel("Chat 2", out var w2) && w2.Open && w2.Floating && w2.FloatingLeft == 900,
                    "an extra window's place, open and popped-out state are an ordinary panel row");
                Eq(File.ReadAllText(a), aText, "reading writes nothing");

                PanelStateStore.SetValue("chatwin.2.tab", "Loot Log");
                Check(PanelStateStore.TryGetValue("chatwin.2.tab", out v) && v == "Loot Log", "set, then read back");
                Check(File.ReadAllText(a).Contains("value.chatwin.2.tab=Loot Log", StringComparison.Ordinal), "saved in the layout's file");
                Check(File.ReadAllText(a).Contains("panel.Chat 2=open,400,20,300,200,floating,900,100", StringComparison.Ordinal), "the panel rows are kept");
                string before = File.ReadAllText(a);
                PanelStateStore.SetValue("chatwin.2.tab", "Loot Log");
                Eq(File.ReadAllText(a), before, "the same value again writes nothing new");

                PanelStateStore.SetValue("chatwin.3.tab", "Tab = with, commas");
                Check(PanelStateStore.TryGetValue("chatwin.3.tab", out v) && v == "Tab = with, commas", "'=' and ',' in a value survive in memory");

                PanelStateStore.SwitchTo(b, flushCurrent: true);
                Check(PanelStateStore.Generation != gen, "a switch moves the generation (open windows read their tab again)");
                Check(!PanelStateStore.TryGetValue("chatwin.2.tab", out _), "another layout: its own values (none)");
                Check(!File.Exists(b), "reading a layout without a file makes none");

                PanelStateStore.SwitchTo(a, flushCurrent: false);
                Check(PanelStateStore.TryGetValue("chatwin.2.tab", out v) && v == "Loot Log", "back: the value is read from the file");
                Check(PanelStateStore.TryGetValue("chatwin.3.tab", out v) && v == "Tab = with, commas", "'=' and ',' survive the file");

                PanelStateStore.SetValue("chatwin.3.tab", "line\r\nbreak");
                Check(PanelStateStore.TryGetValue("chatwin.3.tab", out v) && !v.Contains('\n') && !v.Contains('\r'), "line breaks are dropped (one row per value)");
                PanelStateStore.SetValue("chatwin.3.tab", null);
                Check(!PanelStateStore.TryGetValue("chatwin.3.tab", out _), "null removes it");
                Check(!File.ReadAllText(a).Contains("chatwin.3", StringComparison.Ordinal), "and its row");

                Case("PanelStateStore values: a file from an older build, and Remember Open Windows off");
                string old = Path.Combine(root, "old", "panel_state.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(old)!);
                File.WriteAllText(old, "panel.Chat=open,1,2,3,4\r\n");
                PanelStateStore.SwitchTo(old, flushCurrent: false);
                Check(PanelStateStore.TryGetPanel("Chat", out _) && !PanelStateStore.TryGetValue("chatwin.2.tab", out _), "no value rows: loads as before");
                RynthCore.Engine.Plugins.EngineSettings.RememberWindowLayout = false;
                try
                {
                    string oldText = File.ReadAllText(old);
                    PanelStateStore.SetValue("chatwin.2.tab", "Combat");
                    Check(PanelStateStore.TryGetValue("chatwin.2.tab", out v) && v == "Combat", "off: kept in memory");
                    Eq(File.ReadAllText(old), oldText, "off: the file isn't written (as panel places)");
                }
                finally { RynthCore.Engine.Plugins.EngineSettings.RememberWindowLayout = true; }
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(root)!, recursive: true); } catch { }
            }
        }

        private static string? FindRepoFile(string relative)
        {
            string? dir = AppContext.BaseDirectory;
            while (dir != null)
            {
                string p = Path.Combine(dir, relative);
                if (File.Exists(p)) return p;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        private static void Sources()
        {
            Case("sources: the catalog's usage, and nothing saves a tab by itself");
            string? replies = FindRepoFile(Path.Combine("src", "RynthCore.Engine", "Compatibility", "ChatCommandReplies.cs"));
            Check(replies != null && File.ReadAllText(replies).Contains("\"" + ChatWindows.Usage + "\"", StringComparison.Ordinal),
                "ChatCommandReplies' chatwindow row has ChatWindows.Usage");

            string? face = FindRepoFile(Path.Combine("src", "RynthCore.Engine", "ImGui", "Panels", "ChatWindowFace.cs"));
            Check(face != null, "found ChatWindowFace.cs");
            if (face != null)
            {
                string src = File.ReadAllText(face);
                int from = src.IndexOf("public void OnShown()", StringComparison.Ordinal);
                int to = src.IndexOf("// ── Opening, closing", StringComparison.Ordinal);
                Check(from > 0 && to > from, "found the face's open/draw code");
                if (from > 0 && to > from)
                {
                    string body = src.Substring(from, to - from);
                    Check(!body.Contains("SaveTab(", StringComparison.Ordinal) && !body.Contains("SetValue(", StringComparison.Ordinal),
                        "opening, reading and drawing a window never saves its tab (only a player's pick, open or rename does)");
                }
            }

            string? view = FindRepoFile(Path.Combine("src", "RynthCore.Engine", "ImGui", "Panels", "ChatView.cs"));
            Check(view != null, "found ChatView.cs");
            if (view != null)
                Eq(Regex.Matches(File.ReadAllText(view), @"_selectTab\(").Count, 2,
                    "a view picks a tab in two places only: a click in the tab list, and a tab it created");
        }
    }
}

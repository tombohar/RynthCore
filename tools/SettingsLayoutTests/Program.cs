using System.Collections;
using System.Reflection;

namespace SettingsLayoutTests
{
    // Reaches RynthCore.Engine's internal settings schema and QuickSettings by reflection. See the csproj.
    internal static class Program
    {
        private static int _fails, _asserts;
        private static void Check(bool cond, string msg)
        {
            _asserts++;
            if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
        }

        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        private static Assembly _engine = null!;
        private static Type _schema = null!, _quick = null!;

        private static IList Tabs => (IList)_schema.GetField("Tabs", All)!.GetValue(null)!;
        private static string Name(object tab) => (string)tab.GetType().GetProperty("Name")!.GetValue(tab)!;
        private static IList Rows(object tab) => (IList)tab.GetType().GetProperty("Rows")!.GetValue(tab)!;
        private static string Label(object row) => (string?)row.GetType().GetProperty("Label")!.GetValue(row) ?? "";
        private static string Kind(object row) => row.GetType().GetProperty("Kind")!.GetValue(row)!.ToString()!;
        private static string? KeyOf(object row) => (string?)_quick.GetMethod("KeyOf", All)!.Invoke(null, new[] { row });
        private static object? RowOf(string key) => _quick.GetMethod("RowOf", All)!.Invoke(null, new object[] { key });

        private static IEnumerable<string> AllKeys()
        {
            foreach (object tab in Tabs)
                foreach (object row in Rows(tab))
                    if (KeyOf(row) is { } key) yield return key;
        }

        private static int Main(string[] args)
        {
            _engine = Assembly.Load("RynthCore.Engine");
            _schema = _engine.GetType("RynthCore.Engine.UI.Data.SettingsSchema", throwOnError: true)!;
            _quick = _engine.GetType("RynthCore.Engine.UI.QuickSettings", throwOnError: true)!;

            // -dump <file>: every pin key of this build's layout, one per line (how OldPinKeys.txt was made).
            if (args.Length == 2 && args[0] == "-dump")
            {
                File.WriteAllLines(args[1], AllKeys());
                Console.WriteLine($"wrote {AllKeys().Count()} keys to {args[1]}");
                return 0;
            }

            TabList();
            OldPins();
            OldTabNames();
            Moves();
            HuntAndDirtyFighting();
            return Finish();
        }

        // ── Hunt On Login and the Dirty Fighting rotation (rows added 2026-11-11, after the regroup) ──────
        private static void HuntAndDirtyFighting()
        {
            Type settings = _engine.GetType("RynthCore.Engine.UI.Data.RynthAiSettings", throwOnError: true)!;
            Type ctxType = _engine.GetType("RynthCore.Engine.UI.Data.RynthAiSettingsJsonContext", throwOnError: true)!;
            object ctx = ctxType.GetProperty("Default", All)!.GetValue(null)!;
            var info = (System.Text.Json.Serialization.Metadata.JsonTypeInfo)ctxType.GetProperty("RynthAiSettings", All)!.GetValue(ctx)!;
            object Parse(string json) => System.Text.Json.JsonSerializer.Deserialize(json, info)!;
            object? P(object s, string name) => settings.GetProperty(name, All)!.GetValue(s);

            object? Row(string tab, string label)
            {
                foreach (object t in Tabs)
                    if (Name(t) == tab)
                        foreach (object r in Rows(t))
                            if (Label(r).Trim() == label) return r;
                return null;
            }
            bool Visible(object row, object s) => (bool)row.GetType().GetMethod("IsVisible")!.Invoke(row, new[] { s })!;
            double Get(object row, object s) => (double)((Delegate)row.GetType().GetProperty("Get")!.GetValue(row)!).DynamicInvoke(s)!;
            void Set(object row, object s, double v) => ((Delegate)row.GetType().GetProperty("Set")!.GetValue(row)!).DynamicInvoke(s, v);

            object? hunt = Row("General", "Hunt On Login"), patrol = Row("General", "Patrol On Login");
            object? rot = Row("Combat", "Dirty Fighting rotation"), order = Row("Combat", "Effect order"), then = Row("Combat", "Then attack");
            Check(hunt != null && patrol != null, "General has Patrol On Login and Hunt On Login");
            Check(rot != null && order != null && then != null, "Combat has the Dirty Fighting rotation, its order and its Then attack");
            if (hunt == null || patrol == null || rot == null || order == null || then == null) return;
            Check(KeyOf(hunt) == "General/Starting Up/Hunt On Login", $"Hunt On Login sits under Starting Up (got {KeyOf(hunt)})");
            Check(KeyOf(rot) == "Combat/Attack Power & Height/Dirty Fighting rotation", $"the rotation sits under Attack Power & Height (got {KeyOf(rot)})");

            // A RynthAi that doesn't send them: the rows hide, and nothing is claimed.
            object older = Parse("{\"PatrolOnLogin\":true}");
            Check(!Visible(hunt, older) && !Visible(rot, older) && !Visible(order, older) && !Visible(then, older), "hidden for a RynthAi that doesn't send them");
            Check(P(older, "HuntOnLogin") == null && P(older, "DfHeightRotation") == null, "...and their values stay null (a save leaves them alone)");

            // One or the other, as RynthAi keeps them.
            object s1 = Parse("{\"PatrolOnLogin\":true,\"HuntOnLogin\":false}");
            Check(Visible(hunt, s1) && Get(hunt, s1) == 0, "Hunt On Login shows once RynthAi sends it");
            Set(hunt, s1, 1);
            Check((bool?)P(s1, "HuntOnLogin") == true && (bool)P(s1, "PatrolOnLogin")! == false, "ticking Hunt On Login clears Patrol On Login");
            Set(patrol, s1, 1);
            Check((bool)P(s1, "PatrolOnLogin")! && (bool?)P(s1, "HuntOnLogin") == false, "ticking Patrol On Login clears Hunt On Login");
            Set(patrol, s1, 0);
            Check(!(bool)P(s1, "PatrolOnLogin")! && (bool?)P(s1, "HuntOnLogin") == false, "unticking one leaves the other as it was");
            object s2 = Parse("{\"PatrolOnLogin\":false}");
            Set(patrol, s2, 1);
            Check(P(s2, "HuntOnLogin") == null, "Patrol On Login with a RynthAi that has no Hunt On Login sends no Hunt On Login");

            // The rotation: its two choices show while it's on; RynthAi's values both ways.
            object d = Parse("{\"DfHeightRotation\":false,\"DfHeightOrder\":\"HML\",\"DfDefaultHeight\":-1}");
            Check(Visible(rot, d) && !Visible(order, d) && !Visible(then, d), "the rotation shows; its order and Then attack only while it's on");
            Set(rot, d, 1);
            Check((bool?)P(d, "DfHeightRotation") == true && Visible(order, d) && Visible(then, d), "on: the order and Then attack show");
            Check(Get(order, d) == 0 && Get(then, d) == 0, "HML is the first order; -1 is 'Attack Height setting'");
            Set(order, d, 5);
            Set(then, d, 3);
            Check((string?)P(d, "DfHeightOrder") == "LMH" && (int?)P(d, "DfDefaultHeight") == 2, "the last order is LMH; High is 2");
            object d2 = Parse("{\"DfHeightRotation\":true,\"DfHeightOrder\":\"mlh\",\"DfDefaultHeight\":0}");
            Check(Get(order, d2) == 3 && Get(then, d2) == 1, "RynthAi's MLH (any case) and Low (0) read back as theirs");
            var items = (string[])order.GetType().GetProperty("Items")!.GetValue(order)!;
            Check(items.Length == 6 && items[0] == "High, Medium, Low" && items[5] == "Low, Medium, High", "the order choices read as RynthAi's window shows them");
        }

        // ── The tabs, in order, and the sidebar's groups ───────────────────────────────
        private static void TabList()
        {
            string[] want =
            {
                "Interface", "Game Client", "Hotkeys",
                "General", "Recharge", "Combat", "Spell Combat", "Ranges", "Navigation", "Buffing", "Looting", "Inventory", "Vendoring",
                "Diagnostics",
            };
            string[] got = Tabs.Cast<object>().Select(Name).ToArray();
            Check(got.SequenceEqual(want), $"the tabs, in order (got {string.Join(", ", got)})");

            var groups = new List<string>();
            foreach (object tab in Tabs)
                if (tab.GetType().GetProperty("Group")!.GetValue(tab) is string g) groups.Add(Name(tab) + "=" + g);
            Check(groups.SequenceEqual(new[] { "Interface=RynthCore · this PC", "General=RynthAi · saved in the profile", "Diagnostics=Support" }),
                $"the sidebar's three group headings sit over Interface, General and Diagnostics (got {string.Join("; ", groups)})");

            // Every pinnable row has its own key (a duplicate would make two rows one pin).
            var keys = AllKeys().ToList();
            Check(keys.Count == keys.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                "no two pinnable rows share a pin key: " + string.Join(", ", keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)));

            // No tab starts or ends on an empty heading, and no tab is empty.
            foreach (object tab in Tabs)
            {
                IList rows = Rows(tab);
                Check(rows.Count > 0, $"{Name(tab)} has rows");
                if (rows.Count == 0) continue;
                Check(Kind(rows[rows.Count - 1]!) is not ("Section" or "Spacer"), $"{Name(tab)} doesn't end on a heading or a spacer");
            }
        }

        // ── Every pin of the old layout still finds its row ───────────────────────────
        private static void OldPins()
        {
            string file = Path.Combine(AppContext.BaseDirectory, "OldPinKeys.txt");
            Check(File.Exists(file), "OldPinKeys.txt is beside the test");
            if (!File.Exists(file)) return;
            string[] old = File.ReadAllLines(file).Where(l => l.Trim().Length > 0).ToArray();
            Check(old.Length > 250, $"OldPinKeys.txt holds the old layout's keys ({old.Length})");

            MethodInfo? migrate = _quick.GetMethod("MigrateKey", All);
            Check(migrate != null, "QuickSettings.MigrateKey exists");
            if (migrate == null) return;
            string? Migrate(string k) => (string?)migrate.Invoke(null, new object[] { k });

            // Rows taken out of the panel on purpose (their values stay in the payload).
            var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Misc//Auto Cram",                                              // the same switch as Inventory > Packs > Enable Autocram
                "Recharge/Helper Settings (%) — NOT WIRED YET/Heal Others",    // inert sliders: hidden until they do something
                "Recharge/Helper Settings (%) — NOT WIRED YET/Re-stam Others",
                "Recharge/Helper Settings (%) — NOT WIRED YET/Infuse Others",
            };
            int moved = 0;
            foreach (string key in old)
            {
                string? now = Migrate(key);
                if (gone.Contains(key))
                {
                    // Auto Cram's pin goes to the one Autocram switch left; the inert sliders' pins go nowhere.
                    if (key == "Misc//Auto Cram") Check(now != null && RowOf(now) is { } r && Label(r) == "Enable Autocram", $"{key} -> Enable Autocram (got {now})");
                    else Check(now == null, $"{key}: its row is gone, so the pin leads nowhere (got {now})");
                    continue;
                }
                // Labels and sections can hold '/', so the old key must end with the row's whole label.
                object? row = now == null ? null : RowOf(now);
                Check(row != null && key.EndsWith("/" + Label(row).Trim(), StringComparison.Ordinal) && now!.EndsWith("/" + Label(row).Trim(), StringComparison.Ordinal),
                    $"{key} -> the row with the same label (got {now ?? "nothing"})");
                if (now != null && !string.Equals(now, key, StringComparison.OrdinalIgnoreCase)) moved++;
            }
            Check(moved > 80, $"a good share of the old keys moved ({moved})");

            // The owner's two pins (2026-11-11), by name.
            Check(Migrate("Misc//Video patch") == "Game Client/Performance/Video patch", "Misc//Video patch -> Game Client/Performance/Video patch");
            Check(Migrate("Spell Combat/Ring Spell Override/Min Ring Targets") == "Spell Combat/Ring Spell Override/Min Ring Targets", "a pin that didn't move stays as it is");
            Check(Migrate("Nowhere/At All/Nothing") == null, "a key no layout ever had leads nowhere (kept in the file, not drawn)");
        }

        // ── /rc settings <old name> still opens the tab ───────────────────────────────
        private static void OldTabNames()
        {
            MethodInfo? find = _schema.GetMethod("FindTab", All);
            Check(find != null, "SettingsSchema.FindTab exists");
            if (find == null) return;
            string? Find(string n) => find.Invoke(null, new object[] { n }) is { } t ? Name(t) : null;
            foreach (var (from, to) in new[]
            {
                ("Display", "Interface"), ("ui", "Interface"), ("misc", "General"), ("Melee Combat", "Combat"), ("melee", "Combat"),
                ("Crafting", "Combat"), ("interface", "Interface"), ("game client", "Game Client"), ("inventory", "Inventory"),
                ("buffing", "Buffing"), ("Hotkeys", "Hotkeys"), ("Diagnostics", "Diagnostics"),
            })
                Check(Find(from) == to, $"'{from}' opens {to} (got {Find(from) ?? "nothing"})");
            Check(Find("No Such Tab") == null, "an unknown name opens nothing");
        }

        // ── Where a few rows went (the ones other code finds by tab or label) ─────────
        private static void Moves()
        {
            string? Where(string label)
            {
                foreach (object tab in Tabs)
                    foreach (object row in Rows(tab))
                        if (Label(row).Trim() == label && KeyOf(row) is { } k) return k;
                return null;
            }
            foreach (var (label, key) in new[]
            {
                ("Hide retail radar", "Interface/Hide The Game's Own/Hide retail radar"),
                ("Lock your vitals plate", "Interface/Your Vitals/Lock your vitals plate"),
                ("Borderless Windowed", "Game Client/Game Window/Borderless Windowed"),
                ("Insert Key Releases the UI", "Hotkeys//Insert Key Releases the UI"),
                ("Enable FPS Limit", "Game Client/Performance/Enable FPS Limit"),
                ("Close Client on Server Disconnect", "Game Client/Disconnects And Updates/Close Client on Server Disconnect"),
                ("Vaulted Dungeons", "General/Server Features/Vaulted Dungeons"),
                ("Auto Fellowship Management", "General/Fellowship/Auto Fellowship Management"),
                ("Stop Macro On Death", "General/Safety Stops/Stop Macro On Death"),
                ("Recall On Death", "General/On Death/Recall On Death"),
                ("Buff Spell Interval (ms)", "Buffing//Buff Spell Interval (ms)"),
                ("Use Buff Items", "Buffing//Use Buff Items"),
                ("Targeting", "Combat/Targeting/Targeting"),
                ("Attempts Before Blacklist", "Combat/Targeting/Attempts Before Blacklist"),
                ("Enable Raycasting", "Combat/Line Of Sight/Enable Raycasting"),
                ("Magic Response", "Combat/Hitting the Environment/Magic Response"),
                ("Enable Missile Crafting", "Combat/Missile Ammo Crafting/Enable Missile Crafting"),
                ("Enable Autostack", "Inventory/Packs/Enable Autostack"),
                ("Give Interval (ms)", "Inventory/Giving And Trading/Give Interval (ms)"),
                ("Describe Items When Selected", "Inventory/Item Info/Describe Items When Selected"),
                ("Monster Range", "Ranges/Standard Ranges (Yards)/Monster Range"),
                ("Corpse Max (yd)", "Ranges/Corpse Acquisition (Yards)/Corpse Max (yd)"),
                ("Coverage", "Navigation/Dungeon Patrol/Coverage"),
            })
                Check(Where(label) == key, $"{label} is at {key} (got {Where(label) ?? "nothing"})");
            Check(Where("Auto Cram") == null, "the duplicate Auto Cram row is gone (Inventory > Packs > Enable Autocram stays)");
            Check(Where("Heal Others") == null, "the inert Helper sliders are hidden");
        }

        private static int Finish()
        {
            Console.WriteLine(_fails == 0 ? $"PASS ({_asserts} checks)" : $"FAIL: {_fails} of {_asserts} checks");
            return _fails == 0 ? 0 : 1;
        }
    }
}

using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CombatSettingsTests
{
    // Reaches RynthCore.Engine's internal settings mirror, schema and Monsters rule by reflection.
    // See the csproj.
    internal static class Program
    {
        private static int _fails, _asserts;
        private static void Check(bool cond, string msg)
        {
            _asserts++;
            if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
        }

        private const string Ns = "RynthCore.Engine.UI.Data.";
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        // RynthAi's SettingsBridgePayload names, with the type each mirror field must have.
        private static readonly (string Name, Type Type, string Default)[] Mirrors =
        {
            ("VariablePowerMin", typeof(int?), "50"),
            ("VariablePowerMax", typeof(int?), "100"),
            ("MissileEnvironmentResponse", typeof(int?), "0"),
            ("MagicEnvironmentResponse", typeof(int?), "0"),
            ("EnvironmentImpactLimit", typeof(int?), "2"),
            ("EnvironmentImpactWindowSec", typeof(int?), "20"),
            ("EnvironmentApproachMeters", typeof(int?), "6"),
            ("EnvironmentMinDistance", typeof(int?), "3"),
            ("EnvironmentSkipSec", typeof(int?), "30"),
            ("KitePullMeters", typeof(int?), "20"),
            ("KiteTagFirst", typeof(bool?), "true"),
        };

        private static int Main()
        {
            Assembly engine = Assembly.Load("RynthCore.Engine");
            Type settingsType = engine.GetType(Ns + "RynthAiSettings", throwOnError: true)!;
            Type ctxType = engine.GetType(Ns + "RynthAiSettingsJsonContext", throwOnError: true)!;
            Type schemaType = engine.GetType(Ns + "SettingsSchema", throwOnError: true)!;

            object ctx = ctxType.GetProperty("Default", All)!.GetValue(null)!;
            var info = (JsonTypeInfo)ctxType.GetProperty("RynthAiSettings", All)!.GetValue(ctx)!;
            object Parse(string json) => JsonSerializer.Deserialize(json, info)!;

            // ── The mirrors ─────────────────────────────────────────────────────────────
            object fresh = Activator.CreateInstance(settingsType, nonPublic: true)!;
            string freshJson = JsonSerializer.Serialize(fresh, info);
            foreach (var (name, type, _) in Mirrors)
            {
                PropertyInfo? p = settingsType.GetProperty(name, All);
                Check(p != null && p.PropertyType == type, $"{name}: a nullable mirror of RynthAi's setting");
                if (p == null) continue;
                Check(p.GetValue(fresh) == null, $"{name}: a fresh mirror holds no value (nothing is claimed until RynthAi sends it)");
                Check(p.GetValue(Parse("{\"BoostNavPriority\":true}")) == null, $"{name}: an older RynthAi's payload leaves it absent");
                Check(freshJson.Contains($"\"{name}\":null"), $"{name}: an unset mirror saves null, never a made-up value (RynthAi leaves its own alone)");
            }
            string sent = "{\"VariablePowerMin\":40,\"VariablePowerMax\":90,\"MissileEnvironmentResponse\":2,\"MagicEnvironmentResponse\":3," +
                          "\"EnvironmentImpactLimit\":4,\"EnvironmentImpactWindowSec\":45,\"EnvironmentApproachMeters\":8,\"EnvironmentMinDistance\":5," +
                          "\"EnvironmentSkipSec\":60,\"KitePullMeters\":33,\"KiteTagFirst\":false}";
            object withValues = Parse(sent);
            Check((int?)settingsType.GetProperty("VariablePowerMin", All)!.GetValue(withValues) == 40, "RynthAi's value arrives");
            Check((bool?)settingsType.GetProperty("KiteTagFirst", All)!.GetValue(withValues) == false, "a false bool arrives as false, not null");
            string back = JsonSerializer.Serialize(withValues, info);
            foreach (string piece in new[] { "\"VariablePowerMax\":90", "\"MagicEnvironmentResponse\":3", "\"KitePullMeters\":33", "\"KiteTagFirst\":false" })
                Check(back.Contains(piece), $"a save sends back what RynthAi sent ({piece})");

            // ── The rows ────────────────────────────────────────────────────────────────
            var tabs = new Dictionary<string, List<object>>();
            foreach (object t in (IEnumerable)schemaType.GetField("Tabs", All)!.GetValue(null)!)
            {
                string tabName = (string)t.GetType().GetProperty("Name")!.GetValue(t)!;
                tabs[tabName] = ((IEnumerable)t.GetType().GetProperty("Rows")!.GetValue(t)!).Cast<object>().ToList();
            }
            Check(tabs.ContainsKey("Melee Combat") && tabs.ContainsKey("Spell Combat"), "the Melee Combat and Spell Combat tabs exist");
            if (!tabs.ContainsKey("Melee Combat") || !tabs.ContainsKey("Spell Combat")) return Finish();

            static string Label(object r) => (string?)r.GetType().GetProperty("Label")!.GetValue(r) ?? "";
            static string Kind(object r) => r.GetType().GetProperty("Kind")!.GetValue(r)!.ToString()!;
            object? Row(string tab, string label, string kind) => tabs[tab].FirstOrDefault(r => Label(r) == label && Kind(r) == kind);
            bool Visible(object row, object s) => (bool)row.GetType().GetMethod("IsVisible")!.Invoke(row, new[] { s })!;
            double Get(object row, object s) => (double)((Delegate)row.GetType().GetProperty("Get")!.GetValue(row)!).DynamicInvoke(s)!;
            void Set(object row, object s, double v) => ((Delegate)row.GetType().GetProperty("Set")!.GetValue(row)!).DynamicInvoke(s, v);

            // (tab, label, kind, default shown, min, max)
            var rows = new (string Tab, string Label, string Kind, double Default, double Min, double Max)[]
            {
                ("Melee Combat", "Variable Min %", "Int", 50, 0, 100),
                ("Melee Combat", "Variable Max %", "Int", 100, 0, 100),
                ("Melee Combat", "Missile Response", "Combo", 0, 0, 3),
                ("Melee Combat", "Hits to trip", "Int", 2, 1, 10),
                ("Melee Combat", "Window (s)", "Int", 20, 5, 120),
                ("Melee Combat", "Move closer by (m)", "Int", 6, 1, 30),
                ("Melee Combat", "Closest (m)", "Int", 3, 1, 30),
                ("Melee Combat", "Skip target (s)", "Int", 30, 5, 300),
                ("Melee Combat", "Pull Distance (m)", "Int", 20, 3, 100),
                ("Melee Combat", "Tag Before Fleeing", "Bool", 1, 0, 1),
                ("Spell Combat", "Magic Response", "Combo", 0, 0, 3),
            };
            object older = Parse("{\"BoostNavPriority\":true}");            // what an older RynthAi sends
            object newer = Parse("{\"VariablePowerMin\":50,\"VariablePowerMax\":100,\"MissileEnvironmentResponse\":0,\"MagicEnvironmentResponse\":0," +
                                 "\"EnvironmentImpactLimit\":2,\"EnvironmentImpactWindowSec\":20,\"EnvironmentApproachMeters\":6,\"EnvironmentMinDistance\":3," +
                                 "\"EnvironmentSkipSec\":30,\"KitePullMeters\":20,\"KiteTagFirst\":true}");   // the plugin's own defaults
            foreach (var (tab, label, kind, def, min, max) in rows)
            {
                object? row = Row(tab, label, kind);
                Check(row != null, $"{tab} / {label}: a {kind} row");
                if (row == null) continue;
                Check(!Visible(row, older), $"{label}: hidden for an older RynthAi (it never sent the value)");
                Check(!Visible(row, fresh), $"{label}: hidden before RynthAi has sent anything");
                Check(Visible(row, newer), $"{label}: shown once RynthAi sends it");
                Check(Get(row, newer) == def, $"{label}: shows the plugin's default ({def}) as sent");
                Check(Get(row, older) == def, $"{label}: reads the default for an unset mirror, without throwing");
                if (kind != "Bool") Check((double)row.GetType().GetProperty("Min")!.GetValue(row)! == min && (double)row.GetType().GetProperty("Max")!.GetValue(row)! == max, $"{label}: range {min}..{max}");
            }

            // Section headers and spacers of these groups follow the same gate (no empty headers on an older RynthAi).
            foreach (string sec in new[] { "Variable Power (monster rules)", "Hitting the Environment", "Pull Away (Kite)" })
                foreach (string tab in new[] { "Melee Combat", "Spell Combat" })
                {
                    object? header = Row(tab, sec, "Section");
                    if (header == null) continue;
                    Check(!Visible(header, older), $"{tab} / {sec}: the header is hidden for an older RynthAi");
                    Check(Visible(header, newer), $"{tab} / {sec}: the header shows for a newer one");
                }
            Check(Row("Melee Combat", "Pull Away (Kite)", "Section") != null && Row("Melee Combat", "Hitting the Environment", "Section") != null
                  && Row("Melee Combat", "Variable Power (monster rules)", "Section") != null && Row("Spell Combat", "Hitting the Environment", "Section") != null, "all four section headers exist");

            // The combos list RynthAi's EnvironmentResponse in its order.
            object missile = Row("Melee Combat", "Missile Response", "Combo")!;
            var items = (string[])missile.GetType().GetProperty("Items")!.GetValue(missile)!;
            Check(items.SequenceEqual(new[] { "Off", "Move closer", "New target", "Move closer, then new target" }), "Missile Response: Off, Move closer, New target, Move closer then new target (RynthAi's order)");

            // A row edits only its own property; the rest rides along.
            object edit = Parse("{\"BoostNavPriority\":true," + sent.TrimStart('{'));
            Set(Row("Melee Combat", "Pull Distance (m)", "Int")!, edit, 55);
            Set(Row("Melee Combat", "Tag Before Fleeing", "Bool")!, edit, 1);
            Set(Row("Spell Combat", "Magic Response", "Combo")!, edit, 1);
            string after = JsonSerializer.Serialize(edit, info);
            Check(after.Contains("\"KitePullMeters\":55") && after.Contains("\"KiteTagFirst\":true") && after.Contains("\"MagicEnvironmentResponse\":1"), "the edits land on their own properties");
            Check(after.Contains("\"BoostNavPriority\":true") && after.Contains("\"EnvironmentSkipSec\":60") && after.Contains("\"MissileEnvironmentResponse\":2"), "the other settings ride along unchanged");

            // The rows an older RynthAi lacks don't change what a plain save sends.
            Set(Row("Melee Combat", "Pull Distance (m)", "Int")!, older, 30);   // can't happen in the UI (hidden); must not throw
            Check(true, "setting a hidden row doesn't throw");

            // ── The monster rule: Power and Kite ────────────────────────────────────────
            Type damage = engine.GetType(Ns + "DamageCommands", throwOnError: true)!;
            var powers = (string[])damage.GetField("AttackPowerModes", All)!.GetValue(null)!;
            var kites = (string[])damage.GetField("KiteModes", All)!.GetValue(null)!;
            Check(powers.SequenceEqual(new[] { "Default", "High", "Reckless", "Variable", "Reckless + Variable" }), "Power choices are spelled as RynthAi's AttackPowerModes");
            Check(kites.SequenceEqual(new[] { "Default", "Off", "PullAway" }), "Kite choices are spelled as RynthAi's KiteModes");

            Type monsters = engine.GetType("RynthCore.Engine.UI.Panels.MonstersPanel", throwOnError: true)!;
            Type rule = monsters.GetNestedType("Rule", All)!;
            object r1 = Activator.CreateInstance(rule, nonPublic: true)!;
            Check((string)rule.GetProperty("AttackPower")!.GetValue(r1)! == "Default", "a new rule's Power is Default (follows the DEFAULT line)");
            Check((string)rule.GetProperty("Kite")!.GetValue(r1)! == "Default", "a new rule's Kite is Default (Off on the DEFAULT line)");
            MethodInfo equal = monsters.GetMethod("RulesEqual", All)!;
            Type listType = typeof(List<>).MakeGenericType(rule);
            IList Lst(params object[] xs) { var l = (IList)Activator.CreateInstance(listType)!; foreach (object x in xs) l.Add(x); return l; }
            object r2 = Activator.CreateInstance(rule, nonPublic: true)!;
            Check((bool)equal.Invoke(null, new object[] { Lst(r1), Lst(r2) })!, "two default rules are equal");
            rule.GetProperty("Kite")!.SetValue(r2, "PullAway");
            Check(!(bool)equal.Invoke(null, new object[] { Lst(r1), Lst(r2) })!, "a changed Kite is a change (it is saved)");
            object r3 = Activator.CreateInstance(rule, nonPublic: true)!;
            rule.GetProperty("AttackPower")!.SetValue(r3, "Variable");
            Check(!(bool)equal.Invoke(null, new object[] { Lst(r1), Lst(r3) })!, "a changed Power is a change (it is saved)");

            // ── The monster rule: the Void debuffs (2026-10-27) ─────────────────────────
            string[] voidFields = { "Corrosion", "Corruption", "DestructiveCurse", "WeakeningCurse", "FesteringCurse" };
            var voidDefs = ((string Field, string Label, string Tip)[])damage.GetField("VoidDebuffDefs", All)!.GetValue(null)!;
            Check(voidDefs.Select(d => d.Field).SequenceEqual(voidFields), "the Void debuffs are spelled as RynthAi's MonsterRule fields");
            MethodInfo getT = rule.GetMethod("GetToggle", All)!, setT = rule.GetMethod("SetToggle", All)!;
            object allVoid = Activator.CreateInstance(rule, nonPublic: true)!;
            foreach (string f in voidFields)
            {
                object off = Activator.CreateInstance(rule, nonPublic: true)!, on = Activator.CreateInstance(rule, nonPublic: true)!;
                Check(!(bool)getT.Invoke(off, new object[] { f })!, f + " is off on a new rule");
                setT.Invoke(on, new object[] { f, true });
                setT.Invoke(allVoid, new object[] { f, true });
                Check((bool)rule.GetProperty(f)!.GetValue(on)! && (bool)getT.Invoke(on, new object[] { f })!, f + " is a toggle on its own property");
                Check(!(bool)equal.Invoke(null, new object[] { Lst(off), Lst(on) })!, f + " changed is a change (it is saved)");
            }
            MethodInfo marks = damage.GetMethod("DebuffMarks", All)!;
            Check((string)marks.Invoke(null, new[] { allVoid })! == "CRDWH", "the Damage panel marks them C R D W H");
            MethodInfo copy = damage.GetMethod("CopyOfDefault", All)!;
            object copied = copy.Invoke(null, new[] { allVoid, "Drudge" })!;
            Check(voidFields.All(f => (bool)rule.GetProperty(f)!.GetValue(copied)!), "a monster's own rule starts with Default's Void debuffs");
            // RynthAi keeps its own Void switches when the panel's JSON has no "Corrosion": key
            // (an older engine), so this engine must write exactly that.
            var panelJson = (System.Text.Json.Serialization.JsonSerializerContext)engine.GetType("RynthCore.Engine.UI.Panels.MonstersPanelJsonContext", throwOnError: true)!
                .GetProperty("Default", All)!.GetValue(null)!;
            string ruleJson = JsonSerializer.Serialize((object)Lst(allVoid), listType, panelJson);
            foreach (string f in voidFields)
                Check(ruleJson.Contains($"\"{f}\":true"), $"the panel sends \"{f}\":true");

            return Finish();
        }

        private static int Finish()
        {
            Console.WriteLine(_fails == 0 ? $"All {_asserts} checks passed." : $"{_fails} of {_asserts} checks FAILED.");
            return _fails == 0 ? 0 : 1;
        }
    }
}

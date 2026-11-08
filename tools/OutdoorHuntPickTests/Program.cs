using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace OutdoorHuntPickTests
{
    // Reaches RynthCore.Engine's internal settings mirror by reflection. See the csproj.
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

        private static int Main()
        {
            Assembly engine = Assembly.Load("RynthCore.Engine");
            Type settingsType = engine.GetType(Ns + "RynthAiSettings", throwOnError: true)!;
            Type ctxType = engine.GetType(Ns + "RynthAiSettingsJsonContext", throwOnError: true)!;
            Type schemaType = engine.GetType(Ns + "SettingsSchema", throwOnError: true)!;
            PropertyInfo? prop = settingsType.GetProperty("OutdoorHuntPick", All);
            Check(prop != null && prop.PropertyType == typeof(int?), "the mirror has a nullable int OutdoorHuntPick (RynthAi's payload name)");
            if (prop == null) return Finish();

            object ctx = ctxType.GetProperty("Default", All)!.GetValue(null)!;
            var info = (JsonTypeInfo)ctxType.GetProperty("RynthAiSettings", All)!.GetValue(ctx)!;
            object Parse(string json) => JsonSerializer.Deserialize(json, info)!;

            // What a payload carries: nothing from an older RynthAi, the value from a newer one.
            object fresh = Activator.CreateInstance(settingsType, nonPublic: true)!;
            Check(prop.GetValue(fresh) == null, "a fresh mirror holds no value");
            Check(prop.GetValue(Parse("{\"OutdoorHuntRange\":60,\"OutdoorHuntRadius\":0}")) == null, "a RynthAi without it: absent (null)");
            for (int v = 0; v <= 2; v++)
                Check((int?)prop.GetValue(Parse("{\"OutdoorHuntPick\":" + v + "}")) == v, $"RynthAi sends {v}");

            // A save sends exactly what the mirror holds: unset stays null (RynthAi leaves its setting alone).
            Check(JsonSerializer.Serialize(fresh, info).Contains("\"OutdoorHuntPick\":null"), "an unset mirror saves null");
            Check(JsonSerializer.Serialize(Parse("{\"OutdoorHuntPick\":2}"), info).Contains("\"OutdoorHuntPick\":2"), "Most XP saves 2");

            // The schema row, under Navigation, beside Hunt Range / Hunt Radius.
            object? nav = null;
            foreach (object t in (IEnumerable)schemaType.GetField("Tabs", All)!.GetValue(null)!)
                if ((string)t.GetType().GetProperty("Name")!.GetValue(t)! == "Navigation") nav = t;
            Check(nav != null, "a Navigation tab");
            if (nav == null) return Finish();

            var labels = new List<string>();
            object? row = null;
            int matches = 0;
            foreach (object r in (IEnumerable)nav.GetType().GetProperty("Rows")!.GetValue(nav)!)
            {
                string label = (string?)r.GetType().GetProperty("Label")!.GetValue(r) ?? "";
                labels.Add(label);
                if (label == "Go for") { row = r; matches++; }
            }
            Check(matches == 1, "exactly one Go for row, under Navigation");
            int radius = labels.IndexOf("Hunt Radius (yd)"), goFor = labels.IndexOf("Go for");
            Check(radius >= 0 && goFor == radius + 1, "right after Hunt Radius");
            if (row == null) return Finish();

            Type rt = row.GetType();
            Check(rt.GetProperty("Kind")!.GetValue(row)!.ToString() == "Combo", "a combo");
            var items = (string[])rt.GetProperty("CurrentItems")!.GetValue(row)!;
            Check(items.SequenceEqual(new[] { "Nearest monster", "Biggest pack", "Most XP" }), "the three choices, in RynthAi's order");
            string tip = (string?)rt.GetProperty("Tooltip")!.GetValue(row) ?? "";
            Check(tip.Contains("Nearest monster: the closest one, as before."), "tooltip: nearest");
            Check(tip.Contains("Biggest pack: the most monsters together"), "tooltip: biggest pack");
            Check(tip.Contains("Most XP: the pack worth the most experience, learned from your kills"), "tooltip: most XP");

            var get = (Delegate)rt.GetProperty("Get")!.GetValue(row)!;
            var set = (Delegate)rt.GetProperty("Set")!.GetValue(row)!;
            MethodInfo visible = rt.GetMethod("IsVisible")!;

            Check(!(bool)visible.Invoke(row, new[] { fresh })!, "hidden while RynthAi hasn't sent the value");
            Check(!(bool)visible.Invoke(row, new[] { Parse("{\"OutdoorHuntRange\":60,\"OutdoorHuntRadius\":0}") })!,
                "hidden for a RynthAi with Hunt Range but no Go for");
            object near = Parse("{\"OutdoorHuntRange\":60,\"OutdoorHuntPick\":0}");
            Check((bool)visible.Invoke(row, new[] { near })!, "shown once RynthAi sends it");
            Check((double)get.DynamicInvoke(near)! == 0, "Nearest monster reads 0");
            set.DynamicInvoke(near, 2.0);
            Check((int?)prop.GetValue(near) == 2, "choosing Most XP sets 2");
            set.DynamicInvoke(near, 1.0);
            Check((int?)prop.GetValue(near) == 1, "choosing Biggest pack sets 1");
            set.DynamicInvoke(near, 7.0);
            Check((int?)prop.GetValue(near) == 2, "an out-of-range pick is kept within the choices");
            Check((double)get.DynamicInvoke(Parse("{\"OutdoorHuntPick\":9}"))! == 2, "an unknown value from a newer RynthAi reads as the last choice");
            Check((double)get.DynamicInvoke(Parse("{\"OutdoorHuntPick\":-3}"))! == 0, "a negative value reads Nearest monster");

            // Picking changes nothing else in the payload.
            object before = Parse("{\"OutdoorHuntRange\":60,\"OutdoorHuntRadius\":150,\"OutdoorHuntPick\":0}");
            set.DynamicInvoke(before, 1.0);
            string after = JsonSerializer.Serialize(before, info);
            Check(after.Contains("\"OutdoorHuntRange\":60") && after.Contains("\"OutdoorHuntRadius\":150") && after.Contains("\"OutdoorHuntPick\":1"),
                "Hunt Range and Hunt Radius ride along unchanged");
            return Finish();
        }

        private static int Finish()
        {
            Console.WriteLine(_fails == 0 ? $"All {_asserts} checks passed." : $"{_fails} of {_asserts} checks FAILED.");
            return _fails == 0 ? 0 : 1;
        }
    }
}

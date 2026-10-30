using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace VaultedSettingTests
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
            PropertyInfo? prop = settingsType.GetProperty("EnableVaultedDungeons", All);
            Check(prop != null && prop.PropertyType == typeof(bool?), "the mirror has a nullable EnableVaultedDungeons (RynthAi's payload name)");
            if (prop == null) return Finish();

            object ctx = ctxType.GetProperty("Default", All)!.GetValue(null)!;
            var info = (JsonTypeInfo)ctxType.GetProperty("RynthAiSettings", All)!.GetValue(ctx)!;
            object Parse(string json) => JsonSerializer.Deserialize(json, info)!;

            // What a payload carries: nothing from an older RynthAi, the value from a newer one.
            object fresh = Activator.CreateInstance(settingsType, nonPublic: true)!;
            Check(prop.GetValue(fresh) == null, "a fresh mirror holds no value (nothing is claimed until RynthAi sends it)");
            Check(prop.GetValue(Parse("{\"BoostNavPriority\":true}")) == null, "an older RynthAi's payload: absent (null)");
            Check((bool?)prop.GetValue(Parse("{\"EnableVaultedDungeons\":false}")) == false, "RynthAi sends false");
            Check((bool?)prop.GetValue(Parse("{\"EnableVaultedDungeons\":true}")) == true, "RynthAi sends true");
            Check((bool?)prop.GetValue(Parse("{\"enablevaulteddungeons\":true}")) == true, "the key is matched ignoring case, like the rest");

            // A save sends exactly what the mirror holds: unset stays null (RynthAi leaves its switch alone).
            Check(JsonSerializer.Serialize(fresh, info).Contains("\"EnableVaultedDungeons\":null"), "an unset mirror saves null, never a made-up false");
            Check(JsonSerializer.Serialize(Parse("{\"EnableVaultedDungeons\":true}"), info).Contains("\"EnableVaultedDungeons\":true"), "an On mirror saves true");

            // The schema row.
            object? nav = null;
            foreach (object t in (IEnumerable)schemaType.GetField("Tabs", All)!.GetValue(null)!)
                if ((string)t.GetType().GetProperty("Name")!.GetValue(t)! == "Navigation") nav = t;
            Check(nav != null, "a Navigation tab");
            if (nav == null) return Finish();

            string label = (string)schemaType.GetField("VaultedDungeonsLabel", All)!.GetValue(null)!;
            Check(label == "Vaulted Dungeons", "the row is called Vaulted Dungeons");
            var matches = new List<object>();
            foreach (object r in (IEnumerable)nav.GetType().GetProperty("Rows")!.GetValue(nav)!)
                if ((string?)r.GetType().GetProperty("Label")!.GetValue(r) == label) matches.Add(r);
            Check(matches.Count == 1, "exactly one Vaulted Dungeons row, under Navigation");
            if (matches.Count != 1) return Finish();

            object row = matches[0];
            Type rt = row.GetType();
            Check(rt.GetProperty("Kind")!.GetValue(row)!.ToString() == "Bool", "a checkbox");
            var get = (Delegate)rt.GetProperty("Get")!.GetValue(row)!;
            var set = (Delegate)rt.GetProperty("Set")!.GetValue(row)!;
            MethodInfo visible = rt.GetMethod("IsVisible")!;
            string tip = (string?)rt.GetProperty("Tooltip")!.GetValue(row) ?? "";
            Check(tip.Contains("Off by default") && tip.Contains("Infinite Leaftide"), "the tooltip says it is the master switch, off by default, ILT only");
            Check(tip.Contains("do NOT"), "and warns that pet/fellow kills don't restart the Kill Client timer");

            object unset = Activator.CreateInstance(settingsType, nonPublic: true)!;
            Check(!(bool)visible.Invoke(row, new[] { unset })!, "hidden while RynthAi hasn't sent the value (an older RynthAi)");
            Check((double)get.DynamicInvoke(unset)! == 0, "an unset mirror reads Off");

            object off = Parse("{\"EnableVaultedDungeons\":false}");
            Check((bool)visible.Invoke(row, new[] { off })!, "shown once RynthAi sends it");
            Check((double)get.DynamicInvoke(off)! == 0, "reads Off by default");
            set.DynamicInvoke(off, 1.0);
            Check((bool?)prop.GetValue(off) == true, "ticking it sets EnableVaultedDungeons to true");
            Check((double)get.DynamicInvoke(off)! == 1, "and it reads On");
            set.DynamicInvoke(off, 0.0);
            Check((bool?)prop.GetValue(off) == false, "unticking sets false (not null)");

            // Ticking it changes nothing else in the payload.
            object before = Parse("{\"BoostNavPriority\":true,\"EnableVaultedDungeons\":false}");
            set.DynamicInvoke(before, 1.0);
            string after = JsonSerializer.Serialize(before, info);
            Check(after.Contains("\"BoostNavPriority\":true") && after.Contains("\"EnableVaultedDungeons\":true"), "the other settings ride along unchanged");
            return Finish();
        }

        private static int Finish()
        {
            Console.WriteLine(_fails == 0 ? $"All {_asserts} checks passed." : $"{_fails} of {_asserts} checks FAILED.");
            return _fails == 0 ? 0 : 1;
        }
    }
}

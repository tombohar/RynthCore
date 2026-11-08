using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace ItemsNoBuffTests
{
    // The engine half of the shield "No buff" toggle, against what RynthAi's Items bridge writes
    // (ItemsBridgePayload: PascalCase names, "noBuff" on every weapon entry) and reads
    // (case-insensitive; noBuff applied for Action "Shield" entries only, absent = unchanged).
    internal static class Program
    {
        private static int _fails, _asserts;
        private static void Check(bool cond, string msg)
        {
            _asserts++;
            if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
        }

        private const string Ns = "RynthCore.Engine.UI.Panels.";
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        private static int Main()
        {
            Assembly engine = Assembly.Load("RynthCore.Engine");
            Type ctxType = engine.GetType(Ns + "ItemsPanelJsonContext", throwOnError: true)!;
            object ctx = ctxType.GetProperty("Default", All)!.GetValue(null)!;
            var info = (JsonTypeInfo)ctxType.GetProperty("Payload", All)!.GetValue(ctx)!;
            Type payloadType = engine.GetType(Ns + "ItemsPanel+Payload", throwOnError: true)!;
            Type entryType = engine.GetType(Ns + "ItemsPanel+WeaponEntry", throwOnError: true)!;

            List<object> Weapons(object payload) =>
                ((System.Collections.IEnumerable)payloadType.GetProperty("Weapons", All)!.GetValue(payload)!).Cast<object>().ToList();
            bool? NoBuff(object e) => (bool?)entryType.GetProperty("NoBuff", All)!.GetValue(e);
            bool Shows(object e) => (bool)entryType.GetProperty("ShowsNoBuff", All)!.GetValue(e)!;
            string Name(object e) => (string)entryType.GetProperty("Name", All)!.GetValue(e)!;

            // --- RynthAi -> engine (the shape RynthAi's BuildItemsJson writes)
            const string fromPlugin = "{\n  \"Weapons\": [\n" +
                "    { \"Id\": 1, \"Name\": \"Sword\", \"Action\": \"Weapon\", \"Element\": \"Slash\", \"Kind\": \"Melee\", \"KeepBuffed\": false, \"noBuff\": true },\n" +
                "    { \"Id\": 2, \"Name\": \"Kite Shield\", \"Action\": \"Shield\", \"Element\": \"Shield\", \"Kind\": \"Shield\", \"KeepBuffed\": true, \"noBuff\": false },\n" +
                "    { \"Id\": 3, \"Name\": \"Rose Shield\", \"Action\": \"Shield\", \"Element\": \"Shield\", \"Kind\": \"Shield\", \"KeepBuffed\": false, \"noBuff\": true },\n" +
                "    { \"Id\": 4, \"Name\": \"Buckler\", \"Action\": \"Weapon\", \"Element\": \"Shield\", \"Kind\": \"Shield\", \"noBuff\": true }\n" +
                "  ],\n  \"MissingIds\": []\n}";
            object p = JsonSerializer.Deserialize(fromPlugin, info)!;
            var w = Weapons(p);
            Check(w.Count == 4, "four entries read");
            Check(NoBuff(w[0]) == true && NoBuff(w[1]) == false && NoBuff(w[2]) == true, "noBuff is read per entry");
            Check(!Shows(w[0]), "a weapon row: no toggle (RynthAi ignores noBuff on weapons)");
            Check(Shows(w[1]) && Shows(w[2]), "a shield row (Action Shield): the toggle");
            Check(!Shows(w[3]), "a shield filed as a Weapon (Kind Shield, Action Weapon): no toggle (RynthAi ignores it there)");

            // --- An older RynthAi: no field -> no toggle, nothing written back
            object old = JsonSerializer.Deserialize("{\"Weapons\":[{\"Id\":2,\"Name\":\"Kite Shield\",\"Action\":\"Shield\",\"Element\":\"Shield\"}]}", info)!;
            object oldShield = Weapons(old)[0];
            Check(NoBuff(oldShield) == null, "an older RynthAi: noBuff is null");
            Check(!Shows(oldShield), "and the shield row has no toggle");
            string oldBack = JsonSerializer.Serialize(old, info);
            Check(!oldBack.Contains("noBuff"), "and the save carries no noBuff (RynthAi keeps its saved value): " + oldBack);
            Check(!oldBack.Contains("ShowsNoBuff"), "the face-only ShowsNoBuff is not on the wire");

            // --- Engine -> RynthAi: the toggle flipped and saved
            entryType.GetProperty("NoBuff", All)!.SetValue(w[1], true);    // what the faces' click does
            entryType.GetProperty("NoBuff", All)!.SetValue(w[2], false);
            string back = JsonSerializer.Serialize(p, info);
            Check(Regex.IsMatch(back, "\"id\":2,[^}]*\"noBuff\":true"), "the Kite Shield goes back noBuff true: " + back);
            Check(Regex.IsMatch(back, "\"id\":3,[^}]*\"noBuff\":false"), "the Rose Shield goes back noBuff false");
            Check(Regex.IsMatch(back, "\"id\":1,[^}]*\"noBuff\":true"), "a weapon's value is round-tripped untouched (RynthAi ignores it)");
            Check(Regex.Matches(back, "\"action\":\"Shield\"").Count == 2 && Name(w[1]) == "Kite Shield", "names and actions round-trip");

            // The field name is exactly what RynthAi's ItemRule.NoBuff ([JsonPropertyName("noBuff")]) reads.
            var attr = entryType.GetProperty("NoBuff", All)!.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>();
            Check(attr?.Name == "noBuff", "the wire name is noBuff");

            Console.WriteLine(_fails == 0 ? $"All {_asserts} checks passed." : $"{_fails} of {_asserts} checks FAILED.");
            return _fails == 0 ? 0 : 1;
        }
    }
}

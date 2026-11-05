using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace SettingsSaveTests
{
    // A Settings save sends only what the player changed (see the csproj).
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
            Type commands = engine.GetType(Ns + "SettingsCommands", throwOnError: true)!;
            object ctx = ctxType.GetProperty("Default", All)!.GetValue(null)!;
            var info = (JsonTypeInfo)ctxType.GetProperty("RynthAiSettings", All)!.GetValue(ctx)!;
            MethodInfo changed = commands.GetMethod("ChangedFieldsJson", All)!;
            MethodInfo clone = settingsType.GetMethod("Clone", All)!;

            object Parse(string json) => JsonSerializer.Deserialize(json, info)!;
            object Clone(object s) => clone.Invoke(s, null)!;
            void Set(object s, string name, object value) => settingsType.GetProperty(name, All)!.SetValue(s, value);
            JsonObject? Sent(object s) => changed.Invoke(null, new[] { s }) is string j ? JsonNode.Parse(j)!.AsObject() : null;

            // The character's settings as RynthAi sent them (the snapshot).
            object snapshot = Parse("{\"StartMacroOnLogin\":true,\"TopOffHP\":95,\"EnableLooting\":true,\"EnableRaycasting\":false,\"RestamTo\":90}");

            // A face clones it, the player ticks one box: only that box is sent.
            object copy = Clone(snapshot);
            Set(copy, "EnableLooting", false);
            JsonObject? sent = Sent(copy);
            Check(sent != null && sent.Count == 1 && sent.ContainsKey("EnableLooting") && sent["EnableLooting"]!.GetValue<bool>() == false,
                "one edit: only that field is sent (got " + sent?.ToJsonString() + ")");

            // Faces save a clone of their copy: the base is still the snapshot.
            JsonObject? sent2 = Sent(Clone(copy));
            Check(sent2 != null && sent2.Count == 1 && sent2.ContainsKey("EnableLooting"), "a clone of an edited copy diffs against the same snapshot");

            // Nothing edited: nothing is sent.
            Check(Sent(Clone(snapshot)) == null, "an unedited copy sends nothing");

            // The 2026-11-05 case: the face's copy came from a default snapshot (RynthAi not ready yet); one
            // edit must not carry every other default with it.
            object defaults = Parse("{}");
            object staleCopy = Clone(defaults);
            Set(staleCopy, "TopOffHP", 80);
            JsonObject? sent3 = Sent(staleCopy);
            Check(sent3 != null && sent3.Count == 1 && sent3.ContainsKey("TopOffHP"),
                "a copy of a default snapshot sends only the edited field, never the defaults (got " + sent3?.ToJsonString() + ")");

            // A copy that never came from a snapshot is never sent at all.
            object orphan = Activator.CreateInstance(settingsType, nonPublic: true)!;
            Set(orphan, "TopOffHP", 80);
            Check(Sent(orphan) == null, "a settings object with no snapshot behind it is not sent");

            // Two edits: both, and nothing else.
            object two = Clone(snapshot);
            Set(two, "RestamTo", 70);
            Set(two, "StartMacroOnLogin", false);
            JsonObject? sent4 = Sent(two);
            Check(sent4 != null && sent4.Count == 2 && sent4.ContainsKey("RestamTo") && sent4.ContainsKey("StartMacroOnLogin"), "two edits: those two");

            // Raycasting is on unless RynthAi says otherwise.
            object fresh = Activator.CreateInstance(settingsType, nonPublic: true)!;
            Check((bool)settingsType.GetProperty("EnableRaycasting", All)!.GetValue(fresh)! == true, "the engine's raycasting default is on");

            Console.WriteLine(_fails == 0 ? $"PASS ({_asserts} checks)" : $"FAIL ({_fails} of {_asserts})");
            return _fails == 0 ? 0 : 1;
        }
    }
}

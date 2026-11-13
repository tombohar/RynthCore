using System.Text.Json;
using RynthCore.Engine.UI;

namespace RynthCore.Engine.UI
{
    /// <summary>Stub of the engine's background writer: runs the write at once.</summary>
    internal static class UiBackgroundWriter
    {
        public static void Enqueue(string label, Action work) => work();
    }
}

namespace TrackerSettingsTests
{
    // Offline tests for the Tracker's settings file. See the csproj. TrackerSettings loads once per
    // process, so the order matters: an old-style file first, then saves; other files go through Parse.
    internal static class Program
    {
        private static int _fails, _asserts;
        private static void Check(bool cond, string msg)
        {
            _asserts++;
            if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
        }

        private static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "rynth-tracker-settings-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "rynthtracker_settings.json");
            Environment.SetEnvironmentVariable("RYNTHCORE_TRACKER_SETTINGS", file);
            try
            {
                // ToJson: what Save writes, both keys, parseable.
                using (var doc = JsonDocument.Parse(TrackerSettings.ToJson(51, true)))
                {
                    Check(doc.RootElement.GetProperty("bgAlpha").GetInt32() == 51, "ToJson writes bgAlpha");
                    Check(doc.RootElement.GetProperty("textOnly").GetBoolean(), "ToJson writes textOnly true");
                }
                using (var doc = JsonDocument.Parse(TrackerSettings.ToJson(255, false)))
                    Check(!doc.RootElement.GetProperty("textOnly").GetBoolean(), "ToJson writes textOnly false");

                // A file from before Text only (bgAlpha alone): its opacity kept, Text only off.
                File.WriteAllText(file, "{\"bgAlpha\":100}");
                Check(TrackerSettings.BgAlpha == 100, "an old file's opacity is read");
                Check(!TrackerSettings.TextOnly, "an old file (no textOnly) leaves Text only off");

                // Text only on: saved with the opacity.
                TrackerSettings.SetTextOnly(true);
                Check(TrackerSettings.TextOnly, "Text only is on at once");
                using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
                {
                    Check(doc.RootElement.GetProperty("textOnly").GetBoolean(), "Text only on is saved");
                    Check(doc.RootElement.GetProperty("bgAlpha").GetInt32() == 100, "saving Text only keeps the opacity");
                }

                // The opacity slider's save keeps Text only.
                TrackerSettings.SetBgAlpha(40, save: true);
                using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
                {
                    Check(doc.RootElement.GetProperty("bgAlpha").GetInt32() == 40, "the opacity is saved");
                    Check(doc.RootElement.GetProperty("textOnly").GetBoolean(), "saving the opacity keeps Text only");
                }

                // Off again.
                TrackerSettings.SetTextOnly(false);
                Check(!TrackerSettings.TextOnly, "Text only is off at once");
                using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
                    Check(!doc.RootElement.GetProperty("textOnly").GetBoolean(), "Text only off is saved");

                // 2026-11-12: full click-through and hidden stats.
                // The old file had neither: click-through off, every stat shown.
                Check(!TrackerSettings.ClickThrough, "an old file (no clickThrough) leaves click-through off");
                Check(TrackerSettings.HiddenIds.Length == 0, "an old file (no hidden) shows every stat");
                foreach (TrackerStat s in TrackerSettings.Stats)
                    Check(!TrackerSettings.IsLabelHidden(s.Label), $"{s.Label} shown by default");

                // Click-through on: saved with the rest.
                TrackerSettings.SetTextOnly(true);
                TrackerSettings.SetClickThrough(true);
                Check(TrackerSettings.ClickThrough, "click-through is on at once");
                using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
                {
                    Check(doc.RootElement.GetProperty("clickThrough").GetBoolean(), "click-through on is saved");
                    Check(doc.RootElement.GetProperty("textOnly").GetBoolean(), "saving click-through keeps Text only");
                    Check(doc.RootElement.GetProperty("bgAlpha").GetInt32() == 40, "saving click-through keeps the opacity");
                }

                // Hiding stats: by id, the version goes up, saved in the table's order, the rest kept.
                int v0 = TrackerSettings.HiddenVersion;
                TrackerSettings.SetHidden("deaths", true);
                TrackerSettings.SetHidden("lum5m", true);
                Check(TrackerSettings.HiddenVersion > v0, "hiding a stat bumps HiddenVersion");
                Check(TrackerSettings.IsLabelHidden("Deaths") && TrackerSettings.IsLabelHidden("Lum/5m"), "hidden by their labels");
                Check(!TrackerSettings.IsLabelHidden("XP"), "XP still shown");
                int v1 = TrackerSettings.HiddenVersion;
                TrackerSettings.SetHidden("deaths", true);
                Check(TrackerSettings.HiddenVersion == v1, "hiding a hidden stat again changes nothing");
                TrackerSettings.SetHidden("nonsense", true);
                Check(TrackerSettings.HiddenVersion == v1 && !TrackerSettings.IsHidden("nonsense"), "an unknown id is not hidden");
                Check(TrackerSettings.IsLabelHidden("Some new row") == false, "a label not in the table is shown");
                using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
                {
                    var arr = doc.RootElement.GetProperty("hidden").EnumerateArray().Select(e => e.GetString()).ToArray();
                    Check(arr.SequenceEqual(new[] { "lum5m", "deaths" }), $"hidden saved in order (got [{string.Join(",", arr)}])");
                    Check(doc.RootElement.GetProperty("clickThrough").GetBoolean(), "saving hidden keeps click-through");
                }
                TrackerSettings.SetHidden("deaths", false);
                Check(!TrackerSettings.IsLabelHidden("Deaths"), "a stat shown again");
                using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
                    Check(doc.RootElement.GetProperty("hidden").GetArrayLength() == 1, "showing a stat is saved");
                TrackerSettings.ShowAll();
                Check(TrackerSettings.HiddenIds.Length == 0, "Show all clears the hidden set");
                using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
                    Check(doc.RootElement.GetProperty("hidden").GetArrayLength() == 0, "Show all is saved");
                TrackerSettings.SetClickThrough(false);
                using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
                    Check(!doc.RootElement.GetProperty("clickThrough").GetBoolean(), "click-through off is saved");

                // The stat ids are unique and every TrackerSource label is in the table.
                Check(TrackerSettings.Stats.Select(s => s.Id).Distinct().Count() == TrackerSettings.Stats.Length, "stat ids unique");
                Check(TrackerSettings.Stats.Select(s => s.Label).Distinct().Count() == TrackerSettings.Stats.Length, "stat labels unique");
                foreach (string label in new[] { "Session", "XP/hr", "Lum/hr", "Lum/5m", "Pass/hr", "Rad/hr", "Kls/hr", "XP", "Lum", "Pass-up", "Rad", "Kills", "XP/kl", "Deaths" })
                    Check(TrackerSettings.StatId(label) != label, $"{label} has a stat id");

                // Parse: round trip of ToJson, old files, bad values.
                var full = TrackerSettings.Parse(TrackerSettings.ToJson(77, true, true, new[] { "kills", "session", "rad" }));
                Check(full.BgAlpha == 77 && full.TextOnly && full.ClickThrough, "Parse(ToJson) keeps opacity, Text only, click-through");
                Check(full.Hidden.SequenceEqual(new[] { "session", "rad", "kills" }), "Parse(ToJson) keeps hidden, in table order");
                var old = TrackerSettings.Parse("{\"bgAlpha\":100,\"textOnly\":true}");
                Check(old.BgAlpha == 100 && old.TextOnly && !old.ClickThrough && old.Hidden.Length == 0, "an old file parses with the new defaults");
                var empty = TrackerSettings.Parse("{}");
                Check(empty.BgAlpha == 0xCC && !empty.TextOnly && !empty.ClickThrough && empty.Hidden.Length == 0, "an empty file is all defaults");
                var bad = TrackerSettings.Parse("{\"bgAlpha\":\"x\",\"textOnly\":1,\"clickThrough\":\"yes\",\"hidden\":\"deaths\"}");
                Check(bad.BgAlpha == 0xCC && !bad.TextOnly && !bad.ClickThrough && bad.Hidden.Length == 0, "values of the wrong kind are ignored");
                var mixed = TrackerSettings.Parse("{\"bgAlpha\":999,\"clickThrough\":true,\"hidden\":[\"deaths\",5,null,\"bogus\",\"deaths\",\"xp\",{}]}");
                Check(mixed.BgAlpha == 255, "an opacity over 255 is clamped");
                Check(mixed.ClickThrough, "a good key beside bad ones is still read");
                Check(mixed.Hidden.SequenceEqual(new[] { "xp", "deaths" }), $"only known string ids kept, once (got [{string.Join(",", mixed.Hidden)}])");
                var notObject = TrackerSettings.Parse("[1,2]");
                Check(notObject.BgAlpha == 0xCC && notObject.Hidden.Length == 0, "a file that is not an object is all defaults");
                using (var doc = JsonDocument.Parse(TrackerSettings.ToJson(10, false)))
                {
                    Check(!doc.RootElement.GetProperty("clickThrough").GetBoolean(), "ToJson defaults click-through off");
                    Check(doc.RootElement.GetProperty("hidden").GetArrayLength() == 0, "ToJson defaults to nothing hidden");
                }
            }
            catch (Exception ex)
            {
                _fails++;
                Console.WriteLine($"  [FAIL] threw: {ex}");
            }
            finally
            {
                Environment.SetEnvironmentVariable("RYNTHCORE_TRACKER_SETTINGS", null);
                try { Directory.Delete(root, recursive: true); } catch { }
            }

            Console.WriteLine(_fails == 0
                ? $"PASS ({_asserts} assertions)"
                : $"FAIL: {_fails} of {_asserts} assertions failed");
            return _fails == 0 ? 0 : 1;
        }
    }
}

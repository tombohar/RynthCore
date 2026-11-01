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
    // process, so the order matters: an old-style file first, then saves.
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

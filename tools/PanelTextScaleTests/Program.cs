using System.Diagnostics;
using RynthCore.Engine.UI;

namespace RynthCore.Engine.ImGuiBackend
{
    /// <summary>Stub of the engine's fonts: counts the re-bake requests PanelTextScale makes.</summary>
    internal static class ImGuiFonts
    {
        internal static int Rebuilds;
        internal static void RequestRebuild(int delayMs = 0) => Rebuilds++;
    }
}

namespace PanelTextScaleTests
{
    // Offline tests for the toolbar text size. See the csproj.
    internal static class Program
    {
        private static int _fails, _asserts;
        private static void Check(bool cond, string msg)
        {
            _asserts++;
            if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
        }
        private static void Eq<T>(T actual, T expected, string msg) =>
            Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{msg}: expected '{expected}', got '{actual}'");

        // case name -> the file it starts from (null = no file)
        private static readonly (string Name, string? Seed, Action Body)[] Cases =
        {
            ("ChromeSurvivesSetForWindow", "__all=2\n__chrome=3\n", ChromeSurvivesSetForWindow),
            ("StepsInUseHasChromeWhenPerPanelOff", "__all=0\n__chrome=3\n", StepsInUseHasChromeWhenPerPanelOff),
            ("UseForAllAndSetAllLeaveChromeAlone", "__chrome=3\n", UseForAllAndSetAllLeaveChromeAlone),
            ("ChromeZeroIsNotBaked", "__all=0\n__chrome=0\n", ChromeZeroIsNotBaked),
            ("ChromeDoesNotFallBackToAll", "__all=5\n", ChromeDoesNotFallBackToAll),
            ("ChromeClamps", null, ChromeClamps),
            ("RoundTripWrite", null, RoundTripWrite),
            ("RoundTripRead", null, RoundTripRead),
        };

        private static int Main(string[] args)
        {
            if (args.Length > 1 && args[0] == "--child")
            {
                foreach (var c in Cases)
                    if (c.Name == args[1]) { c.Body(); return _fails == 0 && _asserts > 0 ? 0 : 1; }
                Console.WriteLine("  [FAIL] unknown case " + args[1]);
                return 1;
            }

            string root = Path.Combine(Path.GetTempPath(), "rynth-panel-text-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            int fails = 0;
            try
            {
                foreach (var c in Cases)
                {
                    if (c.Name == "RoundTripRead") continue;   // run right after RoundTripWrite, on its file
                    string file = Path.Combine(root, c.Name + ".txt");
                    if (c.Seed != null) File.WriteAllText(file, c.Seed);
                    Console.WriteLine(c.Name);
                    bool ok = RunChild(c.Name, file);
                    if (ok && c.Name == "RoundTripWrite")
                    {
                        Console.WriteLine("RoundTripRead");
                        ok = RunChild("RoundTripRead", file);
                    }
                    if (!ok) fails++;
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine();
            Console.WriteLine(fails == 0 ? "PASS" : $"FAIL ({fails} case(s) failed)");
            return fails == 0 ? 0 : 1;
        }

        private static bool RunChild(string name, string file)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--child");
            psi.ArgumentList.Add(name);
            psi.Environment["RYNTHCORE_PANEL_TEXT_SIZE"] = file;
            using Process p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (output.Length > 0) Console.Write(output);
            return p.ExitCode == 0;
        }

        private static string[] Lines(string? file = null) =>
            File.ReadAllLines(file ?? Environment.GetEnvironmentVariable("RYNTHCORE_PANEL_TEXT_SIZE")!);

        // Sizing one window with per-panel off rewrites every other row to the All
        // Panels size; the toolbar's row is not a panel and must keep its own.
        private static void ChromeSurvivesSetForWindow()
        {
            PanelTextScale.SetForWindow("Radar", 4);
            Eq(PanelTextScale.GetChrome(), 3, "chrome after SetForWindow (per-panel off)");
            Check(Lines().Contains("__chrome=3"), "chrome row still 3 in the file");
            Eq(PanelTextScale.GetOwn("Radar"), 4, "the window itself did change");
            Check(PanelTextScale.PerPanel, "SetForWindow switched per-panel on");
            Eq(PanelTextScale.Get("Other"), 2, "other panels stay at All Panels");
        }

        // Per-panel off: StepsInUse used to skip every non-All row, so the toolbar's
        // size would never have been baked.
        private static void StepsInUseHasChromeWhenPerPanelOff()
        {
            Check(!PanelTextScale.PerPanel, "per-panel is off");
            Eq(PanelTextScale.GetAll(), 0, "All Panels is 0");
            var steps = PanelTextScale.StepsInUse();
            Check(steps.Contains(3), "StepsInUse contains the chrome step 3 (got [" + string.Join(",", steps) + "])");
            Eq(steps.Count, 1, "and nothing else");
        }

        private static void UseForAllAndSetAllLeaveChromeAlone()
        {
            PanelTextScale.UseForAll(5);
            Eq(PanelTextScale.GetChrome(), 3, "chrome after UseForAll(5)");
            PanelTextScale.SetAll(2);
            Eq(PanelTextScale.GetChrome(), 3, "chrome after SetAll(2)");
            Eq(PanelTextScale.GetAll(), 2, "All Panels did change");
            var steps = PanelTextScale.StepsInUse();
            Check(steps.Contains(3) && steps.Contains(2), "both steps baked (got [" + string.Join(",", steps) + "])");
        }

        private static void ChromeZeroIsNotBaked()
        {
            Eq(PanelTextScale.StepsInUse().Count, 0, "nothing in use with chrome 0 and All 0");
            PanelTextScale.SetChrome(3);
            Check(PanelTextScale.StepsInUse().Contains(3), "chrome 3 baked");
            PanelTextScale.SetChrome(0);
            Eq(PanelTextScale.StepsInUse().Count, 0, "back to nothing after SetChrome(0)");
        }

        private static void ChromeDoesNotFallBackToAll()
        {
            Eq(PanelTextScale.GetAll(), 5, "All Panels is 5");
            Eq(PanelTextScale.GetChrome(), 0, "toolbar without its own row is normal size");
        }

        private static void ChromeClamps()
        {
            PanelTextScale.SetChrome(99);
            Eq(PanelTextScale.GetChrome(), PanelTextScale.SizeNames.Length - 1, "clamped to the top size");
            PanelTextScale.SetChrome(-4);
            Eq(PanelTextScale.GetChrome(), 0, "clamped to 0");
        }

        private static void RoundTripWrite()
        {
            int before = RynthCore.Engine.ImGuiBackend.ImGuiFonts.Rebuilds;
            PanelTextScale.SetChrome(4);
            Eq(RynthCore.Engine.ImGuiBackend.ImGuiFonts.Rebuilds, before + 1, "SetChrome requested one re-bake");
            Check(Lines().Contains("__chrome=4"), "file has __chrome=4");
            PanelTextScale.SetChrome(4);
            Eq(RynthCore.Engine.ImGuiBackend.ImGuiFonts.Rebuilds, before + 1, "same size again requests nothing");
        }

        // A fresh process reads what RoundTripWrite saved.
        private static void RoundTripRead()
        {
            Eq(PanelTextScale.GetChrome(), 4, "chrome read back from the file");
        }
    }
}

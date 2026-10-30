// tools/ThemeTests: the colour theme's presets, the player's own colours and saved schemes
// (RynthTheme.cs, ThemeSchemes.cs). Each case runs in its own process with RYNTHCORE_THEME_DIR
// pointing at a fresh temp folder (the theme reads its files once per process).
using System.Diagnostics;
using RynthCore.Engine.ImGuiBackend;

namespace RynthCore.Engine.UI
{
    /// <summary>Stub: the engine saves in the background; here at once, so a case can read the file back.</summary>
    internal static class UiBackgroundWriter
    {
        public static void Enqueue(string label, Action work) => work();
    }
}

namespace RynthCore.Engine
{
    internal static class RynthLog
    {
        public static void UI(string message) { }
    }
}

namespace ThemeTests
{
    internal static class Program
    {
        private const string Mine = "accent=112233\nhighlight=445566\nbackground=010203\ntitlebar=0A0B0C\nbutton=202122\nborder=303132\ntext=F0F1F2\nmuted=A0A1A2\n";
        private static readonly uint[] MineColours = { 0x112233, 0x445566, 0x010203, 0x0A0B0C, 0x202122, 0x303132, 0xF0F1F2, 0xA0A1A2 };

        private static int _failures;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
            if (!ok) _failures++;
        }

        private static string Dir => Environment.GetEnvironmentVariable("RYNTHCORE_THEME_DIR")!;
        private static string ThemeFile => File.ReadAllText(Path.Combine(Dir, "theme.txt")).Replace("\r", "");
        private static string[] SchemeFiles => Directory.Exists(Path.Combine(Dir, "themes"))
            ? Directory.GetFiles(Path.Combine(Dir, "themes"), "*.theme") : Array.Empty<string>();

        private static bool IsMine()
        {
            for (int i = 0; i < 8; i++)
                if ((RynthTheme.GetPrimary(i) & 0xFFFFFF) != MineColours[i]) return false;
            return true;
        }

        private static int Brass => Array.IndexOf(ThemePalette.PresetNames, "Brass");

        private static readonly (string Name, string? ThemeTxt, Action Run)[] Cases =
        {
            ("an older Custom file loads as Custom with its colours", "preset=Custom\n" + Mine, () =>
            {
                Check(RynthTheme.Preset == RynthTheme.Custom, "Custom in use");
                Check(IsMine(), "the eight colours as saved");
            }),
            ("picking a preset keeps the player's own colours, and Custom brings them back", "preset=Custom\n" + Mine, () =>
            {
                RynthTheme.SetPreset(Brass);
                Check(RynthTheme.Preset == Brass, "Brass in use");
                Check(!IsMine(), "Brass's colours on screen");
                string file = ThemeFile;
                Check(file.StartsWith("preset=Brass\n"), "theme.txt says Brass");
                Check(file.Contains("accent=112233") && file.Contains("muted=A0A1A2"), "theme.txt still has the player's eight");
                RynthTheme.SetPicker(RynthTheme.PickerNames().Length - 1);   // Custom
                Check(RynthTheme.Preset == RynthTheme.Custom && IsMine(), "Custom: the player's colours again");
            }),
            ("a preset file with the player's colours (written by this engine) restarts on the preset, Custom intact", "preset=Brass\n" + Mine, () =>
            {
                Check(RynthTheme.Preset == Brass, "Brass in use after a restart");
                RynthTheme.UseCustom();
                Check(IsMine(), "Custom: the player's colours");
            }),
            ("Reset Colours (RynthCore's) keeps the player's colours too", "preset=Custom\n" + Mine, () =>
            {
                RynthTheme.SetPreset(0);
                Check(RynthTheme.Preset == 0, "RynthCore in use");
                Check(ThemeFile.Contains("accent=112233"), "the player's colours still in theme.txt");
            }),
            ("no theme.txt: RynthCore's colours, nothing new written until a change", null, () =>
            {
                Check(RynthTheme.Preset == 0, "RynthCore (the default) in use");
                Check(!File.Exists(Path.Combine(Dir, "theme.txt")), "no theme.txt written");
            }),
            ("a preset-only file (an older engine's) writes no colours of its own", "preset=Ember\n", () =>
            {
                RynthTheme.SetPreset(Brass);
                Check(ThemeFile == "preset=Brass\n", "only the preset line: " + ThemeFile.Replace("\n", " | "));
            }),
            ("Save colours as: a scheme file, in the picker, in use, and back after a restart (part 1)", "preset=Custom\n" + Mine, () =>
            {
                Check(RynthTheme.SaveScheme("  Night watch  ") == "Night watch", "saved as \"Night watch\" (trimmed)");
                Check(SchemeFiles.Length == 1, "one scheme file");
                Check(RynthTheme.PickerNames().Contains("Night watch"), "listed in the Theme picker");
                Check(RynthTheme.SchemeInUse && RynthTheme.SchemeName == "Night watch", "the scheme is in use");
                Check(ThemeFile.StartsWith("preset=scheme:Night watch\n"), "theme.txt names the scheme");
                int idx = RynthTheme.PickerIndex;
                Check(RynthTheme.PickerNames()[idx] == "Night watch", "the picker shows it selected");
            }),
            ("a scheme picked from the picker shows its colours; a scheme that is gone falls back to Custom", "preset=Custom\n" + Mine, () =>
            {
                RynthTheme.SaveScheme("Copy");
                RynthTheme.SetPreset(Brass);
                int i = Array.IndexOf(RynthTheme.PickerNames(), "Copy");
                RynthTheme.SetPicker(i);
                Check(RynthTheme.SchemeInUse && IsMine(), "\"Copy\" in use with its colours");
                Check(!RynthTheme.UseScheme("No such scheme"), "an unknown name is refused");
            }),
            ("names: a preset's or Custom get My in front; empty is refused; 40 characters at most", null, () =>
            {
                Check(ThemeSchemes.CleanName("Brass") == "My Brass", "Brass -> My Brass");
                Check(ThemeSchemes.CleanName("custom") == "My custom", "custom -> My custom");
                Check(ThemeSchemes.CleanName("   ") == "", "blank refused");
                Check(ThemeSchemes.CleanName(new string('x', 60)).Length == 40, "60 characters cut to 40");
                Check(RynthTheme.SaveScheme("") == "", "Save with no name does nothing");
            }),
            ("the same name replaces the scheme; Delete removes the one in use and keeps its colours as Custom", "preset=Custom\n" + Mine, () =>
            {
                RynthTheme.SaveScheme("Mine");
                RynthTheme.SaveScheme("mine");
                Check(SchemeFiles.Length == 1, "still one file after saving the same name again");
                Check(RynthTheme.DeleteCurrentScheme(), "deleted");
                Check(SchemeFiles.Length == 0, "the file is gone");
                Check(RynthTheme.Preset == RynthTheme.Custom && IsMine(), "Custom with the same colours");
                Check(!RynthTheme.PickerNames().Contains("Mine"), "not in the picker any more");
            }),
            ("changing a colour on a preset first keeps the player's own colours as \"My colours\"", "preset=Brass\n" + Mine, () =>
            {
                RynthTheme.SetPrimary(0, 0xFF00FF00);
                Check(RynthTheme.Preset == RynthTheme.Custom, "Custom after the change");
                Check(RynthTheme.PickerNames().Contains("My colours"), "\"My colours\" saved");
                int i = Array.IndexOf(RynthTheme.PickerNames(), "My colours");
                RynthTheme.SetPicker(i);
                Check(IsMine(), "\"My colours\" holds the colours from before");
                RynthTheme.UseCustom();
                Check((RynthTheme.GetPrimary(0) & 0xFFFFFF) == 0x00FF00, "Custom holds the change (Brass + green accent)");
            }),
            ("no second \"My colours\" when they are already kept", "preset=Brass\n" + Mine, () =>
            {
                RynthTheme.UseCustom();
                RynthTheme.SaveScheme("Already");   // the player's own colours, kept by name
                RynthTheme.SetPreset(Brass);
                RynthTheme.SetPrimary(0, 0xFF00FF00);
                Check(!RynthTheme.PickerNames().Contains("My colours"), "nothing new: \"Already\" has them");
            }),
            ("changing a colour while on Custom saves nothing extra", "preset=Custom\n" + Mine, () =>
            {
                RynthTheme.SetPrimary(1, 0xFFFF0000);
                Check(SchemeFiles.Length == 0, "no scheme files");
                Check(ThemeFile.Contains("highlight=FF0000"), "theme.txt has the change");
            }),
        };

        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--case")
            {
                Cases[int.Parse(args[1])].Run();
                return _failures == 0 ? 0 : 1;
            }

            string exe = Environment.ProcessPath!;
            string root = Path.Combine(Path.GetTempPath(), "rynth-themetests-" + Environment.ProcessId);
            int failed = 0;
            for (int i = 0; i < Cases.Length; i++)
            {
                string dir = Path.Combine(root, i.ToString());
                Directory.CreateDirectory(dir);
                if (Cases[i].ThemeTxt != null) File.WriteAllText(Path.Combine(dir, "theme.txt"), Cases[i].ThemeTxt);
                var psi = new ProcessStartInfo(exe, $"--case {i}") { RedirectStandardOutput = true, UseShellExecute = false };
                psi.Environment["RYNTHCORE_THEME_DIR"] = dir;
                using var p = Process.Start(psi)!;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Console.WriteLine($"-- {Cases[i].Name}");
                Console.Write(output);
                if (p.ExitCode != 0) failed++;
            }
            try { Directory.Delete(root, true); } catch { }
            Console.WriteLine();
            Console.WriteLine(failed == 0 ? $"PASS ({Cases.Length} cases)" : $"FAIL ({failed} of {Cases.Length} cases)");
            return failed == 0 ? 0 : 1;
        }
    }
}

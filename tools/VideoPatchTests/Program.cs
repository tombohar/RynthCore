using RynthCore.Engine.D3D9;

namespace VideoPatchTests;

/// <summary>The video patch's modes, focus logic, frame cap and settings file (VideoPatchPolicy).</summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        OffNeverSkips();
        WorldSkipsSceneOnly();
        UnfocusedFollowsFocus();
        AllSkipsUiOnlyWithImGui();
        OutsideTheWorldNothingSkips();
        FrameCap();
        CommandWords();
        ComboMatchesEnum();
        CharacterKeys();
        FileRoundTrip();
        AllIsNeverSaved();
        OffRemovesTheLine();
        DefaultLineAndUnknownLines();
        BadFileValues();

        Console.WriteLine(_failed == 0 ? $"PASS ({_checks} assertions)" : $"FAIL ({_failed} of {_checks} assertions)");
        return _failed == 0 ? 0 : 1;
    }

    private static void Case(string name) { _case = name; Console.WriteLine("ok   " + name); }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"FAIL {_case}: {what}");
    }

    private static VideoPatchDecision D(VideoPatchMode m, bool inWorld = true, bool focused = true, bool imgui = true) =>
        VideoPatchPolicy.Decide(m, inWorld, focused, imgui);

    private static void OffNeverSkips()
    {
        Case("OffNeverSkips");
        foreach (bool w in new[] { true, false })
            foreach (bool f in new[] { true, false })
                foreach (bool i in new[] { true, false })
                    Check(!D(VideoPatchMode.Off, w, f, i).Any, $"off skipped something (world={w} focused={f} imgui={i})");
    }

    private static void WorldSkipsSceneOnly()
    {
        Case("WorldSkipsSceneOnly");
        foreach (bool f in new[] { true, false })
        {
            var d = D(VideoPatchMode.World, focused: f);
            Check(d.SkipWorld, "world mode must skip the 3D scene");
            Check(!d.SkipAcUi, "world mode must keep AC's UI");
        }
    }

    private static void UnfocusedFollowsFocus()
    {
        Case("UnfocusedFollowsFocus");
        Check(!D(VideoPatchMode.Unfocused, focused: true).Any, "focused client must draw everything");
        var bg = D(VideoPatchMode.Unfocused, focused: false);
        Check(bg.SkipWorld && !bg.SkipAcUi, "background client: world skipped, UI kept");
        // Switching back is per frame: the decision has no memory.
        Check(!D(VideoPatchMode.Unfocused, focused: true).Any, "focus back must draw again at once");
    }

    private static void AllSkipsUiOnlyWithImGui()
    {
        Case("AllSkipsUiOnlyWithImGui");
        var all = D(VideoPatchMode.All);
        Check(all.SkipWorld && all.SkipAcUi, "all with ImGui: scene and AC UI skipped");
        var noImgui = D(VideoPatchMode.All, imgui: false);
        Check(noImgui.SkipWorld && !noImgui.SkipAcUi, "all without the ImGui layer falls back to world (AC's chat stays)");
        Check(D(VideoPatchMode.All, focused: true).SkipAcUi, "all ignores focus");
    }

    private static void OutsideTheWorldNothingSkips()
    {
        Case("OutsideTheWorldNothingSkips");
        foreach (VideoPatchMode m in Enum.GetValues<VideoPatchMode>())
            foreach (bool f in new[] { true, false })
                Check(!D(m, inWorld: false, focused: f).Any, $"{m} skipped outside the world (login/character select must draw)");
    }

    private static void FrameCap()
    {
        Case("FrameCap");
        var skip = new VideoPatchDecision(true, false);
        Check(VideoPatchPolicy.FrameCap(skip, 15) == 15, "default cap");
        Check(VideoPatchPolicy.FrameCap(skip, 0) == 0, "0 = no cap");
        Check(VideoPatchPolicy.FrameCap(skip, 2) == VideoPatchPolicy.MinFrameCapFps, "cap clamps up to the minimum");
        Check(VideoPatchPolicy.FrameCap(skip, 500) == VideoPatchPolicy.MaxFrameCapFps, "cap clamps down to the maximum");
        Check(VideoPatchPolicy.FrameCap(VideoPatchDecision.None, 15) == 0, "no cap while drawing");
        Check(VideoPatchPolicy.FrameCap(D(VideoPatchMode.Unfocused, focused: true), 15) == 0, "no cap for a focused 'unfocused' client");
        Check(VideoPatchPolicy.FrameCap(D(VideoPatchMode.Unfocused, focused: false), 15) == 15, "cap for a background 'unfocused' client");
        Check(VideoPatchPolicy.ClampCap(-3) == 0, "negative = no cap");
    }

    private static void CommandWords()
    {
        Case("CommandWords");
        (string word, VideoPatchMode mode)[] words =
        {
            ("off", VideoPatchMode.Off), ("OFF", VideoPatchMode.Off), ("on", VideoPatchMode.World),
            ("world", VideoPatchMode.World), ("unfocused", VideoPatchMode.Unfocused), ("focus", VideoPatchMode.Unfocused),
            ("all", VideoPatchMode.All), (" all ", VideoPatchMode.All),
            ("enable", VideoPatchMode.World), ("Disable", VideoPatchMode.Off),   // /rc|/ra|/ub videopatch enable|disable
        };
        foreach ((string word, VideoPatchMode mode) in words)
            Check(VideoPatchPolicy.TryParse(word, out VideoPatchMode m) && m == mode, $"'{word}' -> {mode}");
        Check(!VideoPatchPolicy.TryParse("status", out _), "'status' is not a mode");
        Check(!VideoPatchPolicy.TryParse(null, out _), "null is not a mode");
        foreach (VideoPatchMode m in Enum.GetValues<VideoPatchMode>())
            Check(VideoPatchPolicy.TryParse(VideoPatchPolicy.Name(m), out VideoPatchMode back) && back == m, $"Name({m}) round-trips");
    }

    private static void ComboMatchesEnum()
    {
        Case("ComboMatchesEnum");
        Check(VideoPatchPolicy.ComboItems.Length == Enum.GetValues<VideoPatchMode>().Length, "one combo item per mode");
        Check(VideoPatchPolicy.ComboItems[(int)VideoPatchMode.Off] == "Off", "index 0 is Off");
        Check(VideoPatchPolicy.ComboItems[(int)VideoPatchMode.All].Contains("session"), "All says it is session only");
    }

    private static void CharacterKeys()
    {
        Case("CharacterKeys");
        Check(VideoPatchPolicy.CharacterKey("Aelrynth", "Some Guy") == "Aelrynth/Some Guy", "server/character");
        Check(VideoPatchPolicy.CharacterKey("", "Some Guy") == "Some Guy", "no server");
        Check(VideoPatchPolicy.CharacterKey("Aelrynth", "") == VideoPatchPolicy.DefaultCharacterKey, "no character = Default");
        Check(VideoPatchPolicy.CharacterKey(null, null) == VideoPatchPolicy.DefaultCharacterKey, "nulls = Default");
        Check(!VideoPatchPolicy.CharacterKey("A=B", "C").Contains('='), "no '=' in a key");
    }

    private static VideoPatchPolicy.FileModel RoundTripModel(VideoPatchPolicy.FileModel m) =>
        VideoPatchPolicy.Parse(VideoPatchPolicy.Serialize(m));

    private static void FileRoundTrip()
    {
        Case("FileRoundTrip");
        var m = VideoPatchPolicy.Parse(null);
        Check(m.FrameCapFps == VideoPatchPolicy.DefaultFrameCapFps, "missing file = default cap");
        Check(m.ModeFor("Aelrynth/A") == VideoPatchMode.Off, "missing file = off");
        m.FrameCapFps = 10;
        m.Set("Aelrynth/A", VideoPatchMode.World);
        m.Set("Aelrynth/B", VideoPatchMode.Unfocused);
        var back = RoundTripModel(m);
        Check(back.FrameCapFps == 10, "cap round-trips");
        Check(back.ModeFor("Aelrynth/A") == VideoPatchMode.World, "A round-trips");
        Check(back.ModeFor("aelrynth/b") == VideoPatchMode.Unfocused, "B round-trips (case-insensitive)");
        Check(back.ModeFor("Aelrynth/C") == VideoPatchMode.Off, "unknown character = off");
        var twice = RoundTripModel(back);
        Check(string.Join("\n", VideoPatchPolicy.Serialize(twice)) == string.Join("\n", VideoPatchPolicy.Serialize(back)), "serialize is stable");
        Check(VideoPatchPolicy.Serialize(twice).Count(l => l.StartsWith("# RynthCore video patch")) == 1, "one header line");
    }

    private static void AllIsNeverSaved()
    {
        Case("AllIsNeverSaved");
        var m = VideoPatchPolicy.Parse(null);
        m.Set("X/Y", VideoPatchMode.World);
        Check(!m.Set("X/Y", VideoPatchMode.All), "Set(all) refuses");
        Check(m.ModeFor("X/Y") == VideoPatchMode.World, "Set(all) keeps the saved mode");
        var hand = VideoPatchPolicy.Parse(new[] { "X/Z=all" });
        Check(hand.ModeFor("X/Z") == VideoPatchMode.Off, "a hand-written 'all' loads as off (a start never hides AC's UI)");
    }

    private static void OffRemovesTheLine()
    {
        Case("OffRemovesTheLine");
        var m = VideoPatchPolicy.Parse(null);
        m.Set("X/Y", VideoPatchMode.World);
        m.Set("X/Y", VideoPatchMode.Off);
        Check(!VideoPatchPolicy.Serialize(m).Any(l => l.StartsWith("X/Y")), "off leaves no line");
    }

    private static void DefaultLineAndUnknownLines()
    {
        Case("DefaultLineAndUnknownLines");
        var m = VideoPatchPolicy.Parse(new[] { "Default=unfocused", "SomethingNew=42", "# a note", "Aelrynth/A=world" });
        Check(m.ModeFor("Aelrynth/Q") == VideoPatchMode.Unfocused, "Default applies to characters without a line");
        Check(m.ModeFor("Aelrynth/A") == VideoPatchMode.World, "own line wins over Default");
        var lines = VideoPatchPolicy.Serialize(m);
        Check(lines.Contains("SomethingNew=42"), "unknown key kept");
        Check(lines.Contains("# a note"), "comment kept");
    }

    private static void BadFileValues()
    {
        Case("BadFileValues");
        var m = VideoPatchPolicy.Parse(new[] { "FrameCapFps=abc", "Aelrynth/A=sideways", "=world", "" });
        Check(m.FrameCapFps == VideoPatchPolicy.DefaultFrameCapFps, "bad cap keeps the default");
        Check(m.ModeFor("Aelrynth/A") == VideoPatchMode.Off, "bad mode = off");
        Check(VideoPatchPolicy.Parse(new[] { "FrameCapFps=1" }).FrameCapFps == VideoPatchPolicy.MinFrameCapFps, "cap 1 clamps to the minimum");
        Check(VideoPatchPolicy.Parse(new[] { "FrameCapFps=0" }).FrameCapFps == 0, "cap 0 = none");
    }
}

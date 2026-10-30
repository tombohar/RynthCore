using RynthCore.Engine.Hotkeys;

namespace HotkeyTests;

// Offline tests for the user hotkeys. See the csproj.
internal static class Program
{
    private static int _fails, _asserts;

    private static void Check(bool cond, string msg)
    {
        _asserts++;
        if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
    }

    private const int VK_F1 = 0x70, VK_F5 = 0x74, VK_F12 = 0x7B, VK_NUMPAD3 = 0x63, VK_ADD = 0x6B, VK_PRIOR = 0x21,
        VK_NEXT = 0x22, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_LSHIFT = 0xA0, VK_RCONTROL = 0xA3, VK_SHIFT = 0x10,
        VK_CONTROL = 0x11, VK_OEM_PLUS = 0xBB, VK_OEM_3 = 0xC0, VK_HOME = 0x24, VK_SPACE = 0x20, VK_UP = 0x26;

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "rynth-hotkey-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Run("ComboParsing", ComboParsing);
            Run("ComboFormatting", ComboFormatting);
            Run("ComboRoundTripEveryKey", ComboRoundTripEveryKey);
            Run("ComboParseErrors", ComboParseErrors);
            Run("MatchingWithModifiers", MatchingWithModifiers);
            Run("NormalizeVk", NormalizeVk);
            Run("KeyRepeatEdge", KeyRepeatEdge);
            Run("TrackerEatsTheWholeKeystroke", TrackerEatsTheWholeKeystroke);
            Run("TypingSuppression", TypingSuppression);
            Run("PersistenceRoundTrip", () => PersistenceRoundTrip(root));
            Run("PersistenceToleratesJunk", PersistenceToleratesJunk);
            Run("ConflictDetection", ConflictDetection);
            Run("ActionsResolve", ActionsResolve);
            Run("CommandParsing", CommandParsing);
            Run("VTankActions", VTankActions);
            Run("MetaStateParam", MetaStateParam);
            Run("OneTouchHeal", OneTouchHeal);
            Run("DefaultKeys", DefaultKeys);
            Run("IltPanelsOnlyOnIlt", IltPanelsOnlyOnIlt);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HotkeyStore.EnvOverride, null);
            try { Directory.Delete(root, true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine(_fails == 0 && _asserts > 0 ? $"PASS ({_asserts} checks)" : $"FAIL ({_fails} of {_asserts} checks)");
        return _fails == 0 && _asserts > 0 ? 0 : 1;
    }

    private static void Run(string name, Action body)
    {
        Console.WriteLine(name);
        try { body(); }
        catch (Exception ex) { _fails++; _asserts++; Console.WriteLine($"  [FAIL] threw {ex.GetType().Name}: {ex.Message}"); }
    }

    private static KeyCombo P(string text)
    {
        bool ok = HotkeyKeys.TryParse(text, out KeyCombo c, out string err);
        Check(ok, $"'{text}' parses ({err})");
        return c;
    }

    private static void ComboParsing()
    {
        Check(P("F1") == new KeyCombo(VK_F1, HotkeyMods.None), "F1");
        Check(P("ctrl+f5") == new KeyCombo(VK_F5, HotkeyMods.Ctrl), "ctrl+f5 (case)");
        Check(P("Ctrl + Shift + F12") == new KeyCombo(VK_F12, HotkeyMods.Ctrl | HotkeyMods.Shift), "spaces");
        Check(P("Shift+Ctrl+F12") == P("Ctrl+Shift+F12"), "modifier order doesn't matter");
        Check(P("Alt+Num3") == new KeyCombo(VK_NUMPAD3, HotkeyMods.Alt), "Num3");
        Check(P("Numpad3") == new KeyCombo(VK_NUMPAD3, HotkeyMods.None), "Numpad3 alias");
        Check(P("Ctrl+Num+") == new KeyCombo(VK_ADD, HotkeyMods.Ctrl), "Num+ after a modifier");
        Check(P("Num+") == new KeyCombo(VK_ADD, HotkeyMods.None), "Num+ alone");
        Check(P("Shift+=") == new KeyCombo(VK_OEM_PLUS, HotkeyMods.Shift), "= key");
        Check(P("Plus") == new KeyCombo(VK_OEM_PLUS, HotkeyMods.None), "Plus alias");
        Check(P("PgUp") == new KeyCombo(VK_PRIOR, HotkeyMods.None), "PgUp alias");
        Check(P("Page Down") == new KeyCombo(VK_NEXT, HotkeyMods.None), "Page Down (space)");
        Check(P("Control+`") == new KeyCombo(VK_OEM_3, HotkeyMods.Ctrl), "Control alias, backtick");
        Check(P("1") == new KeyCombo('1', HotkeyMods.None), "number row");
        Check(P("Alt+q") == new KeyCombo('Q', HotkeyMods.Alt), "letter, lower case");
        Check(P("Del") == new KeyCombo(0x2E, HotkeyMods.None), "Del alias");
        Check(P("Ctrl+Alt+Shift+Home") == new KeyCombo(VK_HOME, HotkeyMods.Ctrl | HotkeyMods.Alt | HotkeyMods.Shift), "all three");
        Check(P("\\") == new KeyCombo(0xDC, HotkeyMods.None), "backslash");
    }

    private static void ComboFormatting()
    {
        Check(new KeyCombo(VK_F1, HotkeyMods.Shift | HotkeyMods.Ctrl).ToString() == "Ctrl+Shift+F1", "Ctrl before Shift");
        Check(new KeyCombo(VK_NUMPAD3, HotkeyMods.Alt | HotkeyMods.Ctrl | HotkeyMods.Shift).ToString() == "Ctrl+Alt+Shift+Num3", "all mods");
        Check(new KeyCombo(VK_PRIOR, HotkeyMods.None).ToString() == "PageUp", "PageUp");
        Check(new KeyCombo(VK_ADD, HotkeyMods.Ctrl).ToString() == "Ctrl+Num+", "Ctrl+Num+");
        Check(default(KeyCombo).ToString() == "(none)", "empty");
        Check(HotkeyKeys.KeyName(0xE7) == "Key 0xE7", "unnamed key");
    }

    private static void ComboRoundTripEveryKey()
    {
        int named = 0;
        for (int vk = 1; vk < 256; vk++)
        {
            if (!HotkeyKeys.IsBindable(vk)) continue;
            named++;
            foreach (HotkeyMods m in new[] { HotkeyMods.None, HotkeyMods.Ctrl, HotkeyMods.Alt | HotkeyMods.Shift, HotkeyMods.Ctrl | HotkeyMods.Alt | HotkeyMods.Shift })
            {
                var c = new KeyCombo(vk, m);
                bool ok = HotkeyKeys.TryParse(c.ToString(), out KeyCombo back, out string err);
                Check(ok && back == c, $"{c} round-trips ({err})");
            }
        }
        // Letters, digits, F1-F24, numpad 0-9 + 5 ops, 6 nav keys + 4 arrows, space/tab/enter/backspace/pause/scroll, 11 punctuation.
        Check(named == 26 + 10 + 24 + 15 + 6 + 4 + 6 + 11, $"every named key but Esc is bindable ({named})");
        Check(!HotkeyKeys.IsBindable(VK_ESCAPE), "Esc not bindable");
        Check(!HotkeyKeys.IsBindable(VK_SHIFT) && HotkeyKeys.IsModifierKey(VK_LSHIFT) && HotkeyKeys.IsModifierKey(VK_RCONTROL), "modifiers");
    }

    private static void ComboParseErrors()
    {
        foreach (string bad in new[] { "", "   ", "Ctrl", "Ctrl+", "Shift+Alt", "Hyper+F1", "F25", "Esc", "Ctrl+Escape", "Banana" })
        {
            bool ok = HotkeyKeys.TryParse(bad, out _, out string err);
            Check(!ok && err.Length > 0, $"'{bad}' refused with a reason");
        }
        HotkeyKeys.TryParse("Ctrl", out _, out string e1);
        Check(e1.Contains("modifier", StringComparison.OrdinalIgnoreCase), "modifier-alone message");
        HotkeyKeys.TryParse("Esc", out _, out string e2);
        Check(e2.Contains("Esc"), "Esc message");
    }

    private static void MatchingWithModifiers()
    {
        var list = new List<HotkeyBinding>
        {
            new(new KeyCombo(VK_F1, HotkeyMods.Ctrl), "macro"),
            new(new KeyCombo(VK_F1, HotkeyMods.None), "combat"),
            new(new KeyCombo(VK_F5, HotkeyMods.Shift), "command", "/ra start", Enabled: false),
            new(new KeyCombo(VK_F1, HotkeyMods.Ctrl), "looting"),   // shadowed by the first
        };
        Check(HotkeyMatch.Find(list, new KeyCombo(VK_F1, HotkeyMods.Ctrl))?.Action == "macro", "Ctrl+F1 -> first binding");
        Check(HotkeyMatch.Find(list, new KeyCombo(VK_F1, HotkeyMods.None))?.Action == "combat", "F1 -> its own binding");
        Check(HotkeyMatch.Find(list, new KeyCombo(VK_F1, HotkeyMods.Ctrl | HotkeyMods.Shift)) == null, "Ctrl+Shift+F1 doesn't fire Ctrl+F1");
        Check(HotkeyMatch.Find(list, new KeyCombo(VK_F1, HotkeyMods.Alt)) == null, "Alt+F1 unbound");
        Check(HotkeyMatch.Find(list, new KeyCombo(VK_F5, HotkeyMods.Shift)) == null, "disabled binding doesn't fire");
        Check(HotkeyMatch.Find(list, default) == null, "empty combo");
        Check(HotkeyMatch.Mods(true, false, true) == (HotkeyMods.Ctrl | HotkeyMods.Shift), "Mods()");
        Check(HotkeyMatch.Mods(false, true, false) == HotkeyMods.Alt, "Mods() alt");
    }

    private static void NormalizeVk()
    {
        Check(HotkeyMatch.NormalizeVk(VK_RETURN, extended: true) == 0, "numpad Enter is AC's");
        Check(HotkeyMatch.NormalizeVk(VK_RETURN, extended: false) == VK_RETURN, "main Enter");
        Check(HotkeyMatch.NormalizeVk(VK_LSHIFT, false) == VK_SHIFT, "LShift -> Shift");
        Check(HotkeyMatch.NormalizeVk(VK_RCONTROL, true) == VK_CONTROL, "RCtrl -> Ctrl");
        Check(HotkeyMatch.NormalizeVk(VK_HOME, extended: true) == VK_HOME && HotkeyMatch.NormalizeVk(VK_HOME, false) == VK_HOME,
            "Home on the nav block and on the numpad (NumLock off) are the same key");
        Check(HotkeyMatch.IsExtended(0x01000000) && !HotkeyMatch.IsExtended(0x00000001), "extended bit");
    }

    private static void KeyRepeatEdge()
    {
        long firstDown = 0x00000001;                 // repeat count 1, previous state up
        long repeat = 0x40000001;                    // bit 30: the key was already down
        long extendedFirst = 0x01000001;
        Check(HotkeyMatch.IsFreshPress(firstDown), "first key-down fires");
        Check(!HotkeyMatch.IsFreshPress(repeat), "auto-repeat doesn't");
        Check(HotkeyMatch.IsFreshPress(extendedFirst), "extended first press fires");
        Check(!HotkeyMatch.IsFreshPress(unchecked((int)0xC0000001)), "key-up-shaped lParam (bits 30/31) isn't a fresh press");

        // Simulate holding a bound key: one fire, then repeats, then a new press fires again.
        var list = new List<HotkeyBinding> { new(new KeyCombo(VK_F5, HotkeyMods.None), "macro") };
        int fires = 0;
        foreach (long lp in new[] { firstDown, repeat, repeat, repeat })
            if (HotkeyMatch.IsFreshPress(lp) && HotkeyMatch.Find(list, new KeyCombo(VK_F5, 0)) != null) fires++;
        Check(fires == 1, $"holding F5 fires once ({fires})");
        if (HotkeyMatch.IsFreshPress(firstDown)) fires++;
        Check(fires == 2, "release and press again fires again");
    }

    private static void TrackerEatsTheWholeKeystroke()
    {
        var t = new HotkeyKeyTracker();
        // F5 fired: its char, repeats and key-up are eaten.
        t.Took(VK_F5);
        Check(t.TakeChar(), "the char from the hotkey's key-down is eaten");
        Check(!t.TakeChar(), "only that one char");
        Check(t.IsEaten(VK_F5), "repeat of the hotkey key is eaten");
        t.Took(VK_F5);   // what the service does on a repeat
        Check(t.TakeChar(), "the repeat's char is eaten too");
        Check(!t.IsEaten('W'), "another key isn't eaten");
        t.OtherKeyDown();
        Check(!t.TakeChar(), "a char after another key's down passes");
        Check(!t.KeyUp('W'), "another key's up passes");
        Check(t.KeyUp(VK_F5), "the hotkey key's up is eaten");
        Check(!t.KeyUp(VK_F5), "a second up passes");
        Check(!t.IsEaten(VK_F5), "after the up the key is free again");

        t.Took(VK_F1);
        t.Reset();
        Check(!t.IsEaten(VK_F1) && !t.TakeChar() && !t.KeyUp(VK_F1), "focus loss forgets held keys");
        Check(!t.KeyUp(999) && !t.IsEaten(-1), "out-of-range keys are ignored");
    }

    private static void TypingSuppression()
    {
        var f1 = new KeyCombo(VK_F1, HotkeyMods.None);
        var w = new KeyCombo('W', HotkeyMods.None);
        var ctrlW = new KeyCombo('W', HotkeyMods.Ctrl);
        var altNum = new KeyCombo(VK_NUMPAD3, HotkeyMods.Alt);
        var shiftQ = new KeyCombo('Q', HotkeyMods.Shift);

        var ok = new TypingState(InWorld: true, GameFocused: true, RynthTextFocus: false, AcTextFocus: false);
        Check(HotkeyRules.Check(ok, w) == HotkeyGate.Fire, "in game, nobody typing: fires");
        Check(HotkeyRules.Check(ok with { InWorld = false }, f1) == HotkeyGate.NotInWorld, "character select: no");
        Check(HotkeyRules.Check(ok with { GameFocused = false }, f1) == HotkeyGate.NotFocused, "another app in front: no");
        Check(HotkeyRules.Check(ok with { RynthTextFocus = true }, ctrlW) == HotkeyGate.RynthTyping, "RynthCore text box: no, even Ctrl combos");
        Check(HotkeyRules.Check(ok with { AcTextFocus = true }, f1) == HotkeyGate.AcTyping, "AC chat typing: no, even F keys");
        Check(HotkeyRules.Check(ok with { AcTextFocus = true, RynthTextFocus = true }, f1) == HotkeyGate.RynthTyping, "ours first");

        var unknown = ok with { AcTextFocus = null };
        Check(HotkeyRules.Check(unknown, w) == HotkeyGate.AcUnknownTextKey, "AC focus unknown: a letter could be typing");
        Check(HotkeyRules.Check(unknown, shiftQ) == HotkeyGate.AcUnknownTextKey, "Shift+letter could be typing");
        Check(HotkeyRules.Check(unknown, ctrlW) == HotkeyGate.Fire, "AC focus unknown: Ctrl combo fires");
        Check(HotkeyRules.Check(unknown, altNum) == HotkeyGate.Fire, "AC focus unknown: Alt combo fires");
        Check(HotkeyRules.Check(unknown, f1) == HotkeyGate.Fire, "AC focus unknown: F key fires");
        Check(HotkeyRules.CantBeTyping(new KeyCombo(VK_F12, HotkeyMods.Shift)) && !HotkeyRules.CantBeTyping(w), "CantBeTyping");
        foreach (HotkeyGate g in Enum.GetValues<HotkeyGate>())
            Check(HotkeyRules.Describe(g).Length > 0, $"{g} described");
    }

    private static void PersistenceRoundTrip(string root)
    {
        var file = new HotkeyFile(false, new List<HotkeyBinding>
        {
            new(new KeyCombo(VK_F1, HotkeyMods.Ctrl), "macro"),
            new(new KeyCombo(VK_ADD, HotkeyMods.Alt | HotkeyMods.Shift), "command", "/rn bc /ra stop", Enabled: false),
            new(new KeyCombo(0xDC, HotkeyMods.None), "panel:Dungeon Map"),
            new(new KeyCombo('5', HotkeyMods.Ctrl), "command", "/lua myscript go\twith a tab"),
        });
        string text = HotkeyStore.Serialize(file);
        HotkeyFile back = HotkeyStore.Parse(text);
        Check(!back.Enabled, "master switch round-trips");
        Check(back.Bindings.Count == 4, $"4 bindings ({back.Bindings.Count})");
        for (int i = 0; i < Math.Min(3, back.Bindings.Count); i++)
            Check(back.Bindings[i] == file.Bindings[i], $"binding {i} round-trips: {back.Bindings[i]}");
        Check(back.Bindings.Count > 3 && back.Bindings[3].Command == "/lua myscript go with a tab", "a tab in a command becomes a space");
        Check(back.Bindings.Count > 1 && back.Bindings[1].Combo.ToString() == "Alt+Shift+Num+", "Num+ survives the file");

        // Through the real file (temp path via the override).
        string path = Path.Combine(root, "sub", "hotkeys.txt");
        Environment.SetEnvironmentVariable(HotkeyStore.EnvOverride, path);
        Check(HotkeyStore.FilePath == path, "override path used");
        HotkeyFile none = HotkeyStore.Load();
        Check(none.Enabled && none.Bindings.Count == 0, "no file: on, empty (nothing bound by default)");
        Check(HotkeyStore.LastWriteUtc() == DateTime.MinValue, "no file: no write time");
        Check(HotkeyStore.Save(file), "save");
        Check(File.Exists(path) && !File.Exists(path + ".tmp"), "file written, temp file gone");
        Check(HotkeyStore.LastWriteUtc() > DateTime.MinValue, "write time");
        HotkeyFile loaded = HotkeyStore.Load();
        Check(loaded.Bindings.Count == 4 && loaded.Bindings[0] == file.Bindings[0] && !loaded.Enabled, "load what was saved");
        Check(HotkeyStore.Save(loaded with { Enabled = true }), "save again (replace)");
        Check(HotkeyStore.Load().Enabled, "replaced");
    }

    private static void PersistenceToleratesJunk()
    {
        string text = "# comment\r\n\r\nenabled=1\r\non\tF1\tmacro\t\r\nthis is junk\r\non\tBanana\tmacro\t\r\non\tF2\t\t\r\n" +
                      "off\tCtrl+F3\tcombat\r\non\tF4\tcommand\t/ra stop\r\n   # indented comment\n";
        HotkeyFile f = HotkeyStore.Parse(text);
        Check(f.Enabled, "enabled");
        Check(f.Bindings.Count == 3, $"junk, unknown key and empty action skipped ({f.Bindings.Count})");
        Check(f.Bindings.Count == 3 && !f.Bindings[1].Enabled && f.Bindings[1].Action == "combat", "off parsed");
        Check(f.Bindings.Count == 3 && f.Bindings[2].IsCommand && f.Bindings[2].Command == "/ra stop", "command parsed (CRLF trimmed)");
        Check(HotkeyStore.Parse(null).Bindings.Count == 0 && HotkeyStore.Parse("").Enabled, "empty text");
        Check(!HotkeyStore.Parse("enabled=0\n").Enabled, "enabled=0");
    }

    private static void ConflictDetection()
    {
        var list = new List<HotkeyBinding>
        {
            new(new KeyCombo(VK_F1, HotkeyMods.None), "macro"),
            new(new KeyCombo(VK_F5, HotkeyMods.Ctrl), "combat", Enabled: false),
            new(new KeyCombo(VK_F1, HotkeyMods.None), "looting"),
        };
        List<string> add = HotkeyConflicts.Warnings(list, new KeyCombo(VK_F1, HotkeyMods.None));
        Check(add.Count == 2 && add[0].Contains("Start/stop the macro") && add[0].Contains("that one fires"), $"new F1: both existing reported ({string.Join(" | ", add)})");
        List<string> edit0 = HotkeyConflicts.Warnings(list, new KeyCombo(VK_F1, HotkeyMods.None), ignoreIndex: 0);
        Check(edit0.Count == 1 && edit0[0].Contains("this one fires"), "editing the first: it wins over the later duplicate");
        List<string> edit2 = HotkeyConflicts.Warnings(list, new KeyCombo(VK_F1, HotkeyMods.None), ignoreIndex: 2);
        Check(edit2.Count == 1 && edit2[0].Contains("that one fires"), "editing the later one: the first wins");
        List<string> off = HotkeyConflicts.Warnings(list, new KeyCombo(VK_F5, HotkeyMods.Ctrl));
        Check(off.Count == 1 && off[0].Contains("switched off"), "duplicate of a switched-off binding says so");
        Check(HotkeyConflicts.Warnings(list, new KeyCombo(VK_F12, HotkeyMods.Alt)).Count == 0, "free combo: no warning");
        Check(HotkeyConflicts.Warnings(list, default).Count == 0, "empty combo: no warning");

        var shadowed = HotkeyConflicts.Shadowed(list);
        Check(shadowed.Count == 1 && shadowed[0] == 2, "the later F1 never fires");

        // Keys AC uses.
        Check(HotkeyConflicts.CommonAcKey(new KeyCombo('W', HotkeyMods.None)) != null, "W");
        Check(HotkeyConflicts.CommonAcKey(new KeyCombo('D', HotkeyMods.Shift)) != null, "Shift+D (walk) still movement");
        Check(HotkeyConflicts.CommonAcKey(new KeyCombo('W', HotkeyMods.Ctrl)) == null, "Ctrl+W is free");
        Check(HotkeyConflicts.CommonAcKey(new KeyCombo(VK_UP, HotkeyMods.None)) != null, "arrow");
        Check(HotkeyConflicts.CommonAcKey(new KeyCombo(VK_SPACE, HotkeyMods.None)) != null, "Space");
        Check(HotkeyConflicts.CommonAcKey(new KeyCombo(VK_RETURN, HotkeyMods.None)) != null, "Enter");
        Check(HotkeyConflicts.CommonAcKey(new KeyCombo(0x09, HotkeyMods.None)) != null, "Tab");
        Check(HotkeyConflicts.CommonAcKey(new KeyCombo('3', HotkeyMods.None)) != null, "number row");
        Check(HotkeyConflicts.CommonAcKey(new KeyCombo('3', HotkeyMods.Alt)) == null, "Alt+3 free");
        Check(HotkeyConflicts.CommonAcKey(new KeyCombo('3', HotkeyMods.Shift)) == null, "Shift+3 not warned");
        Check(HotkeyConflicts.CommonAcKey(new KeyCombo(VK_NUMPAD3, HotkeyMods.None)) == null, "numpad not warned");
        Check(HotkeyConflicts.CommonAcKey(new KeyCombo(VK_F5, HotkeyMods.None)) == null, "F keys not warned");
        List<string> wWarn = HotkeyConflicts.Warnings(new List<HotkeyBinding>(), new KeyCombo('W', HotkeyMods.None));
        Check(wWarn.Count == 1 && wWarn[0].Contains("AC won't get that key"), "AC-key warning text");
    }

    // RynthILT's panels (Bounties, Guardian) are hotkey actions only where the bar's ILT button is:
    // Infinite Leaftide with RynthILT loaded (the engine sets IltAvailable from IltInfo).
    private static void IltPanelsOnlyOnIlt()
    {
        Func<bool> saved = HotkeyActions.IltAvailable;
        try
        {
            HotkeyAction? bounties = HotkeyActions.Find("panel:Bounties");
            HotkeyAction? guardian = HotkeyActions.Find("panel:Guardian");
            Check(bounties is { Ilt: true, Panel: "Bounties" } && guardian is { Ilt: true, Panel: "Guardian" }, "both ILT panels are built-ins, marked ILT");
            int iltCount = 0;
            foreach (HotkeyAction a in HotkeyActions.BuiltIns) if (a.Ilt) iltCount++;
            Check(iltCount == 2, "only those two are ILT");

            HotkeyActions.IltAvailable = static () => false;
            Check(!HotkeyActions.Offered(bounties!) && !HotkeyActions.Offered(guardian!), "off ILT: not offered");
            Check(HotkeyActions.Resolve("bounties") == null && HotkeyActions.Resolve("panel guardian") == null, "off ILT: can't be typed");
            Check(HotkeyActions.Offered(HotkeyActions.Find("panel:Radar")!), "other panels are offered anywhere");
            Check(HotkeyActions.Describe(new HotkeyBinding(new KeyCombo(VK_F5, 0), "panel:Bounties")).Contains("Bounties"),
                "off ILT a saved binding still describes itself");

            HotkeyActions.IltAvailable = static () => true;
            Check(HotkeyActions.Offered(bounties!) && HotkeyActions.Resolve("bounties") == bounties, "on ILT: offered, typed by title");
            Check(HotkeyActions.Resolve("panel guardian") == guardian, "on ILT: 'panel guardian'");

            HotkeyActions.IltAvailable = static () => throw new InvalidOperationException("no engine");
            Check(!HotkeyActions.Offered(bounties!), "a failing check counts as off ILT");
        }
        finally { HotkeyActions.IltAvailable = saved; }
    }

    private static void ActionsResolve()
    {
        Check(HotkeyActions.Resolve("combat")?.Id == "combat", "id");
        Check(HotkeyActions.Resolve("Toggle Combat")?.Id == "combat", "friendly name");
        Check(HotkeyActions.Resolve("panel:Dungeon Map")?.Panel == "Dungeon Map", "panel id");
        Check(HotkeyActions.Resolve("panel dungeonmap")?.Panel == "Dungeon Map", "panel loosely");
        Check(HotkeyActions.Resolve("inventory")?.Panel == "Inventory", "panel title alone");
        Check(HotkeyActions.Resolve("force-rebuff")?.Id == "forcerebuff", "dashes ignored");
        Check(HotkeyActions.Resolve("meta")?.Id == "meta", "'meta' is the Meta toggle, not the Meta panel");
        Check(HotkeyActions.Resolve("panel meta")?.Panel == "Meta", "'panel meta' is the panel");
        Check(HotkeyActions.Resolve("nonsense") == null && HotkeyActions.Resolve("") == null, "unknown");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HotkeyAction a in HotkeyActions.BuiltIns)
        {
            Check(ids.Add(a.Id), $"unique id {a.Id}");
            Check(HotkeyActions.Find(a.Id) == a, $"Find({a.Id})");
            Check(HotkeyActions.Resolve(a.Id) == (HotkeyActions.Offered(a) ? a : null), $"Resolve({a.Id})");
        }
        Check(HotkeyActions.Describe(new HotkeyBinding(new KeyCombo(VK_F1, 0), "command", "/ra start")) == "Chat: /ra start", "describe command");
        Check(HotkeyActions.Describe(new HotkeyBinding(new KeyCombo(VK_F1, 0), "gone")).Contains("Unknown"), "describe unknown");
    }

    private static void CommandParsing()
    {
        Check(HotkeyCommandParser.Parse("").Verb == HotkeyVerb.Help, "empty -> help");
        Check(HotkeyCommandParser.Parse("list").Verb == HotkeyVerb.List, "list");
        Check(HotkeyCommandParser.Parse("LIST").Verb == HotkeyVerb.List, "LIST (case)");
        Check(HotkeyCommandParser.Parse("actions").Verb == HotkeyVerb.Actions, "actions");
        Check(HotkeyCommandParser.Parse("status").Verb == HotkeyVerb.Status, "status");
        Check(HotkeyCommandParser.Parse("on").Verb == HotkeyVerb.On && HotkeyCommandParser.Parse("off").Verb == HotkeyVerb.Off, "on/off");

        HotkeyCommand a = HotkeyCommandParser.Parse("add Ctrl+F1 /ra start");
        Check(a.Verb == HotkeyVerb.Add && a.Combo == new KeyCombo(VK_F1, HotkeyMods.Ctrl) && a.Action == "command" && a.ChatCommand == "/ra start", "add chat command");
        HotkeyCommand b = HotkeyCommandParser.Parse("add F5 macro");
        Check(b.Verb == HotkeyVerb.Add && b.Action == "macro" && b.ChatCommand == "", "add built-in");
        HotkeyCommand c = HotkeyCommandParser.Parse("add Alt+Num3 panel Dungeon Map");
        Check(c.Verb == HotkeyVerb.Add && c.Action == "panel:Dungeon Map", "add panel with a space in its name");
        HotkeyCommand d = HotkeyCommandParser.Parse("add Shift+F2   /rn bc /ra stop  ");
        Check(d.Verb == HotkeyVerb.Add && d.ChatCommand == "/rn bc /ra stop", "command keeps inner spaces, trims ends");
        HotkeyCommand e = HotkeyCommandParser.Parse("bind Ctrl+Num+ toggle combat");
        Check(e.Verb == HotkeyVerb.Add && e.Combo == new KeyCombo(VK_ADD, HotkeyMods.Ctrl) && e.Action == "combat", "bind alias, Num+, friendly name");

        Check(HotkeyCommandParser.Parse("add").Verb == HotkeyVerb.Error, "add with nothing");
        Check(HotkeyCommandParser.Parse("add F5").Verb == HotkeyVerb.Error, "add without action");
        Check(HotkeyCommandParser.Parse("add Banana macro").Error.Contains("Unknown key"), "add bad key");
        Check(HotkeyCommandParser.Parse("add F5 dance").Error.Contains("Unknown action"), "add bad action");
        Check(HotkeyCommandParser.Parse("add Esc macro").Verb == HotkeyVerb.Error, "add Esc refused");

        HotkeyCommand r = HotkeyCommandParser.Parse("remove ctrl+f1");
        Check(r.Verb == HotkeyVerb.Remove && r.Combo == new KeyCombo(VK_F1, HotkeyMods.Ctrl), "remove");
        Check(HotkeyCommandParser.Parse("rm F5").Verb == HotkeyVerb.Remove && HotkeyCommandParser.Parse("del F5").Verb == HotkeyVerb.Remove, "rm/del aliases");
        Check(HotkeyCommandParser.Parse("remove").Verb == HotkeyVerb.Error, "remove without key");
        Check(HotkeyCommandParser.Parse("disable F5").Verb == HotkeyVerb.Disable && HotkeyCommandParser.Parse("enable F5").Verb == HotkeyVerb.Enable, "enable/disable");
        Check(HotkeyCommandParser.Parse("off F5").Verb == HotkeyVerb.Disable && HotkeyCommandParser.Parse("on F5").Verb == HotkeyVerb.Enable, "on/off <combo>");
        Check(HotkeyCommandParser.Parse("frobnicate").Verb == HotkeyVerb.Error, "unknown verb");
    }

    // VTank's hotkey list (2026-10-13): every action, by VTank's name, in its own dropdown group.
    private static void VTankActions()
    {
        // name -> the /ra command it runs (null: the same code as a RynthCore built-in).
        var expected = new (string Name, string? Chat)[]
        {
            ("Meta: set state...", "/ra setmetastate {0}"), ("Start/Stop", null), ("Target lock", "/ra targetlock"),
            ("Navigation", null), ("Looting", null), ("Combat", null), ("Buffing", null), ("Force buff", null),
            ("Cancel force buff", null), ("MeleeLow", "/ra meleelow"), ("MeleeMid", "/ra meleemid"), ("MeleeHigh", "/ra meleehigh"),
            ("NavAddPt", "/ra navaddpt"), ("TestItem", "/ra testitem"), ("PropertyDump", "/ra propertydump"),
            ("Recharge", "/ra recharge"), ("NavPrio", "/ra navprio"), ("LootPrio", "/ra lootprio"),
            ("NavLootPrio", "/ra navlootprio"), ("LootThisCont", "/ra lootthis"),
        };
        var vt = HotkeyActions.BuiltIns.Where(a => a.Group == HotkeyActions.VTankGroup).ToList();
        Check(vt.Count == expected.Length, $"{expected.Length} VTank-style actions ({vt.Count})");
        for (int i = 0; i < Math.Min(vt.Count, expected.Length); i++)
        {
            HotkeyAction a = vt[i];
            Check(a.Name == expected[i].Name, $"#{i} named '{expected[i].Name}' ({a.Name})");
            Check(a.Chat == expected[i].Chat, $"{a.Name}: runs {expected[i].Chat ?? "a built-in"} ({a.Chat ?? "built-in"})");
            Check(a.Id.StartsWith(HotkeyActions.VTankPrefix), $"{a.Name}: id {a.Id} has the vt: prefix");
            Check(a.Tip.Length > 10 && !a.Tip.Contains("VTank"), $"{a.Name}: a plain tooltip");
            Check(a.HasParam == (a.Id == HotkeyActions.MetaStateId), $"{a.Name}: only the meta state takes a parameter");
        }
        // The VTank group comes after every RynthCore built-in (one block in the dropdown).
        int firstVt = Array.FindIndex(HotkeyActions.BuiltIns, a => a.Group.Length > 0);
        Check(firstVt > 0 && HotkeyActions.BuiltIns.Skip(firstVt).All(a => a.Group == HotkeyActions.VTankGroup), "one VTank block at the end");

        // Resolve: ids, VTank names, ids without the prefix; the old built-ins keep winning their own words.
        Check(HotkeyActions.Resolve("vt:targetlock")?.Name == "Target lock", "vt: id");
        Check(HotkeyActions.Resolve("targetlock")?.Id == "vt:targetlock", "id without vt:");
        Check(HotkeyActions.Resolve("Target lock")?.Id == "vt:targetlock", "VTank name");
        Check(HotkeyActions.Resolve("startstop")?.Id == "vt:startstop", "startstop");
        Check(HotkeyActions.Resolve("Start/Stop")?.Id == "vt:startstop", "Start/Stop");
        Check(HotkeyActions.Resolve("LootThisCont")?.Id == "vt:lootthiscont", "LootThisCont");
        Check(HotkeyActions.Resolve("navlootprio")?.Id == "vt:navlootprio", "navlootprio");
        Check(HotkeyActions.Resolve("meleemid")?.Id == "vt:meleemid", "meleemid");
        Check(HotkeyActions.Resolve("combat")?.Id == "combat", "'combat' is still the RynthCore toggle");
        Check(HotkeyActions.Resolve("meta")?.Id == "meta", "'meta' is still the Meta toggle");
        Check(HotkeyActions.Resolve("forcerebuff")?.Id == "forcerebuff", "'forcerebuff' is still the RynthCore built-in");

        // Describe and the chat command each runs.
        var f5 = new KeyCombo(VK_F5, 0);
        Check(HotkeyActions.Describe(new HotkeyBinding(f5, "vt:recharge")) == "Recharge (VTank-style)", "describe VTank action");
        Check(HotkeyActions.Describe(new HotkeyBinding(f5, "macro")) == "Start/stop the macro", "RynthCore built-ins unchanged");
        HotkeyAction tl = HotkeyActions.Find("vt:targetlock")!;
        Check(HotkeyActions.ChatFor(tl, new HotkeyBinding(f5, tl.Id)) == "/ra targetlock", "Target lock -> /ra targetlock");
        Check(HotkeyActions.ChatFor(HotkeyActions.Find("vt:startstop")!, new HotkeyBinding(f5, "vt:startstop")) == "", "a built-in has no chat command");

        // /rc hotkey add with VTank actions.
        HotkeyCommand c = HotkeyCommandParser.Parse("add Ctrl+F5 Target lock");
        Check(c.Verb == HotkeyVerb.Add && c.Action == "vt:targetlock" && c.ChatCommand == "", "add by VTank name");
        c = HotkeyCommandParser.Parse("add Alt+1 lootthiscont");
        Check(c.Verb == HotkeyVerb.Add && c.Action == "vt:lootthiscont", "add by id without vt:");

        // A file with VTank actions round-trips; a stray chat command on one is dropped.
        var file = new HotkeyFile(true, new List<HotkeyBinding>
        {
            new(f5, "vt:navlootprio"),
            new(new KeyCombo(VK_F1, HotkeyMods.Alt), "vt:recharge", "junk"),
        });
        HotkeyFile back = HotkeyStore.Parse(HotkeyStore.Serialize(file));
        Check(back.Bindings.Count == 2 && back.Bindings[0] == file.Bindings[0], "VTank action round-trips");
        Check(back.Bindings.Count == 2 && back.Bindings[1].Command == "", "no text kept for an action without a parameter");
    }

    // "Meta: set state...": the state is the binding's parameter (its Command), kept in the file.
    // One-touch heal (2026-10-16): three built-ins that run RynthAi's /ra heal | stam | mana.
    private static void OneTouchHeal()
    {
        var want = new[] { ("heal", "/ra heal"), ("stamina", "/ra stam"), ("mana", "/ra mana") };
        foreach (var (id, chat) in want)
        {
            HotkeyAction? a = HotkeyActions.Find(id);
            Check(a != null && a.Chat == chat && a.Panel == null && a.Group.Length == 0 && !a.HasParam && a.Tip.Length > 20, $"{id}: a built-in that runs {chat}");
            Check(a != null && HotkeyActions.ChatFor(a, new HotkeyBinding(new KeyCombo(VK_F5, HotkeyMods.None), id)) == chat, $"{id}: the chat line");
        }
        Check(HotkeyActions.Resolve("heal")?.Id == "heal", "Resolve: heal");
        Check(HotkeyActions.Resolve("Heal now (one touch)")?.Id == "heal", "Resolve: by name");
        Check(HotkeyActions.Resolve("Restore mana now (one touch)")?.Id == "mana", "Resolve: mana by name");
        Check(HotkeyActions.Resolve("stamina")?.Id == "stamina", "Resolve: stamina");
        Check(HotkeyActions.Describe(new HotkeyBinding(new KeyCombo(VK_F5, HotkeyMods.None), "heal")) == "Heal now (one touch)", "Describe");
        HotkeyCommand c = HotkeyCommandParser.Parse("add F6 heal");
        Check(c.Verb == HotkeyVerb.Add && c.Action == "heal", "/rc hotkey add F6 heal");
        Check(HotkeyActions.BuiltIns.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == HotkeyActions.BuiltIns.Length, "every id is unique");
        Check(HotkeyActions.Resolve("combat")?.Id == "combat" && HotkeyActions.Resolve("macro")?.Id == "macro", "older words still win");
    }

    // The default keys are offered once per version: only where neither the action nor the key is taken.
    private static void DefaultKeys()
    {
        var f6 = new KeyCombo(0x75, HotkeyMods.None);
        var f7 = new KeyCombo(0x76, HotkeyMods.None);
        var f8 = new KeyCombo(0x77, HotkeyMods.None);
        Check(HotkeyDefaults.All.Length == 3 && HotkeyDefaults.All[0] == (f6, "heal") && HotkeyDefaults.All[1] == (f7, "stamina")
              && HotkeyDefaults.All[2] == (f8, "mana"), "F6 heal, F7 stamina, F8 mana");
        foreach (var (combo, action) in HotkeyDefaults.All)
            Check(HotkeyActions.Find(action) != null && HotkeyConflicts.CommonAcKey(combo) == null, $"{combo}: a real action, no AC key warning");

        // A new PC (no file): all three, saved with the version.
        HotkeyFile empty = new(true, new List<HotkeyBinding>());
        HotkeyFile seeded = HotkeyDefaults.Apply(empty);
        Check(!ReferenceEquals(seeded, empty) && seeded.DefaultsApplied == HotkeyDefaults.Version && seeded.Bindings.Count == 3, "empty file: three keys");
        Check(seeded.Bindings.All(b => b.Enabled && !b.IsCommand), "enabled built-ins");
        HotkeyFile back = HotkeyStore.Parse(HotkeyStore.Serialize(seeded));
        Check(back.DefaultsApplied == HotkeyDefaults.Version && back.Bindings.Count == 3 && back.Enabled, "the version and keys round-trip");
        Check(ReferenceEquals(HotkeyDefaults.Apply(back), back), "offered once: a second Apply changes nothing");

        // A key the player removed does not come back once the file has been offered them.
        HotkeyFile removed = back with { Bindings = back.Bindings.Where(b => b.Action != "heal").ToList() };
        Check(HotkeyDefaults.Apply(removed).Bindings.Count == 2, "a removed default stays removed");

        // Nothing of the player's is taken: a bound key, or an action already bound elsewhere.
        var mine = new List<HotkeyBinding>
        {
            new(f6, "macro"),                                              // F6 is taken: no heal key
            new(new KeyCombo(0x70, HotkeyMods.Ctrl), "mana"),              // mana is bound: no F8
        };
        HotkeyFile kept = HotkeyDefaults.Apply(new HotkeyFile(false, mine));
        Check(!kept.Enabled, "the master switch is not touched");
        Check(kept.Bindings.Count == 3 && kept.Bindings[0].Action == "macro" && kept.Bindings[1].Action == "mana", "the player's bindings stay, first");
        Check(kept.Bindings[2] == new HotkeyBinding(f7, "stamina"), "only the free one is added: " + string.Join(", ", kept.Bindings.Select(b => b.Combo + " " + b.Action)));
        Check(HotkeyDefaults.Apply(new HotkeyFile(true, new List<HotkeyBinding> { new(f7, "heal", Enabled: false) })).Bindings
            .Count(b => b.Action == "heal") == 1, "a switched-off heal key counts as bound");

        // An older file (no defaults= line) is offered them; an unreadable version counts as never.
        HotkeyFile old = HotkeyStore.Parse("enabled=1\non\tCtrl+F1\tmacro\t\n");
        Check(old.DefaultsApplied == 0 && HotkeyDefaults.Apply(old).Bindings.Count == 4, "an older file gets them next to its own");
        Check(HotkeyStore.Parse("defaults=banana\n").DefaultsApplied == 0, "junk version = never offered");
        Check(HotkeyStore.Parse("defaults=-3\n").DefaultsApplied == 0, "negative version = never offered");
    }

    private static void MetaStateParam()
    {
        var f6 = new KeyCombo(0x75, HotkeyMods.Ctrl);
        HotkeyAction meta = HotkeyActions.Find(HotkeyActions.MetaStateId)!;
        Check(meta.Param == "State", "the parameter is the state");
        var b = new HotkeyBinding(f6, meta.Id, "Hunting Grounds");
        Check(HotkeyActions.Describe(b) == "Meta: Hunting Grounds", $"describe ({HotkeyActions.Describe(b)})");
        Check(HotkeyActions.ChatFor(meta, b) == "/ra setmetastate Hunting Grounds", "chat command with the state");
        Check(HotkeyActions.Describe(new HotkeyBinding(f6, meta.Id)) == "Meta: (no state)", "no state yet");

        HotkeyFile back = HotkeyStore.Parse(HotkeyStore.Serialize(new HotkeyFile(true, new List<HotkeyBinding> { b })));
        Check(back.Bindings.Count == 1 && back.Bindings[0] == b, "the state survives the file");

        HotkeyCommand c = HotkeyCommandParser.Parse("add Ctrl+F6 metastate Hunting Grounds");
        Check(c.Verb == HotkeyVerb.Add && c.Action == meta.Id && c.ChatCommand == "Hunting Grounds", $"add metastate <state> ({c.Error})");
        c = HotkeyCommandParser.Parse("add Ctrl+F6 vt:metastate Default");
        Check(c.Verb == HotkeyVerb.Add && c.Action == meta.Id && c.ChatCommand == "Default", "add vt:metastate <state>");
        c = HotkeyCommandParser.Parse("add Ctrl+F6 Meta: set state... Go Home");
        Check(c.Verb == HotkeyVerb.Add && c.Action == meta.Id && c.ChatCommand == "Go Home", "add by its dropdown name");
        c = HotkeyCommandParser.Parse("add Ctrl+F6 metastate");
        Check(c.Verb == HotkeyVerb.Error && c.Error.Contains("needs a state"), $"no state: refused ({c.Error})");
        Check(HotkeyActions.ResolveWithParam("panel Dungeon Map") == null, "panels take no parameter");
        Check(HotkeyActions.ResolveWithParam("metastate") == null, "nothing after the action: no parameter");
    }
}

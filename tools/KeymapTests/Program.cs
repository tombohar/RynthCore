// Offline checks for AcKeymapText (see KeymapTests.csproj).
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Hotkeys;

namespace KeymapTests;

internal static class Program
{
    private static int _failed, _passed;

    private static void Check(bool ok, string what)
    {
        if (ok) { _passed++; return; }
        _failed++;
        Console.WriteLine("FAIL: " + what);
    }

    private static void Eq<T>(T actual, T expected, string what) =>
        Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{what}: got '{actual}', expected '{expected}'");

    // The shape AC writes (CRLF, two-space groups, four-space bindings), cut down.
    private static readonly string Sample = string.Join("\r\n", new[]
    {
        "#Asheron's Call: Throne of Destiny Keymap File",
        "\"User Defined Keymap\" [ 0000003B-02D0-0000-4002-000016000000 ]",
        "",
        "Devices",
        "[",
        "  Keyboard [ GUID_SysKeyboard ]",
        "  Mouse [ GUID_SysMouse ]",
        "]",
        "",
        "MetaKeys",
        "[",
        "  1 [ 0 DIK_LSHIFT ]",
        "  2 [ 0 DIK_LCONTROL ]",
        "  2 [ 0 DIK_RCONTROL ]",
        "  3 [ 0 DIK_LMENU ]",
        "  3 [ 0 DIK_RALT ]",
        "  5 [ 1 DIMOFS_BUTTON3 ]",
        "]",
        "",
        "Bindings",
        "[",
        "  MovementCommands",
        "  [",
        "    MovementForward [ \"\" [ 0 DIK_W ] ]",
        "    MovementForward [ \"\" [ 0 DIK_UPARROW ] ]",
        "  ]",
        "",
        "  UICommands",
        "  [",
        "    ToggleSpellbookPanel [ \"\" [ 0 DIK_F5 ] ]",
        "    ToggleOptionsPanel [ \"\" [ 0 DIK_F11 ] ]",
        "    TogglePluginManager [ \"\" [ 0 DIK_F1 ] 0x00000003 ]",
        "  ]",
        "",
        "  QuickslotCommands",
        "  [",
        "    UseQuickSlot_1 [ \"\" [ 0 DIK_1 ] ]",
        "    UseQuickSlot_5 [ \"\" [ 0 DIK_5 ] ]",
        "    UseQuickSlot_14 [ \"\" [ 0 DIK_5 ] 0x00000004 ]",
        "    CreateShortcut [ \"\" [ 0 DIK_0 ] ]",
        "    UseQuickSlot_1 [ \"\" [ 0 DIK_1 ] 0x00000002 ]",
        "  ]",
        "",
        "  ToggleChatEntry",
        "",
        "  MouseCommands",
        "  [",
        "    SelectLeft [ \"\" [ 1 DIMOFS_BUTTON0 ] ]",
        "  ]",
        "",
        "  EditControls",
        "  [",
        "    CursorStartOfLine [ \"\" [ 0 DIK_HOME ] ]",
        "    CursorPreviousPage [ \"\" [ 0 DIK_PGUP ] ]",
        "  ]",
        "",
        "  CopyAndPasteControls",
        "  [",
        "    CopyText [ \"\" [ 0 DIK_C ] 0x00000002 ]",
        "  ]",
        "]",
        "",
    });

    private static List<string> Lines(string text = "") => AcKeymapText.Split(text.Length > 0 ? text : Sample, out _);

    private static List<AcKeymapText.Line> Of(List<string> lines, string action) =>
        AcKeymapText.Bindings(lines).FindAll(l => l.Action == action);

    private static int Main()
    {
        Reading();
        Names();
        Meta();
        BindTakes();
        BindKeepsTextGroups();
        BindAlready();
        BindNewSlot();
        BindMissingGroup();
        BindEmptyGroup();
        BindUnknownAction();
        Unbind();
        RoundTrip();
        Describe();
        Take();
        Mods();
        ChangesRoundTrip();
        FollowKeepsOwnKeys();
        FollowTwiceIsNothing();
        FollowAllOrNothing();
        FollowOtherMetaNumbers();
        FollowRecordedGroup();
        OlderMarker();
        PendingAndReload();
        ChangesBadLines();
        RealFile();
        Console.WriteLine($"{_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Reading()
    {
        List<string> lines = Lines();
        List<AcKeymapText.Line> all = AcKeymapText.Bindings(lines);
        Eq(all.Count, 14, "binding lines read");
        List<AcKeymapText.Line> slot1 = Of(lines, "UseQuickSlot_1");
        Eq(slot1.Count, 2, "UseQuickSlot_1 lines");
        Check(slot1.TrueForAll(l => l.Group == "QuickslotCommands"), "UseQuickSlot_1 in QuickslotCommands");
        Eq(slot1[1].Meta, 2u, "Ctrl+1's mask");
        Eq(Of(lines, "MovementForward")[1].Dik, "DIK_UPARROW", "an alias read as its one name");
        Eq(Of(lines, "SelectLeft")[0].Device, 1, "the mouse is device 1");
        Eq(Of(lines, "CursorPreviousPage")[0].Group, "EditControls", "group after an empty group");
        Eq(AcKeymapText.Uses(lines, 0, "DIK_F5", 0).Count, 1, "F5 used once");
        Eq(AcKeymapText.Uses(lines, 0, "DIK_HOME", 0).Count, 0, "Home in a text group isn't a game use");
        Eq(AcKeymapText.Uses(lines, 0, "DIK_5", 4).Count, 1, "Alt+5 used");
    }

    private static void Names()
    {
        Eq(AcKeymapText.DikForVk('A'), "DIK_A", "A");
        Eq(AcKeymapText.DikForVk('7'), "DIK_7", "7");
        Eq(AcKeymapText.DikForVk(0x7A), "DIK_F11", "F11");
        Eq(AcKeymapText.DikForVk(0x7E), "DIK_F15", "F15");
        Eq(AcKeymapText.DikForVk(0x7F), (string?)null, "F16: AC has none");
        Eq(AcKeymapText.DikForVk(0x21), "DIK_PGUP", "PageUp as AC writes it");
        Eq(AcKeymapText.DikForVk(0x26), "DIK_UPARROW", "Up as AC writes it");
        Eq(AcKeymapText.DikForVk(0x65), "DIK_NUMPAD5", "Num5");
        Eq(AcKeymapText.DikForVk(0x6A), "DIK_NUMPADSTAR", "Num*");
        Eq(AcKeymapText.DikForVk(0xC0), "DIK_GRAVE", "`");
        Eq(AcKeymapText.Canonical("dik_prior"), "DIK_PGUP", "PRIOR is PGUP");
        Eq(AcKeymapText.Canonical("DIK_UP"), "DIK_UPARROW", "UP is UPARROW");
        Eq(AcKeymapText.Canonical("DIK_LEFTARROW"), "DIK_LEFT", "LEFTARROW is LEFT");
        Eq(AcKeymapText.Canonical("DIK_F3"), "DIK_F3", "a plain name");
    }

    private static void Meta()
    {
        List<string> lines = Lines();
        Check(AcKeymapText.TryMeta(lines, HotkeyMods.None, out uint m0, out _) && m0 == 0, "no modifiers: 0");
        Check(AcKeymapText.TryMeta(lines, HotkeyMods.Ctrl, out uint m1, out _) && m1 == 2, "Ctrl: 0x2");
        Check(AcKeymapText.TryMeta(lines, HotkeyMods.Alt, out uint m2, out _) && m2 == 4, "Alt: 0x4 (metakey 3)");
        Check(AcKeymapText.TryMeta(lines, HotkeyMods.Ctrl | HotkeyMods.Shift, out uint m3, out _) && m3 == 3, "Ctrl+Shift: 0x3");
        List<string> noAlt = Lines(Sample.Replace("  3 [ 0 DIK_LMENU ]\r\n", "").Replace("  3 [ 0 DIK_RALT ]\r\n", ""));
        Check(!AcKeymapText.TryMeta(noAlt, HotkeyMods.Alt, out _, out string err) && err.Contains("Alt"), "no Alt metakey: an error naming it");
    }

    private static void BindTakes()
    {
        List<string> lines = Lines();
        Check(AcKeymapText.Bind(lines, "UseQuickSlot_10", 0, "DIK_F11", 0, out var taken, out _), "bind slot 10 to F11");
        Eq(taken.Count, 1, "one action had F11");
        Eq(taken.Count > 0 ? taken[0].Action : "", "ToggleOptionsPanel", "taken from Options");
        Eq(Of(lines, "ToggleOptionsPanel").Count, 0, "Options lost F11");
        List<AcKeymapText.Line> s10 = Of(lines, "UseQuickSlot_10");
        Check(s10.Count == 1 && s10[0].Group == "QuickslotCommands" && s10[0].Dik == "DIK_F11" && s10[0].Meta == 0, "slot 10 has F11 in QuickslotCommands");
        Check(s10.Count == 1 && lines[s10[0].Index] == "    UseQuickSlot_10 [ \"\" [ 0 DIK_F11 ] ]", "written as AC writes it: " + (s10.Count == 1 ? lines[s10[0].Index] : ""));
        // Taking Ctrl+1 for slot 2: only the Ctrl one goes.
        Check(AcKeymapText.Bind(lines, "UseQuickSlot_2", 0, "DIK_1", 2, out taken, out _), "bind slot 2 to Ctrl+1");
        Check(taken.Count == 1 && taken[0].Action == "UseQuickSlot_1" && taken[0].Meta == 2, "Ctrl+1 taken from slot 1");
        List<AcKeymapText.Line> s1 = Of(lines, "UseQuickSlot_1");
        Check(s1.Count == 1 && s1[0].Meta == 0, "slot 1 keeps plain 1");
        List<AcKeymapText.Line> s2 = Of(lines, "UseQuickSlot_2");
        Check(s2.Count == 1 && lines[s2[0].Index].EndsWith("[ 0 DIK_1 ] 0x00000002 ]"), "slot 2 has Ctrl+1");
        // An alias in the file is the same key: PRIOR takes PGUP's other game use, not EditControls'.
        Check(AcKeymapText.Bind(lines, "UseQuickSlot_3", 0, "DIK_PRIOR", 0, out taken, out _) && taken.Count == 0, "PgUp: only a text group has it");
        Eq(Of(lines, "CursorPreviousPage").Count, 1, "the chat box keeps PgUp");
    }

    private static void BindKeepsTextGroups()
    {
        List<string> lines = Lines();
        Check(AcKeymapText.Bind(lines, "UseQuickSlot_4", 0, "DIK_C", 2, out var taken, out _), "bind slot 4 to Ctrl+C");
        Eq(taken.Count, 0, "Ctrl+C isn't taken from copy");
        Eq(Of(lines, "CopyText").Count, 1, "copy keeps Ctrl+C");
        Check(AcKeymapText.Bind(lines, "UseQuickSlot_6", 0, "DIK_HOME", 0, out _, out _), "bind slot 6 to Home");
        Eq(Of(lines, "CursorStartOfLine").Count, 1, "the chat box keeps Home");
    }

    private static void BindAlready()
    {
        List<string> lines = Lines();
        int before = lines.Count;
        Check(AcKeymapText.Bind(lines, "UseQuickSlot_5", 0, "DIK_5", 0, out var taken, out _) && taken.Count == 0, "slot 5 already has 5");
        Eq(lines.Count, before, "nothing added");
        Eq(Of(lines, "UseQuickSlot_5").Count, 1, "still one line");
    }

    private static void BindNewSlot()
    {
        List<string> lines = Lines();
        Check(AcKeymapText.Bind(lines, "UseQuickSlot_12", 0, "DIK_F12", 0, out _, out _), "slot 12, no line yet");
        List<AcKeymapText.Line> s = Of(lines, "UseQuickSlot_12");
        Check(s.Count == 1 && s[0].Group == "QuickslotCommands", "goes in QuickslotCommands");
        // At the end of the group, before its ']'.
        Check(s.Count == 1 && lines[s[0].Index + 1].Trim() == "]", "at the group's end");
        // A second key goes right after the slot's own line.
        Check(AcKeymapText.Bind(lines, "UseQuickSlot_12", 0, "DIK_F12", 1, out _, out _), "slot 12, Shift+F12 too");
        s = Of(lines, "UseQuickSlot_12");
        Check(s.Count == 2 && s[1].Index == s[0].Index + 1, "next to the first");
    }

    private static void BindMissingGroup()
    {
        string text = Sample.Replace("  QuickslotCommands\r\n  [\r\n", "  OldSlots\r\n  [\r\n");
        List<string> lines = Lines(text);
        // The slot's existing lines are in OldSlots: a new key goes beside them.
        Check(AcKeymapText.Bind(lines, "UseQuickSlot_1", 0, "DIK_F3", 0, out _, out _), "slot 1 beside its lines");
        Check(Of(lines, "UseQuickSlot_1").TrueForAll(l => l.Group == "OldSlots"), "stays in its group");
        // A slot with no line and no QuickslotCommands: the group is made at the end of Bindings.
        Check(AcKeymapText.Bind(lines, "UseQuickSlot_9", 0, "DIK_9", 0, out _, out _), "slot 9, no group");
        List<AcKeymapText.Line> s9 = Of(lines, "UseQuickSlot_9");
        Check(s9.Count == 1 && s9[0].Group == "QuickslotCommands", "QuickslotCommands made");
        Eq(AcKeymapText.Bindings(lines).Count, 16, "every line still read");
    }

    private static void BindEmptyGroup()
    {
        // ToggleChatEntry is a name with no [ ] block: a binding there gets one.
        string text = Sample.Replace("  QuickslotCommands", "  QuickslotCommandsX").Replace("  ToggleChatEntry", "  QuickslotCommands");
        List<string> lines = Lines(text);
        Check(AcKeymapText.Bind(lines, "UseQuickSlot_11", 0, "DIK_F2", 0, out _, out _), "bind into an empty group");
        List<AcKeymapText.Line> s = Of(lines, "UseQuickSlot_11");
        Check(s.Count == 1 && s[0].Group == "QuickslotCommands", "in the empty group, now with a block");
        Eq(Of(lines, "SelectLeft").Count, 1, "the next group still read");
    }

    private static void BindUnknownAction()
    {
        List<string> lines = Lines();
        int before = lines.Count;
        Check(!AcKeymapText.Bind(lines, "SomeNewAction", 0, "DIK_F7", 0, out _, out string err) && err.Length > 0, "no group for an unknown action");
        Eq(lines.Count, before, "nothing changed");
    }

    private static void Unbind()
    {
        List<string> lines = Lines();
        Check(AcKeymapText.Unbind(lines, "UseQuickSlot_1", 0, "DIK_1", 2), "Ctrl+1 off slot 1");
        List<AcKeymapText.Line> s1 = Of(lines, "UseQuickSlot_1");
        Check(s1.Count == 1 && s1[0].Meta == 0, "plain 1 stays");
        Check(!AcKeymapText.Unbind(lines, "UseQuickSlot_1", 0, "DIK_1", 2), "again: nothing to remove");
        Check(AcKeymapText.Unbind(lines, "MovementForward", 0, "DIK_UP", 0), "by an alias");
        Eq(Of(lines, "MovementForward").Count, 1, "W stays");
    }

    private static void RoundTrip()
    {
        List<string> lines = AcKeymapText.Split(Sample, out string nl);
        Eq(nl, "\r\n", "CRLF kept");
        Eq(AcKeymapText.Join(lines, nl), Sample, "split and join give the same text");
        List<string> lf = AcKeymapText.Split(Sample.Replace("\r\n", "\n"), out string nl2);
        Eq(nl2, "\n", "LF kept");
        Eq(lf.Count, lines.Count, "same lines either way");
        Eq(AcKeymapText.Format("UseQuickSlot_1", 0, "DIK_1", 2), "UseQuickSlot_1 [ \"\" [ 0 DIK_1 ] 0x00000002 ]", "a line with a mask");
        Eq(AcKeymapText.Format("UseQuickSlot_1", 0, "DIK_1", 0), "UseQuickSlot_1 [ \"\" [ 0 DIK_1 ] ]", "a line without");
    }

    private static void Describe()
    {
        Eq(AcKeymapText.Describe("UseQuickSlot_5"), "shortcut slot 5", "a slot");
        Eq(AcKeymapText.Describe("ToggleSpellbookPanel"), "Toggle Spellbook Panel", "camel case");
        Eq(AcKeymapText.Describe("AltF4"), "Alt F4", "a key name");
        Eq(AcKeymapText.Describe("LOGOUT"), "LOGOUT", "all capitals");
    }

    private static void Take()
    {
        List<string> lines = Lines();
        List<AcKeymapText.Line> t = AcKeymapText.Take(lines, 0, "DIK_F5", 0);
        Check(t.Count == 1 && t[0].Action == "ToggleSpellbookPanel", "F5 taken off the spellbook");
        Eq(AcKeymapText.Uses(lines, 0, "DIK_F5", 0).Count, 0, "nothing has F5");
        Eq(AcKeymapText.Take(lines, 0, "DIK_HOME", 0).Count, 0, "Home: only a text group has it");
        Eq(Of(lines, "CursorStartOfLine").Count, 1, "the chat box keeps Home");
        Eq(AcKeymapText.GroupOf(lines, "UseQuickSlot_1", 0, "DIK_1", 2), "QuickslotCommands", "Ctrl+1's group");
        Eq(AcKeymapText.GroupOf(lines, "UseQuickSlot_1", 0, "DIK_2", 0), "", "a key the action hasn't");
    }

    private static void Mods()
    {
        List<string> lines = Lines();
        Eq(AcKeymapText.ModsOf(lines, 0), "", "no modifiers");
        Eq(AcKeymapText.ModsOf(lines, 2), "DIK_LCONTROL|DIK_RCONTROL", "Ctrl by its names");
        Eq(AcKeymapText.ModsOf(lines, 3), "DIK_LSHIFT+DIK_LCONTROL|DIK_RCONTROL", "Shift+Ctrl");
        Eq(AcKeymapText.ModsOf(lines, 4), "DIK_LMENU|DIK_RMENU", "Alt (DIK_RALT read as DIK_RMENU)");
        Eq(AcKeymapText.ModsOf(lines, 0x10), "DIMOFS_BUTTON3", "a mouse button as a metakey");
        Eq(AcKeymapText.ModsOf(lines, 0x8), "0x00000008", "a bit no metakey names: the raw mask");
        foreach (uint m in new uint[] { 0, 2, 3, 4, 0x10, 0x8, 0x16 })
            Check(AcKeymapText.TryMaskOf(lines, AcKeymapText.ModsOf(lines, m), out uint back, out _) && back == m, $"mask 0x{m:X} back from its names");
        List<string> noAlt = Lines(Sample.Replace("  3 [ 0 DIK_LMENU ]\r\n", "").Replace("  3 [ 0 DIK_RALT ]\r\n", ""));
        Check(!AcKeymapText.TryMaskOf(noAlt, "DIK_LMENU|DIK_RMENU", out _, out string err) && err.Contains("DIK_LMENU"), "a keymap without Alt: can't");
    }

    // What AcKeymapLive's changer records for a bind it made (the same calls), as one change.
    private static AcKeymapText.Edit BindEdit(List<string> lines, string action, string dik, uint meta)
    {
        Check(AcKeymapText.Bind(lines, action, 0, dik, meta, out _, out string err), $"changer: bind {action} {dik} ({err})");
        return new AcKeymapText.Edit(AcKeymapText.EditKind.Bind, action, 0, AcKeymapText.Canonical(dik), AcKeymapText.ModsOf(lines, meta),
            AcKeymapText.GroupOf(lines, action, 0, dik, meta));
    }

    private static AcKeymapText.Change One(long stamp, int pid, params AcKeymapText.Edit[] edits) => new(stamp, pid, edits.ToList());

    private static List<AcKeymapText.Change> RoundTrip(List<AcKeymapText.Change> log)
    {
        List<AcKeymapText.Change> back = AcKeymapText.ParseChanges(AcKeymapText.FormatChanges(log), out bool older);
        Check(!older, "a log isn't an older marker");
        return back;
    }

    private static void ChangesRoundTrip()
    {
        var log = new List<AcKeymapText.Change>
        {
            One(1000, 11, new AcKeymapText.Edit(AcKeymapText.EditKind.Bind, "UseQuickSlot_10", 0, "DIK_F5", "", "QuickslotCommands")),
            One(2000, 12, new AcKeymapText.Edit(AcKeymapText.EditKind.Unbind, "UseQuickSlot_1", 0, "DIK_1", "DIK_LCONTROL|DIK_RCONTROL"),
                          new AcKeymapText.Edit(AcKeymapText.EditKind.Take, "", 1, "DIMOFS_BUTTON3", "0x00000008")),
        };
        string text = AcKeymapText.FormatChanges(log);
        Check(text.StartsWith(AcKeymapText.ChangesHeader + "\r\n"), "the log starts with its header");
        Check(text.Contains("\r\n2000 12 take - 1 DIMOFS_BUTTON3 0x00000008 -\r\n"), "a take line: " + text);
        List<AcKeymapText.Change> back = RoundTrip(log);
        Eq(back.Count, 2, "two changes back");
        Check(back.Count == 2 && back[0].Stamp == 1000 && back[0].Pid == 11 && back[0].Edits.SequenceEqual(log[0].Edits), "the first, edit for edit");
        Check(back.Count == 2 && back[1].Edits.SequenceEqual(log[1].Edits), "the second (two edits, in order)");
        // Newest, the next stamp (past another clock's), keeping the newest few.
        Eq(AcKeymapText.Newest(back), 2000L, "newest stamp");
        Eq(AcKeymapText.NextStamp(back, 5000), 5000L, "now when it is later");
        Eq(AcKeymapText.NextStamp(back, 1500), 2001L, "past the newest when now is earlier");
        Eq(AcKeymapText.NextStamp(new List<AcKeymapText.Change>(), 7), 7L, "an empty log: now");
        var kept = new List<AcKeymapText.Change>();
        for (int i = 1; i <= 70; i++) kept = AcKeymapText.Append(kept, One(i, 1, new AcKeymapText.Edit(AcKeymapText.EditKind.Take, "", 0, "DIK_F1", "")), 64);
        Check(kept.Count == 64 && kept[0].Stamp == 7 && kept[^1].Stamp == 70, "the newest 64 kept");
        Eq(RoundTrip(kept).Count, 64, "a full log reads back");
        // Out of order in the file: read by stamp.
        string swapped = AcKeymapText.ChangesHeader + "\r\n"
                         + "300 5 take - 0 DIK_F2 - -\r\n"
                         + "100 6 take - 0 DIK_F3 - -\r\n";
        List<AcKeymapText.Change> s = AcKeymapText.ParseChanges(swapped, out _);
        Check(s.Count == 2 && s[0].Stamp == 100 && s[1].Stamp == 300, "by stamp");
    }

    // The player's report: keys changed in Configure Keyboard in one client (unsaved, in memory) came back when
    // another client changed a key, because every other client loaded the whole file. Now each makes the edits to
    // its own keys (AC writes them out: here, its own text).
    private static string ClientB() => Sample
        .Replace("    ToggleSpellbookPanel [ \"\" [ 0 DIK_F5 ] ]", "    ToggleSpellbookPanel [ \"\" [ 0 DIK_F6 ] ]\r\n    ToggleMapPanel [ \"\" [ 0 DIK_F5 ] ]")
        .Replace("    ToggleOptionsPanel [ \"\" [ 0 DIK_F11 ] ]", "    ToggleOptionsPanel [ \"\" [ 0 DIK_F12 ] ]")
        .Replace("    UseQuickSlot_5 [ \"\" [ 0 DIK_5 ] ]\r\n", "");

    private static List<AcKeymapText.Change> ChangesByA()
    {
        List<string> a = Lines();   // client A, the changer: its keys (saved first), then its edits
        var log = new List<AcKeymapText.Change>();
        log = AcKeymapText.Append(log, One(100, 1, BindEdit(a, "UseQuickSlot_10", "DIK_F5", 0)), 64);
        log = AcKeymapText.Append(log, One(200, 1, BindEdit(a, "UseQuickSlot_2", "DIK_1", 2)), 64);
        Check(AcKeymapText.Unbind(a, "MovementForward", 0, "DIK_UPARROW", 0), "changer: Up off forward");
        log = AcKeymapText.Append(log, One(300, 1, new AcKeymapText.Edit(AcKeymapText.EditKind.Unbind, "MovementForward", 0, "DIK_UPARROW", "")), 64);
        // A command's key ("/rdef use 20" on Alt+5): off every game action.
        AcKeymapText.Take(a, 0, "DIK_5", 4);
        log = AcKeymapText.Append(log, One(400, 1, new AcKeymapText.Edit(AcKeymapText.EditKind.Take, "", 0, "DIK_5", AcKeymapText.ModsOf(a, 4))), 64);
        return RoundTrip(log);   // as the follower reads it from "<keymap>.rynthcore"
    }

    private static void FollowKeepsOwnKeys()
    {
        List<AcKeymapText.Change> log = ChangesByA();
        Eq(log.Count, 4, "four changes recorded");
        List<string> b = Lines(ClientB());
        List<AcKeymapText.Change> pending = AcKeymapText.Pending(log, 50, 2);
        Eq(pending.Count, 4, "client B has all four to make");
        Eq(AcKeymapText.ApplyChanges(b, pending, out string err), 1, "client B makes them (" + err + ")");
        // The change landed.
        List<AcKeymapText.Line> s10 = Of(b, "UseQuickSlot_10");
        Check(s10.Count == 1 && s10[0].Dik == "DIK_F5" && s10[0].Meta == 0 && s10[0].Group == "QuickslotCommands", "B: slot 10 has F5");
        List<AcKeymapText.Line> s2 = Of(b, "UseQuickSlot_2");
        Check(s2.Count == 1 && s2[0].Dik == "DIK_1" && s2[0].Meta == 2, "B: slot 2 has Ctrl+1");
        // A key that conflicted in B's own keys came off.
        Eq(Of(b, "ToggleMapPanel").Count, 0, "B: its map panel lost F5 to slot 10");
        List<AcKeymapText.Line> s1 = Of(b, "UseQuickSlot_1");
        Check(s1.Count == 1 && s1[0].Meta == 0, "B: slot 1 lost Ctrl+1, keeps 1");
        Eq(Of(b, "UseQuickSlot_14").Count, 0, "B: Alt+5 taken for the command");
        List<AcKeymapText.Line> fwd = Of(b, "MovementForward");
        Check(fwd.Count == 1 && fwd[0].Dik == "DIK_W", "B: forward lost Up, keeps W");
        // B's own keys, different from A's and never in the file, stayed.
        List<AcKeymapText.Line> book = Of(b, "ToggleSpellbookPanel");
        Check(book.Count == 1 && book[0].Dik == "DIK_F6", "B: its spellbook stays on F6 (A's was F5)");
        List<AcKeymapText.Line> opts = Of(b, "ToggleOptionsPanel");
        Check(opts.Count == 1 && opts[0].Dik == "DIK_F12", "B: its options stay on F12 (A's was F11)");
        Eq(Of(b, "UseQuickSlot_5").Count, 0, "B: slot 5's key it deleted stays deleted");
        Eq(Of(b, "CopyText").Count, 1, "B: copy keeps Ctrl+C");
    }

    private static void FollowTwiceIsNothing()
    {
        List<AcKeymapText.Change> log = ChangesByA();
        List<string> b = Lines(ClientB());
        Eq(AcKeymapText.ApplyChanges(b, log, out _), 1, "made once");
        var after = new List<string>(b);
        Eq(AcKeymapText.ApplyChanges(b, log, out string err), 0, "made again: nothing to do (" + err + ")");
        Check(after.SequenceEqual(b), "the text is the same");
        // The changer itself (its keys hold every edit): nothing either.
        List<string> a = Lines();
        AcKeymapText.ApplyChanges(a, log, out _);
        var a1 = new List<string>(a);
        Eq(AcKeymapText.ApplyChanges(a, log, out _), 0, "the changer's own keys: nothing to do");
        Check(a1.SequenceEqual(a), "the changer's text is the same");
    }

    private static void FollowAllOrNothing()
    {
        // The second edit can't be made (an action with no group): the first isn't made either.
        List<string> b = Lines(ClientB());
        var before = new List<string>(b);
        var log = new List<AcKeymapText.Change>
        {
            One(100, 1, new AcKeymapText.Edit(AcKeymapText.EditKind.Bind, "UseQuickSlot_10", 0, "DIK_F5", "")),
            One(200, 1, new AcKeymapText.Edit(AcKeymapText.EditKind.Bind, "SomeNewAction", 0, "DIK_F7", "")),
        };
        Check(AcKeymapText.ApplyChanges(b, log, out string err) == -1 && err.Contains("SomeNewAction"), "can't: the error names it (" + err + ")");
        Check(before.SequenceEqual(b), "the keys as they were (no part made)");
        // A modifier this keymap hasn't: can't, keys as they were.
        var alt = new List<AcKeymapText.Change> { One(100, 1, new AcKeymapText.Edit(AcKeymapText.EditKind.Bind, "UseQuickSlot_3", 0, "DIK_3", "DIK_LWIN")) };
        Check(AcKeymapText.ApplyChanges(b, alt, out _) == -1 && before.SequenceEqual(b), "an unknown modifier: can't, nothing made");
    }

    private static void FollowOtherMetaNumbers()
    {
        // B's keymap numbers Ctrl 3 and Alt 2: A's Ctrl+1 is still Ctrl+1 there (mask 0x4, not 0x2).
        string swappedMeta = ClientB()
            .Replace("  2 [ 0 DIK_LCONTROL ]\r\n  2 [ 0 DIK_RCONTROL ]\r\n  3 [ 0 DIK_LMENU ]\r\n  3 [ 0 DIK_RALT ]",
                     "  2 [ 0 DIK_LMENU ]\r\n  2 [ 0 DIK_RALT ]\r\n  3 [ 0 DIK_LCONTROL ]\r\n  3 [ 0 DIK_RCONTROL ]")
            .Replace("[ 0 DIK_1 ] 0x00000002 ]", "[ 0 DIK_1 ] 0x00000004 ]")
            .Replace("[ 0 DIK_C ] 0x00000002 ]", "[ 0 DIK_C ] 0x00000004 ]")
            .Replace("[ 0 DIK_5 ] 0x00000004 ]", "[ 0 DIK_5 ] 0x00000002 ]");
        List<string> b = Lines(swappedMeta);
        Check(AcKeymapText.TryMeta(b, HotkeyMods.Ctrl, out uint ctrl, out _) && ctrl == 4, "B's Ctrl is 0x4");
        Eq(AcKeymapText.ApplyChanges(b, ChangesByA(), out string err), 1, "B makes A's changes (" + err + ")");
        List<AcKeymapText.Line> s2 = Of(b, "UseQuickSlot_2");
        Check(s2.Count == 1 && s2[0].Meta == 4, "B: slot 2 has Ctrl+1 by B's numbers");
        List<AcKeymapText.Line> s1 = Of(b, "UseQuickSlot_1");
        Check(s1.Count == 1 && s1[0].Meta == 0, "B: slot 1 lost Ctrl+1 (its 0x4)");
        Eq(Of(b, "UseQuickSlot_14").Count, 0, "B: Alt+5 (its 0x2) taken");
    }

    private static void FollowRecordedGroup()
    {
        // B has no line for the action and it has no default group: the changer's group is where it goes.
        List<string> b = Lines(ClientB());
        var log = new List<AcKeymapText.Change>
            { One(100, 1, new AcKeymapText.Edit(AcKeymapText.EditKind.Bind, "ToggleInventoryPanel", 0, "DIK_I", "", "UICommands")) };
        Eq(AcKeymapText.ApplyChanges(b, RoundTrip(log), out string err), 1, "bound in the recorded group (" + err + ")");
        List<AcKeymapText.Line> inv = Of(b, "ToggleInventoryPanel");
        Check(inv.Count == 1 && inv[0].Group == "UICommands", "in UICommands");
        // A recorded text group is never used for a game key.
        List<string> c = Lines(ClientB());
        var bad = new List<AcKeymapText.Change>
            { One(100, 1, new AcKeymapText.Edit(AcKeymapText.EditKind.Bind, "ToggleInventoryPanel", 0, "DIK_I", "", "EditControls")) };
        Eq(AcKeymapText.ApplyChanges(c, bad, out _), -1, "a text group recorded: no group, can't");
    }

    private static void OlderMarker()
    {
        // What an older RynthCore wrote: the keymap's time alone. Known as older, nothing to make.
        List<AcKeymapText.Change> l = AcKeymapText.ParseChanges("638652816000000000", out bool older);
        Check(older && l.Count == 0, "an older marker: older, no changes");
        l = AcKeymapText.ParseChanges("638652816000000000\r\n", out older);
        Check(older && l.Count == 0, "with a line end too");
        l = AcKeymapText.ParseChanges("", out older);
        Check(!older && l.Count == 0, "empty: nothing, not older");
        l = AcKeymapText.ParseChanges("something else\r\n1 2 take - 0 DIK_F1 - -", out older);
        Check(!older && l.Count == 0, "not a log: nothing");
        l = AcKeymapText.ParseChanges("RynthCore keymap changes 1\r\n1 2 take - 0 DIK_F1 - -", out older);
        Check(!older && l.Count == 0, "a version before 2: nothing");
        l = AcKeymapText.ParseChanges("RynthCore keymap changes 3\r\n1 2 take - 0 DIK_F1 - -", out older);
        Check(!older && l.Count == 1, "a later version: the lines it can read");
    }

    private static void PendingAndReload()
    {
        List<AcKeymapText.Change> log = ChangesByA();
        // The changer (pid 1) never makes its own again; another client what's past what it made.
        Eq(AcKeymapText.Pending(log, 0, 1).Count, 0, "the changer's own: none");
        Eq(AcKeymapText.Pending(log, 200, 2).Count, 2, "past 200: two");
        // After a hot reload the engine starts at what the last one made (its newest): nothing again.
        Eq(AcKeymapText.Pending(log, AcKeymapText.Newest(log), 2).Count, 0, "after a reload: nothing made again");
        // A client started after them: none (AC loaded the file with them); one started before: all.
        Eq(AcKeymapText.Pending(log, 350, 3).Count, 1, "started at 350: the one after");
        // An empty change is never pending.
        var empty = new List<AcKeymapText.Change> { new(500, 4, new List<AcKeymapText.Edit>()) };
        Eq(AcKeymapText.Pending(empty, 0, 2).Count, 0, "an empty change: nothing");
    }

    private static void ChangesBadLines()
    {
        // A change with a line this version can't read is left out whole; the others stay.
        string text = AcKeymapText.ChangesHeader + "\r\n"
                      + "100 1 bind UseQuickSlot_1 0 DIK_F1 - QuickslotCommands\r\n"
                      + "200 1 bind UseQuickSlot_2 0 DIK_F2 - -\r\n"
                      + "200 1 rebind UseQuickSlot_2 0 DIK_F3 - -\r\n"
                      + "300 1 take - 0 DIK_F4 - -\r\n"
                      + "300 1 take - 0 DIK_F4\r\n"
                      + "400 1 unbind UseQuickSlot_4 0 DIK_F4 DIK_LCONTROL|DIK_RCONTROL -\r\n"
                      + "garbage\r\n"
                      + "500 1 bind Bad-Name 0 DIK_F5 - -\r\n"
                      + "# a note\r\n";
        List<AcKeymapText.Change> l = AcKeymapText.ParseChanges(text, out _);
        Check(l.Count == 2 && l[0].Stamp == 100 && l[1].Stamp == 400, "only the whole, readable changes: " + string.Join(",", l.Select(c => c.Stamp)));
        Check(l.Count == 2 && l[1].Edits[0].Mods == "DIK_LCONTROL|DIK_RCONTROL", "the modifiers kept");
    }

    private static void RealFile()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Asheron's Call", "Normal.keymap");
        if (!File.Exists(path)) { Console.WriteLine("(no Normal.keymap here: the real-file case is skipped)"); return; }
        string text = File.ReadAllText(path);
        List<string> lines = AcKeymapText.Split(text, out string nl);
        Eq(AcKeymapText.Join(lines, nl), text, "the real file round trips");
        int count = AcKeymapText.Bindings(lines).Count;
        Check(count > 50, $"the real file's bindings read ({count})");
        Check(Of(lines, "UseQuickSlot_1").Count >= 1, "the real file has slot 1");
        Check(AcKeymapText.TryMeta(lines, HotkeyMods.Ctrl, out uint ctrl, out _) && ctrl != 0, "the real file has Ctrl");
        // In memory only: slot 10 to F11 takes it from whatever has it, and every other line is still read.
        int had = AcKeymapText.Uses(lines, 0, "DIK_F11", 0).Count;
        Check(AcKeymapText.Bind(lines, "UseQuickSlot_10", 0, "DIK_F11", 0, out var taken, out _), "real file: bind slot 10 to F11");
        Eq(taken.Count, had, "real file: F11 taken from all its game uses");
        Eq(AcKeymapText.Bindings(lines).Count, count - had + 1, "real file: one line in, the taken ones out");
        Eq(AcKeymapText.Uses(lines, 0, "DIK_F11", 0).Count, 1, "real file: F11 does one thing now");
    }
}

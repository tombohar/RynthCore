using System.Text.RegularExpressions;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Hotkeys;

namespace ChatCommandReplyTests;

/// <summary>
/// How the engine's chat commands answer (ChatCommandReplies.cs) without AC: every /rc verb with a
/// valid and an invalid form, where each reply goes and its colour, the reply texts, and a read of
/// RynthCoreChatCommands.cs so the verb list can't drift from TryHandle. The dispatcher needs the
/// live engine (panels, settings, AC), so the decision before a command runs is what is tested.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        WhereRepliesGo();
        EveryVerbValidAndInvalid();
        HandlerVerbsPassThrough();
        UnknownAndBare();
        ModuleLinesClassified();
        ParsersShared();
        ReplyTexts();
        SourceMatchesCatalog();
        ChatBurst();
        ControlsRunTheirCommands();

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        Console.WriteLine(_failed == 0 ? "ALL CHAT COMMAND REPLY TESTS PASSED." : $"{_failed} FAILED.");
        return _failed == 0 ? 0 : 1;
    }

    // ── where a reply goes ──────────────────────────────────────────────────

    private static void WhereRepliesGo()
    {
        Case("where a reply goes, and its colour");
        Check(ChatCommandReplies.ToChat(ChatReplyKind.Ok, ChatCommandOrigin.Player), "the player's success: chat");
        Check(ChatCommandReplies.ToChat(ChatReplyKind.Fail, ChatCommandOrigin.Player), "the player's failure: chat");
        Check(!ChatCommandReplies.ToChat(ChatReplyKind.Ok, ChatCommandOrigin.Script), "a script's success: the log only");
        Check(ChatCommandReplies.ToChat(ChatReplyKind.Fail, ChatCommandOrigin.Script), "a script's failure: chat");
        Check(ChatCommandReplies.Effective(ChatCommandOrigin.Script, insidePlayerLine: true) == ChatCommandOrigin.Player,
            "a plugin's line while it handles one the player typed is the player's");
        Check(ChatCommandReplies.Effective(ChatCommandOrigin.Script, insidePlayerLine: false) == ChatCommandOrigin.Script, "a plugin's own line is a script's");
        Check(ChatCommandReplies.Effective(ChatCommandOrigin.Player, insidePlayerLine: false) == ChatCommandOrigin.Player, "typed stays the player's");
        Check(default(ChatCommandOrigin) == ChatCommandOrigin.Script, "the default (an unset [ThreadStatic]) is a script's");
        Check(ChatCommandReplies.ChatType(ChatReplyKind.Ok) == 1, "success: chat type 1 (green), as every reply before");
        Check(ChatCommandReplies.ChatType(ChatReplyKind.Fail) == 0x1A, "failure: 0x1A, AC's own command-notice red");
        Check(ChatCommandReplies.ReplyPrefix == "[RynthCore] ", "the prefix is unchanged");
    }

    // ── every verb: a valid and an invalid form ────────────────────────────

    /// <summary>Verbs whose arguments are checked before they run: (form that runs, form answered with the usage).</summary>
    private static readonly (string Verb, string Valid, string Invalid)[] Checked =
    {
        ("imgui", "imgui on", "imgui frob"),
        ("imgui", "imgui", "imgui on off"),
        ("chathold", "chathold status", "chathold frob"),
        ("ilt", "ilt auto", "ilt frob"),
        ("worldlayer", "worldlayer uipass", "worldlayer frob"),
        ("acmouse", "acmouse watch 30", "acmouse frob"),
        ("acmouse", "mouseover", "mouseover frob"),
        ("vtank", "vtank clear", "vtank frob"),
        ("bounties", "bounties show", "bounties frob"),
        ("bounties", "bounty", "bountys frob"),
        ("guardian", "guardian hide", "guardians frob"),
        ("dashboard", "dashboard tab loot", "dashboard tab frob"),
        ("dashboard", "dashboard classic", "dashboard frob"),
        ("dashboard", "dashboard", "dashboard modern now"),
        ("miniremote", "miniremote section map", "miniremote frob"),
        ("miniremote", "remote toggle", "remote frob"),
        ("miniremote", "miniremote tab pets", "miniremote shows"),
        ("oracle-skills", "oracle-skills attrs", "oracle-skills frob"),
        ("oracle-skills", "oracle-skills off", "oracle-skills"),
        ("skills", "skills toggle", "skills frob"),
        ("progression", "progression", "progression now"),
        ("progression", "progress", "progress now"),
        ("spells", "spells show", "spells frob"),
        ("diagnostics", "diagnostics", "diagnostics now"),
        ("diagnostics", "serverpick", "serverpick now"),
        ("shortcuts", "shortcuts use 5", "shortcuts use five"),
        ("shortcuts", "shortcuts", "shortcuts frob"),
        ("shortcuts", "shortcuts set 3 0x50000001", "shortcuts set"),
        ("shortcuts", "shortcuts clear 18", "shortcuts clear"),
        ("keymap", "keymap bind UseQuickSlot_10 F11", "keymap bind F11"),
        ("keymap", "keymap check /ra start Ctrl+F1", "keymap frob"),
        ("keymap", "keymap dump", "keymap unbind"),
        ("keymap", "keymap", "keymap check x"),
        ("panels", "panels open 0x1F", "panels open"),
        ("panels", "panels toggle 12", "panels open twelve"),
        ("panels", "panels", "panels open 1 2"),
        ("rl", "rl", "rl now"),
        ("rl", "reload", "reload now"),
        ("inventory", "inv", "inv now"),
        ("inventory", "inventory", "inventory now"),
        ("sense", "sense", "sense now"),
        ("sense", "sense compact on", "sense compact maybe"),
        ("sense", "sense collapse off", "sense collapse on off"),
        ("sense", "sense view collapsed", "sense view huge"),
        ("sense", "sense compact", "sense frob on"),
        ("characters", "chars", "characters now"),
        ("itemcount", "itemhud", "itemcount now"),
        ("itemtool", "itemtool", "itemtool now"),
        ("resetbar", "barreset", "resetbar now"),
        ("actionstate", "actions", "actionstate now"),
        ("unlockactions", "unlock", "unlockactions now"),
        ("vitals", "hud", "vitals now"),
        ("layout", "layout", "layout frob"),
        ("layout", "layout reset confirm", "layout reset now"),
        ("layout", "layout default", "layout default now"),
    };

    private static void EveryVerbValidAndInvalid()
    {
        Case("every checked verb: the valid form runs, the invalid one gets the usage");
        foreach (var (verb, valid, invalid) in Checked)
        {
            ChatVerbCheck ok = ChatCommandReplies.Check(valid);
            Check(ok.Problem == null && ok.Verb?.Name == verb, $"'/rc {valid}' runs as {verb} (got {ok.Verb?.Name ?? "unknown"}: {ok.Problem})");
            ChatVerbCheck bad = ChatCommandReplies.Check(invalid);
            Check(bad.Verb?.Name == verb && bad.Problem != null && bad.Problem == bad.Verb.Usage,
                $"'/rc {invalid}' answers with {verb}'s usage (got {bad.Problem ?? "no reply"})");
            Check(bad.Problem != null && bad.Problem.StartsWith("Usage: /rc ", StringComparison.Ordinal),
                $"'/rc {invalid}': the reply is a usage line");
            Check(ChatCommandReplies.KindOfLine(bad.Problem ?? "") == ChatReplyKind.Fail, $"'/rc {invalid}': a failure (red, and shown to a script too)");
        }

        Case("every verb in the list has a valid and an invalid form here");
        var covered = new HashSet<string>(Checked.Select(c => c.Verb));
        foreach (var h in Handled) covered.Add(h.Verb);
        foreach (ChatVerb v in ChatCommandReplies.Verbs)
        {
            Check(covered.Contains(v.Name), $"{v.Name} is tested");
            Check(v.Usage.StartsWith("Usage: /r", StringComparison.Ordinal) && v.Usage.Length > 12, $"{v.Name} has a usage line");
            Check(v.Usage.Contains(v.Name, StringComparison.OrdinalIgnoreCase), $"{v.Name}'s usage names it");
        }
    }

    /// <summary>
    /// Verbs whose own handler checks the arguments (Args null): the check before they run lets the
    /// invalid form through, and the handler or module answers with its usage (Who), in red because
    /// the line starts "Usage" (ModuleLinesClassified) or is the parser's error (hotkey).
    /// </summary>
    private static readonly (string Verb, string Valid, string Invalid, string Who)[] Handled =
    {
        ("vendor", "vendor list", "vendor frob", "VendorTrade: '[RC] /rc vendor list | info | ...'"),
        ("ui", "ui list", "ui frob", "HandleUiCommand: its usage"),
        ("discord", "discord status", "discord frob", "ChatDiscord.HandleCommand: its usage"),
        ("plates", "plates status", "plates frob", "HandlePlatesCommand: the plates usage"),
        ("plates", "nameplates off", "nameplates frob", "HandlePlatesCommand: the plates usage"),
        ("server", "server ilt auto", "server a b c", "HandleServerCommand: the server usage"),
        ("mastery", "mastery", "mastery", "HandleMasteryCommand (takes no arguments; ignores any, as before)"),
        ("video", "video world", "video frob", "VideoPatch.HandleCommand: its usage"),
        ("video", "videopatch fps 30", "videopatch fps x", "VideoPatch.HandleCommand: the fps usage"),
        ("hooks", "hooks status", "hooks frob", "HandleHooksCommand: its usage"),
        ("hooks", "uihooks off Tooltip", "uihooks on", "HandleHooksCommand: its usage"),
        ("flightdump", "flightdump before the wall", "flightdump", "FlightRecorder (any text is the note)"),
        ("quit", "quit", "exit", "closes the client (takes no arguments)"),
        ("report", "report the map froze", "report", "ProblemReport (alone: opens the panel)"),
        ("flight", "flight mark here", "flight frob", "FlightRecorder.HandleCommand: its usage"),
        ("autoid", "autoid limited", "autoid frob", "AutoIdService.HandleCommand: its usage"),
        ("uicost", "uicost 80", "uicost frob", "UiCost.HandleCommand: its usage"),
        ("netmsg", "netmsg log on", "netmsg frob", "ServerMessageStream.HandleCommand: its usage"),
        ("hotkeys", "hotkeys", "hotkeys frob", "HotkeyCommandParser: unknown verb + usage"),
        ("settings", "settings Hotkeys", "settings frob", "SettingsCommand: no tab called 'frob' (tabs: ...)"),
        ("hotkey", "hotkey list", "hotkey frob", "HotkeyCommandParser: unknown verb + usage"),
        ("hotkey", "hotkey add F5 /ra start", "hotkey add F5", "HotkeyCommandParser: the add usage"),
    };

    private static void HandlerVerbsPassThrough()
    {
        Case("verbs that check their own arguments: both forms reach the handler");
        foreach (var (verb, valid, invalid, who) in Handled)
        {
            ChatVerbCheck ok = ChatCommandReplies.Check(valid);
            Check(ok.Problem == null && ok.Verb?.Name == verb, $"'/rc {valid}' runs as {verb}");
            ChatVerbCheck bad = ChatCommandReplies.Check(invalid);
            Check(bad.Problem == null && bad.Verb?.Name == verb, $"'/rc {invalid}' reaches {verb}'s handler, which answers: {who}");
        }

        Case("the /rc hotkey parser answers every invalid form (compiled in)");
        foreach (string args in new[] { "frob", "add F5", "add NotAKey /ra start", "remove", "enable", "add F5 notanaction" })
        {
            HotkeyCommand c = HotkeyCommandParser.Parse(args);
            Check(c.Verb == HotkeyVerb.Error && c.Error.Length > 0, $"'/rc hotkey {args}': {c.Error}");
        }
        foreach (string args in new[] { "", "list", "add F5 /ra start", "remove F5", "on", "off", "status", "actions" })
            Check(HotkeyCommandParser.Parse(args).Verb != HotkeyVerb.Error, $"'/rc hotkey {args}' runs");
    }

    private static void UnknownAndBare()
    {
        Case("unknown verbs and a bare /rc answer with the command list");
        ChatVerbCheck unknown = ChatCommandReplies.Check("frobnicate now");
        Check(unknown.Verb == null && unknown.Problem != null && unknown.Problem.StartsWith("Unknown command '/rc frobnicate'", StringComparison.Ordinal),
            $"an unknown verb says so: {unknown.Problem}");
        Check(ChatCommandReplies.KindOfLine(unknown.Problem ?? "") == ChatReplyKind.Fail, "and is a failure");
        foreach (ChatVerb v in ChatCommandReplies.Verbs)
            Check(unknown.Problem!.Contains(v.Name, StringComparison.Ordinal), $"the list names {v.Name}");
        ChatVerbCheck bare = ChatCommandReplies.Check("");
        Check(bare.Verb == null && bare.Problem != null && bare.Problem.StartsWith("Usage: /rc <command>.", StringComparison.Ordinal), "'/rc' alone: the usage and the list");
        Check(ChatCommandReplies.Check("window save").Problem!.StartsWith("Unknown command '/rc window'", StringComparison.Ordinal),
            "'/rc window save' (sent by /ub window save) is not an engine verb, and now says so");
        Check(ChatCommandReplies.Check("  SKILLS   Show ").Problem == null, "case and spaces as IsVerb takes them");
    }

    private static void ModuleLinesClassified()
    {
        Case("module reply lines: failures and state");
        string[] fails =
        {
            "Usage: /rc video fps <0|5-60> (now none).",
            "Usage: /rc autoid [all|limited|off]  (alone: status)",
            "usage: /rv plates numbers size 0.5-2.5",
            "Video patch unavailable: no hook.",
            "Video patch is switched off (/rc hooks on VideoPatch to switch it back on).",
            "Flight recorder is off.",
            "Not dumped: the last automatic dump was 10 s ago (one per 5 min).",
            "Not capturing.",
            "Capture failed: access denied",
            "The server message stream is off; /rc netmsg on first (engine.json must allow it).",
            "Report not sent: one report a minute.",
            "The Report a problem panel isn't available (the ImGui layer is off?). Use /rc report <what went wrong> to send one from chat.",
            "Log in first: the relay is switched on or off for the character you're playing.",
            "The relay is off: /rc discord on, or use Send test in the Discord panel (/rc discord window).",
            "No route named 'x'. Add one in Chat > gear > Discord (or /rc discord window).",
            "Unknown '/rc hotkey frob'. Usage: ...",
            "No hotkey on F5.",
            "Unlock skipped: not on the game thread.",
            "Can't: no AC panel functions",
        };
        foreach (string f in fails)
            Check(ChatCommandReplies.KindOfLine(f) == ChatReplyKind.Fail, $"failure: {f}");
        string[] oks =
        {
            "Video patch: WORLD - AC's 3D world isn't drawn; AC's UI and the panels are. /rc video off to restore.",
            "Flight recorder: recording, 120 entries recorded",
            "Flight recorder paused for this session (/rc flight on resumes).",
            "Automatic flight dumps on.",
            "AutoId: limited (selected target; ...). Saved in engine.json.",
            "Server message log off.",
            "Hotkeys on.",
            "No hotkeys. Add one in Settings > Hotkeys (/rc hotkeys) or with /rc hotkey add <combo> <action or /command>.",
            "Hotkey added: F5 - /ra start.",
            "Sending your report (with your recent RynthCore log; use the panel, /rc report, to leave the log out)...",
            "Discord relay on for Someone. 2 routes.",
            "Action state (what the client checks before a use, equip or move):",
            "Unlock queued for the next game tick.",
        };
        foreach (string o in oks)
            Check(ChatCommandReplies.KindOfLine(o) == ChatReplyKind.Ok, $"state: {o}");
        Check(ChatCommandReplies.KindOfLines(new[] { "Video patch: off - normal drawing.", "Usage: /rc video fps <0|5-60>" }) == ChatReplyKind.Fail,
            "one failing line makes the answer a failure (all of it then reaches a script's chat)");
        Check(ChatCommandReplies.KindOfLines(Array.Empty<string>()) == ChatReplyKind.Ok, "no lines: not a failure");
    }

    private static void ParsersShared()
    {
        Case("the parsers the dispatcher and the check share");
        Check(ChatCommandReplies.IsVerb("skills show", "skills", out string a) && a == "show", "IsVerb: verb and args");
        Check(ChatCommandReplies.IsVerb("SKILLS", "skills", out a) && a == "", "IsVerb: case, no args");
        Check(!ChatCommandReplies.IsVerb("skillset", "skills", out _), "IsVerb: a longer word is another verb");
        Check(!ChatCommandReplies.IsVerb("flightdump x", "flight", out _), "IsVerb: flight is not flightdump");
        foreach (var (args, want) in new (string, bool?)[] { ("", null), ("toggle", null), ("show", true), ("on", true), ("open", true), ("hide", false), ("off", false), ("close", false) })
            Check(ChatCommandReplies.TryPanelMode(args, out bool? show) && show == want, $"TryPanelMode '{args}'");
        Check(!ChatCommandReplies.TryPanelMode("frob", out _), "TryPanelMode: anything else is wrong");
        Check(ChatCommandReplies.OracleSkillsTab("skills") == 0 && ChatCommandReplies.OracleSkillsTab("attributes") == 1
              && ChatCommandReplies.OracleSkillsTab("progression") == 2 && ChatCommandReplies.OracleSkillsTab("none") == -1
              && ChatCommandReplies.OracleSkillsTab("frob") == ChatCommandReplies.InvalidTab, "oracle-skills tabs");
        Check(ChatCommandReplies.ClassicTab("LOOT") == 5 && ChatCommandReplies.ClassicTab("frob") == -1, "the Classic tabs by name");
        Check(ChatCommandReplies.TryPanelId("0x1F", out uint id) && id == 0x1F && ChatCommandReplies.TryPanelId("12", out id) && id == 12
              && !ChatCommandReplies.TryPanelId("twelve", out _), "AC panel ids: decimal or 0x hex");
    }

    private static void ReplyTexts()
    {
        Case("reply texts");
        Check(ChatCommandReplies.PanelReply("Inventory panel", null, wasOpen: false) == "Inventory panel opened.", "toggle a closed panel");
        Check(ChatCommandReplies.PanelReply("Inventory panel", null, wasOpen: true) == "Inventory panel closed.", "toggle an open panel");
        Check(ChatCommandReplies.PanelReply("Mini Remote", true, wasOpen: false) == "Mini Remote opened.", "show a closed panel");
        Check(ChatCommandReplies.PanelReply("Mini Remote", true, wasOpen: true) == "Mini Remote is already open.", "show an open panel");
        Check(ChatCommandReplies.PanelReply("Bounties panel", false, wasOpen: false) == "Bounties panel is already closed.", "hide a closed panel");
        Check(ChatCommandReplies.PanelReply("Bounties panel", false, wasOpen: true) == "Bounties panel closed.", "hide an open panel");
        Check(ChatCommandReplies.SettingsTabReply("Hotkeys", false) == "Settings opened on Hotkeys.", "Settings opened on a tab");
        Check(ChatCommandReplies.SettingsTabReply("Hotkeys", true) == "Settings switched to Hotkeys.", "Settings switched tab");
        Check(ChatCommandReplies.DashboardReply(true, "loot", true) == "RynthAi dashboard: Classic look, loot tab.", "dashboard tab");
        Check(ChatCommandReplies.DashboardReply(false, null, false) == "RynthAi dashboard: Modern look (RynthAi's dashboard isn't loaded, so nothing opened).", "dashboard without RynthAi");
        for (int t = -1; t <= 2; t++)
            Check(ChatCommandReplies.OracleSkillsReply(t).StartsWith("RynthOracle's window shows", StringComparison.Ordinal), $"oracle-skills {t}");
        Check(ChatCommandReplies.Failed("/rc vendor", "NullReferenceException", "x") == "/rc vendor failed: NullReferenceException: x (the RynthCore log has more).", "a thrown command");
        Check(ChatCommandReplies.Done("/rc sense") == "/rc sense: done.", "the gap filler");
        Check(ChatCommandReplies.CommandName("/rc vendor buy 1") == "/rc vendor" && ChatCommandReplies.CommandName("/rc") == "/rc"
              && ChatCommandReplies.CommandName("/rv plates self on") == "/rv plates", "a command's name in a reply");
    }

    // ── the source: catalog and TryHandle in step, one writer ──────────────

    private static void SourceMatchesCatalog()
    {
        Case("RynthCoreChatCommands.cs: the verbs TryHandle routes are the catalog's, both ways");
        string? path = FindRepoFile(Path.Combine("src", "RynthCore.Engine", "Compatibility", "RynthCoreChatCommands.cs"));
        Check(path != null, "found RynthCoreChatCommands.cs by walking up");
        if (path == null) return;
        string src = File.ReadAllText(path);
        int start = src.IndexOf("private static void Handle(string trimmed)", StringComparison.Ordinal);
        int end = src.IndexOf("private static void HandleUiCommand", StringComparison.Ordinal);
        Check(start > 0 && end > start, "found Handle()");
        if (start < 0 || end < start) return;
        string handle = src.Substring(start, end - start);

        var routed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(handle, @"IsVerb\(sub, ""([^""]+)"""))
            routed.Add(m.Groups[1].Value);
        int sw = handle.IndexOf("switch (sub.ToLowerInvariant())", StringComparison.Ordinal);
        Check(sw > 0, "found the no-argument switch");
        if (sw > 0)
            foreach (Match m in Regex.Matches(handle.Substring(sw), @"case ""([a-z-]+)"":"))
                routed.Add(m.Groups[1].Value);
        Check(routed.Count >= 60, $"read {routed.Count} spellings from Handle()");

        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChatVerb v in ChatCommandReplies.Verbs)
        {
            listed.Add(v.Name);
            foreach (string al in v.Aliases) listed.Add(al);
        }
        foreach (string r in routed)
            Check(listed.Contains(r), $"'{r}' (routed in Handle) is in ChatCommandReplies.Verbs, so it isn't answered as unknown");
        foreach (string l in listed)
            Check(routed.Contains(l), $"'{l}' (in the list) is routed in Handle");

        Case("RynthCoreChatCommands.cs: every reply goes through the one writer");
        Check(Regex.Matches(src, @"EnqueueWriteToChat\(").Count == 1, "one EnqueueWriteToChat (the writer in Reply)");
        Check(!Regex.IsMatch(src, @"\bReply\(\$?""[Uu]sage"), "no usage line is written as a success (they go through Fail)");
        Check(!Regex.IsMatch(src, @"\bReply\((usage|ServerUsage)\)"), "nor the usage constants");
        Check(!Regex.IsMatch(src, @"catch \(Exception ex\) \{ RynthLog\.Compat"), "no command's exception is only logged");
    }

    private static string? FindRepoFile(string relative)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            string candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    // ── the chat rate limit (ChatRateLimiter, 2026-10-28) ──────────

    private static void ChatBurst()
    {
        Case("plugin chat: a burst of 12 lines goes at once, then one per 100 ms");
        var r = new ChatRateLimiter();
        int sent = 0;
        for (int i = 0; i < 20; i++) if (r.TryTake(1000)) sent++;
        Check(sent == 12, $"a 20-line reply at one moment: 12 go (got {sent})");
        Check(!r.TryTake(1050), "50 ms later: still spent");
        Check(r.TryTake(1100), "100 ms later: one more");
        Check(!r.TryTake(1100), "and only one");
        int steady = 0;
        for (long t = 1200; t <= 3100; t += 100) if (r.TryTake(t)) steady++;
        Check(steady == 20, $"one a line every 100 ms keeps going (20 of 20, got {steady})");
        var idle = new ChatRateLimiter();
        idle.TryTake(1000);
        int after = 0;
        for (int i = 0; i < 30; i++) if (idle.TryTake(60_000)) after++;
        Check(after == 12, $"a long quiet spell refills to 12, no more (got {after})");
        var back = new ChatRateLimiter();
        for (int i = 0; i < 12; i++) back.TryTake(5000);
        Check(!back.TryTake(4000), "a clock that went backwards refills nothing");
        int spam = 0;
        var loop = new ChatRateLimiter();
        for (long t = 0; t < 1000; t += 5) if (loop.TryTake(10_000 + t)) spam++;
        Check(spam <= 22, $"a retry loop sending every 5 ms for 1 s: at most 12 + 10 (got {spam})");
    }

    // ── controls that run a command (2026-10-31: buttons for what commands do) ──

    private static void ControlsRunTheirCommands()
    {
        Case("the Sense panel's view switch: each button names a /rc sense command that runs");
        foreach (RynthCore.Engine.UI.Data.SenseLayoutMode to in new[] { RynthCore.Engine.UI.Data.SenseLayoutMode.Full,
                     RynthCore.Engine.UI.Data.SenseLayoutMode.Compact, RynthCore.Engine.UI.Data.SenseLayoutMode.Collapsed })
        {
            string tip = RynthCore.Engine.UI.Data.SenseLayout.SwitchTip(to);
            Match m = Regex.Match(tip, @"/rc (sense [a-z ]+)\)");
            Check(m.Success, $"the {to} button's tip names its command: {tip}");
            if (!m.Success) continue;
            ChatVerbCheck c = ChatCommandReplies.Check(m.Groups[1].Value);
            Check(c.Problem == null && c.Verb?.Name == "sense", $"'/rc {m.Groups[1].Value}' runs");
        }

        Case("the Lua panel's live reload toggle");
        Check(RynthCore.Engine.UI.Data.LuaLiveReload.Label(null) == null, "a RynthLua without live reload: no toggle");
        Check(RynthCore.Engine.UI.Data.LuaLiveReload.Label(true) == "Live reload: on"
              && RynthCore.Engine.UI.Data.LuaLiveReload.Label(false) == "Live reload: off", "it says the value now");
        Check(RynthCore.Engine.UI.Data.LuaLiveReload.ClickArgs(true) == "livereload off"
              && RynthCore.Engine.UI.Data.LuaLiveReload.ClickArgs(false) == "livereload on", "a click sends the other value (/lua livereload on|off)");
        Check(RynthCore.Engine.UI.Data.LuaLiveReload.Tip(true).Contains("/lua livereload on|off", StringComparison.Ordinal)
              && RynthCore.Engine.UI.Data.LuaLiveReload.Tip(false).Contains("/lua livereload on|off", StringComparison.Ordinal), "the tip names the command");
    }

    private static void Case(string name)
    {
        _case = name;
        Console.WriteLine($"-- {name}");
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"  FAIL [{_case}] {what}");
    }
}

using RynthCore.Engine.Compatibility;
using RynthCore.Engine.UI.Data;

namespace ChatTellTests;

/// <summary>
/// RynthChat's /reply, /retell (TellMemory.cs) and Ctrl+V (ChatInputPaste.cs) without AC.
/// Chat lines are written the way AC prints them (HearDirectSpeech's player link, ACE's
/// "You tell" echo).
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private const string BobTells = "<Tell:IIDString:1342177281:Bob>Bob<\\Tell> tells you, \"hi there\"";

    private static int Main()
    {
        ParseShortcuts();
        IncomingTellSetsReplyTarget();
        NpcAndOtherLinesIgnored();
        OutgoingTellSetsRetellTarget();
        RewriteCases();
        PasteClean();
        PasteInsert();
        EmoteLines();
        MeAndAtCommands();
        SlashEmotes();
        SelectionText();
        ItemInfoOneLine();

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        Console.WriteLine(_failed == 0 ? "ALL CHAT TELL TESTS PASSED." : $"{_failed} FAILED.");
        return _failed == 0 ? 0 : 1;
    }

    // ── shortcuts ───────────────────────────────────────────────────────────

    private static void ParseShortcuts()
    {
        Case("parse: /r /reply /rt /retell");
        Check(TellMemory.Parse("/r hello", out string m) == TellShortcut.Reply && m == "hello", "/r hello");
        Check(TellMemory.Parse("/reply  hello there ", out m) == TellShortcut.Reply && m == "hello there", "/reply, trimmed");
        Check(TellMemory.Parse("/R Hi", out m) == TellShortcut.Reply && m == "Hi", "case-insensitive, message case kept");
        Check(TellMemory.Parse("@r hi", out m) == TellShortcut.Reply && m == "hi", "@ prefix, as AC accepts");
        Check(TellMemory.Parse("/rt again", out m) == TellShortcut.Retell && m == "again", "/rt");
        Check(TellMemory.Parse("/retell again", out m) == TellShortcut.Retell && m == "again", "/retell");
        Check(TellMemory.Parse("/r", out m) == TellShortcut.Reply && m == "", "/r alone");
        Check(TellMemory.Parse("  /rt  ", out m) == TellShortcut.Retell && m == "", "/rt alone, padded");
        Check(TellMemory.Parse("/ra start", out _) == TellShortcut.None, "/ra is RynthAi's");
        Check(TellMemory.Parse("/rc ui", out _) == TellShortcut.None, "/rc is the engine's");
        Check(TellMemory.Parse("/rn", out _) == TellShortcut.None, "/rn");
        Check(TellMemory.Parse("/rtx hi", out _) == TellShortcut.None, "/rtx");
        Check(TellMemory.Parse("r hi", out _) == TellShortcut.None, "no slash: said aloud");
        Check(TellMemory.Parse("/tell Bob, hi", out _) == TellShortcut.None, "/tell");
        Check(TellMemory.Parse("", out _) == TellShortcut.None && TellMemory.Parse(null, out _) == TellShortcut.None, "empty / null");
    }

    // ── tracking ────────────────────────────────────────────────────────────

    private static void IncomingTellSetsReplyTarget()
    {
        Case("incoming player tell: the reply target");
        var t = new TellMemory();
        Check(t.LastTeller == null && t.LastTellee == null, "starts empty");
        t.OnChatLine(BobTells);
        Check(t.LastTeller == "Bob", "Bob");
        t.OnChatLine("<Tell:IIDString:1342177300:Sir Alice Two>Sir Alice Two<\\Tell> tells you, \"x, y\"");
        Check(t.LastTeller == "Sir Alice Two", "a spaced name; the newest tell wins");
        t.OnChatLine("<Tell:IIDString:1342177301:Olthy&>Olthy&<\\Tell> tells you, \"grr\"");
        Check(t.LastTeller == "Olthy", "an Olthoi player's suffix dropped");
        t.OnChatLine("<Tell:IIDString:1342177302:Carl>Carl<\\Tell> tells you, \"bye\"\n");
        Check(t.LastTeller == "Carl", "trailing line break");
        Check(t.LastTellee == null, "an incoming tell is not a retell target");
    }

    private static void NpcAndOtherLinesIgnored()
    {
        Case("lines that aren't a player's tell change nothing");
        var t = new TellMemory();
        t.OnChatLine(BobTells);
        t.OnChatLine("Town Crier tells you, \"Hear ye\"");   // an NPC: AC adds no link (retail /r ignores it too)
        Check(t.LastTeller == "Bob", "NPC tell");
        t.OnChatLine("Dave says, \"<Tell:IIDString:1:Eve>Eve<\\Tell> tells you, nonsense\"");
        Check(t.LastTeller == "Bob", "a quoted tell inside a say");
        t.OnChatLine("[Allegiance] <Tell:IIDString:1342177400:Fred>Fred<\\Tell> says, \"hi\"");
        Check(t.LastTeller == "Bob", "channel line with a link");
        t.OnChatLine("Dave says, \"You tell Bob, nothing\"");
        Check(t.LastTellee == null, "a quoted echo inside a say");
        t.OnChatLine("");
        t.OnChatLine(null);
        Check(t.LastTeller == "Bob" && t.LastTellee == null, "empty / null");
    }

    private static void OutgoingTellSetsRetellTarget()
    {
        Case("tells you send: the retell target");
        var t = new TellMemory();
        t.OnChatLine("You tell Bob, \"hello\"");
        Check(t.LastTellee == "Bob", "server echo");
        t.NoteTellSent("  Alice ");
        Check(t.LastTellee == "Alice", "dispatcher's /tell, trimmed");
        t.OnChatLine("You tell Sir Bob Two, \"a, b\"");
        Check(t.LastTellee == "Sir Bob Two", "echo with a spaced name and a comma in the message");
        t.NoteTellSent("");
        t.NoteTellSent(null);
        t.NoteTellSent("  ");
        Check(t.LastTellee == "Sir Bob Two", "blank names ignored");
        Check(t.LastTeller == null, "a sent tell is not a reply target");
        t.Clear();
        Check(t.LastTeller == null && t.LastTellee == null, "Clear");
    }

    // ── rewrite ─────────────────────────────────────────────────────────────

    private static void RewriteCases()
    {
        Case("rewrite to /tell");
        var t = new TellMemory();
        Check(!t.TryRewrite("/say hi", out string? line, out string? err) && line == null && err == null, "not a shortcut");
        Check(t.TryRewrite("/r hi", out line, out err) && line == null && err == TellMemory.NoTellerMessage, "/r with no one to reply to");
        Check(t.TryRewrite("/rt hi", out line, out err) && line == null && err == TellMemory.NoTelleeMessage, "/rt with no one to retell");

        t.OnChatLine(BobTells);
        t.NoteTellSent("Alice");
        Check(t.TryRewrite("/r thanks, mate", out line, out err) && line == "/tell Bob, thanks, mate" && err == null, "/r -> Bob (message commas kept)");
        Check(t.TryRewrite("/reply ok", out line, out _) && line == "/tell Bob, ok", "/reply -> Bob");
        Check(t.TryRewrite("/rt again", out line, out _) && line == "/tell Alice, again", "/rt -> Alice");
        Check(t.TryRewrite("/retell again", out line, out _) && line == "/tell Alice, again", "/retell -> Alice");
        Check(t.TryRewrite("/r", out line, out err) && line == null && err != null && err.Contains("Bob"), "/r alone: says who it would go to");

        // The dispatcher's /tell then remembers Bob, so /rt after /r goes to Bob (retail too: the server echoes "You tell Bob").
        t.NoteTellSent("Bob");
        Check(t.TryRewrite("/rt more", out line, out _) && line == "/tell Bob, more", "/rt after /r");
    }

    // ── paste ───────────────────────────────────────────────────────────────

    private static void PasteClean()
    {
        Case("paste: one clean line");
        Check(ChatInputPaste.Clean("hello") == "hello", "plain");
        Check(ChatInputPaste.Clean("a\r\nb") == "a b", "CRLF -> space");
        Check(ChatInputPaste.Clean("a\n\n\nb") == "a b", "a run of breaks -> one space");
        Check(ChatInputPaste.Clean("a \nb") == "a b" && ChatInputPaste.Clean("a\n b") == "a b", "no double space at a break");
        Check(ChatInputPaste.Clean("\r\nab\r\n") == "ab", "leading / trailing breaks dropped");
        Check(ChatInputPaste.Clean("a\tb") == "a b", "tab -> space");
        Check(ChatInputPaste.Clean("a\u0016b\u0003c\u007Fd") == "abcd", "control characters dropped");
        Check(ChatInputPaste.Clean("héllo ✓") == "héllo ✓", "non-ASCII kept");
        Check(ChatInputPaste.Clean(null) == "" && ChatInputPaste.Clean("") == "", "null / empty");
    }

    private static void PasteInsert()
    {
        Case("paste: inserted at the caret");
        Check(ChatInputPaste.Insert("/tell Bob, ", 11, "hi there", out int c) == "/tell Bob, hi there" && c == 19, "at the end");
        Check(ChatInputPaste.Insert("ac", 1, "b", out c) == "abc" && c == 2, "in the middle");
        Check(ChatInputPaste.Insert("bc", 0, "a", out c) == "abc" && c == 1, "at the start");
        Check(ChatInputPaste.Insert("ab", 99, "c", out c) == "abc" && c == 3, "caret past the end clamped");
        string same = "keep";
        Check(ReferenceEquals(ChatInputPaste.Insert(same, 2, "\r\n", out c), same) && c == 2, "nothing to paste: line unchanged");
        Check(ReferenceEquals(ChatInputPaste.Insert(same, 2, null, out c), same) && c == 2, "empty clipboard: line unchanged");

        string big = new('x', 5000);
        string r = ChatInputPaste.Insert("ab", 2, big, out c);
        Check(r.Length == ChatInputPaste.MaxLineChars && c == ChatInputPaste.MaxLineChars, "capped at MaxLineChars");
        string full = new('y', ChatInputPaste.MaxLineChars);
        Check(ReferenceEquals(ChatInputPaste.Insert(full, 3, "z", out c), full) && c == 3, "full line: nothing added");
        string pair = new string('a', ChatInputPaste.MaxLineChars - 2);
        r = ChatInputPaste.Insert(pair, pair.Length, "b\U0001F600c", out c);   // room for "b" + half the pair
        Check(r == pair + "b" && c == pair.Length + 1, "never splits a surrogate pair");
    }

    // ── emotes (ChatLineRoute.cs) ───────────────────────────────────────────

    private static void EmoteLines()
    {
        Case("emote lines go to AC's parser");
        Check(ChatLineRoute.IsAcEmoteLine("@e waves"), "@e text");
        Check(ChatLineRoute.IsAcEmoteLine("@EM waves"), "@em, any case");
        Check(ChatLineRoute.IsAcEmoteLine("@emote looks around the town curiously."), "@emote text");
        Check(ChatLineRoute.IsAcEmoteLine("@emotes"), "@emotes alone (the list)");
        Check(ChatLineRoute.IsAcEmoteLine("; waves"), "; text");
        Check(ChatLineRoute.IsAcEmoteLine(":waves"), ":text");
        Check(ChatLineRoute.IsAcEmoteLine("*wave*"), "*wave*");
        Check(ChatLineRoute.IsAcEmoteLine("*Warm Hands*"), "*two words*");

        Case("everything else stays speech or a server command");
        Check(!ChatLineRoute.IsAcEmoteLine("hello there"), "plain words");
        Check(!ChatLineRoute.IsAcEmoteLine("@myquests"), "an ACE @command");
        Check(!ChatLineRoute.IsAcEmoteLine("@echo hi"), "@echo is not @e");
        Check(!ChatLineRoute.IsAcEmoteLine("@emu"), "@emu is not @em");
        Check(!ChatLineRoute.IsAcEmoteLine("*"), "a lone asterisk");
        Check(!ChatLineRoute.IsAcEmoteLine("**"), "two asterisks");
        Check(!ChatLineRoute.IsAcEmoteLine("* *"), "asterisks round a space");
        Check(!ChatLineRoute.IsAcEmoteLine("*wave"), "no closing asterisk");
        Check(!ChatLineRoute.IsAcEmoteLine(";"), "a lone semicolon");
        Check(!ChatLineRoute.IsAcEmoteLine(""), "empty");
    }

    // ── /me and @commands (ChatLineRoute.cs, 2026-11-26) ────────────────────

    private static void MeAndAtCommands()
    {
        Case("/me is AC's @emote");
        Check(ChatLineRoute.IsMeEmote("/me looks menacingly at the mob", out string t) && t == "looks menacingly at the mob", "/me text");
        Check(ChatLineRoute.IsMeEmote("/ME   waves  ", out t) && t == "waves", "any case, trimmed");
        Check(ChatLineRoute.IsMeEmote("/me", out t) && t.Length == 0, "/me alone");
        Check(!ChatLineRoute.IsMeEmote("/meet you there", out _), "/meet is not /me");
        Check(!ChatLineRoute.IsMeEmote("/menu", out _), "/menu is not /me");
        Check(!ChatLineRoute.IsMeEmote("me too", out _), "plain words");
        Check(!ChatLineRoute.IsMeEmote("", out _), "empty");

        Case("@commands go to AC's parser");
        Check(ChatLineRoute.IsAtCommand("@loc"), "@loc (a client command)");
        Check(ChatLineRoute.IsAtCommand("@friends add Someone"), "@friends with arguments");
        Check(ChatLineRoute.IsAtCommand("@myquests"), "@myquests (a server command: AC passes it on)");
        Check(!ChatLineRoute.IsAtCommand("@"), "a lone @");
        Check(!ChatLineRoute.IsAtCommand("@ everyone"), "@ then a space: speech");
        Check(!ChatLineRoute.IsAtCommand("@@"), "@@");
        Check(!ChatLineRoute.IsAtCommand("/loc"), "a / command is not an @ one (it takes the / path)");
        Check(!ChatLineRoute.IsAtCommand("hello @Bob"), "an @ inside a line");
    }

    // ── /wave as *Wave* (ChatSlashEmote.cs, 2026-11-26) ─────────────────────

    private static void SlashEmotes()
    {
        Case("/emote word is AC's *Emote*");
        Check(ChatSlashEmote.TryMatch("/wave", out string a) && a == "*Wave*", "/wave");
        Check(ChatSlashEmote.TryMatch("/WAVE", out a) && a == "*Wave*", "any case");
        Check(ChatSlashEmote.TryMatch("/ymca", out a) && a == "*YMCA*", "/ymca, AC's capitals");
        Check(ChatSlashEmote.TryMatch("/BeSeeingYou", out a) && a == "*BeSeeingYou*", "/BeSeeingYou");
        Check(ChatSlashEmote.TryMatch("/wavehigh", out a) && a == "*WaveHigh*", "/wavehigh is WaveHigh, not Wave");
        Check(ChatSlashEmote.TryMatch("/teapot", out a) && a == "*Teapot*", "/teapot");
        int all = 0;
        foreach (string name in ChatSlashEmote.Names)
            if (ChatSlashEmote.TryMatch("/" + name.Replace(" ", ""), out a) && a == "*" + name + "*"
                && ChatLineRoute.IsAcEmoteLine(a))
                all++;
        Check(all == ChatSlashEmote.Names.Length && all == 41, $"every one of AC's 41 emotes, as an asterisk line AC's parser path takes ({all})");

        Case("two-word emotes, run together or apart");
        Check(ChatSlashEmote.TryMatch("/warmhands", out a) && a == "*Warm Hands*", "/warmhands");
        Check(ChatSlashEmote.TryMatch("/warm hands", out a) && a == "*Warm Hands*", "/warm hands");
        Check(ChatSlashEmote.TryMatch("/Warm  Hands", out a) && a == "*Warm Hands*", "/Warm  Hands, two spaces");
        Check(ChatSlashEmote.TryMatch("/scratch head", out a) && a == "*Scratch Head*", "/scratch head");
        Check(ChatSlashEmote.TryMatch("/SHAKEHEAD", out a) && a == "*Shake Head*", "/SHAKEHEAD");
        Check(!ChatSlashEmote.TryMatch("/warm", out _), "/warm alone");
        Check(!ChatSlashEmote.TryMatch("/shake", out _), "/shake alone");
        Check(!ChatSlashEmote.TryMatch("/wave high", out _), "/wave high: WaveHigh is one word, so text after /wave");

        Case("anything else is left as it is");
        Check(!ChatSlashEmote.TryMatch("/wave hello", out a) && a.Length == 0, "/wave hello: text after the emote");
        Check(!ChatSlashEmote.TryMatch("/warm hands now", out _), "/warm hands now");
        Check(!ChatSlashEmote.TryMatch("/waves", out _), "/waves");
        Check(!ChatSlashEmote.TryMatch("/wav", out _), "/wav");
        Check(!ChatSlashEmote.TryMatch("/ wave", out _), "/ wave");
        Check(!ChatSlashEmote.TryMatch("wave", out _), "no slash: speech");
        Check(!ChatSlashEmote.TryMatch("*wave*", out _), "*wave* takes its own route");
        Check(!ChatSlashEmote.TryMatch("@wave", out _), "@wave");
        Check(!ChatSlashEmote.TryMatch("/", out _) && !ChatSlashEmote.TryMatch("", out _) && !ChatSlashEmote.TryMatch(null, out _), "/, empty, null");

        Case("claimed commands win");
        // AC's own client command words (its command table in acclient.exe, 2026-11-26): AC's
        // parser runs them, so none may be an emote word.
        string[] acCommands =
        [
            "abandon", "boot_all", "ma", "alleg_recall", "mansion_recall", "re", "recall", "rt", "clist", "index",
            "log", "unfilter", "filter", "text", "lockui", "loadautoui", "saveautoui", "loadui", "saveui", "version",
            "render", "pkl", "pklite", "loc", "framerate", "endurance", "day", "birth", "age", "status", "msg_types",
            "msgtypes", "message_types", "messagetypes", "unsquelch", "squelch", "hoa", "hom", "hr", "hor", "hslist",
            "hou", "friends_remove", "friends_add", "friends", "loadfile", "fillcomps", "emotes", "me", "emote", "em",
            "e", "pla", "pklarena", "pka", "pkarena", "permit", "mp", "mar", "marketplace", "ls", "lif", "lifestone",
            "die", "cor", "corpse", "consent", "death", "afk", "whisper", "send", "t", "tell", "say", "retell", "pr",
            "mr", "rp", "notell", "chatting", "leave", "join", "vassal", "vassals", "patron", "monarch", "party", "g",
            "group", "fellow", "fellows", "fellowship", "covassal", "covassals", "co-vassals", "channels", "speaker",
            "ah", "alh", "ab", "allegiance", "allegiances", "commands", "help", "house", "lock", "motd", "ho",
            "hometown", "title", "officer", "ban", "br", "broadcast", "ch", "chat", "info", "boot", "who", "olthoi",
            "soc", "society", "crp", "roleplay", "clfg", "lfg", "ct", "trade", "cg", "general", "gu", "guild",
            // and the routes and commands the dispatcher hands out before the emote
            "a", "f", "r", "reply", "s", "rc", "ra", "vt", "ub", "mt", "lua", "rl",
        ];
        int stolen = 0;
        foreach (string cmd in acCommands)
            if (ChatSlashEmote.TryMatch("/" + cmd, out _)) { stolen++; Console.WriteLine($"   an emote name is a command: /{cmd}"); }
        Check(stolen == 0, "no AC, engine or plugin command word is an emote");
        Check(!ChatSlashEmote.TryMatch("/rc ui", out _) && !ChatSlashEmote.TryMatch("/ra start", out _), "/rc and /ra lines");
        Check(!ChatSlashEmote.TryMatch("/e waves", out _) && !ChatSlashEmote.TryMatch("/a hi", out _)
            && !ChatSlashEmote.TryMatch("/f hi", out _) && !ChatSlashEmote.TryMatch("/r hi", out _), "/e /a /f /r lines");
        Check(TellMemory.Parse("/wave", out _) == TellShortcut.None, "the /r rewrite (asked before the emote) leaves /wave alone");
    }

    // ── text selection (ChatTextSelection.cs) ───────────────────────────────

    private static void SelectionText()
    {
        Case("ends in reading order");
        int al = 5, ac = 3, bl = 2, bc = 9;
        ChatTextSelection.Order(ref al, ref ac, ref bl, ref bc);
        Check(al == 2 && ac == 9 && bl == 5 && bc == 3, "later line first: swapped");
        al = 4; ac = 8; bl = 4; bc = 2;
        ChatTextSelection.Order(ref al, ref ac, ref bl, ref bc);
        Check(al == 4 && ac == 2 && bl == 4 && bc == 8, "same line, backwards drag: swapped");
        al = 1; ac = 0; bl = 3; bc = 0;
        ChatTextSelection.Order(ref al, ref ac, ref bl, ref bc);
        Check(al == 1 && bl == 3, "already in order: kept");

        Case("the part of a line that is copied");
        const string line = "Bob says, \"meet at 42.1N, 33.6E\"";
        Check(ChatTextSelection.Part(line, "12:01:33", 0, line.Length) == "12:01:33 " + line, "whole line keeps its timestamp");
        Check(ChatTextSelection.Part(line, null, 0, line.Length) == line, "timestamps off: just the text");
        Check(ChatTextSelection.Part(line, "12:01:33", 19, 31) == "42.1N, 33.6E", "the middle: no timestamp");
        Check(ChatTextSelection.Part(line, "12:01:33", 0, 3) == "12:01:33 Bob", "from the start: timestamp too");
        Check(ChatTextSelection.Part(line, null, 31, 19) == "42.1N, 33.6E", "ends the wrong way round");
        Check(ChatTextSelection.Part(line, null, -4, 999) == line, "out of range: clamped");
        Check(ChatTextSelection.Part("", "12:00:00", 0, 0) == "12:00:00 ", "an empty line");

        Case("surrogate pairs are never split");
        string smile = "a\U0001F600b";   // a, high, low, b
        Check(ChatTextSelection.Clamp(smile, 2) == 1, "between the halves: to the pair's start");
        Check(ChatTextSelection.Clamp(smile, 3) == 3, "after the pair: kept");
        Check(ChatTextSelection.Part(smile, null, 0, 2) == "a", "a cut inside the pair drops the half");

        Case("the character under the mouse");
        Func<int, float> w = _ => 10f;   // every character 10 wide
        Check(ChatTextSelection.CharAtX("hello", 0, 5, -3f, w) == 0, "left of the row: its start");
        Check(ChatTextSelection.CharAtX("hello", 0, 5, 4f, w) == 0, "left half of the first character");
        Check(ChatTextSelection.CharAtX("hello", 0, 5, 6f, w) == 1, "right half: after it");
        Check(ChatTextSelection.CharAtX("hello", 0, 5, 26f, w) == 3, "26 px: between l and l");
        Check(ChatTextSelection.CharAtX("hello", 0, 5, 500f, w) == 5, "right of the row: its end");
        Check(ChatTextSelection.CharAtX("hello world", 6, 11, 12f, w) == 7, "a later row starts at its own start");
        Check(ChatTextSelection.CharAtX(smile, 0, smile.Length, 16f, w) == 3, "a pair is one character");
    }

    // ── an item's info lines as one (ChatTextSelection.cs) ──────────────────

    private static void ItemInfoOneLine()
    {
        const string P = ChatTextSelection.ItemPrefix;
        var lines = new List<string>
        {
            "You say, \"look at this\"",
            P + "Ornate Long Sword, Gold, Craft 6, Value 4,200",
            P + "  Slash, 80-120, +5% Melee D",
            P + "  Damage Rating 3  Wield 300",
            P + "  Spells: Blood Drinker VI, Swift Killer VI",
            P + "Pearl of Blood Drinking, Rare",
            "Bob says, \"nice\"",
        };

        Case("which lines are one item's");
        Check(ChatTextSelection.ItemRun(lines, 1, out int f, out int l) && f == 1 && l == 4, "from its name line: the 3 lines under it");
        Check(ChatTextSelection.ItemRun(lines, 3, out f, out l) && f == 1 && l == 4, "from a line under it: back to its name");
        Check(ChatTextSelection.ItemRun(lines, 5, out f, out l) && f == 5 && l == 5, "the next item: its own line only");
        Check(!ChatTextSelection.ItemRun(lines, 0, out _, out _), "a say line is no item");
        Check(!ChatTextSelection.ItemRun(lines, 99, out _, out _), "out of range");
        Check(ChatTextSelection.IsItemDetail(lines[2]) && !ChatTextSelection.IsItemDetail(lines[1]), "detail = indented under the name");

        Case("joined into one line");
        ChatTextSelection.ItemRun(lines, 2, out f, out l);
        Check(ChatTextSelection.ItemOneLine(lines, f, l)
              == "Ornate Long Sword, Gold, Craft 6, Value 4,200, Slash, 80-120, +5% Melee D, Damage Rating 3, Wield 300, Spells: Blood Drinker VI, Swift Killer VI",
              "tags gone, lines and two-space gaps joined with commas");
        Check(ChatTextSelection.ItemOneLine(lines, 5, 5) == "Pearl of Blood Drinking, Rare", "one line: just without its tag");

        var orphan = new List<string> { P + "  Slash, 80-120", P + "  Wield 300" };
        Check(ChatTextSelection.ItemRun(orphan, 1, out f, out l) && f == 0 && l == 1, "name line scrolled away: the lines that are there");
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
        Console.WriteLine($"   FAIL [{_case}] {what}");
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using RynthCore.Engine.UI.Data;

namespace ChatFilterTests;

/// <summary>
/// RynthChat rule match modes and standard filters without AC: a Regex rule compiles and matches
/// exactly as before; Contains / Starts with / Ends with match plain text; "mode" loads and saves,
/// and a file without it is a file of Regex rules, written back unchanged; every standard filter
/// catches its example lines and spares player speech.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    // Real lines from the RynthCore logs ([BuffChat] type=... text=...), plus player speech.
    private static readonly string[] Lines =
    {
        "You evaded Tusker Guard!",
        "Tusker Guard grazes your foot for 4 points of bludgeoning damage!",
        "Critical hit! Olthoi Swarm Nymph nicks your head for 21 points of piercing damage!",
        "You say, \"Malar Zhavik\"",
        "The spell consumed the following components: Prismatic Taper",
        "Your spell fizzled.",
        "You cast Focus Self I on yourself, but it is surpassed by Incantation of Focus Other",
        "You don't have all the components for this spell.",
        "You're too busy!",
        "AutoRun ON",
        "In portal Space",
        "[General] Aja says, \"lol\"",
        "Bob tells you, \"you evade like a pro (really)\"",
        "Bob says, \"$5 for the Black Opal?\"",
        "",
    };

    private static int Main()
    {
        RegexModeUnchanged();
        LiveSettingsRulesUnchanged();
        Contains();
        StartsWith();
        EndsWith();
        EmptyAndBadPatterns();
        ModeJson();
        SettingsRoundTrip();
        WhenParsing();
        PresetsCatchAndSpare();
        PresetsWellFormed();
        RetailChannels();
        RetailStandardFilters();
        RetailRules();
        RetailSetting();
        TabsAcrossSetting();
        SharedFileReload();
        LineColorTags();
        LineColorKinds();
        LineColorDefaultsUnchanged();
        LineColorRetail();
        LineColorPrecedence();
        LineColorJson();
        HighlightRouting();
        HighlightPrecedence();
        HighlightBand();
        HighlightJson();
        HighlightOldReader();

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        Console.WriteLine(_failed == 0 ? "ALL CHAT FILTER TESTS PASSED." : $"{_failed} FAILED.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Case(string name)
    {
        _case = name;
        Console.WriteLine("- " + name);
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"  FAIL [{_case}] {what}");
    }

    /// <summary>What a rule compiled to before match modes (ChatFilterRule.Recompile, 2026-09-29).</summary>
    private static Regex? Before(string pattern, out string? error)
    {
        error = null;
        if (pattern.Length == 0) return null;
        try { return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(15)); }
        catch (ArgumentException ex) { error = ex.Message; return null; }
    }

    // A long timeout: these check what a mode matches, not the engine's 15 ms limit (see SameAsBefore).
    private static Regex? Now(ChatMatchMode mode, string pattern) => ChatRuleMatching.Compile(mode, pattern, 5000, out _);

    private static void SameAsBefore(string pattern, string why)
    {
        Regex? before = Before(pattern, out string? errBefore);
        Regex? now = ChatRuleMatching.Compile(ChatMatchMode.Regex, pattern, 15, out string? errNow);
        Check((before == null) == (now == null), $"{why}: compiles the same ('{pattern}')");
        Check(errBefore == errNow, $"{why}: same error ('{pattern}')");
        if (before == null || now == null) return;
        Check(before.ToString() == now.ToString() && before.Options == now.Options, $"{why}: same regex and options ('{pattern}')");
        Check(before.MatchTimeout == now.MatchTimeout, $"{why}: same match timeout ('{pattern}')");
        // The comparison matches get time to spare: the 15 ms limit itself is checked just above, and
        // on a busy PC (game clients, a release build) a first match can take longer (2026-10-22).
        Regex beforeSlow = new(before.ToString(), before.Options, TimeSpan.FromSeconds(5));
        Regex nowSlow = new(now.ToString(), now.Options, TimeSpan.FromSeconds(5));
        foreach (string line in Lines)
        {
            Match a = beforeSlow.Match(line), b = nowSlow.Match(line);
            Check(a.Success == b.Success && a.Index == b.Index && a.Length == b.Length, $"{why}: same match on '{line}' ('{pattern}')");
        }
    }

    // ── Regex: as before ─────────────────────────────────────────────────

    private static void RegexModeUnchanged()
    {
        Case("regex rules compile and match exactly as before");
        foreach (string p in new[]
        {
            "You evade", "^You evaded ", @"for \d+ points? of", "^Bob tells you, \"", "Autorun On", "In portal Space",
            @"^\[General\]", "(?i)fizzled", "busy!$", ".", "a|b", "[unclosed", "(", @"\", "  spaced  ",
        })
            SameAsBefore(p, "regex");
        Check(ChatRuleMatching.ToRegex(ChatMatchMode.Regex, "^You evaded ") == "^You evaded ", "a regex pattern is used as typed");
    }

    /// <summary>Every rule in the live settings file (if any) still compiles to what it did.</summary>
    private static void LiveSettingsRulesUnchanged()
    {
        Case("the rules in this PC's rynthchat_settings.json are unchanged (read only)");
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "rynthchat_settings.json");
        if (!File.Exists(path)) { Console.WriteLine("  (no live settings file; skipped)"); return; }
        string json = File.ReadAllText(path);
        RynthChatSettingsDto? dto = JsonSerializer.Deserialize(json, RynthChatJsonContext.Default.RynthChatSettingsDto);
        Check(dto != null, "the live file reads");
        int n = 0;
        foreach (RynthChatFilterDto f in dto?.Filters ?? Array.Empty<RynthChatFilterDto>())
        {
            if (f.Mode != null) continue;   // a plain-text rule saved by a newer build
            Check(ChatRuleMatching.Parse(f.Mode) == ChatMatchMode.Regex, "a rule without mode is Regex");
            SameAsBefore(f.Pattern ?? "", "live rule");
            n++;
        }
        Console.WriteLine($"  ({n} live rule(s) checked)");
    }

    // ── Plain text ───────────────────────────────────────────────────────

    private static void Contains()
    {
        Case("Contains: plain text anywhere, any case, nothing special");
        Regex rx = Now(ChatMatchMode.Contains, "evaded TUSKER")!;
        Check(rx.IsMatch("You evaded Tusker Guard!"), "anywhere, any case");
        Check(!rx.IsMatch("You evaded Olthoi Slayer!"), "not other text");
        Regex special = Now(ChatMatchMode.Contains, "(really)")!;
        Check(special.IsMatch("Bob tells you, \"you evade like a pro (really)\""), "( ) are characters");
        Check(!special.IsMatch("really"), "...not a group");
        Regex money = Now(ChatMatchMode.Contains, "$5 for the")!;
        Check(money.IsMatch("Bob says, \"$5 for the Black Opal?\""), "$ is a character");
        Regex dot = Now(ChatMatchMode.Contains, "fizzled.")!;
        Check(dot.IsMatch("Your spell fizzled."), "a dot matches a dot");
        Check(!dot.IsMatch("Your spell fizzledX"), "...not any character");
        Regex quote = Now(ChatMatchMode.Contains, "You say, \"")!;
        Check(quote.IsMatch("You say, \"Malar Zhavik\""), "a quote");
        Regex star = Now(ChatMatchMode.Contains, "a*b")!;
        Check(star.IsMatch("x a*b y") && !star.IsMatch("aaab"), "* is a character");
        Regex spaced = Now(ChatMatchMode.Contains, " evaded ")!;
        Check(spaced.IsMatch("You evaded Tusker Guard!") && !spaced.IsMatch("evaded"), "spaces in Contains count");
        Match m = rx.Match("You evaded Tusker Guard!");
        Check(m.Index == 4 && m.Length == "evaded TUSKER".Length, "the match is the text (Colour match colours just it)");
    }

    private static void StartsWith()
    {
        Case("Starts with");
        Regex rx = Now(ChatMatchMode.StartsWith, "you say, \"")!;
        Check(rx.IsMatch("You say, \"Malar Zhavik\""), "the start, any case");
        Check(!rx.IsMatch("Bob says, \"You say, \\\"x\\\"\""), "not later in the line");
        Regex lead = Now(ChatMatchMode.StartsWith, "  [General]")!;
        Check(lead.IsMatch("[General] Aja says, \"lol\""), "leading spaces in the pattern don't count");
        Check(!lead.IsMatch("Bob says, \"[General]\""), "brackets are characters, at the start only");
    }

    private static void EndsWith()
    {
        Case("Ends with");
        Regex rx = Now(ChatMatchMode.EndsWith, "evaded your attack.  ")!;
        Check(rx.IsMatch("Drudge Slinker evaded your attack."), "the end, trailing pattern spaces ignored");
        Check(rx.IsMatch("Drudge Slinker evaded your attack.   "), "trailing line spaces ignored");
        Check(!rx.IsMatch("Drudge Slinker evaded your attack. Again"), "not earlier in the line");
        Check(!rx.IsMatch("Drudge Slinker evaded your attackX"), "the dot is a dot");
    }

    private static void EmptyAndBadPatterns()
    {
        Case("empty and bad patterns");
        foreach (ChatMatchMode mode in Enum.GetValues<ChatMatchMode>())
        {
            Check(ChatRuleMatching.Compile(mode, "", 15, out string? e) == null && e == null, $"{mode}: empty matches nothing, no error");
            Check(ChatRuleMatching.Compile(mode, null, 15, out _) == null, $"{mode}: null matches nothing");
        }
        foreach (ChatMatchMode mode in new[] { ChatMatchMode.Contains, ChatMatchMode.StartsWith, ChatMatchMode.EndsWith })
        {
            Check(ChatRuleMatching.Compile(mode, "   ", 15, out _) == null, $"{mode}: spaces only match nothing");
            Check(ChatRuleMatching.Compile(mode, "[unclosed (", 15, out string? e) != null && e == null, $"{mode}: regex syntax is just text");
        }
        Check(ChatRuleMatching.Compile(ChatMatchMode.Regex, "[unclosed", 15, out string? err) == null && err != null, "Regex: a bad regex reports its error");
        Check(ChatRuleMatching.Compile(ChatMatchMode.Regex, "   ", 15, out _) != null, "Regex: spaces are a pattern (as before)");
    }

    // ── Saving and loading ───────────────────────────────────────────────

    private static void ModeJson()
    {
        Case("mode in JSON");
        Check(ChatRuleMatching.ToJson(ChatMatchMode.Regex) == null, "Regex writes no mode");
        foreach (ChatMatchMode mode in Enum.GetValues<ChatMatchMode>())
            Check(ChatRuleMatching.Parse(ChatRuleMatching.ToJson(mode)) == mode, $"{mode} round-trips");
        Check(ChatRuleMatching.Parse(null) == ChatMatchMode.Regex, "absent: Regex");
        Check(ChatRuleMatching.Parse("") == ChatMatchMode.Regex, "empty: Regex");
        Check(ChatRuleMatching.Parse("CONTAINS") == ChatMatchMode.Contains, "any case");
        Check(ChatRuleMatching.Parse("StartsWith") == ChatMatchMode.StartsWith, "StartsWith (enum name)");
        Check(ChatRuleMatching.Parse("EndsWith") == ChatMatchMode.EndsWith, "EndsWith (enum name)");
        Check(ChatRuleMatching.Parse("fuzzy") == ChatMatchMode.Regex, "unknown: Regex");
        Check(ChatRuleMatching.Labels.Length == Enum.GetValues<ChatMatchMode>().Length, "a label per mode");
    }

    private const string OldFile = """
        {"version":2,"fontSize":10,"backgroundAlpha":242,"autoScroll":true,"suppressChat":false,"logEnabled":false,
         "ctrlGatedClickThrough":false,"showTimestamps":true,"mentionHighlight":true,"mentionSound":false,
         "customTabs":["Junk"],
         "filters":[{"pattern":"In portal Space","tab":"Junk","enabled":true,"action":"move"},
                    {"pattern":"^You evaded ","tab":"","enabled":false,"action":"hide","when":"type:21"},
                    {"pattern":"fizzled","tab":"","enabled":true,"action":"color","colorMode":"match","color":"#FFD27A"}]}
        """;

    private static void SettingsRoundTrip()
    {
        Case("settings: an old file loads as Regex rules and saves unchanged; plain-text rules round-trip");
        var opts = RynthChatJsonContext.Default.RynthChatSettingsDto;
        RynthChatSettingsDto old = JsonSerializer.Deserialize(OldFile, opts)!;
        Check(old.Filters!.Length == 3, "three rules");
        Check(old.Filters.All(f => f.Mode == null && ChatRuleMatching.Parse(f.Mode) == ChatMatchMode.Regex), "every old rule is Regex");
        string saved = JsonSerializer.Serialize(old, opts);
        Check(!saved.Contains("\"mode\"", StringComparison.Ordinal), "saving old rules writes no mode key");
        string savedAgain = JsonSerializer.Serialize(JsonSerializer.Deserialize(saved, opts)!, opts);
        Check(saved == savedAgain, "load + save is stable");

        // A plain-text rule, as ChatModel.ToDto writes it, and back.
        old.Filters[0].Mode = ChatRuleMatching.ToJson(ChatMatchMode.Contains);
        old.Filters[1].Mode = ChatRuleMatching.ToJson(ChatMatchMode.StartsWith);
        string withModes = JsonSerializer.Serialize(old, opts);
        Check(withModes.Contains("\"mode\":\"contains\"", StringComparison.Ordinal), "Contains saved as mode=contains");
        RynthChatSettingsDto back = JsonSerializer.Deserialize(withModes, opts)!;
        Check(ChatRuleMatching.Parse(back.Filters![0].Mode) == ChatMatchMode.Contains, "Contains loads back");
        Check(ChatRuleMatching.Parse(back.Filters[1].Mode) == ChatMatchMode.StartsWith, "StartsWith loads back");
        Check(ChatRuleMatching.Parse(back.Filters[2].Mode) == ChatMatchMode.Regex, "the untouched rule is still Regex");
        Check(back.Filters[1].When == "type:21" && back.Filters[1].Action == "hide" && !back.Filters[1].Enabled, "other fields kept");

        // Export / Import uses the rule-set DTO: same field.
        var set = new RynthChatRuleSetDto { Format = RynthChatRuleSetDto.FormatName, Rules = back.Filters };
        string exported = JsonSerializer.Serialize(set, RynthChatJsonContext.Default.RynthChatRuleSetDto);
        RynthChatRuleSetDto imported = JsonSerializer.Deserialize(exported, RynthChatJsonContext.Default.RynthChatRuleSetDto)!;
        Check(ChatRuleMatching.Parse(imported.Rules![0].Mode) == ChatMatchMode.Contains, "export / import keeps the mode");
        RynthChatFilterDto[]? bare = JsonSerializer.Deserialize("[{\"pattern\":\"x\"}]", RynthChatJsonContext.Default.RynthChatFilterDtoArray);
        Check(bare != null && ChatRuleMatching.Parse(bare[0].Mode) == ChatMatchMode.Regex, "a bare old rule array imports as Regex");
    }

    // ── Line colours (ChatLineColors, 2026-10-08) ────────────────────────

    private static void LineColorTags()
    {
        Case("line colours: the [Tag] a channel line starts with");
        Check(ChatLineColors.TagOf("[General] Aja says, \"lol\"") == "General", "[General]");
        Check(ChatLineColors.TagOf("[Trade] You say, \"wts\"") == "Trade", "[Trade], your own line");
        Check(ChatLineColors.TagOf("[Celestial Hand] Bob says, \"x\"") == "Celestial Hand", "a spaced tag");
        Check(ChatLineColors.TagOf("[RynthAi] Started") == null, "a Rynth plugin's prefix is no channel");
        Check(ChatLineColors.TagOf("[General]Aja says") == null, "no space after the tag");
        Check(ChatLineColors.TagOf("Bob says, \"[General] hi\"") == null, "a tag later in the line");
        Check(ChatLineColors.TagOf("[] x") == null && ChatLineColors.TagOf("[ ] x") == null, "an empty tag");
        Check(ChatLineColors.TagOf("[General") == null && ChatLineColors.TagOf("[General]") == null, "unclosed / nothing after it");
        Check(ChatLineColors.TagOf("[" + new string('a', 40) + "] x") != null, "40 characters");
        Check(ChatLineColors.TagOf("[" + new string('a', 41) + "] x") == null, "41 characters: too long");
        Check(ChatLineColors.TagOf("[[x]] y") == null, "nested brackets");
        Check(ChatLineColors.TagOf("") == null && ChatLineColors.TagOf(null) == null, "empty / null");
    }

    private static void LineColorKinds()
    {
        Case("line colours: which kind a line is");
        static string? K(int type, string text, string chan = "Chat") => ChatLineColors.KindOf(type, chan, text);
        Check(K(0x02, "Bob says, \"hi\"") == ChatLineColors.Say, "say (0x02)");
        Check(K(0x03, "Bob tells you, \"hi\"") == ChatLineColors.Tell, "tell to you (0x03)");
        Check(K(0x04, "You tell Bob, \"hi\"") == ChatLineColors.TellOut, "tell you send (0x04)");
        Check(K(0x12, "Bob says to your allegiance, \"hi\"") == ChatLineColors.Allegiance, "allegiance (0x12)");
        Check(K(0x12, "[Allegiance] Bob says, \"hi\"") == ChatLineColors.Allegiance, "[Allegiance] typed 0x12");
        Check(K(0x08, "[Allegiance] Bob says, \"hi\"", "Channels") == ChatLineColors.Allegiance, "[Allegiance] typed 0x08");
        Check(K(0x13, "Bob says to your fellowship, \"hi\"") == ChatLineColors.Fellowship, "fellowship (0x13)");
        Check(K(0x0C, "Bob dances") == ChatLineColors.Emote && K(0x0A, "Bob bows") == ChatLineColors.Emote, "emotes (0x0C, 0x0A)");
        Check(K(0x08, "[General] Aja says, \"lol\"", "Channels") == "chan:General", "/cg: [General]");
        Check(K(0x09, "[General] You say, \"lol\"", "Channels") == "chan:General", "your own /cg line (0x09)");
        Check(K(0x08, "[general] Aja says, \"lol\"", "Channels") == "chan:General", "the listed spelling whatever the case");
        Check(K(0x08, "[Trade] x says", "Channels") == "chan:Trade", "[Trade]");
        Check(K(0x08, "[LFG] x says", "Channels") == "chan:LFG", "[LFG]");
        Check(K(0x08, "[Roleplay] x says", "Channels") == "chan:Roleplay", "[Roleplay]");
        Check(K(0x08, "[Society] x says", "Channels") == "chan:Society", "[Society]");
        Check(K(0x08, "[Radiant Blood] x says", "Channels") == "chan:Society", "a society's own room: Society");
        Check(K(0x08, "[Olthoi] x says", "Channels") == "chan:Olthoi", "[Olthoi]");
        Check(K(0x08, "[Wiki] x says", "Channels") == "chan:Wiki", "any other tag: its own channel");
        Check(ChatLineColors.IsOtherChannel("chan:Wiki") && !ChatLineColors.IsOtherChannel("chan:General")
            && !ChatLineColors.IsOtherChannel("say") && !ChatLineColors.IsOtherChannel(null), "IsOtherChannel");
        Check(K(0x08, "Bob says on the General channel", "Channels") == null, "an untagged channel line: the tab's colour");
        Check(K(0x05, "[General] looks like a tag", "System") == null, "a system line with a tag is no channel line");
        Check(K(0x16, "You hit the drudge") == null && K(0x00, "Welcome", "System") == null, "combat / broadcast: no kind");
        Check(K(-1, "[General] Aja says", "Channels") == "chan:General", "RynthChat before 0.2.0 (no type), Channels tab");
        Check(K(-1, "[General] Aja says", "System") == null, "no type, not a chat tab");
        Check(K(0x02, "[RynthAi] hi") == ChatLineColors.Say, "say keeps its kind whatever the text");

        ChatLineColors.NoteKind("chan:Wiki");
        ChatLineColors.NoteKind("chan:wiki");
        ChatLineColors.NoteKind("chan:General");
        ChatLineColors.NoteKind("say");
        ChatLineColors.NoteKind(null);
        Check(ChatLineColors.SeenTags.Length == 1 && ChatLineColors.SeenTags[0] == "Wiki", "only unlisted tags are remembered, once");
    }

    /// <summary>ChatModel.ChannelArgb before 2026-10-08, copied here.</summary>
    private static uint OldChannelArgb(string chan) => chan switch
    {
        "Chat" => 0xFFE0E0E0, "Channels" => 0xFF7AB8F5, "System" => 0xFF8CA6BF,
        "Combat" => 0xFFD93333, "Rynth" => 0xFFE6B450,
        "Items" => 0xFF9FD8C8,   // the Items tab, new 2026-10-09 (item info used to be in Rynth)
        _ => 0xFFAAAAAA,
    };

    private static readonly string[] AllTabsAndOdd = { "Chat", "Channels", "System", "Combat", "Rynth", "Items", "Other", "Junk", "" };

    private static void LineColorDefaultsUnchanged()
    {
        Case("line colours: with nothing set and Retail colours off every line looks as before");
        ChatLineColors.ResetAll();
        ChatLineColors.RetailColors = false;
        Check(!ChatLineColors.AnySet && ChatLineColors.ToJson() == null, "nothing set");
        var kinds = ChatLineColors.ChatKinds.Concat(ChatLineColors.ChannelKinds).Select(k => k.Key)
            .Append("chan:Wiki").Append(null).ToArray();
        foreach (string tab in AllTabsAndOdd)
        {
            Check(ChatLineColors.TabArgb(tab) == OldChannelArgb(tab), $"tab '{tab}'");
            foreach (string? kind in kinds)
                Check(ChatLineColors.Resolve(null, kind, tab) == OldChannelArgb(tab), $"kind {kind ?? "none"} in '{tab}'");
            Check(ChatLineColors.Resolve(0xFF123456, "say", tab) == 0xFF123456, $"a rule's colour in '{tab}'");
        }
        foreach (ChatColorKind k in ChatLineColors.ChatKinds.Concat(ChatLineColors.ChannelKinds))
            Check(ChatLineColors.EffectiveArgb(k) == OldChannelArgb(k.Tab), $"{k.Label} shows its tab's colour");
        foreach (ChatColorKind k in ChatLineColors.TabKinds)
            Check(ChatLineColors.EffectiveArgb(k) == OldChannelArgb(k.Tab), $"tab {k.Label} shows its built-in colour");
        foreach (string tab in AllTabsAndOdd)
            Check(ChatLineColors.Resolve(null, null, tab, 0x07) == OldChannelArgb(tab), $"a magic line in '{tab}' with Retail colours off");
        ChatLineColors.RetailColors = true;
    }

    private static void LineColorRetail()
    {
        Case("line colours: Retail colours give each kind of line AC's own colour");
        ChatLineColors.ResetAll();
        Check(ChatLineColors.RetailColors, "on by default");
        const uint Magic = 0xFF3FBFFF, Combat = 0xFFFF3F3F, TellIn = 0xFFFFFF3F, White = 0xFFFFFFFF, Purple = 0xFFFF7FFF;
        Check(ChatLineColors.Resolve(null, null, "Combat", 0x07) == Magic, "magic (0x07): light blue");
        Check(ChatLineColors.Resolve(null, null, "Combat", 0x11) == Magic, "spellcasting (0x11): light blue");
        Check(ChatLineColors.Resolve(null, null, "Combat", 0x06) == Combat, "combat (0x06): red");
        Check(ChatLineColors.Resolve(null, null, "Combat", 0x15) == Combat, "combat enemy (0x15): red");
        Check(ChatLineColors.Resolve(null, null, "Combat", 0x16) == 0xFFF57572, "combat self (0x16): light red");
        Check(ChatLineColors.Resolve(null, "tell", "Chat", 0x03) == TellIn, "a tell: yellow");
        Check(ChatLineColors.Resolve(null, "say", "Chat", 0x02) == White, "speech: white");
        Check(ChatLineColors.Resolve(null, null, "System", 0x05) == Purple, "system: purple");
        Check(ChatLineColors.Resolve(null, "allegiance", "Chat", 0x12) == 0xFFEE921E, "allegiance: orange");
        Check(ChatLineColors.RetailArgb(0x21) != null && ChatLineColors.RetailArgb(0x22) == null && ChatLineColors.RetailArgb(-1) == null,
            "the table covers 0x00-0x21");
        Check(ChatLineColors.Resolve(null, null, "Combat", -1) == OldChannelArgb("Combat"), "an unknown type: the tab's colour");
        Check(ChatLineColors.Resolve(null, null, "Other", 0x40) == OldChannelArgb("Other"), "a type past the table: the tab's colour");
        Check(ChatLineColors.Resolve(null, null, "Rynth", 0x05) == OldChannelArgb("Rynth"), "a Rynth line keeps its amber");
        Check(ChatLineColors.Resolve(null, null, "Items", 0x02) == OldChannelArgb("Items"), "an Items line keeps its teal");

        ChatColorKind say = ChatLineColors.ChatKinds.First(k => k.Key == "say");
        Check(ChatLineColors.EffectiveArgb(say) == White, "the settings show an unset Say as AC's white");
        ChatColorKind general = ChatLineColors.ChannelKinds.First(k => k.Key == "chan:General");
        Check(ChatLineColors.EffectiveArgb(general) == 0xFFFF9696, "and an unset channel as AC's channel pink");

        const uint Mine = 0xFF102030, TabMine = 0xFF405060, Rule = 0xFF708090;
        ChatLineColors.Set("tell", Mine);
        Check(ChatLineColors.Resolve(null, "tell", "Chat", 0x03) == Mine, "a kind colour the user set wins");
        ChatLineColors.Set("tab:Combat", TabMine);
        Check(ChatLineColors.Resolve(null, null, "Combat", 0x07) == TabMine, "a tab colour the user set wins over AC's");
        Check(ChatLineColors.EffectiveArgb(ChatLineColors.ChatKinds.First(k => k.Key == "emote")) == 0xFFD2D2C8, "other tabs' kinds still AC's (emote grey)");
        Check(ChatLineColors.Resolve(Rule, "tell", "Chat", 0x03) == Rule, "a rule's colour wins over everything");
        ChatLineColors.ResetAll();
    }

    private static void LineColorPrecedence()
    {
        Case("line colours: rule, then kind, then tab, then built-in");
        ChatLineColors.ResetAll();
        const uint Red = 0xFFFF0000, Green = 0xFF00FF00, Blue = 0xFF0000FF, Rule = 0xFFABCDEF;
        ChatLineColors.Set("fellowship", Red);
        ChatLineColors.Set("chan:General", Green);
        Check(ChatLineColors.Resolve(null, "fellowship", "Chat") == Red, "fellowship set: red");
        Check(ChatLineColors.Resolve(Rule, "fellowship", "Chat") == Rule, "a rule's colour still wins");
        Check(ChatLineColors.Resolve(null, "say", "Chat") == OldChannelArgb("Chat"), "say not set: the Chat colour");
        Check(ChatLineColors.Resolve(null, "chan:General", "Channels") == Green, "/cg lines: green");
        Check(ChatLineColors.Resolve(null, "chan:Trade", "Channels") == OldChannelArgb("Channels"), "Trade not set: the Channels colour");
        Check(ChatLineColors.Resolve(null, ChatLineColors.KindOf(0x08, "Channels", "[general] Aja says"), "Channels") == Green,
            "a line's kind finds its colour (any case)");

        ChatLineColors.Set("tab:Chat", Blue);
        Check(ChatLineColors.Resolve(null, "say", "Chat") == Blue, "Chat tab set: unset kinds in it follow");
        Check(ChatLineColors.Resolve(null, null, "Chat") == Blue, "a kindless Chat line follows");
        Check(ChatLineColors.Resolve(null, "fellowship", "Chat") == Red, "a kind's own colour beats its tab's");
        Check(ChatLineColors.TabArgb("Combat") == OldChannelArgb("Combat"), "other tabs untouched");
        Check(ChatLineColors.EffectiveArgb(ChatLineColors.ChatKinds[0]) == Blue, "Say shows the Chat tab's colour");
        Check(ChatLineColors.EffectiveArgb(ChatLineColors.TabKinds[0]) == Blue, "the Chat tab shows its colour");

        ChatLineColors.Reset("fellowship");
        Check(ChatLineColors.Resolve(null, "fellowship", "Chat") == Blue, "fellowship reset: the Chat tab's colour");
        ChatLineColors.Set("FELLOWSHIP", 0x00112233);
        Check(ChatLineColors.Get("fellowship") == 0xFF112233, "keys any case; colours made opaque");
        ChatLineColors.ResetAll();
        Check(!ChatLineColors.AnySet && ChatLineColors.Resolve(null, "fellowship", "Chat") == OldChannelArgb("Chat"), "reset all: as before");
    }

    private static void LineColorJson()
    {
        Case("line colours: lineColors in rynthchat_settings.json");
        var opts = RynthChatJsonContext.Default.RynthChatSettingsDto;
        RynthChatSettingsDto old = JsonSerializer.Deserialize(OldFile, opts)!;
        Check(old.LineColors == null, "an older file has none");
        ChatLineColors.Set("say", 0xFF010203);
        ChatLineColors.Load(old.LineColors);
        Check(!ChatLineColors.AnySet, "loading it leaves nothing set");
        old.LineColors = ChatLineColors.ToJson();
        Check(!JsonSerializer.Serialize(old, opts).Contains("lineColors", StringComparison.Ordinal), "and saving it writes no lineColors");

        ChatLineColors.Load(new Dictionary<string, string>
        {
            ["say"] = "#FF0000", ["Chan:general"] = "#00FF00", ["tab:chat"] = "0000FF", ["chan:Wiki"] = "#FFFFFF",
            ["bogus"] = "#123456", ["tab:All"] = "#111111", ["tab:Junk"] = "#111111", ["chan:[x]"] = "#222222",
            ["chan:"] = "#333333", ["fellowship"] = "not a colour", ["emote"] = "",
        });
        Check(ChatLineColors.Get("say") == 0xFFFF0000, "say loads");
        Check(ChatLineColors.Get("chan:General") == 0xFF00FF00, "a channel key in any case loads");
        Check(ChatLineColors.Get("tab:Chat") == 0xFF0000FF, "a tab loads");
        Check(ChatLineColors.Get("chan:Wiki") == 0xFFFFFFFF, "another channel loads");
        Check(ChatLineColors.SetKeys.Count == 4, "bad keys and colours are skipped");

        Dictionary<string, string> json = ChatLineColors.ToJson()!;
        Check(json.ContainsKey("chan:General") && json.ContainsKey("tab:Chat") && json["say"] == "#FF0000", "saved in the listed spelling");
        old.LineColors = json;
        string saved = JsonSerializer.Serialize(old, opts);
        Check(saved.Contains("\"lineColors\":{", StringComparison.Ordinal), "written as lineColors");
        RynthChatSettingsDto back = JsonSerializer.Deserialize(saved, opts)!;
        Check(JsonSerializer.Serialize(back, opts) == saved, "load + save is stable");
        Check(back.Filters!.Length == 3 && back.CustomTabs![0] == "Junk", "the rest of the file is kept");
        ChatLineColors.Load(back.LineColors);
        Check(ChatLineColors.Resolve(null, "chan:General", "Channels") == 0xFF00FF00, "a colour survives the round trip");
        ChatLineColors.Load(null);
        Check(!ChatLineColors.AnySet, "loading a file without it clears them");
    }

    // ── Highlight line (2026-11-01): a band behind the whole line ──────────
    // ChatColorMode.Highlight -> ChatRoute.HighlightArgb (the rule's colour); the face draws
    // ChatColorModes.BandArgb of it behind all the line's rows. The first matching Highlight
    // rule wins the band, on its own: Line and Match rules still colour the text.

    private const uint HlA = 0xFF3366FF, HlB = 0xFFFF6633, LineC = 0xFF00FF00, MatchC = 0xFFFF00FF;

    private static ChatFilterRule ColourRule(string pattern, ChatColorMode mode, uint argb,
        ChatRuleAction action = ChatRuleAction.Color, string when = "", string tab = "")
    {
        ChatFilterRule r = Rule(pattern, action, when, tab);
        r.ColorMode = mode;
        r.ColorArgb = argb;
        return r;
    }

    private static ChatRoute Route(string text, string channel, int type, out List<ChatRouter.Step> trace, params ChatFilterRule[] rules)
    {
        trace = new List<ChatRouter.Step>();
        var router = RetailRouter(rules);
        ChatRoute traced = router.Evaluate(text, channel, type, trace);
        ChatRoute quiet = router.Evaluate(text, channel, type, null);
        // The pump routes without a trace, the test box with one: they must agree.
        Check(traced.HighlightArgb == quiet.HighlightArgb && traced.LineArgb == quiet.LineArgb
            && traced.Hidden == quiet.Hidden && traced.InHome == quiet.InHome
            && (traced.Spans?.Length ?? 0) == (quiet.Spans?.Length ?? 0), $"'{text}': traced and untraced routes agree");
        return traced;
    }

    private static bool Says(List<ChatRouter.Step> trace, int rule, string what) =>
        trace.Any(s => s.Rule == rule && s.What.Contains(what, StringComparison.Ordinal));

    private static void HighlightRouting()
    {
        Case("highlight: the route carries the rule's colour");
        try
        {
            ResetPresets();
            ChatRoute r = Route("Bob tells you, \"hi\"", "Chat", 3, out var trace, ColourRule("tells you", ChatColorMode.Highlight, HlA));
            Check(r.HighlightArgb == HlA, "a matching Highlight rule sets HighlightArgb to its colour");
            Check(r.LineArgb == null && r.Spans == null, "...and leaves the text colour alone");
            Check(!r.Hidden && r.InHome && r.Tabs == null, "...and the routing alone");
            Check(Says(trace, 0, "highlights the line"), "the test box says it highlights the line");

            ChatRoute none = Route("Bob says, \"hi\"", "Chat", 2, out var t2, ColourRule("tells you", ChatColorMode.Highlight, HlA));
            Check(ReferenceEquals(none, ChatRoute.Default), "no match: the shared default route (no allocation)");
            Check(t2.Count == 0, "...and nothing in the test box");

            var off = ColourRule("tells you", ChatColorMode.Highlight, HlA);
            off.Enabled = false;
            Check(Route("Bob tells you, \"hi\"", "Chat", 3, out _, off).HighlightArgb == null, "a rule that is off doesn't highlight");

            var combatOnly = ColourRule("evaded", ChatColorMode.Highlight, HlA, when: "chan:Combat");
            Check(Route("You evaded Tusker Guard!", "Combat", 6, out _, combatOnly).HighlightArgb == HlA, "When chan:Combat: a Combat line is highlighted");
            Check(Route("Bob says, \"evaded\"", "Chat", 2, out _, combatOnly).HighlightArgb == null, "...a Chat line isn't");

            ChatRoute moved = Route("Bob tells you, \"hi\"", "Chat", 3, out var t3,
                ColourRule("tells you", ChatColorMode.Highlight, HlA, ChatRuleAction.Move, tab: "Tells"));
            Check(moved.HighlightArgb == HlA && !moved.InHome && moved.Tabs is ["Tells"], "a Move rule can highlight: moved and highlighted");
            Check(Says(t3, 0, "moves it to Tells") && Says(t3, 0, "highlights the line"), "...the test box says both");

            ChatRoute hidden = Route("spam spam", "Chat", 2, out _,
                Rule("spam", ChatRuleAction.Hide), ColourRule("spam", ChatColorMode.Highlight, HlA));
            Check(hidden.Hidden && hidden.HighlightArgb == null, "a Hide rule above: hidden, nothing to highlight");

            Check(!ChatRetailVerdict.IsHidden(RetailRouter(ColourRule("spam", ChatColorMode.Highlight, HlA)), "buy spam now", 3),
                "a Highlight rule never hides a line from the retail chat");

            var mentionRouter = new ChatRouter(new[] { ColourRule("Tester", ChatColorMode.Highlight, HlA) },
                new Regex(@"\bTester\b", RegexOptions.IgnoreCase), self: "", countHits: false);
            ChatRoute both = mentionRouter.Evaluate("Bob says, \"Tester come here\"", "Chat", 2, null);
            Check(both.Mention && both.HighlightArgb == HlA && both.Spans is { Length: 1 } && both.Spans[0].Argb == ChatRouter.MentionArgb,
                "a mention keeps its tint and name colour on a highlighted line");
        }
        finally { ResetPresets(); }
    }

    private static void HighlightPrecedence()
    {
        Case("highlight: the first matching Highlight rule wins; Line and Match still colour the text");
        try
        {
            ResetPresets();
            const string line = "Critical hit! Olthoi Swarm Nymph nicks your head";
            ChatRoute r = Route(line, "Combat", 6, out var trace,
                ColourRule("Critical", ChatColorMode.Highlight, HlA), ColourRule("Olthoi", ChatColorMode.Highlight, HlB));
            Check(r.HighlightArgb == HlA, "two Highlight rules: the higher one's colour");
            Check(Says(trace, 1, "(highlight already set above)"), "the test box says the lower one was beaten");

            ChatRoute nomatch = Route(line, "Combat", 6, out _,
                ColourRule("nothing here", ChatColorMode.Highlight, HlA), ColourRule("Olthoi", ChatColorMode.Highlight, HlB));
            Check(nomatch.HighlightArgb == HlB, "the higher one doesn't match: the lower one's colour");

            ChatRoute lineFirst = Route(line, "Combat", 6, out _,
                ColourRule("Critical", ChatColorMode.Line, LineC), ColourRule("Olthoi", ChatColorMode.Highlight, HlA));
            Check(lineFirst.LineArgb == LineC && lineFirst.HighlightArgb == HlA, "Colour line above Highlight: both apply");
            ChatRoute hlFirst = Route(line, "Combat", 6, out _,
                ColourRule("Critical", ChatColorMode.Highlight, HlA), ColourRule("Olthoi", ChatColorMode.Line, LineC));
            Check(hlFirst.LineArgb == LineC && hlFirst.HighlightArgb == HlA, "Highlight above Colour line: both apply");

            ChatRoute lines = Route(line, "Combat", 6, out _,
                ColourRule("Critical", ChatColorMode.Line, LineC), ColourRule("Olthoi", ChatColorMode.Line, HlB),
                ColourRule("nicks", ChatColorMode.Highlight, HlA));
            Check(lines.LineArgb == LineC && lines.HighlightArgb == HlA, "Colour line unchanged: still the first one wins");

            ChatRoute match = Route(line, "Combat", 6, out _,
                ColourRule("Olthoi", ChatColorMode.Match, MatchC), ColourRule("Critical", ChatColorMode.Highlight, HlA));
            Check(match.HighlightArgb == HlA && match.Spans is { Length: 1 } && match.Spans[0].Argb == MatchC
                && line.Substring(match.Spans[0].Start, match.Spans[0].Length) == "Olthoi", "Colour match and Highlight: the match coloured on the band");
            Check(match.LineArgb == null, "...the rest of the text keeps its normal colour");

            // A Highlight rule takes no part in routing order: a Move below it still decides.
            ChatRoute route = Route(line, "Combat", 6, out _,
                ColourRule("Critical", ChatColorMode.Highlight, HlA), Rule("Olthoi", ChatRuleAction.Move, tab: "Crits"));
            Check(route.HighlightArgb == HlA && route.Tabs is ["Crits"] && !route.InHome, "a Move below a Highlight still moves it");
        }
        finally { ResetPresets(); }
    }

    private static void HighlightBand()
    {
        Case("highlight: the band is the swatch colour at 40%");
        Check(ChatColorModes.BandAlpha == 0x66, "40% (0x66) for an opaque colour");
        Check(ChatColorModes.BandArgb(0xFF3366FF) == 0x663366FF, "an opaque swatch: same RGB, alpha 0x66");
        Check(ChatColorModes.BandArgb(0xFFFFFFFF) == 0x66FFFFFF, "white: alpha 0x66");
        Check(ChatColorModes.BandArgb(0x80112233) == 0x33112233, "an imported translucent colour: fainter still (0x80 -> 0x33)");
        Check(ChatColorModes.BandArgb(0x01112233) >> 24 == 0, "a nearly clear colour: nearly no band");
        Check((ChatColorModes.BandArgb(ChatFilterRule.DefaultColor) & 0xFFFFFF) == (ChatFilterRule.DefaultColor & 0xFFFFFF), "the default amber keeps its RGB");
    }

    private static void HighlightJson()
    {
        Case("highlight: colorMode \"highlight\" in the settings file and Export / Import");
        Check((int)ChatColorMode.None == 0 && (int)ChatColorMode.Line == 1 && (int)ChatColorMode.Match == 2 && (int)ChatColorMode.Highlight == 3,
            "the old values keep their numbers (the dropdown is indexed by them)");
        foreach (ChatColorMode m in Enum.GetValues<ChatColorMode>())
            Check(ChatColorModes.Parse(ChatColorModes.ToJson(m)) == m, $"{m} round-trips");
        Check(ChatColorModes.ToJson(ChatColorMode.None) == null, "None writes no colorMode (as before)");
        Check(ChatColorModes.ToJson(ChatColorMode.Line) == "line" && ChatColorModes.ToJson(ChatColorMode.Match) == "match", "line / match written as before");
        Check(ChatColorModes.ToJson(ChatColorMode.Highlight) == "highlight", "Highlight writes \"highlight\"");
        Check(ChatColorModes.Parse("HIGHLIGHT") == ChatColorMode.Highlight && ChatColorModes.Parse(" highlight ") == ChatColorMode.Highlight, "any case, spaces");
        Check(ChatColorModes.Parse("text") == ChatColorMode.Match, "\"text\" still reads as match");
        Check(ChatColorModes.Parse(null) == ChatColorMode.None && ChatColorModes.Parse("") == ChatColorMode.None
            && ChatColorModes.Parse("sparkle") == ChatColorMode.None, "absent / unknown: None");

        var opts = RynthChatJsonContext.Default.RynthChatSettingsDto;
        RynthChatSettingsDto old = JsonSerializer.Deserialize(OldFile, opts)!;
        foreach (RynthChatFilterDto f in old.Filters!)
            Check(ChatColorModes.ToJson(ChatColorModes.Parse(f.ColorMode)) == f.ColorMode, $"an old rule's colorMode ({f.ColorMode ?? "none"}) is written back unchanged");

        old.Filters[2].ColorMode = ChatColorModes.ToJson(ChatColorMode.Highlight);
        old.Filters[2].Color = ChatLineColors.FormatColor(HlA);
        string saved = JsonSerializer.Serialize(old, opts);
        Check(saved.Contains("\"colorMode\":\"highlight\"", StringComparison.Ordinal), "saved as colorMode:highlight");
        RynthChatSettingsDto back = JsonSerializer.Deserialize(saved, opts)!;
        Check(ChatColorModes.Parse(back.Filters![2].ColorMode) == ChatColorMode.Highlight, "loads back as Highlight");
        Check(ChatLineColors.ParseColor(back.Filters[2].Color) == HlA, "...with its colour");
        Check(JsonSerializer.Serialize(back, opts) == saved, "load + save is stable");

        var set = new RynthChatRuleSetDto { Format = RynthChatRuleSetDto.FormatName, Rules = back.Filters, Tabs = back.CustomTabs };
        string exported = JsonSerializer.Serialize(set, RynthChatJsonContext.Default.RynthChatRuleSetDto);
        RynthChatRuleSetDto imported = JsonSerializer.Deserialize(exported, RynthChatJsonContext.Default.RynthChatRuleSetDto)!;
        Check(ChatColorModes.Parse(imported.Rules![2].ColorMode) == ChatColorMode.Highlight
            && ChatLineColors.ParseColor(imported.Rules[2].Color) == HlA, "Export / Import keeps the highlight and its colour");
        RynthChatFilterDto[]? bare = JsonSerializer.Deserialize("[{\"pattern\":\"x\",\"colorMode\":\"highlight\",\"color\":\"#3366FF\"}]",
            RynthChatJsonContext.Default.RynthChatFilterDtoArray);
        Check(bare != null && ChatColorModes.Parse(bare[0].ColorMode) == ChatColorMode.Highlight, "a bare rule array imports the highlight");
    }

    /// <summary>
    /// The colorMode reading of RynthCore release/2026.10.31.4 (ChatModel.FromDto), copied as it was:
    /// what an older client does with a file that has "highlight" in it.
    /// </summary>
    private static ChatColorMode OldReader(string? colorMode) => (colorMode ?? "").ToLowerInvariant() switch
    {
        "line" => ChatColorMode.Line,
        "match" or "text" => ChatColorMode.Match,
        _ => ChatColorMode.None,
    };

    /// <summary>The colorMode writing of release/2026.10.31.4 (ChatModel.ToDto), copied as it was.</summary>
    private static string? OldWriter(ChatColorMode m) => m switch { ChatColorMode.Line => "line", ChatColorMode.Match => "match", _ => null };

    private static void HighlightOldReader()
    {
        Case("highlight: an older RynthCore reads a file with \"highlight\" without breaking");
        const string newFile = """
            {"version":2,"customTabs":["Junk"],
             "filters":[{"pattern":"tells you","tab":"","enabled":true,"action":"color","colorMode":"highlight","color":"#3366FF"},
                        {"pattern":"In portal Space","tab":"Junk","enabled":true,"action":"move","colorMode":"highlight","color":"#FF6633"},
                        {"pattern":"fizzled","tab":"","enabled":true,"action":"color","colorMode":"match","color":"#FFD27A"}]}
            """;
        RynthChatSettingsDto? dto = null;
        try { dto = JsonSerializer.Deserialize(newFile, RynthChatJsonContext.Default.RynthChatSettingsDto); }
        catch (JsonException) { }
        Check(dto?.Filters is { Length: 3 }, "the file reads (colorMode is a string: no JSON error)");
        if (dto?.Filters is not { Length: 3 } f) return;
        Check(OldReader(f[0].ColorMode) == ChatColorMode.None, "an old reader takes \"highlight\" as no colour");
        Check(ChatLineColors.ParseColor(f[0].Color) == HlA, "...the rule's colour is still read");
        Check(f[1].Action == "move" && f[1].Tab == "Junk" && OldReader(f[1].ColorMode) == ChatColorMode.None, "...a highlighting Move rule still moves");
        Check(OldReader(f[2].ColorMode) == ChatColorMode.Match, "...the other rules are unaffected");
        // An old client that saves again: the mode is dropped, the colour kept (ToDto writes a
        // non-default colour even with no colour mode), so turning it back on finds the colour.
        Check(OldWriter(OldReader(f[0].ColorMode)) == null, "an old client's save drops the highlight (no colorMode)");
        Check(ChatColorModes.Parse(OldWriter(OldReader(f[2].ColorMode))) == ChatColorMode.Match, "...and keeps the other modes");
        // And the new reader reads what an old client writes exactly as the old reader did.
        foreach (string? s in new[] { null, "", "line", "match", "text", "LINE", "bogus" })
            Check(ChatColorModes.Parse(s) == OldReader(s), $"the new reader agrees with the old on '{s ?? "null"}'");
    }

    private static void WhenParsing()
    {
        Case("When: the same reading as before");
        Check(ChatRuleMatching.ParseWhen("") == (null, -1), "empty: any");
        Check(ChatRuleMatching.ParseWhen(null) == (null, -1), "null: any");
        Check(ChatRuleMatching.ParseWhen(" chan:Combat ") == ("Combat", -1), "a channel");
        Check(ChatRuleMatching.ParseWhen("CHAN:Combat") == ("Combat", -1), "chan: any case");
        Check(ChatRuleMatching.ParseWhen("type:21") == (null, 21), "a type");
        Check(ChatRuleMatching.ParseWhen("type:x") == (null, -1), "a bad type: any");
        Check(ChatRuleMatching.ParseWhen("type:-1") == (null, -1), "a negative type: any");
        Check(ChatRuleMatching.ParseWhen("chan:") == (null, -1), "chan: with nothing: any");
        Check(ChatRuleMatching.WhenMatches("combat", -1, "Combat", 3), "channel any case");
        Check(!ChatRuleMatching.WhenMatches("Combat", -1, "Chat", 21), "other channel");
        Check(ChatRuleMatching.WhenMatches(null, 21, "Combat", 21) && !ChatRuleMatching.WhenMatches(null, 21, "Combat", 22), "type");
        Check(ChatRuleMatching.WhenMatches(null, -1, "Chat", 2), "any");
    }

    // ── Retail chat verdict (2026-10-19; idea from Silentkelpie, rynth PR 6) ──
    // A line the rules or standard filters hide also stays out of AC's own chat window.
    // ChatRetailVerdict is the pure part of ChatModel.ShouldHideInRetail.

    private static ChatRouter RetailRouter(params ChatFilterRule[] rules) =>
        new(rules, mention: null, self: "", countHits: false);

    private static ChatFilterRule Rule(string pattern, ChatRuleAction action, string when = "", string tab = "")
    {
        var r = new ChatFilterRule { Pattern = pattern, Action = action, Tab = tab };
        r.When = when;
        r.Recompile();
        return r;
    }

    private static void ResetPresets()
    {
        foreach (var p in RynthCore.Engine.UI.Panels.RynthChatPresets.All)
        {
            p.Enabled = false;
            p.Tab = "";
        }
    }

    private static void RetailChannels()
    {
        Case("retail verdict: the channel is the one the RynthChat plugin files the line under");
        (string Line, int Type, string Channel)[] cases =
        {
            ("Bob says, \"hi\"", 2, "Chat"),
            ("Bob tells you, \"hi\"", 3, "Chat"),
            ("[Trade] Bob says, \"WTS\"", 8, "Channels"),
            ("[Trade] Bob says, \"WTS\"", 0x1C, "Channels"),           // ILT / ACECustom deliver channel lines as 0x1C
            ("[General] Bob says, \"hi\"", 3, "Channels"),             // the tag wins over the type
            ("[Celestial Hand] Bob says, \"hi\"", 3, "Channels"),
            ("[general] Bob says, \"hi\"", 3, "Channels"),             // the tag is case-insensitive
            ("[Generals] Bob says, \"hi\"", 3, "Chat"),                // not a tag
            ("You have killed 12 Drudges!", 0, "System"),
            ("You evaded Tusker Guard!", 6, "Combat"),
            ("Your spell fizzled.", 0x11, "Combat"),
            ("[RynthAi] Nav loaded", 0, "Rynth"),
            ("[RynthChat] hello", 3, "Rynth"),                         // plugin output is Rynth whatever type it was written with
            ("[rynthai] lower case", 0, "Rynth"),
            ("[RynthAi Item] Sword of Bob", 3, "Items"),               // item info is checked before the Rynth prefix
            ("[RynthAi Item]no space", 3, "Rynth"),                    // the Items prefix has its trailing space
            ("something", 0x7F, "Other"),
        };
        foreach (var (line, type, chan) in cases)
            Check(ChatRetailVerdict.ChannelFor(line, type) == chan, $"'{line}' type {type} -> {chan} (got {ChatRetailVerdict.ChannelFor(line, type)})");
        Check(ChatRetailVerdict.ChannelFor("", 2) == "Chat" && ChatRetailVerdict.ChannelFor("", 0x7F) == "Other", "an empty line falls back to the type");
        for (int t = 0; t < 40; t++)
            Check(ChatRetailVerdict.ChannelForType(t) == ChatRetailVerdict.ChannelFor("plain", t), $"type {t}: no tag, the type decides");
    }

    private static void RetailStandardFilters()
    {
        Case("retail verdict: ticked standard filters hide their lines, moves and unticked ones don't");
        var presets = RynthCore.Engine.UI.Panels.RynthChatPresets.All;
        try
        {
            ResetPresets();
            ChatRouter none = RetailRouter();
            foreach (var p in presets)
            {
                int type = p.Where switch
                {
                    RynthCore.Engine.UI.Panels.RynthChatPresets.Scope.CombatOnly => 0x11,
                    RynthCore.Engine.UI.Panels.RynthChatPresets.Scope.ChannelsOnly => 0x08,
                    _ => 0x00,
                };
                Check(!ChatRetailVerdict.IsHidden(none, p.Example, type), $"{p.Id}: unticked, '{p.Example}' prints");
                p.Enabled = true;   // hide
                Check(ChatRetailVerdict.IsHidden(none, p.Example, type), $"{p.Id}: ticked, '{p.Example}' stays out of the retail chat");
                p.Tab = "Spam";     // moved to a tab: RynthChat shows it there, AC still prints it
                Check(!ChatRetailVerdict.IsHidden(none, p.Example, type), $"{p.Id}: moved to a tab, '{p.Example}' still prints in AC");
                p.Enabled = false;
                p.Tab = "";
            }

            // The reported case: Kill task progress.
            var kill = RynthCore.Engine.UI.Panels.RynthChatPresets.Get("social.kill_task_progress");
            Check(kill != null, "social.kill_task_progress exists");
            if (kill != null)
            {
                const string killLine = "You have killed 22556 Fanatical Fiuns! You must kill 100000 to complete your task.";
                kill.Enabled = true;
                Check(ChatRetailVerdict.IsHidden(none, killLine, 0x00), "the kill-task line from the report is hidden");
                Check(ChatRetailVerdict.IsHidden(none, killLine, 0x1B), "...whatever system-ish type it arrives with");
                Check(!ChatRetailVerdict.IsHidden(none, "You have killed 3 Drudges! Your task is complete!", 0x00), "other kill lines print");
                // Player speech is never taken by a filter held to non-chat channels.
                Check(!ChatRetailVerdict.IsHidden(none, "Bob says, \"" + killLine + "\"", 0x02), "a player saying it in Local prints");
                Check(!ChatRetailVerdict.IsHidden(none, "Bob tells you, \"" + killLine + "\"", 0x03), "a player telling it prints");
            }
        }
        finally { ResetPresets(); }
    }

    private static void RetailRules()
    {
        Case("retail verdict: Hide rules hide, Move / Copy / colour rules and off rules don't");
        try
        {
            ResetPresets();
            Check(!ChatRetailVerdict.IsHidden(RetailRouter(), "anything", 3), "no rules: nothing hidden");
            Check(!ChatRetailVerdict.IsHidden(RetailRouter(Rule("spam", ChatRuleAction.Hide)), "fine line", 3), "a line the rule doesn't match prints");
            Check(ChatRetailVerdict.IsHidden(RetailRouter(Rule("spam", ChatRuleAction.Hide)), "buy spam now", 3), "a Hide rule hides it");
            Check(ChatRetailVerdict.IsHidden(RetailRouter(Rule("spam", ChatRuleAction.Hide)), "SPAM", 3), "...case-insensitively");

            var off = Rule("spam", ChatRuleAction.Hide);
            off.Enabled = false;
            Check(!ChatRetailVerdict.IsHidden(RetailRouter(off), "buy spam now", 3), "a rule that is switched off hides nothing");
            Check(!ChatRetailVerdict.IsHidden(RetailRouter(Rule("spam", ChatRuleAction.Move, tab: "Mail")), "buy spam now", 3), "a Move rule prints in AC");
            Check(!ChatRetailVerdict.IsHidden(RetailRouter(Rule("spam", ChatRuleAction.Copy, tab: "Mail")), "buy spam now", 3), "a Copy rule prints in AC");
            var colour = Rule("spam", ChatRuleAction.Color);
            colour.ColorMode = ChatColorMode.Line;
            Check(!ChatRetailVerdict.IsHidden(RetailRouter(colour), "buy spam now", 3), "a colour rule prints in AC");

            // First routing rule decides, as in RynthChat.
            Check(!ChatRetailVerdict.IsHidden(RetailRouter(Rule("spam", ChatRuleAction.Move, tab: "Mail"), Rule("spam", ChatRuleAction.Hide)), "spam", 3),
                "a Move rule ahead of a Hide rule decides: not hidden");
            Check(ChatRetailVerdict.IsHidden(RetailRouter(Rule("spam", ChatRuleAction.Hide), Rule("spam", ChatRuleAction.Move, tab: "Mail")), "spam", 3),
                "a Hide rule ahead of a Move rule decides: hidden");

            // "When" conditions see the channel RynthChat gives the line.
            Check(ChatRetailVerdict.IsHidden(RetailRouter(Rule("Sword", ChatRuleAction.Hide, "chan:Items")), "[RynthAi Item] Sword of Bob", 3), "chan:Items rule hides item info");
            Check(!ChatRetailVerdict.IsHidden(RetailRouter(Rule("Sword", ChatRuleAction.Hide, "chan:Items")), "Bob says, \"Sword\"", 2), "...but not other lines");
            Check(ChatRetailVerdict.IsHidden(RetailRouter(Rule("loaded", ChatRuleAction.Hide, "chan:Rynth")), "[RynthAi] Nav loaded", 0), "chan:Rynth rule hides plugin output");
            Check(ChatRetailVerdict.IsHidden(RetailRouter(Rule("WTS", ChatRuleAction.Hide, "chan:Channels")), "[Trade] Bob says, \"WTS\"", 0x1C), "chan:Channels rule hides a tagged 0x1C channel line");
            Check(ChatRetailVerdict.IsHidden(RetailRouter(Rule("WTS", ChatRuleAction.Hide, "chan:Channels")), "[Trade] Bob says, \"WTS\"", 8), "...and a type 8 one");
            Check(!ChatRetailVerdict.IsHidden(RetailRouter(Rule("WTS", ChatRuleAction.Hide, "chan:Channels")), "Bob tells you, \"WTS\"", 3), "...not a tell");
            Check(ChatRetailVerdict.IsHidden(RetailRouter(Rule("busy", ChatRuleAction.Hide, "type:26")), "You're too busy!", 26), "type:26 rule hides a type 26 line");
            Check(!ChatRetailVerdict.IsHidden(RetailRouter(Rule("busy", ChatRuleAction.Hide, "type:26")), "You're too busy!", 0), "...not another type");

            // A custom rule decides before the standard filters, as in RynthChat: a Move rule keeps a ticked filter from hiding the line.
            var kill = RynthCore.Engine.UI.Panels.RynthChatPresets.Get("social.kill_task_progress")!;
            kill.Enabled = true;
            const string killLine = "You have killed 12 Drudges! You must kill 50 to complete your task.";
            Check(ChatRetailVerdict.IsHidden(RetailRouter(Rule("nothing", ChatRuleAction.Hide)), killLine, 0), "a ticked filter hides when no rule decided first");
            Check(!ChatRetailVerdict.IsHidden(RetailRouter(Rule("killed", ChatRuleAction.Move, tab: "Kills")), killLine, 0), "a Move rule ahead of the ticked filter keeps the line");

            // The retail router must not double count what RynthChat's own pass counts.
            var counted = Rule("spam", ChatRuleAction.Hide);
            RetailRouter(counted).Evaluate("spam", "Chat", 3, null);
            Check(counted.Hits == 0, "a router built with countHits: false leaves Hits alone");
        }
        finally { ResetPresets(); }
    }

    private static void RetailSetting()
    {
        Case("retail verdict: the hideInRetail setting (on unless a file says false)");
        var opts = RynthChatJsonContext.Default.RynthChatSettingsDto;
        RynthChatSettingsDto old = JsonSerializer.Deserialize(OldFile, opts)!;
        Check(old.HideInRetail == null, "a file from before the setting has no value (ChatModel reads that as on)");
        string saved = JsonSerializer.Serialize(old, opts);
        Check(!saved.Contains("hideInRetail", StringComparison.Ordinal), "saving an old file writes no hideInRetail key");

        old.HideInRetail = false;
        string off = JsonSerializer.Serialize(old, opts);
        Check(off.Contains("\"hideInRetail\":false", StringComparison.Ordinal), "off is saved as hideInRetail:false");
        Check(JsonSerializer.Deserialize(off, opts)!.HideInRetail == false, "off loads back");
        old.HideInRetail = true;
        Check(JsonSerializer.Deserialize(JsonSerializer.Serialize(old, opts), opts)!.HideInRetail == true, "on round-trips");
        Check(JsonSerializer.Deserialize("{\"hideInRetail\":null}", opts)!.HideInRetail == null, "null reads as absent");
        Check(JsonSerializer.Deserialize(off, opts)!.Filters!.Length == 3, "the other fields are untouched");
    }

    private static void TabsAcrossSetting()
    {
        Case("tabs across the top: off (the drawer) unless a file says true");
        var opts = RynthChatJsonContext.Default.RynthChatSettingsDto;
        RynthChatSettingsDto old = JsonSerializer.Deserialize(OldFile, opts)!;
        Check(old.TabsAcross == null, "a file from before the setting has no value (ChatModel reads that as the drawer)");
        Check(!JsonSerializer.Serialize(old, opts).Contains("tabsAcross", StringComparison.Ordinal), "saving it writes no tabsAcross key");
        old.TabsAcross = true;
        string on = JsonSerializer.Serialize(old, opts);
        Check(on.Contains("\"tabsAcross\":true", StringComparison.Ordinal), "on is saved as tabsAcross:true");
        Check(JsonSerializer.Deserialize(on, opts)!.TabsAcross == true, "on loads back");
        Check(JsonSerializer.Deserialize(on, opts)!.Filters!.Length == 3, "the other fields are untouched");
    }

    // ── Shared settings reload (2026-10-19; idea from Silentkelpie, rynth PR 6) ──
    // SharedFileWatch: another client rewrote rynthchat_settings.json -> read it; our own saves don't count.

    private static void SharedFileReload()
    {
        Case("shared settings: a client re-reads the file when ANOTHER client saved it");
        string dir = Path.Combine(Path.GetTempPath(), "rynth-sharedwatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "rynthchat_settings.json");
            DateTime t0 = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
            void Write(string text, int secondsAfterT0)
            {
                File.WriteAllText(path, text);
                File.SetLastWriteTimeUtc(path, t0.AddSeconds(secondsAfterT0));
            }

            // No file yet: nothing to read, no error.
            var w = new SharedFileWatch(pollMs: 2000);
            Check(w.Changed(10_000, path) == null, "no file: null");

            // Loaded at start: that timestamp is not a change.
            Write("{\"v\":1}", 0);
            w.Seen(File.GetLastWriteTimeUtc(path));
            Check(w.Changed(20_000, path) == null, "the file we loaded is not a change");

            // Another client saves: read once.
            Write("{\"v\":2}", 5);
            Check(w.Changed(20_500, path) == null, "...but not before the poll interval (2 s) is up");
            Check(w.Changed(22_000, path) == "{\"v\":2}", "another client's save is returned");
            Check(w.Changed(24_000, path) == null, "...once");

            // Our own save (Saved is called just before the swap) is not a change.
            string mine = "{\"v\":3,\"mine\":true}";
            w.Saved(mine);
            Write(mine, 10);
            Check(w.Changed(26_000, path) == null, "this client's own save is not a change");
            Check(w.Changed(28_000, path) == null, "...and stays quiet");

            // Another client saves after ours: read.
            Write("{\"v\":4}", 15);
            Check(w.Changed(30_000, path) == "{\"v\":4}", "another client's later save is returned");
            // It saves the same text as our last save again (a copy of our settings): still ours by text.
            Write(mine, 20);
            Check(w.Changed(32_000, path) == null, "text identical to our last save is treated as ours");

            // The same timestamp again (nothing written): not read.
            Check(w.Changed(34_000, path) == null, "an unchanged timestamp is not read");

            // A file that is being swapped (cannot be read): the change is offered again, not lost.
            Write("{\"v\":5}", 25);
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool threw = false;
                try { w.Changed(36_000, path); }
                catch (IOException) { threw = true; }
                Check(threw, "an unreadable file throws (the caller logs it)");
            }
            Check(w.Changed(38_000, path) == "{\"v\":5}", "the change that could not be read is returned at the next poll");

            // The file is removed: nothing, no throw.
            File.Delete(path);
            Check(w.Changed(40_000, path) == null, "a removed file is null");

            // A watcher that never saw the file (no file at start) reads the first one it finds.
            var late = new SharedFileWatch(pollMs: 2000);
            Write("{\"v\":6}", 30);
            Check(late.Changed(50_000, path) == "{\"v\":6}", "a file that appears after start is read");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── Standard filters ─────────────────────────────────────────────────

    private static void PresetsCatchAndSpare()
    {
        Case("standard filters catch their lines and spare the others");
        foreach (ChatFilterPreset p in ChatFilterPresets.All)
        {
            Check(p.Catches.Length > 0, $"{p.Id}: has example lines");
            foreach (var (text, chan, type) in p.Catches)
                Check(p.Matches(text, chan, type), $"{p.Id}: catches '{text}' ({chan}, type {type})");
            foreach (var (text, chan, type) in p.Spares)
                Check(!p.Matches(text, chan, type), $"{p.Id}: spares '{text}' ({chan}, type {type})");
            // The same rule built the way ChatFilterRule.Recompile builds it.
            Regex? rx = ChatRuleMatching.Compile(p.Mode, p.Pattern, 15, out string? err);
            Check(rx != null && err == null, $"{p.Id}: compiles as a rule");
        }

        // Player speech: no filter that is held to a type or channel may take a say, a tell or an emote's text.
        string[] speech = { "Bob says, \"You evaded Tusker Guard!\"", "Bob tells you, \"Your spell fizzled.\"", "You say, \"You're too busy!\"" };
        foreach (ChatFilterPreset p in ChatFilterPresets.All)
        {
            if (p.When.Length == 0) continue;   // "" presets are checked by their own Spares
            foreach (string s in speech)
            {
                Check(!p.Matches(s, "Chat", 2), $"{p.Id}: never a Local say ('{s}')");
                Check(!p.Matches(s, "Chat", 3), $"{p.Id}: never a tell ('{s}')");
            }
        }
    }

    private static void PresetsWellFormed()
    {
        Case("standard filters are well formed");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChatFilterPreset p in ChatFilterPresets.All)
        {
            Check(ids.Add(p.Id), $"{p.Id}: unique id");
            Check(p.Group.Length > 0 && p.Label.Length > 0 && p.Description.Length > 0, $"{p.Id}: named and described");
            Check(p.Pattern.Trim().Length > 0, $"{p.Id}: has a pattern");
            var (chan, type) = ChatRuleMatching.ParseWhen(p.When);
            Check(p.When.Length == 0 || chan != null || type >= 0, $"{p.Id}: When '{p.When}' is one the rules read");
            Check(p.SameAs(p.Mode, p.Pattern, p.When), $"{p.Id}: SameAs itself");
            Check(!p.SameAs(p.Mode == ChatMatchMode.Regex ? ChatMatchMode.Contains : ChatMatchMode.Regex, p.Pattern, p.When), $"{p.Id}: another mode is another rule");
            Check(!p.SameAs(p.Mode, p.Pattern + "x", p.When), $"{p.Id}: another pattern is another rule");
            Check(ReferenceEquals(ChatFilterPresets.Get(p.Id), p), $"{p.Id}: found by id");
        }
        Check(ChatFilterPresets.Get("no.such") == null, "an unknown id is null");
        Check(ChatFilterPresets.All.Length >= 10, "a useful set");
    }
}

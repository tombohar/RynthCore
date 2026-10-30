using System.Text.Json;
using RynthCore.Engine.UI.Data.Discord;

namespace ChatDiscordTests;

/// <summary>
/// RynthChat's Discord relay, the parts that don't need the engine: what a chat line is, which
/// routes take it, how it reads in Discord, scrollback, settings files, the request body and
/// rate-limit waits, and the DPAPI wrapper. Fake webhook URLs and tokens are built at run time
/// (no credential-shaped literals in the source).
/// </summary>
internal static class Program
{
    private static int _checks, _failed;

    // A well-formed but fake webhook URL / bot token / channel id.
    private static readonly string FakeWebhook =
        "https://discord.com/api/webhooks/" + new string('1', 18) + "/" + new string('x', 68);
    private static readonly string FakeBotToken =
        string.Join(".", new string('A', 24), new string('b', 6), new string('c', 38));
    private static readonly string FakeChannel = new string('7', 19);

    private static int Main()
    {
        Speech();
        Classify();
        Routing();
        Formatting();
        Backlog();
        Packing();
        Validation();
        RequestBody();
        Settings();
        Secrets();
        Dedup();
        Characters();
        Queue();
        FaceText();
        ClientShutdown();   // last: it closes DiscordClient for good

        Console.WriteLine(_failed == 0 && _checks > 0 ? $"PASS ({_checks} checks)" : $"FAIL ({_failed} of {_checks})");
        return _failed == 0 && _checks > 0 ? 0 : 1;
    }

    // ── Speech parsing ───────────────────────────────────────────────────

    private static void Speech()
    {
        Said("Bob tells you, \"hi there\"", 0x03, SpeechChannel.Tell, "Bob", "hi there", own: false);
        Said("You tell Bob, \"yo\"", 0x03, SpeechChannel.Tell, "You", "yo", own: true);
        Said("Bob says on the General channel, \"wts pyreal\"", 0x1B, SpeechChannel.General, "Bob", "wts pyreal", own: false);
        Said("[Trade] Bob says, \"wtb mana stones\"", 0x00, SpeechChannel.Trade, "Bob", "wtb mana stones", own: false);
        Said("Bob says, \"hello\"", 0x02, SpeechChannel.Local, "Bob", "hello", own: false);
        Said("+Bob says to your fellowship, \"pull\"", 0x00, SpeechChannel.Fellowship, "Bob", "pull", own: false);
        Said("Bob says to your covassals, \"gz\"", 0x00, SpeechChannel.Covassals, "Bob", "gz", own: false);
        Said("Bob says to your vassals, \"hi\"", 0x00, SpeechChannel.Vassals, "Bob", "hi", own: false);
        Said("Bob says, \"caf<00e9> time\"", 0x12, SpeechChannel.Allegiance, "Bob", "caf\u00e9 time", own: false);
        Said("Bob says on the LFG channel, \"lf colo\"", 0x00, SpeechChannel.Lfg, "Bob", "lf colo", own: false);

        NotSpeech("You say, \"Zojak Bor\"", ChatSpeech.ChatTypeSpellcasting);   // spell words
        NotSpeech("You have gained 500 experience.", 0x00);
        NotSpeech("Bob says, \"   \"", 0x02);                                   // nothing said
        NotSpeech("", 0x02);
        NotSpeech(null, 0x02);
        NotSpeech("He says \"hi\"", 0x00);                                       // no channel at all

        Check(ChatSpeech.Name(SpeechChannel.Lfg) == "LFG", "LFG is shown upper case");
        Check(ChatSpeech.TryParseName("lfg", out var lfg) && lfg == SpeechChannel.Lfg, "LFG parses back");
        Check(ChatSpeech.TryParseName(" Fellow ", out var fel) && fel == SpeechChannel.Fellowship, "short names parse");
        Check(!ChatSpeech.TryParseName("guild", out _), "unknown names don't parse");
        foreach (var ch in ChatSpeech.All)
            Check(ChatSpeech.TryParseName(ChatSpeech.Name(ch), out var back) && back == ch, $"{ch} survives a save");
        Check(ChatSpeech.Decode("<zzzz> and <0041>") == "<zzzz> and A", "only hex escapes decode");
    }

    private static void Said(string text, int chatType, SpeechChannel ch, string who, string msg, bool own)
    {
        bool ok = ChatSpeech.TryParse(text, chatType, out var c, out string s, out string m, out bool o);
        Check(ok, $"speech: {text}");
        if (!ok) return;
        Check(c == ch, $"{text} -> {ch} (got {c})");
        Check(s == who, $"{text} speaker {who} (got {s})");
        Check(m == msg, $"{text} message \"{msg}\" (got \"{m}\")");
        Check(o == own, $"{text} own={own}");
    }

    private static void NotSpeech(string? text, int chatType)
        => Check(!ChatSpeech.TryParse(text, chatType, out _, out _, out _, out _), $"not speech: {text ?? "(null)"}");

    // ── Line classification ──────────────────────────────────────────────

    private static void Classify()
    {
        Check(ChatDiscordLogic.TryClassify("You tell Bob, \"omw\"", 0x03, "Alice", out var mine), "own tell classifies");
        Check(mine.Own && mine.Speaker == "Alice" && mine.Speech == SpeechChannel.Tell, "own lines are shown as the character");
        Check(ChatDiscordLogic.TryClassify("You tell Bob, \"omw\"", 0x03, "", out var anon) && anon.Speaker == "You",
            "own lines before login keep \"You\"");

        Check(ChatDiscordLogic.TryClassify("You gain 500 xp.", 0x00, "Alice", out var plain), "plain text classifies");
        Check(plain.Speech == null && plain.Speaker.Length == 0 && plain.Message == "You gain 500 xp.", "plain text has no channel");
        Check(plain.Haystack == "You gain 500 xp.", "plain haystack is the text");
        Check(ChatDiscordLogic.TryClassify("Bob tells you, \"rare drop\"", 0x03, "", out var tell) && tell.Haystack == "Bob rare drop",
            "speech haystack is speaker + message");

        Check(!ChatDiscordLogic.TryClassify("   ", 0x00, "Alice", out _), "blank lines are skipped");
        Check(!ChatDiscordLogic.TryClassify(null, 0x00, "Alice", out _), "null lines are skipped");
    }

    // ── Which routes take a line ─────────────────────────────────────────

    private static void Routing()
    {
        var route = Route(DiscordRoute.DefaultSpeechMask(), "System");

        Check(ChatDiscordLogic.Wants(route, SpeechChannel.Tell, _ => false, out string l1) && l1 == "Tell", "default routes take tells");
        Check(ChatDiscordLogic.Wants(route, SpeechChannel.Monarch, _ => false, out _), "default routes take the allegiance channels");
        Check(!ChatDiscordLogic.Wants(route, SpeechChannel.Local, _ => false, out _), "default routes skip local");
        Check(!ChatDiscordLogic.Wants(route, SpeechChannel.General, _ => false, out _), "default routes skip General");

        Check(ChatDiscordLogic.Wants(route, null, t => t == "System", out string l2) && l2 == "System",
            "a line in a ticked tab is taken, labelled with the tab");
        Check(ChatDiscordLogic.Wants(route, SpeechChannel.Local, t => t == "System", out string l3) && l3 == "Local",
            "speech taken through a tab keeps its channel label");
        Check(!ChatDiscordLogic.Wants(route, null, t => t == "Combat", out _), "lines only in other tabs are skipped");

        var none = Route(0);
        Check(!ChatDiscordLogic.Wants(none, SpeechChannel.Tell, _ => true, out _), "a route with nothing ticked takes nothing");

        var all = Route(DiscordRoute.AllSpeechMask());
        foreach (var ch in ChatSpeech.All)
            Check(all.RelaysSpeech(ch), $"all-channels route relays {ch}");

        Check(DiscordFormat.MatchesKeywords("Bob rare drop", DiscordFormat.SplitList("loot, RARE ,")), "keywords match case-insensitively");
        Check(!DiscordFormat.MatchesKeywords("Bob hi", DiscordFormat.SplitList("loot,rare")), "keywords filter");
        Check(DiscordFormat.MatchesKeywords("anything", DiscordFormat.SplitList(" , ")), "no keywords = everything");


        var s = new ChatDiscordSettings { BatchMs = 1500, MinIntervalMs = 2000 };
        var own = new DiscordRoute { CustomPace = true, BatchMs = 0, MinIntervalMs = 60_000 };
        Check(s.BatchFor(route) == 1500 && s.IntervalFor(route) == 2000, "routes use the relay's pacing by default");
        Check(s.BatchFor(own) == 0 && s.IntervalFor(own) == 60_000, "own pacing overrides it");

        Check(!new DiscordRoute().HasSecret, "a new route has no secret");
        Check(new DiscordRoute { WebhookProtected = "blob" }.HasSecret, "webhook routes need the webhook");
        Check(!new DiscordRoute { Mode = DiscordRoute.ModeBot, BotTokenProtected = "blob" }.HasSecret, "bot routes need a channel too");
        Check(new DiscordRoute { Mode = DiscordRoute.ModeBot, BotTokenProtected = "blob", ChannelId = FakeChannel }.HasSecret, "bot route ready");
    }

    private static DiscordRoute Route(uint speech, params string[] tabs)
        => new() { SpeechMask = speech, Tabs = tabs };

    // ── Discord text ─────────────────────────────────────────────────────

    private static void Formatting()
    {
        var at = new DateTime(2026, 10, 11, 12, 34, 56);
        var s = new ChatDiscordSettings { IncludeTimestamp = true, TagCharacter = false };
        ChatDiscordLogic.TryClassify("Bob tells you, \"hi *there*\"", 0x03, "Alice", out var tell);
        Same(ChatDiscordLogic.Format(s, at, tell, "Tell", "Alice"), "`12:34:56` **\\[Tell\\] Bob:** hi \\*there\\*", "speech line");

        s.IncludeTimestamp = false;
        s.TagCharacter = true;
        Same(ChatDiscordLogic.Format(s, at, tell, "Tell", "Alice"), "\\[Alice\\] **\\[Tell\\] Bob:** hi \\*there\\*", "character tag");
        Same(ChatDiscordLogic.Format(s, at, tell, "Tell", ""), "**\\[Tell\\] Bob:** hi \\*there\\*", "no tag before login");

        s.TagCharacter = false;
        ChatDiscordLogic.TryClassify("# You found a rare!", 0x00, "Alice", out var plain);
        Same(ChatDiscordLogic.Format(s, at, plain, "", "Alice"), "\\# You found a rare!", "plain text can't become a heading");
        Same(ChatDiscordLogic.Format(s, at, plain, "System", "Alice"), "**\\[System\\]** # You found a rare!", "tab label");

        string pinged = DiscordFormat.Escape("@everyone look <@123> [x](y)");
        Check(!pinged.Contains("@everyone"), "@everyone is defused");
        Check(pinged.Contains("\\<@123\\>") && pinged.Contains("\\[x\\]"), "mentions and links are escaped");
        Same(DiscordFormat.Escape("a\r\nb"), "a  b", "newlines flatten");

        string longLine = DiscordFormat.FormatLine(at, null, null, new string('z', 5000), false, null);
        Check(longLine.Length == DiscordFormat.MaxLine && longLine.EndsWith('\u2026'), "long lines are cut");
        string cut = DiscordFormat.Truncate(new string('a', 8) + "\\*", 10);
        Check(cut.Length <= 10 && !cut.TrimEnd('\u2026').EndsWith('\\'), "cuts don't strand an escape");

        Same(DiscordFormat.SafeWebhookName("+Bob"), "Bob", "webhook name drops +");
        Same(DiscordFormat.SafeWebhookName("Discord Bob"), "", "Discord refuses names with 'discord'");
        Same(DiscordFormat.SafeWebhookName("everyone"), "", "no 'everyone' name");
        Check(DiscordFormat.SafeWebhookName(new string('n', 100)).Length == 80, "webhook names cap at 80");
    }

    // ── Scrollback, by RynthChat's seq ───────────────────────────────────

    private static void Backlog()
    {
        static ulong[] Seqs(ulong from, int count)
        {
            var a = new ulong[count];
            for (int i = 0; i < count; i++) a[i] = from + (ulong)i;
            return a;
        }
        static int Posted(bool[] post) => post.Count(p => p);

        // Engine start or hot reload: the first read is RynthChat's whole ring (up to 500 lines).
        var g = new DiscordBacklogGate();
        Check(Posted(g.Admit(on: true, Seqs(1, 500))) == 0, "the first read after start is a baseline: 500 lines of scrollback, none posted");
        Check(g.Live, "after the baseline the relay is live");
        Check(Posted(g.Admit(on: true, Seqs(501, 3))) == 3, "lines after the baseline are posted");
        Check(Posted(g.Admit(on: true, Seqs(502, 2))) == 0, "a line seen already isn't posted twice");
        Check(Posted(g.Admit(on: true, new ulong[] { 503, 504, 505 })) == 2, "only the newer lines of a mixed read");

        // An empty first read is still the baseline: the next line is news.
        g = new DiscordBacklogGate();
        Check(Posted(g.Admit(on: true, Array.Empty<ulong>())) == 0 && g.Live, "an empty first read is the baseline");
        Check(Posted(g.Admit(on: true, Seqs(1, 1))) == 1, "the line after an empty baseline is posted");

        // Off, then on again: what arrived meanwhile is history.
        Check(Posted(g.Admit(on: false, Seqs(2, 10))) == 0 && !g.Live, "nothing is posted while off");
        Check(Posted(g.Admit(on: true, Seqs(12, 40))) == 0, "the first read after switching on is a baseline");
        Check(Posted(g.Admit(on: true, Seqs(52, 1))) == 1, "then new lines go");
        g.Off();
        Check(Posted(g.Admit(on: true, Seqs(53, 5))) == 0, "Off (the pump saw it off) re-baselines");

        // RynthChat reloaded: its numbers start from 1 again.
        g.SourceReset();
        Check(Posted(g.Admit(on: true, Seqs(1, 4))) == 0, "after RynthChat restarts its numbering, its first read is a baseline");
        Check(Posted(g.Admit(on: true, Seqs(5, 2))) == 2, "and its new lines go, though their numbers are low");
    }

    // ── Batching ─────────────────────────────────────────────────────────

    private static void Packing()
    {
        var lines = new List<string> { new('a', 900), new('b', 900), new('c', 900) };
        string first = DiscordFormat.Pack(lines, out int taken);
        Check(taken == 2 && first.Length == 1801, $"two 900-char lines fit one message (took {taken}, {first.Length} chars)");

        var huge = new List<string> { new('h', 2500), "next" };
        string one = DiscordFormat.Pack(huge, out int t2);
        Check(t2 == 1 && one.Length == DiscordFormat.MaxContent, "an oversized line still goes, cut to 2000");

        Check(DiscordFormat.Pack(new List<string>(), out int t3) == string.Empty && t3 == 0, "nothing to pack");
    }

    // ── Pasted values ────────────────────────────────────────────────────

    private static void Validation()
    {
        Check(DiscordFormat.TryNormalizeWebhookUrl(FakeWebhook, out string n1) && n1 == FakeWebhook, "webhook URL accepted");
        Check(DiscordFormat.TryNormalizeWebhookUrl(" " + FakeWebhook + "/?wait=true ", out string n2) && n2 == FakeWebhook,
            "query and trailing slash are dropped");
        Check(DiscordFormat.TryNormalizeWebhookUrl(FakeWebhook.Replace("discord.com", "ptb.discordapp.com").Replace("/api/", "/api/v10/"), out _),
            "ptb / discordapp / versioned URLs accepted");
        Check(!DiscordFormat.TryNormalizeWebhookUrl(FakeWebhook.Replace("https:", "http:"), out _), "plain http refused");
        Check(!DiscordFormat.TryNormalizeWebhookUrl(FakeWebhook.Replace("discord.com", "discord.com.example.net"), out _), "other hosts refused");
        Check(!DiscordFormat.TryNormalizeWebhookUrl("", out _), "blank refused");

        Check(DiscordFormat.LooksLikeBotToken(FakeBotToken), "bot token shape accepted");
        Check(!DiscordFormat.LooksLikeBotToken("Bot " + FakeBotToken), "the 'Bot ' prefix isn't part of the token");
        Check(!DiscordFormat.LooksLikeBotToken("short.x.y"), "short tokens refused");
        Check(DiscordFormat.IsSnowflake(FakeChannel), "channel id accepted");
        Check(!DiscordFormat.IsSnowflake("#general"), "channel names refused");

        string secret = new string('s', 40);
        Same(DiscordFormat.Redact("error near " + secret, secret), "error near ***", "secrets are redacted");
        Same(DiscordFormat.Redact("no secret here", ""), "no secret here", "nothing to redact");

        string hookToken = FakeWebhook.Substring(FakeWebhook.LastIndexOf('/') + 1);
        Same(DiscordFormat.Redact("Invalid Webhook Token: " + hookToken, FakeWebhook), "Invalid Webhook Token: ***",
            "the webhook's bare token is redacted");
        string other = "https://discord.com/api/webhooks/" + new string('2', 18) + "/" + new string('y', 68);
        Check(!DiscordFormat.Redact("posting to " + other, FakeWebhook).Contains(new string('y', 68)), "any webhook URL's token is redacted");
        Same(DiscordFormat.Redact("Authorization: Bot " + FakeBotToken, null), "Authorization: Bot ***", "a bare bot token is redacted without being told");
        Same(DiscordFormat.Redact("401 for " + FakeBotToken + ".", FakeBotToken), "401 for ***.", "the bot token in use is redacted");
        Same(DiscordFormat.Redact("HTTP 404: Unknown Webhook (v1.2.3)", FakeWebhook), "HTTP 404: Unknown Webhook (v1.2.3)", "ordinary errors are left alone");
    }

    // ── Request body and status handling ─────────────────────────────────

    private static void RequestBody()
    {
        using (var doc = JsonDocument.Parse(DiscordClient.BuildBody("line \"one\"\nline two", "Alice")))
        {
            var root = doc.RootElement;
            Check(root.GetProperty("content").GetString() == "line \"one\"\nline two", "content round-trips");
            Check(root.GetProperty("username").GetString() == "Alice", "username sent");
            Check(root.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength() == 0, "no mentions can ping");
            Check(root.GetProperty("flags").GetInt32() == 4, "embeds suppressed");
        }
        using (var doc = JsonDocument.Parse(DiscordClient.BuildBody("x", "")))
            Check(!doc.RootElement.TryGetProperty("username", out _), "no username when blank (bot / webhook default)");

        Check(DiscordClient.ParseRetryAfterMs("{\"retry_after\":1.5,\"global\":false}", null) == 1500, "retry_after from JSON");
        Check(DiscordClient.ParseRetryAfterMs(null, "3") == 3000, "Retry-After header");
        Check(DiscordClient.ParseRetryAfterMs("not json", null) == 5000, "unknown wait is 5 s");
        Check(DiscordClient.ParseRetryAfterMs("{\"retry_after\":0.01}", null) == 250, "waits at least 0.25 s");
        Check(DiscordClient.ParseRetryAfterMs("{\"retry_after\":99999}", null) == 600_000, "waits at most 10 min");

        foreach (int st in new[] { 400, 401, 403, 404 }) Check(DiscordClient.IsPermanent(st), $"HTTP {st} pauses the route");
        foreach (int st in new[] { 429, 500, 502, 503 }) Check(!DiscordClient.IsPermanent(st), $"HTTP {st} is retried");
    }

    // ── Settings file ────────────────────────────────────────────────────

    private static void Settings()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "rynthchat_discord.json");

            var fresh = ChatDiscordStore.Load(path, out string n0);
            Check(fresh.Characters.Length == 0 && fresh.Routes.Length == 0 && n0.Length == 0, "no file: defaults, off for everyone");

            var s = new ChatDiscordSettings
            {
                Characters = new[] { DiscordCharacter.Of("Aelrynth", "Alice")! },
                SkipOwn = true,
                TagCharacter = true,
                BatchMs = 999_999,          // clamped on load
                MinIntervalMs = 5,          // clamped on load
                Routes = new[]
                {
                    new DiscordRoute
                    {
                        Name = "Guild", Mode = DiscordRoute.ModeBot, ChannelId = FakeChannel, BotTokenProtected = "blob",
                        SpeechMask = ChatSpeech.Bit(SpeechChannel.Tell) | ChatSpeech.Bit(SpeechChannel.Lfg),
                        Tabs = new[] { "System", "Loot" }, Keywords = "rare", CustomPace = true, BatchMs = 0, MinIntervalMs = 30_000,
                    },
                    new DiscordRoute { Name = "  ", SpeechMask = 0 },
                },
            };
            string json = ChatDiscordStore.Serialize(s);
            Check(json.Contains("\"speech\"") && json.Contains("\"LFG\""), "speech channels are saved by name");
            Check(!json.Contains("speechMask", StringComparison.OrdinalIgnoreCase), "the runtime mask isn't saved");
            Check(!json.Contains("hasSecret", StringComparison.OrdinalIgnoreCase) && !json.Contains("\"status\""), "runtime state isn't saved");
            ChatDiscordStore.Write(path, json);
            Check(Directory.GetFiles(dir, "*.tmp").Length == 0, "the temp file is swapped in");

            var back = ChatDiscordStore.Load(path, out string n1);
            Check(n1.Length == 0, "clean load has no note");
            Check(back.SkipOwn && back.TagCharacter && back.Characters.Length == 1 && back.Characters[0].Name == "Alice" && back.Characters[0].Server == "Aelrynth", "switches round-trip");
            Check(back.BatchMs == ChatDiscordSettings.BatchMaxMs, "batch window clamped");
            Check(back.MinIntervalMs == ChatDiscordSettings.IntervalMinMs, "message spacing clamped");
            Check(back.Routes.Length == 2, "routes round-trip");
            var g = back.Routes[0];
            Check(g.Name == "Guild" && g.Mode == DiscordRoute.ModeBot && g.ChannelId == FakeChannel && g.BotTokenProtected == "blob",
                "route connection round-trips");
            Check(g.RelaysSpeech(SpeechChannel.Tell) && g.RelaysSpeech(SpeechChannel.Lfg) && !g.RelaysSpeech(SpeechChannel.Fellowship),
                "speech channels round-trip");
            Check(g.Tabs.SequenceEqual(new[] { "System", "Loot" }) && g.Keywords == "rare", "tabs and keywords round-trip");
            Check(g.CustomPace && g.BatchMs == 0 && g.MinIntervalMs == 30_000, "route pacing round-trips");
            Check(back.Routes[1].Name == "Discord", "blank route names get a name");
            Check(back.Routes[1].SpeechMask == 0, "a route with every channel unticked stays empty");

            // Hand-edited file: no speech list means the defaults; bad values repaired.
            File.WriteAllText(path, "{ \"enabled\": true, \"routes\": [ { \"name\": \"x\", \"mode\": \"carrier pigeon\", " +
                                    "\"tabs\": [\"System\", \"system\", \" \", null] }, null ], \"maxQueue\": 1 }");
            var hand = ChatDiscordStore.Load(path, out _);
            Check(hand.Characters.Length == 0, "an old file's global \"enabled\" turns nobody on");
            Check(hand.Routes.Length == 1, "null routes dropped");
            Check(hand.Routes[0].Mode == DiscordRoute.ModeWebhook, "unknown modes become webhook");
            Check(hand.Routes[0].SpeechMask == DiscordRoute.DefaultSpeechMask(), "no speech list = the defaults");
            Check(hand.Routes[0].Tabs.Length == 1, $"tabs de-duplicated (got {hand.Routes[0].Tabs.Length})");
            Check(hand.MaxQueue == ChatDiscordSettings.QueueMin, "queue size clamped");

            // A damaged file: defaults, a note for the log, and a copy kept for the user.
            File.WriteAllText(path, "{ not json");
            var bad = ChatDiscordStore.Load(path, out string n2);
            Check(bad.Characters.Length == 0 && bad.Routes.Length == 0, "damaged file: defaults");
            Check(n2.Contains("unreadable"), "damaged file is reported");
            Check(Directory.GetFiles(dir, "*.unreadable-*.json").Length == 1, "damaged file is kept aside");
        }
        finally { TryDelete(dir); }
    }

    // ── DPAPI ────────────────────────────────────────────────────────────

    private static void Secrets()
    {
        string blob = DiscordSecrets.Protect(FakeWebhook);
        Check(blob.Length > 0 && !blob.Contains(FakeWebhook) && !blob.Contains("webhooks"), "protected blobs hide the secret");
        Check(DiscordSecrets.TryUnprotect(blob, out string plain) && plain == FakeWebhook, "protect / unprotect round-trip");
        Check(DiscordSecrets.Protect(FakeWebhook) != blob, "each protect is salted");

        byte[] raw = Convert.FromBase64String(blob);
        raw[raw.Length / 2] ^= 0x55;
        Check(!DiscordSecrets.TryUnprotect(Convert.ToBase64String(raw), out _), "damaged blobs don't open");
        Check(!DiscordSecrets.TryUnprotect("not base64 !!", out _), "garbage doesn't open");
        Check(!DiscordSecrets.TryUnprotect("", out _), "empty doesn't open");
        Check(DiscordSecrets.Protect("") == string.Empty && DiscordSecrets.Protect(null) == string.Empty, "empty in, empty out");
    }

    // ── Repeat filter ────────────────────────────────────────────────────

    private static void Dedup()
    {
        ChatDiscordLogic.TryClassify("Bob tells you, \"hi\"", 0x03, "", out var a);
        ChatDiscordLogic.TryClassify("Bob tells you, \"hi!\"", 0x03, "", out var b);
        Check(ChatDiscordLogic.DedupKey(a, "Tell") != ChatDiscordLogic.DedupKey(b, "Tell"), "different lines, different keys");
        Check(ChatDiscordLogic.DedupKey(a, "Tell") != ChatDiscordLogic.DedupKey(a, "General"), "the label is part of the key");

        var recent = new Dictionary<string, long> { ["old"] = 1_000, ["new"] = 9_000 };
        ChatDiscordLogic.PruneRecent(recent, now: 10_000, windowMs: 5_000);
        Check(!recent.ContainsKey("old") && recent.ContainsKey("new"), "old repeat entries are pruned");
    }

    // ── Per-character switch and merged saves ────────────────────────────

    private static void Characters()
    {
        Check(DiscordCharacter.Of("Aelrynth", "  ") == null && DiscordCharacter.Of("Aelrynth", null) == null, "no name, no character");
        var alice = DiscordCharacter.Of(" Aelrynth ", "+Alice ")!;
        Check(alice.Server == "Aelrynth" && alice.Name == "Alice", "names are trimmed (and lose AC's +)");
        Check(alice.Same(DiscordCharacter.Of("aelrynth", "ALICE")), "same character in any case");
        Check(!alice.Same(DiscordCharacter.Of("Other", "Alice")), "same name on another server is another character");
        Check(!alice.Same(null), "nobody is not Alice");
        var bob = DiscordCharacter.Of("Aelrynth", "Bob")!;
        var carol = DiscordCharacter.Of("Aelrynth", "Carol")!;

        var s = new ChatDiscordSettings();
        Check(!s.IsOnFor(alice) && !s.IsOnFor(null), "off for everyone by default");
        s.Characters = new[] { alice };
        Check(s.IsOnFor(DiscordCharacter.Of("AELRYNTH", "alice")) && !s.IsOnFor(bob), "on only for the characters listed");

        var list = ChatDiscordStore.ApplyCharacterOps(new[] { alice, alice, bob }, new[]
        {
            new DiscordCharacterOp(carol, true), new DiscordCharacterOp(bob, false), new DiscordCharacterOp(carol, true),
        });
        Check(list.Length == 2 && list[0].Same(alice) && list[1].Same(carol), "switches apply in order, without repeats");

        // Two clients start from the same file; each switches its own character.
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "rynthchat_discord.json");
            var shared = new ChatDiscordSettings
            {
                Routes = new[] { new DiscordRoute { Name = "Guild", SpeechMask = ChatSpeech.Bit(SpeechChannel.Lfg), WebhookProtected = "blob" } },
            };
            ChatDiscordStore.Write(path, ChatDiscordStore.Serialize(shared));
            var clientA = ChatDiscordStore.Load(path, out _);
            var clientB = ChatDiscordStore.Load(path, out _);

            string Save(ChatDiscordSettings mine, params DiscordCharacterOp[] ops)
            {
                string? disk = File.Exists(path) ? File.ReadAllText(path) : null;
                string json = ChatDiscordStore.MergeForSave(ChatDiscordStore.Serialize(mine), disk, ops, out var merged);
                ChatDiscordStore.Write(path, json);
                mine.Characters = merged;
                return json;
            }

            Save(clientA, new DiscordCharacterOp(alice, true));
            Save(clientB, new DiscordCharacterOp(bob, true));      // B never saw Alice switched on
            var now = ChatDiscordStore.Load(path, out _);
            Check(now.IsOnFor(alice) && now.IsOnFor(bob), "B's save keeps A's switch");
            Check(clientB.IsOnFor(alice), "a save brings the other clients' switches back");

            clientA.SkipOwn = true;                                  // A, still not knowing Bob, saves an option
            Save(clientA);
            now = ChatDiscordStore.Load(path, out _);
            Check(now.IsOnFor(alice) && now.IsOnFor(bob) && now.SkipOwn, "a save with no switch keeps everyone's switch");
            Check(now.Routes.Length == 1 && now.Routes[0].RelaysSpeech(SpeechChannel.Lfg) && now.Routes[0].WebhookProtected == "blob",
                "routes survive the merge");

            Save(clientB, new DiscordCharacterOp(bob, false));
            now = ChatDiscordStore.Load(path, out _);
            Check(now.IsOnFor(alice) && !now.IsOnFor(bob), "turning one character off leaves the others on");

            // A switch another client turned off doesn't come back from a stale list.
            clientA.Characters = new[] { alice, bob };
            Save(clientA);
            Check(!ChatDiscordStore.Load(path, out _).IsOnFor(bob), "a stale in-memory list doesn't switch anyone back on");

            File.WriteAllText(path, "{ damaged");
            clientA.Characters = new[] { alice, bob };
            string fromBad = ChatDiscordStore.MergeForSave(ChatDiscordStore.Serialize(clientA), File.ReadAllText(path),
                new[] { new DiscordCharacterOp(carol, true) }, out var m1);
            Check(m1.Length == 3 && fromBad.Contains("Carol"), "an unreadable file: our own list is the base");
            ChatDiscordStore.MergeForSave(ChatDiscordStore.Serialize(new ChatDiscordSettings()), null,
                Array.Empty<DiscordCharacterOp>(), out var m2);
            Check(m2.Length == 0, "no file and no switch: off for everyone");

            Check(ChatDiscordStore.ReadCharacters("{ \"characters\": [ { \"server\": \"S\", \"name\": \"X\" }, { \"name\": \" \" } ] }") is { Length: 1 },
                "the list is read back without blanks");
            Check(ChatDiscordStore.ReadCharacters("nope") == null && ChatDiscordStore.ReadCharacters(null) == null, "unreadable lists are null");
        }
        finally { TryDelete(dir); }
    }

    // ── A route's queue: off means off ───────────────────────────────────

    private static void Queue()
    {
        const int Batch = 1500, Interval = 2000, Max = 200;
        var fail = DiscordSendResult.Fail("HTTP 502");
        var ok = new DiscordSendResult(true, 200, 0, false, string.Empty);

        // Normal flow: the batch window, then one post.
        var q = new DiscordRouteQueue();
        q.Add("a", 1000, Max);
        q.Add("b", 1200, Max);
        Check(q.Next(2000, on: true, Batch, Interval) == null, "lines wait out the batch window");
        DiscordBatch? b1 = q.Next(2600, on: true, Batch, Interval);
        Check(b1 != null && b1.Content == "a\nb" && ReferenceEquals(q.InFlight, b1) && q.Pending.Count == 0, "the window's lines go as one message");
        Check(q.Next(9000, on: true, Batch, Interval) == null, "one post in flight at a time");
        Check(q.Apply(b1!, ok, 3000, Interval) == DiscordOutcome.Sent && q.MessagesSent == 1 && q.LinesSent == 2, "a sent batch counts");

        // Pending lines, then the relay goes off.
        q = new DiscordRouteQueue();
        q.Add("x", 0, Max);
        q.Add("y", 0, Max);
        Check(q.Next(10_000, on: false, Batch, Interval) == null && q.Pending.Count == 0, "off drops pending lines");
        Check(q.Dropped == 2, "dropped lines are counted");
        q.Add("z", 20_000, Max);
        Check(q.Stop() == 1 && q.Queued == 0, "Stop drops pending lines");

        // A batch waiting to retry must not go out once the relay or route is off.
        q = new DiscordRouteQueue();
        q.Add("retry me", 0, Max);
        DiscordBatch? b2 = q.Next(5000, on: true, Batch, Interval);
        Check(q.Apply(b2!, fail, 5100, Interval) == DiscordOutcome.Retrying && ReferenceEquals(q.Retry, b2), "a network error waits to retry");
        Check(q.Next(600_000, on: false, Batch, Interval) == null, "a waiting retry is not sent when off");
        Check(q.Retry == null && q.Queued == 0, "off drops the waiting retry");

        // Same after a rate limit.
        q = new DiscordRouteQueue();
        q.Add("limited", 0, Max);
        DiscordBatch? b3 = q.Next(5000, on: true, Batch, Interval);
        Check(q.Apply(b3!, DiscordSendResult.Fail("rate limited", status: 429, retryAfterMs: 1000), 5100, Interval) == DiscordOutcome.RateLimited,
            "429 is a rate limit");
        Check(q.Next(600_000, on: false, Batch, Interval) == null && q.Retry == null, "a rate-limited batch is dropped when off");

        // The post in flight when it goes off: cancelled, and its result can't come back as a retry.
        q = new DiscordRouteQueue();
        q.Add("flying", 0, Max);
        DiscordBatch? b4 = q.Next(5000, on: true, Batch, Interval);
        using (var cts = new CancellationTokenSource())
        {
            b4!.Cancel = cts;
            Check(q.Stop() == 1 && q.InFlight == null, "Stop drops the post in flight");
            Check(cts.IsCancellationRequested, "the request in flight is cancelled");
        }
        Check(q.Apply(b4, fail, 5200, Interval) == DiscordOutcome.Ignored && q.Retry == null, "a stopped post's failure isn't retried");
        Check(q.Apply(b4, ok, 5200, Interval) == DiscordOutcome.Ignored && q.MessagesSent == 0, "a stopped post's result is ignored");
        Check(q.Next(600_000, on: true, Batch, Interval) == null, "nothing comes back after a stop");

        // Only an explicit test gets past the switches.
        q = new DiscordRouteQueue();
        q.Add("chat", 0, Max);
        q.QueueTest("test line");
        DiscordBatch? t1 = q.Next(10, on: false, Batch, Interval);
        Check(t1 != null && t1.IsTest && t1.Content == "test line", "a test goes while off");
        Check(q.Pending.Count == 0, "chat queued with it doesn't");
        Check(q.Stop() == 0 && ReferenceEquals(q.InFlight, t1), "Stop keeps a test in flight");
        Check(q.Apply(t1!, fail, 20, Interval) == DiscordOutcome.Retrying, "a failed test retries");
        Check(q.Next(600_000, on: false, Batch, Interval) is { IsTest: true }, "a test's retry goes while off");

        q = new DiscordRouteQueue();
        q.Add("old", 0, Max);
        DiscordBatch? b5 = q.Next(5000, on: true, Batch, Interval);
        q.Apply(b5!, fail, 5100, Interval);
        q.QueueTest("test");
        Check(q.Retry is { IsTest: true } && q.Dropped == 1, "a test replaces a waiting retry");

        // Permanent errors pause; give up after the retries.
        q = new DiscordRouteQueue();
        q.Add("p", 0, Max);
        DiscordBatch? b6 = q.Next(5000, on: true, Batch, Interval);
        q.Add("later", 5001, Max);
        Check(q.Apply(b6!, DiscordSendResult.Fail("HTTP 401", permanent: true, status: 401), 5100, Interval) == DiscordOutcome.Paused,
            "401 pauses");
        Check(q.Paused && q.Pending.Count == 0 && q.Next(600_000, on: true, Batch, Interval) == null, "a paused route sends nothing");
        q.Resume();
        Check(!q.Paused && q.LastError.Length == 0, "Resume clears a pause");

        q = new DiscordRouteQueue();
        q.Add("doomed", 0, Max);
        long now = 5000;
        DiscordBatch? b7 = q.Next(now, on: true, Batch, Interval);
        DiscordOutcome last = DiscordOutcome.Ignored;
        for (int i = 0; i < DiscordRouteQueue.MaxTransientRetries && b7 != null; i++)
        {
            last = q.Apply(b7, fail, now, Interval);
            now += 120_000;
            b7 = q.Next(now, on: true, Batch, Interval);
        }
        Check(last == DiscordOutcome.GaveUp && q.Dropped == 1, "gives up after the retries");

        q = new DiscordRouteQueue();
        for (int i = 0; i < 25; i++) q.Add("l" + i, 0, maxQueue: 20);
        Check(q.Pending.Count == 20 && q.Dropped == 5 && q.Pending[0] == "l5", "the oldest lines go past the queue limit");

        Check(q.NoteFresh("k", 1000) && !q.NoteFresh("k", 2000) && q.NoteFresh("k", 1000 + DiscordRouteQueue.DedupWindowMs),
            "the repeat filter holds for its window");

        // A new credential while a retry waits: the batch was for the old destination.
        const string Hook = DiscordRoute.ModeWebhook;
        q = new DiscordRouteQueue();
        Check(q.Retarget(Hook, "", "blobA") == 0, "the first destination drops nothing");
        q.Add("for A", 0, Max);
        DiscordBatch? a1 = q.Next(5000, on: true, Batch, Interval);
        Check(a1 != null && a1.Credential == q.Credential, "a batch remembers its destination");
        q.Apply(a1!, fail, 5100, Interval);
        q.Add("also for A", 5200, Max);
        Check(q.Retarget(Hook, "", "blobA") == 0 && ReferenceEquals(q.Retry, a1), "the same destination keeps the retry");
        Check(q.Retarget(Hook, "", new string("blobA".ToCharArray())) == 0, "an equal credential in a new string is the same destination");
        Check(q.Retarget(Hook, "", "blobB") == 2, "a new credential drops the waiting retry and pending lines");
        Check(q.Retry == null && q.Pending.Count == 0 && q.Next(600_000, on: true, Batch, Interval) == null,
            "nothing queued for A goes to B");
        q.Add("for B", 600_000, Max);
        Check(q.Next(700_000, on: true, Batch, Interval) is { Content: "for B" }, "new lines go to the new destination");

        // A post in flight to the old destination fails after the change: dropped, not retried, no pause.
        q = new DiscordRouteQueue();
        q.Retarget(DiscordRoute.ModeBot, FakeChannel, "tokA");
        q.Add("to channel A", 0, Max);
        DiscordBatch? a2 = q.Next(5000, on: true, Batch, Interval);
        Check(q.Retarget(DiscordRoute.ModeBot, "8" + FakeChannel.Substring(1), "tokA") == 0, "a new channel with nothing waiting drops nothing");
        Check(q.Apply(a2!, fail, 5100, Interval) == DiscordOutcome.Retargeted && q.Retry == null, "a failed post to the old destination isn't retried");
        q.Add("x", 5200, Max);
        DiscordBatch? a3 = q.Next(600_000, on: true, Batch, Interval);
        q.Retarget(DiscordRoute.ModeWebhook, FakeChannel, "hook");
        Check(q.Apply(a3!, DiscordSendResult.Fail("HTTP 404", permanent: true, status: 404), 600_100, Interval) == DiscordOutcome.Retargeted
              && !q.Paused, "the old destination's 404 doesn't pause the new one");

        // A test follows the route to its new destination.
        q = new DiscordRouteQueue();
        q.Retarget(Hook, "", "old");
        q.QueueTest("t");
        q.Retarget(Hook, "", "new");
        Check(q.Retry is { IsTest: true } t2 && t2.Credential == q.Credential, "a waiting test goes to the new destination");
    }

    // ── Engine shutdown closes the HTTP client ───────────────────────────

    /// <summary>Last: after Shutdown no client is built again, so a post fails at once, sending nothing.</summary>
    private static void ClientShutdown()
    {
        DiscordClient.Shutdown();
        DiscordClient.Shutdown();   // twice is harmless
        var r = DiscordClient.PostWebhookAsync(FakeWebhook, "x", "", CancellationToken.None).GetAwaiter().GetResult();
        Check(!r.Ok && !r.Permanent && r.Error.Contains("ObjectDisposedException"), $"no posts after shutdown (got \"{r.Error}\")");
        Check(!r.Error.Contains(new string('x', 68)), "the refusal doesn't echo the webhook");
    }

    // ── The panel never formats chat ─────────────────────────────────────

    // ImGui.NET passes the string of these to ImGui as a printf format: "%s%s%s" in a relayed
    // tell crashed the client while the Discord panel's Log tab was open.
    private static readonly System.Text.RegularExpressions.Regex RxFormatCall = new(
        @"ImGui\.(?<fn>Text|TextWrapped|TextColored|TextDisabled|BulletText|LabelText|SetTooltip|SetItemTooltip|LogText)\(\s*(?<arg>[^\r\n]*)");

    /// <summary>
    /// The face can't run here (it needs ImGui), so this reads its source: every call to an ImGui
    /// function that formats its text must be given a plain literal with no '%' conversions;
    /// anything else (chat, routes, Discord's errors) goes through TextUnformatted.
    /// </summary>
    private static void FaceText()
    {
        string face = Path.GetFullPath(Path.Combine(SourceDir(), "..", "..", "src", "RynthCore.Engine", "ImGui", "Panels", "ChatDiscordFace.cs"));
        Check(File.Exists(face), $"face source found ({face})");
        if (!File.Exists(face)) return;
        string src = File.ReadAllText(face);
        int calls = 0;
        foreach (System.Text.RegularExpressions.Match m in RxFormatCall.Matches(src))
        {
            calls++;
            string arg = m.Groups["arg"].Value;
            int line = src.Take(m.Index).Count(c => c == '\n') + 1;
            bool literal = arg.StartsWith('"');
            Check(literal, $"ChatDiscordFace.cs:{line}: ImGui.{m.Groups["fn"].Value} is given a non-literal (use TextUnformatted)");
            if (literal)
                Check(!arg.Replace("%%", "").Contains('%'), $"ChatDiscordFace.cs:{line}: '%' in a format string");
        }
        Check(calls > 0, "the face's tooltips are checked");
        Check(!src.Contains("TextWrapped("), "the face has no TextWrapped");
        Check(src.Contains("TextUnformatted("), "the face writes text with TextUnformatted");
    }

    private static string SourceDir([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetDirectoryName(path)!;

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ChatDiscordTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private static void Same(string got, string want, string what)
        => Check(got == want, $"{what}: want \"{want}\", got \"{got}\"");

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine("FAIL: " + what);
    }
}

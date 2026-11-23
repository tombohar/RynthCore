using System.Text.Json;
using RynthCore.Engine.UI.Data;

namespace ChatFilterTests;

// Player chat (2026-11-22): its own file, so it merges cleanly beside other branches' tests.
internal static partial class Program
{
    // ── Player chat (2026-11-22): every player line under Channels, kept longer than the spam ──

    /// <summary>A line as the engine makes it: the channel RynthChat files it under (ChatRetailVerdict
    /// follows the plugin), AC's name links stripped.</summary>
    private static ChatLine PlayerTestLine(long id, string text, int type) =>
        new(id, "12:00:00", ChatRetailVerdict.ChannelFor(text, type), type, null, text);

    private static void PlayerChatLines()
    {
        Case("player chat: tells both ways, say, emotes, fellowship, allegiance, channels, room lines; not combat, spells, system, loot");
        (string Text, int Type, bool Player)[] lines =
        {
            ("Bob tells you, \"hi\"", 3, true),
            ("You tell Bob, \"hi\"", 4, true),
            ("Bob says, \"hi\"", 2, true),
            ("You say, \"anyone selling keys?\"", 2, true),
            ("Bob waves.", 12, true),
            ("[Allegiance] Bob says, \"hi\"", 18, true),
            ("Bob says to your fellowship, \"pull\"", 19, true),
            ("[General] Bob says, \"hi\"", 8, true),
            ("You say on the Trade channel, \"WTS\"", 9, true),
            ("[Trade] Bob says, \"WTS Black Opal\"", 28, true),
            ("[General] Aja says, \"lol\"", ServerChannelType, true),
            ("[Roleplay] Bob@coldeve says, \"hi\"", 5, true),           // a network Global room line
            ("[Hunters] Bob@coldeve (Discord) says, \"hi\"", 5, true),  // an invite-only network room
            ("[Patron] Bob says, \"well done\"", 5, true),
            ("Admin tells you, \"server restart in 5\"", 0x1F, true),
            ("You say, \"Malar Zhavik\"", 17, false),                  // your spell words
            ("Drudge Sorcerer says, \"Zojak Equin\"", 17, false),
            ("You slash Drudge Slinker for 54 points of slashing damage!", 22, false),
            ("Tusker Guard grazes your foot for 4 points of bludgeoning damage!", 21, false),
            ("The spell consumed the following components: Prismatic Taper", 7, false),
            ("You've banked 1,250 Luminance.", 26, false),
            ("[BANK] Your balances:", 5, false),
            ("You have killed 50 Drudges! Your task is complete!", 0, false),
            ("[RynthAi] Looting Drudge Slinker", 2, false),
            ("[RynthAi Item] Bob's Sword, \"x\"", 0, false),
            ("[Pet] Fire Banshee hit Drudge for 40 damage.", 5, false),
            ("In portal Space", 0, false),
        };
        foreach (var (text, type, player) in lines)
        {
            string chan = ChatRetailVerdict.ChannelFor(text, type);
            Check(ChatPlayerText.IsPlayerChat(chan, type, text) == player, $"'{text}' (type {type}, {chan}): player chat {player}");
            Check(PlayerTestLine(1, text, type).IsPlayerChat == player, $"'{text}': ChatLine.IsPlayerChat {player}");
        }
        // AC's name links, as the plugin sees the text (the engine strips them first).
        Check(ChatPlayerText.LooksLikeSpeech("[Hunters] <Tell:IIDString:0:Bob>Bob<\\Tell> says, \"hi\""), "a room line with AC's name link");
        Check(!ChatPlayerText.LooksLikeSpeech("[Hunters] Bob says hi"), "no quote: not speech");
        Check(!ChatPlayerText.LooksLikeSpeech("[Bank] Balance, \"5\""), "a tag and a quote but nobody says it");
        Check(!ChatPlayerText.LooksLikeSpeech("Bob says, \"[Trade] Ann says, \\\"x\\\"\""), "an untagged say quoting a room line is left to its type");
        Check(!ChatPlayerText.LooksLikeSpeech(null) && !ChatPlayerText.LooksLikeSpeech(""), "null and empty");
    }

    private const int ServerChannelType = 27;

    private static void PlayerChatInChannels()
    {
        Case("player chat: shown under Channels (setting on) as well as its own tab; Move rules still take it away");
        var router = new ChatRouter(Array.Empty<ChatFilterRule>(), null, "", countHits: false);
        ChatLine tell = PlayerTestLine(1, "Bob tells you, \"hi\"", 3);
        ChatLine fellow = PlayerTestLine(2, "Bob says to your fellowship, \"pull\"", 19);
        ChatLine trade = PlayerTestLine(3, "[Trade] Bob says, \"WTS\"", 28);
        ChatLine hit = PlayerTestLine(4, "You slash Drudge Slinker for 54 points of slashing damage!", 22);
        bool In(ChatLine l, ChatRoute r, string tab, bool settingOn = true) => r.ShowsIn(tab, l.Channel, l.IsPlayerChat && settingOn);

        foreach (ChatLine l in new[] { tell, fellow, trade })
        {
            ChatRoute r = router.Evaluate(l, null);
            Check(In(l, r, "Channels"), $"'{l.Text}' is under Channels");
            Check(In(l, r, "All") && In(l, r, l.Channel), $"'{l.Text}' is still under All and {l.Channel}");
            Check(!In(l, r, "Combat") && !In(l, r, "System"), $"'{l.Text}' is not under Combat or System");
        }
        Check(!In(tell, router.Evaluate(tell, null), "Channels", settingOn: false), "setting off: a tell is not under Channels");
        Check(In(trade, router.Evaluate(trade, null), "Channels", settingOn: false), "setting off: a channel line still is");
        Check(!In(hit, router.Evaluate(hit, null), "Channels"), "a combat line is never under Channels");
        Check(ChatRoute.Default.ShowsIn("Chat", "Chat") && !ChatRoute.Default.ShowsIn("Channels", "Chat"), "the old two-argument call is unchanged");

        // Rules: Move takes a line out of All and its channel tabs, Channels included; Copy keeps it there.
        ChatFilterRule Rule(string pattern, ChatRuleAction action, string tab)
        {
            var r = new ChatFilterRule { Pattern = pattern, Action = action, Tab = tab, Mode = ChatMatchMode.Contains };
            r.Recompile();
            return r;
        }
        var moved = new ChatRouter(new[] { Rule(" tells you, ", ChatRuleAction.Move, "Tells") }, null, "", countHits: false).Evaluate(tell, null);
        Check(In(tell, moved, "Tells") && !In(tell, moved, "Channels") && !In(tell, moved, "All"), "Move to Tells: only under Tells");
        var copied = new ChatRouter(new[] { Rule(" tells you, ", ChatRuleAction.Copy, "Tells") }, null, "", countHits: false).Evaluate(tell, null);
        Check(In(tell, copied, "Tells") && In(tell, copied, "Channels") && In(tell, copied, "Chat"), "Copy to Tells: under Tells, Channels and Chat");
        var hidden = new ChatRouter(new[] { Rule(" tells you, ", ChatRuleAction.Hide, "") }, null, "", countHits: false).Evaluate(tell, null);
        Check(!In(tell, hidden, "Channels") && !In(tell, hidden, "All"), "Hide: nowhere");
        // A rule held to "chan:Channels" still means the lines filed there, not every player line.
        var chanRule = Rule("hi", ChatRuleAction.Copy, "X");
        chanRule.When = "chan:Channels";
        Check(!new ChatRouter(new[] { chanRule }, null, "", countHits: false).Evaluate(tell, null).ShowsIn("X", tell.Channel, true),
            "a 'chan:Channels' rule does not match a tell");
    }

    private static readonly Func<ChatLine, bool> IsPlayerLine = l => l.IsPlayerChat;
    private static readonly Func<int, bool> IsEven = n => n % 2 == 0;

    private static void PlayerChatKeptLonger()
    {
        Case("player chat history: player lines outlast Chat history; nothing kept before goes sooner");
        int[] none = Array.Empty<int>();
        // No player limit: exactly the old Merge.
        var rng = new Random(1122);
        for (int t = 0; t < 200; t++)
        {
            int[] old = Enumerable.Range(1, rng.Next(0, 40)).ToArray();
            int[] add = Enumerable.Range(old.Length + 1, rng.Next(0, 40)).ToArray();
            int max = rng.Next(1, 50);
            if (!ChatHistory.MergeKeep(old, add, max, 0, IsEven).SequenceEqual(ChatHistory.Merge(old, add, max)))
            { Check(false, $"playerMax 0 is the old Merge (old {old.Length}, added {add.Length}, max {max})"); break; }
        }
        Check(true, "playerMax 0 is the old Merge (200 random cases)");

        // Evens are "player" lines. 1..100, keep 10 + 20 player lines.
        int[] all = Enumerable.Range(1, 100).ToArray();
        int[] kept = ChatHistory.MergeKeep(none, all, 10, 20, IsEven);
        Check(kept.Skip(kept.Length - 10).SequenceEqual(Enumerable.Range(91, 10)), "the newest 10 are all kept");
        int[] evens = kept.Where(IsEven).ToArray();
        Check(evens.Length == 20 && evens[0] == 62 && evens[^1] == 100, $"the newest 20 player lines are kept, those in the main window counted too ({string.Join(",", evens.Take(3))}..)");
        Check(kept.Length == 25 && kept.SequenceEqual(kept.OrderBy(n => n)), $"25 lines (10 + 15 older player lines), in order ({kept.Length})");
        Check(ChatHistory.MergeKeep(none, all, 10, 3, IsEven).SequenceEqual(Enumerable.Range(91, 10)), "a player limit below the main window adds nothing");
        Check(ChatHistory.MergeKeep(none, all, 200, 5, IsEven).SequenceEqual(all), "under both limits: everything");

        // Merging in steps gives the same as merging at once.
        int[] stepped = none;
        for (int i = 0; i < 100; i += 7) stepped = ChatHistory.MergeKeep(stepped, all.Skip(i).Take(7).ToArray(), 10, 20, IsEven);
        Check(stepped.SequenceEqual(kept), "seven at a time: the same lines");

        // Lowering either setting trims at once; nothing to trim: null.
        Check(ChatHistory.TrimKeep(kept, 10, 20, IsEven) == null, "same settings: nothing to trim");
        int[]? lower = ChatHistory.TrimKeep(kept, 10, 12, IsEven);
        Check(lower != null && lower.Where(IsEven).Count() == 12 && lower.Length == 17, $"player limit 20 -> 12: 17 lines ({lower?.Length})");
        int[]? shorter = ChatHistory.TrimKeep(kept, 4, 20, IsEven);
        Check(shorter != null && shorter.Skip(shorter.Length - 4).SequenceEqual(new[] { 97, 98, 99, 100 }) && shorter.Where(n => !IsEven(n)).Count() == 2,
            "history 10 -> 4: the odd lines outside the newest 4 go");
        Check(ChatHistory.TrimKeep(kept, 1000, 1000, IsEven) == null, "raising the settings drops nothing");

        Check(ChatHistory.ClampPlayer(0) == 5000 && ChatHistory.ClampPlayer(-1) == 5000 && ChatHistory.ClampPlayer(1) == 500
              && ChatHistory.ClampPlayer(7240) == 7000 && ChatHistory.ClampPlayer(7260) == 7500 && ChatHistory.ClampPlayer(999999) == 20000,
            "Player chat history: default 5000, steps of 500, 500..20000");
    }

    private static void PlayerChatFeedSimulated()
    {
        Case("player chat history: an hour of a busy hunt (10,000-line history) keeps every tell, fellowship and allegiance line");
        // The reported case: Chat history at its most (10,000), and tells, allegiance and fellowship
        // gone within the hour. 60,000 combat lines with 300 player lines among them, in polls.
        var rng = new Random(2211);
        ChatLine[] kept = Array.Empty<ChatLine>();
        long id = 0;
        var playerIds = new List<long>();
        string[] say = { "Bob tells you, \"inv\"", "[Allegiance] Ann says, \"gz\"", "Bob says to your fellowship, \"pull\"", "You tell Bob, \"omw\"" };
        int[] sayType = { 3, 18, 19, 4 };
        for (int poll = 0; poll < 6000; poll++)
        {
            var added = new List<ChatLine>();
            for (int k = 0; k < 10; k++) added.Add(PlayerTestLine(++id, "You slash Drudge Slinker for 54 points of slashing damage!", 22));
            if (poll % 20 == 0)
            {
                int s = rng.Next(say.Length);
                added.Add(PlayerTestLine(++id, say[s], sayType[s]));
                playerIds.Add(id);
            }
            kept = ChatHistory.MergeKeep(kept, added, 10000, 5000, IsPlayerLine);
        }
        var keptIds = new HashSet<long>(kept.Select(l => l.Id));
        Check(playerIds.All(keptIds.Contains), $"all {playerIds.Count} player lines are still kept");
        Check(kept.Length == 10000 + playerIds.Count(p => p <= id - 10000), $"the newest 10,000 plus the older player lines ({kept.Length})");
        bool ordered = true;
        for (int i = 1; i < kept.Length; i++) ordered &= kept[i].Id > kept[i - 1].Id;
        Check(ordered, "still in arrival order");
        // The Channels view of it: every player line, in order.
        var channels = kept.Where(l => l.Route.ShowsIn("Channels", l.Channel, l.IsPlayerChat)).Select(l => l.Id).ToList();
        Check(channels.SequenceEqual(playerIds), "the Channels tab shows them all, oldest first");
        // Before: the same feed through Merge kept only the player lines of the last 10,000.
        Check(playerIds.Count(p => p > id - 10000) < playerIds.Count / 2, "(the old Merge would have lost most of them)");
    }

    private static void PlayerChatViewDrops()
    {
        Case("chat view: a line the source dropped leaves the view even when an older one stays (FindGone)");
        var gone = new List<int>();
        Func<int, long> self = n => n;
        ChatHistory.FindGone(new[] { 2, 4, 5, 6, 8, 9 }, new[] { 2, 4, 6, 8, 9, 10 }, self, gone);
        Check(gone.SequenceEqual(new[] { 2 }), $"an interior line (5) is found ({string.Join(",", gone)})");
        ChatHistory.FindGone(new[] { 1, 2, 3, 4 }, new[] { 3, 4, 5 }, self, gone);
        Check(gone.SequenceEqual(new[] { 0, 1 }), "the old case, the oldest lines, still is");
        ChatHistory.FindGone(new[] { 1, 2 }, Array.Empty<int>(), self, gone);
        Check(gone.SequenceEqual(new[] { 0, 1 }), "an empty source drops everything");
        ChatHistory.FindGone(Array.Empty<int>(), new[] { 1 }, self, gone);
        Check(gone.Count == 0, "an empty view drops nothing");
        ChatHistory.FindGone(new[] { 3, 7 }, new[] { 1, 2, 3, 4, 5, 6, 7 }, self, gone);
        Check(gone.Count == 0, "all still there: nothing");
        // Through MergeKeep, as the view sees it: the All tab of evens-are-player lines.
        int[] before = ChatHistory.MergeKeep(Array.Empty<int>(), Enumerable.Range(1, 30).ToArray(), 10, 100, IsEven);
        int[] after = ChatHistory.MergeKeep(before, new[] { 31, 32, 33 }, 10, 100, IsEven);
        ChatHistory.FindGone(before, after, self, gone);
        Check(gone.Select(i => before[i]).SequenceEqual(new[] { 21, 23 }), $"the odd lines that left the main window, between kept even ones ({string.Join(",", gone.Select(i => before[i]))})");
    }

    private static void PlayerChatSettings()
    {
        Case("player chat settings: absent at the defaults, saved when changed");
        var opts = RynthChatJsonContext.Default.RynthChatSettingsDto;
        RynthChatSettingsDto old = JsonSerializer.Deserialize(OldFile, opts)!;
        Check(old.PlayerHistoryLines == null && old.ChannelsAllPlayerChat == null, "a file from before has neither (ChatModel: 5000, on)");
        string saved = JsonSerializer.Serialize(old, opts);
        Check(!saved.Contains("playerHistoryLines", StringComparison.Ordinal) && !saved.Contains("channelsAllPlayerChat", StringComparison.Ordinal),
            "saving it writes neither key");
        old.PlayerHistoryLines = 8000;
        old.ChannelsAllPlayerChat = false;
        RynthChatSettingsDto back = JsonSerializer.Deserialize(JsonSerializer.Serialize(old, opts), opts)!;
        Check(back.PlayerHistoryLines == 8000 && back.ChannelsAllPlayerChat == false, "8000 lines and off load back");
        Check(back.Filters!.Length == 3, "the other fields are untouched");
    }
}

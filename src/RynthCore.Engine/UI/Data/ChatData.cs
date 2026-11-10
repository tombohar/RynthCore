// ============================================================================
//  RynthCore.Engine - UI/Data/ChatData.cs
//  RynthChat's state, shared by both chat faces (ImGui/Panels/ChatFace.cs and
//  the Avalonia UI/Panels/RynthChatPanel.cs) and both filter editors:
//
//    • ChatSource (hub, 100 ms) pulls new lines from RynthChatGetScrollbackJson
//      on the pump thread, keeps the newest ChatModel.HistoryLines ("Chat
//      history", 2,000 by default; 500 before 2026-10-11, see ChatHistory.cs),
//      routes and colours them with the rules, and writes the chat log. It also hands each new line to the
//      Discord relay and ticks it (UI/Data/Discord/ChatDiscord.cs), which keeps
//      this source subscribed while it is on.
//    • ChatModel holds the settings (rynthchat_settings.json), the regex
//      rules and custom tabs, and the chat input line.
//
//  Rules (2026-09-29 overhaul). Each rule is a case-insensitive regex matched
//  against the line's text as AC printed it (no timestamp, so ^ and $ work),
//  or plain text (Contains / Starts with / Ends with, 2026-10-05; see
//  ChatRuleMatching.cs), optionally only for one channel or chat type. It can
//    Move  the line to a tab (it leaves All and its channel tab),
//    Copy  it to a tab as well (it stays where it was),
//    Hide  it everywhere, or
//    Colour it only,
//  and it can colour the whole line, just the matched text, or (2026-11-01)
//  highlight the line with a band behind it. Order: top to bottom. Copy rules
//  add up; the first matching Move or Hide decides and stops the routing. The
//  first matching line colour wins, and so does the first matching highlight
//  (on its own: a line can have both); matched-text colours add up, a higher
//  rule winning where two overlap.
//
//  Threads: rules are evaluated ONLY on the pump (ChatSource) - each new line
//  once, and every kept line again when the rules change (in 8 ms slices, newest
//  first, since the history can be 10,000 lines) - and the result is
//  stored on the line (ChatLine.Route). The faces just read routes; no regex
//  runs per frame. The one UI-thread evaluation is the rule editor's test box,
//  on a keystroke. Every regex has a 15 ms match timeout; a rule that times
//  out is marked Slow and skipped until it is edited, so a pathological
//  pattern costs one timeout, not one per line. A rule that never times out
//  but spends over 100 ms (per 500 lines) re-routing the kept lines is marked Slow too: the
//  pump is the plugin tick thread, and a slow re-route holds every plugin's
//  tick (ChatRouter.Charge).
//
//  The input line: while ChatCaptureActive, Win32Backend's WndProc hook turns
//  keys into the OnChat* callbacks on AC's main thread. ChatModel installs
//  them once at init (InstallInput), so typing works whichever face is on
//  screen; RynthChatSendLine runs there too (it feeds AC's chat parser). The
//  export is resolved by ChatSource on the pump, since PluginExportBinder
//  refuses AC's thread.
//
//  Nothing here touches Avalonia: EntryPoint calls ChatModel.EnsureSettingsLoaded
//  before the overlay starts (see the dispatcher invariant there).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.ImGuiBackend;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Data;

/// <summary>
/// New RynthChat lines while a chat face or the Discord relay holds it (100 ms). Publishes
/// the newest ChatModel.HistoryLines (the "Chat history" setting; 500 until 2026-10-11),
/// routed, and keeps RynthChat's own ring the same length (RynthChatSetCapacity).
/// </summary>
internal sealed unsafe class ChatSource : UiSource<ChatLine[]>
{
    private delegate* unmanaged[Cdecl]<ulong, IntPtr> _get;
    // RynthChat 0.3.0+: how many lines its ring keeps. Null for an older plugin (it keeps 500).
    private delegate* unmanaged[Cdecl]<int, int> _setCapacity;
    private int _sentCapacity;
    private ulong _sinceSeq;
    private long _nextId = 1;
    private bool _boundLogged;
    private long _routedVersion = -1;
    private string _routedName = "";
    // A re-route pass in progress (the rules changed): its router, and the Id below which the
    // kept lines still carry the old routes. It runs newest first, RerouteSliceMs per poll.
    private ChatRouter? _passRouter;
    private long _passBelowId;
    private const double RerouteSliceMs = 8;
    private static readonly long RerouteSliceTicks = (long)(Stopwatch.Frequency * RerouteSliceMs / 1000);

    public ChatSource() : base("Chat", periodMs: 100) { }

    protected internal override void Poll()
    {
        // Another client saved the shared settings: pick up its rules / filters (every 2 s at most).
        ChatModel.PollSettingsFile();
        try { PollLines(); }
        finally
        {
            // Every poll, new lines or not: due Discord posts, results, retries.
            try { Discord.ChatDiscord.PumpTick(); }
            catch (Exception ex) { RynthLog.UI($"ChatSource: Discord relay tick failed ({ex.GetType().Name}: {ex.Message})."); }
        }
    }

    private void PollLines()
    {
        if (_get == null)
        {
            _get = (delegate* unmanaged[Cdecl]<ulong, IntPtr>)PluginExportBinder.Resolve("RynthChat", "RynthChatGetScrollbackJson");
            if (_get != null && !_boundLogged)
            {
                _boundLogged = true;
                RynthLog.UI("ChatSource: bound RynthChat plugin exports.");
            }
        }
        if (ChatModel.SendLinePtr == IntPtr.Zero)
            ChatModel.SendLinePtr = PluginExportBinder.Resolve("RynthChat", "RynthChatSendLine");
        ChatModel.PluginBound = _get != null;
        int maxLines = ChatModel.HistoryLines;
        if (_get != null) SyncPluginCapacity(maxLines);
        ChatModel.PumpLog(null);

        // The rules (or the name mentions look for) changed: route the kept lines again.
        long version = ChatModel.FiltersVersion;
        string name = ChatModel.PumpCharacterName();
        ChatRouter router = ChatModel.PumpRouter(name);
        TrimToLength(maxLines);
        if (version != _routedVersion || name != _routedName)
        {
            _routedVersion = version;
            _routedName = name;
            _passRouter = router;
            _passBelowId = long.MaxValue;
            router.BeginPass(Current?.Value.Length ?? 0);
            router.EndPass();   // paused until its slice below
        }
        if (_passRouter != null) ContinueReroute(_passRouter);

        if (_get == null) return;

        IntPtr ptr = _get(_sinceSeq);
        if (ptr == IntPtr.Zero) return;
        string? json = Marshal.PtrToStringAnsi(ptr);
        if (string.IsNullOrEmpty(json) || json == "[]")
        {
            RelayRead(Array.Empty<ChatLine>());
            return;
        }

        RynthChatLineDto[]? dtos = ParseLines(json, out ulong skippedSeq);
        // Lines that couldn't be read still move the mark past them, or they'd come back every poll.
        if (skippedSeq > _sinceSeq) _sinceSeq = skippedSeq;
        if (dtos == null || dtos.Length == 0)
        {
            RelayRead(Array.Empty<ChatLine>());
            return;
        }

        var added = new List<ChatLine>(dtos.Length);
        foreach (RynthChatLineDto dto in dtos)
        {
            if (dto.Seq <= _sinceSeq) continue;
            _sinceSeq = dto.Seq;
            string text = StripAcLinks(dto.Text ?? "", out string? linkedName);
            var line = new ChatLine(_nextId++, dto.Ts ?? "", dto.Chan ?? "Other", dto.Type,
                                    dto.Sender is { Length: > 0 } s ? StripAcLinks(s, out _) : linkedName, text)
            {
                Seq = dto.Seq,
                // A plugin's own colour for it (ChatOwnColors), matched by the text as AC printed it.
                OwnArgb = ChatOwnColors.Lines.Take(dto.Text, dto.Type, Environment.TickCount64),
            };
            line.Route = router.Evaluate(line, null);
            if (line.Route.Mention) ChatModel.PumpMentionSound();
            added.Add(line);
        }
        if (added.Count == 0)
        {
            RelayRead(added);
            return;
        }

        ChatLine[] old = Current?.Value ?? Array.Empty<ChatLine>();
        Publish(ChatHistory.Merge(old, added, maxLines));
        ChatModel.PumpLog(added);
        RelayRead(added);
    }

    /// <summary>
    /// One time slice of re-routing the kept lines with <paramref name="router"/>, newest
    /// first. Up to 2,000 lines take one slice (5 ms, tools/ChatHistoryBench), as the whole
    /// pass did before; 10,000 take about four polls. Lines that arrive meanwhile are routed
    /// with the same router as they come in. The faces rebuild once, when the pass is done.
    /// </summary>
    private void ContinueReroute(ChatRouter router)
    {
        ChatLine[] kept = Current?.Value ?? Array.Empty<ChatLine>();
        int i = LastIndexBelow(kept, _passBelowId);
        long t0 = Stopwatch.GetTimestamp();
        int n = 0;
        router.ResumePass();
        try
        {
            while (i >= 0)
            {
                ChatLine line = kept[i--];
                line.Route = router.Evaluate(line, null);
                _passBelowId = line.Id;
                if ((++n & 31) == 0 && Stopwatch.GetTimestamp() - t0 > RerouteSliceTicks) break;
            }
        }
        finally { router.EndPass(); }
        if (i >= 0) return;   // more next poll
        _passRouter = null;
        ChatModel.BumpRoutes();
    }

    /// <summary>The index of the newest line whose Id is below <paramref name="id"/> (Ids increase), or -1.</summary>
    private static int LastIndexBelow(ChatLine[] lines, long id)
    {
        int lo = 0, hi = lines.Length;   // first index with Id >= id
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (lines[mid].Id < id) lo = mid + 1; else hi = mid;
        }
        return lo - 1;
    }

    /// <summary>The setting was lowered: drop the oldest kept lines now, not only when new ones arrive.</summary>
    private void TrimToLength(int maxLines)
    {
        ChatLine[]? kept = Current?.Value;
        if (kept != null && ChatHistory.Trim(kept, maxLines) is { } trimmed) Publish(trimmed);
    }

    /// <summary>Tells RynthChat (0.3.0+) the history length once it's bound and whenever the setting moves.</summary>
    private void SyncPluginCapacity(int maxLines)
    {
        if (_setCapacity == null)
        {
            _setCapacity = (delegate* unmanaged[Cdecl]<int, int>)PluginExportBinder.Resolve("RynthChat", "RynthChatSetCapacity");
            if (_setCapacity == null) return;   // an older RynthChat: its ring stays at 500
        }
        if (_sentCapacity == maxLines) return;
        int applied = _setCapacity(maxLines);
        if (applied <= 0) return;   // the plugin isn't up yet: try again next poll
        _sentCapacity = maxLines;
        RynthLog.UI($"ChatSource: RynthChat keeps {applied} lines (chat history {maxLines}).");
    }

    /// <summary>Every scrollback read goes to the Discord relay, empty or not (its first read is a baseline).</summary>
    private static void RelayRead(IReadOnlyList<ChatLine> added)
    {
        try { Discord.ChatDiscord.PumpRead(added); }
        catch (Exception ex) { RynthLog.UI($"ChatSource: Discord relay skipped {added.Count} line(s) ({ex.GetType().Name}: {ex.Message})."); }
    }

    private int _badLinesLogged;

    // AC's clickable-name markup: <Tell:IIDString:0:Chucky>Chucky<\Tell>. The retail chat
    // window renders it as a link; the panel printed it raw (fellowship, allegiance and
    // channel lines, 2026-09-30). Shown as the name alone.
    private static readonly System.Text.RegularExpressions.Regex AcTellLink = new(
        @"<Tell:IIDString:\d+:([^>]*)>(.*?)<\\Tell>",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));

    /// <summary>The text with AC's name links reduced to their display text; <paramref name="firstName"/> is the first linked name.</summary>
    internal static string StripAcLinks(string text, out string? firstName)
    {
        firstName = null;
        if (text.IndexOf("<Tell:", StringComparison.Ordinal) < 0) return text;
        try
        {
            string? first = null;
            string result = AcTellLink.Replace(text, m =>
            {
                string shown = m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[1].Value;
                first ??= m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : shown;
                return shown;
            });
            firstName = first;
            return result;
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return text;
        }
    }

    /// <summary>
    /// The scrollback batch. A line System.Text.Json refuses (a lone UTF-16
    /// surrogate, which a plugin's chat text can carry) used to fail the whole
    /// batch: _sinceSeq never moved, the same batch failed on every poll, and the
    /// chat stood still until that line left the plugin's 500-line ring. Now the
    /// batch is read line by line when it has to be; an unreadable line is
    /// skipped and its seq returned in <paramref name="skippedSeq"/>.
    /// </summary>
    private RynthChatLineDto[]? ParseLines(string json, out ulong skippedSeq)
    {
        skippedSeq = 0;
        try { return JsonSerializer.Deserialize(json, RynthChatJsonContext.Default.RynthChatLineDtoArray); }
        catch (Exception) { }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var list = new List<RynthChatLineDto>();
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                try
                {
                    RynthChatLineDto? dto = el.Deserialize(RynthChatJsonContext.Default.RynthChatLineDto);
                    if (dto != null) list.Add(dto);
                }
                catch (Exception ex)
                {
                    if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("seq", out JsonElement s)
                        && s.TryGetUInt64(out ulong seq) && seq > skippedSeq)
                        skippedSeq = seq;
                    if (_badLinesLogged++ < 5)
                        RynthLog.UI($"ChatSource: skipped a chat line it couldn't read ({ex.GetType().Name}: {ex.Message}).");
                }
            }
            return list.ToArray();
        }
        catch (Exception ex)
        {
            if (_badLinesLogged++ < 5)
                RynthLog.UI($"ChatSource: scrollback JSON unreadable ({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
    }

    protected internal override void Reset()
    {
        // RL loads a fresh plugin copy whose scrollback numbers from 1 again.
        // The lines already shown stay.
        _get = null;
        _setCapacity = null;
        _sentCapacity = 0;
        _sinceSeq = 0;
        Discord.ChatDiscord.PumpSourceReset();
        ChatModel.SendLinePtr = IntPtr.Zero;
        ChatModel.PluginBound = false;
    }
}

internal static class ChatModel
{
    /// <summary>Channel tabs; must match ChatClassifier in the plugin.</summary>
    // Items (2026-10-09): RynthAi's item info, "[RynthAi Item] " (RynthChat's ChatClassifier.IsItemInfo).
    public static readonly string[] BaseTabs = { "All", "Chat", "Channels", "System", "Combat", "Rynth", "Items", "Other" };

    /// <summary>Channel tab colour as 0xAARRGGBB: the user's (Line colours in the chat
    /// settings), else the built-in one (ChatLineColors.BuiltInTabArgb).</summary>
    public static uint ChannelArgb(string chan) => ChatLineColors.TabArgb(chan);

    /// <summary>The line's colour: a rule's line colour, else the one its plugin asked for, else
    /// the colour set for its kind (say, fellowship, [General]...), else its channel's. 0xAARRGGBB.</summary>
    public static uint LineArgb(ChatLine line) =>
        ChatLineColors.ResolveLine(line.Route.LineArgb, line.OwnArgb, line.ColorKind, line.Channel, line.ChatType);

    /// <summary>
    /// What a rule's "When" can be: (stored value, label). Channel tabs, then
    /// single ChatMessageTypes (ACE.Entity.Enum.ChatMessageType; retail's too).
    /// </summary>
    public static readonly (string Value, string Label)[] Conditions =
    {
        ("", "Any line"),
        ("chan:Chat", "Chat tab (say, tells, allegiance, fellowship)"),
        ("chan:Channels", "Channels tab (General, Trade, LFG...)"),
        ("chan:System", "System tab"),
        ("chan:Combat", "Combat tab (combat and magic)"),
        ("chan:Rynth", "Rynth tab (plugin output)"),
        ("chan:Items", "Items tab (item info)"),
        ("chan:Other", "Other tab"),
        ("type:3", "Tells to you"),
        ("type:4", "Tells you send"),
        ("type:2", "Say (local)"),
        ("type:12", "Emotes"),
        ("type:18", "Allegiance"),
        ("type:19", "Fellowship"),
        ("type:8", "Channels (General, Trade...)"),
        ("type:17", "Spell words"),
        ("type:7", "Magic"),
        ("type:22", "Your combat"),
        ("type:21", "Enemy combat"),
        ("type:13", "Advancement"),
        ("type:20", "World broadcast"),
        ("type:16", "Appraisal"),
        ("type:24", "Crafting"),
        ("type:25", "Salvaging"),
        ("type:23", "Recall"),
        ("type:5", "System messages"),
        ("type:26", "Notices (too busy, missing components)"),
    };

    /// <summary>The channel tab a ChatMessageType lands in; mirrors ChatClassifier.ChannelFor in the plugin.</summary>
    public static string ChannelForType(int chatType) => ChatRetailVerdict.ChannelForType(chatType);

    public static string ConditionLabel(string when)
    {
        foreach (var c in Conditions)
            if (string.Equals(c.Value, when, StringComparison.OrdinalIgnoreCase)) return c.Label;
        return when.Length == 0 ? "Any line" : when;
    }

    // ── Settings (any thread; saved with SaveSettings) ───────────────────

    public static volatile float FontSize = 10f;
    public static volatile int BackgroundAlpha = 0xF2;
    public static volatile bool AutoScroll = true;
    public static volatile bool LogEnabled;
    /// <summary>Docked chat lets clicks through unless Ctrl is held (mirrors the radar option).
    /// AvaloniaOverlay.ChatCtrlGatedClickThrough is the Avalonia hit-test gate; set both.</summary>
    public static volatile bool CtrlGatedClickThrough;
    public static volatile string ActiveChannel = "All";
    public static volatile bool ShowTimestamps = true;
    /// <summary>Highlight your character's name (and MentionWords) in chat and channel lines.</summary>
    public static volatile bool MentionHighlight = true;
    /// <summary>
    /// A line the rules or standard filters hide is also kept out of AC's own chat window, not
    /// just RynthChat's (ShouldHideInRetail, from PluginManager's held-line verdict).
    /// </summary>
    public static volatile bool HideInRetailChat = true;
    /// <summary>A short system beep on a new mention (at most every 3 s).</summary>
    public static volatile bool MentionSound;
    /// <summary>Extra words to highlight, comma-separated.</summary>
    public static volatile string MentionWords = "";
    /// <summary>How many lines the scrollback keeps, all tabs together (ChatHistory; "Chat history" in the gear menu).</summary>
    public static volatile int HistoryLines = ChatHistory.DefaultLines;
    /// <summary>The tabs in a row across the top (wrapping), as before 2026-10-09, instead of the channels drawer.</summary>
    public static volatile bool TabsAcross;

    public static void SetHistoryLines(int lines)
    {
        lines = ChatHistory.Clamp(lines);
        if (lines == HistoryLines) return;
        HistoryLines = lines;
        SaveSettings();
    }

    public static void SetCtrlGatedClickThrough(bool on)
    {
        CtrlGatedClickThrough = on;
        AvaloniaOverlay.ChatCtrlGatedClickThrough = on;
        SaveSettings();
    }

    // ── Rules and custom tabs ────────────────────────────────────────────
    //
    // The lists are replaced, never changed in place, so readers on other
    // threads can walk them; a rule's own fields are edited in place and then
    // FiltersChanged.

    private static readonly object EditLock = new();
    private static ChatFilterRule[] _filters = Array.Empty<ChatFilterRule>();
    private static string[] _customTabs = Array.Empty<string>();
    private static long _filtersVersion = 1;
    private static long _routesVersion = 1;
    private static (long Version, string[] Tabs) _allTabs;

    public static ChatFilterRule[] Filters => Volatile.Read(ref _filters);

    /// <summary>Bumped on every rule / tab / mention-setting change. The pump re-routes when it moves.</summary>
    public static long FiltersVersion => Interlocked.Read(ref _filtersVersion);

    /// <summary>Re-route without saving (a rule was just found too slow). Any thread.</summary>
    internal static void BumpFiltersVersion() => Interlocked.Increment(ref _filtersVersion);

    /// <summary>Bumped by the pump once the kept lines carry routes for the current rules; faces rebuild then.</summary>
    public static long RoutesVersion => Interlocked.Read(ref _routesVersion);
    internal static void BumpRoutes() => Interlocked.Increment(ref _routesVersion);

    /// <summary>Moves when the tabs or the routes change (the Avalonia face rebuilds on it).</summary>
    public static long ViewVersion => FiltersVersion + RoutesVersion;

    /// <summary>A rule just made from a chat line: the rule editor scrolls to it and flashes it.</summary>
    public static volatile ChatFilterRule? FocusRule;

    public static void EditFilters(Action<List<ChatFilterRule>> edit)
    {
        lock (EditLock)
        {
            var list = new List<ChatFilterRule>(_filters);
            edit(list);
            Volatile.Write(ref _filters, list.ToArray());
        }
        FiltersChanged();
    }

    /// <summary>Moves a rule one place up (-1) or down (+1). Order matters (see the header).</summary>
    public static void MoveFilter(ChatFilterRule rule, int delta) => EditFilters(list =>
    {
        int i = list.IndexOf(rule), j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        list.RemoveAt(i);
        list.Insert(j, rule);
    });

    /// <summary>After a rule's fields changed: re-route lines and save.</summary>
    public static void FiltersChanged()
    {
        Interlocked.Increment(ref _filtersVersion);
        SaveSettings();
    }

    /// <summary>Adds <paramref name="rule"/> at the end (its tab too) and asks the editor to show it.</summary>
    public static void AddRule(ChatFilterRule rule)
    {
        rule.Recompile();
        if (rule.Tab.Length > 0) EnsureCustomTab(rule.Tab);
        FocusRule = rule;
        EditFilters(list => list.Add(rule));
    }

    /// <summary>The rule a standard filter adds, if the list has it (any action, on or off).</summary>
    public static ChatFilterRule? FindPresetRule(ChatFilterPreset preset)
    {
        foreach (ChatFilterRule r in Filters)
            if (preset.SameAs(r.Mode, r.Pattern, r.When)) return r;
        return null;
    }

    /// <summary>
    /// Adds a standard filter as an ordinary Hide rule at the end (or turns on and shows the one
    /// already there). Returns a message for the user.
    /// </summary>
    public static string AddPresetRule(ChatFilterPreset preset)
    {
        ChatFilterRule? have = FindPresetRule(preset);
        if (have != null)
        {
            FocusRule = have;
            if (have.Enabled) return $"\"{preset.Label}\" is already in your rules.";
            have.Enabled = true;
            FiltersChanged();
            return $"\"{preset.Label}\" was off; it is on again.";
        }
        AddRule(new ChatFilterRule
        {
            Mode = preset.Mode,
            Pattern = preset.Pattern,
            When = preset.When,
            Action = ChatRuleAction.Hide,
        });
        return $"Added \"{preset.Label}\": it hides those lines (edit or delete it like any rule).";
    }

    /// <summary>
    /// A pattern for "lines like this": player speech keeps the speaker and the
    /// way they spoke ("^Bob tells you, \""); anything else is the whole line
    /// with each run of digits made \d+. A long line (an appraisal, a long
    /// broadcast) gives only its start, cut at a word: the whole of one could
    /// outgrow the rule editor's pattern box, which would cut it mid-escape.
    /// </summary>
    public static string PatternFor(ChatLine line)
    {
        const int MaxChars = 140;
        string t = line.Text;
        if (line.Sender != null)
        {
            int q = t.IndexOf(", \"", StringComparison.Ordinal);
            if (q > 0 && q < 120) return "^" + Escape(t[..(q + 3)]);
        }
        bool cut = t.Length > MaxChars;
        if (cut)
        {
            int space = t.LastIndexOf(' ', MaxChars - 1, MaxChars / 2);
            t = t[..(space > 0 ? space : MaxChars)];
            if (t.Length > 0 && char.IsHighSurrogate(t[^1])) t = t[..^1];
        }
        var sb = new StringBuilder("^");
        for (int i = 0; i < t.Length;)
        {
            int j = i;
            if (char.IsDigit(t[i]))
            {
                while (j < t.Length && char.IsDigit(t[j])) j++;
                sb.Append(@"\d+");
            }
            else
            {
                while (j < t.Length && !char.IsDigit(t[j])) j++;
                sb.Append(Escape(t[i..j]));
            }
            i = j;
        }
        return cut ? sb.ToString() : sb.Append('$').ToString();
    }

    // Regex.Escape escapes spaces too, which is legal but hard to read.
    private static string Escape(string s) => Regex.Escape(s).Replace("\\ ", " ");

    /// <summary>
    /// Every tab: the channel tabs, then the created ones, then any named by an
    /// enabled Move/Copy rule that isn't a created tab (a rule set up in the
    /// Avalonia editor, or imported). Cached per filter version. Any thread.
    /// </summary>
    public static string[] AllTabs()
    {
        long version = FiltersVersion;
        var cached = _allTabs;
        if (cached.Tabs != null && cached.Version == version) return cached.Tabs;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tabs = new List<string>();
        foreach (string t in BaseTabs) if (seen.Add(t)) tabs.Add(t);
        foreach (string t in Volatile.Read(ref _customTabs)) if (seen.Add(t)) tabs.Add(t);
        foreach (ChatFilterRule rule in Filters)
            if (rule.Enabled && rule.Routes && rule.Tab.Length > 0 && seen.Add(rule.Tab)) tabs.Add(rule.Tab);
        foreach (string t in RynthCore.Engine.UI.Panels.RynthChatPresets.TargetTabs())
            if (seen.Add(t)) tabs.Add(t);
        string[] result = tabs.ToArray();
        _allTabs = (version, result);
        return result;
    }

    /// <summary>The tabs a Move/Copy rule can send lines to: every tab but All.</summary>
    public static string[] TargetTabs() => AllTabs().Where(t => t != "All").ToArray();

    public static bool IsBaseTab(string tab) => BaseTabs.Contains(tab, StringComparer.OrdinalIgnoreCase);

    /// <summary>The tab to show: the active one, or All when it no longer exists.</summary>
    public static string CurrentTab()
    {
        string active = ActiveChannel;
        return AllTabs().Contains(active, StringComparer.OrdinalIgnoreCase) ? active : "All";
    }

    public static void SelectTab(string tab)
    {
        if (string.Equals(ActiveChannel, tab, StringComparison.Ordinal)) return;
        ActiveChannel = tab;
        SaveSettings();
    }

    /// <summary>Creates a tab (or finds the existing one) and switches to it.</summary>
    public static void AddCustomTab(string name) => AddCustomTab(name, select: true);

    /// <summary>Creates a tab (or finds the existing one); returns its name, or null for a blank name.</summary>
    public static string? AddCustomTab(string name, bool select)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        string? existing = AllTabs().FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
        if (existing == null || !IsBaseTab(existing)) EnsureCustomTab(existing ?? name);
        if (select) ActiveChannel = existing ?? name;
        SaveSettings();
        return existing ?? name;
    }

    private static void EnsureCustomTab(string name)
    {
        if (IsBaseTab(name)) return;
        bool added = false;
        lock (EditLock)
        {
            if (!_customTabs.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                Volatile.Write(ref _customTabs, _customTabs.Append(name).ToArray());
                added = true;
            }
        }
        if (added) Interlocked.Increment(ref _filtersVersion);
    }

    /// <summary>Renames a created tab and the rules that send lines to it. False when the name is taken or blank.</summary>
    public static bool RenameCustomTab(string tab, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || IsBaseTab(tab)) return false;
        if (!string.Equals(tab, newName, StringComparison.OrdinalIgnoreCase)
            && AllTabs().Contains(newName, StringComparer.OrdinalIgnoreCase)) return false;
        lock (EditLock)
        {
            string[] tabs = _customTabs.Select(t => string.Equals(t, tab, StringComparison.OrdinalIgnoreCase) ? newName : t).ToArray();
            if (!tabs.Contains(newName)) tabs = tabs.Append(newName).ToArray();
            Volatile.Write(ref _customTabs, tabs);
        }
        foreach (ChatFilterRule rule in Filters)
            if (string.Equals(rule.Tab, tab, StringComparison.OrdinalIgnoreCase))
                rule.Tab = newName;
        foreach (var preset in RynthCore.Engine.UI.Panels.RynthChatPresets.All)
            if (string.Equals(preset.Tab, tab, StringComparison.OrdinalIgnoreCase))
                preset.Tab = newName;
        if (string.Equals(ActiveChannel, tab, StringComparison.OrdinalIgnoreCase)) ActiveChannel = newName;
        // The extra chat windows showing it follow (their tabs are saved in the panel layout).
        RynthCore.Engine.ImGuiBackend.Panels.ChatWindowFace.OnTabRenamed(tab, newName);
        FiltersChanged();
        return true;
    }

    /// <summary>Deletes a created tab and disables the rules that fill it, so it
    /// doesn't come back through AllTabs and its lines don't vanish.</summary>
    public static void DeleteCustomTab(string tab)
    {
        lock (EditLock)
            Volatile.Write(ref _customTabs,
                _customTabs.Where(t => !string.Equals(t, tab, StringComparison.OrdinalIgnoreCase)).ToArray());
        foreach (ChatFilterRule rule in Filters)
            if (rule.Action is ChatRuleAction.Move or ChatRuleAction.Copy
                && string.Equals(rule.Tab, tab, StringComparison.OrdinalIgnoreCase))
                rule.Enabled = false;
        foreach (var preset in RynthCore.Engine.UI.Panels.RynthChatPresets.All)
            if (string.Equals(preset.Tab, tab, StringComparison.OrdinalIgnoreCase))
                preset.Enabled = false;
        FiltersChanged();
    }

    /// <summary>Is the line shown under <paramref name="tab"/> (rules applied, no search)?</summary>
    public static bool LineInTab(ChatLine line, string tab) => line.Route.ShowsIn(tab, line.Channel);

    public static bool LineVisible(ChatLine line, string tab, string search) =>
        LineInTab(line, tab)
        && (search.Length == 0 || line.FormattedText.Contains(search, StringComparison.OrdinalIgnoreCase));

    // ── Mentions ─────────────────────────────────────────────────────────

    /// <summary>Only what other players say mentions you: chat and channel lines, never your own.</summary>
    internal static bool MentionEligible(string channel, int chatType) =>
        (channel == "Chat" || channel == "Channels") && chatType is not (4 or 9 or 11);

    private static string? _pumpName;
    private static (long Version, string Name, ChatRouter Router)? _pumpRouter;
    private static long _lastBeepTicks;

    [DllImport("user32.dll")] private static extern bool MessageBeep(uint uType);

    /// <summary>Your character's name, "" until known. Pump thread (reads the launch context once).</summary>
    internal static string PumpCharacterName()
    {
        string live = SessionStateRegistry.LastCharacterName;
        if (!string.IsNullOrWhiteSpace(live)) return live.Trim();
        return _pumpName ??= TryReadCharacterName() ?? "";
    }

    /// <summary>The router for the current rules and mention settings. Pump thread; rebuilt when they change.</summary>
    internal static ChatRouter PumpRouter(string name)
    {
        long version = FiltersVersion;
        var cached = _pumpRouter;
        if (cached is { } c && c.Version == version && c.Name == name) return c.Router;
        var router = new ChatRouter(Filters, BuildMentionRegex(name), name, countHits: true);
        _pumpRouter = (version, name, router);
        return router;
    }

    // The retail-chat verdict's own router: no hit counting (RynthChat's pass already counts the
    // same line), rebuilt when the rules or the name change. Pump thread only, like PumpRouter.
    private static (long Version, string Name, ChatRouter Router)? _retailRouter;

    /// <summary>
    /// True when the rules or standard filters hide this incoming line, so AC shouldn't print
    /// it either (HideInRetailChat). Pump thread (PluginManager, before the held line's verdict).
    /// The channel is worked out the way the RynthChat plugin files the line (ChatRetailVerdict).
    /// </summary>
    internal static bool ShouldHideInRetail(string? text, int chatType)
    {
        if (!HideInRetailChat || string.IsNullOrEmpty(text)) return false;
        string line = ChatSource.StripAcLinks(text, out _);
        if (line.Length == 0) return false;

        string name = PumpCharacterName();
        long version = FiltersVersion;
        var cached = _retailRouter;
        ChatRouter router;
        if (cached is { } c && c.Version == version && c.Name == name) router = c.Router;
        else
        {
            router = new ChatRouter(Filters, mention: null, name, countHits: false);
            _retailRouter = (version, name, router);
        }
        return ChatRetailVerdict.IsHidden(router, line, chatType);
    }

    /// <summary>A router for the rule editor's test box (UI thread, on an edit).</summary>
    public static ChatRouter TestRouter()
    {
        string name = SessionStateRegistry.LastCharacterName;
        if (string.IsNullOrWhiteSpace(name)) name = _pumpName ?? "";
        return new ChatRouter(Filters, BuildMentionRegex(name.Trim()), name, countHits: false);
    }

    private static Regex? BuildMentionRegex(string name)
    {
        if (!MentionHighlight) return null;
        var words = new List<string>();
        if (!string.IsNullOrWhiteSpace(name)) words.Add(name.Trim());
        foreach (string w in (MentionWords ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!words.Contains(w, StringComparer.OrdinalIgnoreCase)) words.Add(w);
        if (words.Count == 0) return null;
        string alt = string.Join("|", words.Select(Regex.Escape));
        return new Regex($@"(?<![\w'])(?:{alt})(?![\w])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(ChatFilterRule.TimeoutMs));
    }

    /// <summary>A new line mentions you. Pump thread.</summary>
    internal static void PumpMentionSound()
    {
        if (!MentionSound) return;
        long now = Environment.TickCount64;
        if (now - _lastBeepTicks < 3000) return;
        _lastBeepTicks = now;
        try { MessageBeep(0x40); } catch { }   // MB_ICONASTERISK; queued, returns at once
    }

    // ── Rule sets (import / export) ──────────────────────────────────────

    /// <summary>The rules and the created tabs as JSON, for the clipboard.</summary>
    public static string ExportRules()
    {
        var dto = new RynthChatRuleSetDto
        {
            Format = RynthChatRuleSetDto.FormatName,
            Tabs = Volatile.Read(ref _customTabs),
            Rules = Filters.Select(ToDto).ToArray(),
        };
        return JsonSerializer.Serialize(dto, RynthChatJsonContext.Default.RynthChatRuleSetDto);
    }

    /// <summary>
    /// Adds the rules in <paramref name="json"/> (an export, a whole
    /// rynthchat_settings.json, or a bare rule array) after the current ones.
    /// Returns a message for the user.
    /// </summary>
    public static string ImportRules(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "Clipboard is empty.";
        RynthChatFilterDto[]? rules = null;
        string[]? tabs = null;
        try
        {
            json = json.Trim();
            if (json.StartsWith('['))
                rules = JsonSerializer.Deserialize(json, RynthChatJsonContext.Default.RynthChatFilterDtoArray);
            else
            {
                var set = JsonSerializer.Deserialize(json, RynthChatJsonContext.Default.RynthChatRuleSetDto);
                rules = set?.Rules;
                tabs = set?.Tabs;
                if (rules == null)
                {
                    var settings = JsonSerializer.Deserialize(json, RynthChatJsonContext.Default.RynthChatSettingsDto);
                    rules = settings?.Filters;
                    tabs = settings?.CustomTabs;
                }
            }
        }
        catch (JsonException ex) { return "Not a rule set: " + ex.Message; }
        if (rules == null || rules.Length == 0) return "No rules found in the clipboard.";
        var imported = rules.Select(FromDto).ToList();
        foreach (string t in tabs ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(t)) EnsureCustomTab(t.Trim());
        foreach (ChatFilterRule r in imported)
            if (r.Tab.Length > 0 && r.Action is ChatRuleAction.Move or ChatRuleAction.Copy) EnsureCustomTab(r.Tab);
        EditFilters(list => list.AddRange(imported));
        return $"Imported {imported.Count} rule{(imported.Count == 1 ? "" : "s")}.";
    }

    private static RynthChatFilterDto ToDto(ChatFilterRule f) => new()
    {
        Enabled = f.Enabled,
        Pattern = f.Pattern,
        Tab = f.Tab,
        Action = f.Action switch
        {
            ChatRuleAction.Copy => "copy",
            ChatRuleAction.Hide => "hide",
            ChatRuleAction.Color => "color",
            _ => "move",
        },
        When = f.When.Length > 0 ? f.When : null,
        // "highlight" since 2026-11-01; an older RynthCore reads it as no colour (ChatColorModes.Parse).
        ColorMode = ChatColorModes.ToJson(f.ColorMode),
        // Kept while the rule is set to "No colour" too, so switching colour back on
        // after a relaunch finds the colour that was picked.
        Color = f.ColorMode != ChatColorMode.None || f.ColorArgb != ChatFilterRule.DefaultColor ? FormatColor(f.ColorArgb) : null,
        // Absent for a Regex rule, so a file without plain-text rules is written as before.
        Mode = ChatRuleMatching.ToJson(f.Mode),
    };

    /// <summary>
    /// A rule from its JSON. Rules saved before 2026-09-29 have no "action": an
    /// empty tab hid the line and a tab moved it (while leaving it under All,
    /// which is why a "Junk" rule seemed to do nothing there; a Move now takes
    /// it out of All too).
    /// </summary>
    private static ChatFilterRule FromDto(RynthChatFilterDto f)
    {
        string tab = (f.Tab ?? "").Trim();
        var rule = new ChatFilterRule
        {
            Enabled = f.Enabled,
            Pattern = f.Pattern ?? "",
            Tab = tab,
            Action = (f.Action ?? "").ToLowerInvariant() switch
            {
                "move" => ChatRuleAction.Move,
                "copy" => ChatRuleAction.Copy,
                "hide" => ChatRuleAction.Hide,
                "color" or "colour" => ChatRuleAction.Color,
                _ => tab.Length == 0 ? ChatRuleAction.Hide : ChatRuleAction.Move,
            },
            When = f.When ?? "",
            ColorMode = ChatColorModes.Parse(f.ColorMode),
            ColorArgb = ParseColor(f.Color) ?? ChatFilterRule.DefaultColor,
            // Absent (every file before 2026-10-05): Regex, as those rules always were.
            Mode = ChatRuleMatching.Parse(f.Mode),
        };
        rule.Recompile();
        return rule;
    }

    /// <summary>
    /// "#RRGGBB" or "#AARRGGBB" to 0xAARRGGBB (opaque when no alpha is given).
    /// An alpha of 00 is taken as opaque: a colour that makes the text invisible
    /// is never what a rule wants (hiding is the Hide action).
    /// </summary>
    public static uint? ParseColor(string? s) => ChatLineColors.ParseColor(s);

    /// <summary>0xAARRGGBB as "#RRGGBB", or "#AARRGGBB" when it isn't opaque (an imported colour keeps its alpha).</summary>
    public static string FormatColor(uint argb) => ChatLineColors.FormatColor(argb);

    // ── Plugin binding (set by ChatSource on the pump) ───────────────────

    public static volatile bool PluginBound;
    private static IntPtr _sendLinePtr;
    public static IntPtr SendLinePtr
    {
        get => Volatile.Read(ref _sendLinePtr);
        set => Volatile.Write(ref _sendLinePtr, value);
    }

    // ── Shown ────────────────────────────────────────────────────────────

    private static int _shown;

    /// <summary>
    /// A chat face went on or off screen. Whether RynthChat is shown decides who
    /// owns Enter and whether the retail chatbox is hidden (ChatHooks.RynthChatOwnsChat).
    /// Counted, since the ImGui face and the popped-out Avalonia face hand over
    /// in either order. Any thread.
    /// </summary>
    public static void SetShown(bool shown)
    {
        int n = shown ? Interlocked.Increment(ref _shown) : Interlocked.Decrement(ref _shown);
        if (n < 0) { Interlocked.Exchange(ref _shown, 0); n = 0; }
        ChatHooks.ChatPanelShown = n > 0;
        if (n == 0 && Win32Backend.ChatCaptureActive)
        {
            // Stop swallowing keys for a closed panel; the half-typed line stays, as after Escape.
            Win32Backend.ChatCaptureActive = false;
        }
    }

    // ── Input line (AC's main thread: the WndProc hook) ──────────────────

    private sealed record InputState(string Text, int Cursor, string? Hint);
    private static InputState _input = new("", 0, null);
    private static readonly List<string> History = new();
    private static int _historyIndex = -1;
    private static long _inputVersion;
    private static bool _inputInstalled;

    public static string InputText => Volatile.Read(ref _input).Text;
    public static int InputCursor => Volatile.Read(ref _input).Cursor;
    /// <summary>Shown in the empty input box instead of the usual hint (e.g. "select an NPC first").</summary>
    public static string? InputHint => Volatile.Read(ref _input).Hint;
    public static long InputVersion => Interlocked.Read(ref _inputVersion);

    /// <summary>Raised on AC's main thread after the input line changes.</summary>
    public static event Action? InputChanged;

    private static void SetInput(string text, int cursor, string? hint)
    {
        Volatile.Write(ref _input, new InputState(text, Math.Clamp(cursor, 0, text.Length), hint));
        Interlocked.Increment(ref _inputVersion);
        try { InputChanged?.Invoke(); }
        catch (Exception ex) { RynthLog.UI($"ChatModel: InputChanged handler threw {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Clicking the input box starts typing, like Enter. AC's main thread.</summary>
    public static void BeginTyping()
    {
        if (Win32Backend.ChatCaptureActive || !ChatHooks.RynthChatOwnsChat) return;
        Win32Backend.ChatCaptureActive = true;
        OnActivated();
    }

    /// <summary>Hooks the chat keys up to the input line. Once, at init.</summary>
    public static void InstallInput()
    {
        if (_inputInstalled) return;
        _inputInstalled = true;
        Win32Backend.OnChatCaptureActivated = OnActivated;
        Win32Backend.OnChatTellSelected = () => BeginTellToSelected();
        Win32Backend.OnChatChar = c =>
        {
            // Ctrl+Backspace arrives as WM_CHAR 0x7F (DEL): it used to go into the line
            // as an invisible character and be sent with it.
            if (c == (char)0x7F) return;
            InputState s = _input;
            _historyIndex = -1;
            SetInput(s.Text[..s.Cursor] + c + s.Text[s.Cursor..], s.Cursor + 1, null);
        };
        Win32Backend.OnChatBackspace = () =>
        {
            InputState s = _input;
            if (s.Cursor > 0) SetInput(s.Text[..(s.Cursor - 1)] + s.Text[s.Cursor..], s.Cursor - 1, null);
        };
        Win32Backend.OnChatDelete = () =>
        {
            InputState s = _input;
            if (s.Cursor < s.Text.Length) SetInput(s.Text[..s.Cursor] + s.Text[(s.Cursor + 1)..], s.Cursor, null);
        };
        Win32Backend.OnChatLeft = () => { InputState s = _input; SetInput(s.Text, s.Cursor - 1, null); };
        Win32Backend.OnChatRight = () => { InputState s = _input; SetInput(s.Text, s.Cursor + 1, null); };
        Win32Backend.OnChatHome = () => SetInput(_input.Text, 0, null);
        Win32Backend.OnChatEnd = () => SetInput(_input.Text, _input.Text.Length, null);
        Win32Backend.OnChatUp = () =>
        {
            if (History.Count == 0) return;
            _historyIndex = _historyIndex < 0 ? History.Count - 1 : Math.Max(0, _historyIndex - 1);
            string text = History[_historyIndex];
            SetInput(text, text.Length, null);
        };
        Win32Backend.OnChatDown = () =>
        {
            if (_historyIndex < 0) return;
            string text;
            if (_historyIndex < History.Count - 1) text = History[++_historyIndex];
            else { _historyIndex = -1; text = ""; }
            SetInput(text, text.Length, null);
        };
        // The line has no selection: copy and cut take all of it, paste goes in at the caret.
        Win32Backend.OnChatPaste = () =>
        {
            InputState s = _input;
            string text = ChatInputPaste.Insert(s.Text, s.Cursor, GetClipboardText(), out int cursor);
            if (ReferenceEquals(text, s.Text)) return;
            _historyIndex = -1;
            SetInput(text, cursor, null);
        };
        Win32Backend.OnChatCopy = () =>
        {
            string text = _input.Text;
            if (text.Length > 0) SetClipboardText(text);
        };
        Win32Backend.OnChatCut = () =>
        {
            string text = _input.Text;
            if (text.Length == 0 || !SetClipboardText(text)) return;
            _historyIndex = -1;
            SetInput("", 0, null);
        };
        Win32Backend.OnChatSend = OnSend;
        // Escape leaves the line, as retail's chat does: the half-typed text stays in the box
        // (it used to be erased), and Enter or a click on the box carries on typing it.
        Win32Backend.OnChatCancel = () =>
        {
            _historyIndex = -1;
            InputState s = _input;
            SetInput(s.Text, s.Cursor, null);
        };
    }

    /// <summary>
    /// Typing starts again (Enter, or a click on the box): the text left there by Escape is
    /// kept, with the caret at its end. A sent line was already cleared by OnSend.
    /// </summary>
    private static void OnActivated()
    {
        _historyIndex = -1;
        string text = _input.Text;
        SetInput(text, text.Length, null);
    }

    /// <summary>
    /// Starts "/tell &lt;selected name&gt;, " and keeps typing. AC's main thread
    /// (the Tell button via WM_RYNTHCORE_TELL, or "/tell" alone in OnSend), where
    /// the selection and its name read live.
    /// </summary>
    private static bool BeginTellToSelected()
    {
        if (!TryGetSelectedTellName(out string name))
        {
            SetInput("", 0, "Select an NPC or player in the game first, then Tell.");
            return false;
        }
        BeginTellTo(name);
        return true;
    }

    /// <summary>Starts "/tell &lt;name&gt;, " and keeps typing. AC's main thread.</summary>
    private static void BeginTellTo(string name)
    {
        string text = $"/tell {name}, ";
        _historyIndex = -1;
        Win32Backend.ChatCaptureActive = true;   // keys now go to this line
        SetInput(text, text.Length, null);
    }

    private static void AddHistory(string text)
    {
        if (text.Length == 0 || (History.Count > 0 && History[^1] == text)) return;
        History.Add(text);
        if (History.Count > 100) History.RemoveAt(0);
    }

    // Win32Backend has already cleared ChatCaptureActive. RynthChatSendLine runs
    // here, on AC's main thread, so its CallWindowProcA does too.
    private static unsafe void OnSend()
    {
        string text = _input.Text.Trim();
        // "/tell" or "/t" alone addresses the selected NPC or player instead of
        // being sent (AC would only answer with the usage line).
        if (text.Equals("/tell", StringComparison.OrdinalIgnoreCase) || text.Equals("/t", StringComparison.OrdinalIgnoreCase))
        {
            if (!BeginTellToSelected()) _historyIndex = -1;
            return;
        }
        _historyIndex = -1;
        // /reply and /retell (TellMemory.cs; the dispatcher turns them into a /tell). Alone they
        // start "/tell <name>, " the way "/tell" alone does; with no one to address, the box
        // says so and the line stays in history (Up brings it back).
        TellShortcut shortcut = TellMemory.Parse(text, out string tellMessage);
        if (shortcut != TellShortcut.None)
        {
            string? name = TellMemory.Shared.TargetFor(shortcut);
            if (name == null)
            {
                AddHistory(text);
                SetInput("", 0, shortcut == TellShortcut.Reply ? TellMemory.NoTellerMessage : TellMemory.NoTelleeMessage);
                return;
            }
            if (tellMessage.Length == 0)
            {
                BeginTellTo(name);
                return;
            }
        }
        AddHistory(text);
        SetInput("", 0, null);
        IntPtr send = SendLinePtr;
        if (text.Length == 0 || send == IntPtr.Zero) return;
        IntPtr ptr = Marshal.StringToHGlobalAnsi(text);
        // RynthChat sends the line through Host.InvokeChatParser, on this thread: an engine /rc
        // command in it is the player's, so it answers in chat (RynthCoreChatCommands.PlayerLine).
        try
        {
            using (RynthCoreChatCommands.PlayerLine())
                ((delegate* unmanaged[Cdecl]<IntPtr, void>)send)(ptr);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>The creature selected in the game (an NPC or another player), for /tell.
    /// AC's main thread only. Never yourself, never an item.</summary>
    private static bool TryGetSelectedTellName(out string name)
    {
        name = "";
        uint id = SelectedTargetHooks.ReadCurrentSelectedId();
        if (id == 0 || id == ClientHelperHooks.GetPlayerId()) return false;
        const uint ItemTypeCreature = 0x00000010;
        if (ClientObjectHooks.TryGetItemType(id, out uint flags) && (flags & ItemTypeCreature) == 0) return false;
        return ClientObjectHooks.TryGetObjectName(id, out name) && !string.IsNullOrWhiteSpace(name);
    }

    // ── Clipboard ────────────────────────────────────────────────────────

    // Written directly as CF_UNICODETEXT: Avalonia's clipboard service is
    // unreliable inside acclient (no proper TopLevel owner).
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);

    public static unsafe bool SetClipboardText(string text)
    {
        const uint CF_UNICODETEXT = 13;
        const uint GMEM_MOVEABLE = 0x0002;
        // The clipboard is shared: another app may hold it briefly.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    int bytes = (text.Length + 1) * 2;
                    IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                    if (hMem == IntPtr.Zero) return false;
                    IntPtr dst = GlobalLock(hMem);
                    if (dst == IntPtr.Zero) { GlobalFree(hMem); return false; }
                    fixed (char* src = text)
                        Buffer.MemoryCopy(src, (void*)dst, bytes, text.Length * 2);
                    ((char*)dst)[text.Length] = '\0';
                    GlobalUnlock(hMem);
                    if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
                    {
                        GlobalFree(hMem);   // ownership not taken
                        return false;
                    }
                    return true;            // the system owns hMem now
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(10);
        }
        return false;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr hMem);

    /// <summary>The clipboard's text, or null (empty, not text, or busy). Rule import.</summary>
    public static unsafe string? GetClipboardText()
    {
        const uint CF_UNICODETEXT = 13;
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT) || !OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            IntPtr h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try
            {
                int maxChars = (int)Math.Min((ulong)GlobalSize(h) / 2, 1_000_000);
                int n = 0;
                char* c = (char*)p;
                while (n < maxChars && c[n] != '\0') n++;
                return new string(c, 0, n);
            }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>"Copied N lines", or the busy message.</summary>
    public static string CopyLines(IReadOnlyList<string> lines)
    {
        bool ok = SetClipboardText(string.Join("\r\n", lines));
        return ok ? $"Copied {lines.Count} line{(lines.Count == 1 ? "" : "s")}" : "Clipboard busy — try again";
    }

    // ── Chat log (pump thread, ChatSource) ───────────────────────────────

    private static string? _logCharacterName;
    private static StreamWriter? _logWriter;

    /// <summary>Writes <paramref name="lines"/> when logging is on; closes the file when it's off.</summary>
    internal static void PumpLog(List<ChatLine>? lines)
    {
        if (!LogEnabled)
        {
            if (_logWriter != null) { try { _logWriter.Close(); } catch { } _logWriter = null; }
            return;
        }
        if (lines == null || lines.Count == 0) return;
        try
        {
            // The character playing now: after logging in as another character in the
            // same client, the log used to carry on in the first one's file.
            string name = PumpCharacterName();
            if (name.Length == 0) return;
            if (_logWriter != null && !string.Equals(name, _logCharacterName, StringComparison.Ordinal))
            {
                try { _logWriter.Close(); } catch { }
                _logWriter = null;
            }
            if (_logWriter == null)
            {
                _logCharacterName = name;
                string file = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "ChatLogs");
                Directory.CreateDirectory(dir);
                // Shared for writing: an engine generation left behind by a hot reload
                // (it can't always close the file) must not lock the next one out.
                var stream = new FileStream(Path.Combine(dir, $"{file}.log"), FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                _logWriter = new StreamWriter(stream, System.Text.Encoding.UTF8);
            }
            foreach (ChatLine line in lines) _logWriter.WriteLine(line.FormattedText);
            _logWriter.Flush();
        }
        catch (Exception ex)
        {
            RynthLog.UI($"ChatModel: chat log write failed: {ex.Message}");
            try { _logWriter?.Close(); } catch { }
            _logWriter = null;
        }
    }

    /// <summary>
    /// Closes the chat log. Engine shutdown, after the tick pump (PumpLog's
    /// thread) has stopped: the file stayed open in the old generation after a
    /// hot reload, and the new one couldn't open it (every write failed).
    /// </summary>
    internal static void CloseLog()
    {
        StreamWriter? w = _logWriter;
        _logWriter = null;
        try { w?.Close(); } catch { }
    }

    private static string? TryReadCharacterName()
    {
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore");
            string perProcPath = Path.Combine(root, "launch_contexts", $"launch_context_{Environment.ProcessId}.json");
            string path = File.Exists(perProcPath) ? perProcPath : Path.Combine(root, "launch_context.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.TryGetProperty("TargetCharacter", out JsonElement tc))
            {
                string? name = tc.GetString();
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
            return null;
        }
        catch { return null; }
    }

    // ── Settings persistence ─────────────────────────────────────────────
    //
    // %AppData%\RynthCore\rynthchat_settings.json. Version 2 (2026-09-29) adds
    // "version", showTimestamps, the mention settings, and per rule "action",
    // "when", "colorMode" and "color". A version-1 file loads as before (see
    // FromDto for what its rules become); the first load copies it to
    // rynthchat_settings.v1.bak, and the next save writes version 2.
    // "lineColors" (2026-10-08, ChatLineColors) is written only once a colour
    // is set, so it needs no version bump: a file without it loads as before.
    // "colorMode": "highlight" (2026-11-01) needs none either: "colorMode" is a
    // string, and a RynthCore without it reads an unknown value as no colour.

    public const int SettingsVersion = 2;
    private static readonly object SaveLock = new();
    private static int _settingsLoaded;

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "rynthchat_settings.json");

    /// <summary>Loads the settings (incl. "Hide retail chat") once. Safe before the overlay starts.</summary>
    public static void EnsureSettingsLoaded()
    {
        if (Interlocked.Exchange(ref _settingsLoaded, 1) != 0) return;
        LoadSettings();
    }

    // Every client shares rynthchat_settings.json and saves its whole state, so a client that
    // never re-read the file wrote back stale presets/rules over another client's change (last
    // writer won; a ticked standard filter came back off). Each client now re-reads the file
    // when someone else changes it (PollSettingsFile). SharedFileWatch remembers this client's own
    // last write, so its own saves don't count as a change.
    private static readonly SharedFileWatch SettingsWatch = new(pollMs: 2000);

    /// <summary>
    /// Pump thread (ChatSource.Poll): reloads the settings when another client saved the file.
    /// Rules, standard filters, tabs and display options follow; this client's selected tab stays.
    /// </summary>
    internal static void PollSettingsFile()
    {
        if (Volatile.Read(ref _settingsLoaded) == 0) return;
        try
        {
            string? json = SettingsWatch.Changed(Environment.TickCount64, SettingsPath);   // null: nobody else saved
            if (json == null) return;
            RynthLog.UI("ChatModel: rynthchat_settings.json changed (another client saved it); reloading the rules and filters.");
            LoadSettings(json, reload: true);
        }
        catch (Exception ex) { RynthLog.UI($"ChatModel: settings re-read failed ({ex.GetType().Name}: {ex.Message})."); }
    }

    private static void LoadSettings()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path)) return;
            SettingsWatch.Seen(File.GetLastWriteTimeUtc(path));
            LoadSettings(File.ReadAllText(path), reload: false);
        }
        catch (Exception ex)
        {
            RynthLog.UI($"ChatModel: settings load failed: {ex.Message}");
            KeepUnreadable();
        }
    }

    /// <param name="reload">Another client changed the file: presets it doesn't list go off, and
    /// this client's selected tab is kept.</param>
    private static void LoadSettings(string json, bool reload)
    {
        try
        {
            string path = SettingsPath;
            var dto = JsonSerializer.Deserialize(json, RynthChatJsonContext.Default.RynthChatSettingsDto);
            if (dto == null) return;

            if (dto.Version < SettingsVersion)
            {
                try
                {
                    string backup = Path.ChangeExtension(path, $".v{Math.Max(1, dto.Version)}.bak");
                    if (!File.Exists(backup)) File.Copy(path, backup);
                }
                catch (Exception ex) { RynthLog.UI($"ChatModel: settings backup failed: {ex.Message}"); }
                RynthLog.UI($"ChatModel: migrating rynthchat_settings.json v{Math.Max(1, dto.Version)} -> v{SettingsVersion} ({dto.Filters?.Length ?? 0} rules).");
            }

            FontSize = (float)Math.Clamp(dto.FontSize <= 0 ? 10 : dto.FontSize, 8, 18);
            BackgroundAlpha = Math.Clamp(dto.BackgroundAlpha, 0, 255);
            AutoScroll = dto.AutoScroll;
            ChatHooks.SuppressOriginalChat = dto.SuppressChat;
            LogEnabled = dto.LogEnabled;
            CtrlGatedClickThrough = dto.CtrlGatedClickThrough;
            AvaloniaOverlay.ChatCtrlGatedClickThrough = dto.CtrlGatedClickThrough;
            ShowTimestamps = dto.ShowTimestamps;
            MentionHighlight = dto.MentionHighlight;
            HideInRetailChat = dto.HideInRetail ?? true;   // absent before 2026-10-16: on
            MentionSound = dto.MentionSound;
            MentionWords = dto.MentionWords ?? "";
            HistoryLines = ChatHistory.Clamp(dto.HistoryLines ?? 0);   // absent before 2026-10-11: the default
            TabsAcross = dto.TabsAcross ?? false;   // absent before 2026-10-20 (and while off): the drawer
            ChatLineColors.Load(dto.LineColors);   // absent before 2026-10-08: nothing set
            ChatLineColors.RetailColors = dto.RetailColors ?? true;   // absent before 2026-10-28 (and while on): on

            var tabs = new List<string>();
            foreach (string t in dto.CustomTabs ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(t) && !IsBaseTab(t.Trim())
                    && !tabs.Contains(t.Trim(), StringComparer.OrdinalIgnoreCase)) tabs.Add(t.Trim());
            var rules = new List<ChatFilterRule>();
            foreach (RynthChatFilterDto f in dto.Filters ?? Array.Empty<RynthChatFilterDto>())
            {
                ChatFilterRule rule = FromDto(f);
                rules.Add(rule);
                // A tab typed into a rule used to exist only while the rule was on:
                // make it a real tab, so turning the rule off doesn't drop the tab.
                if (rule.Action is ChatRuleAction.Move or ChatRuleAction.Copy && rule.Tab.Length > 0
                    && !IsBaseTab(rule.Tab) && !tabs.Contains(rule.Tab, StringComparer.OrdinalIgnoreCase))
                    tabs.Add(rule.Tab);
            }
            // The file lists only presets that are on or moved; on a reload every other one goes back off.
            if (reload)
                foreach (var preset in RynthCore.Engine.UI.Panels.RynthChatPresets.All)
                {
                    preset.Enabled = false;
                    preset.Tab = "";
                }
            foreach (RynthChatPresetDto p in dto.Presets ?? Array.Empty<RynthChatPresetDto>())
            {
                var preset = p.Id == null ? null : RynthCore.Engine.UI.Panels.RynthChatPresets.Get(p.Id);
                if (preset == null) continue;   // a preset a later build removed
                preset.Enabled = p.Enabled;
                preset.Tab = (p.Tab ?? "").Trim();
                if (preset.Tab.Length > 0 && !IsBaseTab(preset.Tab)
                    && !tabs.Contains(preset.Tab, StringComparer.OrdinalIgnoreCase))
                    tabs.Add(preset.Tab);
            }
            _customTabs = tabs.ToArray();
            _filters = rules.ToArray();
            Interlocked.Increment(ref _filtersVersion);

            if (!reload && !string.IsNullOrEmpty(dto.ActiveChannel) && AllTabs().Contains(dto.ActiveChannel, StringComparer.OrdinalIgnoreCase))
                ActiveChannel = dto.ActiveChannel;
            else if (reload && !AllTabs().Contains(ActiveChannel, StringComparer.OrdinalIgnoreCase))
                ActiveChannel = "All";   // the tab this client was on was removed elsewhere
        }
        catch (Exception ex) when (reload)
        {
            // A half-written or broken file from another client: keep what this client has.
            RynthLog.UI($"ChatModel: settings re-read skipped (unreadable: {ex.Message}).");
        }
    }

    /// <summary>
    /// The settings file didn't load: keep a copy before the next save writes the
    /// defaults over it, so the rules and tabs in it can be recovered (Import
    /// takes a whole settings file).
    /// </summary>
    private static void KeepUnreadable()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path)) return;
            string copy = Path.ChangeExtension(path, $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(path, copy, overwrite: false);
            RynthLog.UI($"ChatModel: kept the unreadable settings as {Path.GetFileName(copy)}.");
        }
        catch (Exception ex) { RynthLog.UI($"ChatModel: couldn't keep the unreadable settings: {ex.Message}"); }
    }

    /// <summary>Saves in the background (a rule edit saves on every keystroke). Any thread.</summary>
    public static void SaveSettings()
    {
        var dto = new RynthChatSettingsDto
        {
            Version = SettingsVersion,
            FontSize = FontSize,
            BackgroundAlpha = BackgroundAlpha,
            AutoScroll = AutoScroll,
            SuppressChat = ChatHooks.SuppressOriginalChat,
            LogEnabled = LogEnabled,
            CtrlGatedClickThrough = CtrlGatedClickThrough,
            ActiveChannel = ActiveChannel,
            ShowTimestamps = ShowTimestamps,
            MentionHighlight = MentionHighlight,
            HideInRetail = HideInRetailChat,
            MentionSound = MentionSound,
            MentionWords = MentionWords,
            // Left out at the default, so a file follows the default if it ever changes.
            HistoryLines = HistoryLines == ChatHistory.DefaultLines ? null : HistoryLines,
            TabsAcross = TabsAcross ? true : null,
            LineColors = ChatLineColors.ToJson(),   // null (left out) until a colour is set
            RetailColors = ChatLineColors.RetailColors ? null : false,   // left out while on (the default)
            CustomTabs = Volatile.Read(ref _customTabs),
            Filters = Filters.Select(ToDto).ToArray(),
            // Only presets the user touched: new presets in later builds start off.
            Presets = RynthCore.Engine.UI.Panels.RynthChatPresets.All
                .Where(p => p.Enabled || p.Tab.Length > 0)
                .Select(p => new RynthChatPresetDto { Id = p.Id, Enabled = p.Enabled, Tab = p.Tab.Length > 0 ? p.Tab : null })
                .ToArray(),
        };
        UiBackgroundWriter.Enqueue("persist chat settings", () =>
        {
            lock (SaveLock)
            {
                // Written beside it and swapped in: the client dying mid-write (a rule
                // edit saves on every keystroke) used to leave a cut-off file, which
                // loaded as nothing and was then saved over with the defaults.
                string path = SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp = $"{path}.{Environment.ProcessId}.tmp";   // every client shares the file
                try
                {
                    string json = JsonSerializer.Serialize(dto, RynthChatJsonContext.Default.RynthChatSettingsDto);
                    // Before the swap: PollSettingsFile must see this text as ours, not another client's.
                    SettingsWatch.Saved(json);
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, path, overwrite: true);
                }
                catch
                {
                    try { File.Delete(tmp); } catch { }
                    throw;   // UiBackgroundWriter logs it
                }
            }
        });
    }
}

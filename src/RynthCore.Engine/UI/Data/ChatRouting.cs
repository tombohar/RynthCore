// ============================================================================
//  RynthCore.Engine - UI/Data/ChatRouting.cs
//  RynthChat's lines and rules: ChatLine (one scrollback line), ChatRoute (the
//  rules' verdict for it), ChatFilterRule and ChatRouter (which evaluates the
//  rules). Moved out of ChatData.cs (2026-10-11) unchanged, so tools can run
//  the real classes without the engine (tools/ChatHistoryBench). How the rules
//  work and on which thread: see the header of ChatData.cs.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.UI.Data;

/// <summary>One scrollback line. Immutable except its Route, which the pump replaces.</summary>
internal sealed class ChatLine
{
    public ChatLine(long id, string ts, string chan, int chatType, string? sender, string text)
    {
        Id = id;
        Timestamp = ts;
        Channel = chan;
        ChatType = chatType;
        Sender = sender;
        Text = text;
        ColorKind = ChatLineColors.KindOf(chatType, chan, text);
        ChatLineColors.NoteKind(ColorKind);
        // The text already names the speaker ("Bob says, ..."); until 2026-09-29
        // the sender was printed in front of it as well ("Bob: Bob says, ...").
        FormattedText = $"{ts} {text}";
    }

    /// <summary>Engine-assigned, increasing for the whole session (the plugin's own
    /// seq restarts after RL).</summary>
    public long Id { get; }
    /// <summary>RynthChat's own number for the line (restarts when its plugin reloads); 0 when not
    /// from RynthChat. The Discord relay tells news from scrollback by it.</summary>
    public ulong Seq { get; init; }
    public string Timestamp { get; }
    /// <summary>The channel tab it belongs to (ChatClassifier in the plugin).</summary>
    public string Channel { get; }
    /// <summary>AC's ChatMessageType; -1 from RynthChat before 0.2.0.</summary>
    public int ChatType { get; }
    /// <summary>Who spoke, for player speech; metadata only (mentions, rules from a line).</summary>
    public string? Sender { get; }
    /// <summary>The line as AC printed it. Rules match this.</summary>
    public string Text { get; }
    /// <summary>Which line colour setting applies ("say", "chan:General", ...), or null:
    /// its tab's colour (ChatLineColors.KindOf, worked out once here).</summary>
    public string? ColorKind { get; }
    /// <summary>"HH:mm:ss text": copy, log and the Avalonia face.</summary>
    public string FormattedText { get; }

    private ChatRoute _route = ChatRoute.Default;

    /// <summary>Where the rules put this line and how it's coloured. Written by the
    /// pump (before the line is published, and again when the rules change); any thread reads.</summary>
    public ChatRoute Route
    {
        get => Volatile.Read(ref _route);
        internal set => Volatile.Write(ref _route, value);
    }
}

/// <summary>A run of the line's Text drawn in its own colour (0xAARRGGBB).</summary>
internal readonly record struct ChatSpan(int Start, int Length, uint Argb);

/// <summary>The rules' verdict for one line. Immutable.</summary>
internal sealed class ChatRoute
{
    public static readonly ChatRoute Default = new(false, true, null, null, null, false);

    public ChatRoute(bool hidden, bool inHome, string[]? tabs, uint? lineArgb, ChatSpan[]? spans, bool mention)
    {
        Hidden = hidden;
        InHome = inHome;
        Tabs = tabs;
        LineArgb = lineArgb;
        Spans = spans;
        Mention = mention;
    }

    /// <summary>A Hide rule matched: shown nowhere.</summary>
    public bool Hidden { get; }
    /// <summary>Shown under All and its channel tab (false once a Move rule took it away).</summary>
    public bool InHome { get; }
    /// <summary>Tabs a Move or Copy rule sent it to.</summary>
    public string[]? Tabs { get; }
    /// <summary>Whole-line colour from a rule; null = the channel colour.</summary>
    public uint? LineArgb { get; }
    /// <summary>Matched-text colours: offsets into Text, sorted, not overlapping.</summary>
    public ChatSpan[]? Spans { get; }
    /// <summary>Someone said your name (or a mention word).</summary>
    public bool Mention { get; }

    public bool ShowsIn(string tab, string channel)
    {
        if (Hidden) return false;
        if (InHome && (tab == "All" || string.Equals(tab, channel, StringComparison.OrdinalIgnoreCase))) return true;
        if (Tabs != null)
            foreach (string t in Tabs)
                if (string.Equals(t, tab, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

internal enum ChatRuleAction
{
    /// <summary>Only in the rule's tab: leaves All and its channel tab.</summary>
    Move,
    /// <summary>Also in the rule's tab.</summary>
    Copy,
    /// <summary>Nowhere.</summary>
    Hide,
    /// <summary>No routing: the rule only colours.</summary>
    Color,
}

internal enum ChatColorMode { None, Line, Match }

/// <summary>
/// One rule. Its fields are edited in place by a rule editor (then
/// ChatModel.FiltersChanged); the pump reads each field once per line, and a
/// half-applied edit is re-routed by the version bump that follows it.
/// </summary>
internal sealed class ChatFilterRule
{
    public const int TimeoutMs = 15;

    public volatile bool Enabled = true;
    public volatile string Pattern = "";
    /// <summary>The Move / Copy target. Empty: a Move or Copy rule routes nowhere (it can still colour).</summary>
    public volatile string Tab = "";
    public volatile ChatRuleAction Action = ChatRuleAction.Move;
    public volatile ChatColorMode ColorMode = ChatColorMode.None;
    /// <summary>How Pattern is read: a regex (the default, and every rule saved before
    /// 2026-10-05) or plain text. Recompile after changing it.</summary>
    public volatile ChatMatchMode Mode = ChatMatchMode.Regex;
    /// <summary>A new rule's colour (amber).</summary>
    public const uint DefaultColor = 0xFFFFD27A;
    /// <summary>0xAARRGGBB.</summary>
    public volatile uint ColorArgb = DefaultColor;

    private volatile WhenSpec _when = WhenSpec.Any;
    /// <summary>"" any line; "chan:Combat" one channel tab; "type:3" one ChatMessageType (see ChatModel.Conditions).</summary>
    public string When
    {
        get => _when.Raw;
        set => _when = WhenSpec.Parse(value);
    }

    /// <summary>Kept lines this rule matched (recounted when the rules change, then
    /// counted up as lines arrive). Written by the pump; the editor shows it.</summary>
    public int Hits;

    public volatile Regex? Compiled;
    /// <summary>The pattern doesn't compile (or is empty).</summary>
    public volatile bool Invalid;
    /// <summary>Why it doesn't compile; null when it does or is just empty.</summary>
    public volatile string? Error;
    /// <summary>A match timed out: skipped until the pattern is edited.</summary>
    public volatile bool Slow;

    /// <summary>Compiles the pattern once (after every pattern edit). Any thread.</summary>
    public void Recompile()
    {
        Slow = false;
        // A Regex rule compiles exactly as before; plain text is escaped (ChatRuleMatching).
        Regex? rx = ChatRuleMatching.Compile(Mode, Pattern, TimeoutMs, out string? error);
        Compiled = rx;
        Error = error;
        Invalid = rx == null;
    }

    /// <summary>Does the rule route (Move / Copy with a tab, or Hide)?</summary>
    public bool Routes => Action == ChatRuleAction.Hide
        || (Action is ChatRuleAction.Move or ChatRuleAction.Copy && Tab.Length > 0);

    internal bool WhenMatches(string channel, int chatType)
    {
        WhenSpec w = _when;
        if (w.Channel != null) return string.Equals(w.Channel, channel, StringComparison.OrdinalIgnoreCase);
        if (w.Type >= 0) return w.Type == chatType;
        return true;
    }

    private sealed class WhenSpec
    {
        public static readonly WhenSpec Any = new("", null, -1);
        public readonly string Raw;
        public readonly string? Channel;
        public readonly int Type;
        private WhenSpec(string raw, string? channel, int type) { Raw = raw; Channel = channel; Type = type; }

        public static WhenSpec Parse(string? raw)
        {
            raw = (raw ?? "").Trim();
            (string? channel, int type) = ChatRuleMatching.ParseWhen(raw);
            return channel == null && type < 0 ? Any : new WhenSpec(raw, channel, type);
        }
    }
}

/// <summary>
/// Evaluates the rules for one line: a snapshot of the rule list plus the
/// mention regex. Built by ChatModel.PumpRouter (pump) or for the editor's
/// test box (UI thread). One thread per router: each one's cost counters are
/// its own (the pump's lives on the pump; the test box builds a fresh one).
/// </summary>
internal sealed class ChatRouter
{
    /// <summary>Mentions colour the name like this (amber).</summary>
    public const uint MentionArgb = 0xFFFFC857;

    // The 15 ms timeout only catches a pattern that is slow on one line. One that
    // costs a millisecond or two per line never times out, yet re-routing the 500
    // kept lines (on every keystroke in the rule editor) then holds the pump -
    // which is the plugin tick thread - for a second or more. So during a
    // re-route pass each rule's matching time is added up, and a rule that uses
    // more than PassBudgetMs is marked Slow like a timed-out one. An ordinary
    // rule takes a few microseconds a line, a few ms for the pass. One reading
    // counts at most MaxChargeMs, so a GC pause or a descheduled thread in the
    // middle of a match doesn't condemn the rule on its own.
    // Since the history length became a setting (2026-10-11, up to 10,000 lines) the
    // budget is PassBudgetMs per 500 kept lines, so a longer history doesn't condemn a
    // rule sooner; the pump re-routes in time slices (ChatSource), so a pass no longer
    // holds the tick thread for its whole length either.
    private const double PassBudgetMs = 100, MaxChargeMs = 30;
    private const int PassBudgetLines = 500;
    private static readonly long PassBudgetTicks = (long)(Stopwatch.Frequency * PassBudgetMs / 1000);
    private static readonly long MaxChargeTicks = (long)(Stopwatch.Frequency * MaxChargeMs / 1000);

    /// <summary>ChatMessageType Emote (0x0C).</summary>
    private const int EmoteType = 0x0C;

    private readonly ChatFilterRule[] _rules;
    private readonly Regex? _mention;
    private readonly string _self;
    private readonly bool _countHits;
    private readonly long[] _passTicks;
    private long _passBudgetTicks = PassBudgetTicks;
    private bool _inPass;

    /// <param name="self">Your character's name ("" unknown): your own emotes don't mention you.</param>
    /// <param name="countHits">Count matches into ChatFilterRule.Hits (the pump's router; not the test box's).</param>
    public ChatRouter(ChatFilterRule[] rules, Regex? mention, string self, bool countHits)
    {
        _rules = rules;
        _mention = mention;
        _self = self.Trim();
        _countHits = countHits;
        _passTicks = new long[rules.Length];
    }

    /// <summary>
    /// Before routing the <paramref name="lines"/> kept lines again: hit counts restart, and
    /// each rule's time is watched (PassBudgetMs per PassBudgetLines lines, never less than
    /// PassBudgetMs).
    /// </summary>
    public void BeginPass(int lines = PassBudgetLines)
    {
        ResetHits();
        Array.Clear(_passTicks);
        _passBudgetTicks = PassBudgetTicks * Math.Max(PassBudgetLines, lines) / PassBudgetLines;
        _inPass = true;
    }

    /// <summary>The pass is over, or paused between two time slices (lines routed meanwhile aren't charged).</summary>
    public void EndPass() => _inPass = false;

    /// <summary>The next time slice of a pass begun with BeginPass.</summary>
    public void ResumePass() => _inPass = true;

    /// <summary>Adds a rule's matching time during a pass; marks it Slow past the budget. False once it's Slow.</summary>
    private bool Charge(int i, long ticks, List<Step>? trace)
    {
        if (!_inPass) return true;
        _passTicks[i] += Math.Min(ticks, MaxChargeTicks);
        if (_passTicks[i] <= _passBudgetTicks) return true;
        MarkSlow(_rules[i], trace, i, $"over {PassBudgetMs:0} ms per {PassBudgetLines} lines to re-route the kept lines");
        return false;
    }

    /// <summary>Before routing every kept line again.</summary>
    public void ResetHits()
    {
        foreach (ChatFilterRule rule in _rules) Volatile.Write(ref rule.Hits, 0);
    }

    /// <summary>One step of the editor's test: rule index (-1 = mention, -2 = a standard filter) and what it did.</summary>
    public readonly record struct Step(int Rule, string What);

    /// <summary><see cref="Step.Rule"/> of a standard-filter (RynthChatPresets) step.</summary>
    public const int StandardFilterStep = -2;

    public ChatRoute Evaluate(ChatLine line, List<Step>? trace) =>
        Evaluate(line.Text, line.Channel, line.ChatType, trace);

    public ChatRoute Evaluate(string text, string channel, int chatType, List<Step>? trace)
    {
        bool hidden = false, inHome = true, routed = false;
        List<string>? tabs = null;
        uint? lineArgb = null;
        List<(int Start, int Length, uint Argb)>? spans = null;

        for (int i = 0; i < _rules.Length; i++)
        {
            ChatFilterRule rule = _rules[i];
            if (!rule.Enabled || rule.Slow) continue;
            Regex? rx = rule.Compiled;
            if (rx == null) continue;
            ChatRuleAction action = rule.Action;
            ChatColorMode mode = rule.ColorMode;
            string tab = rule.Tab;
            bool routes = action == ChatRuleAction.Hide
                || (action is ChatRuleAction.Move or ChatRuleAction.Copy && tab.Length > 0);
            bool wantsRoute = routes && !routed;
            bool wantsLine = mode == ChatColorMode.Line && lineArgb == null;
            bool wantsMatch = mode == ChatColorMode.Match;
            // Nothing left for it to decide (the test box still shows that it matches).
            if (!wantsRoute && !wantsLine && !wantsMatch && trace == null) continue;
            if (!rule.WhenMatches(channel, chatType)) continue;

            Match m;
            long t0 = Stopwatch.GetTimestamp();
            try { m = rx.Match(text); }
            catch (RegexMatchTimeoutException) { MarkSlow(rule, trace, i, $"a match took over {ChatFilterRule.TimeoutMs} ms"); continue; }
            if (!Charge(i, Stopwatch.GetTimestamp() - t0, trace)) continue;
            if (!m.Success) continue;
            if (_countHits) Interlocked.Increment(ref rule.Hits);

            var what = trace != null ? new StringBuilder() : null;
            if (wantsRoute)
            {
                switch (action)
                {
                    case ChatRuleAction.Hide:
                        hidden = routed = true;
                        what?.Append("hides it");
                        break;
                    case ChatRuleAction.Move:
                        inHome = false;
                        routed = true;
                        AddTab(ref tabs, tab);
                        what?.Append("moves it to ").Append(tab);
                        break;
                    case ChatRuleAction.Copy:
                        AddTab(ref tabs, tab);
                        what?.Append("copies it to ").Append(tab);
                        break;
                }
            }
            else if (routes && trace != null)
                what!.Append("(a Move/Hide above already decided the tab)");
            if (hidden)
            {
                trace?.Add(new Step(i, what!.ToString()));
                break;
            }
            if (wantsLine)
            {
                lineArgb = rule.ColorArgb;
                what?.Append(what.Length > 0 ? ", " : "").Append("colours the line");
            }
            else if (mode == ChatColorMode.Line && trace != null)
                what!.Append(what.Length > 0 ? ", " : "").Append("(line colour already set above)");
            if (wantsMatch)
            {
                int n = 0;
                long t1 = Stopwatch.GetTimestamp();
                try
                {
                    for (; m.Success && n < 16; m = m.NextMatch())
                        if (m.Length > 0) { (spans ??= new()).Add((m.Index, m.Length, rule.ColorArgb)); n++; }
                }
                catch (RegexMatchTimeoutException) { MarkSlow(rule, trace, i, $"a match took over {ChatFilterRule.TimeoutMs} ms"); }
                if (!rule.Slow) Charge(i, Stopwatch.GetTimestamp() - t1, trace);
                what?.Append(what.Length > 0 ? ", " : "").Append(n == 1 ? "colours the match" : $"colours {n} matches");
            }
            if (what is { Length: 0 }) what.Append("matches, but has nothing to do yet (no tab, no colour)");
            trace?.Add(new Step(i, what!.ToString()));
        }

        // Standard filters (RynthChatPresets) decide only what the custom rules left undecided.
        if (!routed)
        {
            var preset = RynthCore.Engine.UI.Panels.RynthChatPresets.FirstMatch(text, channel);
            if (preset != null)
            {
                string presetTab = preset.Tab;
                routed = true;
                if (presetTab.Length == 0)
                {
                    hidden = true;
                    trace?.Add(new Step(StandardFilterStep, $"standard filter \"{preset.Label}\" hides it"));
                }
                else
                {
                    inHome = false;
                    AddTab(ref tabs, presetTab);
                    trace?.Add(new Step(StandardFilterStep, $"standard filter \"{preset.Label}\" moves it to {presetTab}"));
                }
            }
        }

        bool mention = false;
        if (!hidden && _mention != null && ChatModel.MentionEligible(channel, chatType))
        {
            try
            {
                Match m = _mention.Match(text);
                for (int n = 0; m.Success && n < 8; m = m.NextMatch(), n++)
                {
                    // Your own emote prints as "Tom waves." (type Emote, your name first): that's you, not a mention.
                    if (m.Index == 0 && chatType == EmoteType && _self.Length > 0
                        && string.Equals(m.Value, _self, StringComparison.OrdinalIgnoreCase)) continue;
                    mention = true;
                    (spans ??= new()).Add((m.Index, m.Length, MentionArgb));
                }
            }
            catch (RegexMatchTimeoutException) { }
            if (mention) trace?.Add(new Step(-1, "mentions you (highlighted)"));
        }

        if (!hidden && inHome && tabs == null && lineArgb == null && spans == null && !mention)
            return ChatRoute.Default;
        return new ChatRoute(hidden, inHome, tabs?.ToArray(), lineArgb, Resolve(spans, text.Length), mention);
    }

    private static void AddTab(ref List<string>? tabs, string tab)
    {
        tabs ??= new List<string>(2);
        foreach (string t in tabs) if (string.Equals(t, tab, StringComparison.OrdinalIgnoreCase)) return;
        tabs.Add(tab);
    }

    private static void MarkSlow(ChatFilterRule rule, List<Step>? trace, int index, string why)
    {
        if (!rule.Slow)
        {
            rule.Slow = true;
            RynthLog.UI($"ChatModel: rule \"{rule.Pattern}\" is too slow ({why}) and is skipped until edited.");
            // Lines already routed with it are routed again without it.
            ChatModel.BumpFiltersVersion();
        }
        trace?.Add(new Step(index, "too slow - now skipped until edited"));
    }

    /// <summary>Overlapping spans: the earlier one (a higher rule) keeps its characters.</summary>
    private static ChatSpan[]? Resolve(List<(int Start, int Length, uint Argb)>? spans, int textLength)
    {
        if (spans == null || spans.Count == 0) return null;
        if (spans.Count == 1)
            return new[] { new ChatSpan(spans[0].Start, spans[0].Length, spans[0].Argb) };
        var owner = new int[textLength];   // 0 = free, else span index + 1
        for (int s = 0; s < spans.Count; s++)
        {
            int end = Math.Min(textLength, spans[s].Start + spans[s].Length);
            for (int c = spans[s].Start; c < end; c++)
                if (owner[c] == 0) owner[c] = s + 1;
        }
        var result = new List<ChatSpan>();
        for (int c = 0; c < textLength;)
        {
            int o = owner[c];
            int start = c;
            while (c < textLength && owner[c] == o) c++;
            if (o != 0) result.Add(new ChatSpan(start, c - start, spans[o - 1].Argb));
        }
        return result.ToArray();
    }
}

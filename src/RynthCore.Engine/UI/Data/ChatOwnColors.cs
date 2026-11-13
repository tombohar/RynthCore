// ============================================================================
//  RynthCore.Engine - UI/Data/ChatOwnColors.cs
//  A plugin's own colour for a chat line it writes (2026-11-05). A player asked
//  for RynthAi's item info in, say, allegiance orange without the line moving
//  to where allegiance lines go: the chat type picks the tab, AC's own chat
//  window and its colour, so the colour has to travel on its own.
//
//  No API table change (any engine since v64 can be asked): the plugin calls
//    SendPluginCommand("RynthCore.Chat", "nextcolor", "#RRGGBB")
//  and then WriteToChat(text, chatType) on the same thread. The engine arms the
//  colour for that thread (ArmWindowMs), the next WriteToChat takes it and
//  notes (text, type, colour) here, and when RynthChat's copy of the line
//  reaches ChatSource it is drawn in that colour (ChatLine.OwnArgb). The line
//  keeps its chat type: the same tab, the same rules, the same retail window
//  (which can only show the type's colour). An engine without this finds no
//  plugin called "RynthCore.Chat" and answers 0; the plugin then just writes
//  the line, as before. "nextcolor" with "" or "none" disarms.
//
//  Matching is by the text and chat type (RynthChat passes both through
//  unchanged; a line AC cut at 1,024 characters matches by its start). Each
//  noted line colours one arriving line, oldest first. Lines nobody picked up
//  (RynthChat not loaded, the chat face closed for a long time, AC refused the
//  line) are forgotten after LineTtlMs, and at most MaxLines are kept.
//
//  Colours per part of the line (2026-11-11, "nextspans"): a player asked for
//  RynthAi's one-line item info with its name, armour, spells, value ... each
//  in a colour of its own. The same one-shot way, on the same channel (no API
//  change):
//    SendPluginCommand("RynthCore.Chat", "nextspans", "start:length:RRGGBB;...")
//  arms colour runs for the next WriteToChat on this thread. The payload is
//  1..MaxSpans entries "start:length:RRGGBB" joined by ';': start and length
//  are plain decimal UTF-16 offsets into the text the plugin then writes
//  (length >= 1), the colour six hex digits ('#' in front allowed); entries in
//  increasing order and not overlapping. Anything else is refused (answers 0,
//  nothing armed, a log line); "" / "none" disarms (answers 1, so a plugin can
//  ask whether the engine has this). At WriteToChat a run that doesn't fit
//  inside the text drops all of them (logged). Text outside every run keeps
//  the line's colour (nextcolor's, else as usual), so a plugin sends nextcolor
//  first as the fallback: an engine without nextspans answers 0 to it and the
//  line still shows in that one colour. A rule's line colour wins over the
//  runs, a rule's match colour over the run it overlaps (EffectiveSpans).
//
//  Pure (no engine state), so tools/ChatFilterTests compiles it in.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;

namespace RynthCore.Engine.UI.Data;

/// <summary>Lines a plugin wrote with a colour of their own, waiting for RynthChat's copy (any thread).</summary>
internal sealed class ChatOwnColorQueue
{
    /// <summary>A noted line nobody picked up is forgotten after this long.</summary>
    public const long LineTtlMs = 5 * 60_000;
    /// <summary>At most this many lines wait; the oldest goes first.</summary>
    public const int MaxLines = 256;
    /// <summary>AC's longest incoming chat line (ChatCallbackHooks.MaxIncomingChatChars): a longer line arrives cut.</summary>
    public const int MaxArrivingChars = 1024;

    // Argb 0 = no whole-line colour (only runs).
    private readonly record struct Entry(string Text, int ChatType, uint Argb, ChatSpan[]? Spans, long AtMs);

    private readonly object _lock = new();
    private readonly LinkedList<Entry> _lines = new();
    private int _count;   // read without the lock: the fast "nothing waiting" check

    public int Count => Volatile.Read(ref _count);

    /// <summary>The key a line is matched by: its text as AC printed it (CR / LF trimmed, as WriteToChat does).</summary>
    public static string Key(string text) => text.TrimEnd('\r', '\n');

    /// <summary>The plugin is about to write <paramref name="text"/> as <paramref name="chatType"/>, drawn in <paramref name="argb"/>.</summary>
    public void Expect(string text, int chatType, uint argb, long nowMs) => Expect(text, chatType, argb, null, nowMs);

    /// <summary>
    /// As above, with an optional whole-line colour and optional colour runs (offsets into
    /// <paramref name="text"/>, checked by <see cref="ChatOwnColors.FitSpans"/>). Nothing to note when both are null.
    /// </summary>
    public void Expect(string text, int chatType, uint? argb, ChatSpan[]? spans, long nowMs)
    {
        string key = Key(text);
        if (key.Length == 0 || (argb == null && spans == null)) return;
        lock (_lock)
        {
            Expire(nowMs);
            while (_lines.Count >= MaxLines) _lines.RemoveFirst();
            _lines.AddLast(new Entry(key, chatType, argb is uint a ? a | 0xFF000000 : 0u, spans, nowMs));
            Volatile.Write(ref _count, _lines.Count);
        }
    }

    /// <summary>WriteToChat refused the line: it never arrives, so it colours nothing (the newest such entry goes).</summary>
    public void Forget(string text, int chatType)
    {
        string key = Key(text);
        lock (_lock)
        {
            for (var n = _lines.Last; n != null; n = n.Previous)
                if (n.Value.ChatType == chatType && n.Value.Text == key)
                {
                    _lines.Remove(n);
                    break;
                }
            Volatile.Write(ref _count, _lines.Count);
        }
    }

    /// <summary>
    /// The colour for an arriving line, or null: the oldest waiting entry with this text and chat
    /// type is used up. <paramref name="chatType"/> -1 (RynthChat before 0.2.0) matches any type.
    /// </summary>
    public uint? Take(string? text, int chatType, long nowMs) => Take(text, chatType, nowMs, out _);

    /// <summary>As above; <paramref name="spans"/> gets the entry's colour runs (null when none).</summary>
    public uint? Take(string? text, int chatType, long nowMs, out ChatSpan[]? spans)
    {
        spans = null;
        if (Count == 0 || string.IsNullOrEmpty(text)) return null;
        string key = Key(text);
        lock (_lock)
        {
            Expire(nowMs);
            for (var n = _lines.First; n != null; n = n.Next)
            {
                Entry e = n.Value;
                if (chatType >= 0 && e.ChatType != chatType) continue;
                bool same = e.Text == key
                    || (key.Length >= MaxArrivingChars && e.Text.StartsWith(key, StringComparison.Ordinal));
                if (!same) continue;
                _lines.Remove(n);
                Volatile.Write(ref _count, _lines.Count);
                spans = e.Spans;
                return e.Argb != 0 ? e.Argb : null;
            }
            Volatile.Write(ref _count, _lines.Count);
            return null;
        }
    }

    private void Expire(long nowMs)
    {
        while (_lines.First is { } f && nowMs - f.Value.AtMs > LineTtlMs) _lines.RemoveFirst();
    }
}

/// <summary>SendPluginCommand("RynthCore.Chat", ...) and the lines it colours; see the file header.</summary>
internal static class ChatOwnColors
{
    /// <summary>The plugin name plugins pass to SendPluginCommand to reach this.</summary>
    public const string CommandTarget = "RynthCore.Chat";
    /// <summary>"#RRGGBB": the next WriteToChat on this thread is drawn in it; "" / "none" disarms.</summary>
    public const string NextColorAction = "nextcolor";
    /// <summary>An armed colour the next WriteToChat doesn't follow within this long is dropped.</summary>
    public const long ArmWindowMs = 2_000;

    public static readonly ChatOwnColorQueue Lines = new();

    /// <summary>"start:length:RRGGBB;...": colour runs for the next WriteToChat on this thread; "" / "none" disarms.</summary>
    public const string NextSpansAction = "nextspans";
    /// <summary>At most this many runs in one "nextspans".</summary>
    public const int MaxSpans = 32;
    /// <summary>A longer "nextspans" payload is refused.</summary>
    public const int MaxPayloadChars = 1024;
    /// <summary>A run's start or length above this is refused (no line is that long; keeps the sums small).</summary>
    public const int MaxOffset = 100_000;

    [ThreadStatic] private static uint t_armed;      // 0 = none (an armed colour is always opaque)
    [ThreadStatic] private static long t_armedAtMs;
    [ThreadStatic] private static ChatSpan[]? t_armedSpans;
    [ThreadStatic] private static long t_spansAtMs;

    /// <summary>The command. True when it was understood (the plugin then knows the engine has this).</summary>
    public static bool HandlePluginCommand(string? action, string? value, long nowMs) =>
        HandlePluginCommand(action, value, nowMs, out _);

    /// <summary>
    /// As above; <paramref name="error"/> says why a "nextspans" payload was refused (the engine
    /// logs it), null otherwise.
    /// </summary>
    public static bool HandlePluginCommand(string? action, string? value, long nowMs, out string? error)
    {
        error = null;
        string act = (action ?? "").Trim();
        if (string.Equals(act, NextSpansAction, StringComparison.OrdinalIgnoreCase))
        {
            string sv = (value ?? "").Trim();
            t_armedSpans = null;
            if (sv.Length == 0 || sv.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
            ChatSpan[]? spans = ParseSpans(sv, out error);
            if (spans == null) return false;
            t_armedSpans = spans;
            t_spansAtMs = nowMs;
            return true;
        }
        if (!string.Equals(act, NextColorAction, StringComparison.OrdinalIgnoreCase)) return false;
        string v = (value ?? "").Trim();
        if (v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            t_armed = 0;
            return true;
        }
        uint? argb = ChatLineColors.ParseColor(v);
        if (argb == null) return false;
        t_armed = argb.Value | 0xFF000000;
        t_armedAtMs = nowMs;
        return true;
    }

    /// <summary>This thread's armed colour, used up (null when none, or armed too long ago).</summary>
    public static uint? TakeArmed(long nowMs)
    {
        uint armed = t_armed;
        if (armed == 0) return null;
        t_armed = 0;
        return nowMs - t_armedAtMs <= ArmWindowMs ? armed : null;
    }

    /// <summary>This thread's armed colour runs, used up (null when none, or armed too long ago).</summary>
    public static ChatSpan[]? TakeArmedSpans(long nowMs)
    {
        ChatSpan[]? armed = t_armedSpans;
        if (armed == null) return null;
        t_armedSpans = null;
        return nowMs - t_spansAtMs <= ArmWindowMs ? armed : null;
    }

    /// <summary>
    /// A "nextspans" payload as runs (sorted, not overlapping, opaque colours), or null with
    /// <paramref name="error"/> saying why. Strict: see the file header.
    /// </summary>
    public static ChatSpan[]? ParseSpans(string? payload, out string? error)
    {
        error = null;
        string p = (payload ?? "").Trim();
        if (p.Length == 0) { error = "empty"; return null; }
        if (p.Length > MaxPayloadChars) { error = $"longer than {MaxPayloadChars} characters"; return null; }
        string[] parts = p.Split(';');
        if (parts.Length > MaxSpans) { error = $"more than {MaxSpans} runs"; return null; }
        var spans = new ChatSpan[parts.Length];
        int prevEnd = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            string[] f = parts[i].Trim().Split(':');
            if (f.Length != 3) { error = $"run {i + 1} '{parts[i]}' is not start:length:RRGGBB"; return null; }
            if (!TryOffset(f[0], out int start) || !TryOffset(f[1], out int length) || length < 1)
            {
                error = $"run {i + 1} '{parts[i]}': start and length must be whole numbers (length at least 1)";
                return null;
            }
            if (!TryHex6(f[2], out uint rgb)) { error = $"run {i + 1} '{parts[i]}': the colour must be RRGGBB"; return null; }
            if (start < prevEnd) { error = $"run {i + 1} '{parts[i]}' starts before the run in front of it ends"; return null; }
            spans[i] = new ChatSpan(start, length, rgb | 0xFF000000);
            prevEnd = start + length;
        }
        return spans;
    }

    private static bool TryOffset(string s, out int v)
    {
        v = 0;
        if (s.Length == 0 || s.Length > 6) return false;
        foreach (char c in s)
        {
            if (c < '0' || c > '9') return false;
            v = v * 10 + (c - '0');
        }
        return v <= MaxOffset;
    }

    private static bool TryHex6(string s, out uint rgb)
    {
        rgb = 0;
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6) return false;
        foreach (char c in s)
        {
            int d = c >= '0' && c <= '9' ? c - '0'
                  : c >= 'a' && c <= 'f' ? c - 'a' + 10
                  : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
            if (d < 0) return false;
            rgb = (rgb << 4) | (uint)d;
        }
        return true;
    }

    /// <summary>
    /// The runs, when every one fits inside a line <paramref name="textLength"/> characters long
    /// (its text as noted, <see cref="ChatOwnColorQueue.Key"/>); else null and why.
    /// </summary>
    public static ChatSpan[]? FitSpans(ChatSpan[]? spans, int textLength, out string? error)
    {
        error = null;
        if (spans == null) return null;
        foreach (ChatSpan s in spans)
            if (s.Start < 0 || s.Length < 1 || s.Start + s.Length > textLength)
            {
                error = $"run {s.Start}:{s.Length} is outside the {textLength}-character line";
                return null;
            }
        return spans;
    }

    /// <summary>
    /// The runs for a line as RynthChat's copy arrived: <paramref name="arrivedText"/> as it came,
    /// <paramref name="shownText"/> as the chat keeps it (AC's tell links taken out). Offsets only
    /// hold when the two are the same text, so otherwise none; a line AC cut short keeps the
    /// runs (or the parts of them) inside what arrived.
    /// </summary>
    public static ChatSpan[]? ArrivedSpans(ChatSpan[]? spans, string? arrivedText, string shownText)
    {
        if (spans == null) return null;
        string shown = ChatOwnColorQueue.Key(shownText);
        if (ChatOwnColorQueue.Key(arrivedText ?? "") != shown) return null;
        List<ChatSpan>? cut = null;
        for (int i = 0; i < spans.Length; i++)
        {
            ChatSpan s = spans[i];
            if (s.Start + s.Length <= shown.Length) { cut?.Add(s); continue; }
            if (cut == null) { cut = new List<ChatSpan>(spans.Length); for (int k = 0; k < i; k++) cut.Add(spans[k]); }
            if (s.Start < shown.Length) cut.Add(new ChatSpan(s.Start, shown.Length - s.Start, s.Argb));
        }
        if (cut == null) return spans;
        return cut.Count > 0 ? cut.ToArray() : null;
    }

    /// <summary>
    /// The runs a line is drawn with: the rules' match colours (<paramref name="ruleSpans"/>) over
    /// the plugin's own (<paramref name="ownSpans"/>), cut around them. A rule's whole-line colour
    /// (<paramref name="ruleLineColour"/>) wins over the plugin's runs, as it does over its line
    /// colour. Sorted, not overlapping (given sorted, non-overlapping inputs). Called when a line
    /// is measured, never per frame.
    /// </summary>
    public static ChatSpan[]? EffectiveSpans(ChatSpan[]? ownSpans, ChatSpan[]? ruleSpans, bool ruleLineColour)
    {
        if (ownSpans == null || ownSpans.Length == 0 || ruleLineColour) return ruleSpans;
        if (ruleSpans == null || ruleSpans.Length == 0) return ownSpans;
        var result = new List<ChatSpan>(ownSpans.Length + ruleSpans.Length * 2);
        foreach (ChatSpan s in ownSpans)
        {
            int a = s.Start, b = s.Start + s.Length;
            foreach (ChatSpan r in ruleSpans)
            {
                int ra = r.Start, rb = r.Start + r.Length;
                if (rb <= a || ra >= b) continue;
                if (ra > a) result.Add(new ChatSpan(a, ra - a, s.Argb));
                a = Math.Max(a, rb);
            }
            if (b > a) result.Add(new ChatSpan(a, b - a, s.Argb));
        }
        result.AddRange(ruleSpans);
        result.Sort((x, y) => x.Start.CompareTo(y.Start));
        return result.ToArray();
    }
}

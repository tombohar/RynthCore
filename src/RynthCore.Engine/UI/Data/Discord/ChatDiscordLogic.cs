// ============================================================================
//  RynthCore.Engine - UI/Data/Discord/ChatDiscordLogic.cs
//  The relay's per-line decisions, kept free of engine state so
//  tools/ChatDiscordTests can run them: what a chat line is (speech on a
//  channel, or plain text), whether a route wants it and under which label,
//  how it is written for Discord, and whether it is scrollback from before
//  the relay started (DiscordBacklogGate, by RynthChat's line seq).
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Engine.UI.Data.Discord;

/// <summary>A chat line split for the relay. <see cref="Speech"/> null: not player speech.</summary>
internal readonly record struct DiscordLineParts(SpeechChannel? Speech, string Speaker, string Message, bool Own)
{
    /// <summary>"Speaker message" (or the message alone): what route keywords are matched against.</summary>
    public string Haystack => Speaker.Length > 0 ? Speaker + " " + Message : Message;
}

internal static class ChatDiscordLogic
{
    /// <summary>
    /// Splits <paramref name="text"/> (AC's name links already stripped). Player speech gets its
    /// channel and speaker (yours as <paramref name="me"/> when known); anything else is the text
    /// alone. False for blank lines.
    /// </summary>
    public static bool TryClassify(string? text, int chatType, string me, out DiscordLineParts parts)
    {
        parts = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (ChatSpeech.TryParse(text, chatType, out var ch, out string who, out string msg, out bool own))
        {
            parts = new DiscordLineParts(ch, own && me.Length > 0 ? me : who, msg, own);
            return true;
        }
        string plain = ChatSpeech.Decode(text).Trim();
        if (plain.Length == 0) return false;
        parts = new DiscordLineParts(null, string.Empty, plain, false);
        return true;
    }

    /// <summary>
    /// True when <paramref name="route"/> takes the line: its speech channel is ticked, or the line
    /// shows in one of the route's tabs (<paramref name="showsInTab"/>). <paramref name="label"/> is
    /// what the Discord line is tagged with: the speech channel, else the tab that matched.
    /// </summary>
    public static bool Wants(DiscordRoute route, SpeechChannel? speech, Func<string, bool> showsInTab, out string label)
    {
        label = string.Empty;
        if (speech is { } ch && route.RelaysSpeech(ch))
        {
            label = ChatSpeech.Name(ch);
            return true;
        }
        foreach (string tab in route.Tabs)
        {
            if (!showsInTab(tab)) continue;
            label = speech is { } c ? ChatSpeech.Name(c) : tab;
            return true;
        }
        return false;
    }

    /// <summary>The Discord text for one relayed line.</summary>
    public static string Format(ChatDiscordSettings s, DateTime time, in DiscordLineParts parts, string label, string me)
        => DiscordFormat.FormatLine(time, label.Length > 0 ? label : null,
            parts.Speech != null && parts.Speaker.Length > 0 ? parts.Speaker : null,
            parts.Message, s.IncludeTimestamp, s.TagCharacter && me.Length > 0 ? me : null);

    /// <summary>Key for the per-route repeat filter (the same line twice within a few seconds).</summary>
    public static string DedupKey(in DiscordLineParts parts, string label) => $"{label}|{parts.Speaker}|{parts.Message}";

    /// <summary>Drops dedup entries older than <paramref name="windowMs"/>.</summary>
    public static void PruneRecent(Dictionary<string, long> recent, long now, int windowMs)
    {
        List<string>? stale = null;
        foreach (var kv in recent)
            if (now - kv.Value > windowMs) (stale ??= new List<string>()).Add(kv.Key);
        if (stale != null) foreach (string k in stale) recent.Remove(k);
    }
}

/// <summary>
/// Keeps scrollback out of Discord, by RynthChat's line numbers (seq), not by clock time.
/// ChatSource reads RynthChat's scrollback by seq; the first read after the relay comes on
/// for this character (engine start, hot reload, switching it on, logging in a character that
/// has it on) or after RynthChat's numbering restarts is the baseline: up to 500 lines that were
/// already sent, or are history, so none of it is posted. After that a line is posted only if
/// its seq is newer than any seen. Pump thread only.
/// </summary>
internal sealed class DiscordBacklogGate
{
    private bool _live;
    private ulong _newestSeq;

    /// <summary>True once a baseline read has been taken while on.</summary>
    public bool Live => _live;

    /// <summary>The relay is off for this character: the next read while on is a baseline again.</summary>
    public void Off() => _live = false;

    /// <summary>RynthChat's scrollback numbering restarted (its plugin was reloaded): re-baseline.</summary>
    public void SourceReset()
    {
        _live = false;
        _newestSeq = 0;
    }

    /// <summary>
    /// One scrollback read (possibly empty) with the seq of each line in it. Returns which lines
    /// to post: none while off or for the baseline read, else those newer than any seen.
    /// </summary>
    public bool[] Admit(bool on, IReadOnlyList<ulong> seqs)
    {
        var post = new bool[seqs.Count];
        if (!on)
        {
            _live = false;
            return post;
        }
        bool baseline = !_live;
        _live = true;
        for (int i = 0; i < seqs.Count; i++)
        {
            ulong seq = seqs[i];
            bool newer = seq > _newestSeq;
            if (newer) _newestSeq = seq;
            post[i] = newer && !baseline;
        }
        return post;
    }
}

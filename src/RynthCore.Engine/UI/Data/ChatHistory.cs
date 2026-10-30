// ============================================================================
//  RynthCore.Engine - UI/Data/ChatHistory.cs
//  How long RynthChat's scrollback is (the chat setting "Chat history",
//  2026-10-11; a fixed 500 lines before) and the newest-N bookkeeping that
//  goes with it. No engine state: tools/ChatHistoryBench compiles it in.
//
//  The length applies to both copies of the scrollback: the engine's
//  (ChatSource's published ChatLine[]) and RynthChat's own ring, which the
//  engine resizes through the plugin's RynthChatSetCapacity export (RynthChat
//  0.3.0; an older plugin keeps its 500). Lines are numbered by the plugin and
//  the engine either way, so the Discord relay's seq bookkeeping is untouched.
//
//  Cost, measured by tools/ChatHistoryBench (x86, like acclient): about 1.8 MB
//  of managed memory per 1,000 lines (engine 1.2, plugin ring 0.6, the face's
//  lists 0.04), so 18 MB at 10,000 against the 1.3-2.3 GB of address space a
//  client has free. Per poll the work grows with the new lines, not the length
//  (one array copy: 11 us at 10,000). What grows with it: the first read after
//  a bind or reload (15 ms at 2,000, 59 ms at 10,000, once), re-routing after
//  a rule edit (7 / 27 ms, done in 8 ms slices) and re-measuring after a resize
//  (4 / 15 ms, done in 2 ms slices a frame). The table is in that tool's header.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Engine.UI.Data;

internal static class ChatHistory
{
    public const int DefaultLines = 2000;
    public const int MinLines = 500;
    public const int MaxLines = 10000;
    /// <summary>The setting moves in steps of this many lines.</summary>
    public const int StepLines = 500;

    /// <summary>A stored or typed length made valid: 0 or less (absent) is the default.</summary>
    public static int Clamp(int lines)
    {
        if (lines <= 0) return DefaultLines;
        lines = (int)Math.Round(lines / (double)StepLines) * StepLines;
        return Math.Clamp(lines, MinLines, MaxLines);
    }

    /// <summary>
    /// The newest <paramref name="max"/> of <paramref name="old"/> followed by
    /// <paramref name="added"/>, as a new array (the published one is never changed:
    /// faces on other threads read it). Cost: one copy of the kept references.
    /// </summary>
    public static T[] Merge<T>(T[] old, IReadOnlyList<T> added, int max)
    {
        max = Math.Max(1, max);
        int skipAdded = Math.Max(0, added.Count - max);
        int takeAdded = added.Count - skipAdded;
        int keepOld = Math.Max(0, Math.Min(old.Length, max - takeAdded));
        var lines = new T[keepOld + takeAdded];
        Array.Copy(old, old.Length - keepOld, lines, 0, keepOld);
        for (int i = 0; i < takeAdded; i++) lines[keepOld + i] = added[skipAdded + i];
        return lines;
    }

    /// <summary>The newest <paramref name="max"/> lines, or null when there are no more than that.</summary>
    public static T[]? Trim<T>(T[] lines, int max)
    {
        max = Math.Max(1, max);
        if (lines.Length <= max) return null;
        var kept = new T[max];
        Array.Copy(lines, lines.Length - max, kept, 0, max);
        return kept;
    }
}

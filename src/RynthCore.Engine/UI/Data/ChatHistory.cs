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

    // ── Player chat kept longer (2026-11-22, ChatPlayerText) ─────────────────
    //
    // The scrollback is one list, and every tab is a view of it, so a busy hunt's combat lines
    // used to push tells, fellowship and allegiance out of their tabs as fast as everything else
    // (10,000 lines can go in under an hour). Now a line is kept while it is among the newest
    // "Chat history" lines, as before, OR it is player chat among the newest "Player chat history"
    // player lines. Nothing kept before is dropped sooner; player lines outlast the spam. The
    // list stays in arrival order, so the faces, the unread counts and the re-route pass work on
    // it as before (ChatView drops what left by Id, not by position).

    public const int DefaultPlayerLines = 5000;
    public const int MinPlayerLines = 500;
    public const int MaxPlayerLines = 20000;

    /// <summary>A stored or typed "Player chat history" made valid: 0 or less (absent) is the default.</summary>
    public static int ClampPlayer(int lines)
    {
        if (lines <= 0) return DefaultPlayerLines;
        lines = (int)Math.Round(lines / (double)StepLines) * StepLines;
        return Math.Clamp(lines, MinPlayerLines, MaxPlayerLines);
    }

    /// <summary>
    /// <paramref name="old"/> followed by <paramref name="added"/>, keeping the newest
    /// <paramref name="max"/> lines and, older than those, the player lines among the newest
    /// <paramref name="playerMax"/> player lines. A new array, in the same order.
    /// </summary>
    public static T[] MergeKeep<T>(T[] old, IReadOnlyList<T> added, int max, int playerMax, Func<T, bool> isPlayer)
    {
        max = Math.Max(1, max);
        int total = old.Length + added.Count;
        // The newest max lines are all kept (the main window, at the end); before it, only player
        // lines, while fewer than playerMax player lines have been counted from the newest.
        int mainStart = Math.Max(0, total - max);
        int players = 0;
        for (int k = total - 1; k >= mainStart; k--)
            if (isPlayer(At(old, added, k))) players++;
        int extra = 0, oldest = mainStart;
        for (int k = mainStart - 1; k >= 0 && players < playerMax; k--)
        {
            if (!isPlayer(At(old, added, k))) continue;
            players++;
            extra++;
            oldest = k;
        }
        var lines = new T[extra + (total - mainStart)];
        int n = 0;
        for (int k = oldest; k < mainStart; k++)
        {
            T line = At(old, added, k);
            if (isPlayer(line)) lines[n++] = line;
        }
        // The main window: a copy of old's part, then the added lines.
        if (mainStart < old.Length)
        {
            Array.Copy(old, mainStart, lines, n, old.Length - mainStart);
            n += old.Length - mainStart;
        }
        for (int k = Math.Max(mainStart, old.Length); k < total; k++) lines[n++] = added[k - old.Length];
        return lines;
    }

    /// <summary>The lines <see cref="MergeKeep"/> keeps of <paramref name="lines"/>, or null when it keeps them all.</summary>
    public static T[]? TrimKeep<T>(T[] lines, int max, int playerMax, Func<T, bool> isPlayer)
    {
        if (lines.Length <= Math.Max(1, max)) return null;
        T[] kept = MergeKeep(lines, Array.Empty<T>(), max, playerMax, isPlayer);
        return kept.Length == lines.Length ? null : kept;
    }

    private static T At<T>(T[] old, IReadOnlyList<T> added, int k) => k < old.Length ? old[k] : added[k - old.Length];

    /// <summary>
    /// The indices of <paramref name="shown"/> whose Id <paramref name="source"/> no longer has, in
    /// order, into <paramref name="gone"/> (cleared first). Both lists are in Id order: one walk.
    /// A chat view's lines leave by this (ChatView.DropGone): with player chat kept longer, a line
    /// can go while an older one stays, so "older than the source's first line" no longer covers it.
    /// </summary>
    public static void FindGone<T>(IReadOnlyList<T> shown, IReadOnlyList<T> source, Func<T, long> id, List<int> gone)
    {
        gone.Clear();
        int j = 0;
        for (int i = 0; i < shown.Count; i++)
        {
            long want = id(shown[i]);
            while (j < source.Count && id(source[j]) < want) j++;
            if (j >= source.Count || id(source[j]) != want) gone.Add(i);
        }
    }
}

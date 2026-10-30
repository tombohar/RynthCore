// ============================================================================
//  RynthCore.Engine - Compatibility/IncomingChatHold.cs
//
//  Lets a plugin's "eat" (the eatFlag of OnChatWindowText) keep a line out of
//  AC's own chat window, not just out of RynthChat.
//
//  AC prints chat on its main thread (ClientCommunicationSystem::AddTextToScroll,
//  hooked in ChatCallbackHooks). Plugins hear chat on the plugin pump thread, and
//  their handlers touch plugin state that only the pump may touch, so they can't
//  be called inline from AC's thread. Instead the AddTextToScroll detour HOLDS
//  the line (AC doesn't print it yet), the pump hands it to the plugins as
//  before, the plugins' verdict is stored on the held line, and AC's main thread
//  prints the lines nobody ate, in arrival order, from the next main-thread
//  drain (Client::UseTime / EndScene) or the next incoming line, whichever is
//  first. A line still waiting after HoldTimeoutMs prints anyway, so a stalled
//  pump can delay chat by at most that long and never loses it.
//
//  This file is the pure part (no AC, no native calls) so tools\ChatHoldTests can
//  compile it in and test the decisions offline. The native part (reading the
//  line, calling AC's original to print it) is in ChatCallbackHooks.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

/// <summary>One incoming chat line AC has not printed yet, waiting for the plugins' verdict.</summary>
internal sealed class HeldChatLine
{
    public const int Pending = 0;
    public const int Show = 1;
    public const int Eat = 2;

    public HeldChatLine(string raw, uint chatType, uint unknown, IntPtr thisPtr, long heldAtMs)
    {
        Raw = raw;
        ChatType = chatType;
        Unknown = unknown;
        This = thisPtr;
        HeldAtMs = heldAtMs;
    }

    /// <summary>The text exactly as AC was given it (line breaks included); printed as is.</summary>
    public string Raw { get; }
    public uint ChatType { get; }
    /// <summary>AddTextToScroll's third argument (the client's "offer to the plugin API" flag), passed back unchanged.</summary>
    public uint Unknown { get; }
    /// <summary>AddTextToScroll's this (the communication system).</summary>
    public IntPtr This { get; }
    public long HeldAtMs { get; }

    private int _verdict;

    /// <summary>Pending, Show or Eat. Written once by the pump, read by AC's main thread.</summary>
    public int Verdict => Volatile.Read(ref _verdict);

    /// <summary>The plugins' verdict (pump thread). The first verdict wins; later ones are ignored.</summary>
    public void Decide(bool eat) => Interlocked.CompareExchange(ref _verdict, eat ? Eat : Show, Pending);
}

/// <summary>What the AddTextToScroll detour does with one line.</summary>
internal enum ChatHoldDecision
{
    /// <summary>Hold it: AC prints it after the plugins' verdict (unless a plugin eats it).</summary>
    Hold,
    /// <summary>Print it now, plugins hear it afterwards (the old behaviour). Lines already held stay held.</summary>
    PassThrough,
    /// <summary>Print every held line now (no verdict needed), then print this one: holding is off or unsafe.</summary>
    FlushThenPassThrough,
}

/// <summary>Everything the hold decision looks at, so it can be tested without AC.</summary>
internal readonly record struct ChatHoldInputs(
    bool Enabled,          // the /rc chathold switch, and the replay path resolved at install
    bool Disarmed,         // engine teardown has begun
    bool OnMainThread,     // the detour runs on AC's main thread (the held list is main-thread only)
    bool Replaying,        // we are inside our own print of a held line (AC re-entered AddTextToScroll)
    bool HasText,          // the line was read and is not empty
    bool Truncated,        // the line is longer than we read, so it can't be printed back exactly
    bool HasStringInfo,    // the caller passed a StringInfo pointer, which won't outlive the call
    long NowMs,
    long PumpBeatMs,       // last time the plugin pump looked at the chat queue (0 = never)
    long DrainBeatMs,      // last main-thread drain (0 = never)
    int HeldCount);

/// <summary>
/// The held lines, oldest first. Only AC's main thread adds to or releases from it; the pump
/// only writes verdicts on the lines themselves, so no lock is needed here.
/// </summary>
internal sealed class HeldChatQueue
{
    /// <summary>A line still waiting this long prints anyway.</summary>
    public const long HoldTimeoutMs = 400;
    /// <summary>Hold only while the plugin pump has looked at the chat queue this recently.</summary>
    public const long PumpStaleMs = 250;
    /// <summary>Hold only while the main-thread drain (which prints held lines) ran this recently.</summary>
    public const long DrainStaleMs = 500;
    /// <summary>Never hold more than this many lines; past it, everything prints at once.</summary>
    public const int MaxHeld = 256;

    private readonly Queue<HeldChatLine> _lines = new();

    public int Count => _lines.Count;

    // Counters for /rc chathold status. Written on AC's main thread only.
    public long HeldTotal { get; private set; }
    public long ShownTotal { get; private set; }
    public long EatenTotal { get; private set; }
    public long TimedOutTotal { get; private set; }
    public long PrintFailedTotal { get; private set; }

    public static ChatHoldDecision Decide(in ChatHoldInputs i)
    {
        // Inside our own print (AC calls AddTextToScroll again from inside it): print in place,
        // and never touch the held list while a release is walking it.
        if (i.Replaying)
            return ChatHoldDecision.PassThrough;

        // The held list belongs to AC's main thread. A line from any other thread prints as before.
        if (!i.OnMainThread)
            return ChatHoldDecision.PassThrough;

        // Holding is off, the engine is going away, or nobody would answer or print the line in
        // time: print what is held (keeps the order) and stop holding.
        if (!i.Enabled || i.Disarmed)
            return ChatHoldDecision.FlushThenPassThrough;
        if (i.PumpBeatMs == 0 || i.NowMs - i.PumpBeatMs > PumpStaleMs)
            return ChatHoldDecision.FlushThenPassThrough;
        if (i.DrainBeatMs == 0 || i.NowMs - i.DrainBeatMs > DrainStaleMs)
            return ChatHoldDecision.FlushThenPassThrough;
        if (i.HeldCount >= MaxHeld)
            return ChatHoldDecision.FlushThenPassThrough;

        // A line we couldn't print back exactly prints now. Lines still waiting for a verdict
        // stay held (they print a frame later), so a line a plugin eats stays hidden.
        if (!i.HasText || i.Truncated || i.HasStringInfo)
            return ChatHoldDecision.PassThrough;

        return ChatHoldDecision.Hold;
    }

    /// <summary>
    /// The plugins' verdict on one line: eaten when any plugin set the eat flag and none of
    /// them threw on it. A plugin that throws can't be trusted to have meant it, so the line shows.
    /// </summary>
    public static bool PluginsAteIt(int eatFlag, bool anyPluginThrew) => eatFlag != 0 && !anyPluginThrew;

    public void Add(HeldChatLine line)
    {
        _lines.Enqueue(line);
        HeldTotal++;
    }

    /// <summary>
    /// Prints the lines that may print now, oldest first: shown lines, and lines that waited
    /// longer than <see cref="HoldTimeoutMs"/> (all of them when <paramref name="flushAll"/>).
    /// Eaten lines are dropped. Stops at the first line still waiting, so the order never
    /// changes. A print that throws is counted and skipped. Returns how many lines printed.
    /// </summary>
    public int Release(long nowMs, bool flushAll, Action<HeldChatLine> print)
    {
        int printed = 0;
        while (_lines.Count > 0)
        {
            HeldChatLine line = _lines.Peek();
            int verdict = line.Verdict;
            if (verdict == HeldChatLine.Pending && !flushAll && nowMs - line.HeldAtMs < HoldTimeoutMs)
                break;

            _lines.Dequeue();
            if (verdict == HeldChatLine.Eat)
            {
                EatenTotal++;
                continue;
            }

            if (verdict == HeldChatLine.Pending)
                TimedOutTotal++;
            else
                ShownTotal++;

            try
            {
                print(line);
                printed++;
            }
            catch
            {
                PrintFailedTotal++;
            }
        }
        return printed;
    }
}

// ============================================================================
//  RynthCore.Engine - UI/Data/Discord/DiscordRouteQueue.cs
//  One route's send queue, kept free of engine state so
//  tools/ChatDiscordTests can drive it: lines waiting for the batch window,
//  the post in flight, a batch waiting to retry, pacing, pause and counters.
//  ChatDiscord owns one per route and calls it on the pump thread only; it
//  does the decrypting, the HTTP and the log.
//
//  Off means off: when the relay is off for this character or the route is
//  switched off, Stop drops everything queued for it (pending lines, the
//  retry, the post in flight, whose result is then ignored). Only an explicit
//  test message gets past the switches.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;

namespace RynthCore.Engine.UI.Data.Discord;

/// <summary>A packed message waiting for, or in, an HTTP post.</summary>
internal sealed class DiscordBatch
{
    public required List<string> Lines;
    public required string Content;
    public bool IsTest;
    public int Attempts;

    /// <summary>The route's destination when the batch was made (<see cref="DiscordRouteQueue.Credential"/>).</summary>
    public string Credential = string.Empty;

    /// <summary>Cancels this batch's request (set by ChatDiscord while it's in flight).</summary>
    public CancellationTokenSource? Cancel;
}

/// <summary>What <see cref="DiscordRouteQueue.Apply"/> did with a post's result.</summary>
internal enum DiscordOutcome
{
    /// <summary>The batch was no longer in flight (stopped, route removed): result dropped.</summary>
    Ignored,
    Sent,
    Paused,
    RateLimited,
    Retrying,
    GaveUp,
    /// <summary>The route's destination changed while the post was out: a failure is dropped, not retried or paused.</summary>
    Retargeted,
}

internal sealed class DiscordRouteQueue
{
    public const int DedupWindowMs = 3000;
    public const int MaxTransientRetries = 5;
    public const int MaxBackoffMs = 60000;

    public readonly List<string> Pending = new();
    public long FirstPendingMs;
    public long NextAllowedMs;
    public DiscordBatch? InFlight;
    public DiscordBatch? Retry;
    public int BackoffMs;
    public bool Paused;
    public string LastError = string.Empty;
    public int MessagesSent, LinesSent, Dropped;
    public DateTime LastSent;
    public readonly Dictionary<string, long> Recent = new(StringComparer.Ordinal);

    /// <summary>
    /// The destination the queued lines are for: the route's mode and its saved (encrypted)
    /// webhook or bot token + channel. Set by <see cref="Retarget"/>.
    /// </summary>
    public string Credential = string.Empty;

    /// <summary>Lines waiting, in flight or waiting to retry.</summary>
    public int Queued => Pending.Count + (InFlight?.Lines.Count ?? 0) + (Retry?.Lines.Count ?? 0);

    /// <summary>
    /// Called with the route's current destination before lines are added or a post started.
    /// When it changed (a new webhook URL or token pasted, the channel or mode changed), chat
    /// queued for the old destination is dropped rather than sent to the new one: the pending
    /// lines and a batch waiting to retry. A post already in flight went to the old one; if it
    /// fails it isn't retried (<see cref="Apply"/>). A test is kept: it's meant for whatever the
    /// route points at now. Returns the number of lines dropped.
    /// </summary>
    public int Retarget(string credential)
    {
        _mode = _channel = _blob = null;
        return RetargetTo(credential);
    }

    // The route fields the credential was last built from (compared by reference: the pump
    // calls this every tick, and the route's strings are replaced, never edited, on a change).
    private string? _mode, _channel, _blob;

    /// <summary><see cref="Retarget(string)"/> from a route's mode, channel and saved blob (webhook or bot token).</summary>
    public int Retarget(string mode, string channel, string blob)
    {
        if (ReferenceEquals(mode, _mode) && ReferenceEquals(channel, _channel) && ReferenceEquals(blob, _blob)) return 0;
        int dropped = RetargetTo(mode == DiscordRoute.ModeBot ? $"bot|{channel}|{blob}" : $"webhook|{blob}");
        (_mode, _channel, _blob) = (mode, channel, blob);
        return dropped;
    }

    private int RetargetTo(string credential)
    {
        if (string.Equals(credential, Credential, StringComparison.Ordinal)) return 0;
        bool first = Credential.Length == 0 && Queued == 0;
        Credential = credential;
        if (first) return 0;
        int dropped = Pending.Count;
        Pending.Clear();
        if (Retry is { IsTest: false } retry)
        {
            dropped += retry.Lines.Count;
            Retry = null;
        }
        if (Retry is { } test) test.Credential = credential;
        if (dropped > 0)
        {
            Dropped += dropped;
            BackoffMs = 0;
        }
        return dropped;
    }

    /// <summary>Adds a relayed line; the oldest are dropped past <paramref name="maxQueue"/>.</summary>
    public void Add(string line, long now, int maxQueue)
    {
        if (Pending.Count == 0) FirstPendingMs = now;
        Pending.Add(line);
        int over = Pending.Count - maxQueue;
        if (over > 0)
        {
            Pending.RemoveRange(0, over);
            Dropped += over;
        }
    }

    /// <summary>
    /// True (and remembered) unless the same line went to this route within the dedup window.
    /// </summary>
    public bool NoteFresh(string key, long now)
    {
        if (Recent.TryGetValue(key, out long seen) && now - seen < DedupWindowMs) return false;
        Recent[key] = now;
        if (Recent.Count > 256) ChatDiscordLogic.PruneRecent(Recent, now, DedupWindowMs);
        return true;
    }

    /// <summary>Queues a test line to go next (it replaces a waiting retry: that is dropped).</summary>
    public void QueueTest(string line)
    {
        if (Retry is { IsTest: false } old) Dropped += old.Lines.Count;
        Retry = new DiscordBatch { Lines = new List<string> { line }, Content = line, IsTest = true };
        NextAllowedMs = 0;
    }

    /// <summary>Clears a pause (Resume, a credential change, a test).</summary>
    public void Resume()
    {
        Paused = false;
        LastError = string.Empty;
        BackoffMs = 0;
        NextAllowedMs = 0;
    }

    /// <summary>
    /// The relay is off for this character, or the route is: drops the pending lines, the batch
    /// waiting to retry and the post in flight (cancelled; its result will be ignored). A test
    /// message is kept. Returns the number of lines dropped. Pump thread.
    /// </summary>
    public int Stop()
    {
        int dropped = Pending.Count;
        Pending.Clear();
        if (Retry is { IsTest: false } retry)
        {
            dropped += retry.Lines.Count;
            Retry = null;
        }
        if (InFlight is { IsTest: false } flying)
        {
            dropped += flying.Lines.Count;
            InFlight = null;
            try { flying.Cancel?.Cancel(); } catch (ObjectDisposedException) { }
        }
        if (dropped > 0)
        {
            Dropped += dropped;
            BackoffMs = 0;
        }
        return dropped;
    }

    /// <summary>
    /// The next batch to post now, or null. <paramref name="on"/> is the relay's switch for this
    /// character and the route's own switch together: off, only a test may go (anything else is
    /// dropped, as in <see cref="Stop"/>). The batch becomes <see cref="InFlight"/>.
    /// </summary>
    public DiscordBatch? Next(long now, bool on, int batchMs, int intervalMs)
    {
        if (!on) Stop();
        if (InFlight != null || Paused || now < NextAllowedMs) return null;

        DiscordBatch? batch = Retry;
        if (batch == null)
        {
            if (!on || Pending.Count == 0) return null;
            // Wait out the batch window unless the pending text already fills a message.
            int chars = 0;
            foreach (string l in Pending) chars += l.Length + 1;
            if (now - FirstPendingMs < batchMs && chars < DiscordFormat.MaxContent) return null;

            string content = DiscordFormat.Pack(Pending, out int taken);
            batch = new DiscordBatch { Lines = Pending.GetRange(0, taken), Content = content, Credential = Credential };
            Pending.RemoveRange(0, taken);
            FirstPendingMs = now;
        }
        if (batch.IsTest) batch.Credential = Credential;
        Retry = null;
        InFlight = batch;
        NextAllowedMs = now + intervalMs;
        return batch;
    }

    /// <summary>
    /// Applies a post's result. A result for a batch that is no longer in flight (stopped, or
    /// the route was removed) is ignored: it must not come back as a retry.
    /// </summary>
    public DiscordOutcome Apply(DiscordBatch batch, DiscordSendResult r, long now, int intervalMs)
    {
        if (!ReferenceEquals(InFlight, batch)) return DiscordOutcome.Ignored;
        InFlight = null;

        if (r.Ok)
        {
            LastError = string.Empty;
            BackoffMs = 0;
            MessagesSent++;
            LinesSent += batch.Lines.Count;
            LastSent = DateTime.Now;
            NextAllowedMs = Math.Max(NextAllowedMs, now + Math.Max(intervalMs, r.RetryAfterMs));
            return DiscordOutcome.Sent;
        }

        if (!batch.IsTest && !string.Equals(batch.Credential, Credential, StringComparison.Ordinal))
        {
            // Sent to the old destination; the new one gets neither the retry nor a pause.
            Dropped += batch.Lines.Count;
            return DiscordOutcome.Retargeted;
        }

        LastError = r.Error;
        if (r.Permanent)
        {
            Paused = true;
            Dropped += batch.Lines.Count + Pending.Count;
            Pending.Clear();
            return DiscordOutcome.Paused;
        }

        if (r.Status == 429)
        {
            // Rate limits don't count as a failed attempt; Discord says exactly when to come back.
            Retry = batch;
            NextAllowedMs = now + Math.Max(r.RetryAfterMs, intervalMs);
            return DiscordOutcome.RateLimited;
        }

        batch.Attempts++;
        if (batch.Attempts >= MaxTransientRetries)
        {
            Dropped += batch.Lines.Count;
            BackoffMs = 0;
            return DiscordOutcome.GaveUp;
        }
        BackoffMs = BackoffMs == 0 ? 2000 : Math.Min(BackoffMs * 2, MaxBackoffMs);
        Retry = batch;
        NextAllowedMs = now + Math.Max(BackoffMs, r.RetryAfterMs);
        return DiscordOutcome.Retrying;
    }
}

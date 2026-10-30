// ============================================================================
//  RynthCore.Engine - Compatibility/InventoryModel.Batch.cs
//  Several items in one go (the Inventory panel's multi-selection): Drop,
//  Give to the selected creature, or Move into a pack.
//
//  Still one item action at a time (AC refuses a second while one waits): the
//  batch hands the next item to the normal request path only after the one
//  before it has ended (done, refused, gone or timed out), with a short gap,
//  so each item is checked and watched exactly like a single click.
//
//  Threads: RequestBatch / CancelBatch / Batch / BatchRunning any thread;
//  everything else on AC's main thread (BatchTick, from MainThreadTick). The
//  batch keeps running with the panel closed and ends by itself.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

/// <summary>A running or finished batch, for the status row. Immutable.</summary>
internal sealed class InventoryBatchStatus
{
    public InventoryActionKind Kind;
    public int Total, Done, Failed;
    public string TargetName = string.Empty;
    public bool Running;
    /// <summary>Stop was pressed: it ends after the item in flight.</summary>
    public bool Stopping;
    /// <summary>"Giving 3 of 12 to Bob" while running, else the summary.</summary>
    public string Text = string.Empty;
}

internal static partial class InventoryModel
{
    // Between items: lets the server's answer to one settle before the next is sent.
    private static readonly long BatchGapTicks = Freq / 4;
    /// <summary>The most items one batch takes (a full main pack and its side packs).</summary>
    public const int MaxBatch = 400;

    // ── Published / inputs (any thread) ─────────────────────────────────
    private static InventoryBatchStatus? _batchStatus;
    private static InventoryRequest[]? _batchRequested;
    private static int _batchActive, _batchCancel;

    public static InventoryBatchStatus? Batch => Volatile.Read(ref _batchStatus);
    public static bool BatchRunning => Volatile.Read(ref _batchActive) != 0;

    /// <summary>
    /// Starts acting on <paramref name="items"/> one after another (all of one kind; Drop, Give
    /// or Move). Refused while another action or batch is waiting. Any thread.
    /// </summary>
    public static bool RequestBatch(IReadOnlyList<InventoryRequest> items, out string why)
    {
        if (items.Count == 0) { why = "Nothing to do."; return false; }
        if (items.Count > MaxBatch) { why = $"At most {MaxBatch} items at once."; return false; }
        InventoryActionKind kind = items[0].Kind;
        if (kind is not (InventoryActionKind.Drop or InventoryActionKind.Give or InventoryActionKind.Move))
        {
            why = "Only Drop, Give and Move work on several items.";
            return false;
        }
        for (int i = 1; i < items.Count; i++)
        {
            if (items[i].Kind != kind) { why = "A batch is one kind of action."; return false; }
        }
        InventoryPending? busy = Pending;
        if (busy != null)
        {
            why = (busy.WaitingForGame ? "Still waiting for the game: " : "Still waiting for the server: ") + busy.Label;
            return false;
        }
        if (Volatile.Read(ref _requested) != null) { why = "Another item action is about to be sent."; return false; }
        if (Interlocked.CompareExchange(ref _batchActive, 1, 0) != 0) { why = "Several items are already being sent."; return false; }

        var copy = new InventoryRequest[items.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = items[i];
        Volatile.Write(ref _batchCancel, 0);
        Volatile.Write(ref _batchRequested, copy);
        why = string.Empty;
        return true;
    }

    /// <summary>Stop: no more items are sent; the one in flight still ends normally. Any thread.</summary>
    public static void CancelBatch()
    {
        if (!BatchRunning) return;
        Volatile.Write(ref _batchCancel, 1);
        InventoryBatchStatus? s = Batch;
        if (s is { Running: true, Stopping: false })
            Volatile.Write(ref _batchStatus, new InventoryBatchStatus
            {
                Kind = s.Kind, Total = s.Total, Done = s.Done, Failed = s.Failed, TargetName = s.TargetName,
                Running = true, Stopping = true, Text = s.Text + " (stopping)",
            });
    }

    // ── Main thread ─────────────────────────────────────────────────────
    private static readonly Queue<InventoryRequest> _batchQueue = new();
    private static InventoryActionKind _batchKind;
    private static int _batchTotal, _batchDone, _batchFailed;
    private static string _batchTarget = string.Empty, _batchFirstError = string.Empty;
    // The item handed to the request path, until it has ended.
    private static InventoryRequest? _batchWaiting;
    private static InventoryResult? _batchResultBefore;
    private static long _batchNextAt;

    /// <summary>Starts a requested batch, counts the item that ended, sends the next one. Main thread.</summary>
    private static void BatchTick(long now)
    {
        InventoryRequest[]? incoming = Interlocked.Exchange(ref _batchRequested, null);
        if (incoming != null) StartBatch(incoming, now);
        // RequestBatch sets the flag a moment before it hands over the items: they come next tick.
        if (_batchTotal == 0) return;

        // The item handed over last tick: wait until it has ended, then count it.
        if (_batchWaiting != null)
        {
            if (Busy()) return;
            InventoryResult? r = LastResult;
            bool ended = r != null && !ReferenceEquals(r, _batchResultBefore);
            if (ended && r!.Ok) _batchDone++;
            else
            {
                _batchFailed++;
                if (_batchFirstError.Length == 0) _batchFirstError = ended ? r!.Text : "no answer";
            }
            _batchWaiting = null;
            _batchNextAt = now + BatchGapTicks;
            PublishBatch(running: true);
        }

        if (Volatile.Read(ref _batchCancel) != 0) { EndBatch(now, "stopped"); return; }
        if (_batchQueue.Count == 0) { EndBatch(now, string.Empty); return; }
        if (now < _batchNextAt || Busy()) return;

        uint player = ClientHelperHooks.GetPlayerId();
        if (player == 0) { FailRest("not in the world"); EndBatch(now, string.Empty); return; }

        InventoryRequest next = _batchQueue.Peek();
        if (next.Kind == InventoryActionKind.Give
            && (next.TargetId == 0 || next.TargetId == player || !ClientObjectHooks.TryGetItemType(next.TargetId, out _)))
        {
            FailRest(next.TargetName + " is gone");
            EndBatch(now, string.Empty);
            return;
        }

        _batchQueue.Dequeue();
        _batchResultBefore = LastResult;
        if (Interlocked.CompareExchange(ref _requested, next, null) != null)
        {
            // Something else got in first (shouldn't happen: Request refuses during a batch).
            _batchFailed++;
            if (_batchFirstError.Length == 0) _batchFirstError = "another action was in the way";
            _batchNextAt = now + BatchGapTicks;
            PublishBatch(running: true);
            return;
        }
        _batchWaiting = next;
        PublishBatch(running: true);
    }

    /// <summary>An item action requested, held or in flight (ours or not). Main thread.</summary>
    private static bool Busy() => _inFlight != null || _held != null || Volatile.Read(ref _requested) != null;

    private static void StartBatch(InventoryRequest[] items, long now)
    {
        _batchQueue.Clear();
        foreach (InventoryRequest r in items) _batchQueue.Enqueue(r);
        _batchKind = items[0].Kind;
        _batchTotal = items.Length;
        _batchDone = _batchFailed = 0;
        _batchTarget = items[0].TargetName;
        _batchFirstError = string.Empty;
        _batchWaiting = null;
        _batchResultBefore = null;
        _batchNextAt = now;
        PublishBatch(running: true);
        RynthLog.Compat($"InventoryModel: batch {_batchKind} of {_batchTotal} item(s){(_batchTarget.Length > 0 ? " to " + _batchTarget : "")}.");
    }

    private static void FailRest(string why)
    {
        _batchFailed += _batchQueue.Count;
        _batchQueue.Clear();
        if (_batchFirstError.Length == 0) _batchFirstError = why;
    }

    /// <summary>Ends the batch: the summary goes to the status row (LastResult) and the log. Main thread.</summary>
    private static void EndBatch(long now, string how)
    {
        bool started = _batchTotal > 0;
        int skipped = _batchQueue.Count;
        _batchQueue.Clear();
        _batchWaiting = null;
        if (started)
        {
            InventoryBatchStatus s = PublishBatch(running: false, how, skipped);
            Finish(s.Text, _batchFailed == 0 && skipped == 0, now);
            RynthLog.Compat("InventoryModel: " + s.Text);
        }
        _batchTotal = 0;
        Volatile.Write(ref _batchCancel, 0);
        Volatile.Write(ref _batchActive, 0);
    }

    private static InventoryBatchStatus PublishBatch(bool running, string how = "", int skipped = 0)
    {
        var s = new InventoryBatchStatus
        {
            Kind = _batchKind, Total = _batchTotal, Done = _batchDone, Failed = _batchFailed,
            TargetName = _batchTarget, Running = running,
            Stopping = running && Volatile.Read(ref _batchCancel) != 0,
        };
        s.Text = running
            ? BatchProgressText(_batchKind, Math.Min(_batchTotal, _batchDone + _batchFailed + 1), _batchTotal, _batchTarget)
              + (s.Stopping ? " (stopping)" : string.Empty)
            : BatchSummaryText(_batchKind, _batchDone, _batchFailed, skipped, _batchTotal, _batchTarget, how, _batchFirstError);
        Volatile.Write(ref _batchStatus, s);
        return s;
    }

    /// <summary>"Giving 3 of 12 to Bob", "Dropping 3 of 12", "Moving 3 of 12 to Pack".</summary>
    internal static string BatchProgressText(InventoryActionKind kind, int n, int total, string target) => kind switch
    {
        InventoryActionKind.Give => $"Giving {n} of {total} to {target}",
        InventoryActionKind.Move => $"Moving {n} of {total} to {target}",
        _ => $"Dropping {n} of {total}",
    };

    /// <summary>"Gave 10 of 12 items to Bob; 2 failed (first: ...)", "Dropped 12 items.", "Stopped: moved 3 of 12...".</summary>
    internal static string BatchSummaryText(InventoryActionKind kind, int done, int failed, int skipped, int total,
        string target, string how, string firstError)
    {
        string verb = kind switch
        {
            InventoryActionKind.Give => "gave",
            InventoryActionKind.Move => "moved",
            _ => "dropped",
        };
        string where = kind switch
        {
            InventoryActionKind.Give => " to " + target,
            InventoryActionKind.Move => " to " + target,
            _ => string.Empty,
        };
        string count = done == total ? $"{total} item{(total == 1 ? "" : "s")}" : $"{done} of {total} items";
        string text = how.Length > 0
            ? $"{char.ToUpperInvariant(how[0])}{how[1..]}: {verb} {count}{where}"
            : $"{char.ToUpperInvariant(verb[0])}{verb[1..]} {count}{where}";
        if (failed > 0) text += $"; {failed} failed";
        if (skipped > 0) text += $"; {skipped} not sent";
        if (failed > 0 && firstError.Length > 0) text += $" (first: {firstError.TrimEnd('.')})";
        return text + ".";
    }
}

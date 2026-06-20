// ============================================================================
//  RynthCore.Engine - Compatibility/AutoIdService.cs
//
//  Automatically sends RequestId (appraisal) for newly-created world objects
//  so that property caches (AppraisalHooks int/bool/string) are populated
//  without requiring the player to manually select each object.
//
//  Objects are queued from CreateObjectHooks and drained by a background timer
//  with rate-limiting to avoid flooding the server.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal static class AutoIdService
{
    // How often the drain timer fires (ms)
    private const int DrainIntervalMs = 100;

    // Max RequestId calls per drain tick
    private const int MaxPerTick = 3;

    // Skip objects already appraised this session
    private static readonly ConcurrentDictionary<uint, byte> _sent = new();

    // Pending queue
    private static readonly ConcurrentQueue<uint> _queue = new();

    private static Timer? _drainTimer;
    private static bool _started;

    public static void Start()
    {
        if (_started)
            return;
        _started = true;
        _drainTimer = new Timer(DrainTick, null, DrainIntervalMs, DrainIntervalMs);
        RynthLog.Verbose("Compat: AutoIdService started.");
    }

    /// <summary>
    /// Dispose the drain timer at engine shutdown so it doesn't keep firing in
    /// the old generation's (intentionally still-mapped) module across hot-reloads.
    /// </summary>
    public static void Stop()
    {
        if (!_started)
            return;
        _started = false;
        _drainTimer?.Dispose();
        _drainTimer = null;
        RynthLog.Verbose("Compat: AutoIdService stopped.");
    }

    /// <summary>
    /// Enqueue an object for automatic appraisal. Called from CreateObjectHooks detour.
    /// </summary>
    public static void Enqueue(uint objectId)
    {
        if (objectId == 0)
            return;

        // Skip the player character
        uint playerId = ClientHelperHooks.GetPlayerId();
        if (playerId != 0 && objectId == playerId)
            return;

        // Skip if already sent this session
        if (!_sent.TryAdd(objectId, 0))
            return;

        _queue.Enqueue(objectId);
    }

    /// <summary>
    /// Remove an object from the sent set (e.g. when it's destroyed and may be recreated).
    /// </summary>
    public static void Evict(uint objectId)
    {
        _sent.TryRemove(objectId, out _);
    }

    // Last target we issued a priority appraisal for (fires once per target change).
    private static uint _lastPriorityTarget;

    private static void DrainTick(object? state)
    {
        try
        {
            if (!CombatActionHooks.HasRequestId)
                return;

            int sent = 0;

            // Combat-target priority: when the selected target CHANGES, appraise it
            // immediately — enqueued ahead of the create-order backlog so a fast kill
            // still gets its max-HP appraisal out first instead of waiting behind the
            // whole spawn. Fires once per change (guarded), so no per-tick re-appraisal.
            uint target = SelectedTargetHooks.ReadCurrentSelectedId();
            if (target != 0 && target != _lastPriorityTarget)
            {
                _lastPriorityTarget = target;
                uint playerId = ClientHelperHooks.GetPlayerId();
                if (target != playerId && AcMainThreadQueue.EnqueueRequestId(target))
                    sent++;
            }

            while (sent < MaxPerTick && _queue.TryDequeue(out uint objectId))
            {
                // Object may have been destroyed between enqueue and drain
                if (objectId == 0)
                    continue;

                // Marshal onto AC's main thread (EndScene drain) — the Timer thread must
                // not call the native 0xC8 send directly (off-thread heap alloc + non-atomic
                // UI-counter increment). EnqueueRequestId is the marshalled path.
                AcMainThreadQueue.EnqueueRequestId(objectId);
                sent++;
            }
        }
        catch { }
    }
}

// ============================================================================
//  RynthCore.Engine - UI/UiBackgroundWriter.cs
//  Runs UI persistence (panel_state.txt, per-panel settings files) off the
//  calling thread, strictly in the order it was queued.
//
//  ImGui panel faces run on AC's render thread, which must not do file I/O
//  (docs/IMGUI_PARITY_PLAN.md §4.1). Order matters: closing an ImGui face and
//  then popping the panel out writes the same panel_state row twice, and the
//  second write must win. One queue, drained by one pool work item at a time.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;

namespace RynthCore.Engine.UI;

internal static class UiBackgroundWriter
{
    private static readonly object Sync = new();
    private static readonly Queue<(string Label, Action Work)> Pending = new();
    private static bool _draining;

    public static void Enqueue(string label, Action work)
    {
        lock (Sync)
        {
            Pending.Enqueue((label, work));
            if (_draining) return;
            _draining = true;
        }
        ThreadPool.UnsafeQueueUserWorkItem(static _ => Drain(), null);
    }

    private static void Drain()
    {
        while (true)
        {
            (string Label, Action Work) item;
            lock (Sync)
            {
                if (Pending.Count == 0)
                {
                    _draining = false;
                    return;
                }
                item = Pending.Dequeue();
            }

            try { item.Work(); }
            catch (Exception ex) { RynthLog.UI($"UiBackgroundWriter: '{item.Label}' threw {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}

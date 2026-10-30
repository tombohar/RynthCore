// ============================================================================
//  RynthCore.Engine - Compatibility/MutatorGate.cs
//
//  The decision behind AcMainThreadQueue.MustQueue, the one line every AC-mutating helper
//  opens with: call AC now, or queue the call?
//    - off AC's main thread: queue (AC is single-threaded);
//    - on it, called BY a queue drain: call now (the drain already applied the gate);
//    - on it, a stop or cancel (teardown): call now, even in a world transition;
//    - on it otherwise: queue while a world transition holds the drains (render-thread host
//      actions used to run straight into a world AC was tearing down), else call now.
//  holding is asked last and only when it matters (it is main-thread state).
//
//  Pure. Compiled into tools\EngineRobustnessTests.
// ============================================================================

using System;

namespace RynthCore.Engine.Compatibility;

internal static class MutatorGate
{
    public static bool MustQueue(bool onMainThread, bool teardown, bool draining, Func<bool> holding)
    {
        if (!onMainThread) return true;
        if (draining || teardown) return false;
        return holding();
    }
}

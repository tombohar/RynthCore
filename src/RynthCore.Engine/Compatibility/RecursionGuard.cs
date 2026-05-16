// ============================================================================
//  RynthCore.Engine - Compatibility/RecursionGuard.cs
//
//  Diagnostic helper. Each compat-hook detour calls Tick("HookName") on entry.
//  Every N-th call we sample Environment.StackTrace and count how many frames
//  of OUR managed engine code are currently on the call stack. If that exceeds
//  the recursion threshold, we have actual stack-walked-recursion (not just
//  high call frequency). At that point we log the full stack trace once per
//  thread so we can see the recursive pair.
//
//  Why this design: the previous version only counted total detour invocations
//  per thread, so the alarm fired during normal play once 50 hooks had run.
//  Sampling Environment.StackTrace lets us measure the *actual* call depth
//  of our code at the moment of sampling — which is what runaway recursion
//  is, by definition.
//
//  Cost: Environment.StackTrace is ~1ms on x86 NativeAOT. We sample 1 in
//  every 8 calls, capped to one log per thread, so the steady-state overhead
//  during normal gameplay is negligible.
// ============================================================================

using System;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal static class RecursionGuard
{
    /// <summary>Per-thread call counter — drives the sampling cadence below.</summary>
    [ThreadStatic] private static int _callCount;

    /// <summary>Per-thread one-shot. Once a stack has been logged, suppress
    /// further sampling on this thread so we don't recursively allocate inside
    /// the recursion we're trying to diagnose.</summary>
    [ThreadStatic] private static bool _alreadyLogged;

    /// <summary>Sample 1 in 4 calls — keeps overhead bounded but catches
    /// any sustained recursion within milliseconds.</summary>
    private const int SampleMask = 0x3;

    /// <summary>Recursion threshold. Counts ALL managed frames in the trace
    /// (any "   at " line) — including Avalonia/Skia/etc compiled into the
    /// engine. 30 is comfortably above any normal nested-call chain but well
    /// below the depth at which 1MB stack runs out.</summary>
    private const int RecursionFrameThreshold = 30;

    public static void Tick(string detourName)
    {
        // TEMPORARILY DISABLED: ruling out whether this diagnostic itself
        // (Environment.StackTrace allocation per sample) was contributing to
        // the second recursive AV at full budget + D3D9-off + ImGui-off.
        // Re-enable by removing this early-return once verified.
        return;

#pragma warning disable CS0162 // unreachable code
        if (_alreadyLogged) return;
        if ((++_callCount & SampleMask) != 0) return;

        // Sample the stack and count how many of our own frames are on it.
        string trace;
        try
        {
            trace = Environment.StackTrace ?? string.Empty;
        }
        catch
        {
            return;
        }

        // Count any "  at " markers — that's how Environment.StackTrace formats
        // every managed frame regardless of whether it's our code, Avalonia,
        // Skia, or anything else compiled into the engine DLL. This catches
        // recursion in compiled-in dependencies that don't share our namespace.
        const string FrameMarker = "   at ";
        int totalFrames = 0;
        int idx = 0;
        while ((idx = trace.IndexOf(FrameMarker, idx, StringComparison.Ordinal)) >= 0)
        {
            totalFrames++;
            idx += FrameMarker.Length;
            if (totalFrames >= RecursionFrameThreshold) break;
        }

        if (totalFrames < RecursionFrameThreshold)
            return;

        _alreadyLogged = true;

        try
        {
            RynthLog.Info("================================================================");
            RynthLog.Info($"==== RECURSION DETECTED ====  detour={detourName}  totalFrames>={RecursionFrameThreshold}  thread={Thread.CurrentThread.ManagedThreadId}");
            foreach (string raw in trace.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length > 0) RynthLog.Info($"  {line}");
            }
            RynthLog.Info("================================================================");
        }
        catch
        {
            // Logging from inside a recursion detector must never throw.
        }
#pragma warning restore CS0162
    }
}

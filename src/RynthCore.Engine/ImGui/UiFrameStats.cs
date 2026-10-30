// ============================================================================
//  RynthCore.Engine - ImGui/UiFrameStats.cs
//  Cost of the ImGui work on AC's render thread, for the heartbeat's "ui=".
//
//  Record() runs once per EndScene (AC thread) with the ticks spent building
//  and submitting the ImGui frame (0-ish on early-out frames). Once a second
//  it sorts the window in place and publishes the p95 and max; the heartbeat
//  thread only reads the two published floats. No allocation.
// ============================================================================

using System;
using System.Diagnostics;

namespace RynthCore.Engine.ImGuiBackend;

internal static class UiFrameStats
{
    private static readonly long[] _samples = new long[1024];
    private static int _count;
    private static long _windowStart;
    private static volatile float _p95Ms;
    private static volatile float _maxMs;

    /// <summary>95th-percentile ImGui cost per frame over the last full second, in ms.</summary>
    public static float P95Ms => _p95Ms;

    /// <summary>Worst ImGui frame over the last full second, in ms.</summary>
    public static float MaxMs => _maxMs;

    public static void Record(long elapsedTicks)
    {
        long now = Stopwatch.GetTimestamp();
        if (_windowStart == 0) _windowStart = now;
        if (_count < _samples.Length) _samples[_count++] = elapsedTicks;

        if (now - _windowStart < Stopwatch.Frequency) return;

        Array.Sort(_samples, 0, _count);
        int p95 = Math.Clamp((int)Math.Ceiling(_count * 0.95) - 1, 0, _count - 1);
        double toMs = 1000.0 / Stopwatch.Frequency;
        _p95Ms = (float)(_samples[p95] * toMs);
        _maxMs = (float)(_samples[_count - 1] * toMs);
        _count = 0;
        _windowStart = now;
    }
}

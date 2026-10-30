// ============================================================================
//  RynthCore.Engine - Compatibility/ChatRateLimiter.cs
//
//  How many plugin chat lines (WriteToChat off AC's thread) go to AC's chat, and
//  when (2026-10-28). A token bucket: a burst of up to Burst lines at once, then
//  one line per IntervalMs. It used to drop any line within 100 ms of the one
//  before, so a command's help or list reached chat as its first line only
//  ("/rn help", "/ilt help", "/rthreads stats"). The steady rate is unchanged
//  (10 a second), which is what keeps a retry loop's spam ("Casting: X" dozens
//  of times) away from AC's AddTextToScroll and its 0x00460D1D crash.
//
//  Pure (no AC, no engine state), so tools\ChatCommandReplyTests compiles it in.
// ============================================================================

using System;

namespace RynthCore.Engine.Compatibility;

internal sealed class ChatRateLimiter
{
    public const int DefaultBurst = 12;
    public const int DefaultIntervalMs = 100;

    private readonly int _burst;
    private readonly int _intervalMs;
    private readonly object _lock = new();
    private double _tokens;
    private long _at;

    public ChatRateLimiter(int burst = DefaultBurst, int intervalMs = DefaultIntervalMs)
    {
        _burst = Math.Max(1, burst);
        _intervalMs = Math.Max(1, intervalMs);
        _tokens = _burst;
    }

    /// <summary>True when a line may go at <paramref name="nowMs"/> (a monotonic clock, e.g.
    /// Environment.TickCount64), and counts it; false when the burst is spent.</summary>
    public bool TryTake(long nowMs)
    {
        lock (_lock)
        {
            if (_at != 0 && nowMs > _at)
                _tokens = Math.Min(_burst, _tokens + (nowMs - _at) / (double)_intervalMs);
            if (_at == 0 || nowMs > _at) _at = nowMs;
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }
}

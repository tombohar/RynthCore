// WebPanelBrowserGate.cs - when an off-allowlist page may go to the system browser (2026-11-09, Tom OK'd
// the port review's finding: a page, or an ad iframe in it, could open browser tabs on its own). Pure: no
// Win32, no WebView2; WebPanelHost asks it before ShellExecute.
using System;

namespace RynthCore.Engine.WebPanel;

internal enum BrowserOpenVerdict
{
    /// <summary>Open it in the system browser.</summary>
    Open,
    /// <summary>The page asked without a click (script, redirect, timer): dropped.</summary>
    NotUserInitiated,
    /// <summary>Too soon after the last one, or too many this minute: dropped.</summary>
    TooSoon,
}

/// <summary>
/// One gate per panel thread. A page's own request needs the user's click behind it (WebView2's
/// IsUserInitiated); a plugin's Navigate to an off-list URL counts as asked for (a button or a command).
/// Either way at most one open per <see cref="MinGapMs"/> and <see cref="MaxPerMinute"/> a minute.
/// </summary>
internal sealed class WebPanelBrowserGate
{
    public const long MinGapMs = 2000;
    public const int MaxPerMinute = 5;

    private readonly long[] _recent = new long[MaxPerMinute];
    private int _count;
    private int _next;
    private long _last = long.MinValue;

    /// <param name="userInitiated">The page's request came from a click (or a plugin asked).</param>
    /// <param name="nowMs">Environment.TickCount64.</param>
    public BrowserOpenVerdict Decide(bool userInitiated, long nowMs)
    {
        if (!userInitiated) return BrowserOpenVerdict.NotUserInitiated;
        if (_last != long.MinValue && nowMs - _last < MinGapMs) return BrowserOpenVerdict.TooSoon;
        int inLastMinute = 0;
        for (int i = 0; i < _count; i++)
            if (nowMs - _recent[i] < 60_000) inLastMinute++;
        if (inLastMinute >= MaxPerMinute) return BrowserOpenVerdict.TooSoon;

        _recent[_next] = nowMs;
        _next = (_next + 1) % MaxPerMinute;
        if (_count < MaxPerMinute) _count++;
        _last = nowMs;
        return BrowserOpenVerdict.Open;
    }
}

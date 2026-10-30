// ============================================================================
//  RynthCore.Engine - Compatibility/ServerDisconnectClosePolicy.cs
//
//  When to close the client after AC lost the server (ServerDisconnectClose.cs does
//  the closing). No engine dependencies: tools/DisconnectCloseTests compiles this file.
//
//  Fed once per game tick with the clock and what the engine sees:
//    - disconnected: AC's UIFlow mode is Disconnected (0x10000002), the screen that says
//      "The server has disconnected. Please try again in a few minutes." with only a
//      Quit Game button. AC switches to it only when the connection is gone; a logout
//      goes to character select and a quit never changes the screen.
//    - enabled:      engine.json CloseOnServerDisconnect.
//    - exiting:      the client is already closing (UseTime returned 0 / Client::Cleanup).
//    - dumpBusy:     the flight recorder is writing (or about to write) its dump of the
//                    disconnect; the close waits for it, up to DumpGraceMs more.
//
//  Answers, at most once each per visit to the screen:
//    Detected - first tick on the screen (log it, ask for a flight dump);
//    Close    - DelayMs on the screen: close the client (WM_CLOSE);
//    Retry    - RetryAfterMs after Close and still here: close again;
//    GiveUp   - GiveUpAfterMs after Close and still here: say so, stop trying;
//    Left     - the screen went away before the client closed (start over next time).
// ============================================================================

namespace RynthCore.Engine.Compatibility;

internal sealed class ServerDisconnectClosePolicy
{
    public const long DefaultDelayMs = 5000;
    public const long DumpGraceMs = 10000;
    public const long RetryAfterMs = 20000;
    public const long GiveUpAfterMs = 60000;

    public enum Verdict { None, Detected, Close, Retry, GiveUp, Left }

    private readonly long _delayMs;
    private long _sinceMs;      // when the screen was first seen; 0 = not on it
    private long _closedAtMs;   // when Close was answered; 0 = not yet
    private int _attempts;      // Close + Retry answers this visit
    private bool _gaveUp;

    public ServerDisconnectClosePolicy(long delayMs = DefaultDelayMs)
    {
        _delayMs = delayMs < 0 ? 0 : delayMs;
    }

    public long DelayMs => _delayMs;

    /// <summary>On the Disconnected screen (as of the last Update).</summary>
    public bool OnScreen => _sinceMs != 0;

    /// <summary>Milliseconds on the screen as of <paramref name="nowMs"/>, 0 when not on it.</summary>
    public long OnScreenMs(long nowMs) => _sinceMs == 0 ? 0 : nowMs - _sinceMs;

    public int Attempts => _attempts;

    public Verdict Update(long nowMs, bool disconnected, bool enabled, bool exiting, bool dumpBusy)
    {
        if (exiting)
            return Verdict.None;

        if (!disconnected)
        {
            if (_sinceMs == 0)
                return Verdict.None;
            Reset();
            return Verdict.Left;
        }

        if (_sinceMs == 0)
        {
            _sinceMs = nowMs == 0 ? 1 : nowMs;
            return Verdict.Detected;
        }

        if (!enabled)
            return Verdict.None;

        long onScreen = nowMs - _sinceMs;
        if (_attempts == 0)
        {
            if (onScreen < _delayMs)
                return Verdict.None;
            if (dumpBusy && onScreen < _delayMs + DumpGraceMs)
                return Verdict.None;
            _attempts = 1;
            _closedAtMs = nowMs;
            return Verdict.Close;
        }

        long sinceClose = nowMs - _closedAtMs;
        if (_attempts == 1 && sinceClose >= RetryAfterMs)
        {
            _attempts = 2;
            return Verdict.Retry;
        }
        if (!_gaveUp && sinceClose >= GiveUpAfterMs)
        {
            _gaveUp = true;
            return Verdict.GiveUp;
        }
        return Verdict.None;
    }

    private void Reset()
    {
        _sinceMs = 0;
        _closedAtMs = 0;
        _attempts = 0;
        _gaveUp = false;
    }
}

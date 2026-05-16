using System;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// The real spell-cast gate for RynthAi buff/combat recasts.
///
/// Problem: AC refuses a cast issued while the player is mid cast-gesture
/// animation with a server-driven "You're too busy!" text notice. A tight
/// clear-and-recast retry loop on that refusal re-enters AC's AddTextToScroll
/// and produces the visible 0x00460D1D write-AV after long uptime.
///
/// AC's ClientUISystem busy-count (<see cref="BusyCountHooks"/>) is the
/// hourglass/cursor state, NOT the cast gate — it reads 0 while AC still says
/// "too busy". The true LOCAL gate is the cast GESTURE: CMotionInterp's
/// sequenced/one-shot motion queue is non-empty while the wind-up/release
/// animates and empties when it completes (offline RE of acclient.exe; see
/// <see cref="PlayerPhysicsHooks.TryGetCastGestureInProgress"/>).
///
/// This is a SAMPLED gate — no acclient.exe hook is installed. <see cref="Sample"/>
/// runs on the existing 30 Hz NormalPluginPump heartbeat (PluginManager.TickAll)
/// and reads a single CMotionInterp scalar through the already-proven
/// PlayerPhysicsHooks accessor. No new detour, no pattern-scan-and-call (the
/// ACE crash class), no list walk (UAF-safe). The consumer polls the gate at
/// the same tick rate, so the busy→idle edge is observed without any new
/// engine→plugin callback (avoids the NativeAOT reverse-P/Invoke crash class).
/// </summary>
internal static class CastGate
{
    private static int _castBusy;   // 0 = clear to cast, 1 = gesture in progress
    private static int _reachable;  // 1 once CMotionInterp has ever been read
    private static long _gestureCount;
    private static string _statusMessage = "Not sampled yet.";

    public static bool IsInitialized => Volatile.Read(ref _reachable) == 1;
    public static string StatusMessage => _statusMessage;

    /// <summary>Number of cast/action gestures observed completing this session.</summary>
    public static long GestureCount => Interlocked.Read(ref _gestureCount);

    /// <summary>
    /// 0 = clear to cast, 1 = a cast/action gesture is in progress. Mirrors
    /// <see cref="BusyCountHooks.GetBusyState"/> for the host-API pull path.
    /// </summary>
    public static int GetCastBusyState() => Volatile.Read(ref _castBusy);

    /// <summary>
    /// Sampled from the 30 Hz plugin pump (PluginManager.TickAll). Cheap, fully
    /// guarded, never throws. If CMotionInterp isn't reachable (pre-login /
    /// between worlds) the gate reports "clear to cast" so the consumer falls
    /// back to its own throttle/park, which is the hard anti-spam guarantee.
    /// </summary>
    public static void Sample()
    {
        try
        {
            if (!PlayerPhysicsHooks.TryGetCastGestureInProgress(out bool inProgress))
            {
                Volatile.Write(ref _castBusy, 0);
                return;
            }

            Volatile.Write(ref _reachable, 1);

            int was = Volatile.Read(ref _castBusy);
            int now = inProgress ? 1 : 0;
            if (now == was)
                return;

            Volatile.Write(ref _castBusy, now);
            if (now == 1)
            {
                _statusMessage = "Cast gesture in progress.";
            }
            else
            {
                Interlocked.Increment(ref _gestureCount);
                _statusMessage = "Ready (gate open).";
            }
        }
        catch
        {
            // The gate must never destabilise the pump. Default to clear-to-cast;
            // the consumer's throttle still bounds retries.
            Volatile.Write(ref _castBusy, 0);
        }
    }
}

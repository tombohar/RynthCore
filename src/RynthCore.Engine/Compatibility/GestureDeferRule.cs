namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Whether the action ring waits for the motion in flight (AcMainThreadQueue.Drain's gesture defer).
///
/// The ring holds everything but the gesture-safe entries while the character's pending-motion list
/// is non-empty, for at most MaxMs, then runs them and starts a new wait. R-20261123-152528-b02d
/// ("He's doing tiny jumps and attacking air", a Thread on DreamWeave, just after a death and
/// respawn): the list read non-empty for seconds with nothing playing - RynthAi's pacer and the cast
/// gate both logged the animation queue "stuck" after 4 s - so the ring ran only in bursts every
/// 2.5 s. A movement start is dropped as stale at 1.5 s, so nearly every walk the bot sent was
/// dropped ("dropped SetAutoRun(on) 2515 ms"), the character moved 0.65 m in 5 s, nav's stuck
/// check jumped, and the next target's select and attack ran 1.8 s late or not at all.
///
/// A list whose head is the same motion for longer than MaxMs isn't playing it: that is a wedge,
/// and the ring no longer waits on it until the head changes or the list empties (what the cap
/// already did for one wait, now for as long as it lasts). A list that moves on - a bow's shots,
/// swing after swing - is a new motion at the head each time and keeps the old waits.
/// </summary>
internal sealed class GestureDeferRule
{
    public GestureDeferRule(long maxMs) => MaxMs = maxMs;

    /// <summary>The longest one wait lasts, and how long one motion may sit at the head before it counts as stuck.</summary>
    public long MaxMs { get; }

    private long _since;      // when the current wait began (0: not waiting)
    private nint _head;       // the motion at the head of the list when last read (0: none)
    private long _headSince;  // since when it has been the head

    /// <summary>The motion at the head has been there longer than MaxMs: the ring doesn't wait on it.</summary>
    public bool Wedged { get; private set; }

    /// <summary>How long the head has been the same motion.</summary>
    public long HeadAgeMs(long now) => _head == 0 ? 0 : now - _headSince;

    /// <summary>
    /// One drain with entries queued: true when only gesture-safe entries run now.
    /// <paramref name="head"/> is the first pending motion (0: the list is empty).
    /// <paramref name="becameWedged"/> is true on the drain the head is first taken as stuck.
    /// </summary>
    public bool Defer(nint head, long now, out bool becameWedged)
    {
        becameWedged = false;
        if (head == 0)
        {
            _head = 0;
            _headSince = 0;
            Wedged = false;
            _since = 0;
            return false;
        }
        if (head != _head)
        {
            _head = head;
            _headSince = now;
            Wedged = false;
        }
        else if (!Wedged && now - _headSince > MaxMs)
        {
            Wedged = true;
            becameWedged = true;
        }
        if (Wedged)
        {
            _since = 0;
            return false;
        }
        if (_since == 0) _since = now;
        bool defer = now - _since <= MaxMs;
        if (!defer) _since = 0;
        return defer;
    }

    /// <summary>The list couldn't be read: no wait (as before).</summary>
    public void Unread() => _since = 0;

    /// <summary>The gesture-safe entries emptied the ring: the next held entry gets its own full wait.</summary>
    public void RingEmptied() => _since = 0;
}

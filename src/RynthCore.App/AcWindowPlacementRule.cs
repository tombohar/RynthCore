using System;
using System.Collections.Generic;

namespace RynthCore.App;

/// <summary>
/// The launcher's per-account memory of where the AC window sits: what it puts back after the login,
/// and when it writes a new rect to the profile. Pure: MainWindow does the window calls. Checked in
/// tools\LauncherSettingsTests.
///
/// 2026-11-15, a #rynth-issues report, 2026-11-14 ("login changes resolution"): a few seconds after the
/// login the window shrank, half the picture was cut off and the HUD could not be clicked; a second
/// player saw it on mules but not on the main. The launcher had saved 1008x786 at (296,76) six seconds
/// before the client closed (AC's logout / character-select window, 52.6% of the 1608x936 in-game area,
/// so the old "under half" guard let it through), then put that size back on the next login. AC draws
/// at its own resolution whatever the window size, so the smaller window crops the game and leaves the
/// HUD outside the clickable area, and the resolution setting cannot be reached to fix it.
/// So:
///  - Only the position is ever put back. AC sizes its own window from its resolution setting (in the
///    per-account UserPreferences.ini the prefs swap brings in); the launcher never resizes it. This
///    replaces the 2026-10-27 put-back of a shrunk window (AcWindowRestoreRule), which resized it too.
///  - The saved position is used only when the window is about the size it was saved at: a position
///    saved for the small centred login window would put a full-size window half off the screen.
///    It waits for the size to hold (<see cref="RestoreSettle"/>) and, while AC is still at its login
///    size, for it to grow (<see cref="RestoreWatch"/>), then gives up.
///  - The position is pulled onto a monitor's work area at the window's real size.
///  - A new rect is saved only after it held for <see cref="StableFor"/>: the shrink right before
///    the client closes, and AC's own moves at startup and portal-in, never reach the profile.
/// The saved width and height are still written: they are what the size checks compare against.
/// </summary>
internal static class AcWindowPlacementRule
{
    /// <summary>A window rect: outer position and size, as GetWindowRect reports it.</summary>
    public readonly record struct Placement(int X, int Y, int Width, int Height);

    /// <summary>A monitor's work area (the screen minus the taskbar).</summary>
    public readonly record struct WorkArea(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>
    /// How long a new rect must hold before it is saved. AC's own moves (startup, the login window,
    /// portal-in) settle within a few seconds; the report's shrink came 6 s before the client closed.
    /// 30 s clears both with room, and the cost is small: a window moved and closed within 30 s keeps
    /// its old spot.
    /// </summary>
    public static readonly TimeSpan StableFor = TimeSpan.FromSeconds(30);

    /// <summary>How long after the login the restore waits for AC to reach the saved size.</summary>
    public static readonly TimeSpan RestoreWatch = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the window's size must hold before the restore is decided: AC may still be growing its
    /// window from the login size to its resolution when the login is reported, and a position judged
    /// (and pulled onto the screen) at the passing size is wrong for the final one. Two 2 s launcher ticks.
    /// </summary>
    public static readonly TimeSpan RestoreSettle = TimeSpan.FromSeconds(4);

    /// <summary>Pixels of change ignored (DWM frame fudge).</summary>
    public const int Tolerance = 2;

    /// <summary>
    /// How far the outer rect may hang past the work area. Windows 10/11 frames have invisible resize
    /// borders (about 7 px at 100% scaling) outside the visible edge, so a window snapped to the screen
    /// edge reports (-7, 0); the report's own saves were (-4,1). Within this the spot is left alone.
    /// </summary>
    public const int EdgeSlack = 16;

    /// <summary>The saved size and the window's are this close (each side within a quarter) for the saved position to apply.</summary>
    public static bool SizeMatches(int width, int height, int savedWidth, int savedHeight) =>
        savedWidth > 0 && savedHeight > 0 && width > 0 && height > 0
        && Math.Abs(width - savedWidth) * 4L <= Math.Max(width, savedWidth)
        && Math.Abs(height - savedHeight) * 4L <= Math.Max(height, savedHeight);

    /// <summary>The window collapsed below half the reference in-game area: AC's login / character-select window.</summary>
    public static bool IsLoginShrink(int width, int height, int refWidth, int refHeight) =>
        refWidth > 0 && refHeight > 0 && (long)width * height * 2L < (long)refWidth * refHeight;

    /// <summary>
    /// A rect worth looking at: big enough to be the game window, and not AC's startup parking spot at
    /// (-10000,-10000) (multi-monitor setups have negative coordinates, never this negative).
    /// </summary>
    public static bool IsCapturable(Placement p) => p.Width >= 200 && p.Height >= 200 && p.X >= -2000 && p.Y >= -2000;

    public static bool Near(Placement a, Placement b) =>
        Math.Abs(a.X - b.X) <= Tolerance && Math.Abs(a.Y - b.Y) <= Tolerance
        && Math.Abs(a.Width - b.Width) <= Tolerance && Math.Abs(a.Height - b.Height) <= Tolerance;

    // ── Restore ───────────────────────────────────────────────────────────

    public enum RestoreAction
    {
        /// <summary>AC is not at the saved size yet: look again next tick.</summary>
        Wait,
        /// <summary>Move the window to (X, Y), size untouched.</summary>
        Move,
        /// <summary>Leave the window where AC put it; the restore is over.</summary>
        Skip,
    }

    public readonly record struct RestoreDecision(RestoreAction Action, int X, int Y, bool Clamped, string Why);

    /// <summary>
    /// What to do with the saved position, given the window's current size, how long that size has
    /// held, the time since the login was first seen, and the monitors' work areas.
    /// </summary>
    public static RestoreDecision DecideRestore(Placement saved, int width, int height, TimeSpan sizeHeldFor,
                                                TimeSpan sinceLogin, IReadOnlyList<WorkArea> workAreas)
    {
        if (sizeHeldFor < RestoreSettle && sinceLogin <= RestoreWatch)
            return new RestoreDecision(RestoreAction.Wait, 0, 0, false, "window size still changing");
        if (!SizeMatches(width, height, saved.Width, saved.Height))
        {
            return sinceLogin <= RestoreWatch
                ? new RestoreDecision(RestoreAction.Wait, 0, 0, false, "window not at the saved size yet")
                : new RestoreDecision(RestoreAction.Skip, 0, 0, false,
                    $"window is {width}x{height}, the position was saved for {saved.Width}x{saved.Height}");
        }
        if (workAreas.Count == 0)
            return new RestoreDecision(RestoreAction.Skip, 0, 0, false, "no monitor information");

        (int x, int y) = ClampToWorkArea(saved.X, saved.Y, width, height, workAreas);
        bool clamped = x != saved.X || y != saved.Y;
        return new RestoreDecision(RestoreAction.Move, x, y, clamped, clamped ? "pulled onto the screen" : "saved position");
    }

    /// <summary>
    /// The position nearest (x, y) at which a width x height window lies on a work area (give or take
    /// <see cref="EdgeSlack"/>): the area it overlaps most, else the nearest one. A window wider or taller
    /// than the area goes to its left / top edge, so the title bar stays reachable.
    /// </summary>
    public static (int X, int Y) ClampToWorkArea(int x, int y, int width, int height, IReadOnlyList<WorkArea> workAreas)
    {
        if (workAreas.Count == 0) return (x, y);
        WorkArea a = PickArea(x, y, width, height, workAreas);
        return (ClampAxis(x, width, a.Left, a.Right), ClampAxis(y, height, a.Top, a.Bottom));
    }

    private static int ClampAxis(int pos, int size, int lo, int hi)
    {
        int min = lo - EdgeSlack;
        int max = hi - size + EdgeSlack;
        if (max < min) return lo;   // bigger than the area: pin the leading edge
        return Math.Clamp(pos, min, max);
    }

    private static WorkArea PickArea(int x, int y, int width, int height, IReadOnlyList<WorkArea> areas)
    {
        WorkArea best = areas[0];
        long bestOverlap = -1;
        foreach (WorkArea a in areas)
        {
            long ow = Math.Min(x + width, a.Right) - Math.Max(x, a.Left);
            long oh = Math.Min(y + height, a.Bottom) - Math.Max(y, a.Top);
            long overlap = ow > 0 && oh > 0 ? ow * oh : 0;
            if (overlap > bestOverlap) { bestOverlap = overlap; best = a; }
        }
        if (bestOverlap > 0) return best;

        // Entirely off every monitor: the area nearest the window's centre.
        long cx = x + width / 2, cy = y + height / 2;
        long bestDist = long.MaxValue;
        foreach (WorkArea a in areas)
        {
            long dx = cx < a.Left ? a.Left - cx : cx > a.Right ? cx - a.Right : 0;
            long dy = cy < a.Top ? a.Top - cy : cy > a.Bottom ? cy - a.Bottom : 0;
            long d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = a; }
        }
        return best;
    }

    // ── Save ──────────────────────────────────────────────────────────────

    /// <summary>A rect that differs from the saved one, and since when it has held.</summary>
    public sealed record Pending(Placement Rect, DateTime SinceUtc);

    public enum SaveAction
    {
        /// <summary>Nothing new: not capturable, or the same as the reference.</summary>
        None,
        /// <summary>A new rect, not held for <see cref="StableFor"/> yet.</summary>
        Waiting,
        /// <summary>AC's login / character-select window: never saved.</summary>
        LoginShrink,
        /// <summary>Held long enough: write it to the profile.</summary>
        Save,
    }

    /// <param name="FirstSeen">The rect just changed (for logging once instead of every tick).</param>
    public readonly record struct SaveDecision(SaveAction Action, bool FirstSeen);

    /// <summary>
    /// One tick of the save tracker. <paramref name="reference"/> is the saved rect, else the first rect
    /// seen after the login (so AC's own placement is never saved until the player moves the window).
    /// <paramref name="pending"/> is the tracker's state, kept per client by the caller.
    /// </summary>
    public static SaveDecision DecideSave(Placement current, Placement? reference, ref Pending? pending, DateTime nowUtc)
    {
        if (!IsCapturable(current))
            return new SaveDecision(SaveAction.None, false);
        if (reference is Placement r && Near(current, r))
        {
            pending = null;   // back where it was: a shrink that came and went is forgotten
            return new SaveDecision(SaveAction.None, false);
        }

        bool firstSeen = pending is null || !Near(pending.Rect, current);
        if (firstSeen)
            pending = new Pending(current, nowUtc);

        if (reference is Placement rr && IsLoginShrink(current.Width, current.Height, rr.Width, rr.Height))
            return new SaveDecision(SaveAction.LoginShrink, firstSeen);
        if (nowUtc - pending!.SinceUtc < StableFor)
            return new SaveDecision(SaveAction.Waiting, firstSeen);

        pending = null;
        return new SaveDecision(SaveAction.Save, firstSeen);
    }
}

// ============================================================================
//  RynthCore.Engine - UI/AcWindowChromePolicy.cs
//  The decisions behind Settings > Game Client for the Asheron's Call window, as pure
//  functions (no HWND, no engine state) so tools\EngineSettingsTests can run them:
//    - Borderless windowed: what to do given the setting, the "we stripped it" marker
//      and the window's current style bits.
//    - Window position: whether a saved or current rect is worth restoring / saving,
//      and which file a client's rect lives in. Since 2026-11-15 the restore and the
//      save follow the launcher's AcWindowPlacementRule (src\RynthCore.App, compiled in):
//      position only, and a rect is saved only once it held for 30 s. Since 2026-11-21 the
//      window's size comes from AC's resolution (the back buffer), never from a saved rect.
//  AcWindowChrome does the Win32 work and asks these first.
//  Idea and first version: SilentKelpie (RynthCore PR 6).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RynthCore.App;

namespace RynthCore.Engine.UI;

internal static class AcWindowChromePolicy
{
    public const int WsCaption = 0x00C00000;      // WS_BORDER | WS_DLGFRAME
    public const int WsThickFrame = 0x00040000;
    public const int WsSysMenu = 0x00080000;
    public const int WsMinimizeBox = 0x00020000;
    public const int WsMaximizeBox = 0x00010000;
    public const int WsBorder = 0x00800000;

    /// <summary>Caption + frame + sysmenu / min / max: the chrome Borderless Windowed removes.</summary>
    public const int BorderChromeMask =
        WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox | WsBorder;

    // ── Borderless windowed ──────────────────────────────────────────────

    internal enum ChromeAction
    {
        /// <summary>Leave the window alone.</summary>
        None,
        /// <summary>Remove the chrome and remember the style it had (NewMarker).</summary>
        Strip,
        /// <summary>Put the chrome we removed back (NewStyle) and drop the marker.</summary>
        Restore,
        /// <summary>The marker is stale (chrome is back already, or the marker is unusable): drop it.</summary>
        ClearMarker,
    }

    internal readonly record struct ChromeDecision(ChromeAction Action, int NewStyle, int NewMarker, string Reason)
    {
        public static readonly ChromeDecision NoChange = new(ChromeAction.None, 0, 0, "");
    }

    /// <summary>
    /// What to do about the window chrome right now.
    /// <paramref name="marker"/> is the durable "WE stripped this window" record: the window's style
    /// as it was before we stripped it (0 = never stripped by us). Chrome is only ever restored when
    /// that marker exists, so a window that is chrome-less for any other reason (exclusive
    /// fullscreen, an external borderless tool, a game option) is never touched with the setting off.
    /// </summary>
    public static ChromeDecision DecideBorderless(bool want, int marker, int style, bool looksExclusiveFullscreen)
    {
        bool hasChrome = (style & WsCaption) != 0;

        if (want)
        {
            // Not ours to take over: it already looks like exclusive fullscreen.
            if (marker == 0 && looksExclusiveFullscreen)
                return new ChromeDecision(ChromeAction.None, 0, 0, "looks like exclusive fullscreen");
            // No chrome: either we already did it (marker) or it never had any. Nothing to strip.
            if (!hasChrome)
                return ChromeDecision.NoChange;
            // Strip. (Marker present with chrome back means the game put it back; strip again and
            // remember this newer style.)
            return new ChromeDecision(ChromeAction.Strip, style & ~BorderChromeMask, style, "strip");
        }

        // Setting off.
        if (marker == 0)
            return ChromeDecision.NoChange;              // never ours: do nothing at all
        if (hasChrome || (marker & WsCaption) == 0)
            return new ChromeDecision(ChromeAction.ClearMarker, 0, 0, "marker stale");
        return new ChromeDecision(ChromeAction.Restore, style | (marker & BorderChromeMask), 0, "restore");
    }

    // ── Window position ──────────────────────────────────────────────────

    public const int MinWidth = 160, MinHeight = 120, MaxExtent = 16384;
    private const int TitleProbeY = 16;

    internal readonly record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    internal enum PlacementVerdict { Ok, OptedOut, ExclusiveFullscreen, TooSmall, TooLarge, OffScreen }

    /// <summary>
    /// Whether <paramref name="r"/> is a rect worth saving (since 2026-11-15 a restore pulls a saved
    /// position onto the screen instead: DecideRestore). Off-screen rects (a monitor
    /// that is gone) are refused: the window's centre and the middle of its top edge must both be on
    /// a monitor, so neither the window nor its title bar ends up unreachable.
    /// </summary>
    public static PlacementVerdict EvaluatePlacement(bool optedIn, bool looksExclusiveFullscreen,
        Rect r, IReadOnlyList<Rect> monitors)
    {
        if (!optedIn) return PlacementVerdict.OptedOut;
        if (looksExclusiveFullscreen) return PlacementVerdict.ExclusiveFullscreen;
        if (r.Width < MinWidth || r.Height < MinHeight) return PlacementVerdict.TooSmall;
        if (r.Width > MaxExtent || r.Height > MaxExtent) return PlacementVerdict.TooLarge;
        int cx = r.Left + r.Width / 2;
        if (!OnAnyMonitor(monitors, cx, r.Top + r.Height / 2) || !OnAnyMonitor(monitors, cx, r.Top + TitleProbeY))
            return PlacementVerdict.OffScreen;
        return PlacementVerdict.Ok;
    }

    private static bool OnAnyMonitor(IReadOnlyList<Rect> monitors, int x, int y)
    {
        for (int i = 0; i < monitors.Count; i++)
            if (monitors[i].Contains(x, y)) return true;
        return false;
    }

    // ── Restore and save (2026-11-15) ────────────────────────────────────
    //
    // Report R-20261115-160655-1201: right after the 2026.11.15.1 update one player's main came
    // up in a small window, cropped, with the HUD out of reach, for three launches in a row. The
    // log: "AcWindowChrome: restored AC window to (296,51) 806x635" in a 2560x1600 game. This
    // option saved whatever rect differed from the last one every 10 s, character select
    // included (the character name is kept after a logout), so AC's 800x600 logout window
    // became "the" rect, and the restore put its SIZE back with SetWindowPos. AC draws at its own
    // resolution whatever the window size, so the game was cropped: the launcher's bug fixed in
    // 2026.11.15.1 (AcWindowPlacementRule), here in the engine. It had not shown before because
    // the launcher's put-back of a shrunk window (AcWindowRestoreRule, removed in that release)
    // resized the window again right after. So, as in the launcher:
    //  - only the position is put back (SWP_NOSIZE), and only when the window is about the size
    //    the position was saved at; it waits for AC's size to settle and gives up after 60 s;
    //  - the position is pulled onto a monitor's work area at the window's real size;
    //  - a rect is saved only in the world (never at character select), only after it held for
    //    30 s, and never when it is the login-size shrink of the in-game window;
    //  - a profile already holding the small rect heals itself: its position is skipped (the
    //    sizes differ) and the real rect is saved 30 s later.

    internal static AcWindowPlacementRule.Placement ToPlacement(Rect r) => new(r.Left, r.Top, r.Width, r.Height);

    /// <summary>
    /// What to do with the saved rect now: wait, move (position only), or leave the window where
    /// AC put it. <paramref name="sinceInWorld"/> runs from the first look after the login. The
    /// restore with AC's resolution unknown; the overload below takes it.
    /// </summary>
    public static AcWindowPlacementRule.RestoreDecision DecideRestore(Rect? saved, bool minimisedOrMaximised,
        bool looksExclusiveFullscreen, Rect current, TimeSpan sizeHeldFor, TimeSpan sinceInWorld,
        IReadOnlyList<Rect> workAreas)
    {
        if (saved is not Rect sv)
            return new(AcWindowPlacementRule.RestoreAction.Skip, 0, 0, false, "nothing saved");
        if (minimisedOrMaximised)
            return new(AcWindowPlacementRule.RestoreAction.Skip, 0, 0, false, "window minimised or maximised");
        if (looksExclusiveFullscreen)
            return new(AcWindowPlacementRule.RestoreAction.Skip, 0, 0, false, "window looks like exclusive fullscreen");
        if (!AcWindowPlacementRule.IsCapturable(ToPlacement(current)))
        {
            // Parked off-screen or still tiny: AC has not shown its game window yet.
            return sinceInWorld <= AcWindowPlacementRule.RestoreWatch
                ? new(AcWindowPlacementRule.RestoreAction.Wait, 0, 0, false, "window not shown yet")
                : new(AcWindowPlacementRule.RestoreAction.Skip, 0, 0, false, "window never showed");
        }
        var areas = new List<AcWindowPlacementRule.WorkArea>(workAreas.Count);
        foreach (Rect a in workAreas) areas.Add(new AcWindowPlacementRule.WorkArea(a.Left, a.Top, a.Right, a.Bottom));
        return AcWindowPlacementRule.DecideRestore(ToPlacement(sv), current.Width, current.Height,
            sizeHeldFor, sinceInWorld, areas);
    }

    // ── AC's resolution (2026-11-21) ─────────────────────────────────────
    //
    // A window saved at 1940x1100 (client 1934x1071) was put back with its size at every login on a
    // 1600x900 game (an engine and a launcher from before the 2026-11-15 rule). AC draws into a back
    // buffer at its resolution and stretches it to whatever client area the window has: here about
    // 21%, and ImGui's DisplaySize (the client area, 1934x1071) no longer matched GameMatrixCapture's
    // 1600x900 viewport, so nameplates and the rest of the overlay were misplaced too. So:
    //  - The size of AC's window comes from AC's resolution, never from a saved rect: its client area
    //    must be the D3D back buffer's size. A window that already is that size keeps it; one that is
    //    not (and stays so for MismatchHold) is resized to it, its frame (outer minus client, 0 when
    //    borderless) kept as it is.
    //  - A saved size that is not that size is ignored, with one log line (SavedSizeIgnored).
    //  - A rect whose client area is not the resolution (a stretched picture) is never saved.
    //  - A resolution the player picks in AC (1920x1080, say) is the back buffer's size as soon as AC
    //    applies it, so the window follows it.
    // With the back buffer unknown (not read yet) the restore is the position-only one above.

    /// <summary>A width and height in pixels.</summary>
    internal readonly record struct Size(int Width, int Height)
    {
        public bool IsUsable => Width >= MinWidth && Height >= MinHeight && Width <= MaxExtent && Height <= MaxExtent;
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}");
    }

    /// <summary>
    /// How long the client area must have differed from AC's resolution before the restore resizes the
    /// window: AC resizes its own window and resets its device around the login, not quite together.
    /// </summary>
    public static readonly TimeSpan MismatchHold = TimeSpan.FromSeconds(10);

    /// <summary>The client area is exactly AC's resolution: the picture is drawn 1:1, not stretched.</summary>
    public static bool ClientFits(Size client, Size draw) => client.Width == draw.Width && client.Height == draw.Height;

    /// <summary>
    /// The outer size at which the window's client area is <paramref name="draw"/>, keeping the window's
    /// current frame (outer minus client: caption and borders, 0 when borderless).
    /// </summary>
    public static Size OuterForDrawSize(Size draw, Rect outer, Size client)
        => new(draw.Width + Math.Max(0, outer.Width - client.Width), draw.Height + Math.Max(0, outer.Height - client.Height));

    /// <summary>
    /// The restore with AC's resolution known: where to move the window (Action Move, X, Y), whether to
    /// resize it (Resize, to Width x Height, which is AC's resolution plus the window's frame), and the
    /// log line for a saved size that is not put back.
    /// </summary>
    internal readonly record struct RestorePlan(AcWindowPlacementRule.RestoreAction Action, int X, int Y, bool Clamped,
        string Why, bool Resize, int Width, int Height, string? SavedSizeIgnored)
    {
        public static RestorePlan From(AcWindowPlacementRule.RestoreDecision d) => new(d.Action, d.X, d.Y, d.Clamped, d.Why, false, 0, 0, null);
    }

    /// <summary>
    /// What to do with the saved rect now, given the window's client area and AC's resolution
    /// (<paramref name="draw"/>: the back buffer's size, null while unknown). The saved size never
    /// sizes the window: only AC's resolution does.
    /// </summary>
    public static RestorePlan DecideRestore(Rect? saved, bool minimisedOrMaximised, bool looksExclusiveFullscreen,
        Rect current, Size client, Size? draw, TimeSpan sizeHeldFor, TimeSpan sinceInWorld, IReadOnlyList<Rect> workAreas)
    {
        // Nothing saved, the minimised / maximised / fullscreen guards, AC's startup park, and AC's
        // resolution not known: exactly the position-only restore (never a resize).
        if (draw is not Size d || !d.IsUsable || saved is not Rect sv || minimisedOrMaximised || looksExclusiveFullscreen
            || !AcWindowPlacementRule.IsCapturable(ToPlacement(current)))
            return RestorePlan.From(DecideRestore(saved, minimisedOrMaximised, looksExclusiveFullscreen, current,
                sizeHeldFor, sinceInWorld, workAreas));

        bool watching = sinceInWorld <= AcWindowPlacementRule.RestoreWatch;
        if (sizeHeldFor < AcWindowPlacementRule.RestoreSettle && watching)
            return Wait("window size still changing");
        bool fits = ClientFits(client, d);
        if (!fits && sizeHeldFor < MismatchHold && watching)
            return Wait("window not at AC's resolution yet");

        Size target = fits ? new Size(current.Width, current.Height) : OuterForDrawSize(d, current, client);
        string? ignored = AcWindowPlacementRule.SavedSizeIgnored(sv.Width, sv.Height, target.Width, target.Height, d.Width, d.Height);

        // A position saved for a very different window (AC's small login window) is not this window's.
        if (!AcWindowPlacementRule.SizeMatches(target.Width, target.Height, sv.Width, sv.Height))
        {
            if (watching && fits)
                return Wait("window not at the saved size yet");   // AC may still be at its login size
            return new(AcWindowPlacementRule.RestoreAction.Skip, 0, 0, false,
                $"the position was saved for a {sv.Width}x{sv.Height} window, AC's is {target}", !fits, target.Width, target.Height, ignored);
        }
        if (workAreas.Count == 0)
            return new(AcWindowPlacementRule.RestoreAction.Skip, 0, 0, false, "no monitor information", !fits, target.Width, target.Height, ignored);

        var areas = new List<AcWindowPlacementRule.WorkArea>(workAreas.Count);
        foreach (Rect a in workAreas) areas.Add(new AcWindowPlacementRule.WorkArea(a.Left, a.Top, a.Right, a.Bottom));
        (int x, int y) = AcWindowPlacementRule.ClampToWorkArea(sv.Left, sv.Top, target.Width, target.Height, areas);
        bool clamped = x != sv.Left || y != sv.Top;
        return new(AcWindowPlacementRule.RestoreAction.Move, x, y, clamped, clamped ? "pulled onto the screen" : "saved position",
            !fits, target.Width, target.Height, ignored);

        static RestorePlan Wait(string why) => new(AcWindowPlacementRule.RestoreAction.Wait, 0, 0, false, why, false, 0, 0, null);
    }

    /// <summary>
    /// Why a rect must not be saved because of its size, or null: its client area is not AC's
    /// resolution (the picture is stretched), so its size is not the window's. Unknown resolution: no
    /// objection (the position-only rules decide).
    /// </summary>
    public static string? SaveRefusedForSize(Size client, Size? draw)
        => draw is Size d && d.IsUsable && !ClientFits(client, d)
            ? $"its client area {client} is not AC's resolution {d}"
            : null;

    /// <summary>
    /// One look at the window for the save. <paramref name="inWorld"/> false (character select,
    /// the login screen, AC closing) never saves and forgets a pending rect, so time spent at
    /// character select never counts toward the 30 s. <paramref name="saveable"/> false (minimised,
    /// maximised, exclusive fullscreen) likewise. <paramref name="force"/> is Save Layout Now: no
    /// 30 s wait, but still never the login-size shrink. <paramref name="reference"/> is the saved
    /// rect, else the first rect seen after the login (so AC's own placement is never saved until
    /// the player moves the window).
    /// </summary>
    public static AcWindowPlacementRule.SaveDecision DecideSave(bool inWorld, bool saveable, bool force,
        Rect current, Rect? reference, ref AcWindowPlacementRule.Pending? pending, DateTime nowUtc)
    {
        if (!inWorld || !saveable)
        {
            pending = null;
            return new(AcWindowPlacementRule.SaveAction.None, false);
        }
        AcWindowPlacementRule.Placement cur = ToPlacement(current);
        AcWindowPlacementRule.Placement? rf = reference is Rect r ? ToPlacement(r) : null;
        if (!force)
            return AcWindowPlacementRule.DecideSave(cur, rf, ref pending, nowUtc);

        if (!AcWindowPlacementRule.IsCapturable(cur))
            return new(AcWindowPlacementRule.SaveAction.None, false);
        if (rf is AcWindowPlacementRule.Placement f && AcWindowPlacementRule.IsLoginShrink(cur.Width, cur.Height, f.Width, f.Height))
            return new(AcWindowPlacementRule.SaveAction.LoginShrink, true);
        pending = null;
        return new(AcWindowPlacementRule.SaveAction.Save, true);
    }

    // ── Per-client file ──────────────────────────────────────────────────

    public const string SharedFileName = "ac_window.txt";

    /// <summary>The per-client key: "&lt;server&gt;/&lt;character&gt;" (same shape as the other per-character
    /// stores), or null while no character is known.</summary>
    public static string? ClientKey(string? server, string? character)
    {
        string c = (character ?? string.Empty).Trim();
        if (c.Length == 0) return null;
        string s = (server ?? string.Empty).Trim();
        return s.Length > 0 ? s + "/" + c : c;
    }

    /// <summary>
    /// The file for a client's rect: "ac_window.&lt;safe name&gt;-&lt;hash&gt;.txt", or the shared
    /// "ac_window.txt" when there is no key. The hash keeps names that sanitise alike apart.
    /// </summary>
    public static string PlacementFileName(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return SharedFileName;
        string k = key.Trim();
        var sb = new StringBuilder(k.Length);
        foreach (char ch in k)
        {
            if (sb.Length >= 48) break;
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch == '-' ? char.ToLowerInvariant(ch) : '_');
        }
        uint h = 2166136261;                       // FNV-1a over the lower-cased key
        foreach (char ch in k.ToLowerInvariant())
            h = (h ^ ch) * 16777619;
        return "ac_window." + sb + "-" + h.ToString("x8", CultureInfo.InvariantCulture) + ".txt";
    }

    /// <summary>
    /// Whether the placement file can be read or written yet, and which key it uses. The character
    /// is only known after login; the shared file is the fallback once a client has gone
    /// <paramref name="fallbackAfterMs"/> without one.
    /// </summary>
    public static bool TryResolveKey(string? server, string? character, long msSinceFirstTick,
        long fallbackAfterMs, out string? key)
    {
        key = ClientKey(server, character);
        if (key != null) return true;
        return msSinceFirstTick >= fallbackAfterMs;   // key stays null: the shared file
    }

    // ── File format ──────────────────────────────────────────────────────

    public static string FormatPlacement(Rect r)
        => string.Create(CultureInfo.InvariantCulture,
            $"# RynthCore AC window placement - auto-generated.\nleft={r.Left}\ntop={r.Top}\nwidth={r.Width}\nheight={r.Height}\n");

    public static bool TryParsePlacement(IEnumerable<string> lines, out Rect rect)
    {
        rect = default;
        int left = 0, top = 0, width = 0, height = 0;
        bool haveLeft = false, haveTop = false;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            if (!int.TryParse(line[(eq + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) continue;
            if (key.Equals("left", StringComparison.OrdinalIgnoreCase)) { left = n; haveLeft = true; }
            else if (key.Equals("top", StringComparison.OrdinalIgnoreCase)) { top = n; haveTop = true; }
            else if (key.Equals("width", StringComparison.OrdinalIgnoreCase)) width = n;
            else if (key.Equals("height", StringComparison.OrdinalIgnoreCase)) height = n;
        }
        if (!haveLeft || !haveTop || width <= 0 || height <= 0) return false;
        rect = new Rect(left, top, left + width, top + height);
        return true;
    }
}

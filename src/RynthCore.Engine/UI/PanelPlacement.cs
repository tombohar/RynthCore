// ============================================================================
//  RynthCore.Engine - UI/PanelPlacement.cs
//  Where a docked window is drawn versus where it belongs (2026-11-12, "a few
//  of the panels still move around on reload and login (shortcuts, our own
//  toolbar)").
//
//  A docked panel (ImGuiPanelHost) and the toolbar (ImGuiBar) each keep a home:
//  the spot read from panel_state.txt, or the one the player dragged it to (or
//  a script / face asked for). It is drawn at its home pulled onto the screen:
//  [0, display - size] on each axis, the home itself while the display is not
//  known. The pull is worked out again whenever the window's size or the
//  display changes, always from the home, so it lets go as soon as the window
//  fits again: a fit-height plugin window (the Shortcuts bar) that comes up one
//  frame at its saved, taller height, or one whose content is a line long until
//  the game's slots are read, or a client smaller than the place for a moment,
//  only moves the window while it lasts. The home is what is saved: a pull on
//  screen is never written over the saved spot.
//
//  Before, the open's save wrote the pulled-in spot (ImGuiPanelHost.Apply ->
//  Persist(entry.Pos)), a later size change was never undone (only a display
//  change re-applied the saved spot), and the toolbar's pull on screen stayed
//  for the session and was saved by the next size step (its _sizeWatch), so a
//  window near the right or bottom edge came back further in after the next
//  reload or login.
//
//  Pure (no ImGui, no files): tools/PanelLayoutTests. One instance per window,
//  AC's render thread.
// ============================================================================

using System;
using System.Globalization;
using System.Numerics;

namespace RynthCore.Engine.UI;

internal sealed class HomePlace
{
    private Vector2 _size, _display;

    /// <summary>The window's own spot (client px): saved, the player's, or asked for. Never a pull on screen.</summary>
    public Vector2 Home { get; private set; }

    /// <summary>Where it is drawn (client px): the home, pulled onto the screen.</summary>
    public Vector2 Drawn { get; private set; }

    /// <summary>A home has been set (a window never placed has none).</summary>
    public bool IsSet { get; private set; }

    /// <summary>Drawn somewhere else than its home (pulled onto the screen).</summary>
    public bool Pulled => IsSet && Drawn != Home;

    /// <summary>The home, and where that draws for <paramref name="size"/> on <paramref name="display"/>.</summary>
    public void Set(Vector2 home, Vector2 size, Vector2 display)
    {
        Home = home;
        IsSet = true;
        _size = size;
        _display = display;
        Drawn = OnScreen(home, size, display);
    }

    /// <summary>
    /// The window's size or the display changed (or not): drawn from the home again. True when where it
    /// draws changed. Cheap when neither changed. Without a home: nothing.
    /// </summary>
    public bool Fit(Vector2 size, Vector2 display)
    {
        if (!IsSet || (size == _size && display == _display)) return false;
        _size = size;
        _display = display;
        Vector2 next = OnScreen(Home, size, display);
        if (next == Drawn) return false;
        Drawn = next;
        return true;
    }

    /// <summary>The player (or a script, a face) put the window at <paramref name="pos"/>: that is its home now.</summary>
    public void MoveTo(Vector2 pos, Vector2 size, Vector2 display) => Set(pos, size, display);

    /// <summary>
    /// Client px: <paramref name="pos"/> pulled into [0, display - size] on each axis (0 when the window is
    /// bigger than the display); <paramref name="pos"/> itself while the display is not known (1 px or less).
    /// </summary>
    public static Vector2 OnScreen(Vector2 pos, Vector2 size, Vector2 display)
    {
        if (display.X <= 1 || display.Y <= 1) return pos;
        return new Vector2(
            Math.Clamp(pos.X, 0, Math.Max(0, display.X - size.X)),
            Math.Clamp(pos.Y, 0, Math.Max(0, display.Y - size.Y)));
    }

    /// <summary>Within half a pixel on both axes (ImGui truncates window positions).</summary>
    public static bool Near(Vector2 a, Vector2 b) => MathF.Abs(a.X - b.X) <= 0.5f && MathF.Abs(a.Y - b.Y) <= 0.5f;

    /// <summary>"(1301,811)" for the log.</summary>
    public static string Xy(Vector2 v) =>
        "(" + v.X.ToString("0", CultureInfo.InvariantCulture) + "," + v.Y.ToString("0", CultureInfo.InvariantCulture) + ")";

    /// <summary>"299x89" for the log.</summary>
    public static string Wh(Vector2 v) =>
        v.X.ToString("0", CultureInfo.InvariantCulture) + "x" + v.Y.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>
    /// The load line (one per window per placement): where it draws and why. <paramref name="source"/>: where
    /// the home came from ("the saved row", "its first-open corner", ...).
    /// </summary>
    public string Describe(string what, string source)
    {
        string size = _size.X > 0 && _size.Y > 0 ? " " + Wh(_size) : string.Empty;
        string where = $"{what} placed at {Xy(Drawn)}{size} from {source} {Xy(Home)}";
        if (_display.X <= 1 || _display.Y <= 1) return where + "; the display size isn't known yet: drawn at its home.";
        string display = Wh(_display);
        return Pulled
            ? where + $"; pulled onto the {display} display (its home is kept and saved; it goes back when it fits)."
            : where + $" on the {display} display: as saved.";
    }

    /// <summary>The line when a size or display change pulls the window off its home (once per window per load).</summary>
    public string DescribePull(string what) =>
        $"{what} pulled onto the screen at {Xy(Drawn)}: its home {Xy(Home)} at {Wh(_size)} doesn't fit the {Wh(_display)} display. Not saved; it goes back when it fits.";
}

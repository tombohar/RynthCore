// ============================================================================
//  RynthCore.Engine - UI/PopOutPlacement.cs
//  Where a popped-out window opens and where "bring the windows back" puts it.
//  Pure (no Win32, no ImGui): ImGuiPopOuts and LayeredWindow feed it the game
//  window's client rectangle and the monitor's work area; tools/PanelLayoutTests
//  checks it.
//
//  A pop-out's spot is saved in screen pixels (panel_state.txt). Saved alone, it
//  stays where it was on the desktop when the game window doesn't: a client the
//  launcher opens somewhere else, another client using the same layout, a game
//  window dragged since. Players then lost them "outside the window" at launch
//  and moved the game window around to find them (2026-11-24 report). So each
//  saved spot carries the game window's client rectangle at that moment (a
//  panel_state value, AnchorKey(title)): a pop-out that was over the game window
//  then opens at the same place on it now; one put away from it (another monitor)
//  keeps its screen spot. Either way it then comes onto the nearest monitor's
//  work area: wholly when it fits, else its top-left corner, so the title bar is
//  there to drag.
// ============================================================================

using System;
using System.Globalization;

namespace RynthCore.Engine.UI;

/// <summary>A rectangle in screen pixels: left/top inclusive, right/bottom exclusive.</summary>
internal readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    public static ScreenRect FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);

    public bool Intersects(ScreenRect o) => !IsEmpty && !o.IsEmpty && Left < o.Right && o.Left < Right && Top < o.Bottom && o.Top < Bottom;
}

internal static class PopOutPlacement
{
    /// <summary>The panel_state value that holds a pop-out's game window rectangle: "popout.anchor.&lt;title&gt;".</summary>
    public const string AnchorKeyPrefix = "popout.anchor.";

    /// <summary>Cascade step between windows "bring the windows back" lines up (unscaled px).</summary>
    public const int GatherStep = 30;

    /// <summary>Where the first gathered window goes, from the game window's top-left (unscaled px).</summary>
    public const int GatherInset = 40;

    public static string AnchorKey(string title) => AnchorKeyPrefix + title;

    /// <summary>"left,top,width,height" of the game window's client area on the screen.</summary>
    public static string FormatAnchor(ScreenRect client) =>
        string.Create(CultureInfo.InvariantCulture, $"{client.Left},{client.Top},{client.Width},{client.Height}");

    /// <summary>Reads <see cref="FormatAnchor"/>'s text; false for anything else (or an empty rectangle).</summary>
    public static bool TryParseAnchor(string? text, out ScreenRect client)
    {
        client = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] p = text.Split(',');
        if (p.Length != 4) return false;
        var v = new int[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(p[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i])) return false;
        if (v[2] <= 0 || v[3] <= 0) return false;
        client = ScreenRect.FromSize(v[0], v[1], v[2], v[3]);
        return true;
    }

    /// <summary>
    /// A saved pop-out spot for the game window as it is now: when the pop-out was over the game
    /// window when it was saved (<paramref name="savedClient"/>), it moves as far as the game
    /// window has moved since (<paramref name="clientNow"/>); otherwise, or when either rectangle
    /// isn't known, the spot stays. True when it moved.
    /// </summary>
    public static bool FollowGameWindow(ref int left, ref int top, int width, int height, ScreenRect? savedClient, ScreenRect? clientNow)
    {
        if (savedClient is not { IsEmpty: false } was || clientNow is not { IsEmpty: false } now) return false;
        if (!ScreenRect.FromSize(left, top, Math.Max(1, width), Math.Max(1, height)).Intersects(was)) return false;
        int dx = now.Left - was.Left, dy = now.Top - was.Top;
        if (dx == 0 && dy == 0) return false;
        left += dx;
        top += dy;
        return true;
    }

    /// <summary>
    /// Pulls a window inside <paramref name="work"/> (a monitor's work area): wholly when it fits,
    /// else its top-left corner, so its title bar is on the screen. True when it had to move.
    /// </summary>
    public static bool ClampInside(ref int left, ref int top, int width, int height, ScreenRect work)
    {
        if (work.IsEmpty) return false;
        int x = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        int y = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
        if (x == left && y == top) return false;
        left = x;
        top = y;
        return true;
    }

    /// <summary>"Bring the windows back"'s answer: <paramref name="count"/> open pop-outs, <paramref name="moved"/> of them moved.</summary>
    public static string GatherReply(int count, int moved) => count <= 0
        ? "No popped-out windows are open. Docked panels always stay inside the game window."
        : $"Brought {count} popped-out window{(count == 1 ? "" : "s")} onto the game window"
          + (moved < count ? $" ({count - moved} already there)." : ".");

    /// <summary>
    /// "Bring the windows back": where the <paramref name="index"/>th open pop-out goes on the game
    /// window, a cascade from its top-left, starting over at the top when the next step would put
    /// the window's corner past the client's middle. Kept inside the client when it fits.
    /// </summary>
    public static (int Left, int Top) GatherSpot(int index, int width, int height, ScreenRect client, float scale = 1f)
    {
        scale = float.IsFinite(scale) && scale > 0.25f ? scale : 1f;
        int step = Math.Max(8, (int)(GatherStep * scale)), inset = Math.Max(0, (int)(GatherInset * scale));
        int room = Math.Max(1, Math.Min(client.Width, client.Height) / 2 - inset);
        int perColumn = Math.Max(1, room / step + 1);
        int row = Math.Max(0, index) % perColumn, column = Math.Max(0, index) / perColumn;
        int left = client.Left + inset + row * step + column * step * 2;
        int top = client.Top + inset + row * step;
        ClampInside(ref left, ref top, width, height, client);
        return (left, top);
    }
}

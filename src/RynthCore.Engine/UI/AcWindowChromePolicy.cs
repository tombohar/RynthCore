// ============================================================================
//  RynthCore.Engine - UI/AcWindowChromePolicy.cs
//  The decisions behind Settings > Display for the Asheron's Call window, as pure
//  functions (no HWND, no engine state) so tools\EngineSettingsTests can run them:
//    - Borderless windowed: what to do given the setting, the "we stripped it" marker
//      and the window's current style bits.
//    - Window position: whether a saved or current rect is worth restoring / saving,
//      and which file a client's rect lives in.
//  AcWindowChrome does the Win32 work and asks these first.
//  Idea and first version: SilentKelpie (RynthCore PR 6).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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
    /// Whether <paramref name="r"/> is a rect worth saving or restoring. Off-screen rects (a monitor
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

    /// <summary>Write only when the rect is saveable and differs from what was last written.</summary>
    public static bool ShouldWritePlacement(PlacementVerdict verdict, Rect current, Rect? lastWritten)
        => verdict == PlacementVerdict.Ok && (lastWritten is null || lastWritten.Value != current);

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

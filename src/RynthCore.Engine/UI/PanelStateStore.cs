// ============================================================================
//  RynthCore.Engine - UI/PanelStateStore.cs
//  Tiny disk-backed store for Avalonia panel positions/sizes/open-state so a
//  hot reload doesn't snap every panel back to the cascade-default. Plain
//  text key=value to avoid pulling System.Text.Json into the AOT trim graph
//  for the sake of ~30 lines of state.
//
//  File format:
//    bar=<left>,<top>[,<docked|floating>,<floatingLeft>,<floatingTop>]
//    panel.<title>=<open|closed>,<left>,<top>,<width>,<height>
//                  [,<docked|floating>,<screenLeft>,<screenTop>]
//
//  The trailing three columns on each row were added when panels (and later
//  the bar itself) gained the ability to detach into their own top-level
//  layered windows. Files that pre-date the feature still parse — the floating
//  mode just defaults to docked and the screen coords default to 0.
//
//  Stored under %LocalAppData%\RynthCore\panel_state.txt — survives reloads
//  and out-of-game (the Runtime dir might be wiped during deploy).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RynthCore.Engine.UI;

internal static class PanelStateStore
{
    public readonly record struct PanelEntry(
        bool Open,
        double Left,
        double Top,
        double Width,
        double Height,
        bool Floating = false,
        double FloatingLeft = 0,
        double FloatingTop = 0);

    private static readonly object Sync = new();
    private static readonly Dictionary<string, PanelEntry> Panels = new(StringComparer.OrdinalIgnoreCase);
    private static (double Left, double Top)? _barPosition;
    private static bool _barFloating;
    private static (double Left, double Top)? _barFloatingPosition;
    private static bool _loaded;

    private static string FilePath
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "RynthCore", "panel_state.txt");
        }
    }

    public static (double Left, double Top)? BarPosition
    {
        get { lock (Sync) { Load(); return _barPosition; } }
    }

    /// <summary>
    /// True if the bar should restore in its own top-level floating layered
    /// window (instead of inside the in-AC overlay canvas).
    /// </summary>
    public static bool BarFloating
    {
        get { lock (Sync) { Load(); return _barFloating; } }
    }

    /// <summary>
    /// Last-known screen coords of the floating bar. Null if the bar has never
    /// been popped out yet (the popout path falls back to a sensible default).
    /// </summary>
    public static (double Left, double Top)? BarFloatingPosition
    {
        get { lock (Sync) { Load(); return _barFloatingPosition; } }
    }

    public static bool TryGetPanel(string title, out PanelEntry entry)
    {
        lock (Sync)
        {
            Load();
            return Panels.TryGetValue(title, out entry);
        }
    }

    public static IEnumerable<KeyValuePair<string, PanelEntry>> AllOpenPanels()
    {
        lock (Sync)
        {
            Load();
            foreach (var kv in Panels)
                if (kv.Value.Open)
                    yield return kv;
        }
    }

    public static void SetBarPosition(double left, double top)
    {
        lock (Sync)
        {
            Load();
            _barPosition = (left, top);
            Save();
        }
    }

    /// <summary>
    /// Persist the floating-mode flag and last-known screen coords. Called
    /// from PopOutBar / RedockBar and from the FloatingPanelHost move callback
    /// while the bar is floating. Leaves the docked <c>BarPosition</c>
    /// untouched so a redock returns to the user's last docked location.
    /// </summary>
    public static void SetBarFloatingState(bool floating, double floatingLeft, double floatingTop)
    {
        lock (Sync)
        {
            Load();
            _barFloating = floating;
            _barFloatingPosition = (floatingLeft, floatingTop);
            Save();
        }
    }

    public static void SetPanel(string title, PanelEntry entry)
    {
        lock (Sync)
        {
            Load();
            Panels[title] = entry;
            Save();
        }
    }

    public static void MarkPanelClosed(string title)
    {
        lock (Sync)
        {
            Load();
            if (Panels.TryGetValue(title, out PanelEntry existing))
            {
                Panels[title] = existing with { Open = false };
                Save();
            }
        }
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            string path = FilePath;
            if (!File.Exists(path)) return;

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim();
                string value = line[(eq + 1)..].Trim();

                if (string.Equals(key, "bar", StringComparison.OrdinalIgnoreCase))
                {
                    // 2 = legacy (left, top)
                    // 5 = current (left, top, docked|floating, floatingLeft, floatingTop)
                    string[] barParts = value.Split(',');
                    if (barParts.Length == 2 && TryParseDoubles(value, 2, out double[] legacyVals))
                    {
                        _barPosition = (legacyVals[0], legacyVals[1]);
                    }
                    else if (barParts.Length == 5 &&
                             TryParseDoubles(string.Join(',', barParts, 0, 2), 2, out double[] dockedVals) &&
                             TryParseDoubles(string.Join(',', barParts, 3, 2), 2, out double[] floatingVals))
                    {
                        _barPosition = (dockedVals[0], dockedVals[1]);
                        _barFloating = string.Equals(barParts[2].Trim(), "floating", StringComparison.OrdinalIgnoreCase);
                        _barFloatingPosition = (floatingVals[0], floatingVals[1]);
                    }
                }
                else if (key.StartsWith("panel.", StringComparison.OrdinalIgnoreCase))
                {
                    string title = key["panel.".Length..];
                    string[] parts = value.Split(',');
                    // 5 = legacy (open, left, top, width, height)
                    // 8 = current (legacy + floating-mode, screenLeft, screenTop)
                    if (parts.Length != 5 && parts.Length != 8) continue;

                    bool open = string.Equals(parts[0].Trim(), "open", StringComparison.OrdinalIgnoreCase);
                    if (!TryParseDoubles(string.Join(',', parts, 1, 4), 4, out double[] dims))
                        continue;

                    bool floating = false;
                    double floatingLeft = 0;
                    double floatingTop = 0;
                    if (parts.Length == 8)
                    {
                        floating = string.Equals(parts[5].Trim(), "floating", StringComparison.OrdinalIgnoreCase);
                        if (TryParseDoubles(string.Join(',', parts, 6, 2), 2, out double[] screen))
                        {
                            floatingLeft = screen[0];
                            floatingTop = screen[1];
                        }
                    }

                    Panels[title] = new PanelEntry(open, dims[0], dims[1], dims[2], dims[3], floating, floatingLeft, floatingTop);
                }
            }
        }
        catch (Exception ex)
        {
            try { RynthLog.UI($"PanelStateStore: load failed - {ex.Message}"); } catch { }
        }
    }

    private static void Save()
    {
        try
        {
            string path = FilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var sw = new StreamWriter(path, append: false);
            sw.WriteLine("# RynthCore Avalonia panel state — auto-generated, hand-edits OK.");
            if (_barPosition is { } bar)
            {
                // Always emit the extended form if we have any floating state to
                // remember; legacy 2-column form only when no popout has happened.
                if (_barFloating || _barFloatingPosition is not null)
                {
                    (double fl, double ft) = _barFloatingPosition ?? (0, 0);
                    sw.WriteLine(FormattableString.Invariant(
                        $"bar={bar.Left:0.##},{bar.Top:0.##},{(_barFloating ? "floating" : "docked")},{fl:0.##},{ft:0.##}"));
                }
                else
                {
                    sw.WriteLine(FormattableString.Invariant($"bar={bar.Left:0.##},{bar.Top:0.##}"));
                }
            }

            foreach (var kv in Panels)
            {
                PanelEntry e = kv.Value;
                sw.WriteLine(FormattableString.Invariant(
                    $"panel.{kv.Key}={(e.Open ? "open" : "closed")},{e.Left:0.##},{e.Top:0.##},{e.Width:0.##},{e.Height:0.##},{(e.Floating ? "floating" : "docked")},{e.FloatingLeft:0.##},{e.FloatingTop:0.##}"));
            }
        }
        catch (Exception ex)
        {
            try { RynthLog.UI($"PanelStateStore: save failed - {ex.Message}"); } catch { }
        }
    }

    private static bool TryParseDoubles(string csv, int expected, out double[] values)
    {
        string[] parts = csv.Split(',');
        if (parts.Length != expected)
        {
            values = Array.Empty<double>();
            return false;
        }

        values = new double[expected];
        for (int i = 0; i < expected; i++)
        {
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                return false;
        }
        return true;
    }
}

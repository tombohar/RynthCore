// ============================================================================
//  RynthCore.Engine - UI/PanelStateStore.cs
//  Tiny disk-backed store for Avalonia panel positions/sizes/open-state so a
//  hot reload doesn't snap every panel back to the cascade-default. Plain
//  text key=value to avoid pulling System.Text.Json into the AOT trim graph
//  for the sake of ~30 lines of state.
//
//  File format:
//    bar=<left>,<top>[,<docked|floating>,<floatingLeft>,<floatingTop>]
//    barlayout=<rows>,<maxIconsPerRow>        (BarLayout; only when not 1,0)
//    panel.<title>=<open|closed>,<left>,<top>,<width>,<height>
//                  [,<docked|floating>,<screenLeft>,<screenTop>]
//    value.<key>=<text>                       (SetValue: a window's own choice
//                                              that belongs to the layout, e.g.
//                                              an extra chat window's tab)
//
//  The trailing three columns on each row were added when panels (and later
//  the bar itself) gained the ability to detach into their own top-level
//  layered windows. Files that pre-date the feature still parse — the floating
//  mode just defaults to docked and the screen coords default to 0. A file
//  without a barlayout= row gives the toolbar one row, no icon limit.
//
//  Stored under %LocalAppData%\RynthCore\panel_state.txt — survives reloads
//  and out-of-game (the Runtime dir might be wiped during deploy). That is the
//  PC-wide default; in the world a client uses its character's own copy,
//  layouts\<server>\<character>\panel_state.txt (PanelLayout switches the file
//  with SwitchTo). Saves are atomic: a temp file, then a replace.
//
//  ReadOnlyPath (2026-11-05): with Panel Layout Per Character on, PanelLayout
//  names the PC-wide default here and no save writes it (a change stays in
//  memory): a client on the default is at character select or doesn't know its
//  character yet, and such clients saving there overwrote each other.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RynthCore.Engine.Plugins;

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
    private static readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
    private static int _generation;
    private static (double Left, double Top)? _barPosition;
    private static bool _barFloating;
    private static (double Left, double Top)? _barFloatingPosition;
    private static int _barRows = BarLayout.DefaultRows, _barMaxPerRow = BarLayout.NoLimit;
    private static bool _loaded;
    private static string? _path;   // the file in use (SwitchTo); null = the PC-wide default

    /// <summary>The PC-wide default file: %LocalAppData%\RynthCore\panel_state.txt.</summary>
    internal static string DefaultPath
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "RynthCore", "panel_state.txt");
        }
    }

    private static string FilePath => _path ?? DefaultPath;

    private static volatile string? _readOnlyPath;
    private static bool _readOnlyLogged;   // under Sync: the skipped save is logged once per file switch

    /// <summary>
    /// A file no save writes (PanelLayout: the PC-wide default while Panel Layout Per Character is
    /// on); null = none. Changes made while it is in use stay in memory. Any thread.
    /// </summary>
    internal static string? ReadOnlyPath
    {
        get => _readOnlyPath;
        set => _readOnlyPath = value;
    }

    /// <summary>The file in use is <see cref="ReadOnlyPath"/> (under Sync).</summary>
    private static bool InUseIsReadOnly =>
        _readOnlyPath is { } p && string.Equals(Path.GetFullPath(p), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>The file in use now (the default, or the character's). Any thread.</summary>
    internal static string CurrentPath
    {
        get { lock (Sync) return FilePath; }
    }

    /// <summary>
    /// Panel layout per character (PanelLayout): from now on the store reads and writes
    /// <paramref name="path"/>. With <paramref name="flushCurrent"/> the file in use is written
    /// out first (a character's layout being left); the default is never written by a switch.
    /// Everything in memory is then replaced by <paramref name="path"/>'s contents (none: an empty
    /// layout). The UI writer's thread, in order with the other saves.
    /// </summary>
    internal static void SwitchTo(string path, bool flushCurrent)
    {
        lock (Sync)
        {
            if (flushCurrent && _loaded) Save();
            Panels.Clear();
            Values.Clear();
            System.Threading.Interlocked.Increment(ref _generation);
            _barPosition = null;
            _barFloating = false;
            _barFloatingPosition = null;
            _barRows = BarLayout.DefaultRows;
            _barMaxPerRow = BarLayout.NoLimit;
            _path = path;
            _loaded = false;
            _readOnlyLogged = false;
            Load();
        }
    }

    /// <summary>The file's text for what is in memory now (/rc layout default writes it as the default). Any thread.</summary>
    internal static string Snapshot()
    {
        lock (Sync)
        {
            Load();
            return Format();
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

    /// <summary>The toolbar's rows and icons-per-row limit (BarLayout; 1 and no limit unless set).</summary>
    public static (int Rows, int MaxPerRow) BarRows
    {
        get { lock (Sync) { Load(); return (_barRows, _barMaxPerRow); } }
    }

    public static void SetBarRows(int rows, int maxPerRow)
    {
        lock (Sync)
        {
            Load();
            rows = BarLayout.ClampRows(rows);
            maxPerRow = BarLayout.ClampMaxPerRow(maxPerRow);
            if (rows == _barRows && maxPerRow == _barMaxPerRow) return;
            _barRows = rows;
            _barMaxPerRow = maxPerRow;
            Save();
        }
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

    /// <summary>
    /// Goes up each time the store switches to another file (<see cref="SwitchTo"/>): a window that
    /// read a <see cref="TryGetValue"/> value reads it again when this changes. Any thread.
    /// </summary>
    public static int Generation => System.Threading.Volatile.Read(ref _generation);

    /// <summary>A value saved with <see cref="SetValue"/> in the layout in use. Any thread.</summary>
    public static bool TryGetValue(string key, out string value)
    {
        lock (Sync)
        {
            Load();
            if (Values.TryGetValue(key, out string? v)) { value = v; return true; }
            value = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Saves a one-line value in the layout in use (null or empty removes it): a window's own
    /// choice that belongs with its place, such as which tab an extra chat window shows. Only
    /// what the player does may call this. Line breaks are dropped. Any thread.
    /// </summary>
    public static void SetValue(string key, string? value)
    {
        key = key.Replace('\r', ' ').Replace('\n', ' ').Replace('=', ' ').Trim();
        if (key.Length == 0) return;
        value = value?.Replace('\r', ' ').Replace('\n', ' ').Trim();
        lock (Sync)
        {
            Load();
            if (string.IsNullOrEmpty(value))
            {
                if (!Values.Remove(key)) return;
            }
            else
            {
                if (Values.TryGetValue(key, out string? had) && string.Equals(had, value, StringComparison.Ordinal)) return;
                Values[key] = value;
            }
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

    /// <summary>Rewrite panel_state.txt from the in-memory snapshot (Settings > Interface "Save Layout
    /// Now"). An explicit user action, so unlike the automatic saves it is NOT gated by
    /// RememberWindowLayout: it writes even when that is off.</summary>
    public static void Flush()
    {
        lock (Sync)
        {
            Load();
            Save(force: true);
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
                else if (string.Equals(key, "barlayout", StringComparison.OrdinalIgnoreCase))
                {
                    // An unreadable row leaves the defaults (TryParse sets them).
                    BarLayout.TryParse(value, out _barRows, out _barMaxPerRow);
                }
                else if (key.StartsWith("value.", StringComparison.OrdinalIgnoreCase))
                {
                    string name = key["value.".Length..].Trim();
                    if (name.Length > 0 && value.Length > 0) Values[name] = value;
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

    private static void Save(bool force = false)
    {
        // Settings > Interface "Remember Open Windows and Positions" (default on). Off: keep the last
        // file; do not overwrite it while the user is rearranging.
        if (!force && !EngineSettings.RememberWindowLayout)
            return;
        // The PC-wide default with the setting on: not even Save Layout Now writes it (/rc layout
        // default does, PanelLayoutFiles.MakeDefault).
        if (InUseIsReadOnly)
        {
            if (!_readOnlyLogged)
            {
                _readOnlyLogged = true;
                try { RynthLog.UI($"PanelStateStore: not saving {FilePath} - with Panel Layout Per Character on, the PC-wide default is only read (at character select, or until this client knows its character); changes stay in memory."); } catch { }
            }
            return;
        }

        try
        {
            string path = FilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Atomic (temp file, then replace): a client that dies mid-save leaves the old file.
            EngineSettingsFile.WriteAtomic(path, new UTF8Encoding(false).GetBytes(Format()));
        }
        catch (Exception ex)
        {
            try { RynthLog.UI($"PanelStateStore: save failed - {ex.Message}"); } catch { }
        }
    }

    /// <summary>The file's text for what is in memory (under Sync).</summary>
    private static string Format()
    {
        var sw = new StringBuilder();
        sw.AppendLine("# RynthCore Avalonia panel state — auto-generated, hand-edits OK.");
        if (_barPosition is { } bar)
        {
            // Always emit the extended form if we have any floating state to
            // remember; legacy 2-column form only when no popout has happened.
            if (_barFloating || _barFloatingPosition is not null)
            {
                (double fl, double ft) = _barFloatingPosition ?? (0, 0);
                sw.AppendLine(FormattableString.Invariant(
                    $"bar={bar.Left:0.##},{bar.Top:0.##},{(_barFloating ? "floating" : "docked")},{fl:0.##},{ft:0.##}"));
            }
            else
            {
                sw.AppendLine(FormattableString.Invariant($"bar={bar.Left:0.##},{bar.Top:0.##}"));
            }
        }

        // Only when changed: a file that never had the row reads back the same.
        if (_barRows != BarLayout.DefaultRows || _barMaxPerRow != BarLayout.NoLimit)
            sw.AppendLine("barlayout=" + BarLayout.Format(_barRows, _barMaxPerRow));

        foreach (var kv in Panels)
        {
            PanelEntry e = kv.Value;
            sw.AppendLine(FormattableString.Invariant(
                $"panel.{kv.Key}={(e.Open ? "open" : "closed")},{e.Left:0.##},{e.Top:0.##},{e.Width:0.##},{e.Height:0.##},{(e.Floating ? "floating" : "docked")},{e.FloatingLeft:0.##},{e.FloatingTop:0.##}"));
        }
        // Only rows that were set: a file without values reads back the same.
        foreach (var kv in Values)
            sw.AppendLine("value." + kv.Key + "=" + kv.Value);
        return sw.ToString();
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

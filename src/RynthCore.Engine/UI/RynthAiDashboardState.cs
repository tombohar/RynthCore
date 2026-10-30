// ============================================================================
//  RynthCore.Engine - UI/RynthAiDashboardState.cs
//  The RynthAi dashboard's minimized state, shared by its Avalonia and ImGui
//  faces, and the ImGui face's target bar and player vitals toggles,
//  persisted to %AppData%\RynthCore\rynthai_avalonia.cache (line 1
//  minimized|expanded, line 2 unused, line 3 target|notarget, line 4
//  vitals|novitals; line 5 filesopen|nofiles, the Loaded files drawer; line 9
//  ranges|noranges, the Ranges drawer; line 10 patrol|nopatrol, the Patrol
//  drawer; line 11 remote|noremote, the Mini Remote drawer; line 12 the
//  drawers moved to the side they don't start on, "moved:key,key"; line 13
//  quick|noquick, the My settings drawer: the drawers on the dashboard's
//  edges, ImGui/Panels/DashboardDrawers.cs; line 14 classic|modern, the
//  dashboard's look, and line 15 the Classic look's open tab,
//  ImGui/Panels/Classic). Line 2
//  was the Monsters button's Simple/Advanced mode, retired 2026-10-01 with the
//  basic Monsters panel: it is still written ("advanced") so the lines after
//  it keep their places, and ignored on read. Lines 7 and 8 (the dashboard's
//  size with the files block folded / open) are unused since the files moved
//  to a drawer (2026-10-05); kept as read so the lines after them keep their
//  places.
//
//  Deliberately NOT on RynthAiPanel: the engine loads this before
//  AvaloniaOverlay.Start(), and touching RynthAiPanel there runs its static
//  constructor, whose brushes create Dispatcher.UIThread on the wrong thread
//  and kill the whole overlay (PlatformNotSupportedException in MainLoop; see
//  the invariant in EntryPoint next to ChatModel.EnsureSettingsLoaded).
//  Keep Avalonia types out of this class.
// ============================================================================

using System;
using System.IO;

namespace RynthCore.Engine.UI;

internal static class RynthAiDashboardState
{
    private static volatile bool _minimized;
    private static volatile bool _showTargetBar = true;
    private static volatile bool _showVitals = true;
    private static volatile bool _filesOpen;            // the Loaded files drawer (Profile/Nav/Loot/Meta pickers); a popped-out
                                                        // dashboard shows the same block inline instead
    private static volatile bool _barCollapsed;         // the RynthCore bar shows only its grip + expand
    private static volatile bool _rangesOpen;           // the Ranges drawer beside the dashboard is open
    private static volatile bool _patrolOpen;           // the Patrol drawer beside the dashboard is open
    private static volatile bool _remoteOpen;           // the Mini Remote drawer beside the dashboard is open
    private static volatile bool _quickOpen;            // the My settings drawer beside the dashboard is open
    private static volatile bool _classic;              // the Classic (retail) look instead of the dashboard
    private static volatile int _classicTab;            // the Classic look's open tab
    // Drawer keys on the side they don't start on (a tab's right-click moves it). Copy-on-write.
    private static volatile string[] _moved = Array.Empty<string>();
    // Lines 7 and 8, unused since 2026-10-05: written back as read.
    private static string _line7 = "-", _line8 = "-";
    private static bool _loaded;
    private static readonly object LoadSync = new();

    private static string CachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "rynthai_avalonia.cache");

    /// <summary>Minimized (loaded from disk on first use). Any thread.</summary>
    public static bool Minimized
    {
        get { EnsureLoaded(); return _minimized; }
    }

    /// <summary>Any thread; the file is written in the background.</summary>
    public static void SetMinimized(bool minimized)
    {
        EnsureLoaded();
        _minimized = minimized;
        UiBackgroundWriter.Enqueue("rynthai minimized", Save);
    }

    /// <summary>The ImGui dashboard shows the target's name and health bar (at its bottom). Any thread.</summary>
    public static bool ShowTargetBar
    {
        get { EnsureLoaded(); return _showTargetBar; }
    }

    public static void SetShowTargetBar(bool show)
    {
        EnsureLoaded();
        _showTargetBar = show;
        UiBackgroundWriter.Enqueue("rynthai dashboard state", Save);
    }

    /// <summary>The ImGui dashboard shows the player's health, stamina and mana bars (at its bottom). Any thread.</summary>
    public static bool ShowVitals
    {
        get { EnsureLoaded(); return _showVitals; }
    }

    public static void SetShowVitals(bool show)
    {
        EnsureLoaded();
        _showVitals = show;
        UiBackgroundWriter.Enqueue("rynthai dashboard state", Save);
    }

    /// <summary>
    /// The loaded-files block (Profile/Nav/Loot/Meta pickers, meta state) is open: the Loaded files drawer
    /// beside the dashboard, or the block inline on a popped-out dashboard. Any thread.
    /// </summary>
    public static bool FilesOpen
    {
        get { EnsureLoaded(); return _filesOpen; }
    }

    public static void SetFilesOpen(bool open)
    {
        EnsureLoaded();
        _filesOpen = open;
        UiBackgroundWriter.Enqueue("rynthai dashboard state", Save);
    }

    /// <summary>The ImGui bar is collapsed to its grip and an expand button. Any thread.</summary>
    public static bool BarCollapsed
    {
        get { EnsureLoaded(); return _barCollapsed; }
    }

    public static void SetBarCollapsed(bool collapsed)
    {
        EnsureLoaded();
        _barCollapsed = collapsed;
        UiBackgroundWriter.Enqueue("rynthai dashboard state", Save);
    }

    /// <summary>The Ranges drawer (ImGui/Panels/RangesSlideOut.cs) is open beside the dashboard. Any thread.</summary>
    public static bool RangesOpen
    {
        get { EnsureLoaded(); return _rangesOpen; }
    }

    public static void SetRangesOpen(bool open)
    {
        EnsureLoaded();
        _rangesOpen = open;
        UiBackgroundWriter.Enqueue("rynthai dashboard state", Save);
    }

    /// <summary>The Patrol drawer (routes and recorded hazards) is open beside the dashboard. Any thread.</summary>
    public static bool PatrolOpen
    {
        get { EnsureLoaded(); return _patrolOpen; }
    }

    public static void SetPatrolOpen(bool open)
    {
        EnsureLoaded();
        _patrolOpen = open;
        UiBackgroundWriter.Enqueue("rynthai dashboard state", Save);
    }

    /// <summary>The Mini Remote drawer (RynthAi's Mini Remote drawn by the plugin) is open beside the dashboard. Any thread.</summary>
    public static bool RemoteOpen
    {
        get { EnsureLoaded(); return _remoteOpen; }
    }

    public static void SetRemoteOpen(bool open)
    {
        EnsureLoaded();
        _remoteOpen = open;
        UiBackgroundWriter.Enqueue("rynthai dashboard state", Save);
    }

    /// <summary>The My settings drawer (pinned settings) is open beside the dashboard. Any thread.</summary>
    public static bool QuickOpen
    {
        get { EnsureLoaded(); return _quickOpen; }
    }

    public static void SetQuickOpen(bool open)
    {
        EnsureLoaded();
        _quickOpen = open;
        UiBackgroundWriter.Enqueue("rynthai dashboard state", Save);
    }

    /// <summary>The dashboard draws its Classic (retail) look: the tabbed window of ImGui/Panels/Classic. Any thread.</summary>
    public static bool Classic
    {
        get { EnsureLoaded(); return _classic; }
    }

    public static void SetClassic(bool classic)
    {
        EnsureLoaded();
        if (_classic == classic) return;
        _classic = classic;
        UiBackgroundWriter.Enqueue("rynthai dashboard state", Save);
    }

    /// <summary>The Classic look's open tab (0 = Main). Any thread.</summary>
    public static int ClassicTab
    {
        get { EnsureLoaded(); return _classicTab; }
    }

    public static void SetClassicTab(int tab)
    {
        EnsureLoaded();
        if (_classicTab == tab) return;
        _classicTab = tab;
        UiBackgroundWriter.Enqueue("rynthai dashboard state", Save);
    }

    /// <summary>The drawer <paramref name="key"/> sits on the side it doesn't start on. Any thread.</summary>
    public static bool DrawerMoved(string key)
    {
        EnsureLoaded();
        return Array.IndexOf(_moved, key) >= 0;
    }

    public static void SetDrawerMoved(string key, bool moved)
    {
        EnsureLoaded();
        string[] cur = _moved;
        if ((Array.IndexOf(cur, key) >= 0) == moved) return;
        var list = new System.Collections.Generic.List<string>(cur);
        if (moved) list.Add(key);
        else list.Remove(key);
        _moved = list.ToArray();
        UiBackgroundWriter.Enqueue("rynthai dashboard state", Save);
    }

    /// <summary>Reads the file if it hasn't been read yet. Engine init calls this so AC's thread never does.</summary>
    public static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (LoadSync)
        {
            if (_loaded) return;
            try
            {
                string path = CachePath;
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path);
                    _minimized = lines.Length > 0 && lines[0].Trim().Equals("minimized", StringComparison.OrdinalIgnoreCase);
                    _showTargetBar = lines.Length < 3 || !lines[2].Trim().Equals("notarget", StringComparison.OrdinalIgnoreCase);
                    _showVitals = lines.Length < 4 || !lines[3].Trim().Equals("novitals", StringComparison.OrdinalIgnoreCase);
                    // "filesopen" only once the player opened them; the old "files" default reads as folded.
                    _filesOpen = lines.Length >= 5 && lines[4].Trim().Equals("filesopen", StringComparison.OrdinalIgnoreCase);
                    _barCollapsed = lines.Length >= 6 && lines[5].Trim().Equals("barcollapsed", StringComparison.OrdinalIgnoreCase);
                    if (lines.Length >= 7 && lines[6].Trim().Length > 0) _line7 = lines[6].Trim();
                    if (lines.Length >= 8 && lines[7].Trim().Length > 0) _line8 = lines[7].Trim();
                    _rangesOpen = lines.Length >= 9 && lines[8].Trim().Equals("ranges", StringComparison.OrdinalIgnoreCase);
                    _patrolOpen = lines.Length >= 10 && lines[9].Trim().Equals("patrol", StringComparison.OrdinalIgnoreCase);
                    _remoteOpen = lines.Length >= 11 && lines[10].Trim().Equals("remote", StringComparison.OrdinalIgnoreCase);
                    if (lines.Length >= 12 && lines[11].Trim().StartsWith("moved:", StringComparison.OrdinalIgnoreCase))
                        _moved = lines[11].Trim().Substring(6).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    _quickOpen = lines.Length >= 13 && lines[12].Trim().Equals("quick", StringComparison.OrdinalIgnoreCase);
                    _classic = lines.Length >= 14 && lines[13].Trim().Equals("classic", StringComparison.OrdinalIgnoreCase);
                    if (lines.Length >= 15 && int.TryParse(lines[14].Trim(), out int tab)) _classicTab = Math.Max(0, tab);
                }
            }
            catch { /* corrupt cache: start expanded */ }
            _loaded = true;
        }
    }

    private static void Save()
    {
        try
        {
            string path = CachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, (_minimized ? "minimized" : "expanded") + "\nadvanced\n"
                + (_showTargetBar ? "target" : "notarget") + "\n" + (_showVitals ? "vitals" : "novitals") + "\n"
                + (_filesOpen ? "filesopen" : "nofiles") + "\n" + (_barCollapsed ? "barcollapsed" : "bar") + "\n"
                + _line7 + "\n" + _line8 + "\n"
                + (_rangesOpen ? "ranges" : "noranges") + "\n" + (_patrolOpen ? "patrol" : "nopatrol") + "\n"
                + (_remoteOpen ? "remote" : "noremote") + "\n"
                + "moved:" + string.Join(",", _moved) + "\n" + (_quickOpen ? "quick" : "noquick") + "\n"
                + (_classic ? "classic" : "modern") + "\n" + _classicTab + "\n");
        }
        catch { /* best-effort */ }
    }
}

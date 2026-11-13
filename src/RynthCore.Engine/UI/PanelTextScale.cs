// ============================================================================
//  RynthCore.Engine - UI/PanelTextScale.cs
//  Per-panel text size for the in-client (ImGui) panels. Sizes 0-10; 0 is the
//  normal size, each step is 10% bigger, and the last one ("yourjokin") is
//  2.5x. Stored for the whole PC in %LOCALAPPDATA%\RynthCore\panel_text_size.txt
//  as "Title=size" lines; set from Settings > Interface or from a window's own
//  title bar (the text size button, Ctrl+wheel; ImGuiPanelHost). One size
//  ("All Panels") applies to every panel unless "Size each panel separately"
//  is on. The toolbar (the icon bar, ImGuiBar) has its own size, the "chrome"
//  row (Settings > Interface > Toolbar, the bar's right-click menu, Ctrl+wheel on
//  the bar, /rc ui barsize); it is independent of All Panels and per-panel sizes.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RynthCore.Engine.UI;

internal static class PanelTextScale
{
    /// <summary>Picker labels, index = size.</summary>
    public static readonly string[] SizeNames = { "100%", "110%", "120%", "130%", "140%", "150%", "160%", "170%", "180%", "190%", "250%" };

    private static readonly float[] Scales = { 1.0f, 1.1f, 1.2f, 1.3f, 1.4f, 1.5f, 1.6f, 1.7f, 1.8f, 1.9f, 2.5f };

    private static readonly object Sync = new();
    private static readonly Dictionary<string, int> Sizes = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    // RYNTHCORE_PANEL_TEXT_SIZE points a test at its own file instead of the real one.
    private static readonly string FilePath =
        Environment.GetEnvironmentVariable("RYNTHCORE_PANEL_TEXT_SIZE") is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "panel_text_size.txt");

    private const string AllKey = "__all";
    private const string PerPanelKey = "__perPanel";
    private const string ChromeKey = "__chrome";

    /// <summary>The size (0-10) a panel is drawn at: its own when sizing per panel, else All Panels.</summary>
    public static int Get(string title)
    {
        lock (Sync)
        {
            Load();
            int all = Sizes.TryGetValue(AllKey, out int a) ? a : 0;
            if (!(Sizes.TryGetValue(PerPanelKey, out int pp) && pp != 0)) return all;
            return Sizes.TryGetValue(title, out int s) ? s : all;
        }
    }

    /// <summary>The panel's own size setting (per-panel rows), falling back to All Panels.</summary>
    public static int GetOwn(string title)
    {
        lock (Sync)
        {
            Load();
            if (Sizes.TryGetValue(title, out int s)) return s;
            return Sizes.TryGetValue(AllKey, out int a) ? a : 0;
        }
    }

    public static int GetAll() => GetOwn(AllKey);
    public static void SetAll(int size) => Set(AllKey, size);

    /// <summary>The toolbar's size (0-10): its own row, 0 unless set; never All Panels.</summary>
    public static int GetChrome()
    {
        lock (Sync)
        {
            Load();
            return Sizes.TryGetValue(ChromeKey, out int c) ? c : 0;
        }
    }

    public static void SetChrome(int size) => Set(ChromeKey, size);

    /// <summary>Off: every panel uses All Panels. On: each panel has its own size.</summary>
    public static bool PerPanel
    {
        get { lock (Sync) { Load(); return Sizes.TryGetValue(PerPanelKey, out int v) && v != 0; } }
        set => Set(PerPanelKey, value ? 1 : 0);
    }

    public static void Set(string title, int size)
    {
        size = Math.Clamp(size, 0, SizeNames.Length - 1);
        lock (Sync)
        {
            Load();
            if (Sizes.TryGetValue(title, out int cur) && cur == size) return;
            Sizes[title] = size;
            Save();
            RynthCore.Engine.ImGuiBackend.ImGuiFonts.RequestRebuild(RebuildDelayMs);
        }
    }

    // Changes a few clicks or wheel notches apart re-bake the fonts once, after the
    // last one (ImGuiFonts.RequestRebuild). Until then a panel draws at the nearest
    // size already baked (ImGuiFonts.BakedStepNear).
    private const int RebuildDelayMs = 250;

    /// <summary>
    /// Sets one window's size from its own title bar (ImGuiPanelHost). If every panel
    /// shares All Panels, this switches on "Size each panel separately" with every
    /// other panel kept at the All Panels size, so only this window changes.
    /// </summary>
    public static void SetForWindow(string title, int size)
    {
        size = Math.Clamp(size, 0, SizeNames.Length - 1);
        lock (Sync)
        {
            Load();
            bool perPanel = Sizes.TryGetValue(PerPanelKey, out int pp) && pp != 0;
            if (perPanel)
            {
                if (Sizes.TryGetValue(title, out int cur) && cur == size) return;
            }
            else
            {
                int all = Sizes.TryGetValue(AllKey, out int a) ? a : 0;
                if (size == all) return;
                // Rows left over from an earlier per-panel session would come back into
                // effect: start them from what every panel shows now.
                var keys = new List<string>(Sizes.Keys);
                foreach (string k in keys)
                    if (k != AllKey && k != PerPanelKey && k != ChromeKey) Sizes[k] = all;
                Sizes[PerPanelKey] = 1;
            }
            Sizes[title] = size;
            Save();
            RynthCore.Engine.ImGuiBackend.ImGuiFonts.RequestRebuild(RebuildDelayMs);
        }
    }

    /// <summary>One window back to the size every other panel uses (All Panels).</summary>
    public static void ResetWindow(string title) => SetForWindow(title, GetAll());

    /// <summary>Every panel at <paramref name="size"/>: All Panels, per-panel sizing off.</summary>
    public static void UseForAll(int size)
    {
        size = Math.Clamp(size, 0, SizeNames.Length - 1);
        lock (Sync)
        {
            Load();
            Sizes[AllKey] = size;
            Sizes[PerPanelKey] = 0;
            Save();
            RynthCore.Engine.ImGuiBackend.ImGuiFonts.RequestRebuild(RebuildDelayMs);
        }
    }

    /// <summary>"Size 3 (130%)": a size for tooltips.</summary>
    public static string Describe(int step)
    {
        step = Math.Clamp(step, 0, SizeNames.Length - 1);
        int pct = (int)MathF.Round(Factor(step) * 100f);
        return step == 0 ? "normal (100%)" : pct.ToString(CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>Font size factor for a size step.</summary>
    public static float Factor(int step) => Scales[Math.Clamp(step, 0, Scales.Length - 1)];

    /// <summary>The distinct non-zero sizes any panel or the toolbar uses (fonts are baked for these).</summary>
    public static List<int> StepsInUse()
    {
        lock (Sync)
        {
            Load();
            var steps = new List<int>();
            bool perPanel = Sizes.TryGetValue(PerPanelKey, out int pp) && pp != 0;
            foreach (var kv in Sizes)
            {
                if (kv.Key == PerPanelKey) continue;
                if (!perPanel && kv.Key != AllKey && kv.Key != ChromeKey) continue;   // All Panels and the toolbar are in use
                if (kv.Value > 0 && !steps.Contains(kv.Value)) steps.Add(kv.Value);
            }
            return steps;
        }
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (int.TryParse(line.AsSpan(eq + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s))
                    Sizes[line[..eq].Trim()] = Math.Clamp(s, 0, SizeNames.Length - 1);
            }
        }
        catch { }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var lines = new List<string>();
            foreach (var kv in Sizes) lines.Add($"{kv.Key}={kv.Value.ToString(CultureInfo.InvariantCulture)}");
            File.WriteAllLines(FilePath, lines);
        }
        catch { }
    }
}

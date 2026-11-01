// ============================================================================
//  RynthCore.Engine - UI/PanelDrawScale.cs
//  How big a frameless plugin window draws itself (plugin API v90 PanelScale),
//  30% to 250% of normal, or "follow the text size" (the default). Text size
//  (PanelTextScale) can't go under 100%: the fonts are baked at 100% and up.
//  A window that draws its own content (RynthDefective's shortcut bar and panel
//  buttons) scales its layout by this and draws its text smaller from the baked
//  font (Tom 2026-10-22: "the shortcut size needs to go a lot smaller").
//  Stored for the whole PC in %LOCALAPPDATA%\RynthCore\panel_draw_scale.txt as
//  "Title=percent" lines; every client looks for a change at most every 2 s.
//
//  Sharp when bigger (2026-11-01, Tom: enlarged windows "get blurry"): a window
//  drawn over 100% took its text from the normal bake, stretched. Now its fonts
//  come from the text size bake just above its size (FontStep; PluginWindowFace
//  switches ImGuiFonts.CurrentStep while the plugin draws), shrunk a little at
//  most, and ImGuiFonts bakes the sizes the saved ones need with the text sizes.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RynthCore.Engine.UI;

internal static class PanelDrawScale
{
    public const int Min = 30, Max = 250, Step = 10;

    /// <summary>Settings picker labels: index 0 follows the text size, then 30% ... 250%.</summary>
    public static readonly string[] Names = BuildNames();

    private static string[] BuildNames()
    {
        var list = new List<string> { "Text size" };
        for (int p = Min; p <= Max; p += Step) list.Add(p.ToString(CultureInfo.InvariantCulture) + "%");
        return list.ToArray();
    }

    public static int ToIndex(int percent) => percent <= 0 ? 0 : (Math.Clamp(percent, Min, Max) - Min) / Step + 1;
    public static int FromIndex(int index) => index <= 0 ? 0 : Min + (index - 1) * Step;

    private static readonly object Sync = new();
    private static readonly Dictionary<string, int> Scales = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _fileTime = DateTime.MinValue;
    private static long _checkedMs = long.MinValue / 2;
    // The font steps last handed to ImGuiFonts' bake ("3,6"; null before the first bake).
    private static string? _bakedSteps;
    private const int RebuildDelayMs = 250;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "panel_draw_scale.txt");

    /// <summary>The window's size in percent, 0 = it follows its text size. Any thread.</summary>
    public static int Get(string title)
    {
        lock (Sync)
        {
            Refresh();
            return Scales.TryGetValue(title, out int p) ? p : 0;
        }
    }

    /// <summary>Sets it (30-250, rounded to 10%), or 0 to follow the text size again. Any thread.</summary>
    public static void Set(string title, int percent)
    {
        percent = percent <= 0 ? 0 : (int)Math.Round(Math.Clamp(percent, Min, Max) / (double)Step) * Step;
        lock (Sync)
        {
            Refresh();
            if ((Scales.TryGetValue(title, out int cur) ? cur : 0) == percent) return;
            if (percent == 0) Scales.Remove(title); else Scales[title] = percent;
            NoteSteps();
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var lines = new List<string> { "# RynthCore: how big each self-drawn window is (percent; Settings > UI > Toolbar)." };
                foreach (var kv in Scales) lines.Add($"{kv.Key}={kv.Value.ToString(CultureInfo.InvariantCulture)}");
                File.WriteAllLines(path, lines);
                _fileTime = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex)
            {
                RynthLog.UI($"PanelDrawScale: could not save {FilePath}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // Caller holds Sync. Reads the file again when it changed (another client), looked at every 2 s.
    private static void Refresh()
    {
        long now = Environment.TickCount64;
        if (now - _checkedMs < 2000) return;
        _checkedMs = now;
        try
        {
            string path = FilePath;
            DateTime t = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (t == _fileTime) return;
            _fileTime = t;
            Scales.Clear();
            if (t != DateTime.MinValue)
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    int eq = line.LastIndexOf('=');
                    if (eq <= 0 || !int.TryParse(line.AsSpan(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p)) continue;
                    if (p >= Min && p <= Max) Scales[line[..eq].Trim()] = p;
                }
            NoteSteps();
        }
        catch { /* the old values stay */ }
    }

    /// <summary>
    /// The text size step a window drawn at <paramref name="percent"/> takes its fonts from: the smallest
    /// bake at least that big (shrinking a bigger bake stays sharp, stretching a smaller one blurs);
    /// 0 at or under normal. A percent is of 96-DPI pixels; the fonts are baked at <paramref name="fontScale"/>.
    /// </summary>
    public static int FontStep(int percent, float fontScale)
    {
        if (percent <= 0) return 0;
        float want = percent / 100f / Math.Max(0.5f, fontScale);
        int last = PanelTextScale.SizeNames.Length - 1;
        for (int step = 0; step < last; step++)
            if (PanelTextScale.Factor(step) >= want - 0.005f) return step;
        return last;
    }

    /// <summary>The non-zero font steps the saved sizes need; ImGuiFonts bakes them with the text sizes. Any thread.</summary>
    public static List<int> FontStepsInUse(float fontScale)
    {
        lock (Sync)
        {
            Refresh();
            List<int> steps = Steps(fontScale);
            _bakedSteps = string.Join(",", steps);
            return steps;
        }
    }

    // Caller holds Sync.
    private static List<int> Steps(float fontScale)
    {
        var steps = new List<int>();
        foreach (int p in Scales.Values)
        {
            int step = FontStep(p, fontScale);
            if (step > 0 && !steps.Contains(step)) steps.Add(step);
        }
        steps.Sort();
        return steps;
    }

    // Caller holds Sync. A size whose font step isn't in the bake: re-bake once the changes stop.
    private static void NoteSteps()
    {
        if (_bakedSteps == null) return;
        string now = string.Join(",", Steps(RynthCore.Engine.ImGuiBackend.EngineFrameController.FontScale));
        if (now != _bakedSteps) RynthCore.Engine.ImGuiBackend.ImGuiFonts.RequestRebuild(RebuildDelayMs);
    }
}

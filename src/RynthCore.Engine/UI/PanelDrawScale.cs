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
            if (t == DateTime.MinValue) return;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int eq = line.LastIndexOf('=');
                if (eq <= 0 || !int.TryParse(line.AsSpan(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p)) continue;
                if (p >= Min && p <= Max) Scales[line[..eq].Trim()] = p;
            }
        }
        catch { /* the old values stay */ }
    }
}

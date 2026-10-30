// ============================================================================
//  RynthCore.Engine - UI/PanelColumnStore.cs
//  Column widths the user dragged in the in-game panels (Damage, Monsters,
//  Meta). Stored for the whole PC in %LOCALAPPDATA%\RynthCore\panel_columns.txt
//  as "Panel.key=value" lines. Read once; written when a drag ends.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RynthCore.Engine.UI;

internal static class PanelColumnStore
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, float> Values = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "panel_columns.txt");

    public static float Get(string key, float fallback)
    {
        lock (Sync)
        {
            Load();
            return Values.TryGetValue(key, out float v) ? v : fallback;
        }
    }

    public static void Set(string key, float value)
    {
        lock (Sync)
        {
            Load();
            Values[key] = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var lines = new List<string>();
                foreach (var kv in Values) lines.Add($"{kv.Key}={kv.Value.ToString("0.###", CultureInfo.InvariantCulture)}");
                File.WriteAllLines(FilePath, lines);
            }
            catch { }
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
                if (float.TryParse(line.AsSpan(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    Values[line[..eq].Trim()] = v;
            }
        }
        catch { }
    }
}

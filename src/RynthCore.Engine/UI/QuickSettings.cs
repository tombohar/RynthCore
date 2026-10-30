// ============================================================================
//  RynthCore.Engine - UI/QuickSettings.cs
//  The settings a player pinned to the dashboard's My settings drawer
//  (ImGui/Panels/QuickSettingsDrawer.cs), in the order they pinned them.
//
//  A pin is a row of the Settings schema (UI/Data/SettingsData.cs), named by
//  "Tab/Section/Label" (the section it sits under; "" before the first one),
//  since labels repeat across tabs ("Level 1" in Buffing and Spell Combat).
//  Only rows that edit or do something can be pinned (Pinnable); a pin whose
//  row no longer exists (renamed in an update) is kept in the file but not
//  drawn, so it comes back if the row does.
//
//  Persisted to %AppData%\RynthCore\rynthai_quick_settings.txt, one key per
//  line, written in the background (UiBackgroundWriter). The pins are the
//  player's, not the profile's: the same drawer on every character; the values
//  are each profile's own. Any thread for reads; changes on AC's thread.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.UI;

internal static class QuickSettings
{
    private static readonly object Sync = new();
    private static List<string> _pins = new();
    private static bool _loaded;

    private static Dictionary<string, SettingRow>? _byKey;
    private static Dictionary<SettingRow, string>? _keyOf;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "rynthai_quick_settings.txt");

    /// <summary>Rows that edit a value or do something; headings, notes and the hotkeys editor can't be pinned.</summary>
    public static bool Pinnable(SettingRow row) => row.Kind is SettingKind.Bool or SettingKind.Int or SettingKind.Float
        or SettingKind.Double or SettingKind.Combo or SettingKind.Text or SettingKind.Button or SettingKind.Color;

    /// <summary>The row's pin key ("Tab/Section/Label"), or null for a row that can't be pinned.</summary>
    public static string? KeyOf(SettingRow row)
    {
        EnsureIndex();
        return _keyOf!.TryGetValue(row, out string? key) ? key : null;
    }

    public static SettingRow? RowOf(string key)
    {
        EnsureIndex();
        return _byKey!.TryGetValue(key, out SettingRow? row) ? row : null;
    }

    /// <summary>The pinned keys, in order (a copy).</summary>
    public static string[] Pins
    {
        get { EnsureLoaded(); lock (Sync) return _pins.ToArray(); }
    }

    public static bool IsPinned(string key)
    {
        EnsureLoaded();
        lock (Sync) return _pins.Contains(key);
    }

    /// <summary>Pins <paramref name="key"/> at the end, or unpins it.</summary>
    public static void SetPinned(string key, bool pinned)
    {
        EnsureLoaded();
        lock (Sync)
        {
            if (pinned == _pins.Contains(key)) return;
            if (pinned) _pins.Add(key);
            else _pins.Remove(key);
        }
        UiBackgroundWriter.Enqueue("quick settings", Save);
    }

    /// <summary>Moves a pin up (-1) or down (+1) among the pins.</summary>
    public static void Move(string key, int by)
    {
        EnsureLoaded();
        lock (Sync)
        {
            int i = _pins.IndexOf(key);
            int j = i + by;
            if (i < 0 || j < 0 || j >= _pins.Count) return;
            (_pins[i], _pins[j]) = (_pins[j], _pins[i]);
        }
        UiBackgroundWriter.Enqueue("quick settings", Save);
    }

    /// <summary>The rows under section <paramref name="section"/> of tab <paramref name="tab"/> (up to the next section).</summary>
    public static SettingRow[] SectionRows(string tab, string section)
    {
        var list = new List<SettingRow>();
        foreach (SettingsTab t in SettingsSchema.Tabs)
        {
            if (!t.Name.Equals(tab, StringComparison.OrdinalIgnoreCase)) continue;
            bool inside = false;
            foreach (SettingRow row in t.Rows)
            {
                if (row.Kind == SettingKind.Section)
                {
                    if (inside) break;
                    inside = row.Label.Equals(section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (inside && row.Kind != SettingKind.Spacer) list.Add(row);
            }
        }
        return list.ToArray();
    }

    private static void EnsureIndex()
    {
        if (_byKey != null) return;
        var byKey = new Dictionary<string, SettingRow>(StringComparer.OrdinalIgnoreCase);
        var keyOf = new Dictionary<SettingRow, string>(ReferenceEqualityComparer.Instance);
        foreach (SettingsTab tab in SettingsSchema.Tabs)
        {
            string section = "";
            foreach (SettingRow row in tab.Rows)
            {
                if (row.Kind == SettingKind.Section) { section = row.Label.Trim(); continue; }
                if (!Pinnable(row)) continue;
                string key = tab.Name + "/" + section + "/" + row.Label.Trim();
                if (byKey.TryAdd(key, row)) keyOf[row] = key;
            }
        }
        _keyOf = keyOf;
        _byKey = byKey;
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Sync)
        {
            if (_loaded) return;
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        string key = line.Trim();
                        if (key.Length > 0 && !_pins.Contains(key)) _pins.Add(key);
                    }
            }
            catch { /* unreadable: start with no pins */ }
            _loaded = true;
        }
    }

    private static void Save()
    {
        try
        {
            string[] lines;
            lock (Sync) lines = _pins.ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath, lines);
        }
        catch { /* best-effort */ }
    }
}

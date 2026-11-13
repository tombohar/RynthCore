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
//  drawn, so it comes back if the row does. A pin saved before a row moved to
//  another tab or section (the 2026-11-11 regroup) is found again by its label
//  (MigrateKey) and rewritten under the row's new key when the file loads.
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

    // Where each tab's rows went in the 2026-11-11 regroup (UI/Data/SettingsData.cs). A pin of the old layout
    // ("Misc//Video patch") is the row with the same label on one of these tabs (Game Client > Performance).
    private static readonly Dictionary<string, string[]> MovedTo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Display"] = new[] { "Interface", "Game Client", "Hotkeys", "Inventory" },
        ["UI"] = new[] { "Interface" },
        ["Misc"] = new[] { "Game Client", "General", "Combat", "Inventory" },
        ["Recharge"] = new[] { "Recharge", "General", "Buffing" },
        ["Melee Combat"] = new[] { "Combat" },
        ["Spell Combat"] = new[] { "Spell Combat", "General", "Buffing", "Combat" },
        ["Ranges"] = new[] { "Ranges", "Combat" },
        ["Navigation"] = new[] { "Navigation", "General" },
        ["Crafting"] = new[] { "Combat" },
        ["Looting"] = new[] { "Looting", "Inventory" },
        ["Diagnostics"] = new[] { "Diagnostics", "Game Client" },
    };

    // A row taken out whose pin goes to the row left doing the same thing.
    private static readonly Dictionary<string, string> Replaced = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Misc//Auto Cram"] = "Inventory/Packs/Enable Autocram",   // Misc's copy of Looting's Enable Autocram
    };

    /// <summary>
    /// The key <paramref name="key"/> stands for now: itself while its row exists, else the key of the row it
    /// moved to (same label, on a tab the old one's rows went to; the same section first), else null.
    /// </summary>
    public static string? MigrateKey(string key)
    {
        EnsureIndex();
        if (_byKey!.TryGetValue(key, out SettingRow? row)) return _keyOf![row];
        if (Replaced.TryGetValue(key, out string? replaced)) return RowOf(replaced) != null ? replaced : null;
        // Tab names have no '/', but sections and labels can ("War/Void Casting Settings", "Day/Night timer"),
        // so the key is matched against each row's label rather than split.
        int first = key.IndexOf('/');
        if (first <= 0) return null;
        string tab = key[..first], rest = key[(first + 1)..];   // "Section/Label"
        if (!MovedTo.TryGetValue(tab, out string[]? tabs)) tabs = new[] { tab };
        string? best = null;
        int bestLength = -1;
        bool bestSameSection = false;
        foreach (KeyValuePair<SettingRow, string> pair in _keyOf!)
        {
            string label = pair.Key.Label.Trim(), k = pair.Value;
            if (label.Length == 0 || rest.Length <= label.Length || rest[^(label.Length + 1)] != '/' || !rest.EndsWith(label, StringComparison.Ordinal)) continue;
            string newTab = k[..k.IndexOf('/')];
            if (Array.FindIndex(tabs, t => t.Equals(newTab, StringComparison.OrdinalIgnoreCase)) < 0) continue;
            string section = rest[..(rest.Length - label.Length - 1)];
            bool sameSection = k[(newTab.Length + 1)..(k.Length - label.Length - 1)].Equals(section, StringComparison.OrdinalIgnoreCase);
            // The longest label that fits wins ("Avoid Lava/Acid Floors" over a row called "Acid Floors"), then the same section.
            if (label.Length > bestLength || (label.Length == bestLength && sameSection && !bestSameSection))
            {
                best = k;
                bestLength = label.Length;
                bestSameSection = sameSection;
            }
        }
        return best;
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
        bool migrated = false;
        lock (Sync)
        {
            if (_loaded) return;
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        string key = line.Trim();
                        if (key.Length == 0) continue;
                        // A pin of a row that moved is rewritten under its new key; one that leads nowhere is kept as it is.
                        string now = MigrateKey(key) ?? key;
                        migrated |= now != key;
                        if (!_pins.Contains(now)) _pins.Add(now);
                    }
            }
            catch { /* unreadable: start with no pins */ }
            _loaded = true;
        }
        if (migrated) UiBackgroundWriter.Enqueue("quick settings", Save);
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

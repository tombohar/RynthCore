// ============================================================================
//  RynthCore.Engine - ImGui/ThemeSchemes.cs
//  The colour schemes a player saved (Tom 2026-10-22: "a profile people can
//  save their color schemes"): named sets of the theme's eight colours, one
//  file each in %AppData%\RynthCore\themes\ ("name=", then the eight colours
//  as theme.txt writes them), for the PC, so every client lists the same ones.
//  RynthTheme puts them in Settings > Display's Theme picker between the
//  presets and Custom; "Save colours as" adds one, Delete removes the one in
//  use.
//
//  The list is read from the folder at most every 3 s (a scheme saved in one
//  client shows in the others' pickers without a restart) and at once after
//  this client saves or deletes one. Any thread; the files are small.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RynthCore.Engine.ImGuiBackend;

internal static class ThemeSchemes
{
    private const int MaxNameLength = 40;
    private const long RescanMs = 3000;

    private static readonly object Sync = new();
    private static string[] _names = Array.Empty<string>();
    private static Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);   // display name -> file
    private static long _scannedAt = long.MinValue / 2;

    private static string Folder => Path.Combine(RynthTheme.Folder, "themes");

    /// <summary>The saved schemes' names, A-Z (read again from the folder at most every 3 s).</summary>
    public static string[] Names
    {
        get
        {
            long now = Environment.TickCount64;
            lock (Sync)
            {
                if (now - _scannedAt < RescanMs) return _names;
                _scannedAt = now;
            }
            Rescan();
            lock (Sync) return _names;
        }
    }

    private static void Rescan()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(Folder))
                foreach (string file in Directory.GetFiles(Folder, "*.theme"))
                {
                    string name = ReadName(file);
                    if (name.Length > 0 && !files.ContainsKey(name)) files[name] = file;
                }
        }
        catch { /* unreadable folder: no schemes */ }
        string[] names = files.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        lock (Sync)
        {
            _files = files;
            _names = names;
        }
    }

    private static void Invalidate()
    {
        lock (Sync) _scannedAt = long.MinValue / 2;
    }

    /// <summary>The name as saved (its own capitals), or <paramref name="name"/> when there's none by it.</summary>
    public static string DisplayName(string name)
    {
        _ = Names;
        lock (Sync)
            return _names.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
    }

    /// <summary>
    /// A name a scheme can have: trimmed, at most 40 characters, not a preset's or "Custom"
    /// (those get "My " in front, so the picker never shows two alike). "" when nothing is left.
    /// </summary>
    public static string CleanName(string name)
    {
        name = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength].TrimEnd();
        if (name.Length == 0) return string.Empty;
        if (name.Equals("Custom", StringComparison.OrdinalIgnoreCase)
            || ThemePalette.PresetNames.Any(p => p.Equals(name, StringComparison.OrdinalIgnoreCase)))
            name = "My " + name;
        return name;
    }

    /// <summary>The scheme's eight colours (and its shades), or null when there's none by that name.</summary>
    public static ThemePalette? Load(string name)
    {
        string? file;
        _ = Names;
        lock (Sync) _files.TryGetValue(name, out file);
        if (file == null) return null;
        try
        {
            var p = ThemePalette.Default();
            int found = 0;
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
                for (int i = 0; i < ThemePalette.Primaries; i++)
                    if (key.Equals(ThemePalette.Key(i), StringComparison.OrdinalIgnoreCase)
                        && uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out uint c))
                    {
                        p.P[i] = 0xFF000000 | c;
                        found++;
                    }
            }
            if (found == 0) return null;
            p.Derive();
            return p;
        }
        catch { return null; }
    }

    /// <summary>Saves (or replaces) scheme <paramref name="name"/> (already cleaned). False when the file can't be written.</summary>
    public static bool Save(string name, ThemePalette palette)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string? existing;
            lock (Sync) _files.TryGetValue(name, out existing);
            string file = existing ?? Path.Combine(Folder, FileStem(name) + ".theme");
            var sb = new StringBuilder();
            sb.Append("name=").Append(name).Append('\n');
            for (int i = 0; i < ThemePalette.Primaries; i++)
                sb.Append(ThemePalette.Key(i)).Append('=').Append((palette.P[i] & 0xFFFFFF).ToString("X6")).Append('\n');
            File.WriteAllText(file, sb.ToString());
            Invalidate();
            RynthLog.UI($"ThemeSchemes: saved \"{name}\" ({file}).");
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.UI($"ThemeSchemes: could not save \"{name}\": {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static bool Delete(string name)
    {
        string? file;
        _ = Names;
        lock (Sync) _files.TryGetValue(name, out file);
        if (file == null) return false;
        try
        {
            File.Delete(file);
            Invalidate();
            RynthLog.UI($"ThemeSchemes: deleted \"{name}\".");
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Keeps <paramref name="palette"/> as a scheme unless it's already kept: the same eight colours as
    /// a preset or a saved scheme. Saved as <paramref name="baseName"/>, or "baseName 2", 3... when taken.
    /// </summary>
    public static void KeepIfNew(string baseName, ThemePalette palette)
    {
        for (int i = 0; i < ThemePalette.PresetNames.Length; i++)
            if (SameColours(ThemePalette.Preset(i), palette)) return;
        foreach (string n in Names)
        {
            ThemePalette? s = Load(n);
            if (s != null && SameColours(s, palette)) return;
        }
        string name = baseName;
        for (int k = 2; Names.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) && k < 100; k++)
            name = baseName + " " + k;
        Save(name, palette);
    }

    private static bool SameColours(ThemePalette a, ThemePalette b)
    {
        for (int i = 0; i < ThemePalette.Primaries; i++)
            if ((a.P[i] & 0xFFFFFF) != (b.P[i] & 0xFFFFFF)) return false;
        return true;
    }

    private static string ReadName(string file)
    {
        try
        {
            foreach (string raw in File.ReadLines(file))
            {
                string line = raw.Trim();
                if (line.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
                    return CleanStored(line[5..]);
            }
        }
        catch { return string.Empty; }
        return CleanStored(Path.GetFileNameWithoutExtension(file));
    }

    private static string CleanStored(string name)
    {
        name = name.Trim();
        return name.Length > MaxNameLength ? name[..MaxNameLength].TrimEnd() : name;
    }

    /// <summary>A file name for the scheme: its letters and digits, the rest as '_'.</summary>
    private static string FileStem(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is ' ' or '-' ? c : '_');
        string stem = sb.ToString().Trim();
        return stem.Length == 0 ? "scheme" : stem;
    }
}

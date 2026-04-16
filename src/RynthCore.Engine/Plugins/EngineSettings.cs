using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RynthCore.Engine.Plugins;

internal static class EngineSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RynthCore",
        "engine.json");

    private static List<string> _pluginPaths = new();
    private static bool _loaded;

    public static IReadOnlyList<string> PluginPaths
    {
        get
        {
            EnsureLoaded();
            return _pluginPaths;
        }
    }

    public static void AddPluginPath(string path)
    {
        EnsureLoaded();
        string full = Path.GetFullPath(path);
        for (int i = 0; i < _pluginPaths.Count; i++)
        {
            if (string.Equals(_pluginPaths[i], full, StringComparison.OrdinalIgnoreCase))
                return;
        }
        _pluginPaths.Add(full);
        Save();
    }

    public static void RemovePluginPath(int index)
    {
        EnsureLoaded();
        if (index >= 0 && index < _pluginPaths.Count)
        {
            _pluginPaths.RemoveAt(index);
            Save();
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (!File.Exists(SettingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(SettingsPath));
            if (doc.RootElement.TryGetProperty("PluginPaths", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    string? val = el.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        _pluginPaths.Add(val);
                }
            }
        }
        catch (Exception ex)
        {
            RynthLog.Plugin($"EngineSettings: Failed to load {SettingsPath}: {ex.Message}");
        }
    }

    private static void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (dir != null) Directory.CreateDirectory(dir);

            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteStartArray("PluginPaths");
                foreach (string p in _pluginPaths)
                    w.WriteStringValue(p);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            File.WriteAllBytes(SettingsPath, ms.ToArray());
        }
        catch (Exception ex)
        {
            RynthLog.Plugin($"EngineSettings: Failed to save: {ex.Message}");
        }
    }
}

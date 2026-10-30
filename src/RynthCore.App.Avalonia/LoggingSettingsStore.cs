using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using RynthCore.Install;

namespace RynthCore.App.Avalonia;

/// <summary>
/// Launcher-side access to every live logging switch:
/// <list type="bullet">
///   <item>Engine: engine.json "LoggingLevel" (global threshold) and "LogCategories" (per-category emit
///         level). The engine polls engine.json once a second, so edits apply to running clients.</item>
///   <item>RynthAi: Logs\Diagnostics\diagnostics.json "Categories" and "Events" (Off / Trace / Info).
///         RynthAi polls it every couple of seconds and rewrites it with the full catalog on load.</item>
/// </list>
/// </summary>
internal static class LoggingSettingsStore
{
    /// <summary>One engine log category as shown in the launcher.</summary>
    internal readonly record struct EngineCategory(string Name, string DefaultLevel, string Description);

    /// <summary>One RynthAi category / event row read from diagnostics.json.</summary>
    internal sealed record DiagnosticsRow(string Key, string Level, string Description);

    /// <summary>Global level choices (engine.json "LoggingLevel"), most to least quiet.</summary>
    internal static readonly string[] GlobalLevels = { "Off", "Error", "Warning", "Info", "Debug", "Trace" };

    /// <summary>Engine category emit levels. Info = normal log; Debug/Trace show only at that global level.</summary>
    internal static readonly string[] EngineCategoryLevels = { "Off", "Trace", "Debug", "Info" };

    /// <summary>RynthAi category / event levels: Off, Trace (trace file only), Info (also the normal log).</summary>
    internal static readonly string[] RynthAiLevels = { "Off", "Trace", "Info" };

    /// <summary>Must match RynthCore.Engine.LogCategory names and LogSettings defaults.</summary>
    internal static readonly EngineCategory[] EngineCategories =
    {
        new("General", "Info",  "Uncategorised engine lines."),
        new("D3D9",    "Info",  "D3D9 hook, EndScene, bootstrapper, matrix capture, nav markers."),
        new("Compat",  "Info",  "Client hooks: objects, combat, movement, vitals, chat."),
        new("Render",  "Off",   "ImGui rendering and Win32 input (very chatty)."),
        new("Plugin",  "Info",  "Plugin loader/manager and every plugin host Log line."),
        new("UI",      "Info",  "Avalonia overlay and panels."),
        new("Verbose", "Debug", "Detail lines across all subsystems (Info = always in the normal log)."),
    };

    private const string EngineCategoriesProperty = "LogCategories";

    /// <summary>RynthAi's diagnostics.json (created by RynthAi on its first load).</summary>
    internal static string DiagnosticsPath =>
        Path.Combine(RynthInstallPaths.RynthAiDir, "Logs", "Diagnostics", "diagnostics.json");

    // ── Engine ──────────────────────────────────────────────────────────

    /// <summary>Current engine category levels; categories missing from engine.json get their defaults.</summary>
    internal static Dictionary<string, string> ReadEngineCategoryLevels()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (EngineCategory c in EngineCategories) result[c.Name] = c.DefaultLevel;

        Dictionary<string, JsonElement> fields = EngineJsonStore.Read();
        if (fields.TryGetValue(EngineCategoriesProperty, out JsonElement cats) && cats.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in cats.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && result.ContainsKey(p.Name))
                    result[p.Name] = p.Value.GetString() ?? result[p.Name];
            }
        }
        return result;
    }

    /// <summary>Sets one engine category level in engine.json, preserving every other field.</summary>
    internal static void SetEngineCategoryLevel(string category, string level)
    {
        Dictionary<string, string> levels = ReadEngineCategoryLevels();
        levels[category] = level;

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (EngineCategory c in EngineCategories)
                w.WriteString(c.Name, levels[c.Name]);
            w.WriteEndObject();
        }

        Dictionary<string, JsonElement> fields = EngineJsonStore.Read();
        using var doc = JsonDocument.Parse(ms.ToArray());
        fields[EngineCategoriesProperty] = doc.RootElement.Clone();
        EngineJsonStore.Write(fields);
    }

    // ── RynthAi ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reads RynthAi's category and event rows. Returns false (with empty lists) when diagnostics.json
    /// does not exist yet or cannot be parsed (e.g. RynthAi is mid-write; retry later).
    /// </summary>
    internal static bool TryReadRynthAi(out List<DiagnosticsRow> categories, out List<DiagnosticsRow> events, out string? error)
    {
        categories = new List<DiagnosticsRow>();
        events = new List<DiagnosticsRow>();
        error = null;
        try
        {
            if (!File.Exists(DiagnosticsPath))
            {
                error = "diagnostics.json not found - start RynthAi once to create it.";
                return false;
            }
            if (JsonNode.Parse(File.ReadAllText(DiagnosticsPath)) is not JsonObject root)
            {
                error = "diagnostics.json is not a JSON object.";
                return false;
            }
            ReadRows(root["Categories"] as JsonArray, "Name", categories);
            ReadRows(root["Events"] as JsonArray, "Key", events);
            if (categories.Count == 0)
                error = "diagnostics.json has no category list - update RynthAi to 0.6.18 or later.";
            return true;
        }
        catch (Exception ex)
        {
            error = $"diagnostics.json unreadable ({ex.GetType().Name}: {ex.Message}).";
            return false;
        }
    }

    /// <summary>Sets a RynthAi category level ("Off", "Trace", "Info") and keeps "Traces" in sync.</summary>
    internal static void SetRynthAiCategoryLevel(string category, string level)
        => UpdateDiagnostics("Categories", "Name", category, level);

    /// <summary>Sets a RynthAi key-event level ("Off", "Trace", "Info").</summary>
    internal static void SetRynthAiEventLevel(string eventKey, string level)
        => UpdateDiagnostics("Events", "Key", eventKey, level);

    private static void ReadRows(JsonArray? array, string keyField, List<DiagnosticsRow> into)
    {
        if (array == null) return;
        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject o) continue;
            string key = o[keyField]?.GetValue<string>() ?? string.Empty;
            if (key.Length == 0) continue;
            into.Add(new DiagnosticsRow(
                key,
                o["Level"]?.GetValue<string>() ?? "Off",
                o["Description"]?.GetValue<string>() ?? string.Empty));
        }
    }

    /// <summary>Read-modify-write of one row's Level; every other field in the file is preserved.</summary>
    private static void UpdateDiagnostics(string section, string keyField, string key, string level)
    {
        string path = DiagnosticsPath;
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            throw new InvalidDataException("diagnostics.json is not a JSON object.");
        if (root[section] is not JsonArray rows)
            throw new InvalidDataException($"diagnostics.json has no \"{section}\" list.");

        bool found = false;
        foreach (JsonNode? node in rows)
        {
            if (node is JsonObject o && string.Equals(o[keyField]?.GetValue<string>(), key, StringComparison.OrdinalIgnoreCase))
            {
                o["Level"] = level;
                found = true;
            }
        }
        if (!found)
            throw new KeyNotFoundException($"\"{key}\" is not in diagnostics.json {section}.");

        // RynthAi reads Categories when present, but older readers (and /ra trace status) use Traces.
        if (section == "Categories")
        {
            var traces = new JsonArray();
            foreach (JsonNode? node in rows)
            {
                if (node is JsonObject o && !string.Equals(o["Level"]?.GetValue<string>(), "Off", StringComparison.OrdinalIgnoreCase))
                    traces.Add(o["Name"]?.GetValue<string>());
            }
            root["Traces"] = traces;
        }

        // Atomic swap so RynthAi's poll never reads a half-written file.
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        File.Move(tmp, path, overwrite: true);
    }
}

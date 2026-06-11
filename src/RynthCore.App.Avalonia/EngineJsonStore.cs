using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RynthCore.App.Avalonia;

/// <summary>
/// Read-modify-write access to %APPDATA%\RynthCore\engine.json. The launcher
/// owns a few engine-side flags (DComp overlay toggle, plugin paths, etc.)
/// but the engine has its own flags that the launcher should never overwrite
/// (EnableImGuiShell, EnableImGuiBackend, EngineHookCount, ...). This helper
/// preserves every field it doesn't touch so those don't get clobbered when
/// the launcher updates one setting.
/// </summary>
internal static class EngineJsonStore
{
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RynthCore",
        "engine.json");

    // Latched when an EXISTING engine.json failed to parse. While set, Write()
    // is refused: writing the (empty) read-back would silently destroy every
    // field the parse failure hid — EnableImGuiShell, PluginPaths, the lot —
    // the exact clobber this class's doc comment promises not to do.
    private static bool _refuseWrites;

    /// <summary>
    /// Returns the JSON document as a mutable dictionary. Missing file → empty
    /// dict. Parse error on an EXISTING file → empty dict for read-only use,
    /// but writes are disabled until the file is fixed or deleted.
    /// </summary>
    public static Dictionary<string, JsonElement> Read()
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(Path)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(Path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                result[p.Name] = p.Value.Clone();
            _refuseWrites = false;
        }
        catch (Exception ex)
        {
            if (!_refuseWrites)
                LauncherDiag.Info($"ENGINEJSON: {Path} failed to parse ({ex.GetType().Name}: {ex.Message}) — launcher writes to it are DISABLED until the file is fixed or deleted, so the engine's settings can't be clobbered.");
            _refuseWrites = true;
        }
        return result;
    }

    public static void Write(Dictionary<string, JsonElement> fields)
    {
        if (_refuseWrites)
        {
            LauncherDiag.Info($"ENGINEJSON: write refused — {Path} is corrupt (see earlier ENGINEJSON line). Fix or delete it, then retry.");
            return;
        }

        string? dir = System.IO.Path.GetDirectoryName(Path);
        if (dir != null) Directory.CreateDirectory(dir);

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (KeyValuePair<string, JsonElement> kv in fields)
            {
                w.WritePropertyName(kv.Key);
                kv.Value.WriteTo(w);
            }
            w.WriteEndObject();
        }

        // Atomic swap (with .bak) — a crash mid-write otherwise truncates the
        // file, and the engine silently reverts ALL settings on a corrupt read.
        string tmp = Path + ".tmp";
        File.WriteAllBytes(tmp, ms.ToArray());
        if (File.Exists(Path))
            File.Replace(tmp, Path, Path + ".bak");
        else
            File.Move(tmp, Path);
    }

    public static bool? GetBool(string field)
    {
        Dictionary<string, JsonElement> fields = Read();
        if (!fields.TryGetValue(field, out JsonElement el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public static void SetBool(string field, bool value)
    {
        Dictionary<string, JsonElement> fields = Read();
        using var doc = JsonDocument.Parse(value ? "true" : "false");
        fields[field] = doc.RootElement.Clone();
        Write(fields);
    }

    public static void SetStringArray(string field, IEnumerable<string> values)
    {
        Dictionary<string, JsonElement> fields = Read();

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            foreach (string v in values) w.WriteStringValue(v);
            w.WriteEndArray();
        }
        using var doc = JsonDocument.Parse(ms.ToArray());
        fields[field] = doc.RootElement.Clone();
        Write(fields);
    }
}

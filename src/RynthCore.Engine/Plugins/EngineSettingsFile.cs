using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace RynthCore.Engine.Plugins;

/// <summary>
/// File side of <see cref="EngineSettings"/>'s save: engine.json is shared (the launcher writes its
/// own keys there, e.g. EnableDcompOverlay), so a save keeps every top-level key the engine doesn't
/// own exactly as it was, and swaps the file in atomically (temp file, then replace) so a crash
/// mid-save can't leave a truncated engine.json. No logging and no engine state: the
/// EngineSettingsTests tool compiles this file in.
/// Idea from Silentkelpie (rynth PR #1, CopyUnownedFields); re-implemented here.
/// </summary>
internal static class EngineSettingsFile
{
    /// <summary>
    /// The new engine.json: <paramref name="ownedJson"/> (a JSON object holding the keys the
    /// engine writes, in its order), then every top-level key of <paramref name="existingJson"/>
    /// that is neither in <paramref name="ownedKeys"/> nor written by the engine, value untouched.
    /// A key in <paramref name="ownedKeys"/> that the engine didn't write this time (one it only
    /// writes when not the default) is left out, as before. A missing or empty existing file has
    /// nothing to keep. Returns null, with <paramref name="error"/> set, when the existing file
    /// isn't a readable JSON object: overwriting it would lose whatever it holds.
    /// </summary>
    /// <param name="changedKeys">
    /// When given (and the file exists): only these of the engine's keys are taken from
    /// <paramref name="ownedJson"/> (one it left out, at its default, is removed); every other
    /// key, owned or not, keeps the file's value. So a client that changed one setting doesn't
    /// write its stale copy of the others over what other clients saved since.
    /// Idea from Silentkelpie (rynth PR #6); re-implemented against our key set.
    /// </param>
    public static byte[]? Merge(string? existingJson, byte[] ownedJson, ICollection<string> ownedKeys, out string? error,
                                ICollection<string>? changedKeys = null)
    {
        error = null;
        JsonDocument? existing = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(existingJson))
            {
                try
                {
                    existing = JsonDocument.Parse(existingJson);
                }
                catch (JsonException ex)
                {
                    error = ex.Message;
                    return null;
                }
                if (existing.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = $"the top level is {existing.RootElement.ValueKind}, not an object";
                    return null;
                }
            }

            using JsonDocument owned = JsonDocument.Parse(ownedJson);
            var written = new HashSet<string>(StringComparer.Ordinal);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                // Only the changed keys when there's a file to keep the rest from.
                bool onlyChanged = changedKeys != null && existing != null;
                foreach (JsonProperty p in owned.RootElement.EnumerateObject())
                {
                    if (onlyChanged && !changedKeys!.Contains(p.Name)) continue;
                    if (!written.Add(p.Name)) continue;
                    p.WriteTo(w);
                }
                if (existing != null)
                {
                    foreach (JsonProperty p in existing.RootElement.EnumerateObject())
                    {
                        if (onlyChanged ? changedKeys!.Contains(p.Name) : ownedKeys.Contains(p.Name)) continue;
                        if (!written.Add(p.Name)) continue;   // also drops a duplicate key in the old file
                        p.WriteTo(w);
                    }
                }
                w.WriteEndObject();
            }
            return ms.ToArray();
        }
        finally
        {
            existing?.Dispose();
        }
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to a temp file next to <paramref name="path"/> (unique per
    /// process, since every client's engine can save), then swaps it in with File.Replace (or a
    /// move when there is no file yet). A reader or another client's save holding the file for a
    /// moment gets a few short retries. The temp file never stays behind. Throws on failure.
    /// </summary>
    public static void WriteAtomic(string path, byte[] bytes)
    {
        string tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                        File.Replace(tmp, path, destinationBackupFileName: null);
                    else
                        File.Move(tmp, path, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(25 * attempt);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(25 * attempt);
                }
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}

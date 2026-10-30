using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Engine.Plugins;

/// <summary>
/// De-duplicates engine.json's PluginPaths before anything is shadow-copied or loaded. Every
/// plugin's shadow copy sits in the same session folder under its file name, so two entries with
/// one file name can never both load: the second copy would overwrite a mapped file.
///   - The same file listed twice (any case, / or \, ".." or "." segments): loaded once; a
///     harmless repeat, said once in the log.
///   - The same file name in two folders: a real conflict (two builds of one plugin). The one
///     listed first (or already loaded) wins; the other is ignored with a warning naming both.
/// No logging and no engine state: PluginManager writes the lines, and tools/PluginManifestTests
/// compiles this file in.
/// Idea from Silentkelpie (rynth PR #1); re-implemented here.
/// </summary>
internal static class PluginPathDedupe
{
    /// <summary>An entry that won't load: <see cref="Path"/> as listed, and the entry that loads
    /// instead. <see cref="SameFile"/>: both name one file (a repeat), not two copies.</summary>
    internal readonly record struct Skipped(string Path, string KeptPath, bool SameFile, bool KeptAlreadyLoaded)
    {
        public string Message => SameFile
            ? (KeptAlreadyLoaded
                ? $"{Path} is already loaded - listed twice in engine.json PluginPaths, loaded once."
                : $"{Path} is listed twice in engine.json PluginPaths - loaded once.")
            : $"plugin conflict: {System.IO.Path.GetFileName(Path)} is listed from two folders. "
              + $"Using {KeptPath} ({(KeptAlreadyLoaded ? "already loaded" : "listed first")}); ignoring {Path}. "
              + "Remove the one you don't want from the launcher's plugin list.";
    }

    internal sealed class Result
    {
        public List<string> Load { get; } = new();
        public List<Skipped> Skipped { get; } = new();
    }

    /// <summary>
    /// <paramref name="paths"/> in order, less repeats and file-name conflicts with an earlier
    /// entry or with <paramref name="alreadyLoaded"/> (plugins loaded before this pass). The paths
    /// kept are returned as listed, not rewritten.
    /// </summary>
    public static Result Dedupe(IReadOnlyList<string> paths, IEnumerable<string>? alreadyLoaded = null)
    {
        var result = new Result();
        // File name -> (normalized full path, path as listed, already loaded?)
        var byName = new Dictionary<string, (string Full, string Listed, bool Loaded)>(StringComparer.OrdinalIgnoreCase);

        if (alreadyLoaded != null)
        {
            foreach (string loaded in alreadyLoaded)
            {
                if (string.IsNullOrWhiteSpace(loaded)) continue;
                string name = FileName(loaded);
                if (!byName.ContainsKey(name))
                    byName[name] = (Normalize(loaded), loaded, true);
            }
        }

        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string name = FileName(path);
            string full = Normalize(path);
            if (byName.TryGetValue(name, out var kept))
            {
                bool same = string.Equals(kept.Full, full, StringComparison.OrdinalIgnoreCase);
                result.Skipped.Add(new Skipped(path, kept.Listed, same, kept.Loaded));
                continue;
            }
            byName[name] = (full, path, false);
            result.Load.Add(path);
        }
        return result;
    }

    private static string FileName(string path) => Path.GetFileName(path.Trim().TrimEnd('/', '\\'));

    /// <summary>Full path with \ separators and no trailing separator; the raw text if the path
    /// can't be resolved (it then only matches itself).</summary>
    internal static string Normalize(string path)
    {
        string p = path.Trim();
        try
        {
            p = Path.GetFullPath(p);
        }
        catch (Exception)
        {
            // Bad characters or the like: compare as written.
        }
        return p.Replace('/', '\\').TrimEnd('\\');
    }
}

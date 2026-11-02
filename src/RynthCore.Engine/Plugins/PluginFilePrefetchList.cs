// ============================================================================
//  RynthCore.Engine - Plugins/PluginFilePrefetchList.cs
//  The files PluginFilePrefetch opens ahead of the plugin load: each listed
//  plugin's DLL first (in engine.json order: the manifest gate reads them first),
//  then the other DLLs and PDBs in each plugin's folder (its dependencies and
//  symbols, which ManagedPlugins loads), then Runtime\PluginShared's DLLs.
//  Top-level files only; each file once (any case); missing plugins skipped.
//
//  BCL only, so tools\PluginManifestTests compiles it too.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Engine.Plugins;

internal static class PluginFilePrefetchList
{
    public static List<string> Build(IReadOnlyList<string> pluginPaths, string? sharedDir)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new List<string>();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return; }
            if (seen.Add(full)) files.Add(full);
        }

        foreach (string path in pluginPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string trimmed = path.Trim();
            if (!File.Exists(trimmed)) continue;
            Add(trimmed);
            string? dir = Path.GetDirectoryName(Path.GetFullPath(trimmed));
            if (dir != null && seenFolders.Add(dir)) folders.Add(dir);
        }

        foreach (string dir in folders)
            AddFolder(dir, Add, withPdbs: true);
        if (!string.IsNullOrEmpty(sharedDir))
            AddFolder(sharedDir, Add, withPdbs: false);
        return files;
    }

    private static void AddFolder(string dir, Action<string> add, bool withPdbs)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var found = new List<string>(Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly));
            if (withPdbs) found.AddRange(Directory.GetFiles(dir, "*.pdb", SearchOption.TopDirectoryOnly));
            found.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string f in found) add(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that can't be listed is left to the load itself.
        }
    }
}

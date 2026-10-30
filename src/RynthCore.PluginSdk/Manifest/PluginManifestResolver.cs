// ============================================================================
//  RynthCore.PluginSdk - Manifest/PluginManifestResolver.cs
//  Decides which of a set of plugins can start on an engine, and in what order:
//    - a plugin whose manifest needs a newer (or older) engine API is refused;
//    - a plugin whose REQUIRED dependency is missing, too old or refused is refused
//      (and so on down the chain);
//    - required dependencies start before the plugins that need them. Optional
//      dependencies never refuse anything and never reorder: plugins look each other
//      up lazily (GetPluginInterface), and a pair of plugins that can use each other
//      would otherwise form a cycle.
//  A plugin without a manifest is never refused and keeps its place, so a set with no
//  manifests at all comes out exactly as it went in.
//
//  Pure logic, used by the engine's PluginManager and by tools\PluginManifestTests.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.PluginSdk.Manifest;

/// <summary>One plugin to consider: its path (or any key), and its manifest if it has one.</summary>
public sealed class PluginCandidate
{
    /// <param name="fileVersion">The DLL's version resource (ProductVersion), used when the manifest has no version or there is no manifest.</param>
    public PluginCandidate(string key, RynthPluginManifest? manifest, string? fileVersion = null)
    {
        Key = key;
        Manifest = manifest;
        Name = manifest?.Name is { Length: > 0 } n ? n : RynthPluginManifest.NameFromFile(key);
        Version = manifest?.Version is { Length: > 0 } v ? v : fileVersion ?? "";
    }

    public string Key { get; }
    public RynthPluginManifest? Manifest { get; }
    /// <summary>The manifest name, or for a plugin without one the name from its file.</summary>
    public string Name { get; }
    public string Version { get; }
}

public sealed class PluginRefusal
{
    public PluginRefusal(PluginCandidate plugin, string reason, bool engine)
    {
        Plugin = plugin;
        Reason = reason;
        IsEngineMismatch = engine;
    }

    public PluginCandidate Plugin { get; }
    /// <summary>One sentence, fit for chat: "RynthOracle needs a newer RynthCore (API 77)".</summary>
    public string Reason { get; }
    /// <summary>True for an engine API mismatch, false for a dependency problem.</summary>
    public bool IsEngineMismatch { get; }
}

public sealed class PluginResolution
{
    /// <summary>The plugins to load and start, in order.</summary>
    public List<PluginCandidate> Order { get; } = new();
    public List<PluginRefusal> Refused { get; } = new();
    /// <summary>Things worth a log line that refuse nothing (a missing optional dependency, a dev build, a cycle).</summary>
    public List<string> Notes { get; } = new();
}

public static class PluginManifestResolver
{
    public static PluginResolution Resolve(IReadOnlyList<PluginCandidate> candidates, uint engineApi)
    {
        var result = new PluginResolution();
        var refused = new Dictionary<PluginCandidate, string>();

        // 1. The engine API.
        foreach (PluginCandidate c in candidates)
        {
            if (c.Manifest?.CheckEngine(engineApi) is { } why)
                Refuse(result, refused, c, why, engine: true);
        }

        // 2. Required dependencies, until nothing more falls.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (PluginCandidate c in candidates)
            {
                if (refused.ContainsKey(c) || c.Manifest == null) continue;
                foreach (RynthPluginDependency d in c.Manifest.Dependencies)
                {
                    if (d.Optional) continue;
                    string? why = CheckDependency(candidates, refused, c, d);
                    if (why == null) continue;
                    Refuse(result, refused, c, why, engine: false);
                    changed = true;
                    break;
                }
            }
        }

        // 3. Notes on optional dependencies and dev builds (refuse nothing).
        foreach (PluginCandidate c in candidates)
        {
            if (refused.ContainsKey(c) || c.Manifest == null) continue;
            foreach (RynthPluginDependency d in c.Manifest.Dependencies)
            {
                PluginCandidate? dep = Find(candidates, d.Name);
                if (d.Optional && (dep == null || refused.ContainsKey(dep)))
                    result.Notes.Add($"{c.Name}: optional plugin {d.Name} is not loaded; the features that use it are off.");
                else if (d.Optional && !PluginVersion.Satisfies(dep!.Version, d.MinVersion))
                    result.Notes.Add($"{c.Name}: optional plugin {d.Name} is older ({dep.Version}) than it can use ({d.MinVersion}).");
                else if (dep != null && d.MinVersion.Length > 0 && PluginVersion.IsDevBuild(dep.Version))
                    result.Notes.Add($"{c.Name}: {d.Name} is a dev build ({dep.Version}); taken as satisfying {d.MinVersion}.");
            }
        }

        // 4. Order: each plugin after its required dependencies, otherwise as given.
        var accepted = new List<PluginCandidate>();
        foreach (PluginCandidate c in candidates)
            if (!refused.ContainsKey(c)) accepted.Add(c);

        var placed = new HashSet<PluginCandidate>();
        var visiting = new HashSet<PluginCandidate>();
        void Place(PluginCandidate c)
        {
            if (placed.Contains(c)) return;
            if (!visiting.Add(c))
            {
                result.Notes.Add($"{c.Name}: its required dependencies form a cycle; started in list order.");
                return;
            }
            if (c.Manifest != null)
            {
                foreach (RynthPluginDependency d in c.Manifest.Dependencies)
                {
                    if (d.Optional) continue;
                    PluginCandidate? dep = Find(accepted, d.Name);
                    if (dep != null) Place(dep);
                }
            }
            visiting.Remove(c);
            if (placed.Add(c)) result.Order.Add(c);
        }
        foreach (PluginCandidate c in accepted) Place(c);
        return result;
    }

    /// <summary>
    /// Null when <paramref name="d"/> is satisfied by a plugin in <paramref name="available"/>
    /// (by name; a dev build satisfies any version), else why not.
    /// </summary>
    public static string? CheckDependency(IReadOnlyList<PluginCandidate> available, IReadOnlyDictionary<PluginCandidate, string>? refused,
                                          PluginCandidate owner, RynthPluginDependency d)
    {
        PluginCandidate? dep = Find(available, d.Name);
        string needs = d.MinVersion.Length > 0 ? $"{d.Name} {d.MinVersion} or newer" : $"the {d.Name} plugin";
        if (dep == null)
            return $"{owner.Name} needs {needs}, which is not in the plugin list";
        if (refused != null && refused.ContainsKey(dep))
            return $"{owner.Name} needs {d.Name}, which was not loaded";
        if (!PluginVersion.Satisfies(dep.Version, d.MinVersion))
            return dep.Version.Length > 0
                ? $"{owner.Name} needs {d.Name} {d.MinVersion} or newer (this one is {dep.Version})"
                : $"{owner.Name} needs {d.Name} {d.MinVersion} or newer (this one has no version)";
        return null;
    }

    public static PluginCandidate? Find(IReadOnlyList<PluginCandidate> list, string name)
    {
        foreach (PluginCandidate c in list)
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    private static void Refuse(PluginResolution result, Dictionary<PluginCandidate, string> refused, PluginCandidate c, string why, bool engine)
    {
        if (refused.ContainsKey(c)) return;
        refused[c] = why;
        result.Refused.Add(new PluginRefusal(c, why, engine));
    }
}

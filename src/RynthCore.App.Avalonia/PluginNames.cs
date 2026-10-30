using System;
using System.IO;
using System.Linq;
using RynthCore.App;

namespace RynthCore.App.Avalonia;

/// <summary>
/// How the launcher names a plugin, and which plugins are server plugins. The plugin list used to
/// show each DLL's file name ("RynthCore.Plugin.RynthAi") over its full path; it shows "RynthAi", a line
/// about what it does and its folder instead. A server plugin (one per game server, KnownServerCatalog)
/// goes on the Server plugins tab, not among the regular plugins.
/// </summary>
internal static class PluginNames
{
    private const string Prefix = "RynthCore.Plugin.";

    /// <summary>"RynthCore.Plugin.RynthAi.dll" or "RynthCore.Plugin.RynthAi" or "RynthAi" -> "RynthAi".</summary>
    public static string Friendly(string fileOrName)
    {
        string n = Path.GetFileName(fileOrName ?? string.Empty);
        if (n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        if (n.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && n.Length > Prefix.Length) n = n[Prefix.Length..];
        return n;
    }

    /// <summary>Plugins for one server besides its own server plugin (catalog id of that server).</summary>
    private static readonly (string Plugin, string ServerId)[] AlsoForOneServer =
    {
        ("RynthThreads", "dreamweave"),   // DreamWeave's Fragment Press recorder
    };

    /// <summary>The server a plugin belongs to, or null for a regular plugin.</summary>
    public static KnownServer? ServerOf(string fileOrName)
    {
        string n = Friendly(fileOrName);
        KnownServer? s = KnownServerCatalog.All.FirstOrDefault(k => string.Equals(k.PluginName, n, StringComparison.OrdinalIgnoreCase));
        if (s != null) return s;
        foreach (var (plugin, id) in AlsoForOneServer)
            if (string.Equals(plugin, n, StringComparison.OrdinalIgnoreCase))
                return KnownServerCatalog.All.FirstOrDefault(k => k.Id == id);
        return null;
    }

    public static bool IsServerPlugin(string fileOrName) => ServerOf(fileOrName) != null;
}

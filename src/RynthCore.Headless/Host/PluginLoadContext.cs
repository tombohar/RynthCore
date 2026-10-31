// ============================================================================
//  RynthCore.Headless - Host/PluginLoadContext.cs
//  One collectible AssemblyLoadContext per plugin load, like the engine's
//  ManagedPlugins (src/RynthCore.Engine/Plugins/ManagedPlugins.cs, whose approach this
//  follows): the plugin's own folder first, then a shared folder, else the default
//  context (framework). Loaded from streams so the folder stays unlocked. Native
//  libraries (cimgui) resolve from the same folders if present; headless plugins never
//  draw, so a missing one only matters if something calls ImGui.
//  Statics live per context, so each character's RynthAi is its own instance.
// ============================================================================

using System.Reflection;
using System.Runtime.Loader;

namespace RynthCore.Headless.Host;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _dir;
    private readonly string? _shared;

    public PluginLoadContext(string name, string pluginDir, string? sharedDir) : base(name, isCollectible: true)
    {
        _dir = pluginDir;
        _shared = sharedDir;
    }

    public Assembly LoadPlugin(string path) => LoadFile(path);

    private Assembly LoadFile(string path)
    {
        using var dll = File.OpenRead(path);
        string pdb = Path.ChangeExtension(path, ".pdb");
        if (!File.Exists(pdb)) return LoadFromStream(dll);
        using var sym = File.OpenRead(pdb);
        return LoadFromStream(dll, sym);
    }

    private string? Pick(string file)
    {
        string a = Path.Combine(_dir, file);
        string? b = _shared != null ? Path.Combine(_shared, file) : null;
        bool ha = File.Exists(a), hb = b != null && File.Exists(b);
        if (ha && hb) return File.GetLastWriteTimeUtc(a) >= File.GetLastWriteTimeUtc(b!) ? a : b;
        return ha ? a : hb ? b : null;
    }

    protected override Assembly? Load(AssemblyName name)
    {
        string? path = Pick(name.Name + ".dll");
        return path != null ? LoadFile(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string file = unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? unmanagedDllName : unmanagedDllName + ".dll";
        string? path = Pick(file);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}

using System;
using System.Diagnostics;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Detects whether a Decal-style coexistence stack is loaded inside this
/// acclient.exe process. When true, the engine skips its D3D9 hooks and
/// in-game overlays so Decal's own EndScene rendering isn't disrupted —
/// only Avalonia floating panels (LayeredWindow / GDI) remain available.
///
/// Result is cached on first call: a Decal stack appearing later in the
/// session would still take effect on a hot-reload, but during a single
/// run the snapshot at engine init is what gates the D3D9 path.
/// </summary>
internal static class DecalDetection
{
    private static readonly string[] DecalModuleNames =
    {
        "decal.dll",
        "UBLoader.dll",
        "Decal.Adapter.dll",
        "phatacd.dll",
    };

    private static readonly object Sync = new();
    private static bool _evaluated;
    private static string? _detectedModule;

    public static bool IsDecalLoaded => DetectedModule != null;

    public static string? DetectedModule
    {
        get
        {
            lock (Sync)
            {
                if (_evaluated)
                    return _detectedModule;

                _evaluated = true;
                _detectedModule = ProbeOnce();
                return _detectedModule;
            }
        }
    }

    private static string? ProbeOnce()
    {
        try
        {
            using Process self = Process.GetCurrentProcess();
            foreach (ProcessModule module in self.Modules)
            {
                string name = module.ModuleName;
                for (int i = 0; i < DecalModuleNames.Length; i++)
                {
                    if (string.Equals(name, DecalModuleNames[i], StringComparison.OrdinalIgnoreCase))
                        return DecalModuleNames[i];
                }
            }
        }
        catch
        {
            // Failed to enumerate modules — treat as no Decal so we don't
            // disable our own rendering on a transient probe error.
        }

        return null;
    }
}

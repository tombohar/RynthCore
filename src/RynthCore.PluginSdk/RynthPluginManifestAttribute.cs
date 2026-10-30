using System;

namespace RynthCore.PluginSdk;

/// <summary>
/// The plugin's manifest minimum, as an assembly attribute: build\RynthCore.PluginSdk.targets adds
/// it from RynthPluginMinEngineApi alongside the embedded manifest. RynthPluginBase.MinimumApiVersion
/// defaults to it, so the plugin's own check (which also guards engines from before manifests) and
/// the engine's/launcher's manifest check use the same number. Works under NativeAOT.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class RynthPluginManifestAttribute : Attribute
{
    public RynthPluginManifestAttribute(uint minEngineApi) => MinEngineApi = minEngineApi;

    /// <summary>The manifest's minEngineApi.</summary>
    public uint MinEngineApi { get; }
}

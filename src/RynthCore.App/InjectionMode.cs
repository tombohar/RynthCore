namespace RynthCore.App;

/// <summary>
/// Per-account choice of which modding stack to inject when launching the AC
/// client. Selected from the launcher's per-row Injection Method dropdown.
/// </summary>
internal enum InjectionMode
{
    /// <summary>RynthCore.Loader.dll → RynthCore.Engine.dll injected via the
    /// in-process injector. The native RynthCore stack.</summary>
    RynthCore = 0,

    /// <summary>Decal's Inject.dll injected with its DecalStartup export. The
    /// account runs against Decal + UBService + VTank or whatever Decal plugins
    /// the user has registered.</summary>
    Decal = 1,
}

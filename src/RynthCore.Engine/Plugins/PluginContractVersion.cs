// ============================================================================
//  RynthCore.Engine - Plugins/PluginContractVersion.cs
//  The plugin API's version: the number of the last table (RynthCoreAPI) layout.
//
//  One file, compiled into three assemblies so they can't disagree:
//    - the engine (this folder), which hands it to plugins in RynthCoreAPI.Version;
//    - the SDK (RynthCore.PluginSdk), as RynthCoreHost.CurrentApiVersion;
//    - the launcher (RynthCore.App.Avalonia), as the API of the engine it installed,
//      to hold back feed plugins whose minEngineApi is newer.
//  tools\PluginManifestTests also checks the SDK's table covers this version.
// ============================================================================

namespace RynthCore.Engine.Plugins;

/// <summary>Current API version. Bump when adding fields to RynthCoreAPI (and the SDK's RynthCoreApiNative).</summary>
internal static class PluginContractVersion
{
    // v81: GetMergeStackResult (was v77 on QOL-items, then v80; renumbered after upstream's v77-v80).
    // v82: SalvageDirect (salvage without the salvage panel; was v81 on feature/salvage-direct).
    // v83: GetObjectServerAge and RemoveClientObject (ghost creatures, 2026-10-12).
    // v84: GetObjectProperties (every property of an object, for /ub propertydump; 2026-10-13).
    // v85: ArmConfirmation and GetLastConfirmation (answer the server's yes/no prompt, e.g. the
    //      craft chance, for a plugin; from SilentKelpie's build, 2026-10-15).
    // v86: GetServerStateJson (the server list, active server and owning plugins for every plugin;
    //      server plugins declare their server in their manifest; from SilentKelpie's build).
    // v87: GetWieldedItems (what the player wields and where, from the server's wield / contain /
    //      drop messages: EquipmentTracker; the client's own fields go stale, 2026-10-18).
    // v88: managed plugins find their common dependencies in Runtime\PluginShared (installed with
    //      RynthCore), so the update feed ships each managed plugin as one DLL; releases build the
    //      plugins managed (collectible: a reload frees them) and they declare minEngineApi 88.
    // v89: plugin windows: GetUiColor, GetUiFont and SetPanelOpen (the engine hosts the windows
    //      a plugin's sections manifest declares; the plugin draws them; docs/PLUGIN_WINDOWS.md).
    // v90: windows that draw their own frame (manifest "chrome": "none", "fitHeight"):
    //      PanelDragWithLastItem, SetPanelPopOut, PanelTextSize; GetUiColor's Muted and instrument
    //      shades (KeyFace, Hairline, Inset, Faint), GetUiFont's Dash and Narrow fonts (2026-10-21).
    // v91: SetAmbientLightFloor and GetAmbientLight: a floor under AC's world ambient light, drawn
    //      in this client only (D3D9/AmbientLight.cs; RynthVision's dungeon light, 2026-10-27).
    //      No table change, same release: RenderSettingsDrawer also draws "nameplates" and
    //      "combattext" (popped out too), the engine-owned tabs of RynthVision's own Vision window.
    // v92: SetActionKey op 3, a RynthCore command's key as an engine hotkey only (AC's keymap untouched),
    //      and the SDK's BindCommandKeyOnly / UnbindCommandKeyOnly; no new function or field, but a plugin
    //      calling them needs this SDK in Runtime\PluginShared (fix/keymap-follow-change, 2026-10-27).
    // v93: GetMonsterHealth: the monster plates' last SERVER health reading for an object and its age in ms
    //      (MonsterHudData.TryGetServerHealth; the plates' round-robin probe replies included, their hit
    //      estimates never), so a plugin can rank every nearby monster by health while the server streams
    //      only the queried one. The probe replies still don't reach plugins as UpdateHealth. Read-only,
    //      any thread (feature/hud-health-for-plugins, 2026-11-07). Note: feature/reload-state,
    //      feature/engine-events and the branches built on them (unmerged) also use 93 and 94; whichever
    //      merges second renumbers.
    // v94: WebPanelOpen, WebPanelNavigate, WebPanelClose and WebPanelGetState: a WebView2 web panel over the
    //      game window (WebPanel\WebPanelApi.cs; DreamWeave's in-game market and tools), appended after v93's
    //      GetMonsterHealth (was v93 on up/web-panel; renumbered after upstream's v93). The number is
    //      PROVISIONAL: the unmerged branches named under v93 also use 94; whichever merges second renumbers.
    // v95: no table change. SectionManifest.cs reads a plugin window's "barMenu" (a hover dropdown on its
    //      bar icon: up to 6 items of id, tooltip, icon and a /command the engine runs on click). Engines
    //      before v95 ignore the key (was v94 on up/bar-menu; renumbered after upstream's v93). The number
    //      is PROVISIONAL, as 94 is: whichever merges second renumbers.
    /// <summary>The API version that added the "barMenu" manifest key; a plugin declaring it pins its own copy against this.</summary>
    public const uint BarMenu = 95;

    public const uint Current = 95;
}

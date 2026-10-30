// ============================================================================
//  RynthCore.Engine - D3D9/AmbientLight.cs
//  A floor under the client's world ambient light (2026-10-27, plugin API
//  v91): the hook RynthVision's dungeon light uses. A player asked for
//  SkunkVision's "Dungeon Light" (problem report R-20261027-141534-6a2b:
//  the dark TOR dungeons). Drawing only, in this client: nothing is sent to
//  the server and nobody else sees a difference.
//
//  How AC lights its world (read offline from our client, 4,841,472 bytes;
//  the Chorizite names in GhidraTools\symbols.csv):
//    - AC draws with D3D9 fixed-function lighting. Its world ambient is three
//      floats (0x008682B0, RGB) that SmartBox::SetWorldAmbientLight
//      (0x004530E0) sets to level x colour. CellManager::ChangePosition
//      (0x00455AD0) picks them on every cell change: outdoors, or in an
//      EnvCell that sees outside (a building), from the sky
//      (LScape::calc_object_light); in any other EnvCell (a dungeon)
//      SetWorldAmbientLight(0.2, white), a flat 20 %: that is why dungeons
//      are dark away from their torches.
//    - Every viewpoint update (Render::update_viewpoint: the world once a
//      frame, and the 3D views in AC's own UI, e.g. the paper doll) runs
//      PrimD3DRender::UpdateLightsInternal (0x0059BEE0), which makes those
//      floats a colour and hands it to RenderDeviceD3D::SetFFAmbientColor32
//      (0x005A3EB0, below): SetRenderState(D3DRS_AMBIENT) when it differs from
//      the copy AC keeps at +0x4A0. Its only other caller is
//      SetDefaultDeviceStates (after a device reset).
//    - Dungeon walls (EnvCells) are drawn with lighting on: their static
//      lights (torches) are baked into the vertex colour
//      (D3DPolyRender::SetStaticLightingVertexColors), which
//      D3DPolyRender::RenderMeshSubset uses as the EMISSIVE colour, with a
//      white material (1.0 at 0x00821E38) for ambient and diffuse. A wall is
//      torches + D3DRS_AMBIENT + dynamic lights, so more ambient lights every
//      wall, floor, creature and item evenly; torches, textures and the fog
//      (distance fade to black) are unchanged.
//    - Gamma is no way round it: RenderDeviceD3D::SetGamma is SetGammaRamp,
//      which D3D9 applies only in full-screen exclusive mode.
//  The hook changes SetFFAmbientColor32's colour argument: per channel the
//  brighter of AC's and the floor (AmbientLightPolicy). AC's cache then holds
//  what was really set, so it stays consistent; a new floor shows on the next
//  frame (UpdateLightsInternal calls it every frame) and floor 0 hands AC's
//  colour back the same way. The floor applies wherever AC sets an ambient
//  while it is set: the plugin decides where (RynthVision: dungeons only by
//  default) and clears it when it doesn't apply.
//
//  Safety (as VideoPatch):
//    - A hook on one argument, not a patch of AC's floats or code paths: with
//      no floor (the default) the detour passes AC's colour through as it is.
//    - Kill switch: UI hook slot "AmbientLight" (engine.json DisabledUiHooks
//      ["AmbientLight"]; /rc hooks off AmbientLight = pass-through now).
//    - Pattern first (HookResolver; tools\pe_pattern.py CHECK covers it), the
//      Decal guard (UiHookRegistry.TryInstall). Unresolved: logged once, and
//      SetFloor answers 0 so the plugin can say so.
//    - The floor goes back to 0 when the plugins unload (reload, rescan,
//      shutdown): a plugin that is gone never leaves the world lit.
//    - Cost: one managed call per SetFFAmbientColor32, a few per frame.
// ============================================================================

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.D3D9;

internal static unsafe class AmbientLight
{
    // RenderDeviceD3D::SetFFAmbientColor32 on the 4,841,472-byte client. Pattern is the source of truth.
    private const int SetAmbientFallbackVa = 0x005A3EB0;

    // __thiscall (RenderDeviceD3D* this, D3DCOLOR color), ret 4:
    // push esi; mov esi,ecx; mov al,[esi+470h] (force flag); test al,al; push edi;
    // mov edi,[esp+0Ch] (color); jnz +8; cmp [esi+4A0h],edi (cached ambient); jz +1Bh;
    // mov eax,[esi+468h] (IDirect3DDevice9*); mov ecx,[eax]; push edi;
    // push 8Bh (D3DRS_AMBIENT); ... call [ecx+0E4h] (SetRenderState).
    // The first 24 bytes are shared by ten sibling SetFF* setters (other cache offsets); with the
    // cache offset and the push of D3DRS_AMBIENT it is unique in the scanned window (C:\Turbine
    // client, 2026-10-27). MinHook relocates the first three instructions (9 bytes, no branches).
    private static readonly byte?[] SetAmbientPattern =
    [
        0x56,
        0x8B, 0xF1,
        0x8A, 0x86, 0x70, 0x04, 0x00, 0x00,
        0x84, 0xC0,
        0x57,
        0x8B, 0x7C, 0x24, 0x0C,
        0x75, 0x08,
        0x39, 0xBE, 0xA0, 0x04, 0x00, 0x00,
        0x74, 0x1B,
        0x8B, 0x86, 0x68, 0x04, 0x00, 0x00,
        0x8B, 0x08,
        0x57,
        0x68, 0x8B, 0x00, 0x00, 0x00,
    ];

    private static readonly UiHookSlot Slot =
        UiHookRegistry.Register("AmbientLight", "RenderDeviceD3D::SetFFAmbientColor32 - the world's ambient light (light floor)", SetAmbientFallbackVa);

    private static IntPtr _origSetAmbient;
    private static int _initialized;

    /// <summary>The hook is live (cleared first on shutdown, so the detour passes through).</summary>
    public static volatile bool IsInstalled;

    // The floor (0x00RRGGBB, 0 = none): any thread writes, AC's render thread reads.
    private static uint _floor;
    // What the detour last saw and set (AC's render thread writes; diagnostics read).
    private static uint _lastClient, _lastApplied;

    /// <summary>Installed, switched on, and so able to light: what SetFloor and Get report.</summary>
    public static bool Live => IsInstalled && Slot.Live;

    public static void Initialize()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            return;

        // Whatever happens below: a floor never outlives the plugins that set it.
        PluginManager.PluginsUnloaded += ClearOnUnload;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
        {
            Slot.Reason = "acclient.exe not available";
            RynthLog.Compat("AmbientLight: acclient.exe not available - unavailable.");
            return;
        }
        if (!UiHookRegistry.SwitchedOn(Slot))
            return;

        HookResolver.ResolveResult r = HookResolver.Resolve(text, "AmbientLight.RenderDeviceD3D::SetFFAmbientColor32", SetAmbientPattern, SetAmbientFallbackVa);
        delegate* unmanaged[Thiscall]<IntPtr, uint, void> d = &SetAmbientDetour;
        if (UiHookRegistry.TryInstall(Slot, r, (IntPtr)d, out IntPtr t))
        {
            _origSetAmbient = t;
            Thread.MemoryBarrier();
            if (UiHookRegistry.Enable(Slot))
                IsInstalled = true;
        }
        RynthLog.Compat(IsInstalled
            ? $"AmbientLight: ready (RenderDeviceD3D::SetFFAmbientColor32 @ 0x{Slot.Address.ToInt32():X8} via {Slot.Source}); no floor until a plugin sets one."
            : $"AmbientLight: unavailable - {Slot.Reason}.");
    }

    /// <summary>
    /// Engine shutdown / hot reload (lifecycle thread): the detour passes straight to AC from here
    /// on, then the hook is disabled. Idempotent.
    /// </summary>
    public static void Uninstall()
    {
        Volatile.Write(ref _floor, 0u);
        PluginManager.PluginsUnloaded -= ClearOnUnload;
        if (!IsInstalled) return;
        IsInstalled = false;
        Thread.MemoryBarrier();
        int status = Hooking.MinHook.MH_DisableHook(Slot.Address);
        RynthLog.Compat($"AmbientLight: uninstalled (disable = {Hooking.MinHook.StatusString(status)}).");
    }

    private static void ClearOnUnload()
    {
        if (Volatile.Read(ref _floor) == 0) return;
        Volatile.Write(ref _floor, 0u);
        Slot.LastNote = "floor cleared (plugins unloaded)";
        RynthLog.Compat("AmbientLight: plugins unloaded - floor cleared (AC's own ambient again).");
    }

    // ── Plugin API v91 (any thread) ─────────────────────────────────────

    /// <summary>
    /// v91 SetAmbientLightFloor: the least ambient light AC may use, 0x00RRGGBB (alpha ignored), 0 =
    /// none (AC's own). Takes effect on the next frame. Returns 1 when the hook is live (the floor
    /// lights the world), 0 when it isn't (not installed, switched off): the value is kept either
    /// way. The last caller's floor wins.
    /// </summary>
    public static int SetFloor(uint argb)
    {
        try
        {
            uint floor = AmbientLightPolicy.FloorRgb(argb);
            uint old = Volatile.Read(ref _floor);
            Volatile.Write(ref _floor, floor);
            if (floor != old)
            {
                Slot.LastNote = floor == 0 ? "floor off" : $"floor 0x{floor:X6}";
                RynthLog.Verbose($"AmbientLight: floor 0x{old:X6} -> 0x{floor:X6} (hook {(Live ? "live" : "not live")}).");
            }
            return Live ? 1 : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// v91 GetAmbientLight: the last ambient colour AC asked for and the one that was set (0xAARRGGBB;
    /// 0 before the first frame). Returns 1 when the hook is live, 0 when it isn't (then both are 0).
    /// Either pointer may be null.
    /// </summary>
    public static int Get(uint* client, uint* applied)
    {
        try
        {
            bool live = Live;
            if (client != null) *client = live ? Volatile.Read(ref _lastClient) : 0u;
            if (applied != null) *applied = live ? Volatile.Read(ref _lastApplied) : 0u;
            return live ? 1 : 0;
        }
        catch { return 0; }
    }

    // ── The detour (AC's render thread) ─────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static void SetAmbientDetour(IntPtr renderDevice, uint color)
    {
        uint set = color;
        try
        {
            if (IsInstalled && Slot.Enabled)
            {
                set = AmbientLightPolicy.Apply(color, Volatile.Read(ref _floor));
                _lastClient = color;
                _lastApplied = set;
                Interlocked.Increment(ref Slot.Fired);
            }
        }
        catch { set = color; }
        ((delegate* unmanaged[Thiscall]<IntPtr, uint, void>)_origSetAmbient)(renderDevice, set);
    }
}

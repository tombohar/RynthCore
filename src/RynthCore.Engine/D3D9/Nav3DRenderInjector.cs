// ═══════════════════════════════════════════════════════════════════════════
//  RynthCore.Engine — D3D9/Nav3DRenderInjector.cs
//  Where the world overlays (Nav3D markers, then UnderUiLayer's nameplates and
//  combat text) draw in AC's frame: after AC's 3D world, before AC's 2D UI.
//  At most once per frame (one flag for every path).
//
//  Paths, in order of preference (WorldLayerMode.Auto):
//    UI pass     AcUiPassHook: the entry of AC's RenderUI::RenderObjects, after
//                all 3D including the alpha-sorted foliage/effects list. One
//                call per frame; the DrawIndexedPrimitive hook below is then
//                switched OFF (no per-draw cost).
//    Transition  only when the UI-pass hook isn't installed: the DIP hook spots
//                ZENABLE going 1->0 after 3D draws. Known to fire too early
//                outdoors (AC's sky pass after the landblocks draws with Z off,
//                before the alpha list), so foliage can cover the overlays.
//    EndScene    the fallback (EngineFrameController): a frame where neither
//                fired (no AC UI this frame, portal space, a loading screen).
//  "/rc worldlayer" reports the path and counters, and "/rc worldlayer
//  auto|uipass|transition|endscene" forces a path for the session (A/B checks).
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.D3D9;

/// <summary>Where the world overlays draw. Session only (/rc worldlayer); Auto at every start.</summary>
internal enum WorldLayerMode
{
    /// <summary>UI pass when the hook is installed, else the ZENABLE transition.</summary>
    Auto,
    /// <summary>Only at AC's UI pass (EndScene fallback otherwise).</summary>
    UiPass,
    /// <summary>Only at the first ZENABLE 1->0 transition (the pre-2026-10-04 behaviour).</summary>
    Transition,
    /// <summary>Always at EndScene (over AC's UI): for comparison only.</summary>
    EndScene,
}

internal static class Nav3DRenderInjector
{
    private const uint D3DRS_ZENABLE = 7;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DrawIndexedPrimitiveD(IntPtr dev, uint primitiveType,
        int baseVertexIndex, uint minVertexIndex, uint numVertices,
        uint startIndex, uint primCount);

    private static DrawIndexedPrimitiveD? _originalDIP;
    private static DrawIndexedPrimitiveD? _hookDelegate;
    private static IntPtr _dipTarget;
    private static bool _hookInstalled;
    private static bool _dipEnabled;
    private static bool _inRender;

    // Per-frame transition detection
    private static uint _lastZEnable;
    private static bool _markersRenderedThisFrame;
    private static bool _seen3D;

    // Frames whose overlays drew at each place (diagnostics; AC's render thread).
    private static long _atUiPass, _atTransition, _atEndScene;

    /// <summary>Session override, set by /rc worldlayer (any thread; read on AC's render thread).</summary>
    public static volatile WorldLayerMode Mode = WorldLayerMode.Auto;

    /// <summary>The world overlays already drew this frame (any path).</summary>
    public static bool RenderedThisFrame => _markersRenderedThisFrame;

    /// <summary>True while the overlays themselves are drawing (the draw hooks ignore those draws).</summary>
    internal static bool InOverlayRender => _inRender;

    private static bool UseUiPass =>
        Mode == WorldLayerMode.UiPass || (Mode == WorldLayerMode.Auto && AcUiPassHook.IsInstalled);

    private static bool UseTransition =>
        Mode == WorldLayerMode.Transition || (Mode == WorldLayerMode.Auto && !AcUiPassHook.IsInstalled);

    public static void ResetFrame()
    {
        _markersRenderedThisFrame = false;
        _seen3D = false;
        _lastZEnable = 0;
    }

    public static void Install(IntPtr pDevice)
    {
        if (_hookInstalled) return;

        try
        {
            IntPtr addr = DecalD3D9.HookTarget(pDevice, DeviceVTableIndex.DrawIndexedPrimitive);
            if (addr == IntPtr.Zero) return;   // Decal client and the slot isn't d3d9's: not hooked

            _hookDelegate = new DrawIndexedPrimitiveD(Detour);
            IntPtr hookPtr = Marshal.GetFunctionPointerForDelegate(_hookDelegate);
            _originalDIP = Marshal.GetDelegateForFunctionPointer<DrawIndexedPrimitiveD>(
                MinHook.HookCreate(addr, hookPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(addr);
            _dipTarget = addr;
            _dipEnabled = true;
            _hookInstalled = true;

            RynthLog.D3D9("Nav3DRenderInjector: DrawIndexedPrimitive hook installed.");
        }
        catch (Exception ex)
        {
            RynthLog.D3D9($"Nav3DRenderInjector: Hook install FAILED — {ex.Message}");
        }
    }

    /// <summary>
    /// Frame boundary (end of EndScene, AC's render thread): the DIP hook runs only
    /// when the transition path needs it. With the UI-pass hook live it is switched
    /// off, so AC's draws pay nothing.
    /// </summary>
    public static void SyncHookState()
    {
        // Teardown: AcUiPassHook.Uninstall flips Auto to "transition"; don't re-patch then.
        if (!_hookInstalled || EngineLifecycle.IsShuttingDown) return;
        bool want = UseTransition;
        if (want == _dipEnabled) return;
        int status = want ? MinHook.MH_EnableHook(_dipTarget) : MinHook.MH_DisableHook(_dipTarget);
        if (status == MinHook.MH_OK || status == MinHook.MH_ERROR_ENABLED || status == MinHook.MH_ERROR_DISABLED)
        {
            _dipEnabled = want;
            RynthLog.D3D9($"Nav3DRenderInjector: DrawIndexedPrimitive hook {(want ? "enabled" : "disabled")} " +
                          $"(world overlays: {DescribePath()}).");
        }
        else if (want)
        {
            RynthLog.D3D9($"Nav3DRenderInjector: re-enabling the DrawIndexedPrimitive hook failed ({MinHook.StatusString(status)}).");
        }
        else
        {
            // Couldn't switch it off: harmless, the detour returns early when not needed.
            _dipEnabled = true;
        }
    }

    /// <summary>AcUiPassHook: AC's RenderUI::RenderObjects is about to draw AC's 2D UI.</summary>
    public static void OnAcUiPass()
    {
        if (!UseUiPass) return;
        IntPtr dev = ImGuiBackend.EngineFrameController.CachedDevice;
        if (dev == IntPtr.Zero) return;
        if (PresentWorldOverlays(dev, sceneViewport: true))
            _atUiPass++;
    }

    /// <summary>
    /// EngineFrameController, EndScene, before the ImGui panels: draws the overlays
    /// when no earlier path did this frame, then closes the frame for them (the ImGui
    /// panels' own draws can't trigger the transition path afterwards).
    /// </summary>
    public static void PresentAtEndScene(IntPtr dev)
    {
        if (_markersRenderedThisFrame || dev == IntPtr.Zero) return;
        _markersRenderedThisFrame = true;
        // Video patch: no 3D scene this frame, so no markers or plates over the black.
        if (VideoPatch.SkipWorldThisFrame) return;
        _inRender = true;
        try { ImGuiBackend.DX9Backend.RenderNav3D(dev); }
        catch { }
        try { ImGuiBackend.UnderUiLayer.PresentFallback(dev); }
        catch { }
        _inRender = false;
        _atEndScene++;
    }

    /// <summary>Draws the Nav3D markers, then the world overlays, once per frame. False: not drawn here.</summary>
    private static bool PresentWorldOverlays(IntPtr dev, bool sceneViewport)
    {
        if (_markersRenderedThisFrame) return false;
        // Video patch: the 3D scene wasn't drawn this frame; the overlays (projected with
        // stale matrices) would float over black. Closed for the frame, nothing drawn.
        if (VideoPatch.SkipWorldThisFrame)
        {
            _markersRenderedThisFrame = true;
            return false;
        }
        // Decal clients: VVS/Decal/UB draw 3D into their own render targets too; only AC's
        // back buffer gets the markers and nameplates (measured 2026-09-30: without this they
        // showed up inside a Virindi window). Checked once per frame, only with Decal.
        if (DecalD3D9.Enabled && !ImGuiBackend.DX9Backend.IsRenderingToBackBuffer(dev)) return false;

        _markersRenderedThisFrame = true;
        _inRender = true;
        // This frame's camera, not the one EndScene captured last frame (see RefreshView).
        try { GameMatrixCapture.RefreshView(); }
        catch { }
        try
        {
            // At the UI pass AC has set a full-screen viewport; the markers project
            // with the 3D view's (captured at the last EndScene).
            ImGuiBackend.DX9Backend.RenderNav3D(dev, sceneViewport);
        }
        catch
        {
        }
        // The world overlays (nameplates, combat text) go here too, over the
        // markers and under AC's UI. UnderUiLayer presents a list at most once.
        try
        {
            if (sceneViewport) ImGuiBackend.UnderUiLayer.PresentAtUiPass(dev);
            else ImGuiBackend.UnderUiLayer.PresentAtTransition(dev);
        }
        catch
        {
        }
        _inRender = false;
        return true;
    }

    private static int Detour(IntPtr dev, uint primitiveType,
        int baseVertexIndex, uint minVertexIndex, uint numVertices,
        uint startIndex, uint primCount)
    {
        if (_inRender)
            return _originalDIP!(dev, primitiveType, baseVertexIndex,
                minVertexIndex, numVertices, startIndex, primCount);

        if (!UseTransition)
            return _originalDIP!(dev, primitiveType, baseVertexIndex,
                minVertexIndex, numVertices, startIndex, primCount);

        ImGuiBackend.DX9Backend.DeviceGetRenderState(dev, D3DRS_ZENABLE, out uint zEnable);

        if (zEnable != 0)
            _seen3D = true;

        // Detect 3D→UI transition: ZENABLE goes from 1→0 after 3D draws
        if (!_markersRenderedThisFrame && _seen3D && _lastZEnable != 0 && zEnable == 0)
        {
            if (PresentWorldOverlays(dev, sceneViewport: false))
                _atTransition++;
        }

        _lastZEnable = zEnable;

        return _originalDIP!(dev, primitiveType, baseVertexIndex,
            minVertexIndex, numVertices, startIndex, primCount);
    }

    /// <summary>The path the overlays use now, for status lines.</summary>
    public static string DescribePath() => Mode switch
    {
        WorldLayerMode.UiPass => AcUiPassHook.IsInstalled ? "AC UI pass (forced)" : "AC UI pass (forced, hook missing: EndScene)",
        WorldLayerMode.Transition => _hookInstalled ? "ZENABLE 1->0 transition (forced)" : "ZENABLE transition (forced, DIP hook missing: EndScene)",
        WorldLayerMode.EndScene => "EndScene (forced, over AC's UI)",
        _ => AcUiPassHook.IsInstalled ? "AC UI pass (auto)" : "ZENABLE 1->0 transition (auto: UI-pass hook not installed)",
    };

    /// <summary>One line for /rc worldlayer status and /rc imgui diag.</summary>
    public static string Describe() =>
        $"world overlays: {DescribePath()}; frames drawn at UI pass {_atUiPass}, at transition {_atTransition}, " +
        $"at EndScene {_atEndScene}; DIP hook {(_hookInstalled ? (_dipEnabled ? "on" : "off") : "not installed")}; " +
        $"UI-pass hook: {AcUiPassHook.Status}, {AcUiPassHook.Calls} calls";
}

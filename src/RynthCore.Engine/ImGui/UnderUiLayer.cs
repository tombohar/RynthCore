// ============================================================================
//  RynthCore.Engine - ImGui/UnderUiLayer.cs
//  ImGui geometry that belongs to the 3D world (RynthVision's nameplates, the
//  player plate and the combat text): drawn after AC's 3D scene and BEFORE
//  AC's own UI, so AC's chat, radar, inventory and other windows cover it the
//  way they cover the world. Everything else ImGui draws (the bar, the panels)
//  still renders at EndScene, over AC's UI.
//
//  How:
//    Build   inside the main ImGui frame (EndScene), MonsterHud paints into
//            one of two ImDrawLists owned here instead of ImGui's background
//            list. The lists are created once from the main context's shared
//            draw-list data and reset each frame, so their buffers are reused
//            (no per-frame allocation once they have grown).
//    Commit  at the end of EndScene the list just built becomes the one to
//            show during the NEXT frame; the other list is built into next.
//    Present the next frame, right after the Nav3D markers, when AC's 2D UI
//            pass begins (D3D9.AcUiPassHook -> Nav3DRenderInjector ->
//            PresentAtUiPass; after all of AC's 3D, alpha-sorted foliage
//            included). Without that hook, Nav3DRenderInjector's
//            DrawIndexedPrimitive hook spots ZENABLE going 1->0 instead
//            (PresentAtTransition; fires before the foliage outdoors). Either
//            way the list goes through DX9Backend with a full state save/restore.
//    Fallback a frame where neither fired (portal space, a loading screen, no
//            AC UI this frame) presents the list at EndScene instead, before
//            the ImGui panels - the same place RenderNav3D's fallback draws.
//            A list is presented at most once: no double draw.
//
//  The plates are therefore one frame behind the frame they were built in -
//  the same lag the Nav3D markers have, since both project with the camera
//  GameMatrixCapture took at the previous EndScene - on both paths, so a frame
//  that falls back doesn't jump.
//
//  Threads: AC's render thread only (EndScene, the UI-pass detour and the DIP
//  detour all run on it). Shutdown may run on the lifecycle thread while AC's
//  thread is still in a detour: it unpublishes the lists, waits for a present in flight to
//  finish, then frees them.
// ============================================================================

using System;
using System.Numerics;
using System.Threading;
using ImGuiNET;

namespace RynthCore.Engine.ImGuiBackend;

internal static unsafe class UnderUiLayer
{
    private static ImDrawList* _listA, _listB;
    private static ImDrawList* _building;   // being built this frame (null: nothing built)
    private static Vector2 _buildingSize;
    private static ImDrawList* _ready;      // to present this frame (built last frame)
    private static Vector2 _readySize;
    private static bool _presented;         // _ready already drawn this frame
    private static int _presenting;         // a present is in flight (Shutdown waits for it)
    private static volatile bool _shutDown;

    /// <summary>True after <see cref="BeginBuild"/> returned a list this frame. AC's render thread.</summary>
    public static bool IsBuilding => _building != null;

    /// <summary>Frames presented at AC's UI pass / at the transition / at the EndScene fallback (diagnostics).</summary>
    private static long _atUiPass, _atTransition, _atFallback;

    /// <summary>
    /// Inside the main ImGui frame, main context current: a cleared draw list to
    /// paint this frame's world overlays into, laid out for <paramref name="display"/>
    /// (ImGui display pixels). Null when the layer isn't available. AC's render thread.
    /// </summary>
    public static ImDrawListPtr BeginBuild(Vector2 display)
    {
        if (_shutDown) return new ImDrawListPtr(null);
        if (_listA == null)
        {
            IntPtr shared = ImGuiNET.ImGui.GetDrawListSharedData();
            if (shared == IntPtr.Zero) return new ImDrawListPtr(null);
            _listA = ImGuiNative.ImDrawList_ImDrawList(shared);
            _listB = ImGuiNative.ImDrawList_ImDrawList(shared);
        }
        // Never the list waiting to be shown (the fallback may still present it this frame).
        ImDrawList* list = _ready == _listA ? _listB : _listA;
        var ptr = new ImDrawListPtr(list);
        // What ImGui does for its own background list each frame: reset (keeps the
        // buffers), the frame's flags, the font atlas texture, a full-screen clip.
        ptr._ResetForNewFrame();
        ptr.PushTextureID(ImGuiNET.ImGui.GetIO().Fonts.TexID);
        ptr.PushClipRect(Vector2.Zero, display, false);
        _building = list;
        _buildingSize = display;
        return ptr;
    }

    /// <summary>
    /// Called by the DIP hook at AC's 3D->UI transition: draws last frame's world
    /// overlays under AC's UI. AC's render thread.
    /// </summary>
    public static void PresentAtTransition(IntPtr device)
    {
        if (Present(device)) _atTransition++;
    }

    /// <summary>
    /// Called by AcUiPassHook (through Nav3DRenderInjector) when AC's 2D UI pass begins:
    /// draws last frame's world overlays after all of AC's 3D (foliage included) and
    /// under AC's UI. RenderDrawList sets its own full-screen viewport. AC's render thread.
    /// </summary>
    public static void PresentAtUiPass(IntPtr device)
    {
        if (Present(device)) _atUiPass++;
    }

    /// <summary>
    /// EndScene, before the ImGui panels are submitted: when neither the UI pass nor
    /// the transition drew them this frame, the overlays are drawn here instead.
    /// AC's render thread.
    /// </summary>
    public static void PresentFallback(IntPtr device)
    {
        if (Present(device)) _atFallback++;
    }

    private static bool Present(IntPtr device)
    {
        if (_presented) return false;
        Interlocked.Exchange(ref _presenting, 1);
        try
        {
            ImDrawList* list = _ready;
            if (list == null || _shutDown) return false;
            _presented = true;
            if (list->VtxBuffer.Size == 0) return false;
            DX9Backend.RenderDrawList(list, _readySize, device);
            return true;
        }
        finally
        {
            Volatile.Write(ref _presenting, 0);
        }
    }

    /// <summary>
    /// End of EndScene (every frame, ImGui on or off): what was built this frame is
    /// presented next frame; nothing built means nothing to present. AC's render thread.
    /// </summary>
    public static void EndFrame()
    {
        _ready = _shutDown ? null : _building;
        _readySize = _buildingSize;
        _building = null;
        _presented = false;
    }

    /// <summary>One line for /rc imgui diag.</summary>
    public static string Describe() =>
        $"under-UI layer: presented at AC UI pass {_atUiPass}, at 3D->UI transition {_atTransition}, at EndScene (fallback) {_atFallback}";

    /// <summary>
    /// Engine shutdown, before DX9Backend and the ImGui context go. Any thread:
    /// unpublishes the lists, waits (briefly) for a present in flight, frees them.
    /// </summary>
    public static void Shutdown()
    {
        _shutDown = true;
        ImDrawList* a = _listA, b = _listB;
        _ready = null;
        _building = null;
        _listA = _listB = null;
        Thread.MemoryBarrier();
        var spin = new SpinWait();
        long deadline = Environment.TickCount64 + 200;
        while (Volatile.Read(ref _presenting) != 0 && Environment.TickCount64 < deadline)
            spin.SpinOnce();
        if (Volatile.Read(ref _presenting) != 0)
        {
            // Still drawing after 200 ms: leak the two lists rather than free them under it.
            RynthLog.Render("UnderUiLayer: a present was still running at shutdown; its lists are left allocated.");
            return;
        }
        if (a != null) ImGuiNative.ImDrawList_destroy(a);
        if (b != null) ImGuiNative.ImDrawList_destroy(b);
    }
}

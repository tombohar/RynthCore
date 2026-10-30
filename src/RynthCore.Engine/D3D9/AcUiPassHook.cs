// ============================================================================
//  RynthCore.Engine - D3D9/AcUiPassHook.cs
//  Hooks AC's RenderUI::RenderObjects, the call that draws AC's 2D UI, so the
//  world overlays (Nav3D markers, nameplates, combat text) draw right before
//  it: after ALL of AC's 3D (alpha-sorted foliage and effects included) and
//  under every AC window.
//
//  Why a function hook and not a D3D state signal (2026-10-04, plates drew
//  behind foliage): AC's frame is Client::UseTime -> SceneTool::StartFrame ->
//  SmartBox::Draw (the whole 3D scene) -> SceneTool::EndFrame, and EndFrame
//  sets a full-screen viewport, calls RenderUI::RenderObjects (each UI
//  element's surface, in z-order), KeyStone::Update, the debug HUDs, restores
//  the viewport and ends the scene. Inside SmartBox::Draw, ZENABLE goes 1->0
//  more than once before the UI: the outdoor sky pass after the landblocks
//  (LScape -> sky pass 1, depth mode ALWAYS/no-write, DrawObjCellForDummies)
//  draws with Z off, and only AFTER it does SmartBox::RenderNormalMode flush
//  the deferred alpha list (D3DPolyRender::FlushAlphaList(0)) - where the
//  alpha-sorted foliage and effects draw. The old "first ZENABLE 1->0" signal
//  fired at that sky pass, so the alpha list covered the plates. The entry of
//  RenderUI::RenderObjects is exactly "3D done, 2D UI begins", costs one call
//  per frame instead of work on every draw, and needs no prediction.
//  Evidence and addresses: ops/overnight/2026-10-04-plates-foliage.md (Aeshnidae).
//
//  The function takes no arguments and returns nothing (it loads ecx itself;
//  its caller doesn't use eax/ecx/edx after it), so a cdecl void() detour is
//  exact. AC's main thread only (the render thread). A frame where AC draws no
//  UI never calls it; the overlays then draw at EndScene (the existing
//  fallback), which is also "after all 3D".
//
//  Teardown: EngineLifecycle calls Uninstall before EndSceneHook.Uninstall, so
//  the detour stops entering the overlay code before UnderUiLayer/DX9Backend
//  are torn down (EndSceneHook.Uninstall then waits 80 ms for a call in flight).
//  The hook is only disabled, like every Compatibility hook at MH_DisableHook(ALL);
//  the next generation's HookCreate reclaims the target (reload recovery / the
//  loader facade).
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.D3D9;

internal static class AcUiPassHook
{
    // RenderUI::RenderObjects on the 4,841,472-byte client. Pattern is the source of truth.
    private const int RenderObjectsFallbackVa = 0x004488A0;

    // mov eax,[list count]; sub esp,8; push esi; xor esi,esi; test eax,eax;
    // mov byte [in-render flag],1; mov dword [..],0; mov ecx,imm; jbe +10;
    // mov eax,[list head]; test eax,eax; je +5; lea esi,[eax-8]; jmp +2; xor esi,esi;
    // test esi,esi; mov [esp+8],ecx; je +20. Unique in .text (checked 2026-10-04 on
    // both C:\Turbine and C:\Games\RynthCore\AcClient copies); absolute addresses wildcarded.
    private static readonly byte?[] RenderObjectsPattern =
    [
        0xA1, null, null, null, null,
        0x83, 0xEC, 0x08,
        0x56,
        0x33, 0xF6,
        0x85, 0xC0,
        0xC6, 0x05, null, null, null, null, 0x01,
        0xC7, 0x05, null, null, null, null, 0x00, 0x00, 0x00, 0x00,
        0xB9, null, null, null, null,
        0x76, 0x10,
        0xA1, null, null, null, null,
        0x85, 0xC0,
        0x74, 0x05,
        0x8D, 0x70, 0xF8,
        0xEB, 0x02,
        0x33, 0xF6,
        0x85, 0xF6,
        0x89, 0x4C, 0x24, 0x08,
        0x74, 0x20,
    ];

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RenderObjectsD();

    private static RenderObjectsD? _original;
    private static RenderObjectsD? _detour;
    private static IntPtr _target;
    private static long _calls;

    /// <summary>True once the hook is live. Read on AC's render thread every frame.</summary>
    public static volatile bool IsInstalled;

    public static string Status { get; private set; } = "not installed";

    /// <summary>AC UI passes seen since install (diagnostics).</summary>
    public static long Calls => Interlocked.Read(ref _calls);

    /// <summary>InitWorker step. Resolves and hooks RenderUI::RenderObjects. Idempotent.</summary>
    public static void Initialize()
    {
        if (IsInstalled) return;
        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
        {
            Status = "acclient.exe not available";
            return;
        }

        var r = HookResolver.Resolve(text, "AcUiPassHook.RenderUI::RenderObjects",
            RenderObjectsPattern, RenderObjectsFallbackVa);
        if (!r.Success)
        {
            Status = $"not resolved ({r.Detail}); overlays use the ZENABLE transition";
            RynthLog.Compat($"AcUiPassHook: {Status}");
            return;
        }

        try
        {
            _target = r.Address;
            _detour = Detour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_detour);
            _original = Marshal.GetDelegateForFunctionPointer<RenderObjectsD>(MinHook.HookCreate(_target, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(_target);
            IsInstalled = true;
            Status = $"hooked RenderUI::RenderObjects @ 0x{_target.ToInt32():X8} ({r.Detail})";
            RynthLog.Compat($"AcUiPassHook: {Status}");
        }
        catch (Exception ex)
        {
            Status = $"install failed: {ex.Message}";
            RynthLog.Compat($"AcUiPassHook: install threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Engine shutdown / hot reload (lifecycle thread), before the render-side teardown:
    /// stops the detour drawing at once, then disables the hook. Idempotent.
    /// </summary>
    public static void Uninstall()
    {
        if (!IsInstalled) return;
        IsInstalled = false;   // the detour checks this first: no overlay draw from here on
        Thread.MemoryBarrier();
        int status = MinHook.MH_DisableHook(_target);
        Status = $"uninstalled (disable = {MinHook.StatusString(status)})";
        RynthLog.Compat($"AcUiPassHook: {Status}");
    }

    private static void Detour()
    {
        Interlocked.Increment(ref _calls);
        if (IsInstalled && !EngineLifecycle.IsShuttingDown)
        {
            // Video patch "all": AC's 2D UI isn't drawn this frame (nor the world overlays,
            // which skip with the 3D scene). Off, or any problem: AC's UI pass as usual.
            bool skipUi = false;
            try { skipUi = VideoPatch.SkipAcUiThisFrame; }
            catch { }
            if (skipUi) return;
            try { Nav3DRenderInjector.OnAcUiPass(); }
            catch { /* never let an overlay problem take AC's UI pass down */ }
        }
        _original!();
    }
}

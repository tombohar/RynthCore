// ============================================================================
//  RynthCore.Engine - Compatibility/RadarHooks.cs
//
//  Hooks gmRadarUI::DrawObjects (blips) and gmRadarUI::DrawChildren (bezel,
//  compass tokens, coord readouts) to capture the singleton 'this' pointer
//  and, when SuppressOriginalDraw is set, blank the vanilla radar entirely
//  so a plugin can own that rect.
//
//  Exposes TryGetRadarRect, which asks UIElement::GetSurfaceBox for the
//  radar element's current screen rect.
//
//  All four target addresses are now resolved via HookResolver.Resolve:
//  pattern-scan first, hardcoded VA fallback, or UNAVAILABLE — the install
//  log line tells us exactly how each one resolved on the live binary.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static class RadarHooks
{
    // Fallback VAs (validated against the 4,841,472-byte client recorded in
    // docs/ACCLIENT_HOOK_INVENTORY.md). Only used if pattern-scan misses.
    private const int GmRadarUIDrawObjectsFallbackVa   = 0x004D9FE0;
    private const int GmRadarUIDrawChildrenFallbackVa  = 0x004DA380;
    private const int UIElementGetSurfaceBoxFallbackVa = 0x00462220;
    private const int UIElementSetVisibleFallbackVa    = 0x00462390;

    // gmRadarUI::DrawObjects(UISurface*) — large 0xDC stack frame, fld of
    // a Single field at +0x600, fmul against a global float in .data.
    private static readonly byte?[] DrawObjectsPattern =
    [
        0x81, 0xEC, 0xDC, 0x00, 0x00, 0x00, 0x53, 0x55,
        0x56, 0x8B, 0xF1, 0xD9, 0x86, 0x00, 0x06, 0x00,
        0x00, 0x8B, 0x86, 0x0C, 0x06, 0x00, 0x00,
        0xD8, 0x25, null, null, null, null,   // fmul dword ptr [imm32] — global float
        0x33, 0xDB, 0x3B, 0xC3
    ];

    // gmRadarUI::DrawChildren(Box2D&,Box2D&,SmartArray&,UISurface*)
    private static readonly byte?[] DrawChildrenPattern =
    [
        0x8B, 0x44, 0x24, 0x0C, 0x8B, 0x54, 0x24, 0x04,
        0x53, 0x55, 0x56, 0x8B, 0x74, 0x24, 0x1C, 0x57,
        0x56, 0x8B, 0xF9, 0x8B, 0x4C, 0x24, 0x1C, 0x50,
        0x51, 0x52, 0x8B, 0xCF,
        0xE8, null, null, null, null,         // call rel32
        0x56
    ];

    // UIElement::SetVisible(bool) — entry uses [global vftable cache] + virtual call.
    private static readonly byte?[] UIElementSetVisiblePattern =
    [
        0x51, 0x53, 0x56, 0x57,
        0x8B, 0x3D, null, null, null, null,   // mov edi, ds:[imm32]
        0x8B, 0xF1,
        0xE8, null, null, null, null,         // call rel32
        0x8B, 0x9E, 0xA4, 0x00, 0x00, 0x00,
        0x88, 0x44, 0x24, 0x0F
    ];

    // UIElement::GetSurfaceBox(Box2D*) — 'test byte ptr [ecx+0x556], 1' is
    // the unique fingerprint (UIElement-specific flags field).
    private static readonly byte?[] UIElementGetSurfaceBoxPattern =
    [
        0xF6, 0x81, 0x56, 0x05, 0x00, 0x00, 0x01,
        0x74, 0x11, 0x56, 0x8B, 0x74, 0x24, 0x08, 0x56,
        0xE8, null, null, null, null,         // call rel32
        0x8B, 0xC6, 0x5E, 0xC2, 0x04, 0x00
    ];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void GmRadarUIDrawObjectsDelegate(IntPtr thisPtr, IntPtr uiSurface);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void GmRadarUIDrawChildrenDelegate(IntPtr thisPtr, IntPtr clipRect, IntPtr clipInside, IntPtr boxArray, IntPtr uiSurface);

    private static GmRadarUIDrawObjectsDelegate?  _originalDrawObjects;
    private static GmRadarUIDrawObjectsDelegate?  _drawObjectsDetour;   // held alive to prevent GC
    private static GmRadarUIDrawChildrenDelegate? _originalDrawChildren;
    private static GmRadarUIDrawChildrenDelegate? _drawChildrenDetour;

    private static IntPtr _gmRadarUIInstance;
    private static IntPtr _uiElementSetVisibleAddress;
    private static IntPtr _uiElementGetSurfaceBoxAddress;
    private static bool _hookInstalled;
    private static string _statusMessage = "Not initialized.";

    public static bool IsInstalled => _hookInstalled;
    public static string StatusMessage => _statusMessage;

    private static bool _suppressOriginalDraw;
    public static bool SuppressOriginalDraw
    {
        get => _suppressOriginalDraw;
        set
        {
            if (_suppressOriginalDraw == value) return;
            _suppressOriginalDraw = value;
            RynthLog.Info($"RadarHooks: SuppressOriginalDraw -> {value}. _gmRadarUIInstance=0x{_gmRadarUIInstance.ToInt32():X8}, drawObjectsHits={_drawObjectsHits}, drawChildrenHits={_drawChildrenHits}.");
        }
    }

    private static int _drawObjectsHits;
    private static bool _isHiddenAsserted;
    private static int _drawChildrenHits;

    /// <summary>
    /// The captured gmRadarUI singleton. Zero until the radar has rendered at
    /// least once this session (usually within the first frame after login).
    /// </summary>
    public static IntPtr GmRadarUIInstance => _gmRadarUIInstance;

    public static void Initialize()
    {
        if (_hookInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        // Resolve the two helper function pointers used by TryGetRadarRect /
        // any future SetVisible call. These are direct-call sites, not hooks,
        // but if their VAs are wrong we'd AV inside acclient.exe just like a
        // bad hook would — so resolve them through the same gate.
        var setVisible = HookResolver.Resolve(textSection, "RadarHooks.UIElement::SetVisible",
            UIElementSetVisiblePattern, UIElementSetVisibleFallbackVa);
        if (setVisible.Success) _uiElementSetVisibleAddress = setVisible.Address;

        var getSurfaceBox = HookResolver.Resolve(textSection, "RadarHooks.UIElement::GetSurfaceBox",
            UIElementGetSurfaceBoxPattern, UIElementGetSurfaceBoxFallbackVa);
        if (getSurfaceBox.Success) _uiElementGetSurfaceBoxAddress = getSurfaceBox.Address;

        bool drawObjectsOk = TryInstallDrawObjectsHook(textSection);
        bool drawChildrenOk = TryInstallDrawChildrenHook(textSection);

        if (drawObjectsOk && drawChildrenOk)
        {
            _hookInstalled = true;
            _statusMessage = "Hooked gmRadarUI::DrawObjects + DrawChildren.";
            RynthLog.Info($"RadarHooks: {_statusMessage}");
        }
        else
        {
            _statusMessage = $"Partial install — DrawObjects={drawObjectsOk}, DrawChildren={drawChildrenOk}.";
            RynthLog.Compat($"RadarHooks: {_statusMessage}");
        }
    }

    private static bool TryInstallDrawObjectsHook(AcClientTextSection textSection)
    {
        var resolved = HookResolver.Resolve(textSection, "RadarHooks.DrawObjects",
            DrawObjectsPattern, GmRadarUIDrawObjectsFallbackVa);
        if (!resolved.Success)
            return false;

        try
        {
            _drawObjectsDetour = DrawObjectsDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_drawObjectsDetour);
            _originalDrawObjects = Marshal.GetDelegateForFunctionPointer<GmRadarUIDrawObjectsDelegate>(
                MinHook.HookCreate(resolved.Address, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(resolved.Address);
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"RadarHooks: DrawObjects hook install threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryInstallDrawChildrenHook(AcClientTextSection textSection)
    {
        var resolved = HookResolver.Resolve(textSection, "RadarHooks.DrawChildren",
            DrawChildrenPattern, GmRadarUIDrawChildrenFallbackVa);
        if (!resolved.Success)
            return false;

        try
        {
            _drawChildrenDetour = DrawChildrenDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_drawChildrenDetour);
            _originalDrawChildren = Marshal.GetDelegateForFunctionPointer<GmRadarUIDrawChildrenDelegate>(
                MinHook.HookCreate(resolved.Address, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(resolved.Address);
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"RadarHooks: DrawChildren hook install threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void DrawObjectsDetour(IntPtr thisPtr, IntPtr uiSurface)
    {
        RecursionGuard.Tick("RadarHooks.DrawObjects");
        if (thisPtr != IntPtr.Zero)
            _gmRadarUIInstance = thisPtr;

        int hit = Interlocked.Increment(ref _drawObjectsHits);
        if (hit <= 3)
            RynthLog.Info($"RadarHooks: DrawObjects detour fired #{hit} this=0x{thisPtr.ToInt32():X8} suppress={_suppressOriginalDraw}.");

        // Always call original — see SuppressOriginalDraw comment. Hiding is
        // done via TickHide / SetVisible(false) per frame, NOT by skipping
        // the draw. Skipping leaves AC's downstream UI state inconsistent
        // (the SmartArray& boxArray output param, etc.) and crashes the
        // client when the next UI traversal (e.g. PopOutPanel) walks it.
        try
        {
            _originalDrawObjects!(thisPtr, uiSurface);
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"RadarHooks: DrawObjects original threw {ex.GetType().Name}: {ex.Message}"); } catch { }
            throw;
        }
    }

    private static void DrawChildrenDetour(IntPtr thisPtr, IntPtr clipRect, IntPtr clipInside, IntPtr boxArray, IntPtr uiSurface)
    {
        RecursionGuard.Tick("RadarHooks.DrawChildren");
        if (thisPtr != IntPtr.Zero)
            _gmRadarUIInstance = thisPtr;

        int hit = Interlocked.Increment(ref _drawChildrenHits);
        if (hit <= 3)
            RynthLog.Info($"RadarHooks: DrawChildren detour fired #{hit} this=0x{thisPtr.ToInt32():X8} suppress={_suppressOriginalDraw}.");

        try
        {
            _originalDrawChildren!(thisPtr, clipRect, clipInside, boxArray, uiSurface);
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"RadarHooks: DrawChildren original threw {ex.GetType().Name}: {ex.Message}"); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Per-frame visibility assertion — called from EndSceneHook every frame.
    /// While <see cref="SuppressOriginalDraw"/> is on, calls SetVisible(false)
    /// on the captured radar singleton each frame so AC can't sneak the
    /// radar back on. When the flag flips off, calls SetVisible(true) once
    /// to restore. Mirrors the same pattern <see cref="ChatHooks.TickHide"/>
    /// uses for the retail chatbox. Confirmed working in production.
    /// </summary>
    public static unsafe void TickHide()
    {
        // Don't touch the singleton between in-world sessions — AC frees the
        // UIElement during the logout-to-charselect transition and a stale
        // pointer dereference inside UIElement::IsVisible AVs the client.
        if (!LoginLifecycleHooks.HasObservedLoginComplete)
            return;

        IntPtr inst = _gmRadarUIInstance;
        IntPtr setVisible = _uiElementSetVisibleAddress;
        if (inst == IntPtr.Zero || setVisible == IntPtr.Zero)
            return;

        if (_suppressOriginalDraw)
        {
            try
            {
                ((delegate* unmanaged[Thiscall]<IntPtr, int, void>)setVisible)(inst, 0);
            }
            catch { /* best-effort */ }
            _isHiddenAsserted = true;
            return;
        }

        if (_isHiddenAsserted)
        {
            try
            {
                ((delegate* unmanaged[Thiscall]<IntPtr, int, void>)setVisible)(inst, 1);
            }
            catch { /* best-effort */ }
            _isHiddenAsserted = false;
        }
    }

    public static void ResetCachedInstance()
    {
        _gmRadarUIInstance = IntPtr.Zero;
        _isHiddenAsserted = false;
    }

    /// <summary>
    /// Returns the retail radar element's current screen rect (x0,y0,x1,y1 in
    /// pixels, exclusive on x1/y1). Calls UIElement::GetSurfaceBox on the
    /// captured singleton. Returns false until the radar has rendered at least
    /// once, or if the rect looks invalid.
    /// </summary>
    public static unsafe bool TryGetRadarRect(out int x0, out int y0, out int x1, out int y1)
    {
        x0 = y0 = x1 = y1 = 0;
        IntPtr inst = _gmRadarUIInstance;
        if (inst == IntPtr.Zero)
            return false;
        if (_uiElementGetSurfaceBoxAddress == IntPtr.Zero)
            return false;

        // Box2D is four int32s laid out (x0, y0, x1, y1). GetSurfaceBox follows
        // MSVC's "return struct by value" ABI: caller passes the output slot as
        // the first stack argument; the callee fills it and returns the same ptr.
        int* box = stackalloc int[4];
        box[0] = box[1] = box[2] = box[3] = 0;

        try
        {
            ((delegate* unmanaged[Thiscall]<IntPtr, int*, int*>)_uiElementGetSurfaceBoxAddress)(inst, box);
        }
        catch
        {
            return false;
        }

        int bx0 = box[0], by0 = box[1], bx1 = box[2], by1 = box[3];
        if (bx1 <= bx0 || by1 <= by0 || bx1 > 10000 || by1 > 10000 || bx0 < -1000 || by0 < -1000)
            return false;

        x0 = bx0; y0 = by0; x1 = bx1; y1 = by1;
        return true;
    }
}

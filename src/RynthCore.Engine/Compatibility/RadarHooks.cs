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
//  VA derivation (map offset + 0x00401000 = live VA):
//    000D8FE0 gmRadarUI::DrawObjects(UISurface*)                       → 0x004D9FE0
//    000D9380 gmRadarUI::DrawChildren(Box2D&,Box2D&,SmartArray&,UISurface*) → 0x004DA380
//    00061220 UIElement::GetSurfaceBox(Box2D*)                         → 0x00462220
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static class RadarHooks
{
    private const int GmRadarUIDrawObjectsVa   = 0x004D9FE0;
    private const int GmRadarUIDrawChildrenVa  = 0x004DA380;
    private const int UIElementGetSurfaceBoxVa = 0x00462220;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void GmRadarUIDrawObjectsDelegate(IntPtr thisPtr, IntPtr uiSurface);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void GmRadarUIDrawChildrenDelegate(IntPtr thisPtr, IntPtr clipRect, IntPtr clipInside, IntPtr boxArray, IntPtr uiSurface);

    private static GmRadarUIDrawObjectsDelegate?  _originalDrawObjects;
    private static GmRadarUIDrawObjectsDelegate?  _drawObjectsDetour;   // held alive to prevent GC
    private static GmRadarUIDrawChildrenDelegate? _originalDrawChildren;
    private static GmRadarUIDrawChildrenDelegate? _drawChildrenDetour;

    private static IntPtr _gmRadarUIInstance;
    private static bool _hookInstalled;
    private static string _statusMessage = "Not initialized.";

    public static bool IsInstalled => _hookInstalled;
    public static string StatusMessage => _statusMessage;

    /// <summary>
    /// When true, the detours skip the original gmRadarUI draw calls, blanking
    /// the vanilla radar so a custom renderer can own that rect. The UIElement
    /// is still captured on entry, so GetSurfaceBox keeps working.
    /// </summary>
    public static bool SuppressOriginalDraw;

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

        bool drawObjectsOk = TryInstallDrawObjectsHook(textSection);
        bool drawChildrenOk = TryInstallDrawChildrenHook(textSection);

        if (drawObjectsOk && drawChildrenOk)
        {
            _hookInstalled = true;
            _statusMessage = "Hooked gmRadarUI::DrawObjects + DrawChildren.";
            RynthLog.Verbose("Compat: radar hooks ready (DrawObjects + DrawChildren).");
        }
        else
        {
            _statusMessage = $"Partial install — DrawObjects={drawObjectsOk}, DrawChildren={drawChildrenOk}.";
            RynthLog.Compat($"Compat: radar hook {_statusMessage}");
        }
    }

    private static bool TryInstallDrawObjectsHook(AcClientTextSection textSection)
    {
        int funcOff = GmRadarUIDrawObjectsVa - textSection.TextBaseVa;
        if (funcOff < 0 || funcOff >= textSection.Bytes.Length)
        {
            RynthLog.Compat($"Compat: DrawObjects VA out of range @ 0x{GmRadarUIDrawObjectsVa:X8}.");
            return false;
        }

        byte firstByte = textSection.Bytes[funcOff];
        if (firstByte is 0x00 or 0xCC or 0xC3)
        {
            RynthLog.Compat($"Compat: DrawObjects looks invalid @ 0x{GmRadarUIDrawObjectsVa:X8} (opcode 0x{firstByte:X2}).");
            return false;
        }

        try
        {
            IntPtr targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _drawObjectsDetour = DrawObjectsDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_drawObjectsDetour);
            _originalDrawObjects = Marshal.GetDelegateForFunctionPointer<GmRadarUIDrawObjectsDelegate>(
                MinHook.HookCreate(targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(targetAddress);
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: DrawObjects hook failed - {ex.Message}");
            return false;
        }
    }

    private static bool TryInstallDrawChildrenHook(AcClientTextSection textSection)
    {
        int funcOff = GmRadarUIDrawChildrenVa - textSection.TextBaseVa;
        if (funcOff < 0 || funcOff >= textSection.Bytes.Length)
        {
            RynthLog.Compat($"Compat: DrawChildren VA out of range @ 0x{GmRadarUIDrawChildrenVa:X8}.");
            return false;
        }

        byte firstByte = textSection.Bytes[funcOff];
        if (firstByte is 0x00 or 0xCC or 0xC3)
        {
            RynthLog.Compat($"Compat: DrawChildren looks invalid @ 0x{GmRadarUIDrawChildrenVa:X8} (opcode 0x{firstByte:X2}).");
            return false;
        }

        try
        {
            IntPtr targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _drawChildrenDetour = DrawChildrenDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_drawChildrenDetour);
            _originalDrawChildren = Marshal.GetDelegateForFunctionPointer<GmRadarUIDrawChildrenDelegate>(
                MinHook.HookCreate(targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(targetAddress);
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: DrawChildren hook failed - {ex.Message}");
            return false;
        }
    }

    private static void DrawObjectsDetour(IntPtr thisPtr, IntPtr uiSurface)
    {
        if (thisPtr != IntPtr.Zero)
            _gmRadarUIInstance = thisPtr;

        if (SuppressOriginalDraw)
            return;

        _originalDrawObjects!(thisPtr, uiSurface);
    }

    private static void DrawChildrenDetour(IntPtr thisPtr, IntPtr clipRect, IntPtr clipInside, IntPtr boxArray, IntPtr uiSurface)
    {
        if (thisPtr != IntPtr.Zero)
            _gmRadarUIInstance = thisPtr;

        if (SuppressOriginalDraw)
            return;

        _originalDrawChildren!(thisPtr, clipRect, clipInside, boxArray, uiSurface);
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

        // Box2D is four int32s laid out (x0, y0, x1, y1). GetSurfaceBox follows
        // MSVC's "return struct by value" ABI: caller passes the output slot as
        // the first stack argument; the callee fills it and returns the same ptr.
        int* box = stackalloc int[4];
        box[0] = box[1] = box[2] = box[3] = 0;

        try
        {
            ((delegate* unmanaged[Thiscall]<IntPtr, int*, int*>)UIElementGetSurfaceBoxVa)(inst, box);
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

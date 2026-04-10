// ═══════════════════════════════════════════════════════════════════════════
//  RynthCore.Engine — D3D9/Nav3DRenderInjector.cs
//  Hooks DrawIndexedPrimitive to inject nav marker rendering at the
//  3D→UI boundary. When ZENABLE transitions from 1→0, renders markers
//  so they appear after the 3D world but before the game's UI pass.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.D3D9;

internal static class Nav3DRenderInjector
{
    private const uint D3DRS_ZENABLE = 7;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DrawIndexedPrimitiveD(IntPtr dev, uint primitiveType,
        int baseVertexIndex, uint minVertexIndex, uint numVertices,
        uint startIndex, uint primCount);

    private static DrawIndexedPrimitiveD? _originalDIP;
    private static DrawIndexedPrimitiveD? _hookDelegate;
    private static bool _hookInstalled;
    private static bool _inRender;

    // Per-frame transition detection
    private static uint _lastZEnable;
    private static bool _markersRenderedThisFrame;
    private static bool _seen3D;

    public static bool RenderedThisFrame => _markersRenderedThisFrame;

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
            IntPtr vtable = Marshal.ReadIntPtr(pDevice);
            IntPtr addr = Marshal.ReadIntPtr(vtable, DeviceVTableIndex.DrawIndexedPrimitive * IntPtr.Size);

            _hookDelegate = new DrawIndexedPrimitiveD(Detour);
            IntPtr hookPtr = Marshal.GetFunctionPointerForDelegate(_hookDelegate);
            _originalDIP = Marshal.GetDelegateForFunctionPointer<DrawIndexedPrimitiveD>(
                MinHook.HookCreate(addr, hookPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(addr);
            _hookInstalled = true;

            RynthLog.D3D9("Nav3DRenderInjector: DrawIndexedPrimitive hook installed.");
        }
        catch (Exception ex)
        {
            RynthLog.D3D9($"Nav3DRenderInjector: Hook install FAILED — {ex.Message}");
        }
    }

    private static int Detour(IntPtr dev, uint primitiveType,
        int baseVertexIndex, uint minVertexIndex, uint numVertices,
        uint startIndex, uint primCount)
    {
        if (_inRender)
            return _originalDIP!(dev, primitiveType, baseVertexIndex,
                minVertexIndex, numVertices, startIndex, primCount);

        ImGuiBackend.DX9Backend.DeviceGetRenderState(dev, D3DRS_ZENABLE, out uint zEnable);

        if (zEnable != 0)
            _seen3D = true;

        // Detect 3D→UI transition: ZENABLE goes from 1→0 after 3D draws
        if (!_markersRenderedThisFrame && _seen3D && _lastZEnable != 0 && zEnable == 0)
        {
            _markersRenderedThisFrame = true;
            _inRender = true;
            try
            {
                ImGuiBackend.DX9Backend.RenderNav3D(dev);
            }
            catch
            {
            }
            _inRender = false;
        }

        _lastZEnable = zEnable;

        return _originalDIP!(dev, primitiveType, baseVertexIndex,
            minVertexIndex, numVertices, startIndex, primCount);
    }
}

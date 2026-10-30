// ============================================================================
//  RynthCore.Engine - ImGui/PopOutSurface.cs
//  The D3D9 side of one popped-out panel (docs/IMGUI_PARITY_PLAN.md §4.2):
//  its ImGui draw data is rendered into an off-screen render target, read back
//  into system memory and handed to the panel's LayeredWindow
//  (UpdateLayeredWindow, per-pixel alpha).
//
//  Two render targets alternate: each frame renders into one and reads back
//  the other, which the GPU finished a frame ago, so the readback never waits
//  on the GPU (a one-frame delay, ~33 ms at 30 Hz). The first frame after a
//  (re)size reads its own target once.
//
//  A picture can be larger than the panel: a tooltip or popup reaching past it
//  is drawn in a margin around it (ImGuiPopOuts). Each target remembers where
//  the panel sits in its picture, so the late readback is shown with the
//  margins it was drawn with (LayeredWindow.Present keeps the panel in place).
//
//  The targets are cleared to transparent black; DX9Backend blends colour with
//  SRCALPHA/INVSRCALPHA and alpha with ONE/INVSRCALPHA, which leaves exactly
//  the premultiplied BGRA that UpdateLayeredWindow wants.
//
//  AC's render thread only (inside EndScene). All resources are released by
//  Dispose; nothing is static.
// ============================================================================

using System;
using ImGuiNET;
using RynthCore.Engine.D3D9;
using RynthCore.Engine.UI;

namespace RynthCore.Engine.ImGuiBackend;

internal sealed unsafe class PopOutSurface : IDisposable
{
    private const uint D3DFMT_A8R8G8B8 = 21;
    private const uint D3DPOOL_SYSTEMMEM = 2;
    private const uint D3DCLEAR_TARGET = 1;
    private const uint D3DLOCK_READONLY = 0x10;

    // IDirect3DDevice9 slots not in DeviceVTableIndex.
    private const int CreateRenderTargetSlot = 28;
    private const int GetRenderTargetDataSlot = 32;
    private const int CreateOffscreenPlainSurfaceSlot = 36;
    private const int SetDepthStencilSurfaceSlot = 39;
    private const int GetDepthStencilSurfaceSlot = 40;
    // IDirect3DSurface9
    private const int SurfaceLockRectSlot = 13;
    private const int SurfaceUnlockRectSlot = 14;

    private struct Viewport { public uint X, Y, Width, Height; public float MinZ, MaxZ; }
    private struct LockedRect { public int Pitch; public IntPtr Bits; }

    /// <summary>Where the panel sits in a picture: its offset and size (px).</summary>
    public readonly record struct Placement(int InsetLeft, int InsetTop, int ContentWidth, int ContentHeight);

    private readonly IntPtr[] _targets = new IntPtr[2];
    private readonly Placement[] _placements = new Placement[2];
    private IntPtr _readback;
    private int _width, _height;
    private int _current;
    private bool _previousValid;
    private int _failures;

    public int Width => _width;
    public int Height => _height;

    /// <summary>Diagnostics (/rc ui snap): the next frame pushed is also written here as a 32-bit BMP.</summary>
    public string? SnapshotPath;

    /// <summary>
    /// Renders <paramref name="drawData"/> (sized <paramref name="width"/> ×
    /// <paramref name="height"/>, the panel placed in it at <paramref name="placement"/>)
    /// and pushes the latest finished frame to <paramref name="window"/>. False when
    /// the device refused (logged, retried next frame).
    /// </summary>
    public bool Render(IntPtr device, ImDrawDataPtr drawData, int width, int height, Placement placement, LayeredWindow window)
    {
        if (device == IntPtr.Zero || width <= 0 || height <= 0) return false;
        if ((width != _width || height != _height || _targets[0] == IntPtr.Zero) && !Create(device, width, height))
            return false;

        void** vt = *(void***)device;
        IntPtr oldTarget = IntPtr.Zero, oldDepth = IntPtr.Zero;
        Viewport oldViewport;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vt[DeviceVTableIndex.GetRenderTarget])(device, 0, &oldTarget);
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vt[GetDepthStencilSurfaceSlot])(device, &oldDepth);
        ((delegate* unmanaged[Stdcall]<IntPtr, Viewport*, int>)vt[DeviceVTableIndex.GetViewport])(device, &oldViewport);
        try
        {
            IntPtr target = _targets[_current];
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)vt[DeviceVTableIndex.SetRenderTarget])(device, 0, target) < 0)
                return Fail("SetRenderTarget");
            // Our target can be larger than AC's depth buffer (a pop-out bigger than
            // the game window); nothing here uses depth, so unbind it.
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vt[SetDepthStencilSurfaceSlot])(device, IntPtr.Zero);
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, uint, float, uint, int>)vt[DeviceVTableIndex.Clear])(
                device, 0, IntPtr.Zero, D3DCLEAR_TARGET, 0x00000000, 1f, 0);
            DX9Backend.RenderDrawData(drawData, device);
            _placements[_current] = placement;
        }
        finally
        {
            // SetRenderTarget resets the viewport to the whole target: put AC's back.
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)vt[DeviceVTableIndex.SetRenderTarget])(device, 0, oldTarget);
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vt[SetDepthStencilSurfaceSlot])(device, oldDepth);
            ((delegate* unmanaged[Stdcall]<IntPtr, Viewport*, int>)vt[DeviceVTableIndex.SetViewport])(device, &oldViewport);
            ReleaseCom(oldTarget);
            ReleaseCom(oldDepth);
        }

        // Read back the target the GPU has finished: last frame's, or this one on the first frame.
        int read = _previousValid ? 1 - _current : _current;
        bool pushed = Push(device, vt, read, window);
        _unpushed = read != _current;   // this frame's picture is still to be shown
        _previousValid = true;
        _current = 1 - _current;
        return pushed;
    }

    private bool _unpushed;

    /// <summary>
    /// Shows the last rendered frame if it hasn't been shown yet (the readback
    /// runs a frame behind): called on a frame that was skipped because nothing
    /// changed. Cheap no-op otherwise.
    /// </summary>
    public void Flush(IntPtr device, LayeredWindow window)
    {
        if (!_unpushed || device == IntPtr.Zero || _targets[0] == IntPtr.Zero) return;
        _unpushed = false;
        Push(device, *(void***)device, 1 - _current, window);
    }

    private bool Push(IntPtr device, void** vt, int index, LayeredWindow window)
    {
        IntPtr target = _targets[index];
        Placement at = _placements[index];
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)vt[GetRenderTargetDataSlot])(device, target, _readback) < 0)
            return Fail("GetRenderTargetData");
        void** svt = *(void***)_readback;
        LockedRect locked;
        if (((delegate* unmanaged[Stdcall]<IntPtr, LockedRect*, IntPtr, uint, int>)svt[SurfaceLockRectSlot])(_readback, &locked, IntPtr.Zero, D3DLOCK_READONLY) < 0)
            return Fail("LockRect");
        try
        {
            window.Present(locked.Bits, locked.Pitch, _width, _height, at.InsetLeft, at.InsetTop, at.ContentWidth, at.ContentHeight);
            if (SnapshotPath != null)
            {
                string path = SnapshotPath;
                SnapshotPath = null;
                WriteBmp(path, (byte*)locked.Bits, _width, _height, locked.Pitch);
            }
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)svt[SurfaceUnlockRectSlot])(_readback);
        }
        _failures = 0;
        return true;
    }

    private static void WriteBmp(string path, byte* bits, int width, int height, int pitch)
    {
        try
        {
            int rowBytes = width * 4, imageBytes = rowBytes * height;
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
            using var bw = new System.IO.BinaryWriter(fs);
            bw.Write((ushort)0x4D42); bw.Write(54 + imageBytes); bw.Write(0); bw.Write(54);
            bw.Write(40); bw.Write(width); bw.Write(-height); bw.Write((ushort)1); bw.Write((ushort)32);
            bw.Write(0); bw.Write(imageBytes); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
            for (int y = 0; y < height; y++)
                bw.Write(new ReadOnlySpan<byte>(bits + (long)y * pitch, rowBytes));
            RynthLog.UI($"PopOutSurface: snapshot {width}x{height} written to {path}.");
        }
        catch (Exception ex) { RynthLog.UI($"PopOutSurface: snapshot failed: {ex.Message}"); }
    }

    private bool Create(IntPtr device, int width, int height)
    {
        Release();
        void** vt = *(void***)device;
        for (int i = 0; i < 2; i++)
        {
            IntPtr surface = IntPtr.Zero;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, int, IntPtr*, IntPtr, int>)vt[CreateRenderTargetSlot])(
                device, (uint)width, (uint)height, D3DFMT_A8R8G8B8, 0, 0, 0, &surface, IntPtr.Zero);
            if (hr < 0 || surface == IntPtr.Zero) { Release(); return Fail($"CreateRenderTarget {width}x{height} hr=0x{hr:X8}"); }
            _targets[i] = surface;
        }
        IntPtr readback = IntPtr.Zero;
        int rhr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, IntPtr*, IntPtr, int>)vt[CreateOffscreenPlainSurfaceSlot])(
            device, (uint)width, (uint)height, D3DFMT_A8R8G8B8, D3DPOOL_SYSTEMMEM, &readback, IntPtr.Zero);
        if (rhr < 0 || readback == IntPtr.Zero) { Release(); return Fail($"CreateOffscreenPlainSurface {width}x{height} hr=0x{rhr:X8}"); }
        _readback = readback;
        _width = width;
        _height = height;
        _current = 0;
        _previousValid = false;
        return true;
    }

    private bool Fail(string what)
    {
        if (_failures++ < 5)
            RynthLog.Render($"PopOutSurface: {what} failed; the pop-out keeps its last picture.");
        return false;
    }

    private void Release()
    {
        for (int i = 0; i < 2; i++) { ReleaseCom(_targets[i]); _targets[i] = IntPtr.Zero; }
        ReleaseCom(_readback);
        _readback = IntPtr.Zero;
        _width = _height = 0;
        _previousValid = false;
    }

    private static void ReleaseCom(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        void** vt = *(void***)obj;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)vt[DeviceVTableIndex.Release])(obj);
    }

    public void Dispose() => Release();
}

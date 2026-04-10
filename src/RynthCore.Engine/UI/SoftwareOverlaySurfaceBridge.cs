using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.UI;

internal sealed class SoftwareOverlaySurfaceBridge : IOverlaySurfaceBridge
{
    private sealed class PendingFrame
    {
        public PendingFrame(byte[] pixels, int width, int height)
        {
            Frame = new OverlaySurfaceFrame(pixels, width, height);
        }

        public OverlaySurfaceFrame Frame { get; }
    }

    private PendingFrame? _pending;

    public string Name => "software-bgra32";
    public bool SupportsGpuInterop => false;
    public bool ShouldAttemptSharedTextures => false;

    public void SubmitSoftwareFrame(IntPtr pixelData, int byteCount, int width, int height)
    {
        var buffer = new byte[byteCount];
        Marshal.Copy(pixelData, buffer, 0, byteCount);
        Interlocked.Exchange(ref _pending, new PendingFrame(buffer, width, height));
    }

    public void SubmitSharedTexture(OverlaySurfaceKind kind, OverlaySharedTextureDescriptor descriptor)
    {
        RynthLog.UI($"SoftwareOverlaySurfaceBridge: Ignoring {kind} shared texture submission while software bridge is active.");
    }

    public void NotifySharedTextureOpened(IntPtr sharedHandle)
    {
    }

    public void NotifySharedTextureUnavailable()
    {
    }

    public bool TryConsume(out OverlaySurfaceFrame? frame)
    {
        PendingFrame? pending = Interlocked.Exchange(ref _pending, null);
        if (pending == null)
        {
            frame = null;
            return false;
        }

        frame = pending.Frame;
        return true;
    }
}

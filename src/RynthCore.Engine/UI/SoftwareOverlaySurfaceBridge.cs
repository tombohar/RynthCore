using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.UI;

internal sealed class SoftwareOverlaySurfaceBridge : IOverlaySurfaceBridge
{
    // Reuses a single byte[] across frames. Eliminates ~350 MB/sec LOH churn at 1600x900@60fps,
    // which was hammering the GC and (suspected) exposing/triggering an LFH heap corruption AV
    // in ntdll. The producer's Marshal.Copy and the consumer's MemoryCopy can run concurrently
    // on the same buffer at worst (visual tearing during fast frames), which is harmless: byte[]
    // is GC-tracked so the buffer can't be freed underneath the consumer.
    private byte[]? _buffer;
    private int _pendingByteCount;
    private int _pendingWidth;
    private int _pendingHeight;
    private int _hasPending;
    private readonly object _sync = new();
    // Single reusable frame instance — TryConsume mutates it via ResetSoftware.
    // Avoids ~45,000 small-heap allocs over a 20-min session that fed LFH churn.
    private readonly OverlaySurfaceFrame _frame = new(Array.Empty<byte>(), 0, 0);

    public string Name => "software-bgra32";
    public bool SupportsGpuInterop => false;
    public bool ShouldAttemptSharedTextures => false;

    public void SubmitSoftwareFrame(IntPtr pixelData, int byteCount, int width, int height)
    {
        lock (_sync)
        {
            if (_buffer == null || _buffer.Length < byteCount)
                _buffer = new byte[byteCount];
            Marshal.Copy(pixelData, _buffer, 0, byteCount);
            _pendingByteCount = byteCount;
            _pendingWidth = width;
            _pendingHeight = height;
        }
        Interlocked.Exchange(ref _hasPending, 1);
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
        if (Interlocked.Exchange(ref _hasPending, 0) == 0)
        {
            frame = null;
            return false;
        }

        byte[]? buffer;
        int width, height;
        lock (_sync)
        {
            buffer = _buffer;
            width = _pendingWidth;
            height = _pendingHeight;
        }

        if (buffer == null)
        {
            frame = null;
            return false;
        }

        _frame.ResetSoftware(buffer, width, height);
        frame = _frame;
        return true;
    }
}

using System;
using System.Threading;

namespace RynthCore.Engine.UI;

internal sealed class AngleOverlaySurfaceBridge : IOverlaySurfaceBridge
{
    private readonly object _sync = new();
    private readonly SoftwareOverlaySurfaceBridge _softwareFallback = new();
    private OverlaySurfaceFrame? _pendingSharedFrame;
    private IntPtr _activeSharedHandle;
    private IntPtr _lastProbedSharedHandle;
    private bool _sharedInteropUnavailable;
    private int _loggedSoftwareFallback;
    private int _loggedSharedTexture;
    private int _loggedSharedUnavailable;

    public string Name => "angle-preferred-hybrid";
    public bool SupportsGpuInterop => true;
    public bool ShouldAttemptSharedTextures => !_sharedInteropUnavailable;

    public void SubmitSoftwareFrame(IntPtr pixelData, int byteCount, int width, int height)
    {
        if (Interlocked.Exchange(ref _loggedSoftwareFallback, 1) == 0)
        {
            RynthLog.UI("AngleOverlaySurfaceBridge: Using software frames as a fallback until native shared textures are available.");
        }

        _softwareFallback.SubmitSoftwareFrame(pixelData, byteCount, width, height);
    }

    public void SubmitSharedTexture(OverlaySurfaceKind kind, OverlaySharedTextureDescriptor descriptor)
    {
        if (_sharedInteropUnavailable)
            return;

        if (Interlocked.Exchange(ref _loggedSharedTexture, 1) == 0)
        {
            RynthLog.UI($"AngleOverlaySurfaceBridge: Received first {kind} shared texture submission.");
        }

        lock (_sync)
        {
            if (descriptor.SharedHandle != IntPtr.Zero &&
                _activeSharedHandle != IntPtr.Zero &&
                descriptor.SharedHandle != _activeSharedHandle)
            {
                // A new shared handle usually means the producer recreated its texture.
                // Pause shared consumption until the renderer confirms it opened the new one.
                _activeSharedHandle = IntPtr.Zero;
            }

            _pendingSharedFrame = new OverlaySurfaceFrame(kind, descriptor);
        }
    }

    public void NotifySharedTextureOpened(IntPtr sharedHandle)
    {
        if (sharedHandle == IntPtr.Zero)
            return;

        lock (_sync)
        {
            _activeSharedHandle = sharedHandle;
            _lastProbedSharedHandle = sharedHandle;
        }
    }

    public void NotifySharedTextureUnavailable()
    {
        lock (_sync)
        {
            _sharedInteropUnavailable = true;
            _pendingSharedFrame = null;
            _activeSharedHandle = IntPtr.Zero;
            _lastProbedSharedHandle = IntPtr.Zero;
        }

        if (Interlocked.Exchange(ref _loggedSharedUnavailable, 1) == 0)
        {
            RynthLog.UI("AngleOverlaySurfaceBridge: Shared-texture consumption is unavailable on this client; staying on software frames.");
        }
    }

    public bool TryConsume(out OverlaySurfaceFrame? frame)
    {
        if (_sharedInteropUnavailable)
            return _softwareFallback.TryConsume(out frame);

        OverlaySurfaceFrame? sharedProbeFrame = null;
        bool sharedIsActive;

        lock (_sync)
        {
            sharedIsActive = _activeSharedHandle != IntPtr.Zero;

            if (_pendingSharedFrame?.SharedTexture is OverlaySharedTextureDescriptor descriptor &&
                descriptor.SharedHandle != IntPtr.Zero)
            {
                if (_activeSharedHandle != IntPtr.Zero && descriptor.SharedHandle == _activeSharedHandle)
                {
                    _pendingSharedFrame = null;
                    sharedIsActive = true;
                }
                else if (descriptor.SharedHandle != _lastProbedSharedHandle)
                {
                    _lastProbedSharedHandle = descriptor.SharedHandle;
                    sharedProbeFrame = _pendingSharedFrame;
                    _pendingSharedFrame = null;
                }
            }
        }

        if (sharedProbeFrame != null)
        {
            frame = sharedProbeFrame;
            return true;
        }

        if (sharedIsActive)
        {
            frame = null;
            return false;
        }

        return _softwareFallback.TryConsume(out frame);
    }
}

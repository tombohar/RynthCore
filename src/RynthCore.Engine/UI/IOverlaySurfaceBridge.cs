using System;

namespace RynthCore.Engine.UI;

internal interface IOverlaySurfaceBridge
{
    string Name { get; }
    bool SupportsGpuInterop { get; }
    bool ShouldAttemptSharedTextures { get; }
    void SubmitSoftwareFrame(IntPtr pixelData, int byteCount, int width, int height);
    void SubmitSharedTexture(OverlaySurfaceKind kind, OverlaySharedTextureDescriptor descriptor);
    void NotifySharedTextureOpened(IntPtr sharedHandle);
    void NotifySharedTextureUnavailable();
    bool TryConsume(out OverlaySurfaceFrame? frame);
}

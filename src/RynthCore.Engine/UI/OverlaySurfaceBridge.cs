using System;

namespace RynthCore.Engine.UI;

internal static class OverlaySurfaceBridge
{
    private static readonly IOverlaySurfaceBridge SoftwareBridge = new SoftwareOverlaySurfaceBridge();
    private static readonly IOverlaySurfaceBridge AngleBridge = new AngleOverlaySurfaceBridge();
    private static IOverlaySurfaceBridge _activeBridge = SoftwareBridge;
    private static bool _loggedSelection;

    public static string ActiveName => _activeBridge.Name;
    public static bool ActiveSupportsGpuInterop => _activeBridge.SupportsGpuInterop;
    public static bool ShouldAttemptSharedTextures => _activeBridge.ShouldAttemptSharedTextures;

    public static void UseSoftwareFallback()
    {
        _activeBridge = SoftwareBridge;
        _loggedSelection = false;
    }

    public static void UseAngleStub()
    {
        _activeBridge = AngleBridge;
        _loggedSelection = false;
    }

    public static void SubmitSoftwareFrame(IntPtr pixelData, int byteCount, int width, int height)
    {
        LogSelectionOnce();
        _activeBridge.SubmitSoftwareFrame(pixelData, byteCount, width, height);
    }

    public static void SubmitSharedTexture(OverlaySurfaceKind kind, OverlaySharedTextureDescriptor descriptor)
    {
        LogSelectionOnce();
        _activeBridge.SubmitSharedTexture(kind, descriptor);
    }

    public static void NotifySharedTextureOpened(IntPtr sharedHandle)
    {
        _activeBridge.NotifySharedTextureOpened(sharedHandle);
    }

    public static void NotifySharedTextureUnavailable()
    {
        _activeBridge.NotifySharedTextureUnavailable();
    }

    public static bool TryConsume(out OverlaySurfaceFrame? frame)
    {
        LogSelectionOnce();
        return _activeBridge.TryConsume(out frame);
    }

    private static void LogSelectionOnce()
    {
        if (_loggedSelection)
            return;

        _loggedSelection = true;
        RynthLog.UI($"OverlaySurfaceBridge: Active path = {ActiveName} (gpuInterop={ActiveSupportsGpuInterop}).");
    }
}

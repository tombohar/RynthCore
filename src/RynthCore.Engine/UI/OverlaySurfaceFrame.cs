using System;

namespace RynthCore.Engine.UI;

internal enum OverlaySurfaceKind
{
    SoftwareBgra32 = 0,
    AngleSharedTexture = 1,
    DxgiSharedTexture = 2,
    D3D9SharedTexture = 3
}

internal readonly record struct OverlaySharedTextureDescriptor(
    IntPtr SharedHandle,
    long AdapterLuid,
    int Width,
    int Height,
    int PixelFormat,
    int RowPitch,
    int Usage);

internal sealed class OverlaySurfaceFrame
{
    public OverlaySurfaceFrame(byte[] pixels, int width, int height)
    {
        Kind = OverlaySurfaceKind.SoftwareBgra32;
        SoftwarePixels = pixels;
        Width = width;
        Height = height;
    }

    public OverlaySurfaceFrame(OverlaySurfaceKind kind, OverlaySharedTextureDescriptor sharedTexture)
    {
        Kind = kind;
        SharedTexture = sharedTexture;
        Width = sharedTexture.Width;
        Height = sharedTexture.Height;
    }

    public OverlaySurfaceKind Kind { get; private set; }
    public byte[]? SoftwarePixels { get; private set; }
    public OverlaySharedTextureDescriptor? SharedTexture { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>
    /// Reuses this instance for a new software frame. Lets the bridge avoid
    /// allocating a new <see cref="OverlaySurfaceFrame"/> on every consumed
    /// frame at 36-60 Hz — the small-bucket LFH churn from that allocation
    /// was bisected as residual heap-corruption pressure.
    /// </summary>
    internal void ResetSoftware(byte[] pixels, int width, int height)
    {
        Kind = OverlaySurfaceKind.SoftwareBgra32;
        SoftwarePixels = pixels;
        SharedTexture = null;
        Width = width;
        Height = height;
    }
}

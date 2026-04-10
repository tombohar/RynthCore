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

    public OverlaySurfaceKind Kind { get; }
    public byte[]? SoftwarePixels { get; }
    public OverlaySharedTextureDescriptor? SharedTexture { get; }
    public int Width { get; }
    public int Height { get; }
}

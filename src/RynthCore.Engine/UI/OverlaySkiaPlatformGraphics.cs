using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Skia;
using SkiaSharp;

namespace RynthCore.Engine.UI;

internal sealed class OverlaySkiaPlatformGraphics : IPlatformGraphicsWithFeatures
{
    private readonly OverlaySkiaGpuContext _sharedContext = new();

    public bool UsesSharedContext => true;

    public IPlatformGraphicsContext CreateContext() => _sharedContext;

    public IPlatformGraphicsContext GetSharedContext() => _sharedContext;

    public object? TryGetFeature(Type featureType) => _sharedContext.TryGetFeature(featureType);
}

internal sealed class OverlaySkiaGpuContext : ISkiaGpu
{
    private static readonly IDisposable NoopLease = new NoopDisposable();
    private readonly object _sync = new();
    private OverlaySkiaRenderTarget? _renderTarget;

    public bool IsLost => false;

    public IDisposable EnsureCurrent() => NoopLease;

    public object? TryGetFeature(Type featureType) => null;

    public ISkiaGpuRenderTarget? TryCreateRenderTarget(IEnumerable<object> surfaces)
    {
        lock (_sync)
        {
            _renderTarget ??= new OverlaySkiaRenderTarget();
            return _renderTarget;
        }
    }

    public ISkiaSurface? TryCreateSurface(PixelSize size, ISkiaGpuRenderSession? session)
    {
        if (size.Width <= 0 || size.Height <= 0)
            return null;

        return new OverlaySkiaSurface(size.Width, size.Height);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _renderTarget?.Dispose();
            _renderTarget = null;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

internal sealed class OverlaySkiaRenderTarget : ISkiaGpuRenderTarget
{
    private readonly object _sync = new();
    private OverlayD3D9SharedTexturePublisher? _sharedTexturePublisher;
    private SKSurface? _surface;
    private byte[]? _softwareBuffer;
    private int _width;
    private int _height;
    private int _loggedSharedTextureDisabled;

    public bool IsCorrupted => false;

    public ISkiaGpuRenderSession BeginRenderingSession()
    {
        lock (_sync)
        {
            EnsureSurface();
            if (_surface == null)
                throw new InvalidOperationException("Overlay Skia render surface could not be created.");

            _surface.Canvas.Clear(SKColors.Transparent);
            return new OverlaySkiaRenderSession(this, _surface, _width, _height);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _surface?.Dispose();
            _surface = null;
            _softwareBuffer = null;
            _sharedTexturePublisher?.Dispose();
            _width = 0;
            _height = 0;
        }
    }

    public byte[] RentSoftwareBuffer(int byteCount)
    {
        lock (_sync)
        {
            if (_softwareBuffer == null || _softwareBuffer.Length != byteCount)
                _softwareBuffer = new byte[byteCount];

            return _softwareBuffer;
        }
    }

    public bool TrySubmitSharedTexture(
        IntPtr pixelData,
        int byteCount,
        int width,
        int height,
        int rowPitch,
        out OverlaySharedTextureDescriptor descriptor)
    {
        lock (_sync)
        {
            if (!AvaloniaOverlay.UseAnglePreferredBridge || !OverlaySurfaceBridge.ShouldAttemptSharedTextures)
            {
                if (_sharedTexturePublisher != null)
                {
                    _sharedTexturePublisher.Dispose();
                    _sharedTexturePublisher = null;
                }

                if (Interlocked.Exchange(ref _loggedSharedTextureDisabled, 1) == 0)
                {
                    RynthLog.UI("OverlaySkiaRenderTarget: Shared-texture uploads disabled for this session; continuing with software submissions.");
                }

                descriptor = default;
                return false;
            }

            _sharedTexturePublisher ??= new OverlayD3D9SharedTexturePublisher();
            return _sharedTexturePublisher.TryUpload(pixelData, byteCount, width, height, rowPitch, out descriptor);
        }
    }

    private void EnsureSurface()
    {
        int width = AvaloniaOverlay.ClientPixelWidth > 1 ? AvaloniaOverlay.ClientPixelWidth : AvaloniaOverlay.ViewportWidth;
        int height = AvaloniaOverlay.ClientPixelHeight > 1 ? AvaloniaOverlay.ClientPixelHeight : AvaloniaOverlay.ViewportHeight;

        if (width <= 1 || height <= 1)
        {
            width = _width > 1 ? _width : 1;
            height = _height > 1 ? _height : 1;
        }

        if (_surface != null && width == _width && height == _height)
            return;

        _surface?.Dispose();
        _width = width;
        _height = height;

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _surface = SKSurface.Create(info);
        RynthLog.UI($"OverlaySkiaRenderTarget: Created raster render surface {_width}x{_height}.");
    }
}

internal sealed class OverlaySkiaRenderSession : ISkiaGpuRenderSession
{
    private static int _loggedTinyBootstrapFrame;
    private readonly OverlaySkiaRenderTarget _owner;
    private readonly SKSurface _surface;
    private readonly int _width;
    private readonly int _height;
    private bool _disposed;

    public OverlaySkiaRenderSession(OverlaySkiaRenderTarget owner, SKSurface surface, int width, int height)
    {
        _owner = owner;
        _surface = surface;
        _width = width;
        _height = height;
    }

    public GRContext GrContext => null!;
    public double ScaleFactor => 1.0d;
    public SKSurface SkSurface => _surface;
    public GRSurfaceOrigin SurfaceOrigin => GRSurfaceOrigin.TopLeft;

    public unsafe void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_width <= 1 || _height <= 1)
        {
            if (Interlocked.Exchange(ref _loggedTinyBootstrapFrame, 1) == 0)
            {
                RynthLog.UI("OverlaySkiaRenderSession: Skipping tiny bootstrap frame until the game surface reports a real size.");
            }

            return;
        }

        if (!AvaloniaOverlay.ShouldUseCustomSkiaProducer)
            return;

        var info = new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
        byte[] pixels = _owner.RentSoftwareBuffer(info.BytesSize);

        fixed (byte* pixelPtr = pixels)
        {
            if (!_surface.ReadPixels(info, (IntPtr)pixelPtr, info.RowBytes, 0, 0))
            {
                RynthLog.UI("OverlaySkiaRenderSession: Failed to read pixels from custom render target.");
                return;
            }

            AvaloniaOverlay.SurfacePixelWidth = _width;
            AvaloniaOverlay.SurfacePixelHeight = _height;
            if (_owner.TrySubmitSharedTexture((IntPtr)pixelPtr, pixels.Length, _width, _height, info.RowBytes, out OverlaySharedTextureDescriptor sharedDescriptor))
            {
                OverlaySurfaceBridge.SubmitSharedTexture(OverlaySurfaceKind.D3D9SharedTexture, sharedDescriptor);
            }

            OverlaySurfaceBridge.SubmitSoftwareFrame((IntPtr)pixelPtr, pixels.Length, _width, _height);
            AvaloniaOverlay.NotifyCustomFrameSubmitted(_width, _height);
        }
    }
}

internal sealed class OverlaySkiaSurface : ISkiaSurface
{
    public OverlaySkiaSurface(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Unable to create Skia surface.");
    }

    public bool CanBlit => true;
    public SKSurface Surface { get; }

    public void Blit(SKCanvas canvas)
    {
        using var snapshot = Surface.Snapshot();
        canvas.DrawImage(snapshot, 0, 0);
    }

    public void Dispose()
    {
        Surface.Dispose();
    }
}

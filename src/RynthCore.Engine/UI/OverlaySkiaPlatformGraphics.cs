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
    private static int _detachedTargetCount;
    private readonly object _sync = new();
    private OverlaySkiaRenderTarget? _renderTarget;

    /// <summary>
    /// How many TopLevels other than the overlay window asked for a render
    /// target this session. Expected to stay 0: popups are embedded in the
    /// overlay window (Win32PlatformOptions.OverlayPopups). Logged with every
    /// popup open/close so a regression is visible in the per-pid log.
    /// </summary>
    internal static int DetachedTargetCount => Volatile.Read(ref _detachedTargetCount);

    public bool IsLost => false;

    public IDisposable EnsureCurrent() => NoopLease;

    public object? TryGetFeature(Type featureType) => null;

    public ISkiaGpuRenderTarget? TryCreateRenderTarget(IEnumerable<object> surfaces)
    {
        lock (_sync)
        {
            // Only the overlay window may own the render target whose frames go
            // to the game. Every Avalonia TopLevel calls this - before the
            // OverlayPopups fix each ToolTip/Flyout/ContextMenu/ComboBox opened
            // a PopupRoot TopLevel that got this same target: its render cleared
            // the shared surface and submitted a popup-only frame (every docked
            // panel vanished), and closing it disposed the surface under the
            // overlay window (rebuilt on its next frame). Any other TopLevel now
            // gets a detached target that renders nowhere.
            IntPtr hwnd = TryGetHwnd(surfaces);
            IntPtr overlayHwnd = AvaloniaOverlay.AvaloniaHwnd;
            bool isOverlayWindow = overlayHwnd != IntPtr.Zero && hwnd != IntPtr.Zero
                ? hwnd == overlayHwnd
                : _renderTarget == null || _renderTarget.IsDisposed;

            if (!isOverlayWindow)
            {
                int n = Interlocked.Increment(ref _detachedTargetCount);
                RynthLog.UI($"OverlaySkiaGpuContext: TopLevel hwnd=0x{hwnd.ToInt64():X} is not the overlay window (0x{overlayHwnd.ToInt64():X}); gave it detached render target #{n} so it can't blank the docked panels.");
                return new DetachedOverlayRenderTarget();
            }

            if (_renderTarget == null || _renderTarget.IsDisposed)
                _renderTarget = new OverlaySkiaRenderTarget();
            return _renderTarget;
        }
    }

    private static IntPtr TryGetHwnd(IEnumerable<object> surfaces)
    {
        try
        {
            foreach (object surface in surfaces)
            {
                if (surface is IPlatformHandle handle &&
                    string.Equals(handle.HandleDescriptor, "HWND", StringComparison.OrdinalIgnoreCase))
                {
                    return handle.Handle;
                }
            }
        }
        catch
        {
        }

        return IntPtr.Zero;
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
    /// <summary>
    /// Exposed so OverlaySkiaRenderSession.Dispose can hold the same lock
    /// while it touches _surface (via ReadPixels). Without this, a parallel
    /// BeginRenderingSession that triggers EnsureSurface size-change can
    /// dispose the SKSurface that a still-running session is reading from
    /// → native UAF into Skia's heap → LFH freelist corruption surfaces
    /// minutes later as the AV at ntdll!RtlpHeap RVA 0x5B10C.
    /// </summary>
    internal object SyncRoot => _sync;
    private OverlayD3D9SharedTexturePublisher? _sharedTexturePublisher;
    private SKSurface? _surface;
    private byte[]? _softwareBuffer;
    private int _width;
    private int _height;
    private int _loggedSharedTextureDisabled;
    private bool _disposed;
    private static int _surfaceCreateCount;

    /// <summary>
    /// Full-size raster surfaces created this session (startup + resizes, and
    /// the old popup-flash rebuilds). Popup open/close logging compares it
    /// before and after each popup: anything other than +0 is the flash.
    /// </summary>
    internal static int SurfaceCreateCount => Volatile.Read(ref _surfaceCreateCount);

    internal bool IsDisposed
    {
        get { lock (_sync) return _disposed; }
    }

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
            _disposed = true;
            _surface?.Dispose();
            _surface = null;
            _softwareBuffer = null;
            _sharedTexturePublisher?.Dispose();
            _sharedTexturePublisher = null;
            _width = 0;
            _height = 0;
        }
    }

    public byte[] RentSoftwareBuffer(int byteCount)
    {
        lock (_sync)
        {
            // Only grow. Reallocating on every pixel-difference (Length != byteCount)
            // produces ~MB-sized LOH allocations that churn the LFH and triggered
            // the heap-corruption AV at ntdll!RtlpHeap RVA 0x5B10C.
            if (_softwareBuffer == null || _softwareBuffer.Length < byteCount)
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

        // Why the surface is being (re)built. "recreate-after-dispose" at an
        // unchanged size is the old popup-flash signature: a TopLevel disposed
        // this target and the overlay window had to rebuild it.
        string reason = _surface != null
            ? $"resize {_width}x{_height}->{width}x{height}"
            : _disposed ? "recreate-after-dispose" : "initial";
        _disposed = false;

        _surface?.Dispose();
        _width = width;
        _height = height;

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _surface = SKSurface.Create(info);
        int total = Interlocked.Increment(ref _surfaceCreateCount);
        RynthLog.UI($"OverlaySkiaRenderTarget: Created raster render surface {_width}x{_height} (reason={reason}, total={total}).");
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

        // Hold the owner's lock for the duration of the surface read. Without
        // this, a parallel BeginRenderingSession on a different thread can
        // size-change → dispose the SKSurface we still hold a ref to → ReadPixels
        // writes into freed Skia native memory → LFH heap corruption. lock is
        // reentrant so the inner RentSoftwareBuffer / TrySubmitSharedTexture
        // calls (which also take _sync) are safe.
        lock (_owner.SyncRoot)
        {
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
}

/// <summary>
/// Render target for any Avalonia TopLevel that is not the overlay window.
/// Avalonia needs somewhere to draw, but only the overlay window's frames may
/// reach the game: this target draws into a private 1x1 raster surface and
/// never submits, reads back or clears anything shared.
/// </summary>
internal sealed class DetachedOverlayRenderTarget : ISkiaGpuRenderTarget
{
    private readonly object _sync = new();
    private SKSurface? _surface;

    public bool IsCorrupted => false;

    public ISkiaGpuRenderSession BeginRenderingSession()
    {
        lock (_sync)
        {
            _surface ??= SKSurface.Create(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("Detached overlay render surface could not be created.");
            return new Session(_surface);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _surface?.Dispose();
            _surface = null;
        }
    }

    private sealed class Session : ISkiaGpuRenderSession
    {
        public Session(SKSurface surface) => SkSurface = surface;

        public GRContext GrContext => null!;
        public double ScaleFactor => 1.0d;
        public SKSurface SkSurface { get; }
        public GRSurfaceOrigin SurfaceOrigin => GRSurfaceOrigin.TopLeft;

        public void Dispose()
        {
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

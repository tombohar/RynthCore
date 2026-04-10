// ============================================================================
//  RynthCore.Engine - UI/OverlayFrameBuffer.cs
//  Lock-minimised pixel handoff from the Avalonia UI thread to the D3D9
//  render thread. Avalonia submits a captured BGRA frame; EndScene consumes
//  it once, uploads to a D3D9 texture, then re-uses that texture until the
//  next submit.
//
//  Thread model:
//    Writer: Avalonia STA thread (DispatcherTimer ~30 fps)
//    Reader: D3D9 render thread (EndScene ~60 fps)
//
//  The submitted buffer is replaced atomically (pointer swap). The reader
//  holds a reference for the duration of the upload, preventing GC collection
//  even if the writer has already posted a newer frame.
// ============================================================================

using System;
using System.Threading;

namespace RynthCore.Engine.UI;

internal static class OverlayFrameBuffer
{
    private sealed class Frame
    {
        public readonly byte[] Pixels;
        public readonly int Width;
        public readonly int Height;

        public Frame(byte[] pixels, int w, int h)
        {
            Pixels = pixels;
            Width  = w;
            Height = h;
        }
    }

    // Written by Avalonia thread, read by D3D9 thread.
    // Interlocked.Exchange gives the sequential-consistency guarantee we need.
    private static Frame? _pending;

    /// <summary>
    /// Submit a new BGRA frame from the Avalonia UI thread.
    /// The pixel array is copied so the caller can reuse its bitmap lock buffer.
    /// </summary>
    public static void Submit(IntPtr pixelData, int byteCount, int w, int h)
    {
        var buf = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(pixelData, buf, 0, byteCount);
        Interlocked.Exchange(ref _pending, new Frame(buf, w, h));
    }

    /// <summary>
    /// Consume the latest pending frame from the D3D9 render thread.
    /// Returns true if a new frame is available; the caller owns the byte array
    /// until the next call (no further synchronisation required).
    /// </summary>
    public static bool TryConsume(out byte[]? pixels, out int w, out int h)
    {
        Frame? frame = Interlocked.Exchange(ref _pending, null);
        if (frame == null)
        {
            pixels = null; w = 0; h = 0;
            return false;
        }

        pixels = frame.Pixels;
        w      = frame.Width;
        h      = frame.Height;
        return true;
    }
}

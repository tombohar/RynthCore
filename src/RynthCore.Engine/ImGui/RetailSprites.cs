// ============================================================================
//  RynthCore.Engine - ImGui/RetailSprites.cs
//  The retail UI's own pictures (0x06 textures in client_portal.dat: window
//  backgrounds, title bars, slot frames, meters, scrollbar pieces) as ImGui
//  textures, for faces that draw the retail look (the Inventory's Classic view).
//
//  Unlike ScriptIcons (item icons, many, least recently drawn evicted) this is a
//  small fixed set asked for by id: each picture is decoded once, made into a
//  texture once and kept until the engine shuts down or ImGui is torn down (hot
//  reload). Nothing is decoded or created per frame once a picture is in.
//
//  Threads:
//    - AC's render thread: TryGet (inside the ImGui frame), BeginFrame (creates
//      the textures decoded since the last frame, at most 16 a frame, before
//      anything draws) and Shutdown (releases them all). Only it touches D3D.
//    - One worker at a time on the thread pool: reads and decodes the pictures
//      from client_portal.dat (AcDatFile / AcIconDecoder, pure code, no AC memory).
//    The two meet only in two small queues under one lock.
//
//  Bounded: at most 192 pictures, each at most 512 x 512 (the biggest the
//  inventory uses is its 300 x 362 background, ~0.4 MB). A picture that isn't
//  in the dat or doesn't decode is tried again after 30 s; until then the caller
//  draws its own stand-in (TryGet returns false).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using RynthCore.Engine.UI.ScriptWindows;

namespace RynthCore.Engine.ImGuiBackend;

internal static class RetailSprites
{
    private const int MaxSprites = 192;
    private const int MaxCreatesPerFrame = 16;
    private static readonly long FailedRetryTicks = Stopwatch.Frequency * 30;

    private sealed class Sprite
    {
        public IntPtr Ptr;
        public bool Pending;
        public long FailedAt;
    }

    private readonly struct Result
    {
        public readonly uint Id;
        public readonly AcIconImage? Image;
        public readonly int Generation;

        public Result(uint id, AcIconImage? image, int generation)
        {
            Id = id;
            Image = image;
            Generation = generation;
        }
    }

    // ── AC's render thread only ──────────────────────────────────────────
    private static readonly Dictionary<uint, Sprite> Sprites = new(MaxSprites);
    private static readonly List<Result> ResultScratch = new(MaxCreatesPerFrame);

    // ── Shared with the worker, under Sync ───────────────────────────────
    private static readonly object Sync = new();
    private static readonly Queue<(uint Id, int Generation)> Requests = new(MaxSprites);
    private static readonly Queue<Result> Results = new(MaxSprites);
    private static bool _draining;
    private static int _generation;
    private static bool _datMissingLogged;

    /// <summary>Pictures that are textures now (diagnostics).</summary>
    public static int LiveTextures { get; private set; }

    /// <summary>
    /// The texture for a retail UI picture (a 0x06 texture id), once it is in; otherwise
    /// false, and the first ask queues its decode. AC's render thread.
    /// </summary>
    public static bool TryGet(uint textureId, out IntPtr texture)
    {
        texture = IntPtr.Zero;
        if (!AcIconDecoder.IsTextureId(textureId)) return false;
        if (!Sprites.TryGetValue(textureId, out Sprite? sprite))
        {
            if (Sprites.Count >= MaxSprites) return false;
            sprite = new Sprite();
            Sprites[textureId] = sprite;
        }
        if (sprite.Ptr != IntPtr.Zero)
        {
            texture = sprite.Ptr;
            return true;
        }
        if (!sprite.Pending && (sprite.FailedAt == 0 || Stopwatch.GetTimestamp() - sprite.FailedAt >= FailedRetryTicks)
            && Enqueue(textureId))
            sprite.Pending = true;
        return false;
    }

    /// <summary>
    /// Start of the ImGui frame, before anything draws: turns the pictures decoded since the
    /// last frame into textures (at most 16). AC's render thread.
    /// </summary>
    public static void BeginFrame(IntPtr device)
    {
        ResultScratch.Clear();
        lock (Sync)
        {
            while (Results.Count > 0 && ResultScratch.Count < MaxCreatesPerFrame)
                ResultScratch.Add(Results.Dequeue());
        }
        if (ResultScratch.Count == 0) return;

        long now = Stopwatch.GetTimestamp();
        foreach (Result r in ResultScratch)
        {
            if (r.Generation != _generation || !Sprites.TryGetValue(r.Id, out Sprite? sprite)) continue;
            sprite.Pending = false;
            if (sprite.Ptr != IntPtr.Zero) continue;
            IntPtr ptr = IntPtr.Zero;
            if (r.Image != null)
            {
                try { ptr = DX9Backend.CreateTextureRgba(device, r.Image.Width, r.Image.Height, r.Image.Pixels); }
                catch { ptr = IntPtr.Zero; }
            }
            if (ptr != IntPtr.Zero)
            {
                sprite.Ptr = ptr;
                sprite.FailedAt = 0;
                LiveTextures++;
            }
            else
            {
                sprite.FailedAt = now;
            }
        }
        ResultScratch.Clear();
    }

    /// <summary>
    /// Engine shutdown or ImGui teardown (hot reload, unload): releases every texture and
    /// drops the queues; a worker still running finishes into a generation nobody reads.
    /// AC's render thread.
    /// </summary>
    public static void Shutdown()
    {
        lock (Sync)
        {
            _generation++;
            Requests.Clear();
            Results.Clear();
        }
        foreach (Sprite s in Sprites.Values)
        {
            if (s.Ptr == IntPtr.Zero) continue;
            try { DX9Backend.ReleaseTexture(s.Ptr); } catch { }
            s.Ptr = IntPtr.Zero;
        }
        Sprites.Clear();
        ResultScratch.Clear();
        LiveTextures = 0;
    }

    // ── Worker ───────────────────────────────────────────────────────────

    private static bool Enqueue(uint id)
    {
        lock (Sync)
        {
            if (Requests.Count + Results.Count >= MaxSprites) return false;
            Requests.Enqueue((id, _generation));
            if (_draining) return true;
            _draining = true;
        }
        try
        {
            ThreadPool.UnsafeQueueUserWorkItem(static _ => Drain(), null);
        }
        catch
        {
            lock (Sync) _draining = false;
        }
        return true;
    }

    /// <summary>The worker: decodes one picture at a time until the queue is empty or the engine stops.</summary>
    private static void Drain()
    {
        AcDatFile? dat = null;
        bool datTried = false;
        try
        {
            while (true)
            {
                (uint Id, int Generation) r;
                lock (Sync)
                {
                    if (Requests.Count == 0 || EngineThreads.Stopping)
                    {
                        Requests.Clear();
                        _draining = false;
                        return;
                    }
                    r = Requests.Dequeue();
                }

                if (!datTried)
                {
                    datTried = true;
                    string? path = AcDatFile.FindPortalDat();
                    string why = "client_portal.dat not found next to acclient.exe";
                    if (path != null) dat = AcDatFile.Open(path, out why);
                    if (dat == null && !_datMissingLogged)
                    {
                        _datMissingLogged = true;
                        RynthLog.UI($"RetailSprites: no retail UI pictures ({why}); panels draw their own stand-ins.");
                    }
                }

                AcIconImage? image = null;
                try { image = dat == null ? null : AcIconDecoder.Decode(dat, r.Id, AcIconDecoder.MaxUiSide); }
                catch { image = null; }
                lock (Sync)
                {
                    if (r.Generation == _generation) Results.Enqueue(new Result(r.Id, image, r.Generation));
                }
            }
        }
        catch
        {
            lock (Sync) { Requests.Clear(); _draining = false; }
        }
        finally
        {
            try { dat?.Dispose(); } catch { }
        }
    }
}

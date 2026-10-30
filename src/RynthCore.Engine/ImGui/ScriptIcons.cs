// ============================================================================
//  RynthCore.Engine - ImGui/ScriptIcons.cs
//  Script windows: the D3D9 texture cache behind the Image / ImageButton ops.
//  An icon is asked for by kind and id: an icon (0x06 texture) id, an object id
//  (the item's icon with its underlay and overlay) or a spell id (the spell's
//  icon). Design: RynthSuite Docs/RYNTHLUA_WINDOWS_DESIGN.md §9 (icons).
//
//  Threads:
//    - AC's render thread: TryGet (from the replay), BeginFrame (creates the
//      textures decoded since the last frame, at most 8 a frame, and evicts the
//      least recently drawn ones over the cap) and Shutdown (releases them all).
//      Only this thread touches D3D.
//    - One worker at a time on the thread pool: resolves an object's icon ids
//      from the engine's PWD snapshot (ClientObjectHooks, the off-thread path:
//      no AC memory), a spell's icon from the portal spell table, and decodes
//      and stacks the pictures from client_portal.dat (AcIconDecoder). Nothing
//      it does touches AC or D3D.
//    The two meet only in two small queues under one lock.
//
//  Bounded: about 256 textures kept (32 x 32 icons: 4 KB each, twice with the
//  managed pool's system copy), 8 MB of pixels at most (a picture is at most
//  128 x 128), 2048 remembered ids, 128 requests in flight. What was drawn in
//  the last frame is never evicted (so a hud showing more icons than the cap
//  doesn't thrash); past the byte budget a new picture stays a placeholder.
//  Eviction and creation run at the start of the frame, before anything is
//  drawn, so a texture is never released while a draw list still points at it
//  (every draw list of the frames before has been rendered by then). Shutdown
//  (engine unload, hot reload) releases every texture and drops the queues;
//  results of an older generation are thrown away.
//
//  A missing, not yet decoded or undecodable icon draws a placeholder (the
//  replay's job); nothing here throws into the replay.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.ImGuiBackend.Hud;
using RynthCore.Engine.UI.ScriptWindows;

namespace RynthCore.Engine.ImGuiBackend;

internal static class ScriptIcons
{
    public const int MaxTextures = 256;
    private const long MaxLiveBytes = 8L * 1024 * 1024;
    private const int MaxSlots = 2048;
    private const int MaxCreatesPerFrame = 8;
    private const int MaxPendingRequests = 128;

    private static readonly long ObjectRefreshTicks = Stopwatch.Frequency * 2;   // enchantment overlays etc. change
    private static readonly long UnresolvedRetryTicks = Stopwatch.Frequency;      // an object not seen yet, a spell table still loading
    private static readonly long FailedRetryTicks = Stopwatch.Frequency * 30;     // an icon that didn't decode
    private static readonly long BudgetRetryTicks = Stopwatch.Frequency * 2;      // no room in the byte budget

    private const uint StypeIcon = 8, StypeIconOverlay = 50, StypeIconUnderlay = 52, StypeUiEffects = 18, StypeItemType = 1;

    /// <summary>What one picture is made of: icon, underlay and overlay texture ids (0 = none).</summary>
    // Effects / ItemType (Object icons only) pick the plate the icon is drawn on when the server sent no underlay.
    private readonly record struct Layers(uint Icon, uint Underlay, uint Overlay, uint Effects = 0, uint ItemType = 0);

    /// <summary>One asked-for (kind, id): the layers it resolved to.</summary>
    private sealed class Slot
    {
        public Layers Layers;
        public bool Resolved;
        public bool Pending;
        public long ResolvedAt;
        public int LastUsed;
    }

    /// <summary>One picture's texture (or why it has none yet).</summary>
    private sealed class Texture
    {
        public IntPtr Ptr;
        public int Bytes;
        public bool Pending;
        public long FailedAt;
        /// <summary>How long after FailedAt to try again.</summary>
        public long RetryTicks;
        public int LastUsed;
    }

    private enum Work : byte { Resolve, Decode }

    private readonly struct Request
    {
        public readonly Work Work;
        public readonly ScriptIconKind Kind;
        public readonly uint Id;
        public readonly Layers Layers;
        public readonly int Generation;

        public Request(Work work, ScriptIconKind kind, uint id, Layers layers, int generation)
        {
            Work = work;
            Kind = kind;
            Id = id;
            Layers = layers;
            Generation = generation;
        }
    }

    private readonly struct Result
    {
        public readonly Work Work;
        public readonly ulong SlotKey;
        public readonly Layers Layers;
        public readonly bool Ok;
        public readonly AcIconImage? Image;
        public readonly int Generation;

        public Result(Work work, ulong slotKey, Layers layers, bool ok, AcIconImage? image, int generation)
        {
            Work = work;
            SlotKey = slotKey;
            Layers = layers;
            Ok = ok;
            Image = image;
            Generation = generation;
        }
    }

    // ── AC's render thread only ──────────────────────────────────────────
    private static readonly Dictionary<ulong, Slot> Slots = new(256);
    private static readonly Dictionary<Layers, Texture> Textures = new(MaxTextures);
    private static readonly List<(int LastUsed, Layers Key)> EvictScratch = new(MaxTextures);
    private static readonly List<ulong> SlotScratch = new();
    private static readonly List<Result> ResultScratch = new(MaxCreatesPerFrame);
    private static int _frame;
    private static int _live;
    private static long _liveBytes;

    // ── Shared with the worker, under Sync ───────────────────────────────
    private static readonly object Sync = new();
    private static readonly Queue<Request> Requests = new(MaxPendingRequests);
    private static readonly Queue<Result> Results = new(MaxPendingRequests);
    private static bool _draining;
    private static int _generation;
    private static bool _datMissingLogged;

    /// <summary>Textures alive now (diagnostics).</summary>
    public static int LiveTextures => _live;

    private static ulong SlotKey(ScriptIconKind kind, uint id) => ((ulong)kind << 32) | id;

    /// <summary>
    /// The texture for an icon, if it is ready; otherwise false and, when needed, the work to
    /// get it is queued (the replay draws a placeholder meanwhile). AC's render thread.
    /// </summary>
    public static bool TryGet(ScriptIconKind kind, uint id, out IntPtr texture)
    {
        texture = IntPtr.Zero;
        if (kind > ScriptIconKind.Spell || id == 0) return false;
        long now = Stopwatch.GetTimestamp();

        ulong key = SlotKey(kind, id);
        if (!Slots.TryGetValue(key, out Slot? slot))
        {
            if (Slots.Count >= MaxSlots) PruneSlots();
            slot = new Slot();
            if (kind == ScriptIconKind.Icon)
            {
                slot.Layers = new Layers(AcIconDecoder.NormalizeIconId(id), 0, 0);
                slot.Resolved = true;
            }
            Slots[key] = slot;
        }
        slot.LastUsed = _frame;

        // Objects are looked up again every 2 s (an overlay can change); a spell's icon never
        // changes; an id that didn't resolve is tried again after a second.
        if (kind != ScriptIconKind.Icon && !slot.Pending)
        {
            bool due = !slot.Resolved
                ? slot.ResolvedAt == 0 || now - slot.ResolvedAt >= UnresolvedRetryTicks
                : kind == ScriptIconKind.Object && now - slot.ResolvedAt >= ObjectRefreshTicks;
            if (due && Enqueue(new Request(Work.Resolve, kind, id, default, _generation)))
                slot.Pending = true;
        }
        if (!slot.Resolved) return false;

        if (!Textures.TryGetValue(slot.Layers, out Texture? tex))
        {
            tex = new Texture();
            Textures[slot.Layers] = tex;
        }
        tex.LastUsed = _frame;
        if (tex.Ptr != IntPtr.Zero)
        {
            texture = tex.Ptr;
            return true;
        }
        if (!tex.Pending && (tex.FailedAt == 0 || now - tex.FailedAt >= tex.RetryTicks)
            && Enqueue(new Request(Work.Decode, kind, id, slot.Layers, _generation)))
            tex.Pending = true;
        return false;
    }

    /// <summary>
    /// The texture for an item given as its layers rather than as a live object (the vault's items): the
    /// icon on the plate its type / effects ask for, with underlay and overlay, as an object would be drawn.
    /// False until it is ready (the work is queued). AC's render thread.
    /// </summary>
    public static bool TryGetItem(uint icon, uint underlay, uint overlay, uint effects, uint itemType, out IntPtr texture)
    {
        texture = IntPtr.Zero;
        if (icon == 0) return false;
        long now = Stopwatch.GetTimestamp();
        var layers = new Layers(AcIconDecoder.NormalizeIconId(icon), underlay, overlay, effects, itemType);
        if (!Textures.TryGetValue(layers, out Texture? tex))
        {
            tex = new Texture();
            Textures[layers] = tex;
        }
        tex.LastUsed = _frame;
        if (tex.Ptr != IntPtr.Zero)
        {
            texture = tex.Ptr;
            return true;
        }
        if (!tex.Pending && (tex.FailedAt == 0 || now - tex.FailedAt >= tex.RetryTicks)
            && Enqueue(new Request(Work.Decode, ScriptIconKind.Object, icon, layers, _generation)))
            tex.Pending = true;
        return false;
    }

    /// <summary>
    /// Start of the ImGui frame, before anything draws: creates the textures decoded since
    /// the last frame (at most 8) and evicts the least recently drawn ones over the cap.
    /// AC's render thread.
    /// </summary>
    public static void BeginFrame(IntPtr device)
    {
        _frame++;
        ResultScratch.Clear();
        lock (Sync)
        {
            int creates = 0;
            while (Results.Count > 0)
            {
                // Decodes wait their turn once 8 textures were made this frame; lookups don't.
                if (Results.Peek().Work == Work.Decode && creates >= MaxCreatesPerFrame) break;
                Result r = Results.Dequeue();
                if (r.Work == Work.Decode) creates++;
                ResultScratch.Add(r);
            }
        }
        if (ResultScratch.Count == 0 && Textures.Count <= MaxTextures) return;

        long now = Stopwatch.GetTimestamp();
        foreach (Result r in ResultScratch)
        {
            if (r.Generation != _generation) continue;
            if (r.Work == Work.Resolve)
            {
                if (!Slots.TryGetValue(r.SlotKey, out Slot? slot)) continue;
                slot.Pending = false;
                slot.ResolvedAt = now;
                // A lookup that failed keeps the last picture (an object briefly out of the
                // snapshot shouldn't blink); a slot that never resolved stays a placeholder.
                if (r.Ok)
                {
                    slot.Layers = r.Layers;
                    slot.Resolved = true;
                }
                continue;
            }

            if (!Textures.TryGetValue(r.Layers, out Texture? tex)) continue;   // evicted meanwhile
            tex.Pending = false;
            if (tex.Ptr != IntPtr.Zero) continue;
            IntPtr ptr = IntPtr.Zero;
            int bytes = r.Image == null ? 0 : r.Image.Width * r.Image.Height * 4;
            bool room = r.Image != null && (_liveBytes + bytes <= MaxLiveBytes || Evict(bytes));
            if (room)
            {
                try { ptr = DX9Backend.CreateTextureRgba(device, r.Image!.Width, r.Image.Height, r.Image.Pixels); }
                catch { ptr = IntPtr.Zero; }
            }
            if (ptr != IntPtr.Zero)
            {
                tex.Ptr = ptr;
                tex.Bytes = bytes;
                tex.FailedAt = 0;
                _live++;
                _liveBytes += bytes;
            }
            else
            {
                // Didn't decode (try again much later), or no room yet (soon).
                tex.FailedAt = now;
                tex.RetryTicks = r.Image != null && !room ? BudgetRetryTicks : FailedRetryTicks;
            }
        }
        ResultScratch.Clear();

        if (Textures.Count > MaxTextures) Evict(0);
    }

    /// <summary>
    /// Drops the least recently drawn pictures that weren't drawn in the last frame: until at most
    /// MaxTextures remain and, for <paramref name="needBytes"/> &gt; 0, until that many more bytes
    /// fit the budget. Returns whether they fit.
    /// </summary>
    private static bool Evict(int needBytes)
    {
        EvictScratch.Clear();
        foreach (var kv in Textures)
            if (!kv.Value.Pending && kv.Value.LastUsed < _frame - 1)
                EvictScratch.Add((kv.Value.LastUsed, kv.Key));
        EvictScratch.Sort((a, b) => a.LastUsed.CompareTo(b.LastUsed));
        for (int i = 0; i < EvictScratch.Count; i++)
        {
            bool overCount = Textures.Count > MaxTextures;
            bool overBytes = needBytes > 0 && _liveBytes + needBytes > MaxLiveBytes;
            if (!overCount && !overBytes) break;
            if (Textures.Remove(EvictScratch[i].Key, out Texture? tex) && tex.Ptr != IntPtr.Zero)
            {
                try { DX9Backend.ReleaseTexture(tex.Ptr); } catch { }
                _live--;
                _liveBytes -= tex.Bytes;
            }
        }
        EvictScratch.Clear();
        return _liveBytes + needBytes <= MaxLiveBytes;
    }

    /// <summary>Forgets the ids not drawn for the longest (their textures stay until evicted).</summary>
    private static void PruneSlots()
    {
        SlotScratch.Clear();
        int keepAfter = _frame - 600;   // ~10 s at 60 fps
        foreach (var kv in Slots)
            if (!kv.Value.Pending && kv.Value.LastUsed < keepAfter) SlotScratch.Add(kv.Key);
        if (SlotScratch.Count == 0)
            foreach (var kv in Slots)
                if (!kv.Value.Pending) SlotScratch.Add(kv.Key);   // every one is recent: start over
        foreach (ulong k in SlotScratch) Slots.Remove(k);
        SlotScratch.Clear();
    }

    /// <summary>
    /// Engine shutdown or ImGui teardown (hot reload, unload): releases every texture and drops
    /// the queues; a worker still running finishes into a generation nobody reads. AC's render thread.
    /// </summary>
    public static void Shutdown()
    {
        lock (Sync)
        {
            _generation++;
            Requests.Clear();
            Results.Clear();
        }
        foreach (Texture t in Textures.Values)
        {
            if (t.Ptr == IntPtr.Zero) continue;
            try { DX9Backend.ReleaseTexture(t.Ptr); } catch { }
            t.Ptr = IntPtr.Zero;
        }
        Textures.Clear();
        Slots.Clear();
        EvictScratch.Clear();
        ResultScratch.Clear();
        _live = 0;
        _liveBytes = 0;
    }

    // ── Worker ───────────────────────────────────────────────────────────

    /// <summary>Queues work for the worker (starting it when idle). False when too much is in flight.</summary>
    private static bool Enqueue(Request r)
    {
        lock (Sync)
        {
            if (Requests.Count + Results.Count >= MaxPendingRequests) return false;
            Requests.Enqueue(r);
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

    /// <summary>The worker: one request at a time until the queue is empty or the engine stops.</summary>
    private static void Drain()
    {
        AcDatFile? dat = null;
        bool datTried = false;
        try
        {
            while (true)
            {
                Request r;
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

                Result result;
                try
                {
                    result = r.Work == Work.Resolve ? ResolveNow(r) : DecodeNow(r, ref dat, ref datTried);
                }
                catch
                {
                    result = new Result(r.Work, SlotKey(r.Kind, r.Id), r.Layers, false, null, r.Generation);
                }
                lock (Sync)
                {
                    if (r.Generation == _generation) Results.Enqueue(result);
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

    /// <summary>An object's icon, underlay and overlay (PWD snapshot, no AC memory), or a spell's icon.</summary>
    private static Result ResolveNow(Request r)
    {
        ulong key = SlotKey(r.Kind, r.Id);
        if (r.Kind == ScriptIconKind.Object)
        {
            // Off AC's main thread these read the 10 Hz main-thread PWD snapshot.
            if (!ClientObjectHooks.TryGetObjectDataIdProperty(r.Id, StypeIcon, out uint icon) || icon == 0)
                return new Result(Work.Resolve, key, default, false, null, r.Generation);
            ClientObjectHooks.TryGetObjectDataIdProperty(r.Id, StypeIconUnderlay, out uint under);
            ClientObjectHooks.TryGetObjectDataIdProperty(r.Id, StypeIconOverlay, out uint over);
            ClientObjectHooks.TryGetObjectIntProperty(r.Id, StypeUiEffects, out int effects);
            ClientObjectHooks.TryGetObjectIntProperty(r.Id, StypeItemType, out int itemType);
            return new Result(Work.Resolve, key, new Layers(icon, under, over, unchecked((uint)effects), unchecked((uint)itemType)), true, null, r.Generation);
        }
        if (r.Kind == ScriptIconKind.Spell)
        {
            PortalSpellTable.EnsureLoadQueued();
            bool ok = PortalSpellTable.TryGetIcon(r.Id, out uint icon) && icon != 0;
            return new Result(Work.Resolve, key, ok ? new Layers(icon, 0, 0) : default, ok, null, r.Generation);
        }
        return new Result(Work.Resolve, key, new Layers(AcIconDecoder.NormalizeIconId(r.Id), 0, 0), true, null, r.Generation);
    }

    private static Result DecodeNow(Request r, ref AcDatFile? dat, ref bool datTried)
    {
        ulong key = SlotKey(r.Kind, r.Id);
        if (!datTried)
        {
            datTried = true;
            string? path = AcDatFile.FindPortalDat();
            string why = "client_portal.dat not found next to acclient.exe";
            if (path != null) dat = AcDatFile.Open(path, out why);
            if (dat == null && !_datMissingLogged)
            {
                _datMissingLogged = true;
                RynthLog.UI($"ScriptIcons: no icons ({why}); script windows draw placeholders.");
            }
        }
        // An object's icon sits on its plate (the server's underlay, else the one its effects / type ask for).
        uint under = dat != null && r.Kind == ScriptIconKind.Object ? AcIconPlates.For(dat, r.Layers.Underlay, r.Layers.Effects, r.Layers.ItemType) : r.Layers.Underlay;
        AcIconImage? image = dat == null ? null : AcIconDecoder.Compose(dat, r.Layers.Icon, under, r.Layers.Overlay, dat != null && r.Kind == ScriptIconKind.Object ? AcIconPlates.RimFor(dat, r.Layers.Effects) : 0u);
        return new Result(Work.Decode, key, r.Layers, image != null, image, r.Generation);
    }
}

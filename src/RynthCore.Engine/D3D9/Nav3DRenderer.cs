// ═══════════════════════════════════════════════════════════════════════════
//  RynthCore.Engine — D3D9/Nav3DRenderer.cs
//  Data store for 3D navigation markers. Plugins submit geometry every plugin
//  tick via AddRing/AddLine/AddTriangle; DX9Backend.RenderNav3D reads it
//  during EndScene.
//
//  Double-buffered to keep the render thread (60 Hz, AC's EndScene) from
//  reading a half-rewritten buffer while the plugin pump thread (~30 Hz) is
//  mid Clear → Submit. Without this, every plugin tick produced a 1-frame
//  empty/partial render that the user sees as flicker — pronounced when the
//  player or camera is in motion (because the submitted contents change each
//  tick; when stationary the race window contains identical data so it's
//  invisible).
//
//  Pattern:
//    pump:    ClearFrame() → AddX(...) × N → CommitFrame()
//    render:  Ready once per draw, then reads only that buffer; never
//             touches "pending"
//  CommitFrame atomically swaps the buffer reads see. A tick during which the
//  player crossed a landblock boundary is dropped (see CommitFrame).
// ═══════════════════════════════════════════════════════════════════════════

using System.Threading;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.D3D9;

internal static class Nav3DRenderer
{
    private const int MaxRings = 256;
    private const int MaxLines = 512;
    // Triangle: three vertices + color. Sized for a ~3×3-landblock slope
    // overlay around the player: 24-cell radius ≈ 49×49 cells × 2 triangles
    // = 4802 triangles worst case, with headroom for water and other uses.
    private const int MaxTriangles = 8192;

    private const float DefaultRingHeight = 0.5f; // back-compat default for AddRing

    /// <summary>One tick's submissions. Render side: read-only.</summary>
    internal sealed class NavBuffer
    {
        public readonly float[] RingX = new float[MaxRings];
        public readonly float[] RingY = new float[MaxRings];
        public readonly float[] RingZ = new float[MaxRings];
        public readonly float[] RingRadius = new float[MaxRings];
        public readonly float[] RingThick = new float[MaxRings];
        public readonly float[] RingHeight = new float[MaxRings];
        public readonly uint[]  RingColor = new uint[MaxRings];
        public int RingCount;

        public readonly float[] LineX1 = new float[MaxLines];
        public readonly float[] LineY1 = new float[MaxLines];
        public readonly float[] LineZ1 = new float[MaxLines];
        public readonly float[] LineX2 = new float[MaxLines];
        public readonly float[] LineY2 = new float[MaxLines];
        public readonly float[] LineZ2 = new float[MaxLines];
        public readonly float[] LineThick = new float[MaxLines];
        public readonly uint[]  LineColor = new uint[MaxLines];
        public int LineCount;

        public readonly float[] TriX1 = new float[MaxTriangles];
        public readonly float[] TriY1 = new float[MaxTriangles];
        public readonly float[] TriZ1 = new float[MaxTriangles];
        public readonly float[] TriX2 = new float[MaxTriangles];
        public readonly float[] TriY2 = new float[MaxTriangles];
        public readonly float[] TriZ2 = new float[MaxTriangles];
        public readonly float[] TriX3 = new float[MaxTriangles];
        public readonly float[] TriY3 = new float[MaxTriangles];
        public readonly float[] TriZ3 = new float[MaxTriangles];
        public readonly uint[]  TriColor = new uint[MaxTriangles];
        public int TriCount;

        // The player's landblock at the moment ClearFrame was called for
        // this buffer. All AddX submissions in this tick are in PLAYER-
        // landblock-local coordinates (per RynthAi + RynthVision contract),
        // so the render side needs THIS landblock — not the current
        // player landblock — when it computes the view-matrix shift, or the
        // tick's geometry will jump 192m when the player crosses a boundary
        // between this tick and the next render frame.
        // Zero = "no player pose available; fall back to current".
        public uint SubmissionPlayerLandblock;

        public void Reset() { RingCount = 0; LineCount = 0; TriCount = 0; SubmissionPlayerLandblock = 0; }
    }

    // Triple buffer:
    //   _ready   — what the render thread reads (volatile, swap published here)
    //   _pending — what the pump writes into for the current tick
    //   _spare   — last tick's ready, "cooling off" one tick before the pump
    //              recycles it. This guarantees the buffer the render thread
    //              has captured isn't overwritten mid-iteration: when pump
    //              commits, the buffer the reader was holding becomes _spare
    //              (untouched for this tick) before becoming _pending the
    //              tick after that. Cheap insurance against any timing where
    //              render takes longer than one pump interval.
    private static readonly NavBuffer _a = new();
    private static readonly NavBuffer _b = new();
    private static readonly NavBuffer _c = new();
    private static volatile NavBuffer _ready = _a;
    private static NavBuffer _pending = _b;
    private static NavBuffer _spare = _c;

    // ── Render-side reads ────────────────────────────────────────────────────

    /// <summary>
    /// The latest committed frame. Take it once per draw and read everything
    /// from it: re-reading this (volatile) property per item would mix two
    /// ticks if a commit landed mid-draw (a count from one, contents from the
    /// other). The triple buffer keeps a taken frame untouched for a full
    /// tick after it stops being ready.
    /// </summary>
    public static NavBuffer Ready => _ready;

    // The landblock the plugin tick that produced the ready buffer was using.
    // Render thread uses this for view-matrix shifting so geometry stays
    // anchored to that tick's reference frame even if the player crosses a
    // landblock boundary between this tick and the next render frame.
    // Returns 0 when no pose was sampled; caller should fall back to
    // current player landblock in that case.
    public static uint ReadyPlayerLandblock => _ready.SubmissionPlayerLandblock;

    // ── Pump-side writes ─────────────────────────────────────────────────────

    public static void ClearFrame()
    {
        _pending.Reset();
        // Capture the player's current landblock so the render side knows
        // which frame the tick's submissions are anchored in.
        if (PlayerPhysicsHooks.TryGetPlayerPose(out uint cellId, out _, out _, out _,
                out _, out _, out _, out _))
            _pending.SubmissionPlayerLandblock = cellId >> 16;
    }

    public static void AddRing(float wx, float wy, float wz, float radius, float thickness, uint colorArgb)
        => AddRingEx(wx, wy, wz, radius, thickness, DefaultRingHeight, colorArgb);

    public static void AddRingEx(float wx, float wy, float wz, float radius, float thickness, float height, uint colorArgb)
    {
        var p = _pending;
        if (p.RingCount >= MaxRings) return;
        int i = p.RingCount++;
        p.RingX[i] = wx; p.RingY[i] = wy; p.RingZ[i] = wz;
        p.RingRadius[i] = radius;
        p.RingThick[i] = thickness;
        p.RingHeight[i] = height;
        p.RingColor[i] = colorArgb;
    }

    public static void AddLine(float x1, float y1, float z1, float x2, float y2, float z2, float thickness, uint colorArgb)
    {
        var p = _pending;
        if (p.LineCount >= MaxLines) return;
        int i = p.LineCount++;
        p.LineX1[i] = x1; p.LineY1[i] = y1; p.LineZ1[i] = z1;
        p.LineX2[i] = x2; p.LineY2[i] = y2; p.LineZ2[i] = z2;
        p.LineThick[i] = thickness;
        p.LineColor[i] = colorArgb;
    }

    public static void AddTriangle(float x1, float y1, float z1,
                                   float x2, float y2, float z2,
                                   float x3, float y3, float z3, uint colorArgb)
    {
        var p = _pending;
        if (p.TriCount >= MaxTriangles) return;
        int i = p.TriCount++;
        p.TriX1[i] = x1; p.TriY1[i] = y1; p.TriZ1[i] = z1;
        p.TriX2[i] = x2; p.TriY2[i] = y2; p.TriZ2[i] = z2;
        p.TriX3[i] = x3; p.TriY3[i] = y3; p.TriZ3[i] = z3;
        p.TriColor[i] = colorArgb;
    }

    /// <summary>
    /// Cycle: _pending → _ready → _spare → _pending. The newly-filled buffer
    /// becomes ready; the old ready becomes spare (idle this tick); the old
    /// spare becomes the next pending. Render reads of the new ready buffer
    /// become visible across cores via the volatile field. Called by
    /// PluginManager.TickAll AFTER all plugins have submitted.
    ///
    /// The pump runs off AC's thread, so the player can cross a landblock
    /// boundary between ClearFrame and here. Plugins sample the pose during
    /// their tick, so some of this tick's geometry may then be in the old
    /// landblock's frame and some in the new one, while the frame is anchored
    /// to the old — whatever was sampled after the crossing would draw 192 m
    /// off for a tick. Such a tick is dropped: the previous frame stays ready
    /// (one tick stale, consistent) and the next tick is anchored correctly.
    /// </summary>
    public static void CommitFrame()
    {
        uint anchored = _pending.SubmissionPlayerLandblock;
        if (anchored != 0 &&
            PlayerPhysicsHooks.TryGetPlayerPose(out uint cellId, out _, out _, out _, out _, out _, out _, out _) &&
            (cellId >> 16) != 0 && (cellId >> 16) != anchored)
        {
            _pending.Reset();
            return;
        }

        NavBuffer newReady   = _pending;
        NavBuffer newSpare   = _ready;
        NavBuffer newPending = _spare;
        _spare = newSpare;
        _pending = newPending;
        Volatile.Write(ref _ready, newReady);
    }

    // ── C ABI callbacks for plugin contract ──────────────────────────────────
    public static void Nav3DClearCallback() => ClearFrame();
    public static void Nav3DAddRingCallback(float wx, float wy, float wz, float radius, float thickness, uint color)
        => AddRing(wx, wy, wz, radius, thickness, color);
    public static void Nav3DAddRingExCallback(float wx, float wy, float wz, float radius, float thickness, float height, uint color)
        => AddRingEx(wx, wy, wz, radius, thickness, height, color);
    public static void Nav3DAddLineCallback(float x1, float y1, float z1, float x2, float y2, float z2, float thickness, uint color)
        => AddLine(x1, y1, z1, x2, y2, z2, thickness, color);
    public static void Nav3DAddTriangleCallback(float x1, float y1, float z1, float x2, float y2, float z2, float x3, float y3, float z3, uint color)
        => AddTriangle(x1, y1, z1, x2, y2, z2, x3, y3, z3, color);
}

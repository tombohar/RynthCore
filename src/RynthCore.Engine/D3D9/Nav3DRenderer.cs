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
//    pump:    BeginFrame() → AddX(...) × N → CommitFrame()
//    render:  reads from the "ready" buffer only; never touches "pending"
//  CommitFrame atomically swaps the index reads see.
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

    private sealed class NavBuffer
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

    public static int RingCount     => _ready.RingCount;
    public static int LineCount     => _ready.LineCount;
    public static int TriangleCount => _ready.TriCount;

    public static void GetRing(int i, out float x, out float y, out float z,
        out float radius, out float thickness, out float height, out uint color)
    {
        var r = _ready;
        x = r.RingX[i]; y = r.RingY[i]; z = r.RingZ[i];
        radius = r.RingRadius[i]; thickness = r.RingThick[i];
        height = r.RingHeight[i]; color = r.RingColor[i];
    }

    public static void GetLine(int i, out float x1, out float y1, out float z1,
        out float x2, out float y2, out float z2, out float thickness, out uint color)
    {
        var r = _ready;
        x1 = r.LineX1[i]; y1 = r.LineY1[i]; z1 = r.LineZ1[i];
        x2 = r.LineX2[i]; y2 = r.LineY2[i]; z2 = r.LineZ2[i];
        thickness = r.LineThick[i]; color = r.LineColor[i];
    }

    public static void GetTriangle(int i, out float x1, out float y1, out float z1,
        out float x2, out float y2, out float z2,
        out float x3, out float y3, out float z3, out uint color)
    {
        var r = _ready;
        x1 = r.TriX1[i]; y1 = r.TriY1[i]; z1 = r.TriZ1[i];
        x2 = r.TriX2[i]; y2 = r.TriY2[i]; z2 = r.TriZ2[i];
        x3 = r.TriX3[i]; y3 = r.TriY3[i]; z3 = r.TriZ3[i];
        color = r.TriColor[i];
    }

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
    /// </summary>
    public static void CommitFrame()
    {
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

// ============================================================================
//  RynthCore.Engine - ImGui/DrawSubmitPlan.cs
//  How DX9Backend hands one ImDrawCmd to DrawIndexedPrimitiveUP. Pure logic,
//  no engine or D3D types (tools/UiCostTests compiles it). The submit's
//  counters (SubmitStats) live in UiCostModel.cs, which stays self-contained.
//
//  DrawIndexedPrimitiveUP copies NumVertices vertices out of the caller's
//  memory into the runtime's own vertex buffer on EVERY call. The renderer used
//  to pass the whole draw list's vertex count for each command, so a window
//  with V vertices and C draw commands copied V x C vertices a frame. Every
//  PushClipRect starts a new command; the Loot Editor's rule list pushes two
//  clip rects per row (name, conditions), so with the editor open the frame's
//  vertices went from ~13k to ~30k in one child list split into a command or
//  more per row, and the submit copied the list once per command: on the order
//  of a million vertices (tens of MB) a frame through the runtime's ring
//  buffer. Logged live: submit 200-330 ms for <1 ms of building (and the GPU/
//  driver stalls slowed another client on the PC too).
//
//  Now each command gets only the vertices its indices reach: the min..max of
//  its indices. When min > 0 the indices are rebased into a scratch copy and
//  the vertex pointer moved by min, so MinVertexIndex is always 0 (some D3D9
//  implementations copy from vertex 0 up to MinVertexIndex + NumVertices,
//  whatever MinVertexIndex says). A frame now copies about its vertex count.
// ============================================================================

namespace RynthCore.Engine.ImGuiBackend;

internal static unsafe class DrawSubmitPlan
{
    /// <summary>
    /// The vertices one command uses: the smallest and largest of its <paramref name="count"/>
    /// indices (count &gt; 0). Linear in count; over a frame that is the frame's index count.
    /// </summary>
    public static void IndexRange(ushort* idx, int count, out int min, out int max)
    {
        int lo = ushort.MaxValue, hi = 0;
        for (int i = 0; i < count; i++)
        {
            int v = idx[i];
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        min = lo;
        max = hi;
    }

    /// <summary>dst[i] = src[i] - by, for the <paramref name="count"/> indices of a command whose smallest index is <paramref name="by"/>.</summary>
    public static void Rebase(ushort* src, ushort* dst, int count, int by)
    {
        for (int i = 0; i < count; i++)
            dst[i] = (ushort)(src[i] - by);
    }

    /// <summary>
    /// What to draw for one command: its first vertex (from the command's VtxOffset) and how many
    /// vertices to hand over. False when an index reaches past the list (a broken list: skipped,
    /// nothing read out of bounds). <paramref name="listVtx"/> is the list's vertex count.
    /// </summary>
    public static bool Plan(ushort* idx, int count, int listVtx, int vtxOffset, out int first, out int numVerts)
    {
        first = 0;
        numVerts = 0;
        if (count <= 0 || vtxOffset < 0 || vtxOffset >= listVtx) return false;
        IndexRange(idx, count, out int min, out int max);
        if (vtxOffset + max >= listVtx) return false;
        first = min;
        numVerts = max - min + 1;
        return true;
    }
}

// ============================================================================
//  RynthCore.Engine - ImGui/FontPixels.cs
//  ImGui's font atlas pixels (RGBA32) as D3D9 A8R8G8B8 rows (BGRA in memory). The bake
//  worker converts them (ImGuiFonts.BakeWorker), so AC's thread only copies rows into the
//  texture (DX9Backend.CreateFontTexture). Pure: tools\EngineRobustnessTests checks it.
// ============================================================================

using System;

namespace RynthCore.Engine.ImGuiBackend;

internal static class FontPixels
{
    /// <summary>R and B swap in each 32-bit pixel (little-endian RGBA: R in the low byte).</summary>
    public static void RgbaToBgra(ReadOnlySpan<uint> rgba, Span<uint> bgra)
    {
        if (bgra.Length < rgba.Length) throw new ArgumentException("destination too small", nameof(bgra));
        for (int i = 0; i < rgba.Length; i++)
        {
            uint p = rgba[i];
            bgra[i] = (p & 0xFF00FF00u) | ((p & 0x000000FFu) << 16) | ((p >> 16) & 0x000000FFu);
        }
    }
}

// ============================================================================
//  RynthCore.StatusAgent - Dat/DxtUtil.cs
//  DXT1 / DXT3 / DXT5 (BC1 / BC2 / BC3) block decompression to RGBA bytes.
//  Written for RynthCore (MIT) on 2026-10-05 from the public S3TC / DirectX
//  block-compression format description:
//
//    Images are stored as 4 x 4 pixel blocks, left to right, top to bottom.
//    Colour block (8 bytes, the whole of a DXT1 block and the second half of
//    a DXT3/DXT5 block): two RGB565 end-point colours c0, c1 (uint16 each),
//    then 16 two-bit indices, one per pixel, row by row, low bits first.
//      DXT1, c0 > c1:  index 2 = (2*c0 + c1) / 3, index 3 = (c0 + 2*c1) / 3
//      DXT1, c0 <= c1: index 2 = (c0 + c1) / 2,   index 3 = transparent black
//      DXT3 / DXT5 always use the four-colour form.
//    DXT3 alpha (first 8 bytes): 16 explicit four-bit alphas, low nibble first.
//    DXT5 alpha (first 8 bytes): end points a0, a1 (one byte each), then 16
//    three-bit indices (48 bits, little-endian, low bits first):
//      a0 > a1:  indices 2..7 = 6 interpolated values between a0 and a1
//      a0 <= a1: indices 2..5 = 4 interpolated values, 6 = 0, 7 = 255
//
//  Used by the agent's IconDecoder and, linked, by the engine's
//  UI/ScriptWindows/AcIconDecoder.cs (and RynthSuite's ScriptWindows tests).
// ============================================================================

using System;
using System.IO;

namespace RynthCore.Imaging
{
    /// <summary>DXT1/3/5 decompression; output is width * height * 4 bytes, R, G, B, A.</summary>
    public static class DxtUtil
    {
        internal static byte[] DecompressDxt1(byte[] imageData, int width, int height) =>
            Decompress(imageData, width, height, BlockKind.Dxt1);

        internal static byte[] DecompressDxt1(Stream imageStream, int width, int height) =>
            DecompressDxt1(ReadAll(imageStream), width, height);

        internal static byte[] DecompressDxt3(byte[] imageData, int width, int height) =>
            Decompress(imageData, width, height, BlockKind.Dxt3);

        internal static byte[] DecompressDxt3(Stream imageStream, int width, int height) =>
            DecompressDxt3(ReadAll(imageStream), width, height);

        internal static byte[] DecompressDxt5(byte[] imageData, int width, int height) =>
            Decompress(imageData, width, height, BlockKind.Dxt5);

        internal static byte[] DecompressDxt5(Stream imageStream, int width, int height) =>
            DecompressDxt5(ReadAll(imageStream), width, height);

        private enum BlockKind { Dxt1, Dxt3, Dxt5 }

        private static byte[] ReadAll(Stream s)
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        private static byte[] Decompress(byte[] src, int width, int height, BlockKind kind)
        {
            if (width <= 0 || height <= 0) return Array.Empty<byte>();
            var rgba = new byte[width * height * 4];
            int blockBytes = kind == BlockKind.Dxt1 ? 8 : 16;
            int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;

            Span<byte> palette = stackalloc byte[16];   // 4 colours x RGBA
            Span<byte> alpha = stackalloc byte[16];     // per pixel (DXT3/5)
            Span<byte> alphaTable = stackalloc byte[8]; // DXT5 end points + interpolated
            int at = 0;
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++, at += blockBytes)
                {
                    if (at + blockBytes > src.Length) return rgba; // short data: the rest stays clear
                    int colourAt = at;
                    bool hasAlphaBlock = kind != BlockKind.Dxt1;
                    if (kind == BlockKind.Dxt3)
                    {
                        for (int i = 0; i < 16; i++)
                        {
                            int nibble = (src[at + (i >> 1)] >> ((i & 1) * 4)) & 0xF;
                            alpha[i] = (byte)(nibble * 17);
                        }
                        colourAt += 8;
                    }
                    else if (kind == BlockKind.Dxt5)
                    {
                        byte a0 = src[at], a1 = src[at + 1];
                        alphaTable[0] = a0;
                        alphaTable[1] = a1;
                        if (a0 > a1)
                        {
                            for (int i = 1; i <= 6; i++) alphaTable[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
                        }
                        else
                        {
                            for (int i = 1; i <= 4; i++) alphaTable[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                            alphaTable[6] = 0;
                            alphaTable[7] = 255;
                        }
                        ulong bits = 0;
                        for (int i = 0; i < 6; i++) bits |= (ulong)src[at + 2 + i] << (8 * i);
                        for (int i = 0; i < 16; i++) alpha[i] = alphaTable[(int)((bits >> (3 * i)) & 7)];
                        colourAt += 8;
                    }

                    ushort c0 = (ushort)(src[colourAt] | (src[colourAt + 1] << 8));
                    ushort c1 = (ushort)(src[colourAt + 2] | (src[colourAt + 3] << 8));
                    Expand565(c0, palette.Slice(0, 4));
                    Expand565(c1, palette.Slice(4, 4));
                    if (kind != BlockKind.Dxt1 || c0 > c1)
                    {
                        for (int ch = 0; ch < 3; ch++)
                        {
                            palette[8 + ch] = (byte)((2 * palette[ch] + palette[4 + ch]) / 3);
                            palette[12 + ch] = (byte)((palette[ch] + 2 * palette[4 + ch]) / 3);
                        }
                        palette[11] = 255;
                        palette[15] = 255;
                    }
                    else
                    {
                        for (int ch = 0; ch < 3; ch++)
                        {
                            palette[8 + ch] = (byte)((palette[ch] + palette[4 + ch]) / 2);
                            palette[12 + ch] = 0;
                        }
                        palette[11] = 255;
                        palette[15] = 0;
                    }

                    uint indices = (uint)(src[colourAt + 4] | (src[colourAt + 5] << 8) | (src[colourAt + 6] << 16) | (src[colourAt + 7] << 24));
                    for (int py = 0; py < 4; py++)
                    {
                        int y = by * 4 + py;
                        if (y >= height) break;
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px;
                            if (x >= width) continue;
                            int p = py * 4 + px;
                            int idx = (int)((indices >> (2 * p)) & 3);
                            int o = (y * width + x) * 4;
                            rgba[o] = palette[idx * 4];
                            rgba[o + 1] = palette[idx * 4 + 1];
                            rgba[o + 2] = palette[idx * 4 + 2];
                            rgba[o + 3] = hasAlphaBlock ? alpha[p] : palette[idx * 4 + 3];
                        }
                    }
                }
            }
            return rgba;
        }

        // RGB565 to 8-bit channels: v * 255 / max, rounded with the (t + t / 2^n) / 2^n
        // approximation (t = v * 255 + 2^(n-1)), so the output matches, bit for bit,
        // the decoder this file replaced.
        private static void Expand565(ushort c, Span<byte> rgba)
        {
            rgba[0] = Scale((c >> 11) & 0x1F, 5);
            rgba[1] = Scale((c >> 5) & 0x3F, 6);
            rgba[2] = Scale(c & 0x1F, 5);
            rgba[3] = 255;
        }

        private static byte Scale(int v, int bits)
        {
            int t = v * 255 + (1 << (bits - 1));
            return (byte)(((t >> bits) + t) >> bits);
        }
    }
}

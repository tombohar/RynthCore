// ============================================================================
//  RynthCore.StatusAgent - Dat/IconDecoder.cs
//  An item icon (a 0x06 Texture in client_portal.dat) as RGBA pixels, for the
//  phone remote. Written for RynthCore (MIT) on 2026-10-05.
//
//  Written from the Texture file layout as Chorizite's DatReaderWriter (MIT,
//  see the notice in Dat/DatDatabase.cs) describes it (RenderSurface and
//  Palette) and the Direct3D pixel-format definitions:
//    Texture 0x06: id u32, data category u32, width i32, height i32,
//                  format u32, length i32, data[length], and for the palette
//                  formats (P8, INDEX16) the default palette id u32.
//    Palette 0x04: id u32, colour count i32, colours (u32 A8R8G8B8 each).
//  Formats in the retail portal dat: A8R8G8B8, R8G8B8, R5G6B5, A4R4G4B4, A8,
//  P8, INDEX16, the landscape R8G8B8 / alpha customs and DXT1/3/5 (DxtUtil.cs).
//  Raw JPEG textures are not decoded.
// ============================================================================

using RynthCore.Imaging;
using RynthCore2.TerrainData;

namespace RynthCore.StatusAgent;

/// <summary>Decodes an AC item icon (a 0x06 Texture in portal.dat) to RGBA pixels.</summary>
internal sealed class IconDecoder
{
    private readonly DatDatabase _portal;
    public IconDecoder(DatDatabase portal) { _portal = portal; }

    /// <summary>Decoded RGBA bitmap (Pixels = w*h*4, bytes in R,G,B,A order).</summary>
    public sealed class Rgba
    {
        public int Width;
        public int Height;
        public byte[] Pixels = System.Array.Empty<byte>();
    }

    private const uint A8R8G8B8 = 0x15, R8G8B8 = 0x14, R5G6B5 = 0x17, A4R4G4B4 = 0x1A, A8 = 0x1C,
        P8 = 0x29, Index16 = 0x65, LscapeR8G8B8 = 0xF3, LscapeAlpha = 0xF4,
        Dxt1 = 0x31545844, Dxt3 = 0x33545844, Dxt5 = 0x35545844;

    /// <summary>The texture <paramref name="textureId"/> as RGBA, or null (missing, unsupported or damaged).</summary>
    public Rgba? Decode(uint textureId)
    {
        try
        {
            byte[]? file = _portal.GetFileData(textureId);
            if (file == null || file.Length < 24) return null;
            var r = new ReadOnlySpan<byte>(file);
            int w = I32(r, 8), h = I32(r, 12);
            uint format = U32(r, 16);
            int length = I32(r, 20);
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096 || length < 0 || 24L + length > file.Length) return null;
            var src = r.Slice(24, length);
            int n = w * h;

            switch (format)
            {
                case Dxt1: return Make(w, h, DxtUtil.DecompressDxt1(src.ToArray(), w, h));
                case Dxt3: return Make(w, h, DxtUtil.DecompressDxt3(src.ToArray(), w, h));
                case Dxt5: return Make(w, h, DxtUtil.DecompressDxt5(src.ToArray(), w, h));
            }

            var px = new byte[n * 4];
            switch (format)
            {
                case A8R8G8B8:   // B, G, R, A in memory
                    if (length < n * 4) return null;
                    for (int i = 0; i < n; i++) Put(px, i, src[i * 4 + 2], src[i * 4 + 1], src[i * 4], src[i * 4 + 3]);
                    break;
                case R8G8B8:     // B, G, R
                    if (length < n * 3) return null;
                    for (int i = 0; i < n; i++) Put(px, i, src[i * 3 + 2], src[i * 3 + 1], src[i * 3], 255);
                    break;
                case LscapeR8G8B8: // R, G, B
                    if (length < n * 3) return null;
                    for (int i = 0; i < n; i++) Put(px, i, src[i * 3], src[i * 3 + 1], src[i * 3 + 2], 255);
                    break;
                case R5G6B5:
                    if (length < n * 2) return null;
                    for (int i = 0; i < n; i++)
                    {
                        int v = src[i * 2] | (src[i * 2 + 1] << 8);
                        // Top bits only (low bits left 0), as the decoder this replaced did.
                        Put(px, i, (byte)(((v >> 11) & 0x1F) << 3), (byte)(((v >> 5) & 0x3F) << 2), (byte)((v & 0x1F) << 3), 255);
                    }
                    break;
                case A4R4G4B4:
                    if (length < n * 2) return null;
                    for (int i = 0; i < n; i++)
                    {
                        int v = src[i * 2] | (src[i * 2 + 1] << 8);
                        Put(px, i, (byte)(((v >> 8) & 0xF) * 17), (byte)(((v >> 4) & 0xF) * 17), (byte)((v & 0xF) * 17), (byte)(((v >> 12) & 0xF) * 17));
                    }
                    break;
                case A8:          // one byte a pixel, shown as an opaque grey level
                case LscapeAlpha:
                    if (length < n) return null;
                    for (int i = 0; i < n; i++) Put(px, i, src[i], src[i], src[i], 255);
                    break;
                case P8:
                case Index16:
                {
                    int bpp = format == P8 ? 1 : 2;
                    if (length < n * bpp || 24L + length + 4 > file.Length) return null;
                    uint[]? palette = LoadPalette(U32(r, 24 + length));
                    if (palette == null) return null;
                    for (int i = 0; i < n; i++)
                    {
                        int idx = bpp == 1 ? src[i] : src[i * 2] | (src[i * 2 + 1] << 8);
                        uint argb = idx < palette.Length ? palette[idx] : 0;
                        Put(px, i, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
                    }
                    break;
                }
                default:
                    return null;
            }
            return Make(w, h, px);
        }
        catch
        {
            return null;
        }
    }

    // A 0x04 Palette's colours (A8R8G8B8), or null.
    private uint[]? LoadPalette(uint paletteId)
    {
        byte[]? file = _portal.GetFileData(paletteId);
        if (file == null || file.Length < 8) return null;
        int count = I32(file, 4);
        if (count < 0 || 8L + count * 4L > file.Length) return null;
        var colours = new uint[count];
        for (int i = 0; i < count; i++) colours[i] = U32(file, 8 + i * 4);
        return colours;
    }

    private static void Put(byte[] px, int i, byte r, byte g, byte b, byte a)
    {
        px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = a;
    }

    private static Rgba Make(int w, int h, byte[] rgba) => new() { Width = w, Height = h, Pixels = rgba };
    private static int I32(ReadOnlySpan<byte> s, int at) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(s.Slice(at));
    private static uint U32(ReadOnlySpan<byte> s, int at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(at));
}

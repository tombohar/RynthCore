using ACE.DatLoader;
using RynthCore2.TerrainData;

namespace RynthCore.StatusAgent;

/// <summary>
/// Decodes an AC item ICON to RGBA pixels. An item's icon DID (PWD _iconID, range 0x06xxxxxx) points
/// straight at a 0x06 Texture in portal.dat, so this is the Texture path only — trimmed from
/// AcClientReborn's AcTextureDecoder (no Surface/SurfaceTexture/clothing). Format byte-layout + the
/// format constants are ported verbatim from ACEmulator's DatLoader.
/// </summary>
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

    // Palette (0x04): Id(u32), Colors(List<uint>: i32 count + u32[] ARGB).
    private uint[]? LoadPalette(uint paletteId)
    {
        byte[]? data = _portal.GetFileData(paletteId);
        if (data == null || data.Length < 8) return null;
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        r.ReadUInt32();
        int n = r.ReadInt32();
        if (n <= 0 || n > 100000) return null;
        var pal = new uint[n];
        for (int i = 0; i < n; i++)
        {
            if (ms.Position + 4 > ms.Length) { System.Array.Resize(ref pal, i); break; }
            pal[i] = r.ReadUInt32();
        }
        return pal;
    }

    // Texture (0x06): Id(u32),Unknown(i32),Width(i32),Height(i32),Format(u32),Length(i32),Source[Length],
    //                 if INDEX16/P8: DefaultPaletteId(u32).
    public Rgba? Decode(uint textureId)
    {
        byte[]? data = _portal.GetFileData(textureId);
        if (data == null || data.Length < 24) return null;
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        r.ReadUInt32(); r.ReadInt32();
        int w = r.ReadInt32(), h = r.ReadInt32();
        uint fmt = r.ReadUInt32();
        int len = r.ReadInt32();
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096 || len < 0 || ms.Position + len > ms.Length) return null;
        byte[] src = r.ReadBytes(len);
        uint defPal = 0;
        if (fmt == 101 /*INDEX16*/ || fmt == 41 /*P8*/) { if (ms.Position + 4 <= ms.Length) defPal = r.ReadUInt32(); }

        var rgba = new byte[w * h * 4];
        void Px(int i, int rr, int gg, int bb, int aa) { int o = i * 4; rgba[o] = (byte)rr; rgba[o + 1] = (byte)gg; rgba[o + 2] = (byte)bb; rgba[o + 3] = (byte)aa; }

        switch (fmt)
        {
            case 827611204: return Wrap(DxtUtil.DecompressDxt1(src, w, h), w, h); // DXT1
            case 861165636: return Wrap(DxtUtil.DecompressDxt3(src, w, h), w, h); // DXT3
            case 894720068: return Wrap(DxtUtil.DecompressDxt5(src, w, h), w, h); // DXT5
            case 20: // R8G8B8 (stored B,G,R)
                for (int i = 0; i < w * h && i * 3 + 2 < src.Length; i++) Px(i, src[i * 3 + 2], src[i * 3 + 1], src[i * 3], 255);
                break;
            case 243: // CUSTOM_LSCAPE_R8G8B8 (stored R,G,B)
                for (int i = 0; i < w * h && i * 3 + 2 < src.Length; i++) Px(i, src[i * 3], src[i * 3 + 1], src[i * 3 + 2], 255);
                break;
            case 21: // A8R8G8B8
            case 22: // X8R8G8B8
                for (int i = 0; i < w * h && i * 4 + 3 < src.Length; i++)
                { int b = src[i * 4], g = src[i * 4 + 1], rr = src[i * 4 + 2], a = fmt == 22 ? 255 : src[i * 4 + 3]; Px(i, rr, g, b, a); }
                break;
            case 23: // R5G6B5
                for (int i = 0; i < w * h && i * 2 + 1 < src.Length; i++)
                { ushort v = (ushort)(src[i * 2] | (src[i * 2 + 1] << 8)); Px(i, ((v >> 11) & 0x1F) << 3, ((v >> 5) & 0x3F) << 2, (v & 0x1F) << 3, 255); }
                break;
            case 26: // A4R4G4B4
                for (int i = 0; i < w * h && i * 2 + 1 < src.Length; i++)
                { ushort v = (ushort)(src[i * 2] | (src[i * 2 + 1] << 8)); Px(i, ((v >> 8) & 0xF) * 17, ((v >> 4) & 0xF) * 17, (v & 0xF) * 17, ((v >> 12) & 0xF) * 17); }
                break;
            case 28:  // A8 (greyscale)
            case 244: // LSCAPE_ALPHA
                for (int i = 0; i < w * h && i < src.Length; i++) Px(i, src[i], src[i], src[i], 255);
                break;
            case 101: // INDEX16
            case 41:  // P8
            {
                uint[]? pal = LoadPalette(defPal);
                if (pal == null || pal.Length == 0) return null;
                bool p8 = fmt == 41;
                for (int i = 0; i < w * h; i++)
                {
                    int idx;
                    if (p8) { if (i >= src.Length) break; idx = src[i]; }
                    else { if (i * 2 + 1 >= src.Length) break; idx = (ushort)(src[i * 2] | (src[i * 2 + 1] << 8)); }
                    uint cc = pal[idx % pal.Length];
                    Px(i, (int)((cc >> 16) & 0xFF), (int)((cc >> 8) & 0xFF), (int)(cc & 0xFF), (int)((cc >> 24) & 0xFF));
                }
                break;
            }
            default:
                return null; // unhandled format
        }
        return new Rgba { Width = w, Height = h, Pixels = rgba };
    }

    private static Rgba Wrap(byte[] rgba, int w, int h) => new Rgba { Width = w, Height = h, Pixels = rgba }; // DxtUtil already RGBA
}

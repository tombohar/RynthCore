// ============================================================================
//  RynthCore.Engine - UI/ScriptWindows/AcIconDecoder.cs
//  Script windows: decodes Asheron's Call icons (0x06 Texture files in
//  client_portal.dat) to RGBA, and stacks an item's underlay, icon and overlay
//  into one picture. Pure code: no AC memory, no D3D, no engine state, so the
//  RynthSuite offline tests compile it in and decode real icons.
//
//  Written for RynthCore (MIT); the dat reader and the texture decoding were
//  rewritten on 2026-10-05 from the public dat container layout and the
//  Texture / Palette layouts as Chorizite's DatReaderWriter (MIT) describes
//  them (its notice is in RynthCore.StatusAgent/Dat/DatDatabase.cs):
//    - header at 0x140: magic, block size, file size, ..., root node offset
//      (uint32 at 0x160); a block starts with the next block's offset (0 =
//      last); the directory is a B-tree of nodes (62 child offsets, an entry
//      count, entries of 24 bytes: flags, id, offset, size, date, iteration);
//    - Texture: id u32, data category u32, width i32, height i32, format u32,
//      length i32, data[length], and for P8 / INDEX16 a palette id u32;
//      Palette: id u32, count i32, count x u32 A8R8G8B8.
//  DXT1/3/5 use the StatusAgent's DxtUtil.cs, linked into the engine project.
//  The pixels are the same as the StatusAgent's Dat/IconDecoder.cs (the phone
//  remote's icons) produces.
//
//  Threads: whatever thread owns the AcDatFile (the icon worker). Every read is
//  bounds-checked; a bad file returns null, never throws out of Decode/Compose.
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using RynthCore.Imaging;

namespace RynthCore.Engine.UI.ScriptWindows;

/// <summary>A read-only portal.dat opened for a batch of reads. Not thread-safe.</summary>
internal sealed class AcDatFile : IDisposable
{
    private const long HeaderOffset = 0x140;
    private const int ChildSlots = 62, MaxEntries = 61, EntryBytes = 24;
    private const int EntriesOffset = ChildSlots * 4 + 4;                    // 252
    private const int NodeBytes = EntriesOffset + MaxEntries * EntryBytes;   // 1716
    private const int DepthLimit = 16;

    private readonly FileStream _fs;
    private readonly long _length;
    private readonly int _payload;   // block size - 4
    private readonly uint _root;
    private readonly byte[] _node = new byte[NodeBytes];

    private AcDatFile(FileStream fs, uint blockSize, uint root)
    {
        _fs = fs;
        _length = fs.Length;
        _payload = (int)blockSize - 4;
        _root = root;
    }

    /// <summary>Opens <paramref name="path"/> read-only, sharing everything (AC holds its own handles).</summary>
    public static AcDatFile? Open(string path, out string why)
    {
        FileStream? fs = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            Span<byte> header = stackalloc byte[36];
            fs.Position = HeaderOffset;
            if (!ReadFully(fs, header)) { why = "short header"; fs.Dispose(); return null; }
            uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4));
            uint root = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(32));
            if (blockSize <= 4 || blockSize > 65536 || root == 0 || root >= fs.Length)
            {
                why = $"not a dat file (block size {blockSize}, root 0x{root:X8})";
                fs.Dispose();
                return null;
            }
            why = "";
            return new AcDatFile(fs, blockSize, root);
        }
        catch (Exception ex)
        {
            fs?.Dispose();
            why = ex.Message;
            return null;
        }
    }

    /// <summary>client_portal.dat (or portal.dat) next to the running exe or in the current directory; null when none.</summary>
    public static string? FindPortalDat()
    {
        var dirs = new List<string>(2);
        try
        {
            string? exe = Environment.ProcessPath;
            string? dir = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir)) dirs.Add(dir);
        }
        catch { }
        try { dirs.Add(Environment.CurrentDirectory); } catch { }
        foreach (string dir in dirs)
            foreach (string name in new[] { "client_portal.dat", "portal.dat" })
            {
                try
                {
                    string full = Path.Combine(dir, name);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
        return null;
    }

    /// <summary>A file's bytes by id, or null (not in the dat, over <paramref name="maxSize"/>, or unreadable).</summary>
    public byte[]? Read(uint fileId, int maxSize)
    {
        try
        {
            uint at = _root;
            for (int depth = 0; depth < DepthLimit && at != 0; depth++)
            {
                int got = ReadChain(at, _node);
                if (got < EntriesOffset) return null;
                int count = BinaryPrimitives.ReadInt32LittleEndian(_node.AsSpan(EntriesOffset - 4));
                if (count < 0 || count > MaxEntries || got < EntriesOffset + count * EntryBytes) return null;

                // Entries are sorted by id: binary search for the first id >= fileId.
                int lo = 0, hi = count;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (IdAt(mid) < fileId) lo = mid + 1; else hi = mid;
                }
                if (lo < count && IdAt(lo) == fileId)
                {
                    var e = _node.AsSpan(EntriesOffset + lo * EntryBytes);
                    uint offset = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(8));
                    uint size = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(12));
                    if (size == 0 || size > (uint)maxSize || size > _length) return null;
                    var data = new byte[size];
                    return ReadChain(offset, data) == data.Length ? data : null;
                }
                // Not in this node: a leaf (first child offset 0) ends the search; otherwise
                // child `lo` holds the ids between entries lo-1 and lo.
                if (BinaryPrimitives.ReadUInt32LittleEndian(_node) == 0) return null;
                at = BinaryPrimitives.ReadUInt32LittleEndian(_node.AsSpan(lo * 4));
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private uint IdAt(int i) => BinaryPrimitives.ReadUInt32LittleEndian(_node.AsSpan(EntriesOffset + i * EntryBytes + 4));

    // Copies the block chain at byte offset `first` into `dest`; returns the bytes
    // filled (fewer than dest.Length when the chain ends first), -1 on a bad link.
    private int ReadChain(uint first, Span<byte> dest)
    {
        long block = first;
        int done = 0;
        Span<byte> link = stackalloc byte[4];
        while (done < dest.Length)
        {
            if (block == 0) return done;
            if (block + 4 > _length) return -1;
            int take = Math.Min(_payload, dest.Length - done);
            _fs.Position = block;
            if (!ReadFully(_fs, link) || !ReadFully(_fs, dest.Slice(done, take))) return -1;
            done += take;
            block = BinaryPrimitives.ReadUInt32LittleEndian(link);
        }
        return done;
    }

    private static bool ReadFully(FileStream fs, Span<byte> dest)
    {
        int got = 0;
        while (got < dest.Length)
        {
            int n = fs.Read(dest.Slice(got));
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    public void Dispose() => _fs.Dispose();
}

/// <summary>An RGBA picture (Pixels = Width * Height * 4, bytes R, G, B, A).</summary>
internal sealed class AcIconImage
{
    public int Width;
    public int Height;
    public byte[] Pixels = Array.Empty<byte>();
}

internal static class AcIconDecoder
{
    /// <summary>Icons are 32 x 32; anything bigger than this (a UI picture, not an icon) is refused.</summary>
    public const int MaxSide = 128;
    /// <summary>The largest side a retail UI picture (a window background, a title bar) may have.</summary>
    public const int MaxUiSide = 512;
    private const int MaxTextureFile = 1024 * 1024;
    private const int MaxPaletteFile = 256 * 1024;

    /// <summary>UtilityBelt's rule for an icon id: below 0x06000000, 0x06000000 is added.</summary>
    public static uint NormalizeIconId(uint id) => id < 0x06000000u ? id + 0x06000000u : id;

    /// <summary>0x06xxxxxx: a Texture file (icons live there).</summary>
    public static bool IsTextureId(uint id) => (id >> 24) == 0x06;

    /// <summary>
    /// The item picture AC shows: the underlay, then the icon, then the overlay, each drawn over
    /// the last (alpha blended) at the icon's size. 0 = no underlay / overlay. Null when the icon
    /// itself can't be decoded; an underlay or overlay that can't be is left out.
    /// </summary>
    public static AcIconImage? Compose(AcDatFile dat, uint icon, uint underlay, uint overlay, uint rimTexture = 0)
    {
        AcIconImage? baseImage = Decode(dat, icon);
        if (baseImage == null) return null;
        RepaintOutline(baseImage, BandDepth, rimTexture != 0 ? RimImage(dat, rimTexture) : null);
        // The server gives some items (foci) an overlay that IS the icon. Drawn over the repainted icon it
        // would bring the dat's white outline back, and it adds nothing the icon has not already drawn.
        if (overlay == icon) overlay = 0;
        AcIconImage? under = underlay != 0 ? Decode(dat, underlay) : null;
        AcIconImage? over = overlay != 0 ? Decode(dat, overlay) : null;
        if (under == null && over == null) return baseImage;

        int w = baseImage.Width, h = baseImage.Height;
        var outPx = new byte[w * h * 4];
        if (under != null) Over(outPx, w, h, under);
        Over(outPx, w, h, baseImage);
        if (over != null) Over(outPx, w, h, over);
        return new AcIconImage { Width = w, Height = h, Pixels = outPx };
    }

    /// <summary>The outline colour retail paints over an icon's authored white band (measured in AcClientReborn's IconOutline).</summary>
    private const byte OutlineR = 0x30, OutlineG = 0x2D, OutlineB = 0x7B;
    // The outline of an item with effects (magical, an element...) is not one colour: retail lets the dat's
    // effect texture (AcIconPlates.RimFor: a vertical gradient, pale at the top to deep at the bottom) show
    // through the icon's outline band, texel for texel. Read off a retail robe, whose outline runs from pale
    // cyan at the head to deep blue at the hem, which is the blue effect texture 0x060011CA.
    private static readonly Dictionary<uint, AcIconImage?> RimCache = new();

    private static AcIconImage? RimImage(AcDatFile dat, uint id)
    {
        lock (RimCache)
        {
            if (!RimCache.TryGetValue(id, out AcIconImage? img))
                RimCache[id] = img = Decode(dat, id);
            return img;
        }
    }
    private const int BandDepth = 2;

    /// <summary>
    /// The dat authors every icon's outline as near-white (pure white, or red on a gem) and retail
    /// repaints it, so drawing the dat as-is leaves a white halo. The band is the near-white texels
    /// within <see cref="BandDepth"/> texels of the silhouette edge (a texel's distance is 1 when it
    /// touches transparency, 2 when it touches one that does). It is a distance, not a chain from
    /// the edge, because some art (the foci) has a one-texel dark shadow OUTSIDE its white band: the
    /// white never touches transparency there, and an adjacency test left it white along that side.
    /// The depth cap keeps a genuinely white item (a flame) from becoming a silhouette, and interior
    /// highlights deeper than that survive. Measured over every 32x32 icon in 0x06000F00..0x06007000.
    /// </summary>
    internal static void RepaintOutline(AcIconImage img, int depth = BandDepth, AcIconImage? rim = null)
    {
        int w = img.Width, h = img.Height;
        byte[] px = img.Pixels;
        if (px.Length < w * h * 4) return;
        bool Opaque(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && px[(y * w + x) * 4 + 3] >= 128;
        bool NearWhite(int x, int y)
        {
            int o = (y * w + x) * 4;
            return px[o + 3] >= 128 && px[o] >= 226 && px[o + 1] >= 226 && px[o + 2] >= 226;
        }
        // Distance 1 = opaque and touching transparency (or the image edge); n = touching a distance n-1 texel.
        var dist = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (Opaque(x, y) && !(Opaque(x - 1, y) && Opaque(x + 1, y) && Opaque(x, y - 1) && Opaque(x, y + 1)))
                    dist[y * w + x] = 1;
        // One level past the band is taken only where it continues the band (a near-white texel
        // touching a repainted one): art with a two-texel shadow outside its white leaves a straggler.
        int reach = depth + 1;
        for (int lvl = 2; lvl <= reach; lvl++)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (dist[y * w + x] != 0 || !Opaque(x, y)) continue;
                    int prev = lvl - 1;
                    bool near = (x > 0 && dist[y * w + x - 1] == prev) || (x + 1 < w && dist[y * w + x + 1] == prev)
                        || (y > 0 && dist[(y - 1) * w + x] == prev) || (y + 1 < h && dist[(y + 1) * w + x] == prev);
                    if (near) dist[y * w + x] = (byte)lvl;
                }
        var band = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (dist[y * w + x] != 0 && dist[y * w + x] <= depth && NearWhite(x, y)) band[y * w + x] = true;
        var extra = new List<int>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (dist[y * w + x] != reach || !NearWhite(x, y)) continue;
                if ((x > 0 && band[y * w + x - 1]) || (x + 1 < w && band[y * w + x + 1])
                    || (y > 0 && band[(y - 1) * w + x]) || (y + 1 < h && band[(y + 1) * w + x]))
                    extra.Add(y * w + x);
            }
        foreach (int i in extra) band[i] = true;
        for (int i = 0; i < w * h; i++)
        {
            if (!band[i]) continue;
            int o = i * 4;
            if (rim != null && rim.Width > 0 && rim.Height > 0)
            {
                int x = i % w, y = i / w;
                int ro = ((y * rim.Height / h) * rim.Width + x * rim.Width / w) * 4;
                if (ro + 2 < rim.Pixels.Length)
                {
                    px[o] = rim.Pixels[ro]; px[o + 1] = rim.Pixels[ro + 1]; px[o + 2] = rim.Pixels[ro + 2];
                    continue;
                }
            }
            px[o] = OutlineR; px[o + 1] = OutlineG; px[o + 2] = OutlineB;
        }
    }

    /// <summary>Draws <paramref name="src"/> over <paramref name="dst"/> (w x h), scaled nearest-neighbour when sizes differ.</summary>
    private static void Over(byte[] dst, int w, int h, AcIconImage src)
    {
        for (int y = 0; y < h; y++)
        {
            int sy = src.Height == h ? y : y * src.Height / h;
            for (int x = 0; x < w; x++)
            {
                int sx = src.Width == w ? x : x * src.Width / w;
                int s = (sy * src.Width + sx) * 4, d = (y * w + x) * 4;
                int sa = src.Pixels[s + 3];
                if (sa == 0) continue;
                if (sa == 255)
                {
                    dst[d] = src.Pixels[s];
                    dst[d + 1] = src.Pixels[s + 1];
                    dst[d + 2] = src.Pixels[s + 2];
                    dst[d + 3] = 255;
                    continue;
                }
                int da = dst[d + 3];
                int outA = sa + da * (255 - sa) / 255;
                if (outA <= 0) continue;
                for (int c = 0; c < 3; c++)
                    dst[d + c] = (byte)((src.Pixels[s + c] * sa + dst[d + c] * da * (255 - sa) / 255) / outA);
                dst[d + 3] = (byte)outA;
            }
        }
    }

    /// <summary>
    /// A 0x06 Texture as RGBA, or null (not a texture id, missing, an unsupported format, larger
    /// than <see cref="MaxSide"/>, or short data). Layout: id u32, data category u32, width i32,
    /// height i32, format u32, length i32, data[length], then for INDEX16/P8 a palette id u32.
    /// </summary>
    public static AcIconImage? Decode(AcDatFile dat, uint textureId) => Decode(dat, textureId, MaxSide);

    /// <summary>
    /// As <see cref="Decode(AcDatFile, uint)"/>, allowing sides up to <paramref name="maxSide"/>
    /// (at most <see cref="MaxUiSide"/>): the retail UI pictures (ImGui/RetailSprites).
    /// </summary>
    public static AcIconImage? Decode(AcDatFile dat, uint textureId, int maxSide)
    {
        if (!IsTextureId(textureId)) return null;
        try
        {
            byte[]? data = dat.Read(textureId, MaxTextureFile);
            return data == null ? null : DecodeTextureFile(data, id => dat.Read(id, MaxPaletteFile), maxSide);
        }
        catch
        {
            return null;
        }
    }

    private const uint FmtR8G8B8 = 0x14, FmtA8R8G8B8 = 0x15, FmtR5G6B5 = 0x17, FmtA4R4G4B4 = 0x1A, FmtA8 = 0x1C,
        FmtP8 = 0x29, FmtIndex16 = 0x65, FmtLscapeR8G8B8 = 0xF3, FmtLscapeAlpha = 0xF4,
        FmtDxt1 = 0x31545844, FmtDxt3 = 0x33545844, FmtDxt5 = 0x35545844;

    /// <summary>A Texture file's bytes as RGBA (see <see cref="Decode(AcDatFile, uint)"/>); <paramref name="readPalette"/> loads a 0x04 palette.</summary>
    internal static AcIconImage? DecodeTextureFile(byte[] data, Func<uint, byte[]?> readPalette, int maxSide = MaxSide)
    {
        if (data.Length < 24) return null;
        var file = new ReadOnlySpan<byte>(data);
        int w = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(8));
        int h = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(12));
        uint format = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(16));
        int length = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(20));
        int limit = Math.Min(maxSide, MaxUiSide);
        if (w <= 0 || h <= 0 || w > limit || h > limit) return null;
        if (length < 0 || 24L + length > data.Length) return null;
        var src = file.Slice(24, length);
        int n = w * h;

        switch (format)
        {
            case FmtDxt1: return Dxt(src, w, h, 8, DxtUtil.DecompressDxt1);
            case FmtDxt3: return Dxt(src, w, h, 16, DxtUtil.DecompressDxt3);
            case FmtDxt5: return Dxt(src, w, h, 16, DxtUtil.DecompressDxt5);
        }

        var px = new byte[n * 4];
        switch (format)
        {
            case FmtA8R8G8B8:       // memory order B, G, R, A
                if (length < n * 4) return null;
                for (int i = 0; i < n; i++) Set(px, i, src[i * 4 + 2], src[i * 4 + 1], src[i * 4], src[i * 4 + 3]);
                break;
            case FmtR8G8B8:         // B, G, R
                if (length < n * 3) return null;
                for (int i = 0; i < n; i++) Set(px, i, src[i * 3 + 2], src[i * 3 + 1], src[i * 3], 255);
                break;
            case FmtLscapeR8G8B8:   // R, G, B
                if (length < n * 3) return null;
                for (int i = 0; i < n; i++) Set(px, i, src[i * 3], src[i * 3 + 1], src[i * 3 + 2], 255);
                break;
            case FmtR5G6B5:         // top bits only, as the StatusAgent's decoder
                if (length < n * 2) return null;
                for (int i = 0; i < n; i++)
                {
                    int v = src[i * 2] | (src[i * 2 + 1] << 8);
                    Set(px, i, (byte)(((v >> 11) & 0x1F) << 3), (byte)(((v >> 5) & 0x3F) << 2), (byte)((v & 0x1F) << 3), 255);
                }
                break;
            case FmtA4R4G4B4:
                if (length < n * 2) return null;
                for (int i = 0; i < n; i++)
                {
                    int v = src[i * 2] | (src[i * 2 + 1] << 8);
                    Set(px, i, (byte)(((v >> 8) & 0xF) * 17), (byte)(((v >> 4) & 0xF) * 17), (byte)((v & 0xF) * 17), (byte)(((v >> 12) & 0xF) * 17));
                }
                break;
            case FmtA8:             // one byte a pixel: an opaque grey level
            case FmtLscapeAlpha:
                if (length < n) return null;
                for (int i = 0; i < n; i++) Set(px, i, src[i], src[i], src[i], 255);
                break;
            case FmtP8:
            case FmtIndex16:
            {
                int bytesPer = format == FmtP8 ? 1 : 2;
                if (length < n * bytesPer || 24L + length + 4 > data.Length) return null;
                uint[]? palette = Palette(readPalette(BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(24 + length))));
                if (palette == null) return null;
                for (int i = 0; i < n; i++)
                {
                    int idx = bytesPer == 1 ? src[i] : src[i * 2] | (src[i * 2 + 1] << 8);
                    uint argb = idx < palette.Length ? palette[idx] : 0;
                    Set(px, i, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
                }
                break;
            }
            default:
                return null;   // raw JPEG, anything else
        }
        return new AcIconImage { Width = w, Height = h, Pixels = px };
    }

    private static void Set(byte[] px, int i, byte r, byte g, byte b, byte a)
    {
        px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = a;
    }

    private static AcIconImage? Dxt(ReadOnlySpan<byte> src, int w, int h, int blockBytes, Func<byte[], int, int, byte[]> decompress)
    {
        long need = (long)((w + 3) / 4) * ((h + 3) / 4) * blockBytes;
        if (src.Length < need) return null;
        return new AcIconImage { Width = w, Height = h, Pixels = decompress(src.Slice(0, (int)need).ToArray(), w, h) };
    }

    /// <summary>Palette (0x04): id u32, count i32, count x u32 ARGB.</summary>
    private static uint[]? Palette(byte[]? data)
    {
        if (data == null || data.Length < 8) return null;
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        if (count < 0 || 8L + count * 4L > data.Length) return null;
        var colours = new uint[count];
        for (int i = 0; i < count; i++) colours[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8 + i * 4));
        return colours;
    }
}

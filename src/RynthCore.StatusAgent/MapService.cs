using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RynthCore.StatusAgent;

/// <summary>
/// Serves baked dungeon floor-plan PNGs from RynthAi's on-disk maps
/// (default C:\Games\RynthSuite\RynthAi\Maps\{landblock:X8}_{layer}.bin). Each .bin is a FINISHED
/// top-down raster (20-byte header [Magic, xMin, yMin, w, h] + w*h ARGB uint32 pixels, 0.5 world-units
/// /pixel, row 0 = northernmost) written as a side-effect of the in-game D3D9 bake. The agent only
/// RE-ENCODES the already-baked pixels to PNG — it cannot generate a map (the DAT decoder lives in the
/// plugin). Reads are mid-write tolerant (the bake uses FileMode.Create with no temp+rename) and return
/// null on any problem so the endpoint 404s. Windows-only (System.Drawing), same as IconService.
/// </summary>
internal sealed partial class MapService
{
    private const uint Magic = 0x524D5458u;   // "XTMR" — must match DungeonMapTexture.cs
    private const int MaxDim = 8192;

    private readonly string _mapsDir;
    // PNG cache keyed by (landblock,layer). The value remembers the (mtime,size) it was built from, so a
    // re-bake (the map grows as the bot explores) is detected and the PNG is rebuilt.
    private readonly ConcurrentDictionary<(uint Lb, int Layer), (DateTime Mtime, long Bytes, byte[] Png)> _cache = new();

    public MapService(string mapsDir) => _mapsDir = mapsDir;

    [GeneratedRegex(@"^([0-9A-Fa-f]{8})_(\d+)\.bin$")]
    private static partial Regex FileRx();

    internal sealed record MapEntry(uint Landblock, int Layer, long Bytes, DateTime MtimeUtc, int W, int H, int XMin, int YMin);

    /// <summary>Enumerate every readable .bin map (header parsed for bounds). Never throws.</summary>
    public IReadOnlyList<MapEntry> List()
    {
        var list = new List<MapEntry>();
        string[] files;
        try { files = Directory.GetFiles(_mapsDir, "*.bin"); }
        catch { return list; }
        foreach (string path in files)
        {
            Match m = FileRx().Match(Path.GetFileName(path));
            if (!m.Success) continue;
            if (!uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint lb)) continue;
            if (!int.TryParse(m.Groups[2].Value, out int layer)) continue;
            if (TryReadHeader(path, out int w, out int h, out int xMin, out int yMin, out long len, out DateTime mtime))
                list.Add(new MapEntry(lb, layer, len, mtime, w, h, xMin, yMin));
        }
        return list;
    }

    /// <summary>Decode one .bin to PNG (+ its metadata), or null if unavailable / partial / corrupt.</summary>
    public (byte[] Png, MapEntry Meta)? GetPng(uint lb, int layer)
    {
        string path = Path.Combine(_mapsDir, $"{lb:X8}_{layer}.bin");
        if (!TryReadHeader(path, out int w, out int h, out int xMin, out int yMin, out long len, out DateTime mtime))
            return null;
        var meta = new MapEntry(lb, layer, len, mtime, w, h, xMin, yMin);
        var key = (lb, layer);
        if (_cache.TryGetValue(key, out var c) && c.Mtime == mtime && c.Bytes == len)
            return (c.Png, meta);
        try
        {
            byte[] raw = new byte[(long)w * h * 4];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length < 20L + raw.Length) return null;   // pixels not fully written yet (mid-bake)
                fs.Seek(20, SeekOrigin.Begin);
                int off = 0;
                while (off < raw.Length)
                {
                    int got = fs.Read(raw, off, raw.Length - off);
                    if (got <= 0) return null;
                    off += got;
                }
            }
            byte[] png = BgraBytesToPng(raw, w, h);
            _cache[key] = (mtime, len, png);
            return (png, meta);
        }
        catch { return null; }
    }

    private static bool TryReadHeader(string path, out int w, out int h, out int xMin, out int yMin, out long len, out DateTime mtime)
    {
        w = h = xMin = yMin = 0; len = 0; mtime = default;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < 20) return false;
            len = fi.Length; mtime = fi.LastWriteTimeUtc;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != Magic) return false;
            xMin = br.ReadInt32(); yMin = br.ReadInt32(); w = br.ReadInt32(); h = br.ReadInt32();
            if (w < 1 || w > MaxDim || h < 1 || h > MaxDim) { w = h = 0; return false; }
            if (len < 20L + (long)w * h * 4) { w = h = 0; return false; }   // header present but pixels not yet
            return true;
        }
        catch { w = h = 0; return false; }
    }

    /// The .bin packs each pixel as ARGB uint32 (A&lt;&lt;24|R&lt;&lt;16|G&lt;&lt;8|B); on little-endian that is byte
    /// order B,G,R,A == Format32bppArgb's in-memory layout, so copy the raw bytes verbatim — NO R&lt;-&gt;B
    /// swizzle (unlike IconService.RgbaToPng, whose input is RGBA). Row 0 = north = PNG top (correct).
    private static byte[] BgraBytesToPng(byte[] bgra, int w, int h)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(bgra, y * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
        }
        finally { bmp.UnlockBits(bd); }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}

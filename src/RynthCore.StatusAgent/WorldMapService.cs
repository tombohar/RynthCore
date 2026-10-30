using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RynthCore2.TerrainData;

namespace RynthCore.StatusAgent;

/// <summary>
/// A landscape map of Dereth for the phone (GET /worldmap): every outdoor landblock of the client's
/// cell.dat (record 0xXXYYFFFF: 9x9 terrain words + height indices), one pixel per 24-unit terrain cell,
/// coloured by terrain type, roads drawn, hills shaded from the height indices. 255 landblocks x 8 cells
/// = 2040 pixels square, north up. Rendered once in the background on the first request (the world does
/// not change) and cached as a JPEG next to the agent's other files, so later starts serve it at once.
///
/// Frame (shared with the status "wx"/"wy" and the dungeon floor plans): absolute world units, x west to
/// east, y south to north, 192 units a landblock. Pixel = (wx / 24, 2040 - wy / 24).
/// </summary>
internal sealed class WorldMapService
{
    public const int CellsPerLandblock = 8;
    public const int Landblocks = 255;
    public const int Size = Landblocks * CellsPerLandblock;   // 2040
    public const double UnitsPerPixel = 24.0;
    private const string CacheName = "worldmap-v1.jpg";

    private readonly string _cellDatPath;
    private readonly string _cacheDir;
    private volatile byte[]? _jpeg;
    private int _started;
    private volatile string _state = "idle";

    public WorldMapService(string cellDatPath, string cacheDir)
    {
        _cellDatPath = cellDatPath;
        _cacheDir = cacheDir;
    }

    /// <summary>True when a map can be made here at all (the cell.dat is on this box, or one is cached).</summary>
    public bool Available => _jpeg != null || File.Exists(_cellDatPath) || File.Exists(CachePath);

    public string State => _state;

    private string CachePath => Path.Combine(_cacheDir, CacheName);

    /// <summary>The JPEG, or null while it is being made (the first call starts that). Any thread.</summary>
    public byte[]? GetJpeg()
    {
        var j = _jpeg;
        if (j != null) return j;
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _state = "being drawn";
            _ = Task.Run(LoadOrRender);
        }
        return null;
    }

    private void LoadOrRender()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                _jpeg = File.ReadAllBytes(CachePath);
                _state = "ready";
                AgentLog.Info($"[worldmap] served from cache {CachePath} ({_jpeg.Length / 1024:N0} KB).");
                return;
            }
            if (!File.Exists(_cellDatPath)) { _state = "no cell.dat"; AgentLog.Warn($"[worldmap] cell.dat not found at '{_cellDatPath}'."); return; }

            _state = "being drawn";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            byte[] rgb;
            int found;
            using (var db = new DatDatabase())
            {
                if (!db.Open(_cellDatPath)) { _state = "cell.dat unreadable"; AgentLog.Warn($"[worldmap] could not open '{_cellDatPath}'."); return; }
                rgb = Render(lb => db.GetFileData(((uint)lb << 16) | 0xFFFF), out found);
            }
            byte[] jpeg = EncodeJpeg(rgb, Size, Size, 88);
            try
            {
                Directory.CreateDirectory(_cacheDir);
                string tmp = CachePath + ".tmp";
                File.WriteAllBytes(tmp, jpeg);
                File.Move(tmp, CachePath, overwrite: true);
            }
            catch (Exception ex) { AgentLog.Warn($"[worldmap] cache write failed: {ex.Message}"); }
            _jpeg = jpeg;
            _state = "ready";
            AgentLog.Info($"[worldmap] rendered {found:N0} landblocks in {sw.Elapsed.TotalSeconds:0.0}s ({jpeg.Length / 1024:N0} KB).");
        }
        catch (Exception ex)
        {
            _state = "failed";
            AgentLog.Warn($"[worldmap] render failed: {ex.GetType().Name}: {ex.Message}");
            Interlocked.Exchange(ref _started, 0);   // let a later request try again
        }
    }

    /// <summary>
    /// Paint the whole map into a 24-bit RGB buffer (row 0 = north). <paramref name="readLandblock"/>
    /// returns the raw 0xXXYYFFFF record for a landblock id 0xXXYY, or null where there is none (open sea).
    /// Internal for the tests, which hand it made-up landblocks.
    /// </summary>
    internal static byte[] Render(Func<int, byte[]?> readLandblock, out int found)
    {
        var rgb = new byte[Size * Size * 3];
        Fill(rgb, Sea);
        found = 0;
        var terrain = new ushort[81];
        var heights = new byte[81];
        for (int lbx = 0; lbx < Landblocks; lbx++)
        {
            for (int lby = 0; lby < Landblocks; lby++)
            {
                byte[]? raw = readLandblock((lbx << 8) | lby);
                if (!TryParse(raw, terrain, heights)) continue;
                found++;
                for (int ix = 0; ix < CellsPerLandblock; ix++)
                {
                    for (int iy = 0; iy < CellsPerLandblock; iy++)
                    {
                        int v = ix * 9 + iy;
                        ushort word = terrain[v];
                        int type = (word >> 2) & 0x1F;
                        Rgb c = Palette(type);
                        bool water = type is >= 16 and <= 20;
                        if ((word & 0x3) != 0 && !water) c = Road;
                        if (!water)
                        {
                            // Light from the north-west: a cell rising to the east and falling to the north is lit.
                            int dzx = heights[(ix + 1) * 9 + iy] - heights[v];
                            int dzy = heights[ix * 9 + iy + 1] - heights[v];
                            double s = Math.Clamp(1.0 + 0.045 * (dzx - dzy), 0.62, 1.35);
                            c = c.Scale(s);
                        }
                        int px = lbx * CellsPerLandblock + ix;
                        int py = Size - 1 - (lby * CellsPerLandblock + iy);
                        int o = (py * Size + px) * 3;
                        rgb[o] = c.R; rgb[o + 1] = c.G; rgb[o + 2] = c.B;
                    }
                }
            }
        }
        return rgb;
    }

    /// <summary>Record layout: u32 id, u32 hasObjects, u16 terrain[81], u8 height[81] (vertex = ix*9 + iy).</summary>
    internal static bool TryParse(byte[]? raw, ushort[] terrain, byte[] heights)
    {
        if (raw == null || raw.Length < 8 + 81 * 2 + 81) return false;
        for (int i = 0; i < 81; i++) terrain[i] = BitConverter.ToUInt16(raw, 8 + i * 2);
        Buffer.BlockCopy(raw, 8 + 81 * 2, heights, 0, 81);
        return true;
    }

    /// <summary>Where a world position lands on the map, in pixels (may fall outside for bad input).</summary>
    public static (double X, double Y) ToPixel(double wx, double wy) => (wx / UnitsPerPixel, Size - wy / UnitsPerPixel);

    // ── colours ─────────────────────────────────────────────────────────────

    internal readonly record struct Rgb(byte R, byte G, byte B)
    {
        public Rgb Scale(double s) => new(Clamp(R * s), Clamp(G * s), Clamp(B * s));
        private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
    }

    private static readonly Rgb Sea = new(0x1F, 0x4A, 0x78);
    private static readonly Rgb Road = new(0xA8, 0x93, 0x6C);

    /// <summary>ACE's LandDefs.TerrainType, 0..20; anything else (the reserved slots) a neutral grey.</summary>
    internal static Rgb Palette(int type) => type switch
    {
        0 => new(0x7A, 0x74, 0x66),   // BarrenRock
        1 => new(0x6E, 0x8B, 0x3D),   // Grassland
        2 => new(0xD8, 0xE8, 0xF0),   // Ice
        3 => new(0x4F, 0x7A, 0x2A),   // LushGrass
        4 => new(0x5A, 0x6B, 0x3A),   // MarshSparseSwamp
        5 => new(0x6B, 0x52, 0x36),   // MudRichDirt
        6 => new(0x2E, 0x2A, 0x30),   // ObsidianPlain
        7 => new(0x8C, 0x75, 0x50),   // PackedDirt
        8 => new(0x8A, 0x7B, 0x55),   // PatchyDirt
        9 => new(0x7F, 0x8F, 0x48),   // PatchyGrassland
        10 => new(0xD8, 0xC4, 0x8A),  // SandYellow
        11 => new(0xB5, 0xAE, 0x98),  // SandGrey
        12 => new(0xB8, 0xA4, 0x7A),  // SandRockStrewn
        13 => new(0x9A, 0x84, 0x68),  // SedimentaryRock
        14 => new(0x84, 0x7A, 0x63),  // SemiBarrenRock
        15 => new(0xF0, 0xF4, 0xF7),  // Snow
        16 => new(0x3C, 0x6E, 0x9E),  // WaterRunning
        17 => new(0x3F, 0x78, 0xA8),  // WaterStandingFresh
        18 => new(0x35, 0x6B, 0x9C),  // WaterShallowSea
        19 => new(0x2F, 0x62, 0x93),  // WaterShallowStillSea
        20 => new(0x1F, 0x4A, 0x78),  // WaterDeepSea
        _ => new(0x80, 0x80, 0x80),
    };

    private static void Fill(byte[] rgb, Rgb c)
    {
        for (int i = 0; i < rgb.Length; i += 3) { rgb[i] = c.R; rgb[i + 1] = c.G; rgb[i + 2] = c.B; }
    }

    private static byte[] EncodeJpeg(byte[] rgb, int w, int h, int quality)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[w * 3];
            for (int y = 0; y < h; y++)
            {
                // GDI+ 24bpp is B,G,R in memory.
                for (int x = 0; x < w; x++)
                {
                    int s = (y * w + x) * 3;
                    row[x * 3] = rgb[s + 2]; row[x * 3 + 1] = rgb[s + 1]; row[x * 3 + 2] = rgb[s];
                }
                Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, row.Length);
            }
        }
        finally { bmp.UnlockBits(bd); }
        var enc = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid)
                  ?? throw new InvalidOperationException("no jpeg encoder");
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
        using var ms = new MemoryStream();
        bmp.Save(ms, enc, ep);
        return ms.ToArray();
    }
}

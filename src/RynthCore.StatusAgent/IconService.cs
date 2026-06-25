using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RynthCore2.TerrainData;

namespace RynthCore.StatusAgent;

/// <summary>
/// Serves item-icon PNGs decoded on-demand from portal.dat (for the read-only inventory viewer).
/// Lazy-opens the dat on the first request and caches each decoded PNG by DID (icons are immutable),
/// so each unique icon is decoded once per agent lifetime. Thread-safe. Returns null whenever icons
/// are unavailable — no dat file (e.g. an agent running off the game box), an undecodable DID, or a
/// decode error — so the endpoint 404s and the app falls back to a text/glyph row.
/// </summary>
internal sealed class IconService : IDisposable
{
    private readonly string _datPath;
    private readonly object _openLock = new();
    private DatDatabase? _portal;
    private IconDecoder? _decoder;
    private bool _openFailed;

    // Decoded PNG by DID. Icons never change, so this only grows; bounded by a simple cap (a full char's
    // worth of icons is ~hundreds, so the cap is never realistically hit — it's just a runaway guard).
    private readonly ConcurrentDictionary<uint, byte[]> _cache = new();
    private const int CacheCap = 4000;

    public IconService(string datPath) => _datPath = datPath;

    private bool EnsureOpen()
    {
        if (_portal != null) return true;
        if (_openFailed) return false;
        lock (_openLock)
        {
            if (_portal != null) return true;
            if (_openFailed) return false;
            try
            {
                if (!File.Exists(_datPath))
                {
                    _openFailed = true;
                    AgentLog.Warn($"[icon] portal.dat not found at '{_datPath}' — icons disabled (set IconDatPath in statusagent.json).");
                    return false;
                }
                var db = new DatDatabase();
                if (!db.Open(_datPath))
                {
                    db.Dispose();
                    _openFailed = true;
                    AgentLog.Warn($"[icon] failed to open portal.dat at '{_datPath}'.");
                    return false;
                }
                _decoder = new IconDecoder(db);
                _portal = db;
                AgentLog.Info($"[icon] portal.dat opened: {_datPath} ({db.RecordCount:N0} files indexed).");
                return true;
            }
            catch (Exception ex)
            {
                _openFailed = true;
                AgentLog.Warn($"[icon] open error: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Returns the PNG bytes for an icon DID (0x06xxxxxx), or null if unavailable/undecodable.</summary>
    public byte[]? GetPng(uint did)
    {
        if (did == 0) return null;
        if (_cache.TryGetValue(did, out var cached)) return cached;
        if (!EnsureOpen() || _decoder == null) return null;
        try
        {
            var img = _decoder.Decode(did);
            if (img == null || img.Pixels.Length == 0 || img.Width <= 0 || img.Height <= 0)
                return null;
            byte[] png = RgbaToPng(img.Pixels, img.Width, img.Height);
            if (_cache.Count > CacheCap) _cache.Clear();
            _cache[did] = png;
            return png;
        }
        catch (Exception ex)
        {
            AgentLog.Debug($"[icon] decode 0x{did:X8} failed: {ex.Message}");
            return null;
        }
    }

    /// RGBA (R,G,B,A) -> PNG via System.Drawing (already referenced for the screen JPEG path; Windows-only,
    /// and the agent is net*-windows). Format32bppArgb is BGRA in memory, so swizzle R&lt;-&gt;B on copy.
    private static byte[] RgbaToPng(byte[] rgba, int w, int h)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int s = (y * w + x) * 4, d = x * 4;
                    row[d] = rgba[s + 2];     // B
                    row[d + 1] = rgba[s + 1]; // G
                    row[d + 2] = rgba[s];     // R
                    row[d + 3] = rgba[s + 3]; // A
                }
                Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, w * 4);
            }
        }
        finally { bmp.UnlockBits(bd); }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    public void Dispose()
    {
        try { _portal?.Dispose(); } catch { }
        _cache.Clear();
    }
}

// DatReaderCheck - proves a change to RynthCore's dat code on the real dat files (read only).
// Written for RynthCore (MIT). The modes are described in DatReaderCheck.csproj.
//
// Only the API of the code under test is used (StatusAgent: DatDatabase, IconDecoder; engine:
// AcDatFile, AcIconDecoder), so the same source builds against the old code and the new, and
// two builds' outputs can be compared line by line.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RynthCore.Engine.UI.ScriptWindows;
using RynthCore.StatusAgent;
using RynthCore2.TerrainData;

namespace DatReaderCheck;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "manifest") return Manifest(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "icons") return Icons(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "diff") return Diff(args[1], args[2]);
        Console.WriteLine("usage: manifest <AC folder> <out> | icons <AC folder> <out> | diff <a> <b>");
        return 2;
    }

    private static StreamWriter Out(string path) => new(path, false, new UTF8Encoding(false)) { NewLine = "\n" };

    // ------------------------------------------------------------------ manifest

    private static int Manifest(string acDir, string outPath)
    {
        using var w = Out(outPath);
        int rc = DumpDat(w, Path.Combine(acDir, "client_portal.dat"));
        rc |= DumpDat(w, Path.Combine(acDir, "client_cell_1.dat"));
        var p = Process.GetCurrentProcess();
        p.Refresh();
        w.WriteLine($"#t process peak working set {p.PeakWorkingSet64 / 1048576.0:F1} MB");
        return rc;
    }

    private static int DumpDat(StreamWriter w, string path)
    {
        Console.WriteLine("== " + Path.GetFileName(path));
        w.WriteLine("== " + Path.GetFileName(path));
        var sw = Stopwatch.StartNew();
        using var db = new DatDatabase();
        bool ok = db.Open(path);
        w.WriteLine($"open {ok}");
        w.WriteLine($"#t open {sw.Elapsed.TotalMilliseconds:F0} ms");
        if (!ok) { Console.WriteLine("open failed: " + string.Join(" | ", db.DiagLog)); return 1; }
        w.WriteLine($"header FileType={db.FileType:X8} BlockSize={db.BlockSize} FileSize={db.FileSize} DataSet={db.DataSet:X8} BTreeRoot={db.BTreeRoot:X8} IsLoaded={db.IsLoaded} records={db.RecordCount}");

        using var engine = AcDatFile.Open(path, out string why);
        w.WriteLine($"engine-open {(engine != null ? "ok" : why)}");

        var all = db.EntriesInRange(0, uint.MaxValue);
        w.WriteLine($"entries-in-range-all {all.Count} {Hashes(all)}");
        var ids = all.Select(e => e.id).OrderBy(i => i).ToList();
        var idSet = new HashSet<uint>(ids);

        sw.Restart();
        int nulls = 0, mismatch = 0, engineDiff = 0, engineNull = 0;
        long bytes = 0;
        foreach (uint id in ids)
        {
            var f = db.FindFile(id);
            if (f == null || f.ObjectId != id) mismatch++;
            byte[]? data = db.GetFileData(id);
            string digest;
            if (data == null) { nulls++; digest = "NULL"; }
            else { bytes += data.Length; digest = data.Length + " " + Convert.ToHexString(SHA256.HashData(data)); }
            w.WriteLine($"f {id:X8} {f?.BitFlags:X8} {f?.FileOffset:X8} {f?.FileSize} = {digest}");

            if (engine != null)
            {
                byte[]? e = engine.Read(id, int.MaxValue);
                if (e == null) engineNull++;
                else if (data == null || !e.AsSpan().SequenceEqual(data)) engineDiff++;
            }
        }
        w.WriteLine($"files {ids.Count} read-null {nulls} find-mismatch {mismatch} bytes {bytes} engine-null {engineNull} engine-differs {engineDiff}");
        w.WriteLine($"#t all files {sw.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine($"  {ids.Count} files, {nulls} null, {mismatch} find mismatches; engine reader: {engineNull} null, {engineDiff} differ");

        // The engine reader's size cap and a few caps around real sizes.
        if (engine != null)
        {
            int capped = 0;
            foreach (var e in all.Take(2000)) if (engine.Read(e.id, (int)e.size - 1) == null) capped++;
            int fits = 0;
            foreach (var e in all.Take(2000)) if (engine.Read(e.id, (int)e.size) != null) fits++;
            w.WriteLine($"engine-cap below-size-null {capped} at-size-ok {fits}");
        }

        sw.Restart();
        int probes = 0, misses = 0, wrong = 0, engineWrong = 0;
        void Probe(uint id)
        {
            probes++;
            var f = db.FindFile(id);
            bool expect = idSet.Contains(id);
            if (f == null) { misses++; if (expect) wrong++; }
            else if (!expect || f.ObjectId != id) wrong++;
            if (engine != null && probes % 8 == 0 && (engine.Read(id, int.MaxValue) != null) != expect) engineWrong++;
            if (db.GetFileData(id) == null == expect) wrong++;
        }
        foreach (uint id in ids)
        {
            if (id != uint.MaxValue) Probe(id + 1);
            if (id != 0) Probe(id - 1);
        }
        var rng = new Random(20261005);
        for (int i = 0; i < 1_000_000; i++) Probe((uint)rng.NextInt64(0, 1L << 32));
        for (uint t = 0; t <= 0x40; t++) for (uint lo = 0; lo < 0x400; lo++) Probe((t << 24) | lo);
        Probe(0); Probe(uint.MaxValue);
        w.WriteLine($"probes {probes} misses {misses} wrong {wrong} engine-wrong {engineWrong}");
        w.WriteLine($"#t probes {sw.Elapsed.TotalMilliseconds:F0} ms");

        for (uint t = 0; t <= 0xFF; t++)
        {
            var r = db.EntriesInRange(t << 24, (t << 24) | 0xFFFFFF);
            if (r.Count > 0) w.WriteLine($"range {t:X2} {r.Count} {Hashes(r)}");
        }
        int withCells = 0, cellIds = 0;
        for (uint lb = 0; lb <= 0xFFFF; lb++)
        {
            var c = db.GetLandblockCellIds(lb);
            if (c.Count == 0) continue;
            withCells++; cellIds += c.Count;
            w.WriteLine($"lb {lb:X4} {c.Count} {Hashes(c.Select(i => (i, 0u)).ToList())}");
        }
        w.WriteLine($"landblocks with-cells {withCells} cell-ids {cellIds}");
        db.Close();
        w.WriteLine($"closed IsLoaded={db.IsLoaded} find-after-close={(db.FindFile(ids[0]) == null ? "null" : "hit")}");
        return 0;
    }

    // Which entries a list holds, whatever their order, then (on a "#o" line, which diff reports
    // but doesn't count) the order itself: the old reader returned ties and cell ids in its
    // dictionary's order; the new one sorts them (size then id, cell ids ascending).
    private static string Hashes(List<(uint id, uint size)> list) =>
        Hash(list.OrderBy(e => e.id).ThenBy(e => e.size).ToList()) + "\n#o order " + Hash(list);

    private static string Hash(List<(uint id, uint size)> list)
    {
        var b = new byte[list.Count * 8];
        for (int i = 0; i < list.Count; i++)
        {
            BitConverter.TryWriteBytes(b.AsSpan(i * 8), list[i].id);
            BitConverter.TryWriteBytes(b.AsSpan(i * 8 + 4), list[i].size);
        }
        return Convert.ToHexString(SHA256.HashData(b), 0, 12);
    }

    // ------------------------------------------------------------------ icons

    private static int Icons(string acDir, string outPath)
    {
        using var w = Out(outPath);
        string path = Path.Combine(acDir, "client_portal.dat");
        using var db = new DatDatabase();
        if (!db.Open(path)) { Console.WriteLine("open failed"); return 1; }
        using var engine = AcDatFile.Open(path, out string why);
        if (engine == null) { Console.WriteLine("engine open failed: " + why); return 1; }
        var decoder = new IconDecoder(db);

        var ids = db.EntriesInRange(0x06000000, 0x06FFFFFF).Select(e => e.id).OrderBy(i => i).ToList();
        w.WriteLine($"textures {ids.Count}");
        var byFormat = new SortedDictionary<string, int[]>(); // format -> [count, status ok, engine ok, engine-ui ok]
        double agentMs = 0, engineMs = 0;
        var sw = new Stopwatch();
        var decodable = new List<uint>();
        foreach (uint id in ids)
        {
            byte[]? raw = db.GetFileData(id);
            string fmt = "?";
            int fw = 0, fh = 0;
            if (raw != null && raw.Length >= 24)
            {
                fw = BitConverter.ToInt32(raw, 8); fh = BitConverter.ToInt32(raw, 12);
                fmt = BitConverter.ToUInt32(raw, 16).ToString("X8");
            }
            sw.Restart();
            var a = decoder.Decode(id);
            agentMs += sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var e1 = AcIconDecoder.Decode(engine, id);
            var e2 = AcIconDecoder.Decode(engine, id, AcIconDecoder.MaxUiSide);
            engineMs += sw.Elapsed.TotalMilliseconds;
            w.WriteLine($"t {id:X8} {fmt} {fw}x{fh} agent={D(a?.Width, a?.Height, a?.Pixels)} icon={D(e1?.Width, e1?.Height, e1?.Pixels)} ui={D(e2?.Width, e2?.Height, e2?.Pixels)}");
            if (!byFormat.TryGetValue(fmt, out var c)) byFormat[fmt] = c = new int[4];
            c[0]++;
            if (a != null) c[1]++;
            if (e1 != null) { c[2]++; if (fw == 32 && fh == 32) decodable.Add(id); }
            if (e2 != null) c[3]++;
        }
        foreach (var kv in byFormat)
            w.WriteLine($"format {kv.Key}: {kv.Value[0]} textures, agent decodes {kv.Value[1]}, engine icon {kv.Value[2]}, engine ui {kv.Value[3]}");
        w.WriteLine($"#t agent decode {agentMs:F0} ms, engine decode (icon + ui) {engineMs:F0} ms");
        Console.WriteLine($"{ids.Count} textures");
        foreach (var kv in byFormat) Console.WriteLine($"  {kv.Key}: {kv.Value[0]}  agent {kv.Value[1]}  icon {kv.Value[2]}  ui {kv.Value[3]}");

        // Composed item pictures: underlay + icon + overlay (0 = none).
        var rng = new Random(424242);
        for (int i = 0; i < 3000 && decodable.Count > 0; i++)
        {
            uint icon = decodable[rng.Next(decodable.Count)];
            uint under = rng.Next(3) == 0 ? 0 : decodable[rng.Next(decodable.Count)];
            uint over = rng.Next(3) == 0 ? 0 : decodable[rng.Next(decodable.Count)];
            var c = AcIconDecoder.Compose(engine, icon, under, over);
            w.WriteLine($"c {icon:X8} {under:X8} {over:X8} {D(c?.Width, c?.Height, c?.Pixels)}");
        }
        // Ids that aren't textures, or aren't there.
        foreach (uint id in new uint[] { 0, 0x05000001, 0x06FFFFFF, 0x07000001, 0x0E00000E })
            w.WriteLine($"x {id:X8} agent={D(decoder.Decode(id)?.Width, 0, decoder.Decode(id)?.Pixels)} icon={D(AcIconDecoder.Decode(engine, id)?.Width, 0, AcIconDecoder.Decode(engine, id)?.Pixels)}");
        return 0;
    }

    private static string D(int? w, int? h, byte[]? px) =>
        px == null ? "null" : $"{w}x{h}:{Convert.ToHexString(SHA256.HashData(px), 0, 12)}";

    // ------------------------------------------------------------------ diff

    private static int Diff(string a, string b)
    {
        var oa = File.ReadLines(a).Where(l => l.StartsWith("#o")).ToList();
        var ob = File.ReadLines(b).Where(l => l.StartsWith("#o")).ToList();
        int orderDiffs = oa.Count == ob.Count ? oa.Where((l, i) => l != ob[i]).Count() : -1;
        Console.WriteLine($"order lines: {oa.Count} vs {ob.Count}, {(orderDiffs < 0 ? "counts differ" : orderDiffs + " differ")} (reported, not counted)");
        using var la = File.ReadLines(a).Where(l => !l.StartsWith('#')).GetEnumerator();
        using var lb = File.ReadLines(b).Where(l => !l.StartsWith('#')).GetEnumerator();
        long n = 0, diffs = 0;
        while (true)
        {
            bool ha = la.MoveNext(), hb = lb.MoveNext();
            if (!ha && !hb) break;
            n++;
            string sa = ha ? la.Current : "<end>", sb = hb ? lb.Current : "<end>";
            if (sa == sb) continue;
            if (diffs < 40) Console.WriteLine($"line {n}:\n  a: {sa}\n  b: {sb}");
            diffs++;
        }
        Console.WriteLine($"{n} lines compared, {diffs} differ");
        foreach (var (name, file) in new[] { ("a", a), ("b", b) })
        {
            Console.WriteLine($"timings {name}:");
            foreach (var l in File.ReadLines(file).Where(l => l.StartsWith("#t"))) Console.WriteLine("  " + l);
        }
        return diffs == 0 ? 0 : 1;
    }
}

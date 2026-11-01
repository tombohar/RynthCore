using System.Diagnostics;
using RynthCore.Engine.ImGuiBackend;

namespace UiCostTests;

// Offline tests for the UI frame cost breakdown. See the csproj.
internal static class Program
{
    private static int _fails, _asserts;

    // Logic tests run on a microsecond clock: 1 tick = 1 us, so Ms(1.5) = 1500 ticks.
    private const long Us = 1_000_000;
    private static long Ms(double ms) => (long)Math.Round(ms * 1000);
    private static long Sec(double s) => (long)Math.Round(s * Us);

    private static void Check(bool cond, string msg)
    {
        _asserts++;
        if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
    }

    private static void Eq<T>(T actual, T expected, string msg) =>
        Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{msg}: expected '{expected}', got '{actual}'");

    private static int Main()
    {
        Run("FrameLineFormat", FrameLineFormat);
        Run("ThresholdAndRateLimit", ThresholdAndRateLimit);
        Run("ThresholdClamp", ThresholdClamp);
        Run("RollingWindow", RollingWindow);
        Run("HeartbeatTopThree", HeartbeatTopThree);
        Run("DescribeNow", DescribeNow);
        Run("ExclusiveTiming", ExclusiveTiming);
        Run("OverflowSlot", OverflowSlot);
        Run("TimingOff", TimingOff);
        Run("DiscardFrame", DiscardFrame);
        Run("Overhead", Overhead);
        Run("DrawPlanRange", DrawPlanRange);
        Run("DrawPlanLootEditorShape", DrawPlanLootEditorShape);
        Run("SubmitCountersInLine", SubmitCountersInLine);
        Run("SubmitCountersInHeartbeatAndDescribe", SubmitCountersInHeartbeatAndDescribe);
        Console.WriteLine();
        Console.WriteLine(_fails == 0 && _asserts > 0 ? $"PASS ({_asserts} checks)" : $"FAIL ({_fails} of {_asserts} checks)");
        return _fails == 0 && _asserts > 0 ? 0 : 1;
    }

    private static void Run(string name, Action body)
    {
        Console.WriteLine(name);
        try { body(); }
        catch (Exception ex) { _fails++; _asserts++; Console.WriteLine($"  [FAIL] threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
    }

    private static UiCostModel NewModel() => new(Us) { Enabled = true };

    private static void FrameLineFormat()
    {
        var m = NewModel();
        int radar = m.Register("Radar");
        int hud = m.Register("Monster HUD", unit: "plates");
        int chat = m.Register("Chat");
        int tiny = m.Register("Tiny");
        int nav = m.Register("Nav3D markers", outside: true);
        m.Add(chat, Ms(4.0));
        m.Add(radar, Ms(100.0));
        m.Add(radar, Ms(20.3));   // two calls in one frame add up
        m.Add(hud, Ms(41.0), 38);
        m.Add(tiny, Ms(0.01));
        m.Add(nav, Ms(1.2));
        string? line = m.EndFrame(Ms(187.0), Sec(10), logSlow: true);
        Check(line != null, "a 187 ms frame over 50 writes a line");
        if (line == null) return;
        Console.WriteLine("  " + line);
        Check(line.StartsWith("[UiCost] frame 187.0 ms (over 50): Radar 120.3, Monster HUD 41.0 (38 plates), untimed 21.7, Chat 4.0", StringComparison.Ordinal),
            "parts largest first, with counts and the untimed rest: " + line);
        Check(line.Contains(", +1 more 0.0", StringComparison.Ordinal), "a part under 0.05 ms is summed as +1 more");
        Check(line.EndsWith(" | 3D pass, not in ui=: Nav3D markers 1.2", StringComparison.Ordinal), "the 3D pass part listed apart, not in untimed");
        Check(!line.Contains("Tiny", StringComparison.Ordinal), "the tiny part isn't named");

        // The next frame starts clean.
        m.Add(chat, Ms(60));
        string? next = m.EndFrame(Ms(60), Sec(20), logSlow: true);
        Check(next != null && !next.Contains("Radar", StringComparison.Ordinal) && next.Contains("Chat 60.0", StringComparison.Ordinal),
            "a frame's parts don't carry into the next: " + next);

        // More than LineTopItems parts: the rest are summed.
        var many = NewModel();
        for (int i = 0; i < 12; i++) many.Add(many.Register("P" + i), Ms(10 + i));
        string? l2 = many.EndFrame(Ms(500), Sec(1), logSlow: true);
        Check(l2 != null && l2.Contains("P11 21.0, P10 20.0", StringComparison.Ordinal), "largest first among many: " + l2);
        // Shown: untimed (314) + P11..P5 = 8 parts; P4..P0 (14+13+12+11+10 = 60) summed.
        Check(l2 != null && l2.Contains(", +5 more 60.0", StringComparison.Ordinal), "past the top 8 the rest are summed: " + l2);
    }

    private static void ThresholdAndRateLimit()
    {
        var m = NewModel();
        int p = m.Register("Panel");
        string? Frame(double ms, double atSec, bool log = true)
        {
            m.Add(p, Ms(ms * 0.9));
            return m.EndFrame(Ms(ms), Sec(atSec), log);
        }
        Check(Frame(60, 0) != null, "the first slow frame is logged");
        Check(Frame(70, 1) == null, "another 1 s later is not (rate limit)");
        Check(Frame(500, 4.99) == null, "nor 4.99 s later");
        Check(Frame(60, 5.0) != null, "5 s after the last line: logged again");
        Check(Frame(49.9, 20) == null, "under the threshold: not logged");
        Check(Frame(50, 21) != null, "exactly at the threshold: logged");
        Check(Frame(60, 40, log: false) == null, "logging off: nothing");
        Check(Frame(60, 41) != null, "logging back on, 20 s after the last line: logged");
        Eq(m.LinesWritten, 4L, "lines written");
        m.ThresholdMs = 100;
        Check(Frame(99, 60) == null, "threshold raised to 100: a 99 ms frame isn't slow");
        Check(Frame(100, 61) != null, "...a 100 ms frame is");
        var t = m.WindowTotals(Sec(61));
        Eq(t.Slow, 1L, "slow frames counted in the window at the current threshold");
    }

    private static void ThresholdClamp()
    {
        Eq(UiCostModel.ClampThresholdMs(1), UiCostModel.MinThresholdMs, "below the range");
        Eq(UiCostModel.ClampThresholdMs(99999), UiCostModel.MaxThresholdMs, "above the range");
        Eq(UiCostModel.ClampThresholdMs(75), 75, "in range");
        var m = NewModel();
        Eq(m.ThresholdMs, UiCostModel.DefaultThresholdMs, "default threshold");
        Eq(UiCostModel.DefaultThresholdMs, 50, "the default is 50 ms");
        m.ThresholdMs = 0;
        Eq(m.ThresholdMs, UiCostModel.MinThresholdMs, "a 0 threshold is clamped");
    }

    private static void RollingWindow()
    {
        var m = NewModel();
        int radar = m.Register("Radar");
        // A frame every 100 ms for 10 s; at t = 2.0 s a 100 ms radar spike.
        for (int i = 0; i < 100; i++)
        {
            double t = i * 0.1;
            bool spike = i == 20;
            m.Add(radar, Ms(spike ? 100 : 2));
            m.EndFrame(Ms(spike ? 150 : 5), Sec(t), logSlow: false);
            if (i == 30)
            {
                var early = m.SlotWindow(Sec(3.05), radar);
                Check(Math.Abs(early.MaxMs - 100) < 0.001, $"at 3 s the window still has the spike (max {early.MaxMs})");
            }
        }
        var w = m.WindowTotals(Sec(9.95));
        Eq(w.Frames, 50L, "the window holds the last 5 one-second buckets (5.0-9.9 s)");
        Check(Math.Abs(w.AvgMs - 5) < 0.001, $"avg ui 5 ms ({w.AvgMs})");
        Check(Math.Abs(w.MaxMs - 5) < 0.001, $"the spike at 2 s has left the window ({w.MaxMs})");
        var r = m.SlotWindow(Sec(9.95), radar);
        Check(Math.Abs(r.AvgMs - 2) < 0.001 && Math.Abs(r.MaxMs - 2) < 0.001, $"radar avg/max 2/2 ({r.AvgMs}/{r.MaxMs})");

        // Nothing for 10 s: the window is empty, and the first frame after the gap starts a new one.
        Eq(m.WindowTotals(Sec(20)).Frames, 0L, "after a 10 s gap the window is empty");
        m.Add(radar, Ms(7));
        m.EndFrame(Ms(8), Sec(20), logSlow: false);
        var after = m.WindowTotals(Sec(20.1));
        Eq(after.Frames, 1L, "one frame after the gap");
        Check(Math.Abs(after.MaxMs - 8) < 0.001, "...and only it");
        Eq(m.FramesSeen, 101L, "every frame was folded in");

        // A gap of 2 buckets: the buckets in between are empty, older ones still count.
        var g = NewModel();
        int s = g.Register("S");
        g.Add(s, Ms(1)); g.EndFrame(Ms(1), Sec(0), false);
        g.Add(s, Ms(3)); g.EndFrame(Ms(3), Sec(2.5), false);
        var gw = g.WindowTotals(Sec(2.6));
        Eq(gw.Frames, 2L, "frames 2.5 s apart are both in the window");
        Check(Math.Abs(gw.AvgMs - 2) < 0.001, $"avg of 1 and 3 ({gw.AvgMs})");
    }

    private static void HeartbeatTopThree()
    {
        var m = NewModel();
        int a = m.Register("Monster HUD (plates, combat text)", unit: "plates");
        int b = m.Register("Radar");
        int c = m.Register("Chat");
        int d = m.Register("Bar");
        for (int i = 0; i < 10; i++)
        {
            m.Add(c, Ms(1));
            m.Add(a, Ms(i == 5 ? 41 : 5), 38);
            m.Add(d, Ms(0.5));
            m.Add(b, Ms(3));
            m.EndFrame(Ms(i == 5 ? 46 : 10), Sec(i * 0.1), logSlow: false);
        }
        string f = m.HeartbeatField(Sec(1.0));
        Console.WriteLine("  hb:" + f);
        // a: avg (9*5+41)/10 = 8.6, max 41; b 3/3; c 1/1; untimed 0.5/0.5; d 0.5/0.5.
        Eq(f, " uitop=Monster_HUD_(plates;_combat_text):8.6/41.0,Radar:3.0/3.0,Chat:1.0/1.0", "top 3 by average, avg/max, no spaces");
        Check(f.LastIndexOf(' ') == 0, "only the leading space");
        Eq(NewModel().HeartbeatField(Sec(1)), "", "no frames: no field");
    }

    private static void DescribeNow()
    {
        var m = NewModel();
        int radar = m.Register("Radar");
        int hud = m.Register("Monster HUD", unit: "plates");
        int nav = m.Register("Nav3D markers", outside: true);
        for (int i = 0; i < 20; i++)
        {
            bool slow = i == 7;
            m.Add(radar, Ms(slow ? 120 : 1));
            m.Add(hud, Ms(slow ? 41 : 2.1), slow ? 38 : 10);
            m.Add(nav, Ms(0.5));
            m.EndFrame(Ms(slow ? 187 : 4), Sec(i * 0.05), logSlow: true);
        }
        List<string> lines = m.Describe(Sec(1.0));
        foreach (string l in lines) Console.WriteLine("  " + l);
        Check(lines.Count == 4, $"summary, per-frame, slowest frame, 3D pass lines ({lines.Count})");
        if (lines.Count < 4) return;
        Check(lines[0].StartsWith("UI cost, last ", StringComparison.Ordinal) && lines[0].Contains("20 frames", StringComparison.Ordinal)
              && lines[0].Contains("max 187.0 ms", StringComparison.Ordinal) && lines[0].Contains("1 at 50 ms+", StringComparison.Ordinal), "summary: " + lines[0]);
        Check(lines[1].StartsWith("Per frame, avg (max): Radar ", StringComparison.Ordinal), "radar first by average: " + lines[1]);
        Check(lines[1].Contains("Monster HUD 4.0 (41.0, 38 plates)", StringComparison.Ordinal), "hud avg, max and the most plates: " + lines[1]);
        Eq(lines[2], "Slowest frame 187.0 ms: Radar 120.0, Monster HUD 41.0 (38 plates), untimed 26.0", "the slowest frame part by part");
        Check(lines[3].StartsWith("3D pass, not in ui=, avg (max): Nav3D markers 0.5 (0.5)", StringComparison.Ordinal), "3D pass: " + lines[3]);

        var empty = NewModel();
        List<string> none = empty.Describe(Sec(1));
        Check(none.Count == 1 && none[0].StartsWith("UI cost: no UI frames", StringComparison.Ordinal), "no frames: one line saying so");
        empty.Enabled = false;
        Check(empty.Describe(Sec(1))[0].Contains("timing off", StringComparison.Ordinal), "says when timing is off");
    }

    private static void ExclusiveTiming()
    {
        long f = Stopwatch.Frequency;
        var m = new UiCostModel(f) { Enabled = true };
        int host = m.Register("Panel host upkeep");
        int panel = m.Register("Inventory");
        long start = Stopwatch.GetTimestamp() - f / 10;   // the host part began 100 ms ago
        long mark = m.Mark;
        m.Add(panel, f * 60 / 1000);                       // a panel inside it took 60 ms
        m.StopExclusive(host, start, mark);
        m.EndFrame(f / 10, Stopwatch.GetTimestamp(), logSlow: false);
        var h = m.SlotWindow(Stopwatch.GetTimestamp(), host);
        var p = m.SlotWindow(Stopwatch.GetTimestamp(), panel);
        Check(h.AvgMs >= 39.9 && h.AvgMs < 45, $"the host keeps only its own 40 ms ({h.AvgMs:0.00})");
        Check(Math.Abs(p.AvgMs - 60) < 0.01, $"the panel keeps its 60 ms ({p.AvgMs:0.00})");

        // Stop with a real Start.
        int q = m.Register("Q");
        long s = m.Start();
        Check(s != 0, "Start gives a timestamp while timing is on");
        Thread.Sleep(5);
        m.Stop(q, s);
        m.EndFrame(f / 100, Stopwatch.GetTimestamp(), logSlow: false);
        Check(m.SlotWindow(Stopwatch.GetTimestamp(), q).MaxMs >= 4, "a 5 ms sleep is timed");
    }

    private static void OverflowSlot()
    {
        var m = NewModel();
        int last = -1;
        for (int i = 0; i < UiCostModel.MaxSlots + 40; i++) last = m.Register("N" + i);
        Eq(last, UiCostModel.OverflowSlot, "past MaxSlots a name gets the overflow slot");
        Eq(m.SlotCount, UiCostModel.MaxSlots, "slot count stops at MaxSlots");
        m.Add(last, Ms(70));
        string? line = m.EndFrame(Ms(80), Sec(1), true);
        Check(line != null && line.Contains("(more names) 70.0", StringComparison.Ordinal), "the overflow slot is named: " + line);
        m.Add(9999, Ms(5));   // out of range: ignored
        m.Add(-1, Ms(5));
        Eq(m.Mark, 0L, "an out-of-range slot adds nothing");
    }

    private static void TimingOff()
    {
        var m = new UiCostModel(Stopwatch.Frequency) { Enabled = false };
        int p = m.Register("P");
        long s = m.Start();
        Eq(s, 0L, "Start is 0 while timing is off");
        m.Stop(p, s);
        m.StopExclusive(p, s, 0);
        Eq(m.Mark, 0L, "Stop with 0 adds nothing");
    }

    private static void DiscardFrame()
    {
        var m = NewModel();
        int nav = m.Register("Nav3D markers", outside: true);
        int p = m.Register("P");
        m.Add(nav, Ms(30));
        m.Add(p, Ms(30));
        m.DiscardFrame();
        Eq(m.Mark, 0L, "discarded");
        string? line = m.EndFrame(Ms(60), Sec(1), true);
        Check(line != null && !line.Contains("Nav3D", StringComparison.Ordinal) && line.Contains("untimed 60.0", StringComparison.Ordinal),
            "a discarded frame's parts don't reach the next frame: " + line);
    }

    // The per-frame cost of the breakdown: Parts Start/Stop pairs and an EndFrame (as UiCost does each
    // frame), against the same frame with timing off (each Start a flag check, each Stop a 0 check).
    private static void Overhead()
    {
        const int Parts = 40, Frames = 200_000;
        var on = new UiCostModel(Stopwatch.Frequency) { Enabled = true };
        var off = new UiCostModel(Stopwatch.Frequency) { Enabled = false };
        int[] slotsOn = new int[Parts], slotsOff = new int[Parts];
        for (int i = 0; i < Parts; i++) { slotsOn[i] = on.Register("Part " + i); slotsOff[i] = off.Register("Part " + i); }

        static double Bench(UiCostModel m, int[] slots, bool endFrame, int frames)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int f = 0; f < frames; f++)
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    long s = m.Start();
                    m.Stop(slots[i], s);
                }
                if (m.Enabled)
                {
                    var st = new SubmitStats { Lists = 20, Draws = 400, TexSwitches = 3, VtxCopied = 30_000, VtxWholeList = 900_000 };
                    m.SetSubmit(st);
                    m.AddUploads(0, f % 1000 == 0 ? 1 : 0);
                }
                if (endFrame) m.EndFrame(Stopwatch.Frequency / 100, Stopwatch.GetTimestamp(), logSlow: false);
            }
            return (Stopwatch.GetTimestamp() - t0) * 1e9 / Stopwatch.Frequency / frames;
        }

        Bench(on, slotsOn, true, 20_000);   // warm up (JIT)
        Bench(off, slotsOff, false, 20_000);
        long gc0 = GC.GetAllocatedBytesForCurrentThread();
        double onNs = Bench(on, slotsOn, true, Frames);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - gc0;
        double offNs = Bench(off, slotsOff, false, Frames);
        Console.WriteLine($"  {Parts} timed parts a frame: on {onNs / 1000.0:0.00} us/frame, off {offNs:0.0} ns/frame, " +
                          $"{allocated / (double)Frames:0.0} bytes allocated a frame (on); one Start/Stop pair {(onNs / Parts):0} ns.");
        Check(onNs < 100_000, $"on: under 0.1 ms a frame ({onNs:0} ns)");
        Check(offNs < 2_000, $"off: under 2 us a frame ({offNs:0} ns)");
        Check(allocated / (double)Frames < 1.0, $"no allocation per frame ({allocated} bytes over {Frames} frames)");
    }

    // ── DrawSubmitPlan: which vertices one draw command hands to DrawIndexedPrimitiveUP ──

    private static unsafe void DrawPlanRange()
    {
        ushort[] idx = { 104, 105, 106, 104, 106, 107 };   // one quad, vertices 104..107
        fixed (ushort* p = idx)
        {
            DrawSubmitPlan.IndexRange(p, idx.Length, out int min, out int max);
            Eq(min, 104, "smallest index");
            Eq(max, 107, "largest index");

            Check(DrawSubmitPlan.Plan(p, idx.Length, listVtx: 500, vtxOffset: 0, out int first, out int num), "a quad inside the list is drawn");
            Eq(first, 104, "first vertex = smallest index");
            Eq(num, 4, "4 vertices handed over, not the list's 500");

            ushort[] rebased = new ushort[idx.Length];
            fixed (ushort* r = rebased) DrawSubmitPlan.Rebase(p, r, idx.Length, first);
            Eq(string.Join(",", rebased), "0,1,2,0,2,3", "indices rebased to the first vertex");

            // VtxOffset (lists past 64k vertices): the indices are relative to it.
            Check(DrawSubmitPlan.Plan(p, idx.Length, listVtx: 70_000, vtxOffset: 65_536, out first, out num) && first == 104 && num == 4,
                "with a VtxOffset the range is relative to it");
            Check(!DrawSubmitPlan.Plan(p, idx.Length, listVtx: 65_600, vtxOffset: 65_536, out _, out _),
                "an index past the list's end (offset + 107 >= 65600) is refused");
            Check(!DrawSubmitPlan.Plan(p, idx.Length, listVtx: 107, vtxOffset: 0, out _, out _), "index 107 in a 107-vertex list is refused");
            Check(DrawSubmitPlan.Plan(p, idx.Length, listVtx: 108, vtxOffset: 0, out _, out num) && num == 4, "...in a 108-vertex list it is drawn");
            Check(!DrawSubmitPlan.Plan(p, 0, 500, 0, out _, out _), "no indices: nothing to draw");
            Check(!DrawSubmitPlan.Plan(p, idx.Length, 500, 500, out _, out _), "a VtxOffset at the list's end is refused");
        }
        ushort[] zero = { 0, 1, 2 };
        fixed (ushort* z = zero)
            Check(DrawSubmitPlan.Plan(z, 3, 3, 0, out int f0, out int n0) && f0 == 0 && n0 == 3, "starting at vertex 0: no rebase needed (first 0)");
        ushort[] one = { 9, 9, 9 };
        fixed (ushort* o = one)
            Check(DrawSubmitPlan.Plan(o, 3, 10, 0, out int f1, out int n1) && f1 == 9 && n1 == 1, "a degenerate command: one vertex");
    }

    // The Loot Editor's rule list as ImGui builds it: per visible row a command in the child's clip
    // (row background, 5 icon buttons, the badge: ~450 vertices), then one per PushClipRect (name ~80,
    // conditions ~240). Vertices are appended in order, each command's indices reach only its own.
    private static unsafe void DrawPlanLootEditorShape()
    {
        const int Rows = 25;
        int[] cmdVtx = { 448, 80, 240 };   // quads: multiples of 4
        var idx = new List<ushort>();
        var cmds = new List<(int off, int count)>();
        int v = 0;
        for (int r = 0; r < Rows; r++)
            foreach (int cv in cmdVtx)
            {
                int off = idx.Count;
                for (int q = 0; q < cv / 4; q++)
                {
                    int b = v + q * 4;
                    idx.AddRange(new[] { (ushort)b, (ushort)(b + 1), (ushort)(b + 2), (ushort)b, (ushort)(b + 2), (ushort)(b + 3) });
                }
                cmds.Add((off, idx.Count - off));
                v += cv;
            }
        int listVtx = v;
        long copied = 0, wholeList = 0;
        ushort[] arr = idx.ToArray();
        fixed (ushort* p = arr)
            foreach (var (off, count) in cmds)
            {
                Check(DrawSubmitPlan.Plan(p + off, count, listVtx, 0, out _, out int num), "every command is drawable");
                copied += num;
                wholeList += listVtx;
            }
        const int Stride = 28;   // DX9Backend.CustomVertex: xyzrhw, colour, uv
        Console.WriteLine($"  {Rows} rows, {listVtx} vertices, {cmds.Count} commands: copied {copied} vertices ({copied * Stride / 1024.0:0} KB) " +
                          $"vs whole-list {wholeList} ({wholeList * Stride / 1048576.0:0.0} MB) a frame, {wholeList / (double)copied:0}x less");
        Eq(copied, (long)listVtx, "each vertex is copied once a frame");
        Eq(wholeList, (long)listVtx * cmds.Count, "the old policy copied the list once per command");

        // The memcpy alone (a lower bound: the D3D9 runtime copies into its own ring buffer, which also
        // stalls on the GPU when it wraps), printed, not checked.
        byte[] src = new byte[listVtx * Stride], dst = new byte[listVtx * Stride];
        long t0 = Stopwatch.GetTimestamp();
        for (int c = 0; c < cmds.Count; c++) Buffer.BlockCopy(src, 0, dst, 0, src.Length);
        double oldMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        t0 = Stopwatch.GetTimestamp();
        Buffer.BlockCopy(src, 0, dst, 0, src.Length);
        double newMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        Console.WriteLine($"  memcpy of that volume in system RAM: whole-list {oldMs:0.00} ms, per-command range {newMs:0.000} ms");
    }

    private static void SubmitCountersInLine()
    {
        var m = NewModel();
        int sub = m.Register("ImGui submit", unit: "vtx");
        int nav = m.Register("Nav3D markers", outside: true);
        m.Add(sub, Ms(300), 29344);
        m.Add(nav, Ms(1));
        m.SetSubmit(new SubmitStats { Lists = 21, Draws = 412, TexSwitches = 3, VtxCopied = 31_100, VtxWholeList = 4_200_000, Skipped = 2 });
        m.AddUploads(1, 2);
        string? line = m.EndFrame(Ms(301), Sec(1), true);
        Console.WriteLine("  " + line);
        Check(line != null && line.Contains("ImGui submit 300.0 (29344 vtx), untimed 1.0 | submit: 21 lists, 412 draws, 3 tex switches, " +
                                            "31.1k vtx copied (whole-list 4.2M), 2 skipped; uploads: 1 font, 2 tex | 3D pass, not in ui=: Nav3D markers 1.0",
                                            StringComparison.Ordinal), "submit counters and uploads after the parts, before the 3D pass: " + line);

        // The next frame starts clean: no submit recorded, no uploads -> no section.
        m.Add(sub, Ms(60), 100);
        string? next = m.EndFrame(Ms(60), Sec(10), true);
        Check(next != null && !next.Contains("| submit", StringComparison.Ordinal) && !next.Contains("uploads", StringComparison.Ordinal),
            "a frame's counters don't carry into the next: " + next);

        // Uploads alone.
        m.Add(sub, Ms(60));
        m.AddUploads(1, 0);
        string? up = m.EndFrame(Ms(60), Sec(20), true);
        Check(up != null && up.Contains(" | uploads: 1 font, 0 tex", StringComparison.Ordinal), "uploads without a submit: " + up);

        // Discarded with the frame.
        m.SetSubmit(new SubmitStats { Lists = 1, Draws = 1 });
        m.AddUploads(5, 5);
        m.DiscardFrame();
        m.Add(sub, Ms(60));
        string? d = m.EndFrame(Ms(60), Sec(30), true);
        Check(d != null && !d.Contains("| submit", StringComparison.Ordinal) && !d.Contains("uploads", StringComparison.Ordinal), "discarded counters are gone: " + d);

        Eq(UiCostModel.Compact(950), "950", "compact under 1000");
        Eq(UiCostModel.Compact(31_150), "31.2k", "compact thousands");
        Eq(UiCostModel.Compact(4_200_000), "4.2M", "compact millions");
        Eq(UiCostModel.Compact(31_000), "31k", "compact without a trailing .0");
    }

    private static void SubmitCountersInHeartbeatAndDescribe()
    {
        var m = NewModel();
        int sub = m.Register("ImGui submit", unit: "vtx");
        for (int i = 0; i < 10; i++)
        {
            m.Add(sub, Ms(i == 4 ? 300 : 20), 20_000);
            m.SetSubmit(new SubmitStats { Lists = 18, Draws = i == 4 ? 412 : 120, TexSwitches = i == 4 ? 3 : 1, VtxCopied = i == 4 ? 31_000 : 13_000, VtxWholeList = i == 4 ? 4_200_000 : 400_000 });
            if (i == 2) m.AddUploads(1, 0);
            if (i == 6) m.AddUploads(0, 3);
            m.EndFrame(Ms(i == 4 ? 301 : 21), Sec(i * 0.1), logSlow: false);
        }
        string f = m.HeartbeatField(Sec(1.0));
        Console.WriteLine("  hb:" + f);
        Check(f.EndsWith(" uisub=draws:412,texsw:3,vtx:31k/4.2M,up:1/3", StringComparison.Ordinal),
            "the window's largest submit and its summed uploads: " + f);
        Check(f.StartsWith(" uitop=ImGui_submit:", StringComparison.Ordinal), "uitop first: " + f);

        List<string> lines = m.Describe(Sec(1.0));
        foreach (string l in lines) Console.WriteLine("  " + l);
        Check(lines.Exists(l => l == "ImGui submit, largest per frame: 18 lists, 412 draws, 3 tex switches, 31k vtx copied (whole-list 4.2M); uploads (window total): 1 font, 3 tex"),
            "/rc uicost now names the submit's largest counters");

        // Without a submit recorded the heartbeat field is as before.
        var plain = NewModel();
        int p = plain.Register("Radar");
        plain.Add(p, Ms(5));
        plain.EndFrame(Ms(5), Sec(0), false);
        Eq(plain.HeartbeatField(Sec(0.5)), " uitop=Radar:5.0/5.0", "no submit: no uisub");
    }
}

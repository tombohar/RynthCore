// ============================================================================
//  RynthCore.Engine - ImGui/UiCostModel.cs
//  The UI frame cost breakdown (ImGui/UiCost.cs): which part of the UI frame
//  takes the time. Pure logic, no engine types (tools/UiCostTests compiles it).
//
//  Each timed part of a frame is a slot (a docked panel by title, a plugin's
//  render callback, the monster HUD, the ImGui submit, ...). Start/Stop add the
//  ticks of one call to the slot for this frame; EndFrame folds the frame into
//  a rolling window (Buckets x 1 s) and, when the frame's UI time is over the
//  threshold and no line was written in the last LogIntervalSeconds, returns
//  ONE line naming the frame's biggest parts. Nothing allocates per frame:
//  the arrays are fixed, a slot's name is made once, and strings are built only
//  for that line and for the summaries (/rc uicost now, the heartbeat field).
//
//  "Outside" slots (the Nav3D markers and the world overlays' present at AC's
//  3D->UI pass) cost render time but are not part of the heartbeat's ui=; they
//  are listed apart. Whatever the frame spent that no slot covers is "untimed"
//  (Dear ImGui's own work between the timed calls, input, ini saves...).
//
//  Threads: Register, Start, Stop and EndFrame on AC's render thread; the
//  summaries from any thread, with the caller holding one lock around them and
//  EndFrame (UiCost does).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace RynthCore.Engine.ImGuiBackend;

internal sealed class UiCostModel
{
    public const int DefaultThresholdMs = 50;
    public const int MinThresholdMs = 5;
    public const int MaxThresholdMs = 2000;
    public const int MaxSlots = 160;
    /// <summary>The rolling window: this many 1 s buckets (the summaries cover the last 4-5 s).</summary>
    public const int Buckets = 5;
    public const int LogIntervalSeconds = 5;
    /// <summary>Parts named in a slow-frame line; the rest are summed as "+N more".</summary>
    public const int LineTopItems = 8;

    /// <summary>A threshold from a setting or a command, kept in range.</summary>
    public static int ClampThresholdMs(int ms) => Math.Clamp(ms, MinThresholdMs, MaxThresholdMs);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly long _freq;
    private readonly long _bucketTicks;
    private readonly long _logIntervalTicks;

    // Slots.
    private readonly string[] _names = new string[MaxSlots];
    private readonly string?[] _units = new string?[MaxSlots];
    private readonly bool[] _outside = new bool[MaxSlots];
    private int _slotCount;
    /// <summary>Slot 0: names past MaxSlots share it.</summary>
    public const int OverflowSlot = 0;

    // This frame.
    private readonly long[] _fTicks = new long[MaxSlots];
    private readonly int[] _fCount = new int[MaxSlots];
    private readonly int[] _fCalls = new int[MaxSlots];
    private readonly int[] _touched = new int[MaxSlots];
    private int _touchedCount;
    private long _fAll, _fOutside;
    // This frame's ImGui submit counters (SubmitStats, end of this file) and texture uploads.
    private SubmitStats _fSubmit;
    private bool _fHasSubmit;
    private int _fFontUploads, _fTexUploads;

    // The window, one row per bucket.
    private readonly long[] _bStart = new long[Buckets];
    private readonly long[] _bFrames = new long[Buckets];
    private readonly long[] _bUiSum = new long[Buckets];
    private readonly long[] _bUiMax = new long[Buckets];
    private readonly long[] _bSlow = new long[Buckets];
    private readonly long[] _bUntimedSum = new long[Buckets];
    private readonly long[] _bUntimedMax = new long[Buckets];
    private readonly long[,] _bSum = new long[Buckets, MaxSlots];
    private readonly long[,] _bMax = new long[Buckets, MaxSlots];
    private readonly int[,] _bMaxCount = new int[Buckets, MaxSlots];
    // The bucket's slowest frame, part by part.
    private readonly long[,] _bWorst = new long[Buckets, MaxSlots];
    private readonly int[,] _bWorstCount = new int[Buckets, MaxSlots];
    private readonly long[] _bWorstUntimed = new long[Buckets];
    // The bucket's submit counters (each one's largest frame) and uploads (summed).
    private readonly SubmitStats[] _bSubmitMax = new SubmitStats[Buckets];
    private readonly bool[] _bHasSubmit = new bool[Buckets];
    private readonly long[] _bFontUploads = new long[Buckets];
    private readonly long[] _bTexUploads = new long[Buckets];
    private int _cur = -1;

    private long _lastLogAt;
    private bool _loggedOnce;
    private int _thresholdMs = DefaultThresholdMs;
    private long _thresholdTicks;

    // Scratch for sorting (render thread for the frame line; under the caller's lock for summaries).
    private readonly int[] _order = new int[MaxSlots + 1];
    private readonly long[] _orderTicks = new long[MaxSlots + 1];
    private readonly long[] _winSum = new long[MaxSlots];
    private readonly long[] _winMax = new long[MaxSlots];
    private readonly int[] _winMaxCount = new int[MaxSlots];
    private SubmitStats _winSubmit;
    private bool _winHasSubmit;
    private long _winFontUploads, _winTexUploads;

    /// <summary>Timing on: Start returns a timestamp; off: 0 and every Stop is a no-op.</summary>
    public bool Enabled;

    /// <summary>Frames folded into the window since the start (diagnostics, tests).</summary>
    public long FramesSeen { get; private set; }

    /// <summary>Slow-frame lines returned by EndFrame since the start.</summary>
    public long LinesWritten { get; private set; }

    public UiCostModel(long ticksPerSecond)
    {
        _freq = Math.Max(1, ticksPerSecond);
        _bucketTicks = _freq;
        _logIntervalTicks = _freq * LogIntervalSeconds;
        ThresholdMs = DefaultThresholdMs;
        Register("(more names)");
        for (int i = 0; i < MaxSlots; i++) _fCount[i] = -1;
    }

    /// <summary>A frame at or over this many ms is slow (logged, counted).</summary>
    public int ThresholdMs
    {
        get => _thresholdMs;
        set
        {
            _thresholdMs = ClampThresholdMs(value);
            _thresholdTicks = _thresholdMs * _freq / 1000;
        }
    }

    public int SlotCount => Volatile.Read(ref _slotCount);

    public string SlotName(int slot) => (uint)slot < (uint)SlotCount ? _names[slot] : "?";

    /// <summary>
    /// A new slot. <paramref name="unit"/> names the count passed to Stop ("plates", "vtx"), null for
    /// none; <paramref name="outside"/>: not part of the frame's UI time (listed apart). Past
    /// MaxSlots the overflow slot. Render thread (the summaries read only slots already published).
    /// </summary>
    public int Register(string name, string? unit = null, bool outside = false)
    {
        int n = _slotCount;
        if (n >= MaxSlots) return OverflowSlot;
        _names[n] = name;
        _units[n] = unit;
        _outside[n] = outside;
        Volatile.Write(ref _slotCount, n + 1);
        return n;
    }

    /// <summary>A timestamp to pass to Stop, or 0 while timing is off.</summary>
    public long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Adds the time since <paramref name="start"/> (from Start) to <paramref name="slot"/>; a no-op for 0.</summary>
    public void Stop(int slot, long start, int count = -1)
    {
        if (start == 0) return;
        Add(slot, Stopwatch.GetTimestamp() - start, count);
    }

    /// <summary>The ticks every slot got this frame so far: Mark before a part, StopExclusive after it.</summary>
    public long Mark => _fAll;

    /// <summary>
    /// As Stop, less what the slots timed inside it got (since <paramref name="mark"/>): a part that
    /// draws other timed parts (the panel host around its panels, a pop-out window around its panel)
    /// keeps only its own time, so nothing is counted twice.
    /// </summary>
    public void StopExclusive(int slot, long start, long mark, int count = -1)
    {
        if (start == 0) return;
        long ticks = Stopwatch.GetTimestamp() - start - (_fAll - mark);
        Add(slot, ticks < 0 ? 0 : ticks, count);
    }

    /// <summary>Adds <paramref name="ticks"/> (and a count, when not negative) to a slot for this frame.</summary>
    public void Add(int slot, long ticks, int count = -1)
    {
        if ((uint)slot >= (uint)_slotCount) return;
        if (_fCalls[slot]++ == 0) _touched[_touchedCount++] = slot;
        _fTicks[slot] += ticks;
        if (count >= 0) _fCount[slot] = (_fCount[slot] < 0 ? 0 : _fCount[slot]) + count;
        _fAll += ticks;
        if (_outside[slot]) _fOutside += ticks;
    }

    /// <summary>This frame's ImGui submit counters (the main submit; a second call this frame replaces them).</summary>
    public void SetSubmit(in SubmitStats stats)
    {
        _fSubmit = stats;
        _fHasSubmit = true;
    }

    /// <summary>Textures made or re-uploaded this frame: the font atlas, and any other (icons, map tiles, pictures).</summary>
    public void AddUploads(int font, int tex)
    {
        if (font > 0) _fFontUploads += font;
        if (tex > 0) _fTexUploads += tex;
    }

    /// <summary>
    /// Ends the frame: <paramref name="uiTicks"/> is its UI time (what the heartbeat's ui= records).
    /// Folds it into the window and returns the slow-frame line when one is due (over the threshold,
    /// none in the last LogIntervalSeconds, logging on), else null.
    /// </summary>
    public string? EndFrame(long uiTicks, long now, bool logSlow)
    {
        Rotate(now);
        int b = _cur;
        long inside = _fAll - _fOutside;
        long untimed = uiTicks - inside;
        if (untimed < 0) untimed = 0;

        FramesSeen++;
        _bFrames[b]++;
        _bUiSum[b] += uiTicks;
        bool worst = uiTicks > _bUiMax[b] || _bFrames[b] == 1;
        if (uiTicks > _bUiMax[b]) _bUiMax[b] = uiTicks;
        bool slow = uiTicks >= _thresholdTicks;
        if (slow) _bSlow[b]++;
        _bUntimedSum[b] += untimed;
        if (untimed > _bUntimedMax[b]) _bUntimedMax[b] = untimed;

        if (worst)
        {
            for (int s = 0; s < MaxSlots; s++) { _bWorst[b, s] = 0; _bWorstCount[b, s] = -1; }
            _bWorstUntimed[b] = untimed;
        }
        for (int i = 0; i < _touchedCount; i++)
        {
            int s = _touched[i];
            long t = _fTicks[s];
            _bSum[b, s] += t;
            if (t > _bMax[b, s]) _bMax[b, s] = t;
            if (_fCount[s] > _bMaxCount[b, s]) _bMaxCount[b, s] = _fCount[s];
            if (worst) { _bWorst[b, s] = t; _bWorstCount[b, s] = _fCount[s]; }
        }
        if (_fHasSubmit) { _bSubmitMax[b].MaxWith(_fSubmit); _bHasSubmit[b] = true; }
        _bFontUploads[b] += _fFontUploads;
        _bTexUploads[b] += _fTexUploads;

        string? line = null;
        if (logSlow && slow && (!_loggedOnce || now - _lastLogAt >= _logIntervalTicks))
        {
            _loggedOnce = true;
            _lastLogAt = now;
            LinesWritten++;
            line = FormatFrameLine(uiTicks, untimed);
        }

        DiscardFrame();   // clears only what this frame touched
        return line;
    }

    /// <summary>Drops what this frame timed without folding it in (no UI frame this EndScene).</summary>
    public void DiscardFrame()
    {
        for (int i = 0; i < _touchedCount; i++)
        {
            int s = _touched[i];
            _fTicks[s] = 0;
            _fCalls[s] = 0;
            _fCount[s] = -1;
        }
        _touchedCount = 0;
        _fAll = 0;
        _fOutside = 0;
        _fSubmit = default;
        _fHasSubmit = false;
        _fFontUploads = _fTexUploads = 0;
    }

    private void Rotate(long now)
    {
        if (_cur < 0)
        {
            _cur = 0;
            ClearBucket(0, now);
            return;
        }
        long age = now - _bStart[_cur];
        if (age < _bucketTicks) return;
        long steps = age / _bucketTicks;
        if (steps >= Buckets)
        {
            for (int i = 0; i < Buckets; i++) ClearBucket(i, 0);
            _cur = 0;
            _bStart[0] = now;
            return;
        }
        long start = _bStart[_cur];
        for (long i = 0; i < steps; i++)
        {
            start += _bucketTicks;
            _cur = (_cur + 1) % Buckets;
            ClearBucket(_cur, start);
        }
    }

    private void ClearBucket(int b, long start)
    {
        _bStart[b] = start;
        _bFrames[b] = _bUiSum[b] = _bUiMax[b] = _bSlow[b] = _bUntimedSum[b] = _bUntimedMax[b] = _bWorstUntimed[b] = 0;
        _bSubmitMax[b] = default;
        _bHasSubmit[b] = false;
        _bFontUploads[b] = _bTexUploads[b] = 0;
        for (int s = 0; s < MaxSlots; s++)
        {
            _bSum[b, s] = 0; _bMax[b, s] = 0; _bMaxCount[b, s] = -1;
            _bWorst[b, s] = 0; _bWorstCount[b, s] = -1;
        }
    }

    // ── Text ───────────────────────────────────────────────────────────────

    private string Ms(long ticks) => (ticks * 1000.0 / _freq).ToString("0.0", Inv);

    private string WithCount(int slot, int count) =>
        count >= 0 && _units[slot] is { } unit ? $" ({count.ToString(Inv)} {unit})" : "";

    /// <summary>
    /// "[UiCost] frame 187.0 ms (over 50): Radar 120.3, Monster HUD 41.0 (38 plates), ..., untimed 3.2,
    /// +4 more 0.9 | 3D pass, not in ui=: Nav3D markers 1.2". Parts under 0.05 ms are left out.
    /// </summary>
    private string FormatFrameLine(long uiTicks, long untimed)
    {
        var sb = new StringBuilder(256);
        sb.Append("[UiCost] frame ").Append(Ms(uiTicks)).Append(" ms (over ").Append(_thresholdMs.ToString(Inv)).Append("): ");
        int n = 0;
        for (int i = 0; i < _touchedCount; i++)
        {
            int s = _touched[i];
            if (_outside[s]) continue;
            _order[n] = s; _orderTicks[n] = _fTicks[s]; n++;
        }
        _order[n] = -1; _orderTicks[n] = untimed; n++;
        SortDesc(n);
        AppendTop(sb, n, LineTopItems, s => _fCount[s]);
        AppendSubmit(sb, _fHasSubmit, _fSubmit, _fFontUploads, _fTexUploads);

        int o = 0;
        for (int i = 0; i < _touchedCount; i++)
        {
            int s = _touched[i];
            if (!_outside[s]) continue;
            _order[o] = s; _orderTicks[o] = _fTicks[s]; o++;
        }
        if (o > 0)
        {
            SortDesc(o);
            sb.Append(" | 3D pass, not in ui=: ");
            AppendTop(sb, o, LineTopItems, s => _fCount[s]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// " | submit: 21 lists, 412 draws, 3 tex switches, 31k vtx copied (whole-list 4.2M); uploads: 1 font, 0 tex".
    /// The submit part only when one was recorded, the uploads only when there were any.
    /// </summary>
    private static void AppendSubmit(StringBuilder sb, bool has, in SubmitStats s, long font, long tex)
    {
        if (has)
        {
            sb.Append(" | submit: ").Append(s.Lists.ToString(Inv)).Append(" lists, ")
              .Append(s.Draws.ToString(Inv)).Append(" draws, ")
              .Append(s.TexSwitches.ToString(Inv)).Append(" tex switches, ")
              .Append(Compact(s.VtxCopied)).Append(" vtx copied (whole-list ").Append(Compact(s.VtxWholeList)).Append(')');
            if (s.Skipped > 0) sb.Append(", ").Append(s.Skipped.ToString(Inv)).Append(" skipped");
        }
        if (font > 0 || tex > 0)
            sb.Append(has ? "; uploads: " : " | uploads: ").Append(font.ToString(Inv)).Append(" font, ").Append(tex.ToString(Inv)).Append(" tex");
    }

    /// <summary>950, 31.2k, 4.2M.</summary>
    public static string Compact(long n)
    {
        if (n < 1000) return n.ToString(Inv);
        if (n < 1_000_000) return (n / 1000.0).ToString("0.#", Inv) + "k";
        return (n / 1_000_000.0).ToString("0.#", Inv) + "M";
    }

    private void AppendTop(StringBuilder sb, int n, int top, Func<int, int> count)
    {
        long minTicks = _freq / 20000;   // 0.05 ms
        int shown = 0, more = 0;
        long moreTicks = 0;
        for (int i = 0; i < n; i++)
        {
            int s = _order[i];
            long t = _orderTicks[i];
            if (shown >= top || t < minTicks || t <= 0)
            {
                if (t > 0) { more++; moreTicks += t; }
                continue;
            }
            if (shown > 0) sb.Append(", ");
            if (s < 0) sb.Append("untimed ").Append(Ms(t));
            else sb.Append(_names[s]).Append(' ').Append(Ms(t)).Append(WithCount(s, count(s)));
            shown++;
        }
        if (shown == 0) sb.Append("nothing timed");
        if (more > 0) sb.Append(", +").Append(more.ToString(Inv)).Append(" more ").Append(Ms(moreTicks));
    }

    // Insertion sort of _order/_orderTicks [0, n) by ticks, largest first (n is small).
    private void SortDesc(int n)
    {
        for (int i = 1; i < n; i++)
        {
            int s = _order[i];
            long t = _orderTicks[i];
            int j = i - 1;
            while (j >= 0 && _orderTicks[j] < t)
            {
                _order[j + 1] = _order[j];
                _orderTicks[j + 1] = _orderTicks[j];
                j--;
            }
            _order[j + 1] = s;
            _orderTicks[j + 1] = t;
        }
    }

    /// <summary>The live buckets at <paramref name="now"/> (the window), folded into the _win arrays.</summary>
    private (long frames, long uiSum, long uiMax, long slow, long untimedSum, long untimedMax, long spanTicks, int worstBucket) FoldWindow(long now)
    {
        Array.Clear(_winSum);
        Array.Clear(_winMax);
        for (int s = 0; s < MaxSlots; s++) _winMaxCount[s] = -1;
        _winSubmit = default;
        _winHasSubmit = false;
        _winFontUploads = _winTexUploads = 0;
        long frames = 0, uiSum = 0, uiMax = 0, slow = 0, untimedSum = 0, untimedMax = 0, oldest = now;
        int worstBucket = -1;
        if (_cur < 0) return (0, 0, 0, 0, 0, 0, 0, -1);
        long horizon = now - _bucketTicks * Buckets;
        int slots = SlotCount;
        for (int b = 0; b < Buckets; b++)
        {
            if (_bFrames[b] == 0 || _bStart[b] < horizon) continue;
            if (_bStart[b] < oldest) oldest = _bStart[b];
            frames += _bFrames[b];
            uiSum += _bUiSum[b];
            if (worstBucket < 0 || _bUiMax[b] > uiMax) { uiMax = _bUiMax[b]; worstBucket = b; }
            slow += _bSlow[b];
            untimedSum += _bUntimedSum[b];
            if (_bUntimedMax[b] > untimedMax) untimedMax = _bUntimedMax[b];
            if (_bHasSubmit[b]) { _winSubmit.MaxWith(_bSubmitMax[b]); _winHasSubmit = true; }
            _winFontUploads += _bFontUploads[b];
            _winTexUploads += _bTexUploads[b];
            for (int s = 0; s < slots; s++)
            {
                _winSum[s] += _bSum[b, s];
                if (_bMax[b, s] > _winMax[s]) _winMax[s] = _bMax[b, s];
                if (_bMaxCount[b, s] > _winMaxCount[s]) _winMaxCount[s] = _bMaxCount[b, s];
            }
        }
        return (frames, uiSum, uiMax, slow, untimedSum, untimedMax, now - oldest, worstBucket);
    }

    /// <summary>
    /// The window's biggest parts by average per frame, for the heartbeat: " uitop=Radar:2.1/120.3,..."
    /// (avg/max ms, spaces in names as '_'). Empty when the window has no frames.
    /// </summary>
    public string HeartbeatField(long now, int top = 3)
    {
        var w = FoldWindow(now);
        if (w.frames == 0) return "";
        int n = FillWindowOrder(w.untimedSum, includeOutside: false);
        var sb = new StringBuilder(" uitop=");
        int shown = 0;
        for (int i = 0; i < n && shown < top; i++)
        {
            if (_orderTicks[i] <= 0) break;
            int s = _order[i];
            if (shown > 0) sb.Append(',');
            string name = s < 0 ? "untimed" : _names[s];
            sb.Append(name.Replace(' ', '_').Replace(',', ';').Replace('=', '-'));
            sb.Append(':').Append(Ms(_orderTicks[i] / w.frames)).Append('/').Append(Ms(s < 0 ? w.untimedMax : _winMax[s]));
            shown++;
        }
        if (shown == 0) return "";
        // The window's largest submit: " uisub=draws:412,texsw:3,vtx:31k/4.2M,up:0/0"
        // (vtx copied / what whole-list copies would have been; uploads font/other, summed).
        if (_winHasSubmit || _winFontUploads > 0 || _winTexUploads > 0)
        {
            sb.Append(" uisub=draws:").Append(_winSubmit.Draws.ToString(Inv))
              .Append(",texsw:").Append(_winSubmit.TexSwitches.ToString(Inv))
              .Append(",vtx:").Append(Compact(_winSubmit.VtxCopied)).Append('/').Append(Compact(_winSubmit.VtxWholeList))
              .Append(",up:").Append(_winFontUploads.ToString(Inv)).Append('/').Append(_winTexUploads.ToString(Inv));
        }
        return sb.ToString();
    }

    private int FillWindowOrder(long untimedSum, bool includeOutside)
    {
        int n = 0;
        int slots = SlotCount;
        for (int s = 0; s < slots; s++)
        {
            if (_winSum[s] <= 0 || _outside[s] != includeOutside) continue;
            _order[n] = s; _orderTicks[n] = _winSum[s]; n++;
        }
        if (!includeOutside && untimedSum > 0) { _order[n] = -1; _orderTicks[n] = untimedSum; n++; }
        SortDesc(n);
        return n;
    }

    /// <summary>
    /// The breakdown for /rc uicost now: the window's frames, the parts by average per frame (with
    /// their worst), the slowest frame part by part, and the 3D pass parts.
    /// </summary>
    public List<string> Describe(long now, int top = 8)
    {
        var lines = new List<string>();
        var w = FoldWindow(now);
        string state = Enabled ? $"timing on, slow frames logged at {_thresholdMs.ToString(Inv)} ms+" : "timing off";
        if (w.frames == 0)
        {
            lines.Add($"UI cost: no UI frames in the last {Buckets.ToString(Inv)} s ({state}).");
            return lines;
        }
        double span = w.spanTicks / (double)_freq;
        lines.Add($"UI cost, last {span.ToString("0.0", Inv)} s: {w.frames.ToString(Inv)} frames, avg {Ms(w.uiSum / w.frames)} ms, " +
                  $"max {Ms(w.uiMax)} ms, {w.slow.ToString(Inv)} at {_thresholdMs.ToString(Inv)} ms+ ({state}).");

        int n = FillWindowOrder(w.untimedSum, includeOutside: false);
        var sb = new StringBuilder("Per frame, avg (max): ");
        AppendWindowItems(sb, n, top, w.frames, w.untimedMax);
        lines.Add(sb.ToString());

        if (w.worstBucket >= 0)
        {
            int b = w.worstBucket;
            int m = 0;
            int slots = SlotCount;
            for (int s = 0; s < slots; s++)
            {
                if (_outside[s] || _bWorst[b, s] <= 0) continue;
                _order[m] = s; _orderTicks[m] = _bWorst[b, s]; m++;
            }
            _order[m] = -1; _orderTicks[m] = _bWorstUntimed[b]; m++;
            SortDesc(m);
            var wsb = new StringBuilder("Slowest frame ").Append(Ms(w.uiMax)).Append(" ms: ");
            AppendTop(wsb, m, top, s => _bWorstCount[b, s]);
            lines.Add(wsb.ToString());
        }

        if (_winHasSubmit || _winFontUploads > 0 || _winTexUploads > 0)
        {
            var ssb = new StringBuilder("ImGui submit, largest per frame");
            AppendSubmit(ssb, _winHasSubmit, _winSubmit, _winFontUploads, _winTexUploads);
            // "... | submit: ..." reads as "ImGui submit, largest per frame: ..." here; uploads are the window's sum.
            ssb.Replace(" | submit:", ":").Replace(" | uploads:", ": uploads (window total):").Replace("; uploads:", "; uploads (window total):");
            lines.Add(ssb.ToString());
        }

        int o = FillWindowOrder(0, includeOutside: true);
        if (o > 0)
        {
            var osb = new StringBuilder("3D pass, not in ui=, avg (max): ");
            AppendWindowItems(osb, o, top, w.frames, 0);
            lines.Add(osb.ToString());
        }
        return lines;
    }

    private void AppendWindowItems(StringBuilder sb, int n, int top, long frames, long untimedMax)
    {
        int shown = 0;
        for (int i = 0; i < n && shown < top; i++)
        {
            int s = _order[i];
            long avg = _orderTicks[i] / frames;
            if (shown > 0) sb.Append(", ");
            if (s < 0) sb.Append("untimed ").Append(Ms(avg)).Append(" (").Append(Ms(untimedMax)).Append(')');
            else
            {
                sb.Append(_names[s]).Append(' ').Append(Ms(avg)).Append(" (").Append(Ms(_winMax[s]));
                if (_winMaxCount[s] >= 0 && _units[s] is { } unit) sb.Append(", ").Append(_winMaxCount[s].ToString(Inv)).Append(' ').Append(unit);
                sb.Append(')');
            }
            shown++;
        }
        if (shown == 0) sb.Append("nothing timed");
        else if (n > shown) sb.Append(", +").Append((n - shown).ToString(Inv)).Append(" more");
    }

    /// <summary>The window's totals (tests): frames, average and max UI ms, slow frames.</summary>
    public (long Frames, double AvgMs, double MaxMs, long Slow) WindowTotals(long now)
    {
        var w = FoldWindow(now);
        if (w.frames == 0) return (0, 0, 0, 0);
        return (w.frames, w.uiSum * 1000.0 / _freq / w.frames, w.uiMax * 1000.0 / _freq, w.slow);
    }

    /// <summary>One slot's window total, average per frame and max, in ms (tests).</summary>
    public (double AvgMs, double MaxMs) SlotWindow(long now, int slot)
    {
        var w = FoldWindow(now);
        if (w.frames == 0 || (uint)slot >= (uint)SlotCount) return (0, 0);
        return (_winSum[slot] * 1000.0 / _freq / w.frames, _winMax[slot] * 1000.0 / _freq);
    }
}

/// <summary>
/// One ImGui submit's counters (DX9Backend.RenderLists): what the [UiCost] line and the
/// heartbeat print about the submit, so a slow one names its shape.
/// </summary>
internal struct SubmitStats
{
    /// <summary>Draw lists drawn.</summary>
    public int Lists;
    /// <summary>Draw calls made (commands with geometry, not clipped away).</summary>
    public int Draws;
    /// <summary>Commands skipped: clipped away or broken.</summary>
    public int Skipped;
    /// <summary>SetTexture calls that changed the texture.</summary>
    public int TexSwitches;
    /// <summary>Vertices handed to the draw calls (what the runtime copies).</summary>
    public long VtxCopied;
    /// <summary>What the same submit would have copied handing each command its whole list (the old renderer).</summary>
    public long VtxWholeList;

    public void Clear() => this = default;

    /// <summary>Folds another submit in (the window keeps each counter's largest).</summary>
    public void MaxWith(in SubmitStats o)
    {
        if (o.Lists > Lists) Lists = o.Lists;
        if (o.Draws > Draws) Draws = o.Draws;
        if (o.Skipped > Skipped) Skipped = o.Skipped;
        if (o.TexSwitches > TexSwitches) TexSwitches = o.TexSwitches;
        if (o.VtxCopied > VtxCopied) VtxCopied = o.VtxCopied;
        if (o.VtxWholeList > VtxWholeList) VtxWholeList = o.VtxWholeList;
    }
}

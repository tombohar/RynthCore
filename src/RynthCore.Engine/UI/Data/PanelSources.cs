// ============================================================================
//  RynthCore.Engine - UI/Data/PanelSources.cs
//  Hub sources for the P1 panels (Log, Tracker). Both faces of a panel read
//  the same snapshot; display strings are formatted here, on the pump thread,
//  so drawing allocates nothing.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RynthCore.Engine.UI.Data;

/// <summary>The engine's recent-log ring with the sequence number of its last line.</summary>
internal sealed record LogSnapshot(string[] Lines, long LastSeq)
{
    /// <summary>Sequence number of Lines[0].</summary>
    public long FirstSeq => LastSeq - Lines.Length + 1;
}

internal sealed class LogSource : UiSource<LogSnapshot>
{
    public LogSource() : base("Log", periodMs: 1000) { }

    protected internal override void Poll()
    {
        if (Current is { } cur && cur.Value.LastSeq == EntryPoint.RecentLogSeq) return;
        string[] lines = EntryPoint.GetRecentLogLines(out long lastSeq);
        Publish(new LogSnapshot(lines, lastSeq));
    }
}

/// <summary>
/// The Tracker panel's rows, formatted: ten (Lum/5m from RynthTracker 0.4.0), plus two pass-up rows
/// on ACECustom (the bank is read) and two Radiance rows on Aelrynth once earned.
/// </summary>
internal sealed record TrackerSnapshot(string[] Labels, string[] Values)
{
    public static readonly string[] DefaultLabels = { "Session", "XP/hr", "Lum/hr", "Lum/5m", "Kls/hr", "XP", "Lum", "Kills", "XP/kl", "Deaths" };
}

/// <summary>
/// RynthTracker's RynthTrackerGetSnapshotJson (single consumer: it frees the
/// previous buffer on each call, so only the hub may call it).
/// </summary>
internal sealed unsafe class TrackerSource : UiSource<TrackerSnapshot>
{
    private delegate* unmanaged[Cdecl]<IntPtr> _getSnapshotJson;
    private delegate* unmanaged[Cdecl]<void> _reset;
    private string? _lastJson;

    public TrackerSource() : base("Tracker", periodMs: 500) { }

    /// <summary>Resets the session counters (RynthTrackerReset). Any thread; runs on the pump.</summary>
    public void RequestReset()
    {
        UiDataHub.Post("Tracker reset", () =>
        {
            Bind();
            if (_reset != null) _reset();
            RequestRefresh();
        });
    }

    protected internal override void Poll()
    {
        Bind();
        if (_getSnapshotJson == null) return;

        IntPtr ptr = _getSnapshotJson();
        string? json = ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr); // copy now: freed on the next call
        if (string.IsNullOrEmpty(json) || json == _lastJson) return;
        _lastJson = json;

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement r = doc.RootElement;
        double Num(string key) => r.TryGetProperty(key, out JsonElement v) && v.TryGetDouble(out double d) ? d : 0;

        CultureInfo ic = CultureInfo.InvariantCulture;
        // At the level cap the server adds no experience, so the XP rows say so instead of 0.
        bool maxLevel = Num("ml") > 0;
        string Xp(long v) => maxLevel ? "max level" : FormatNum(v);
        var labels = new List<string> { "Session", "XP/hr", "Lum/hr" };
        var values = new List<string> { FormatTime(Num("ss")), Xp((long)Num("xh")), FormatNum((long)Num("lh")) };
        // Luminance of the last five minutes (RynthTracker 0.4.0; an older one doesn't send it).
        if (r.TryGetProperty("l5", out _)) { labels.Add("Lum/5m"); values.Add(FormatNum((long)Num("l5"))); }
        // Allegiance pass-up: only where the plugin can tell it apart (ACECustom's bank is read).
        bool passUp = Num("pk") > 0;
        if (passUp) { labels.Add("Pass/hr"); values.Add(FormatNum((long)Num("ph"))); }
        // Radiance (Aelrynth's Bank session total): rows only once some has been earned.
        long rad = (long)Num("rt");
        if (rad > 0) { labels.Add("Rad/hr"); values.Add(FormatNum((long)Num("rh"))); }
        labels.AddRange(new[] { "Kls/hr", "XP", "Lum" });
        values.AddRange(new[] { Num("kh").ToString("F1", ic), Xp((long)Num("xt")), FormatNum((long)Num("lt")) });
        if (passUp) { labels.Add("Pass-up"); values.Add(FormatNum((long)Num("pt"))); }
        if (rad > 0) { labels.Add("Rad"); values.Add(FormatNum(rad)); }
        labels.AddRange(new[] { "Kills", "XP/kl", "Deaths" });
        values.AddRange(new[] { ((long)Num("kt")).ToString(ic), Xp((long)Num("xk")), ((long)Num("dt")).ToString(ic) });
        Publish(new TrackerSnapshot(labels.ToArray(), values.ToArray()));
    }

    protected internal override void Reset()
    {
        _getSnapshotJson = null;
        _reset = null;
        _lastJson = null;
        ClearSnapshot();
    }

    private void Bind()
    {
        if (_getSnapshotJson == null)
            _getSnapshotJson = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthTracker", "RynthTrackerGetSnapshotJson");
        if (_reset == null)
            _reset = (delegate* unmanaged[Cdecl]<void>)PluginExportBinder.Resolve("RynthTracker", "RynthTrackerReset");
    }

    // Same formatting as the Avalonia Tracker had.
    private static string FormatTime(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    private static string FormatNum(long value)
    {
        if (value == 0) return "0";
        bool neg = value < 0;
        string s = (neg ? -value : value).ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder(s.Length + s.Length / 3);
        int offset = s.Length % 3;
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && (i - offset) % 3 == 0) sb.Append(',');
            sb.Append(s[i]);
        }
        return neg ? "-" + sb : sb.ToString();
    }
}

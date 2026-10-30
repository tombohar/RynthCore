using System;
using System.Diagnostics;
using System.Globalization;

namespace RynthCore.App.Avalonia;

/// <summary>The step a launcher download is on.</summary>
public enum DownloadStage
{
    /// <summary>Bytes arriving: Done/Total are bytes, against the signed size from the feed.</summary>
    Downloading,
    /// <summary>The download finished and matched the signed size and SHA-256.</summary>
    Verified,
    /// <summary>Unpacking an archive: Done/Total are files.</summary>
    Unpacking,
    /// <summary>Putting the files in place (no measurable progress).</summary>
    Installing,
}

/// <summary>
/// One progress report for the launcher's download area. <see cref="What"/> names the download
/// ("RynthNav map", "RynthOracle", "RynthCore 2026.10.5.1"). Reports come from the updater at a
/// few a second at most (<see cref="DownloadMeter"/>); the first, the last and every stage change
/// always come through.
/// </summary>
public sealed record DownloadProgress(string What, DownloadStage Stage, long Done, long Total,
                                      double BytesPerSecond = 0, TimeSpan? Remaining = null, TimeSpan Elapsed = default)
{
    /// <summary>0..100, or null when the step has no measurable progress.</summary>
    public double? Percent => Total > 0 ? Math.Clamp(Done * 100.0 / Total, 0, 100) : null;

    /// <summary>The line under the progress bar.</summary>
    public string Detail => Stage switch
    {
        DownloadStage.Downloading => FormatDownloading(),
        DownloadStage.Verified => $"{FormatMb(Total)} downloaded and verified against the signed feed.",
        DownloadStage.Unpacking => Total > 0
            ? $"Unpacking {Done.ToString("N0", CultureInfo.InvariantCulture)} of {Total.ToString("N0", CultureInfo.InvariantCulture)} files…"
            : "Unpacking…",
        _ => "Installing…",
    };

    /// <summary>The activity-log line when a download is done: "RynthNav map: 539 MB in 2m10s, verified".</summary>
    public string Summary => $"{What}: {FormatMb(Total)} in {FormatDuration(Elapsed)}, verified";

    private string FormatDownloading()
    {
        string s = $"{FormatMb(Done, withUnit: false)} of {FormatMb(Total)}";
        if (BytesPerSecond > 0) s += $" · {FormatMb((long)BytesPerSecond)}/s";
        if (Remaining is { } r) s += $" · about {FormatDuration(r)} left";
        return s;
    }

    /// <summary>"539 MB", "12.4 MB", "0.3 MB" (one decimal under 100 MB).</summary>
    public static string FormatMb(long bytes, bool withUnit = true)
    {
        double mb = bytes / (1024.0 * 1024.0);
        string n = mb >= 100 ? mb.ToString("N0", CultureInfo.InvariantCulture) : mb.ToString("0.0", CultureInfo.InvariantCulture);
        return withUnit ? n + " MB" : n;
    }

    /// <summary>"8s", "2m10s", "1h05m".</summary>
    public static string FormatDuration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m{t.Seconds:00}s";
        return $"{Math.Max(0, (int)Math.Round(t.TotalSeconds))}s";
    }
}

/// <summary>
/// Measures one download: speed over the last few seconds, time left, and throttling so the UI
/// gets a handful of reports a second rather than one per 80 KB read. The clock is injectable
/// for tests.
/// </summary>
public sealed class DownloadMeter
{
    public static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(4);

    private readonly string _what;
    private readonly long _total;
    private readonly IProgress<DownloadProgress>? _progress;
    private readonly Func<TimeSpan> _clock;
    private readonly TimeSpan _start;
    private TimeSpan _lastReport = TimeSpan.MinValue;
    // A ring of (time, bytes) samples for the speed over the last few seconds.
    private readonly (TimeSpan T, long Bytes)[] _samples = new (TimeSpan, long)[32];
    private int _sampleCount, _sampleHead;

    public DownloadMeter(string what, long total, IProgress<DownloadProgress>? progress, Func<TimeSpan>? clock = null)
    {
        _what = what;
        _total = total;
        _progress = progress;
        if (clock == null)
        {
            var sw = Stopwatch.StartNew();
            clock = () => sw.Elapsed;
        }
        _clock = clock;
        _start = _clock();
        AddSample(_start, 0);
    }

    public TimeSpan Elapsed => _clock() - _start;

    /// <summary>Called after each read; reports at most every <see cref="ReportEvery"/>, and always at the end.</summary>
    public void Report(long done)
    {
        if (_progress == null) return;
        TimeSpan now = _clock();
        bool last = done >= _total;
        if (!last && _lastReport != TimeSpan.MinValue && now - _lastReport < ReportEvery) return;
        _lastReport = now;
        AddSample(now, done);
        double speed = Speed(now, done);
        TimeSpan? remaining = speed > 0 && !last ? TimeSpan.FromSeconds((_total - done) / speed) : last ? TimeSpan.Zero : null;
        _progress.Report(new DownloadProgress(_what, DownloadStage.Downloading, done, _total, speed, remaining, now - _start));
    }

    /// <summary>The download matched the signed size and hash.</summary>
    public void Verified() =>
        _progress?.Report(new DownloadProgress(_what, DownloadStage.Verified, _total, _total, 0, TimeSpan.Zero, Elapsed));

    private void AddSample(TimeSpan t, long bytes)
    {
        _samples[_sampleHead] = (t, bytes);
        _sampleHead = (_sampleHead + 1) % _samples.Length;
        if (_sampleCount < _samples.Length) _sampleCount++;
    }

    private double Speed(TimeSpan now, long done)
    {
        // The oldest sample still inside the window (or the oldest kept) is the baseline.
        (TimeSpan T, long Bytes) baseline = (now, done);
        for (int i = 0; i < _sampleCount; i++)
        {
            var s = _samples[(_sampleHead - _sampleCount + i + _samples.Length) % _samples.Length];
            if (now - s.T <= SpeedWindow || i == _sampleCount - 1) { baseline = s; break; }
        }
        double secs = (now - baseline.T).TotalSeconds;
        return secs > 0.05 ? (done - baseline.Bytes) / secs : 0;
    }
}

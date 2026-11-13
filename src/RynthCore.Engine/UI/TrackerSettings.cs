// ============================================================================
//  RynthCore.Engine - UI/TrackerSettings.cs
//  The Tracker panel's background opacity, its text-only look, the Ledger's full click-through
//  and the stat rows it hides, shared by its ImGui and Avalonia faces:
//  %APPDATA%\RynthCore\rynthtracker_settings.json
//  ({"bgAlpha":0-255,"textOnly":false,"clickThrough":false,"hidden":["lum5m",...]}).
//  Loaded on the engine init worker; saves go through UiBackgroundWriter so
//  the ImGui face never writes a file from AC's thread.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RynthCore.Engine.UI;

/// <summary>A row the Tracker can show: a stable id (saved), the snapshot's label, and what it is.</summary>
internal sealed record TrackerStat(string Id, string Label, string Name, string? Note = null);

/// <summary>What the settings file holds (TrackerSettings.Parse).</summary>
internal sealed record TrackerSettingsData(int BgAlpha, bool TextOnly, bool ClickThrough, string[] Hidden);

internal static class TrackerSettings
{
    /// <summary>
    /// Every row TrackerSource can send, in its order. The id is what "hidden" saves; the label is the
    /// snapshot's (UI/Data/PanelSources.cs TrackerSource.Poll). Pass and Rad rows only come on some servers.
    /// </summary>
    internal static readonly TrackerStat[] Stats =
    {
        new("session",   "Session", "Session time"),
        new("xpRate",    "XP/hr",   "XP per hour"),
        new("lumRate",   "Lum/hr",  "Luminance per hour"),
        new("lum5m",     "Lum/5m",  "Luminance, last 5 minutes"),
        new("passRate",  "Pass/hr", "Pass-up per hour", "Only where the bank is read (ACECustom)."),
        new("radRate",   "Rad/hr",  "Radiance per hour", "Only on Aelrynth, once some is earned."),
        new("killRate",  "Kls/hr",  "Kills per hour"),
        new("xp",        "XP",      "XP total"),
        new("lum",       "Lum",     "Luminance total"),
        new("passUp",    "Pass-up", "Pass-up total", "Only where the bank is read (ACECustom)."),
        new("rad",       "Rad",     "Radiance total", "Only on Aelrynth, once some is earned."),
        new("kills",     "Kills",   "Kills"),
        new("xpPerKill", "XP/kl",   "XP per kill"),
        new("deaths",    "Deaths",  "Deaths"),
    };

    private static volatile int _bgAlpha = 0xCC; // ~80%
    private static volatile bool _textOnly;
    private static volatile bool _clickThrough;
    // Replaced whole on a change (never edited), so the background save reads a settled set.
    private static volatile HashSet<string> _hidden = new(StringComparer.Ordinal);
    private static volatile int _hiddenVersion;
    private static volatile bool _loaded;
    private static readonly object LoadSync = new();

    // RYNTHCORE_TRACKER_SETTINGS: another file (tools\TrackerSettingsTests).
    private static string SettingsPath =>
        Environment.GetEnvironmentVariable("RYNTHCORE_TRACKER_SETTINGS") is { Length: > 0 } over ? over
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "rynthtracker_settings.json");

    /// <summary>Background alpha 0-255.</summary>
    public static byte BgAlpha
    {
        get
        {
            EnsureLoaded();
            return (byte)_bgAlpha;
        }
    }

    /// <summary>
    /// Text only (2026-11-01, the owner: "make the window invisible besides the writing"): docked, the
    /// panel has no frame, title or background, only its rows (shadowed), and clicks pass through
    /// except over them.
    /// </summary>
    public static bool TextOnly
    {
        get
        {
            EnsureLoaded();
            return _textOnly;
        }
    }

    /// <summary>
    /// Full click-through (2026-11-12, the owner: "It needs to have click through capability"): text
    /// only, the Ledger never takes the mouse, even over its numbers, unless Ctrl is held.
    /// </summary>
    public static bool ClickThrough
    {
        get
        {
            EnsureLoaded();
            return _clickThrough;
        }
    }

    /// <summary>Goes up each time the hidden set changes (the face rebuilds its rows).</summary>
    public static int HiddenVersion
    {
        get
        {
            EnsureLoaded();
            return _hiddenVersion;
        }
    }

    /// <summary>The stat id for a snapshot label (a label not in <see cref="Stats"/> is its own id).</summary>
    public static string StatId(string label)
    {
        foreach (TrackerStat s in Stats)
            if (s.Label == label) return s.Id;
        return label;
    }

    /// <summary>Whether the row with this snapshot label is hidden (2026-11-12: "show/hide some of the stats").</summary>
    public static bool IsLabelHidden(string label) => IsHidden(StatId(label));

    public static bool IsHidden(string id)
    {
        EnsureLoaded();
        return _hidden.Contains(id);
    }

    /// <summary>The hidden ids, in <see cref="Stats"/> order.</summary>
    public static string[] HiddenIds
    {
        get
        {
            EnsureLoaded();
            return Ordered(_hidden);
        }
    }

    /// <summary>Sets the alpha now; <paramref name="save"/> writes the file in the background.</summary>
    public static void SetBgAlpha(byte alpha, bool save)
    {
        EnsureLoaded();
        _bgAlpha = alpha;
        if (save)
            UiBackgroundWriter.Enqueue("tracker settings", Save);
    }

    /// <summary>Text only on or off, saved in the background.</summary>
    public static void SetTextOnly(bool on)
    {
        EnsureLoaded();
        _textOnly = on;
        UiBackgroundWriter.Enqueue("tracker settings", Save);
    }

    /// <summary>Full click-through on or off, saved in the background.</summary>
    public static void SetClickThrough(bool on)
    {
        EnsureLoaded();
        _clickThrough = on;
        UiBackgroundWriter.Enqueue("tracker settings", Save);
    }

    /// <summary>Hides or shows one stat (by id), saved in the background.</summary>
    public static void SetHidden(string id, bool hidden)
    {
        EnsureLoaded();
        if (!IsKnownId(id) || _hidden.Contains(id) == hidden) return;
        var next = new HashSet<string>(_hidden, StringComparer.Ordinal);
        if (hidden) next.Add(id); else next.Remove(id);
        _hidden = next;
        _hiddenVersion++;
        UiBackgroundWriter.Enqueue("tracker settings", Save);
    }

    /// <summary>Shows every stat again, saved in the background.</summary>
    public static void ShowAll()
    {
        EnsureLoaded();
        if (_hidden.Count == 0) return;
        _hidden = new HashSet<string>(StringComparer.Ordinal);
        _hiddenVersion++;
        UiBackgroundWriter.Enqueue("tracker settings", Save);
    }

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (LoadSync)
        {
            if (_loaded) return;
            try
            {
                string path = SettingsPath;
                if (File.Exists(path))
                {
                    TrackerSettingsData d = Parse(File.ReadAllText(path));
                    _bgAlpha = d.BgAlpha;
                    _textOnly = d.TextOnly;
                    _clickThrough = d.ClickThrough;
                    _hidden = new HashSet<string>(d.Hidden, StringComparer.Ordinal);
                }
            }
            catch { }
            _loaded = true;
        }
    }

    private static bool IsKnownId(string id)
    {
        foreach (TrackerStat s in Stats)
            if (s.Id == id) return true;
        return false;
    }

    private static string[] Ordered(IReadOnlyCollection<string> ids)
    {
        var list = new List<string>();
        foreach (TrackerStat s in Stats)
            foreach (string id in ids)
                if (id == s.Id) { list.Add(id); break; }
        return list.ToArray();
    }

    /// <summary>
    /// The file's settings; a missing key or a value of the wrong kind is the default (opacity ~80%,
    /// framed, click-through off, every stat shown); unknown or repeated stat ids are dropped.
    /// Throws only when the text is not JSON.
    /// </summary>
    internal static TrackerSettingsData Parse(string json)
    {
        int bgAlpha = 0xCC;
        bool textOnly = false, clickThrough = false;
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        JsonElement r = doc.RootElement;
        if (r.ValueKind == JsonValueKind.Object)
        {
            if (r.TryGetProperty("bgAlpha", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int a))
                bgAlpha = Math.Clamp(a, 0, 255);
            if (r.TryGetProperty("textOnly", out var t) && t.ValueKind is JsonValueKind.True or JsonValueKind.False)
                textOnly = t.GetBoolean();
            if (r.TryGetProperty("clickThrough", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False)
                clickThrough = c.GetBoolean();
            if (r.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in h.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { } id && IsKnownId(id))
                        hidden.Add(id);
        }
        return new TrackerSettingsData(bgAlpha, textOnly, clickThrough, Ordered(hidden));
    }

    /// <summary>The settings file's text.</summary>
    internal static string ToJson(int bgAlpha, bool textOnly, bool clickThrough = false, IReadOnlyCollection<string>? hidden = null)
    {
        var sb = new StringBuilder();
        sb.Append("{\"bgAlpha\":").Append(bgAlpha)
          .Append(",\"textOnly\":").Append(textOnly ? "true" : "false")
          .Append(",\"clickThrough\":").Append(clickThrough ? "true" : "false")
          .Append(",\"hidden\":[");
        bool first = true;
        foreach (string id in Ordered(hidden ?? Array.Empty<string>()))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(id).Append('"');   // only known ids (Ordered): plain letters, nothing to escape
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static void Save()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ToJson(_bgAlpha, _textOnly, _clickThrough, _hidden));
        }
        catch { }
    }
}

// ============================================================================
//  RynthCore.Headless - Host/Status/RemoteMove.cs
//  Hold-to-move from the phone (DrakRemote), the protocol RynthRemote speaks for a retail
//  client; the rules are RynthRemote's MoveHold (RynthRemotePlugin/RemoteMove.cs, our own
//  code), restated here so a headless character answers the phone the same way.
//
//  Protocol 2: moveStart {"dir","deadmanMs"} (the phone promises keep-alives), then
//  moveKeepAlive {"dirs":[...]} every ~200 ms while held; with none for deadmanMs (default
//  600, 300..3000) every held direction is let go. A keep-alive lets go of any held
//  direction it no longer names, and never starts anything except to resume what the
//  dead-man itself stopped within ResumeMs (never after an explicit stop).
//  Legacy (a bare direction): a 2 s window re-armed by each repeated start.
//  moveStop "stop" (or anything unreadable) lets go of everything; a direction, of that one.
// ============================================================================

using System.Text.Json;

namespace RynthCore.Headless.Host.Status;

public sealed class RemoteMove
{
    public const long LegacyDeadMs = 2000;
    public const long DefaultDeadMs = 600;
    public const long MinDeadMs = 300;
    public const long MaxDeadMs = 3000;
    public const long ResumeMs = 1500;

    public static readonly string[] Dirs = { "forward", "back", "left", "right" };

    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resumable = new(StringComparer.Ordinal);
    private bool _armed, _keepAliveMode;
    private long _deadMs = LegacyDeadMs, _deadline, _resumableUntil;

    public IReadOnlyCollection<string> Held => _held;
    public bool KeepAliveMode => _keepAliveMode;

    public static bool IsDir(string? d) => d != null && Array.IndexOf(Dirs, d) >= 0;

    /// <summary>A press; the key changes to make ((dir, true) only when it wasn't already held).</summary>
    public List<(string Dir, bool On)> Start(string dir, long? deadmanMs, long now)
    {
        var acts = new List<(string, bool)>(1);
        if (!IsDir(dir)) return acts;
        if (deadmanMs is long ms)
        {
            _keepAliveMode = true;
            _deadMs = Math.Clamp(ms, MinDeadMs, MaxDeadMs);
        }
        else if (_held.Count == 0)
        {
            _keepAliveMode = false;
            _deadMs = LegacyDeadMs;
        }
        _resumable.Clear();
        if (_held.Add(dir)) acts.Add((dir, true));
        _armed = true;
        _deadline = now + _deadMs;
        return acts;
    }

    /// <summary>A release; always reports it, held or not (an explicit stop must always stop).</summary>
    public List<(string Dir, bool On)> Stop(string what)
    {
        var acts = new List<(string, bool)>(4);
        _resumable.Clear();
        if (what == "stop")
        {
            foreach (string d in Dirs) acts.Add((d, false));
            _held.Clear();
        }
        else if (IsDir(what))
        {
            _held.Remove(what);
            acts.Add((what, false));
        }
        if (_held.Count == 0) _armed = false;
        return acts;
    }

    /// <summary>"Still held: these." Refreshes the dead-man and lets go of held directions not named.</summary>
    public List<(string Dir, bool On)> KeepAlive(IReadOnlyCollection<string> dirs, long now)
    {
        var acts = new List<(string, bool)>(2);
        if (_held.Count == 0)
        {
            if (_resumable.Count == 0 || now > _resumableUntil) return acts;
            foreach (string d in dirs)
                if (_resumable.Contains(d) && _held.Add(d)) acts.Add((d, true));
            _resumable.Clear();
            if (_held.Count == 0) return acts;
            _armed = true;
            _deadline = now + _deadMs;
            return acts;
        }
        foreach (string d in _held.ToList())
            if (!dirs.Contains(d)) { _held.Remove(d); acts.Add((d, false)); }
        if (_held.Count == 0) { _armed = false; return acts; }
        _deadline = now + _deadMs;
        return acts;
    }

    /// <summary>Every tick: past the window, lets go of every direction; <paramref name="fired"/> says so.</summary>
    public List<(string Dir, bool On)> Tick(long now, out bool fired)
    {
        fired = false;
        var acts = new List<(string, bool)>(0);
        if (!_armed || now <= _deadline) return acts;
        fired = true;
        if (_keepAliveMode && _held.Count > 0)
        {
            _resumable.Clear();
            foreach (string d in _held) _resumable.Add(d);
            _resumableUntil = now + ResumeMs;
        }
        foreach (string d in Dirs) acts.Add((d, false));
        _held.Clear();
        _armed = false;
        return acts;
    }

    /// <summary>Lets go of everything at once (the session is going away).</summary>
    public void Clear()
    {
        _held.Clear();
        _resumable.Clear();
        _armed = false;
    }

    // ── command values ──────────────────────────────────────────────────────

    /// <summary>A moveStart value: a bare direction (legacy) or {"dir":"right","deadmanMs":600}.</summary>
    public static bool TryParseStart(string? value, out string dir, out long? deadmanMs)
    {
        dir = "";
        deadmanMs = null;
        string v = (value ?? "").Trim();
        if (v.Length == 0) return false;
        if (v[0] != '{') { dir = v.ToLowerInvariant(); return IsDir(dir); }
        try
        {
            using var doc = JsonDocument.Parse(v);
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return false;
            if (r.TryGetProperty("dir", out JsonElement d) && d.ValueKind == JsonValueKind.String) dir = (d.GetString() ?? "").ToLowerInvariant();
            deadmanMs = DefaultDeadMs;
            if (r.TryGetProperty("deadmanMs", out JsonElement m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt64(out long ms)) deadmanMs = ms;
            return IsDir(dir);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>A moveKeepAlive value: {"dirs":["forward","right"]}; unknown names are dropped.</summary>
    public static bool TryParseKeepAlive(string? value, out List<string> dirs)
    {
        dirs = new List<string>(2);
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(value) ? "{}" : value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("dirs", out JsonElement a) || a.ValueKind != JsonValueKind.Array) return false;
            foreach (JsonElement e in a.EnumerateArray())
            {
                string d = e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "").ToLowerInvariant() : "";
                if (IsDir(d) && !dirs.Contains(d)) dirs.Add(d);
            }
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>A moveStop value: "stop" or a direction; anything unreadable stops everything.</summary>
    public static string ParseStop(string? value)
    {
        string v = (value ?? "").Trim().ToLowerInvariant();
        return v == "stop" || IsDir(v) ? v : "stop";
    }
}

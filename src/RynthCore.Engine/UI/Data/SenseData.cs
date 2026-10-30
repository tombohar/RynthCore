// ============================================================================
//  RynthCore.Engine - UI/Data/SenseData.cs
//  Sense, our take on Virindi Sense: a watch list of names, and every object
//  the client knows about, anywhere, whose name matches one.
//
//    SenseStore    the watch list and the alert settings, in
//                  %LocalAppData%\RynthCore\sense.txt (read and written on
//                  UiBackgroundWriter, never on AC's render thread).
//    SensePattern  one term, case-insensitive. Without wildcards the name only
//                  has to contain it ("drake" finds "Dragon's Isle Drake");
//                  with * or ? the whole name must match ("Drake*", "?ire Ant").
//    SenseSource   the scan: on the plugin pump thread at 2 Hz, reading only the
//                  engine's main-thread snapshots (LiveObjectIds, the identity,
//                  position, PWD and attackable snapshots, the player pose),
//                  never AC memory. Publishes an immutable SenseView and raises
//                  the optional chat line / beep for a new match. Polled while
//                  the Sense face is open, and while an alert is on with at least
//                  one enabled term, so alerts work with the panel closed.
//
//  How far it sees: the whole client object table, not a radar radius. The
//  client knows what the server has sent it, which is every object in the
//  landblocks it has loaded around you (outdoors on ACE: your 192 m landblock
//  and the eight around it, so a few hundred yards, far past the radar), or
//  the dungeon you are in.
//
//  Distances: outdoors and in surface buildings the landblock grid is one
//  space (world = landblock x 192 m + cell-local origin). A dungeon has its own
//  space: a distance is shown only when you and the object are in the same
//  landblock, or both in the overworld; otherwise the row says "elsewhere" and
//  gives the landblock. A landblock counts as overworld when anything known in
//  it (or you) stands in an outdoor cell; indoor cells of a landblock with no
//  outdoor objects are taken for a dungeon.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.UI.Data;

/// <summary>One watch-list term. Immutable.</summary>
internal sealed class SensePattern
{
    public SensePattern(string text, bool enabled)
    {
        Text = (text ?? string.Empty).Trim();
        Enabled = enabled;
        IsGlob = Text.IndexOfAny(Wildcards) >= 0;
    }

    private static readonly char[] Wildcards = { '*', '?' };

    public string Text { get; }
    public bool Enabled { get; }
    /// <summary>True when the term has * or ?: the whole name must match it.</summary>
    public bool IsGlob { get; }

    public bool Matches(string name)
    {
        if (Text.Length == 0 || string.IsNullOrEmpty(name)) return false;
        return IsGlob ? Glob(name, Text) : name.Contains(Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Case-insensitive whole-string match of <paramref name="s"/> against
    /// <paramref name="p"/> (* any run, ? one character). Iterative, backtracking
    /// only to the last *; no allocation.
    /// </summary>
    internal static bool Glob(string s, string p)
    {
        int si = 0, pi = 0, star = -1, mark = 0;
        while (si < s.Length)
        {
            if (pi < p.Length && p[pi] == '*') { star = pi++; mark = si; }
            else if (pi < p.Length && (p[pi] == '?' || Same(p[pi], s[si]))) { si++; pi++; }
            else if (star >= 0) { pi = star + 1; si = ++mark; }
            else return false;
        }
        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }

    private static bool Same(char a, char b) => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}

/// <summary>
/// The watch list and alert settings. Edited on AC's thread (the face, /rc sense),
/// read by the scan on the pump thread: the term list is an immutable array
/// swapped whole, with a version the scan compares. File I/O on UiBackgroundWriter.
/// </summary>
internal static class SenseStore
{
    public const int MaxTerms = 40;
    public const int MaxTermLength = 64;

    private static readonly object Sync = new();
    private static SensePattern[] _terms = Array.Empty<SensePattern>();
    private static long _version;
    private static volatile bool _loaded;
    private static bool _loadQueued;
    private static volatile bool _alertChat;
    private static volatile bool _alertBeep;
    private static bool _alertSubscribed;

    /// <summary>The current terms (never null; do not modify). Any thread.</summary>
    public static SensePattern[] Terms => Volatile.Read(ref _terms);

    /// <summary>Bumped on every change to the terms. Any thread.</summary>
    public static long Version => Interlocked.Read(ref _version);

    public static bool Loaded => _loaded;

    /// <summary>A chat line when a new match appears (off by default).</summary>
    public static bool AlertChat => _alertChat;

    /// <summary>The system beep when a new match appears (off by default).</summary>
    public static bool AlertBeep => _alertBeep;

    public static bool AnyEnabled
    {
        get
        {
            foreach (SensePattern t in Terms)
                if (t.Enabled && t.Text.Length > 0) return true;
            return false;
        }
    }

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "sense.txt");

    /// <summary>Queues the one-time read off the calling thread. Any thread; cheap after the first call.</summary>
    public static void EnsureLoadQueued()
    {
        if (_loaded) return;
        lock (Sync)
        {
            if (_loadQueued) return;
            _loadQueued = true;
        }
        UiBackgroundWriter.Enqueue("sense list (load)", LoadNow);
    }

    private static void LoadNow()
    {
        var list = new List<SensePattern>();
        bool chat = false, beep = false;
        try
        {
            string path = FilePath;
            if (File.Exists(path))
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line[..eq].Trim();
                    string val = line[(eq + 1)..];
                    switch (key)
                    {
                        case "alertChat": chat = val.Trim() == "1"; break;
                        case "alertBeep": beep = val.Trim() == "1"; break;
                        case "term":
                        {
                            // term=1|pattern (1 on, 0 off); a bare term=pattern is on.
                            bool on = true;
                            string text = val;
                            int bar = val.IndexOf('|');
                            if (bar is 1 && (val[0] == '0' || val[0] == '1'))
                            {
                                on = val[0] == '1';
                                text = val[2..];
                            }
                            text = Clean(text);
                            if (text.Length > 0 && list.Count < MaxTerms && IndexOf(list, text) < 0)
                                list.Add(new SensePattern(text, on));
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RynthLog.UI($"Sense: reading sense.txt failed - {ex.GetType().Name}: {ex.Message}");
        }

        lock (Sync)
        {
            Volatile.Write(ref _terms, list.ToArray());
            _alertChat = chat;
            _alertBeep = beep;
            Interlocked.Increment(ref _version);
            _loaded = true;
        }
        SyncAlertSubscription();
    }

    private static string Clean(string text)
    {
        text = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > MaxTermLength ? text[..MaxTermLength] : text;
    }

    private static int IndexOf(IReadOnlyList<SensePattern> list, string text)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Text, text, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Adds a term (switched on). An existing term with the same text is switched
    /// on instead. False when the text is empty, the list is full or not loaded yet.
    /// </summary>
    public static bool Add(string text, out string message)
    {
        text = Clean(text);
        if (!_loaded) { message = "The watch list is still loading."; return false; }
        if (text.Length == 0) { message = "Type a name first."; return false; }
        lock (Sync)
        {
            SensePattern[] cur = _terms;
            int at = IndexOf(cur, text);
            if (at >= 0)
            {
                message = cur[at].Enabled ? $"\"{cur[at].Text}\" is already on the list." : $"\"{cur[at].Text}\" switched on.";
                if (!cur[at].Enabled) Replace(at, new SensePattern(cur[at].Text, true));
                return true;
            }
            if (cur.Length >= MaxTerms) { message = $"The list is full ({MaxTerms} terms)."; return false; }
            var next = new SensePattern[cur.Length + 1];
            Array.Copy(cur, next, cur.Length);
            next[^1] = new SensePattern(text, true);
            Publish(next);
            message = $"Watching for \"{text}\".";
            return true;
        }
    }

    public static void SetEnabled(int index, bool enabled)
    {
        lock (Sync)
        {
            SensePattern[] cur = _terms;
            if ((uint)index >= (uint)cur.Length || cur[index].Enabled == enabled) return;
            Replace(index, new SensePattern(cur[index].Text, enabled));
        }
    }

    public static void Remove(int index)
    {
        lock (Sync)
        {
            SensePattern[] cur = _terms;
            if ((uint)index >= (uint)cur.Length) return;
            var next = new SensePattern[cur.Length - 1];
            Array.Copy(cur, 0, next, 0, index);
            Array.Copy(cur, index + 1, next, index, cur.Length - index - 1);
            Publish(next);
        }
    }

    public static void SetAlerts(bool chat, bool beep)
    {
        if (chat == _alertChat && beep == _alertBeep) return;
        _alertChat = chat;
        _alertBeep = beep;
        SyncAlertSubscription();
        Save();
    }

    // Under Sync.
    private static void Replace(int index, SensePattern pattern)
    {
        var next = (SensePattern[])_terms.Clone();
        next[index] = pattern;
        Publish(next);
    }

    // Under Sync.
    private static void Publish(SensePattern[] next)
    {
        Volatile.Write(ref _terms, next);
        Interlocked.Increment(ref _version);
        UiSources.Sense.RequestRefresh();
        SyncAlertSubscription();
        Save();
    }

    /// <summary>
    /// Keeps the scan running with the panel closed while an alert is on and a term
    /// is enabled (one subscription of its own). Any thread.
    /// </summary>
    public static void SyncAlertSubscription()
    {
        bool want = _loaded && (_alertChat || _alertBeep) && AnyEnabled;
        lock (Sync)
        {
            if (want == _alertSubscribed) return;
            _alertSubscribed = want;
            if (want) UiSources.Sense.Subscribe();
            else UiSources.Sense.Unsubscribe();
        }
    }

    /// <summary>Writes the list in the background. Any thread.</summary>
    public static void Save()
    {
        if (!_loaded) return;   // never overwrite the file with the empty pre-load list
        UiBackgroundWriter.Enqueue("sense list", SaveNow);
    }

    private static void SaveNow()
    {
        try
        {
            SensePattern[] terms = Terms;
            var sb = new StringBuilder();
            sb.AppendLine("# RynthCore Sense watch list - auto-generated, hand-edits OK. /rc sense in game.");
            sb.AppendLine("# term=1|name (1 on, 0 off). No * or ?: the name contains it. With * or ?: the whole name matches.");
            sb.AppendLine(_alertChat ? "alertChat=1" : "alertChat=0");
            sb.AppendLine(_alertBeep ? "alertBeep=1" : "alertBeep=0");
            foreach (SensePattern t in terms)
                sb.Append("term=").Append(t.Enabled ? '1' : '0').Append('|').AppendLine(t.Text);
            string path = FilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString());
        }
        catch (Exception ex)
        {
            RynthLog.UI($"Sense: writing sense.txt failed - {ex.GetType().Name}: {ex.Message}");
        }
    }
}

internal enum SenseKind : byte { Monster, Npc, Player, Item, Portal, Corpse }

/// <summary>One match, with its texts built by the scan. Immutable once published.</summary>
internal sealed class SenseRow
{
    public uint Id;
    public string Name = "";
    public SenseKind Kind;
    /// <summary>True when a distance and bearing mean something (same space as you).</summary>
    public bool Measured;
    public float Distance;            // yd (= m); +inf when not measured
    public float Bearing = float.NaN; // compass degrees from you, 0 = North, clockwise; NaN unknown / on top of you
    public string DistanceText = "";
    public string CompassText = "";
    public string LocationText = "";
    public string Tooltip = "";
    /// <summary>Environment.TickCount64 when the match first appeared (0 = was already there when the scan started).</summary>
    public long FirstSeenMs;
}

/// <summary>A published scan.</summary>
internal sealed class SenseView
{
    public static readonly SenseView Empty = new(Array.Empty<SenseRow>(), "", 0);

    public SenseView(SenseRow[] rows, string status, int known)
    {
        Rows = rows;
        Status = status;
        Known = known;
    }

    public SenseRow[] Rows { get; }
    public string Status { get; }
    /// <summary>Objects in the client's table at the scan.</summary>
    public int Known { get; }
}

/// <summary>The Sense scan (see the file header). Pump thread.</summary>
internal sealed class SenseSource : UiSource<SenseView>
{
    public SenseSource() : base("Sense", periodMs: 500) { }

    private const uint TypeCreature = 0x10, TypePortal = 0x10000;
    private const uint BfPlayer = 0x8, BfCorpse = 0x2000, BfPortal = 0x40000;
    private const float LandblockSize = 192f;
    private const int MaxRows = 300;
    private const int MaxAlertLines = 3;
    private const long AlertCooldownMs = 120_000;   // the same object again within this: no second alert
    private const long GapMs = 3000;                // longer between polls: the scan was off, nothing is "new"
    private const int ChatType = 1;                 // the colour of RynthCore's own replies

    private static readonly string[] Compass16 = { "N", "NE", "NE", "E", "E", "SE", "SE", "S", "S", "SW", "SW", "W", "W", "NW", "NW", "N" };

    private long _lastPollMs;
    private long _seenVersion = -1;
    private Dictionary<uint, long> _firstSeen = new();
    private Dictionary<uint, long> _firstSeenNext = new();
    private readonly Dictionary<uint, long> _lastAlertMs = new();
    private long _nextAlertPruneMs;
    private long _lastBeepMs;
    private readonly List<(uint Id, string Name, SensePattern Term)> _hits = new();
    private readonly HashSet<uint> _overworldBlocks = new();
    private readonly List<SenseRow> _rows = new();
    private readonly List<SenseRow> _alerts = new();

    [DllImport("user32.dll")] private static extern bool MessageBeep(uint uType);

    protected internal override void Poll()
    {
        RynthNavAtlas.EnsureLoadedForNames();   // dungeon names for LocationText
        long now = Environment.TickCount64;
        bool gap = _lastPollMs == 0 || now - _lastPollMs > GapMs;
        _lastPollMs = now;

        if (!SenseStore.Loaded) { PublishStatus("Loading the watch list...", 0); return; }

        SensePattern[] terms = SenseStore.Terms;
        long version = SenseStore.Version;
        bool termsChanged = version != _seenVersion;
        _seenVersion = version;

        int enabled = 0;
        foreach (SensePattern t in terms) if (t.Enabled && t.Text.Length > 0) enabled++;
        if (enabled == 0)
        {
            _firstSeen.Clear();
            PublishStatus(terms.Length == 0 ? "Add a name to watch for." : "Every term is switched off.", 0);
            return;
        }

        uint playerId = ClientHelperHooks.GetPlayerId();
        if (playerId == 0 || !LoginLifecycleHooks.HasObservedLoginComplete || LogoutLifecycleHooks.HasObservedLogout)
        {
            _firstSeen.Clear();
            PublishStatus("Not in the world.", 0);
            return;
        }
        if (TeleportStateHooks.IsPortaling)
        {
            PublishStatus("In portal space...", 0);
            return;
        }
        // Position only: the face turns the arrows with the heading of the frame it draws.
        if (!PlayerPhysicsHooks.TryGetPlayerPoseSnapshot(out uint pCell, out float px, out float py, out float pz,
                out _, out _, out _, out _) || (pCell >> 16) == 0)
        {
            PublishStatus("Waiting for your position...", 0);
            return;
        }

        // Pass 1: names only (a dictionary lookup per id).
        uint[] ids = ClientObjectHooks.LiveObjectIds;
        _hits.Clear();
        for (int i = 0; i < ids.Length; i++)
        {
            uint id = ids[i];
            if (id == 0 || id == playerId) continue;
            if (!ClientObjectHooks.TryGetSnapshotName(id, out string name) || name.Length == 0) continue;
            foreach (SensePattern t in terms)
            {
                if (!t.Enabled || !t.Matches(name)) continue;
                _hits.Add((id, name, t));
                break;
            }
        }

        // Which landblocks are overworld (only needed when someone stands indoors).
        _overworldBlocks.Clear();
        bool needBlocks = !IsOutdoorCell(pCell);
        if (!needBlocks)
        {
            foreach (var h in _hits)
                if (ClientObjectHooks.TryGetSnapshotPosition(h.Id, out uint c, out _, out _, out _) && !IsOutdoorCell(c)) { needBlocks = true; break; }
        }
        if (needBlocks)
        {
            if (IsOutdoorCell(pCell)) _overworldBlocks.Add(pCell >> 16);
            for (int i = 0; i < ids.Length; i++)
                if (ClientObjectHooks.TryGetSnapshotPosition(ids[i], out uint c, out _, out _, out _) && (c >> 16) != 0 && IsOutdoorCell(c))
                    _overworldBlocks.Add(c >> 16);
        }
        bool playerOverworld = IsOverworld(pCell);

        // Pass 2: rows for the hits that stand somewhere (not in a pack, not wielded).
        _rows.Clear();
        _alerts.Clear();
        _firstSeenNext.Clear();
        bool flashNew = !gap;
        bool alertNew = !gap && !termsChanged && (SenseStore.AlertChat || SenseStore.AlertBeep);
        foreach (var h in _hits)
        {
            if (_rows.Count >= MaxRows) break;
            if (!ClientObjectHooks.TryGetSnapshotPosition(h.Id, out uint cell, out float x, out float y, out float z) || (cell >> 16) == 0)
                continue;
            bool hasPwd = ClientObjectHooks.TryGetSnapshotPwdInfo(h.Id, out uint bf, out uint container, out uint wielder);
            if (hasPwd && (container != 0 || wielder != 0)) continue;

            var row = new SenseRow { Id = h.Id, Name = h.Name, Kind = Classify(h.Id, hasPwd ? bf : 0) };
            bool objOverworld = IsOverworld(cell);
            row.Measured = (cell >> 16) == (pCell >> 16) || (playerOverworld && objOverworld);
            uint lb = cell >> 16;
            string coords = objOverworld ? Coords(cell, x, y) : "";
            if (row.Measured)
            {
                Offset(pCell, cell, out float ox, out float oy);
                float dx = x + ox - px, dy = y + oy - py, dz = z - pz;
                row.Distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                float flat = MathF.Sqrt(dx * dx + dy * dy);
                if (flat >= 0.5f)
                {
                    float b = MathF.Atan2(dx, dy) * (180f / MathF.PI);
                    row.Bearing = b < 0 ? b + 360f : b;
                    row.CompassText = Compass16[(int)(row.Bearing / 22.5f) & 15];
                }
                else
                {
                    row.CompassText = "here";
                }
                row.DistanceText = row.Distance.ToString("0", CultureInfo.InvariantCulture) + " yd";
                row.LocationText = objOverworld ? coords : RynthNavAtlas.DungeonLabel(lb);
            }
            else
            {
                row.Distance = float.PositiveInfinity;
                row.DistanceText = "elsewhere";
                row.CompassText = "";
                row.LocationText = objOverworld
                    ? coords + " (LB " + lb.ToString("X4", CultureInfo.InvariantCulture) + ")"
                    : RynthNavAtlas.DungeonName(lb) is { Length: > 0 } dn ? dn : "dungeon LB " + lb.ToString("X4", CultureInfo.InvariantCulture);
            }

            bool isNew;
            if (_firstSeen.TryGetValue(h.Id, out long first)) { isNew = false; }
            else { isNew = true; first = flashNew ? now : 0; }
            row.FirstSeenMs = first;
            _firstSeenNext[h.Id] = first;
            row.Tooltip = BuildTooltip(row, h.Term, cell, coords);
            _rows.Add(row);

            if (isNew && alertNew && (!_lastAlertMs.TryGetValue(h.Id, out long last) || now - last > AlertCooldownMs))
            {
                _lastAlertMs[h.Id] = now;
                _alerts.Add(row);
            }
        }
        (_firstSeen, _firstSeenNext) = (_firstSeenNext, _firstSeen);

        _rows.Sort(static (a, b) =>
        {
            int c = a.Distance.CompareTo(b.Distance);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        RaiseAlerts(now);

        string status = _rows.Count switch
        {
            0 => $"No matches among {ids.Length:N0} objects the client knows.",
            1 => $"1 match among {ids.Length:N0} objects the client knows.",
            _ => $"{_rows.Count:N0} matches among {ids.Length:N0} objects the client knows.",
        };
        Publish(new SenseView(_rows.ToArray(), status, ids.Length));
    }

    private void PublishStatus(string status, int known)
    {
        SenseView? cur = Current?.Value;
        if (cur != null && cur.Rows.Length == 0 && cur.Status == status) return;
        Publish(new SenseView(Array.Empty<SenseRow>(), status, known));
    }

    private void RaiseAlerts(long now)
    {
        if (now >= _nextAlertPruneMs)
        {
            _nextAlertPruneMs = now + 60_000;
            List<uint>? stale = null;
            foreach (var kv in _lastAlertMs)
                if (now - kv.Value > AlertCooldownMs) (stale ??= new List<uint>()).Add(kv.Key);
            if (stale != null) foreach (uint id in stale) _lastAlertMs.Remove(id);
        }
        if (_alerts.Count == 0) return;

        if (SenseStore.AlertChat)
        {
            _alerts.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
            int shown = Math.Min(MaxAlertLines, _alerts.Count);
            for (int i = 0; i < shown; i++)
            {
                SenseRow r = _alerts[i];
                string where = r.Measured
                    ? r.DistanceText + (r.CompassText.Length > 0 && r.CompassText != "here" ? " " + r.CompassText : "")
                    : "elsewhere (" + r.LocationText + ")";
                AcMainThreadQueue.EnqueueWriteToChat("Sense: " + r.Name + " " + where, ChatType);
            }
            if (_alerts.Count > shown)
                AcMainThreadQueue.EnqueueWriteToChat($"Sense: and {_alerts.Count - shown} more (open the Sense panel).", ChatType);
        }
        if (SenseStore.AlertBeep && now - _lastBeepMs > 3000)
        {
            _lastBeepMs = now;
            try { MessageBeep(0x40); } catch { }   // MB_ICONASTERISK; queued, returns at once
        }
    }

    private static SenseKind Classify(uint id, uint bitfield)
    {
        if ((bitfield & BfPlayer) != 0) return SenseKind.Player;
        if ((bitfield & BfCorpse) != 0) return SenseKind.Corpse;
        ClientObjectHooks.TryGetSnapshotItemType(id, out uint type);
        if ((type & TypePortal) != 0 || (bitfield & BfPortal) != 0) return SenseKind.Portal;
        if ((type & TypeCreature) != 0)
            return ClientObjectHooks.TryGetSnapshotAttackable(id, out bool attackable) && attackable ? SenseKind.Monster : SenseKind.Npc;
        return SenseKind.Item;
    }

    public static string KindName(SenseKind kind) => kind switch
    {
        SenseKind.Monster => "Monster",
        SenseKind.Npc => "NPC",
        SenseKind.Player => "Player",
        SenseKind.Portal => "Portal",
        SenseKind.Corpse => "Corpse",
        _ => "Item",
    };

    private static bool IsOutdoorCell(uint cell) => (cell & 0xFFFF) < 0x100;

    private bool IsOverworld(uint cell) => IsOutdoorCell(cell) || _overworldBlocks.Contains(cell >> 16);

    /// <summary>Metres from the player's landblock origin to <paramref name="cell"/>'s (192 m per landblock step).</summary>
    private static void Offset(uint fromCell, uint cell, out float ox, out float oy)
    {
        int dxb = (int)((cell >> 24) & 0xFF) - (int)((fromCell >> 24) & 0xFF);
        int dyb = (int)((cell >> 16) & 0xFF) - (int)((fromCell >> 16) & 0xFF);
        ox = dxb * LandblockSize;
        oy = dyb * LandblockSize;
    }

    /// <summary>"42.1N 33.6E" (PlayerPhysicsHooks.TryGetLiveCoords' basis).</summary>
    internal static string Coords(uint cell, float x, float y)
    {
        int lbX = (int)((cell >> 24) & 0xFF), lbY = (int)((cell >> 16) & 0xFF);
        double ew = (lbX * 8.0 + x / 24.0 - 1019.5) / 10.0;
        double ns = (lbY * 8.0 + y / 24.0 - 1019.5) / 10.0;
        return Math.Abs(ns).ToString("0.0", CultureInfo.InvariantCulture) + (ns >= 0 ? "N " : "S ")
             + Math.Abs(ew).ToString("0.0", CultureInfo.InvariantCulture) + (ew >= 0 ? "E" : "W");
    }

    private static string BuildTooltip(SenseRow r, SensePattern term, uint cell, string coords)
    {
        var sb = new StringBuilder(160);
        sb.Append(r.Name).Append('\n');
        sb.Append(KindName(r.Kind)).Append("  0x").Append(r.Id.ToString("X8", CultureInfo.InvariantCulture)).Append('\n');
        if (r.Measured)
        {
            sb.Append(r.DistanceText);
            if (!float.IsNaN(r.Bearing))
                sb.Append(' ').Append(r.CompassText).Append(" (bearing ").Append(r.Bearing.ToString("0", CultureInfo.InvariantCulture)).Append("°)");
            else
                sb.Append(", right here");
            sb.Append('\n');
        }
        else
        {
            sb.Append("Elsewhere: not in the same space as you (another dungeon or landblock), so no distance.\n");
        }
        if (coords.Length > 0) sb.Append(coords).Append("  ");
        sb.Append("landblock ").Append((cell >> 16).ToString("X4", CultureInfo.InvariantCulture))
          .Append(", cell 0x").Append(cell.ToString("X8", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("Matches \"").Append(term.Text).Append("\"\n");
        sb.Append("Click to select it.");
        return sb.ToString();
    }
}

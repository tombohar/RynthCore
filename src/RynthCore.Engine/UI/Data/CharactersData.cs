// ============================================================================
//  RynthCore.Engine - UI/Data/CharactersData.cs
//  Your other characters (the other RynthCore clients on this PC, through the
//  RynthNet plugin) for the World Map's and Mini Map's other-characters layer.
//  The Characters window itself is RynthNet's own since 2026-10-21 (it draws it,
//  with its own copy of this logic); this goes when the world map moves to its
//  plugin (docs/PLUGIN_WINDOWS.md).
//
//  CharactersSource polls RynthNet's RynthPluginGetSnapshotJson twice a second
//  while a map shows the layer (RynthNet rebuilds it twice a second; per-thread
//  buffer, so the hub thread may call it). It adds this client's own pose and
//  vitals (snapshots, no AC calls) and, for a character this client can see,
//  its object position (fresher than the status, which comes every second).
//  The logic (parse, distance, state, order) is CharactersModel.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Data;

/// <summary>A vital bar: fill and "cur/max".</summary>
internal readonly record struct CharVital(float Fraction, string Text);

/// <summary>One row of the Characters face, preformatted on the pump thread.</summary>
internal sealed class CharacterRow
{
    public string Name = "";
    /// <summary>The server's name when it differs from this client's ("" otherwise).</summary>
    public string OtherWorld = "";
    public bool IsSelf;
    public bool LoggedOut;
    public uint Id;
    /// <summary>The character is in this client's object table (a click selects it).</summary>
    public bool InView;

    public bool HasVitals;
    public CharVital Hp, St, Mp;

    public bool Measured;
    public float Bearing = float.NaN;
    public string DistanceText = "";
    public string Compass = "";
    public string Location = "";
    /// <summary>Where it is on the surface map, in degrees (the World Map's other-characters layer): set when it is outdoors or in an overworld building.</summary>
    public bool HasMap;
    public double MapNs, MapEw;

    public string StateText = "";
    public CharStateKind StateKind;
    public bool Stale;
    public string StaleText = "";
    public string Tooltip = "";
}

internal sealed class CharactersView
{
    public static readonly CharactersView Empty = new(Array.Empty<CharacterRow>(), "", false, "");

    public CharactersView(CharacterRow[] rows, string status, bool netLoaded, string myName)
    {
        Rows = rows;
        Status = status;
        NetLoaded = netLoaded;
        MyName = myName;
    }

    /// <summary>This client first (when logged in), then the others in a stable order.</summary>
    public CharacterRow[] Rows { get; }
    /// <summary>The line above the rows: how many, or why there are none.</summary>
    public string Status { get; }
    public bool NetLoaded { get; }
    public string MyName { get; }
}

/// <summary>RynthNet's snapshot plus this client's pose, twice a second while the Characters face is open.</summary>
internal sealed unsafe class CharactersSource : UiSource<CharactersView>
{
    public const string PluginName = "RynthNet";

    private delegate* unmanaged[Cdecl]<IntPtr> _get;
    private long _retryAtMs;
    private string? _lastJson;
    private NetCharSnapshot? _lastSnap;
    private readonly HashSet<uint> _overworldBlocks = new();
    private readonly List<NetCharEntry> _sorted = new();
    private readonly Func<uint, bool> _isOverworldBlock;
    private readonly Func<uint, string> _dungeonName = RynthNavAtlas.DungeonName;

    public CharactersSource() : base("Characters", periodMs: 500)
    {
        _isOverworldBlock = lb => _overworldBlocks.Contains(lb) || RynthNavAtlas.IsMapLandblock(lb);
    }

    protected internal override void Poll()
    {
        RynthNavAtlas.EnsureLoadedForNames();   // dungeon names and surface landblocks
        long now = Environment.TickCount64;
        if (_get == null)
        {
            if (now < _retryAtMs) return;
            _get = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve(PluginName, "RynthPluginGetSnapshotJson");
            if (_get == null)
            {
                _retryAtMs = now + 5000;
                PublishIfChanged(new CharactersView(Array.Empty<CharacterRow>(),
                    "RynthNet isn't loaded. Add it in the launcher's Plugins tab on each client.", false, ""));
                return;
            }
        }

        IntPtr ptr = _get();
        string? json = ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        if (json != _lastJson)
        {
            _lastJson = json;
            _lastSnap = CharactersModel.Parse(json);
        }
        NetCharSnapshot snap = _lastSnap ?? NetCharSnapshot.Empty;
        Publish(Build(snap));
    }

    private void PublishIfChanged(CharactersView view)
    {
        CharactersView? cur = Current?.Value;
        if (cur != null && cur.Rows.Length == 0 && cur.Status == view.Status && cur.NetLoaded == view.NetLoaded) return;
        Publish(view);
    }

    private CharactersView Build(NetCharSnapshot snap)
    {
        bool inWorld = ClientHelperHooks.GetPlayerId() != 0 && LoginLifecycleHooks.HasObservedLoginComplete
            && !LogoutLifecycleHooks.HasObservedLogout;
        uint myCell = 0;
        float mx = 0, my = 0, mz = 0;
        bool havePose = inWorld && PlayerPhysicsHooks.TryGetPlayerPoseSnapshot(out myCell, out mx, out my, out mz,
            out _, out _, out _, out _) && (myCell >> 16) != 0;
        if (!havePose) { myCell = 0; mx = my = mz = 0; }
        bool portaling = TeleportStateHooks.IsPortaling;

        _sorted.Clear();
        foreach (NetCharEntry e in snap.Clients) _sorted.Add(e);
        _sorted.Sort(CharactersModel.CompareRows);

        // Which indoor landblocks are surface buildings: any this client sees an outdoor object in
        // (only worth the scan when someone stands indoors).
        _overworldBlocks.Clear();
        bool anyIndoor = havePose && !CharactersModel.IsOutdoorCell(myCell);
        foreach (NetCharEntry e in _sorted)
            if (e.HasPose && !CharactersModel.IsOutdoorCell(e.Cell)) { anyIndoor = true; break; }
        if (anyIndoor && havePose)
        {
            uint[] ids = ClientObjectHooks.LiveObjectIds;
            for (int i = 0; i < ids.Length; i++)
                if (ClientObjectHooks.TryGetSnapshotPosition(ids[i], out uint c, out _, out _, out _)
                    && (c >> 16) != 0 && CharactersModel.IsOutdoorCell(c))
                    _overworldBlocks.Add(c >> 16);
        }

        var rows = new List<CharacterRow>(_sorted.Count + 1);
        string myName = snap.MyName;
        if (inWorld)
        {
            if (myName.Length == 0 && ClientObjectHooks.TryGetSnapshotName(ClientHelperHooks.GetPlayerId(), out string n)) myName = n;
            rows.Add(SelfRow(myName, snap.MyWorld, havePose, myCell, mx, my, portaling));
        }

        int others = 0, stale = 0;
        foreach (NetCharEntry e in _sorted)
        {
            CharacterRow row = OtherRow(e, snap.MyWorld, havePose && !portaling, myCell, mx, my, mz);
            rows.Add(row);
            if (!row.LoggedOut) others++;
            if (row.Stale) stale++;
        }

        string status;
        if (!snap.Online && snap.Version.Length > 0)
            status = "RynthNet is offline" + (snap.Offline.Length > 0 ? ": " + snap.Offline : ".");
        else if (_sorted.Count == 0)
            status = "No other RynthCore clients with RynthNet on this PC.";
        else
            status = others == 1 ? "1 other character" : others + " other characters";
        if (stale > 0) status += $" ({stale} not updating)";
        return new CharactersView(rows.ToArray(), status, true, myName);
    }

    private CharacterRow SelfRow(string name, string world, bool havePose, uint cell, float x, float y, bool portaling)
    {
        var row = new CharacterRow
        {
            Name = name.Length > 0 ? name : "You",
            IsSelf = true,
            Id = ClientHelperHooks.GetPlayerId(),
            StateText = "this client",
            Compass = "",
        };
        if (PlayerVitalsHooks.TryGetSnapshot(out PlayerVitalsSnapshot v) && v.MaxHealth > 0)
        {
            row.HasVitals = true;
            row.Hp = Vital(v.Health, v.MaxHealth);
            row.St = Vital(v.Stamina, v.MaxStamina);
            row.Mp = Vital(v.Mana, v.MaxMana);
        }
        if (portaling) row.Location = "portal space";
        else if (havePose)
        {
            uint lb = cell >> 16;
            bool over = CharactersModel.IsOutdoorCell(cell) || _isOverworldBlock(lb);
            row.Location = over ? CharactersModel.Coords(cell, x, y) : RynthNavAtlas.DungeonLabel(lb);
        }
        row.Tooltip = row.Name + " (this client)" + (world.Length > 0 ? "\n" + world : "");
        return row;
    }

    private CharacterRow OtherRow(NetCharEntry e, string myWorld, bool canMeasure, uint myCell, float mx, float my, float mz)
    {
        (string stateText, CharStateKind kind) = CharactersModel.State(e);
        var row = new CharacterRow
        {
            Name = e.LoggedIn && e.Name.Length > 0 ? e.Name : "Not logged in (pid " + e.Pid.ToString(CultureInfo.InvariantCulture) + ")",
            LoggedOut = kind == CharStateKind.LoggedOut,
            Id = e.Id,
            StateText = stateText,
            StateKind = kind,
            Stale = CharactersModel.IsStale(e),
        };
        if (row.Stale) row.StaleText = "no update " + (e.AgeMs / 1000).ToString(CultureInfo.InvariantCulture) + " s";
        bool sameWorld = e.World.Length == 0 || myWorld.Length == 0 || string.Equals(e.World, myWorld, StringComparison.OrdinalIgnoreCase);
        if (!sameWorld) row.OtherWorld = e.World;

        if (e.HasVitals && e.HpMax > 0)
        {
            row.HasVitals = true;
            row.Hp = Vital(e.Hp, e.HpMax);
            row.St = Vital(e.St, e.StMax);
            row.Mp = Vital(e.Mp, e.MpMax);
        }

        // Where: this client's own view of the character when it has one (fresher), else the status.
        uint cell = e.Cell;
        float x = e.X, y = e.Y, z = e.Z;
        bool hasPose = e.HasPose;
        if (sameWorld && e.Id != 0 && ClientObjectHooks.TryGetSnapshotName(e.Id, out _))
        {
            row.InView = true;
            if (ClientObjectHooks.TryGetSnapshotPosition(e.Id, out uint c2, out float x2, out float y2, out float z2) && (c2 >> 16) != 0)
            {
                cell = c2; x = x2; y = y2; z = z2;
                hasPose = true;
            }
        }

        if (row.LoggedOut) row.Location = "";
        else if (!sameWorld) row.Location = "on " + e.World;
        else if (e.Portal) row.Location = "portal space";
        else if (hasPose)
        {
            if (_isOverworldBlock(cell >> 16))
            {
                // A pose is relative to its landblock, indoors too: the surface coordinates of the spot.
                row.HasMap = true;
                row.MapEw = (((cell >> 24) & 0xFF) * 8.0 + x / 24.0 - 1019.5) / 10.0;
                row.MapNs = (((cell >> 16) & 0xFF) * 8.0 + y / 24.0 - 1019.5) / 10.0;
            }
            CharPlace p = canMeasure
                ? CharactersModel.Locate(myCell, mx, my, mz, cell, x, y, z, _isOverworldBlock, _dungeonName)
                : CharactersModel.Locate(0, 0, 0, 0, cell, x, y, z, _isOverworldBlock, _dungeonName);
            row.Measured = p.Measured;
            row.Bearing = p.Bearing;
            row.DistanceText = p.Measured ? p.DistanceText : "elsewhere";
            row.Compass = p.Compass;
            row.Location = p.Location;
        }
        else if (e.HasCoords)
        {
            row.Location = CharactersModel.CoordsText(e.Ns, e.Ew);
            row.HasMap = true; row.MapNs = e.Ns; row.MapEw = e.Ew;
        }

        row.Tooltip = Tooltip(e, row, cell, hasPose);
        return row;
    }

    private static CharVital Vital(uint cur, uint max) => new(CharactersModel.Fraction(cur, max), CharactersModel.VitalText(cur, max));

    private static string Tooltip(NetCharEntry e, CharacterRow row, uint cell, bool hasPose)
    {
        var sb = new StringBuilder(200);
        sb.Append(row.Name).Append('\n');
        if (e.World.Length > 0) sb.Append(e.World).Append('\n');
        if (e.Tags.Length > 0) sb.Append("tags: ").Append(string.Join(", ", e.Tags)).Append('\n');
        sb.Append(row.StateText);
        if (e.HasBot && e.Action.Length > 0 && !e.Macro) sb.Append(" (last action: ").Append(e.Action).Append(')');
        sb.Append('\n');
        if (row.Measured) sb.Append(row.DistanceText).Append(' ').Append(row.Compass).Append(", ");
        else if (hasPose && row.OtherWorld.Length == 0) sb.Append("Elsewhere: not in the same space as you (another dungeon or landblock), so no distance.\n");
        if (row.Location.Length > 0) sb.Append(row.Location);
        if (hasPose) sb.Append(" (LB ").Append((cell >> 16).ToString("X4", CultureInfo.InvariantCulture)).Append(')');
        sb.Append('\n');
        if (e.AgeMs >= 0) sb.Append("status ").Append((e.AgeMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(" s old\n");
        if (row.Id == 0) sb.Append("Its RynthNet is older than 0.1.1: no object id, so a click can't select it.");
        else if (row.InView) sb.Append("Click to select. Right-click for commands.");
        else sb.Append("Not in view: a click can't select it. Right-click for commands.");
        return sb.ToString();
    }

    protected internal override void Reset()
    {
        _get = null;
        _retryAtMs = 0;
        _lastJson = null;
        _lastSnap = null;
        ClearSnapshot();
    }
}

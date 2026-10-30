using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// What the player wields, and where, from the server's own messages (the way Decal's object
/// database knows it, and why VTank never guesses):
///   * GameEvent 0x0023 WieldObject [item][EquipMask]: the item is wielded in that slot;
///   * GameEvent 0x0022 InventoryPutObjInContainer [item][container]..: it is in a container;
///   * GameEvent 0x019A InventoryPutObjectIn3D [item]: it was dropped.
/// (ACE: GameEventWieldItem, GameEventItemServerSaysContainId, GameEventItemServerSaysMoveItem.)
///
/// The client's own copy of these fields goes stale on this client: CurrentWieldedLocation reads 0
/// and the PWD wielder keeps naming the player after the item went into a pack (Drakkon fought
/// bare-handed and died over it, 2026-10-17/18). The tracker starts from the client's PWD snapshot
/// where its fields agree (wielder = the player and no container), then follows the messages.
/// Messages that arrive before that start are kept and applied on top of it.
///
/// The record survives an engine reload (/rc rl, a publish): every change is written to
/// %LOCALAPPDATA%\RynthCore\equip\p{pid}.txt and a new engine in the same client starts from it.
/// The client's fields are only right at login: restarted from them mid-session, the tracker had
/// the Sceptre "in hand" that the server had put into the pack minutes before (2026-10-18 12:29).
/// At logout the file is deleted, so the next login starts from the login's own records.
///
/// Thread safety: the game-event hook runs on AC's main thread, plugins ask from the pump thread;
/// everything goes through <see cref="Gate"/>.
/// </summary>
internal static class EquipmentTracker
{
    private static readonly object Gate = new();
    private static readonly Dictionary<uint, uint> _wielded = new();        // item -> EquipMask
    private static readonly Dictionary<uint, uint> _pending = new();        // before the start: item -> EquipMask (0 = not wielded)
    private static uint _playerId;
    private static bool _seeded;
    private static int _changes;
    private static string _source = "";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "equip",
        $"p{Environment.ProcessId}.txt");

    /// <summary>Where the current record started from ("saved", "login records", "client records mid-session").</summary>
    public static string Source { get { lock (Gate) return _source; } }

    // Writes the record (under Gate). A failed write only loses the carry-over across a reload.
    private static void SaveLocked()
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = new List<string>(_wielded.Count + 1) { $"player {_playerId:X8}" };
            foreach (var kv in _wielded) lines.Add($"{kv.Key:X8} {kv.Value:X8}");
            File.WriteAllLines(path, lines);
        }
        catch { }
    }

    // The saved record for this client and character, or null.
    private static Dictionary<uint, uint>? LoadSaved(uint player)
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path)) return null;
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0] != $"player {player:X8}") return null;
            var map = new Dictionary<uint, uint>();
            for (int i = 1; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split(' ');
                if (parts.Length == 2
                    && uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out uint id)
                    && uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out uint loc))
                    map[id] = loc;
            }
            return map;
        }
        catch { return null; }
    }

    private static void DeleteSaved()
    {
        try { File.Delete(FilePath); } catch { }
    }

    /// <summary>A WieldObject for <paramref name="item"/> at <paramref name="location"/> (main thread).</summary>
    public static void OnWield(uint item, uint location)
    {
        if (item == 0) return;
        lock (Gate)
        {
            if (!_seeded) { _pending[item] = location; return; }
            if (location == 0) _wielded.Remove(item); else _wielded[item] = location;
            _changes++;
            SaveLocked();
        }
        RynthLog.Compat($"Equipment: 0x{item:X8} wielded at 0x{location:X8} (server)");
    }

    /// <summary>An InventoryPutObjInContainer for <paramref name="item"/> (main thread): not wielded.</summary>
    public static void OnContained(uint item, uint container)
    {
        if (item == 0) return;
        bool was;
        lock (Gate)
        {
            if (!_seeded) { _pending[item] = 0; return; }
            was = _wielded.Remove(item);
            if (was) { _changes++; SaveLocked(); }
        }
        if (was) RynthLog.Compat($"Equipment: 0x{item:X8} into container 0x{container:X8} (server)");
    }

    /// <summary>An InventoryPutObjectIn3D for <paramref name="item"/> (main thread): dropped, not wielded.</summary>
    public static void OnDropped(uint item)
    {
        if (item == 0) return;
        bool was;
        lock (Gate)
        {
            if (!_seeded) { _pending[item] = 0; return; }
            was = _wielded.Remove(item);
            if (was) { _changes++; SaveLocked(); }
        }
        if (was) RynthLog.Compat($"Equipment: 0x{item:X8} dropped (server)");
    }

    /// <summary>
    /// The player's wielded items into <paramref name="ids"/> / <paramref name="locations"/>. Returns the
    /// count, or -1 while there is no player or no PWD snapshot to start from. Any thread.
    /// </summary>
    public static int Copy(Span<uint> ids, Span<uint> locations)
    {
        if (!EnsureStarted()) return -1;
        lock (Gate)
        {
            int n = 0;
            foreach (var kv in _wielded)
            {
                if (n >= ids.Length || n >= locations.Length) break;
                ids[n] = kv.Key;
                locations[n] = kv.Value;
                n++;
            }
            return n;
        }
    }

    /// <summary>Starts (or restarts, for another character) from the PWD snapshot. False until it can.</summary>
    private static bool EnsureStarted()
    {
        if (LogoutLifecycleHooks.HasObservedLogout)
        {
            bool had;
            lock (Gate) had = _seeded;
            if (had) { Reset(); DeleteSaved(); RynthLog.Compat("Equipment: logged out - the record is dropped"); }
            return false;
        }
        uint player = ClientHelperHooks.GetPlayerId();
        if (player == 0) return false;
        lock (Gate)
        {
            if (_seeded && player == _playerId) return true;
        }
        Dictionary<uint, uint>? saved = LoadSaved(player);
        if (saved == null && !ClientObjectHooks.HasPwdSnapshot) return false;

        var seed = new List<(uint Id, uint Loc)>();
        string from;
        if (saved != null)
        {
            foreach (var kv in saved) seed.Add((kv.Key, kv.Value));
            from = "the record saved before the engine reload";
        }
        else
        {
            ClientObjectHooks.CollectWieldedFromSnapshot(player, seed);
            from = LoginLifecycleHooks.HasObservedLoginComplete ? "the client's records (at login)" : "the client's records (mid-session: may be stale until items move)";
        }
        string list;
        lock (Gate)
        {
            if (_seeded && player == _playerId) return true;
            bool other = _seeded && player != _playerId;
            _wielded.Clear();
            foreach (var (id, loc) in seed) _wielded[id] = loc;
            // Messages seen before the start are newer than the snapshot read, except after a
            // character change (they were the last character's).
            if (!other)
                foreach (var kv in _pending)
                {
                    if (kv.Value == 0) _wielded.Remove(kv.Key);
                    else _wielded[kv.Key] = kv.Value;
                }
            _pending.Clear();
            _playerId = player;
            _seeded = true;
            _source = from;
            SaveLocked();
            list = _wielded.Count == 0 ? "nothing" : string.Join(", ", FormatAll());
        }
        RynthLog.Compat($"Equipment: started for 0x{player:X8} from {from}: {list}");
        return true;
    }

    private static IEnumerable<string> FormatAll()
    {
        foreach (var kv in _wielded) yield return $"0x{kv.Key:X8}@0x{kv.Value:X}";
    }

    /// <summary>Forget everything (logout, teardown): the next query starts again.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _wielded.Clear();
            _pending.Clear();
            _seeded = false;
            _playerId = 0;
        }
    }

    /// <summary>Changes followed since the start (diagnostics).</summary>
    public static int Changes { get { lock (Gate) return _changes; } }
}

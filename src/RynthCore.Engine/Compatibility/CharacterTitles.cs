// ============================================================================
//  RynthCore.Engine - Compatibility/CharacterTitles.cs
//
//  The titles the character holds, for plugins (API v75 GetCharacterTitles).
//  The server sends them only in game events, never as properties:
//    0x0029 CharacterTitle at login (ACE GameEventCharacterTitle):
//           [u32 1][u32 current title][u32 count][count x u32 title id]
//    0x002B UpdateTitle for each new or newly displayed title (GameEventUpdateTitle):
//           [u32 title id][u32 set as display title]
//  Both reach SmartBoxHooks.ParseGameEvent ([event type][payload]) on AC's main
//  thread, or the Decal bridge's copy on the pump. Parsing only: nothing here
//  touches AC.
//
//  Owner: a 0x0029 is sent once per login, so it belongs to whoever is logging
//  in; the player id is noted when known and adopted at the first read when it
//  wasn't yet. A list for another character is never served.
//
//  Survives a hot reload: the list is mirrored into this process's environment
//  (RYNTHCORE_CHAR_TITLES), as VTankWatch does, because the server won't send
//  it again until the next login.
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal static class CharacterTitles
{
    public const uint EventCharacterTitle = 0x0029;
    public const uint EventUpdateTitle = 0x002B;
    private const int MaxTitles = 4096;
    private const string EnvName = "RYNTHCORE_CHAR_TITLES";

    private static readonly object Lock = new();
    private static uint _owner;
    private static uint _current;
    private static List<uint>? _titles;       // null = nothing received for this process yet
    private static int _seq;
    private static bool _restored;

    public static bool IsTitleEvent(uint eventType) => eventType is EventCharacterTitle or EventUpdateTitle;

    /// <summary>A title game event: <paramref name="data"/> is [event type][payload], <paramref name="size"/> bytes.</summary>
    public static unsafe void OnGameEvent(uint eventType, IntPtr data, uint size, uint playerId)
    {
        if (data == IntPtr.Zero || size < 4 || size > 0x10000) return;
        var span = new ReadOnlySpan<byte>((void*)data, (int)size);
        lock (Lock)
        {
            RestoreOnce();
            if (eventType == EventCharacterTitle)
            {
                if (!TryParseTitleList(span, out uint current, out List<uint> titles)) return;
                _owner = playerId;
                _current = current;
                _titles = titles;
            }
            else
            {
                if (!TryParseUpdate(span, out uint title, out bool display)) return;
                if (_titles == null || (_owner != 0 && playerId != 0 && _owner != playerId)) return;
                if (!_titles.Contains(title) && _titles.Count < MaxTitles) _titles.Add(title);
                if (display) _current = title;
            }
            _seq++;
            Persist();
        }
    }

    /// <summary>
    /// For the plugin API: copies up to <paramref name="max"/> title ids and returns how many
    /// the character holds (which may be more than were copied), or -1 when the list for this
    /// character isn't known (not received since the client started, or another character's).
    /// </summary>
    public static unsafe int Copy(uint playerId, uint* ids, int max, uint* current)
    {
        lock (Lock)
        {
            RestoreOnce();
            if (_titles == null || playerId == 0) return -1;
            if (_owner == 0) _owner = playerId;           // received before the id was known
            else if (_owner != playerId) return -1;
            if (current != null) *current = _current;
            if (ids != null)
                for (int i = 0; i < _titles.Count && i < max; i++) ids[i] = _titles[i];
            return _titles.Count;
        }
    }

    public static int Sequence => Volatile.Read(ref _seq);

    /// <summary>For /rc: what the engine holds.</summary>
    public static string Describe()
    {
        lock (Lock)
        {
            RestoreOnce();
            return _titles == null
                ? "no title list received since this client started"
                : $"{_titles.Count} titles for 0x{_owner:X8}, current {_current}";
        }
    }

    // ── Wire (pure) ─────────────────────────────────────────────────────────

    /// <summary>0x0029: [type][u32 1][u32 current][u32 count][count x u32]. False when it doesn't fit.</summary>
    internal static bool TryParseTitleList(ReadOnlySpan<byte> msg, out uint current, out List<uint> titles)
    {
        current = 0;
        titles = new List<uint>();
        if (msg.Length < 16) return false;
        current = BinaryPrimitives.ReadUInt32LittleEndian(msg[8..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(msg[12..]);
        if (count > MaxTitles || 16 + (long)count * 4 > msg.Length) return false;
        for (int i = 0; i < (int)count; i++)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(msg[(16 + i * 4)..]);
            if (!titles.Contains(id)) titles.Add(id);
        }
        return true;
    }

    /// <summary>0x002B: [type][u32 title][u32 set as display title].</summary>
    internal static bool TryParseUpdate(ReadOnlySpan<byte> msg, out uint title, out bool display)
    {
        title = 0;
        display = false;
        if (msg.Length < 12) return false;
        title = BinaryPrimitives.ReadUInt32LittleEndian(msg[4..]);
        display = BinaryPrimitives.ReadUInt32LittleEndian(msg[8..]) != 0;
        return true;
    }

    // ── Hot-reload mirror: "owner;current;id,id,..." (hex owner, decimal ids) ──

    private static void Persist()
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(_owner.ToString("X8", CultureInfo.InvariantCulture)).Append(';')
              .Append(_current.ToString(CultureInfo.InvariantCulture)).Append(';');
            for (int i = 0; i < _titles!.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_titles[i].ToString(CultureInfo.InvariantCulture));
            }
            Environment.SetEnvironmentVariable(EnvName, sb.ToString());
        }
        catch { }
    }

    private static void RestoreOnce()
    {
        if (_restored) return;
        _restored = true;
        try
        {
            if (TryParseMirror(Environment.GetEnvironmentVariable(EnvName), out uint owner, out uint current, out List<uint> titles))
            {
                _owner = owner;
                _current = current;
                _titles = titles;
                _seq++;
            }
        }
        catch { }
    }

    internal static bool TryParseMirror(string? s, out uint owner, out uint current, out List<uint> titles)
    {
        owner = 0;
        current = 0;
        titles = new List<uint>();
        if (string.IsNullOrEmpty(s)) return false;
        string[] parts = s.Split(';');
        if (parts.Length != 3) return false;
        if (!uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out owner)) return false;
        if (!uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out current)) return false;
        foreach (string p in parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!uint.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out uint id)) return false;
            if (titles.Count < MaxTitles && !titles.Contains(id)) titles.Add(id);
        }
        return true;
    }
}

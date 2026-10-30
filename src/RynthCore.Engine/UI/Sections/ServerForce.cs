// ============================================================================
//  RynthCore.Engine - UI/Sections/ServerForce.cs
//  The per-character server override: Auto (detect), On (treat this world as
//  the server) or Off (never treat it as the server), one per server id.
//
//  Pure (no AC, no engine state, no I/O): the offline tests (tools/SectionsTests)
//  compile this file as is. ServerForceStore.cs holds the loaded file and saves it.
//
//  File (server_force.txt, one line per override, tab-separated):
//      <character key> \t <server id> \t on|off
//  Auto is the absence of a line. Blank lines and lines starting with '#' are
//  skipped; a malformed line is dropped, never thrown.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Engine.UI.Sections;

/// <summary>How a server's detection is overridden for one character.</summary>
internal enum ServerForce
{
    /// <summary>Detect it (launch host, announced world name, the configured lists).</summary>
    Auto,
    /// <summary>Treat this world as the server, whatever detection says.</summary>
    On,
    /// <summary>Never treat this world as the server.</summary>
    Off,
}

internal static class ServerForceText
{
    /// <summary>"on" / "off" / "auto" (and the older /rc words) to a mode; false for anything else.</summary>
    public static bool TryParse(string? text, out ServerForce mode)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "auto": case "detect": mode = ServerForce.Auto; return true;
            case "on": case "force": case "yes": case "true": mode = ServerForce.On; return true;
            case "off": case "never": case "no": case "false": mode = ServerForce.Off; return true;
            default: mode = ServerForce.Auto; return false;
        }
    }

    public static string Name(ServerForce mode) => mode switch
    {
        ServerForce.On => "on",
        ServerForce.Off => "off",
        _ => "auto",
    };

    /// <summary>Applies an override to a detected verdict.</summary>
    public static bool Apply(ServerForce mode, bool detected) => mode switch
    {
        ServerForce.On => true,
        ServerForce.Off => false,
        _ => detected,
    };
}

/// <summary>server_force.txt contents: character key -> server id -> On / Off. Not thread-safe (the store locks).</summary>
internal sealed class ServerForceFile
{
    /// <summary>Longest character key or server id kept (anything longer is a damaged line).</summary>
    public const int MaxFieldLength = 128;

    private readonly Dictionary<string, Dictionary<string, ServerForce>> _byCharacter =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The character's override for <paramref name="serverId"/> (Auto when none).</summary>
    public ServerForce Get(string characterKey, string serverId)
        => _byCharacter.TryGetValue(characterKey, out var map) && map.TryGetValue(serverId, out ServerForce m) ? m : ServerForce.Auto;

    /// <summary>Every override saved for the character (a copy).</summary>
    public Dictionary<string, ServerForce> For(string characterKey)
        => _byCharacter.TryGetValue(characterKey, out var map)
            ? new Dictionary<string, ServerForce>(map, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ServerForce>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sets (or with Auto, removes) one override. True when the contents changed.</summary>
    public bool Set(string characterKey, string serverId, ServerForce mode)
    {
        if (!ValidField(characterKey) || !ValidField(serverId)) return false;
        if (mode == ServerForce.Auto)
        {
            if (!_byCharacter.TryGetValue(characterKey, out var existing) || !existing.Remove(serverId)) return false;
            if (existing.Count == 0) _byCharacter.Remove(characterKey);
            return true;
        }
        if (!_byCharacter.TryGetValue(characterKey, out var map))
            _byCharacter[characterKey] = map = new Dictionary<string, ServerForce>(StringComparer.OrdinalIgnoreCase);
        if (map.TryGetValue(serverId, out ServerForce old) && old == mode) return false;
        map[serverId] = mode;
        return true;
    }

    public static ServerForceFile Parse(IEnumerable<string>? lines)
    {
        var file = new ServerForceFile();
        if (lines == null) return file;
        foreach (string raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string line = raw.TrimEnd('\r', '\n');
            if (line.TrimStart().StartsWith('#')) continue;
            string[] f = line.Split('\t');
            if (f.Length < 3) continue;
            string key = f[0].Trim(), server = f[1].Trim();
            if (!ServerForceText.TryParse(f[2], out ServerForce mode) || mode == ServerForce.Auto) continue;
            file.Set(key, server, mode);
        }
        return file;
    }

    public string Write()
    {
        var sb = new StringBuilder();
        sb.Append("# RynthCore per-character server overrides: character key, server id, on|off (auto = no line).\n");
        foreach (var (key, map) in _byCharacter)
            foreach (var (server, mode) in map)
                sb.Append(key).Append('\t').Append(server).Append('\t').Append(ServerForceText.Name(mode)).Append('\n');
        return sb.ToString();
    }

    /// <summary>No tab or line break (the file's separators), not empty, not overlong.</summary>
    private static bool ValidField(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxFieldLength) return false;
        foreach (char c in text)
            if (c is '\t' or '\n' or '\r') return false;
        return true;
    }
}

// ============================================================================
//  RynthCore.Engine - UI/Sections/ServerStateJson.cs
//  The server list as JSON for plugins (plugin API v86, GetServerStateJson):
//  one answer to "which server is this, and which plugin has its settings",
//  the same verdicts the Mini Remote, the menus and the "servermenu" push use.
//
//  {
//    "schema": 1, "revision": 12,
//    "host": "infiniteleaftide.online", "world": "InfiniteLeaftide",
//    "pick": "auto" | "<id>" | "mixed",
//    "active": "ilt", "activeName": "Infinite Leaftide", "activePlugin": "RynthILT",
//    "servers": [ { "id", "name", "active", "detected", "force": "auto|on|off",
//                   "builtIn", "plugin", "reason" }, ... ]
//  }
//  "world" is what the server announced at login ("" before, or when the hook
//  isn't there); "active" is "" when no server is active (a retail-like world).
//  "plugin" is the server's owner (ServerSelection.OwnerPlugin): the plugin to
//  ask for the server's settings ("RynthServer.Profile", or RynthILT's
//  "RynthILT.Server"); "" when none is loaded.
//
//  Pure (BCL only): tools/SectionsTests compiles it.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RynthCore.Engine.UI.Sections;

internal static class ServerStateJson
{
    public const int Schema = 1;

    /// <summary>The JSON for <paramref name="servers"/> (from <paramref name="manifests"/>), UTF-8 text.</summary>
    public static string Build(IReadOnlyList<ServerState> servers, IReadOnlyList<SectionManifest> manifests,
                               int revision, string? host, string? world, string enginePlugin)
    {
        ServerState? active = ServerSelection.ActiveServer(servers);
        string pick = ServerSelection.CurrentPick(servers) ?? "mixed";

        using var stream = new MemoryStream(512);
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteNumber("revision", revision);
            w.WriteString("host", host ?? string.Empty);
            w.WriteString("world", world ?? string.Empty);
            w.WriteString("pick", pick);
            w.WriteString("active", active?.Id ?? string.Empty);
            w.WriteString("activeName", active?.Name ?? string.Empty);
            w.WriteString("activePlugin", active == null ? string.Empty : ServerSelection.OwnerPlugin(manifests, active.Id, enginePlugin));
            w.WriteStartArray("servers");
            foreach (ServerState s in servers)
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("name", s.Name);
                w.WriteBoolean("active", s.Active);
                w.WriteBoolean("detected", s.Detected);
                w.WriteString("force", ServerForceText.Name(s.Force));
                w.WriteBoolean("builtIn", s.BuiltIn);
                w.WriteString("plugin", ServerSelection.OwnerPlugin(manifests, s.Id, enginePlugin));
                w.WriteString("reason", s.Reason ?? string.Empty);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Copies <paramref name="json"/> as NUL-terminated UTF-8 into the caller's buffer when it
    /// fits and returns its byte length either way (length &gt;= capacity: too small, nothing
    /// written). The plugin API's convention for text it hands out.
    /// </summary>
    public static unsafe int Write(string json, byte* buffer, int capacity)
    {
        int length = Encoding.UTF8.GetByteCount(json);
        if (buffer == null || capacity <= length) return length;
        fixed (char* src = json)
            Encoding.UTF8.GetBytes(src, json.Length, buffer, capacity);
        buffer[length] = 0;
        return length;
    }
}

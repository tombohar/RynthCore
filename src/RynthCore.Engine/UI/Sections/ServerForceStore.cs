// ============================================================================
//  RynthCore.Engine - UI/Sections/ServerForceStore.cs
//  server_force.txt: each character's Auto / On / Off override per server
//  (ServerForce.cs has the format). IltInfo and ServerInfo read their override
//  here, so "/rc ilt on", "/rc server aelrynth" and the Mini Remote's Char
//  section all set the same saved value.
//
//  Before a character is logged in there is no key to save under: an override
//  set then holds for this session only (until a character logs in).
//
//  Threads: reads from any thread are a volatile snapshot (a lock only when the
//  character changes); a change locks briefly and queues the save on
//  UiBackgroundWriter, never AC's render thread.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.UI.Sections;

internal static class ServerForceStore
{
    private static readonly object Sync = new();
    private static ServerForceFile _file = new();                     // under Sync
    private static readonly Dictionary<string, ServerForce> Session = // under Sync: no character yet
        new(StringComparer.OrdinalIgnoreCase);
    private static volatile bool _loaded;
    private static bool _loadQueued;

    private sealed record Snap(string Key, Dictionary<string, ServerForce> Map);
    private static Snap? _snap;

    /// <summary>Bumped on every change, so callers can re-read cheaply.</summary>
    private static int _version;
    public static int Version => Volatile.Read(ref _version);

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "server_force.txt");

    /// <summary>A character is logged in: overrides are saved for it.</summary>
    public static bool HasCharacter => SessionStateRegistry.LastCharacterName.Length > 0;

    private static string CharacterKey =>
        D3D9.VideoPatchPolicy.CharacterKey(SessionStateRegistry.LastServerName, SessionStateRegistry.LastCharacterName);

    /// <summary>Queues the one-time read off the calling thread. Any thread; cheap after the first call.</summary>
    public static void EnsureLoadQueued()
    {
        if (_loaded) return;
        lock (Sync)
        {
            if (_loadQueued) return;
            _loadQueued = true;
        }
        UiBackgroundWriter.Enqueue("server overrides (load)", LoadNow);
    }

    private static void LoadNow()
    {
        ServerForceFile file;
        try
        {
            string path = FilePath;
            file = File.Exists(path) ? ServerForceFile.Parse(File.ReadAllLines(path)) : new ServerForceFile();
        }
        catch (Exception ex)
        {
            RynthLog.UI($"ServerForce: reading server_force.txt failed - {ex.GetType().Name}: {ex.Message}");
            file = new ServerForceFile();
        }
        lock (Sync)
        {
            _file = file;
            Volatile.Write(ref _snap, null);
            _loaded = true;
        }
        Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// The override for <paramref name="serverId"/>: the logged-in character's saved one, or
    /// before login the session's. Auto until the file is read. Any thread.
    /// </summary>
    public static ServerForce Get(string serverId)
    {
        EnsureLoadQueued();
        if (!HasCharacter)
        {
            lock (Sync) return Session.TryGetValue(serverId, out ServerForce m) ? m : ServerForce.Auto;
        }
        if (!_loaded) return ServerForce.Auto;
        string key = CharacterKey;
        Snap? s = Volatile.Read(ref _snap);
        if (s == null || !string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase))
        {
            lock (Sync)
            {
                s = new Snap(key, _file.For(key));
                Volatile.Write(ref _snap, s);
            }
        }
        return s.Map.TryGetValue(serverId, out ServerForce mode) ? mode : ServerForce.Auto;
    }

    /// <summary>
    /// Sets the override. Saved for the logged-in character; before login it holds for this
    /// session. False (with the reason) before the file is read. Any thread.
    /// </summary>
    public static bool Set(string serverId, ServerForce mode, out string where)
    {
        EnsureLoadQueued();
        if (!HasCharacter)
        {
            lock (Sync)
            {
                if (mode == ServerForce.Auto) Session.Remove(serverId);
                else Session[serverId] = mode;
            }
            Interlocked.Increment(ref _version);
            where = "this session (log in a character to save it for that character)";
            return true;
        }
        if (!_loaded) { where = "not yet: still reading the saved overrides, try again in a moment"; return false; }

        string key = CharacterKey;
        string? text = null;
        lock (Sync)
        {
            if (_file.Set(key, serverId, mode))
            {
                Volatile.Write(ref _snap, new Snap(key, _file.For(key)));
                text = _file.Write();
            }
        }
        Interlocked.Increment(ref _version);
        if (text != null) UiBackgroundWriter.Enqueue("server overrides", () => SaveNow(text));
        where = "saved for " + SessionStateRegistry.LastCharacterName;
        return true;
    }

    private static void SaveNow(string text)
    {
        try
        {
            string path = FilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            RynthLog.UI($"ServerForce: writing server_force.txt failed - {ex.GetType().Name}: {ex.Message}");
        }
    }
}

// ============================================================================
//  RynthCore.Engine - Compatibility/ServerInfo.cs
//
//  Which server this client is on: ServerInfo.IsAelrynth is the one flag the
//  Aelrynth-only features (the Skills panel's mastery) check before they show
//  anything or send a server command another server wouldn't know.
//
//  Signals (combined by ServerDetect.Decide, AelrynthWire.cs):
//    - the launch command line's -h host[:port] / -p port (read once);
//    - the world name the server announced at login (AccountHooks, the
//      SendNotice_WorldName hook; not the launcher's own profile name);
//    - the Bank mod's custom player properties 9101-9103 (PropertyCaches).
//  Unknown means not Aelrynth. "/rc server aelrynth|other|auto" (or the Mini
//  Remote's Char section) overrides it, saved per character
//  (UI/Sections/ServerForceStore).
//
//  Threads: any. Re-evaluated at most once a second; each read in between is a
//  volatile load and a compare. Never touches AC (dictionary reads only).
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal static class ServerInfo
{
    public enum Mode { Auto, ForceAelrynth, ForceOther }

    private const int RefreshMs = 1000;

    /// <summary>The server id the override is saved under (UI/Sections).</summary>
    public const string ServerId = "aelrynth";

    private static volatile ServerVerdict _verdict = ServerVerdict.Unknown;
    private static long _nextRefreshMs;
    private static int _hostRead;
    private static readonly object HostLock = new();
    private static string _host = "";
    private static int _port;
    private static ServerHostKind _hostKind = ServerHostKind.Unknown;
    private static volatile bool _bankSeen;        // sticky: the properties only ever appear on Aelrynth
    private static volatile bool _lastAelrynth;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetCommandLineW();

    /// <summary>True when the client is on Aelrynth (live, staging or a dev copy) - or the override says so. Any thread.</summary>
    public static bool IsAelrynth => Current.IsAelrynth;

    /// <summary>Aelrynth's staging world (port 9030 / "Staging" world name). Any thread.</summary>
    public static bool IsStaging => Current.IsStaging;

    /// <summary>The current character's saved override (session-only before login).</summary>
    public static Mode Override
    {
        get => UI.Sections.ServerForceStore.Get(ServerId) switch
        {
            UI.Sections.ServerForce.On => Mode.ForceAelrynth,
            UI.Sections.ServerForce.Off => Mode.ForceOther,
            _ => Mode.Auto,
        };
    }

    /// <summary>Sets the override (saved per character); <paramref name="where"/> says where it went.</summary>
    public static bool SetOverride(Mode mode, out string where)
    {
        var force = mode switch
        {
            Mode.ForceAelrynth => UI.Sections.ServerForce.On,
            Mode.ForceOther => UI.Sections.ServerForce.Off,
            _ => UI.Sections.ServerForce.Auto,
        };
        bool ok = UI.Sections.ServerForceStore.Set(ServerId, force, out where);
        Volatile.Write(ref _nextRefreshMs, 0);
        return ok;
    }

    /// <summary>Re-evaluates on the next read (the character or an override changed).</summary>
    public static void Invalidate() => Volatile.Write(ref _nextRefreshMs, 0);

    /// <summary>What detection alone says (no override), for the Char section and /rc server.</summary>
    public static ServerVerdict Detected
    {
        get
        {
            ReadHostOnce();
            return ServerDetect.Decide(_hostKind, _host, _port, AccountHooks.AnnouncedWorldName, _bankSeen);
        }
    }

    /// <summary>The current verdict (re-evaluated at most once a second). Any thread.</summary>
    public static ServerVerdict Current
    {
        get
        {
            long now = Environment.TickCount64;
            if (now >= Volatile.Read(ref _nextRefreshMs))
            {
                Volatile.Write(ref _nextRefreshMs, now + RefreshMs);
                Refresh();
            }
            return _verdict;
        }
    }

    /// <summary>The connect host and port from the command line, for /rc server. Any thread.</summary>
    public static string HostDescription
    {
        get
        {
            ReadHostOnce();
            return _host.Length == 0 ? "(none)" : _port > 0 ? $"{_host}:{_port}" : _host;
        }
    }

    /// <summary>The connect host from the command line, lower case ("" when none). Any thread (IltInfo).</summary>
    public static string LaunchHost
    {
        get
        {
            ReadHostOnce();
            return _host;
        }
    }

    private static void Refresh()
    {
        try
        {
            ReadHostOnce();
            if (!_bankSeen)
                _bankSeen = HasBankProperties();

            ServerVerdict v = Override switch
            {
                Mode.ForceAelrynth => new ServerVerdict { IsAelrynth = true, Reason = "forced on for this character (/rc server aelrynth auto undoes it)" },
                Mode.ForceOther => new ServerVerdict { Reason = "forced off for this character (/rc server aelrynth auto undoes it)" },
                _ => ServerDetect.Decide(_hostKind, _host, _port, AccountHooks.AnnouncedWorldName, _bankSeen),
            };
            _verdict = v;
            if (v.IsAelrynth != _lastAelrynth)
            {
                _lastAelrynth = v.IsAelrynth;
                RynthLog.Compat($"ServerInfo: {v.Reason}");
            }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"ServerInfo: refresh failed - {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ReadHostOnce()
    {
        if (Volatile.Read(ref _hostRead) != 0)
            return;
        lock (HostLock)
        {
            if (_hostRead != 0)
                return;
            ReadHost();
            Volatile.Write(ref _hostRead, 1);
        }
    }

    private static void ReadHost()
    {
        try
        {
            IntPtr p = GetCommandLineW();
            string? cmd = p == IntPtr.Zero ? null : Marshal.PtrToStringUni(p);
            // Only -h / -p are kept: the command line also carries the account password.
            if (ServerDetect.TryParseLaunchHost(cmd, out string host, out int port))
            {
                _host = host;
                _port = port;
            }
            _hostKind = ServerDetect.ClassifyHost(_host);
        }
        catch
        {
            _hostKind = ServerHostKind.Unknown;
        }
    }

    private static bool HasBankProperties()
    {
        uint me = ClientHelperHooks.GetPlayerId();
        if (me == 0) return false;
        return PropertyCaches.TryGetInt64(me, ServerDetect.RadianceEarnedProperty, out _)
            || PropertyCaches.TryGetInt64(me, ServerDetect.LuminanceBankedProperty, out _)
            || PropertyCaches.TryGetInt64(me, ServerDetect.LuminanceDrawnProperty, out _);
    }

    /// <summary>The Bank mod's "Radiance earned this session" (9101), when the server sends it. Any thread.</summary>
    public static bool TryGetRadianceEarned(out long value)
    {
        value = 0;
        uint me = ClientHelperHooks.GetPlayerId();
        return me != 0 && PropertyCaches.TryGetInt64(me, ServerDetect.RadianceEarnedProperty, out value);
    }
}

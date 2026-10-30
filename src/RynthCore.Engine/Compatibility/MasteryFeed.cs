// ============================================================================
//  RynthCore.Engine - Compatibility/MasteryFeed.cs
//
//  Aelrynth skill mastery for the Skills panel. The numbers exist only on the
//  server: the SkillMastery mod answers "/mastery-data" with ONE chat line,
//  "~ael1 " + JSON (AelrynthWire.cs parses it). This feed asks for that line,
//  reads it, and hides it from chat.
//
//  Only ever on Aelrynth (ServerInfo.IsAelrynth): anywhere else nothing is sent,
//  because another server would answer "Unknown command". If an Aelrynth server
//  answers that anyway (the mod switched off), the feed stops asking until the
//  next login.
//
//  When it asks (each rate limited: one request in flight, 4 s apart at least):
//    - once, 4 s after login (waits up to a minute for detection to settle);
//    - when the Skills panel opens and the last reply is over 30 s old;
//    - 2 s after a mastery raise this feed sent;
//    - "/rc mastery".
//
//  Hiding: the incoming-chat hook (ChatCallbackHooks.IncomingChatAddTextDetour)
//  offers every line to OnIncomingLine first. A data line that answers OUR
//  request (sent in the last 15 s), and ACE's "Unknown command: mastery-data"
//  for it, are eaten: not drawn, not passed on. A line the player asked for by
//  typing /mastery-data is read but left in chat.
//
//  Threads: MainThreadTick and OnIncomingLine run on AC's main thread (the only
//  place a command is sent). The panel's calls (PanelOpened, RequestRefresh,
//  QueueRaise, Snapshot) are any-thread: they set fields under a small lock and
//  the main thread does the sending.
// ============================================================================

using System;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

/// <summary>One published reply with what the panel needs beside it. Immutable.</summary>
internal sealed class MasterySnapshot
{
    public required MasteryData Data { get; init; }
    public required int Version { get; init; }
    public required long ReceivedMs { get; init; }
    /// <summary>The Bank mod's Radiance-earned property (9101) when the reply arrived; -1 when the server doesn't send it.</summary>
    public required long EarnedAtReceipt { get; init; }
}

internal static class MasteryFeed
{
    private const int LoginDelayMs = 4000;
    private const int LoginGiveUpMs = 60_000;
    private const int MinIntervalMs = 4000;
    private const int ReplyWindowMs = 15_000;
    private const int StaleMs = 30_000;
    private const int AfterRaiseMs = 2000;
    private const int MinRaiseIntervalMs = 1500;
    private const int MaxSendFailures = 3;

    private static readonly object Gate = new();

    // Any thread, under Gate.
    private static long _wantAtMs;                 // 0 = no request wanted
    private static string? _raiseCommand;
    private static string _raiseLabel = "";

    // Main thread only.
    private static uint _playerId;
    private static long _loginAtMs;
    private static bool _loginPending;
    private static long _lastDataSendMs = -MinIntervalMs;
    private static long _lastRaiseMs = -MinRaiseIntervalMs;
    private static int _sendFailures;
    private static int _version;

    // Read anywhere.
    private static volatile MasterySnapshot? _snapshot;
    private static long _awaitingUntilMs;          // our request's reply window (Volatile)
    private static volatile bool _unsupported;
    private static volatile string _status = "not asked yet";

    /// <summary>The last reply, or null (none this login). Any thread.</summary>
    public static MasterySnapshot? Snapshot => _snapshot;

    /// <summary>This login's Aelrynth server answered "Unknown command": nothing more is asked until the next login.</summary>
    public static bool Unsupported => _unsupported;

    /// <summary>
    /// Our incoming-chat hook isn't installed (Decal bridge mode leaves AddTextToScroll to
    /// Decal), so a reply could be neither read nor hidden: nothing is asked or raised, and
    /// the server's JSON line never lands in the player's chat. Any thread.
    /// </summary>
    public static bool NoChatHook => !ChatCallbackHooks.IncomingInstalled;

    /// <summary>A request is out and its reply not in yet.</summary>
    public static bool Awaiting => Environment.TickCount64 < Volatile.Read(ref _awaitingUntilMs);

    /// <summary>What the feed did last, for /rc mastery and the panel.</summary>
    public static string Status => _status;

    // ── Any thread: the panel and /rc ───────────────────────────────────

    /// <summary>The Skills panel opened: ask unless the last reply is fresh. Does nothing off Aelrynth.</summary>
    public static void PanelOpened()
    {
        if (!ServerInfo.IsAelrynth || _unsupported) return;
        MasterySnapshot? s = _snapshot;
        if (s == null || Environment.TickCount64 - s.ReceivedMs > StaleMs)
            RequestAt(Environment.TickCount64);
    }

    /// <summary>Ask now (still rate limited). Does nothing off Aelrynth.</summary>
    public static void RequestRefresh()
    {
        if (!ServerInfo.IsAelrynth || _unsupported) return;
        RequestAt(Environment.TickCount64);
    }

    private static void RequestAt(long atMs)
    {
        lock (Gate)
        {
            if (_wantAtMs == 0 || atMs < _wantAtMs)
                _wantAtMs = Math.Max(1, atMs);
        }
    }

    /// <summary>
    /// Queues one "/raise &lt;skill&gt; &lt;count&gt;" for the main thread (the caller has
    /// confirmed with the player). False with the reason when it can't be sent.
    /// </summary>
    public static bool QueueRaise(MasterySkill skill, int count, out string why)
    {
        if (!ServerInfo.IsAelrynth) { why = "Not on Aelrynth."; return false; }
        if (_unsupported) { why = "This server has no mastery commands."; return false; }
        if (NoChatHook) { why = "Not available under Decal (Decal owns incoming chat, so the reply can't be read)."; return false; }
        string? cmd = MasteryWire.RaiseCommand(skill.Key, count);
        if (cmd == null) { why = "That skill's name can't be sent to the server."; return false; }
        lock (Gate)
        {
            if (_raiseCommand != null) { why = "A raise is already on its way."; return false; }
            _raiseCommand = cmd;
            _raiseLabel = $"{skill.Name} +{count}";
        }
        why = "";
        return true;
    }

    /// <summary>
    /// The Radiance a raise can spend now, as near as the client knows: the balance in
    /// the last reply plus what the Bank mod says was earned since (property 9101 rising).
    /// Earnings from the last few seconds may not be banked yet, so the server can still
    /// refuse a raise this says fits. Any thread (dictionary reads); call it about once a second.
    /// </summary>
    public static long EstimateRadiance(MasterySnapshot s)
    {
        long r = s.Data.Radiance;
        if (s.EarnedAtReceipt >= 0 && ServerInfo.TryGetRadianceEarned(out long earned) && earned > s.EarnedAtReceipt)
            r += earned - s.EarnedAtReceipt;
        return r;
    }

    // ── AC's main thread ────────────────────────────────────────────────

    /// <summary>
    /// MainThreadSnapshots.Tick, every tick. <paramref name="playerLive"/>: the player
    /// object is usable (commands are only sent then); a logout (or no player) ends the
    /// login, so the next one asks again even on the same character. A few compares when
    /// nothing is wanted; sends at most one command per call.
    /// </summary>
    internal static void MainThreadTick(bool playerLive)
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;
        long now = Environment.TickCount64;
        uint me = LogoutLifecycleHooks.HasObservedLogout ? 0 : ClientHelperHooks.GetPlayerId();
        if (me != _playerId)
            NewLogin(me, now);
        if (me == 0 || !playerLive)
            return;
        // Idle: two volatile loads, no lock.
        if (!_loginPending && Volatile.Read(ref _wantAtMs) == 0 && Volatile.Read(ref _raiseCommand) == null)
            return;

        string? raise;
        long wantAt;
        lock (Gate)
        {
            raise = _raiseCommand;
            wantAt = _wantAtMs;
        }
        if (!_loginPending && wantAt == 0 && raise == null)
            return;

        if (NoChatHook)
        {
            // Decal bridge mode: the reply would show in chat and never reach us.
            _loginPending = false;
            if (wantAt != 0 || raise != null)
                lock (Gate) { _wantAtMs = 0; _raiseCommand = null; }
            _status = "not asked: Decal owns incoming chat (bridge mode), so the reply can't be read or hidden";
            return;
        }

        if (!ServerInfo.IsAelrynth || _unsupported)
        {
            // Detection can settle a few seconds into the login (the Bank properties, the
            // world name): the login request waits for it, up to a minute.
            if (_loginPending && now - _loginAtMs > LoginGiveUpMs)
                _loginPending = false;
            if (wantAt != 0 || raise != null)
            {
                lock (Gate) { _wantAtMs = 0; _raiseCommand = null; }
                if (raise != null)
                    _status = _unsupported ? "raise not sent: this server has no mastery commands" : "raise not sent: not on Aelrynth";
            }
            return;
        }

        if (raise != null && now - _lastRaiseMs >= MinRaiseIntervalMs)
        {
            string label;
            lock (Gate) { _raiseCommand = null; label = _raiseLabel; }
            _lastRaiseMs = now;
            if (ChatCommandDispatcher.SendServerCommand(raise))
            {
                _status = $"sent: raise {label}";
                RynthLog.Compat($"MasteryFeed: sent {raise}");
                RequestAt(now + AfterRaiseMs);
            }
            else
            {
                _status = $"raise {label} could not be sent";
            }
            return;                                // one command per tick
        }

        if (_loginPending && now - _loginAtMs >= LoginDelayMs)
        {
            _loginPending = false;
            RequestAt(now);
            wantAt = now;
        }

        if (wantAt != 0 && now >= wantAt && now - _lastDataSendMs >= MinIntervalMs && !Awaiting)
        {
            lock (Gate) { _wantAtMs = 0; }
            _lastDataSendMs = now;
            _loginPending = false;                 // the panel asked first: that covers the login request
            if (ChatCommandDispatcher.SendServerCommand(MasteryWire.DataRequest))
            {
                Volatile.Write(ref _awaitingUntilMs, now + ReplyWindowMs);
                _sendFailures = 0;
                _status = "asked the server";
            }
            else if (++_sendFailures < MaxSendFailures)
            {
                RequestAt(now + 10_000);
                _status = "could not send the request; trying again shortly";
            }
            else
            {
                _status = "could not send the request (the client's talk function isn't bound)";
            }
        }
    }

    private static void NewLogin(uint me, long now)
    {
        _playerId = me;
        _loginAtMs = now;
        _loginPending = me != 0;
        _snapshot = null;
        _unsupported = false;
        _sendFailures = 0;
        Volatile.Write(ref _awaitingUntilMs, 0);
        lock (Gate) { _wantAtMs = 0; _raiseCommand = null; }
        _status = me != 0 ? "waiting to ask after login" : "logged out";
    }

    /// <summary>
    /// Every incoming chat line, from the chat hook (AC's main thread), before AC draws
    /// it. True = eat it (our own request's reply). Never throws.
    /// </summary>
    internal static bool OnIncomingLine(string line)
    {
        try
        {
            if (line.Length < 8)
                return false;
            bool awaiting = Awaiting;
            if (MasteryWire.IsDataLine(line))
            {
                if (!ServerInfo.IsAelrynth)
                    return false;
                if (MasteryWire.TryParse(line, out MasteryData? data) && data != null)
                {
                    Publish(data);
                    Volatile.Write(ref _awaitingUntilMs, 0);
                    return awaiting;
                }
                // Not a mastery line (another mod's data line), or one too damaged to read.
                if (awaiting && line.Contains("\"t\":\"mastery\"", StringComparison.Ordinal))
                {
                    Volatile.Write(ref _awaitingUntilMs, 0);
                    _status = "the server's reply could not be read";
                    RynthLog.Compat($"MasteryFeed: unreadable reply ({line.Length} chars)");
                    return true;
                }
                return false;
            }
            if (awaiting && MasteryWire.IsUnknownCommand(line, MasteryWire.DataCommand))
            {
                _unsupported = true;
                Volatile.Write(ref _awaitingUntilMs, 0);
                _status = "this server has no mastery commands (asked once; not again until the next login)";
                RynthLog.Compat("MasteryFeed: the server answered Unknown command: mastery-data - not asking again this login.");
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void Publish(MasteryData data)
    {
        long earned = ServerInfo.TryGetRadianceEarned(out long e) ? e : -1;
        _snapshot = new MasterySnapshot
        {
            Data = data,
            Version = ++_version,
            ReceivedMs = Environment.TickCount64,
            EarnedAtReceipt = earned,
        };
        _status = data.Ok
            ? $"{data.Skills.Count} skills, {data.Radiance:N0} Radiance banked"
            : "the server could not read your mastery" + (data.Why.Length > 0 ? $" ({data.Why})" : "");
    }
}

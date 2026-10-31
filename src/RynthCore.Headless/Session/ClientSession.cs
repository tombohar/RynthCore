// ============================================================================
//  RynthCore.Headless - Session/ClientSession.cs
//  One account's connection, from LoginRequest to Disconnect:
//
//    Connecting      LoginRequest sent, waiting for the ConnectRequest
//    CharacterSelect handshake done; CharacterList / ServerName / DDD arrive
//    EnteringWorld   0xF7C8 sent -> 0xF7DF ServerReady -> 0xF657 sent
//    InWorld         PlayerCreate seen; LoginComplete sent once our object exists
//    LoggingOut      0xF653 sent, waiting for the server's 0xF653 + a fresh CharacterList
//    Closed / Failed Disconnect sent (or the server went away)
//
//  Single-threaded: the owner calls Tick() in a loop. Events fire from inside Tick.
//  The password is used for the LoginRequest only and is never logged or stored here.
// ============================================================================

using System.Buffers.Binary;
using RynthCore.Headless.Messages;
using RynthCore.Headless.Net;
using RynthCore.Headless.World;
using RynthCore.PluginSdk.Net;

namespace RynthCore.Headless.Session;

public enum SessionState { Idle, Connecting, CharacterSelect, EnteringWorld, InWorld, LoggingOut, Closed, Failed }

public sealed class SessionOptions
{
    public required string Account { get; init; }
    /// <summary>Selects by name (case-insensitive); null = stay at character select.</summary>
    public string? Character { get; init; }
    /// <summary>With no <see cref="Character"/>: enter the world as the first character listed.</summary>
    public bool PickFirstCharacter { get; init; }
    public long ConnectTimeoutMs { get; init; } = 10_000;
    public long EnterWorldTimeoutMs { get; init; } = 30_000;
    public long LogoutTimeoutMs { get; init; } = 20_000;
    public long SilenceTimeoutMs { get; init; } = 45_000;
}

public sealed partial class ClientSession
{
    private readonly SessionOptions _opt;
    private readonly Transport _net;
    private readonly Func<long> _now;

    private long _stateSince;
    private long _lastHeardMs;
    private long _lastPackets;
    private uint _actionSeq;
    private bool _loginCompleteSent;
    private bool _logoffConfirmed;
    private string _serverAccount = "";

    public SessionState State { get; private set; } = SessionState.Idle;
    public string? FailReason { get; private set; }
    public IReadOnlyList<(uint Id, string Name)> Characters { get; private set; } = Array.Empty<(uint, string)>();
    public uint CharacterId { get; private set; }
    public uint PlayerId { get; private set; }
    public string WorldName { get; private set; } = "";
    public int ObjectsSeen { get; private set; }
    public double ServerTime => _net.LastServerTime;
    /// <summary>The server's clock now: the last time sync plus the local time since it arrived (0 before the first).</summary>
    public double ServerTimeNow => _net.LastServerTime <= 0 ? 0 : _net.LastServerTime + (_now() - _net.LastServerTimeAtMs) / 1000.0;
    public WorldState World { get; }

    public event Action<string>? Log;
    public event Action<ChatLine>? Chat;
    public event Action<ObjectSeen>? ObjectCreated;
    public event Action<uint>? ObjectDeleted;
    public event Action<SessionState>? StateChanged;
    /// <summary>Every whole server message (opcode first), before the session handles it.</summary>
    public event Action<uint, ReadOnlyMemory<byte>>? RawMessage;

    internal Transport Transport => _net;

    internal ClientSession(IDatagramLink link, SessionOptions options, Func<long> nowMs)
    {
        _opt = options;
        _now = nowMs;
        _net = new Transport(link, nowMs);
        World = new WorldState(nowMs);
        World.PlayerMoveTo += OnServerMoveTo;
        World.PlayerCorrected += _ => { if (Moving) EndMove(false, "the server moved us"); };
    }

    public static ClientSession Connect(UdpLink link, SessionOptions options)
    {
        long t0 = Environment.TickCount64;
        return new ClientSession(link, options, () => Environment.TickCount64 - t0);
    }

    public void Start(string password)
    {
        if (State != SessionState.Idle) throw new InvalidOperationException("already started");
        _net.SendLoginRequest(LoginMessages.LoginRequest(_opt.Account, password, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        _lastHeardMs = _now();
        SetState(SessionState.Connecting);
    }

    /// <summary>Local chat (or an @command).</summary>
    public void Say(string text)
    {
        if (State != SessionState.InWorld) throw new InvalidOperationException("not in world");
        SendAction(GameActionType.Talk, LoginMessages.TalkPayload(text));
    }

    /// <summary>Starts a clean logout; Tick sends the Disconnect once the server confirms.</summary>
    public void Logout()
    {
        if (State == SessionState.InWorld || State == SessionState.EnteringWorld)
        {
            _net.SendMessage(LoginMessages.LogOff(), MessageQueue.Login);
            SetState(SessionState.LoggingOut);
        }
        else if (State is SessionState.CharacterSelect or SessionState.Connecting)
        {
            Close("logout at character select");
        }
    }

    /// <summary>Ends the session right away.</summary>
    public void Close(string why)
    {
        if (State is SessionState.Closed or SessionState.Failed) return;
        _net.SendDisconnect();
        Info($"closed: {why}");
        SetState(SessionState.Closed);
    }

    public bool Finished => State is SessionState.Closed or SessionState.Failed;

    public void Tick()
    {
        if (Finished) return;
        _net.Pump();
        if (_net.PacketsIn != _lastPackets)
        {
            _lastPackets = _net.PacketsIn;
            _lastHeardMs = _now();
        }
        while (_net.TryDequeue(out byte[] m))
            Handle(m);
        if (Finished) return;
        TickDrive();      // before TickMovement: a walk that just started takes over from the drive first
        TickMovement();
        _net.Tick();
        CheckTimers();
    }

    // ── timers ──────────────────────────────────────────────────────────────

    private void CheckTimers()
    {
        long now = _now();
        long inState = now - _stateSince;
        switch (State)
        {
            case SessionState.Connecting when inState > _opt.ConnectTimeoutMs:
                Fail("no answer from the server (ConnectRequest/CharacterList)");
                return;
            case SessionState.EnteringWorld when inState > _opt.EnterWorldTimeoutMs:
                Fail("timed out entering the world");
                return;
            case SessionState.LoggingOut when inState > _opt.LogoutTimeoutMs:
                Close("logout not confirmed in time");
                return;
        }
        if (State != SessionState.Connecting && now - _lastHeardMs > _opt.SilenceTimeoutMs)
            Fail($"server silent for {(now - _lastHeardMs) / 1000}s");
    }

    // ── messages ────────────────────────────────────────────────────────────

    private void Handle(byte[] m)
    {
        if (!ServerMessage.TryFromWire(m, out ServerMessage msg)) return;
        RawMessage?.Invoke(msg.Opcode, m);
        World.Apply(msg);
        ReadOnlySpan<byte> b = msg.Body;

        switch (msg.Opcode)
        {
            case Opcode.CharacterList:
                if (!LoginMessages.TryParseCharacterList(b, out CharacterListData list)) { Info("CharacterList: unparsed"); break; }
                _serverAccount = list.Account;
                Characters = list.Characters.Select(c => (c.Id, c.Name)).ToList();
                if (State == SessionState.LoggingOut)
                {
                    if (_logoffConfirmed) Close("logged out");
                    break;
                }
                Info($"characters: {string.Join(", ", Characters.Select(c => c.Name))}");
                if (State == SessionState.Connecting) SetState(SessionState.CharacterSelect);
                PickCharacter();
                break;

            case Opcode.ServerName:
                if (LoginMessages.TryParseServerName(b, out int online, out int max, out string world))
                {
                    WorldName = world;
                    Info($"server: {world} ({online}/{max} online)");
                }
                break;

            case Opcode.DddInterrogation:
                _net.SendMessage(LoginMessages.DddInterrogationResponse(), MessageQueue.Database);
                break;

            case Opcode.DddEnd:
                break;

            case Opcode.EnterWorldServerReady:
                if (State == SessionState.EnteringWorld)
                    _net.SendMessage(LoginMessages.EnterWorld(CharacterId, _serverAccount), MessageQueue.Login);
                break;

            case Opcode.CharacterError:
                Fail($"character error 0x{U32(b):X}");
                break;

            case Opcode.AccountBoot:
                Fail("booted by the server");
                break;

            case Opcode.PlayerCreate:
                // Our own CreateObject travels in another queue and may come first.
                PlayerId = U32(b);
                SetState(SessionState.InWorld);
                if (World.Get(PlayerId) != null && !_loginCompleteSent)
                    SendLoginComplete();
                break;

            case Opcode.ObjectCreate:
                ObjectsSeen++;
                if (ObjectCreate.TryParse(b, out ObjectCreateData o))
                {
                    ObjectCreated?.Invoke(new ObjectSeen(o.Id, o.Name, o.WeenieClassId, o.ItemType, o.Position?.Cell ?? 0));
                    if (o.Id == PlayerId && PlayerId != 0 && !_loginCompleteSent)
                        SendLoginComplete();
                }
                else
                {
                    Info($"ObjectCreate 0x{U32(b):X8}: unparsed ({b.Length} bytes)");
                }
                break;

            case Opcode.ObjectDelete:
                ObjectDeleted?.Invoke(U32(b));
                break;

            case Opcode.PlayerTeleport:
                if (State == SessionState.InWorld) SendLoginComplete();
                break;

            case Opcode.CharacterLogOff:
                if (State == SessionState.LoggingOut) _logoffConfirmed = true;
                break;

            case Opcode.HearSpeech:
                if (HearSpeech.TryParse(b, out HearSpeech hs))
                    Chat?.Invoke(new ChatLine(hs.ChatType, hs.SenderName, hs.SenderId, hs.Text));
                break;

            case Opcode.HearRangedSpeech:
                if (HearRangedSpeech.TryParse(b, out HearRangedSpeech hr))
                    Chat?.Invoke(new ChatLine(hr.ChatType, hr.SenderName, hr.SenderId, hr.Text));
                break;

            case Opcode.HearEmote:
            case Opcode.HearSoulEmote:
                if (HearEmote.TryParse(b, msg.Opcode == Opcode.HearSoulEmote, out HearEmote he))
                    Chat?.Invoke(new ChatLine(0x0C, he.SenderName, he.SenderId, he.Text));
                break;

            case Opcode.ServerMessage:
                if (LoginMessages.TryParseServerMessage(b, out string text, out uint type))
                    Chat?.Invoke(new ChatLine(type, "", 0, text));
                break;

            case Opcode.GameEvent:
                HandleEvent(msg);
                break;
        }
    }

    private void HandleEvent(ServerMessage msg)
    {
        ReadOnlySpan<byte> e = msg.EventBody;
        switch (msg.EventType)
        {
            case GameEventType.HearDirectSpeech:
                if (HearDirectSpeech.TryParse(e, out HearDirectSpeech t))
                    Chat?.Invoke(new ChatLine(t.ChatType, t.SenderName, t.SenderId, t.Text));
                break;
            case GameEventType.ChannelBroadcast:
                if (ChannelBroadcast.TryParse(e, out ChannelBroadcast c))
                    Chat?.Invoke(new ChatLine(0x08, c.SenderName, 0, c.Text));
                break;
        }
    }

    private void PickCharacter()
    {
        if (State != SessionState.CharacterSelect) return;
        (uint Id, string Name) pick;
        if (_opt.Character != null)
            pick = Characters.FirstOrDefault(c => string.Equals(c.Name.TrimStart('+'), _opt.Character.TrimStart('+'), StringComparison.OrdinalIgnoreCase));
        else if (_opt.PickFirstCharacter)
            pick = Characters.FirstOrDefault();
        else
            return;
        if (pick.Id == 0)
        {
            Fail(_opt.Character != null ? $"no character named '{_opt.Character}' on this account" : "no characters on this account");
            return;
        }
        CharacterId = pick.Id;
        Info($"entering world as {pick.Name} (0x{pick.Id:X8})");
        _net.SendMessage(LoginMessages.EnterWorldRequest(), MessageQueue.Login);
        SetState(SessionState.EnteringWorld);
    }

    private void SendLoginComplete()
    {
        // Sent with action sequence 0, like the retail client; it clears the "portal space" state.
        _net.SendMessage(LoginMessages.GameAction(0, GameActionType.LoginComplete), MessageQueue.UI);
        _loginCompleteSent = true;
        Info("login complete sent");
    }

    private void SendAction(uint type, ReadOnlySpan<byte> payload)
    {
        _net.SendMessage(LoginMessages.GameAction(++_actionSeq, type, payload), MessageQueue.UI);
        ActionSent?.Invoke(type, payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0);
    }

    /// <summary>A game action went out (type, its first u32 argument or 0). For tracing.</summary>
    public event Action<uint, uint>? ActionSent;

    // ── helpers ─────────────────────────────────────────────────────────────

    private static uint U32(ReadOnlySpan<byte> b) => b.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(b) : 0;

    private void SetState(SessionState s)
    {
        if (State == s) return;
        State = s;
        _stateSince = _now();
        StateChanged?.Invoke(s);
    }

    private void Fail(string why)
    {
        if (Finished) return;
        FailReason = why;
        _net.SendDisconnect();
        Info($"failed: {why}");
        SetState(SessionState.Failed);
    }

    private void Info(string s) => Log?.Invoke(s);

    public string Stats =>
        $"in {_net.PacketsIn} out {_net.PacketsOut} resends {_net.Resends} naks {_net.NaksSent} " +
        $"badsum {_net.BadChecksums} bad {_net.BadPackets} dup {_net.Duplicates} rejected {_net.Rejected} missing {_net.Missing.Count}";
}

public readonly record struct ChatLine(uint ChatType, string Sender, uint SenderId, string Text);

public readonly record struct ObjectSeen(uint Id, string Name, uint WeenieClassId, uint ItemType, uint Cell);

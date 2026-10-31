// ============================================================================
//  rynth-headless - Bot.cs
//  One character in a multi-character run: its own socket, session, world model,
//  plugin host (each plugin in its own load context, so two RynthAi instances don't
//  share statics) and log; the geometry and spell table are shared by all bots.
//
//  Life: connect -> in world (plugins get OnLoginComplete) -> its commands, 2 s apart ->
//  runs until its stay time is up or the runner stops -> end commands, 3 s apart ->
//  logout. A session that fails is retried (reconnect: 15 s, doubling to 5 min; an
//  account still in use by a session being booted: 8 s).
// ============================================================================

using System.Text;
using RynthCore.Headless.Dat;
using RynthCore.Headless.Host;
using RynthCore.Headless.Host.Status;
using RynthCore.Headless.Movement;
using RynthCore.Headless.Net;
using RynthCore.Headless.Session;

namespace RynthCore.Headless.Cli;

internal sealed class Bot : IDisposable
{
    private readonly RunConfig _run;
    private readonly SessionConfig _cfg;
    private readonly GeometryWalk? _geo;
    private readonly SpellTable? _spells;
    private readonly Action<string> _console;
    private readonly StreamWriter _log;

    private UdpLink? _link;
    private ClientSession? _session;
    private HostContext? _ctx;
    private PluginHost? _plugins;
    private HostBridge? _bridge;
    private readonly StatusExporter? _status;
    private readonly int _clientId;

    private long _nextConnect, _inWorldAt = -1, _nextCommand, _nextEnd;
    private int _commandIndex, _endIndex, _failures;
    private bool _stopping, _loggingOut, _done;

    public string Label => _cfg.Label;
    public bool Done => _done;
    public UdpLink? Link => _link;
    public ClientSession? Session => _session;
    public HostContext? Context => _ctx;
    public int Failures => _failures;
    public long InWorldSince => _inWorldAt;
    /// <summary>The id its status files carry (RynthCore.&lt;id&gt;.*), stable across reconnects.</summary>
    public int ClientId => _clientId;

    public Bot(RunConfig run, SessionConfig cfg, int slot, GeometryWalk? geo, SpellTable? spells, Action<string> console)
    {
        _clientId = StatusExporter.MakeClientId(slot);
        _run = run;
        _cfg = cfg;
        _geo = geo;
        _spells = spells;
        _console = console;
        Directory.CreateDirectory(run.SuiteDir);
        _log = new StreamWriter(Path.Combine(run.SuiteDir, $"headless-{Safe(cfg.Account)}.log"), append: true, Encoding.UTF8) { AutoFlush = true };
        if (run.StatusDir.Length > 0)
            _status = new StatusExporter(run.StatusDir, _clientId, cfg.Account) { ServerName = cfg.ServerName, LastCharacter = cfg.Character ?? "" };
    }

    private static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    private void Say(string s)
    {
        _log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}");
        _console($"[{Label}] {s}");
    }

    private void Note(string s) => _log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}");

    /// <summary>Asks the bot to finish: end commands, logout, done.</summary>
    public void Stop() => _stopping = true;

    public void Tick(long now)
    {
        if (_done) return;
        if (_session == null)
        {
            _status?.Tick();
            if (_stopping) { Finish(); return; }
            if (now >= _nextConnect) Connect(now);
            return;
        }

        _session.Tick();
        _bridge?.Tick();
        _status?.Tick();

        if (_session.Finished)
        {
            Say($"session ended: {_session.State}{(_session.FailReason != null ? " - " + _session.FailReason : "")}; {_session.Stats}");
            bool inUse = _session.FailReason == "character error 0x1";
            bool failed = _session.State == SessionState.Failed;
            TearDown();
            if (_stopping || _loggingOut && !failed || !_cfg.Reconnect) { Finish(); return; }
            _failures++;
            long wait = inUse ? 8_000 : Math.Min(300_000, 15_000L << Math.Min(_failures - 1, 5));
            _nextConnect = now + wait;
            Say($"reconnecting in {wait / 1000} s");
            return;
        }

        if (_session.State != SessionState.InWorld) return;
        if (_inWorldAt < 0)
        {
            _inWorldAt = now;
            _failures = 0;
            _nextCommand = now + 4000;
            Say($"in the world as {_session.World.PlayerObject?.Name ?? "?"}");
        }

        if (_commandIndex < _cfg.Commands.Count && now >= _nextCommand && !_stopping)
        {
            string cmd = _cfg.Commands[_commandIndex++];
            Say($"command {cmd}: {(Invoke(cmd) ? "handled" : "refused")}");
            _nextCommand = now + 2000;
        }

        bool timeUp = _cfg.StaySeconds > 0 && now - _inWorldAt > _cfg.StaySeconds * 1000L;
        if ((timeUp || _stopping) && !_loggingOut)
        {
            if (_endIndex < _cfg.EndCommands.Count)
            {
                if (now >= _nextEnd)
                {
                    string cmd = _cfg.EndCommands[_endIndex++];
                    Say($"end command {cmd}: {(Invoke(cmd) ? "handled" : "refused")}");
                    _nextEnd = now + 3000;
                }
                return;
            }
            if (now < _nextEnd) return;
            _loggingOut = true;
            _stopping = true;
            Say("logging out");
            _session.Logout();
        }
    }

    private bool Invoke(string cmd)
    {
        if (_ctx != null && _plugins != null) return _ctx.ActInvokeChatParser(cmd);
        _session!.Say(cmd);
        return true;
    }

    private void Connect(long now)
    {
        string problem = $"environment variable {_cfg.PasswordEnv} is not set";
        string? password = _cfg.LauncherProfileId.Length > 0 ? LauncherAccounts.TryGetPasswordById(_cfg.LauncherProfileId, out problem)
            : _cfg.LauncherAccount.Length > 0 ? LauncherAccounts.TryGetPassword(_cfg.LauncherAccount, _cfg.Host, _cfg.ServerPort, out problem)
            : Environment.GetEnvironmentVariable(_cfg.PasswordEnv);
        if (string.IsNullOrEmpty(password))
        {
            Say($"no password: {problem}");
            Finish();
            return;
        }

        _link = UdpLink.Open(_cfg.Host, _cfg.ServerPort);
        _session = ClientSession.Connect(_link, new SessionOptions
        {
            Account = _cfg.Account,
            Character = _cfg.Character,
            PickFirstCharacter = _cfg.Character == null,
        });
        _session.Log += Note;
        _session.StateChanged += s => Note($"state: {s}");
        // What the server refuses and where it sends us: a bot has no screen to show these on.
        var w = _session.World;
        w.InventoryFailed += (item, error) => Note($"[server] inventory move of 0x{item:X8} failed (error 0x{error:X})");
        w.Error += (code, text) => Note($"[server] error 0x{code:X}{(text.Length > 0 ? ": " + text : "")}");
        w.TransientMessage += text => Note($"[server] {text}");
        w.PlayerMoveTo += m => Note($"[server] move-to type {m.MovementType}{(m.TargetId != 0 ? $" object 0x{m.TargetId:X8}" : "")} within {m.DistanceToObject:F1} m");
        if (_geo != null)
        {
            _session.Walker = _geo;
            _geo.TrackDoors(_session.World);
        }

        _ctx = new HostContext(_session, _cfg.Account, _geo, _spells) { LogSink = Note, TraceActions = true };
        var plugins = _cfg.Plugins ?? _run.Plugins;
        if (plugins.Count > 0)
        {
            _plugins = new PluginHost(_ctx);
            foreach (string p in plugins)
                Say($"plugin {Path.GetFileName(p)}: {(_plugins.Load(p) ? "loaded" : "FAILED")}");
            _bridge = new HostBridge(_ctx, _plugins);
        }
        if (_status != null) _status.Context = _ctx;

        _inWorldAt = -1;
        _commandIndex = 0;
        _endIndex = 0;
        _loggingOut = false;
        Say($"connecting to {_cfg.Host}:{_cfg.ServerPort}");
        _session.Start(password);
    }

    private void TearDown()
    {
        if (_status != null) _status.Context = null;
        _plugins?.Dispose();
        _plugins = null;
        _bridge = null;
        _ctx = null;
        _session = null;
        _link?.Dispose();
        _link = null;
        _inWorldAt = -1;
    }

    private void Finish()
    {
        _done = true;
        _status?.Retire();
    }

    public void Dispose()
    {
        if (_session is { Finished: false }) _session.Close("process exiting");
        TearDown();
        _status?.Retire();
        _log.Dispose();
    }
}

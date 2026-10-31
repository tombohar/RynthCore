// ============================================================================
//  rynth-headless - LauncherSession.cs
//  `run --launcher`: what the engine's SessionStateRegistry does for an injected client,
//  for the one character the launcher started this process for. It writes the launcher's
//  %APPDATA%\RynthCore\sessions\session_<pid>.json (IsLoggedIn once in the world, LogoutAtUtc
//  when it leaves) and the account's character cache from the server's character list, so the
//  launcher's row and character dropdown work as they do for a retail client.
// ============================================================================

using System.Diagnostics;
using RynthCore.App;
using RynthCore.Headless.Session;

namespace RynthCore.Headless.Cli;

internal sealed class LauncherSession
{
    private readonly string _account, _server, _target;
    private readonly DateTime _startedUtc;
    private ClientSession? _seen;
    private bool _loggedIn, _cacheWritten;
    private DateTime? _loginUtc;
    private string _name = "";

    public LauncherSession(RunConfig run, SessionConfig session)
    {
        _account = session.Account;
        _server = session.ServerName;
        _target = session.Character ?? "";
        using (Process me = Process.GetCurrentProcess()) _startedUtc = me.StartTime.ToUniversalTime();
        Write(loggedIn: false, character: "", logoutUtc: null);
    }

    public void Tick(Bot bot)
    {
        ClientSession? s = bot.Session;
        if (!ReferenceEquals(s, _seen))
        {
            _seen = s;
            _cacheWritten = false;
        }
        if (s != null && !_cacheWritten && s.Characters.Count > 0)
        {
            _cacheWritten = true;
            Try(() => CharacterCacheStore.Write(_account, _server, s.Characters.Select(c => c.Name).ToList()));
        }

        bool inWorld = s?.State == SessionState.InWorld && s.World.PlayerObject != null;
        if (inWorld && !_loggedIn)
        {
            _loggedIn = true;
            _loginUtc = DateTime.UtcNow;
            string name = _name = s!.World.PlayerObject!.Name;
            Write(loggedIn: true, character: name, logoutUtc: null);
            Try(() => CharacterCacheStore.UpsertCharacter(_account, _server, name));
        }
        else if (!inWorld && _loggedIn)
        {
            _loggedIn = false;
            Write(loggedIn: false, character: _name, logoutUtc: DateTime.UtcNow);
        }
    }

    private void Write(bool loggedIn, string character, DateTime? logoutUtc)
        => Try(() => SessionStateStore.WriteForProcess(Environment.ProcessId, new SessionStateRecord
        {
            AccountName = _account,
            ServerName = _server,
            TargetCharacter = _target,
            CharacterName = character,
            LaunchStartedAtUtc = _startedUtc,
            LoginCompletedAtUtc = _loginUtc,
            IsLoggedIn = loggedIn,
            LogoutAtUtc = logoutUtc,
        }));

    private static void Try(Action a)
    {
        try { a(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

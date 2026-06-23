using System.Text.Json;

namespace RynthCore.StatusAgent;

/// <summary>
/// Records each AC client's play session (login → exit) so the player can review how past runs did
/// (kills, kills/hr, XP, deaths, …). Fed one snapshot list per agent cycle via <see cref="Observe"/>;
/// a run is finalised when its client reports "dead", relaunches (uptime resets), or vanishes from the
/// feed for longer than the grace window. Finished runs persist to <c>runs.json</c> so the history
/// survives agent restarts. Agent-side only — the game client is never touched.
/// </summary>
internal sealed class RunArchive
{
    // A run must drop its uptime by more than this to count as a relaunch (vs. normal jitter / a clock skew).
    private const int RestartSlackSec = 120;
    // A pid missing from the feed this long is treated as finished (covers a transient unreadable-file blip
    // without losing the run; a clean exit usually finalises sooner via the explicit "dead" state).
    private const int GoneGraceSec = 60;
    // Only keep a finished run that actually did something — filters brief loads / instant relaunches.
    private const int MinKeepSec = 120;

    private readonly string _runsPath;
    private readonly string _host;
    private readonly int _maxRuns;
    private readonly object _lock = new();

    // In-progress runs, keyed by pid. Finished runs, newest first.
    private readonly Dictionary<int, LiveRun> _live = new();
    private readonly List<RunRecord> _finished = new();

    public RunArchive(string runsPath, string host, int maxRuns = 1000)
    {
        _runsPath = runsPath;
        _host = host;
        _maxRuns = Math.Max(1, maxRuns);
        LoadFromDisk();
    }

    /// <summary>One agent cycle's worth of client snapshots. Starts/updates/finalises runs accordingly.</summary>
    public void Observe(List<ClientStatus> clients)
    {
        var now = DateTimeOffset.UtcNow;
        bool dirty = false;
        lock (_lock)
        {
            var seen = new HashSet<int>();
            foreach (ClientStatus c in clients)
            {
                seen.Add(c.Pid);
                bool rich = string.Equals(c.Source, "status-file", StringComparison.Ordinal);
                bool dead = string.Equals(c.State, "dead", StringComparison.OrdinalIgnoreCase);

                _live.TryGetValue(c.Pid, out LiveRun? run);

                // Relaunch reusing the same pid: uptime fell well below the max we've seen → close the old run.
                if (run != null && rich && c.UptimeSec + RestartSlackSec < run.MaxUptimeSec)
                {
                    dirty |= Finalize(run, now);
                    _live.Remove(c.Pid);
                    run = null;
                }

                if (run == null)
                {
                    // Only a live, rich client (real bot stats) starts a run. A bare heartbeat-log client or a
                    // already-dead one never opens one — that would archive an empty session.
                    if (!rich || dead) continue;
                    run = NewRun(c, now);
                    _live[c.Pid] = run;
                }

                run.LastSeenUtc = now;
                if (rich) run.Update(c);

                if (dead)
                {
                    dirty |= Finalize(run, now);
                    _live.Remove(c.Pid);
                }
            }

            // Finalise runs whose client dropped out of the feed entirely (clean exit we never saw flagged
            // "dead", a relaunch to a new pid, or the status file pruned) once past the grace window.
            foreach (var kv in _live.ToArray())
            {
                if (seen.Contains(kv.Key)) continue;
                if ((now - kv.Value.LastSeenUtc).TotalSeconds >= GoneGraceSec)
                {
                    dirty |= Finalize(kv.Value, now);
                    _live.Remove(kv.Key);
                }
            }
        }
        if (dirty) Persist();
    }

    /// <summary>The /runs payload bytes: in-progress runs (ongoing, newest first) then finished runs.</summary>
    public byte[] BuildJsonBytes()
    {
        lock (_lock)
        {
            var runs = new List<RunRecord>(_live.Count + _finished.Count);
            foreach (LiveRun lr in _live.Values.OrderByDescending(r => r.StartUtc))
                runs.Add(lr.ToRecord(ongoing: true, endUtc: null, now: DateTimeOffset.UtcNow));
            runs.AddRange(_finished);   // already newest-first

            var payload = new RunsPayload
            {
                Host = _host,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Count = runs.Count,
                Runs = runs,
            };
            return JsonSerializer.SerializeToUtf8Bytes(payload, AgentJsonContext.Default.RunsPayload);
        }
    }

    // ── internals ───────────────────────────────────────────────────────────

    private static LiveRun NewRun(ClientStatus c, DateTimeOffset now)
    {
        // uptimeSec is the AC client's own session length, so now-uptime is the true login time even if the
        // agent started mid-session. Clamp non-negative for a bogus/zero uptime.
        var start = now - TimeSpan.FromSeconds(Math.Max(0, c.UptimeSec));
        var run = new LiveRun
        {
            Pid = c.Pid,
            StartUtc = start,
            RunId = $"{c.Pid}-{start.ToUnixTimeSeconds()}",
        };
        run.Update(c);
        return run;
    }

    /// <summary>Append a finished run if it's worth keeping. Returns true if the archive changed.</summary>
    private bool Finalize(LiveRun run, DateTimeOffset now)
    {
        long duration = Math.Max(run.MaxUptimeSec, (long)(now - run.StartUtc).TotalSeconds);
        if (run.Kills <= 0 && run.Xp <= 0 && duration < MinKeepSec)
            return false;   // brief, fruitless session — not worth archiving

        _finished.Insert(0, run.ToRecord(ongoing: false, endUtc: now, now: now));
        if (_finished.Count > _maxRuns)
            _finished.RemoveRange(_maxRuns, _finished.Count - _maxRuns);
        return true;
    }

    private void Persist()
    {
        try
        {
            var payload = new RunsPayload
            {
                Host = _host,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Count = _finished.Count,
                Runs = _finished,   // persist finished runs only; ongoing ones rebuild from the live feed
            };
            byte[] bytes;
            lock (_lock) bytes = JsonSerializer.SerializeToUtf8Bytes(payload, AgentJsonContext.Default.RunsPayload);

            string? dir = Path.GetDirectoryName(_runsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = _runsPath + ".tmp";
            File.WriteAllBytes(tmp, bytes);          // UTF-8, no BOM
            File.Move(tmp, _runsPath, overwrite: true);
        }
        catch (Exception ex) { AgentLog.Debug($"runs.json write failed: {ex.Message}"); }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_runsPath)) return;
            var payload = JsonSerializer.Deserialize(File.ReadAllText(_runsPath), AgentJsonContext.Default.RunsPayload);
            if (payload?.Runs == null) return;
            lock (_lock)
            {
                _finished.Clear();
                foreach (RunRecord r in payload.Runs.Where(r => !r.Ongoing).Take(_maxRuns))
                    _finished.Add(r);
            }
            AgentLog.Info($"Run archive: loaded {_finished.Count} past run(s) from {_runsPath}.");
        }
        catch (Exception ex) { AgentLog.Warn($"runs.json load failed ({ex.GetType().Name}: {ex.Message}); starting empty."); }
    }

    /// <summary>Mutable in-progress run; the last snapshot wins for each field.</summary>
    private sealed class LiveRun
    {
        public int Pid;
        public string RunId = "";
        public DateTimeOffset StartUtc;
        public DateTimeOffset LastSeenUtc;
        public long MaxUptimeSec;

        public string Account = "";
        public string Character = "";
        public string Server = "";
        public int Kills;
        public double KillsPerHour;
        public long Xp;
        public double XpPerHour;
        public double LuminancePerHour;
        public int Deaths;
        public double VitaePct;
        public string Area = "";

        public void Update(ClientStatus c)
        {
            if (!string.IsNullOrEmpty(c.Account)) Account = c.Account;
            if (!string.IsNullOrEmpty(c.Character)) Character = c.Character;
            if (!string.IsNullOrEmpty(c.Server)) Server = c.Server;
            if (!string.IsNullOrEmpty(c.Area)) Area = c.Area;
            // Counters only ever grow within a session; guard against a transient 0 read clobbering the total.
            if (c.SessionKills > Kills) Kills = c.SessionKills;
            if (c.XpSession > Xp) Xp = c.XpSession;
            if (c.DeathsSession > Deaths) Deaths = c.DeathsSession;
            if (c.KillsPerHour > 0) KillsPerHour = c.KillsPerHour;
            if (c.XpPerHour > 0) XpPerHour = c.XpPerHour;
            if (c.LuminancePerHour > 0) LuminancePerHour = c.LuminancePerHour;
            if (c.VitaePct > 0) VitaePct = c.VitaePct;
            if (c.UptimeSec > MaxUptimeSec) MaxUptimeSec = c.UptimeSec;
        }

        public RunRecord ToRecord(bool ongoing, DateTimeOffset? endUtc, DateTimeOffset now) => new()
        {
            RunId = RunId,
            Pid = Pid,
            Account = Account,
            Character = Character,
            Server = Server,
            StartUtc = StartUtc,
            EndUtc = endUtc,
            DurationSec = Math.Max(MaxUptimeSec, (long)(now - StartUtc).TotalSeconds),
            Kills = Kills,
            KillsPerHour = KillsPerHour,
            Xp = Xp,
            XpPerHour = XpPerHour,
            LuminancePerHour = LuminancePerHour,
            Deaths = Deaths,
            VitaePct = VitaePct,
            Area = Area,
            Ongoing = ongoing,
        };
    }
}

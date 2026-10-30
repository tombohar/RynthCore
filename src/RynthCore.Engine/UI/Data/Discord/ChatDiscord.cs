// ============================================================================
//  RynthCore.Engine - UI/Data/Discord/ChatDiscord.cs
//  RynthChat's Discord relay: copies chosen chat (speech channels, or every
//  line that shows in a chat tab) to Discord channels by webhook or bot.
//  Configured in the Chat panel's settings (gear > Discord) and its Discord
//  panel (ImGui/Panels/ChatDiscordFace.cs); /rc discord for quick control.
//
//  Threads
//    • ChatSource.Poll (the pump) hands every scrollback read to PumpRead and
//      calls PumpTick each poll: routing, batching, at most one HTTP post per
//      route in flight, applying results. The relay keeps the chat feed
//      subscribed while it is on (or its panel is open), so it runs with the
//      Chat panel closed.
//    • The panel and /rc discord (AC's thread) edit Settings in place and
//      queue Resume / Send test / Clear log; secrets are encrypted and the
//      file written on UiBackgroundWriter, never on AC's thread.
//    • HTTP posts run on the thread pool and hand results back through a queue.
//
//  Per route, lines arriving within the batch window go out as one message
//  (<= 2000 characters), spaced by the route's minimum interval or whatever
//  Discord's rate limit asks for. Network / 5xx failures retry with backoff;
//  400 / 401 / 403 / 404 pause the route until it's resumed or its
//  credential changes. Scrollback read when the relay starts (engine start,
//  hot reload, switching it on) is not sent: the first read after it comes
//  on is a baseline, judged by RynthChat's line seq (DiscordBacklogGate).
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.UI.Data.Discord;

internal enum DiscordLogKind { Sent, Error, Info }

/// <summary>One line in the Discord panel's log (immutable once added).</summary>
internal sealed record DiscordLogEntry(DateTime Time, DiscordLogKind Kind, string Route, string Text);

internal static class ChatDiscord
{
    private static ChatDiscordSettings _settings = new();
    private static int _loaded;
    private static readonly string FilePath = ChatDiscordStore.DefaultPath;

    // Any thread -> pump.
    private static readonly ConcurrentQueue<(DiscordRoute Route, DiscordBatch Batch, DiscordSendResult Result)> Done = new();
    private static readonly ConcurrentQueue<DiscordRoute> ResumeQueue = new();
    private static readonly ConcurrentQueue<DiscordRoute> TestQueue = new();
    private static volatile bool _clearLog;
    private static volatile bool _shutdown;
    private static readonly CancellationTokenSource Cts = new();
    private static int _postsRunning;

    // Pump only.
    private static readonly Dictionary<DiscordRoute, DiscordRouteQueue> States = new(ReferenceEqualityComparer.Instance);
    private static readonly List<DiscordLogEntry> Log = new();

    // Pump writes, panel reads.
    private static DiscordLogEntry[] _logSnapshot = Array.Empty<DiscordLogEntry>();

    // The chat feed (UiSources.Chat) is held while some character has the relay on, or its panel is open.
    private static int _feedHeld, _panelsOpen;

    // Pump only: keeps the scrollback read when the relay comes on out of Discord.
    private static readonly DiscordBacklogGate Backlog = new();

    // The character playing, as the pump last saw it (the panel can't ask: the lookup may read a file).
    private static string _characterName = string.Empty;

    // Per-character switch. A switch is applied to the in-memory list at once and queued as an
    // op; the next save (background writer) merges the queued ops into the file's list. Until
    // that save lands, _pending says what this client's character asked for, so an older
    // merge or refresh can't switch it back on (or off) meanwhile.
    private sealed record SelfPending(DiscordCharacter Character, bool On);
    private static readonly ConcurrentQueue<(DiscordCharacterOp Op, SelfPending? Pending)> CharacterOps = new();
    private static SelfPending? _pending;

    // Another client may switch characters in the file: the pump looks at its time every few
    // seconds and re-reads the list (only the list) when it changed.
    private const int FileCheckMs = 3000;
    private static long _nextFileCheckMs;
    private static long _fileStampTicks;
    private static int _refreshQueued;

    /// <summary>The live settings. Any thread; fields are edited in place (see ChatDiscordSettings).</summary>
    public static ChatDiscordSettings Settings => Volatile.Read(ref _settings);

    /// <summary>The character this client relays as ("" before login). Any thread.</summary>
    public static string CharacterName => Volatile.Read(ref _characterName);

    /// <summary>The character logged in on this client (server + name), or null before login. Any thread.</summary>
    public static DiscordCharacter? CurrentCharacter()
        => DiscordCharacter.Of(SessionStateRegistry.LastServerName, SessionStateRegistry.LastCharacterName);

    /// <summary>True when the relay is on for <paramref name="character"/>. Any thread.</summary>
    public static bool IsOn(DiscordCharacter? character)
    {
        if (character == null) return false;
        SelfPending? p = Volatile.Read(ref _pending);
        if (p != null && p.Character.Same(character)) return p.On;
        return Settings.IsOnFor(character);
    }

    /// <summary>True when the relay is on for the character logged in here. Any thread.</summary>
    public static bool IsOnHere => IsOn(CurrentCharacter());

    // ── Setup (InitWorker / panel) ───────────────────────────────────────

    /// <summary>Loads the settings once (file I/O: InitWorker or a background thread) and opens the feed if on.</summary>
    public static void EnsureLoaded()
    {
        if (Interlocked.Exchange(ref _loaded, 1) != 0) return;
        ChatDiscordSettings s = ChatDiscordStore.Load(FilePath, out string note);
        Volatile.Write(ref _settings, s);
        Interlocked.Exchange(ref _fileStampTicks, FileStampTicks());
        if (note.Length > 0) RynthLog.UI($"ChatDiscord: {note}.");
        // Subscribing touches UiSources; done on the pump, like every other hub change.
        UiDataHub.Post("chat discord feed", SyncFeed);
    }

    /// <summary>
    /// Turns the relay on or off for the character logged in here (and saves). False before
    /// login: there is no character to switch. Any thread.
    /// </summary>
    public static bool SetEnabled(bool on)
    {
        DiscordCharacter? me = CurrentCharacter();
        if (me == null) return false;
        if (IsOn(me) == on) return true;
        var pending = new SelfPending(me, on);
        Volatile.Write(ref _pending, pending);
        SwitchCharacter(new DiscordCharacterOp(me, on), pending);
        return true;
    }

    /// <summary>Turns the relay off for another character (the panel's list). Any thread.</summary>
    public static void TurnOffFor(DiscordCharacter character)
    {
        DiscordCharacter? me = CurrentCharacter();
        if (character.Same(me)) { SetEnabled(false); return; }
        SwitchCharacter(new DiscordCharacterOp(character, false), null);
    }

    private static void SwitchCharacter(DiscordCharacterOp op, SelfPending? pending)
    {
        var s = Settings;
        s.Characters = ChatDiscordStore.ApplyCharacterOps(s.Characters, new[] { op });
        CharacterOps.Enqueue((op, pending));
        Save();
        SyncFeed();
    }

    /// <summary>The Discord panel opened / closed: it keeps the feed (and so the relay's tick) running. AC thread.</summary>
    public static void PanelShown(bool shown)
    {
        int n = shown ? Interlocked.Increment(ref _panelsOpen) : Interlocked.Decrement(ref _panelsOpen);
        if (n < 0) Interlocked.Exchange(ref _panelsOpen, 0);
        SyncFeed();
    }

    /// <summary>
    /// Holds the chat feed while any character on this PC has the relay on (the pump then decides
    /// for the one logged in, who may only be known after login or a hot reload) or the panel is open.
    /// </summary>
    private static void SyncFeed()
    {
        bool want = !_shutdown
                    && (Settings.Characters.Length > 0 || Volatile.Read(ref _pending) is { On: true }
                        || Volatile.Read(ref _panelsOpen) > 0);
        if (want)
        {
            if (Interlocked.CompareExchange(ref _feedHeld, 1, 0) == 0)
            {
                UiSources.Chat.Subscribe();
                UiSources.Chat.RequestRefresh();
            }
        }
        else if (Interlocked.CompareExchange(ref _feedHeld, 0, 1) == 1)
            UiSources.Chat.Unsubscribe();
    }

    /// <summary>Saves in the background (the JSON is taken now; the character list is merged then). Any thread.</summary>
    public static void Save()
    {
        string json;
        try { json = ChatDiscordStore.Serialize(Settings); }
        catch (Exception ex)
        {
            RynthLog.UI($"ChatDiscord: settings not saved ({ex.GetType().Name}: {ex.Message}).");
            return;
        }
        UiBackgroundWriter.Enqueue("persist chat discord", () => WriteMerged(json));
    }

    /// <summary>
    /// Background writer: re-reads the file, applies this client's queued character switches to
    /// its list, writes our settings with that list, and takes the list back. Other clients'
    /// switches survive; ours win for our own character.
    /// </summary>
    private static void WriteMerged(string ours)
    {
        var ops = new List<(DiscordCharacterOp Op, SelfPending? Pending)>();
        while (CharacterOps.TryDequeue(out var o)) ops.Add(o);
        var plain = new List<DiscordCharacterOp>(ops.Count);
        foreach (var o in ops) plain.Add(o.Op);
        try
        {
            string? disk = null;
            try { if (File.Exists(FilePath)) disk = File.ReadAllText(FilePath); }
            catch (IOException) { }
            string json = ChatDiscordStore.MergeForSave(ours, disk, plain, out DiscordCharacter[] merged);
            ChatDiscordStore.Write(FilePath, json);
            Interlocked.Exchange(ref _fileStampTicks, FileStampTicks());
            Settings.Characters = merged;
        }
        catch
        {
            // Not written: keep the switches for the next save.
            foreach (var o in ops) CharacterOps.Enqueue(o);
            throw;
        }
        // Saved: the file now says what our character asked for (unless it asked again since).
        foreach (var o in ops)
            if (o.Pending != null) Interlocked.CompareExchange(ref _pending, null, o.Pending);
        SyncFeed();
    }

    /// <summary>Pump: every few seconds, re-reads the character list if another client changed the file.</summary>
    private static void CheckFile(long now)
    {
        if (now < Volatile.Read(ref _nextFileCheckMs)) return;
        Volatile.Write(ref _nextFileCheckMs, now + FileCheckMs);
        if (FileStampTicks() == Interlocked.Read(ref _fileStampTicks)) return;
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        // On the writer, so it can't land between a save's read and write.
        UiBackgroundWriter.Enqueue("refresh chat discord characters", () =>
        {
            try
            {
                long stamp = FileStampTicks();
                string? disk = File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
                DiscordCharacter[]? list = ChatDiscordStore.ReadCharacters(disk);
                Interlocked.Exchange(ref _fileStampTicks, stamp);
                // Switches still queued here go in with the next save; keep them in view.
                if (list != null)
                {
                    var queued = new List<DiscordCharacterOp>();
                    foreach (var o in CharacterOps) queued.Add(o.Op);
                    Settings.Characters = ChatDiscordStore.ApplyCharacterOps(list, queued);
                }
            }
            finally { Interlocked.Exchange(ref _refreshQueued, 0); }
            SyncFeed();
        });
    }

    private static long FileStampTicks()
    {
        try { return File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath).Ticks : 0; }
        catch (Exception) { return 0; }
    }

    /// <summary>
    /// Engine shutdown, after the pump stopped: cancels posts in flight, gives them a moment to
    /// unwind (they'd otherwise finish in this dying generation's code), then disposes the HTTP
    /// client so nothing of it keeps the generation from unloading.
    /// </summary>
    public static void Shutdown()
    {
        _shutdown = true;
        try { Cts.Cancel(); } catch { }
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _postsRunning) > 0 && wait.ElapsedMilliseconds < 1000) Thread.Sleep(10);
        int left = Volatile.Read(ref _postsRunning);
        if (left > 0) RynthLog.UI($"ChatDiscord: {left} post(s) still running at shutdown; closing the HTTP client under them.");
        DiscordClient.Shutdown();
        while (Done.TryDequeue(out _)) { }
        try { Cts.Dispose(); } catch { }
    }
    // ── Panel / command requests (any thread) ────────────────────────────

    /// <summary>Clears a pause on <paramref name="route"/> (the Resume button, or a credential change).</summary>
    public static void Resume(DiscordRoute route) => ResumeQueue.Enqueue(route);

    /// <summary>Posts a test line to <paramref name="route"/> (ignoring its filters) and resumes it.</summary>
    public static void SendTest(DiscordRoute route)
    {
        ResumeQueue.Enqueue(route);
        TestQueue.Enqueue(route);
    }

    public static void ClearLog() => _clearLog = true;

    public static DiscordLogEntry[] LogSnapshot() => Volatile.Read(ref _logSnapshot);

    /// <summary>
    /// Stores a pasted webhook URL on <paramref name="route"/>, encrypted on the background writer.
    /// Returns an error for the panel, or empty when accepted. The plain text is not kept.
    /// </summary>
    public static string SetWebhook(DiscordRoute route, string url)
    {
        if (!DiscordFormat.TryNormalizeWebhookUrl(url, out string normalized))
            return "That isn't a Discord webhook URL (https://discord.com/api/webhooks/<id>/<token>).";
        UiBackgroundWriter.Enqueue("protect discord webhook", () =>
        {
            route.WebhookProtected = DiscordSecrets.Protect(normalized);
            Save();
            Resume(route);
        });
        return string.Empty;
    }

    /// <summary>Stores a pasted bot token (encrypted on the background writer). Returns an error, or empty.</summary>
    public static string SetBotToken(DiscordRoute route, string token)
    {
        if (!DiscordFormat.LooksLikeBotToken(token))
            return "That doesn't look like a bot token (Developer Portal > Bot > Reset Token).";
        string trimmed = token.Trim();
        UiBackgroundWriter.Enqueue("protect discord bot token", () =>
        {
            route.BotTokenProtected = DiscordSecrets.Protect(trimmed);
            Save();
            Resume(route);
        });
        return string.Empty;
    }

    // ── Pump ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One read of RynthChat's scrollback (the new lines, already routed by the chat rules; empty
    /// when nothing was new). ChatSource.Poll, pump thread, after every read, so the first read
    /// after the relay comes on can be taken as the baseline (DiscordBacklogGate).
    /// </summary>
    internal static void PumpRead(IReadOnlyList<ChatLine> added)
    {
        if (_shutdown) return;
        var seqs = new ulong[added.Count];
        for (int i = 0; i < seqs.Length; i++) seqs[i] = added[i].Seq;
        bool baseline = !Backlog.Live;
        bool[] post = Backlog.Admit(RelayOn(), seqs);
        if (baseline && Backlog.Live && added.Count > 0)
            RynthLog.UI($"ChatDiscord: {added.Count} line(s) of scrollback not relayed (relay started).");
        if (Array.IndexOf(post, true) < 0) return;

        var s = Settings;
        DiscordRoute[] routes = s.Routes;
        if (routes.Length == 0) return;
        string me = SafeName(ChatModel.PumpCharacterName());

        // Lines reach here within a poll (100 ms) of being printed: now is their time.
        DateTime time = DateTime.Now;
        long nowMs = Environment.TickCount64;
        // Keyword lists parsed once per batch of lines (the panel may edit them between polls).
        var keywords = new List<string>[routes.Length];
        for (int i = 0; i < routes.Length; i++) keywords[i] = DiscordFormat.SplitList(routes[i].Keywords);

        for (int n = 0; n < added.Count; n++)
        {
            if (!post[n]) continue;
            ChatLine line = added[n];
            ChatRoute chatRoute = line.Route;
            if (s.SkipHidden && chatRoute.Hidden) continue;
            if (!ChatDiscordLogic.TryClassify(line.Text, line.ChatType, me, out DiscordLineParts parts)) continue;
            if (parts.Own && s.SkipOwn) continue;

            string channel = line.Channel;
            bool ShowsIn(string tab) => chatRoute.ShowsIn(tab, channel);
            for (int i = 0; i < routes.Length; i++)
            {
                DiscordRoute route = routes[i];
                if (!route.Enabled || !route.HasSecret) continue;
                if (!ChatDiscordLogic.Wants(route, parts.Speech, ShowsIn, out string label)) continue;
                if (!DiscordFormat.MatchesKeywords(parts.Haystack, keywords[i])) continue;

                DiscordRouteQueue q = State(route);
                NoteRetargeted(route, Retarget(q, route));
                if (q.Paused) continue;
                if (!q.NoteFresh(ChatDiscordLogic.DedupKey(parts, label), nowMs)) continue;
                q.Add(ChatDiscordLogic.Format(s, time, parts, label, me), nowMs, s.MaxQueue);
            }
        }
    }

    /// <summary>RynthChat's scrollback numbering restarted (ChatSource.Reset, its plugin reloaded). Pump thread.</summary>
    internal static void PumpSourceReset() => Backlog.SourceReset();

    /// <summary>Applies requests and results, starts due posts, publishes status. ChatSource.Poll, pump thread.</summary>
    internal static void PumpTick()
    {
        if (_shutdown) return;
        long now = Environment.TickCount64;
        var s = Settings;
        DiscordRoute[] routes = s.Routes;
        Volatile.Write(ref _characterName, SafeName(ChatModel.PumpCharacterName()));
        CheckFile(now);
        bool relayOn = RelayOn();
        if (!relayOn) Backlog.Off();

        PruneStates(routes);
        while (ResumeQueue.TryDequeue(out var r))
            if (States.TryGetValue(r, out var q)) q.Resume();
        while (Done.TryDequeue(out var d)) ApplyResult(d.Route, d.Batch, d.Result, now);
        while (TestQueue.TryDequeue(out var t)) QueueTest(t);
        if (_clearLog)
        {
            _clearLog = false;
            Log.Clear();
            Volatile.Write(ref _logSnapshot, Array.Empty<DiscordLogEntry>());
        }

        foreach (var route in routes)
        {
            // Off means off: the relay or the route switched off drops what the route had
            // queued, the batch waiting to retry and the post in flight. Only a test goes.
            if (!States.TryGetValue(route, out var q)) continue;
            if (!(relayOn && route.Enabled)) NoteStopped(route, q.Stop());
            // A new webhook / token / channel: what was queued for the old one isn't sent to it.
            NoteRetargeted(route, Retarget(q, route));
            StartNextPost(s, route, relayOn, now);
        }
        PublishStatus(routes);
    }

    /// <summary>Whether this client relays at all right now: the relay is on for the character logged in.</summary>
    private static bool RelayOn() => IsOnHere;

    private static DiscordRouteQueue State(DiscordRoute route)
    {
        if (!States.TryGetValue(route, out var q)) States[route] = q = new DiscordRouteQueue();
        return q;
    }

    private static void PruneStates(DiscordRoute[] routes)
    {
        if (States.Count <= routes.Length) return;
        var live = new HashSet<DiscordRoute>(routes, ReferenceEqualityComparer.Instance);
        var gone = new List<DiscordRoute>();
        foreach (var r in States.Keys) if (!live.Contains(r)) gone.Add(r);
        foreach (var r in gone)
        {
            // A removed route's post in flight (test or not) is cancelled; its result then finds
            // no queue and is dropped.
            DiscordRouteQueue q = States[r];
            q.Stop();
            if (q.InFlight is { } flying)
            {
                try { flying.Cancel?.Cancel(); } catch (ObjectDisposedException) { }
            }
            States.Remove(r);
        }
    }

    /// <summary>
    /// Tells <paramref name="q"/> where <paramref name="route"/> sends now (its mode, channel and
    /// saved, encrypted credential); a change drops what was queued for the old destination.
    /// </summary>
    private static int Retarget(DiscordRouteQueue q, DiscordRoute route)
        => q.Retarget(route.Mode, route.ChannelId,
            route.Mode == DiscordRoute.ModeBot ? route.BotTokenProtected : route.WebhookProtected);

    private static void NoteRetargeted(DiscordRoute route, int dropped)
    {
        if (dropped > 0) AddLog(Settings, DiscordLogKind.Info, route.Name, $"Destination changed: {dropped} line(s) queued for the old one dropped.");
    }

    private static void NoteStopped(DiscordRoute route, int dropped)
    {
        if (dropped > 0) AddLog(Settings, DiscordLogKind.Info, route.Name, $"Off: {dropped} queued line(s) dropped.");
    }

    private static void QueueTest(DiscordRoute route)
    {
        string me = SafeName(ChatModel.PumpCharacterName());
        string line = DiscordFormat.FormatLine(DateTime.Now, null, null,
            $"RynthChat Discord relay test from {(me.Length > 0 ? me : "RynthCore")} (route \"{route.Name}\").",
            Settings.IncludeTimestamp, null);
        State(route).QueueTest(line);
    }

    private static void StartNextPost(ChatDiscordSettings s, DiscordRoute route, bool relayOn, long now)
    {
        if (!States.TryGetValue(route, out var q)) return;
        DiscordBatch? batch = q.Next(now, relayOn && route.Enabled, s.BatchFor(route), s.IntervalFor(route));
        if (batch == null) return;

        // Decrypt only now, on the way out; the plain text lives in the request task alone.
        string mode = route.Mode, channelId = route.ChannelId;
        string blob = mode == DiscordRoute.ModeBot ? route.BotTokenProtected : route.WebhookProtected;
        if (blob.Length == 0)
        {
            ApplyResult(route, batch, DiscordSendResult.Fail(
                mode == DiscordRoute.ModeBot ? "no bot token saved" : "no webhook URL saved", permanent: true), now);
            return;
        }
        if (!DiscordSecrets.TryUnprotect(blob, out string secret))
        {
            ApplyResult(route, batch, DiscordSendResult.Fail(
                "the saved credential can't be read on this Windows user; paste it again", permanent: true), now);
            return;
        }
        string displayName = route.DisplayName.Length > 0 ? route.DisplayName : SafeName(ChatModel.PumpCharacterName());

        // Each post can be cancelled on its own (Stop, a removed route) or with the engine.
        CancellationTokenSource cts;
        try { cts = CancellationTokenSource.CreateLinkedTokenSource(Cts.Token); }
        catch (ObjectDisposedException) { return; }   // shutting down
        batch.Cancel = cts;
        Interlocked.Increment(ref _postsRunning);
        _ = Task.Run(async () =>
        {
            DiscordSendResult r;
            try
            {
                r = mode == DiscordRoute.ModeBot
                    ? await DiscordClient.PostBotAsync(secret, channelId, batch.Content, cts.Token).ConfigureAwait(false)
                    : await DiscordClient.PostWebhookAsync(secret, batch.Content, displayName, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { r = DiscordSendResult.Fail(DiscordFormat.Redact(ex.Message, secret)); }
            finally
            {
                batch.Cancel = null;
                cts.Dispose();
                Interlocked.Decrement(ref _postsRunning);
            }
            Done.Enqueue((route, batch, r));
        });
    }

    private static void ApplyResult(DiscordRoute route, DiscordBatch batch, DiscordSendResult r, long now)
    {
        // A route removed meanwhile has no queue: nothing to retry, nothing to report.
        if (!States.TryGetValue(route, out var q)) return;
        var s = Settings;
        switch (q.Apply(batch, r, now, s.IntervalFor(route)))
        {
            case DiscordOutcome.Sent:
                AddLog(s, DiscordLogKind.Sent, route.Name, batch.IsTest ? "Test message sent." : $"{batch.Lines.Count} line(s): {batch.Content}");
                break;
            case DiscordOutcome.Paused:
                AddLog(s, DiscordLogKind.Error, route.Name, $"Paused: {r.Error}");
                RynthLog.UI($"ChatDiscord: route '{route.Name}' paused: {r.Error}");
                break;
            case DiscordOutcome.RateLimited:
                AddLog(s, DiscordLogKind.Info, route.Name, r.Error);
                break;
            case DiscordOutcome.GaveUp:
                AddLog(s, DiscordLogKind.Error, route.Name, $"Gave up after {batch.Attempts} tries ({r.Error}); {batch.Lines.Count} line(s) dropped.");
                RynthLog.UI($"ChatDiscord: route '{route.Name}' dropped a batch: {r.Error}");
                break;
            case DiscordOutcome.Retrying:
                AddLog(s, DiscordLogKind.Error, route.Name, $"{r.Error}; retrying in {q.BackoffMs / 1000}s.");
                break;
            case DiscordOutcome.Retargeted:
                AddLog(s, DiscordLogKind.Info, route.Name, $"{r.Error}; the destination changed meanwhile, so {batch.Lines.Count} line(s) dropped.");
                break;
        }
    }

    private static void PublishStatus(DiscordRoute[] routes)
    {
        foreach (var route in routes)
        {
            if (!States.TryGetValue(route, out var q)) continue;
            int queued = q.Queued;
            DiscordRouteStatus? old = route.Status;
            if (old != null && old.Queued == queued && old.MessagesSent == q.MessagesSent && old.Dropped == q.Dropped
                && old.Paused == q.Paused && ReferenceEquals(old.LastError, q.LastError) && old.LastSent == q.LastSent)
                continue;
            route.Status = new DiscordRouteStatus(queued, q.MessagesSent, q.LinesSent, q.Dropped, q.Paused, q.LastError, q.LastSent);
        }
    }

    private static void AddLog(ChatDiscordSettings s, DiscordLogKind kind, string route, string text)
    {
        Log.Add(new DiscordLogEntry(DateTime.Now, kind, route, text));
        if (Log.Count > s.MaxLog) Log.RemoveRange(0, Log.Count - s.MaxLog);
        Volatile.Write(ref _logSnapshot, Log.ToArray());
    }

    private static string SafeName(string? name) => (name ?? string.Empty).Trim().TrimStart('+');

    // ── Status and /rc discord ───────────────────────────────────────────

    /// <summary>"2 of 3 route(s) ready." Any thread.</summary>
    public static string StatusLine()
    {
        var s = Settings;
        int ready = 0;
        foreach (var r in s.Routes) if (r.Enabled && r.HasSecret) ready++;
        return $"{ready} of {s.Routes.Length} route(s) ready.";
    }

    /// <summary>"On for Bob (Aelrynth)." / "Off for Bob (Aelrynth)." / "Log in to switch it on." Any thread.</summary>
    public static string SwitchLine()
    {
        DiscordCharacter? me = CurrentCharacter();
        if (me == null) return "Log in to switch it on for a character.";
        return $"{(IsOn(me) ? "On" : "Off")} for {me}.";
    }

    /// <summary>
    /// /rc discord [on|off|status|test [route]|resume [route]]. Returns the reply lines ("window"
    /// is handled by the caller: it opens the panel). AC's thread.
    /// </summary>
    public static List<string> HandleCommand(string args)
    {
        var reply = new List<string>();
        var s = Settings;
        string[] parts = (args ?? string.Empty).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "status";
        string rest = parts.Length > 1 ? parts[1] : string.Empty;
        switch (verb)
        {
            case "on":
            case "off":
                if (!SetEnabled(verb == "on"))
                {
                    reply.Add("Log in first: the relay is switched on or off for the character you're playing.");
                    break;
                }
                reply.Add($"Discord relay {verb} for {CurrentCharacter()}. {StatusLine()}");
                break;
            case "test":
            case "resume":
                // The relay's tick runs while the chat feed is held (a character on, or the panel open).
                if (verb == "test" && Volatile.Read(ref _feedHeld) == 0)
                {
                    reply.Add("The relay is off: /rc discord on, or use Send test in the Discord panel (/rc discord window).");
                    break;
                }
                int n = 0;
                foreach (var r in s.Routes)
                {
                    if (rest.Length > 0 && !r.Name.Equals(rest, StringComparison.OrdinalIgnoreCase)) continue;
                    if (verb == "test") SendTest(r); else Resume(r);
                    n++;
                }
                reply.Add(n == 0
                    ? $"No route{(rest.Length > 0 ? $" named '{rest}'" : "s")}. Add one in Chat > gear > Discord (or /rc discord window)."
                    : $"{(verb == "test" ? "Test sent to" : "Resumed")} {n} route(s).");
                break;
            case "status":
                reply.Add($"Discord relay: {SwitchLine()} {StatusLine()}");
                if (s.Characters.Length > 0)
                    reply.Add("  On for: " + string.Join(", ", Array.ConvertAll(s.Characters, c => c.ToString())));
                foreach (var r in s.Routes)
                {
                    var st = r.Status;
                    reply.Add($"  {r.Name}: {(r.Enabled ? "on" : "off")}, {(r.HasSecret ? r.Mode : "no destination set")}"
                              + (st == null ? string.Empty
                                  : $", {st.MessagesSent} sent, {st.Queued} queued, {st.Dropped} dropped{(st.Paused ? ", PAUSED" : string.Empty)}"
                                    + (st.LastError.Length > 0 ? $" - {st.LastError}" : string.Empty)));
                }
                reply.Add("/rc discord on|off (this character) | window | test [route] | resume [route] | status");
                break;
            default:
                // A word it doesn't take: the usage, not the status (every command answers a typo so).
                reply.Add("Usage: /rc discord [on|off|status|window|test [route]|resume [route]]");
                break;
        }
        return reply;
    }
}

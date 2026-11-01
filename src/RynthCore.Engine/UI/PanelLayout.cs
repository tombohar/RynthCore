// ============================================================================
//  RynthCore.Engine - UI/PanelLayout.cs
//  Panel layout per character (PanelLayoutPolicy.cs has the rules and names):
//  which layout's files this client uses, the switch between them, and the
//  engine's own saving of imgui.ini.
//
//  Switching. Tick (EndScene, AC's render thread, every frame) works out the
//  layout the client should use: the PC-wide default until a character is in
//  the world, then that character's (SessionStateRegistry's server and name,
//  known straight after LoginComplete, which a hot reload marks at engine init).
//  A switch then runs in three steps:
//    1. render thread: snapshot the ImGui windows of the layout being left;
//    2. UI writer (UiBackgroundWriter, in order with every other UI save): write
//       out the layout being left when it is a character's, seed a new
//       character's files from the default, PanelStateStore.SwitchTo the new
//       panel_state.txt, read the new imgui.ini;
//    3. render thread, before any panel draws: LoadIniSettingsFromMemory (Dear
//       ImGui 1.91.6 applies loaded settings to windows that already exist,
//       WindowSettingsHandler_ApplyAll), and the bar and the open panels take
//       their places from the new store (ImGuiBar / ImGuiPanelHost.OnLayoutSwitched).
//  After a login, panels draw only once step 3 is done for that login
//  (ReadyToDraw, EngineFrameController.PanelsLive): never first at the default
//  places and then a jump.
//
//  imgui.ini. The context's IniFilename stays null (EngineFrameController.Init):
//  the engine loads the file of the layout in use (step 3) and saves it when
//  Dear ImGui asks (io.WantSaveIniSettings), at a switch and at shutdown, off
//  the render thread and atomically, always to the file of the layout the
//  context holds.
//
//  The default pair is written by clients that use it in the world as before
//  (setting off, no character name) and by /rc layout default; not by a switch,
//  not at character select with the setting on, and never with the windows of a
//  character that logged out (PanelLayoutPolicy.MayAutoSaveIni).
// ============================================================================

using System;
using System.IO;
using System.Threading;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI;

internal static class PanelLayout
{
    private static readonly PanelLayoutSession Session = new();
    private static readonly LatestTextWrites IniWrites = new(PanelLayoutFiles.WriteText);
    private static volatile bool _initialized;

    /// <summary>A finished step 2, waiting for the render thread.</summary>
    private sealed record Loaded(LayoutSwitch Switch, LayoutFiles Files, string? IniText, string? Error, Action<string?>? After);
    private static Loaded? _loaded;   // written by the UI writer, read by the render thread

    // Render thread: the layout applied (step 3; one reference, so other threads read it whole)
    // and the ImGui windows' text for it.
    private static Loaded? _applied;
    private static string? _activeIniText;
    private static bool _contextHeldCharacter;   // the ImGui context has had a character's windows in it
    private static LayoutOwner? _placedFor;      // the layout the open panels were last placed from
    private static bool _placedForSet;

    /// <summary>The character whose layout is applied; null = the default (or none yet).</summary>
    private static LayoutOwner? Active => Volatile.Read(ref _applied)?.Switch.To;

    // The identity Tick last read (SessionStateRegistry's strings), so it isn't rebuilt every frame.
    private static string? _idServer, _idCharacter;
    private static LayoutOwner? _idOwner;

    /// <summary>An /rc layout default or reset waiting for the render thread (one at a time).</summary>
    private sealed record PendingOp(LayoutCommand Kind, Action<string, bool> Reply);
    private static PendingOp? _pendingOp;
    private static Action<string, bool>? _resetReply;   // render thread: the reset switch's answer

    /// <summary>True once the engine runs layouts (Initialize). Until then imgui.ini is Dear ImGui's own, as before.</summary>
    public static bool Initialized => _initialized;

    private static string LocalRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore");

    /// <summary>The PC-wide default imgui.ini (the file every client used before).</summary>
    internal static string DefaultIniPath => Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, PanelLayoutPolicy.ImGuiIniFileName);

    private static LayoutFiles FilesFor(LayoutOwner? who) => PanelLayoutPolicy.FilesFor(LocalRoot, DefaultIniPath, who);

    /// <summary>Engine init (InitWorker), before the EndScene hook goes in. From here the engine owns imgui.ini.</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        RynthLog.UI($"PanelLayout: per character {(EngineSettings.PanelLayoutPerCharacter ? "on" : "off")}; layouts under {Path.Combine(LocalRoot, PanelLayoutPolicy.LayoutsFolder)}.");
    }

    private static bool InWorld => LoginLifecycleHooks.HasObservedLoginComplete;

    private static LayoutOwner? Identity()
    {
        string server = SessionStateRegistry.LastServerName, character = SessionStateRegistry.LastCharacterName;
        if (!ReferenceEquals(server, _idServer) || !ReferenceEquals(character, _idCharacter))
        {
            _idServer = server;
            _idCharacter = character;
            _idOwner = PanelLayoutPolicy.Owner(server, character);
        }
        return _idOwner;
    }

    /// <summary>
    /// True when panels may draw (EngineFrameController.PanelsLive): after a login, only once that
    /// login's layout is in place. Always true before Initialize. Render thread.
    /// </summary>
    public static bool ReadyToDraw =>
        !_initialized || Session.ReadyToDraw(Environment.TickCount64, InWorld, LoginLifecycleHooks.LoginCompleteAtMs);

    // ── the frame (render thread) ─────────────────────────────────────────

    /// <summary>
    /// Once a frame, from EndScene (render thread, outside the ImGui frame). <paramref name="imguiInUse"/>:
    /// the ImGui layer is on and didn't fail, so a layout waits for its context; <paramref name="imguiReady"/>:
    /// the context exists. <paramref name="snapshotIni"/> / <paramref name="loadIni"/> run on it.
    /// </summary>
    public static void Tick(bool imguiInUse, bool imguiReady, Func<string?> snapshotIni, Action<string> loadIni)
    {
        if (!_initialized) return;
        try
        {
            ApplyLoaded(imguiInUse, imguiReady, loadIni);

            if (!Session.InFlight && Volatile.Read(ref _pendingOp) != null && Interlocked.Exchange(ref _pendingOp, null) is { } op)
                RunOp(op, imguiReady ? snapshotIni : null);

            LayoutSwitch? sw = Session.Tick(Environment.TickCount64, EngineSettings.PanelLayoutPerCharacter, InWorld,
                LoginLifecycleHooks.LoginCompleteAtMs, SessionStateRegistry.LoginRecorded, Identity());
            if (sw is { } s)
                StartSwitch(s, s.FlushFrom && imguiReady ? snapshotIni() : null);
            if (_resetReply is { } unanswered)
            {
                // The reset asked for this frame didn't become a switch: the layout in use changed first.
                _resetReply = null;
                unanswered(PanelLayoutPolicy.FailedReply("reset the panel layout", "the layout in use changed first; try again"), false);
            }
        }
        catch (Exception ex)
        {
            RynthLog.UI($"PanelLayout: tick threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Step 1: the switch goes to the UI writer, with the windows of the layout being left.</summary>
    private static void StartSwitch(LayoutSwitch sw, string? fromIni)
    {
        LayoutFiles from = FilesFor(sw.From), to = FilesFor(sw.To);
        if (sw.FlushFrom && fromIni != null) IniWrites.Queue(from.ImGuiIni, fromIni);
        Action<string?>? after = null;
        if (sw.Reseed && _resetReply is { } reply && sw.To is { } who)
        {
            _resetReply = null;
            after = error => reply(error == null ? PanelLayoutPolicy.ResetDoneReply(who)
                : PanelLayoutPolicy.FailedReply("reset " + PanelLayoutPolicy.Describe(who) + "'s panel layout", error), error == null);
        }
        UiBackgroundWriter.Enqueue("panel layout switch", () => LoadOnWriter(sw, from, to, after));
    }

    /// <summary>Step 2 (UI writer): write out what is left, seed or reseed, switch the store, read the new imgui.ini.</summary>
    private static void LoadOnWriter(LayoutSwitch sw, LayoutFiles from, LayoutFiles to, Action<string?>? after)
    {
        string? ini = null, error = null;
        string seeded = string.Empty;
        try
        {
            if (sw.FlushFrom) IniWrites.Flush(from.ImGuiIni);
        }
        catch (Exception ex)
        {
            RynthLog.UI($"PanelLayout: writing out {from.ImGuiIni} failed - {ex.Message}");
        }
        try
        {
            if (sw.To != null)
            {
                LayoutFiles defaults = FilesFor(null);
                if (sw.Reseed)
                {
                    PanelLayoutFiles.CopyOver(defaults, to);
                    seeded = " (reset from the default)";
                }
                else
                {
                    var copied = PanelLayoutFiles.SeedMissing(defaults, to);
                    if (copied.PanelState || copied.Ini)
                        seeded = " (new: " + (copied.PanelState && copied.Ini ? "both files" : copied.PanelState ? "panel_state.txt" : "imgui.ini")
                            + " copied from the default)";
                }
            }
        }
        catch (Exception ex)
        {
            // The switch still happens: the character's files are made by its first save.
            error = ex.Message;
        }
        // Never throws (it logs a file it can't read and starts empty).
        PanelStateStore.SwitchTo(to.PanelState, flushCurrent: sw.FlushFrom);
        try { ini = PanelLayoutFiles.ReadText(to.ImGuiIni); }
        catch (Exception ex) { error ??= ex.Message; }

        string who = sw.To is { } owner ? PanelLayoutPolicy.Describe(owner) + "'s" : "the PC-wide default";
        if (error == null) RynthLog.UI($"PanelLayout: switched to {who} layout{seeded}: {to.PanelState}, {to.ImGuiIni}.");
        else RynthLog.Warn($"PanelLayout: switched to {who} layout ({to.PanelState}), but its files had a problem - {error}.");
        Volatile.Write(ref _loaded, new Loaded(sw, to, ini, error, after));
        try { after?.Invoke(error); } catch { }
    }

    /// <summary>Step 3 (render thread): the loaded layout goes into the ImGui context, the bar and the open panels.</summary>
    private static void ApplyLoaded(bool imguiInUse, bool imguiReady, Action<string> loadIni)
    {
        Loaded? l = Volatile.Read(ref _loaded);
        if (l == null || ReferenceEquals(l, _applied)) return;
        if (imguiInUse && !imguiReady) return;   // the context is made this frame: apply on the next
        if (imguiReady && !string.IsNullOrEmpty(l.IniText))
        {
            try { loadIni(l.IniText); }
            catch (Exception ex) { RynthLog.UI($"PanelLayout: loading {l.Files.ImGuiIni} threw {ex.GetType().Name}: {ex.Message}"); }
        }
        Volatile.Write(ref _applied, l);
        _activeIniText = l.IniText;
        if (l.Switch.To != null) _contextHeldCharacter = true;
        Session.MarkApplied(l.Switch.Gen);

        // A reset places the panels again even though the layout's owner is the same.
        if (InWorld && (l.Switch.Reseed || !_placedForSet || !Equals(l.Switch.To, _placedFor)))
        {
            // At character select nothing draws: the panels are placed when a character comes in.
            _placedFor = l.Switch.To;
            _placedForSet = true;
            ImGuiBackend.ImGuiBar.OnLayoutSwitched();
            ImGuiBackend.ImGuiPanelHost.OnLayoutSwitched();
        }
    }

    /// <summary>
    /// The ImGui context was just made (EngineFrameController.Init, context current, before its first
    /// frame): it starts with the windows of the layout in use, if one is applied already.
    /// </summary>
    public static void OnContextCreated(Action<string> loadIni)
    {
        if (!_initialized || _applied == null || string.IsNullOrEmpty(_activeIniText)) return;
        try { loadIni(_activeIniText); }
        catch (Exception ex) { RynthLog.UI($"PanelLayout: loading the layout into the new context threw {ex.GetType().Name}: {ex.Message}"); }
    }

    // ── saving imgui.ini ──────────────────────────────────────────────────

    /// <summary>
    /// The ImGui windows' text to save (render thread): Dear ImGui's dirty timer (io.WantSaveIniSettings),
    /// or <paramref name="explicitSave"/> for Save Layout Now. Written in the background, atomically, to
    /// the file of the layout the context holds.
    /// </summary>
    public static void QueueIniSave(string text, bool explicitSave)
    {
        if (!_initialized)
        {
            // Dear ImGui owns the file (IniFilename pinned): Save Layout Now writes the default, as before.
            if (explicitSave) UiBackgroundWriter.Enqueue("imgui.ini", () => PanelLayoutFiles.WriteText(DefaultIniPath, text));
            return;
        }
        if (!MaySave(explicitSave, out string path)) return;
        _activeIniText = text;
        IniWrites.Queue(path, text);
        UiBackgroundWriter.Enqueue("imgui.ini", () =>
        {
            try { IniWrites.Flush(path); }
            catch (Exception ex) { RynthLog.UI($"PanelLayout: saving {path} failed - {ex.Message}"); }
        });
    }

    /// <summary>The ImGui context is about to go (engine shutdown, a hot reload): its windows are written now.</summary>
    public static void SaveIniOnShutdown(string text)
    {
        if (!_initialized || !MaySave(explicitSave: false, out string path)) return;
        try
        {
            IniWrites.Queue(path, text);
            IniWrites.Flush(path);
        }
        catch (Exception ex)
        {
            RynthLog.UI($"PanelLayout: saving {path} at shutdown failed - {ex.Message}");
        }
    }

    private static bool MaySave(bool explicitSave, out string path)
    {
        path = string.Empty;
        // Nothing applied yet: the context holds no file's windows, and saving it would empty one.
        if (Volatile.Read(ref _applied) is not { } applied) return false;
        if (!explicitSave && !PanelLayoutPolicy.MayAutoSaveIni(applied.Switch.To != null, EngineSettings.PanelLayoutPerCharacter, InWorld, _contextHeldCharacter))
            return false;
        path = applied.Files.ImGuiIni;
        return true;
    }

    // ── /rc layout and Settings ───────────────────────────────────────────

    /// <summary>/rc layout: the layout in use, its files, and the default's. Any thread (racy reads).</summary>
    public static string[] DescribeLines()
    {
        LayoutFiles defaults = FilesFor(null);
        if (!_initialized)
            return PanelLayoutPolicy.StatusLines(null, defaults, DefaultReason.NotRunning, defaults, false);
        bool perCharacter = EngineSettings.PanelLayoutPerCharacter;
        Loaded? applied = Volatile.Read(ref _applied);
        LayoutOwner? active = applied?.Switch.To;
        LayoutFiles files = applied?.Files ?? defaults;
        DefaultReason why = DefaultReason.None;
        if (active == null)
        {
            if (!perCharacter) why = DefaultReason.SettingOff;
            else if (!InWorld) why = DefaultReason.NotInWorld;
            else if (!Session.Decided) why = DefaultReason.WaitingForName;
            else if (Session.Wanted == null) why = DefaultReason.NoCharacterName;
        }
        return PanelLayoutPolicy.StatusLines(active, files, why, defaults, perCharacter);
    }

    // Settings > Display's line, built again only when what it shows changed (it draws every frame).
    private static Loaded? _lineFor;
    private static int _lineFlags = -1;
    private static string _line = string.Empty;

    /// <summary>Settings > Display: whose layout this client uses and where its files are. Render thread.</summary>
    public static string SettingsLine()
    {
        bool perCharacter = EngineSettings.PanelLayoutPerCharacter;
        Loaded? applied = Volatile.Read(ref _applied);
        int flags = (perCharacter ? 1 : 0) | (InWorld ? 2 : 0) | (Session.Decided ? 4 : 0) | (Session.Wanted != null ? 8 : 0) | (_initialized ? 16 : 0);
        if (ReferenceEquals(applied, _lineFor) && flags == _lineFlags) return _line;
        _lineFor = applied;
        _lineFlags = flags;
        LayoutFiles defaults = FilesFor(null);
        LayoutOwner? active = applied?.Switch.To;
        DefaultReason why = DefaultReason.None;
        if (!_initialized) why = DefaultReason.NotRunning;
        else if (active == null)
        {
            if (!perCharacter) why = DefaultReason.SettingOff;
            else if (!InWorld) why = DefaultReason.NotInWorld;
            else if (!Session.Decided) why = DefaultReason.WaitingForName;
            else if (Session.Wanted == null) why = DefaultReason.NoCharacterName;
        }
        _line = PanelLayoutPolicy.SettingsLine(active, applied?.Files ?? defaults, why);
        return _line;
    }

    /// <summary>Settings > Display's reset button: there is a character's own layout to reset.</summary>
    public static bool CanResetFromSettings => _initialized && Active != null;

    /// <summary>Settings > Display's reset confirmation text.</summary>
    public static string ResetConfirmText() => PanelLayoutPolicy.ResetConfirmText(Active);

    /// <summary>Settings > Display "Reset this character's layout" (confirmed): /rc layout reset confirm, answered in chat.</summary>
    public static void ResetFromSettings()
    {
        static void Chat(string text, bool ok) =>
            AcMainThreadQueue.EnqueueWriteToChat("[RynthCore] " + text, ok ? 1 : 0x1A);
        string? problem = Request(LayoutCommand.ResetConfirm, Chat);
        if (problem != null) Chat(problem, false);
    }

    /// <summary>
    /// /rc layout default or reset: null when it can run, else why not (with the usage). Reset without
    /// confirm only answers what it would do (<paramref name="ask"/>). Any thread.
    /// </summary>
    public static string? Check(LayoutCommand kind, out string ask)
    {
        ask = string.Empty;
        string verb = kind == LayoutCommand.MakeDefault ? "make a default from this client" : "reset the panel layout";
        LayoutOwner? active = Active;
        string? problem = PanelLayoutPolicy.CharacterLayoutProblem(verb, _initialized, EngineSettings.PanelLayoutPerCharacter,
            InWorld, active, Session.InFlight);
        if (problem == null && kind == LayoutCommand.ResetAsk && active != null)
            ask = PanelLayoutPolicy.ResetAskReply(active, FilesFor(null));
        return problem;
    }

    /// <summary>
    /// /rc layout default or reset confirm: queued for the render thread; <paramref name="reply"/> gets the
    /// result (text, ok) from the UI writer when it is done. Null when queued, else why not. Any thread.
    /// </summary>
    public static string? Request(LayoutCommand kind, Action<string, bool> reply)
    {
        string? problem = Check(kind, out _);
        if (problem != null) return problem;
        if (Interlocked.CompareExchange(ref _pendingOp, new PendingOp(kind, reply), null) != null)
            return PanelLayoutPolicy.BusyReply;
        return null;
    }

    /// <summary>Settings > Display "Layout for New Characters": /rc layout default, answered in chat.</summary>
    public static void MakeDefaultFromSettings()
    {
        static void Chat(string text, bool ok) =>
            AcMainThreadQueue.EnqueueWriteToChat("[RynthCore] " + text, ok ? 1 : 0x1A);
        string? problem = Request(LayoutCommand.MakeDefault, Chat);
        if (problem != null) Chat(problem, false);
    }

    /// <summary>Runs a queued /rc layout op (render thread, no switch in flight).</summary>
    private static void RunOp(PendingOp op, Func<string?>? snapshotIni)
    {
        Loaded? applied = Volatile.Read(ref _applied);
        if (applied?.Switch.To is not { } who)
        {
            // The layout changed between the command and this frame (a logout, the setting).
            op.Reply(PanelLayoutPolicy.CharacterLayoutProblem("change the panel layout", true, EngineSettings.PanelLayoutPerCharacter,
                InWorld, null, false) ?? PanelLayoutPolicy.FailedReply("change the panel layout", "no character layout is in use"), false);
            return;
        }
        LayoutFiles files = applied.Files;
        if (op.Kind == LayoutCommand.ResetConfirm)
        {
            // The next switch (this frame) copies the default over this character's files and
            // places the panels from it; it answers when its files are done.
            _resetReply = op.Reply;
            Session.WantReseed();
            return;
        }

        // Make default: this character's windows as they are now (saved to its own file too), then
        // the panel store's text, both written as the default, in order after every save queued so far.
        string? ini = snapshotIni?.Invoke();
        if (ini != null)
        {
            _activeIniText = ini;
            IniWrites.Queue(files.ImGuiIni, ini);
        }
        LayoutFiles defaults = FilesFor(null);
        UiBackgroundWriter.Enqueue("panel layout default", () =>
        {
            try
            {
                IniWrites.Flush(files.ImGuiIni);
                PanelLayoutFiles.MakeDefault(files, defaults, PanelStateStore.Snapshot(), ini);
                RynthLog.UI($"PanelLayout: {PanelLayoutPolicy.Describe(who)}'s layout is now the default ({defaults.PanelState}, {defaults.ImGuiIni}).");
                op.Reply(PanelLayoutPolicy.MakeDefaultDoneReply(who, defaults), true);
            }
            catch (Exception ex)
            {
                op.Reply(PanelLayoutPolicy.FailedReply("make the default", ex.Message), false);
            }
        });
    }
}

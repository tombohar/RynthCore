// ============================================================================
//  RynthCore.Engine - D3D9/VideoPatch.cs
//  The "video patch" (2026-10-05): stop the client drawing its 3D world (and,
//  in "all", AC's 2D UI too) to save CPU and GPU on background clients. Game
//  logic, physics, networking and the plugins keep running: only drawing stops.
//  Written from scratch from AC's own frame (no third-party code).
//
//  Where (addresses checked offline against our client, both copies; report in
//  ops/overnight/2026-10-05-video-patch.md, Aeshnidae):
//    Client::UseTime (0x00411FA0) runs the frame: ... SmartBox::UseTime (physics
//    and object updates) -> SceneTool::StartFrame (Clear to black + BeginScene)
//    -> SmartBox::Draw -> SceneTool::EndFrame (RenderUI::RenderObjects = AC's 2D
//    UI, the debug HUDs, EndScene, Present) -> Device::DoFrameSleep.
//    SmartBox::Draw (0x00455610) tail-jumps to SmartBox::DrawNoBlit (0x00454CC0),
//    which sets the normal mode, updates the camera (SmartBox::update_viewer),
//    calls SmartBox::RenderNormalMode (0x00453B40) when there is a viewer cell,
//    then hands the mouse-pick result to the selection handler.
//    RenderNormalMode is the whole 3D scene: FOV / view distance, the landscape
//    or the cell's portal walk, objects, particles (RenderDevice vtable +0x48),
//    then D3DPolyRender::FlushAlphaList(0) (the alpha-sorted foliage/effects)
//    and two SmartBox callbacks: the targeting callback (+0x100, reports the
//    selected object's on-screen box) and a post-render callback (+0x114).
//    __thiscall, no stack arguments, plain `ret`: the detour returns without
//    calling the original to skip the scene. update_viewer, the pick handling
//    and everything outside SmartBox::Draw still run; the back buffer is still
//    cleared by StartFrame, so the window shows black behind AC's UI.
//  "All" also skips RenderUI::RenderObjects (AcUiPassHook asks SkipAcUiThisFrame).
//
//  Safety:
//    - A hook, not a byte patch: "off" (or the kill switch) is instant, and the
//      detour only ever decides "call AC's original or not".
//    - Kill switch: it is a UI hook slot named "VideoPatch" (UiHookRegistry):
//      engine.json DisabledUiHooks ["VideoPatch"] keeps it out at the next start,
//      /rc hooks off VideoPatch makes it a pass-through now.
//    - Pattern first (HookResolver; tools\pe_pattern.py CHECK covers it), Decal
//      guard (UiHookRegistry.TryInstall). Unresolved: logged once, and /rc video
//      answers "unavailable".
//    - Only in the world (PanelsLive's rule): login, character select and client
//      teardown always draw. "All" never saves; a restart or an engine reload
//      comes back with the character's saved mode (off/unfocused/world).
//    - While the world is skipped, EndSceneHook caps the frame rate (FrameCapFps,
//      default 15) so the freed time is slept, not spun.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.D3D9;

internal static unsafe class VideoPatch
{
    // SmartBox::RenderNormalMode on the 4,841,472-byte client. Pattern is the source of truth.
    private const int RenderNormalModeFallbackVa = 0x00453B40;

    // mov eax,[render device global]; sub esp,14h; push esi; mov esi,ecx;
    // mov cl,[eax+0ACh]; test cl,cl; jz far (skip to FlushAlphaList); mov ecx,[esi+0Ch];
    // and ecx,0FFFFh; push ebx; cmp ecx,100h (cell id < 0x100 = outdoors); push edi;
    // sbb edi,edi; neg edi; jnz +11h. Unique in the scanned window on both client copies
    // (C:\Turbine and C:\Games\RynthCore\AcClient, 2026-10-05); the global and the jz
    // target are wildcarded.
    private static readonly byte?[] RenderNormalModePattern =
    [
        0xA1, null, null, null, null,
        0x83, 0xEC, 0x14,
        0x56,
        0x8B, 0xF1,
        0x8A, 0x88, 0xAC, 0x00, 0x00, 0x00,
        0x84, 0xC9,
        0x0F, 0x84, null, null, null, null,
        0x8B, 0x4E, 0x0C,
        0x81, 0xE1, 0xFF, 0xFF, 0x00, 0x00,
        0x53,
        0x81, 0xF9, 0x00, 0x01, 0x00, 0x00,
        0x57,
        0x1B, 0xFF,
        0xF7, 0xDF,
        0x75, 0x11,
    ];

    private static readonly UiHookSlot Slot =
        UiHookRegistry.Register("VideoPatch", "SmartBox::RenderNormalMode - the 3D scene (video patch)", RenderNormalModeFallbackVa);

    private static IntPtr _origRenderNormalMode;
    private static int _initialized;

    /// <summary>The hook is live (cleared first on shutdown, so the detour passes through).</summary>
    public static volatile bool IsInstalled;

    // ── Mode and settings (any thread) ──────────────────────────────────

    private static volatile int _mode;                                   // VideoPatchMode
    private static volatile int _capFps = VideoPatchPolicy.DefaultFrameCapFps;
    private static readonly object FileSync = new();
    private static VideoPatchPolicy.FileModel? _file;                     // under FileSync
    private static string _loadedKey = string.Empty;                     // heartbeat thread

    public static VideoPatchMode Mode => (VideoPatchMode)_mode;
    public static int FrameCapFps { get { EnsureFileLoaded(); return _capFps; } }

    // ── Per-frame decision (AC's render thread) ─────────────────────────

    private static int _decidedFrame = -1;
    private static long _decidedAt;
    private static VideoPatchDecision _frame;
    private static readonly long DecisionMaxAge = Stopwatch.Frequency / 10;   // 100 ms
    private static readonly int OwnPid = Environment.ProcessId;

    // ── Counters (render thread writes; heartbeat reads) ────────────────

    private static long _scenesSkipped, _scenesDrawn, _uiSkipped;
    private static long _worldTicks;
    private static int _worldFrames;
    private static long _worldWindowStart;
    /// <summary>AC's 3D scene cost per drawn frame, ms, over the last second (0 while skipped).</summary>
    private static volatile float _worldMs;

    // ── Measurement (heartbeat thread) ──────────────────────────────────

    private const int RingSize = 5;
    private static readonly float[] CpuRing = new float[RingSize], DrawRing = new float[RingSize], WorldRing = new float[RingSize];
    private static int _ringCount, _ringPos;
    private static long _lastCpu100ns, _lastWallTicks;
    private static float _lastCpuPct = -1;
    private static long _lastSkipCount;
    private static long _hbTick;
    private static volatile string? _pendingChange;      // "off -> world": the heartbeat logs "before" and arms "after"
    private static long _afterDueTick;
    private static string _afterLabel = string.Empty;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    // ════════════════════════════════════════════════════════════════════
    //  Install / uninstall
    // ════════════════════════════════════════════════════════════════════

    /// <summary>InitWorker step. Resolves and hooks SmartBox::RenderNormalMode. Idempotent.</summary>
    public static void Initialize()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
        {
            Slot.Reason = "acclient.exe not available";
            RynthLog.Compat("VideoPatch: acclient.exe not available - unavailable.");
            return;
        }
        if (!UiHookRegistry.SwitchedOn(Slot))
            return;

        HookResolver.ResolveResult r = HookResolver.Resolve(text, "VideoPatch.SmartBox::RenderNormalMode", RenderNormalModePattern, RenderNormalModeFallbackVa);
        delegate* unmanaged[Thiscall]<IntPtr, void> d = &RenderNormalModeDetour;
        if (UiHookRegistry.TryInstall(Slot, r, (IntPtr)d, out IntPtr t))
        {
            _origRenderNormalMode = t;
            Thread.MemoryBarrier();
            if (UiHookRegistry.Enable(Slot))
                IsInstalled = true;
        }
        RynthLog.Compat(IsInstalled
            ? $"VideoPatch: ready (SmartBox::RenderNormalMode @ 0x{Slot.Address.ToInt32():X8} via {Slot.Source}); mode stays off until /rc video or Settings."
            : $"VideoPatch: unavailable - {Slot.Reason}.");
    }

    /// <summary>
    /// Engine shutdown / hot reload (lifecycle thread), with AcUiPassHook.Uninstall: the
    /// detour passes straight to AC from here on, then the hook is disabled. Idempotent.
    /// </summary>
    public static void Uninstall()
    {
        if (!IsInstalled) return;
        IsInstalled = false;
        Thread.MemoryBarrier();
        int status = Hooking.MinHook.MH_DisableHook(Slot.Address);
        RynthLog.Compat($"VideoPatch: uninstalled (disable = {Hooking.MinHook.StatusString(status)}).");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static void RenderNormalModeDetour(IntPtr smartBox)
    {
        bool skip = false;
        try { skip = IsInstalled && CurrentFrame().SkipWorld; }
        catch { /* draw normally */ }
        if (skip)
        {
            Interlocked.Increment(ref _scenesSkipped);
            return;
        }

        long t0 = Stopwatch.GetTimestamp();
        ((delegate* unmanaged[Thiscall]<IntPtr, void>)_origRenderNormalMode)(smartBox);
        long t1 = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _scenesDrawn);
        _worldTicks += t1 - t0;
        _worldFrames++;
        if (_worldWindowStart == 0) _worldWindowStart = t1;
        if (t1 - _worldWindowStart >= Stopwatch.Frequency)
        {
            _worldMs = _worldFrames > 0 ? (float)(_worldTicks * 1000.0 / Stopwatch.Frequency / _worldFrames) : 0f;
            _worldTicks = 0;
            _worldFrames = 0;
            _worldWindowStart = t1;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Per-frame decision (AC's render thread)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>AC's 3D scene is (being) skipped this frame. Render thread.</summary>
    public static bool SkipWorldThisFrame => _mode != 0 && IsInstalled && CurrentFrame().SkipWorld;

    /// <summary>AC's 2D UI is skipped this frame ("all"). Render thread (AcUiPassHook).</summary>
    public static bool SkipAcUiThisFrame
    {
        get
        {
            if (_mode != (int)VideoPatchMode.All || !IsInstalled || !CurrentFrame().SkipAcUi) return false;
            Interlocked.Increment(ref _uiSkipped);
            return true;
        }
    }

    /// <summary>The "rendering paused" banner should draw (the ImGui frame asks). Render thread.</summary>
    public static bool WantsBanner => _mode == (int)VideoPatchMode.All && IsInstalled && CurrentFrame().SkipAcUi;

    /// <summary>
    /// The fps cap for the frame EndScene is finishing (0 = none). EndSceneHook, AC's render
    /// thread, after the FPS governor.
    /// </summary>
    public static int FrameCapThisFrame()
    {
        if (_mode == 0 || !IsInstalled) return 0;
        return VideoPatchPolicy.FrameCap(CurrentFrame(), _capFps);
    }

    // One decision per frame (keyed by EndSceneHook's back-buffer frame count, and at most
    // 100 ms old for a client whose EndScene isn't hooked), so the scene, the UI pass, the
    // overlays, the banner and the cap of one frame agree.
    private static VideoPatchDecision CurrentFrame()
    {
        int frame = EndSceneHook.BackBufferFrames;
        long now = Stopwatch.GetTimestamp();
        if (frame != _decidedFrame || now - _decidedAt > DecisionMaxAge)
        {
            _decidedFrame = frame;
            _decidedAt = now;
            _frame = Evaluate();
        }
        return _frame;
    }

    private static VideoPatchDecision Evaluate()
    {
        var mode = (VideoPatchMode)_mode;
        if (mode == VideoPatchMode.Off || !IsInstalled || !Slot.Enabled || EngineLifecycle.IsShuttingDown)
            return VideoPatchDecision.None;
        bool inWorld = LoginLifecycleHooks.HasObservedLoginComplete
            && UiFlowHooks.InWorldOrUnknown
            && !UiFlowHooks.ClientCleanupStarted;
        bool focused = mode != VideoPatchMode.Unfocused || IsClientFocused();
        // "All" needs the banner/panels (our ImGui layer) and the UI-pass hook that skips
        // AC's UI; without either it acts as "world".
        bool uiSkippable = Plugins.EngineSettings.EnableImGuiBackend && AcUiPassHook.IsInstalled;
        return VideoPatchPolicy.Decide(mode, inWorld, focused, uiSkippable);
    }

    /// <summary>The game window, or any window of this process, is the foreground window (EndSceneHook's rule).</summary>
    private static bool IsClientFocused()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg == ImGuiBackend.Win32Backend.GameHwnd) return true;
        GetWindowThreadProcessId(fg, out uint pid);
        return pid == (uint)OwnPid;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Setting the mode
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sets the mode now. <paramref name="save"/>: also store it for this character
    /// (never for "all"). Any thread. Returns the reply for chat.
    /// </summary>
    public static string SetMode(VideoPatchMode mode, bool save, string source)
    {
        if (!IsInstalled && mode != VideoPatchMode.Off)
            return $"Video patch unavailable: {Slot.Reason}.";
        if (IsInstalled && !Slot.Enabled && mode != VideoPatchMode.Off)
            return "Video patch is switched off (/rc hooks on VideoPatch to switch it back on).";

        var old = (VideoPatchMode)_mode;
        _mode = (int)mode;
        _decidedFrame = int.MinValue;   // the render thread decides again on its next look
        Slot.Note("mode " + VideoPatchPolicy.Name(mode));
        string savedNote = string.Empty;
        if (save)
        {
            if (VideoPatchPolicy.IsSaved(mode))
            {
                string key = CurrentCharacterKey();
                lock (FileSync)
                {
                    EnsureFileLoadedLocked();
                    _file!.Set(key, mode);
                    SaveFileLocked();
                }
                savedNote = key == VideoPatchPolicy.DefaultCharacterKey
                    ? " Saved as this PC's default (character name unknown)."
                    : " Saved for this character.";
            }
            else
            {
                savedNote = " This session only (\"all\" is never saved).";
            }
        }
        if (old != mode)
        {
            _pendingChange = $"{VideoPatchPolicy.Name(old)} -> {VideoPatchPolicy.Name(mode)}";
            RynthLog.Compat($"VideoPatch: mode {VideoPatchPolicy.Name(old)} -> {VideoPatchPolicy.Name(mode)} ({source}).");
        }
        return Describe(mode) + savedNote;
    }

    /// <summary>Sets the frame cap used while the world is skipped (0 = none, else 5..60). Saved for the PC.</summary>
    public static string SetFrameCap(int fps)
    {
        int cap = VideoPatchPolicy.ClampCap(fps);
        lock (FileSync)
        {
            EnsureFileLoadedLocked();
            _file!.FrameCapFps = cap;
            _capFps = cap;
            SaveFileLocked();
        }
        RynthLog.Compat($"VideoPatch: frame cap while patched -> {(cap > 0 ? cap + " fps" : "none")}.");
        return cap > 0
            ? $"While the world isn't drawn the client runs at most {cap} fps (saved for this PC)."
            : "No extra frame cap while the world isn't drawn (saved for this PC); the FPS limiter still applies.";
    }

    private static string Describe(VideoPatchMode mode) => mode switch
    {
        VideoPatchMode.World => "Video patch: WORLD - AC's 3D world isn't drawn; AC's UI and the panels are. /rc video off to restore.",
        VideoPatchMode.Unfocused => "Video patch: UNFOCUSED - the 3D world isn't drawn while another window has the focus; it draws again the moment you switch back.",
        VideoPatchMode.All => "Video patch: ALL - nothing of AC's is drawn; the panels and a banner are. Type /rc video off (or Settings > Misc) to restore.",
        _ => "Video patch: off - normal drawing.",
    };

    // ════════════════════════════════════════════════════════════════════
    //  /rc video
    // ════════════════════════════════════════════════════════════════════

    /// <summary>/rc video [status|off|on|world|unfocused|all|fps &lt;n&gt;]. Returns the chat replies.</summary>
    public static List<string> HandleCommand(string args)
    {
        var replies = new List<string>();
        string[] parts = (args ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts[0].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            replies.AddRange(StatusLines());
            return replies;
        }
        if (parts[0].Equals("fps", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("cap", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps))
                replies.Add(SetFrameCap(fps));
            else
                replies.Add($"Usage: /rc video fps <0|5-60> (now {(FrameCapFps > 0 ? FrameCapFps + " fps" : "none")}).");
            return replies;
        }
        if (parts.Length == 1 && VideoPatchPolicy.TryParse(parts[0], out VideoPatchMode mode))
        {
            replies.Add(SetMode(mode, save: true, "/rc video"));
            return replies;
        }
        replies.Add("Usage: /rc video [status|enable|disable|off|world|unfocused|all|fps <n>] (enable = world, disable = off; also /ra videopatch and /ub videopatch)");
        return replies;
    }

    public static List<string> StatusLines()
    {
        EnsureFileLoaded();
        var lines = new List<string>();
        if (!IsInstalled)
        {
            lines.Add($"Video patch unavailable: {Slot.Reason}.");
            return lines;
        }
        var mode = (VideoPatchMode)_mode;
        string active = mode == VideoPatchMode.Off ? "" : (Interlocked.Read(ref _scenesSkipped) > _lastSkipCount ? " (skipping now)" : " (drawing now)");
        lines.Add($"Video patch: {VideoPatchPolicy.Name(mode)}{active}{(Slot.Enabled ? "" : " - switched OFF (/rc hooks on VideoPatch)")}. " +
                  $"Saved for {CurrentCharacterKey()}: {VideoPatchPolicy.Name(SavedModeFor(CurrentCharacterKey()))}. " +
                  $"Frame cap while patched: {(_capFps > 0 ? _capFps + " fps" : "none")}.");
        lines.Add($"Scenes drawn {Interlocked.Read(ref _scenesDrawn)}, skipped {Interlocked.Read(ref _scenesSkipped)}; AC UI passes skipped {Interlocked.Read(ref _uiSkipped)}; " +
                  $"3D scene cost {_worldMs:0.0} ms/frame; client CPU {(_lastCpuPct >= 0 ? _lastCpuPct.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "n/a")} " +
                  $"(of all cores); {HookText()}.");
        if (mode == VideoPatchMode.All && !(Plugins.EngineSettings.EnableImGuiBackend && AcUiPassHook.IsInstalled))
            lines.Add($"\"all\" acts as \"world\" here: {(Plugins.EngineSettings.EnableImGuiBackend ? "the UI-pass hook isn't installed (" + AcUiPassHook.Status + ")" : "the ImGui layer is off")}, so AC's UI keeps drawing.");
        lines.Add("Modes: off | unfocused (world only while in the background) | world | all (session only). The phone live view shows what the client draws.");
        return lines;
    }

    private static string HookText() => $"hook @ 0x{Slot.Address.ToInt32():X8} via {Slot.Source}";

    // ════════════════════════════════════════════════════════════════════
    //  Settings file
    // ════════════════════════════════════════════════════════════════════

    private static string SettingPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "video_patch.txt");

    private static string CurrentCharacterKey() =>
        VideoPatchPolicy.CharacterKey(SessionStateRegistry.LastServerName, SessionStateRegistry.LastCharacterName);

    private static VideoPatchMode SavedModeFor(string key)
    {
        lock (FileSync)
        {
            EnsureFileLoadedLocked();
            return _file!.ModeFor(key);
        }
    }

    private static void EnsureFileLoaded()
    {
        if (Volatile.Read(ref _file) != null) return;
        lock (FileSync) EnsureFileLoadedLocked();
    }

    private static void EnsureFileLoadedLocked()
    {
        if (_file != null) return;
        string[]? lines = null;
        try
        {
            if (File.Exists(SettingPath)) lines = File.ReadAllLines(SettingPath);
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"VideoPatch: could not read {SettingPath}: {ex.GetType().Name}: {ex.Message}");
        }
        _file = VideoPatchPolicy.Parse(lines);
        _capFps = _file.FrameCapFps;
    }

    private static void SaveFileLocked()
    {
        try
        {
            string path = SettingPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, VideoPatchPolicy.Serialize(_file!));
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"VideoPatch: could not write {SettingPath}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Heartbeat: per-character load, CPU and before/after measurement
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// HeartbeatLogger, once a second (its thread). Loads the character's saved mode
    /// once the login is known, samples the process CPU, logs the before/after lines
    /// around a mode change, and returns the heartbeat field (empty while off).
    /// </summary>
    public static string OnHeartbeat(int drawFps)
    {
        _hbTick++;
        EnsureFileLoaded();
        float cpu = SampleCpuPercent();
        _lastCpuPct = cpu;
        float worldMs = _worldMs;
        long skips = Interlocked.Read(ref _scenesSkipped);
        bool skipping = skips > _lastSkipCount;
        _lastSkipCount = skips;
        if (skipping) _worldMs = worldMs = 0f;   // nothing drawn this second

        try { LoadForCharacter(); }
        catch (Exception ex) { RynthLog.Compat($"VideoPatch: per-character load failed: {ex.GetType().Name}: {ex.Message}"); }

        // A mode change: "before" from the last seconds (this second is mixed, left out),
        // "after" 10 s later from the 5 s before that.
        string? change = Interlocked.Exchange(ref _pendingChange, null);
        if (change != null)
        {
            RynthLog.Compat($"VideoPatch: {change} - before (last {Math.Min(_ringCount, RingSize)} s): {Averages()}.");
            _afterDueTick = _hbTick + 10;
            _afterLabel = change;
            _ringCount = 0;
            _ringPos = 0;
        }
        else
        {
            if (cpu >= 0)
            {
                CpuRing[_ringPos] = cpu;
                DrawRing[_ringPos] = drawFps;
                WorldRing[_ringPos] = worldMs;
                _ringPos = (_ringPos + 1) % RingSize;
                if (_ringCount < RingSize) _ringCount++;
            }
            if (_afterDueTick != 0 && _hbTick >= _afterDueTick)
            {
                _afterDueTick = 0;
                RynthLog.Compat($"VideoPatch: {_afterLabel} - after (10 s on, last {Math.Min(_ringCount, RingSize)} s): {Averages()}.");
            }
        }

        var mode = (VideoPatchMode)_mode;
        if (mode == VideoPatchMode.Off) return string.Empty;
        string cpuText = cpu >= 0 ? cpu.ToString("0.0", CultureInfo.InvariantCulture) : "?";
        return $" vp={VideoPatchPolicy.Name(mode)}:{(skipping ? "skip" : "draw")} cpu={cpuText}%";
    }

    private static string Averages()
    {
        int n = Math.Min(_ringCount, RingSize);
        if (n == 0) return "no samples yet";
        float c = 0, d = 0, w = 0;
        for (int i = 0; i < n; i++) { c += CpuRing[i]; d += DrawRing[i]; w += WorldRing[i]; }
        c /= n; d /= n; w /= n;
        string frame = d > 0 ? (1000f / d).ToString("0.0", CultureInfo.InvariantCulture) + " ms" : "n/a";
        return string.Create(CultureInfo.InvariantCulture,
            $"cpu={c:0.0}% of all cores, draw={d:0.0} fps (frame {frame}), 3D scene={w:0.00} ms/frame");
    }

    // Process CPU over the last heartbeat, as Task Manager shows it (share of all cores).
    private static float SampleCpuPercent()
    {
        try
        {
            if (!GetProcessTimes(GetCurrentProcess(), out _, out _, out long kernel, out long user)) return -1;
            long cpu = kernel + user;                       // 100 ns units
            long wall = Stopwatch.GetTimestamp();
            float pct = -1;
            if (_lastWallTicks != 0)
            {
                double wall100ns = (wall - _lastWallTicks) * 10_000_000.0 / Stopwatch.Frequency;
                if (wall100ns > 0)
                    pct = (float)((cpu - _lastCpu100ns) * 100.0 / wall100ns / Math.Max(1, Environment.ProcessorCount));
            }
            _lastCpu100ns = cpu;
            _lastWallTicks = wall;
            return pct;
        }
        catch
        {
            return -1;
        }
    }

    // The character's saved mode, applied when the client learns which character it is
    // (login; an engine reload replays it). "All" is never saved, so a restart or a reload
    // never comes back with AC's UI hidden.
    private static void LoadForCharacter()
    {
        if (!LoginLifecycleHooks.HasObservedLoginComplete) return;
        string key = CurrentCharacterKey();
        if (key == _loadedKey) return;
        _loadedKey = key;
        VideoPatchMode saved = SavedModeFor(key);
        if (saved == (VideoPatchMode)_mode) return;
        if (!IsInstalled)
        {
            if (saved != VideoPatchMode.Off)
                RynthLog.Compat($"VideoPatch: {key} has '{VideoPatchPolicy.Name(saved)}' saved, but the hook is unavailable ({Slot.Reason}).");
            return;
        }
        RynthLog.Compat($"VideoPatch: {key}: saved mode '{VideoPatchPolicy.Name(saved)}'.");
        SetMode(saved, save: false, "saved for " + key);
    }
}

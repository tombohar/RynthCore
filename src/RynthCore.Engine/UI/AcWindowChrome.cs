// ============================================================================
//  RynthCore.Engine - UI/AcWindowChrome.cs
//  Settings > Game Client helpers for the Asheron's Call game HWND:
//    - Borderless windowed: strip caption / thick frame while leaving the client
//      windowed (not exclusive fullscreen). Chrome is only given back to a window
//      WE stripped: that is recorded as an HWND property (SetProp) holding the
//      window's style from before, which outlives an in-process engine hot reload
//      and dies with the process. No marker and the setting off = hands off.
//    - Remember placement (opt-in): persist the outer window rect per client
//      (ac_window.<server-character>.txt) and put its POSITION back once after login.
//      Since 2026-11-15 (report R-20261115-160655-1201) never its size, and a rect is
//      saved only in the world once it held for 30 s: see AcWindowChromePolicy.
//      While this is on, the launcher leaves the window's position to the engine.
//  Original: SilentKelpie (RynthCore PR 6). Decisions live in AcWindowChromePolicy.
//
//  Threading. Tick runs inside EndScene (the render callback) and only READS there
//  (styles, rect, the marker); anything that changes the window - the style, a
//  SetWindowPos that moves or resizes it - is posted to the game window's own WndProc
//  (Win32Backend.PostToGameThread, the way pop-out windows are created), so it never
//  runs inside the render call. Rect files are written by UiBackgroundWriter, never
//  on the render thread. With no WndProc subclass (ImGui backend off) the feature
//  stays inactive instead of touching the window from the render thread.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.App;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Plugins;
using static RynthCore.Engine.UI.AcWindowChromePolicy;

namespace RynthCore.Engine.UI;

internal static class AcWindowChrome
{
    private const int GwlStyle = -16;

    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpFramechanged = 0x0020;

    // HWND properties: they live on the window, so they survive an engine hot reload.
    private const string StyleProp = "RynthCore.AcWindowChrome.StyleBeforeBorderless";
    private const string PlacementDoneProp = "RynthCore.AcWindowChrome.PlacementRestored";

    private const long TickIntervalMs = 2000;
    private const long NoKeyFallbackMs = 90000;     // no character this long: use the shared file
    private const long PostStuckMs = 10000;         // a posted job that never ran may be posted again
    private const long StyleRetryMs = 10000;        // a style change that does not stick is not retried every tick

    private static readonly object Sync = new();
    private static readonly Action PostedWork = RunOnGameThread;
    private static long _nextTickMs, _firstTickMs, _workPostedAtMs, _nextStyleActionMs;
    private static int _workPosted;
    private static bool _loggedNoExecutor;

    // Placement (under Sync). The saved rect, read once per key; the restore's clocks; what a new
    // rect is compared with (the saved rect, else the first one seen in the world) and the rect
    // waiting out AcWindowPlacementRule.StableFor. A hot reload resets them, which is fine: the
    // PlacementDoneProp on the HWND keeps the restore from running twice.
    private static string? _savedKey;
    private static bool _savedLoaded;
    private static Rect? _saved;
    private static long _restoreWatchStartMs, _sizeSinceMs;
    private static int _sizeW, _sizeH;
    private static Rect? _reference;
    private static bool _referenceSet;
    private static AcWindowPlacementRule.Pending? _pending;

    // ── Frame loop (EndScene): reads + posts only ────────────────────────

    /// <summary>Call from the frame loop; throttled. Never changes the window itself.</summary>
    public static void Tick(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        long now = Environment.TickCount64;
        if (now < _nextTickMs) return;
        _nextTickMs = now + TickIntervalMs;
        if (_firstTickMs == 0) _firstTickMs = now;

        try
        {
            if (!ImGuiBackend.Win32Backend.CanRunOnGameThread)
            {
                // No WndProc subclass to run window changes from: stay out of the window.
                if (!_loggedNoExecutor && (EngineSettings.BorderlessWindowed || EngineSettings.RememberAcWindowPosition))
                {
                    _loggedNoExecutor = true;
                    RynthLog.UI("AcWindowChrome: no game-thread executor (ImGui backend off); display chrome inactive.");
                }
                return;
            }

            int style = GetWindowLong(hwnd, GwlStyle);
            bool work = now >= Interlocked.Read(ref _nextStyleActionMs)
                && DecideBorderless(EngineSettings.BorderlessWindowed, ReadMarker(hwnd), style,
                    LooksExclusiveFullscreen(hwnd, style)).Action != ChromeAction.None;

            // Posted every tick until the restore is decided: it waits for AC's size to settle.
            if (EngineSettings.RememberAcWindowPosition && !PlacementDone(hwnd) && InWorld() && KeyReady(now, out _))
                work = true;

            if (work) PostWork(now);

            MaybeSavePlacement(hwnd, now, style);
        }
        catch (Exception ex)
        {
            try { RynthLog.UI($"AcWindowChrome: tick failed - {ex.Message}"); } catch { }
        }
    }

    /// <summary>A Settings > Game Client toggle changed: apply it on the game thread soon (not inline).</summary>
    public static void RequestApply()
    {
        try
        {
            if (ImGuiBackend.Win32Backend.CanRunOnGameThread)
                PostWork(Environment.TickCount64);
            // else: Tick applies it once a game-thread executor exists.
        }
        catch (Exception ex)
        {
            try { RynthLog.UI($"AcWindowChrome: apply request failed - {ex.Message}"); } catch { }
        }
    }

    private static void PostWork(long now)
    {
        // One job in flight; a job that never ran (window gone, reload) may be re-posted after a while.
        if (Interlocked.CompareExchange(ref _workPosted, 1, 0) != 0 && now - _workPostedAtMs < PostStuckMs)
            return;
        _workPostedAtMs = now;
        Volatile.Write(ref _workPosted, 1);
        if (!ImGuiBackend.Win32Backend.PostToGameThread(PostedWork))
            Volatile.Write(ref _workPosted, 0);
    }

    // ── Game thread (the game WndProc) ───────────────────────────────────

    private static void RunOnGameThread()
    {
        Volatile.Write(ref _workPosted, 0);
        try
        {
            IntPtr hwnd = ResolveGameHwnd();
            if (hwnd == IntPtr.Zero) return;
            lock (Sync)
            {
                if (EngineSettings.RememberAcWindowPosition && !PlacementDone(hwnd))
                    TryRestorePlacement(hwnd);
                ApplyBorderless(hwnd);
            }
        }
        catch (Exception ex)
        {
            try { RynthLog.UI($"AcWindowChrome: apply failed - {ex.Message}"); } catch { }
        }
    }

    private static void ApplyBorderless(IntPtr hwnd)
    {
        int style = GetWindowLong(hwnd, GwlStyle);
        ChromeDecision d = DecideBorderless(EngineSettings.BorderlessWindowed, ReadMarker(hwnd), style,
            LooksExclusiveFullscreen(hwnd, style));
        if (d.Action != ChromeAction.None)
            Interlocked.Exchange(ref _nextStyleActionMs, Environment.TickCount64 + StyleRetryMs);
        switch (d.Action)
        {
            case ChromeAction.Strip:
                SetWindowLong(hwnd, GwlStyle, d.NewStyle);
                SetProp(hwnd, StyleProp, new IntPtr(d.NewMarker));
                Refresh(hwnd);
                RynthLog.UI("AcWindowChrome: borderless windowed on.");
                break;
            case ChromeAction.Restore:
                SetWindowLong(hwnd, GwlStyle, d.NewStyle);
                RemoveProp(hwnd, StyleProp);
                Refresh(hwnd);
                RynthLog.UI("AcWindowChrome: window borders restored.");
                break;
            case ChromeAction.ClearMarker:
                RemoveProp(hwnd, StyleProp);
                RynthLog.UI("AcWindowChrome: borderless marker cleared (" + d.Reason + ").");
                break;
        }
    }

    // Style change only: same outer rect, non-client area rebuilt.
    private static void Refresh(IntPtr hwnd)
        => SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpFramechanged);

    private static void TryRestorePlacement(IntPtr hwnd)
    {
        long now = Environment.TickCount64;
        if (!InWorld() || !KeyReady(now, out string? key)) return;   // login screen / character select
        if (_restoreWatchStartMs == 0) _restoreWatchStartMs = now;
        if (!GetWindowRect(hwnd, out RECT wr)) return;
        var cur = new Rect(wr.Left, wr.Top, wr.Right, wr.Bottom);
        if (cur.Width != _sizeW || cur.Height != _sizeH)
        {
            _sizeW = cur.Width;
            _sizeH = cur.Height;
            _sizeSinceMs = now;
        }

        Rect? saved = LoadSaved(key);
        var d = DecideRestore(saved, IsIconic(hwnd) || IsZoomed(hwnd),
            LooksExclusiveFullscreen(hwnd, GetWindowLong(hwnd, GwlStyle)), cur,
            TimeSpan.FromMilliseconds(now - _sizeSinceMs), TimeSpan.FromMilliseconds(now - _restoreWatchStartMs),
            WorkAreas());
        if (d.Action == AcWindowPlacementRule.RestoreAction.Wait) return;   // asked again next tick

        // One decision per window, even a skipped one; the HWND carries it across a hot reload.
        SetProp(hwnd, PlacementDoneProp, new IntPtr(1));
        SetReference(saved);
        if (d.Action == AcWindowPlacementRule.RestoreAction.Move && saved is Rect from)
        {
            // SWP_NOSIZE: AC sizes its own window from its resolution; a smaller window crops the
            // picture and leaves the HUD out of reach (R-20261115-160655-1201).
            SetWindowPos(hwnd, IntPtr.Zero, d.X, d.Y, 0, 0, SwpNosize | SwpNozorder | SwpNoactivate);
            RynthLog.UI(d.Clamped
                ? FormattableString.Invariant($"AcWindowChrome: restored AC window position to ({d.X},{d.Y}), pulled onto the screen from ({from.Left},{from.Top}); size left to AC ({cur.Width}x{cur.Height}).")
                : FormattableString.Invariant($"AcWindowChrome: restored AC window position to ({d.X},{d.Y}); size left to AC ({cur.Width}x{cur.Height})."));
        }
        else if (saved is Rect sv)
        {
            RynthLog.UI(FormattableString.Invariant(
                $"AcWindowChrome: saved position ({sv.Left},{sv.Top}) not restored ({d.Why}); leaving AC where it is."));
        }
    }

    /// <summary>The saved rect for this client, read once per key (null: none, or unreadable).</summary>
    private static Rect? LoadSaved(string? key)
    {
        if (_savedLoaded && string.Equals(_savedKey, key, StringComparison.Ordinal)) return _saved;
        _savedLoaded = true;
        _savedKey = key;
        _saved = null;
        string path = PlacementPath(key);
        try
        {
            if (File.Exists(path) && TryParsePlacement(File.ReadAllLines(path), out Rect r))
                _saved = r;
        }
        catch (Exception ex)
        {
            try { RynthLog.UI($"AcWindowChrome: read placement failed - {ex.Message}"); } catch { }
        }
        return _saved;
    }

    private static void SetReference(Rect? r)
    {
        _reference = r;
        _referenceSet = true;
        _pending = null;
    }

    // ── Saving the rect (read on the render thread, written in the background) ──

    private static void MaybeSavePlacement(IntPtr hwnd, long now, int style)
    {
        if (!EngineSettings.RememberAcWindowPosition) return;
        // Not before the restore is decided: it would overwrite the saved rect with the launch position.
        if (!PlacementDone(hwnd)) return;
        TrySavePlacement(hwnd, style, now, force: false);
    }

    private static void TrySavePlacement(IntPtr hwnd, int style, long now, bool force)
    {
        if (!KeyReady(now, out string? key)) return;
        if (!GetWindowRect(hwnd, out RECT wr)) return;
        var cur = new Rect(wr.Left, wr.Top, wr.Right, wr.Bottom);
        bool saveable = !IsIconic(hwnd) && !IsZoomed(hwnd) && !LooksExclusiveFullscreen(hwnd, style);
        bool inWorld = InWorld();
        bool baselineOk = inWorld && saveable && AcWindowPlacementRule.IsCapturable(ToPlacement(cur));

        lock (Sync)
        {
            // After a hot reload the restore was decided by the old engine: start from the saved rect.
            if (!_referenceSet) SetReference(LoadSaved(key));
            if (_reference is null && !force)
            {
                // Nothing saved: the first rect in the world is AC's own placement, never saved
                // until the player moves the window away from it (as in the launcher).
                if (baselineOk) _reference = cur;
                return;
            }

            var d = DecideSave(inWorld, saveable, force, cur, _reference, ref _pending, DateTime.UtcNow);
            if (d.Action == AcWindowPlacementRule.SaveAction.LoginShrink)
            {
                if (d.FirstSeen && _reference is Rect rf)
                    RynthLog.UI(FormattableString.Invariant(
                        $"AcWindowChrome: not saving {cur.Width}x{cur.Height}, under half the in-game {rf.Width}x{rf.Height} (the login / character-select window)."));
                return;
            }
            if (d.Action != AcWindowPlacementRule.SaveAction.Save) return;

            // Off-screen (a monitor that is gone) or absurd: not worth saving.
            if (EvaluatePlacement(true, false, cur, Monitors()) != PlacementVerdict.Ok) return;
            _reference = cur;
        }

        string path = PlacementPath(key);
        UiBackgroundWriter.Enqueue("ac window placement", () => WritePlacement(path, cur));
    }

    private static void WritePlacement(string path, Rect r)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, FormatPlacement(r));
        }
        catch (Exception ex)
        {
            try { RynthLog.UI($"AcWindowChrome: save placement failed - {ex.Message}"); } catch { }
        }
    }

    /// <summary>Settings > Interface "Save Layout Now". Runs on the render thread (a button in a
    /// frame): everything that touches a file is queued to the background writer.</summary>
    public static void SaveLayoutNow()
    {
        try
        {
            IntPtr hwnd = ResolveGameHwnd();
            if (hwnd != IntPtr.Zero && EngineSettings.RememberAcWindowPosition)
                TrySavePlacement(hwnd, GetWindowLong(hwnd, GwlStyle), Environment.TickCount64, force: true);
        }
        catch (Exception ex)
        {
            try { RynthLog.UI($"AcWindowChrome: save window failed - {ex.Message}"); } catch { }
        }

        try { ImGuiBackend.Hud.MonsterHudSettings.Save(); } catch { /* best-effort */ }

        try
        {
            // To the imgui.ini of the panel layout in use (this character's, or the PC-wide default).
            if (ImGuiNET.ImGui.GetCurrentContext() != IntPtr.Zero)
                PanelLayout.QueueIniSave(ImGuiNET.ImGui.SaveIniSettingsToMemory(), explicitSave: true);
        }
        catch (Exception ex)
        {
            try { RynthLog.UI($"AcWindowChrome: imgui.ini save failed - {ex.Message}"); } catch { }
        }

        // Rewrites the layout's panel_state.txt from the in-memory state (even with Remember Open Windows off).
        UiBackgroundWriter.Enqueue("panel state (save now)", PanelStateStore.Flush);

        try
        {
            // The PC-wide default with Panel Layout Per Character on is only read (2026-11-05): say so.
            string? problem = PanelLayout.SavePlacesProblem();
            if (problem == null)
                AcMainThreadQueue.EnqueueWriteToChat("[RynthCore] Saved open windows and HUD positions.", 1);
            else
                AcMainThreadQueue.EnqueueWriteToChat("[RynthCore] Saved HUD positions. Open windows and their places weren't saved: " + problem + ".", 0x1A);
        }
        catch { /* chat may not be ready */ }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// In the world after a login: not the login screen, not character select (where AC's window
    /// is its small 800x600 one), not AC closing. A mode not seen yet counts as the world, as
    /// elsewhere in the engine; the 30 s rule and the login-shrink guard cover that gap.
    /// </summary>
    private static bool InWorld()
        => LoginLifecycleHooks.HasObservedLoginComplete
            && UiFlowHooks.InWorldOrUnknown
            && !UiFlowHooks.ClientCleanupStarted;

    private static bool KeyReady(long now, out string? key)
        => TryResolveKey(SessionStateRegistry.LastServerName, SessionStateRegistry.LastCharacterName,
            _firstTickMs == 0 ? 0 : now - _firstTickMs, NoKeyFallbackMs, out key);

    private static string PlacementPath(string? key)
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "RynthCore", PlacementFileName(key));
    }

    private static int ReadMarker(IntPtr hwnd) => unchecked((int)GetProp(hwnd, StyleProp).ToInt64());

    private static bool PlacementDone(IntPtr hwnd) => GetProp(hwnd, PlacementDoneProp) != IntPtr.Zero;

    /// <summary>Game HWND for Settings toggles (EntryPoint, else Win32Backend).</summary>
    internal static IntPtr ResolveGameHwndForSettings() => ResolveGameHwnd();

    private static IntPtr ResolveGameHwnd()
    {
        IntPtr hwnd = EntryPoint.GameHwnd;
        if (hwnd != IntPtr.Zero) return hwnd;
        try { return ImGuiBackend.Win32Backend.GameHwnd; } catch { return IntPtr.Zero; }
    }

    /// <summary>True when the outer rect fills a monitor and has no caption: exclusive fullscreen or
    /// an already borderless fullscreen; do not fight it.</summary>
    private static bool LooksExclusiveFullscreen(IntPtr hwnd, int style)
    {
        if ((style & WsCaption) != 0) return false;
        if (!GetWindowRect(hwnd, out RECT wr)) return false;
        IntPtr mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (mon == IntPtr.Zero) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        int mw = mi.rcMonitor.Right - mi.rcMonitor.Left;
        int mh = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
        int ww = wr.Right - wr.Left;
        int wh = wr.Bottom - wr.Top;
        return Math.Abs(ww - mw) <= 4 && Math.Abs(wh - mh) <= 4
            && Math.Abs(wr.Left - mi.rcMonitor.Left) <= 4
            && Math.Abs(wr.Top - mi.rcMonitor.Top) <= 4;
    }

    private static List<Rect> Monitors() => EnumMonitors(work: false);

    /// <summary>Each monitor's work area (the screen minus the taskbar): where a restored window is pulled onto.</summary>
    private static List<Rect> WorkAreas() => EnumMonitors(work: true);

    private static List<Rect> EnumMonitors(bool work)
    {
        var list = new List<Rect>(4);
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr dc, ref RECT rc, IntPtr data) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref mi))
            {
                RECT m = work ? mi.rcWork : mi.rcMonitor;
                list.Add(new Rect(m.Left, m.Top, m.Right, m.Bottom));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr RemoveProp(IntPtr hWnd, string lpString);
}

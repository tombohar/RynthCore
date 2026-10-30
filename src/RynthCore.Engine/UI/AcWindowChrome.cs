// ============================================================================
//  RynthCore.Engine - UI/AcWindowChrome.cs
//  Settings > Display helpers for the Asheron's Call game HWND:
//    - Borderless windowed: strip caption / thick frame while leaving the client
//      windowed (not exclusive fullscreen). Chrome is only given back to a window
//      WE stripped: that is recorded as an HWND property (SetProp) holding the
//      window's style from before, which outlives an in-process engine hot reload
//      and dies with the process. No marker and the setting off = hands off.
//    - Remember placement (opt-in): persist the outer window rect per client
//      (ac_window.<server-character>.txt) and put it back once after login.
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
    private const long SaveIntervalMs = 10000;
    private const long NoKeyFallbackMs = 90000;     // no character this long: use the shared file
    private const long PostStuckMs = 10000;         // a posted job that never ran may be posted again
    private const long StyleRetryMs = 10000;        // a style change that does not stick is not retried every tick

    private static readonly object Sync = new();
    private static readonly Action PostedWork = RunOnGameThread;
    private static long _nextTickMs, _nextSaveMs, _firstTickMs, _workPostedAtMs, _nextStyleActionMs;
    private static int _workPosted;
    private static Rect? _lastWritten;
    private static bool _loggedNoExecutor;

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

            if (EngineSettings.RememberAcWindowPosition && !PlacementDone(hwnd) && KeyReady(now, out _))
                work = true;

            if (work) PostWork(now);

            MaybeSavePlacement(hwnd, now, style);
        }
        catch (Exception ex)
        {
            try { RynthLog.UI($"AcWindowChrome: tick failed - {ex.Message}"); } catch { }
        }
    }

    /// <summary>A Settings > Display toggle changed: apply it on the game thread soon (not inline).</summary>
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
        if (!KeyReady(Environment.TickCount64, out string? key)) return;   // still at the login screen
        // One attempt per window, even a skipped one; the HWND carries it across a hot reload.
        SetProp(hwnd, PlacementDoneProp, new IntPtr(1));

        if (IsIconic(hwnd) || IsZoomed(hwnd)) return;
        string path = PlacementPath(key);
        if (!File.Exists(path)) return;

        Rect saved;
        try
        {
            if (!TryParsePlacement(File.ReadAllLines(path), out saved)) return;
        }
        catch (Exception ex)
        {
            try { RynthLog.UI($"AcWindowChrome: read placement failed - {ex.Message}"); } catch { }
            return;
        }

        PlacementVerdict v = EvaluatePlacement(true, LooksExclusiveFullscreen(hwnd, GetWindowLong(hwnd, GwlStyle)),
            saved, Monitors());
        if (v != PlacementVerdict.Ok)
        {
            RynthLog.UI($"AcWindowChrome: saved placement not restored ({v}); leaving AC where it is.");
            return;
        }

        SetWindowPos(hwnd, IntPtr.Zero, saved.Left, saved.Top, saved.Width, saved.Height,
            SwpNozorder | SwpNoactivate);
        _lastWritten = saved;
        RynthLog.UI(FormattableString.Invariant(
            $"AcWindowChrome: restored AC window to ({saved.Left},{saved.Top}) {saved.Width}x{saved.Height}."));
    }

    // ── Saving the rect (read on the render thread, written in the background) ──

    private static void MaybeSavePlacement(IntPtr hwnd, long now, int style)
    {
        if (!EngineSettings.RememberAcWindowPosition) return;
        if (now < _nextSaveMs) return;
        // Not before the restore attempt: it would overwrite the saved rect with the launch position.
        if (!PlacementDone(hwnd)) return;
        _nextSaveMs = now + SaveIntervalMs;
        TrySavePlacement(hwnd, style, now, force: false);
    }

    private static void TrySavePlacement(IntPtr hwnd, int style, long now, bool force)
    {
        if (!KeyReady(now, out string? key)) return;
        if (IsIconic(hwnd) || IsZoomed(hwnd)) return;
        if (!GetWindowRect(hwnd, out RECT wr)) return;
        var cur = new Rect(wr.Left, wr.Top, wr.Right, wr.Bottom);
        PlacementVerdict v = EvaluatePlacement(true, LooksExclusiveFullscreen(hwnd, style), cur, Monitors());
        if (!force && !ShouldWritePlacement(v, cur, _lastWritten)) return;
        if (v != PlacementVerdict.Ok) return;
        _lastWritten = cur;
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

    /// <summary>Settings > Display "Save Layout Now". Runs on the render thread (a button in a
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
            if (ImGuiNET.ImGui.GetCurrentContext() != IntPtr.Zero)
            {
                string ini = ImGuiNET.ImGui.SaveIniSettingsToMemory();
                string iniPath = Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "imgui.ini");
                UiBackgroundWriter.Enqueue("imgui.ini", () =>
                {
                    Directory.CreateDirectory(RynthCore.Install.RynthInstallPaths.RynthAiDir);
                    File.WriteAllText(iniPath, ini);
                });
            }
        }
        catch (Exception ex)
        {
            try { RynthLog.UI($"AcWindowChrome: imgui.ini save failed - {ex.Message}"); } catch { }
        }

        // Rewrites panel_state.txt from the in-memory state (even with Remember Open Windows off).
        UiBackgroundWriter.Enqueue("panel state (save now)", PanelStateStore.Flush);

        try
        {
            AcMainThreadQueue.EnqueueWriteToChat("[RynthCore] Saved open windows and HUD positions.", 1);
        }
        catch { /* chat may not be ready */ }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

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

    private static List<Rect> Monitors()
    {
        var list = new List<Rect>(4);
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr dc, ref RECT rc, IntPtr data) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref mi))
                list.Add(new Rect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom));
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

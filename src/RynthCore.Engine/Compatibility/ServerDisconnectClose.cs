// ============================================================================
//  RynthCore.Engine - Compatibility/ServerDisconnectClose.cs
//
//  Closes the client when AC lost the server (2026-10-11). AC then shows "The server
//  has disconnected. Please try again in a few minutes." with only a Quit Game button,
//  and the client sat there until someone clicked it, so no launcher relaunched it.
//
//  Signal: UIFlow mode Disconnected (0x10000002, UiFlowHooks), which AC switches to only
//  when the connection is gone. In the logs of 2026-10-05..11 it appears exactly at the
//  eight drops (World -> Disconnected seven times, CharacterSelect -> Disconnected once)
//  and never on a logout (World -> CharacterSelect) or a quit (no screen change). The
//  other candidates were weaker: ClientNet::LogOffServer also runs on every quit (twice,
//  from Client::Disconnect and Client::CleanupNet) and came up to 142 s after the screen;
//  the logoff request AC sends (gmGamePlayUI::EndSession -> LogOffCharacter) looks like
//  any other logoff.
//
//  What it does (AC's main thread, GameTickHooks post-tick, so the mode poll just ran):
//    - first tick on the screen: logs it and asks the flight recorder for a dump (the
//      last minutes of traffic say whether the server went quiet or sent a disconnect);
//    - after ServerDisconnectClosePolicy.DefaultDelayMs (5 s; longer while that dump is
//      being written): posts WM_CLOSE to the game window, the same as its close button.
//      That runs the normal shutdown: Win32Backend stops the plugin pump and starts
//      EngineLifecycle.Shutdown, AC's UseTime returns 0 and the client exits with 0;
//    - still open 20 s later: posts WM_CLOSE once more; 60 s later: says so and stops.
//  Off (engine.json CloseOnServerDisconnect false, Settings > Misc): only the log line.
//  Polled rather than evented, so a hot reload on the screen still closes the client.
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class ServerDisconnectClose
{
    private const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    private static readonly ServerDisconnectClosePolicy Policy = new();
    private static int _fromMode;

    /// <summary>AC's main thread, every game tick (GameTickHooks, after the queue drain).</summary>
    public static void MainTick()
    {
        int mode = UiFlowHooks.CurrentMode;
        bool disconnected = mode == UiFlowHooks.ModeDisconnected;
        if (!disconnected && !Policy.OnScreen)
            return; // the common case: two reads and out

        long now = Environment.TickCount64;
        bool exiting = GameTickHooks.GameLoopExiting || UiFlowHooks.ClientCleanupStarted || EngineThreads.Stopping;
        bool enabled = EngineSettings.CloseOnServerDisconnect;
        bool dumpBusy = false;
        try { dumpBusy = Net.FlightRecorder.DumpPendingOrRunning; } catch { }

        long onScreenMs = Policy.OnScreenMs(now);
        switch (Policy.Update(now, disconnected, enabled, exiting, dumpBusy))
        {
            case ServerDisconnectClosePolicy.Verdict.Detected:
                OnDetected(enabled);
                break;
            case ServerDisconnectClosePolicy.Verdict.Close:
                PostClose($"closing the client after {onScreenMs / 1000.0:0.0}s on the Disconnected screen{(dumpBusy ? " (waited for the flight dump)" : "")}");
                break;
            case ServerDisconnectClosePolicy.Verdict.Retry:
                PostClose($"the client is still open {ServerDisconnectClosePolicy.RetryAfterMs / 1000}s after WM_CLOSE - sending it again");
                break;
            case ServerDisconnectClosePolicy.Verdict.GiveUp:
                RynthLog.Error($"ServerDisconnect: the client is still open {ServerDisconnectClosePolicy.GiveUpAfterMs / 1000}s after WM_CLOSE (sent twice) - not trying again. Click Quit Game.");
                break;
            case ServerDisconnectClosePolicy.Verdict.Left:
                RynthLog.Warn($"ServerDisconnect: the Disconnected screen went away after {onScreenMs / 1000.0:0.0}s (now {UiFlowHooks.ModeName(mode)}) before the client closed.");
                break;
        }
    }

    private static void OnDetected(bool enabled)
    {
        _fromMode = UiFlowHooks.PreviousMode;
        string who = DescribeSession();
        string action = enabled
            ? $"closing the client in {Policy.DelayMs / 1000}s so a launcher can relaunch it (Close on server disconnect, Settings > Misc)"
            : "Close on server disconnect is off (Settings > Misc) - the client stays on this screen";
        RynthLog.Warn($"ServerDisconnect: AC lost the server and shows its 'server has disconnected' screen ({UiFlowHooks.ModeName(_fromMode)} -> Disconnected){who}; {action}.");
        try { Net.FlightRecorder.RequestAuto("server disconnect (the Disconnected screen)", "engine"); }
        catch { }
    }

    private static string DescribeSession()
    {
        try
        {
            string ch = SessionStateRegistry.LastCharacterName;
            string acct = SessionStateRegistry.LastAccountName;
            string world = AccountHooks.AnnouncedWorldName ?? SessionStateRegistry.LastServerName;
            if (string.IsNullOrEmpty(ch) && string.IsNullOrEmpty(acct) && string.IsNullOrEmpty(world))
                return "";
            return $" - {(string.IsNullOrEmpty(ch) ? "?" : ch)} ({(string.IsNullOrEmpty(acct) ? "?" : acct)}) on {(string.IsNullOrEmpty(world) ? "?" : world)}";
        }
        catch { return ""; }
    }

    /// <summary>
    /// Closes the client now, the way its close button does (WM_CLOSE: AC logs the character
    /// off and the engine shuts down normally). /rc quit, and RynthAi's /ra, /mt and /ub quit.
    /// </summary>
    public static void CloseClientNow(string why) => PostClose(why);

    private static void PostClose(string why)
    {
        IntPtr hwnd = ImGuiBackend.Win32Backend.GameHwnd;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            try { hwnd = Process.GetCurrentProcess().MainWindowHandle; } catch { hwnd = IntPtr.Zero; }
        }
        if (hwnd == IntPtr.Zero)
        {
            RynthLog.Error($"ServerDisconnect: {why}, but the game window isn't known - can't close the client.");
            return;
        }
        bool ok = PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        RynthLog.Warn($"ServerDisconnect: {why} - WM_CLOSE to 0x{hwnd.ToInt64():X} {(ok ? "posted" : $"FAILED (Win32 error {err})")}.");
    }
}

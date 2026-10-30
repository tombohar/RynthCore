// ============================================================================
//  RynthCore.Engine - Compatibility/VTankWatch.cs
//
//  "Is VTank's macro running in this client?" for the Decal bridge, so RynthAi can
//  follow the one-bot-per-client rule (docs/DECAL_BRIDGE_PLAN.md, "What's lost or
//  different under Decal"): when VTank starts, RynthAi's macro stops.
//
//  Signal (VTankSignal.cs): VTank's documented /vt start and /vt stop commands, seen
//    - in every line Decal's chat parser hands the bridge (CommandLineText ->
//      ChatBarEnter record, eaten or not): typed lines, VTank meta Chat Command
//      actions and other plugins' InvokeChatParser calls;
//    - in the lines the engine itself sends through Decal (TryInvokeChatParser), which
//      the bridge doesn't echo back (RynthAi metas, on-login commands, plugins).
//  Cleared at Decal's Logoff (Decal plugins, VTank included, end with the session).
//  NOT seen: VTank's own "Run Macro" button and its own stops (death with
//  StopMacroOnDeath, a meta that stops it internally). VTank documents no chat line
//  for those, so there is nothing legal to read; see the report for options.
//
//  Survives a hot reload: the state is mirrored into this process's environment
//  (RYNTHCORE_VTANK_MACRO), which outlives an engine generation.
//
//  Without Decal nothing here runs: every entry point is called from DecalBridgeHost
//  after its Active check, and GetPluginState answers 0 ("not watching").
// ============================================================================

using System;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal static class VTankWatch
{
    /// <summary>Plugin API v74 GetVTankState flags.</summary>
    public const uint FlagWatching = 1u << 0;
    public const uint FlagRunning = 1u << 1;

    private const string EnvName = "RYNTHCORE_VTANK_MACRO";
    private const int MaxChatLogLines = 30;

    private static int _running = ReadEnv();
    private static int _seq = _running;   // a restored "running" is a change the plugins haven't seen
    private static string _lastChange = _running != 0 ? "restored after a reload" : "none yet";
    private static int _chatLogged;

    /// <summary>True while VTank's macro was last seen turned on (bridge mode only).</summary>
    public static bool Running => DecalBridgeHost.Active && Volatile.Read(ref _running) != 0;

    /// <summary>Bumped on every change of <see cref="Running"/>.</summary>
    public static int Sequence => Volatile.Read(ref _seq);

    /// <summary>
    /// Plugin API v74: bit0 = the engine watches VTank (Decal bridge mode), bit1 = VTank's
    /// macro is running as far as the engine saw. 0 without Decal. Any thread.
    /// </summary>
    public static uint GetPluginState(out int sequence)
    {
        sequence = Volatile.Read(ref _seq);
        if (!DecalBridgeHost.Active)
            return 0;
        return FlagWatching | (Volatile.Read(ref _running) != 0 ? FlagRunning : 0);
    }

    /// <summary>A line Decal's chat parser saw (typed, meta, plugin). Pump thread.</summary>
    public static void ObserveParserLine(string text) => Observe(text, "Decal's chat parser");

    /// <summary>A line the engine sent through Decal's InvokeChatParser. Any thread.</summary>
    public static void ObserveSentLine(string text) => Observe(text, "sent by RynthCore");

    /// <summary>Decal ended the session: VTank (a Decal plugin) ends with it.</summary>
    public static void OnLogoff()
    {
        if (Set(false, "logoff"))
            RynthLog.Info("VTankWatch: logoff - VTank's macro counts as stopped.");
    }

    /// <summary>Diagnostic only: log the first VTank-looking chat lines, so a test shows what VTank prints.</summary>
    public static void ObserveChatText(string text)
    {
        if (Volatile.Read(ref _chatLogged) >= MaxChatLogLines || !VTankSignal.LooksLikeVTankChat(text))
            return;
        if (Interlocked.Increment(ref _chatLogged) > MaxChatLogLines)
            return;
        string t = text.TrimEnd('\r', '\n');
        if (t.Length > 160) t = t.Substring(0, 160) + "...";
        RynthLog.Info($"VTankWatch: chat line '{t}' (logged for reference; not used as a signal)");
    }

    /// <summary>/rc vtank clear: the player says VTank isn't running (it was stopped with its own button).</summary>
    public static bool Clear() => Set(false, "/rc vtank clear");

    public static string Describe() =>
        !DecalBridgeHost.Active
            ? "VTank watch: off (no Decal bridge in this client; RynthAi is never stopped for VTank)."
            : $"VTank watch: on. VTank macro {(Volatile.Read(ref _running) != 0 ? "RUNNING" : "not running")} (last change: {Volatile.Read(ref _lastChange)}). " +
              "Signal: /vt start and /vt stop through Decal's chat parser; VTank's own button is not seen.";

    private static void Observe(string text, string source)
    {
        int verb = VTankSignal.ParseCommand(text);
        if (verb == VTankSignal.None)
            return;
        bool on = verb == VTankSignal.Start;
        if (Set(on, $"'{text.Trim()}' ({source})"))
            RynthLog.Info($"VTankWatch: VTank macro {(on ? "STARTED" : "STOPPED")} - '{text.Trim()}' ({source}).");
    }

    private static bool Set(bool on, string why)
    {
        int v = on ? 1 : 0;
        if (Interlocked.Exchange(ref _running, v) == v)
            return false;
        Volatile.Write(ref _lastChange, $"{(on ? "started" : "stopped")} by {why} at {DateTime.Now:HH:mm:ss}");
        Interlocked.Increment(ref _seq);
        try { Environment.SetEnvironmentVariable(EnvName, on ? "1" : null); } catch { }
        return true;
    }

    private static int ReadEnv()
    {
        try { return Environment.GetEnvironmentVariable(EnvName) == "1" ? 1 : 0; }
        catch { return 0; }
    }
}

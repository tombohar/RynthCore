using System;

namespace RynthCore.Engine;

/// <summary>
/// Centralised logging — every log call in the Engine routes through here.
/// Each category method writes at that category's emit level (engine.json "LogCategories",
/// editable live from the launcher; see <see cref="LogSettings"/>), gated by the global
/// "LoggingLevel". Messages keep their existing prefix text (e.g. "D3D9VTable: scanning…").
/// </summary>
internal static class RynthLog
{
    // ── Category methods ─────────────────────────────────────────────────

    /// <summary>D3D9 subsystem: vtable, EndScene, bootstrapper, matrix capture, nav3D.</summary>
    internal static void D3D9(string msg) => WriteCategory(LogCategory.D3D9, msg);

    /// <summary>Compatibility hooks: SmartBox, client objects, combat, movement, vitals, chat, etc.</summary>
    internal static void Compat(string msg) => WriteCategory(LogCategory.Compat, msg);

    /// <summary>ImGui rendering: context, DX9 backend, Win32 input, shell.</summary>
    internal static void Render(string msg) => WriteCategory(LogCategory.Render, msg);

    /// <summary>Plugin system: loader, manager, lifecycle callbacks, plugin host Log lines.</summary>
    internal static void Plugin(string msg) => WriteCategory(LogCategory.Plugin, msg);

    /// <summary>UI / Avalonia overlay subsystem.</summary>
    internal static void UI(string msg) => WriteCategory(LogCategory.UI, msg);

    /// <summary>Verbose detail (any subsystem). Debug by default; set the Verbose category to Info to see it in the normal log.</summary>
    internal static void Verbose(string msg) => WriteCategory(LogCategory.Verbose, msg);

    /// <summary>Same as <see cref="Verbose"/>.</summary>
    internal static void Debug(string msg) => WriteCategory(LogCategory.Verbose, msg);

    /// <summary>Trace-level line: written only when the global level is Trace.</summary>
    internal static void Trace(string msg) => WriteAt(EntryPoint.EngineLogLevel.Trace, msg);

    /// <summary>Uncategorised line (General category, Info by default).</summary>
    internal static void Info(string msg) => WriteCategory(LogCategory.General, msg);

    /// <summary>Same as <see cref="Warn"/>.</summary>
    internal static void Warning(string msg) => Warn(msg);

    /// <summary>
    /// INFO line for a rare state change a support log must show (the server verdicts): written
    /// whenever the global LoggingLevel allows Info, whatever the categories' emit levels say.
    /// A category set to Trace under a global Info level would otherwise drop it.
    /// </summary>
    internal static void Milestone(string msg)
    {
        if (EntryPoint.ShouldLog(EntryPoint.EngineLogLevel.Info))
            EntryPoint.LogTagged("engine", msg, "INF");
    }

    // Most recent WRN/ERR text + when, surfaced via the GetEngineStatusJson host bridge so a stuck box
    // can be diagnosed remotely without reading the PC log. Recorded even when the line is filtered.
    internal static volatile string? LastIssue;
    internal static DateTime LastIssueUtc;

    /// <summary>WARN line — a recoverable problem worth grepping for. Hidden only at level Error/Off.</summary>
    internal static void Warn(string msg)
    {
        LastIssue = msg; LastIssueUtc = DateTime.UtcNow;
        if (EntryPoint.ShouldLog(EntryPoint.EngineLogLevel.Warning))
            EntryPoint.LogTagged("engine", msg, "WRN");
    }

    /// <summary>
    /// Always-on heartbeat line, written at every LoggingLevel (Off included). The launcher's
    /// wedge watchdog reads RynthCore.&lt;pid&gt;.log going quiet for 120 s as a dead engine and
    /// kills the client, so this line must never be filtered: on 2026-10-06 LoggingLevel=Info
    /// with General=Trace dropped it and healthy in-world clients were killed and relaunched
    /// every few minutes.
    /// </summary>
    internal static void Heartbeat(string msg) => EntryPoint.LogTagged("engine", msg, "INF");

    /// <summary>Always-on ERROR line — a fault/crash/disable, written even at level Off. Triage with grep "[ERR]".</summary>
    internal static void Error(string msg)
    {
        LastIssue = msg; LastIssueUtc = DateTime.UtcNow;
        EntryPoint.LogTagged("engine", msg, "ERR");
    }

    // ── Diagnostic traces (2026-10-05, Drakkon's held casts on DreamWeave) ──
    // A movement packet sent while a cast winds up can cancel the cast or leave the server
    // holding it open, and the old movement lines were 1 in 8 with no caller. Both switches
    // are on by default; each costs one bool test when off.

    /// <summary>Every plugin movement call (SetAutoRun, SetMotion, DoMovement/StopMovement,
    /// StopCompletely, TurnToHeading, jumps), one "Move:" line each with the calling plugin.
    /// Identical calls in a row from one caller (turns: any angle) are counted, not written,
    /// for up to MoveRepeatWindowMs; the count rides on the next line written.</summary>
    internal static bool MoveTraceEnabled = true;

    /// <summary>Every server UseDone (0x01C7) with its sequence number and error code
    /// (before: the first 25 of a session).</summary>
    internal static bool ActionDoneTraceEnabled = true;

    private const long MoveRepeatWindowMs = 1000;
    private static readonly object _moveGate = new();
    private static string _moveLastKey = "";
    private static long _moveLastWrittenMs = long.MinValue;
    private static int _moveRepeats;
    private static long _moveRepeatFirstMs;
    private static string _moveRepeatLastArg = "";

    /// <summary>
    /// One plugin movement call (see <see cref="MoveTraceEnabled"/>). <paramref name="name"/>
    /// must be a string literal (it also goes into AcActionTrace, which keeps the last 256
    /// actions for crash dumps); <paramref name="a"/>/<paramref name="b"/> are its trace args.
    /// </summary>
    internal static void Move(string name, string arg, bool ok, bool collapseByKind = false, uint a = 0, uint b = 0)
    {
        Compatibility.AcActionTrace.Record(name, a, b);
        if (!MoveTraceEnabled) return;
        string? line = null;
        try
        {
            string caller = MoveCaller();
            lock (_moveGate)
            {
                long now = Environment.TickCount64;
                string key = collapseByKind ? $"{caller}|{name}" : $"{caller}|{name}({arg})|{ok}";
                if (key == _moveLastKey && now - _moveLastWrittenMs < MoveRepeatWindowMs)
                {
                    if (_moveRepeats == 0) _moveRepeatFirstMs = now;
                    _moveRepeats++;
                    _moveRepeatLastArg = arg;
                    return;
                }
                string more = "";
                if (_moveRepeats > 0)
                {
                    string[] parts = _moveLastKey.Split('|');
                    string what = parts.Length > 1 ? parts[1] : "?";
                    string last = parts.Length > 2 ? "" : $", last {_moveRepeatLastArg}";
                    more = $" (+{_moveRepeats} more {what}{last} by {parts[0]} before this, over {now - _moveRepeatFirstMs} ms)";
                }
                line = $"Move: {name}({arg}) by {caller} -> {(ok ? "ok" : "FAILED")}{more}";
                _moveLastKey = key;
                _moveLastWrittenMs = now;
                _moveRepeats = 0;
                _moveRepeatLastArg = "";
            }
        }
        catch { return; }
        Write(line);
    }

    /// <summary>The plugin whose tick or event is calling, else the thread.</summary>
    private static string MoveCaller()
    {
        var plugin = Plugins.PluginManager.CurrentDispatch;
        if (plugin != null)
            return string.IsNullOrEmpty(plugin.DisplayName) ? plugin.FileName : plugin.DisplayName;
        return Compatibility.MainThreadGuard.IsOnMainThread()
            ? "AC main thread"
            : $"thread {Environment.CurrentManagedThreadId}";
    }

    /// <summary>True when a line in <paramref name="category"/> would be written (guard expensive messages).</summary>
    internal static bool IsEnabled(LogCategory category) => LogSettings.IsEnabled(category);

    // ── Sink ─────────────────────────────────────────────────────────────

    /// <summary>Uncategorised line (General category); used by the Move/UseDone traces.</summary>
    private static void Write(string? message)
    {
        if (message != null)
            WriteCategory(LogCategory.General, message);
    }

    private static void WriteCategory(LogCategory category, string message)
        => WriteAt(LogSettings.LevelFor(category), message);

    private static void WriteAt(EntryPoint.EngineLogLevel level, string message)
    {
        if (EntryPoint.ShouldLog(level))
            EntryPoint.LogTagged("engine", message, LogSettings.Tag(level));
    }
}

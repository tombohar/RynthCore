// ============================================================================
//  RynthCore.Engine - ImGui/UiCost.cs
//  Which part of the UI frame is slow: the breakdown behind the heartbeat's
//  ui= (UiFrameStats). The logic is in UiCostModel (tested offline); this is
//  the engine side - the slots by name, the settings, the log line, the
//  heartbeat field and /rc uicost.
//
//  Timed (each one Start/Stop pair around a call, on AC's render thread):
//    frame setup (fonts, icon textures, backends' NewFrame), the monster HUD
//    update and draw (plates, combat text; from EngineFrameController, the
//    HUD's own code is not touched), ImGui NewFrame and EndFrame+Render, the
//    shell, each plugin's Render / RenderOverlay callback (by plugin), the bar,
//    the server menus, the panel host's upkeep, each docked panel and plugin
//    window (by title), each popped-out panel (by title) and its pop-out window
//    (its own frame, render and present), the ImGui submit (with its vertex
//    count), and outside ui= the Nav3D markers and the world overlays' present
//    at AC's 3D->UI pass.
//  Counted (no timing): the main submit's draw lists, draw calls, texture
//  switches and vertices copied, against what copying each command's whole list
//  would have been (DrawSubmitPlan), and the textures uploaded that frame (the
//  font atlas, the others). The line ends "| submit: ...; uploads: ...", the
//  heartbeat field adds " uisub=draws:N,texsw:N,vtx:copied/whole-list,up:font/tex".
//
//  engine.json UiCostLog (default on) and UiCostThresholdMs (default 50):
//  Settings > Diagnostics, /rc uicost [on|off|<ms>|now]. Off = no timing at
//  all (each site is one flag check). On, a frame at or over the threshold
//  writes ONE "[UiCost] frame ..." line, at most once every 5 s; the
//  heartbeat gets " uitop=..." only while its ui p95 is over the threshold.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RynthCore.Engine.ImGuiBackend;

internal static class UiCost
{
    private static readonly UiCostModel Model = new(Stopwatch.Frequency);
    private static readonly object Sync = new();

    // Fixed parts of the frame.
    internal static readonly int FrameSetup = Model.Register("Frame setup");
    internal static readonly int HudUpdate = Model.Register("Monster HUD update");
    internal static readonly int ImGuiNewFrame = Model.Register("ImGui NewFrame");
    internal static readonly int Shell = Model.Register("Shell");
    internal static readonly int MonsterHud = Model.Register("Monster HUD (plates, combat text)", unit: "plates");
    internal static readonly int VideoBanner = Model.Register("Video patch banner");
    internal static readonly int ServerMenus = Model.Register("Server menus");
    internal static readonly int Bar = Model.Register("Bar");
    internal static readonly int PanelHost = Model.Register("Panel host upkeep", unit: "panels");
    internal static readonly int DrawerBodies = Model.Register("Settings drawer bodies");
    internal static readonly int ImGuiRender = Model.Register("ImGui EndFrame+Render");
    internal static readonly int FrameFinish = Model.Register("Frame finish (ini, input)");
    internal static readonly int PopOuts = Model.Register("Pop-outs upkeep");
    internal static readonly int Submit = Model.Register("ImGui submit", unit: "vtx");
    internal static readonly int Nav3D = Model.Register("Nav3D markers", outside: true);
    internal static readonly int WorldPresent = Model.Register("World overlays present", outside: true);

    // Named parts, a slot each the first time the name is seen (render thread only).
    private static readonly Dictionary<string, int> PanelSlots = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> Popped = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> PopOutWindows = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> PluginRender = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, int> PluginOverlay = new(StringComparer.Ordinal);

    private static bool _settingsRead;

    /// <summary>Timing on (engine.json UiCostLog). Read once per frame from the settings.</summary>
    public static bool Enabled => Model.Enabled;

    /// <summary>A timestamp for a Stop, or 0 while timing is off.</summary>
    public static long Start()
    {
        if (!_settingsRead) RefreshSettings();
        return Model.Start();
    }

    public static void Stop(int slot, long start, int count = -1) => Model.Stop(slot, start, count);

    /// <summary>What the slots got this frame so far, for <see cref="StopExclusive"/>.</summary>
    public static long Mark => Model.Mark;

    public static void StopExclusive(int slot, long start, long mark, int count = -1) => Model.StopExclusive(slot, start, mark, count);

    /// <summary>The main ImGui submit's counters (DX9Backend.LastSubmit right after it), for the line and the heartbeat.</summary>
    public static void SetSubmit(in SubmitStats stats)
    {
        if (Model.Enabled) Model.SetSubmit(stats);
    }

    /// <summary>A docked panel or plugin window, by its title.</summary>
    public static void StopPanel(string title, long start)
    {
        if (start == 0) return;
        Model.Stop(SlotFor(PanelSlots, title, ""), start);
    }

    /// <summary>A popped-out panel's drawing, by its title.</summary>
    public static void StopPopped(string title, long start)
    {
        if (start == 0) return;
        Model.Stop(SlotFor(Popped, title, " (popped out)"), start);
    }

    /// <summary>A pop-out window's own frame, render and present (its panel's drawing not included).</summary>
    public static void StopPopOutWindow(string title, long start, long mark)
    {
        if (start == 0) return;
        Model.StopExclusive(SlotFor(PopOutWindows, title, " pop-out window"), start, mark);
    }

    /// <summary>A plugin's Render callback (the shell branch).</summary>
    public static void StopPluginRender(string plugin, long start)
    {
        if (start == 0) return;
        Model.Stop(SlotFor(PluginRender, plugin, " (plugin render)"), start);
    }

    /// <summary>A plugin's RenderOverlay callback (its overlay windows).</summary>
    public static void StopPluginOverlay(string plugin, long start)
    {
        if (start == 0) return;
        Model.Stop(SlotFor(PluginOverlay, plugin, " (plugin overlay)"), start);
    }

    private static int SlotFor(Dictionary<string, int> map, string name, string suffix)
    {
        if (map.TryGetValue(name, out int slot)) return slot;
        slot = Model.Register(string.IsNullOrEmpty(name) ? "(no name)" + suffix : name + suffix);
        map[name] = slot;
        return slot;
    }

    /// <summary>
    /// The end of a UI frame, where UiFrameStats records it (<paramref name="uiTicks"/> is the same
    /// number). Writes the slow-frame line when one is due. Render thread.
    /// </summary>
    public static void EndFrame(long uiTicks)
    {
        RefreshSettings();
        DX9Backend.TakeUploads(out int fontUploads, out int texUploads);   // this frame's (also cleared while off)
        if (!Model.Enabled && Model.Mark == 0) return;   // off, and nothing left from a frame that was on
        Model.AddUploads(fontUploads, texUploads);
        string? line;
        lock (Sync) line = Model.EndFrame(uiTicks, Stopwatch.GetTimestamp(), logSlow: Model.Enabled);
        if (line != null) RynthLog.Info(line);
    }

    /// <summary>No UI frame this EndScene (the ImGui layer is off): what was timed is dropped. Render thread.</summary>
    public static void DiscardFrame()
    {
        if (Model.Mark != 0) Model.DiscardFrame();
    }

    private static void RefreshSettings()
    {
        _settingsRead = true;
        Model.Enabled = Plugins.EngineSettings.UiCostLog;
        int ms = Plugins.EngineSettings.UiCostThresholdMs;
        if (ms != Model.ThresholdMs) lock (Sync) Model.ThresholdMs = ms;
    }

    /// <summary>
    /// The heartbeat's " uitop=a:avg/max,b:avg/max,c:avg/max" (the window's top 3 by average), only
    /// while timing is on and <paramref name="uiP95Ms"/> is at or over the threshold; otherwise "".
    /// Heartbeat thread.
    /// </summary>
    public static string HeartbeatField(float uiP95Ms)
    {
        if (!Model.Enabled || uiP95Ms < Model.ThresholdMs) return "";
        lock (Sync) return Model.HeartbeatField(Stopwatch.GetTimestamp(), 3);
    }

    /// <summary>/rc uicost [on|off|&lt;ms&gt;|now|status]: the reply lines (every form answers).</summary>
    public static List<string> HandleCommand(string args)
    {
        string a = (args ?? "").Trim().ToLowerInvariant();
        var lines = new List<string>();
        if (a is "on" or "off")
        {
            Plugins.EngineSettings.UiCostLog = a == "on";
            RefreshSettings();
            lines.Add(a == "on"
                ? $"UI cost timing on: a UI frame of {Model.ThresholdMs} ms or more writes one [UiCost] line to the log (at most every 5 s)."
                : "UI cost timing off: nothing is timed or logged. /rc uicost on starts it again.");
            return lines;
        }
        if (a.EndsWith("ms", StringComparison.Ordinal)) a = a.Substring(0, a.Length - 2).Trim();
        if (int.TryParse(a, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int ms))
        {
            int v = UiCostModel.ClampThresholdMs(ms);
            Plugins.EngineSettings.UiCostThresholdMs = v;
            RefreshSettings();
            string clamped = v != ms ? $" (kept to {UiCostModel.MinThresholdMs}-{UiCostModel.MaxThresholdMs})" : "";
            lines.Add($"UI cost threshold {v} ms{clamped}; timing {(Model.Enabled ? "on" : "off - /rc uicost on to log")}.");
            return lines;
        }
        if (a is "now" or "")
        {
            lock (Sync) lines.AddRange(Model.Describe(Stopwatch.GetTimestamp()));
            if (!Model.Enabled) lines.Add("Timing is off, so this shows nothing new: /rc uicost on.");
            return lines;
        }
        if (a == "status")
        {
            lines.Add($"UI cost timing {(Model.Enabled ? "on" : "off")}, threshold {Model.ThresholdMs} ms, {Model.SlotCount} parts named, " +
                      $"{Model.FramesSeen} frames timed, {Model.LinesWritten} slow-frame lines logged.");
            return lines;
        }
        lines.Add(Usage);
        return lines;
    }

    public const string Usage = Compatibility.ChatCommandReplies.UiCostUsage;
}

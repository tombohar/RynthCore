// ============================================================================
//  RynthCore.Engine - UI/ServerMenus.cs
//  The servers and sections, as menus: the bottom half of the RynthAi
//  dashboard's Char menu and of the Mini Remote's right-click menu (the top
//  half, "Character", is the windows every server has), and the "servermenu"
//  push that gives RynthAi the same list for its own menus.
//
//  Everything comes from UI/Sections/SectionRegistry: the servers (built in or
//  declared by a plugin), whether each is active (detected or forced for this
//  character), and what the engine and the installed plugins add for each. A
//  server's group shows only while it is active and has items.
//
//  Items run "/rc" commands through the engine's dispatcher (any other command
//  goes through the chat dispatcher, as if typed); a plugin surface opens its
//  section in the Mini Remote ("/rc miniremote section <id>").
//
//  Push (Tick: when it changes, and every ResendMs in case RynthAi missed one),
//  tab-separated lines:
//    V  id  name  active 0/1  force auto|on|off  detected 0/1  reason   every server
//    X  id  label                                               every section, rail order
//    S  id  name  active 0/1  force command                     a server with items
//    I  label  command  tooltip  feature                        its items
//    N  id  pattern  default|engine.json                        a server's world names
//  RynthAi 0.5.54 and older read only S and I (unknown kinds and extra fields are
//  skipped); a "section:<id>" feature is a surface it may show in its own Mini Remote.
//  RynthILT (0.1.1+) gets the same push for its copy of the ILT settings (V and N),
//  and so does every server plugin whose sections manifest declares a server
//  (RynthColdeve, RynthAelrynth, ...: their server card's verdict and override).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using RynthCore.Engine.UI.Data;
using RynthCore.Engine.UI.Sections;

namespace RynthCore.Engine.UI;

internal static class ServerMenus
{
    /// <summary>How often RynthAi gets the list again even when nothing changed.</summary>
    private const long ResendMs = 30_000;
    /// <summary>How often the payload is rebuilt to look for a change.</summary>
    private const long CheckMs = 1_000;

    /// <summary>Feature prefix of an item that is a plugin surface (its section in the Mini Remote).</summary>
    public const string SectionFeaturePrefix = "section:";

    /// <summary>How often RynthILT gets the list again: it may load (or hot-reload) after the last change.</summary>
    private const long IltResendMs = 10_000;

    /// <summary>The plugin that keeps a copy of the ILT settings.</summary>
    private const string RynthIltPlugin = "RynthILT";

    private static long _nextCheckMs;
    private static long _lastPushMs;
    private static string _lastPayload = string.Empty;
    private static long _lastIltPushMs;
    private static string _lastIltPayload = string.Empty;
    // Server plugins (sections manifests that declare a server): the last payload each took, and when.
    private static readonly Dictionary<string, (string Payload, long AtMs)> _serverPluginPushes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>RynthAi is installed: Tick pushes it the list.</summary>
    public static bool PushToRynthAi { get; set; }

    /// <summary>RynthILT is installed: Tick pushes it the list too (its ILT settings view).</summary>
    public static bool PushToRynthIlt { get; set; }

    /// <summary>One menu item: a command, with the section it belongs to.</summary>
    internal sealed record MenuItem(string Label, string Command, string Tooltip, string Feature, bool Enabled, string Note);

    /// <summary>A server's items for the menus: every section's, surfaces as "open the section".</summary>
    internal sealed record MenuGroup(ServerState Server, MenuItem[] Items);

    /// <summary>
    /// Runs an item's command: an engine "/rc" command directly, anything else through the
    /// chat dispatcher (plugins' commands, as if typed). Render thread. A click on an engine
    /// button or menu is a Script's command: what it opens is the answer, so only a failure
    /// reaches chat.
    /// </summary>
    /// <param name="showReply">Answer in chat as for a typed command: a click whose result the player
    /// wants to read (the server window's "Treat this world as ..." prints the server summary). Other
    /// clicks stay quiet unless they fail.</param>
    public static void Run(string command, bool showReply = false)
    {
        string cmd = SectionManifestParser.Command(command);
        if (cmd.Length == 0) return;
        var origin = showReply ? Compatibility.ChatCommandOrigin.Player : Compatibility.ChatCommandOrigin.Script;
        if (Compatibility.RynthCoreChatCommands.TryHandle(cmd, origin)) return;
        if (cmd.StartsWith("/rc ", StringComparison.OrdinalIgnoreCase))
        {
            RynthLog.UI($"ServerMenus: '{cmd}' is not an engine command.");
            return;
        }
        Compatibility.ChatCommandDispatcher.Dispatch(cmd);
    }

    /// <summary>The command that opens section <paramref name="sectionId"/> in the Mini Remote.</summary>
    public static string OpenSectionCommand(string sectionId) => "/rc miniremote section " + sectionId;

    /// <summary>
    /// The command that makes this character's world count as <paramref name="serverId"/>: the
    /// Settings picker's own "pick" (that server on, every other off), so the picker then shows it
    /// instead of "Mixed". "/rc server &lt;id&gt; on" left the others on Auto.
    /// </summary>
    public static string ForceCommand(string serverId) => "/rc server pick " + serverId;

    /// <summary>Every server that has items (active or not), with them. Any thread.</summary>
    public static List<MenuGroup> Groups(SectionRegistry.Snapshot snap)
    {
        var byServer = new Dictionary<string, List<MenuItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (SectionManifest m in snap.Manifests)
            foreach (SectionContribution c in m.Contributions)
            {
                if (c.IsUniversal) continue;
                if (!byServer.TryGetValue(c.Server, out var list)) byServer[c.Server] = list = new List<MenuItem>();
                list.Add(c.IsSurface
                    ? new MenuItem(c.Label, OpenSectionCommand(c.Section), c.Tooltip, SectionFeaturePrefix + c.Section, c.Enabled, c.Note)
                    : new MenuItem(c.Label, c.Command, c.Tooltip, string.Empty, c.Enabled, c.Note));
            }
        var groups = new List<MenuGroup>();
        foreach (ServerState s in snap.Servers)
            if (byServer.TryGetValue(s.Id, out var items))
                groups.Add(new MenuGroup(s, items.ToArray()));
        return groups;
    }

    // ── RynthAi push ───────────────────────────────────────────────────

    /// <summary>
    /// Render thread, every frame: sends RynthAi the list when it changed (a server
    /// detected or forced, or not any more; a plugin's sections) and every <see cref="ResendMs"/>.
    /// </summary>
    public static void Tick()
    {
        SectionRegistry.Tick();   // the server verdicts (GetServerStateJson) and late server-plugin loads
        SectionRegistry.Snapshot snap = SectionRegistry.Current;
        List<string> serverPlugins = ServerPluginTargets(snap);
        if (!PushToRynthAi && !PushToRynthIlt && serverPlugins.Count == 0) return;
        long now = Environment.TickCount64;
        if (now < _nextCheckMs) return;
        _nextCheckMs = now + CheckMs;

        string payload = BuildPayload(snap, IltWorldNames());
        // Same resend rule as RynthILT's: a plugin may (re)load after the last change.
        foreach (string plugin in serverPlugins)
        {
            bool due = !_serverPluginPushes.TryGetValue(plugin, out var last)
                       || last.Payload != payload || now - last.AtMs >= IltResendMs;
            if (due && Plugins.PluginManager.SendPluginCommand(plugin, "servermenu", payload))
                _serverPluginPushes[plugin] = (payload, now);
        }
        if (PushToRynthAi && (payload != _lastPayload || now - _lastPushMs >= ResendMs))
        {
            _lastPayload = payload;
            _lastPushMs = now;
            RynthAiCommands.ApplyRemoteCommand("servermenu", payload);
        }
        // Not delivered (RynthILT not loaded yet, or an older one without the export): try again next check.
        if (PushToRynthIlt && (payload != _lastIltPayload || now - _lastIltPushMs >= IltResendMs)
            && Plugins.PluginManager.SendPluginCommand(RynthIltPlugin, "servermenu", payload))
        {
            _lastIltPayload = payload;
            _lastIltPushMs = now;
        }
    }

    /// <summary>
    /// The plugins to push to besides RynthAi and RynthILT (they have their own paths): each one
    /// whose sections manifest declares a server. Pure apart from reading <paramref name="snap"/>.
    /// </summary>
    internal static List<string> ServerPluginTargets(SectionRegistry.Snapshot snap)
    {
        var list = new List<string>();
        foreach (SectionManifest m in snap.Manifests)
        {
            if (m.Servers.Length == 0 || m.Plugin.Length == 0) continue;
            if (m.Plugin.Equals("RynthAi", StringComparison.OrdinalIgnoreCase)
                || (PushToRynthIlt && m.Plugin.Equals(RynthIltPlugin, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!list.Exists(p => p.Equals(m.Plugin, StringComparison.OrdinalIgnoreCase))) list.Add(m.Plugin);
        }
        return list;
    }

    /// <summary>The ILT world-name patterns in force, and whether they are the built-in defaults.</summary>
    private static (string Server, string Pattern, bool Default)[] IltWorldNames()
    {
        IReadOnlyList<string>? names = Plugins.EngineSettings.IltServerNames;
        IReadOnlyList<string> list = names ?? Compatibility.IltDetect.DefaultServerNames;
        var result = new (string, string, bool)[list.Count];
        for (int i = 0; i < list.Count; i++) result[i] = ("ilt", list[i], names == null);
        return result;
    }

    /// <summary>
    /// The push text for <paramref name="snap"/> and the servers' <paramref name="worldNames"/>.
    /// Pure apart from reading them.
    /// </summary>
    internal static string BuildPayload(SectionRegistry.Snapshot snap,
                                        IReadOnlyList<(string Server, string Pattern, bool Default)>? worldNames = null)
    {
        var sb = new StringBuilder(512);
        foreach (ServerState s in snap.Servers)
            sb.Append('V').Append('\t').Append(Clean(s.Id)).Append('\t').Append(Clean(s.Name)).Append('\t')
              .Append(s.Active ? '1' : '0').Append('\t').Append(ServerForceText.Name(s.Force)).Append('\t')
              .Append(s.Detected ? '1' : '0').Append('\t').Append(Clean(s.Reason ?? string.Empty)).Append('\n');
        if (worldNames != null)
            foreach (var n in worldNames)
                sb.Append('N').Append('\t').Append(Clean(n.Server)).Append('\t').Append(Clean(n.Pattern)).Append('\t')
                  .Append(n.Default ? "default" : "engine.json").Append('\n');
        foreach (SectionView v in snap.Sections)
            sb.Append('X').Append('\t').Append(Clean(v.Id)).Append('\t').Append(Clean(v.Label)).Append('\n');
        foreach (MenuGroup g in Groups(snap))
        {
            sb.Append('S').Append('\t').Append(Clean(g.Server.Id)).Append('\t').Append(Clean(g.Server.Name)).Append('\t')
              .Append(g.Server.Active ? '1' : '0').Append('\t').Append(Clean(ForceCommand(g.Server.Id))).Append('\n');
            foreach (MenuItem i in g.Items)
                sb.Append('I').Append('\t').Append(Clean(i.Label)).Append('\t').Append(Clean(i.Command)).Append('\t')
                  .Append(Clean(i.Note.Length > 0 && !i.Enabled ? i.Note : i.Tooltip)).Append('\t').Append(Clean(i.Feature)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Field text without the payload's separators (the push is ANSI, so ASCII only).</summary>
    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            sb.Append(c is '\t' or '\n' or '\r' ? ' ' : c < 128 ? c : '?');
        return sb.ToString();
    }

    // ── ImGui menus ────────────────────────────────────────────────────

    /// <summary>
    /// The server half of an ImGui popup menu: each active server's name and items. With
    /// servers that have items but none active, a note and one "Treat this world as ..." item
    /// each (saved for this character). Draws nothing (not even <paramref name="separatorFirst"/>'s
    /// separator) when no server has items. Returns true when it drew something.
    /// </summary>
    public static bool DrawImGuiMenu(bool separatorFirst = false)
    {
        List<MenuGroup> groups = Groups(SectionRegistry.Current);
        if (groups.Count == 0) return false;
        if (separatorFirst) ImGuiNET.ImGui.Separator();

        bool any = false;
        foreach (MenuGroup g in groups)
        {
            if (!g.Server.Active) continue;
            any = true;
            SafeImGui.TextDisabled("Server: " + g.Server.Name);
            foreach (MenuItem item in g.Items)
            {
                if (ImGuiNET.ImGui.MenuItem(item.Label + "##srv_" + g.Server.Id + "_" + item.Command, "", false, item.Enabled))
                    Run(item.Command);
                string tip = item.Enabled || item.Note.Length == 0 ? item.Tooltip : item.Note;
                if (tip.Length > 0) SafeImGui.SetItemTooltip(tip);
            }
        }
        if (any) return true;

        ImGuiNET.ImGui.TextDisabled("Server: no installed server plugin matches this world");
        foreach (MenuGroup g in groups)
        {
            if (ImGuiNET.ImGui.MenuItem("Treat this world as " + g.Server.Name + "##srvforce_" + g.Server.Id))
                Run(ForceCommand(g.Server.Id), showReply: true);
            SafeImGui.SetItemTooltip("Saved for this character. Undo: Settings > Game Client > Server > Auto (detect), or /rc server pick auto.");
        }
        return true;
    }

    /// <summary>
    /// A "Sections" submenu: every Mini Remote section, opening it there. Returns true when
    /// one was picked.
    /// </summary>
    public static bool DrawSectionsMenu(string label = "Sections")
    {
        SectionRegistry.Snapshot snap = SectionRegistry.Current;
        if (snap.Sections.Count == 0 || !ImGuiNET.ImGui.BeginMenu(label)) return false;
        bool picked = false;
        foreach (SectionView v in snap.Sections)
        {
            if (ImGuiNET.ImGui.MenuItem(v.Label + "##sec_" + v.Id, "", false, v.HasContent || v.Core))
            {
                Run(OpenSectionCommand(v.Id));
                picked = true;
            }
        }
        ImGuiNET.ImGui.EndMenu();
        return picked;
    }
}

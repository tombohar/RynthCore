using System;
using S = RynthCore.Engine.ImGuiBackend.Hud.MonsterHudSettings;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Engine-level <c>/rc ...</c> chat commands. Intercepted on every chat-entry
/// path BEFORE plugin pre-dispatch and before the line reaches AC's chat:
/// <list type="bullet">
///   <item>typed in-game — <see cref="ChatCallbackHooks"/> OutgoingChatDetour</item>
///   <item>dispatch.txt / OnLoginCommandRunner / Host.InvokeChatParser —
///         <see cref="ChatCommandDispatcher.Dispatch"/></item>
/// </list>
/// Every command answers (2026-10-28): what changed and the value now, or why not with the
/// usage; an unknown verb or arguments a verb can't take get the usage before anything runs
/// (<see cref="ChatCommandReplies.Check"/>). Typed by the player (AC's chat bar, RynthChat's box,
/// a hotkey, or a plugin passing on a typed line) every reply goes to chat; sent by a plugin, a
/// command file, the login commands or an engine button, a success goes to the log only and a
/// failure still to chat (<see cref="ChatCommandOrigin"/>). All replies go through Reply / Fail.
/// Commands:
/// <list type="bullet">
///   <item><c>/rc resetbar</c> — recover the RynthCore overlay bar when it has
///         been dragged off the visible client area (its in-overlay "Rs" reset
///         button rides the bar itself, so it's unreachable once the bar is).
///         The launcher button writes this to dispatch.txt as the out-of-band
///         recovery.</item>
///   <item><c>/rc actionstate</c> (alias <c>/rc actions</c>) — read-only: the client's
///         item-action gates (pending item request, attacking flag, busy count,
///         target mode, combat state), see <see cref="ClientActionGates"/>.
///         <c>/rc unlockactions</c> (alias <c>/rc unlock</c>) opens those gates now.</item>
///   <item><c>/rc vitals</c> (alias <c>/rc hud</c>) — toggle the custom D3D9
///         Health/Stamina/Mana HUD on/off (persisted to engine.json).</item>
///   <item><c>/rc ui retailvitals [on|off|status]</c> — hide AC's own
///         health/stamina/mana bars (<see cref="RetailVitalsHooks"/>; saved for the
///         PC, also in Settings &gt; UI). Alone it reports the state.</item>
///   <item><c>/rc ui retailtoolbar [on|off|status]</c> — hide AC's toolbar panel and
///         its shortcut slots (<see cref="RetailToolbarHooks"/>; the shortcut keys keep
///         working; saved for the PC, also in Settings &gt; UI).</item>
///   <item><c>/rc ui barsize [0-10|+|-|status]</c> - the RynthCore bar's size (its own text
///         size step, <see cref="UI.PanelTextScale"/>; saved for the PC, also in Settings &gt;
///         Display). Alone it reports the size.</item>
///   <item><c>/rc ui barrows [1-6|+|-|status]</c> and <c>/rc ui baricons [2-30|none|+|-|status]</c>
///         - the bar's rows and the most icons a row holds (none = no limit; <see cref="UI.BarLayout"/>,
///         saved for the PC, also in the grip's menu and Settings &gt; Display). Alone each reports both.</item>
///   <item><c>/rc vendor [list|info|status|buy &lt;id|sel&gt; [n]|sell &lt;id|sel&gt; [id ...]]</c>
///         — read / trade with the open vendor through the engine's vendor
///         primitives (<see cref="VendorTrade"/>); a manual test hook for the
///         plugin API. Replies go to chat from the main-thread drain and to the log.</item>
///   <item><c>/rc chathold [on|off|status]</c> — incoming chat waits for the plugins'
///         verdict before AC prints it, so a line a plugin eats is kept out of AC's chat
///         window too (<see cref="ChatCallbackHooks"/>). On at every start; not saved.</item>
///   <item><c>/rc discord [on|off|status|window|test [route]|resume [route]]</c> — RynthChat's
///         Discord relay (<see cref="UI.Data.Discord.ChatDiscord"/>): switch it on or off for the
///         character you're playing (off for every character by default), list the routes,
///         post a test line, clear a pause; window opens its panel (Chat &gt; gear &gt; Discord).</item>
///   <item><c>/rv plates</c> (aliases <c>/rv nameplates</c>, <c>/rc plates</c>) — the
///         RynthVision nameplates and combat text: monster plates on|off, self, debuffs,
///         numbers, gains [on|off] (with their sub-switches), all on|off, all|engaged,
///         dist/max/scale/opacity/lift &lt;n&gt;, field toggles, reset, status
///         (<see cref="ImGuiBackend.Hud.MonsterHud"/>). "/rv plates" is caught here,
///         before plugin pre-dispatch, so the RynthVision plugin's own "/rv" handler
///         never sees it; every other "/rv" line still goes to the plugin.</item>
///   <item><c>/rc server [auto|aelrynth|other]</c> — which server the engine detects
///         (<see cref="ServerInfo"/>) and why; aelrynth/other force it for this session.</item>
///   <item><c>/rc hooks [on|off &lt;name&gt;]</c> (alias <c>/rc uihooks</c>) — the UI hooks
///         (screen mode, client cleanup, tooltip, drag/drop; <see cref="UiHookRegistry"/>):
///         status, or switch one off/on (now and in engine.json).</item>
///   <item><c>/rc video [status|enable|disable|off|world|unfocused|all|fps &lt;n&gt;]</c> (alias
///         <c>/rc videopatch</c>; "on" / "enable" = world, "disable" = off; RynthAi forwards /ra and /ub videopatch) — the video patch
///         (<see cref="D3D9.VideoPatch"/>): stop drawing AC's 3D world (world), only while
///         the client is in the background (unfocused), or AC's UI too (all, session only).
///         Saved per character (also Settings &gt; Misc); fps = the frame cap while patched.</item>
///   <item><c>/rc mastery</c> — the Aelrynth mastery feed's state (<see cref="MasteryFeed"/>),
///         and ask the server for /mastery-data again.</item>
///   <item><c>/rc worldlayer [auto|uipass|transition|endscene]</c> — where the world overlays
///         (nameplates, combat text, Nav3D markers) draw in AC's frame
///         (<see cref="D3D9.Nav3DRenderInjector"/>): alone it reports the path and counters;
///         a mode forces it for this session (A/B checks). Auto at every start; not saved.</item>
///   <item><c>/rc acmouse [watch [seconds]]</c> (alias <c>/rc mouseover</c>) - what AC's own UI
///         has under the cursor (<see cref="AcMouseOver"/>: the 3D world or one of its windows,
///         with the element's id and type), i.e. whether a nameplate there may take the click;
///         watch = every change for a while (default 20 s), to chat and the log.</item>
///   <item><c>/rc netmsg [status|on|off|log on|off|capture on|off]</c> - the reassembled
///         server-message stream for plugins (<see cref="Net.ServerMessageStream"/>, API v78):
///         counters; on/off for this session (engine.json "ServerMessageStream": false is the
///         kill switch); log = the SDK parsers run on live messages, results to the log;
///         capture = raw datagrams to a .rnc file for tools/NetMessageTests.</item>
///   <item><c>/rc flightdump [note]</c> - write the flight recorder (<see cref="Net.FlightRecorder"/>:
///         the last minutes of client/server messages and engine markers) to
///         Logs\flight-&lt;pid&gt;-&lt;time&gt;.txt; <c>/rc flightdump auto &lt;reason&gt;</c> is the rate-limited
///         automatic form. <c>/rc flight [status|mark &lt;text&gt;|on|off|auto on|off]</c>.</item>
///   <item><c>/rc quit</c> / <c>/rc exit</c> - closes the client cleanly, as its close button does.</item>
///   <item><c>/rc report [text]</c> - "Report a problem" (<see cref="Report.ProblemReport"/>): alone it
///         opens the panel; with text it sends that report now, with the recent log attached
///         (the panel is where the log can be left out). One report a minute.</item>
///   <item><c>/rc autoid [all|limited|off]</c> - which background appraisals the engine's AutoId
///         sends (<see cref="AutoIdService"/>, <see cref="AutoIdPolicy"/>; saved in engine.json);
///         alone it reports the mode and the identify counts by who asked.</item>
///   <item><c>/rc hotkeys</c> - opens Settings &gt; Hotkeys. <c>/rc hotkey list | add &lt;combo&gt; &lt;action or
///         /command&gt; | remove &lt;combo&gt; | enable|disable &lt;combo&gt; | actions | status | on|off</c> - the user
///         hotkeys (<see cref="Hotkeys.HotkeyService"/>; saved for the PC in hotkeys.txt).</item>
///   <item><c>/rc diagnostics</c> (or <c>/rc serverpick</c>) - opens Settings &gt; Diagnostics, the server
///         picker: the one place a server is forced for this character (plugins' "Force a server" links).</item>
///   <item><c>/rc layout [status] | default | reset [confirm]</c> - the panel layout this client uses
///         (<see cref="UI.PanelLayout"/>: its character's own, or the PC-wide default and why) and its files;
///         default makes this character's layout the one new characters start from; reset (then reset
///         confirm) puts this character back to the default. Also Settings &gt; Display.</item>
///   <item><c>/rc uicost [on|off|&lt;ms&gt;|now|status]</c> - the UI frame cost breakdown
///         (<see cref="ImGuiBackend.UiCost"/>): timing on or off, the slow-frame threshold, and (now, or
///         alone) which parts of the UI frame took the time over the last seconds. Also Settings &gt; Diagnostics.</item>
/// </list>
/// </summary>
internal static class RynthCoreChatCommands
{
    private const string Prefix = "/rc ";
    /// <summary>RynthNet's Characters window (a plugin window: its title is its panel key).</summary>
    private const string CharactersWindowTitle = "Characters";

    /// <summary>The Settings tab with the server picker (UI/Data/SettingsData.cs "Diagnostics").</summary>
    private const string DiagnosticsTabName = "Diagnostics";

    // The line being handled on this thread: who sent it, and whether it has answered yet.
    [ThreadStatic] private static ChatCommandOrigin _origin;
    [ThreadStatic] private static bool _answered;

    // Above 0 while a line the player typed is handed on (to the plugins, or by RynthChat's box).
    [ThreadStatic] private static int _playerLineDepth;

    /// <summary>
    /// Until disposed, every line handled on this thread is the player's: a /rc command a plugin runs
    /// while it handles a line the player typed answers in chat (RynthAi's /ra videopatch runs
    /// /rc videopatch through Host.InvokeChatParser, on the same thread, before returning).
    /// <c>using (RynthCoreChatCommands.PlayerLine()) PluginManager.DispatchChatBarEnter(line);</c>
    /// </summary>
    internal static PlayerLineScope PlayerLine()
    {
        _playerLineDepth++;
        return default;
    }

    /// <summary>Ends a <see cref="PlayerLine"/>.</summary>
    internal readonly struct PlayerLineScope : IDisposable
    {
        public void Dispose()
        {
            if (_playerLineDepth > 0) _playerLineDepth--;
        }
    }

    /// <summary>
    /// True if <paramref name="line"/> was a RynthCore engine command and has
    /// been handled — the caller must consume it (do NOT forward to AC chat
    /// or to plugins). Every handled line answers (<see cref="ChatCommandReplies"/>):
    /// in chat for the <paramref name="origin"/> Player; for a Script, a success goes
    /// to the log only and a failure to chat.
    /// </summary>
    public static bool TryHandle(string? line, ChatCommandOrigin origin)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        string trimmed = line.Trim();
        if (!IsEngineLine(trimmed))
            return false;

        ChatCommandOrigin outerOrigin = _origin;
        bool outerAnswered = _answered;
        _origin = ChatCommandReplies.Effective(origin, _playerLineDepth > 0);
        _answered = false;
        try
        {
            try { Handle(trimmed); }
            catch (Exception ex) { Failed(ChatCommandReplies.CommandName(trimmed), ex); }
            if (!_answered)
            {
                // A handler that said nothing is a gap to fill (this log line names it), never silence.
                RynthLog.Compat($"RynthCoreChatCommands: '{trimmed}' wrote no reply.");
                Reply(ChatCommandReplies.Done(ChatCommandReplies.CommandName(trimmed)));
            }
            return true;
        }
        finally
        {
            _origin = outerOrigin;
            _answered = outerAnswered;
        }
    }

    /// <summary>"/rc", "/rc ..." and "/rv plates|nameplates ..." are the engine's; every other "/rv" line
    /// goes on to the RynthVision plugin.</summary>
    private static bool IsEngineLine(string trimmed)
    {
        if (trimmed.Equals("/rc", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!trimmed.StartsWith("/rv ", StringComparison.OrdinalIgnoreCase))
            return false;
        string rv = trimmed.Substring(4).Trim();
        return IsVerb(rv, "plates", out _) || IsVerb(rv, "nameplates", out _);
    }

    /// <summary>Runs one engine line (<see cref="IsEngineLine"/>). An exception is answered by TryHandle.</summary>
    private static void Handle(string trimmed)
    {
        // /rv plates ... : the RynthVision overlays live engine-side, so the engine answers
        // before the plugin's "/rv" prefix handler would (it runs after this, on pre-dispatch).
        if (trimmed.StartsWith("/rv ", StringComparison.OrdinalIgnoreCase))
        {
            string rv = trimmed.Substring(4).Trim();
            if (!IsVerb(rv, "plates", out string rvArgs))
                IsVerb(rv, "nameplates", out rvArgs);
            HandlePlatesCommand(rvArgs);
            return;
        }

        string sub = trimmed.Length > 3 ? trimmed.Substring(3).Trim() : string.Empty;

        // An unknown verb, or arguments the verb can't take, answers with the usage before anything
        // runs ("/rc" alone lists the commands). Commands with their own handler check theirs there.
        ChatVerbCheck check = ChatCommandReplies.Check(sub);
        if (check.Problem != null)
        {
            Fail(check.Problem);
            return;
        }

        // /rc vendor [list|info|status|buy <id|sel> [n]|sell <id|sel> [id ...]]: VendorTrade's own
        // "[RC]" lines are the reply (to chat from the main-thread drain, and to the log).
        if (IsVerb(sub, "vendor", out string vendorArgs))
        {
            VendorTrade.HandleChatCommand(vendorArgs);
            AnswersItself();
            return;
        }

        // /rc imgui [on|off|fonttest|diag]
        if (IsVerb(sub, "imgui", out string imguiArgs))
        {
            HandleImGuiCommand(imguiArgs);
            return;
        }

        // /rc ui list | open|close|popout|redock|snap <Panel> | bar settings (HandleUiCommand)
        if (IsVerb(sub, "ui", out string uiArgs))
        {
            HandleUiCommand(uiArgs);
            return;
        }

        // /rc chathold [on|off|status]: lines a plugin eats stay out of AC's chat window too
        if (IsVerb(sub, "chathold", out string holdArgs))
        {
            HandleChatHoldCommand(holdArgs);
            return;
        }

        // /rc discord [on|off|status|window|test [route]|resume [route]] : RynthChat's Discord relay
        if (IsVerb(sub, "discord", out string discordArgs))
        {
            HandleDiscordCommand(discordArgs);
            return;
        }

        // /rc plates [on|off|all|engaged|dist N|max N|scale X|opacity X|lift X|names|hp|level|weak|distance|fade|reset|status]
        if (IsVerb(sub, "plates", out string platesArgs) || IsVerb(sub, "nameplates", out platesArgs))
        {
            HandlePlatesCommand(platesArgs);
            return;
        }

        // /rc server [<server> auto|on|off | names ilt ...] : the servers, why each is active, per-character overrides
        if (IsVerb(sub, "server", out string serverArgs))
        {
            HandleServerCommand(serverArgs);
            return;
        }

        // /rc ilt [auto|on|off] : is this Infinite Leaftide (the ILT section), with a per-character override
        if (IsVerb(sub, "ilt", out string iltArgs))
        {
            HandleIltCommand(iltArgs);
            return;
        }

        // /rc mastery : the Aelrynth mastery feed's state, and ask the server again
        if (IsVerb(sub, "mastery", out _))
        {
            HandleMasteryCommand();
            return;
        }

        // /rc worldlayer [auto|uipass|transition|endscene] : where the world overlays draw
        if (IsVerb(sub, "worldlayer", out string worldArgs))
        {
            HandleWorldLayerCommand(worldArgs);
            return;
        }

        // /rc acmouse [watch [seconds]] : what AC's own UI has under the cursor (the 3D world or
        // one of its windows) - whether a nameplate there may take the click (AcMouseOver)
        if (IsVerb(sub, "acmouse", out string acMouseArgs) || IsVerb(sub, "mouseover", out acMouseArgs))
        {
            string[] p = acMouseArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length > 0 && p[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
            {
                int secs = p.Length > 1 && int.TryParse(p[1], out int n) ? n : 20;
                AcMouseOver.Watch(secs);
                Reply($"Watching AC's mouse-over for {Math.Clamp(secs, 1, 120)} s: move the cursor over the world and over AC's windows.");
            }
            else
            {
                Reply(AcMouseOver.Describe());
            }
            return;
        }

        // /rc video [status|off|world|unfocused|all|fps <n>] : the video patch (stop drawing the 3D world)
        if (IsVerb(sub, "video", out string videoArgs) || IsVerb(sub, "videopatch", out videoArgs))
        {
            ReplyLines(D3D9.VideoPatch.HandleCommand(videoArgs));
            return;
        }

        // /rc hooks [on|off <name>] : the UI hooks (screen mode, client cleanup, tooltip, drag/drop)
        if (IsVerb(sub, "hooks", out string hooksArgs) || IsVerb(sub, "uihooks", out hooksArgs))
        {
            HandleHooksCommand(hooksArgs);
            return;
        }

        // /rc flightdump [note] | /rc flightdump auto <reason> : write the flight recorder now
        if (IsVerb(sub, "flightdump", out string dumpArgs))
        {
            ReplyLines(Net.FlightRecorder.HandleDumpCommand(dumpArgs));
            return;
        }

        // /rc quit | /rc exit : close the client cleanly (as its close button; the character logs off)
        if (IsVerb(sub, "quit", out _) || IsVerb(sub, "exit", out _))
        {
            Reply("Closing the client.");
            ServerDisconnectClose.CloseClientNow("/rc quit");
            return;
        }

        // /rc report [text] : Report a problem - alone it opens the panel; with text it sends now (with the log)
        if (IsVerb(sub, "report", out string reportArgs))
        {
            var lines = new System.Collections.Generic.List<string>(Report.ProblemReport.HandleCommand(reportArgs));
            if (lines.Count == 0)
                Reply("Report a problem: the panel is open (or /rc report <what went wrong> sends one from chat).");
            else
                ReplyLines(lines);
            return;
        }

        // /rc flight [status|mark <text>|on|off|auto on|off] : the flight recorder
        if (IsVerb(sub, "flight", out string flightArgs))
        {
            ReplyLines(Net.FlightRecorder.HandleCommand(flightArgs));
            return;
        }

        // /rc autoid [all|limited|off] : which background appraisals AutoId sends (alone: status)
        if (IsVerb(sub, "autoid", out string autoIdArgs))
        {
            ReplyLines(AutoIdService.HandleCommand(autoIdArgs));
            return;
        }

        // /rc uicost [on|off|<ms>|now|status] : the UI frame cost breakdown (ImGui/UiCost; alone: now)
        if (IsVerb(sub, "uicost", out string uiCostArgs))
        {
            ReplyLines(ImGuiBackend.UiCost.HandleCommand(uiCostArgs));
            return;
        }

        // /rc netmsg [status|on|off|log on|off|capture on|off] : the v78 server message stream
        if (IsVerb(sub, "netmsg", out string netArgs))
        {
            ReplyLines(Net.ServerMessageStream.HandleCommand(netArgs));
            return;
        }

        // /rc vtank [clear] : what the engine knows about VTank's macro (Decal bridge only);
        // clear = VTank was stopped with its own button, which the engine can't see.
        if (IsVerb(sub, "vtank", out string vtankArgs))
        {
            if (vtankArgs.Equals("clear", StringComparison.OrdinalIgnoreCase))
                Reply(VTankWatch.Clear() ? "VTank marked as not running; RynthAi's macro can start again." : "VTank was already marked as not running.");
            Reply(VTankWatch.Describe());
            return;
        }

        // /rc bounties|guardian [show|hide|toggle] : the ILT section's panels, like the bar's
        // ILT button (the RynthILT plugin's bounty tracker and Temple guardian tools). Only on
        // Infinite Leaftide with RynthILT installed.
        if (IsVerb(sub, "bounties", out string panelArgs) || IsVerb(sub, "bounty", out panelArgs) || IsVerb(sub, "bountys", out panelArgs))
        {
            IltPanel(ImGuiBackend.Panels.BountiesFace.Title, "Bounties", "RynthILT", panelArgs);
            return;
        }
        if (IsVerb(sub, "guardian", out panelArgs) || IsVerb(sub, "guardians", out panelArgs))
        {
            IltPanel(ImGuiBackend.Panels.GuardianFace.Title, "Guardian", "RynthILT", panelArgs);
            return;
        }

        // /rc dashboard [classic|modern|tab <name>] : the RynthAi dashboard's look (the title bar buttons and
        // Settings > Display), and the Classic look's tab (main, monsters, gear, buffs, route, loot, meta,
        // tuning); either opens the dashboard. No argument: says which look is on.
        if (IsVerb(sub, "dashboard", out string dashArgs))
        {
            DashboardCommand(dashArgs.Trim());
            return;
        }

        // /rc miniremote [show|hide|toggle|tab <tab>|section <id>] : the ImGui Mini Remote panel, like
        // its bar button (RynthAi's dashboard and "/ra hub" use show / hide). "tab" opens it on one of
        // its tabs (RynthAi's pets, crafting; RynthILT's gear, games; none = the remote). Quests are
        // RynthOracle's window (/ro) since 2026-10-13. Since 2026.10.5.54 the old "character" tab
        // (the generic overview) opens RynthOracle's Character tab (/ro character). Infinite
        // Leaftide's own tabs ("charms", "banking", "gear", "games") stay RynthILT's, in this window.
        // "section" opens it on one of its sections (char = Session Stats, map, skills, ...; "remote" = the remote).
        if (IsVerb(sub, "miniremote", out string remoteArgs) || IsVerb(sub, "remote", out remoteArgs))
        {
            MiniRemoteCommand(remoteArgs);
            return;
        }

        // /rc oracle-skills <skills|attrs|progress|off> : RynthOracle says which SkillsFace body to embed
        // in its window (engine bridge; Silentkelpie's design). Any valid form also tells the engine that
        // Oracle has the tabs, which hides the standalone Skills bar icon and routes /rc skills there.
        if (IsVerb(sub, "oracle-skills", out string oracleSkillsArgs))
        {
            int tab = ChatCommandReplies.OracleSkillsTab(oracleSkillsArgs);
            if (tab == ChatCommandReplies.InvalidTab)
            {
                Fail(ChatCommandReplies.Usage("oracle-skills"));
                return;
            }
            ImGuiBackend.Panels.OracleSkillsEmbed.Announce();
            if (tab < 0) ImGuiBackend.Panels.OracleSkillsEmbed.Clear();
            else ImGuiBackend.Panels.OracleSkillsEmbed.SetActiveTab(tab);
            Reply(ChatCommandReplies.OracleSkillsReply(tab));
            return;
        }

        // /rc skills [show|hide|toggle] : RynthOracle's Skills tab when Oracle provides it (see
        // SkillsRouting), else the standalone ImGui Skills panel. /rc progression (or progress) opens the
        // Progress tab / the panel's Progression tab (ILT worlds; it shows once RynthILT reports ILT features).
        if (IsVerb(sub, "skills", out string skillsArgs))
        {
            if (!TryPanelMode(skillsArgs, out bool? show))
            {
                Fail(ChatCommandReplies.Usage("skills"));
                return;
            }
            bool oracle = ImGuiBackend.Panels.SkillsRouting.OracleTabsAvailable;
            bool wasShowing = oracle
                ? UI.ScriptWindows.ScriptWindowRegistry.IsOpen(ImGuiBackend.Panels.SkillsRouting.OracleKey)
                  && ImGuiBackend.Panels.OracleSkillsEmbed.ActiveTab == 0
                : ImGuiBackend.ImGuiPanelHost.IsOpen(ImGuiBackend.Panels.SkillsFace.Title);
            if (!ImGuiBackend.Panels.SkillsRouting.SetOpen(show))
                Fail("The Skills panel isn't available (the ImGui layer is off?).");
            else
                Reply(ChatCommandReplies.PanelReply(oracle ? "RynthOracle's Skills tab" : "Skills panel", show, wasShowing));
            return;
        }
        if (IsVerb(sub, "progression", out _) || IsVerb(sub, "progress", out _))
        {
            bool oracle = ImGuiBackend.Panels.SkillsRouting.OracleTabsAvailable;
            if (!ImGuiBackend.Panels.SkillsRouting.Open(2))
                Fail("The Skills panel isn't available (the ImGui layer is off?).");
            else
                Reply(oracle ? "RynthOracle's Progress tab opened." : "Skills panel opened on Progression.");
            return;
        }

        // /rc spells [show|hide|toggle] : RynthAi's Spells window (spell catalog, buff profiles).
        if (IsVerb(sub, "spells", out string spellsArgs))
        {
            if (!ImGuiBackend.ImGuiPanelHost.HasFace(ImGuiBackend.Panels.RynthAiFace.Title))
                Fail("The Spells window comes with RynthAi; it isn't installed.");
            else if (!TryPanelMode(spellsArgs, out bool? show))
                Fail(ChatCommandReplies.Usage("spells"));
            else
            {
                UI.Data.RynthAiCommands.ApplyRemoteCommand("spells", show == null ? "toggle" : show.Value ? "show" : "hide");
                Reply(show == null ? "Opening or closing RynthAi's Spells window (toggle)."
                    : show.Value ? "Opening RynthAi's Spells window." : "Closing RynthAi's Spells window.");
            }
            return;
        }

        // /rc hotkeys : Settings > Hotkeys. /rc hotkey list|add|remove|enable|disable|actions|status|on|off
        // (Hotkeys/HotkeyService.cs; "/rc hotkeys <verb>" works too).
        if (IsVerb(sub, "hotkeys", out string hotkeyArgs) && hotkeyArgs.Length == 0)
        {
            if (!ImGuiBackend.ImGuiPanelHost.HasFace(ImGuiBackend.Panels.SettingsFace.Title))
                Fail("The Settings panel isn't available (the ImGui layer is off?). /rc hotkey list|add|remove work here in chat.");
            else
                OpenSettingsTab(ImGuiBackend.Panels.HotkeysSettingsUi.TabName);
            return;
        }
        // /rc diagnostics : Settings > Diagnostics, where a server is forced for this character (the
        // server picker). The one place to force a server since 2026.10.5.54, so plugins link here.
        if ((IsVerb(sub, "diagnostics", out string diagArgs) || IsVerb(sub, "serverpick", out diagArgs)) && diagArgs.Length == 0)
        {
            if (!ImGuiBackend.ImGuiPanelHost.HasFace(ImGuiBackend.Panels.SettingsFace.Title))
                Fail("The Settings panel isn't available (the ImGui layer is off?). /rc server <id> auto|on|off works here in chat.");
            else
                OpenSettingsTab(DiagnosticsTabName);
            return;
        }
        // /rc settings [tab] : the Settings panel (toggled), or opened on a tab ("/rc settings buffing").
        // A plugin's own window links to a tab this way (RynthAi's dashboard: the Buffing key's
        // right-click), as it can't reach the engine's panel otherwise. 2026-10-21.
        // /rc shortcuts [use N]: the player's 18 AC shortcut slots and their keys (AcShortcuts,
        // AcKeymap), or use slot N the way its key does. 2026-10-22, for checking the item bar's base.
        if (IsVerb(sub, "shortcuts", out string shortcutArgs))
        {
            string[] sa = shortcutArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (sa.Length >= 2 && sa[0].Equals("use", StringComparison.OrdinalIgnoreCase) && int.TryParse(sa[1], out int useSlot))
            {
                Answer(AcShortcuts.Enqueue(AcShortcuts.Op.Use, useSlot - 1, 0),
                    $"Using shortcut {useSlot}.", $"Shortcut {useSlot} can't be used ({AcShortcuts.Status})");
                return;
            }
            // /rc shortcuts set N: the selected item into slot N; clear N: empty it (the retail toolbar's way).
            if (sa.Length >= 2 && sa[0].Equals("set", StringComparison.OrdinalIgnoreCase) && int.TryParse(sa[1], out int setSlot))
            {
                uint sel = sa.Length >= 3 && uint.TryParse(sa[2].Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                    System.Globalization.NumberStyles.HexNumber, null, out uint given) ? given : ClientHelperHooks.GetSelectedItemId();
                if (sel == 0) Fail("Select the item first.");
                else Answer(AcShortcuts.Enqueue(AcShortcuts.Op.Set, setSlot - 1, sel),
                    $"Shortcut {setSlot}: 0x{sel:X8}.", $"Shortcut {setSlot} can't be set ({AcShortcuts.Status})");
                return;
            }
            if (sa.Length >= 2 && sa[0].Equals("clear", StringComparison.OrdinalIgnoreCase) && int.TryParse(sa[1], out int clearSlot))
            {
                Answer(AcShortcuts.Enqueue(AcShortcuts.Op.Clear, clearSlot - 1, 0),
                    $"Shortcut {clearSlot} emptied.", $"Shortcut {clearSlot} can't be emptied ({AcShortcuts.Status})");
                return;
            }
            AcShortcuts.Slot[] slots = AcShortcuts.Slots;
            if (slots.Length == 0) { Fail("No shortcut slots read yet (in the world? " + AcShortcuts.Status + ")"); return; }
            Reply($"Shortcuts (keymap {System.IO.Path.GetFileName(AcKeymap.FilePath)}):");
            for (int i = 0; i < slots.Length; i++)
            {
                uint id = slots[i].ObjectId;
                string what = id == 0 ? "-" : ClientObjectHooks.TryGetSnapshotName(id, out string n) && n.Length > 0 ? n : $"0x{id:X8}";
                string keys = AcKeymap.KeysText($"UseQuickSlot_{i + 1}");
                Reply($"  {i + 1,2}: {what}{(keys.Length > 0 ? "  [" + keys + "]" : "")}");
            }
            return;
        }

        // /rc keymap [check|bind|unbind <action> <key> | reload]: AC's keys, changed so every client follows
        // (AcKeymapLive). <action> is a keymap action (UseQuickSlot_5) or a quoted-free /command. 2026-10-22.
        if (IsVerb(sub, "keymap", out string keymapArgs))
        {
            string[] ka = keymapArgs.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            string verb = ka.Length > 0 ? ka[0].ToLowerInvariant() : "";
            if (verb is "check" or "bind" or "unbind" && ka.Length == 3)
            {
                // The key is the last word; the action is everything between (a command has spaces).
                int last = ka[2].LastIndexOf(' ');
                string action = last < 0 ? ka[1] : ka[1] + " " + ka[2][..last];
                string key = last < 0 ? ka[2] : ka[2][(last + 1)..];
                if (verb == "check")
                {
                    int r = AcKeymapLive.Check(action, key, out string msg);
                    if (r == 0) Reply($"{key} is free for {action}.");
                    else Fail(msg);
                }
                else if (!AcKeymapLive.Enqueue(action, key, verb == "bind" ? AcKeymapLive.Op.Bind : AcKeymapLive.Op.Unbind, out string why))
                    Fail(why);
                else
                    AnswersItself();   // AC's thread says how the change went (AcKeymapLive.DrainRequests)
                return;
            }
            if (verb == "dump")
            {
                string dumpPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rynthcore-keymap-memory.keymap");
                AcKeymapLive.RequestDump(dumpPath);
                if (AcKeymapLive.CanChange)
                    AnswersItself();   // written on AC's next tick, which says so in chat (AcKeymapLive.Tick)
                else
                    Fail($"AC's keys can't be read here yet ({AcKeymapLive.Status}); they're written to {dumpPath} once they can.");
                return;
            }
            Reply($"Keymap {AcKeymap.FilePath} - {AcKeymapLive.Status} Usage: /rc keymap check|bind|unbind <action> <key> | dump, e.g. /rc keymap bind UseQuickSlot_10 F11");
            return;
        }

        // /rc panels [open|close|toggle <id>]: AC's own panels (AcPanels): their ids, classes and which are open.
        if (IsVerb(sub, "panels", out string acPanelArgs))
        {
            string[] pa = acPanelArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (pa.Length == 2 && ChatCommandReplies.TryPanelId(pa[1], out uint panelId))
            {
                AcPanels.Op op = pa[0].ToLowerInvariant() switch { "open" => AcPanels.Op.Open, "close" => AcPanels.Op.Close, _ => AcPanels.Op.Toggle };
                if (!AcPanels.Enqueue(panelId, op)) Fail("Can't: " + AcPanels.Status);
                else Reply($"AC panel {panelId}: {op.ToString().ToLowerInvariant()} requested.");
                return;
            }
            var panels = AcPanels.List(names: true);
            Reply($"AC panels ({panels.Count}; {AcPanels.Status}):");
            foreach (var p in panels)
                Reply($"  {p.Id,3} {p.Class}{(p.Open ? " - open" : "")}");
            return;
        }

        if (IsVerb(sub, "settings", out string settingsArgs))
        {
            SettingsCommand(settingsArgs);
            return;
        }
        if (IsVerb(sub, "sense", out string senseArgs) && senseArgs.Length > 0)
        {
            SenseLayoutCommand(senseArgs);   // alone, /rc sense opens or closes the panel (the switch below)
            return;
        }
        // /rc layout [status] | default | reset [confirm]: this client's panel layout (UI/PanelLayout).
        if (IsVerb(sub, "layout", out string layoutArgs))
        {
            HandleLayoutCommand(layoutArgs);
            return;
        }
        if (IsVerb(sub, "hotkey", out hotkeyArgs) || IsVerb(sub, "hotkeys", out hotkeyArgs))
        {
            // The parser's own errors (an unknown verb, a bad key) are the failures; the rest is what changed.
            bool bad = Hotkeys.HotkeyCommandParser.Parse(hotkeyArgs).Verb == Hotkeys.HotkeyVerb.Error;
            ReplyLines(Hotkeys.HotkeyService.HandleCommand(hotkeyArgs), bad ? ChatReplyKind.Fail : ChatReplyKind.Ok);
            return;
        }

        switch (sub.ToLowerInvariant())
        {
            case "rl":
            case "reload":
                // Same as the bar's RL button. Signalled from a pool thread: the
                // loader's reload shuts this engine down, and this handler runs
                // inside AC's outgoing-chat hook.
                RynthLog.Compat("RynthCoreChatCommands: /rc rl — engine reload requested.");
                Reply("Reloading RynthCore (as the bar's RL button).");
                EntryPoint.NoteManualReload();   // by hand: not held for combat (EntryPoint.DeferReloadWhilePluginsObject)
                System.Threading.ThreadPool.UnsafeQueueUserWorkItem(static _ => EngineLifecycle.SignalReload(), null);
                return;

            case "inv":
            case "inventory":
                // The ImGui Inventory panel (open/close), like its bar button.
                TogglePanel(ImGuiBackend.Panels.InventoryFace.Title, "Inventory panel",
                    "The Inventory panel isn't available (the ImGui layer is off?).");
                return;

            case "sense":
                // The ImGui Sense panel (open/close), like its bar button.
                TogglePanel(ImGuiBackend.Panels.SenseFace.Title, "Sense panel",
                    "The Sense panel isn't available (the ImGui layer is off?).");
                return;

            case "characters":
            case "chars":
                // The Characters window: RynthNet's own since 2026-10-21 (a plugin window, keyed by its title).
                TogglePanel(CharactersWindowTitle, "Characters window",
                    "The Characters window comes with the RynthNet plugin (add it in the launcher's Plugins tab).");
                return;

            case "itemcount":
            case "itemhud":
                // The ImGui Item Count panel (open/close), like its bar button.
                TogglePanel(ImGuiBackend.Panels.ItemCountFace.Title, "Item Count panel",
                    "The Item Count panel isn't available (the ImGui layer is off?).");
                return;

            case "resetbar":
            case "barreset":
                RynthLog.Compat("RynthCoreChatCommands: /rc resetbar — resetting overlay bar position.");
                ImGuiBackend.RynthCoreShell.RequestExternalReset();
                Reply("RynthCore bar moved back to the top-left corner.");
                return;

            case "actionstate":
            case "actions":
                // Read-only: every client-side gate on item use/equip/move
                // (ClientActionGates). Works while those are locked — chat still does.
                ReplyLines(ClientActionGates.DescribeLines());
                return;

            case "unlockactions":
            case "unlock":
                ReplyLines(ClientActionGates.RequestUnlock());
                return;

            case "vitals":
            case "hud":
            {
                // The old D3D9 vitals HUD is gone (2026-09-29): your vitals are the
                // RynthVision player plate now.
                Fail("The old vitals HUD was removed. Your health/stamina/mana bars are the RynthVision player plate: /rv plates self [on|off].");
                return;
            }

            default:
                // Consume anything else under the /rc namespace so a typo
                // does not leak into world chat (ChatCommandReplies.Check answers first).
                Fail(ChatCommandReplies.UnknownReply(sub));
                return;
        }
    }

    /// <summary>
    /// /rc ui list (each panel's face and placement, UI.PanelRouter), open/close/popout/
    /// redock/snap a panel, and the bar's settings. /rc ui &lt;Panel|bar&gt; avalonia|imgui|both
    /// no longer picks a face (2026-10-14: docked panels and the bar are always ImGui); it
    /// says so. Replies are queued to chat, not written from inside this outgoing-chat hook.
    /// </summary>
    private static void HandleUiCommand(string args)
    {
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string line in UI.PanelRouter.Describe())
                Reply(line);
            return;
        }

        // /rc ui retailvitals [on|off]: hide AC's own health/stamina/mana bars
        // (RetailVitalsHooks). Alone it reports; anything but on/off toggles.
        if (parts[0].Equals("retailvitals", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 2 && parts[1].Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                Reply(RetailVitalsHooks.Describe());
                return;
            }
            if (parts.Length >= 2)
                RetailVitalsHooks.HideRetailVitals = Flag(parts[1], RetailVitalsHooks.HideRetailVitals);
            Reply(RetailVitalsHooks.Describe());
            return;
        }

        // /rc ui retailtoolbar [on|off]: hide AC's toolbar panel and its shortcut slots
        // (RetailToolbarHooks). Alone it reports; anything but on/off toggles.
        if (parts[0].Equals("retailtoolbar", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 2 && parts[1].Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                try { RetailToolbarHooks.LogStructure(); } catch { }
                Reply(RetailToolbarHooks.Describe());
                return;
            }
            if (parts.Length >= 2)
                RetailToolbarHooks.HideRetailToolbar = Flag(parts[1], RetailToolbarHooks.HideRetailToolbar);
            Reply(RetailToolbarHooks.Describe());
            return;
        }

        // /rc ui barsize [0-10|+|-|status]: the bar's size. Alone it reports it.
        if (parts[0].Equals("barsize", StringComparison.OrdinalIgnoreCase))
        {
            int cur = UI.PanelTextScale.GetChrome();
            if (parts.Length >= 2 && !parts[1].Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                int next;
                // Bare + / -: one step. +n / -n: that many steps. Plain n: that size.
                if (parts[1] == "+") next = cur + 1;
                else if (parts[1] == "-") next = cur - 1;
                else if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out next))
                {
                    Fail("Usage: /rc ui barsize [0-10|+|-|status]");
                    return;
                }
                else if (parts[1][0] is '+' or '-')
                    next += cur;
                UI.PanelTextScale.SetChrome(next);
            }
            Reply("Bar size: " + UI.PanelTextScale.Describe(UI.PanelTextScale.GetChrome()) + ".");
            return;
        }

        // /rc ui barrows [1-6|+|-|status]: the bar's rows. Alone it reports the rows and the limit.
        if (parts[0].Equals("barrows", StringComparison.OrdinalIgnoreCase))
        {
            int cur = ImGuiBackend.ImGuiBar.Rows;
            if (parts.Length >= 2 && !parts[1].Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                int next;
                // Bare + / -: one row. +n / -n: that many. Plain n: that many rows.
                if (parts[1] == "+") next = cur + 1;
                else if (parts[1] == "-") next = cur - 1;
                else if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out next))
                {
                    Fail("Usage: /rc ui barrows [1-6|+|-|status]");
                    return;
                }
                else if (parts[1][0] is '+' or '-')
                    next += cur;
                ImGuiBackend.ImGuiBar.SetRowLayout(next, ImGuiBackend.ImGuiBar.MaxPerRow);
            }
            Reply(ImGuiBackend.ImGuiBar.DescribeRowLayout());
            return;
        }

        // /rc ui baricons [2-30|none|+|-|status]: the most icons a bar row holds (none or 0 = no
        // limit). + / - step like the grip's menu (past 30 is no limit). Alone it reports both.
        if (parts[0].Equals("baricons", StringComparison.OrdinalIgnoreCase))
        {
            int cur = ImGuiBackend.ImGuiBar.MaxPerRow;
            if (parts.Length >= 2 && !parts[1].Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                int next;
                if (parts[1] == "+" || parts[1] == "-")
                    next = UI.BarLayout.StepMaxPerRow(cur, parts[1] == "+" ? 1 : -1, ImGuiBackend.ImGuiBar.ShownPerRow);
                else if (parts[1].Equals("none", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("off", StringComparison.OrdinalIgnoreCase))
                    next = UI.BarLayout.NoLimit;
                else if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out next))
                {
                    Fail("Usage: /rc ui baricons [2-30|none|+|-|status]");
                    return;
                }
                ImGuiBackend.ImGuiBar.SetRowLayout(ImGuiBackend.ImGuiBar.Rows, next);
            }
            Reply(ImGuiBackend.ImGuiBar.DescribeRowLayout());
            return;
        }

        // /rc ui channels: the chat's channels drawer opens or closes, as its button does (tests, keys).
        if (parts[0].Equals("channels", StringComparison.OrdinalIgnoreCase))
        {
            ImGuiBackend.Panels.ChatFace.ToggleChannelsRequested = true;
            Reply(ImGuiBackend.ImGuiPanelHost.IsOpen(ImGuiBackend.Panels.ChatFace.Title)
                ? "Chat: the channels drawer opens or closes."
                : "Chat: the channels drawer opens or closes the next time the chat window shows.");
            return;
        }

        // /rc ui open|close|popout|redock|snap <Panel>: drive a panel by name (tests,
        // dispatch.txt). Names may have spaces ("Monster Detail").
        string verb = parts[0].ToLowerInvariant();
        if (verb is "open" or "close" or "popout" or "redock" or "snap" && parts.Length >= 2)
        {
            // "Monsters" (the retired basic panel) is the Damage panel now.
            string title = UI.PanelRouter.Canonical(string.Join(' ', parts, 1, parts.Length - 1));
            if (title.Equals("bar", StringComparison.OrdinalIgnoreCase) && verb is "popout" or "redock")
            {
                ImGuiBackend.ImGuiBar.SetPoppedOut(verb == "popout");
                Reply($"/rc ui {verb} bar: requested.");
                return;
            }
            if (title.Equals("bar", StringComparison.OrdinalIgnoreCase) && verb == "snap") title = "__bar";
            else if (!ImGuiBackend.ImGuiPanelHost.HasFace(title)) { Fail($"No ImGui panel named '{title}'."); return; }
            switch (verb)
            {
                case "open": ImGuiBackend.ImGuiPanelHost.Open(title); break;
                case "close": ImGuiBackend.ImGuiPanelHost.Close(title); break;
                case "popout": ImGuiBackend.ImGuiPanelHost.PopOut(title); break;
                case "redock": ImGuiBackend.ImGuiPanelHost.Redock(title); break;
                case "snap":
                    ImGuiBackend.Win32Backend.PostToGameThread(() =>
                        RynthLog.UI($"/rc ui snap {title}: " + (ImGuiBackend.ImGuiPopOuts.RequestSnapshot(title) ?? "not popped out")));
                    break;
            }
            Reply($"/rc ui {verb} {title}: requested.");
            return;
        }

        // The old face choice: nothing to pick any more (nothing is stored either).
        if (parts.Length == 2 && UI.PanelFaceStore.TryParseFace(parts[1], out _))
        {
            Fail("The Avalonia / Both faces are gone: docked panels and the bar always show their ImGui face.");
            return;
        }

        Fail("Usage: /rc ui list  |  /rc ui open|close|popout|redock|snap <Panel>  |  /rc ui retailvitals [on|off]  |  /rc ui retailtoolbar [on|off]  |  /rc ui barsize [0-10|+|-|status]  |  /rc ui barrows [1-6|+|-]  |  /rc ui baricons [2-30|none|+|-]");
    }

    /// <summary>
    /// /rc imgui on|off switches the engine's in-client ImGui layer live and
    /// saves it to engine.json; /rc imgui fonttest toggles the font/theme test
    /// window; /rc imgui alone reports the state.
    /// </summary>
    private static void HandleImGuiCommand(string arg)
    {
        switch (arg.ToLowerInvariant())
        {
            case "on":
                Plugins.EngineSettings.EnableImGuiBackend = true;
                ImGuiBackend.ImGuiBar.ApplyFace();
                Reply("ImGui layer ON (saved). The self-test result is in the log (ImGuiSelfTest:). Panels: /rc ui list.");
                return;
            case "off":
                Plugins.EngineSettings.EnableImGuiBackend = false;
                ImGuiBackend.Win32Backend.ClearCaptureFlags();
                ImGuiBackend.Win32Backend.DiscardQueuedInput();
                ImGuiBackend.ImGuiBar.ApplyFace(); // the Avalonia bar comes back
                Reply("ImGui layer OFF (saved).");
                return;
            case "fonttest":
                ImGuiBackend.ImGuiFontTest.Enabled = !ImGuiBackend.ImGuiFontTest.Enabled;
                Reply($"ImGui font test window {(ImGuiBackend.ImGuiFontTest.Enabled ? "shown" : "hidden")}"
                      + (Plugins.EngineSettings.EnableImGuiBackend ? "." : " (the ImGui layer is off: /rc imgui on)."));
                return;
            case "diag":
                foreach (string line in ImGuiBackend.EngineFrameController.DescribeState())
                    RynthLog.Info("ImGuiDiag: " + line);
                foreach (string line in ImGuiBackend.ImGuiPanelHost.DescribeFaces())
                    RynthLog.Info("ImGuiDiag: " + line);
                foreach (string line in UI.ScriptWindows.ScriptWindowRegistry.Describe())
                    RynthLog.Info("ImGuiDiag: " + line);
                RynthLog.Info($"ImGuiDiag: bar visible={ImGuiBackend.ImGuiBar.Visible} face={UI.PanelFaceStore.FaceName(ImGuiBackend.ImGuiBar.ResolveFace())} avaloniaBarSuppressed={UI.AvaloniaOverlay.DockedBarSuppressed}");
                Reply("ImGui diagnostics written to the RynthCore log (ImGuiDiag:).");
                return;
            case "":
                Reply($"ImGui layer is {(Plugins.EngineSettings.EnableImGuiBackend ? "ON" : "OFF")}. Usage: /rc imgui on|off|fonttest|diag");
                return;
            default:
                Fail("Usage: /rc imgui on|off|fonttest|diag");
                return;
        }
    }

    /// <summary>
    /// /rc chathold on|off|status: incoming chat waits for the plugins before AC prints it,
    /// so a line a plugin eats (RynthAi's Meta Manager eats the reply to its own /myquests)
    /// is not shown in AC's chat window either (ChatCallbackHooks, IncomingChatHold.cs).
    /// On at every start; off prints every line at once, as before, and plugins can then
    /// only keep a line out of RynthChat.
    /// </summary>
    private static void HandleChatHoldCommand(string arg)
    {
        switch (arg.ToLowerInvariant())
        {
            case "on":
                ChatCallbackHooks.SetHoldEnabled(true);
                break;
            case "off":
                ChatCallbackHooks.SetHoldEnabled(false);
                break;
            case "":
            case "status":
                break;
            default:
                Fail("Usage: /rc chathold [on|off|status]");
                return;
        }
        Reply(ChatCallbackHooks.HoldStatusText());
    }

    private const string ServerUsage = ChatCommandReplies.ServerUsage;

    /// <summary>
    /// /rc server: every server the engine and the plugins know, whether each is active and why.
    /// "&lt;server&gt; on|off|auto" forces one on or off, or back to detecting, saved for this
    /// character (this session only before login). "aelrynth" / "other" / "auto" alone are the
    /// older Aelrynth override. "names ilt" lists, adds or removes Infinite Leaftide's world-name
    /// patterns (engine.json IltServerNames).
    /// </summary>
    private static void HandleServerCommand(string args)
    {
        string[] words = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string first = words.Length > 0 ? words[0].ToLowerInvariant() : string.Empty;

        if (first == "names")
        {
            HandleServerNames(words);
            return;
        }
        if (first == "pick")
        {
            // One server for this character (every other off), or "auto": the Diagnostics page's picker.
            if (words.Length != 2) { Fail(ServerUsage); return; }
            UI.Sections.ServerSelectionStore.Apply(words[1], out string message);
            Reply(message);
            ReplyServers();
            return;
        }
        if (words.Length == 1)
        {
            // The older single-word form: the Aelrynth override.
            ServerInfo.Mode? legacy = first switch
            {
                "aelrynth" or "on" => ServerInfo.Mode.ForceAelrynth,
                "other" or "off" => ServerInfo.Mode.ForceOther,
                "auto" => ServerInfo.Mode.Auto,
                _ => null,
            };
            if (legacy == null) { Fail(ServerUsage); return; }
            SetServerForce(ServerInfo.ServerId, legacy switch
            {
                ServerInfo.Mode.ForceAelrynth => UI.Sections.ServerForce.On,
                ServerInfo.Mode.ForceOther => UI.Sections.ServerForce.Off,
                _ => UI.Sections.ServerForce.Auto,
            });
        }
        else if (words.Length == 2)
        {
            if (!UI.Sections.ServerForceText.TryParse(words[1], out UI.Sections.ServerForce mode)) { Fail(ServerUsage); return; }
            if (!SetServerForce(first, mode)) return;
        }
        else if (words.Length > 2)
        {
            Fail(ServerUsage);
            return;
        }

        ReplyServers();
        ServerVerdict v = ServerInfo.Current;
        Reply($"Aelrynth signals: host {ServerInfo.HostDescription}; world name {AccountHooks.AnnouncedWorldName ?? "(not announced)"}"
            + (v.IsStaging ? "; staging" : "") + ".");
        Reply($"Titles (plugin API v75): {CharacterTitles.Describe()}.");
    }

    /// <summary>Saves a server override (per character) and says where it went. False for an unknown server.</summary>
    private static bool SetServerForce(string serverId, UI.Sections.ServerForce mode)
    {
        string id = UI.Sections.SectionManifestParser.Id(serverId);
        if (id.Length == 0 || (!UI.Sections.SectionRegistry.IsBuiltInServer(id)
                               && UI.Sections.SectionRegistry.Current.Server(id) == null))
        {
            Fail($"No server called '{serverId}'. /rc server lists them.");
            return false;
        }
        if (!UI.Sections.ServerForceStore.Set(id, mode, out string where))
        {
            Fail($"Server override not changed: {where}.");
            return false;
        }
        UI.Sections.SectionRegistry.Invalidate();
        Reply($"Server override {id} = {UI.Sections.ServerForceText.Name(mode)} ({where}).");
        return true;
    }

    /// <summary>One line per server: active or not, detected or not, the override, why.</summary>
    private static void ReplyServers()
    {
        var snap = UI.Sections.SectionRegistry.Current;
        if (snap.Servers.Count == 0)
        {
            // Before the first frame built a snapshot: the built-in verdicts directly.
            IltVerdict ilt = IltInfo.Current;
            ServerVerdict ael = ServerInfo.Current;
            Reply($"Server ilt (Infinite Leaftide): {(ilt.IsIlt ? "active" : "not active")} - {ilt.Reason}.");
            Reply($"Server aelrynth (Aelrynth): {(ael.IsAelrynth ? "active" : "not active")} - {ael.Reason}.");
            return;
        }
        foreach (UI.Sections.ServerState s in snap.Servers)
        {
            string force = s.Force == UI.Sections.ServerForce.Auto ? "auto"
                : $"forced {UI.Sections.ServerForceText.Name(s.Force)} (detected: {(s.Detected ? "yes" : "no")})";
            Reply($"Server {s.Id} ({s.Name}): {(s.Active ? "active" : "not active")}, {force} - {s.Reason}.");
        }
    }

    /// <summary>/rc server names ilt [add|remove &lt;pattern&gt;]: Infinite Leaftide's world-name patterns.</summary>
    private static void HandleServerNames(string[] words)
    {
        if (words.Length < 2 || !words[1].Equals(IltInfo.ServerId, StringComparison.OrdinalIgnoreCase))
        {
            Fail("Usage: /rc server names ilt [add|remove <pattern>] (other servers' lists come from their plugins).");
            return;
        }
        if (words.Length >= 4)
        {
            bool add = words[2].Equals("add", StringComparison.OrdinalIgnoreCase);
            if (!add && !words[2].Equals("remove", StringComparison.OrdinalIgnoreCase))
            {
                Fail("Usage: /rc server names ilt [add|remove <pattern>]");
                return;
            }
            string pattern = string.Join(' ', words, 3, words.Length - 3);
            bool changed = Plugins.EngineSettings.EditIltServerName(pattern, add);
            if (changed)
                Reply($"Infinite Leaftide world names: {(add ? "added" : "removed")} \"{pattern}\" (engine.json).");
            else
                Fail($"Infinite Leaftide world names: \"{pattern}\" was {(add ? "already there (or too long)" : "not there")}.");
            IltInfo.Invalidate();
            UI.Sections.SectionRegistry.Invalidate();
        }
        else if (words.Length == 3)
        {
            Fail("Usage: /rc server names ilt [add|remove <pattern>]");
            return;
        }
        Reply($"Infinite Leaftide lists: {IltInfo.ListsDescription}.");
    }

    /// <summary>
    /// /rc ilt: the Infinite Leaftide verdict, its signals and the lists in force. "on" /
    /// "off" force it, saved for this character (this session only before login); "auto" goes
    /// back to detecting. The lists are engine.json's IltServerNames / IltHosts.
    /// </summary>
    private static void HandleIltCommand(string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on": case "ilt": SetServerForce(IltInfo.ServerId, UI.Sections.ServerForce.On); break;
            case "off": case "other": SetServerForce(IltInfo.ServerId, UI.Sections.ServerForce.Off); break;
            case "auto": SetServerForce(IltInfo.ServerId, UI.Sections.ServerForce.Auto); break;
            case "": break;
            default:
                Fail("Usage: /rc ilt [auto|on|off]");
                return;
        }
        IltVerdict v = IltInfo.Current;
        Reply($"ILT: {(v.IsIlt ? "Infinite Leaftide" : "not Infinite Leaftide")} - {v.Reason}.");
        Reply($"Signals: host {ServerInfo.HostDescription}; world name {AccountHooks.AnnouncedWorldName ?? "(not announced)"}; "
            + $"override {IltInfo.Override}. Lists: {IltInfo.ListsDescription}.");
        bool installed = ImGuiBackend.ImGuiPanelHost.HasFace(ImGuiBackend.Panels.BountiesFace.Title);
        Reply(!installed ? "RynthILT: not installed (no ILT features: Bounties, Guardian, the Mini Remote's ILT tabs, Progression)."
            : IltInfo.PluginLoaded ? "RynthILT: loaded (the ILT bar button: Bounties, Guardian; the Mini Remote's ILT tabs; /rc progression)."
            : "RynthILT: installed but not loaded (yet).");
    }

    private static void HandleMasteryCommand()
    {
        if (!ServerInfo.IsAelrynth)
        {
            Fail("Mastery: not on Aelrynth, so nothing is asked (/rc server for why).");
            return;
        }
        MasterySnapshot? s = MasteryFeed.Snapshot;
        Reply($"Mastery: {MasteryFeed.Status}"
            + (s != null ? $"; last reply {(Environment.TickCount64 - s.ReceivedMs) / 1000}s ago" : "; no reply this login")
            + (MasteryFeed.Unsupported ? "" : ". Asking again."));
        MasteryFeed.RequestRefresh();
    }

    /// <summary>
    /// /rc worldlayer: report (no argument / status) or force (auto, uipass, transition,
    /// endscene) where the world overlays draw. The render thread picks the mode up next
    /// frame; the DIP hook is switched at the next frame boundary.
    /// </summary>
    private static void HandleWorldLayerCommand(string arg)
    {
        D3D9.WorldLayerMode? mode = arg.ToLowerInvariant() switch
        {
            "auto" => D3D9.WorldLayerMode.Auto,
            "uipass" or "ui" => D3D9.WorldLayerMode.UiPass,
            "transition" or "zenable" => D3D9.WorldLayerMode.Transition,
            "endscene" => D3D9.WorldLayerMode.EndScene,
            _ => null,
        };
        if (mode is { } m)
        {
            D3D9.Nav3DRenderInjector.Mode = m;
            Reply($"World overlays now: {D3D9.Nav3DRenderInjector.DescribePath()} (this session only).");
            return;
        }
        if (arg.Length > 0 && !arg.Equals("status", StringComparison.OrdinalIgnoreCase))
            Fail("Usage: /rc worldlayer [auto|uipass|transition|endscene]");
        Reply(D3D9.Nav3DRenderInjector.Describe());
        Reply(ImGuiBackend.UnderUiLayer.Describe());
    }

    /// <summary>
    /// "" / "toggle" → null (flip), "show" / "on" / "open" → true, "hide" / "off" / "close" →
    /// false. False for anything else (<see cref="ChatCommandReplies.TryPanelMode"/>).
    /// </summary>
    private static bool TryPanelMode(string args, out bool? show) => ChatCommandReplies.TryPanelMode(args, out show);

    /// <summary>Opens (true), closes (false) or toggles (null) an ImGui panel, like its bar button.</summary>
    private static void SetPanelOpen(string title, bool? show)
    {
        if (show == null || show.Value != ImGuiBackend.ImGuiPanelHost.IsOpen(title))
            UI.PanelRouter.Toggle(title);
    }

    /// <summary>Opens or closes an ImGui panel (its bar button), and says which; <paramref name="missing"/> when there's none.</summary>
    private static void TogglePanel(string title, string label, string missing)
    {
        if (!ImGuiBackend.ImGuiPanelHost.HasFace(title))
        {
            Fail(missing);
            return;
        }
        bool wasOpen = ImGuiBackend.ImGuiPanelHost.IsOpen(title);
        UI.PanelRouter.Toggle(title);
        Reply(ChatCommandReplies.PanelReply(label, null, wasOpen));
    }

    /// <summary>
    /// "/rc sense compact on|off | collapse on|off | view full|compact|collapsed": the Sense panel's
    /// look (saved in sense.txt), and the look now; alone, compact / collapse / view say the look now.
    /// </summary>
    private static void SenseLayoutCommand(string args)
    {
        string title = ImGuiBackend.Panels.SenseFace.Title;
        if (!ImGuiBackend.ImGuiPanelHost.HasFace(title))
        {
            Fail("The Sense panel isn't available (the ImGui layer is off?).");
            return;
        }
        if (!UI.Data.SenseStore.Loaded)
        {
            UI.Data.SenseStore.EnsureLoadQueued();
            Fail("Can't change the Sense view yet: its settings are still loading. Try again in a moment.");
            return;
        }
        UI.Data.SenseLayoutAnswer a = UI.Data.SenseLayout.Apply(args, UI.Data.SenseStore.Layout,
            ImGuiBackend.ImGuiPanelHost.IsOpen(title));
        if (!a.Ok)
        {
            Fail(a.Reply);
            return;
        }
        if (!UI.Data.SenseStore.SetLayout(a.Next))
        {
            Fail("Can't change the Sense view yet: its settings are still loading. Try again in a moment.");
            return;
        }
        Reply(a.Reply);
    }

    /// <summary>Settings, opened on <paramref name="tab"/> (or switched to it when open).</summary>
    private static void OpenSettingsTab(string tab)
    {
        string title = ImGuiBackend.Panels.SettingsFace.Title;
        bool wasOpen = ImGuiBackend.ImGuiPanelHost.IsOpen(title);
        ImGuiBackend.Panels.SettingsFace.RequestTab(tab);
        if (!wasOpen)
            UI.PanelRouter.Toggle(title);
        Reply(ChatCommandReplies.SettingsTabReply(tab, wasOpen));
    }

    /// <summary>"/rc settings [tab]": the Settings panel (toggled), or opened on a tab.</summary>
    private static void SettingsCommand(string tabName)
    {
        string title = ImGuiBackend.Panels.SettingsFace.Title;
        if (!ImGuiBackend.ImGuiPanelHost.HasFace(title))
        {
            Fail("The Settings panel isn't available (the ImGui layer is off?).");
            return;
        }
        if (tabName.Length == 0)
        {
            TogglePanel(title, "Settings", "The Settings panel isn't available (the ImGui layer is off?).");
            return;
        }
        UI.Data.SettingsTab? tab = Array.Find(UI.Data.SettingsSchema.Tabs, t => t.Name.Equals(tabName, StringComparison.OrdinalIgnoreCase));
        if (tab != null)
        {
            OpenSettingsTab(tab.Name);
            return;
        }
        // As before, Settings opens anyway (on the tab it was on); the reply says the name was wrong.
        bool wasOpen = ImGuiBackend.ImGuiPanelHost.IsOpen(title);
        ImGuiBackend.Panels.SettingsFace.RequestTab(tabName);
        if (!wasOpen)
            UI.PanelRouter.Toggle(title);
        Fail($"No Settings tab called '{tabName}' (tabs: {string.Join(", ", Array.ConvertAll(UI.Data.SettingsSchema.Tabs, t => t.Name))}); "
            + (wasOpen ? "Settings stays on its tab." : "Settings opened on its last tab."));
    }

    /// <summary>"/rc dashboard [classic|modern|tab &lt;name&gt;]": the RynthAi dashboard's look and the Classic look's tab.</summary>
    private static void DashboardCommand(string args)
    {
        string title = ImGuiBackend.Panels.RynthAiFace.Title;
        string usage = ChatCommandReplies.Usage("dashboard");
        if (args.Length == 0)
        {
            Reply("The RynthAi dashboard's look is " + (UI.RynthAiDashboardState.Classic ? "Classic" : "Modern") + ". " + usage);
            return;
        }
        bool classic;
        string? tabName = null;
        if (IsVerb(args, "tab", out string tab))
        {
            int i = ChatCommandReplies.ClassicTab(tab);
            if (i < 0) { Fail(usage); return; }
            UI.RynthAiDashboardState.SetClassicTab(i);
            ImGuiBackend.Panels.RynthAiFace.SwitchLook(classic: true);
            classic = true;
            tabName = ChatCommandReplies.ClassicTabNames[i];
        }
        else if (args.Equals("classic", StringComparison.OrdinalIgnoreCase)) { ImGuiBackend.Panels.RynthAiFace.SwitchLook(classic: true); classic = true; }
        else if (args.Equals("modern", StringComparison.OrdinalIgnoreCase)) { ImGuiBackend.Panels.RynthAiFace.SwitchLook(classic: false); classic = false; }
        else { Fail(usage); return; }
        bool hasDashboard = ImGuiBackend.ImGuiPanelHost.HasFace(title);
        if (hasDashboard && !ImGuiBackend.ImGuiPanelHost.IsOpen(title))
            ImGuiBackend.ImGuiPanelHost.Open(title);
        Reply(ChatCommandReplies.DashboardReply(classic, tabName, hasDashboard));
    }

    /// <summary>"/rc bounties|guardian [show|hide|toggle]": one of the ILT section's panels.</summary>
    private static void IltPanel(string title, string label, string plugin, string args)
    {
        if (!ImGuiBackend.ImGuiPanelHost.HasFace(title))
            Fail($"The {label} panel isn't available (the {plugin} plugin isn't installed, or the ImGui layer is off).");
        else if (!TryPanelMode(args, out bool? show))
            Fail($"Usage: /rc {label.ToLowerInvariant()} [show|hide|toggle]");
        else if (show != false && !IltInfo.IsIlt && !ImGuiBackend.ImGuiPanelHost.IsOpen(title))
            Fail($"{label} is an Infinite Leaftide feature, and this isn't Infinite Leaftide (/rc ilt for why).");
        else
        {
            bool wasOpen = ImGuiBackend.ImGuiPanelHost.IsOpen(title);
            SetPanelOpen(title, show);
            Reply(ChatCommandReplies.PanelReply(label + " panel", show, wasOpen));
        }
    }

    /// <summary>"/rc miniremote [show|hide|toggle|tab &lt;tab&gt;|section &lt;id&gt;]" (the comment at its verb in Handle).</summary>
    private static void MiniRemoteCommand(string remoteArgs)
    {
        string remoteTitle = ImGuiBackend.Panels.MiniRemoteFace.Title;
        if (IsVerb(remoteArgs, "tab", out string moved) && MovedTabCommand(moved) is string movedTo)
        {
            UI.ServerMenus.Run(movedTo);
            Reply($"The {moved.Trim().ToLowerInvariant()} tab is RynthOracle's now: ran {movedTo}.");
        }
        else if (!ImGuiBackend.ImGuiPanelHost.HasFace(remoteTitle))
            Fail("The Mini Remote panel isn't available (the ImGui layer is off?).");
        else if (IsVerb(remoteArgs, "tab", out string tab)
                 && tab.Length > 0 && ImGuiBackend.Panels.MiniRemoteFace.TryRequestSurface("section:" + tab.Trim().ToLowerInvariant()))
        {
            // A RynthAi that publishes sections: its tabs are sections here.
            ImGuiBackend.ImGuiPanelHost.Open(remoteTitle);
            Reply($"Mini Remote opened on {tab.Trim().ToLowerInvariant()}.");
        }
        else if (IsVerb(remoteArgs, "tab", out tab))
        {
            ImGuiBackend.Panels.MiniRemoteFace.RequestSection(UI.Sections.SectionCatalog.RemoteId);
            ImGuiBackend.ImGuiPanelHost.Open(remoteTitle);
            UI.Data.RynthAiCommands.ApplyRemoteCommand("hub", tab.Length > 0 ? "tab " + tab : "tab");
            Reply(tab.Length > 0 ? $"Mini Remote opened; RynthAi was asked for its {tab} tab." : "Mini Remote opened on RynthAi's remote.");
        }
        else if (IsVerb(remoteArgs, "section", out string section))
        {
            string id = UI.Sections.SectionManifestParser.Id(section);
            var snap = UI.Sections.SectionRegistry.Current;
            if (id != UI.Sections.SectionCatalog.RemoteId && snap.Find(id) == null)
            {
                var names = new System.Collections.Generic.List<string> { UI.Sections.SectionCatalog.RemoteId };
                foreach (UI.Sections.SectionView v in snap.Sections) names.Add(v.Id);
                Fail($"Usage: /rc miniremote section <{string.Join("|", names)}>");
            }
            else
            {
                ImGuiBackend.Panels.MiniRemoteFace.RequestSection(id);
                ImGuiBackend.ImGuiPanelHost.Open(remoteTitle);
                Reply($"Mini Remote opened on section {id}.");
            }
        }
        else if (!TryPanelMode(remoteArgs, out bool? show))
            Fail(ChatCommandReplies.Usage("miniremote"));
        else
        {
            bool wasOpen = ImGuiBackend.ImGuiPanelHost.IsOpen(remoteTitle);
            SetPanelOpen(remoteTitle, show);
            Reply(ChatCommandReplies.PanelReply("Mini Remote", show, wasOpen));
        }
    }

    /// <summary>
    /// The command for a Mini Remote tab that moved out (2026.10.5.54): "character" (the generic
    /// overview) is RynthOracle's Character tab. Null for a tab still in the Mini Remote, which
    /// includes RynthILT's Leaftide tabs (Charms, Banking, Gear, Games): those stay RynthILT's.
    /// </summary>
    internal static string? MovedTabCommand(string tab) => tab.Trim().ToLowerInvariant() switch
    {
        "character" or "char" => "/ro character",
        _ => null,
    };

    /// <summary>"verb" or "verb args..." (case-insensitive); args trimmed (<see cref="ChatCommandReplies.IsVerb"/>).</summary>
    private static bool IsVerb(string sub, string verb, out string args) => ChatCommandReplies.IsVerb(sub, verb, out args);

    /// <summary>
    /// /rv plates (alias /rc plates): the RynthVision nameplates and combat text
    /// (ImGui/MonsterHud). Alone it toggles the monster plates; on|off, all on|off,
    /// self|debuffs|others|numbers|gains [sub] [on|off], debuffs size / self debuffs size &lt;50-200&gt;,
    /// all|engaged, dist|max|scale|opacity|lift &lt;n&gt;, names|hp|level|weak|distance|fade|click [on|off],
    /// reset, status. Every change is saved.
    /// </summary>
    private static void HandlePlatesCommand(string args)
    {
        const string usage = ChatCommandReplies.PlatesUsage;
        if (!ImGuiBackend.Hud.HudFeed.VisionInstalled)
        {
            Fail("RynthVision isn't in the plugin list (launcher, Plugins tab), so its nameplates and combat text are off.");
            return;
        }
        if (!ImGuiBackend.Hud.MonsterHudSettings.Loaded)
        {
            ImGuiBackend.Hud.MonsterHudSettings.EnsureLoadQueued();
            Fail("Nameplate settings are still loading - try again in a moment.");
            return;
        }

        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        string? value = parts.Length > 1 ? parts[1] : null;
        string? sub = value != null && !IsOnOff(value) ? value.ToLowerInvariant() : null;
        string? subValue = sub != null ? (parts.Length > 2 ? parts[2] : null) : value;
        bool save = true;

        switch (verb)
        {
            case "":
                S.Enabled = !S.Enabled;
                break;
            case "on": S.Enabled = true; break;
            case "off": S.Enabled = false; break;
            case "all":
                // "all on|off": every overlay; "all" alone: plates for all monsters (the filter).
                if (value != null && IsOnOff(value)) S.SetAll(Flag(value, true));
                else S.Filter = ImGuiBackend.Hud.PlateFilter.All;
                break;
            case "engaged":
            case "combat":
            case "target":
                S.Filter = ImGuiBackend.Hud.PlateFilter.Engaged; break;
            case "self":
            case "me":
                switch (sub)
                {
                    case null: S.SelfPlate = Flag(subValue, S.SelfPlate); break;
                    case "numbers": case "text": S.SelfNumbers = Flag(subValue, S.SelfNumbers); break;
                    case "name": S.SelfName = Flag(subValue, S.SelfName); break;
                    case "firstperson": case "fp": S.SelfHideFirstPerson = Flag(subValue, S.SelfHideFirstPerson); break;
                    // "Debuffs on you": on|off, or its icon size ("self debuffs size 150").
                    case "debuffs": case "debuff":
                        if (subValue != null && subValue.Equals("size", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!S.TryParseDebuffIconSize(parts.Length > 3 ? parts[3] : null, out int sdi))
                            {
                                Fail($"Usage: /rv plates self debuffs size {S.MinDebuffIconSize}-{S.MaxDebuffIconSize} (percent; now {S.SelfDebuffIconSize}%)");
                                return;
                            }
                            S.SelfDebuffIconSize = sdi;
                        }
                        else S.SelfDebuffs = Flag(subValue, S.SelfDebuffs);
                        break;
                    // Placement: fixed on screen (drag it while unlocked) or following the character.
                    case "fixed": case "screen": S.SelfPlacement = ImGuiBackend.Hud.SelfPlacement.Fixed; break;
                    case "follow": case "world": S.SelfPlacement = ImGuiBackend.Hud.SelfPlacement.Follow; break;
                    // Unlocking a plate that follows you fixes it on screen first (only a fixed plate drags).
                    case "lock": S.SetSelfLocked(subValue == null || Flag(subValue, S.SelfLocked)); break;
                    case "unlock": S.SetSelfLocked(false); break;
                    case "reset": S.SelfFixedX = S.DefaultSelfFixedX; S.SelfFixedY = S.DefaultSelfFixedY; break;
                    case "position": case "pos":
                    {
                        string? w = subValue?.ToLowerInvariant();
                        ImGuiBackend.Hud.SelfPlatePosition? p = w switch
                        {
                            "above" or "over" or "top" or "head" => ImGuiBackend.Hud.SelfPlatePosition.Above,
                            "below" or "under" or "feet" or "bottom" => ImGuiBackend.Hud.SelfPlatePosition.Below,
                            "left" => ImGuiBackend.Hud.SelfPlatePosition.Left,
                            "right" => ImGuiBackend.Hud.SelfPlatePosition.Right,
                            _ => null,
                        };
                        if (p == null) { Fail("Usage: /rv plates self position above|below|left|right"); return; }
                        S.SelfPosition = p.Value;
                        break;
                    }
                    default: Fail(usage); return;
                }
                break;
            case "click":
            case "clickselect":
                S.ClickToSelect = Flag(value, S.ClickToSelect); break;
            case "refresh":
            case "health":
                // "refresh [on|off]" or "refresh <seconds>" (between two health queries for other plates).
                if (value != null && !IsOnOff(value))
                {
                    if (!TryNum(value, out float rs))
                    {
                        Fail(FormattableString.Invariant(
                            $"Usage: /rv plates refresh [on|off] | refresh {ImGuiBackend.Hud.HealthProbe.MinIntervalSec:0.0}-{ImGuiBackend.Hud.HealthProbe.MaxIntervalSec:0.0} (seconds; now {S.HealthRefreshSec:0.0#})"));
                        return;
                    }
                    S.HealthRefreshSec = Math.Clamp(rs, ImGuiBackend.Hud.HealthProbe.MinIntervalSec, ImGuiBackend.Hud.HealthProbe.MaxIntervalSec);
                    S.RefreshOthersHealth = true;
                }
                else S.RefreshOthersHealth = Flag(value, S.RefreshOthersHealth);
                break;
            case "onscreen":
            case "keeponscreen":
                S.KeepOnScreen = Flag(value, S.KeepOnScreen); break;
            case "debuffs":
            case "debuff":
                if (sub == "size")
                {
                    // The monster plates' icon size ("debuffs size 150"); your own row's is "self debuffs size".
                    if (!S.TryParseDebuffIconSize(subValue, out int mdi))
                    {
                        Fail($"Usage: /rv plates debuffs size {S.MinDebuffIconSize}-{S.MaxDebuffIconSize} (percent; now {S.MonsterDebuffIconSize}%)");
                        return;
                    }
                    S.MonsterDebuffIconSize = mdi;
                }
                else S.ShowDebuffs = Flag(value, S.ShowDebuffs);
                break;
            case "others":
                S.ShowOthersDebuff = Flag(value, S.ShowOthersDebuff); break;
            case "numbers":
            case "juice":
                switch (sub)
                {
                    case null: S.Numbers = Flag(subValue, S.Numbers); break;
                    case "dealt": case "damage": S.NumDealt = Flag(subValue, S.NumDealt); break;
                    case "taken": S.NumTaken = Flag(subValue, S.NumTaken); break;
                    case "heals": case "heal": S.NumHeals = Flag(subValue, S.NumHeals); break;
                    case "kills": case "kill": case "burst": S.NumKills = Flag(subValue, S.NumKills); break;
                    case "restores": case "stamina": case "mana": S.NumRestores = Flag(subValue, S.NumRestores); break;
                    case "size":
                        if (!TryNum(subValue, out float nsz)) { Fail("Usage: /rv plates numbers size 0.5-2.5"); return; }
                        S.NumSize = Math.Clamp(nsz, S.MinNumSize, S.MaxNumSize); break;
                    case "time": case "duration":
                        if (!TryNum(subValue, out float ntm)) { Fail("Usage: /rv plates numbers time 0.5-3"); return; }
                        S.NumTime = Math.Clamp(ntm, S.MinNumTime, S.MaxNumTime); break;
                    default: Fail(usage); return;
                }
                break;
            case "gains":
            case "gain":
                switch (sub)
                {
                    case "size":
                        if (!TryNum(subValue, out float gsz)) { Fail("Usage: /rv plates gains size 0.5-2.5"); return; }
                        S.GainSize = Math.Clamp(gsz, S.MinNumSize, S.MaxNumSize); break;
                    case "time": case "duration":
                        if (!TryNum(subValue, out float gtm)) { Fail("Usage: /rv plates gains time 0.5-3"); return; }
                        S.GainTime = Math.Clamp(gtm, S.MinNumTime, S.MaxNumTime); break;
                    case null: S.Gains = Flag(subValue, S.Gains); break;
                    case "xp": S.GainXp = Flag(subValue, S.GainXp); break;
                    case "lum": case "luminance": S.GainLum = Flag(subValue, S.GainLum); break;
                    case "radiance": S.GainRadiance = Flag(subValue, S.GainRadiance); break;
                    default: Fail(usage); return;
                }
                break;
            case "dist":
            case "range":
                if (!TryNum(value, out float d)) { Fail(usage); return; }
                S.MaxDistance = Math.Clamp(d, S.MinDistance, S.MaxDistanceLimit);
                break;
            case "max":
                if (!TryNum(value, out float n)) { Fail(usage); return; }
                S.MaxPlates = Math.Clamp((int)Math.Round(n), S.MinPlates, S.MaxPlatesLimit);
                break;
            case "scale":
                if (!TryNum(value, out float sc)) { Fail(usage); return; }
                S.Scale = Math.Clamp(sc, S.MinScale, S.MaxScale);
                break;
            case "opacity":
            case "alpha":
                if (!TryNum(value, out float o)) { Fail(usage); return; }
                if (o > 1f) o /= 100f;   // "opacity 80" means 80 %
                S.Opacity = Math.Clamp(o, S.MinOpacity, S.MaxOpacity);
                break;
            case "lift":
            case "height":
                if (!TryNum(value, out float h)) { Fail(usage); return; }
                S.HeightLift = Math.Clamp(h, S.MinLift, S.MaxLift);
                break;
            case "npcs":
            case "npc":
                switch (sub)
                {
                    case null: S.NpcNames = Flag(subValue, S.NpcNames); break;
                    case "dist": case "range":
                        if (!TryNum(subValue, out float nd)) { Fail("Usage: /rv plates npcs dist 5-80"); return; }
                        S.NpcMaxDistance = Math.Clamp(nd, S.MinNpcDistance, S.MaxNpcDistanceLimit); break;
                    case "max":
                        if (!TryNum(subValue, out float nm)) { Fail("Usage: /rv plates npcs max 1-40"); return; }
                        S.MaxNpcLabels = Math.Clamp((int)Math.Round(nm), S.MinNpcLabels, S.MaxNpcLabelsLimit); break;
                    default: Fail(usage); return;
                }
                break;
            case "portals":
            case "portal":
                switch (sub)
                {
                    case null: S.PortalNames = Flag(subValue, S.PortalNames); break;
                    case "dist": case "range":
                        if (!TryNum(subValue, out float pd)) { Fail("Usage: /rv plates portals dist 5-120"); return; }
                        S.PortalMaxDistance = Math.Clamp(pd, S.MinPortalDistance, S.MaxPortalDistanceLimit); break;
                    default: Fail(usage); return;
                }
                break;
            case "players":
                S.PlayerNames = Flag(value, S.PlayerNames); break;
            case "playerbars": S.PlayerBars = Flag(value, S.PlayerBars); break;
            case "names": S.ShowNames = Flag(value, S.ShowNames); break;
            case "hp": S.ShowHpNumbers = Flag(value, S.ShowHpNumbers); break;
            case "level": S.ShowLevel = Flag(value, S.ShowLevel); break;
            case "weak": S.ShowWeakness = Flag(value, S.ShowWeakness); break;
            case "distance": S.ShowDistance = Flag(value, S.ShowDistance); break;
            case "fade": S.FadeWithDistance = Flag(value, S.FadeWithDistance); break;
            case "reset":
                S.ResetToDefaults();
                save = false;
                break;
            case "status":
            case "diag":
                foreach (string line in ImGuiBackend.Hud.MonsterHud.DescribeLines())
                    Reply(line);
                return;
            case "test":
                ImGuiBackend.Hud.CombatText.SpawnTest();
                Reply("Sample combat numbers, a kill burst and gains spawned around you.");
                return;
            default:
                Fail(usage);
                return;
        }

        if (save) S.Save();
        Reply(S.Describe()
              + (Plugins.EngineSettings.EnableImGuiBackend ? "" : " (The ImGui layer is off, so nothing draws: /rc imgui on.)"));
    }

    private static bool IsOnOff(string v) =>
        v.Equals("on", StringComparison.OrdinalIgnoreCase) || v.Equals("off", StringComparison.OrdinalIgnoreCase) ||
        v == "1" || v == "0" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("toggle", StringComparison.OrdinalIgnoreCase);

    private static bool TryNum(string? s, out float v)
    {
        v = 0;
        return s != null && float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
    }

    /// <summary>
    /// /rc hooks: one line per UI hook (UiHookRegistry: UseNewMode, ClientCleanup,
    /// StartTooltip, ResetTooltip, CheckTooltip, StartDragandDrop, CatchDroppedItem) with
    /// its address, how it was found, on/off and how often it fired, then the screen and
    /// tooltip/drag state. "/rc hooks off &lt;name&gt;" makes that hook pass straight to AC
    /// now and leaves it out at the next start (engine.json DisabledUiHooks); "on" undoes it.
    /// </summary>
    private static void HandleHooksCommand(string args)
    {
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && (parts[0].Equals("on", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("off", StringComparison.OrdinalIgnoreCase)))
        {
            string said = UiHookRegistry.SetEnabled(parts[1], parts[0].Equals("on", StringComparison.OrdinalIgnoreCase));
            if (said.StartsWith("No UI hook called", StringComparison.Ordinal)) Fail(said);
            else Reply(said);
            return;
        }
        if (parts.Length != 0 && !(parts.Length == 1 && parts[0].Equals("status", StringComparison.OrdinalIgnoreCase)))
        {
            Fail("Usage: /rc hooks [status] | /rc hooks on|off <name>");
            return;
        }
        foreach (string line in UiHookRegistry.DescribeLines())
            Reply(line);
    }

    /// <summary>"on"/"off" sets; anything else (or nothing) toggles.</summary>
    private static bool Flag(string? value, bool current) =>
        value?.ToLowerInvariant() switch { "on" or "1" or "true" => true, "off" or "0" or "false" => false, _ => !current };

    /// <summary>
    /// /rc discord: the relay's switch, status, test and resume (UI.Data.Discord.ChatDiscord);
    /// "window" opens its panel, like Chat > gear > Discord settings.
    /// </summary>
    private static void HandleDiscordCommand(string args)
    {
        if (!ImGuiBackend.ImGuiPanelHost.HasFace(ImGuiBackend.Panels.ChatDiscordFace.Title))
        {
            Fail("The Discord relay needs the RynthChat plugin.");
            return;
        }
        string verb = args.Trim();
        if (verb.Equals("window", StringComparison.OrdinalIgnoreCase) || verb.Equals("show", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("settings", StringComparison.OrdinalIgnoreCase))
        {
            TogglePanel(ImGuiBackend.Panels.ChatDiscordFace.Title, "Discord relay window", "The Discord relay needs the RynthChat plugin.");
            return;
        }
        ReplyLines(UI.Data.Discord.ChatDiscord.HandleCommand(verb));
    }

    /// <summary>
    /// /rc layout: which panel layout this client uses and its files (UI/PanelLayout: the character's own,
    /// or the PC-wide default and why); default makes this character's the default for new characters;
    /// reset asks, reset confirm copies the default over this character's. Those two answer when their
    /// files are written (from the UI writer), or at once with why they can't run.
    /// </summary>
    private static void HandleLayoutCommand(string args)
    {
        UI.LayoutCommand cmd = UI.PanelLayoutPolicy.ParseCommand(args);
        switch (cmd)
        {
            case UI.LayoutCommand.Status:
                ReplyLines(UI.PanelLayout.DescribeLines());
                return;
            case UI.LayoutCommand.ResetAsk:
            {
                string? problem = UI.PanelLayout.Check(cmd, out string ask);
                Answer(problem == null, ask, problem ?? "");
                return;
            }
            case UI.LayoutCommand.MakeDefault:
            case UI.LayoutCommand.ResetConfirm:
            {
                string? problem = UI.PanelLayout.Request(cmd, LaterReply());
                if (problem != null) Fail(problem);
                return;
            }
            default:
                Fail(UI.PanelLayoutPolicy.Usage);
                return;
        }
    }

    // ── replies (ChatCommandReplies says where each goes and its colour) ─────

    /// <summary>
    /// The answer of a command whose result comes later from another thread (a file job on the UI
    /// writer): (text, ok), sent as this line's origin would send it. Marks this line answered.
    /// </summary>
    private static Action<string, bool> LaterReply()
    {
        ChatCommandOrigin origin = _origin;
        _answered = true;
        return (text, ok) =>
        {
            ChatReplyKind kind = ok ? ChatReplyKind.Ok : ChatReplyKind.Fail;
            Reply(text, kind, ChatCommandReplies.ToChat(kind, origin));
        };
    }

    /// <summary>It worked, or the state now: in chat (green) for the player, the log only for a script.</summary>
    private static void Reply(string text) => Reply(text, ChatReplyKind.Ok, ChatCommandReplies.ToChat(ChatReplyKind.Ok, _origin));

    /// <summary>It didn't (the usage, an unknown verb, can't): in chat (red) whoever sent the command.</summary>
    private static void Fail(string text) => Reply(text, ChatReplyKind.Fail, ChatCommandReplies.ToChat(ChatReplyKind.Fail, _origin));

    /// <summary><paramref name="okText"/> when <paramref name="ok"/>, else <paramref name="failText"/> as a failure.</summary>
    private static void Answer(bool ok, string okText, string failText)
    {
        if (ok) Reply(okText);
        else Fail(failText);
    }

    /// <summary>
    /// A module's reply lines as one answer (<see cref="ChatCommandReplies.KindOfLines"/>, or
    /// <paramref name="kind"/> when the caller knows): when it failed every line goes to chat, so a
    /// script's failure keeps its context; each line keeps its own colour.
    /// </summary>
    private static void ReplyLines(System.Collections.Generic.IEnumerable<string> lines, ChatReplyKind? kind = null)
    {
        var list = new System.Collections.Generic.List<string>(lines);
        ChatReplyKind all = kind ?? ChatCommandReplies.KindOfLines(list);
        bool toChat = ChatCommandReplies.ToChat(all, _origin);
        foreach (string l in list)
            Reply(l, kind ?? ChatCommandReplies.KindOfLine(l), toChat);
    }

    /// <summary>The command writes its own answer, now or from AC's thread later (VendorTrade's
    /// "[RC]" lines, AcKeymapLive's result): nothing more to say here.</summary>
    private static void AnswersItself() => _answered = true;

    /// <summary>A command that threw: the log (with the type) and chat.</summary>
    private static void Failed(string command, Exception ex)
    {
        RynthLog.Compat($"RynthCoreChatCommands: {command} failed - {ex.GetType().Name}: {ex.Message}");
        Fail(ChatCommandReplies.Failed(command, ex.GetType().Name, ex.Message));
    }

    /// <summary>The one writer: the log always, chat (AC's main-thread queue) when <paramref name="toChat"/>.</summary>
    private static void Reply(string text, ChatReplyKind kind, bool toChat)
    {
        _answered = true;
        RynthLog.Compat($"RynthCoreChatCommands: {text}{(toChat ? "" : " (log only: a script's command)")}");
        if (toChat)
            AcMainThreadQueue.EnqueueWriteToChat(ChatCommandReplies.ReplyPrefix + text, ChatCommandReplies.ChatType(kind));
    }
}

// ============================================================================
//  RynthCore.Engine - Compatibility/ChatCommandReplies.cs
//
//  How the engine's chat commands (/rc ..., /rv plates) answer. Every command a
//  player types gets a clear line back: what changed and the value now, or why
//  not with the usage (2026-10-28: "It should tell you whether it worked for
//  every single command a player punches in").
//
//  The pure part (no AC, no engine state), so tools\ChatCommandReplyTests can
//  compile it in: who a line came from and whether a reply goes to chat or only
//  to the log, the two reply colours, every verb with its usage, the argument
//  check made before a command runs, and the reply texts built from plain
//  values. Writing the reply (log + chat) is RynthCoreChatCommands.Reply.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RynthCore.Engine.Compatibility;

/// <summary>Who an engine command line came from (<see cref="RynthCoreChatCommands.TryHandle"/>).</summary>
internal enum ChatCommandOrigin
{
    /// <summary>
    /// A plugin (Host.InvokeChatParser: a meta, a window's button), the command files (dispatch.txt,
    /// RynthRemote's sendchat), the login commands, or one of the engine's own buttons and menus.
    /// A reply that says it worked goes to the log only; one that says it didn't still goes to chat.
    /// </summary>
    Script = 0,

    /// <summary>
    /// The player: typed in AC's chat bar or RynthChat's, a RynthCore hotkey, or a line a plugin passes
    /// on while it handles one the player typed (/ra videopatch runs /rc videopatch). Every reply goes
    /// to chat.
    /// </summary>
    Player = 1,
}

/// <summary>What a reply says: it worked (or here is the state), or it didn't.</summary>
internal enum ChatReplyKind
{
    Ok = 0,
    Fail = 1,
}

/// <summary>
/// One engine verb: its name, other spellings, usage, and the argument check made before it runs
/// (<see cref="Args"/> null: the command's own handler checks its arguments and answers with its usage).
/// </summary>
internal sealed record ChatVerb(string Name, string[] Aliases, string Usage, Func<string, bool>? Args);

/// <summary>What the check before a command runs found: the verb (null: unknown), its arguments, and
/// the reply when the command can't run (null: run it).</summary>
internal readonly record struct ChatVerbCheck(ChatVerb? Verb, string Args, string? Problem);

internal static class ChatCommandReplies
{
    /// <summary>Every engine reply starts with this (the style since the first /rc command).</summary>
    public const string ReplyPrefix = "[RynthCore] ";

    /// <summary>ChatMessageType 0x01, green in AC's chat: it worked, or the state.</summary>
    public const int OkChatType = 1;

    /// <summary>ChatMessageType 0x1A, red: the type AC prints its own command notices in
    /// ("Someone must @tell you first!", ChatCommandDispatcher.ChatTypeNotice).</summary>
    public const int FailChatType = 0x1A;

    // ── who sent it, where the reply goes ──────────────────────────────────

    /// <summary>The origin a line is handled with: the player's when the caller says so, or when it
    /// arrives while a line the player typed is being handed on (RynthCoreChatCommands.PlayerLine).</summary>
    public static ChatCommandOrigin Effective(ChatCommandOrigin given, bool insidePlayerLine) =>
        given == ChatCommandOrigin.Player || insidePlayerLine ? ChatCommandOrigin.Player : ChatCommandOrigin.Script;

    /// <summary>True when the reply goes to chat as well as the log: always for the player, and for
    /// anyone else only when it says the command didn't work.</summary>
    public static bool ToChat(ChatReplyKind kind, ChatCommandOrigin origin) =>
        kind == ChatReplyKind.Fail || origin == ChatCommandOrigin.Player;

    /// <summary>The AC chat type (colour) a reply is written with.</summary>
    public static int ChatType(ChatReplyKind kind) => kind == ChatReplyKind.Fail ? FailChatType : OkChatType;

    /// <summary>
    /// How the modules a command hands its arguments to (the video patch, flight recorder, report,
    /// AutoId, server messages, Discord relay, action gates) start a line that says the command
    /// didn't work. Their other lines are the state.
    /// </summary>
    private static readonly string[] FailOpenings =
    {
        "Usage", "Unknown", "Can't", "Not dumped", "Not capturing", "Capture failed", "Report not sent",
        "Log in first", "No route", "No hotkey on", "Which key", "Unlock skipped",
        "Video patch unavailable", "Video patch is switched off", "Flight recorder is off",
        "The server message stream is off", "The relay is off",
    };

    /// <summary>Whether a line a module returned says the command didn't work.</summary>
    public static ChatReplyKind KindOfLine(string line)
    {
        string s = line.TrimStart();
        foreach (string f in FailOpenings)
            if (s.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                return ChatReplyKind.Fail;
        return s.Contains("isn't available", StringComparison.OrdinalIgnoreCase) ? ChatReplyKind.Fail : ChatReplyKind.Ok;
    }

    /// <summary>A module's reply lines as one answer: failed when any line says so.</summary>
    public static ChatReplyKind KindOfLines(IEnumerable<string> lines)
    {
        foreach (string l in lines)
            if (KindOfLine(l) == ChatReplyKind.Fail)
                return ChatReplyKind.Fail;
        return ChatReplyKind.Ok;
    }

    // ── the verbs ──────────────────────────────────────────────────────────

    public const string PlatesUsage =
        "Usage: /rv plates [on|off] | all on|off | self [numbers|name|firstperson|lock|debuffs|lost] [on|off] | " +
        "self fixed|follow|unlock|reset | self debuffs size <50-200%> | " +
        "debuffs [on|off] | debuffs size <50-200%> | others [on|off] | numbers [dealt|taken|heals|kills] [on|off] | " +
        "gains [xp|lum|radiance] [on|off] | npcs [on|off] | npcs dist <yd>|max <n> | players [on|off] | portals [on|off] | portals dist <yd> | " +
        "all|engaged |dist <yd>|max <n>|scale <x>|opacity <x>|lift <m> | " +
        "names|hp|level|weak|distance|fade|click [on|off] | refresh [on|off] | refresh <seconds> | reset | status | test";

    public const string ServerUsage =
        "Usage: /rc server [<server> auto|on|off] | pick <server>|auto | [aelrynth|other|auto] | names ilt [add|remove <pattern>]";

    public const string HotkeyUsage =
        "Usage: /rc hotkey list | add <combo> <action or /command> | remove <combo> | enable|disable <combo> | actions | status | on|off";

    /// <summary>The Classic dashboard look's tabs, in order (RynthAiDashboardState.SetClassicTab's index).</summary>
    public static readonly string[] ClassicTabNames = { "main", "monsters", "gear", "buffs", "route", "loot", "meta", "tuning" };

    private static ChatVerb V(string name, string usage, Func<string, bool>? args, params string[] aliases) =>
        new(name, aliases, usage, args);

    private static bool NoArgs(string a) => a.Length == 0;

    private static Func<string, bool> Words(params string[] allowed) =>
        a => Array.Exists(allowed, w => w.Equals(a, StringComparison.OrdinalIgnoreCase));

    private static bool PanelArgs(string a) => TryPanelMode(a, out _);

    /// <summary>
    /// Every /rc verb RynthCoreChatCommands answers. Keep it in step with TryHandle: a verb missing
    /// here is answered as unknown before its handler runs (tools\ChatCommandReplyTests reads TryHandle
    /// and checks both ways).
    /// </summary>
    public static readonly ChatVerb[] Verbs =
    {
        V("vendor", "Usage: /rc vendor list | info | status | buy <id|sel> [n] | sell <id|sel> [id ...]", null),
        V("imgui", "Usage: /rc imgui on|off|fonttest|diag", Words("", "on", "off", "fonttest", "diag")),
        V("ui", "Usage: /rc ui list | open|close|popout|redock|snap <Panel> | channels | retailvitals [on|off] | retailtoolbar [on|off] | barsize [0-10|+|-|status] | barrows [1-6|+|-] | baricons [2-30|none|+|-]", null),
        V("chathold", "Usage: /rc chathold [on|off|status]", Words("", "on", "off", "status")),
        V("discord", "Usage: /rc discord [on|off|status|window|test [route]|resume [route]]", null),
        V("plates", PlatesUsage, null, "nameplates"),
        V("server", ServerUsage, null),
        V("ilt", "Usage: /rc ilt [auto|on|off]", Words("", "on", "ilt", "off", "other", "auto")),
        V("mastery", "Usage: /rc mastery (the Aelrynth mastery feed's state; asks the server again)", null),
        V("worldlayer", "Usage: /rc worldlayer [auto|uipass|transition|endscene|status]",
            Words("", "status", "auto", "uipass", "ui", "transition", "zenable", "endscene")),
        V("acmouse", "Usage: /rc acmouse [watch [seconds]]", a => a.Length == 0 || IsVerb(a, "watch", out _), "mouseover"),
        V("video", "Usage: /rc video [status|enable|disable|off|world|unfocused|all|fps <n>]", null, "videopatch"),
        V("hooks", "Usage: /rc hooks [status] | /rc hooks on|off <name>", null, "uihooks"),
        V("flightdump", "Usage: /rc flightdump [note] | /rc flightdump auto <reason>", null),
        V("quit", "Usage: /rc quit (closes the client, as its close button)", null, "exit"),
        V("report", "Usage: /rc report [what went wrong] (alone: opens the Report a problem panel)", null),
        V("flight", "Usage: /rc flight [status|mark <text>|on|off|auto on|off]", null),
        V("autoid", "Usage: /rc autoid [all|limited|off]  (alone: status)", null),
        V("netmsg", "Usage: /rc netmsg [status|on|off|log on|off|capture on|off]", null),
        V("vtank", "Usage: /rc vtank [clear]", Words("", "clear")),
        V("bounties", "Usage: /rc bounties [show|hide|toggle]", PanelArgs, "bounty", "bountys"),
        V("guardian", "Usage: /rc guardian [show|hide|toggle]", PanelArgs, "guardians"),
        V("dashboard", "Usage: /rc dashboard classic|modern|tab <" + string.Join("|", ClassicTabNames) + ">", DashboardArgs),
        V("miniremote", "Usage: /rc miniremote [show|hide|toggle|tab <tab>|section <id>]",
            a => TryPanelMode(a, out _) || IsVerb(a, "tab", out _) || IsVerb(a, "section", out _), "remote"),
        V("oracle-skills", "Usage: /rc oracle-skills <skills|attrs|progress|off>", a => OracleSkillsTab(a) != InvalidTab),
        V("skills", "Usage: /rc skills [show|hide|toggle]", PanelArgs),
        V("progression", "Usage: /rc progression (opens the Progress tab)", NoArgs, "progress"),
        V("spells", "Usage: /rc spells [show|hide|toggle]", PanelArgs),
        V("hotkeys", "Usage: /rc hotkeys (opens Settings > Hotkeys) | /rc hotkeys <verb> (as /rc hotkey)", null),
        V("diagnostics", "Usage: /rc diagnostics (opens Settings > Diagnostics, the server picker)", NoArgs, "serverpick"),
        V("settings", "Usage: /rc settings [tab]", null),
        V("shortcuts", "Usage: /rc shortcuts [use <1-18> | set <1-18> [id] | clear <1-18>]", ShortcutsArgs),
        V("keymap", "Usage: /rc keymap check|bind|unbind <action> <key> | dump, e.g. /rc keymap bind UseQuickSlot_10 F11", KeymapArgs),
        V("panels", "Usage: /rc panels [open|close|toggle <id>]", PanelsArgs),
        V("hotkey", HotkeyUsage, null),
        V("rl", "Usage: /rc rl (reloads RynthCore, as the bar's RL button)", NoArgs, "reload"),
        V("inventory", "Usage: /rc inventory (opens or closes the Inventory panel)", NoArgs, "inv"),
        V("sense", RynthCore.Engine.UI.Data.SenseLayout.Usage, RynthCore.Engine.UI.Data.SenseLayout.IsValidArgs),
        V("characters", "Usage: /rc characters (opens or closes RynthNet's Characters window)", NoArgs, "chars"),
        V("itemcount", "Usage: /rc itemcount (opens or closes the Item Count panel)", NoArgs, "itemhud"),
        V("itemtool", "Usage: /rc itemtool (opens or closes the Item Tool panel: chest looter and Quick give)", NoArgs),
        V("resetbar", "Usage: /rc resetbar (moves the RynthCore bar back to the top-left corner)", NoArgs, "barreset"),
        V("actionstate", "Usage: /rc actionstate (the client's item-action gates)", NoArgs, "actions"),
        V("unlockactions", "Usage: /rc unlockactions (opens the client's item-action gates now)", NoArgs, "unlock"),
        V("vitals", "Usage: /rc vitals (the old vitals HUD is gone: /rv plates self)", NoArgs, "hud"),
        V("layout", "Usage: /rc layout [status] | default | reset [confirm]", LayoutArgs),
        V("uicost", UiCostUsage, null),
    };

    /// <summary>/rc uicost: the UI frame cost breakdown (ImGui/UiCost.cs).</summary>
    public const string UiCostUsage =
        "Usage: /rc uicost [on|off|<ms>|now|status]  (alone: now; <ms> = the slow-frame threshold, 5-2000)";

    /// <summary>"/rc layout": nothing or status, default, reset, reset confirm (any spacing).</summary>
    private static bool LayoutArgs(string a) =>
        string.Join(' ', a.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant()
            is "" or "status" or "default" or "reset" or "reset confirm";

    /// <summary>The verb <paramref name="word"/> names (its name or another spelling), or null.</summary>
    public static ChatVerb? Find(string word)
    {
        foreach (ChatVerb v in Verbs)
        {
            if (v.Name.Equals(word, StringComparison.OrdinalIgnoreCase)) return v;
            foreach (string a in v.Aliases)
                if (a.Equals(word, StringComparison.OrdinalIgnoreCase)) return v;
        }
        return null;
    }

    /// <summary>The usage of the verb called <paramref name="name"/>.</summary>
    public static string Usage(string name) => Find(name)?.Usage ?? "Usage: /rc " + name;

    /// <summary>The check made before a /rc command runs: <paramref name="sub"/> is the line after "/rc ".</summary>
    public static ChatVerbCheck Check(string sub)
    {
        string s = sub.Trim();
        int sp = s.IndexOf(' ');
        string word = sp < 0 ? s : s.Substring(0, sp);
        string args = sp < 0 ? string.Empty : s.Substring(sp + 1).Trim();
        if (word.Length == 0)
            return new ChatVerbCheck(null, string.Empty, "Usage: /rc <command>. " + CommandList());
        ChatVerb? v = Find(word);
        if (v == null)
            return new ChatVerbCheck(null, args, UnknownReply(word));
        if (v.Args != null && !v.Args(args))
            return new ChatVerbCheck(v, args, v.Usage);
        return new ChatVerbCheck(v, args, null);
    }

    /// <summary>"RynthCore's commands: /rc acmouse, actionstate, ...": every verb by its name.</summary>
    public static string CommandList()
    {
        var names = new List<string>(Verbs.Length);
        foreach (ChatVerb v in Verbs) names.Add(v.Name);
        names.Sort(StringComparer.Ordinal);
        return "RynthCore's commands: /rc " + string.Join(", ", names) + ".";
    }

    /// <summary>The answer to a /rc verb the engine doesn't have.</summary>
    public static string UnknownReply(string word) => $"Unknown command '/rc {word}'. {CommandList()}";

    /// <summary>"/rc vendor" for "/rc vendor buy 1": the command's name in a reply.</summary>
    public static string CommandName(string trimmedLine)
    {
        string s = trimmedLine.Trim();
        int first = s.IndexOf(' ');
        if (first < 0) return s;
        int second = s.IndexOf(' ', first + 1);
        return second < 0 ? s : s.Substring(0, second);
    }

    // ── argument parsing (shared with RynthCoreChatCommands) ───────────────

    /// <summary>"verb" or "verb args..." (case-insensitive); args trimmed.</summary>
    public static bool IsVerb(string sub, string verb, out string args)
    {
        args = string.Empty;
        if (sub.Equals(verb, StringComparison.OrdinalIgnoreCase)) return true;
        if (!sub.StartsWith(verb + " ", StringComparison.OrdinalIgnoreCase)) return false;
        args = sub.Substring(verb.Length + 1).Trim();
        return true;
    }

    /// <summary>
    /// "" / "toggle" → null (flip), "show" / "on" / "open" → true, "hide" / "off" / "close" →
    /// false. False for anything else.
    /// </summary>
    public static bool TryPanelMode(string args, out bool? show)
    {
        show = null;
        switch (args.Trim().ToLowerInvariant())
        {
            case "": case "toggle": return true;
            case "show": case "on": case "open": show = true; return true;
            case "hide": case "off": case "close": show = false; return true;
            default: return false;
        }
    }

    /// <summary>What "/rc oracle-skills" can't take.</summary>
    public const int InvalidTab = -2;

    /// <summary>"/rc oracle-skills &lt;x&gt;": -1 off, 0 skills, 1 attributes, 2 progress, <see cref="InvalidTab"/> for anything else.</summary>
    public static int OracleSkillsTab(string args) => args.Trim().ToLowerInvariant() switch
    {
        "off" or "clear" or "none" or "-1" => -1,
        "0" or "skills" or "skill" => 0,
        "1" or "attrs" or "attributes" or "vitals" => 1,
        "2" or "progress" or "progression" => 2,
        _ => InvalidTab,
    };

    /// <summary>The Classic look's tab index for <paramref name="name"/>, or -1.</summary>
    public static int ClassicTab(string name) =>
        Array.FindIndex(ClassicTabNames, n => n.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool DashboardArgs(string a) =>
        a.Length == 0 || a.Equals("classic", StringComparison.OrdinalIgnoreCase) || a.Equals("modern", StringComparison.OrdinalIgnoreCase)
        || (IsVerb(a, "tab", out string tab) && ClassicTab(tab) >= 0);

    /// <summary>"use N", "set N [id]", "clear N" (more words are ignored, as before), or nothing (the list).</summary>
    private static bool ShortcutsArgs(string a)
    {
        string[] sa = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (sa.Length == 0) return true;
        return sa.Length >= 2 && int.TryParse(sa[1], out _)
            && (sa[0].Equals("use", StringComparison.OrdinalIgnoreCase) || sa[0].Equals("set", StringComparison.OrdinalIgnoreCase)
                || sa[0].Equals("clear", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"check|bind|unbind &lt;action&gt; &lt;key&gt;", "dump", or nothing (the keymap's state).</summary>
    private static bool KeymapArgs(string a)
    {
        string[] ka = a.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (ka.Length == 0) return true;
        string verb = ka[0].ToLowerInvariant();
        return verb == "dump" || (verb is "check" or "bind" or "unbind" && ka.Length == 3);
    }

    /// <summary>"&lt;op&gt; &lt;id&gt;" (open, close, anything else toggles, as before), or nothing (the list).</summary>
    private static bool PanelsArgs(string a)
    {
        string[] pa = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return pa.Length == 0 || (pa.Length == 2 && TryPanelId(pa[1], out _));
    }

    /// <summary>An AC panel id: decimal, or hex with 0x.</summary>
    public static bool TryPanelId(string text, out uint id) =>
        uint.TryParse(text.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
            text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.HexNumber : NumberStyles.Integer,
            null, out id);

    // ── reply texts ────────────────────────────────────────────────────────

    /// <summary>"Bounties panel opened." / "... closed." / "... is already open.": a panel after
    /// show (true), hide (false) or toggle (null), from whether it was open before.</summary>
    public static string PanelReply(string label, bool? show, bool wasOpen)
    {
        if (show is bool want && want == wasOpen)
            return $"{label} is already {(want ? "open" : "closed")}.";
        bool nowOpen = show ?? !wasOpen;
        return $"{label} {(nowOpen ? "opened" : "closed")}.";
    }

    /// <summary>"Settings opened on Hotkeys." / "Settings switched to Hotkeys."</summary>
    public static string SettingsTabReply(string tab, bool wasOpen) =>
        $"Settings {(wasOpen ? "switched to" : "opened on")} {tab}.";

    /// <summary>The answer when a command threw.</summary>
    public static string Failed(string command, string error, string message) =>
        $"{command} failed: {error}: {message} (the RynthCore log has more).";

    /// <summary>The answer when a command ran and its handler said nothing (a gap: the log names it).</summary>
    public static string Done(string command) => $"{command}: done.";

    /// <summary>"/rc oracle-skills": what RynthOracle's window embeds now.</summary>
    public static string OracleSkillsReply(int tab) => tab switch
    {
        0 => "RynthOracle's window shows the Skills tab.",
        1 => "RynthOracle's window shows the Attributes tab.",
        2 => "RynthOracle's window shows the Progress tab.",
        _ => "RynthOracle's window shows none of the Skills tabs.",
    };

    /// <summary>"/rc dashboard ...": the look now (and the Classic tab), and whether the dashboard opened.</summary>
    public static string DashboardReply(bool classic, string? tab, bool hasDashboard)
    {
        var sb = new StringBuilder("RynthAi dashboard: ").Append(classic ? "Classic" : "Modern").Append(" look");
        if (tab != null) sb.Append(", ").Append(tab.ToLowerInvariant()).Append(" tab");
        sb.Append(hasDashboard ? "." : " (RynthAi's dashboard isn't loaded, so nothing opened).");
        return sb.ToString();
    }
}

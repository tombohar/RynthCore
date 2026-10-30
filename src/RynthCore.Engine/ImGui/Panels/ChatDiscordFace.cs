// ============================================================================
//  RynthCore.Engine - ImGui/Panels/ChatDiscordFace.cs
//  The Discord panel: everything about copying chat to Discord
//  (UI/Data/Discord/ChatDiscord.cs). Opened from the Chat panel's gear menu
//  (Discord settings...) or /rc discord window.
//
//    Routes    one tab per destination: webhook URL, or bot token + channel ID;
//              which speech channels and which chat tabs it gets, keywords,
//              and optionally its own pacing; Send test / Resume
//    Settings  who relays (the characters it's on for, your own lines, hidden lines),
//              the message format, and the default pacing and limits
//    Log       messages sent, rate limits and errors
//
//  Settings fields are edited in place (single-value writes the pump
//  tolerates); adding or removing a route replaces the Routes array, and a
//  route's tab list is replaced too. Each change saves (ChatDiscord.Save, on
//  the background writer). Secrets go to ChatDiscord, which encrypts them off
//  this thread; the box is cleared at once and the text is never shown again.
//  AC's render thread only.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI.Data;
using RynthCore.Engine.UI.Data.Discord;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class ChatDiscordFace : IImGuiPanel
{
    public const string Title = "Discord";

    public static void Register() => ImGuiPanelHost.Register(Title,
        new PanelSpec(new Vector2(560, 520), new Vector2(420, 300)),
        () => new ChatDiscordFace());

    private string _secretInput = string.Empty;
    private string _secretMessage = string.Empty;
    private bool _secretMessageIsError;
    private DiscordRoute? _secretFor;
    private DiscordRoute? _confirmRemove;
    private int _lastLogCount;

    public void OnShown()
    {
        ChatModel.EnsureSettingsLoaded();
        ChatDiscord.EnsureLoaded();
        ChatDiscord.PanelShown(true);
    }

    public void OnHidden()
    {
        _confirmRemove = null;
        ChatDiscord.PanelShown(false);
    }

    public void Draw()
    {
        var s = ChatDiscord.Settings;
        DrawSwitch();
        ImGuiNET.ImGui.SameLine();
        Label("| " + ChatDiscord.StatusLine(), Mute);
        ImGuiNET.ImGui.Separator();

        if (ImGuiNET.ImGui.BeginTabBar("##dc_tabs"))
        {
            if (ImGuiNET.ImGui.BeginTabItem("Routes")) { DrawRoutes(s); ImGuiNET.ImGui.EndTabItem(); }
            if (ImGuiNET.ImGui.BeginTabItem("Settings")) { DrawSettings(s); ImGuiNET.ImGui.EndTabItem(); }
            if (ImGuiNET.ImGui.BeginTabItem("Log")) { DrawLog(); ImGuiNET.ImGui.EndTabItem(); }
            ImGuiNET.ImGui.EndTabBar();
        }
    }

    /// <summary>
    /// This character's switch (the relay is off for every character until each is switched on).
    /// Shared by the Chat panel's gear menu.
    /// </summary>
    internal static void DrawSwitch()
    {
        DiscordCharacter? me = ChatDiscord.CurrentCharacter();
        bool on = ChatDiscord.IsOn(me);
        ImGuiNET.ImGui.BeginDisabled(me == null);
        if (ImGuiNET.ImGui.Checkbox("Copy this character's chat to Discord##dc_on", ref on)) ChatDiscord.SetEnabled(on);
        ImGuiNET.ImGui.EndDisabled();
        if (me == null)
            ImGuiNET.ImGui.SetItemTooltip("Log in first: the relay is switched on for one character at a time.");
        else
            ImGuiNET.ImGui.SetItemTooltip("On for this character only; every other character stays off until it's switched on too.\n" +
                                          "Off: nothing from this character is queued or sent (Send test still works).");
    }

    // ── Routes ───────────────────────────────────────────────────────────

    private void DrawRoutes(ChatDiscordSettings s)
    {
        if (ImGuiNET.ImGui.SmallButton("Add route"))
        {
            var r = new DiscordRoute { Name = $"Route {s.Routes.Length + 1}" };
            ChatDiscordStore.NormalizeRoute(r);
            s.Routes = new List<DiscordRoute>(s.Routes) { r }.ToArray();
            ChatDiscord.Save();
        }
        ImGuiNET.ImGui.SetItemTooltip("A route is one Discord channel and the chat it receives.");

        if (s.Routes.Length == 0)
        {
            ImGuiNET.ImGui.Spacing();
            Wrapped("No routes yet. Add one, then paste a webhook URL from Discord: Server Settings > Integrations > "
                    + "Webhooks > New Webhook > pick the channel > Copy Webhook URL.", Text);
            return;
        }

        if (!ImGuiNET.ImGui.BeginTabBar("##dc_routes", ImGuiTabBarFlags.FittingPolicyScroll)) return;
        DiscordRoute? remove = null;
        for (int i = 0; i < s.Routes.Length; i++)
        {
            DiscordRoute r = s.Routes[i];
            string label = (r.Status?.Paused == true ? "! " : r.Enabled ? "" : "(off) ") + r.Name + "###dc_route" + i;
            if (!ImGuiNET.ImGui.BeginTabItem(label)) continue;
            ImGuiNET.ImGui.PushID(i);
            if (DrawRoute(s, r)) remove = r;
            ImGuiNET.ImGui.PopID();
            ImGuiNET.ImGui.EndTabItem();
        }
        ImGuiNET.ImGui.EndTabBar();

        if (remove != null)
        {
            var list = new List<DiscordRoute>(s.Routes);
            list.Remove(remove);
            s.Routes = list.ToArray();
            if (ReferenceEquals(_secretFor, remove)) ClearSecretBox(null);
            ChatDiscord.Save();
        }
    }

    /// <summary>One route's editor; true when the player asked to remove it.</summary>
    private bool DrawRoute(ChatDiscordSettings s, DiscordRoute r)
    {
        if (!ReferenceEquals(_secretFor, r)) ClearSecretBox(r);

        DiscordRouteStatus? st = r.Status;
        if (st != null)
        {
            if (st.Paused) Wrapped("Paused: " + st.LastError, Red);
            else if (st.LastError.Length > 0) Wrapped("Last problem: " + st.LastError, Amber);
            Label($"{st.MessagesSent} message(s), {st.LinesSent} line(s) sent, {st.Queued} queued, {st.Dropped} dropped"
                  + (st.LastSent != default ? $", last {st.LastSent:HH:mm:ss}" : string.Empty), Mute);
        }

        if (ImGuiNET.ImGui.Checkbox("Enabled##r_on", ref r.Enabled)) ChatDiscord.Save();
        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.SetNextItemWidth(200f);
        string name = r.Name;
        if (Input("Name##r_name", "route name", ref name, 40)) r.Name = name;
        if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit())
        {
            if (string.IsNullOrWhiteSpace(r.Name)) r.Name = "Discord";
            ChatDiscord.Save();
        }

        // Connection
        Heading("Connection");
        int mode = Math.Max(0, Array.IndexOf(DiscordRoute.Modes, r.Mode));
        ImGuiNET.ImGui.SetNextItemWidth(220f);
        if (ImGuiNET.ImGui.Combo("Send with##r_mode", ref mode, DiscordRoute.ModeNames, DiscordRoute.ModeNames.Length))
        {
            r.Mode = DiscordRoute.Modes[mode];
            ClearSecretBox(r);
            ChatDiscord.Resume(r);
            ChatDiscord.Save();
        }
        ImGuiNET.ImGui.SetItemTooltip("Webhook: simplest, one URL per channel, posts under any name.\n"
                                      + "Bot: your own Discord bot posts it (it must be in the server with Send Messages there).");

        bool bot = r.Mode == DiscordRoute.ModeBot;
        bool saved = bot ? r.BotTokenProtected.Length > 0 : r.WebhookProtected.Length > 0;
        ImGuiNET.ImGui.SetNextItemWidth(330f);
        Input((bot ? "Bot token" : "Webhook URL") + "##r_secret",
            saved ? "saved (paste a new one to replace it)" : bot ? "paste the bot token" : "https://discord.com/api/webhooks/...",
            ref _secretInput, 512, ImGuiInputTextFlags.Password);
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.Button("Save##r_secret_save") && _secretInput.Trim().Length > 0)
        {
            string err = bot ? ChatDiscord.SetBotToken(r, _secretInput) : ChatDiscord.SetWebhook(r, _secretInput);
            _secretInput = string.Empty;
            _secretMessageIsError = err.Length > 0;
            _secretMessage = err.Length > 0 ? err : "Saved, encrypted for this Windows user. Use Send test to check it.";
        }
        if (saved)
        {
            ImGuiNET.ImGui.SameLine();
            if (ImGuiNET.ImGui.Button("Clear##r_secret_clear"))
            {
                if (bot) r.BotTokenProtected = string.Empty; else r.WebhookProtected = string.Empty;
                _secretMessage = "Cleared.";
                _secretMessageIsError = false;
                ChatDiscord.Save();
            }
        }
        if (_secretMessage.Length > 0) Wrapped(_secretMessage, _secretMessageIsError ? Red : Green);

        if (bot)
        {
            ImGuiNET.ImGui.SetNextItemWidth(200f);
            string chId = r.ChannelId;
            if (Input("Channel ID##r_chid", "e.g. 123456789012345678", ref chId, 24, ImGuiInputTextFlags.CharsDecimal))
            {
                r.ChannelId = chId.Trim();
                ChatDiscord.Resume(r);
            }
            if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit()) ChatDiscord.Save();
            ImGuiNET.ImGui.SetItemTooltip("Discord: User Settings > Advanced > Developer Mode on, then right-click the channel > Copy Channel ID.");
            if (r.ChannelId.Length > 0 && !DiscordFormat.IsSnowflake(r.ChannelId))
                Label("A channel ID is 15-22 digits.", Red);
        }
        else
        {
            ImGuiNET.ImGui.SetNextItemWidth(200f);
            Input("Post as##r_name_as", "character name", ref r.DisplayName, 80);
            if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit()) ChatDiscord.Save();
            ImGuiNET.ImGui.SetItemTooltip("Name shown on the webhook's messages. Blank uses the character's name.\n"
                                          + "Discord refuses names containing \"discord\" or \"clyde\"; those fall back to the webhook's own name.");
        }

        if (ImGuiNET.ImGui.Button("Send test##r_test")) ChatDiscord.SendTest(r);
        ImGuiNET.ImGui.SetItemTooltip("Posts one test line to this route now (also clears a pause).");
        if (st?.Paused == true)
        {
            ImGuiNET.ImGui.SameLine();
            if (ImGuiNET.ImGui.Button("Resume##r_resume")) ChatDiscord.Resume(r);
        }

        DrawSpeech(r);
        DrawTabs(r);

        Heading("Filter");
        ImGuiNET.ImGui.SetNextItemWidth(330f);
        Input("Keywords##r_kw", "blank = every line; e.g. rare, Asheron", ref r.Keywords, 300);
        if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit()) ChatDiscord.Save();
        ImGuiNET.ImGui.SetItemTooltip("Comma-separated. When set, only lines containing one of these words (any case) are relayed.");

        Heading("How often");
        if (ImGuiNET.ImGui.Checkbox("Own pacing for this route##r_pace", ref r.CustomPace)) ChatDiscord.Save();
        ImGuiNET.ImGui.SetItemTooltip("Off: the route uses the pacing on the Settings tab.");
        if (r.CustomPace)
            DrawPacing(ref r.BatchMs, ref r.MinIntervalMs, "##r");
        else
            Label($"Using the default: batch {s.BatchMs / 1000.0:0.#} s, at most one message every {s.MinIntervalMs / 1000.0:0.#} s.", Mute);

        ImGuiNET.ImGui.Spacing();
        ImGuiNET.ImGui.Separator();
        bool remove = false;
        if (ReferenceEquals(_confirmRemove, r))
        {
            // The saved webhook URL / bot token goes with the route; there's no undo.
            Label("Delete this route and its saved credential?", Amber);
            ImGuiNET.ImGui.SameLine();
            if (ImGuiNET.ImGui.SmallButton("Delete##r_remove_yes"))
            {
                remove = true;
                _confirmRemove = null;
            }
            ImGuiNET.ImGui.SameLine();
            if (ImGuiNET.ImGui.SmallButton("Cancel##r_remove_no")) _confirmRemove = null;
        }
        else
        {
            if (ImGuiNET.ImGui.SmallButton("Remove route##r_remove")) _confirmRemove = r;
            ImGuiNET.ImGui.SetItemTooltip("Deletes this route and its saved credential (asks first).");
        }
        return remove;
    }

    private static void DrawSpeech(DiscordRoute r)
    {
        Heading("Speech channels");
        if (ImGuiNET.ImGui.SmallButton("All##sp_all")) SetSpeech(r, DiscordRoute.AllSpeechMask());
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.SmallButton("None##sp_none")) SetSpeech(r, 0);
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.SmallButton("Defaults##sp_def")) SetSpeech(r, DiscordRoute.DefaultSpeechMask());
        ImGuiNET.ImGui.SameLine();
        Label("what players say, by channel", Mute);
        if (!ImGuiNET.ImGui.BeginTable("##sp_grid", 4, ImGuiTableFlags.SizingStretchSame)) return;
        foreach (SpeechChannel ch in ChatSpeech.All)
        {
            ImGuiNET.ImGui.TableNextColumn();
            uint bit = ChatSpeech.Bit(ch);
            bool ticked = (r.SpeechMask & bit) != 0;
            if (ImGuiNET.ImGui.Checkbox(ChatSpeech.Name(ch) + "##sp" + (int)ch, ref ticked))
                SetSpeech(r, ticked ? r.SpeechMask | bit : r.SpeechMask & ~bit);
            if (ch == SpeechChannel.Local) ImGuiNET.ImGui.SetItemTooltip("Local speech includes every NPC and player near you; it can be busy.");
        }
        ImGuiNET.ImGui.EndTable();
    }

    private static void SetSpeech(DiscordRoute r, uint mask)
    {
        r.SpeechMask = mask;
        ChatDiscord.Save();
    }

    /// <summary>Chat tabs: every line that shows in a ticked tab goes, whatever it is.</summary>
    private static void DrawTabs(DiscordRoute r)
    {
        Heading("Chat tabs");
        Label("every line that shows in the tab (System, Combat, or tabs your chat rules fill)", Mute);
        string[] all = ChatModel.AllTabs();
        var shown = new List<string>(all);
        foreach (string t in r.Tabs)
            if (!shown.Exists(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) shown.Add(t);
        if (!ImGuiNET.ImGui.BeginTable("##tab_grid", 4, ImGuiTableFlags.SizingStretchSame)) return;
        for (int i = 0; i < shown.Count; i++)
        {
            string tab = shown[i];
            ImGuiNET.ImGui.TableNextColumn();
            bool ticked = Array.Exists(r.Tabs, t => string.Equals(t, tab, StringComparison.OrdinalIgnoreCase));
            bool missing = Array.IndexOf(all, tab) < 0;
            if (ImGuiNET.ImGui.Checkbox((missing ? tab + " (gone)" : tab) + "##tab" + i, ref ticked))
            {
                var list = new List<string>(r.Tabs);
                list.RemoveAll(t => string.Equals(t, tab, StringComparison.OrdinalIgnoreCase));
                if (ticked) list.Add(tab);
                r.Tabs = list.ToArray();
                ChatDiscord.Save();
            }
            if (tab == "All") ImGuiNET.ImGui.SetItemTooltip("Every line in the All tab: very busy. Pair it with keywords.");
            else if (missing) ImGuiNET.ImGui.SetItemTooltip("This tab no longer exists; untick it to drop it.");
        }
        ImGuiNET.ImGui.EndTable();
    }

    private void ClearSecretBox(DiscordRoute? forRoute)
    {
        _secretFor = forRoute;
        _secretInput = string.Empty;
        _secretMessage = string.Empty;
        _secretMessageIsError = false;
    }

    // ── Settings ─────────────────────────────────────────────────────────

    private static void DrawSettings(ChatDiscordSettings s)
    {
        Heading("Who relays");
        Label(ChatDiscord.SwitchLine(), Text);
        DiscordCharacter[] on = s.Characters;
        if (on.Length == 0)
            Label("Off for every character.", Mute);
        else
        {
            Label("On for:", Mute);
            DiscordCharacter? me = ChatDiscord.CurrentCharacter();
            for (int i = 0; i < on.Length; i++)
            {
                DiscordCharacter c = on[i];
                ImGuiNET.ImGui.PushID(i);
                if (ImGuiNET.ImGui.SmallButton("Turn off##s_char_off")) ChatDiscord.TurnOffFor(c);
                ImGuiNET.ImGui.SameLine();
                Label(c.ToString() + (c.Same(me) ? " (this client)" : string.Empty), c.Same(me) ? Green : Text);
                ImGuiNET.ImGui.PopID();
            }
        }
        Wrapped("Each character is switched on by itself (the checkbox at the top while you play it, or /rc discord on). "
                + "With several clients in one allegiance or fellowship, switch on just one of them so each line is posted once.", Mute);
        if (ImGuiNET.ImGui.Checkbox("Skip lines I type##s_own", ref s.SkipOwn)) ChatDiscord.Save();
        if (ImGuiNET.ImGui.Checkbox("Skip lines my chat rules hide##s_hidden", ref s.SkipHidden)) ChatDiscord.Save();
        ImGuiNET.ImGui.SetItemTooltip("Lines a Hide rule or standard filter hides in the chat aren't sent either.");

        Heading("Message format");
        if (ImGuiNET.ImGui.Checkbox("Timestamp (HH:mm:ss)##s_ts", ref s.IncludeTimestamp)) ChatDiscord.Save();
        if (ImGuiNET.ImGui.Checkbox("Start lines with [Character]##s_tag", ref s.TagCharacter)) ChatDiscord.Save();
        ImGuiNET.ImGui.SetItemTooltip("Useful when several characters post into the same Discord channel.");

        Heading("How often (default for every route)");
        DrawPacing(ref s.BatchMs, ref s.MinIntervalMs, "##s");

        Heading("Limits");
        ImGuiNET.ImGui.SetNextItemWidth(200f);
        if (ImGuiNET.ImGui.SliderInt("Queue limit (lines)##s_queue", ref s.MaxQueue, ChatDiscordSettings.QueueMin, ChatDiscordSettings.QueueMax))
            s.MaxQueue = Math.Clamp(s.MaxQueue, ChatDiscordSettings.QueueMin, ChatDiscordSettings.QueueMax);
        if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit()) ChatDiscord.Save();
        ImGuiNET.ImGui.SetItemTooltip("Lines waiting per route; past this the oldest are dropped.");
        ImGuiNET.ImGui.SetNextItemWidth(200f);
        if (ImGuiNET.ImGui.SliderInt("Log size##s_log", ref s.MaxLog, ChatDiscordSettings.LogMin, ChatDiscordSettings.LogMax))
            s.MaxLog = Math.Clamp(s.MaxLog, ChatDiscordSettings.LogMin, ChatDiscordSettings.LogMax);
        if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit()) ChatDiscord.Save();

        ImGuiNET.ImGui.Spacing();
        Wrapped("Routes and settings are shared by every client and character (%AppData%\\RynthCore\\rynthchat_discord.json); "
                + "the on / off switch is per character. Webhook URLs and bot tokens "
                + "are encrypted for this Windows user; on another PC or user they must be pasted again.", Mute);
        Wrapped("Relayed text never pings: @everyone, @here and user mentions are sent as plain text, and links don't unfurl.", Mute);
    }

    /// <summary>Batch window and message spacing sliders (seconds on screen, ms stored).</summary>
    private static void DrawPacing(ref int batchMs, ref int intervalMs, string id)
    {
        float batch = batchMs / 1000f;
        ImGuiNET.ImGui.SetNextItemWidth(200f);
        if (ImGuiNET.ImGui.SliderFloat("Batch window" + id + "_batch", ref batch, ChatDiscordSettings.BatchMinMs / 1000f,
                ChatDiscordSettings.BatchMaxMs / 1000f, "%.1f s", ImGuiSliderFlags.Logarithmic))
            batchMs = Math.Clamp((int)MathF.Round(batch * 1000f), ChatDiscordSettings.BatchMinMs, ChatDiscordSettings.BatchMaxMs);
        if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit()) ChatDiscord.Save();
        ImGuiNET.ImGui.SetItemTooltip("Lines arriving this close together go out as one Discord message.\n"
                                      + "Longer = fewer, bigger messages (a digest); 0 = each line as soon as allowed.");

        float interval = intervalMs / 1000f;
        ImGuiNET.ImGui.SetNextItemWidth(200f);
        if (ImGuiNET.ImGui.SliderFloat("At most one message every" + id + "_interval", ref interval, ChatDiscordSettings.IntervalMinMs / 1000f,
                ChatDiscordSettings.IntervalMaxMs / 1000f, "%.1f s", ImGuiSliderFlags.Logarithmic))
            intervalMs = Math.Clamp((int)MathF.Round(interval * 1000f), ChatDiscordSettings.IntervalMinMs, ChatDiscordSettings.IntervalMaxMs);
        if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit()) ChatDiscord.Save();
        ImGuiNET.ImGui.SetItemTooltip("Spacing between messages to one route. Discord allows about 30 a minute per channel;\n"
                                      + "2 s stays under it. Lines that arrive meanwhile wait and go in the next message.");
        Label($"Up to {60000.0 / Math.Max(1, intervalMs):0.#} messages a minute per route.", Mute);
    }

    // ── Log ──────────────────────────────────────────────────────────────

    private void DrawLog()
    {
        DiscordLogEntry[] log = ChatDiscord.LogSnapshot();
        if (ImGuiNET.ImGui.SmallButton("Clear##log_clear")) ChatDiscord.ClearLog();
        ImGuiNET.ImGui.SameLine();
        Label($"{log.Length} entries", Mute);

        if (!ImGuiNET.ImGui.BeginChild("##dc_log", new Vector2(0, 0), ImGuiChildFlags.Borders)) { ImGuiNET.ImGui.EndChild(); return; }
        foreach (DiscordLogEntry e in log)
        {
            uint col = e.Kind switch { DiscordLogKind.Sent => Green, DiscordLogKind.Error => Red, _ => Amber };
            Label(e.Time.ToString("HH:mm:ss"), Mute);
            ImGuiNET.ImGui.SameLine();
            Label(e.Route, col);
            ImGuiNET.ImGui.SameLine();
            // Relayed chat and Discord's replies: never a format string (see Wrapped).
            WrappedUnformatted(e.Text);
        }
        // Follow new lines unless the player scrolled up.
        if (log.Length != _lastLogCount && ImGuiNET.ImGui.GetScrollY() >= ImGuiNET.ImGui.GetScrollMaxY() - 20f)
            ImGuiNET.ImGui.SetScrollHereY(1f);
        _lastLogCount = log.Length;
        ImGuiNET.ImGui.EndChild();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void Heading(string text)
    {
        ImGuiNET.ImGui.Spacing();
        Label(text, Teal);
    }

    /// <summary>
    /// Wrapped text in <paramref name="color"/>. ImGui.NET's TextWrapped / Text / TextColored pass
    /// their string to ImGui as a printf format, so chat or an error Discord sent back holding
    /// "%s" would crash the client. Everything here goes through TextUnformatted.
    /// </summary>
    private static void Wrapped(string text, uint color)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, color);
        WrappedUnformatted(text);
        ImGuiNET.ImGui.PopStyleColor();
    }

    private static void WrappedUnformatted(string text)
    {
        ImGuiNET.ImGui.PushTextWrapPos(0f);
        ImGuiNET.ImGui.TextUnformatted(text);
        ImGuiNET.ImGui.PopTextWrapPos();
    }

    /// <summary>A text box that keeps the keyboard from AC while it's being typed in.</summary>
    private static bool Input(string label, string hint, ref string value, uint maxLength,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        bool changed = ImGuiNET.ImGui.InputTextWithHint(label, hint, ref value, maxLength, flags);
        if (ImGuiNET.ImGui.IsItemActive()) ImGuiTextFocus.NoteActive(label);
        return changed;
    }
}

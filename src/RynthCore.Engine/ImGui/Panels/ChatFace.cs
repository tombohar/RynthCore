// ============================================================================
//  RynthCore.Engine - ImGui/Panels/ChatFace.cs
//  ImGui face of RynthChat's main window (docked or popped out; the old
//  Avalonia face is UI/Panels/RynthChatPanel.cs): channel and custom tabs with
//  unread counts (right-click a tab: mark read, rename, delete, open in a new
//  window), Filters (the ChatFilters rule editor), the settings gear (with a
//  colour per kind of line under "Line colours", ChatLineColors), the New chat
//  window button, search, the coloured scrollback, and the input line with
//  Tell and the panel's resize grip beside it.
//
//  The scrollback, its menus and the unread counts are a ChatView (ChatView.cs,
//  2026-11-09), shared with the extra chat windows (ChatWindowFace.cs, "Chat 2"
//  to "Chat 7"), each of which has its own view and tab. This window shows
//  ChatModel's current tab, as before; the settings and the input line are
//  this window's only.
//
//  The input line belongs to ChatModel (UI/Data/ChatData.cs): Win32Backend
//  turns the chat keys into ChatModel edits on AC's thread, which is also
//  this face's thread. Clicking the box starts typing, like Enter. While
//  typing, Ctrl+V pastes and Ctrl+C / Ctrl+X copy / cut the whole line;
//  /r and /rt reply and retell (TellMemory). The extra windows have no input
//  line: there is one chat entry (Enter, the typed line, the Tell button), and
//  it stays here.
//
//  The panel has no strip under the body for the resize grip (PanelSpec.
//  GripInBody): the input row stops short of the bottom-right corner by
//  ImGuiPanelHost.BodyGripReserve and the host draws the grip there.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class ChatFace : IImGuiPanel
{
    public const string Title = "Chat";

    // The channel tabs live in a drawer on the window's left edge (2026-10-09: the wrapped
    // tab rows took too much room), the way the dashboard's ranges do; "Tabs across the
    // top" (ChatModel.TabsAcross, 2026-10-20) puts the row back for those who want it.
    private readonly ChannelsDrawer _channels;
    private readonly DashboardDrawers _drawers;
    private readonly ChatView _view;

    /// <summary>/rc ui channels: the channels drawer opens or closes on the next draw, as its button does.</summary>
    public static volatile bool ToggleChannelsRequested;

    public ChatFace()
    {
        _channels = new ChannelsDrawer(this);
        _drawers = new DashboardDrawers("##chat_drawers", _channels);
        _view = new ChatView(Title, isMain: true, ChatModel.CurrentTab, ChatModel.SelectTab);
    }

    /// <summary>The window spec, shared with the extra chat windows (ChatWindowFace).</summary>
    internal static PanelSpec Spec => new(new Vector2(460, 300), new Vector2(260, 140), Background: 0, EdgeToEdge: true, GripInBody: true);

    public static void Register() => ImGuiPanelHost.Register(Title, Spec, () => new ChatFace());

    private static readonly uint InputActive = RynthTheme.Argb(0xFFFFD700), TellBg = RynthTheme.Argb(0xFF0E2E3A);
    private static uint TabActive => RynthTheme.AccentFill;
    private static uint InputBg => RynthTheme.BoxBg;
    private static uint InputBorder => RynthTheme.AccentFill;

    private readonly byte[] _mentionWords = new byte[256];
    private float _inputScroll;

    private bool _openSettings;
    /// <summary>A line colour changed and isn't saved yet (saved once the picker is let go).</summary>
    private bool _coloursDirty;
    /// <summary>The Chat history slider's value while it's held (0: not held).</summary>
    private int _historyEdit;

    public bool ClickThrough => ChatModel.CtrlGatedClickThrough && !ImGuiNET.ImGui.GetIO().KeyCtrl;

    /// <summary>New lines arrive a few times a second at most; popped out and idle, 5 Hz is plenty.</summary>
    public int PopOutIdleHz => 5;

    public void OnShown()
    {
        ChatModel.EnsureSettingsLoaded();
        UiSources.Chat.Subscribe();
        UiSources.Chat.RequestRefresh();
        ChatModel.SetShown(true);
        _view.OnShown();
    }

    public void OnHidden()
    {
        ChatModel.SetShown(false);
        UiSources.Chat.Unsubscribe();
        _view.OnHidden();
    }

    public void Draw()
    {
        Vector2 origin = ImGuiNET.ImGui.GetCursorScreenPos();
        Vector2 size = ImGuiNET.ImGui.GetContentRegionAvail();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        uint bg = RynthTheme.Argb(((uint)Math.Clamp(ChatModel.BackgroundAlpha, 0, 255) << 24) | 0x0A121A);
        dl.AddRectFilled(origin, origin + size, bg, 4);
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui10));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 4));
        float x0 = origin.X + 4, w = size.X - 8;

        var snap = UiSources.Chat.Current;
        ChatLine[] lines = snap?.Value ?? Array.Empty<ChatLine>();
        string tab = _view.BeginFrame(lines, snap?.Version ?? -1);

        bool across = ChatModel.TabsAcross;
        float y;
        if (across)
        {
            y = DrawTabStrip(new Vector2(x0, origin.Y + 4), w);
            _view.DrawSearch(new Vector2(x0, y), w);
            y += 26;
        }
        else y = DrawHeader(new Vector2(x0, origin.Y + 4), w);

        _view.SyncLines(snap, lines, tab);

        // Scrollback, then the input row along the bottom; the grip takes its
        // bottom-right corner (ImGuiPanelHost.BodyGripReserve).
        const float inputH = 22, bottomPad = 2;
        float reserve = ImGuiPanelHost.BodyGripReserve;
        float inputY = origin.Y + size.Y - bottomPad - inputH;
        float listH = Math.Max(20, inputY - 4 - y);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x0, y));
        _view.DrawLines(new Vector2(w, listH));
        float inputRight = reserve > 0 ? origin.X + size.X - reserve - 3 : x0 + w;
        DrawInput(new Vector2(x0, inputY), inputRight - x0, inputH);

        ImGuiNET.ImGui.PopStyleVar();
        DrawPopups();
        ImGuiNET.ImGui.PopFont();
        if (!across) _drawers.Draw();   // last: its own window, placed against this one
    }

    // ── Tabs ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The buttons at the right of the top row: the gear, Filters and New chat window, right to
    /// left. Returns the x left of them.
    /// </summary>
    private float DrawRightButtons(Vector2 start, float w, float h)
    {
        float gearW = 22, filtW = 22, winW = 22, right = start.X + w;
        if (IconButton("##chat_gear", PhosphorIcons.Gear, new Vector2(right - gearW, start.Y), new Vector2(gearW, h), Text, BtnFill))
            _openSettings = true;
        ImGuiNET.ImGui.SetItemTooltip("Chat settings");
        if (IconButton("##chat_filters", PhosphorIcons.Funnel, new Vector2(right - gearW - 3 - filtW, start.Y), new Vector2(filtW, h), Text, BtnFill))
            PanelRouter.Toggle("ChatFilters");
        ImGuiNET.ImGui.SetItemTooltip("Filters: move, copy or hide lines by regex, and colour them");
        float winX = right - gearW - filtW - 6 - winW;
        _view.DrawNewWindowButton("##chat_newwin_btn", new Vector2(winX, start.Y), new Vector2(winW, h));
        return winX;
    }

    /// <summary>
    /// One row: the channel button (current tab, unread elsewhere; it opens the channels
    /// drawer, or a list when popped out), search, New chat window, Filters and the gear.
    /// Returns the y below it.
    /// </summary>
    private float DrawHeader(Vector2 start, float w)
    {
        const float h = 22;
        float buttonsX = DrawRightButtons(start, w, h);

        bool drawerOpen = _drawers.IsOpen(_channels);
        if (_view.DrawTabButton("##chat_channel", start, w * 0.45f, h, drawerOpen))
        {
            if (ImGuiPopOuts.InPopOutFrame) _view.OpenTabsPopup();   // no drawer beside a popped-out window
            else _drawers.Toggle(_channels);
        }
        if (ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right)) _view.OpenTabMenu(ChatModel.CurrentTab());

        // Search fills the rest.
        float sx = start.X + _view.LastTabButtonWidth + 4, sw = buttonsX - 4 - sx;
        if (sw > 40) _view.DrawSearch(new Vector2(sx, start.Y), sw);
        return start.Y + h + 4;
    }

    /// <summary>
    /// "Tabs across the top" (ChatModel.TabsAcross): every tab in a row that wraps left of
    /// the buttons, as before the drawer; right-click a tab to rename, delete or open it in a
    /// new window, + adds one. Returns the y below the strip.
    /// </summary>
    private float DrawTabStrip(Vector2 start, float w)
    {
        const float h = 20;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float stripRight = DrawRightButtons(start, w, h) - 5;
        float x = start.X, y = start.Y;
        string current = ChatModel.CurrentTab();
        var unreadAll = _view.Unread;
        foreach (string tab in ChatModel.AllTabs())
        {
            bool active = string.Equals(tab, current, StringComparison.OrdinalIgnoreCase);
            unreadAll.TryGetValue(tab, out var unread);
            string? badge = !active && unread.Count > 0 ? (unread.Count > 99 ? "99+" : unread.Count.ToString()) : null;
            float badgeW = badge != null ? ChatView.BadgeWidth(badge) : 0;
            float textW = ImGuiNET.ImGui.CalcTextSize(tab).X;
            float bw = textW + 12 + (badge != null ? badgeW + 3 : 0);
            if (x > start.X && x + bw > stripRight) { x = start.X; y += h + 2; }
            ImGuiNET.ImGui.PushID(tab);
            var p = new Vector2(x, y);
            if (Button("##tab", "", p, new Vector2(bw, h), Text, active ? TabActive : BtnFill))
                ChatModel.SelectTab(tab);
            if (ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right)) _view.OpenTabMenu(tab);
            if (unread.Mention && !active) ImGuiNET.ImGui.SetItemTooltip("Someone mentioned you here");
            else ImGuiNET.ImGui.SetItemTooltip(ChatModel.IsBaseTab(tab)
                ? "Right-click: open in a new window"
                : "Right-click to rename, delete or open this tab in a new window");
            dl.AddText(new Vector2(x + 6, y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Text, tab);
            if (badge != null) ChatView.DrawBadge(dl, new Vector2(x + 6 + textW + 3, y + 4), badgeW, h - 8, badge, unread.Mention);
            ImGuiNET.ImGui.PopID();
            x += bw + 2;
        }
        if (x > start.X && x + 18 > stripRight) { x = start.X; y += h + 2; }
        if (IconButton("##chat_addtab", PhosphorIcons.Plus, new Vector2(x, y), new Vector2(18, h), ChatView.TabAdd, BtnFill, font: UiFont.Ui11))
            _view.OpenNewTab();
        ImGuiNET.ImGui.SetItemTooltip("New tab (fill it with a rule: right-click a line, or Filters)");
        return y + h + 4;
    }

    /// <summary>
    /// The channel rows (the drawer's panel; the popped-out list draws its own): each tab with
    /// its unread count, the current one highlighted; click picks it, right-click its menu
    /// (open in a new window; rename or delete a created tab); "+ New tab" at the end.
    /// </summary>
    private void DrawChannelRows(Vector2 start, float w, float rowH)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        string current = ChatModel.CurrentTab();
        var unreadAll = _view.Unread;
        float y = start.Y;
        foreach (string tab in ChatModel.AllTabs())
        {
            bool active = string.Equals(tab, current, StringComparison.OrdinalIgnoreCase);
            unreadAll.TryGetValue(tab, out var unread);
            string? badge = !active && unread.Count > 0 ? (unread.Count > 99 ? "99+" : unread.Count.ToString()) : null;
            ImGuiNET.ImGui.PushID(tab);
            var p = new Vector2(start.X, y);
            if (Button("##row", "", p, new Vector2(w, rowH), Text, active ? TabActive : BtnFill))
                ChatModel.SelectTab(tab);
            if (ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right)) _view.OpenTabMenu(tab);
            if (unread.Mention && !active) ImGuiNET.ImGui.SetItemTooltip("Someone mentioned you here");
            else ImGuiNET.ImGui.SetItemTooltip(ChatModel.IsBaseTab(tab)
                ? "Right-click: open in a new window"
                : "Right-click to rename, delete or open this tab in a new window");
            float badgeW = badge != null ? ChatView.BadgeWidth(badge) : 0;
            float ty = y + (rowH - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
            dl.PushClipRect(p, p + new Vector2(w - (badge != null ? badgeW + 8 : 0), rowH), true);
            dl.AddText(new Vector2(p.X + 6, ty), active ? Teal : Text, tab);
            dl.PopClipRect();
            if (badge != null) ChatView.DrawBadge(dl, new Vector2(p.X + w - badgeW - 4, y + 3), badgeW, rowH - 6, badge, unread.Mention);
            ImGuiNET.ImGui.PopID();
            y += rowH + 2;
        }
        if (Button("##chat_addtab", PhosphorIcons.Plus + " New tab", new Vector2(start.X, y), new Vector2(w, rowH), ChatView.TabAdd, BtnFill))
            _view.OpenNewTab();
        ImGuiNET.ImGui.SetItemTooltip("New tab (fill it with a rule: right-click a line, or Filters)");
    }

    /// <summary>The channels drawer on the chat window's left edge (DashboardDrawers' mechanics).</summary>
    private sealed class ChannelsDrawer : DashboardDrawer
    {
        // The chat window often sits against the screen's left edge: the channels then open on its right.
        public override bool FlipsWhenNoRoom => true;

        private const float RowH = 20;
        private static bool s_open;   // this session only
        private readonly ChatFace _face;

        public ChannelsDrawer(ChatFace face) => _face = face;

        public override string Key => "chat_channels";
        public override string Icon => PhosphorIcons.ChatCircleText;
        public override string Name => "channels";
        public override string TabTooltip(bool right) => "Channels: every tab, with what's unread";
        public override float Width(float k) => 150 * k;
        public override float Height(float k) =>
            (2 * Pad + TitleH + 4) * k + (ChatModel.AllTabs().Length + 1) * (RowH + 2) * k;
        public override bool SavedOpen => s_open;
        public override void SaveOpen(bool open) => s_open = open;

        public override void DrawPanel(Vector2 origin, float width, float height, float k)
        {
            float y = PanelFrame(origin, width, height, k, Icon, "CHANNELS", null, "Hide the channels");
            _face.DrawChannelRows(new Vector2(origin.X + Pad * k, y), width - 2 * Pad * k, RowH * k);
        }
    }

    // ── Input ────────────────────────────────────────────────────────────

    private void DrawInput(Vector2 p, float w, float h)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float tellW = ButtonWidth("Tell");
        float boxW = Math.Max(20, w - tellW - 4);
        bool typing = Win32Backend.ChatCaptureActive;

        ImGuiNET.ImGui.SetCursorScreenPos(p);
        if (ImGuiNET.ImGui.InvisibleButton("##chat_input", new Vector2(boxW, h)))
            ChatModel.BeginTyping();
        if (!typing && ChatHooks.RynthChatOwnsChat) ImGuiNET.ImGui.SetItemTooltip("Click or press Enter to chat (Ctrl+V pastes; /r replies, /rt retells)");
        dl.AddRectFilled(p, p + new Vector2(boxW, h), InputBg, 3);
        dl.AddRect(p, p + new Vector2(boxW, h), typing ? InputActive : InputBorder, 3);

        string text = ChatModel.InputText;
        float fh = ImGuiNET.ImGui.GetFontSize();
        float ty = p.Y + (h - fh) * 0.5f;
        dl.PushClipRect(p + new Vector2(2, 1), p + new Vector2(boxW - 2, h - 1), true);
        if (text.Length == 0 && !typing)
        {
            _inputScroll = 0;
            dl.AddText(new Vector2(p.X + 5, ty), Faded(Mute), ChatModel.InputHint ?? "Press Enter to chat…");
        }
        else
        {
            int cursor = Math.Clamp(ChatModel.InputCursor, 0, text.Length);
            float caretX = cursor == 0 ? 0 : ImGuiNET.ImGui.CalcTextSize(text[..cursor]).X;
            float room = boxW - 12;
            if (caretX - _inputScroll > room) _inputScroll = caretX - room;
            if (caretX < _inputScroll) _inputScroll = caretX;
            float tx = p.X + 5 - _inputScroll;
            dl.AddText(new Vector2(tx, ty), Text, text);
            if (typing && ImGuiNET.ImGui.GetTime() % 1.0 < 0.6)
                dl.AddLine(new Vector2(tx + caretX, ty), new Vector2(tx + caretX, ty + fh), Text);
        }
        dl.PopClipRect();

        if (Button("##chat_tell", "Tell", new Vector2(p.X + boxW + 4, p.Y), new Vector2(tellW, h), Text, TellBg))
            Win32Backend.RequestTellSelected();
        ImGuiNET.ImGui.SetItemTooltip("Select an NPC or player in the game, then click to start /tell <name>, — or type /tell and press Enter.");
    }

    // ── Popups ───────────────────────────────────────────────────────────

    private void DrawPopups()
    {
        if (ToggleChannelsRequested)
        {
            ToggleChannelsRequested = false;
            if (ImGuiPopOuts.InPopOutFrame) _view.OpenTabsPopup();
            else _drawers.Toggle(_channels);
        }
        if (_openSettings)
        {
            _openSettings = false;
            WriteUtf8(_mentionWords, ChatModel.MentionWords);
            ImGuiNET.ImGui.OpenPopup("##chat_settings");
        }

        ChatView.PushPopupStyle();
        DrawSettingsPopup();
        _view.DrawPopups();
        ChatView.PopPopupStyle();
    }

    private void DrawSettingsPopup()
    {
        if (!ImGuiNET.ImGui.BeginPopup("##chat_settings"))
        {
            if (_coloursDirty) SaveColours();   // closed mid-drag
            return;
        }
        ImGuiNET.ImGui.PushItemWidth(150);
        int font = (int)MathF.Round(ChatModel.FontSize);
        if (ImGuiNET.ImGui.SliderInt("Font size", ref font, 8, 18)) ChatModel.FontSize = font;
        if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit()) ChatModel.SaveSettings();
        int pct = (int)MathF.Round(ChatModel.BackgroundAlpha / 2.55f);
        if (ImGuiNET.ImGui.SliderInt("Background", ref pct, 0, 100, "%d%%"))
            ChatModel.BackgroundAlpha = (int)MathF.Round(pct * 2.55f);
        if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit()) ChatModel.SaveSettings();
        // How many lines the chat keeps (ChatHistory): applied when the slider is let go, so
        // dragging it down doesn't cut the history at every step on the way.
        int history = _historyEdit > 0 ? _historyEdit : ChatModel.HistoryLines;
        if (ImGuiNET.ImGui.SliderInt("Chat history", ref history, ChatHistory.MinLines, ChatHistory.MaxLines, "%d lines"))
            _historyEdit = ChatHistory.Clamp(history);
        if (ImGuiNET.ImGui.IsItemDeactivated() && _historyEdit > 0)
        {
            ChatModel.SetHistoryLines(_historyEdit);
            _historyEdit = 0;
        }
        ImGuiNET.ImGui.SetItemTooltip("How many lines the chat keeps, all tabs together (default 2000).\n" +
            "About 2 MB of memory per 1000 lines. Lowering it drops the oldest lines.");
        ImGuiNET.ImGui.PopItemWidth();

        bool across = ChatModel.TabsAcross;
        if (ImGuiNET.ImGui.Checkbox("Tabs across the top", ref across))
        {
            ChatModel.TabsAcross = across;
            if (across) _drawers.Close(_channels);
            ChatModel.SaveSettings();
        }
        ImGuiNET.ImGui.SetItemTooltip("On: every tab in a row across the top, with search under it.\n" +
            "Off: one channel button that opens the tabs in a drawer beside the chat.");
        bool ts = ChatModel.ShowTimestamps;
        if (ImGuiNET.ImGui.Checkbox("Timestamps", ref ts))
        {
            ChatModel.ShowTimestamps = ts;
            ChatModel.SaveSettings();
        }
        bool auto = ChatModel.AutoScroll;
        if (ImGuiNET.ImGui.Checkbox("Auto-scroll", ref auto))
        {
            ChatModel.AutoScroll = auto;
            if (auto) _view.Restick();
            ChatModel.SaveSettings();
        }
        bool hide = ChatHooks.SuppressOriginalChat;
        if (ImGuiNET.ImGui.Checkbox("Hide retail chat (Enter types here)", ref hide))
        {
            ChatHooks.SuppressOriginalChat = hide;
            ChatModel.SaveSettings();
        }
        ImGuiNET.ImGui.SetItemTooltip("On: the retail chatbox is hidden and Enter types in RynthChat.\n" +
            "Off: the retail chatbox is shown and Enter types there; RynthChat keeps showing lines and its Tell button still works.");
        bool log = ChatModel.LogEnabled;
        if (ImGuiNET.ImGui.Checkbox("Log to file", ref log))
        {
            ChatModel.LogEnabled = log;
            ChatModel.SaveSettings();
        }
        ImGuiNET.ImGui.SetItemTooltip("%%AppData%%\\RynthCore\\ChatLogs\\<character>.log"); // a format string: %% is a literal %
        bool through = ChatModel.CtrlGatedClickThrough;
        if (ImGuiNET.ImGui.Checkbox("Click-through (hold Ctrl to interact)", ref through))
            ChatModel.SetCtrlGatedClickThrough(through);
        ImGuiNET.ImGui.SetItemTooltip("Docked chat only; popped out it's always interactive.\n" +
            "On: clicks pass through to the game; hold Ctrl to use the chat.");

        ImGuiNET.ImGui.Separator();
        bool mention = ChatModel.MentionHighlight;
        if (ImGuiNET.ImGui.Checkbox("Highlight my name", ref mention))
        {
            ChatModel.MentionHighlight = mention;
            ChatModel.FiltersChanged();   // re-highlights the kept lines
        }
        ImGuiNET.ImGui.SetItemTooltip("When another player's chat or channel line says your character's name\n" +
            "(or a word below), the name is highlighted, the line tinted and the tab's count turns gold.");
        if (mention)
        {
            Label("Also highlight (comma-separated):", Mute);
            Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
            TextBox("##mention_words", _mentionWords, p, 230, "e.g. Drak, buffs", out _);
            if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit())
            {
                ChatModel.MentionWords = Utf8(_mentionWords).Trim();
                ChatModel.FiltersChanged();
            }
            NextLine(p, 26);
            bool sound = ChatModel.MentionSound;
            if (ImGuiNET.ImGui.Checkbox("Beep when mentioned", ref sound))
            {
                ChatModel.MentionSound = sound;
                ChatModel.SaveSettings();
            }
        }
        // Discord above the colours: the colour grid opens downward, so it stays last.
        DrawDiscordSettings();
        ImGuiNET.ImGui.Separator();
        if (ImGuiNET.ImGui.CollapsingHeader("Line colours")) DrawLineColours();

        if (!ChatModel.PluginBound) Label("Plugin not bound", Mute);
        // A colour drag changes it every frame: save once the picker lets go.
        if (_coloursDirty && !ImGuiNET.ImGui.IsAnyItemActive()) SaveColours();
        ImGuiNET.ImGui.EndPopup();
    }

    private void SaveColours()
    {
        _coloursDirty = false;
        ChatModel.SaveSettings();
    }

    /// <summary>
    /// A colour per kind of line (ChatLineColors), two to a row: say, tells,
    /// allegiance, fellowship, emotes; each chat channel by its [Tag] (and any
    /// other tag seen this session); then the tab colours everything else takes.
    /// A rule's line colour still wins over all of these. x puts one back to its default.
    /// </summary>
    private void DrawLineColours()
    {
        bool retail = ChatLineColors.RetailColors;
        if (ImGuiNET.ImGui.Checkbox("Retail colours", ref retail))
        {
            ChatLineColors.RetailColors = retail;
            SaveColours();
        }
        SafeImGui.SetItemTooltip("Each kind of line in AC's own colour: magic and spells blue, combat red, tells yellow,\n" +
            "system purple, allegiance orange. RynthCore's own lines (the Rynth and Items tabs) keep theirs.\n" +
            "Off: every line takes its tab's colour. A colour you set below still wins.");
        Label("A rule's colour wins over these.", Mute);
        ColourGroup("Chat", ChatLineColors.ChatKinds);

        var channels = new List<ChatColorKind>(ChatLineColors.ChannelKinds);
        var others = new List<string>(ChatLineColors.SeenTags);
        foreach (string key in ChatLineColors.SetKeys)
            if (ChatLineColors.IsOtherChannel(key)) others.Add(key[ChatLineColors.ChanPrefix.Length..]);
        foreach (string tag in others)
            if (!channels.Exists(c => string.Equals(c.Label, tag, StringComparison.OrdinalIgnoreCase)))
                channels.Add(new ChatColorKind(ChatLineColors.ChanPrefix + tag, tag, $"[{tag}] lines", "Channels"));
        ColourGroup("Channels", channels);

        ColourGroup("Tabs (everything else)", ChatLineColors.TabKinds);

        if (ChatLineColors.AnySet)
        {
            if (ImGuiNET.ImGui.SmallButton("Reset all colours"))
            {
                ChatLineColors.ResetAll();
                SaveColours();
            }
        }
    }

    private void ColourGroup(string title, IReadOnlyList<ChatColorKind> kinds)
    {
        Label(title, Teal);
        float x0 = ImGuiNET.ImGui.GetCursorPosX();
        for (int i = 0; i < kinds.Count; i++)
        {
            if (i % 2 == 1) ImGuiNET.ImGui.SameLine(x0 + 150);
            ColourRow(kinds[i]);
        }
    }

    private void ColourRow(ChatColorKind kind)
    {
        uint argb = ChatLineColors.EffectiveArgb(kind);
        bool set = ChatLineColors.Get(kind.Key) != null;
        ImGuiNET.ImGui.PushID(kind.Key);
        Vector4 v = RynthTheme.Vec(argb);
        var rgb = new Vector3(v.X, v.Y, v.Z);
        if (ImGuiNET.ImGui.ColorEdit3("##col", ref rgb, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
        {
            ChatLineColors.Set(kind.Key, 0xFF000000
                | ((uint)Math.Clamp((int)MathF.Round(rgb.X * 255), 0, 255) << 16)
                | ((uint)Math.Clamp((int)MathF.Round(rgb.Y * 255), 0, 255) << 8)
                | (uint)Math.Clamp((int)MathF.Round(rgb.Z * 255), 0, 255));
            _coloursDirty = true;
        }
        ImGuiNET.ImGui.SameLine();
        Label(kind.Label, RynthTheme.Argb(argb));
        SafeImGui.SetItemTooltip(kind.Hint + (set ? "" : kind.Key.StartsWith(ChatLineColors.TabPrefix, StringComparison.Ordinal)
            ? (ChatLineColors.RetailColors ? "\nThe built-in colour, for lines AC has no colour for (Retail colours on)." : "\nThe built-in colour.")
            : ChatLineColors.RetailColors && ChatLineColors.Get(ChatLineColors.TabPrefix + kind.Tab) == null
                ? "\nNot set: AC's own colour." : $"\nNot set: the {kind.Tab} tab's colour."));
        if (set)
        {
            ImGuiNET.ImGui.SameLine();
            if (ImGuiNET.ImGui.SmallButton("x"))
            {
                ChatLineColors.Reset(kind.Key);
                SaveColours();
            }
            ImGuiNET.ImGui.SetItemTooltip("Back to the default colour");
        }
        ImGuiNET.ImGui.PopID();
    }

    /// <summary>
    /// Settings menu: this character's Discord relay switch, its status and the way into the
    /// Discord panel, in two compact rows like the rest of the menu.
    /// </summary>
    private static void DrawDiscordSettings()
    {
        ImGuiNET.ImGui.Separator();
        ChatDiscordFace.DrawSwitch();
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.SmallButton("Discord…##chat_discord"))
        {
            if (!ImGuiPanelHost.IsOpen(ChatDiscordFace.Title)) PanelRouter.Toggle(ChatDiscordFace.Title);
            ImGuiNET.ImGui.CloseCurrentPopup();
        }
        ImGuiNET.ImGui.SetItemTooltip("Discord panel: connection (webhook or bot), which channels and tabs go where,\n" +
            "keywords, how often, and the send log.");
        Label(UI.Data.Discord.ChatDiscord.SwitchLine() + " " + UI.Data.Discord.ChatDiscord.StatusLine(), Mute);
    }
}

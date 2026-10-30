// ============================================================================
//  RynthCore.Engine - ImGui/Panels/ChatFace.cs
//  ImGui face of RynthChat (docked or popped out; the old Avalonia face is
//  UI/Panels/RynthChatPanel.cs): channel and custom tabs with unread counts
//  (right-click a tab: mark read, rename, delete), Filters (the ChatFilters
//  rule editor), the settings gear (with a colour per kind of line under
//  "Line colours", ChatLineColors), search, the coloured scrollback, and the
//  input line with Tell and the panel's resize grip beside it.
//
//  Scrollback: wrapped lines, heights cached per wrap width and font; only
//  the lines in view are drawn. The timestamp is dimmed and the text wraps
//  under itself. Lines keep the colour and highlights the rules gave them
//  (ChatLine.Route, worked out on the pump - no regex runs here); a line with
//  highlights is laid out by hand (LayoutSpans) so each run can take its own
//  colour. Drag across lines to copy them (the keyboard stays with the game,
//  so copy is mouse-driven). Right-click a line to copy it or to make a rule
//  from it: move / copy lines like it to a tab, hide them, colour them. A line
//  with map coordinates in it ("42.1N, 33.6E") also offers "Arrow to" and "Go
//  to" them, sent to RynthNav (UI/Data/ChatCoords.cs finds them). The
//  coordinates are also drawn as underlined links: click points RynthNav's
//  arrow there (its overlay opens), Ctrl+click walks there.
//  Follows the newest line until you scroll up; the scroll for new lines is set
//  in the same frame they're drawn (DrawLines; until 2026-10-11 it landed a frame
//  late and the text bounced). Scrolled up, the view holds still. The history is
//  as long as the "Chat history" setting (ChatHistory, up to 10,000 lines): no
//  per-frame work walks all of it, and re-measuring after a resize is time-sliced.
//
//  The input line belongs to ChatModel (UI/Data/ChatData.cs): Win32Backend
//  turns the chat keys into ChatModel edits on AC's thread, which is also
//  this face's thread. Clicking the box starts typing, like Enter. While
//  typing, Ctrl+V pastes and Ctrl+C / Ctrl+X copy / cut the whole line;
//  /r and /rt reply and retell (TellMemory).
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
    private bool _openTabsPopup;

    /// <summary>/rc ui channels: the channels drawer opens or closes on the next draw, as its button does.</summary>
    public static volatile bool ToggleChannelsRequested;

    public ChatFace()
    {
        _channels = new ChannelsDrawer(this);
        _drawers = new DashboardDrawers("##chat_drawers", _channels);
    }

    public static void Register() => ImGuiPanelHost.Register(Title,
        new PanelSpec(new Vector2(460, 300), new Vector2(260, 140), Background: 0, EdgeToEdge: true, GripInBody: true),
        () => new ChatFace());

    private static readonly uint TabAdd = RynthTheme.Argb(0xFF7AE09A), InputActive = RynthTheme.Argb(0xFFFFD700),
        SelBg = RynthTheme.Argb(0x663A6EA5), TellBg = RynthTheme.Argb(0xFF0E2E3A),
        TsColor = RynthTheme.Argb(0xFF6F8397), MentionBg = RynthTheme.Argb(0x33FFC857),
        BadgeBg = RynthTheme.Argb(0xFF1F6F7A), BadgeMention = RynthTheme.Argb(0xFFB8860B);
    private static uint TabActive => RynthTheme.AccentFill;
    private static uint InputBg => RynthTheme.BoxBg;
    private static uint InputBorder => RynthTheme.AccentFill;
    private static uint FlashBg => RynthTheme.WithAlpha(RynthTheme.AccentFill, 0xE0);
    private const float StickThreshold = 24f;

    // Visible lines for the current tab + search, with wrapped heights; _lay
    // holds the hand layout of lines with highlights (null for plain lines).
    private readonly List<ChatLine> _vis = new(512);
    private readonly List<float> _h = new(512);
    private readonly List<SpanLayout?> _lay = new(512);
    private float[] _tops = new float[1];
    private int _heightsFrom;           // first new line: not measured at all yet
    // Lines below this index were measured for another width or font, or not at all (a
    // rebuild gives them one row each until then): MeasureLines catches up a few ms a frame.
    private int _measuredFrom;
    private float _rowH = 13f;          // one row of the chat font: an unmeasured line's height
    private bool _topsDirty = true;
    private long _seenLinesVersion = -1, _seenRoutes = -1, _lastId;
    private string _seenTab = "", _seenSearch = "";
    private float _seenWrap = -1, _seenFont = -1;
    private bool _seenTs;

    private readonly byte[] _search = new byte[128], _newTab = new byte[64], _mentionWords = new byte[256];
    private bool _stick = true;
    private float _lastScrollY;
    // Height of lines dropped off the top (the buffer is full) while not following the
    // tail: the view is moved up by as much, so the lines being read stay put instead of
    // sliding away.
    private float _droppedAbove;
    // The part of a dropped-lines move ImGui's whole-pixel scroll couldn't take, carried
    // into the next one (heights can be fractional at some DPI scales).
    private float _scrollCarry;
    // The list's scrollbar width (always shown), and a correction to the list width
    // DrawLines works out before its BeginChild, should the child ever report another.
    private const float ListScrollbarW = 10f;
    private float _wrapFix;
    private float _inputScroll;

    // Selection: indices into _vis.
    private int _selAnchor = -1, _selEnd = -1;
    private bool _selDragging, _selMoved;
    private Vector2 _selDown;
    private string? _flash;
    private double _flashUntil;

    // Unread lines per tab since it was last on screen (Id of the newest line seen there).
    private readonly Dictionary<string, long> _seenId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Count, bool Mention)> _unread = new(StringComparer.OrdinalIgnoreCase);
    private long _unreadLines = -1, _unreadRoutes = -1, _unreadTabsVersion = -1, _unreadCountedTo;
    private string _unreadTab = "";

    private bool _openSettings, _openNewTab, _focusNewTab, _openTabMenu, _openLineMenu, _openRename;
    /// <summary>A line colour changed and isn't saved yet (saved once the picker is let go).</summary>
    private bool _coloursDirty;
    /// <summary>The Chat history slider's value while it's held (0: not held).</summary>
    private int _historyEdit;
    private string? _ctxTab, _renameFrom;
    private ChatLine? _ctxLine;
    private string _ctxPattern = "";
    // Map coordinates in the right-clicked line (ChatCoords, found when the menu opens):
    // its menu offers "Arrow to" / "Go to" them, through RynthNav.
    private List<ChatCoord> _ctxCoords = new();
    /// <summary>A Move/Copy rule waiting for the New tab popup to name its tab.</summary>
    private ChatFilterRule? _pendingRule;

    public bool ClickThrough => ChatModel.CtrlGatedClickThrough && !ImGuiNET.ImGui.GetIO().KeyCtrl;

    /// <summary>New lines arrive a few times a second at most; popped out and idle, 5 Hz is plenty.</summary>
    public int PopOutIdleHz => 5;

    public void OnShown()
    {
        ChatModel.EnsureSettingsLoaded();
        UiSources.Chat.Subscribe();
        UiSources.Chat.RequestRefresh();
        ChatModel.SetShown(true);
        _stick = true;
        _seenRoutes = -1;   // full rebuild on the first frame
        _seenId.Clear();    // every tab starts read
        _unreadLines = _unreadRoutes = -1;   // a full recount
    }

    public void OnHidden()
    {
        ChatModel.SetShown(false);
        UiSources.Chat.Unsubscribe();
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
        string tab = ChatModel.CurrentTab();
        UpdateUnread(lines, snap?.Version ?? -1, tab);

        bool across = ChatModel.TabsAcross;
        float y;
        if (across)
        {
            y = DrawTabStrip(new Vector2(x0, origin.Y + 4), w);
            DrawSearch(new Vector2(x0, y), w);
            y += 26;
        }
        else y = DrawHeader(new Vector2(x0, origin.Y + 4), w);

        SyncLines(snap, lines, tab);

        // Scrollback, then the input row along the bottom; the grip takes its
        // bottom-right corner (ImGuiPanelHost.BodyGripReserve).
        const float inputH = 22, bottomPad = 2;
        float reserve = ImGuiPanelHost.BodyGripReserve;
        float inputY = origin.Y + size.Y - bottomPad - inputH;
        float listH = Math.Max(20, inputY - 4 - y);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x0, y));
        DrawLines(new Vector2(w, listH));
        float inputRight = reserve > 0 ? origin.X + size.X - reserve - 3 : x0 + w;
        DrawInput(new Vector2(x0, inputY), inputRight - x0, inputH);

        ImGuiNET.ImGui.PopStyleVar();
        DrawPopups();
        ImGuiNET.ImGui.PopFont();
        if (!across) _drawers.Draw();   // last: its own window, placed against this one
    }

    // ── Tabs ─────────────────────────────────────────────────────────────

    private const string SearchHint = PhosphorIcons.MagnifyingGlass + " Search…";

    /// <summary>
    /// One row: the channel button (current tab, unread elsewhere; it opens the channels
    /// drawer, or a list when popped out), search, Filters and the gear. Returns the y below it.
    /// </summary>
    private float DrawHeader(Vector2 start, float w)
    {
        const float h = 22;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float gearW = 22, filtW = 22, right = start.X + w;
        if (IconButton("##chat_gear", PhosphorIcons.Gear, new Vector2(right - gearW, start.Y), new Vector2(gearW, h), Text, BtnFill))
            _openSettings = true;
        ImGuiNET.ImGui.SetItemTooltip("Chat settings");
        if (IconButton("##chat_filters", PhosphorIcons.Funnel, new Vector2(right - gearW - 3 - filtW, start.Y), new Vector2(filtW, h), Text, BtnFill))
            PanelRouter.Toggle("ChatFilters");
        ImGuiNET.ImGui.SetItemTooltip("Filters: move, copy or hide lines by regex, and colour them");

        // The channel button: icon, current tab, and the unread count of every other tab.
        string current = ChatModel.CurrentTab();
        int others = 0;
        bool mention = false;
        foreach (var kv in _unread)
        {
            if (string.Equals(kv.Key, current, StringComparison.OrdinalIgnoreCase)) continue;
            others += kv.Value.Count;
            mention |= kv.Value.Mention && kv.Value.Count > 0;
        }
        string? badge = others > 0 ? (others > 99 ? "99+" : others.ToString()) : null;
        float badgeW = badge != null ? BadgeWidth(badge) : 0;
        string label = PhosphorIcons.ChatCircleText + " " + current;
        float labelW = ImGuiNET.ImGui.CalcTextSize(label).X;
        float caretW = ImGuiNET.ImGui.CalcTextSize(PhosphorIcons.CaretDown).X;
        float bw = MathF.Min(labelW + 16 + caretW + (badge != null ? badgeW + 4 : 0), w * 0.45f);
        bool drawerOpen = _drawers.IsOpen(_channels);
        if (Button("##chat_channel", "", start, new Vector2(bw, h), Text, drawerOpen ? TabActive : BtnFill))
        {
            if (ImGuiPopOuts.InPopOutFrame) _openTabsPopup = true;   // no drawer beside a popped-out window
            else _drawers.Toggle(_channels);
        }
        if (ImGuiNET.ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
            SafeImGui.SetTooltip(mention ? "Channels (someone mentioned you)" : "Channels: pick a tab, see what's unread");
        float ty = start.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
        dl.PushClipRect(start, start + new Vector2(bw - caretW - 6 - (badge != null ? badgeW + 4 : 0), h), true);
        dl.AddText(new Vector2(start.X + 6, ty), Text, label);
        dl.PopClipRect();
        float bx = start.X + bw - caretW - 6;
        if (badge != null)
        {
            bx -= badgeW + 4;
            DrawBadge(dl, new Vector2(bx, start.Y + 4), badgeW, h - 8, badge, mention);
            bx += badgeW + 4;
        }
        dl.AddText(new Vector2(start.X + bw - caretW - 5, ty), Mute, PhosphorIcons.CaretDown);

        // Search fills the rest.
        float sx = start.X + bw + 4, sw = right - gearW - filtW - 7 - sx;
        if (sw > 40) DrawSearch(new Vector2(sx, start.Y), sw);
        return start.Y + h + 4;
    }

    /// <summary>
    /// "Tabs across the top" (ChatModel.TabsAcross): every tab in a row that wraps left of
    /// Filters and the gear, as before the drawer; right-click a tab to rename or delete it,
    /// + adds one. Returns the y below the strip.
    /// </summary>
    private float DrawTabStrip(Vector2 start, float w)
    {
        const float h = 20;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float gearW = 22, filtW = 22, right = start.X + w;
        if (IconButton("##chat_gear", PhosphorIcons.Gear, new Vector2(right - gearW, start.Y), new Vector2(gearW, h), Text, BtnFill))
            _openSettings = true;
        ImGuiNET.ImGui.SetItemTooltip("Chat settings");
        if (IconButton("##chat_filters", PhosphorIcons.Funnel, new Vector2(right - gearW - 3 - filtW, start.Y), new Vector2(filtW, h), Text, BtnFill))
            PanelRouter.Toggle("ChatFilters");
        ImGuiNET.ImGui.SetItemTooltip("Filters: move, copy or hide lines by regex, and colour them");

        float stripRight = right - gearW - filtW - 8;
        float x = start.X, y = start.Y;
        string current = ChatModel.CurrentTab();
        foreach (string tab in ChatModel.AllTabs())
        {
            bool active = string.Equals(tab, current, StringComparison.OrdinalIgnoreCase);
            _unread.TryGetValue(tab, out var unread);
            string? badge = !active && unread.Count > 0 ? (unread.Count > 99 ? "99+" : unread.Count.ToString()) : null;
            float badgeW = badge != null ? BadgeWidth(badge) : 0;
            float textW = ImGuiNET.ImGui.CalcTextSize(tab).X;
            float bw = textW + 12 + (badge != null ? badgeW + 3 : 0);
            if (x > start.X && x + bw > stripRight) { x = start.X; y += h + 2; }
            ImGuiNET.ImGui.PushID(tab);
            var p = new Vector2(x, y);
            if (Button("##tab", "", p, new Vector2(bw, h), Text, active ? TabActive : BtnFill))
                ChatModel.SelectTab(tab);
            if (ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right)) { _ctxTab = tab; _openTabMenu = true; }
            if (unread.Mention && !active) ImGuiNET.ImGui.SetItemTooltip("Someone mentioned you here");
            else if (!ChatModel.IsBaseTab(tab)) ImGuiNET.ImGui.SetItemTooltip("Right-click to rename or delete this tab");
            dl.AddText(new Vector2(x + 6, y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Text, tab);
            if (badge != null) DrawBadge(dl, new Vector2(x + 6 + textW + 3, y + 4), badgeW, h - 8, badge, unread.Mention);
            ImGuiNET.ImGui.PopID();
            x += bw + 2;
        }
        if (x > start.X && x + 18 > stripRight) { x = start.X; y += h + 2; }
        if (IconButton("##chat_addtab", PhosphorIcons.Plus, new Vector2(x, y), new Vector2(18, h), TabAdd, BtnFill, font: UiFont.Ui11))
        {
            _pendingRule = null;
            WriteUtf8(_newTab, "");
            _openNewTab = _focusNewTab = true;
        }
        ImGuiNET.ImGui.SetItemTooltip("New tab (fill it with a rule: right-click a line, or Filters)");
        return y + h + 4;
    }

    private static float BadgeWidth(string badge)
    {
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        float w = ImGuiNET.ImGui.CalcTextSize(badge).X + 8;
        ImGuiNET.ImGui.PopFont();
        return w;
    }

    private static void DrawBadge(ImDrawListPtr dl, Vector2 p, float w, float h, string badge, bool mention)
    {
        dl.AddRectFilled(p, p + new Vector2(w, h), mention ? BadgeMention : BadgeBg, h * 0.5f);
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        dl.AddText(p + new Vector2(4, (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Text, badge);
        ImGuiNET.ImGui.PopFont();
    }

    /// <summary>
    /// The channel rows (the drawer's panel; the popped-out list draws its own): each tab with
    /// its unread count, the current one highlighted; click picks it, right-click renames or
    /// deletes a created tab; "+ New tab" at the end.
    /// </summary>
    private void DrawChannelRows(Vector2 start, float w, float rowH)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        string current = ChatModel.CurrentTab();
        float y = start.Y;
        foreach (string tab in ChatModel.AllTabs())
        {
            bool active = string.Equals(tab, current, StringComparison.OrdinalIgnoreCase);
            _unread.TryGetValue(tab, out var unread);
            string? badge = !active && unread.Count > 0 ? (unread.Count > 99 ? "99+" : unread.Count.ToString()) : null;
            ImGuiNET.ImGui.PushID(tab);
            var p = new Vector2(start.X, y);
            if (Button("##row", "", p, new Vector2(w, rowH), Text, active ? TabActive : BtnFill))
                ChatModel.SelectTab(tab);
            if (ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right)) { _ctxTab = tab; _openTabMenu = true; }
            if (unread.Mention && !active) ImGuiNET.ImGui.SetItemTooltip("Someone mentioned you here");
            else if (!ChatModel.IsBaseTab(tab)) ImGuiNET.ImGui.SetItemTooltip("Right-click to rename or delete this tab");
            float badgeW = badge != null ? BadgeWidth(badge) : 0;
            float ty = y + (rowH - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
            dl.PushClipRect(p, p + new Vector2(w - (badge != null ? badgeW + 8 : 0), rowH), true);
            dl.AddText(new Vector2(p.X + 6, ty), active ? Teal : Text, tab);
            dl.PopClipRect();
            if (badge != null) DrawBadge(dl, new Vector2(p.X + w - badgeW - 4, y + 3), badgeW, rowH - 6, badge, unread.Mention);
            ImGuiNET.ImGui.PopID();
            y += rowH + 2;
        }
        if (Button("##chat_addtab", PhosphorIcons.Plus + " New tab", new Vector2(start.X, y), new Vector2(w, rowH), TabAdd, BtnFill))
        {
            _pendingRule = null;
            WriteUtf8(_newTab, "");
            _openNewTab = _focusNewTab = true;
        }
        ImGuiNET.ImGui.SetItemTooltip("New tab (fill it with a rule: right-click a line, or Filters)");
    }

    /// <summary>Popped out (no drawer beside the window): the channels as a list under the button.</summary>
    private void DrawTabsPopup()
    {
        if (!ImGuiNET.ImGui.BeginPopup("##chat_tabs")) return;
        string current = ChatModel.CurrentTab();
        foreach (string tab in ChatModel.AllTabs())
        {
            _unread.TryGetValue(tab, out var unread);
            bool active = string.Equals(tab, current, StringComparison.OrdinalIgnoreCase);
            string label = !active && unread.Count > 0 ? $"{tab}  ({unread.Count})" : tab;
            if (ImGuiNET.ImGui.Selectable(label + "##tabsel_" + tab, active)) ChatModel.SelectTab(tab);
        }
        ImGuiNET.ImGui.Separator();
        if (ImGuiNET.ImGui.Selectable(PhosphorIcons.Plus + " New tab"))
        {
            _pendingRule = null;
            WriteUtf8(_newTab, "");
            _openNewTab = _focusNewTab = true;
        }
        ImGuiNET.ImGui.EndPopup();
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

    /// <summary>
    /// Unread counts per tab: lines shown there that arrived after it was last on
    /// screen. Recounted only when the lines, the routes or the tabs change.
    /// </summary>
    private void UpdateUnread(ChatLine[] lines, long linesVersion, string current)
    {
        long newest = lines.Length > 0 ? lines[^1].Id : 0;
        _seenId[current] = newest;
        long routes = ChatModel.RoutesVersion, tabsVersion = ChatModel.FiltersVersion;
        bool recount = routes != _unreadRoutes || tabsVersion != _unreadTabsVersion
            || !string.Equals(current, _unreadTab, StringComparison.Ordinal);
        if (!recount && linesVersion == _unreadLines) return;
        _unreadLines = linesVersion;
        _unreadRoutes = routes;
        _unreadTabsVersion = tabsVersion;
        _unreadTab = current;
        // Only new lines arrived: count just those (lines past _unreadCountedTo). A full recount
        // walks every unread line of every tab - up to the whole history per tab (3.7 ms at
        // 10,000 lines, tools/ChatHistoryBench), which used to run on every new batch.
        if (recount) _unread.Clear();
        foreach (string tab in ChatModel.AllTabs())
        {
            if (!_seenId.TryGetValue(tab, out long seen)) { _seenId[tab] = newest; continue; }   // a new tab starts read
            long from = recount ? seen : Math.Max(seen, _unreadCountedTo);
            int count = 0;
            bool mention = false;
            for (int i = lines.Length - 1; i >= 0 && lines[i].Id > from; i--)
                if (ChatModel.LineInTab(lines[i], tab)) { count++; mention |= lines[i].Route.Mention; }
            if (count == 0) continue;
            _unread.TryGetValue(tab, out var had);
            _unread[tab] = (had.Count + count, had.Mention || mention);
        }
        _unreadCountedTo = newest;
    }

    private void DrawSearch(Vector2 p, float w)
    {
        bool hasText = _search[0] != 0;
        TextBox("##chat_search", _search, p, hasText ? w - 24 : w, SearchHint, out _);
        if (hasText)
        {
            if (IconButton("##chat_search_x", PhosphorIcons.X, new Vector2(p.X + w - 22, p.Y), new Vector2(22, 22), Mute, BtnFill, font: UiFont.Ui10))
                WriteUtf8(_search, "");
            ImGuiNET.ImGui.SetItemTooltip("Clear the search");
        }
    }

    // ── Lines ────────────────────────────────────────────────────────────

    /// <summary>Brings _vis up to date with the snapshot, tab, search and routes.</summary>
    private void SyncLines(UiSnapshot<ChatLine[]>? snap, ChatLine[] lines, string tab)
    {
        string search = Utf8(_search);
        long routes = ChatModel.RoutesVersion;

        if (routes != _seenRoutes || tab != _seenTab || search != _seenSearch)
        {
            _seenRoutes = routes;
            _seenTab = tab;
            _seenSearch = search;
            _seenLinesVersion = snap?.Version ?? -1;
            _vis.Clear();
            _h.Clear();
            _lay.Clear();
            foreach (ChatLine line in lines)
                if (ChatModel.LineVisible(line, tab, search)) { _vis.Add(line); _h.Add(_rowH); _lay.Add(null); }
            _lastId = lines.Length > 0 ? lines[^1].Id : _lastId;
            _heightsFrom = _measuredFrom = _vis.Count;   // all unmeasured: MeasureLines, newest first
            _topsDirty = true;
            ClearSelection();
            _stick = true;   // a rebuild re-pins to the newest line
            _droppedAbove = 0f;
            return;
        }
        if (snap == null || snap.Version == _seenLinesVersion) return;
        _seenLinesVersion = snap.Version;

        // Drop lines the source no longer keeps, then add the new ones.
        long oldest = lines.Length > 0 ? lines[0].Id : long.MaxValue;
        int drop = 0;
        while (drop < _vis.Count && _vis[drop].Id < oldest) drop++;
        if (drop > 0 && !_selDragging)
        {
            if (!Following)
                for (int i = 0; i < drop; i++) _droppedAbove += _h[i];
            _vis.RemoveRange(0, drop);
            _h.RemoveRange(0, drop);
            _lay.RemoveRange(0, drop);
            _heightsFrom = Math.Max(0, _heightsFrom - drop);
            _measuredFrom = Math.Max(0, _measuredFrom - drop);
            if (_selAnchor >= 0) { _selAnchor -= drop; _selEnd -= drop; if (_selAnchor < 0 || _selEnd < 0) ClearSelection(); }
            _topsDirty = true;
        }
        foreach (ChatLine line in lines)
        {
            if (line.Id <= _lastId) continue;
            _lastId = line.Id;
            if (!ChatModel.LineVisible(line, tab, search)) continue;
            if (_heightsFrom > _vis.Count) _heightsFrom = _vis.Count;
            _vis.Add(line);
            _h.Add(_rowH);
            _lay.Add(null);
            _topsDirty = true;
        }
    }

    /// <summary>The view follows the newest line (auto-scroll on and not scrolled away).</summary>
    private bool Following => ChatModel.AutoScroll && _stick;

    private void DrawLines(Vector2 size)
    {
        float fontSize = ChatModel.FontSize;
        ImFontPtr font = ImGuiFonts.Chat(fontSize);
        ImGuiNET.ImGui.PushFont(font);
        bool showTs = ChatModel.ShowTimestamps;

        // The lines are measured BEFORE the list's BeginChild, so this frame's scroll can be
        // set for this frame's lines (see "Where the view goes" below). The width they wrap
        // to is the child's: its width less its scrollbar (always shown) and a margin;
        // _wrapFix corrects that guess if the child ever reports otherwise.
        float wrapW = Math.Max(20, MathF.Floor(size.X) - ListScrollbarW - 6 + _wrapFix);
        float tsW = showTs ? ImGuiNET.ImGui.CalcTextSize("00:00:00 ").X : 0;
        MeasureLines(font, wrapW, tsW, fontSize, showTs, MathF.Floor(size.Y));
        float total = _tops[_vis.Count];

        // Where the view goes this frame. (2026-10-11, the chat "bouncing": until now the
        // view followed the newest line with SetScrollHereY at the end of the frame, which
        // only lands in the NEXT frame's Begin. So every batch of new lines was first drawn
        // at the old scroll: with the buffer full, lines leave the top as others arrive,
        // and the text jumped by the difference - up and back down when a long line left
        // and a short one came - then snapped into place a frame later. A popped-out chat
        // redraws at 5 Hz while idle, so the wrong frame stayed up for 200 ms.) Now the
        // content height and the scroll are both handed to this frame's BeginChild:
        //  • the content size, so ImGui's scroll limit is this frame's and not last frame's
        //    (it clamps any scroll set before Begin to the limit it knows);
        //  • following: the bottom (the limit clamps 'total' to exactly the bottom);
        //  • not following: only the lines dropped off a full buffer's top, so what is
        //    being read stays put (fractions carried, so rounding can't creep the view).
        // A frame with wheel input over the list leaves the scroll to the wheel (this would
        // override it); a drag-select scrolls the list itself.
        Vector2 listPos = ImGuiNET.ImGui.GetCursorScreenPos();
        bool wheelHere = ImGuiNET.ImGui.GetIO().MouseWheel != 0f
            && ImGuiNET.ImGui.IsMouseHoveringRect(listPos, listPos + size, false);
        ImGuiNET.ImGui.SetNextWindowContentSize(new Vector2(0f, total));
        bool compensated = false;
        if (Following)
        {
            _droppedAbove = 0f;
            _scrollCarry = 0f;
            // Not counted as "our move": a scrollbar drag in this Begin overrides it, and the
            // check below must see that to stop following.
            if (!wheelHere && !_selDragging)
                ImGuiNET.ImGui.SetNextWindowScroll(new Vector2(-1f, total));
        }
        else if (_droppedAbove != 0f && !wheelHere)
        {
            float target = _lastScrollY + _scrollCarry - _droppedAbove;
            float applied = MathF.Round(Math.Max(0f, target));   // ImGui rounds scroll to whole pixels
            _scrollCarry = target > 0f ? target - applied : 0f;
            ImGuiNET.ImGui.SetNextWindowScroll(new Vector2(-1f, applied));
            _droppedAbove = 0f;
            compensated = true;
        }

        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, 0u);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, 0u);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, ListScrollbarW);
        bool open = ImGuiNET.ImGui.BeginChild("##chat_lines", size, ImGuiChildFlags.None, ImGuiWindowFlags.AlwaysVerticalScrollbar);
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(2);
        if (!open) { ImGuiNET.ImGui.EndChild(); ImGuiNET.ImGui.PopFont(); return; }

        // The child's real width, should the guess above ever be off (a style change):
        // measured again now, and the guess corrected from the next frame on.
        float realWrapW = Math.Max(20, ImGuiNET.ImGui.GetContentRegionAvail().X - 6);
        if (Math.Abs(realWrapW - wrapW) > 0.5f)
        {
            _wrapFix += realWrapW - wrapW;
            wrapW = realWrapW;
            MeasureLines(font, wrapW, tsW, fontSize, showTs, MathF.Floor(size.Y));
            total = _tops[_vis.Count];
        }
        float textW = Math.Max(20, wrapW - tsW);
        int n = _vis.Count;

        // Following the tail until the user scrolls away from it.
        float scrollY = ImGuiNET.ImGui.GetScrollY(), maxY = ImGuiNET.ImGui.GetScrollMaxY();
        if (compensated) _lastScrollY = scrollY;   // our move, not the user's
        else if (Math.Abs(scrollY - _lastScrollY) > 0.5f) _scrollCarry = 0f;   // the user's: start afresh
        // Wheel up over the chat stops following at once; reaching the bottom again resumes it.
        // Not while it has nothing to scroll: no scroll change would ever turn following back on.
        if (maxY > 0f && ImGuiNET.ImGui.IsWindowHovered() && ImGuiNET.ImGui.GetIO().MouseWheel > 0f)
            _stick = false;
        else if (Math.Abs(scrollY - _lastScrollY) > 0.5f)
            _stick = scrollY >= maxY - StickThreshold;

        Vector2 basePos = ImGuiNET.ImGui.GetCursorScreenPos();
        float viewH = ImGuiNET.ImGui.GetWindowHeight();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        int first = FirstAtOrBelow(scrollY, n);
        int lo = Math.Min(_selAnchor, _selEnd), hi = Math.Max(_selAnchor, _selEnd);
        for (int i = first; i < n && _tops[i] < scrollY + viewH; i++)
        {
            Vector2 p = basePos + new Vector2(0, _tops[i]);
            ChatLine line = _vis[i];
            ChatRoute route = line.Route;
            if (route.Mention)
                dl.AddRectFilled(p, p + new Vector2(wrapW + 6, _h[i]), MentionBg);
            if (lo >= 0 && i >= lo && i <= hi)
                dl.AddRectFilled(p, p + new Vector2(wrapW + 6, _h[i]), SelBg);
            uint col = RynthTheme.Argb(ChatModel.LineArgb(line));
            if (showTs) dl.AddText(font, font.FontSize, p + new Vector2(3, 0), TsColor, line.Timestamp);
            Vector2 tp = p + new Vector2(3 + tsW, 0);
            SpanLayout? lay = _lay[i];
            if (lay == null)
                dl.AddText(font, font.FontSize, tp, col, line.Text, textW);
            else
                foreach (Seg seg in lay.Segs)
                {
                    dl.AddText(font, font.FontSize, tp + seg.Offset, seg.Argb == 0 ? col : RynthTheme.Argb(seg.Argb), seg.Text);
                    if (seg.Link >= 0)   // a map coordinate: underlined, it's clickable
                    {
                        Vector2 u = tp + seg.Offset + new Vector2(0, font.FontSize - 1);
                        dl.AddLine(u, u + new Vector2(seg.W, 0), RynthTheme.Argb(LinkCol), 1f);
                    }
                }
        }
        ImGuiNET.ImGui.Dummy(new Vector2(wrapW, Math.Max(1, total)));
        HoverLink(basePos, tsW, n, out ChatCoord? hoveredLink);
        HandleSelection(basePos, wrapW, scrollY, viewH, n, hoveredLink);
        HandleLineMenu(basePos, wrapW, n);

        _lastScrollY = scrollY;

        // "Copied N lines" / "Rule added", top-right, for a moment.
        if (_flash != null)
        {
            if (ImGuiNET.ImGui.GetTime() > _flashUntil) _flash = null;
            else
            {
                ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
                Vector2 ts = ImGuiNET.ImGui.CalcTextSize(_flash);
                Vector2 wp = ImGuiNET.ImGui.GetWindowPos();
                Vector2 fp = new(Math.Max(wp.X + 2, wp.X + wrapW - ts.X - 12), wp.Y + 4);
                dl.AddRectFilled(fp, fp + ts + new Vector2(12, 4), FlashBg, 3);
                dl.AddText(fp + new Vector2(6, 2), Text, _flash);
                ImGuiNET.ImGui.PopFont();
            }
        }

        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopFont();   // pushed before the child, for MeasureLines
    }

    /// <summary>Time MeasureLines may spend a frame catching up on lines measured for another width.</summary>
    private const double MeasureBudgetMs = 2.0;
    private static readonly long MeasureBudgetTicks = (long)(System.Diagnostics.Stopwatch.Frequency * MeasureBudgetMs / 1000);

    /// <summary>
    /// Brings the line heights and tops up to date for this wrap width, font and timestamp
    /// setting. Chat font pushed. New lines are measured at once. When the width, font or
    /// timestamps change (or after a rebuild) every line needs measuring again: 16 ms at
    /// 10,000 lines (tools/ChatHistoryBench), every frame of a resize drag. So that is done
    /// a slice at a time: the lines in view first, then from the newest line up, for
    /// MeasureBudgetMs a frame; the others keep their old height meanwhile. Scrolled up,
    /// the view is moved by however much the lines above it grew or shrank, so what's being
    /// read stays put (the same way as lines dropping off the top).
    /// </summary>
    private void MeasureLines(ImFontPtr font, float wrapW, float tsW, float fontSize, bool showTs, float viewH)
    {
        // The text starts after the timestamp and wraps under itself.
        float textW = Math.Max(20, wrapW - tsW);
        _rowH = font.FontSize;
        if (wrapW != _seenWrap || fontSize != _seenFont || showTs != _seenTs)
        {
            _seenWrap = wrapW;
            _seenFont = fontSize;
            _seenTs = showTs;
            _measuredFrom = Math.Min(_heightsFrom, _vis.Count);   // all measured so far is stale
        }
        bool changed = false;
        for (int i = _heightsFrom; i < _vis.Count; i++) { MeasureLine(font, i, textW); changed = true; }
        _heightsFrom = _vis.Count;

        if (_measuredFrom > 0)
        {
            UpdateTops();
            int n = _vis.Count;
            bool following = Following;
            // Where the view will be (last frame's scroll, less any move still to come).
            float viewTop = _lastScrollY + _scrollCarry - _droppedAbove;
            int firstVisible = following || n == 0 ? int.MaxValue : FirstAtOrBelow(Math.Max(0f, viewTop), n);
            if (!following)
                for (int i = firstVisible; i < Math.Min(n, _measuredFrom) && _tops[i] < viewTop + viewH; i++)
                    MeasureLine(font, i, textW);
            float grewAbove = 0f;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int count = 0;
            while (_measuredFrom > 0)
            {
                int i = --_measuredFrom;
                float old = _h[i];
                MeasureLine(font, i, textW);
                if (i < firstVisible) grewAbove += _h[i] - old;
                if ((++count & 15) == 0 && System.Diagnostics.Stopwatch.GetTimestamp() - t0 > MeasureBudgetTicks) break;
            }
            if (!following) _droppedAbove -= grewAbove;
            changed = true;
        }
        if (changed) _topsDirty = true;
        UpdateTops();
    }

    private void MeasureLine(ImFontPtr font, int i, float textW)
    {
        ChatLine line = _vis[i];
        ChatSpan[]? spans = line.Route.Spans;
        // Map coordinates ("82.3N, 34.2E") become links. Found here, when the line is measured,
        // never per frame; a line without any keeps the plain (or highlighted) path.
        List<ChatCoord> coords = ChatCoords.MayHaveCoords(line.Text) ? ChatCoords.Find(line.Text) : NoCoords;
        if (coords.Count > 0)
        {
            ChatSpan[] withLinks = ChatCoords.LinkSpans(spans, coords, LinkCol);
            SpanLayout lay = LayoutSpans(font, line.Text, withLinks, textW, coords);
            _lay[i] = lay;
            _h[i] = lay.Height;
        }
        else if (spans != null)
        {
            SpanLayout lay = LayoutSpans(font, line.Text, spans, textW);
            _lay[i] = lay;
            _h[i] = lay.Height;
        }
        else
        {
            _lay[i] = null;
            _h[i] = ImGuiNET.ImGui.CalcTextSize(line.Text, false, textW).Y;
        }
    }

    private void UpdateTops()
    {
        if (!_topsDirty && _tops.Length > _vis.Count) return;
        if (_tops.Length < _vis.Count + 1) _tops = new float[Math.Max(_vis.Count + 1, _tops.Length * 2)];
        _tops[0] = 0;
        for (int i = 0; i < _vis.Count; i++) _tops[i + 1] = _tops[i] + _h[i];
        _topsDirty = false;
    }

    private void Flash(string message, double seconds = 2.0)
    {
        _flash = message;
        _flashUntil = ImGuiNET.ImGui.GetTime() + seconds;
    }

    // ── Highlighted lines: hand layout ───────────────────────────────────

    // Argb 0 = the line's colour. W: the run's width. Link: index into SpanLayout.Links (a map
    // coordinate drawn as a link: click = RynthNav arrow, Ctrl+click = go), -1 for plain text.
    private readonly record struct Seg(string Text, Vector2 Offset, uint Argb, float W = 0f, int Link = -1);

    private sealed class SpanLayout
    {
        public required float Height;
        public required Seg[] Segs;
        /// <summary>The line's map coordinates (ChatCoords), when any are drawn as links.</summary>
        public ChatCoord[]? Links;
    }

    /// <summary>Coordinate links in chat lines: soft teal, underlined (0xAARRGGBB, like ChatSpan.Argb).</summary>
    private const uint LinkCol = 0xFF5ECFE0;

    private static readonly List<ChatCoord> NoCoords = new();

    /// <summary>
    /// Word-wraps <paramref name="text"/> to <paramref name="wrapW"/> and cuts each
    /// row where a highlight starts or ends, so every run is drawn in its own
    /// colour. Called when a line is measured, never per frame.
    /// <paramref name="links"/>: the line's map coordinates, whose runs get a link index.
    /// </summary>
    private static SpanLayout LayoutSpans(ImFontPtr font, string text, ChatSpan[] spans, float wrapW, List<ChatCoord>? links = null)
    {
        // Which coordinate link a run [s0, s1) belongs to (-1 none).
        int LinkOf(int s0, int s1)
        {
            if (links == null) return -1;
            for (int k = 0; k < links.Count; k++)
                if (s0 >= links[k].Start && s1 <= links[k].Start + links[k].Length) return k;
            return -1;
        }

        float size = font.FontSize;
        float Width(int start, int end) => end <= start ? 0 : font.CalcTextSizeA(size, float.MaxValue, 0f, text[start..end]).X;

        // Rows as [start, end) character ranges.
        var rows = new List<(int Start, int End)>();
        int rowStart = 0;
        float x = 0;
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\n') { rows.Add((rowStart, i)); rowStart = i + 1; x = 0; i++; continue; }
            // A word and the spaces after it.
            int j = i;
            while (j < text.Length && text[j] != ' ' && text[j] != '\n') j++;
            while (j < text.Length && text[j] == ' ') j++;
            float ww = Width(i, j);
            int wordEnd = j;
            while (wordEnd > i && text[wordEnd - 1] == ' ') wordEnd--;
            float inkW = Width(i, wordEnd);
            if (x > 0 && x + inkW > wrapW)
            {
                rows.Add((rowStart, i));
                rowStart = i;
                x = 0;
            }
            if (x == 0 && inkW > wrapW)
            {
                // A word wider than the row: break it by characters.
                int k = i;
                while (k < wordEnd)
                {
                    int m = k + 1;
                    while (m < wordEnd && Width(k, m + 1) <= wrapW) m++;
                    if (m < wordEnd) { rows.Add((rowStart, m)); rowStart = m; }
                    else x = Width(k, j);
                    k = m;
                }
                i = j;
                continue;
            }
            x += ww;
            i = j;
        }
        rows.Add((rowStart, text.Length));

        var segs = new List<Seg>();
        for (int r = 0; r < rows.Count; r++)
        {
            (int a, int b) = rows[r];
            float y = r * size, sx = 0;
            int c = a;
            foreach (ChatSpan span in spans)
            {
                int s0 = Math.Max(span.Start, a), s1 = Math.Min(span.Start + span.Length, b);
                if (s1 <= s0) continue;
                if (s0 > c) { segs.Add(new Seg(text[c..s0], new Vector2(sx, y), 0)); sx += Width(c, s0); }
                float spanW = Width(s0, s1);
                segs.Add(new Seg(text[s0..s1], new Vector2(sx, y), span.Argb, spanW, LinkOf(s0, s1)));
                sx += spanW;
                c = s1;
            }
            if (c < b) segs.Add(new Seg(text[c..b], new Vector2(sx, y), 0));
        }
        return new SpanLayout { Height = Math.Max(1, rows.Count) * size, Segs = segs.ToArray(), Links = links?.ToArray() };
    }

    /// <summary>Index of the line containing content y (clamped to the list).</summary>
    private int FirstAtOrBelow(float y, int n)
    {
        int lo = 0, hi = n;   // first i with _tops[i + 1] > y
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_tops[mid + 1] > y) hi = mid; else lo = mid + 1;
        }
        return Math.Min(lo, Math.Max(0, n - 1));
    }

    /// <summary>
    /// The map coordinate link under the mouse, if any (hand cursor and a tooltip). Only the
    /// hovered line's runs are looked at.
    /// </summary>
    private void HoverLink(Vector2 basePos, float tsW, int n, out ChatCoord? link)
    {
        link = null;
        if (n == 0 || _selDragging || !ImGuiNET.ImGui.IsWindowHovered()) return;
        Vector2 mouse = ImGuiNET.ImGui.GetMousePos();
        float contentY = mouse.Y - basePos.Y;
        if (contentY < 0 || contentY >= _tops[n]) return;
        int i = FirstAtOrBelow(contentY, n);
        if (_lay[i] is not { Links: { Length: > 0 } links } lay) return;
        Vector2 tp = basePos + new Vector2(3 + tsW, _tops[i]);
        float h = ImGuiNET.ImGui.GetFontSize();
        foreach (Seg seg in lay.Segs)
        {
            if (seg.Link < 0 || seg.Link >= links.Length) continue;
            Vector2 a = tp + seg.Offset, b = a + new Vector2(seg.W, h);
            if (mouse.X < a.X || mouse.X >= b.X || mouse.Y < a.Y || mouse.Y >= b.Y) continue;
            link = links[seg.Link];
            ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            bool nav = ImGuiPanelHost.HasFace(RynthNavFace.Title);
            SafeImGui.SetTooltip(nav
                ? $"Click: point the arrow at {link.Value.Text}\nCtrl+click: RynthNav walks there\nRight-click: more"
                : $"{link.Value.Text}\n(RynthNav isn't loaded: no arrow)");
            return;
        }
    }

    /// <summary>A click on a coordinate link: RynthNav's arrow there, or (Ctrl) walk there.</summary>
    private void ClickLink(ChatCoord c)
    {
        if (!ImGuiPanelHost.HasFace(RynthNavFace.Title)) { Flash("RynthNav isn't loaded", 1.5); return; }
        if (ImGuiNET.ImGui.GetIO().KeyCtrl)
        {
            RynthNavCommands.Go(c.Text, c.Ns, c.Ew);
            Flash("Going to " + c.Text, 1.5);
        }
        else
        {
            RynthNavCommands.Arrow(c.Text, c.Ns, c.Ew);
            Flash("Arrow: " + c.Text, 1.5);
        }
    }

    private void HandleSelection(Vector2 basePos, float wrapW, float scrollY, float viewH, int n, ChatCoord? hoveredLink = null)
    {
        if (n == 0) { ClearSelection(); return; }
        Vector2 mouse = ImGuiNET.ImGui.GetMousePos();
        float contentY = mouse.Y - basePos.Y;
        if (!_selDragging)
        {
            if (!ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                || !ImGuiNET.ImGui.IsWindowHovered()
                || mouse.X > basePos.X + wrapW + 6)   // the scrollbar
                return;
            ClearSelection();
            if (contentY < 0 || contentY >= _tops[n]) return;
            _selAnchor = _selEnd = FirstAtOrBelow(contentY, n);
            _selDragging = true;
            _selMoved = false;
            _selDown = mouse;
            _downLink = hoveredLink;   // a click (no drag) on it acts on release
            return;
        }

        if (Math.Abs(mouse.X - _selDown.X) > 3 || Math.Abs(mouse.Y - _selDown.Y) > 3) _selMoved = true;
        _selEnd = FirstAtOrBelow(Math.Clamp(contentY, 0, _tops[n] - 1), n);

        // Dragging past the edges scrolls.
        Vector2 wp = ImGuiNET.ImGui.GetWindowPos();
        if (mouse.Y < wp.Y) ImGuiNET.ImGui.SetScrollY(Math.Max(0, scrollY - 12));
        else if (mouse.Y > wp.Y + viewH) ImGuiNET.ImGui.SetScrollY(scrollY + 12);

        if (ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left)) return;
        _selDragging = false;
        // A drag (even within one line) copies; a plain click just clears, or follows a
        // coordinate link it was pressed on.
        if (!_selMoved && _selEnd == _selAnchor)
        {
            ChatCoord? link = _downLink;
            ClearSelection();
            if (link is { } c) ClickLink(c);
            return;
        }
        int lo = Math.Min(_selAnchor, _selEnd), hi = Math.Max(_selAnchor, _selEnd);
        var parts = new List<string>(hi - lo + 1);
        for (int i = lo; i <= hi && i < _vis.Count; i++) parts.Add(CopyText(_vis[i]));
        Flash(ChatModel.CopyLines(parts), 1.5);
    }

    private static string CopyText(ChatLine line) => ChatModel.ShowTimestamps ? line.FormattedText : line.Text;

    /// <summary>Right-click on a line: its menu (drawn in DrawPopups).</summary>
    private void HandleLineMenu(Vector2 basePos, float wrapW, int n)
    {
        if (n == 0 || _selDragging || !ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right) || !ImGuiNET.ImGui.IsWindowHovered())
            return;
        Vector2 mouse = ImGuiNET.ImGui.GetMousePos();
        float contentY = mouse.Y - basePos.Y;
        if (mouse.X > basePos.X + wrapW + 6 || contentY < 0 || contentY >= _tops[n]) return;
        _ctxLine = _vis[FirstAtOrBelow(contentY, n)];
        _ctxPattern = ChatModel.PatternFor(_ctxLine);
        _ctxCoords = ImGuiPanelHost.HasFace(RynthNavFace.Title) ? ChatCoords.Find(_ctxLine.Text) : new List<ChatCoord>();
        _openLineMenu = true;
    }

    private void ClearSelection()
    {
        _selAnchor = _selEnd = -1;
        _selDragging = _selMoved = false;
        _downLink = null;
    }

    /// <summary>The coordinate link the left button went down on (acted on if it comes up without a drag).</summary>
    private ChatCoord? _downLink;

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

    // ── Rules from a line ────────────────────────────────────────────────

    private void RuleFromLine(ChatRuleAction action, string? tab)
    {
        var rule = new ChatFilterRule { Pattern = _ctxPattern, Action = action, Tab = tab ?? "" };
        if (action == ChatRuleAction.Color)
        {
            rule.ColorMode = ChatColorMode.Line;
            ChatModel.AddRule(rule);
            if (!ImGuiPanelHost.IsOpen("ChatFilters")) PanelRouter.Toggle("ChatFilters");
            Flash("Rule added - pick its colour in Filters");
            return;
        }
        ChatModel.AddRule(rule);
        Flash(action switch
        {
            ChatRuleAction.Move => $"Rule added: lines like this move to {tab}",
            ChatRuleAction.Copy => $"Rule added: lines like this also show in {tab}",
            _ => "Rule added: lines like this are hidden (undo in Filters)",
        }, 3);
    }

    private void TabMenu(string id, ChatRuleAction action)
    {
        if (!ImGuiNET.ImGui.BeginMenu(id)) return;
        foreach (string t in ChatModel.TargetTabs())
            if (ImGuiNET.ImGui.MenuItem(t)) RuleFromLine(action, t);
        ImGuiNET.ImGui.Separator();
        if (ImGuiNET.ImGui.MenuItem("New tab…"))
        {
            _pendingRule = new ChatFilterRule { Pattern = _ctxPattern, Action = action };
            WriteUtf8(_newTab, "");
            _openNewTab = _focusNewTab = true;
        }
        ImGuiNET.ImGui.EndMenu();
    }

    // ── Popups ───────────────────────────────────────────────────────────

    private void DrawPopups()
    {
        if (ToggleChannelsRequested)
        {
            ToggleChannelsRequested = false;
            if (ImGuiPopOuts.InPopOutFrame) _openTabsPopup = true;
            else _drawers.Toggle(_channels);
        }
        if (_openSettings)
        {
            _openSettings = false;
            WriteUtf8(_mentionWords, ChatModel.MentionWords);
            ImGuiNET.ImGui.OpenPopup("##chat_settings");
        }
        if (_openNewTab) { _openNewTab = false; ImGuiNET.ImGui.OpenPopup("##chat_newtab"); }
        if (_openTabMenu) { _openTabMenu = false; ImGuiNET.ImGui.OpenPopup("##chat_tabmenu"); }
        if (_openLineMenu) { _openLineMenu = false; ImGuiNET.ImGui.OpenPopup("##chat_linemenu"); }
        if (_openRename) { _openRename = false; ImGuiNET.ImGui.OpenPopup("##chat_rename"); }
        if (_openTabsPopup) { _openTabsPopup = false; ImGuiNET.ImGui.OpenPopup("##chat_tabs"); }

        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, RynthTheme.Argb(0xF2060C14));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, TabActive);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, BtnFill);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.CheckMark, Teal);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.SliderGrab, Teal);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Amber);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.HeaderHovered, TabActive);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));

        DrawSettingsPopup();
        DrawTabsPopup();
        DrawNewTabPopup();
        DrawTabMenu();
        DrawRenamePopup();
        DrawLineMenu();

        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(8);
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
            if (auto) _stick = true;
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

    private void DrawNewTabPopup()
    {
        if (!ImGuiNET.ImGui.BeginPopup("##chat_newtab"))
        {
            _pendingRule = null;
            return;
        }
        if (_pendingRule != null)
            Label(_pendingRule.Action == ChatRuleAction.Copy ? "New tab for a copy of lines like this:" : "New tab for lines like this:", Mute);
        if (_focusNewTab) { ImGuiNET.ImGui.SetKeyboardFocusHere(); _focusNewTab = false; }
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        bool enter = TextBox("##newtab_name", _newTab, p, 120, "tab name", out _, ImGuiInputTextFlags.EnterReturnsTrue, 20);
        bool add = Button("##newtab_add", "Add", new Vector2(p.X + 124, p.Y), new Vector2(ButtonWidth("Add"), 20), Text, TabActive);
        bool cancel = Button("##newtab_cancel", "Cancel", new Vector2(p.X + 128 + ButtonWidth("Add"), p.Y), new Vector2(ButtonWidth("Cancel"), 20), Text, BtnFill);
        NextLine(p, 20);
        if (enter || add)
        {
            ChatFilterRule? pending = _pendingRule;
            string? name = ChatModel.AddCustomTab(Utf8(_newTab), select: pending == null);
            if (name != null && pending != null)
            {
                pending.Tab = name;
                ChatModel.AddRule(pending);
                Flash(pending.Action == ChatRuleAction.Copy
                    ? $"Rule added: lines like this also show in {name}"
                    : $"Rule added: lines like this move to {name}", 3);
            }
            _pendingRule = null;
            ImGuiNET.ImGui.CloseCurrentPopup();
        }
        else if (cancel || ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _pendingRule = null;
            ImGuiNET.ImGui.CloseCurrentPopup();
        }
        ImGuiNET.ImGui.EndPopup();
    }

    private void DrawTabMenu()
    {
        if (!ImGuiNET.ImGui.BeginPopup("##chat_tabmenu")) return;
        string tab = _ctxTab ?? "";
        Label(tab, Teal);
        ImGuiNET.ImGui.Separator();
        if (ImGuiNET.ImGui.MenuItem("Mark as read"))
        {
            var lines = UiSources.Chat.Current?.Value;
            _seenId[tab] = lines is { Length: > 0 } ? lines[^1].Id : 0;
            _unread.Remove(tab);
        }
        if (ImGuiNET.ImGui.MenuItem("Mark all tabs read"))
        {
            var lines = UiSources.Chat.Current?.Value;
            long newest = lines is { Length: > 0 } ? lines[^1].Id : 0;
            foreach (string t in ChatModel.AllTabs()) _seenId[t] = newest;
            _unread.Clear();
        }
        if (!ChatModel.IsBaseTab(tab))
        {
            if (ImGuiNET.ImGui.MenuItem("Rename…"))
            {
                _renameFrom = tab;
                WriteUtf8(_newTab, tab);
                _openRename = _focusNewTab = true;
            }
            if (ImGuiNET.ImGui.MenuItem("Delete tab")) ChatModel.DeleteCustomTab(tab);
        }
        if (ImGuiNET.ImGui.MenuItem("Edit rules…") && !ImGuiPanelHost.IsOpen("ChatFilters"))
            PanelRouter.Toggle("ChatFilters");
        ImGuiNET.ImGui.EndPopup();
    }

    private void DrawRenamePopup()
    {
        if (!ImGuiNET.ImGui.BeginPopup("##chat_rename")) return;
        Label($"Rename \"{_renameFrom}\" (its rules follow):", Mute);
        if (_focusNewTab) { ImGuiNET.ImGui.SetKeyboardFocusHere(); _focusNewTab = false; }
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        bool enter = TextBox("##rename_name", _newTab, p, 140, "tab name", out _, ImGuiInputTextFlags.EnterReturnsTrue, 20);
        bool ok = Button("##rename_ok", "Rename", new Vector2(p.X + 144, p.Y), new Vector2(ButtonWidth("Rename"), 20), Text, TabActive);
        NextLine(p, 20);
        if (enter || ok)
        {
            if (_renameFrom != null && !ChatModel.RenameCustomTab(_renameFrom, Utf8(_newTab)))
                Flash("That name is taken (or blank)");
            ImGuiNET.ImGui.CloseCurrentPopup();
        }
        else if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Escape))
            ImGuiNET.ImGui.CloseCurrentPopup();
        ImGuiNET.ImGui.EndPopup();
    }

    private void DrawLineMenu()
    {
        ImGuiNET.ImGui.SetNextWindowSizeConstraints(new Vector2(200, 0), new Vector2(420, float.MaxValue));
        if (!ImGuiNET.ImGui.BeginPopup("##chat_linemenu")) return;
        ChatLine? line = _ctxLine;
        if (line == null) { ImGuiNET.ImGui.EndPopup(); return; }
        // Coordinates in the line: point RynthNav's arrow there, or walk there (left-click
        // selects lines, so this lives in the right-click menu).
        for (int i = 0; i < _ctxCoords.Count && i < 3; i++)
        {
            ChatCoord c = _ctxCoords[i];
            ImGuiNET.ImGui.PushID(i);
            if (ImGuiNET.ImGui.MenuItem(PhosphorIcons.NavigationArrow + "  Arrow to " + c.Text))
                RynthNavCommands.Arrow(c.Text, c.Ns, c.Ew);
            if (ImGuiNET.ImGui.MenuItem(PhosphorIcons.Play + "  Go to " + c.Text))
                RynthNavCommands.Go(c.Text, c.Ns, c.Ew);
            ImGuiNET.ImGui.PopID();
        }
        if (_ctxCoords.Count > 0) ImGuiNET.ImGui.Separator();
        if (ImGuiNET.ImGui.MenuItem("Copy line")) Flash(ChatModel.CopyLines(new[] { CopyText(line) }), 1.5);
        ImGuiNET.ImGui.Separator();
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Mute);
        ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + 400);
        ImGuiNET.ImGui.TextUnformatted("Lines like this:  " + _ctxPattern);
        ImGuiNET.ImGui.PopTextWrapPos();
        ImGuiNET.ImGui.PopStyleColor();
        TabMenu("Move them to", ChatRuleAction.Move);
        TabMenu("Also show them in", ChatRuleAction.Copy);
        if (ImGuiNET.ImGui.MenuItem("Hide them")) RuleFromLine(ChatRuleAction.Hide, null);
        if (ImGuiNET.ImGui.MenuItem("Colour them…")) RuleFromLine(ChatRuleAction.Color, null);
        ImGuiNET.ImGui.Separator();
        if (ImGuiNET.ImGui.MenuItem("Edit rules…") && !ImGuiPanelHost.IsOpen("ChatFilters"))
            PanelRouter.Toggle("ChatFilters");
        ImGuiNET.ImGui.EndPopup();
    }
}

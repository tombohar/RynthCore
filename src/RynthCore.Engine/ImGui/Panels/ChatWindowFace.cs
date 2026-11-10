// ============================================================================
//  RynthCore.Engine - ImGui/Panels/ChatWindowFace.cs
//  Extra chat windows (2026-11-09; retail AC has them): "Chat 2" to "Chat 7"
//  (UI/Data/ChatWindows.cs), each showing one tab of RynthChat beside the main
//  chat, with its own scroll (following the newest line until you scroll up),
//  search, unread counts and right-click menus - a ChatView of its own, drawn
//  by the same code as the main chat's.
//
//  Opening: the main chat's New chat window button (a list of tabs), "Open in
//  new window" in any tab's right-click menu (main chat or an extra window), or
//  /rc chatwindow open [tab]. Each window opens in the first free slot. Its
//  header row: the tab button (click: the tabs, to switch this window's tab;
//  right-click: the tab's menu), search, New chat window and Filters. The
//  window's own header has pop-out and close, as every panel's.
//
//  Each window is an ordinary engine panel to the host (registered once, by
//  key), so it opens, closes, moves, resizes, pops out of the client into its
//  own window and docks back like the main chat, independently of the others,
//  and where it is, its size, docked or popped out and whether it is open are
//  saved in the panel layout (panel_state.txt, per character) and come back
//  after a hot reload or a restart (ImGuiPanelHost.RestoreOnce). The tab it
//  shows is saved in that layout too (PanelStateStore value chatwin.<n>.tab),
//  written only when the player opens a window on a tab, picks another tab
//  for it, or renames that tab; a tab that is deleted shows as All without
//  changing what is saved.
//
//  No input line: there is one chat entry (Enter, the typed line, Tell), the
//  main chat's. These windows don't count as the chat being shown
//  (ChatModel.SetShown), so Enter never types into a window without a box.
//  AC's thread (the panel host), except the static Open/Close/Describe calls
//  (any thread: they only queue panel requests and read the store).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class ChatWindowFace : IImGuiPanel
{
    private readonly int _number;
    private readonly string _key;
    private readonly ChatView _view;
    /// <summary>The tab saved for this window ("" when none).</summary>
    private string _savedTab = "";
    private int _readGeneration = -1, _readVersion = -1;
    private string _shownHeader = "";

    /// <summary>Goes up when any window's saved tab changes: the open ones read theirs again.</summary>
    private static int s_tabsVersion;

    private ChatWindowFace(int number)
    {
        _number = number;
        _key = ChatWindows.Key(number);
        _view = new ChatView(_key, isMain: false, () => CurrentTab, tab => SaveTab(_number, tab));
    }

    /// <summary>The extra windows' panels: once, at engine init, beside the main chat.</summary>
    public static void RegisterAll()
    {
        for (int n = ChatWindows.FirstNumber; n <= ChatWindows.LastNumber; n++)
        {
            int number = n;
            ImGuiPanelHost.Register(ChatWindows.Key(number), ChatFace.Spec, () => new ChatWindowFace(number));
        }
    }

    private string CurrentTab => ChatWindows.EffectiveTab(_savedTab, ChatModel.AllTabs());

    public bool ClickThrough => ChatModel.CtrlGatedClickThrough && !ImGuiNET.ImGui.GetIO().KeyCtrl;

    public int PopOutIdleHz => 5;

    public void OnShown()
    {
        ChatModel.EnsureSettingsLoaded();
        UiSources.Chat.Subscribe();
        UiSources.Chat.RequestRefresh();
        ReadTab();
        UpdateHeader();
        _view.OnShown();
    }

    public void OnHidden()
    {
        UiSources.Chat.Unsubscribe();
        _view.OnHidden();
    }

    /// <summary>Reads this window's saved tab (on open, after a layout switch, after a tab was saved).</summary>
    private void ReadTab()
    {
        _readGeneration = PanelStateStore.Generation;
        _readVersion = Volatile.Read(ref s_tabsVersion);
        _savedTab = PanelStateStore.TryGetValue(ChatWindows.TabValueKey(_number), out string t) ? t : "";
    }

    private void UpdateHeader()
    {
        string header = ChatWindows.HeaderText(_number, CurrentTab);
        if (string.Equals(header, _shownHeader, StringComparison.Ordinal)) return;
        _shownHeader = header;
        ImGuiPanelHost.SetDisplayTitle(_key, header);
    }

    public void Draw()
    {
        if (PanelStateStore.Generation != _readGeneration || Volatile.Read(ref s_tabsVersion) != _readVersion)
            ReadTab();
        UpdateHeader();

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

        // Top row: the tab button, search, New chat window, Filters.
        const float h = 22;
        var start = new Vector2(x0, origin.Y + 4);
        float right = x0 + w, btnW = 22;
        if (IconButton("##chatwin_filters", PhosphorIcons.Funnel, new Vector2(right - btnW, start.Y), new Vector2(btnW, h), Text, BtnFill))
            PanelRouter.Toggle("ChatFilters");
        ImGuiNET.ImGui.SetItemTooltip("Filters: move, copy or hide lines by regex, and colour them");
        float winX = right - btnW - 3 - btnW;
        _view.DrawNewWindowButton("##chatwin_newwin", new Vector2(winX, start.Y), new Vector2(btnW, h));
        if (_view.DrawTabButton("##chatwin_tab", start, w * 0.45f, h, highlight: false))
            _view.OpenTabsPopup();
        if (ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right)) _view.OpenTabMenu(tab);
        float sx = start.X + _view.LastTabButtonWidth + 4, sw = winX - 4 - sx;
        if (sw > 40) _view.DrawSearch(new Vector2(sx, start.Y), sw);
        float y = start.Y + h + 4;

        _view.SyncLines(snap, lines, tab);

        // The scrollback fills the rest, ending above the resize grip in the bottom-right corner.
        float reserve = ImGuiPanelHost.BodyGripReserve;
        float bottom = origin.Y + size.Y - Math.Max(2f, reserve);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x0, y));
        _view.DrawLines(new Vector2(w, Math.Max(20, bottom - y)));

        ImGuiNET.ImGui.PopStyleVar();
        ChatView.PushPopupStyle();
        _view.DrawPopups();
        ChatView.PopPopupStyle();
        ImGuiNET.ImGui.PopFont();
    }

    // ── Opening, closing and the saved tabs (any thread) ────────────────────

    private static string SavedTab(int number) =>
        PanelStateStore.TryGetValue(ChatWindows.TabValueKey(number), out string t) ? t : "";

    /// <summary>Saves the tab window <paramref name="number"/> shows: the player picked it.</summary>
    private static void SaveTab(int number, string tab)
    {
        PanelStateStore.SetValue(ChatWindows.TabValueKey(number), tab);
        Interlocked.Increment(ref s_tabsVersion);
    }

    /// <summary>The extra windows are registered (RynthChat is installed and the ImGui layer is on). Any thread.</summary>
    internal static bool Available =>ImGuiPanelHost.HasFace(ChatWindows.Key(ChatWindows.FirstNumber));

    private const string NotAvailable = "Chat windows aren't available (the ImGui layer is off?).";

    /// <summary>
    /// Opens <paramref name="tab"/> (null or blank: the main chat's tab) in the first free extra
    /// window; one that already shows it stays the only one. The player asked for it (a button,
    /// a menu or a command). Returns whether a window shows it now, and what to tell the player.
    /// </summary>
    public static (bool Ok, string Message) OpenNew(string? tab)
    {
        if (!Available) return (false, NotAvailable);
        string[] all = ChatModel.AllTabs();
        string? resolved = string.IsNullOrWhiteSpace(tab) ? ChatModel.CurrentTab() : ChatWindows.Resolve(tab, all);
        if (resolved == null) return (false, $"No tab called '{tab!.Trim()}'. Tabs: {string.Join(", ", all)}.");
        for (int n = ChatWindows.FirstNumber; n <= ChatWindows.LastNumber; n++)
            if (ImGuiPanelHost.IsOpen(ChatWindows.Key(n))
                && string.Equals(ChatWindows.EffectiveTab(SavedTab(n), all), resolved, StringComparison.OrdinalIgnoreCase))
                return (true, $"Chat {n} already shows {resolved}.");
        int free = ChatWindows.FirstFree(ImGuiPanelHost.IsOpen);
        if (free == 0) return (false, $"All {ChatWindows.MaxWindows} extra chat windows are open: close one first.");
        SaveTab(free, resolved);
        ImGuiPanelHost.Open(ChatWindows.Key(free));
        return (true, $"Chat {free} opened on {resolved}.");
    }

    /// <summary>Shows <paramref name="tab"/> in window <paramref name="number"/> (opening it if closed).</summary>
    public static (bool Ok, string Message) ShowTab(int number, string tab)
    {
        if (!Available) return (false, NotAvailable);
        string[] all = ChatModel.AllTabs();
        string? resolved = ChatWindows.Resolve(tab, all);
        if (resolved == null) return (false, $"No tab called '{tab.Trim()}'. Tabs: {string.Join(", ", all)}.");
        SaveTab(number, resolved);
        bool wasOpen = ImGuiPanelHost.IsOpen(ChatWindows.Key(number));
        if (!wasOpen) ImGuiPanelHost.Open(ChatWindows.Key(number));
        return (true, wasOpen ? $"Chat {number} now shows {resolved}." : $"Chat {number} opened on {resolved}.");
    }

    public static (bool Ok, string Message) Close(int number)
    {
        if (!Available) return (false, NotAvailable);
        string key = ChatWindows.Key(number);
        if (!ImGuiPanelHost.IsOpen(key)) return (false, $"Chat {number} isn't open.");
        ImGuiPanelHost.Close(key);
        return (true, $"Chat {number} closed.");
    }

    public static (bool Ok, string Message) CloseAll()
    {
        if (!Available) return (false, NotAvailable);
        int closed = 0;
        foreach (string key in ChatWindows.Keys())
            if (ImGuiPanelHost.IsOpen(key)) { ImGuiPanelHost.Close(key); closed++; }
        return (true, closed == 0 ? "No extra chat window is open." : $"Closed {closed} chat window{(closed == 1 ? "" : "s")}.");
    }

    /// <summary>One line per extra window: open or closed, its tab, docked or popped out.</summary>
    public static List<string> Describe()
    {
        var lines = new List<string>();
        if (!Available) { lines.Add(NotAvailable); return lines; }
        string[] all = ChatModel.AllTabs();
        int open = 0;
        for (int n = ChatWindows.FirstNumber; n <= ChatWindows.LastNumber; n++)
        {
            string key = ChatWindows.Key(n);
            if (!ImGuiPanelHost.IsOpen(key)) continue;
            open++;
            string saved = SavedTab(n);
            string tab = ChatWindows.EffectiveTab(saved, all);
            string gone = saved.Length > 0 && !string.Equals(saved, tab, StringComparison.OrdinalIgnoreCase) ? $" ({saved} is gone)" : "";
            lines.Add($"Chat {n}: {tab}{gone}, {(ImGuiPanelHost.IsPopped(key) ? "popped out" : "in the client")}.");
        }
        lines.Insert(0, open == 0
            ? "No extra chat window is open (the main chat's window button, or right-click a tab: Open in new window). " + ChatWindows.Usage
            : $"{open} of {ChatWindows.MaxWindows} extra chat windows open:");
        return lines;
    }

    /// <summary>
    /// The player renamed a tab (ChatModel.RenameCustomTab): the windows that show it follow. Any thread.
    /// </summary>
    public static void OnTabRenamed(string from, string to)
    {
        for (int n = ChatWindows.FirstNumber; n <= ChatWindows.LastNumber; n++)
            if (ChatWindows.FollowRename(SavedTab(n), from, to) is string next)
                SaveTab(n, next);
    }
}

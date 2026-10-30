// ============================================================================
//  RynthCore.Engine - ImGui/Panels/InventoryFace.cs
//  The in-game Inventory panel (docs/IMGUI_INVENTORY.md). Engine only.
//  Two looks, one set of behaviours (a toggle switches them, remembered):
//    Classic   the retail inventory window, drawn from the client's own
//              pictures (InventoryFace.Classic.cs; Phase 2)
//    Modern    the Phase 1 layout, below
//
//    header    burden (percent of capacity) and pyreals; item count; buttons for
//              Mini Remote, RynthSort (/rsort), Item Count (/rc itemcount) and
//              Global Inventory (/ginv), folded into one menu when the header is short;
//              on Aelrynth also the Vault (always there; the grid shows the account
//              vault, picking a pack goes back)
//    tools     search box, attribute filter (InventoryFace.AttrFilter.cs), sort
//              picker; category chips
//    packs     All, Main pack, each side pack (its icon and fill), Worn
//    grid      the chosen pack's items as icons with stack counts; hover for a
//              tooltip (asks the server to identify an unknown item, throttled);
//              double-click uses; right-click for Use / Equip / Split / Move to /
//              Drop / Give; drag onto a pack to move, onto Equip / Drop / Give
//    zones     Equip, Drop, Give-to-<selected creature>: drop targets for a drag,
//              or buttons for the selected item(s)
//    status    the one action waiting for the server (with a timer), or how the
//              last one ended; a several-item run's progress and Stop
//    several   Ctrl+click / Shift+click / Select all pick several items for Drop,
//              Give or Move (InventoryFace.MultiSelect.cs)
//
//  Data: InventoryModel (Compatibility), captured and acted on on AC's main
//  thread. This face only reads its published objects and posts requests; one
//  item action at a time (a click while one waits is refused, never queued).
//  Several items go as a batch the model sends one after another.
//  Per frame: no allocation except the tooltip of a newly hovered item and the
//  filtered list when the view, pack, filter, search or sort change.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Net;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.ScriptWindows;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed partial class InventoryFace : IImGuiPanel
{
    public const string Title = "Inventory";

    // Default: the Classic view at its retail size (300 x 362 body, plus the status strip).
    // Chromeless: the Classic look has the retail title bar (drag, close) and the Modern look
    // draws its own background, drag area and close button, so the engine's DRAG header would
    // be a second title bar.
    public static void Register() => ImGuiPanelHost.Register(Title,
        new PanelSpec(new Vector2(302, 426), new Vector2(140, 300), PanelChrome.None,
            Background: 0, Rounding: 0, EdgeToEdge: true),
        () => new InventoryFace());

    // ── Layout (px, like the other faces) ───────────────────────────────
    private const float Cell = 38, Icon = 32, RowH = 24, PackRowH = 38, ZoneH = 30, StatusH = 20;
    private const uint AllPacks = 0, WornPacks = 1;     // pseudo pack ids (real ids are never 0 or 1)

    private static readonly uint CellHot = RynthTheme.Argb(0xFF1C3148), StackCol = RynthTheme.Argb(0xFFFFFFFF),
        StackShadow = RynthTheme.Argb(0xE0000000), DropHot = RynthTheme.Argb(0xFF1F5A3A),
        Placeholder = RynthTheme.Argb(0xFF1A2633);
    private static uint CellBg => RynthTheme.RowAlt;
    private static uint CellSel => RynthTheme.Accent;

    private static readonly string[] SortNames = { "Pack order", "Name", "Value", "Burden", "Type", "Stack" };
    private static readonly string[] SortLabels = Array.ConvertAll(SortNames, n => PhosphorIcons.SortAscending + " " + n);
    private static readonly string[] CategoryNames = { "All", "Weapons", "Armor", "Jewelry", "Usable", "Comps", "Salvage", "Misc" };
    private static readonly InventoryCategory[] CategoryOf =
    {
        default, InventoryCategory.Weapons, InventoryCategory.Armor, InventoryCategory.Jewelry, InventoryCategory.Usable,
        InventoryCategory.Components, InventoryCategory.Salvage, InventoryCategory.Misc,
    };

    // ── State ───────────────────────────────────────────────────────────
    private uint _pack = AllPacks;
    private int _sort, _category;
    private readonly byte[] _search = new byte[64];
    private string _searchText = string.Empty;
    private uint _selected;
    private readonly Picker _picker = new("##inv_sort");

    // Filtered, sorted list (rebuilt only when an input changes).
    private readonly List<InventoryItem> _shown = new(256);
    private InventoryView? _builtFor;
    private uint _builtPack = uint.MaxValue;
    private int _builtSort = -1, _builtCategory = -1;
    private string _builtSearch = "\0";
    private bool _builtClassic;
    private int _builtOrderVersion = -1;
    // The shown list follows the server's slot order for this pack (so a cell is a slot).
    private bool _orderKnown;
    private readonly Dictionary<uint, int> _rank = new();
    // Leave pack-slot items (foci) out of this list: the Classic main pack grid (they're in the side column).
    private bool _skipSlotItems;
    private string _countText = string.Empty;

    // Hover and tooltip cache.
    private InventoryItem? _hover, _tipItem;
    private string[] _tipBase = Array.Empty<string>();

    // Drag.
    private InventoryItem? _pressItem, _dragItem;
    private Vector2 _pressAt;
    // Slot: one paperdoll slot (Id = its equip mask, Name = the slot's name).
    // PackSlots: the Classic side column's pack slots (Id = the main pack): only a
    // pack-slot item (a focus) goes there, which moves it to the main pack.
    private enum DropKind : byte { None, Pack, Equip, Drop, Give, Slot, PackSlots }
    private struct Target { public Vector2 Min, Max; public DropKind Kind; public uint Id; public string Name; public int Slot; }
    private readonly List<Target> _targets = new(16);

    // Context menu and split dialog (opened at the end of Draw, outside any child).
    private InventoryItem? _ctxItem;
    private bool _openCtx, _openSplit;
    private InventoryItem? _splitItem;
    private int _splitAmount = 1;
    private uint _splitTarget;
    // "Add to loot profile..." (right-click): RynthAi builds the rule, the popup previews it.
    private readonly LootAddDialog _lootAdd = new("##inv_lootadd");

    // Status line cache.
    private string _statusText = string.Empty;
    private int _statusKey = int.MinValue;
    private string _refusal = string.Empty;
    private long _refusalAt;

    public void OnShown()
    {
        InventorySettingsStore.Load();
        LoadAttrFilter();
        InventoryModel.Subscribe();
    }

    public void OnHidden()
    {
        InventoryModel.Hover(0);
        InventoryModel.Unsubscribe();
        _dragItem = _pressItem = null;
        _pressNarrow = false;
        ItemDragBridge.EndPopOutDrag();
        UiDropTargets.EngineDragObject = 0;
        _lootAdd.Cancel();
    }

    // =====================================================================
    //  Frame
    // =====================================================================

    public void Draw()
    {
        InventoryView view = InventoryModel.Current;
        _targets.Clear();
        _hover = null;

        float w = Begin(out Vector2 origin, out Vector2 size);
        if (view.PlayerId == 0)
        {
            Label("Waiting for your character's inventory...", Mute);
            End(origin, size);
            InventoryModel.Hover(0);
            return;
        }

        _hoverLabel = _hoverLabel2 = null;
        // Text follows the panel's size setting (the - / + in the Classic title bar).
        ImGuiNET.ImGui.SetWindowFontScale(InventorySettingsStore.SizeFactor);
        if (InventorySettingsStore.Classic) ClassicBody(view, origin, size);
        else ModernBody(view, origin, size, w);

        InventoryModel.Hover(_dragItem == null ? _hover?.Id ?? 0 : 0);
        FinishDrag();
        UiDropTargets.EngineDragObject = _dragItem?.Id ?? 0;
        End(origin, size);

        Tooltip();
        DrawDragGhost();
        ContextMenu(view);
        SplitDialog(view);
        ConfirmDialog();
        AttrFilterDialog();
        _lootAdd.Draw();
        _picker.Draw();
    }

    /// <summary>The Phase 1 layout: header, tools, chips, pack column | grid, zones, status.</summary>
    private void ModernBody(InventoryView view, Vector2 origin, Vector2 size, float w)
    {
        if (_pack != AllPacks && _pack != WornPacks && view.PackOf(_pack) == null) _pack = AllPacks;
        ImGuiNET.ImGui.GetWindowDrawList().AddRectFilled(origin, origin + size, RynthTheme.PanelGlass, 6);
        ImGuiNET.ImGui.GetWindowDrawList().AddRect(origin, origin + size, RynthTheme.PanelBorder, 6);
        RebuildIfNeeded(view, _pack);   // the header shows the count
        Header(view, w);
        Tools(w);
        Chips(w);
        RebuildIfNeeded(view, _pack);

        // Body: pack column | grid, then zones and the status row at the bottom.
        float bottomH = ZoneH + StatusH + 8;
        Vector2 bodyPos = ImGuiNET.ImGui.GetCursorScreenPos();
        float bodyH = Math.Max(60, origin.Y + size.Y - 6 - bottomH - bodyPos.Y);
        float colW = w >= 380 ? 128 : 46;
        PackColumn(view, bodyPos, colW, bodyH);
        // The Vault (Aelrynth) takes the grid's place; picking a pack in the column goes back to it.
        if (_vaultOpen && (!VaultFeed.Available || _pack != _vaultOpenedOnPack)) _vaultOpen = false;
        if (_vaultOpen) ModernVault(new Vector2(bodyPos.X + colW + 4, bodyPos.Y), new Vector2(w - colW - 4, bodyH));
        else Grid(new Vector2(bodyPos.X + colW + 4, bodyPos.Y), new Vector2(w - colW - 4, bodyH));
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(bodyPos.X, bodyPos.Y + bodyH + 4));
        Zones(view, w);
        StatusRow(w);
    }

    /// <summary>RynthInventory's Global Inventory script window key (UiWindow "RynthInventory/Main").</summary>
    private const string GlobalInventoryKey = "RynthInventory/Main";
    /// <summary>RynthSort's script window key (UiWindow "RynthSort/Main").</summary>
    private const string RynthSortKey = "RynthSort/Main";

    private static bool MiniRemoteOpen => ImGuiPanelHost.IsOpen(MiniRemoteFace.Title);
    private static bool ItemCountOpen => ImGuiPanelHost.IsOpen(ItemCountFace.Title);
    private static bool RynthSortOpen => ScriptWindowRegistry.IsOpen(RynthSortKey);
    private static bool GlobalInventoryOpen => ScriptWindowRegistry.IsOpen(GlobalInventoryKey);

    private const string MiniRemoteTip = "Mini Remote (/rc miniremote): remote, sections and HUDs";
    private const string ItemCountTip = "Item Count (/rc itemcount): how many of each named item you carry";
    private static string RynthSortTip() => ScriptWindowRegistry.Has(RynthSortKey)
        ? "RynthSort (/rsort): pack sort rules and auto-sort"
        : "RynthSort (/rsort) - needs the RynthSort plugin";
    private static string GlobalInventoryTip() => ScriptWindowRegistry.Has(GlobalInventoryKey)
        ? "Global Inventory (/ginv): every character's saved packs and storage"
        : "Global Inventory (/ginv) - needs the RynthInventory plugin";

    /// <summary>Opens or closes the Item Count panel (same as /rc itemcount).</summary>
    private static void ToggleItemCount()
    {
        if (!ImGuiPanelHost.HasFace(ItemCountFace.Title)) return;
        PanelRouter.Toggle(ItemCountFace.Title);
    }

    /// <summary>Opens or closes the Mini Remote (same as /rc miniremote).</summary>
    private static void ToggleMiniRemote()
    {
        if (!ImGuiPanelHost.HasFace(MiniRemoteFace.Title)) return;
        PanelRouter.Toggle(MiniRemoteFace.Title);
    }

    /// <summary>
    /// Opens or closes a plugin script window by key, or runs <paramref name="fallbackCmd"/>
    /// when the plugin hasn't submitted it yet.
    /// </summary>
    private static void ToggleScriptWindow(string key, string fallbackCmd)
    {
        if (ScriptWindowRegistry.Has(key))
        {
            ScriptWindowRegistry.PlayerSetVisible(key, !ScriptWindowRegistry.IsOpen(key));
            return;
        }
        ServerMenus.Run(fallbackCmd);
    }

    /// <summary>Opens or closes RynthInventory's Global Inventory (same as /ginv).</summary>
    private static void ToggleGlobalInventory() => ToggleScriptWindow(GlobalInventoryKey, "/ginv");

    /// <summary>Opens or closes RynthSort (same as /rsort).</summary>
    private static void ToggleRynthSort() => ToggleScriptWindow(RynthSortKey, "/rsort");

    // The "more" menu both looks use when their bar is too short for the four buttons.
    private bool _openMore;

    /// <summary>The "more" popup: the four related panels as menu lines (checked while open). Draw where its button was.</summary>
    private void RelatedMenu()
    {
        if (_openMore)
        {
            _openMore = false;
            ImGuiNET.ImGui.OpenPopup("##inv_more_menu");
        }
        PushMenuStyle();
        if (ImGuiNET.ImGui.BeginPopup("##inv_more_menu"))
        {
            if (ImGuiNET.ImGui.MenuItem("Mini Remote", "/rc miniremote", MiniRemoteOpen, ImGuiPanelHost.HasFace(MiniRemoteFace.Title)))
                ToggleMiniRemote();
            SafeImGui.SetItemTooltip(MiniRemoteTip);
            if (ImGuiNET.ImGui.MenuItem("RynthSort", "/rsort", RynthSortOpen))
                ToggleRynthSort();
            SafeImGui.SetItemTooltip(RynthSortTip());
            if (ImGuiNET.ImGui.MenuItem("Item Count", "/rc itemcount", ItemCountOpen, ImGuiPanelHost.HasFace(ItemCountFace.Title)))
                ToggleItemCount();
            SafeImGui.SetItemTooltip(ItemCountTip);
            if (ImGuiNET.ImGui.MenuItem("Global Inventory", "/ginv", GlobalInventoryOpen))
                ToggleGlobalInventory();
            SafeImGui.SetItemTooltip(GlobalInventoryTip());
            ImGuiNET.ImGui.EndPopup();
        }
        PopMenuStyle();
    }

    // ── Header: burden, pyreals, count ──────────────────────────────────

    private void Header(InventoryView view, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImFontPtr icons = ImGuiFonts.Get(UiFont.Ui14);
        float ty = p.Y + (RowH - ImGuiNET.ImGui.GetFontSize()) * 0.5f;

        PhosphorIcons.DrawCentered(dl, icons, PhosphorIcons.Scales, p, new Vector2(18, RowH), Amber);
        float x = p.X + 20;
        dl.AddText(new Vector2(x, ty), BurdenColour(view), view.BurdenText);
        float bw = ImGuiNET.ImGui.CalcTextSize(view.BurdenText).X;
        ImGuiNET.ImGui.SetCursorScreenPos(p);
        ImGuiNET.ImGui.InvisibleButton("##burden", new Vector2(x + bw - p.X, RowH));
        SafeImGui.SetItemTooltip(view.BurdenTip);

        x += bw + 16;
        PhosphorIcons.DrawCentered(dl, icons, PhosphorIcons.Coins, new Vector2(x, p.Y), new Vector2(18, RowH), Amber);
        dl.AddText(new Vector2(x + 20, ty), Text, view.PyrealText);

        // Close (the engine's header is off for this panel).
        if (IconButton("##inv_close", PhosphorIcons.X, new Vector2(p.X + w - 22, p.Y + 1), new Vector2(22, 22), Amber, BtnFill, font: UiFont.Ui11))
            ImGuiPanelHost.Close(Title);
        ImGuiNET.ImGui.SetItemTooltip("Close");

        // Switch to the Classic (retail) look.
        var tb = new Vector2(p.X + w - 46, p.Y + 1);
        if (IconButton("##inv_classic", PhosphorIcons.AppWindow, tb, new Vector2(22, 22), Amber, BtnFill, font: UiFont.Ui11))
            InventorySettingsStore.SetClassic(true);
        ImGuiNET.ImGui.SetItemTooltip("Classic look (the retail inventory window)");

        // Related panels (Mini Remote, RynthSort, Item Count, Global Inventory): four icons when the room
        // left of the Classic / Close buttons holds them, else one "more" menu, else none (the Character
        // menu has them all). The item count text gives way last. On Aelrynth the Vault button sits next
        // to the Classic button and takes its room first: it is never dropped
        // (UI/InventoryTitleLayout.InventoryHeaderLayout, tested in tools/BarDeclutterTests).
        float cw = ImGuiNET.ImGui.CalcTextSize(_countText).X;
        float leftEnd = x + 20 + ImGuiNET.ImGui.CalcTextSize(view.PyrealText).X + 6;
        InventoryHeaderLayout hl = InventoryHeaderLayout.Compute(w, leftEnd - p.X, cw, VaultFeed.Available);
        Vector2 At(TitleSlot s) => new(p.X + s.X, p.Y + 1);
        var btn22 = new Vector2(InventoryHeaderLayout.Btn, InventoryHeaderLayout.Btn);
        if (hl.Plan.Extras == TitleExtrasMode.Icons)
        {
            if (IconButton("##inv_ginv", PhosphorIcons.GlobeHemisphereWest, At(hl.GInv), btn22,
                    GlobalInventoryOpen ? Teal : Amber, BtnFill, font: UiFont.Ui11))
                ToggleGlobalInventory();
            SafeImGui.SetItemTooltip(GlobalInventoryTip());
            if (IconButton("##inv_itemcount", PhosphorIcons.Bag, At(hl.Count), btn22,
                    ItemCountOpen ? Teal : Amber, BtnFill, font: UiFont.Ui11))
                ToggleItemCount();
            SafeImGui.SetItemTooltip(ItemCountTip);
            if (IconButton("##inv_rsort", PhosphorIcons.SortAscending, At(hl.Sort), btn22,
                    RynthSortOpen ? Teal : Amber, BtnFill, font: UiFont.Ui11))
                ToggleRynthSort();
            SafeImGui.SetItemTooltip(RynthSortTip());
            if (IconButton("##inv_miniremote", PhosphorIcons.SquaresFour, At(hl.Remote), btn22,
                    MiniRemoteOpen ? Teal : Amber, BtnFill, font: UiFont.Ui11))
                ToggleMiniRemote();
            SafeImGui.SetItemTooltip(MiniRemoteTip);
        }
        else if (hl.Plan.Extras == TitleExtrasMode.Menu)
        {
            if (IconButton("##inv_more", PhosphorIcons.DotsThree, At(hl.More), btn22,
                    MiniRemoteOpen || RynthSortOpen || ItemCountOpen || GlobalInventoryOpen ? Teal : Amber, BtnFill, font: UiFont.Ui11))
                _openMore = true;
            ImGuiNET.ImGui.SetItemTooltip("Mini Remote, RynthSort, Item Count, Global Inventory");
        }
        RelatedMenu();

        // Vault (Aelrynth only): the grid shows the account vault instead of the packs.
        if (hl.Plan.Vault)
        {
            if (IconButton("##inv_vault_m", PhosphorIcons.TreasureChest, At(hl.Vault), btn22,
                    _vaultOpen ? Teal : Amber, BtnFill, font: UiFont.Ui11))
            {
                _vaultOpen = !_vaultOpen;
                _vaultOpenedOnPack = _pack;
                if (_vaultOpen) VaultFeed.ViewOpened();
            }
            ImGuiNET.ImGui.SetItemTooltip("Your Aelrynth account vault");
        }

        if (hl.Plan.ShowCount) dl.AddText(new Vector2(p.X + hl.CountX, ty), Mute, _countText);
        float countLeft = hl.Plan.ShowCount ? p.X + hl.CountX : p.X + hl.RightEdge;

        // Drag surface: the empty part of the header, between the pyreals and the count.
        float dragFrom = leftEnd, dragTo = countLeft - 4;
        if (dragTo > dragFrom)
        {
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(dragFrom, p.Y));
            ImGuiNET.ImGui.InvisibleButton("##inv_drag", new Vector2(dragTo - dragFrom, RowH));
            ImGuiPanelHost.DragWindowWithLastItem();
        }
        NextLine(p, RowH + 2);
    }

    private static uint BurdenColour(InventoryView view)
    {
        if (view.BurdenCapacity <= 0 || view.Burden < 0) return Text;
        return view.Burden > view.BurdenCapacity ? Red : view.Burden * 10 > view.BurdenCapacity * 9 ? Amber : Green;
    }

    // ── Search, sort, categories ────────────────────────────────────────

    private void Tools(float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float sortW = 118;
        float boxW = Math.Max(80, w - sortW - 26 - 26 - 8);
        if (TextBox("##inv_search", _search, p, boxW, PhosphorIcons.MagnifyingGlass + " Search names", out _))
            _searchText = Utf8(_search).Trim().ToLowerInvariant();
        if (IconButton("##inv_clear", PhosphorIcons.X, new Vector2(p.X + boxW + 2, p.Y), new Vector2(22, 22), Mute, BtnFill,
                enabled: _search[0] != 0, font: UiFont.Ui11))
        {
            Array.Clear(_search);
            _searchText = string.Empty;
        }
        ImGuiNET.ImGui.SetItemTooltip("Clear the search");
        AttrFilterButton(new Vector2(p.X + boxW + 26, p.Y));
        Vector2 sp = new(p.X + w - sortW, p.Y);
        if (Button("##inv_sortbtn", SortLabels[_sort], sp, new Vector2(sortW, 22), Text, BtnFill,
                leftAlign: true))
            _picker.Open(new Vector2(sp.X, sp.Y + 24), SortNames, _sort, i => _sort = i, sortW);
        ImGuiNET.ImGui.SetItemTooltip("Sort the items");
        NextLine(p, 26);
    }

    private void Chips(float w)
    {
        Vector2 start = ImGuiNET.ImGui.GetCursorScreenPos();
        float x = start.X, y = start.Y;
        ImFontPtr f = ImGuiFonts.Get(UiFont.Ui9);
        const float h = 18;
        for (int i = 0; i < CategoryNames.Length; i++)
        {
            string label = CategoryNames[i];
            float cw = CalcWidth(f, label) + 12;
            if (x > start.X && x + cw > start.X + w) { x = start.X; y += h + 3; }
            var pos = new Vector2(x, y);
            ImGuiNET.ImGui.SetCursorScreenPos(pos);
            ImGuiNET.ImGui.PushID(i);
            if (ImGuiNET.ImGui.InvisibleButton("##chip", new Vector2(cw, h))) _category = i;
            bool hot = ImGuiNET.ImGui.IsItemHovered();
            ImGuiNET.ImGui.PopID();
            bool on = _category == i;
            var dl = ImGuiNET.ImGui.GetWindowDrawList();
            dl.AddRectFilled(pos, pos + new Vector2(cw, h), on ? Selected : hot ? Lighten(BtnFill) : BtnFill, 9);
            dl.AddRect(pos, pos + new Vector2(cw, h), on ? Teal : BtnBord, 9);
            dl.AddText(f, f.FontSize * InventorySettingsStore.SizeFactor, pos + new Vector2(6, (h - f.FontSize) * 0.5f), on ? Teal : Text, label);
            x += cw + 4;
        }
        NextLine(start, y - start.Y + h + 4);
    }

    private static float CalcWidth(ImFontPtr font, string text)
    {
        ImGuiNET.ImGui.PushFont(font);
        float w = ImGuiNET.ImGui.CalcTextSize(text).X;
        ImGuiNET.ImGui.PopFont();
        return w;
    }

    // ── Filtered list ───────────────────────────────────────────────────

    /// <summary>
    /// The filtered, sorted list for <paramref name="pack"/> (a real pack id, AllPacks or
    /// WornPacks; the caller makes sure a real id is in the view). Rebuilt only on change.
    /// </summary>
    private void RebuildIfNeeded(InventoryView view, uint pack)
    {
        bool classic = InventorySettingsStore.Classic;
        bool attrOn = AttrOn;
        int attrKey = attrOn ? _attr.Version : -1;
        if (ReferenceEquals(view, _builtFor) && _builtPack == pack && _builtSort == _sort
            && _builtCategory == _category && ReferenceEquals(_builtSearch, _searchText) && _builtClassic == classic
            && _builtOrderVersion == Net.InventoryOrder.Version && _builtAttrKey == attrKey && !AttrRefreshDue(attrOn))
            return;
        if (_builtPack != pack || !ReferenceEquals(_builtSearch, _searchText) || _builtCategory != _category || _builtAttrKey != attrKey)
            _gridFirstRow = 0;   // another pack or filter: back to the top (Classic grid)
        _builtAttrKey = attrKey;
        BeginAttrPass(attrOn);
        if (!ReferenceEquals(view, _builtFor)) PruneSelection(view);
        _builtFor = view;
        _builtPack = pack;
        _builtSort = _sort;
        _builtCategory = _category;
        _builtSearch = _searchText;
        _builtClassic = classic;
        _builtOrderVersion = Net.InventoryOrder.Version;

        _shown.Clear();
        InventoryPack? one = pack == WornPacks || pack == AllPacks ? null : view.PackOf(pack);
        // Classic, the main pack open: its pack-slot items (foci) are in the side column, not
        // the grid (retail). A search or filter (all packs) and the Modern look list them.
        _skipSlotItems = classic && one != null && one.IsMain;
        if (pack == WornPacks) AddFiltered(view.Worn);
        else if (one == null) foreach (InventoryPack p in view.Packs) AddFiltered(p.Items);
        else AddFiltered(one.Items);

        Comparison<InventoryItem>? cmp = _sort switch
        {
            1 => ByName,
            2 => static (a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : ByName(a, b),
            3 => static (a, b) => b.Burden != a.Burden ? b.Burden.CompareTo(a.Burden) : ByName(a, b),
            4 => static (a, b) => a.Category != b.Category ? a.Category.CompareTo(b.Category) : ByName(a, b),
            5 => static (a, b) => b.Stack != a.Stack ? b.Stack.CompareTo(a.Stack) : ByName(a, b),
            _ => null,   // pack order: as captured (packs in order, ascending id inside each)
        };
        if (cmp != null) _shown.Sort(cmp);
        else ApplyServerOrder(one);

        int total = pack == WornPacks ? view.Worn.Length : one == null ? view.ItemCount
            : _skipSlotItems ? one.Used : one.Items.Length;
        _countText = _shown.Count == total
            ? total.ToString(CultureInfo.InvariantCulture) + " items"
            : _shown.Count.ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture);
        if (_unidentified.Count > 0)
            _countText += ", " + _unidentified.Count.ToString(CultureInfo.InvariantCulture) + " not ID'd";
        _selectAllLabel = "Select all shown (" + _shown.Count.ToString(CultureInfo.InvariantCulture) + ")";
        // The Classic grid's caption, retail's "Contents of <pack>".
        _captionText = pack == WornPacks ? "Worn"
            : one == null ? (_shown.Count == total ? "All packs" : "Matches in all packs: " + _countText)
            : one.IsMain ? "Contents of Backpack"
            : "Contents of " + one.Name;
    }


    /// <summary>
    /// Pack order = the server's: the list ACE sent for this pack (Net/InventoryOrder), so cell k is
    /// slot k. A pack whose list was never seen (a side pack not opened yet, or an engine that
    /// reloaded after login) stays in id order and <see cref="_orderKnown"/> says so: drops there
    /// take the first empty slot, as before.
    /// </summary>
    private void ApplyServerOrder(InventoryPack? pack)
    {
        _orderKnown = false;
        if (pack == null || !Net.InventoryOrder.TryGetOrder(pack.Id, false, out uint[] items)) return;
        Net.InventoryOrder.TryGetOrder(pack.Id, true, out uint[] slots);
        _rank.Clear();
        for (int i = 0; i < items.Length; i++) _rank[items[i]] = i;
        for (int i = 0; i < slots.Length; i++) _rank[slots[i]] = 1_000_000 + i;
        _shown.Sort((a, b) =>
        {
            int ra = _rank.TryGetValue(a.Id, out int x) ? x : int.MaxValue, rb = _rank.TryGetValue(b.Id, out int y) ? y : int.MaxValue;
            return ra != rb ? ra.CompareTo(rb) : a.Id.CompareTo(b.Id);
        });
        _orderKnown = true;
    }

    /// <summary>A grid cell is a slot only while the list is the server's, whole and in pack order, and the item is an ordinary one.</summary>
    private bool CellsAreSlots(InventoryItem item) =>
        _orderKnown && _sort == 0 && !FiltersOn && !item.RequiresPackSlot && !item.IsPack;

    /// <summary>Some filter hides items: a category, search text or the attribute filter.</summary>
    private bool FiltersOn => _category != 0 || _searchText.Length > 0 || AttrOn;

    /// <summary>The slot an item holds now among its pack's ordinary items, or -1 when unknown.</summary>
    private static int SlotOf(InventoryItem item)
    {
        if (!Net.InventoryOrder.TryGetOrder(item.Container, false, out uint[] ids)) return -1;
        return Array.IndexOf(ids, item.Id);
    }

    private static int ByName(InventoryItem a, InventoryItem b)
    {
        int c = string.CompareOrdinal(a.SearchName, b.SearchName);
        return c != 0 ? c : a.Id.CompareTo(b.Id);
    }

    private void AddFiltered(InventoryItem[] items)
    {
        InventoryCategory cat = CategoryOf[_category];
        foreach (InventoryItem it in items)
        {
            if (_skipSlotItems && it.RequiresPackSlot) continue;
            if (_category != 0 && it.Category != cat) continue;
            if (_searchText.Length > 0 && !it.SearchName.Contains(_searchText, StringComparison.Ordinal)) continue;
            if (_attrPass && !AttrKeeps(it)) continue;
            _shown.Add(it);
        }
    }

    // ── Pack column ─────────────────────────────────────────────────────

    private void PackColumn(InventoryView view, Vector2 pos, float colW, float h)
    {
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBg);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2, 2));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 2));
        ImGuiNET.ImGui.BeginChild("##inv_packs", new Vector2(colW, h), ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGuiNET.ImGui.PopStyleVar(2);
        float iw = ImGuiNET.ImGui.GetContentRegionAvail().X;
        bool wide = colW > 60;

        PackRow("##p_all", AllPacks, PhosphorIcons.SquaresFour, 0, "All packs", view.ItemCountText, iw, wide, DropKind.None, "");
        for (int i = 0; i < view.Packs.Length; i++)
        {
            InventoryPack p = view.Packs[i];
            ImGuiNET.ImGui.PushID(i);
            PackRow("##p", p.Id, p.IsMain ? PhosphorIcons.Backpack : string.Empty, p.IsMain ? 0 : p.Id, p.Name, p.FillText,
                iw, wide, DropKind.Pack, p.Name);
            ImGuiNET.ImGui.PopID();
        }
        PackRow("##p_worn", WornPacks, PhosphorIcons.Shield, 0, "Worn", view.WornCountText, iw, wide, DropKind.Equip, "Worn");

        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopStyleColor();
    }

    /// <summary>One pack entry: click shows it; it is a drop target (move into it, or equip for Worn).</summary>
    private void PackRow(string id, uint packId, string glyph, uint iconObject, string name, string fill, float w, bool wide,
        DropKind drop, string dropName)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var sz = new Vector2(w, PackRowH);
        if (ImGuiNET.ImGui.InvisibleButton(id, sz)) _pack = packId;
        bool hot = ImGuiNET.ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        if (!wide && hot && _dragItem == null && ImGuiNET.ImGui.BeginTooltip())
        {
            ImGuiNET.ImGui.TextUnformatted(name);
            ImGuiNET.ImGui.TextUnformatted(fill);
            ImGuiNET.ImGui.EndTooltip();
        }
        bool on = _pack == packId;
        bool dropHot = _dragItem != null && drop != DropKind.None && hot;

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + sz, dropHot ? DropHot : on ? Selected : hot ? CellHot : BtnFill, 3);
        dl.AddRect(p, p + sz, on || dropHot ? Teal : BtnBord, 3);
        var iconMin = new Vector2(p.X + 3, p.Y + (PackRowH - Icon) * 0.5f);
        if (iconObject != 0) DrawItemIcon(dl, iconObject, iconMin, Icon);
        else PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui14), glyph, iconMin, new Vector2(Icon, Icon), on ? Teal : Mute);
        if (wide)
        {
            float tx = p.X + Icon + 8;
            dl.PushClipRect(new Vector2(tx, p.Y), p + sz, true);
            dl.AddText(new Vector2(tx, p.Y + 3), on ? Teal : Text, name);
            ImFontPtr f9 = ImGuiFonts.Get(UiFont.Ui9);
            dl.AddText(f9, f9.FontSize * InventorySettingsStore.SizeFactor, new Vector2(tx, p.Y + PackRowH - f9.FontSize - 4), Mute, fill);
            dl.PopClipRect();
        }
        if (drop != DropKind.None)
        {
            // Only the part inside the (scrolling) pack column counts: a row scrolled out of
            // view must not catch a drop over the search box or the zones.
            Vector2 wmin = ImGuiNET.ImGui.GetWindowPos(), wmax = wmin + ImGuiNET.ImGui.GetWindowSize();
            Vector2 min = Vector2.Max(p, wmin), max = Vector2.Min(p + sz, wmax);
            if (max.X > min.X && max.Y > min.Y)
                _targets.Add(new Target { Min = min, Max = max, Kind = drop, Id = packId, Name = dropName });
        }
    }

    // ── Grid ────────────────────────────────────────────────────────────

    private void Grid(Vector2 pos, Vector2 size)
    {
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBg);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(3, 3));
        ImGuiNET.ImGui.BeginChild("##inv_grid", size, ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGuiNET.ImGui.PopStyleVar();

        // Showing one pack: dropping on the grid moves the item into that pack.
        if (_pack != AllPacks && _pack != WornPacks && _builtFor?.PackOf(_pack) is InventoryPack shownPack)
        {
            Vector2 wmin = ImGuiNET.ImGui.GetWindowPos();
            _targets.Add(new Target { Min = wmin, Max = wmin + ImGuiNET.ImGui.GetWindowSize(), Kind = DropKind.Pack,
                Id = shownPack.Id, Name = shownPack.Name });
        }

        float avail = ImGuiNET.ImGui.GetContentRegionAvail().X;
        int cols = Math.Max(1, (int)(avail / Cell));
        int rows = (_shown.Count + cols - 1) / cols;
        Vector2 o = ImGuiNET.ImGui.GetCursorScreenPos();
        var content = new Vector2(cols * Cell, Math.Max(rows * Cell, 1));
        var dl = ImGuiNET.ImGui.GetWindowDrawList();

        if (_shown.Count == 0)
        {
            dl.AddText(o + new Vector2(4, 4), Mute, FiltersOn ? "Nothing matches." : "Empty.");
            ImGuiNET.ImGui.Dummy(new Vector2(avail, 20));
        }
        else
        {
            // One button over the whole grid: hover, click, double-click, right-click and
            // drag are worked out from the mouse position (no item per cell).
            ImGuiNET.ImGui.InvisibleButton("##cells", content, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
            bool hovered = ImGuiNET.ImGui.IsItemHovered();
            Vector2 mouse = ImGuiNET.ImGui.GetMousePos();
            int hoverIdx = -1;
            if (hovered)
            {
                int c = (int)((mouse.X - o.X) / Cell), r = (int)((mouse.Y - o.Y) / Cell);
                if (c >= 0 && c < cols && r >= 0 && r < rows)
                {
                    int i = r * cols + c;
                    if (i < _shown.Count) hoverIdx = i;
                }
            }
            InventoryItem? hot = hoverIdx >= 0 ? _shown[hoverIdx] : null;
            _hover = hot;

            if (hot != null && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                ClickItem(hot);
                _pressItem = hot;
                _pressAt = mouse;
            }
            if (hot != null && ImGuiNET.ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                _pressItem = null;
                Act(InventoryActionKind.Use, hot, 0, "", 0);
            }
            if (hot != null && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                RightClickItem(hot);
            if (_pressItem != null && _dragItem == null && ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left)
                && Vector2.DistanceSquared(mouse, _pressAt) > 25)
                _dragItem = _pressItem;

            // Visible rows only.
            Vector2 clipMin = ImGuiNET.ImGui.GetWindowPos(), clipMax = clipMin + ImGuiNET.ImGui.GetWindowSize();
            int firstRow = Math.Max(0, (int)((clipMin.Y - o.Y) / Cell));
            int lastRow = Math.Min(rows - 1, (int)((clipMax.Y - o.Y) / Cell));
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));   // stack counts
            for (int r = firstRow; r <= lastRow; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    if (i >= _shown.Count) break;
                    InventoryItem it = _shown[i];
                    var cmin = new Vector2(o.X + c * Cell, o.Y + r * Cell);
                    var cmax = cmin + new Vector2(Cell - 2, Cell - 2);
                    bool sel = IsPicked(it);
                    dl.AddRectFilled(cmin, cmax, i == hoverIdx ? CellHot : CellBg, 3);
                    DrawItemIcon(dl, it.Id, cmin + new Vector2(2, 2), Icon, ReferenceEquals(it, _dragItem) ? 0x60FFFFFFu : 0xFFFFFFFFu);
                    if (it.StackText.Length > 0)
                    {
                        Vector2 ts = ImGuiNET.ImGui.CalcTextSize(it.StackText);
                        var tp = new Vector2(cmax.X - ts.X - 2, cmax.Y - ts.Y - 1);
                        dl.AddText(tp + Vector2.One, StackShadow, it.StackText);
                        dl.AddText(tp, StackCol, it.StackText);
                    }
                    if (sel) dl.AddRect(cmin, cmax, CellSel, 3, ImDrawFlags.None, 1.5f);
                    else if (it.Wielder != 0) dl.AddRect(cmin, cmax, Amber, 3);
                }
            }
            ImGuiNET.ImGui.PopFont();
        }

        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopStyleColor();
    }

    // The pack showing when the Modern Vault opened (picking another pack closes the Vault).
    private uint _vaultOpenedOnPack;

    /// <summary>
    /// The Modern look's Vault: the account vault's items in the grid's style, a caption with how full
    /// it is and Refresh. Read-only apart from right-click > Withdraw (VaultContextMenu).
    /// </summary>
    private void ModernVault(Vector2 pos, Vector2 size)
    {
        VaultSnapshot? snap = VaultFeed.Snapshot;
        VaultItem[] items = snap?.Items ?? Array.Empty<VaultItem>();
        var pdl = ImGuiNET.ImGui.GetWindowDrawList();
        const float CapH = 22;
        float ty = pos.Y + (CapH - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
        pdl.AddText(new Vector2(pos.X + 4, ty), Text, VaultCaption(snap));
        if (IconButton("##vault_refresh_m", PhosphorIcons.ArrowsClockwise, new Vector2(pos.X + size.X - 22, pos.Y), new Vector2(22, 20),
                VaultFeed.Awaiting ? Teal : Amber, BtnFill, font: UiFont.Ui11))
            VaultFeed.RequestRefresh();
        ImGuiNET.ImGui.SetItemTooltip("Read the vault again");

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + CapH));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBg);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(3, 3));
        ImGuiNET.ImGui.BeginChild("##inv_vault_grid", new Vector2(size.X, Math.Max(30, size.Y - CapH)), ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGuiNET.ImGui.PopStyleVar();

        float avail = ImGuiNET.ImGui.GetContentRegionAvail().X;
        int cols = Math.Max(1, (int)(avail / Cell));
        int rows = (items.Length + cols - 1) / cols;
        Vector2 o = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        if (items.Length == 0)
        {
            string empty = snap == null ? (VaultFeed.Awaiting ? "Reading the vault..." : VaultEmptyText) : "The vault is empty.";
            dl.AddText(o + new Vector2(4, 4), Mute, empty);
            ImGuiNET.ImGui.Dummy(new Vector2(avail, 20));
        }
        else
        {
            var content = new Vector2(cols * Cell, Math.Max(rows * Cell, 1));
            ImGuiNET.ImGui.InvisibleButton("##vault_cells_m", content, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
            bool hovered = ImGuiNET.ImGui.IsItemHovered();
            Vector2 mouse = ImGuiNET.ImGui.GetMousePos();
            int hoverIdx = -1;
            if (hovered)
            {
                int c = (int)((mouse.X - o.X) / Cell), r = (int)((mouse.Y - o.Y) / Cell);
                if (c >= 0 && c < cols && r >= 0 && r < rows && r * cols + c < items.Length) hoverIdx = r * cols + c;
            }

            // Visible rows only.
            Vector2 clipMin = ImGuiNET.ImGui.GetWindowPos(), clipMax = clipMin + ImGuiNET.ImGui.GetWindowSize();
            int firstRow = Math.Max(0, (int)((clipMin.Y - o.Y) / Cell));
            int lastRow = Math.Min(rows - 1, (int)((clipMax.Y - o.Y) / Cell));
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));   // stack counts
            for (int r = firstRow; r <= lastRow; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    if (i >= items.Length) break;
                    VaultItem it = items[i];
                    var cmin = new Vector2(o.X + c * Cell, o.Y + r * Cell);
                    var cmax = cmin + new Vector2(Cell - 2, Cell - 2);
                    dl.AddRectFilled(cmin, cmax, i == hoverIdx ? CellHot : CellBg, 3);
                    Vector2 imin = cmin + new Vector2(2, 2);
                    if (ScriptIcons.TryGetItem(it.Icon, it.Underlay, it.Overlay, it.Effects, it.ItemType, out IntPtr tex))
                    {
                        IconSampling.Begin(dl);
                        dl.AddImage(tex, imin, imin + new Vector2(Icon, Icon));
                        IconSampling.End(dl);
                    }
                    else
                        dl.AddRectFilled(imin, imin + new Vector2(Icon, Icon), Placeholder, 2);
                    if (it.Stack > 1)
                    {
                        string st = it.Stack.ToString(CultureInfo.InvariantCulture);
                        Vector2 ts = ImGuiNET.ImGui.CalcTextSize(st);
                        var tp = new Vector2(cmax.X - ts.X - 2, cmax.Y - ts.Y - 1);
                        dl.AddText(tp + Vector2.One, StackShadow, st);
                        dl.AddText(tp, StackCol, st);
                    }
                }
            }
            ImGuiNET.ImGui.PopFont();

            if (hoverIdx >= 0)
            {
                VaultItem hot = items[hoverIdx];
                SafeImGui.SetTooltip(hot.Stack > 1 ? $"{hot.Name} ({hot.Stack:N0})" : hot.Name);
                if (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                {
                    _vaultCtx = hot;
                    _openVaultCtx = true;
                }
            }
        }

        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopStyleColor();
        VaultContextMenu();   // in the panel's window, outside the child
    }

    /// <summary>An item's icon (with underlay and overlay) from the shared icon cache, or a placeholder.</summary>
    private static void DrawItemIcon(ImDrawListPtr dl, uint objectId, Vector2 min, float size, uint tint = 0xFFFFFFFF)
    {
        if (ScriptIcons.TryGet(ScriptIconKind.Object, objectId, out IntPtr tex))
        {
            IconSampling.Begin(dl);   // retail draws icons crisp
            dl.AddImage(tex, min, min + new Vector2(size, size), Vector2.Zero, Vector2.One, tint);
            IconSampling.End(dl);
        }
        else
            dl.AddRectFilled(min, min + new Vector2(size, size), Placeholder, 2);
    }

    // ── Zones: equip, drop, give ────────────────────────────────────────

    private void Zones(InventoryView view, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        InventoryTarget? target = InventoryModel.Target;
        InventoryItem? sel = SelectedItem(view);
        RefreshSelectionLabels(target);
        float zw = (w - 8) / 3f;
        var sz = new Vector2(zw, ZoneH);

        Zone("##z_equip", DropKind.Equip, PhosphorIcons.Shield, "Equip", new Vector2(p.X, p.Y), sz, 0, "",
            InventoryModel.WieldAvailable, sel,
            "Equip: drag an item here, or click to equip the selected item.");
        Zone("##z_drop", DropKind.Drop, PhosphorIcons.ArrowFatDown, _dropLabel, new Vector2(p.X + zw + 4, p.Y), sz, 0, "",
            InventoryModel.DropAvailable, sel, DropTip);
        Zone("##z_give", DropKind.Give, PhosphorIcons.Gift, _giveLabel, new Vector2(p.X + 2 * (zw + 4), p.Y), sz,
            target?.Id ?? 0, target?.Name ?? "", InventoryModel.GiveAvailable && target != null, sel,
            target?.ZoneTip ?? "Select a creature, NPC or player in the game to give to.");
        NextLine(p, ZoneH + 4);
    }

    private const string DropTip = "Drop on the ground: drag an item here, or click to drop the selected item(s).\n"
        + "Ctrl+click or Shift+click items to drop several.";

    private void Zone(string id, DropKind kind, string glyph, string label, Vector2 pos, Vector2 size, uint targetId,
        string targetName, bool enabled, InventoryItem? selected, string tip)
    {
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, size);
        bool hot = ImGuiNET.ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        SafeImGui.SetItemTooltip(tip);
        bool dropHot = enabled && _dragItem != null && hot;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        uint fill = dropHot ? DropHot : _dragItem != null && enabled ? Selected : hot && enabled ? Lighten(BtnFill) : BtnFill;
        dl.AddRectFilled(pos, pos + size, fill, 3);
        dl.AddRect(pos, pos + size, dropHot || (_dragItem != null && enabled) ? Teal : BtnBord, 3);
        uint fg = enabled ? Text : Faded(Mute);
        PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui14), glyph, pos + new Vector2(4, 0), new Vector2(18, size.Y), fg);
        dl.PushClipRect(pos, pos + size, true);
        dl.AddText(new Vector2(pos.X + 26, pos.Y + (size.Y - ImGuiNET.ImGui.GetFontSize()) * 0.5f), fg, label);
        dl.PopClipRect();
        if (enabled)
            _targets.Add(new Target { Min = pos, Max = pos + size, Kind = kind, Id = targetId, Name = targetName });
        // Refused with a message while one waits. Equip is always the one selected item.
        if (clicked && enabled && kind != DropKind.Equip && _multi.IsMulti)
            DropOnSelection(kind, targetId, targetName, null);
        else if (clicked && enabled && selected != null)
            DropOn(kind, targetId, targetName, selected);
    }

    private InventoryItem? SelectedItem(InventoryView view) => _selected == 0 ? null : FindAny(view, _selected);

    // ── Status row ──────────────────────────────────────────────────────

    private void StatusRow(float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        StatusLine(p, w, StatusH, null);
        NextLine(p, StatusH);
    }

    /// <summary>
    /// The one action waiting for the server, a refusal, or how the last action ended; when
    /// there is nothing to say, the pyreals (<paramref name="idleView"/>, Classic) or a hint.
    /// </summary>
    private void StatusLine(Vector2 p, float w, float statusH, InventoryView? idleView)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        long now = Stopwatch.GetTimestamp();
        InventoryPending? pending = InventoryModel.Pending;
        InventoryResult? result = InventoryModel.LastResult;
        float ty = p.Y + (statusH - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
        // A several-item run: its progress and Stop (a button, so outside the clip below).
        if (BatchStatus(dl, p, w, statusH, ty)) return;
        dl.PushClipRect(p, p + new Vector2(w, statusH), true);
        if (pending != null)
        {
            int tenths = (int)((now - pending.StartedTicks) * 10 / Stopwatch.Frequency);
            int key = (int)(pending.ItemId * 31) ^ tenths ^ ((int)pending.Kind << 24)
                ^ (pending.WaitingForGame ? (0x40000000 ^ pending.BusyWith.GetHashCode()) : 0);
            if (key != _statusKey)
            {
                _statusKey = key;
                string secs = "  " + (tenths / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";
                // Held until the client's own item gate opens (the bot or another action has it).
                _statusText = pending.WaitingForGame
                    ? "Waiting for the game: " + pending.Label + " (busy with " + pending.BusyWith + ")" + secs
                    : "Waiting for the server: " + pending.Label + secs;
            }
            PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), PhosphorIcons.HourglassMedium, p, new Vector2(16, statusH), Amber);
            dl.AddText(new Vector2(p.X + 18, ty), Amber, _statusText);
        }
        else if (_refusal.Length > 0 && (now - _refusalAt) < Stopwatch.Frequency * 4)
        {
            PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), PhosphorIcons.Warning, p, new Vector2(16, statusH), Amber);
            dl.AddText(new Vector2(p.X + 18, ty), Amber, _refusal);
        }
        else if (result != null && (now - result.AtTicks) < Stopwatch.Frequency * 8)
        {
            PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), result.Ok ? PhosphorIcons.Check : PhosphorIcons.Warning,
                p, new Vector2(16, statusH), result.Ok ? Green : Red);
            dl.AddText(new Vector2(p.X + 18, ty), result.Ok ? Green : Red, result.Text);
        }
        else if (_selHint.Length > 0)
        {
            PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), PhosphorIcons.CheckSquare, p, new Vector2(16, statusH), Teal);
            dl.AddText(new Vector2(p.X + 18, ty), Text, _selHint);
        }
        else if (idleView != null)
        {
            PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), PhosphorIcons.Coins, p, new Vector2(16, statusH), Amber);
            dl.AddText(new Vector2(p.X + 18, ty), Text, idleView.PyrealText);
            float pw = ImGuiNET.ImGui.CalcTextSize(idleView.PyrealText).X;
            dl.AddText(new Vector2(p.X + 22 + pw, ty), Mute, "pyreals");
        }
        else
        {
            dl.AddText(new Vector2(p.X + 2, ty), Faded(Mute), "Double-click to use. Drag to a pack, Equip, Drop or Give. Right-click for more.");
        }
        dl.PopClipRect();
    }

    // ── Actions ─────────────────────────────────────────────────────────

    private void Act(InventoryActionKind kind, InventoryItem item, uint target, string targetName, int amount, int slot = -1)
    {
        var req = new InventoryRequest
        {
            Kind = kind, ItemId = item.Id, TargetId = target, TargetName = targetName, Amount = amount, ItemName = item.Name, Slot = slot,
        };
        if (!InventoryModel.Request(req, out string why))
        {
            _refusal = why;
            _refusalAt = Stopwatch.GetTimestamp();
        }
        else
        {
            _refusal = string.Empty;
        }
    }

    private void DropOn(DropKind kind, uint targetId, string targetName, InventoryItem item, int slot = -1)
    {
        switch (kind)
        {
            case DropKind.Pack:
                if (slot == -2)
                {
                    Refuse(_sort != 0 || FiltersOn
                        ? "To reorder, set Sort to Pack order and clear the search and filter."
                        : "The pack's order isn't known yet: log in again so the panel can learn it.");
                    return;
                }
                if (slot >= 0)
                {
                    // Dropped on a cell of the grid: that slot in the pack, which is also a reorder inside it.
                    if (item.Container == targetId && item.Wielder == 0 && SlotOf(item) == slot) return;   // already there
                    Act(InventoryActionKind.Move, item, targetId, targetName, 0, slot);
                    break;
                }
                if (item.Container == targetId && item.Wielder == 0) return;   // same pack: nothing to do
                if (item.RequiresPackSlot && targetId != InventoryModel.Current.PlayerId)
                {
                    // A side pack has no pack slots (ACE: ContainerCapacity 0); the server would refuse.
                    Refuse(item.Name + " uses a pack slot: it can only go in the main pack.");
                    return;
                }
                Act(InventoryActionKind.Move, item, targetId, targetName, 0);
                break;
            case DropKind.PackSlots:
                if (!item.RequiresPackSlot)
                {
                    Refuse("Only items that use a pack slot (foci) go there. Drop it on a pack to move it.");
                    return;
                }
                if (item.Container == targetId && item.Wielder == 0) return;   // already in the main pack
                Act(InventoryActionKind.Move, item, targetId, targetName, 0);
                break;
            case DropKind.Equip:
                if (item.Wielder != 0) return;
                Act(InventoryActionKind.Wield, item, 0, "", 0);
                break;
            case DropKind.Slot:
                if (item.Wielder != 0) return;   // already worn (moving between slots isn't supported)
                if ((item.ValidLocations & targetId) == 0)
                {
                    Refuse(item.Name + " doesn't go in the " + targetName + " slot.");
                    return;
                }
                Act(InventoryActionKind.Wield, item, targetId, targetName, 0);
                break;
            case DropKind.Drop:
                Act(InventoryActionKind.Drop, item, 0, "", 0);
                break;
            case DropKind.Give:
                if (targetId != 0) Act(InventoryActionKind.Give, item, targetId, targetName, 0);
                break;
        }
    }

    /// <summary>Back to everything: no search text, no category, the attribute filter off (its rules kept).</summary>
    private void ClearFilters()
    {
        Array.Clear(_search);
        _searchText = string.Empty;
        _category = 0;
        SetAttrEnabled(false);
    }

    private void Refuse(string why)
    {
        _refusal = why;
        _refusalAt = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Would <paramref name="pack"/> refuse <paramref name="item"/> (full, or no place for it)?
    /// ACE's rules (Container.TryAddToInventory): a pack-slot item takes one of the main pack's
    /// pack slots (a side pack has none); anything else takes an item slot.
    /// </summary>
    private static bool PackRefuses(InventoryView view, InventoryPack pack, InventoryItem item)
    {
        if (item.Container == pack.Id && item.Wielder == 0) return false;   // already there
        if (item.RequiresPackSlot)
            return !pack.IsMain || Math.Max(0, view.Packs.Length - 1) + view.SlotItems.Length >= view.ContainerSlots;
        return pack.Used >= pack.Capacity;
    }

    /// <summary>Mouse released during a drag: act on the target under it (collected this frame).</summary>
    private void FinishDrag()
    {
        if (!ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            InventoryItem? item = _dragItem, pressed = _pressItem;
            bool narrow = _pressNarrow;
            _dragItem = null;
            _pressItem = null;
            _pressNarrow = false;
            if (item == null)
            {
                // A plain click on one of a group, not dragged: just that item now.
                if (narrow && pressed != null) _multi.Single(pressed.Id);
                return;
            }
            ItemDragBridge.EndPopOutDrag();
            Vector2 m = ImGuiNET.ImGui.GetMousePos();
            foreach (Target t in _targets)
            {
                if (m.X < t.Min.X || m.Y < t.Min.Y || m.X >= t.Max.X || m.Y >= t.Max.Y) continue;
                DropOnSelection(t.Kind, t.Id, t.Name, item, t.Slot);
                return;
            }
            // Let go outside this panel: another engine panel that takes items (Item Count,
            // Mini Remote, the Loot Editor) gets it, as with a drag out of AC's own inventory.
            if (!ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
                UiDropTargets.TryClaim(item.Id, 0, out _);
        }
    }

    /// <summary>
    /// ImGui drag-and-drop payload type for one item (uint object id), published while an item is
    /// dragged so other ImGui windows (RynthAi's Mini Remote slots) can accept the drop. Must
    /// match MiniRemoteHud.ItemPayloadType in RynthAi.
    /// </summary>
    public const string ItemPayloadType = "RYNTH_INV_ITEM";

    /// <summary>
    /// Selects <paramref name="it"/> here and in the game, like a click in the retail inventory, so
    /// "selected item" features (Mini Remote slots, item info, loot rules) see it too.
    /// </summary>
    private void SelectItem(InventoryItem it)
    {
        _selected = it.Id;
        // Queued, not called inline: Draw runs inside EndScene, and AC's SetSelectedObject rebuilds
        // UI elements, so it runs at the main-thread queue's drain point instead.
        if (ClientHelperHooks.GetSelectedItemId() != it.Id)
            AcMainThreadQueue.EnqueueSetSelectedObject(it.Id);
    }

    private unsafe void DrawDragGhost()
    {
        if (_dragItem == null) return;
        var fg = ImGuiNET.ImGui.GetForegroundDrawList();
        Vector2 m = ImGuiNET.ImGui.GetMousePos() - new Vector2(Icon * 0.5f, Icon * 0.5f);
        DrawItemIcon(fg, _dragItem.Id, m, Icon, 0xD0FFFFFF);
        DragBadge(fg, m);
        ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        // A popped-out Inventory draws in its own ImGui context: the client's drop targets
        // only see the drag through the bridge, which republishes it in the main context.
        if (ImGuiPopOuts.InPopOutFrame)
        {
            ItemDragBridge.NotePopOutDrag(_dragItem.Id);
            return;
        }

        // Re-submitted every frame of the drag (an extern source expires when it isn't); the
        // ghost above is the preview, so no ImGui tooltip.
        if (ImGuiNET.ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceExtern | ImGuiDragDropFlags.SourceNoPreviewTooltip))
        {
            uint id = _dragItem.Id;
            ImGuiNET.ImGui.SetDragDropPayload(ItemPayloadType, (IntPtr)(&id), sizeof(uint));
            ImGuiNET.ImGui.EndDragDropSource();
        }
    }

    // ── Tooltip ─────────────────────────────────────────────────────────

    private void Tooltip()
    {
        InventoryItem? it = _hover;
        if (_dragItem != null || ImGuiNET.ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId)) return;
        if (it == null)
        {
            // A pack cell, an empty paperdoll slot, the burden meter (Classic view).
            if (_hoverLabel == null) return;
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
            if (ImGuiNET.ImGui.BeginTooltip())
            {
                ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Amber);
                ImGuiNET.ImGui.TextUnformatted(_hoverLabel);
                ImGuiNET.ImGui.PopStyleColor();
                if (_hoverLabel2 != null)
                {
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Text);
                    ImGuiNET.ImGui.TextUnformatted(_hoverLabel2);
                    ImGuiNET.ImGui.PopStyleColor();
                }
                ImGuiNET.ImGui.EndTooltip();
            }
            ImGuiNET.ImGui.PopStyleColor(2);
            return;
        }
        if (!ReferenceEquals(it, _tipItem))
        {
            _tipItem = it;
            _tipBase = BaseLines(it);
        }
        InventoryTooltip? extra = InventoryModel.Tooltip;
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        if (ImGuiNET.ImGui.BeginTooltip())
        {
            ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetFontSize() * 24);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Amber);
            ImGuiNET.ImGui.TextUnformatted(it.Name);
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Text);
            foreach (string line in _tipBase) ImGuiNET.ImGui.TextUnformatted(line);
            ImGuiNET.ImGui.PopStyleColor();
            if (extra != null && extra.Id == it.Id)
            {
                ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Mute);
                foreach (string line in extra.Lines) ImGuiNET.ImGui.TextUnformatted(line);
                ImGuiNET.ImGui.PopStyleColor();
            }
            ImGuiNET.ImGui.PopTextWrapPos();
            ImGuiNET.ImGui.EndTooltip();
        }
        ImGuiNET.ImGui.PopStyleColor(2);
    }

    private static string[] BaseLines(InventoryItem it)
    {
        var lines = new List<string>(6);
        if (it.Stack > 1)
            lines.Add(it.MaxStack > 1 ? $"Stack {it.Stack:N0} of {it.MaxStack:N0}" : $"Stack {it.Stack:N0}");
        lines.Add($"Value {it.Value:N0}    Burden {it.Burden:N0}");
        if (it.Workmanship > 0)
            lines.Add("Workmanship " + it.Workmanship.ToString("0.##", CultureInfo.InvariantCulture));
        if (it.Wielder != 0) lines.Add("Equipped");
        if (it.IsPack) lines.Add($"Pack: {it.ItemsCapacity} slots");
        if (it.RequiresPackSlot) lines.Add("Uses a pack slot (main pack only)");
        return lines.ToArray();
    }

    // ── Context menu ────────────────────────────────────────────────────

    private void ContextMenu(InventoryView view)
    {
        if (_openCtx)
        {
            _openCtx = false;
            ImGuiNET.ImGui.OpenPopup("##inv_ctx");
        }
        PushMenuStyle();
        bool open = ImGuiNET.ImGui.BeginPopup("##inv_ctx");
        if (!open) { PopMenuStyle(); return; }
        try
        {
            InventoryItem? it = _ctxItem;
            if (it == null) { ImGuiNET.ImGui.CloseCurrentPopup(); return; }
            if (IsGroup(it)) { GroupMenu(view, it); return; }
            bool idle = InventoryModel.Pending == null && !InventoryModel.BatchRunning;
            SafeImGui.TextDisabled(it.Name);
            ImGuiNET.ImGui.Separator();
            if (ImGuiNET.ImGui.MenuItem("Use", "double-click", false, idle))
                Act(InventoryActionKind.Use, it, 0, "", 0);
            if (it.Wielder == 0 && it.ValidLocations != 0
                && ImGuiNET.ImGui.MenuItem("Equip", "", false, idle && InventoryModel.WieldAvailable))
                Act(InventoryActionKind.Wield, it, 0, "", 0);
            if (it.Stack > 1 && it.Wielder == 0 && ImGuiNET.ImGui.MenuItem("Split...", "", false, idle))
            {
                _splitItem = it;
                _splitAmount = Math.Max(1, it.Stack / 2);
                _splitTarget = it.Container;
                _openSplit = true;
            }
            if (ImGuiNET.ImGui.BeginMenu("Move to", idle))
            {
                foreach (InventoryPack p in view.Packs)
                {
                    bool here = p.Id == it.Container && it.Wielder == 0;
                    bool fits = !it.RequiresPackSlot || p.IsMain;   // a focus goes in the main pack only
                    if (ImGuiNET.ImGui.MenuItem(p.Name, p.FillText, here, !here && fits))
                        Act(InventoryActionKind.Move, it, p.Id, p.Name, 0);
                }
                ImGuiNET.ImGui.EndMenu();
            }
            if (it.Wielder == 0 && ImGuiNET.ImGui.MenuItem("Drop", "", false, idle && InventoryModel.DropAvailable))
                Act(InventoryActionKind.Drop, it, 0, "", 0);
            InventoryTarget? target = InventoryModel.Target;
            if (target != null && it.Wielder == 0
                && ImGuiNET.ImGui.MenuItem(target.MenuLabel, "", false, idle && InventoryModel.GiveAvailable))
                Act(InventoryActionKind.Give, it, target.Id, target.Name, 0);
            ImGuiNET.ImGui.Separator();
            if (ImGuiNET.ImGui.MenuItem("Add to loot profile..."))
                _lootAdd.Open(it.Id, it.Name, toOpenProfile: false);
            if (ImGuiNET.ImGui.IsItemHovered())
                ImGuiNET.ImGui.SetTooltip("Make a RynthAi loot rule for this item (preview first)");
            if (!it.IsPack && ImGuiNET.ImGui.MenuItem("Add to item count HUD"))
                UI.Data.RynthAiCommands.ApplyRemoteCommand("itemhudadd", it.Name);
            if (ImGuiNET.ImGui.IsItemHovered())
                ImGuiNET.ImGui.SetTooltip("Track how many of these you carry on RynthAi's floating item count HUD (/ra itemhud add)");
            if (!it.IsPack) MiniRemoteMenu(it);
            ImGuiNET.ImGui.Separator();
            SelectionMenuItems(it);
            if (!idle)
                ImGuiNET.ImGui.TextDisabled("(an item action is waiting)");
        }
        finally
        {
            ImGuiNET.ImGui.EndPopup();
            PopMenuStyle();
        }
    }

    /// <summary>RynthAi Mini Remote quick-use grid size (HudState.MiniRemoteSlotCount, 6 rows of 5).</summary>
    private const int MiniRemoteSlots = 30, MiniRemoteColumns = 5;

    /// <summary>
    /// "Add to Mini Remote" submenu: first empty slot, or a numbered grid laid out like the
    /// remote's own slots. RynthAi validates the item and reports the result in chat.
    /// </summary>
    private static void MiniRemoteMenu(InventoryItem it)
    {
        if (!ImGuiNET.ImGui.BeginMenu("Add to Mini Remote")) return;
        if (ImGuiNET.ImGui.MenuItem("First empty slot"))
            UI.Data.RynthAiCommands.ApplyRemoteCommand("remoteslot", "first " + it.Id.ToString(CultureInfo.InvariantCulture));
        ImGuiNET.ImGui.Separator();
        ImGuiNET.ImGui.TextDisabled("Or pick a slot (replaces what's there):");
        for (int i = 0; i < MiniRemoteSlots; i++)
        {
            if (i % MiniRemoteColumns != 0) ImGuiNET.ImGui.SameLine();
            string label = (i + 1).ToString("00", CultureInfo.InvariantCulture);
            if (ImGuiNET.ImGui.Button(label, new Vector2(28, 0)))
            {
                UI.Data.RynthAiCommands.ApplyRemoteCommand("remoteslot",
                    (i + 1).ToString(CultureInfo.InvariantCulture) + " " + it.Id.ToString(CultureInfo.InvariantCulture));
                ImGuiNET.ImGui.CloseCurrentPopup();
            }
        }
        ImGuiNET.ImGui.Separator();
        ImGuiNET.ImGui.TextDisabled("Tip: you can also drag the item onto a slot.");
        ImGuiNET.ImGui.EndMenu();
    }

    // ── Split dialog ────────────────────────────────────────────────────

    private void SplitDialog(InventoryView view)
    {
        if (_openSplit)
        {
            _openSplit = false;
            ImGuiNET.ImGui.SetNextWindowPos(ImGuiNET.ImGui.GetMousePos(), ImGuiCond.Always);
            ImGuiNET.ImGui.OpenPopup("##inv_split");
        }
        PushMenuStyle();
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 8));
        bool open = ImGuiNET.ImGui.BeginPopup("##inv_split");
        ImGuiNET.ImGui.PopStyleVar();
        if (!open) { PopMenuStyle(); return; }
        try
        {
            InventoryItem? it = _splitItem;
            // The stack may have changed or gone since the dialog opened: use the live copy.
            InventoryItem? live = it == null ? null : FindById(view, it.Id);
            if (live == null || live.Stack < 2)
            {
                ImGuiNET.ImGui.TextUnformatted("That stack is gone.");
                if (Button("##sp_close", "Close", ImGuiNET.ImGui.GetCursorScreenPos(), new Vector2(80, 22), Text, BtnFill))
                    ImGuiNET.ImGui.CloseCurrentPopup();
                ImGuiNET.ImGui.Dummy(new Vector2(80, 24));
                return;
            }
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Amber);
            ImGuiNET.ImGui.TextUnformatted(live.Name);
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.TextUnformatted("Stack of " + live.StackText + ". Split off:");
            int max = live.Stack - 1;
            _splitAmount = Math.Clamp(_splitAmount, 1, max);
            ImGuiNET.ImGui.SetNextItemWidth(200);
            ImGuiNET.ImGui.SliderInt("##sp_slider", ref _splitAmount, 1, max);
            ImGuiNET.ImGui.SetNextItemWidth(200);
            ImGuiNET.ImGui.InputInt("##sp_input", ref _splitAmount, 1, 10);
            _splitAmount = Math.Clamp(_splitAmount, 1, max);

            ImGuiNET.ImGui.TextUnformatted("Into:");
            InventoryPack? into = view.PackOf(_splitTarget);
            if (into == null) { _splitTarget = live.Container; into = view.PackOf(_splitTarget); }
            foreach (InventoryPack p in view.Packs)
            {
                if (ImGuiNET.ImGui.Selectable(p.Name + "  (" + p.FillText + ")##sp" + p.Id.ToString(CultureInfo.InvariantCulture),
                        p.Id == _splitTarget))
                    _splitTarget = p.Id;
            }

            bool idle = InventoryModel.Pending == null;
            Vector2 bp = ImGuiNET.ImGui.GetCursorScreenPos() + new Vector2(0, 4);
            if (Button("##sp_ok", PhosphorIcons.Scissors + " Split", bp, new Vector2(96, 24), Teal, BtnFill, enabled: idle && into != null))
            {
                Act(InventoryActionKind.Split, live, _splitTarget, into?.Name ?? "", _splitAmount);
                ImGuiNET.ImGui.CloseCurrentPopup();
            }
            if (Button("##sp_cancel", "Cancel", bp + new Vector2(102, 0), new Vector2(80, 24), Text, BtnFill))
                ImGuiNET.ImGui.CloseCurrentPopup();
            ImGuiNET.ImGui.SetCursorScreenPos(bp + new Vector2(0, 28));
            ImGuiNET.ImGui.Dummy(new Vector2(200, 0));
        }
        finally
        {
            ImGuiNET.ImGui.EndPopup();
            PopMenuStyle();
        }
    }

    private static InventoryItem? FindById(InventoryView view, uint id)
    {
        foreach (InventoryPack p in view.Packs)
            foreach (InventoryItem it in p.Items)
                if (it.Id == id) return it;
        return null;
    }

    private static void PushMenuStyle()
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Header, Selected);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Lighten(BtnFill));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, BtnFill);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.SliderGrab, Teal);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 4f);
    }

    private static void PopMenuStyle()
    {
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(7);
    }
}

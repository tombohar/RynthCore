// ============================================================================
//  RynthCore.Engine - ImGui/Panels/InventoryFace.Classic.cs
//  The Inventory panel's Classic look (docs/IMGUI_INVENTORY.md, Phase 2): the
//  retail inventory window, drawn with the client's own pictures from
//  client_portal.dat (RetailSprites) at the retail layout's positions.
//
//  Where the numbers come from: the retail window is LayoutDesc 0x21000023 in
//  the client's dats. Tom's OpenAC client rebuilds it from the dat
//  (src/AcDream.App/UI/Layout: InventoryController, PaperdollController,
//  PaperdollSlotBackgrounds, ItemListCellTemplate, UiItemSlot, RetailChromeSprites)
//  and pins the resolved layout in tests/AcDream.App.Tests/UI/Layout/fixtures/
//  inventory_21000023.json. The element positions, sizes and picture ids below
//  are read from that layout (and the empty-cell pictures re-resolved from the
//  installed dats the way OpenAC's ItemListCellTemplate does); the resize rules
//  (the grid and pack column grow with the window's height, the background's
//  25 px title band isn't stretched) follow AcClientReborn's InventoryGeometry.
//  Credit: OpenAC (Tom's project) for the layout and sprite research.
//
//    title     retail title bar "Inventory of <name>", Vault, Remote / Sort / Count /
//              GInv (text, icons or one menu: InventoryTitleLayout), size / tools /
//              Modern toggles, close
//    tools     search, sort, category chips (toggle, the funnel in the title)
//    paperdoll the 24 equip slots at their retail places (the 3D figure isn't
//              drawn, so the retail "Slots" view is always on)
//    column    burden meter, main pack, then the main pack's pack slots: side
//              packs, then the items that take a pack slot (foci), as retail
//              lists them; its scrollbar
//    contents  "Contents of <pack>": the open pack's slots, retail scrollbar;
//              with a search or filter on, the matches from every pack
//    status    the action waiting / last outcome (or the pyreals), Drop, Give
//
//  Same behaviours as the Modern look: one action at a time, drag to a pack /
//  slot / Drop / Give, double-click to use, right-click menu, split dialog.
//  Geometry is in retail pixels times the UI scale (DPI x the panel's text size),
//  shrunk only to fit a window narrower than 300.
// ============================================================================

using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Net;
using RynthCore.Engine.UI;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed partial class InventoryFace
{
    // ── Retail art (0x06 textures in client_portal.dat) ─────────────────
    private static class Art
    {
        public const uint Backdrop = 0x06004D0Au;       // 300x362 stone background (element 0x100001D0)
        public const uint TitleBar = 0x06004CFAu;       // 276x25 title bar (0x100001D3)
        public const uint Close = 0x06004D0Cu;          // 24x25 close button (0x100001D2); +1 = pressed
        public const uint GridEmpty = 0x06004D20u;      // empty contents cell (prototype 0x1000033A, ItemSlot_Empty)
        public const uint PackEmpty = 0x06000F6Eu;      // empty side-pack cell (prototype 0x1000033F > 0x1000033E)
        public const uint OpenPack = 0x06005D9Cu;       // 36x36 gold arrow on the open pack
        public const uint MainPackIcon = 0x0600127Eu;   // the main pack's backpack
        public const uint Selected = 0x06004D21u;       // yellow square on the selected item
        public const uint Waiting = 0x0600109Au;        // hatch on an item waiting for the server
        public const uint AcceptGrid = 0x060011F9u, AcceptPack = 0x060011F7u, Reject = 0x060011F8u;
        public const uint BurdenBack = 0x0600121Du, BurdenFill = 0x0600121Cu;       // meter 0x100001D9 and its fill
        public const uint CapacityBack = 0x06004D22u, CapacityFill = 0x06004D23u;   // 5x30 pack fill meter
        public const uint Track = 0x06004C5Fu;          // 16x32 scrollbar chain, tiled (LayoutDesc 0x2100003E)
        public const uint ThumbTop = 0x06004C60u, ThumbMid = 0x06004C63u, ThumbBottom = 0x06004C66u;
        public const uint Down = 0x06004C69u, Up = 0x06004C6Cu;   // +1 rollover, +2 pressed
    }

    // Retail geometry (px at scale 1), LayoutDesc 0x21000023.
    private const float RetailW = 300, TitleH = 25, FieldY = 23, ContentsY = 237, SideW = 61, CellPx = 32, PackPx = 36;
    private const int SideBagSlots = 7;

    private static uint Rgb(uint argb) => RynthTheme.Argb(argb);
    private static readonly uint StoneFallback = Rgb(0xFF16140F), CellFallback = Rgb(0xFF1C1B18), CellEdge = Rgb(0xFF4A4436),
        Gold = Rgb(0xFFCAB06D), White = Rgb(0xFFFFFFFF), Black = Rgb(0xFF000000), SelYellow = Rgb(0xFFF0E040),
        AcceptGreen = Rgb(0xFF30D040), RejectRed = Rgb(0xFFE03030);

    /// <summary>One paperdoll slot: its equip mask, retail place in the paperdoll field, empty picture.</summary>
    private readonly struct DollSlot
    {
        public readonly uint Mask;
        public readonly float X, Y;
        public readonly uint Empty;
        public readonly string Name;
        /// <summary>Aetheria slots: the unlock bit (PropertyInt 322); 0 = always shown.</summary>
        public readonly uint Sigil;

        public DollSlot(uint mask, float x, float y, uint empty, string name, uint sigil = 0)
        {
            Mask = mask;
            X = x;
            Y = y;
            Empty = empty;
            Name = name;
            Sigil = sigil;
        }
    }

    // Element ids, masks and empty pictures: OpenAC's PaperdollSlotBackgrounds (pinned from the
    // live dat there, re-checked against the installed dats); places: the layout's elements.
    private static readonly DollSlot[] Doll =
    {
        new(0x00000001u, 84, 28, 0x06006D7Fu, "Head"),                // 0x100005AB
        new(0x00000200u, 84, 64, 0x06006D7Bu, "Chest"),               // 0x100005AC
        new(0x00000400u, 84, 100, 0x06006D79u, "Abdomen"),            // 0x100005AD
        new(0x00000800u, 48, 64, 0x06006D87u, "Upper arms"),          // 0x100005AE
        new(0x00001000u, 48, 100, 0x06006D81u, "Lower arms"),         // 0x100005AF
        new(0x00000020u, 48, 136, 0x06006D7Du, "Hands"),              // 0x100005B0
        new(0x00002000u, 120, 100, 0x06006D89u, "Upper legs"),        // 0x100005B1
        new(0x00004000u, 120, 136, 0x06006D83u, "Lower legs"),        // 0x100005B2
        new(0x00000100u, 120, 172, 0x06006D85u, "Feet"),              // 0x100005B3
        new(0x00000002u, 192, 80, 0x060032C5u, "Shirt"),              // 0x100001E2
        new(0x00000040u, 192, 116, 0x060032C4u, "Pants"),            // 0x100001E3
        new(0x00008000u, 8, 8, 0x06000F68u, "Neck"),                  // 0x100001DA
        new(0x04000000u, 8, 44, 0x06006A6Cu, "Trinket"),              // 0x1000058E
        new(0x00020000u, 8, 80, 0x06000F6Au, "Right wrist"),          // 0x100001DD
        new(0x00080000u, 8, 116, 0x06000F6Bu, "Right ring"),          // 0x100001DE
        new(0x00200000u, 8, 172, 0x06000F6Cu, "Shield"),              // 0x100001E1
        new(0x00010000u, 156, 80, 0x06000F5Du, "Left wrist"),         // 0x100001DB
        new(0x00040000u, 156, 116, 0x06000F5Au, "Left ring"),         // 0x100001DC
        new(0x03500000u, 156, 172, 0x06000F66u, "Weapon"),            // 0x100001DF: melee, missile, held, two-handed
        new(0x00800000u, 190, 172, 0x06000F5Eu, "Ammunition"),        // 0x100001E0
        new(0x08000000u, 192, 44, 0x0600708Fu, "Cloak"),              // 0x100005E9
        new(0x10000000u, 126, 8, 0x06006BEFu, "Blue Aetheria", 1),    // 0x10000595
        new(0x20000000u, 158, 8, 0x06006BF0u, "Yellow Aetheria", 2),  // 0x10000596
        new(0x40000000u, 190, 8, 0x06006BF1u, "Red Aetheria", 4),     // 0x10000597
    };

    // ── Classic state ───────────────────────────────────────────────────
    private int _gridFirstRow, _packFirstRow;
    private float _thumbGrab;
    private string _captionText = string.Empty;
    private string? _hoverLabel, _hoverLabel2;
    private string _titleText = "Inventory";
    private string? _titleFor;
    // The title cut to its room (InventoryTitleLayout.Elide), kept while the text and the room stay the same.
    private string _titleCut = string.Empty;
    private string? _titleCutFor;
    private float _titleCutRoom, _titleCutW;

    // =====================================================================
    //  Body
    // =====================================================================

    private void ClassicBody(InventoryView view, Vector2 origin, Vector2 size)
    {
        // Scale: the UI scale (DPI x this panel's text size), shrunk only to fit a narrow window.
        float ui = EngineFrameController.FontScale * PanelTextScale.Factor(ImGuiFonts.CurrentStep);
        if (!(ui > 0f) || !float.IsFinite(ui)) ui = 1f;
        ui *= InventorySettingsStore.SizeFactor;   // the panel's own size (the - / + in the title bar)
        // Whole-pixel cells keep point-sampled icons even (32 px art drawn at 46.4 px is uneven).
        float want = Math.Max(0.4f, MathF.Round(32f * ui) / 32f);
        float s = Math.Max(0.4f, Math.Min(want, size.X / RetailW));
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        // Retail's window is a fixed width at its size: the panel follows it (the - / + in the title bar
        // resize it), so there is no spare strip beside the burden meter.
        FitWidth(size, RetailW * want);
        float w = Math.Min(size.X, RetailW * s);
        Vector2 o = origin;
        float statusH = MathF.Round(20 * s);
        float bottom = o.Y + size.Y - statusH;

        // The open pack: a real pack (Classic has no "All" or "Worn" entry; the paperdoll is Worn).
        if (_pack == AllPacks || _pack == WornPacks || view.PackOf(_pack) == null)
            _pack = view.Packs.Length > 0 ? view.Packs[0].Id : AllPacks;
        bool filtered = FiltersOn;
        RebuildIfNeeded(view, filtered ? AllPacks : _pack);

        Backdrop(dl, o, w, bottom - o.Y, s);
        TitleBar(view, dl, o, w, s);

        // Tools (search, sort, categories): between the title bar and the retail body.
        float toolsBottom = o.Y + TitleH * s;
        if (InventorySettingsStore.ShowTools)
        {
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(o.X + 6, toolsBottom + 4));
            Tools(w - 12);
            Chips(w - 12);
            toolsBottom = ImGuiNET.ImGui.GetCursorScreenPos().Y;
        }
        float ry = toolsBottom - TitleH * s;          // where retail y = 0 lands
        float r = Math.Max(ContentsY + 60, (bottom - ry) / s);   // the retail window's height (px at scale 1)

        // The side column sits against the grid, where retail has it: the retail window is a fixed width,
        // so a wider panel leaves its spare room on the right instead of a gap in the middle.
        float sx = o.X + Math.Min(w - SideW * s, (RetailW - SideW) * s);
        Paperdoll(view, dl, new Vector2(o.X, ry + FieldY * s), s);
        SideColumn(view, dl, new Vector2(sx, ry + FieldY * s), r, s, filtered);
        Contents(view, dl, new Vector2(o.X, ry + ContentsY * s), sx, r, s, filtered);
        StatusStrip(view, dl, new Vector2(o.X, bottom), w, statusH, s);

        // A press on an item that moves far enough starts a drag.
        if (_pressItem != null && _dragItem == null && ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left)
            && Vector2.DistanceSquared(ImGuiNET.ImGui.GetMousePos(), _pressAt) > 25)
            _dragItem = _pressItem;
    }

    // ── Background and title bar ────────────────────────────────────────

    /// <summary>The stone background: its 25 px title band at scale, the rest stretched (AcClientReborn's rule).</summary>
    private static void Backdrop(ImDrawListPtr dl, Vector2 o, float w, float h, float s)
    {
        if (!RetailSprites.TryGet(Art.Backdrop, out IntPtr tex))
        {
            dl.AddRectFilled(o, o + new Vector2(w, h), StoneFallback);
            return;
        }
        const float ArtH = 362f;
        float band = Math.Min(h, TitleH * s);
        dl.AddImage(tex, o, o + new Vector2(w, band), Vector2.Zero, new Vector2(1, TitleH / ArtH));
        if (h > band)
            dl.AddImage(tex, o + new Vector2(0, band), o + new Vector2(w, h), new Vector2(0, TitleH / ArtH), Vector2.One);
    }

    private void TitleBar(InventoryView view, ImDrawListPtr dl, Vector2 o, float w, float s)
    {
        float h = TitleH * s, closeW = 24 * s;
        float barW = w - closeW;

        // The bar: three slices so its bevelled ends keep their shape at any width.
        if (RetailSprites.TryGet(Art.TitleBar, out IntPtr tex))
        {
            const float Src = 276f, Cap = 14f;
            float cap = Math.Min(Cap * s, barW * 0.5f);
            float u = Cap / Src;
            dl.AddImage(tex, o, o + new Vector2(cap, h), Vector2.Zero, new Vector2(u, 1));
            dl.AddImage(tex, o + new Vector2(cap, 0), o + new Vector2(barW - cap, h), new Vector2(u, 0), new Vector2(1 - u, 1));
            dl.AddImage(tex, o + new Vector2(barW - cap, 0), o + new Vector2(barW, h), new Vector2(1 - u, 0), Vector2.One);
        }
        else
        {
            dl.AddRectFilled(o, o + new Vector2(barW, h - 1), Black, 4);
            dl.AddRect(o, o + new Vector2(barW, h - 1), Gold, 4);
        }

        // "Inventory of <name>": its width decides how much room the layout owes the title.
        if (!ReferenceEquals(_titleFor, view.PlayerName))
        {
            _titleFor = view.PlayerName;
            _titleText = view.PlayerName.Length > 0 ? "Inventory of " + view.PlayerName : "Inventory";
        }
        ImFontPtr f = ImGuiFonts.Get(UiFont.UiBold11);
        float titlePx = 12 * s;
        float titleK = titlePx / f.FontSize;
        ImGuiNET.ImGui.PushFont(f);
        Vector2 ts = ImGuiNET.ImGui.CalcTextSize(_titleText) * titleK;
        ImGuiNET.ImGui.PopFont();

        // Where everything goes (UI/InventoryTitleLayout, tested): the four toggles at the right end, Vault
        // next to them (always there on a vault server), then the related-panel buttons as text, as icons or
        // as one "more" menu, whichever leaves the title its room. Nothing lands off the panel.
        InventoryTitleLayout lay = InventoryTitleLayout.Compute(barW, s, VaultFeed.Available, ts.X);
        float btn = lay.Btn;
        float by = o.Y + (h - btn) * 0.5f;
        Vector2 At(TitleSlot sl) => new(o.X + sl.X, by);

        if (TitleToggle("##inv_smaller", PhosphorIcons.Minus, At(lay.Smaller), btn, false))
            InventorySettingsStore.StepSize(-1);
        SafeImGui.SetItemTooltip($"Smaller ({InventorySettingsStore.SizePct}%)");
        if (TitleToggle("##inv_bigger", PhosphorIcons.Plus, At(lay.Bigger), btn, false))
            InventorySettingsStore.StepSize(+1);
        SafeImGui.SetItemTooltip($"Bigger ({InventorySettingsStore.SizePct}%)");
        if (TitleToggle("##inv_tools", PhosphorIcons.Funnel, At(lay.Tools), btn, InventorySettingsStore.ShowTools))
        {
            // Closing the search / filter row shows every item again.
            if (InventorySettingsStore.ShowTools) ClearFilters();
            InventorySettingsStore.SetShowTools(!InventorySettingsStore.ShowTools);
        }
        ImGuiNET.ImGui.SetItemTooltip("Search, sort and filter");
        if (TitleToggle("##inv_modern", PhosphorIcons.SquaresFour, At(lay.Modern), btn, false))
            InventorySettingsStore.SetClassic(false);
        ImGuiNET.ImGui.SetItemTooltip("Modern look");

        // Vault (Aelrynth only): the contents area shows the account vault instead of the open pack.
        if (lay.Vault.Present)
        {
            if (TitleTextToggle("##inv_vault", "Vault", At(lay.Vault), new Vector2(lay.Vault.W, btn), _vaultOpen))
            {
                _vaultOpen = !_vaultOpen;
                if (_vaultOpen) VaultFeed.ViewOpened();
            }
            ImGuiNET.ImGui.SetItemTooltip("Your Aelrynth account vault");
        }
        else if (!VaultFeed.Available)
        {
            _vaultOpen = false;
        }

        // Related panels: Mini Remote, RynthSort, Item Count, Global Inventory.
        switch (lay.Mode)
        {
            case TitleExtrasMode.Text:
                if (TitleTextToggle("##inv_miniremote", "Remote", At(lay.Remote), new Vector2(lay.Remote.W, btn), MiniRemoteOpen))
                    ToggleMiniRemote();
                SafeImGui.SetItemTooltip(MiniRemoteTip);
                if (TitleTextToggle("##inv_rsort", "Sort", At(lay.Sort), new Vector2(lay.Sort.W, btn), RynthSortOpen))
                    ToggleRynthSort();
                SafeImGui.SetItemTooltip(RynthSortTip());
                if (TitleTextToggle("##inv_itemcount", "Count", At(lay.Count), new Vector2(lay.Count.W, btn), ItemCountOpen))
                    ToggleItemCount();
                SafeImGui.SetItemTooltip(ItemCountTip);
                if (TitleTextToggle("##inv_ginv", "GInv", At(lay.GInv), new Vector2(lay.GInv.W, btn), GlobalInventoryOpen))
                    ToggleGlobalInventory();
                SafeImGui.SetItemTooltip(GlobalInventoryTip());
                break;
            case TitleExtrasMode.Icons:
                if (TitleToggle("##inv_miniremote", PhosphorIcons.SlidersHorizontal, At(lay.Remote), btn, MiniRemoteOpen))
                    ToggleMiniRemote();
                SafeImGui.SetItemTooltip(MiniRemoteTip);
                if (TitleToggle("##inv_rsort", PhosphorIcons.SortAscending, At(lay.Sort), btn, RynthSortOpen))
                    ToggleRynthSort();
                SafeImGui.SetItemTooltip(RynthSortTip());
                if (TitleToggle("##inv_itemcount", PhosphorIcons.Bag, At(lay.Count), btn, ItemCountOpen))
                    ToggleItemCount();
                SafeImGui.SetItemTooltip(ItemCountTip);
                if (TitleToggle("##inv_ginv", PhosphorIcons.GlobeHemisphereWest, At(lay.GInv), btn, GlobalInventoryOpen))
                    ToggleGlobalInventory();
                SafeImGui.SetItemTooltip(GlobalInventoryTip());
                break;
            case TitleExtrasMode.Menu:
                if (TitleToggle("##inv_more", PhosphorIcons.DotsThree, At(lay.More), btn,
                        MiniRemoteOpen || RynthSortOpen || ItemCountOpen || GlobalInventoryOpen))
                    _openMore = true;
                ImGuiNET.ImGui.SetItemTooltip("Mini Remote, RynthSort, Item Count, Global Inventory");
                break;
        }
        RelatedMenu();

        // Drag surface: the bar left of the buttons (moves the panel, like the header).
        float dragW = Math.Max(1, lay.LeftmostX - 2);
        ImGuiNET.ImGui.SetCursorScreenPos(o);
        ImGuiNET.ImGui.InvisibleButton("##inv_titledrag", new Vector2(dragW, h));
        ImGuiPanelHost.DragWindowWithLastItem();

        // The title: centred on the bar when it fits, else against the left edge and cut with "...".
        float room = lay.TitleWidth - 4 * s;
        if (room >= 1)
        {
            string shown = _titleText;
            if (ts.X > room)
            {
                if (!ReferenceEquals(_titleCutFor, _titleText) || _titleCutRoom != room)
                {
                    CutTitle(f, titleK, room);
                }
                shown = _titleCut;
                ts.X = _titleCutW;
            }
            if (shown.Length > 0)
            {
                float left = o.X + lay.TitleLeft + 4 * s;
                float tx = Math.Clamp(o.X + (barW - ts.X) * 0.5f, left, Math.Max(left, o.X + lay.TitleRight - ts.X));
                dl.PushClipRect(o + new Vector2(lay.TitleLeft, 0), new Vector2(o.X + lay.TitleRight, o.Y + h), true);
                dl.AddText(f, titlePx, new Vector2(tx, o.Y + (h - ts.Y) * 0.5f), White, shown);
                dl.PopClipRect();
            }
        }

        // The retail close button.
        var cp = new Vector2(o.X + barW, o.Y);
        ImGuiNET.ImGui.SetCursorScreenPos(cp);
        if (ImGuiNET.ImGui.InvisibleButton("##inv_close", new Vector2(closeW, h)))
            ImGuiPanelHost.Close(Title);
        bool down = ImGuiNET.ImGui.IsItemActive();
        ImGuiNET.ImGui.SetItemTooltip("Close");
        if (!Sprite(dl, down ? Art.Close + 1 : Art.Close, cp, new Vector2(closeW, h)))
        {
            dl.AddRect(cp + new Vector2(3 * s, 3 * s), cp + new Vector2(closeW - 3 * s, h - 3 * s), Gold, 2);
            dl.AddLine(cp + new Vector2(7 * s, h * 0.5f), cp + new Vector2(closeW - 7 * s, h * 0.5f), Gold, 2);
        }
    }

    /// <summary>Cuts the title text to <paramref name="room"/> px (only when it changed): _titleCut and its width.</summary>
    private void CutTitle(ImFontPtr font, float k, float room)
    {
        ImGuiNET.ImGui.PushFont(font);
        _titleCut = InventoryTitleLayout.Elide(_titleText, room, t => ImGuiNET.ImGui.CalcTextSize(t).X * k);
        _titleCutW = _titleCut.Length == 0 ? 0 : ImGuiNET.ImGui.CalcTextSize(_titleCut).X * k;
        ImGuiNET.ImGui.PopFont();
        _titleCutFor = _titleText;
        _titleCutRoom = room;
    }

    private static bool TitleToggle(string id, string glyph, Vector2 pos, float size, bool on)
    {
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, new Vector2(size, size));
        bool hot = ImGuiNET.ImGui.IsItemHovered();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        if (on || hot) dl.AddRect(pos, pos + new Vector2(size, size), on ? Gold : Faded(Gold), 3);
        PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), glyph, pos, new Vector2(size, size), on || hot ? Gold : Faded(Gold));
        return clicked;
    }

    // ── Paperdoll ───────────────────────────────────────────────────────

    private void Paperdoll(InventoryView view, ImDrawListPtr dl, Vector2 field, float s)
    {
        float cell = CellPx * s;
        uint pendingId = InventoryModel.Pending?.ItemId ?? 0;
        for (int i = 0; i < Doll.Length; i++)
        {
            DollSlot slot = Doll[i];
            InventoryItem? worn = null;
            foreach (InventoryItem it in view.Worn)
                if ((it.Location & slot.Mask) != 0) { worn = it; break; }
            if (slot.Sigil != 0 && worn == null && (view.AetheriaMask & slot.Sigil) == 0) continue;   // locked sigil slot

            var min = new Vector2(MathF.Round(field.X + slot.X * s), MathF.Round(field.Y + slot.Y * s));
            var max = min + new Vector2(cell, cell);
            ImGuiNET.ImGui.SetCursorScreenPos(min);
            ImGuiNET.ImGui.PushID(i);
            ImGuiNET.ImGui.InvisibleButton("##doll", new Vector2(cell, cell), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
            bool hovered = ImGuiNET.ImGui.IsItemHovered();
            ImGuiNET.ImGui.PopID();

            // The slot's tile is drawn filled or not: it is part of the panel, and it is what says an
            // item is worn (retail draws a worn item like any other, over its slot).
            CellPicture(dl, slot.Empty, min, cell);
            if (worn != null)
            {
                DrawItemIcon(dl, worn.Id, min, cell, ReferenceEquals(worn, _dragItem) ? 0x60FFFFFFu : 0xFFFFFFFFu);
                ItemPointer(worn, hovered);
            }
            else
            {
                if (hovered)
                {
                    _hoverLabel = slot.Name;
                    _hoverLabel2 = "Empty. Drag an item here to wear it.";
                }
            }

            bool over = MouseIn(min, max);
            if (_dragItem != null && over)
            {
                bool fits = _dragItem.Wielder == 0 && (_dragItem.ValidLocations & slot.Mask) != 0;
                Overlay(dl, fits ? Art.AcceptGrid : Art.Reject, min, cell, fits ? AcceptGreen : RejectRed);
            }
            if (worn != null)
            {
                if (worn.Id == pendingId) Overlay(dl, Art.Waiting, min, cell, Amber);
                if (worn.Id == _selected) Overlay(dl, Art.Selected, min, cell, SelYellow);
            }
            _targets.Add(new Target { Min = min, Max = max, Kind = DropKind.Slot, Id = slot.Mask, Name = slot.Name });
        }
    }

    // ── Side column: burden, main pack, side packs ──────────────────────

    private void SideColumn(InventoryView view, ImDrawListPtr dl, Vector2 field, float r, float s, bool filtered)
    {
        // Burden: caption and percent (36x15 each at y 7 and 18), meter 11x58 at (44, 8).
        // Retail's text here is about 13 px (a serif face); the smallest UI font was too small to read.
        ImFontPtr f = ImGuiFonts.Get(UiFont.Ui14);
        CenterText(dl, f, "Burden", new Vector2(field.X - 3 * s, field.Y + 5 * s), new Vector2(42 * s, 15 * s), White, 13 * s);
        CenterText(dl, f, view.BurdenText, new Vector2(field.X - 3 * s, field.Y + 18 * s), new Vector2(42 * s, 15 * s), White, 13 * s);
        var mMin = new Vector2(field.X + 44 * s, field.Y + 8 * s);
        var mSize = new Vector2(11 * s, 58 * s);
        // Retail's meter holds 0-300% of capacity (OpenAC's BurdenMath.LoadToFill: load / 3).
        float load = view.BurdenCapacity > 0 && view.Burden >= 0 ? view.Burden / (float)view.BurdenCapacity : 0f;
        float fill = Math.Clamp(load / 3f, 0f, 1f);
        if (!Sprite(dl, Art.BurdenBack, mMin, mSize)) dl.AddRectFilled(mMin, mMin + mSize, CellFallback);
        if (fill > 0f)
        {
            var fMin = new Vector2(mMin.X, mMin.Y + mSize.Y * (1 - fill));
            if (RetailSprites.TryGet(Art.BurdenFill, out IntPtr ft))
                dl.AddImage(ft, fMin, mMin + mSize, new Vector2(0, 1 - fill), Vector2.One);
            else
                dl.AddRectFilled(fMin, mMin + mSize, load > 2f ? Red : load > 1f ? Amber : Green);
        }
        if (MouseIn(field + new Vector2(0, 7 * s), mMin + mSize) && ImGuiNET.ImGui.IsWindowHovered())
        {
            _hoverLabel = "Burden";
            _hoverLabel2 = view.BurdenTip;
        }

        // Main pack (36x36 at (6, 32)).
        InventoryPack? main = view.Packs.Length > 0 ? view.Packs[0] : null;
        PackCell(view, dl, main, new Vector2(MathF.Round(field.X + 6 * s), MathF.Round(field.Y + 32 * s)), s, -1, filtered);

        // The main pack's pack slots (36 px pitch at (6, 73)): the side packs, then the items
        // that take a pack slot (foci), as retail lists them; retail's seven slots (eight with
        // the augmentation) or more. Scrollbar at (41, 73); they scroll together.
        float listY = field.Y + 73 * s;
        float listH = Math.Max(PackPx * s, (r - FieldY - 87) * s);
        int rows = Math.Max(1, (int)(listH / (PackPx * s)));
        int sidePacks = Math.Max(0, view.Packs.Length - 1);
        int used = sidePacks + view.SlotItems.Length;
        int slots = Math.Max(view.ContainerSlots > 0 ? view.ContainerSlots : SideBagSlots, used);
        _packFirstRow = Math.Clamp(_packFirstRow, 0, Math.Max(0, slots - rows));
        uint pendingId = InventoryModel.Pending?.ItemId ?? 0;
        for (int row = 0; row < rows; row++)
        {
            int i = _packFirstRow + row;
            if (i >= slots) break;
            var min = new Vector2(MathF.Round(field.X + 6 * s), MathF.Round(listY + row * PackPx * s));
            if (i < sidePacks) PackCell(view, dl, view.Packs[i + 1], min, s, i, filtered);
            else if (i < used) SlotItemCell(view, dl, view.SlotItems[i - sidePacks], min, s, i, pendingId);
            else
            {
                PackCell(view, dl, null, min, s, i, filtered);
                SlotDropHint(view, dl, min + new Vector2(2 * s, 2 * s), CellPx * s);
            }
        }
        // The column itself (after the pack cells, which win): a pack-slot item dropped here
        // moves to the main pack, where it lives. Nothing goes *inside* a pack-slot item.
        var colMin = new Vector2(field.X, listY);
        var colMax = new Vector2(field.X + 41 * s, listY + Math.Min(listH, rows * PackPx * s));
        if (view.PlayerId != 0)
            _targets.Add(new Target { Min = colMin, Max = colMax, Kind = DropKind.PackSlots, Id = view.PlayerId, Name = "Main pack" });
        if (MouseIn(new Vector2(field.X, listY), new Vector2(field.X + SideW * s, listY + listH))
            && ImGuiNET.ImGui.IsWindowHovered())
            Wheel(ref _packFirstRow, slots - rows);
        // Retail draws this scrollbar only when the slots do not all fit.
        if (slots > rows)
            Scrollbar("##inv_packsb", new Vector2(field.X + 41 * s, listY), listH, s, ref _packFirstRow, slots, rows);
    }

    /// <summary>
    /// A pack-slot item (a focus) in the side column: a 36x36 cell with the item at (2, 2),
    /// drawn and handled like a grid item (select, double-click use, drag, tooltip, menu).
    /// </summary>
    private void SlotItemCell(InventoryView view, ImDrawListPtr dl, InventoryItem it, Vector2 min, float s, int index, uint pendingId)
    {
        float box = PackPx * s, cell = CellPx * s;
        var inner = min + new Vector2(2 * s, 2 * s);
        ImGuiNET.ImGui.SetCursorScreenPos(min);
        ImGuiNET.ImGui.PushID(index + 2);
        ImGuiNET.ImGui.InvisibleButton("##slotitem", new Vector2(box, box), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGuiNET.ImGui.IsItemHovered();
        ImGuiNET.ImGui.PopID();
        ItemPointer(it, hovered);
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));   // stack counts
        CellPicture(dl, Art.PackEmpty, inner, cell);   // the slot stays under what fills it
        ItemCell(dl, it, inner, cell, pendingId);
        ImGuiNET.ImGui.PopFont();
        SlotDropHint(view, dl, inner, cell);
    }

    /// <summary>While a drag is over a pack-slot cell: accept a pack-slot item headed for the main pack, else refuse.</summary>
    private void SlotDropHint(InventoryView view, ImDrawListPtr dl, Vector2 inner, float cell)
    {
        if (_dragItem == null || !MouseIn(inner, inner + new Vector2(cell, cell))) return;
        InventoryPack? main = view.Packs.Length > 0 ? view.Packs[0] : null;
        if (_dragItem.RequiresPackSlot && _dragItem.Container == view.PlayerId && _dragItem.Wielder == 0) return;   // already here
        bool ok = main != null && _dragItem.RequiresPackSlot && !PackRefuses(view, main, _dragItem);
        Overlay(dl, ok ? Art.AcceptPack : Art.Reject, inner, cell, ok ? AcceptGreen : RejectRed);
    }

    /// <summary>One pack cell (36x36, the picture at (2, 2)): click opens it; drop an item on it to move it in.</summary>
    private void PackCell(InventoryView view, ImDrawListPtr dl, InventoryPack? pack, Vector2 min, float s, int index, bool filtered)
    {
        float box = PackPx * s, cell = CellPx * s;
        var inner = min + new Vector2(2 * s, 2 * s);
        ImGuiNET.ImGui.SetCursorScreenPos(min);
        ImGuiNET.ImGui.PushID(index + 2);
        bool clicked = ImGuiNET.ImGui.InvisibleButton("##pack", new Vector2(box, box));
        bool hovered = ImGuiNET.ImGui.IsItemHovered();
        ImGuiNET.ImGui.PopID();

        if (pack == null)
        {
            CellPicture(dl, Art.PackEmpty, inner, cell);
            return;
        }
        if (clicked && _dragItem == null) _pack = pack.Id;
        CellPicture(dl, Art.PackEmpty, inner, cell);   // the slot stays under what fills it
        if (pack.IsMain)
        {
            if (!Sprite(dl, Art.MainPackIcon, inner, new Vector2(cell, cell)))
                PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui14), PhosphorIcons.Backpack, inner, new Vector2(cell, cell), Amber);
        }
        else
        {
            DrawItemIcon(dl, pack.Id, inner, cell);
        }

        // Fill meter (5x30 at the cell's right edge), hidden while the pack is empty (retail's
        // rule). Pack-slot items (foci) don't count against the main pack's 102.
        if (pack.Used > 0 && pack.Capacity > 0)
        {
            float f = Math.Clamp(pack.Used / (float)pack.Capacity, 0f, 1f);
            var bMin = inner + new Vector2(cell - 5 * s, 1 * s);
            var bSize = new Vector2(5 * s, 30 * s);
            if (!Sprite(dl, Art.CapacityBack, bMin, bSize)) dl.AddRectFilled(bMin, bMin + bSize, Black);
            var fMin = new Vector2(bMin.X, bMin.Y + bSize.Y * (1 - f));
            if (RetailSprites.TryGet(Art.CapacityFill, out IntPtr ft))
                dl.AddImage(ft, fMin, bMin + bSize, new Vector2(0, 1 - f), Vector2.One);
            else
                dl.AddRectFilled(fMin, bMin + bSize, Gold);
        }

        if (pack.Id == _pack && !filtered)
        {
            if (!Sprite(dl, Art.OpenPack, min, new Vector2(box, box)))
                dl.AddTriangleFilled(new Vector2(min.X, min.Y + box * 0.35f), new Vector2(min.X + 5 * s, min.Y + box * 0.5f),
                    new Vector2(min.X, min.Y + box * 0.65f), Gold);
        }
        if (_dragItem != null && MouseIn(min, min + new Vector2(box, box)))
        {
            bool here = _dragItem.Container == pack.Id && _dragItem.Wielder == 0;
            bool full = !here && PackRefuses(view, pack, _dragItem);
            if (!here) Overlay(dl, full ? Art.Reject : Art.AcceptPack, inner, cell, full ? RejectRed : AcceptGreen);
        }
        if (hovered)
        {
            _hoverLabel = pack.Name;
            _hoverLabel2 = pack.FillText;
        }
        _targets.Add(new Target { Min = min, Max = min + new Vector2(box, box), Kind = DropKind.Pack, Id = pack.Id, Name = pack.Name });
    }

    // ── Contents: the open pack's slots ─────────────────────────────────

    private void Contents(InventoryView view, ImDrawListPtr dl, Vector2 field, float sideX, float r, float s, bool filtered)
    {
        if (_vaultOpen && VaultFeed.Available)
        {
            VaultContents(dl, field, sideX, r, s);
            return;
        }
        float cell = CellPx * s;
        var gridPos = new Vector2(MathF.Round(field.X + 15 * s), MathF.Round(field.Y + 20 * s));
        float availW = sideX - 32 * s - gridPos.X;   // retail: grid 15..207, scrollbar 207..223, column at 239
        int cols = Math.Max(1, (int)(availW / cell + 0.01f));   // exact multiples must not round down (6 columns at retail size)
        float gridW = cols * cell;
        float gridH = Math.Max(cell, (r - ContentsY - 5 - 24) * s);
        int rows = Math.Max(1, (int)(gridH / cell + 0.01f));

        // "Contents of <pack>" over the grid.
        CenterText(dl, ImGuiFonts.Get(UiFont.Ui14), _captionText, new Vector2(gridPos.X, field.Y), new Vector2(gridW, 15 * s), White, 12 * s);

        InventoryPack? open = filtered ? null : view.PackOf(_pack);
        int cells = open != null ? Math.Max(open.Capacity, _shown.Count) : _shown.Count;
        int totalRows = (cells + cols - 1) / cols;
        _gridFirstRow = Math.Clamp(_gridFirstRow, 0, Math.Max(0, totalRows - rows));

        var area = new Vector2(gridW, rows * cell);
        ImGuiNET.ImGui.SetCursorScreenPos(gridPos);
        ImGuiNET.ImGui.InvisibleButton("##inv_cells", area, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGuiNET.ImGui.IsItemHovered();
        Vector2 mouse = ImGuiNET.ImGui.GetMousePos();
        int hoverCell = -1;
        if (MouseIn(gridPos, gridPos + area))
        {
            int c = (int)((mouse.X - gridPos.X) / cell), rr = (int)((mouse.Y - gridPos.Y) / cell);
            if (c >= 0 && c < cols && rr >= 0 && rr < rows) hoverCell = (_gridFirstRow + rr) * cols + c;
            if (ImGuiNET.ImGui.IsWindowHovered()) Wheel(ref _gridFirstRow, totalRows - rows);
        }
        if (hovered && hoverCell >= 0 && hoverCell < _shown.Count) ItemPointer(_shown[hoverCell], true);

        // Only the rows on show. Retail draws the empty-cell picture under EVERY cell and the item
        // over it (AcClientReborn does the same), so the grid's lines show behind an item's icon.
        uint pendingId = InventoryModel.Pending?.ItemId ?? 0;
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));   // stack counts
        for (int rr = 0; rr < rows; rr++)
        {
            for (int c = 0; c < cols; c++)
            {
                int i = (_gridFirstRow + rr) * cols + c;
                if (i >= cells) break;
                var min = new Vector2(gridPos.X + c * cell, gridPos.Y + rr * cell);
                CellPicture(dl, Art.GridEmpty, min, cell);
                if (i >= _shown.Count) continue;
                ItemCell(dl, _shown[i], min, cell, pendingId);
            }
        }
        ImGuiNET.ImGui.PopFont();

        if (open != null)
        {
            // Dropping on the grid moves the item into the open pack - to the slot under the mouse when
            // the cells are the server's slots (retail: a drop on a cell places it there, which is also
            // how items are reordered inside a pack).
            int dropSlot = -1;
            if (_dragItem != null && hoverCell >= 0 && CellsAreSlots(_dragItem))
            {
                int mine = _dragItem.Container == open.Id && _dragItem.Wielder == 0 ? SlotOf(_dragItem) : -1;
                int others = _shown.Count - (mine >= 0 ? 1 : 0);
                dropSlot = Math.Min(hoverCell, Math.Max(0, others));
            }
            // A drop inside the item's own pack that cannot be a slot says why, instead of doing nothing.
            if (dropSlot < 0 && _dragItem != null && hoverCell >= 0 && _dragItem.Container == open.Id && _dragItem.Wielder == 0
                && !_dragItem.RequiresPackSlot && !_dragItem.IsPack)
                dropSlot = -2;
            bool sameSpot = dropSlot >= 0 && _dragItem != null && _dragItem.Container == open.Id && _dragItem.Wielder == 0 && SlotOf(_dragItem) == dropSlot;
            if (_dragItem != null && hoverCell >= 0 && hoverCell < cells
                && (dropSlot >= 0 ? !sameSpot : !(_dragItem.Container == open.Id && _dragItem.Wielder == 0)))
            {
                int c = hoverCell % cols, rr = hoverCell / cols - _gridFirstRow;
                var min = new Vector2(gridPos.X + c * cell, gridPos.Y + rr * cell);
                bool full = PackRefuses(view, open, _dragItem);
                Overlay(dl, full ? Art.Reject : Art.AcceptGrid, min, cell, full ? RejectRed : AcceptGreen);
            }
            _targets.Add(new Target { Min = gridPos, Max = gridPos + area, Kind = DropKind.Pack, Id = open.Id, Name = open.Name, Slot = dropSlot });
        }
        else if (_shown.Count == 0)
        {
            dl.AddText(gridPos + new Vector2(4, 4), Mute, "Nothing matches.");
        }

        Scrollbar("##inv_gridsb", new Vector2(gridPos.X + gridW, gridPos.Y), rows * cell, s, ref _gridFirstRow, totalRows, rows);
    }


    // ── The Aelrynth account vault (Inventory > Vault) ──────────────────

    private bool _vaultOpen;
    private int _vaultFirstRow;
    private VaultItem? _vaultCtx;
    private bool _openVaultCtx;

    /// <summary>The vault view's caption: how full it is, or the read's status before the first read.</summary>
    private static string VaultCaption(VaultSnapshot? snap)
    {
        if (snap == null) return VaultFeed.Status;
        int n = snap.Items.Length;
        return snap.Max > 0 ? $"Aelrynth Vault: {n:N0} of {snap.Max:N0}" : $"Aelrynth Vault: {n:N0}";
    }

    /// <summary>What the vault view says before it has been read.</summary>
    private static string VaultEmptyText => VaultFeed.Unsupported ? "The vault is not open on this server yet." : "Click Refresh to read the vault.";

    /// <summary>
    /// The vault's right-click menu (both looks): Withdraw the item. Call it in the panel's own window
    /// (not inside a child), so the popup opened and drawn here share one ID.
    /// </summary>
    private void VaultContextMenu()
    {
        if (_openVaultCtx)
        {
            _openVaultCtx = false;
            ImGuiNET.ImGui.OpenPopup("##vault_ctx");
        }
        PushMenuStyle();
        if (ImGuiNET.ImGui.BeginPopup("##vault_ctx"))
        {
            VaultItem? it = _vaultCtx;
            if (it == null) ImGuiNET.ImGui.CloseCurrentPopup();
            else
            {
                SafeImGui.TextDisabled(it.Name);
                ImGuiNET.ImGui.Separator();
                if (ImGuiNET.ImGui.MenuItem("Withdraw", "", false, VaultFeed.Available))
                    VaultFeed.Withdraw(it);
            }
            ImGuiNET.ImGui.EndPopup();
        }
        PopMenuStyle();
    }

    /// <summary>A small labelled button in the title bar.</summary>
    private static bool TitleTextToggle(string id, string label, Vector2 pos, Vector2 size, bool on)
    {
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, size);
        bool hot = ImGuiNET.ImGui.IsItemHovered();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        uint col = on || hot ? Gold : Faded(Gold);
        dl.AddRect(pos, pos + size, col, 3);
        Vector2 ts = ImGuiNET.ImGui.CalcTextSize(label);
        dl.AddText(pos + new Vector2((size.X - ts.X) * 0.5f, (size.Y - ts.Y) * 0.5f), col, label);
        return clicked;
    }

    /// <summary>
    /// The Vault view: the contents area shows the account vault (the grid's own frames for every slot,
    /// items drawn as they are in a pack). Read-only apart from right-click > Withdraw.
    /// </summary>
    private void VaultContents(ImDrawListPtr dl, Vector2 field, float sideX, float r, float s)
    {
        float cell = CellPx * s;
        var gridPos = new Vector2(MathF.Round(field.X + 15 * s), MathF.Round(field.Y + 20 * s));
        float availW = sideX - 32 * s - gridPos.X;
        int cols = Math.Max(1, (int)(availW / cell + 0.01f));   // exact multiples must not round down (6 columns at retail size)
        float gridW = cols * cell;
        float gridH = Math.Max(cell, (r - ContentsY - 5 - 24) * s);
        int rows = Math.Max(1, (int)(gridH / cell + 0.01f));

        VaultSnapshot? snap = VaultFeed.Snapshot;
        VaultItem[] items = snap?.Items ?? Array.Empty<VaultItem>();
        int cells = snap == null ? 0 : Math.Max(snap.Max, items.Length);
        CenterText(dl, ImGuiFonts.Get(UiFont.Ui14), VaultCaption(snap), new Vector2(gridPos.X, field.Y), new Vector2(gridW, 15 * s), White, 12 * s);

        // Read again (the vault changes when anything is deposited or withdrawn).
        var rb = new Vector2(gridPos.X + gridW - 44 * s, field.Y + 1 * s);
        if (TitleTextToggle("##vault_refresh", "Refresh", rb, new Vector2(44 * s, 14 * s), VaultFeed.Awaiting))
            VaultFeed.RequestRefresh();

        int totalRows = (cells + cols - 1) / cols;
        _vaultFirstRow = Math.Clamp(_vaultFirstRow, 0, Math.Max(0, totalRows - rows));
        var area = new Vector2(gridW, rows * cell);
        ImGuiNET.ImGui.SetCursorScreenPos(gridPos);
        ImGuiNET.ImGui.InvisibleButton("##vault_cells", area, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGuiNET.ImGui.IsItemHovered();
        Vector2 mouse = ImGuiNET.ImGui.GetMousePos();
        int hoverCell = -1;
        if (MouseIn(gridPos, gridPos + area))
        {
            int c = (int)((mouse.X - gridPos.X) / cell), rr = (int)((mouse.Y - gridPos.Y) / cell);
            if (c >= 0 && c < cols && rr >= 0 && rr < rows) hoverCell = (_vaultFirstRow + rr) * cols + c;
            if (ImGuiNET.ImGui.IsWindowHovered()) Wheel(ref _vaultFirstRow, totalRows - rows);
        }

        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));   // stack counts
        for (int rr = 0; rr < rows; rr++)
        {
            for (int c = 0; c < cols; c++)
            {
                int i = (_vaultFirstRow + rr) * cols + c;
                if (i >= cells) break;
                var min = new Vector2(gridPos.X + c * cell, gridPos.Y + rr * cell);
                CellPicture(dl, Art.GridEmpty, min, cell);
                if (i >= items.Length) continue;
                VaultItem it = items[i];
                if (ScriptIcons.TryGetItem(it.Icon, it.Underlay, it.Overlay, it.Effects, it.ItemType, out IntPtr tex))
                {
                    IconSampling.Begin(dl);
                    dl.AddImage(tex, min, min + new Vector2(cell, cell));
                    IconSampling.End(dl);
                }
                else
                    dl.AddRectFilled(min, min + new Vector2(cell, cell), Placeholder, 2);
                if (it.Stack > 1)
                {
                    string st = it.Stack.ToString(CultureInfo.InvariantCulture);
                    Vector2 ts = ImGuiNET.ImGui.CalcTextSize(st);
                    var tp = new Vector2(min.X + cell - ts.X - 1, min.Y + cell - ts.Y);
                    dl.AddText(tp + Vector2.One, StackShadow, st);
                    dl.AddText(tp, StackCol, st);
                }
            }
        }
        ImGuiNET.ImGui.PopFont();

        if (hovered && hoverCell >= 0 && hoverCell < items.Length)
        {
            VaultItem hot = items[hoverCell];
            SafeImGui.SetTooltip(hot.Stack > 1 ? $"{hot.Name} ({hot.Stack:N0})" : hot.Name);
            if (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                _vaultCtx = hot;
                _openVaultCtx = true;
            }
        }
        else if (snap == null && !VaultFeed.Awaiting)
        {
            dl.AddText(gridPos + new Vector2(4, 22 * s), Mute, VaultEmptyText);
        }

        VaultContextMenu();

        if (snap != null)   // nothing to scroll before the vault has been read
            Scrollbar("##inv_vaultsb", new Vector2(gridPos.X + gridW, gridPos.Y), rows * cell, s, ref _vaultFirstRow, totalRows, rows);
    }

    // ── Status strip: the action / outcome (or pyreals), Drop, Give ─────

    private void StatusStrip(InventoryView view, ImDrawListPtr dl, Vector2 p, float w, float h, float s)
    {
        dl.AddRectFilled(p, p + new Vector2(w, h), Black);
        dl.AddLine(p, p + new Vector2(w, 0), Faded(Gold));
        InventoryTarget? target = InventoryModel.Target;
        InventoryItem? sel = SelectedItem(view);
        RefreshSelectionLabels(target);
        float zh = h - 4, giveW = Math.Min(128 * s, w * 0.42f), dropW = 62 * s;
        var give = new Vector2(p.X + w - giveW - 2, p.Y + 2);
        var drop = new Vector2(give.X - dropW - 3, p.Y + 2);
        Zone("##z_drop", DropKind.Drop, PhosphorIcons.ArrowFatDown, _dropLabel, drop, new Vector2(dropW, zh), 0, "",
            InventoryModel.DropAvailable, sel, DropTip);
        Zone("##z_give", DropKind.Give, PhosphorIcons.Gift, _giveLabel, give, new Vector2(giveW, zh),
            target?.Id ?? 0, target?.Name ?? "", InventoryModel.GiveAvailable && target != null, sel,
            target?.ZoneTip ?? "Select a creature, NPC or player in the game to give to.");
        StatusLine(new Vector2(p.X + 3, p.Y), Math.Max(10, drop.X - p.X - 6), h, view);
    }

    // ── Pieces ──────────────────────────────────────────────────────────

    /// <summary>
    /// One item's cell picture (the contents grid and the side column's pack-slot items):
    /// its icon (faded while dragged), stack count (in the caller's font), and the waiting
    /// and selected overlays.
    /// </summary>
    private void ItemCell(ImDrawListPtr dl, InventoryItem it, Vector2 min, float cell, uint pendingId)
    {
        DrawItemIcon(dl, it.Id, min, cell, ReferenceEquals(it, _dragItem) ? 0x60FFFFFFu : 0xFFFFFFFFu);
        if (it.StackText.Length > 0)
        {
            Vector2 ts = ImGuiNET.ImGui.CalcTextSize(it.StackText);
            var tp = new Vector2(min.X + cell - ts.X - 1, min.Y + cell - ts.Y);
            dl.AddText(tp + Vector2.One, StackShadow, it.StackText);
            dl.AddText(tp, StackCol, it.StackText);
        }
        if (it.Id == pendingId) Overlay(dl, Art.Waiting, min, cell, Amber);
        if (IsPicked(it)) Overlay(dl, Art.Selected, min, cell, SelYellow);
    }

    /// <summary>Hover, click (select, and a press that may become a drag), double-click (use), right-click (menu).</summary>
    private void ItemPointer(InventoryItem it, bool hovered)
    {
        if (!hovered) return;
        _hover = it;
        if (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            ClickItem(it);
            _pressItem = it;
            _pressAt = ImGuiNET.ImGui.GetMousePos();
        }
        if (ImGuiNET.ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            _pressItem = null;
            Act(InventoryActionKind.Use, it, 0, "", 0);
        }
        if (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            RightClickItem(it);
    }

    /// <summary>
    /// The retail scrollbar (LayoutDesc 0x2100003E's 16 px variant): tiled chain, up and down
    /// gems, three-piece thumb. Clicking a gem moves a row; dragging the thumb, or clicking the
    /// chain, moves to that point.
    /// </summary>
    private void Scrollbar(string id, Vector2 pos, float h, float s, ref int first, int total, int visible)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float w = 16 * s, btn = Math.Min(16 * s, h * 0.5f);
        int max = Math.Max(0, total - visible);
        first = Math.Clamp(first, 0, max);
        if (h < 2 || w < 2) return;

        // Chain, tiled down the shaft (stretching smears the beads).
        if (RetailSprites.TryGet(Art.Track, out IntPtr track))
        {
            float tile = 32 * s;
            for (float y = 0; y < h; y += tile)
            {
                float th = Math.Min(tile, h - y);
                dl.AddImage(track, new Vector2(pos.X, pos.Y + y), new Vector2(pos.X + w, pos.Y + y + th), Vector2.Zero,
                    new Vector2(1, th / tile));
            }
        }
        else
        {
            dl.AddRectFilled(pos + new Vector2(w * 0.3f, 0), pos + new Vector2(w * 0.7f, h), Faded(Gold));
        }

        ImGuiNET.ImGui.PushID(id);
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        if (ImGuiNET.ImGui.InvisibleButton("##up", new Vector2(w, btn)) && first > 0) first--;
        uint upState = ImGuiNET.ImGui.IsItemActive() ? 2u : ImGuiNET.ImGui.IsItemHovered() ? 1u : 0u;
        var downPos = new Vector2(pos.X, pos.Y + h - btn);
        ImGuiNET.ImGui.SetCursorScreenPos(downPos);
        if (ImGuiNET.ImGui.InvisibleButton("##down", new Vector2(w, btn)) && first < max) first++;
        uint downState = ImGuiNET.ImGui.IsItemActive() ? 2u : ImGuiNET.ImGui.IsItemHovered() ? 1u : 0u;

        float t0 = pos.Y + btn, span = h - 2 * btn;
        if (max > 0 && span > 4)
        {
            float th = Math.Clamp(span * visible / Math.Max(1, total), Math.Min(16 * s, span), span);
            float t = t0 + (span - th) * first / max;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(pos.X, t0));
            ImGuiNET.ImGui.InvisibleButton("##track", new Vector2(w, span));
            if (ImGuiNET.ImGui.IsItemActive())
            {
                float my = ImGuiNET.ImGui.GetMousePos().Y;
                if (ImGuiNET.ImGui.IsItemActivated()) _thumbGrab = my >= t && my < t + th ? my - t : th * 0.5f;
                float rel = (my - _thumbGrab - t0) / Math.Max(1f, span - th);
                first = Math.Clamp((int)MathF.Round(rel * max), 0, max);
                t = t0 + (span - th) * first / max;
            }
            // Thumb: 3 px caps, the middle stretched.
            float cap = Math.Min(3 * s, th * 0.5f);
            var a = new Vector2(pos.X, t);
            if (!Sprite(dl, Art.ThumbTop, a, new Vector2(w, cap))
                | !Sprite(dl, Art.ThumbMid, a + new Vector2(0, cap), new Vector2(w, th - 2 * cap))
                | !Sprite(dl, Art.ThumbBottom, a + new Vector2(0, th - cap), new Vector2(w, cap)))
                dl.AddRectFilled(a, a + new Vector2(w, th), Rgb(0xFF203C78), 2);
        }
        ImGuiNET.ImGui.PopID();

        if (!Sprite(dl, Art.Up + upState, pos, new Vector2(w, btn)))
            PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), PhosphorIcons.CaretUp, pos, new Vector2(w, btn), Gold);
        if (!Sprite(dl, Art.Down + downState, downPos, new Vector2(w, btn)))
            PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), PhosphorIcons.CaretDown, downPos, new Vector2(w, btn), Gold);
    }

    private static void Wheel(ref int first, int max)
    {
        float wheel = ImGuiNET.ImGui.GetIO().MouseWheel;
        if (wheel == 0) return;
        first = Math.Clamp(first - Math.Sign(wheel), 0, Math.Max(0, max));
    }

    /// <summary>A retail picture at min..min+size; false (nothing drawn) until it has loaded.</summary>
    private static bool Sprite(ImDrawListPtr dl, uint id, Vector2 min, Vector2 size)
    {
        if (!RetailSprites.TryGet(id, out IntPtr tex)) return false;
        dl.AddImage(tex, min, min + size);
        return true;
    }

    /// <summary>An empty cell's picture, or a plain dark cell until it has loaded.</summary>
    private static void CellPicture(ImDrawListPtr dl, uint id, Vector2 min, float size)
    {
        if (Sprite(dl, id, min, new Vector2(size, size))) return;
        dl.AddRectFilled(min, min + new Vector2(size, size), CellFallback);
        dl.AddRect(min, min + new Vector2(size, size), CellEdge);
    }

    /// <summary>A retail overlay (selected, waiting, drop accepted / refused), or an outline in <paramref name="fallback"/>.</summary>
    private static void Overlay(ImDrawListPtr dl, uint id, Vector2 min, float size, uint fallback)
    {
        if (Sprite(dl, id, min, new Vector2(size, size))) return;
        dl.AddRect(min, min + new Vector2(size, size), fallback, 2, ImDrawFlags.None, 1.5f);
    }

    /// <summary>
    /// Text centred in a box. <paramref name="px"/> is the drawn size in screen pixels (retail's text is a fixed
    /// size per 32 px cell, so callers pass N * the layout scale); 0 = the font's own size times the panel size.
    /// </summary>
    private static void CenterText(ImDrawListPtr dl, ImFontPtr f, string text, Vector2 pos, Vector2 size, uint color, float px = 0f)
    {
        if (text.Length == 0) return;
        float drawPx = px > 0f ? px : f.FontSize * InventorySettingsStore.SizeFactor;
        ImGuiNET.ImGui.PushFont(f);
        Vector2 ts = ImGuiNET.ImGui.CalcTextSize(text);
        ImGuiNET.ImGui.PopFont();
        ts *= drawPx / f.FontSize;
        dl.PushClipRect(pos, pos + size, true);
        dl.AddText(f, drawPx, new Vector2(pos.X + (size.X - ts.X) * 0.5f, pos.Y + (size.Y - ts.Y) * 0.5f), color, text);
        dl.PopClipRect();
    }

    private long _fitAtMs;

    /// <summary>Asks the host to make the panel exactly <paramref name="wantPx"/> wide (screen px) when it is not; at most twice a second.</summary>
    private void FitWidth(Vector2 size, float wantPx)
    {
        if (ImGuiPopOuts.InPopOutFrame || Math.Abs(size.X - wantPx) <= 4f) return;
        long now = Environment.TickCount64;
        if (now - _fitAtMs < 500) return;
        _fitAtMs = now;
        float place = ImGuiPanelHost.PlacementScale();
        ImGuiPanelHost.RequestSize(Title, new Vector2(wantPx / place, size.Y / place));
    }

    private static bool MouseIn(Vector2 min, Vector2 max)
    {
        Vector2 m = ImGuiNET.ImGui.GetMousePos();
        return m.X >= min.X && m.Y >= min.Y && m.X < max.X && m.Y < max.Y;
    }
}

/// <summary>
/// The Inventory panel's remembered choices: Classic or Modern look, whether the
/// Classic view shows its tools row, its size, and the attribute filter's rules.
/// %LOCALAPPDATA%\RynthCore\inventory_settings.txt,
/// read once when the panel opens, written in the background when a choice changes.
/// </summary>
internal static class InventorySettingsStore
{
    private static readonly object Sync = new();
    private static bool _loaded;

    /// <summary>The retail look (default) or the Phase 1 modern layout.</summary>
    public static bool Classic { get; private set; } = true;
    /// <summary>Classic view: the search / sort / category row under the title bar.</summary>
    public static bool ShowTools { get; private set; }
    /// <summary>The panel's size in percent of the UI scale (50-150, steps of 5); text follows it.</summary>
    public static int SizePct { get; private set; } = 80;
    public static float SizeFactor => SizePct / 100f;
    /// <summary>The attribute filter's rules (InventoryAttributeFilter.Serialize); "" = none.</summary>
    public static string AttrFilter { get; private set; } = string.Empty;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "inventory_settings.txt");

    public static void Load()
    {
        lock (Sync)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (line.StartsWith('#') || eq <= 0) continue;
                    string key = line[..eq].Trim(), val = line[(eq + 1)..].Trim();
                    if (key == "classic") Classic = val == "1";
                    else if (key == "tools") ShowTools = val == "1";
                    else if (key == "size" && int.TryParse(val, out int pct)) SizePct = Math.Clamp(pct, 50, 150);
                    else if (key == "attrfilter") AttrFilter = val;
                }
            }
            catch { /* defaults stay */ }
        }
    }

    public static void SetClassic(bool on)
    {
        if (Classic == on) return;
        Classic = on;
        Save();
    }

    public static void StepSize(int dir)
    {
        int next = Math.Clamp(SizePct + 5 * Math.Sign(dir), 50, 150);
        if (next == SizePct) return;
        SizePct = next;
        Save();
    }

    public static void SetShowTools(bool on)
    {
        if (ShowTools == on) return;
        ShowTools = on;
        Save();
    }

    public static void SetAttrFilter(string line)
    {
        line ??= string.Empty;
        if (AttrFilter == line) return;
        AttrFilter = line;
        Save();
    }

    /// <summary>Writes the settings in the background (the caller is AC's render thread).</summary>
    private static void Save()
    {
        string text = "# RynthCore inventory panel settings - auto-generated, hand-edits OK.\n"
            + "classic=" + (Classic ? "1" : "0") + "\n"
            + "tools=" + (ShowTools ? "1" : "0") + "\n"
            + "size=" + SizePct + "\n"
            + "attrfilter=" + AttrFilter + "\n";
        UiBackgroundWriter.Enqueue("inventory settings", () =>
        {
            lock (Sync)
            {
                try
                {
                    string path = FilePath;
                    string? dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, text);
                }
                catch { /* non-fatal */ }
            }
        });
    }
}

// ============================================================================
//  RynthCore.Engine - ImGui/Panels/InventoryFace.VendorBuy.cs
//  Buying from a vendor with the Inventory panel showing (2026-11-26: a player
//  couldn't buy in bulk once RynthCore's windows were up).
//
//  In AC's own windows an item dragged out of the vendor's list onto the pack
//  takes the retail toolbar's number with it. Let go over this panel instead and
//  AC used to drop it nowhere. Now the panel takes it (ImGui/UiDropTargets, only
//  items on the open vendor's list; AC keeps the item where it was) and asks how
//  many: a number box that starts at the toolbar's number (VendorBuyMath.Prefill),
//  1 / 10 / 100 / half / max, the cost and what you have, and Buy N, which goes
//  through VendorTrade (the same checks as /rc vendor buy: money, pack room and
//  burden before it sends). The box stays open with the answer, for the next buy.
//
//  AC's main thread (the face draws in EndScene); VendorTrade's queue sends.
// ============================================================================

using System;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed partial class InventoryFace
{
    private const string VendorDropKey = "Inventory.VendorBuy";
    private const string BuyPopupId = "##inv_vendorbuy";
    private static readonly Predicate<uint> IsVendorItem = VendorTrade.IsVendorItem;

    private uint _buyItem, _buyVendor, _buySeq;
    private int _buyAmount = 1;
    private bool _openBuy;
    private string _buyAnswer = string.Empty;
    private bool _buyAnswerOk;

    /// <summary>
    /// Every frame after the body: the panel takes items dragged out of an open vendor's list
    /// (and shows that it does while one is dragged).
    /// </summary>
    private void AcceptVendorDrops(Vector2 origin, Vector2 size)
    {
        VendorSnapshot? snap = VendorTrade.TradableSnapshot;
        UiDropTargets.Publish(VendorDropKey, Title, origin, origin + size, snap != null, IsVendorItem);

        if (UiDropTargets.TryTake(VendorDropKey, out uint id) && snap?.Find(id) is { } dropped)
            OpenBuy(snap, dropped);

        if (snap == null) return;
        uint dragging = UiElementHooks.CurrentDragObject;
        if (dragging == 0 || snap.Find(dragging) == null) return;
        bool over = UiDropTargets.IsCursorOver(VendorDropKey);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddRect(origin + new Vector2(1, 1), origin + size - new Vector2(1, 1), over ? Green : Teal, 4, ImDrawFlags.None, over ? 3f : 1.5f);
        string hint = over ? "Let go: buy it (you pick how many)" : "Drop it here to buy (you pick how many)";
        Vector2 ts = ImGuiNET.ImGui.CalcTextSize(hint);
        Vector2 at = new(origin.X + Math.Max(4, (size.X - ts.X) * 0.5f), origin.Y + size.Y * 0.5f - ts.Y * 0.5f);
        dl.AddRectFilled(at - new Vector2(6, 3), at + ts + new Vector2(6, 3), ShellBg, 4);
        dl.AddText(at, over ? Green : Teal, hint);
    }

    private void OpenBuy(VendorSnapshot snap, VendorItemInfo item)
    {
        _buyItem = item.ObjectId;
        _buyVendor = snap.VendorId;
        _buySeq = 0;
        _buyAnswer = string.Empty;
        int toolbar = ClientHelperHooks.TryReadSplitAmount(out int n) ? n : 0;
        _buyAmount = VendorBuyMath.Prefill(toolbar, MaxBuy(snap, item));
        _openBuy = true;
    }

    private static int MaxBuy(VendorSnapshot snap, VendorItemInfo item) =>
        VendorBuyMath.MaxBuy(item.Unlimited ? -1 : item.Amount, VendorTrade.MoneyFor(snap), n => CostOf(snap, item, n));

    private static long CostOf(VendorSnapshot snap, VendorItemInfo item, int n)
    {
        VendorTrade.EstimateBuy(item, n, snap.SellRate, out long cost, out _, out _, out _);
        return cost;
    }

    private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The "Buy how many?" box. Draw with the other dialogs.</summary>
    private void BuyDialog()
    {
        if (_openBuy)
        {
            _openBuy = false;
            ImGuiNET.ImGui.SetNextWindowPos(ImGuiNET.ImGui.GetMousePos(), ImGuiCond.Always);
            ImGuiNET.ImGui.OpenPopup(BuyPopupId);
        }
        PushMenuStyle();
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 8));
        bool open = ImGuiNET.ImGui.BeginPopup(BuyPopupId);
        ImGuiNET.ImGui.PopStyleVar();
        if (!open) { PopMenuStyle(); return; }
        try
        {
            VendorSnapshot? snap = VendorTrade.TradableSnapshot;
            VendorItemInfo? item = snap != null && snap.VendorId == _buyVendor ? snap.Find(_buyItem) : null;
            if (snap == null || item == null)
            {
                ImGuiNET.ImGui.TextUnformatted(snap == null ? "The vendor closed." : "That item is no longer on the vendor's list.");
                if (Button("##vb_close", "Close", ImGuiNET.ImGui.GetCursorScreenPos(), new Vector2(80, 22), Text, BtnFill))
                    ImGuiNET.ImGui.CloseCurrentPopup();
                ImGuiNET.ImGui.Dummy(new Vector2(80, 24));
                return;
            }

            string currency = VendorTrade.CurrencyName(snap);
            long money = VendorTrade.MoneyFor(snap);
            int max = MaxBuy(snap, item);
            int unit = VendorTrade.UnitPrice(item, snap.SellRate);

            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Amber);
            ImGuiNET.ImGui.TextUnformatted(PhosphorIcons.Storefront + " " + item.Name);
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.TextUnformatted($"{N(unit)} {currency} each" + (item.Unlimited ? "" : $", the vendor has {N(item.Amount)}"));
            ImGuiNET.ImGui.TextUnformatted("How many?");

            ImGuiNET.ImGui.SetNextItemWidth(200);
            ImGuiNET.ImGui.InputInt("##vb_n", ref _buyAmount, 1, 10);
            _buyAmount = VendorBuyMath.Clamp(_buyAmount, max);
            SafeImGui.SetItemTooltip("Type how many to buy. It started at the game toolbar's number when that was set.");

            // Quick numbers.
            Vector2 cp = ImGuiNET.ImGui.GetCursorScreenPos();
            float x = cp.X;
            void Quick(string id, string label, int value, string tip)
            {
                float bw = ButtonWidth(label);
                if (Button(id, label, new Vector2(x, cp.Y), new Vector2(bw, 20), Text, BtnFill, enabled: max >= 1))
                    _buyAmount = VendorBuyMath.Clamp(value, max);
                SafeImGui.SetItemTooltip(tip);
                x += bw + 3;
            }
            Quick("##vb_1", "1", 1, "One.");
            Quick("##vb_10", "10", 10, "Ten.");
            Quick("##vb_100", "100", 100, "A hundred.");
            Quick("##vb_half", "half", VendorBuyMath.Half(max), $"Half of the most you can buy now ({N(VendorBuyMath.Half(max))}).");
            Quick("##vb_max", "max", max, money >= 0
                ? $"The most you can buy now ({N(max)}): what the vendor has and what you can pay for. Pack room and burden are checked when you buy."
                : $"What the vendor has ({N(max)}). Your {currency} couldn't be read; the buy checks them.");
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(cp.X, cp.Y + 24));

            long cost = CostOf(snap, item, _buyAmount);
            bool cantPay = money >= 0 && cost > money;
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, cantPay ? Red : Text);
            ImGuiNET.ImGui.TextUnformatted($"Cost {N(cost)} {currency}" + (money >= 0 ? $" (you have {N(money)})" : ""));
            ImGuiNET.ImGui.PopStyleColor();

            // The answer to the last Buy.
            var (seq, state, result, message) = VendorTrade.LastStatus();
            bool waiting = _buySeq != 0 && seq == _buySeq && state is 1 or 2;
            if (_buySeq != 0 && seq == _buySeq && state is 3 or 4)
            {
                _buyAnswerOk = state == 3 && result == 1;
                _buyAnswer = state == 4 ? "Not bought: " + message + "."
                    : result == 1 ? "Bought." : result == 2 ? "The vendor refused (see chat)." : "No answer from the vendor.";
                _buySeq = 0;
            }

            bool can = max >= 1 && !waiting && !cantPay;
            Vector2 bp = ImGuiNET.ImGui.GetCursorScreenPos() + new Vector2(0, 4);
            string buyLabel = PhosphorIcons.Coins + " Buy " + N(_buyAmount);
            float buyW = Math.Max(96, ButtonWidth(buyLabel));
            if (Button("##vb_buy", buyLabel, bp, new Vector2(buyW, 24), Teal, BtnFill, enabled: can))
            {
                uint s = VendorTrade.RequestBuy(_buyVendor, [_buyItem], [_buyAmount]);
                if (s != 0)
                {
                    _buySeq = s;
                    _buyAnswer = string.Empty;
                }
                else
                {
                    _buyAnswerOk = false;
                    _buyAnswer = "Not bought: " + VendorTrade.LastStatus().Message + ".";
                }
            }
            SafeImGui.SetItemTooltip(max < 1 ? (money >= 0 ? $"You can't pay for one ({N(unit)} {currency})." : "The vendor has none left.")
                : cantPay ? $"Costs {N(cost)} {currency}; you have {N(money)}."
                : waiting ? "Waiting for the vendor."
                : $"Buy {N(_buyAmount)} {item.Name} for {N(cost)} {currency}. The game checks your money, pack room and burden before it sends.");
            if (Button("##vb_cancel", "Close", bp + new Vector2(buyW + 6, 0), new Vector2(80, 24), Text, BtnFill))
                ImGuiNET.ImGui.CloseCurrentPopup();
            ImGuiNET.ImGui.SetCursorScreenPos(bp + new Vector2(0, 28));

            string line = waiting ? "Waiting for the vendor..." : _buyAnswer;
            if (line.Length > 0)
            {
                ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, waiting ? Mute : _buyAnswerOk ? Green : Red);
                ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + 260);
                ImGuiNET.ImGui.TextUnformatted(line);
                ImGuiNET.ImGui.PopTextWrapPos();
                ImGuiNET.ImGui.PopStyleColor();
            }
            ImGuiNET.ImGui.Dummy(new Vector2(260, 0));
        }
        finally
        {
            ImGuiNET.ImGui.EndPopup();
            PopMenuStyle();
        }
    }
}

// ============================================================================
//  RynthCore.Engine - ImGui/Panels/InventoryFace.MultiSelect.cs
//  Several items at once, in both looks:
//    pick      click = one item; Ctrl+click adds / removes one; Shift+click
//              the run from the last clicked item (Ctrl+Shift adds the run);
//              right-click menu: Select all shown
//    act       with more than one picked, Drop, Give and a drop on a pack act
//              on all of them (drag any picked item, or click the zone); the
//              right-click menu has Drop / Give / Move to for the group
//    confirm   Drop and Give of several ask first (given items don't come
//              back); Move doesn't
//    run       InventoryModel.RequestBatch sends them one at a time; the status
//              row shows "Giving 3 of 12 to Bob" with a Stop button
//  Worn items, and items that can't go where they're sent, are left out (and
//  counted in the confirm / refusal text).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed partial class InventoryFace
{
    private const int ConfirmNameLines = 10;
    private static readonly Func<InventoryItem, uint> IdOfItem = static it => it.Id;

    private readonly InventorySelection _multi = new();
    // A plain press on an item that is part of the group: the group stays for a drag;
    // released without dragging, the selection narrows to that item (Explorer's rule).
    private bool _pressNarrow;

    // Drop / Give confirmation (opened at the end of Draw, like the split dialog).
    private bool _openConfirm;
    private InventoryRequest[] _confirmItems = Array.Empty<InventoryRequest>();
    private string _confirmTitle = string.Empty, _confirmOk = string.Empty, _confirmNote = string.Empty;
    private string[] _confirmNames = Array.Empty<string>();

    // Labels that carry the selection's size, rebuilt only when it or the give target changes.
    private int _labelsFor = -1;
    private InventoryTarget? _labelsTarget;
    private string _dropLabel = "Drop", _giveLabel = "Give", _countBadge = string.Empty, _selHint = string.Empty;
    private string _menuCount = string.Empty, _menuDrop = string.Empty, _menuGive = string.Empty, _menuMove = string.Empty;
    private string _selectAllLabel = "Select all shown";

    // Status row cache for a running batch.
    private InventoryBatchStatus? _batchShown;

    /// <summary>A left click on an item: one item, or Ctrl / Shift to pick several.</summary>
    private void ClickItem(InventoryItem it)
    {
        ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();
        _pressNarrow = false;
        if (io.KeyShift)
        {
            _multi.Range(_shown, IdOfItem, it.Id, additive: io.KeyCtrl);
            SelectItem(it);
            return;
        }
        if (io.KeyCtrl)
        {
            // Ctrl+click right after a right-click selection keeps that item too.
            if (_multi.Count == 0 && _selected != 0 && _selected != it.Id) _multi.Single(_selected);
            if (_multi.Toggle(it.Id)) SelectItem(it);
            else _selected = _multi.Count > 0 ? _multi.Ids[_multi.Count - 1] : 0;
            return;
        }
        if (_multi.IsMulti && _multi.Contains(it.Id)) _pressNarrow = true;
        else _multi.Single(it.Id);
        SelectItem(it);
    }

    /// <summary>A right click: on a picked item the menu acts on the group, else on that one item.</summary>
    private void RightClickItem(InventoryItem it)
    {
        if (!_multi.Contains(it.Id)) _multi.Single(it.Id);
        _selected = it.Id;
        _ctxItem = it;
        _openCtx = true;
    }

    /// <summary>True when <paramref name="it"/> is drawn as selected (the one clicked, or part of the group).</summary>
    private bool IsPicked(InventoryItem it) => it.Id == _selected || _multi.Contains(it.Id);

    /// <summary>True when an action on <paramref name="it"/> means the whole group.</summary>
    private bool IsGroup(InventoryItem? it) => _multi.IsMulti && (it == null || _multi.Contains(it.Id));

    /// <summary>
    /// A zone click or a drop: on the group (Drop, Give, or a pack) when several are picked
    /// and <paramref name="item"/> is one of them; otherwise the single-item action.
    /// <paramref name="slot"/> is the pack cell a single item was dropped on (-1: none);
    /// a group goes to the end of the pack.
    /// </summary>
    private void DropOnSelection(DropKind kind, uint targetId, string targetName, InventoryItem? item, int slot = -1)
    {
        if (IsGroup(item) && kind is DropKind.Drop or DropKind.Give or DropKind.Pack)
        {
            GroupAction(kind, targetId, targetName);
            return;
        }
        if (item != null) DropOn(kind, targetId, targetName, item, slot);
    }

    /// <summary>Builds the group's requests; Drop and Give ask first, Move starts at once.</summary>
    private void GroupAction(DropKind kind, uint targetId, string targetName)
    {
        InventoryView view = InventoryModel.Current;
        InventoryActionKind action = kind switch
        {
            DropKind.Drop => InventoryActionKind.Drop,
            DropKind.Give => InventoryActionKind.Give,
            _ => InventoryActionKind.Move,
        };
        if (action == InventoryActionKind.Give && targetId == 0) { Refuse("Select someone in the game to give to."); return; }

        var list = new List<InventoryRequest>(_multi.Count);
        int worn = 0, gone = 0, cant = 0;
        foreach (uint id in _multi.Ids)
        {
            InventoryItem? it = FindAny(view, id);
            if (it == null) { gone++; continue; }
            if (it.Wielder != 0) { worn++; continue; }
            if (action == InventoryActionKind.Move)
            {
                // Already there (or the pack itself): skipped quietly, like a single item's drop.
                if (it.Id == targetId || it.Container == targetId) continue;
                // Foci and packs go only in the main pack (a side pack has no pack slots).
                if ((it.RequiresPackSlot || it.IsPack) && targetId != view.PlayerId) { cant++; continue; }
            }
            list.Add(new InventoryRequest
            {
                Kind = action, ItemId = it.Id, TargetId = action == InventoryActionKind.Drop ? 0 : targetId,
                TargetName = action == InventoryActionKind.Drop ? string.Empty : targetName, ItemName = it.Name,
            });
        }
        if (list.Count > InventoryModel.MaxBatch)
        {
            Refuse($"{list.Count} items: at most {InventoryModel.MaxBatch} at once.");
            return;
        }

        string note = LeftOutText(worn, gone, cant);
        if (list.Count == 0)
        {
            // All already in that pack: nothing to do, nothing to say.
            if (action == InventoryActionKind.Move && note.Length == 0) return;
            Refuse(note.Length > 0 ? "Nothing to send: " + note : "Nothing to send.");
            return;
        }
        if (action == InventoryActionKind.Move)
        {
            StartBatch(list);
            if (note.Length > 0 && _refusal.Length == 0) Refuse("Left out: " + note);
            return;
        }
        OpenConfirm(action, list, targetName, note);
    }

    /// <summary>"2 worn, 1 gone" for the items a group action leaves out ("" when none).</summary>
    private static string LeftOutText(int worn, int gone, int cant)
    {
        var parts = new List<string>(3);
        if (worn > 0) parts.Add($"{worn} worn");
        if (cant > 0) parts.Add($"{cant} can only go in the main pack");
        if (gone > 0) parts.Add($"{gone} gone");
        return string.Join(", ", parts);
    }

    private void StartBatch(IReadOnlyList<InventoryRequest> list)
    {
        if (!InventoryModel.RequestBatch(list, out string why)) Refuse(why);
        else _refusal = string.Empty;
    }

    // ── Confirmation ────────────────────────────────────────────────────

    private void OpenConfirm(InventoryActionKind action, List<InventoryRequest> list, string targetName, string note)
    {
        string n = list.Count.ToString(CultureInfo.InvariantCulture);
        _confirmItems = list.ToArray();
        _confirmTitle = action == InventoryActionKind.Give
            ? $"Give {n} item{(list.Count == 1 ? "" : "s")} to {targetName}?"
            : $"Drop {n} item{(list.Count == 1 ? "" : "s")} on the ground?";
        _confirmOk = (action == InventoryActionKind.Give ? PhosphorIcons.Gift + " Give " : PhosphorIcons.ArrowFatDown + " Drop ") + n;
        _confirmNote = note.Length > 0 ? "Left out: " + note + "." : string.Empty;
        _confirmNames = ConfirmNames(list);
        _openConfirm = true;
    }

    /// <summary>The items by name ("Pyreal x3"), in picked order, at most ConfirmNameLines lines.</summary>
    private static string[] ConfirmNames(List<InventoryRequest> list)
    {
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (InventoryRequest r in list)
        {
            if (counts.TryGetValue(r.ItemName, out int c)) counts[r.ItemName] = c + 1;
            else { counts[r.ItemName] = 1; order.Add(r.ItemName); }
        }
        var lines = new List<string>(Math.Min(order.Count, ConfirmNameLines) + 1);
        for (int i = 0; i < order.Count && i < ConfirmNameLines; i++)
            lines.Add(counts[order[i]] > 1 ? $"{order[i]}  x{counts[order[i]]}" : order[i]);
        if (order.Count > ConfirmNameLines)
            lines.Add($"...and {order.Count - ConfirmNameLines} more kind{(order.Count - ConfirmNameLines == 1 ? "" : "s")}");
        return lines.ToArray();
    }

    private void ConfirmDialog()
    {
        if (_openConfirm)
        {
            _openConfirm = false;
            ImGuiNET.ImGui.SetNextWindowPos(ImGuiNET.ImGui.GetMousePos(), ImGuiCond.Always);
            ImGuiNET.ImGui.OpenPopup("##inv_batch_confirm");
        }
        PushMenuStyle();
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 8));
        bool open = ImGuiNET.ImGui.BeginPopup("##inv_batch_confirm");
        ImGuiNET.ImGui.PopStyleVar();
        if (!open) { PopMenuStyle(); return; }
        try
        {
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Amber);
            ImGuiNET.ImGui.TextUnformatted(_confirmTitle);
            ImGuiNET.ImGui.PopStyleColor();
            foreach (string line in _confirmNames) SafeImGui.BulletText(line);
            if (_confirmNote.Length > 0) SafeImGui.TextDisabled(_confirmNote);
            if (_confirmItems.Length > 0 && _confirmItems[0].Kind == InventoryActionKind.Give)
                ImGuiNET.ImGui.TextDisabled("Given items can't be taken back.");

            bool idle = InventoryModel.Pending == null && !InventoryModel.BatchRunning;
            Vector2 bp = ImGuiNET.ImGui.GetCursorScreenPos() + new Vector2(0, 4);
            if (Button("##bc_ok", _confirmOk, bp, new Vector2(110, 24), Teal, BtnFill, enabled: idle && _confirmItems.Length > 0))
            {
                StartBatch(_confirmItems);
                _confirmItems = Array.Empty<InventoryRequest>();
                ImGuiNET.ImGui.CloseCurrentPopup();
            }
            if (!idle) ImGuiNET.ImGui.SetItemTooltip("An item action is still waiting.");
            if (Button("##bc_cancel", "Cancel", bp + new Vector2(116, 0), new Vector2(80, 24), Text, BtnFill))
            {
                _confirmItems = Array.Empty<InventoryRequest>();
                ImGuiNET.ImGui.CloseCurrentPopup();
            }
            ImGuiNET.ImGui.SetCursorScreenPos(bp + new Vector2(0, 28));
            ImGuiNET.ImGui.Dummy(new Vector2(200, 0));
        }
        finally
        {
            ImGuiNET.ImGui.EndPopup();
            PopMenuStyle();
        }
    }

    // ── Labels ──────────────────────────────────────────────────────────

    /// <summary>Zone labels, menu entries and hints that show the group's size. Rebuilt only on change.</summary>
    private void RefreshSelectionLabels(InventoryTarget? target)
    {
        int n = _multi.IsMulti ? _multi.Count : 0;
        if (n == _labelsFor && ReferenceEquals(target, _labelsTarget)) return;
        _labelsFor = n;
        _labelsTarget = target;
        string count = n.ToString(CultureInfo.InvariantCulture);
        _dropLabel = n > 0 ? "Drop " + count : "Drop";
        _giveLabel = target == null ? "Give" : n > 0 ? "Give " + count + ": " + target.Name : target.ZoneLabel;
        _countBadge = n > 0 ? count : string.Empty;
        _selHint = n > 0
            ? count + " selected: Drop, Give, drag to a pack, or right-click. Ctrl+click adds or removes; Shift+click picks a run."
            : string.Empty;
        _menuCount = count + " items selected";
        _menuDrop = "Drop " + count + " items";
        _menuGive = target == null ? string.Empty : "Give " + count + " items to " + target.Name;
        _menuMove = "Move " + count + " items to";
    }

    // ── Group menu ──────────────────────────────────────────────────────

    /// <summary>The right-click menu when the clicked item is part of a group.</summary>
    private void GroupMenu(InventoryView view, InventoryItem clicked)
    {
        bool idle = InventoryModel.Pending == null && !InventoryModel.BatchRunning;
        InventoryTarget? target = InventoryModel.Target;
        RefreshSelectionLabels(target);
        SafeImGui.TextDisabled(_menuCount);
        ImGuiNET.ImGui.Separator();
        if (ImGuiNET.ImGui.MenuItem(_menuDrop, "", false, idle && InventoryModel.DropAvailable))
            GroupAction(DropKind.Drop, 0, string.Empty);
        if (target != null && ImGuiNET.ImGui.MenuItem(_menuGive, "", false, idle && InventoryModel.GiveAvailable))
            GroupAction(DropKind.Give, target.Id, target.Name);
        if (ImGuiNET.ImGui.BeginMenu(_menuMove, idle))
        {
            foreach (InventoryPack p in view.Packs)
                if (ImGuiNET.ImGui.MenuItem(p.Name, p.FillText, false, true))
                    GroupAction(DropKind.Pack, p.Id, p.Name);
            ImGuiNET.ImGui.EndMenu();
        }
        ImGuiNET.ImGui.Separator();
        SelectionMenuItems(clicked);
        if (!idle) ImGuiNET.ImGui.TextDisabled("(an item action is waiting)");
    }

    /// <summary>Select all shown / select only this one (both menus).</summary>
    private void SelectionMenuItems(InventoryItem clicked)
    {
        if (ImGuiNET.ImGui.MenuItem(_selectAllLabel, "", false, _shown.Count > 1))
            _multi.SelectAll(_shown, IdOfItem);
        if (ImGuiNET.ImGui.IsItemHovered())
            ImGuiNET.ImGui.SetTooltip("Pick every item the grid shows (this pack, or the search's matches). Ctrl+click and Shift+click also pick several.");
        if (_multi.IsMulti && ImGuiNET.ImGui.MenuItem("Select only this item"))
        {
            _multi.Single(clicked.Id);
            _selected = clicked.Id;
        }
    }

    // ── Status and pieces ───────────────────────────────────────────────

    /// <summary>
    /// A running batch in the status row: progress and a Stop button at the row's right end.
    /// False when no batch is running.
    /// </summary>
    private bool BatchStatus(ImDrawListPtr dl, Vector2 p, float w, float statusH, float ty)
    {
        InventoryBatchStatus? batch = InventoryModel.Batch;
        if (batch == null || !batch.Running) { _batchShown = null; return false; }
        _batchShown = batch;
        float stop = Math.Min(statusH - 2, 20);
        var stopPos = new Vector2(p.X + w - stop - 1, p.Y + (statusH - stop) * 0.5f);
        PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), PhosphorIcons.HourglassMedium, p, new Vector2(16, statusH), Amber);
        dl.PushClipRect(p, new Vector2(stopPos.X - 2, p.Y + statusH), true);
        dl.AddText(new Vector2(p.X + 18, ty), Amber, batch.Text);
        dl.PopClipRect();
        if (IconButton("##inv_batch_stop", PhosphorIcons.Stop, stopPos, new Vector2(stop, stop), Red, BtnFill,
                enabled: !batch.Stopping, font: UiFont.Ui11))
            InventoryModel.CancelBatch();
        ImGuiNET.ImGui.SetItemTooltip("Stop: no more items are sent (the one in flight still finishes).");
        return true;
    }

    /// <summary>The group's size on the drag ghost.</summary>
    private void DragBadge(ImDrawListPtr fg, Vector2 ghostMin)
    {
        if (!IsGroup(_dragItem) || _countBadge.Length == 0) return;
        Vector2 ts = ImGuiNET.ImGui.CalcTextSize(_countBadge);
        var tp = ghostMin + new Vector2(Icon - ts.X + 4, Icon - ts.Y + 4);
        fg.AddRectFilled(tp - new Vector2(3, 1), tp + ts + new Vector2(3, 1), Faded(Black), 4);
        fg.AddText(tp, Amber, _countBadge);
    }

    /// <summary>Forgets picked items that left the inventory (given, dropped, merged). On a new view only.</summary>
    private void PruneSelection(InventoryView view)
    {
        if (_multi.Count == 0) return;
        _multi.Prune(id => FindAny(view, id) != null);
    }

    /// <summary>An item in the packs or worn, by id.</summary>
    private static InventoryItem? FindAny(InventoryView view, uint id)
    {
        foreach (InventoryPack p in view.Packs)
            foreach (InventoryItem it in p.Items)
                if (it.Id == id) return it;
        foreach (InventoryItem it in view.Worn)
            if (it.Id == id) return it;
        return null;
    }
}

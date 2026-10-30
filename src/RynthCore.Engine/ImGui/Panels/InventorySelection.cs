// ============================================================================
//  RynthCore.Engine - ImGui/Panels/InventorySelection.cs
//  The Inventory panel's multi-selection: which items are picked for a
//  Drop / Give / Move of several at once. Windows-style: click picks one,
//  Ctrl+click adds or removes one, Shift+click picks the run from the last
//  clicked item (the anchor) in the order the grid shows them, Ctrl+Shift+click
//  adds that run.
//
//  Ids only (object ids), no engine state, so tools/InventorySelectionTests can
//  run it. AC's render thread only (the panel's Draw); not thread-safe.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class InventorySelection
{
    private readonly HashSet<uint> _set = new();
    // Picked order: a batch acts on the items in the order the player picked them.
    private readonly List<uint> _order = new();

    /// <summary>Where a Shift+click run starts: the last item clicked or Ctrl+clicked (0 = none).</summary>
    public uint Anchor { get; private set; }

    public int Count => _order.Count;

    /// <summary>The picked ids, in picked order.</summary>
    public IReadOnlyList<uint> Ids => _order;

    /// <summary>More than one item picked: the zones and the menu act on all of them.</summary>
    public bool IsMulti => _order.Count > 1;

    public bool Contains(uint id) => _set.Contains(id);

    public void Clear()
    {
        _set.Clear();
        _order.Clear();
        Anchor = 0;
    }

    /// <summary>A plain click: just <paramref name="id"/>.</summary>
    public void Single(uint id)
    {
        Clear();
        if (id == 0) return;
        Add(id);
        Anchor = id;
    }

    /// <summary>Ctrl+click: adds <paramref name="id"/>, or removes it if picked. Returns true when it is now picked.</summary>
    public bool Toggle(uint id)
    {
        if (id == 0) return false;
        Anchor = id;
        if (_set.Remove(id))
        {
            _order.Remove(id);
            return false;
        }
        Add(id);
        return true;
    }

    /// <summary>
    /// Shift+click: the items from the anchor to <paramref name="id"/>, both included, in the
    /// order of <paramref name="shown"/>. <paramref name="additive"/> (Ctrl+Shift) keeps what is
    /// already picked. With no anchor on show (another pack, a new search) it picks just
    /// <paramref name="id"/>. The anchor stays where it was, so the run can be changed.
    /// </summary>
    public void Range<T>(IReadOnlyList<T> shown, Func<T, uint> idOf, uint id, bool additive)
    {
        int to = IndexOf(shown, idOf, id);
        int from = Anchor == 0 ? -1 : IndexOf(shown, idOf, Anchor);
        if (to < 0 || from < 0)
        {
            if (additive) { Add(id); Anchor = id; }
            else Single(id);
            return;
        }
        uint anchor = Anchor;
        if (!additive)
        {
            _set.Clear();
            _order.Clear();
        }
        int step = to >= from ? 1 : -1;
        for (int i = from; ; i += step)
        {
            Add(idOf(shown[i]));
            if (i == to) break;
        }
        Anchor = anchor;
    }

    /// <summary>Everything in <paramref name="shown"/> (the grid's current list), in its order.</summary>
    public void SelectAll<T>(IReadOnlyList<T> shown, Func<T, uint> idOf)
    {
        Clear();
        for (int i = 0; i < shown.Count; i++) Add(idOf(shown[i]));
        if (_order.Count > 0) Anchor = _order[0];
    }

    /// <summary>Drops ids that <paramref name="exists"/> no longer finds (given, dropped, merged). True if any went.</summary>
    public bool Prune(Func<uint, bool> exists)
    {
        int before = _order.Count;
        _order.RemoveAll(id => !exists(id));
        if (_order.Count == before) return false;
        _set.Clear();
        foreach (uint id in _order) _set.Add(id);
        if (Anchor != 0 && !_set.Contains(Anchor)) Anchor = 0;
        return true;
    }

    private void Add(uint id)
    {
        if (id != 0 && _set.Add(id)) _order.Add(id);
    }

    private static int IndexOf<T>(IReadOnlyList<T> shown, Func<T, uint> idOf, uint id)
    {
        for (int i = 0; i < shown.Count; i++)
            if (idOf(shown[i]) == id) return i;
        return -1;
    }
}

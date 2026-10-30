// ============================================================================
//  RynthCore.Engine - Compatibility/InventoryFilterFacts.cs
//  One item's values for the Inventory panel's attribute filter
//  (InventoryAttributeFilter): public ones from the panel's InventoryItem,
//  appraised ones from AppraisalHooks' cache (filled by an identify).
//
//  Threads: the panel's (render) thread. AppraisalHooks' getters lock their
//  cache and PortalSpellTable's lookups read an immutable table, so neither
//  touches AC. One instance is re-bound per item (no allocation per item
//  except a spells string, built only when a rule asks for it).
// ============================================================================

using System;
using System.Text;
using RynthCore.Engine.ImGuiBackend.Hud;

namespace RynthCore.Engine.Compatibility;

internal sealed class InventoryFilterFacts : IInventoryFilterFacts
{
    private InventoryItem _item = null!;
    private bool _identified;
    private string? _spells;
    private readonly uint[] _spellBuf = new uint[64];
    private readonly StringBuilder _sb = new(256);

    /// <summary>Points the facts at <paramref name="item"/>; cheap.</summary>
    public void Bind(InventoryItem item)
    {
        _item = item;
        _identified = AppraisalHooks.HasAppraisalData(item.Id);
        _spells = null;
    }

    public bool Identified => _identified;
    public uint SlotMask => _item.ValidLocations;

    public double Number(FilterAttrInfo attr)
    {
        uint id = _item.Id;
        switch (attr.Source)
        {
            case FilterSource.Public:
                return attr.Id switch
                {
                    FilterAttr.Value => _item.Value,
                    FilterAttr.Burden => _item.Burden,
                    FilterAttr.Workmanship => _item.Workmanship,
                    FilterAttr.Stack => Math.Max(1, _item.Stack),
                    _ => 0,
                };
            case FilterSource.Int:
                return AppraisalHooks.TryGetCachedIntProperty(id, attr.Stype, out int i) ? i : 0;
            case FilterSource.Float:
            case FilterSource.FloatModPercent:
            case FilterSource.FloatPercent:
                return AppraisalHooks.TryGetCachedDoubleProperty(id, attr.Stype, out double d)
                    ? InventoryAttributeFilter.Scale(attr.Source, d) : 0;
            case FilterSource.Special when attr.Id == FilterAttr.WieldLevel:
                return InventoryAttributeFilter.WieldLevel(
                    IntOr0(id, InventoryAttributeFilter.StypeWieldRequirements),
                    IntOr0(id, InventoryAttributeFilter.StypeWieldDifficulty),
                    IntOr0(id, InventoryAttributeFilter.StypeWieldRequirements2),
                    IntOr0(id, InventoryAttributeFilter.StypeWieldDifficulty2));
            default:
                return 0;
        }
    }

    public bool Flag(FilterAttrInfo attr) => attr.Source switch
    {
        FilterSource.Public => attr.Id switch
        {
            FilterAttr.Equipped => _item.Wielder != 0,
            FilterAttr.CastsSpell => _item.SpellId != 0,
            _ => false,
        },
        FilterSource.IntNonZero => IntOr0(_item.Id, attr.Stype) != 0,
        FilterSource.Bool => AppraisalHooks.TryGetCachedBoolProperty(_item.Id, attr.Stype, out bool b) && b,
        _ => false,
    };

    public string Text(FilterAttrInfo attr) => attr.Id switch
    {
        FilterAttr.Name => _item.Name,
        FilterAttr.Material => InventoryAttributeFilter.MaterialName(_item.MaterialType),
        FilterAttr.SetName => InventoryAttributeFilter.SetName(IntOr0(_item.Id, attr.Stype)),
        FilterAttr.Spells => _spells ??= SpellNames(),
        _ => string.Empty,
    };

    private static int IntOr0(uint id, uint stype) =>
        AppraisalHooks.TryGetCachedIntProperty(id, stype, out int v) ? v : 0;

    /// <summary>The appraised spells' names, newline-separated (a rule's text can't span two).</summary>
    private string SpellNames()
    {
        int n = AppraisalHooks.GetObjectSpellIds(_item.Id, _spellBuf, _spellBuf.Length);
        if (n <= 0) return string.Empty;
        if (!PortalSpellTable.Ready) PortalSpellTable.EnsureLoadQueued();
        _sb.Clear();
        int shown = Math.Min(n, _spellBuf.Length);
        for (int k = 0; k < shown; k++)
        {
            // Spell ids in an appraisal can carry the layer in the high word.
            uint sid = _spellBuf[k] & 0xFFFF;
            if (k > 0) _sb.Append('\n');
            _sb.Append(PortalSpellTable.TryGetName(sid, out string name) ? name : "spell " + sid);
        }
        return _sb.ToString();
    }
}

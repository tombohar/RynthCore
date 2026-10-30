// ============================================================================
//  RynthCore.Engine - ImGui/Panels/InventoryFace.AttrFilter.cs
//  The Inventory panel's attribute filter: the funnel-list button in the tools
//  row, its rule editor popup, and the per-item check in the filtered list.
//  Rules and matching: Compatibility/InventoryAttributeFilter.cs; values:
//  Compatibility/InventoryFilterFacts.cs.
//
//  Rules on appraised attributes (armor level, ratings, spells...) can't judge
//  an item the server hasn't identified yet: those items are hidden and
//  counted, and "Identify N" queues them (InventoryModel.QueueIdentify, a few a
//  second on AC's main thread). While such a rule is on, the list is rebuilt
//  once a second so new identifies show up.
//
//  Classic: the filter applies only while the tools row is open (closing the
//  row turns it off, like the search), so nothing hides items unseen.
//  Threads: the panel's (render) thread only.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed partial class InventoryFace
{
    private const string AttrPopupId = "##inv_attrfilter";
    private static readonly long AttrRefreshTicks = Stopwatch.Frequency;   // rebuild while appraisal rules are on

    private InventoryAttributeFilter _attr = new();
    private bool _attrLoaded;
    private readonly InventoryFilterFacts _facts = new();
    // Items the last rebuild hid because they need an identify.
    private readonly List<uint> _unidentified = new();
    private int _builtAttrKey = int.MinValue;
    private long _attrRefreshAt;
    private bool _attrPass;
    private bool _openAttr, _attrPopupOpen, _attrSavePending;
    private string _attrSummary = string.Empty;
    private int _attrSummaryVersion = -1;
    // The text rules' edit buffers, by rule.
    private readonly Dictionary<InventoryFilterRule, byte[]> _attrText = new();

    /// <summary>The attribute filter hides items now (on, has rules and, in Classic, the tools row is open).</summary>
    private bool AttrOn => _attr.Active && (!InventorySettingsStore.Classic || InventorySettingsStore.ShowTools);

    private void LoadAttrFilter()
    {
        if (_attrLoaded) return;
        _attrLoaded = true;
        _attr = InventoryAttributeFilter.Parse(InventorySettingsStore.AttrFilter);
    }

    /// <summary>An appraisal rule is on and a second has passed: rebuild to pick up new identifies.</summary>
    private bool AttrRefreshDue(bool attrOn) =>
        attrOn && _attr.NeedsIdentify && Stopwatch.GetTimestamp() >= _attrRefreshAt;

    /// <summary>Start of a list rebuild.</summary>
    private void BeginAttrPass(bool attrOn)
    {
        _attrPass = attrOn;
        _unidentified.Clear();
        _attrRefreshAt = Stopwatch.GetTimestamp() + AttrRefreshTicks;
    }

    /// <summary>Does the attribute filter keep <paramref name="item"/>? An unknown one is hidden and counted.</summary>
    private bool AttrKeeps(InventoryItem item)
    {
        _facts.Bind(item);
        switch (_attr.Evaluate(_facts))
        {
            case FilterVerdict.Match:
                return true;
            case FilterVerdict.Unknown:
                _unidentified.Add(item.Id);
                return false;
            default:
                return false;
        }
    }

    /// <summary>Records an edit: the list rebuilds now; the rules are saved when the editor closes.</summary>
    private void AttrChanged()
    {
        _attr.Touch();
        _attrSavePending = true;
    }

    private void SetAttrEnabled(bool on)
    {
        if (_attr.Enabled == on) return;
        _attr.Enabled = on;
        _attr.Touch();
        InventorySettingsStore.SetAttrFilter(_attr.Serialize());
    }

    private void SaveAttrIfPending()
    {
        if (!_attrSavePending) return;
        _attrSavePending = false;
        InventorySettingsStore.SetAttrFilter(_attr.Serialize());
    }

    // ── The tools-row button ────────────────────────────────────────────

    private void AttrFilterButton(Vector2 pos)
    {
        bool on = AttrOn;
        if (IconButton("##inv_attr", PhosphorIcons.ListChecks, pos, new Vector2(22, 22), on ? Teal : Mute,
                on ? Selected : BtnFill, border: on ? Teal : 0u, font: UiFont.Ui11))
            _openAttr = true;
        if (!ImGuiNET.ImGui.IsItemHovered()) return;
        if (_attrSummaryVersion != _attr.Version)
        {
            _attrSummaryVersion = _attr.Version;
            _attrSummary = _attr.Rules.Count == 0 ? string.Empty : _attr.Summary();
        }
        string tip = on ? "Attribute filter on: " + _attrSummary
            : _attr.Rules.Count > 0 ? "Attribute filter off (" + _attrSummary + ")"
            : "Filter by item attributes (armor level, ratings, spells, slot...)";
        if (on && _unidentified.Count > 0)
            tip += "\n" + _unidentified.Count.ToString(CultureInfo.InvariantCulture) + " hidden until identified.";
        SafeImGui.SetTooltip(tip);
    }

    // ── The editor popup ────────────────────────────────────────────────

    /// <summary>Once per frame, outside any child.</summary>
    private void AttrFilterDialog()
    {
        if (_openAttr)
        {
            _openAttr = false;
            _attrPopupOpen = true;
            ImGuiNET.ImGui.SetNextWindowPos(ImGuiNET.ImGui.GetMousePos(), ImGuiCond.Always);
            ImGuiNET.ImGui.OpenPopup(AttrPopupId);
        }
        if (!_attrPopupOpen) return;

        PushAttrStyle();
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        bool visible = ImGuiNET.ImGui.BeginPopup(AttrPopupId, ImGuiWindowFlags.AlwaysAutoResize);
        ImGuiNET.ImGui.PopStyleVar();
        if (!visible)
        {
            PopAttrStyle();
            _attrPopupOpen = false;   // clicked outside, or Esc
            SaveAttrIfPending();
            return;
        }
        try
        {
            if (AttrBody())
            {
                ImGuiNET.ImGui.CloseCurrentPopup();
                _attrPopupOpen = false;
                SaveAttrIfPending();
            }
        }
        finally
        {
            ImGuiNET.ImGui.EndPopup();
            PopAttrStyle();
        }
    }

    /// <summary>The popup's contents; true when it should close.</summary>
    private bool AttrBody()
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Amber);
        ImGuiNET.ImGui.TextUnformatted(PhosphorIcons.ListChecks + " Filter by attributes");
        ImGuiNET.ImGui.PopStyleColor();

        bool on = _attr.Enabled;
        if (ImGuiNET.ImGui.Checkbox("Filter on", ref on))
        {
            _attr.Enabled = on;
            AttrChanged();
        }
        ImGuiNET.ImGui.SameLine(0, 16);
        ImGuiNET.ImGui.TextUnformatted("Keep items matching");
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.RadioButton("all rules", !_attr.MatchAny)) { _attr.MatchAny = false; AttrChanged(); }
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.RadioButton("any rule", _attr.MatchAny)) { _attr.MatchAny = true; AttrChanged(); }
        ImGuiNET.ImGui.Separator();

        int remove = -1;
        for (int i = 0; i < _attr.Rules.Count; i++)
        {
            ImGuiNET.ImGui.PushID(i);
            if (RuleRow(_attr.Rules[i])) remove = i;
            ImGuiNET.ImGui.PopID();
        }
        if (remove >= 0)
        {
            _attrText.Remove(_attr.Rules[remove]);
            _attr.Rules.RemoveAt(remove);
            AttrChanged();
        }
        if (_attr.Rules.Count == 0)
        {
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Mute);
            ImGuiNET.ImGui.TextUnformatted("No rules yet. Add one, e.g. Armor level >= 300.");
            ImGuiNET.ImGui.PopStyleColor();
        }
        if (_attr.Rules.Count < InventoryAttributeFilter.MaxRules
            && ImGuiNET.ImGui.Button(PhosphorIcons.Plus + " Add rule"))
        {
            _attr.Rules.Add(InventoryAttributeFilter.NewRule(FilterAttr.ArmorLevel));
            if (_attr.Rules.Count == 1) _attr.Enabled = true;   // the first rule turns the filter on
            AttrChanged();
        }

        IdentifySection();

        ImGuiNET.ImGui.Separator();
        bool close = false;
        if (ImGuiNET.ImGui.Button("Clear rules") && _attr.Rules.Count > 0)
        {
            _attr.Rules.Clear();
            _attrText.Clear();
            AttrChanged();
        }
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.Button("Close")) close = true;
        return close;
    }

    /// <summary>One rule: attribute, operator, value, remove. True when its remove button was pressed.</summary>
    private bool RuleRow(InventoryFilterRule rule)
    {
        FilterAttrInfo info = InventoryAttributeFilter.Info(rule.Attr);

        ImGuiNET.ImGui.SetNextItemWidth(180);
        if (ImGuiNET.ImGui.BeginCombo("##attr", AttrLabel(info)))
        {
            bool first = true;
            foreach (FilterAttrInfo a in InventoryAttributeFilter.Attributes)
            {
                if (a.NeedsId && first)
                {
                    first = false;
                    ImGuiNET.ImGui.Separator();
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Mute);
                    ImGuiNET.ImGui.TextUnformatted("Needs an identify (*)");
                    ImGuiNET.ImGui.PopStyleColor();
                }
                if (ImGuiNET.ImGui.Selectable(AttrLabel(a), a.Id == rule.Attr) && a.Id != rule.Attr)
                {
                    InventoryAttributeFilter.SetAttr(rule, a.Id);
                    AttrChanged();
                }
                SafeImGui.SetItemTooltip(a.Tip);
            }
            ImGuiNET.ImGui.EndCombo();
        }
        SafeImGui.SetItemTooltip(info.Tip);
        info = InventoryAttributeFilter.Info(rule.Attr);

        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.SetNextItemWidth(info.Kind == FilterAttrKind.Text ? 120 : 60);
        if (ImGuiNET.ImGui.BeginCombo("##op", InventoryAttributeFilter.OpLabel(rule.Op)))
        {
            foreach (FilterOp op in InventoryAttributeFilter.OpsFor(info.Kind))
            {
                if (ImGuiNET.ImGui.Selectable(InventoryAttributeFilter.OpLabel(op), op == rule.Op) && op != rule.Op)
                {
                    rule.Op = op;
                    AttrChanged();
                }
            }
            ImGuiNET.ImGui.EndCombo();
        }

        ImGuiNET.ImGui.SameLine();
        switch (info.Kind)
        {
            case FilterAttrKind.Number:
            {
                ImGuiNET.ImGui.SetNextItemWidth(90);
                double v = rule.Number;
                if (ImGuiNET.ImGui.InputDouble("##num", ref v, 0, 0, "%g") && double.IsFinite(v) && v != rule.Number)
                {
                    rule.Number = v;
                    AttrChanged();
                }
                break;
            }
            case FilterAttrKind.Text:
            {
                if (!_attrText.TryGetValue(rule, out byte[]? buf))
                {
                    buf = new byte[64];
                    byte[] src = Encoding.UTF8.GetBytes(rule.Text);
                    Array.Copy(src, buf, Math.Min(src.Length, buf.Length - 1));   // the last byte stays the terminator
                    _attrText[rule] = buf;
                }
                ImGuiNET.ImGui.SetNextItemWidth(150);
                if (ImGuiNET.ImGui.InputText("##text", buf, (uint)buf.Length))
                {
                    rule.Text = Utf8(buf).Trim();
                    AttrChanged();
                }
                break;
            }
            case FilterAttrKind.Choice:
            {
                ImGuiNET.ImGui.SetNextItemWidth(150);
                int c = Math.Clamp(rule.Choice, 0, InventoryAttributeFilter.SlotNames.Length - 1);
                if (ImGuiNET.ImGui.BeginCombo("##choice", InventoryAttributeFilter.SlotNames[c]))
                {
                    for (int k = 0; k < InventoryAttributeFilter.SlotNames.Length; k++)
                    {
                        if (ImGuiNET.ImGui.Selectable(InventoryAttributeFilter.SlotNames[k], k == c) && k != rule.Choice)
                        {
                            rule.Choice = k;
                            AttrChanged();
                        }
                    }
                    ImGuiNET.ImGui.EndCombo();
                }
                break;
            }
            default:
                ImGuiNET.ImGui.Dummy(new Vector2(150, 0));   // a flag has no value; keeps the remove buttons lined up
                break;
        }

        ImGuiNET.ImGui.SameLine();
        bool removeIt = ImGuiNET.ImGui.SmallButton(PhosphorIcons.X);
        ImGuiNET.ImGui.SetItemTooltip("Remove this rule");
        return removeIt;
    }

    private static string AttrLabel(FilterAttrInfo a) => a.NeedsId ? a.Label + " *" : a.Label;

    /// <summary>How many items wait for an identify, and the button that asks for them.</summary>
    private void IdentifySection()
    {
        if (!_attr.NeedsIdentify) return;
        ImGuiNET.ImGui.Separator();
        int queued = InventoryModel.IdentifyQueueCount;
        if (!AttrOn)
        {
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Mute);
            ImGuiNET.ImGui.TextUnformatted(InventorySettingsStore.Classic && !InventorySettingsStore.ShowTools
                ? "Open the tools row to use the filter." : "Turn the filter on to see which items need an identify.");
            ImGuiNET.ImGui.PopStyleColor();
        }
        else if (_unidentified.Count == 0)
        {
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Green);
            ImGuiNET.ImGui.TextUnformatted("Every item the rules need is identified.");
            ImGuiNET.ImGui.PopStyleColor();
        }
        else
        {
            string n = _unidentified.Count.ToString(CultureInfo.InvariantCulture);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Amber);
            ImGuiNET.ImGui.TextUnformatted(n + (_unidentified.Count == 1 ? " item is" : " items are") + " hidden until identified.");
            ImGuiNET.ImGui.PopStyleColor();
            if (ImGuiNET.ImGui.Button("Identify " + n)) InventoryModel.QueueIdentify(_unidentified);
            ImGuiNET.ImGui.SetItemTooltip("Asks the server about them, 4 a second while this panel is open.");
        }
        if (queued > 0)
        {
            if (AttrOn && _unidentified.Count > 0) ImGuiNET.ImGui.SameLine();
            ImGuiNET.ImGui.TextUnformatted(queued.ToString(CultureInfo.InvariantCulture) + " waiting");
            ImGuiNET.ImGui.SameLine();
            if (ImGuiNET.ImGui.SmallButton("Stop")) InventoryModel.ClearIdentifyQueue();
        }
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Mute);
        ImGuiNET.ImGui.TextUnformatted("* needs an identify. Hovering an item also identifies it.");
        ImGuiNET.ImGui.PopStyleColor();
    }

    private static void PushAttrStyle()
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, BtnFill);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Lighten(BtnFill));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.CheckMark, Teal);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Separator, BtnBord);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Button, BtnFill);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Lighten(BtnFill));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 4f);
    }

    private static void PopAttrStyle()
    {
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(9);
    }
}

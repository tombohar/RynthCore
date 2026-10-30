// ============================================================================
//  RynthCore.Engine - UI/Data/MetaRuleDto.cs
//  One meta rule as the Meta panel sees it (RynthPluginGetMetaJson's "rules",
//  add_rule / update_rule's "rule"). Split out of MetaData.cs (2026-10-05) so
//  the offline editor tests (Tools/MetaEditTests) can compile it without the
//  plugin bridge.
// ============================================================================

using System.Collections.Generic;

namespace RynthCore.Engine.UI.Data;

internal sealed class MetaRuleDto
{
    public string State { get; set; } = "Default";
    public int Condition { get; set; }
    public string ConditionData { get; set; } = string.Empty;
    public int Action { get; set; }
    public string ActionData { get; set; } = string.Empty;
    public List<MetaRuleDto> Children { get; set; } = new();
    public List<MetaRuleDto> ActionChildren { get; set; } = new();
    public bool Enabled { get; set; } = true;
    /// <summary>A name to find the rule by (top-level rules; "" = none). RynthAi saves it in .af as a
    /// "~~ @name ..." line; one without rule names leaves it out of the snapshot ("").</summary>
    public string Name { get => _name; set => _name = value ?? string.Empty; }   // a JSON null is ""
    private string _name = string.Empty;
    public long LastFiredMs { get; set; } = 99999;

    public MetaRuleDto Clone()
    {
        var r = new MetaRuleDto
        {
            State = State, Condition = Condition, ConditionData = ConditionData,
            Action = Action, ActionData = ActionData, Enabled = Enabled, Name = Name, LastFiredMs = LastFiredMs,
        };
        foreach (MetaRuleDto c in Children) r.Children.Add(c.Clone());
        foreach (MetaRuleDto a in ActionChildren) r.ActionChildren.Add(a.Clone());
        return r;
    }
}

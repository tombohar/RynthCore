// ============================================================================
//  RynthCore.Engine - UI/Data/LootEditData.cs
//  RynthAi's loot profile editor state for the ImGui Loot Editor face
//  (docs/IMGUI_LOOT_EDITOR.md). RynthAi owns the data (LootSdk's
//  LootEditSession); these DTOs mirror RynthSuite's Shared/RynthCore.LootSdk/
//  Editing/LootEditWire.cs - keep the two in step. The engine does not
//  reference LootSdk.
//
//  LootEditSource (pump thread, after the plugin tick) asks for the cheap
//  revision every second and fetches the rule list only when it moved; the
//  full rule the face has open comes from a second export, the pickers' names
//  from a third (once per plugin load). Strings are UTF-8 both ways.
//  LootEditCommands posts edits through the hub; the plugin applies them at
//  once, so the same hub step's poll already shows the result.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Data;

internal sealed class LootEditStateDto
{
    public long Revision { get; set; }
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public bool ReadOnly { get; set; }
    public string ReadOnlyReason { get; set; } = string.Empty;
    public bool Dirty { get; set; }
    public bool ChangedOnDisk { get; set; }
    public string InUsePath { get; set; } = string.Empty;
    public bool InUse { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool MessageOk { get; set; } = true;
    public long MessageSeq { get; set; }
    public int Focus { get; set; } = -1;
    public List<LootEditFileDto> Files { get; set; } = new();
    public List<LootEditRowDto> Rules { get; set; } = new();
    /// <summary>"Add to loot profile" popup's rule (added 2026-10); null from an older RynthAi.</summary>
    public LootEditItemDraftDto? ItemDraft { get; set; }
}

internal sealed class LootEditFileDto
{
    public string Path { get; set; } = string.Empty;
    public string Display { get; set; } = string.Empty;
}

internal sealed class LootEditRowDto
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Action { get; set; }
    public int KeepCount { get; set; }
    public string Summary { get; set; } = string.Empty;
    public int Conditions { get; set; }
}

internal sealed class LootEditRuleDto
{
    public int Index { get; set; } = -1;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Action { get; set; } = 1;
    public int KeepCount { get; set; }
    public int Priority { get; set; }
    public string? CustomExpression { get; set; }
    public List<LootEditConditionDto> Conditions { get; set; } = new();

    public LootEditRuleDto Clone()
    {
        var r = (LootEditRuleDto)MemberwiseClone();
        r.Conditions = new List<LootEditConditionDto>(Conditions.Count);
        foreach (LootEditConditionDto c in Conditions) r.Conditions.Add(c.Clone());
        return r;
    }

    /// <summary>Same content (the rule view's "edited?" test).</summary>
    public bool SameAs(LootEditRuleDto o)
    {
        if (Name != o.Name || Enabled != o.Enabled || Action != o.Action || Priority != o.Priority
            || CustomExpression != o.CustomExpression || Conditions.Count != o.Conditions.Count)
            return false;
        if (Action == LootEditVocabulary.KeepUpTo && KeepCount != o.KeepCount) return false;
        for (int i = 0; i < Conditions.Count; i++)
            if (!Conditions[i].SameAs(o.Conditions[i])) return false;
        return true;
    }
}

internal sealed class LootEditConditionDto
{
    public int NodeType { get; set; }
    public string LengthCode { get; set; } = "0";
    public List<string> Lines { get; set; } = new();

    public LootEditConditionDto Clone() => new() { NodeType = NodeType, LengthCode = LengthCode, Lines = new List<string>(Lines) };

    public bool SameAs(LootEditConditionDto o)
    {
        if (NodeType != o.NodeType || LengthCode != o.LengthCode || Lines.Count != o.Lines.Count) return false;
        for (int i = 0; i < Lines.Count; i++) if (Lines[i] != o.Lines[i]) return false;
        return true;
    }
}

internal sealed class LootEditCmd
{
    public string Op { get; set; } = string.Empty;
    public int Index { get; set; } = -1;
    public int To { get; set; } = -1;
    public string Value { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? Expect { get; set; }
    public bool Force { get; set; }
    public LootEditRuleDto? Rule { get; set; }
    /// <summary>item_preview / item_add (added 2026-10).</summary>
    public LootEditItemRequestDto? Item { get; set; }
}

/// <summary>"Add to loot profile": the popup's ask (LootEditWire's LootEditItemRequest).</summary>
internal sealed class LootEditItemRequestDto
{
    /// <summary>0 = the item selected in the game.</summary>
    public uint ItemId { get; set; }
    /// <summary>0 name + class, 1 name only, 2 items like this.</summary>
    public int Match { get; set; }
    /// <summary>VTank action code; 0 = the item's default.</summary>
    public int Action { get; set; }
    /// <summary>-1 = default (one full stack).</summary>
    public int KeepCount { get; set; } = -1;
    public string RuleName { get; set; } = string.Empty;
    /// <summary>True: the profile open in the Loot Editor; false: the one RynthAi loots with.</summary>
    public bool ToOpenProfile { get; set; }
    /// <summary>Also require the item's T11 attributes (added 2026-10-07; an older RynthAi ignores it).</summary>
    public bool IncludeT11 { get; set; }
    /// <summary>The attribute checklist's choices (added 2026-10-18); null: every row at its default.</summary>
    public List<LootEditItemChoiceDto>? Choices { get; set; }
    public int Seq { get; set; }
}

/// <summary>One checklist row as the player set it (LootEditWire's LootEditItemChoice).</summary>
internal sealed class LootEditItemChoiceDto
{
    public string Id { get; set; } = string.Empty;
    public bool On { get; set; }
    /// <summary>A comparison code (LootAttrOps); -1 = the row's default.</summary>
    public int Op { get; set; } = -1;
    /// <summary>The typed value; empty = the item's own.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>One row of the popup's attribute checklist (LootEditWire's LootEditItemAttribute).</summary>
internal sealed class LootEditItemAttributeDto
{
    public string Id { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string ItemValue { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public int Op { get; set; }
    public List<int> Ops { get; set; } = new();
    public bool Editable { get; set; }
    public bool On { get; set; }
    public string Tip { get; set; } = string.Empty;
}

/// <summary>The checklist's comparison codes (LootSdk LootItemAttributeOp) and how the popup labels them.</summary>
internal static class LootAttrOps
{
    public const int AtLeast = 0, Exactly = 1, AtMost = 2, Not = 3, SpellExact = 10, SpellOrBetter = 11, SpellAnyTier = 12;

    public static string Label(int op) => op switch
    {
        AtLeast => ">=",
        Exactly => "=",
        AtMost => "<=",
        Not => "not",
        SpellExact => "exact",
        SpellOrBetter => "or better",
        SpellAnyTier => "any tier",
        _ => "?",
    };
}

/// <summary>The rule an item makes and what an add did (LootEditWire's LootEditItemDraft).</summary>
internal sealed class LootEditItemDraftDto
{
    public int Seq { get; set; }
    public bool Ok { get; set; }
    public string Error { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public bool Stackable { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public string TargetFile { get; set; } = string.Empty;
    public bool TargetInUse { get; set; }
    public string Format { get; set; } = string.Empty;
    public int Match { get; set; }
    /// <summary>The rule includes the item's T11 attributes (added 2026-10-07; false from an older RynthAi).</summary>
    public bool IncludeT11 { get; set; }
    /// <summary>The item is T11 gear (added 2026-10-07; false from an older RynthAi).</summary>
    public bool IsT11 { get; set; }
    public int Action { get; set; }
    public int KeepCount { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public string DefaultRuleName { get; set; } = string.Empty;
    public List<string> Preview { get; set; } = new();
    public int InsertAt { get; set; } = -1;
    public int RuleCount { get; set; }
    public string OrderNote { get; set; } = string.Empty;
    public List<string> Notes { get; set; } = new();
    /// <summary>RynthAi has the attribute checklist (added 2026-10-18; false from an older one: the T11 box shows instead).</summary>
    public bool AttributesSupported { get; set; }
    /// <summary>The item's checklist rows in display order (added 2026-10-18).</summary>
    public List<LootEditItemAttributeDto> Attributes { get; set; } = new();
    public bool Added { get; set; }
    public string Message { get; set; } = string.Empty;
}

internal sealed class LootEditVocabDto
{
    public List<LootEditNameDto> Actions { get; set; } = new();
    public List<LootEditNodeTypeDto> NodeTypes { get; set; } = new();
    public List<LootEditNameDto> ObjectClasses { get; set; } = new();
    public List<LootEditNameDto> LongKeys { get; set; } = new();
    public List<LootEditNameDto> DoubleKeys { get; set; } = new();
    public List<LootEditNameDto> StringKeys { get; set; } = new();
    public List<LootEditNameDto> Skills { get; set; } = new();
    /// <summary>Names for long keys' values (added 2026-10); empty from an older RynthAi (the value stays a text box).</summary>
    public List<LootEditValueTableDto> LongValueTables { get; set; } = new();
}

internal sealed class LootEditNameDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

internal sealed class LootEditValueTableDto
{
    public int Key { get; set; }
    public bool Flags { get; set; }
    public List<LootEditNameDto> Values { get; set; } = new();
}

internal sealed class LootEditNodeTypeDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Lines { get; set; }
    public List<string> Labels { get; set; } = new();
    public List<string> Defaults { get; set; } = new();
    public string Editor { get; set; } = "raw";
    public string KeyTable { get; set; } = string.Empty;
    /// <summary>keyval "long": pick the value by name when the key has a LongValueTables entry (added 2026-10).</summary>
    public bool NamedValues { get; set; }
}

[JsonSerializable(typeof(LootEditStateDto))]
[JsonSerializable(typeof(LootEditRuleDto))]
[JsonSerializable(typeof(LootEditCmd))]
[JsonSerializable(typeof(LootEditVocabDto))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false)]
internal partial class LootEditJsonContext : JsonSerializerContext { }

/// <summary>What the face reads. Immutable once published (the face clones what it edits).</summary>
internal sealed class LootEditView
{
    /// <summary>RynthAi isn't loaded, or is too old to have the loot editor exports.</summary>
    public bool Unavailable { get; init; }
    public LootEditStateDto? State { get; init; }
    /// <summary>The rule asked for with LootEditSource.WantRule, fetched at State's revision; null if none.</summary>
    public LootEditRuleDto? Rule { get; init; }
    public LootEditVocabDto? Vocab { get; init; }
}

/// <summary>The loot editor's state while the face is open (revision each second, the list when it changes).</summary>
internal sealed unsafe class LootEditSource : UiSource<LootEditView>
{
    private delegate* unmanaged[Cdecl]<int> _revision;
    private delegate* unmanaged[Cdecl]<IntPtr> _getState, _getVocab;
    private delegate* unmanaged[Cdecl]<int, IntPtr> _getRule;
    private bool _bound;
    private int _lastRevision;
    private bool _haveState;
    private LootEditStateDto? _state;
    private LootEditVocabDto? _vocab;
    private LootEditRuleDto? _rule;
    private int _ruleIndex = -1, _ruleRevision;
    private int _wantRule = -1;

    public LootEditSource() : base("LootEdit", periodMs: 1000) { }

    /// <summary>The rule the face has open (-1: none). Any thread.</summary>
    public void WantRule(int index)
    {
        if (Interlocked.Exchange(ref _wantRule, index) != index) RequestRefresh();
    }

    protected internal override void Poll()
    {
        if (!_bound)
        {
            _revision = (delegate* unmanaged[Cdecl]<int>)PluginExportBinder.Resolve("RynthAi", "RynthPluginLootEditRevision");
            _getState = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthAi", "RynthPluginGetLootEditJson");
            _getRule = (delegate* unmanaged[Cdecl]<int, IntPtr>)PluginExportBinder.Resolve("RynthAi", "RynthPluginGetLootEditRuleJson");
            _getVocab = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthAi", "RynthPluginGetLootEditVocabJson");
            _bound = _revision != null && _getState != null && _getRule != null && _getVocab != null;
            if (!_bound)
            {
                if (Current == null || !Current.Value.Unavailable) Publish(new LootEditView { Unavailable = true });
                return;
            }
        }

        bool changed = false;
        if (_vocab == null)
        {
            _vocab = Parse(_getVocab(), LootEditJsonContext.Default.LootEditVocabDto);
            changed |= _vocab != null;
        }

        int revision = _revision();
        if (!_haveState || revision != _lastRevision)
        {
            LootEditStateDto? state = Parse(_getState(), LootEditJsonContext.Default.LootEditStateDto);
            if (state != null)
            {
                _state = state;
                _haveState = true;
                // The list call may have opened the profile (first look): the state's own revision is current.
                _lastRevision = unchecked((int)state.Revision);
                changed = true;
            }
        }

        int want = Volatile.Read(ref _wantRule);
        if (want != _ruleIndex || (want >= 0 && _ruleRevision != _lastRevision))
        {
            _rule = want < 0 ? null : Parse(_getRule(want), LootEditJsonContext.Default.LootEditRuleDto);
            if (_rule != null && _rule.Index < 0) _rule = null;
            _ruleIndex = want;
            _ruleRevision = _lastRevision;
            changed = true;
        }

        if (changed) Publish(new LootEditView { State = _state, Rule = _rule, Vocab = _vocab });
    }

    private static T? Parse<T>(IntPtr utf8, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        if (utf8 == IntPtr.Zero) return null;
        string? json = Marshal.PtrToStringUTF8(utf8);
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize(json, info); }
        catch (JsonException ex)
        {
            RynthLog.UI($"LootEditSource: bad JSON from RynthAi ({typeof(T).Name}): {ex.Message}");
            return null;
        }
    }

    protected internal override void Reset()
    {
        _bound = false;
        _revision = null;
        _getState = null;
        _getVocab = null;
        _getRule = null;
        _haveState = false;
        _state = null;
        _vocab = null;
        _rule = null;
        _ruleIndex = -1;
        ClearSnapshot();
    }
}

/// <summary>RynthPluginSendLootEditCommand on the pump thread. Any thread may call Send.</summary>
internal static unsafe class LootEditCommands
{
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _send;

    static LootEditCommands()
    {
        PluginManager.PluginsUnloaded += () => _send = null;
    }

    /// <summary><paramref name="cmd"/> (and its Rule) must not be changed after this call.</summary>
    public static void Send(LootEditCmd cmd)
    {
        UiDataHub.Post("LootEdit " + cmd.Op, () =>
        {
            if (_send == null)
                _send = (delegate* unmanaged[Cdecl]<IntPtr, void>)PluginExportBinder.Resolve("RynthAi", "RynthPluginSendLootEditCommand");
            if (_send == null) return;
            string json = JsonSerializer.Serialize(cmd, LootEditJsonContext.Default.LootEditCmd);
            IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(json);
            try { _send(utf8); }
            finally { Marshal.FreeCoTaskMem(utf8); }
            UiSources.LootEdit.RequestRefresh();
        });
    }

    public static void Simple(string op, int index = -1, string value = "", string path = "", string? expect = null,
        bool force = false, int to = -1)
        => Send(new LootEditCmd { Op = op, Index = index, Value = value, Path = path, Expect = expect, Force = force, To = to });
}

/// <summary>Action codes and badge text shared by the face.</summary>
internal static class LootEditVocabulary
{
    public const int Keep = 1, Salvage = 2, Sell = 3, Read = 4, KeepUpTo = 10;

    /// <summary>The action filter's chips: 0 = all.</summary>
    public static readonly int[] FilterActions = { 0, Keep, KeepUpTo, Salvage, Sell, Read };
    public static readonly string[] FilterLabels = { "All", "Keep", "Keep #", "Salvage", "Sell", "Read" };

    public static string Badge(int action, int keepCount) => action switch
    {
        Keep => "KEEP",
        KeepUpTo => "KEEP " + keepCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Salvage => "SALVAGE",
        Sell => "SELL",
        Read => "READ",
        _ => "?",
    };

    /// <summary>Badge fill, ARGB (the external editor's colours).</summary>
    public static uint BadgeArgb(int action) => action switch
    {
        Keep => 0xFF1F6F3A,
        KeepUpTo => 0xFF196E6E,
        Salvage => 0xFFA35C15,
        Sell => 0xFF7A1F1F,
        Read => 0xFF1F497D,
        _ => 0xFF444444,
    };

    public const int DisabledRule = 9999;
}

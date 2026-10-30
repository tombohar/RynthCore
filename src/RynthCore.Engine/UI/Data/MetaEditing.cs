// ============================================================================
//  RynthCore.Engine - UI/Data/MetaEditing.cs
//  The Meta editor's rules that don't need ImGui (2026-10-05 clean-up), kept
//  here so Tools/MetaEditTests can check them offline:
//
//   MetaStateName   what a typed "+ New state..." name must be
//   MetaAutosave    when an edit is saved (debounce; never mid-typing)
//   MetaBackupPlan  the one .bak per editing session, before the first save
//   MetaRuleEdit    placeholder rule for a new state, Add condition / action,
//                   finding the edited rule again in a newer snapshot
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Engine.UI.Data;

/// <summary>
/// A new meta state's name. RynthAi matches state names ignoring case (MetaManager), so
/// "combat" next to "Combat" would be the same state: refused as a duplicate. The .af file writes
/// <c>STATE: {name}</c> and <c>SetState {name}</c>, so braces can't be in a name.
/// </summary>
internal static class MetaStateName
{
    public const int MaxLength = 48;

    /// <summary>Null when <paramref name="typed"/> (trimmed into <paramref name="name"/>) can be a new
    /// state, else a short reason to show under the box.</summary>
    public static string? Problem(string? typed, IEnumerable<string> existing, out string name)
    {
        name = (typed ?? string.Empty).Trim();
        if (name.Length == 0) return "Type a name for the new state.";
        if (name.Length > MaxLength) return $"At most {MaxLength} characters.";
        foreach (char c in name)
        {
            if (c is '{' or '}') return "A state name can't contain { or }.";
            if (char.IsControl(c)) return "A state name can't contain tabs or line breaks.";
        }
        foreach (string s in existing)
            if (string.Equals(s, name, StringComparison.OrdinalIgnoreCase))
                return $"There is already a state called '{s}'.";
        return null;
    }
}

/// <summary>
/// When the rule being edited is saved. Every change restarts a <see cref="DebounceMs"/> clock;
/// the change is saved once the clock runs out, but never while a text box is being typed in, so
/// a half-typed chat command or regex is not saved (and, on a running meta, can't fire) before the
/// user leaves the box. Back, closing the panel and switching metas save at once (<see cref="Take"/>).
/// </summary>
internal sealed class MetaAutosave
{
    public const double DebounceMs = 750;

    private bool _dirty;
    private double _lastChangeMs;

    /// <summary>An edit that is not saved yet.</summary>
    public bool Dirty => _dirty;

    public void Changed(double nowMs)
    {
        _dirty = true;
        _lastChangeMs = nowMs;
    }

    /// <summary>True when the pending edit should be saved now.</summary>
    public bool Due(double nowMs, bool typing) => _dirty && !typing && nowMs - _lastChangeMs >= DebounceMs;

    /// <summary>Takes the pending edit for saving (a due one, or a flush). True when there was one.</summary>
    public bool Take()
    {
        bool had = _dirty;
        _dirty = false;
        return had;
    }

    /// <summary>Drops the pending edit (the rule was deleted or the draft discarded).</summary>
    public void Clear() => _dirty = false;
}

/// <summary>
/// One backup per meta file per editing session (the panel being open): before the first save
/// that would rewrite a file this session, copy it to <c>&lt;file&gt;.bak</c> (overwriting the last
/// session's). RynthAi saves a .met meta as the .af beside it (the .met is never written), so the
/// file to back up is that .af, if it exists yet.
/// </summary>
internal sealed class MetaBackupPlan
{
    private readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The panel was opened: the next save of each file backs it up again.</summary>
    public void NewSession() => _done.Clear();

    /// <summary>The file a save to <paramref name="metaPath"/> writes, and its backup, when this session
    /// hasn't backed it up yet; null otherwise (or when the meta has no file).</summary>
    public (string File, string Backup)? Before(string? metaPath)
    {
        if (string.IsNullOrWhiteSpace(metaPath)) return null;
        string file = WrittenFile(metaPath);
        if (!_done.Add(file)) return null;
        return (file, BackupOf(file));
    }

    /// <summary>The file RynthAi writes when it saves the meta loaded from <paramref name="metaPath"/>.</summary>
    public static string WrittenFile(string metaPath) =>
        metaPath.EndsWith(".met", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.ChangeExtension(metaPath, ".af") : metaPath;

    public static string BackupOf(string file) => file + ".bak";
}

/// <summary>Rule edits the editor makes.</summary>
internal static class MetaRuleEdit
{
    public const int Never = 0, All = 2, Any = 3, Not = 20;
    public const int NoAction = 0, DoAll = 4;
    /// <summary>"No Kills Within Minutes" (RynthAi MetaConditionType.NoKillsWithinMinutes, .af NoKillsInMins).</summary>
    public const int NoKillsWithinMinutes = 35;

    /// <summary>The minutes box's tooltip: what the condition means, or why the typed value never holds.</summary>
    public static string NoKillsTooltip(string data)
    {
        const string what = "True once you haven't killed anything for this many minutes (decimals allowed: 0.5 = 30 s).\n"
            + "Before your first kill it counts from the macro start (or login). Only your own kills count.\n"
            + "RynthAi only: VTank / metaf can't read it. Same clock as secondssincelastkill[].";
        string t = data.Trim();
        if (t.Length == 0) return what;
        return MinutesOk(t) ? what : $"'{t}' isn't a number of minutes: this condition never holds.\n\n{what}";
    }

    /// <summary>The plugin's reading of the minutes (KillClock.TryParseMinutes): a number, not negative;
    /// a comma decimal is accepted.</summary>
    public static bool MinutesOk(string data) =>
        double.TryParse(data.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double m)
        && !double.IsNaN(m) && !double.IsInfinity(m) && m >= 0;

    /// <summary>The rule a new state starts with: Never -> None, which never fires. A state exists only
    /// while a rule uses it (RynthAi lists the states its rules name, and the .af file writes one
    /// STATE: block per state that has rules), so this rule is what keeps an empty state.</summary>
    public static MetaRuleDto Placeholder(string state) => new() { State = state, Condition = Never, Action = NoAction };

    /// <summary>The longest rule name the editor takes.</summary>
    public const int MaxNameLength = 80;

    /// <summary>A typed rule name: one line (RynthAi writes it as a "~~ @name" comment line), no longer
    /// than <see cref="MaxNameLength"/>. The editor trims the ends when it sends the rule.</summary>
    public static string CleanName(string? typed)
    {
        if (string.IsNullOrEmpty(typed)) return string.Empty;
        string s = typed.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return s.Length > MaxNameLength ? s.Substring(0, MaxNameLength) : s;
    }

    public static bool IsPlaceholder(MetaRuleDto r) =>
        r.Condition == Never && r.Action == NoAction && r.ConditionData.Length == 0 && r.ActionData.Length == 0
        && r.Children.Count == 0 && r.ActionChildren.Count == 0 && r.Name.Length == 0;

    /// <summary>False for conditions with nothing to type: Never, Always, Character Death, Any Vendor Open,
    /// Vendor Closed, Need To Buff, Portalspace Entered / Exited, Navroute Empty (and the composites).</summary>
    public static bool ConditionTakesData(int condition) =>
        condition is not (Never or 1 or 7 or 8 or 9 or 14 or 18 or 19 or 28 or All or Any or Not);

    /// <summary>False for actions with nothing to type: None, Return From Call, Clear Watchdog,
    /// Destroy All Views (and DoAll, which has sub-actions).</summary>
    public static bool ActionTakesData(int action) => action is not (NoAction or 6 or 10 or 15 or DoAll);

    /// <summary>
    /// A new rule is added by autosave only once it is filled in: every condition and action that
    /// takes a value has one, and every All / Any / Not / DoAll has something in it. Otherwise a
    /// rule built condition-first ("Always", then the chat command) would be added, and on a running
    /// meta fire and latch, before its command was typed. Back / close add it as it is.
    /// </summary>
    public static bool ReadyToAdd(MetaRuleDto r) => ConditionReady(r, 0) && ActionReady(r);

    private static bool ConditionReady(MetaRuleDto c, int depth)
    {
        if (c.Condition is All or Any or Not)
        {
            if (c.Children.Count == 0 || depth > 8) return false;
            foreach (MetaRuleDto child in c.Children)
                if (!ConditionReady(child, depth + 1)) return false;
            return true;
        }
        return !ConditionTakesData(c.Condition) || c.ConditionData.Trim().Length > 0;
    }

    private static bool ActionReady(MetaRuleDto a)
    {
        if (a.Action == DoAll)
        {
            if (a.ActionChildren.Count == 0) return false;
            foreach (MetaRuleDto child in a.ActionChildren)
                if (!ActionReady(child)) return false;
            return true;
        }
        return !ActionTakesData(a.Action) || a.ActionData.Trim().Length > 0;
    }

    /// <summary>A Not already has its one condition.</summary>
    public static bool CanAddCondition(MetaRuleDto r) => r.Condition != Not || r.Children.Count == 0;

    /// <summary>
    /// "+ Add" in the Conditions column: All / Any get one more sub-condition (Never until it is
    /// picked, so a half-made rule can't fire); an empty Not gets its operand; any other condition
    /// becomes the first of an All, followed by the new one.
    /// </summary>
    public static void AddCondition(MetaRuleDto r)
    {
        if (r.Condition is All or Any || (r.Condition == Not && r.Children.Count == 0))
        {
            r.Children.Add(new MetaRuleDto { State = r.State, Condition = Never });
            return;
        }
        if (r.Condition == Not) return;
        var first = new MetaRuleDto { State = r.State, Condition = r.Condition, ConditionData = r.ConditionData };
        r.Condition = All;
        r.ConditionData = string.Empty;
        r.Children.Clear();
        r.Children.Add(first);
        r.Children.Add(new MetaRuleDto { State = r.State, Condition = Never });
    }

    /// <summary>"+ Add" in the Actions column: a DoAll gets one more sub-action (None until picked);
    /// any other action becomes the first of a DoAll, followed by the new one.</summary>
    public static void AddAction(MetaRuleDto r)
    {
        if (r.Action == DoAll)
        {
            r.ActionChildren.Add(new MetaRuleDto { State = r.State, Action = NoAction });
            return;
        }
        var first = new MetaRuleDto { State = r.State, Action = r.Action, ActionData = r.ActionData };
        r.Action = DoAll;
        r.ActionData = string.Empty;
        r.ActionChildren.Clear();
        r.ActionChildren.Add(first);
        r.ActionChildren.Add(new MetaRuleDto { State = r.State, Action = NoAction });
    }

    /// <summary>Same state, name, condition, action, data, enabled flag and children (not LastFiredMs).</summary>
    public static bool SameContent(MetaRuleDto a, MetaRuleDto b)
    {
        if (!string.Equals(a.State, b.State, StringComparison.Ordinal) || a.Condition != b.Condition || a.Action != b.Action
            || a.Enabled != b.Enabled || !string.Equals(a.Name, b.Name, StringComparison.Ordinal)
            || !string.Equals(a.ConditionData, b.ConditionData, StringComparison.Ordinal)
            || !string.Equals(a.ActionData, b.ActionData, StringComparison.Ordinal)
            || a.Children.Count != b.Children.Count || a.ActionChildren.Count != b.ActionChildren.Count)
            return false;
        for (int i = 0; i < a.Children.Count; i++)
            if (!SameContent(a.Children[i], b.Children[i])) return false;
        for (int i = 0; i < a.ActionChildren.Count; i++)
            if (!SameContent(a.ActionChildren[i], b.ActionChildren[i])) return false;
        return true;
    }

    /// <summary>
    /// Where the rule last saved as <paramref name="lastSaved"/> is in <paramref name="rules"/> (a newer
    /// snapshot): <paramref name="hint"/> when it is still there, else the nearest rule with the same
    /// content, else <paramref name="hint"/> (the snapshot doesn't show the save yet). Commands address
    /// rules by index, so a save must not land on a rule that moved into the old slot.
    /// </summary>
    public static int Locate(IReadOnlyList<MetaRuleDto> rules, MetaRuleDto lastSaved, int hint)
    {
        if (hint >= 0 && hint < rules.Count && SameContent(rules[hint], lastSaved)) return hint;
        int best = -1;
        for (int i = 0; i < rules.Count; i++)
            if (SameContent(rules[i], lastSaved) && (best < 0 || Math.Abs(i - hint) < Math.Abs(best - hint)))
                best = i;
        return best >= 0 ? best : hint;
    }
}

// ============================================================================
//  RynthCore.Engine - Compatibility/AcKeymapText.cs
//
//  Changing a key in AC's keymap file, as text: the pure part (no engine
//  dependencies, so tools/KeymapTests compiles it as is with Hotkeys/
//  HotkeyModel.cs). AC reads the file when it starts (gmClient::InitKeymap)
//  and writes its keys back when it closes (gmClient::Cleanup); RynthCore
//  changes a binding by editing the file and having every running client load
//  it again (AcKeymapLive), so a key changed on the item bar is the key the
//  retail toolbar and every client answer to.
//
//    Bindings
//    [
//      QuickslotCommands
//      [
//        UseQuickSlot_1 [ "" [ 0 DIK_1 ] ]
//        UseQuickSlot_1 [ "" [ 0 DIK_1 ] 0x00000002 ]
//      ]
//    ]
//
//  A binding takes its key, as AC's Configure Keyboard does: the same key with
//  the same modifiers comes off every other action in the groups the game
//  reads while playing. The text-editing groups keep theirs (a chat box still
//  has Home and Ctrl+C). The key names written are the ones AC writes itself
//  (DIK_PGUP, DIK_UPARROW...), all in its own name table; any name it accepts
//  is matched (DIK_PRIOR is DIK_PGUP).
//
//  A change every client makes (2026-10-27: a player's own Configure Keyboard
//  keys reverted mid-fight when another client changed one): the client that
//  changes a key records the edits it made (Edit, Change) in "<keymap>.
//  rynthcore"; every other client makes the same edits to its OWN keys (AC
//  writes them out, ApplyChanges, AC loads them), never to the shared file's.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RynthCore.Engine.Hotkeys;

namespace RynthCore.Engine.Compatibility;

internal static class AcKeymapText
{
    /// <summary>One binding line: where it is, its group and action, the device (0 keyboard), the key (canonical DIK name), the modifier mask.</summary>
    internal readonly record struct Line(int Index, string Group, string Action, int Device, string Dik, uint Meta);

    /// <summary>Groups for typing: a key bound there never conflicts with one the game uses while playing.</summary>
    public static readonly string[] TextGroups = { "EditControls", "CopyAndPasteControls", "DialogBoxes", "ScrollableControls" };

    /// <summary>The group a new key for an action no group has yet goes in.</summary>
    public static string? DefaultGroup(string action) =>
        action.StartsWith("UseQuickSlot_", StringComparison.OrdinalIgnoreCase) ? "QuickslotCommands" : null;

    // Action [ "name" [ device KEY sub? ] mask? activation? ]
    private static readonly Regex BindingLine = new(
        @"^\s*([A-Za-z0-9_]+)\s*\[\s*""[^""]*""\s*\[\s*(\d+)\s+([A-Za-z0-9_]+)[^\]]*\]\s*(0x[0-9A-Fa-f]+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // n [ device KEY ]
    private static readonly Regex MetaLine = new(@"^\s*(\d+)\s*\[\s*\d+\s+([A-Za-z0-9_]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Name = new(@"^[A-Za-z0-9_]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ── Text in and out ─────────────────────────────────────────────────────

    /// <summary>The file's lines and its line ending ("\r\n" as AC writes it).</summary>
    public static List<string> Split(string text, out string newline)
    {
        newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = new List<string>(text.Split('\n'));
        for (int i = 0; i < lines.Count; i++) lines[i] = lines[i].TrimEnd('\r');
        return lines;
    }

    public static string Join(List<string> lines, string newline) => string.Join(newline, lines);

    // ── Key names ───────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DIK_PRIOR"] = "DIK_PGUP", ["DIK_NEXT"] = "DIK_PGDN", ["DIK_UP"] = "DIK_UPARROW", ["DIK_DOWN"] = "DIK_DOWNARROW",
        ["DIK_RIGHT"] = "DIK_RIGHTARROW", ["DIK_LEFTARROW"] = "DIK_LEFT", ["DIK_BACKSPACE"] = "DIK_BACK",
        ["DIK_CAPSLOCK"] = "DIK_CAPITAL", ["DIK_LALT"] = "DIK_LMENU", ["DIK_RALT"] = "DIK_RMENU",
        ["DIK_MULTIPLY"] = "DIK_NUMPADSTAR", ["DIK_DIVIDE"] = "DIK_NUMPADSLASH", ["DIK_ADD"] = "DIK_NUMPADPLUS",
        ["DIK_SUBTRACT"] = "DIK_NUMPADMINUS", ["DIK_NUMPADPERIOD"] = "DIK_DECIMAL",
    };

    /// <summary>One name per key (upper case): DIK_PRIOR and DIK_PGUP are both DIK_PGUP.</summary>
    public static string Canonical(string dik)
    {
        string d = dik.Trim().ToUpperInvariant();
        return Aliases.TryGetValue(d, out string? c) ? c : d;
    }

    /// <summary>
    /// The DirectInput name AC writes for a Windows virtual key (the engine's hotkey keys), or null for
    /// one AC's keymap can't hold (F16 and up).
    /// </summary>
    public static string? DikForVk(int vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return "DIK_" + (char)vk;
        if (vk is >= 0x70 and <= 0x7E) return "DIK_F" + (vk - 0x70 + 1);            // F1-F15
        if (vk is >= 0x60 and <= 0x69) return "DIK_NUMPAD" + (vk - 0x60);           // Num0-Num9
        return vk switch
        {
            0x6A => "DIK_NUMPADSTAR", 0x6B => "DIK_NUMPADPLUS", 0x6D => "DIK_NUMPADMINUS", 0x6E => "DIK_DECIMAL", 0x6F => "DIK_NUMPADSLASH",
            0x08 => "DIK_BACK", 0x09 => "DIK_TAB", 0x0D => "DIK_RETURN", 0x13 => "DIK_PAUSE", 0x20 => "DIK_SPACE",
            0x21 => "DIK_PGUP", 0x22 => "DIK_PGDN", 0x23 => "DIK_END", 0x24 => "DIK_HOME",
            0x25 => "DIK_LEFT", 0x26 => "DIK_UPARROW", 0x27 => "DIK_RIGHTARROW", 0x28 => "DIK_DOWNARROW",
            0x2D => "DIK_INSERT", 0x2E => "DIK_DELETE", 0x91 => "DIK_SCROLL",
            0xBA => "DIK_SEMICOLON", 0xBB => "DIK_EQUALS", 0xBC => "DIK_COMMA", 0xBD => "DIK_MINUS", 0xBE => "DIK_PERIOD",
            0xBF => "DIK_SLASH", 0xC0 => "DIK_GRAVE", 0xDB => "DIK_LBRACKET", 0xDC => "DIK_BACKSLASH", 0xDD => "DIK_RBRACKET",
            0xDE => "DIK_APOSTROPHE",
            _ => null,
        };
    }

    // ── Reading ─────────────────────────────────────────────────────────────

    /// <summary>The MetaKeys block: each modifier key (canonical name) and its number (its bit is 1 &lt;&lt; (n - 1)).</summary>
    public static Dictionary<string, int> MetaKeys(List<string> lines)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string section = string.Empty;
        foreach (string raw in lines)
        {
            string t = raw.Trim();
            if (t is "Devices" or "MetaKeys" or "Bindings") { section = t; continue; }
            if (section != "MetaKeys") continue;
            Match m = MetaLine.Match(t);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n is >= 1 and <= 32)
                map.TryAdd(Canonical(m.Groups[2].Value), n);
        }
        return map;
    }

    /// <summary>
    /// The modifier mask for Ctrl/Alt/Shift by the file's own MetaKeys numbers (the default file: Shift 1,
    /// Ctrl 2, Alt 4). False, with the reason, when the file has no metakey for one of them.
    /// </summary>
    public static bool TryMeta(List<string> lines, HotkeyMods mods, out uint mask, out string error)
    {
        mask = 0;
        error = string.Empty;
        Dictionary<string, int> meta = MetaKeys(lines);
        bool Add(string left, string right, string what, ref uint m, out string err)
        {
            err = string.Empty;
            if (meta.TryGetValue(left, out int n) || meta.TryGetValue(right, out n)) { m |= 1u << (n - 1); return true; }
            err = $"Your keymap has no {what} modifier (MetaKeys).";
            return false;
        }
        if ((mods & HotkeyMods.Shift) != 0 && !Add("DIK_LSHIFT", "DIK_RSHIFT", "Shift", ref mask, out error)) return false;
        if ((mods & HotkeyMods.Ctrl) != 0 && !Add("DIK_LCONTROL", "DIK_RCONTROL", "Ctrl", ref mask, out error)) return false;
        if ((mods & HotkeyMods.Alt) != 0 && !Add("DIK_LMENU", "DIK_RMENU", "Alt", ref mask, out error)) return false;
        return true;
    }

    /// <summary>Every binding line in the Bindings block, with its group.</summary>
    public static List<Line> Bindings(List<string> lines)
    {
        var list = new List<Line>();
        bool inBindings = false;
        int depth = 0;
        string group = string.Empty;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            if (!inBindings)
            {
                if (t == "Bindings") { inBindings = true; depth = 0; }
                continue;
            }
            if (t == "[") { depth++; continue; }
            if (t == "]") { depth--; if (depth <= 0) inBindings = false; continue; }
            if (depth == 1 && Name.IsMatch(t)) { group = t; continue; }
            Match b = BindingLine.Match(t);
            if (!b.Success) continue;
            uint mask = 0;
            if (b.Groups[4].Success) uint.TryParse(b.Groups[4].Value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out mask);
            list.Add(new Line(i, group, b.Groups[1].Value, int.Parse(b.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                Canonical(b.Groups[3].Value), mask));
        }
        return list;
    }

    public static bool IsTextGroup(string group) => Array.Exists(TextGroups, g => g.Equals(group, StringComparison.OrdinalIgnoreCase));

    /// <summary>The bindings of this key with these modifiers in the groups the game reads while playing.</summary>
    public static List<Line> Uses(List<string> lines, int device, string dik, uint meta)
    {
        string d = Canonical(dik);
        return Bindings(lines).FindAll(l => l.Device == device && l.Dik == d && l.Meta == meta && !IsTextGroup(l.Group));
    }

    // ── Changing ────────────────────────────────────────────────────────────

    /// <summary>
    /// Binds the key to <paramref name="action"/>, taking it from every other action that has it (game
    /// groups). <paramref name="taken"/>: the bindings it came off. Returns false (lines unchanged) with
    /// <paramref name="error"/> when there is nowhere to put it. <paramref name="group"/>: where a new line
    /// goes when the action has none yet (another client's change: the group it went in there), before
    /// <see cref="DefaultGroup"/>.
    /// </summary>
    public static bool Bind(List<string> lines, string action, int device, string dik, uint meta, out List<Line> taken, out string error,
        string? group = null)
    {
        string? recordedGroup = string.IsNullOrEmpty(group) || IsTextGroup(group) ? null : group;
        error = string.Empty;
        string d = Canonical(dik);
        List<Line> all = Bindings(lines);
        taken = all.FindAll(l => l.Device == device && l.Dik == d && l.Meta == meta && !IsTextGroup(l.Group)
                                 && !l.Action.Equals(action, StringComparison.OrdinalIgnoreCase));
        bool already = all.Exists(l => l.Device == device && l.Dik == d && l.Meta == meta
                                       && l.Action.Equals(action, StringComparison.OrdinalIgnoreCase));

        // Where the new line goes: after the action's last line, else at the end of its group.
        group = null;
        int insertAt = -1;
        if (!already)
        {
            foreach (Line l in all)
                if (l.Action.Equals(action, StringComparison.OrdinalIgnoreCase) && !IsTextGroup(l.Group)) { group = l.Group; insertAt = l.Index + 1; }
            if (insertAt < 0)
            {
                group = recordedGroup ?? DefaultGroup(action);
                if (group == null) { error = $"No group for {action} in the keymap."; taken = new(); return false; }
            }
        }

        // Take the key: drop the other lines (bottom up so the indexes hold).
        for (int k = taken.Count - 1; k >= 0; k--)
        {
            lines.RemoveAt(taken[k].Index);
            if (insertAt > taken[k].Index) insertAt--;
        }
        if (already) return true;

        if (insertAt >= 0)
        {
            lines.Insert(insertAt, Indent(lines[insertAt - 1]) + Format(action, device, d, meta));
            return true;
        }
        int close = GroupClose(lines, group!, out int open);
        if (close >= 0)
        {
            lines.Insert(close, Indent(lines[close]) + "  " + Format(action, device, d, meta));
            return true;
        }
        if (open >= 0)
        {
            // The group's name with no [ ] block under it (an empty group).
            string gi = Indent(lines[open]);
            lines.InsertRange(open + 1, new[] { gi + "[", gi + "  " + Format(action, device, d, meta), gi + "]" });
            return true;
        }
        int end = BindingsClose(lines);
        if (end < 0) { error = "The keymap has no Bindings block."; return false; }
        lines.InsertRange(end, new[] { string.Empty, "  " + group, "  [", "    " + Format(action, device, d, meta), "  ]" });
        return true;
    }

    /// <summary>Removes the action's binding of this key with these modifiers. False when it had none.</summary>
    public static bool Unbind(List<string> lines, string action, int device, string dik, uint meta)
    {
        string d = Canonical(dik);
        List<Line> mine = Bindings(lines).FindAll(l => l.Device == device && l.Dik == d && l.Meta == meta
                                                      && l.Action.Equals(action, StringComparison.OrdinalIgnoreCase));
        for (int k = mine.Count - 1; k >= 0; k--) lines.RemoveAt(mine[k].Index);
        return mine.Count > 0;
    }

    /// <summary>Takes the key off every action that has it in the game's groups (an engine command's key); the bindings removed.</summary>
    public static List<Line> Take(List<string> lines, int device, string dik, uint meta)
    {
        List<Line> uses = Uses(lines, device, dik, meta);
        for (int k = uses.Count - 1; k >= 0; k--) lines.RemoveAt(uses[k].Index);
        return uses;
    }

    /// <summary>The game group the action's binding of this key is in ("" when it has none).</summary>
    public static string GroupOf(List<string> lines, string action, int device, string dik, uint meta)
    {
        string d = Canonical(dik);
        List<Line> all = Bindings(lines);
        int i = all.FindIndex(x => x.Device == device && x.Dik == d && x.Meta == meta && !IsTextGroup(x.Group)
                                   && x.Action.Equals(action, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? all[i].Group : string.Empty;
    }

    // ── A change every client makes ─────────────────────────────────────────
    //
    //  "<keymap>.rynthcore", one line per edit, oldest first:
    //    RynthCore keymap changes 2
    //    <stamp> <pid> bind|unbind|take <action, or - for take> <device> <key> <modifiers, or -> <group, or ->
    //  stamp: the change's UTC ticks (every edit of one change has it; each change's is past the last's), pid:
    //  the client that made it. The modifiers are each metakey bit by its key names ("DIK_LCONTROL|DIK_RCONTROL+
    //  DIK_LSHIFT"), so a keymap numbering its MetaKeys differently gets the same keys; "0x00000005" is a raw mask
    //  (a bit no metakey names). The group (a bind's): where the line went, for a keymap with no line for the
    //  action yet. An older RynthCore wrote just the keymap's time there (one number): that says the file
    //  changed, not what.

    /// <summary>Bind: the action gets the key, which comes off every other game action. Unbind: off the action. Take: off every game action.</summary>
    public enum EditKind { Bind, Unbind, Take }

    /// <summary>
    /// One edit of a change. <see cref="Action"/> is "" for Take; <see cref="Mods"/> as <see cref="ModsOf"/>
    /// writes it; <see cref="Group"/> a bind's group where it was made ("" none).
    /// </summary>
    internal readonly record struct Edit(EditKind Kind, string Action, int Device, string Dik, string Mods, string Group = "");

    /// <summary>One change: its stamp (UTC ticks), the client that made it, its edits in order.</summary>
    internal sealed record Change(long Stamp, int Pid, List<Edit> Edits);

    public const string ChangesHeader = "RynthCore keymap changes 2";
    private const string ChangesHeaderPrefix = "RynthCore keymap changes ";

    private static readonly Regex ModsText = new(@"^(0x[0-9A-Fa-f]{1,8}|[A-Za-z0-9_]+(\|[A-Za-z0-9_]+)*(\+[A-Za-z0-9_]+(\|[A-Za-z0-9_]+)*)*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A modifier mask as the key names of its metakeys in this keymap ("" for none): bits joined by '+', each
    /// bit's names by '|'. A bit no metakey names goes as the raw mask ("0x00000010").
    /// </summary>
    public static string ModsOf(List<string> lines, uint mask)
    {
        if (mask == 0) return string.Empty;
        var names = new Dictionary<int, List<string>>();
        foreach (KeyValuePair<string, int> kv in MetaKeys(lines))
        {
            if (!names.TryGetValue(kv.Value, out List<string>? list)) names[kv.Value] = list = new List<string>();
            list.Add(kv.Key);
        }
        var parts = new List<string>();
        for (int n = 1; n <= 32; n++)
        {
            if ((mask & (1u << (n - 1))) == 0) continue;
            if (!names.TryGetValue(n, out List<string>? list)) return "0x" + mask.ToString("X8", CultureInfo.InvariantCulture);
            parts.Add(string.Join("|", list));
        }
        return string.Join("+", parts);
    }

    /// <summary>The mask of <see cref="ModsOf"/>'s text by this keymap's own MetaKeys. False (why) when it has no such modifier.</summary>
    public static bool TryMaskOf(List<string> lines, string mods, out uint mask, out string error)
    {
        mask = 0;
        error = string.Empty;
        if (mods.Length == 0) return true;
        if (mods.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (uint.TryParse(mods.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask)) return true;
            error = $"Not a modifier mask: {mods}.";
            return false;
        }
        Dictionary<string, int> meta = MetaKeys(lines);
        foreach (string part in mods.Split('+'))
        {
            int n = 0;
            foreach (string name in part.Split('|'))
                if (meta.TryGetValue(Canonical(name), out n)) break;
            if (n is < 1 or > 32)
            {
                error = $"This keymap has no {part} modifier (MetaKeys).";
                return false;
            }
            mask |= 1u << (n - 1);
        }
        return true;
    }

    /// <summary>Makes one recorded edit: 1 changed, 0 nothing to do (the keys have it already), -1 can't (why in <paramref name="error"/>).</summary>
    public static int ApplyEdit(List<string> lines, Edit e, out string error)
    {
        if (!TryMaskOf(lines, e.Mods, out uint meta, out error)) return -1;
        switch (e.Kind)
        {
            case EditKind.Bind:
            {
                var before = new List<string>(lines);
                if (!Bind(lines, e.Action, e.Device, e.Dik, meta, out _, out error, e.Group)) return -1;
                return SameLines(before, lines) ? 0 : 1;
            }
            case EditKind.Unbind:
                return Unbind(lines, e.Action, e.Device, e.Dik, meta) ? 1 : 0;
            case EditKind.Take:
                return Take(lines, e.Device, e.Dik, meta).Count > 0 ? 1 : 0;
            default:
                error = "Not an edit.";
                return -1;
        }
    }

    /// <summary>
    /// Every edit of the changes, in order, on a copy of <paramref name="lines"/>: the lines are changed only
    /// when all of them could be made. 1 changed, 0 nothing to do, -1 one couldn't be made (why in
    /// <paramref name="error"/>; the lines as they were).
    /// </summary>
    public static int ApplyChanges(List<string> lines, IEnumerable<Change> changes, out string error)
    {
        error = string.Empty;
        var work = new List<string>(lines);
        foreach (Change c in changes)
            foreach (Edit e in c.Edits)
                if (ApplyEdit(work, e, out string err) < 0)
                {
                    error = $"{Describe(e)}: {err}";
                    return -1;
                }
        if (SameLines(work, lines)) return 0;
        lines.Clear();
        lines.AddRange(work);
        return 1;
    }

    /// <summary>An edit as the log reads: "bind UseQuickSlot_5 DIK_F5 DIK_LCONTROL|DIK_RCONTROL".</summary>
    public static string Describe(Edit e) =>
        KindName(e.Kind) + (e.Action.Length > 0 ? " " + e.Action : "") + (e.Device != 0 ? $" device {e.Device}" : "") + " " + e.Dik
        + (e.Mods.Length > 0 ? " " + e.Mods : "");

    private static bool SameLines(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static string KindName(EditKind k) => k switch { EditKind.Bind => "bind", EditKind.Unbind => "unbind", _ => "take" };

    private static bool TryKind(string s, out EditKind k)
    {
        switch (s)
        {
            case "bind": k = EditKind.Bind; return true;
            case "unbind": k = EditKind.Unbind; return true;
            case "take": k = EditKind.Take; return true;
            default: k = EditKind.Take; return false;
        }
    }

    /// <summary>The log's text (<see cref="ChangesHeader"/>, then one line per edit).</summary>
    public static string FormatChanges(IReadOnlyList<Change> log)
    {
        var sb = new StringBuilder();
        sb.Append(ChangesHeader).Append("\r\n");
        foreach (Change c in log)
            foreach (Edit e in c.Edits)
            {
                sb.Append(c.Stamp.ToString(CultureInfo.InvariantCulture)).Append(' ')
                  .Append(c.Pid.ToString(CultureInfo.InvariantCulture)).Append(' ')
                  .Append(KindName(e.Kind)).Append(' ')
                  .Append(e.Action.Length > 0 ? e.Action : "-").Append(' ')
                  .Append(e.Device.ToString(CultureInfo.InvariantCulture)).Append(' ')
                  .Append(e.Dik).Append(' ')
                  .Append(e.Mods.Length > 0 ? e.Mods : "-").Append(' ')
                  .Append(e.Group.Length > 0 ? e.Group : "-").Append("\r\n");
            }
        return sb.ToString();
    }

    /// <summary>
    /// The changes in a "&lt;keymap&gt;.rynthcore" file's text, by stamp. <paramref name="older"/>: it is an
    /// older RynthCore's marker (the keymap's time alone, no edits). A change with a line this version can't
    /// read is left out whole (never made in part); so is anything in a text that is neither.
    /// </summary>
    public static List<Change> ParseChanges(string text, out bool older)
    {
        older = false;
        var list = new List<Change>();
        List<string> lines = Split(text ?? string.Empty, out _);
        int i = 0;
        while (i < lines.Count && lines[i].Trim().Length == 0) i++;
        if (i >= lines.Count) return list;
        string first = lines[i].Trim();
        if (long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out _)) { older = true; return list; }
        if (!first.StartsWith(ChangesHeaderPrefix, StringComparison.Ordinal)
            || !int.TryParse(first.AsSpan(ChangesHeaderPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int version)
            || version < 2)
            return list;

        var bad = new HashSet<(long, int)>();
        for (i++; i < lines.Count; i++)
        {
            string[] f = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 0 || f[0].StartsWith('#')) continue;
            if (f.Length < 2 || !long.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out long stamp) || stamp <= 0
                || !int.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
                continue;   // not even whose: nothing to leave out
            bool ok = f.Length == 8 && TryKind(f[2], out EditKind kind)
                      && (kind == EditKind.Take ? f[3] == "-" : Name.IsMatch(f[3]))
                      && int.TryParse(f[4], NumberStyles.None, CultureInfo.InvariantCulture, out int device) && device <= 15
                      && Name.IsMatch(f[5])
                      && (f[6] == "-" || ModsText.IsMatch(f[6]))
                      && (f[7] == "-" || Name.IsMatch(f[7]));
            if (!ok) { bad.Add((stamp, pid)); continue; }
            TryKind(f[2], out kind);
            var edit = new Edit(kind, kind == EditKind.Take ? string.Empty : f[3], int.Parse(f[4], CultureInfo.InvariantCulture),
                Canonical(f[5]), f[6] == "-" ? string.Empty : f[6], f[7] == "-" ? string.Empty : f[7]);
            Change? c = list.Count > 0 && list[^1].Stamp == stamp && list[^1].Pid == pid ? list[^1]
                : list.Find(x => x.Stamp == stamp && x.Pid == pid);
            if (c == null) list.Add(c = new Change(stamp, pid, new List<Edit>()));
            c.Edits.Add(edit);
        }
        list.RemoveAll(c => bad.Contains((c.Stamp, c.Pid)));
        // By stamp, keeping the file's order for equal ones (List.Sort isn't stable).
        var ordered = new List<(Change C, int At)>(list.Count);
        for (int k = 0; k < list.Count; k++) ordered.Add((list[k], k));
        ordered.Sort((a, b) => a.C.Stamp != b.C.Stamp ? a.C.Stamp.CompareTo(b.C.Stamp) : a.At.CompareTo(b.At));
        return ordered.ConvertAll(o => o.C);
    }

    /// <summary>The newest stamp in the log (0 for none).</summary>
    public static long Newest(IReadOnlyList<Change> log)
    {
        long n = 0;
        foreach (Change c in log) if (c.Stamp > n) n = c.Stamp;
        return n;
    }

    /// <summary>A new change's stamp: now, or just past the newest in the log when that is later (another clock).</summary>
    public static long NextStamp(IReadOnlyList<Change> log, long nowTicks) => Math.Max(nowTicks, Newest(log) + 1);

    /// <summary>The log with <paramref name="change"/> added, keeping the newest <paramref name="keep"/> changes.</summary>
    public static List<Change> Append(IReadOnlyList<Change> log, Change change, int keep)
    {
        var list = new List<Change>(log) { change };
        if (keep > 0 && list.Count > keep) list.RemoveRange(0, list.Count - keep);
        return list;
    }

    /// <summary>
    /// The changes a client that has made every change up to <paramref name="seenStamp"/> still has to make:
    /// the newer ones, not its own (<paramref name="ownPid"/>), oldest first.
    /// </summary>
    public static List<Change> Pending(IReadOnlyList<Change> log, long seenStamp, int ownPid)
    {
        var list = new List<Change>();
        foreach (Change c in log)
            if (c.Stamp > seenStamp && c.Pid != ownPid && c.Edits.Count > 0) list.Add(c);
        return list;
    }

    /// <summary>A binding line as AC writes it: <c>UseQuickSlot_1 [ "" [ 0 DIK_1 ] 0x00000002 ]</c>.</summary>
    public static string Format(string action, int device, string dik, uint meta) =>
        meta == 0 ? $"{action} [ \"\" [ {device} {dik} ] ]" : $"{action} [ \"\" [ {device} {dik} ] 0x{meta:X8} ]";

    /// <summary>An action's name as the player reads it: "shortcut slot 5", "Toggle Spellbook Panel".</summary>
    public static string Describe(string action)
    {
        if (action.StartsWith("UseQuickSlot_", StringComparison.OrdinalIgnoreCase)) return "shortcut slot " + action[13..];
        var sb = new StringBuilder(action.Length + 8);
        for (int i = 0; i < action.Length; i++)
        {
            char c = action[i];
            if (c == '_') { sb.Append(' '); continue; }
            if (i > 0 && char.IsUpper(c) && (char.IsLower(action[i - 1]) || i + 1 < action.Length && char.IsLower(action[i + 1]) && char.IsUpper(action[i - 1])))
                sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Indent(string line)
    {
        int n = 0;
        while (n < line.Length && line[n] == ' ') n++;
        return line[..n];
    }

    /// <summary>The line of the group's closing ']' (and of its name), or -1.</summary>
    private static int GroupClose(List<string> lines, string group, out int nameLine)
    {
        nameLine = -1;
        bool inBindings = false;
        int depth = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].Trim();
            if (!inBindings)
            {
                if (t == "Bindings") inBindings = true;
                continue;
            }
            if (t == "[")
            {
                depth++;
                continue;
            }
            if (t == "]")
            {
                if (nameLine >= 0 && depth == 2) return i;
                depth--;
                if (depth <= 0) return -1;
                continue;
            }
            if (depth == 1 && Name.IsMatch(t))
            {
                if (nameLine >= 0) return -1;   // the next group: ours had no [ ] block
                if (t.Equals(group, StringComparison.OrdinalIgnoreCase)) nameLine = i;
            }
        }
        return -1;
    }

    /// <summary>The line of the Bindings block's closing ']', or -1.</summary>
    private static int BindingsClose(List<string> lines)
    {
        bool inBindings = false;
        int depth = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].Trim();
            if (!inBindings)
            {
                if (t == "Bindings") inBindings = true;
                continue;
            }
            if (t == "[") depth++;
            else if (t == "]" && --depth == 0) return i;
        }
        return -1;
    }
}

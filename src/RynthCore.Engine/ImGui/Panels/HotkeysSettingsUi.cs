// ============================================================================
//  RynthCore.Engine - ImGui/Panels/HotkeysSettingsUi.cs
//  Settings > Hotkeys: the user hotkeys editor (Hotkeys/HotkeyService.cs).
//
//  A master switch, the list of bindings (key, what it does, on/off; edit,
//  remove, move up/down), Add hotkey, and an editor under the list: Key is
//  captured ("press a key... Esc cancels": the next combo pressed, which never
//  reaches AC), Action is a dropdown of the built-ins or "Chat command" with a
//  text box. The VTank-style actions follow the built-ins under their own header;
//  "Meta: set state..." asks for the state (the loaded meta's states are offered,
//  from UiSources.Meta while the editor shows that action). Duplicate keys and
//  keys AC uses are warned about, never refused.
//  Drawn by SettingsFace for the Hotkeys tab's one row, in its look.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.Hotkeys;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class HotkeysSettingsUi
{
    public const string TabName = "Hotkeys";

    private static readonly uint ToggleOn = C(0xFF33FF33), ToggleOff = C(0xFF334455), Warn = C(0xFFFFB347);
    private static uint Teal => RynthTheme.Accent;
    private static uint Amber => RynthTheme.Highlight;
    private static uint Mute => RynthTheme.TextMuted;
    private static uint TextDim => RynthTheme.Text;
    private static uint ShellBg => RynthTheme.Background;
    private static uint PanelBg => RynthTheme.Field;
    private static uint BtnFill => RynthTheme.Button;
    private static uint BtnBord => RynthTheme.Border;
    private static uint TabActive => RynthTheme.Selected;
    private static uint C(uint argb) => RynthTheme.Argb(argb);

    private const string ChatCommandItem = "Chat command...";

    // The editor: open while adding (EditIndex -1) or editing a binding.
    private bool _editorOpen;
    private int _editIndex = -1;
    private KeyCombo _editCombo;
    private string _editAction = "macro";
    private string _editCommand = string.Empty;
    private string _editParam = string.Empty;   // an action's parameter (the meta state)
    private bool _editEnabled = true;
    private bool _waitingForKey;
    private bool _metaSubscribed;               // UiSources.Meta, for the meta state list

    /// <summary>The section is no longer shown (Settings closed, another tab): a key capture stops.</summary>
    public void Leave()
    {
        if (_waitingForKey) HotkeyService.CancelCapture();
        _waitingForKey = false;
        WatchMetaStates(false);
    }

    /// <summary>Subscribes to the meta snapshot while "Meta: set state..." is being edited (its state list).</summary>
    private void WatchMetaStates(bool on)
    {
        if (on == _metaSubscribed) return;
        _metaSubscribed = on;
        if (on) { UiSources.Meta.Subscribe(); UiSources.Meta.RequestRefresh(); }
        else UiSources.Meta.Unsubscribe();
    }

    /// <summary>Draws the section. True while a text box here has the keyboard.</summary>
    public bool Draw(float w)
    {
        bool typing = false;
        HotkeyFile file = HotkeyService.Current;
        IReadOnlyList<HotkeyBinding> list = file.Bindings;

        PollCapture();

        MasterSwitch(file.Enabled, w);
        Note("A hotkey works in game while you aren't typing (AC's chat or any text box). The key goes to the hotkey, " +
             "not to AC. Saved for this PC: every client uses the same hotkeys. Also: /rc hotkey.", w);

        Section("Your Hotkeys", w);
        if (list.Count == 0)
            Note("(No hotkeys yet. Add hotkey picks a key and what it does.)", w);
        List<int> shadowed = HotkeyConflicts.Shadowed(list);
        for (int i = 0; i < list.Count; i++)
        {
            ImGuiNET.ImGui.PushID(i);
            BindingRow(list, i, shadowed.Contains(i), w);
            ImGuiNET.ImGui.PopID();
        }

        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
        if (!_editorOpen)
        {
            if (ImGuiNET.ImGui.Button(PhosphorIcons.Plus + "  Add hotkey"))
                OpenEditor(-1, null);
            ImGuiNET.ImGui.SetItemTooltip("Bind a key to a built-in action or a chat command.");
        }
        else
        {
            typing = Editor(list, w);
        }
        return typing;
    }

    // ── Rows ───────────────────────────────────────────────────────────────

    private static void MasterSwitch(bool on, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        const string label = "Hotkeys On";
        float labelW = ImGuiNET.ImGui.CalcTextSize(label).X;
        if (ImGuiNET.ImGui.InvisibleButton("##hk_master", new Vector2(Math.Min(w, 18 + labelW), 16)))
            HotkeyService.SetMasterEnabled(!on);
        if (ImGuiNET.ImGui.IsItemHovered()) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGuiNET.ImGui.SetItemTooltip("Off: no hotkey fires (they're kept). Also: /rc hotkey on|off.");
        dl.AddRectFilled(p + new Vector2(0, 2), p + new Vector2(12, 14), on ? ToggleOn : ToggleOff, 2);
        dl.AddText(p + new Vector2(18, (16 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), TextDim, label);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
    }

    // [dot] Ctrl+F1                         [^][v][edit][x]
    //       Toggle Combat
    private void BindingRow(IReadOnlyList<HotkeyBinding> list, int i, bool shadowed, float w)
    {
        HotkeyBinding b = list[i];
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11), small = ImGuiFonts.Get(UiFont.Ui10);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        bool editingThis = _editorOpen && _editIndex == i;
        float rowH = bold.FontSize + small.FontSize + 6;
        if (editingThis) dl.AddRectFilled(p - new Vector2(2, 1), p + new Vector2(w, rowH), TabActive);

        // Enabled dot: click to switch this hotkey on or off.
        ImGuiNET.ImGui.SetCursorScreenPos(p);
        if (ImGuiNET.ImGui.InvisibleButton("##on", new Vector2(14, 16)))
            HotkeyService.Replace(i, b with { Enabled = !b.Enabled });
        SafeImGui.SetItemTooltip(b.Enabled ? "On. Click to switch this hotkey off." : "Off. Click to switch this hotkey on.");
        dl.AddRectFilled(p + new Vector2(0, 2), p + new Vector2(12, 14), b.Enabled ? ToggleOn : ToggleOff, 2);

        dl.AddText(bold, bold.FontSize, p + new Vector2(18, 1), b.Enabled ? Amber : Mute, b.Combo.ToString());
        string what = HotkeyActions.Describe(b);
        float textRight = p.X + w - 4 * 20 - 4;
        dl.PushClipRect(p + new Vector2(18, 0), new Vector2(textRight, p.Y + rowH), true);
        dl.AddText(small, small.FontSize, p + new Vector2(18, bold.FontSize + 3), b.Enabled ? TextDim : Mute, what);
        dl.PopClipRect();
        // The full text on hover (a long chat command is clipped).
        ImGuiNET.ImGui.SetCursorScreenPos(p + new Vector2(18, 0));
        ImGuiNET.ImGui.InvisibleButton("##what", new Vector2(Math.Max(1, textRight - p.X - 18), rowH));
        SafeImGui.SetItemTooltip(b.Combo + ": " + what + (b.Enabled ? "" : " (off)"));

        float x = p.X + w - 4 * 20;
        if (IconButton("##up", new Vector2(x, p.Y), PhosphorIcons.CaretUp, i > 0))
            HotkeyService.Move(i, -1);
        ImGuiNET.ImGui.SetItemTooltip("Move up. Of two hotkeys on the same key, the higher one fires.");
        if (IconButton("##down", new Vector2(x + 20, p.Y), PhosphorIcons.CaretDown, i < list.Count - 1))
            HotkeyService.Move(i, +1);
        ImGuiNET.ImGui.SetItemTooltip("Move down.");
        if (IconButton("##edit", new Vector2(x + 40, p.Y), PhosphorIcons.PencilSimple, true))
            OpenEditor(i, b);
        ImGuiNET.ImGui.SetItemTooltip("Edit: change the key or what it does.");
        if (IconButton("##del", new Vector2(x + 60, p.Y), PhosphorIcons.Trash, true))
        {
            HotkeyService.RemoveAt(i);
            if (_editorOpen && _editIndex == i) CloseEditor();
            else if (_editorOpen && _editIndex > i) _editIndex--;
        }
        ImGuiNET.ImGui.SetItemTooltip("Remove this hotkey.");

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + rowH));
        if (shadowed && b.Enabled)
            Warning($"Never fires: a hotkey higher in the list has {b.Combo}.", w);
        else if (b.IsCommand && b.Command.Trim().Length == 0)
            Warning("No chat command: edit it to add one.", w);
        else if (HotkeyActions.Find(b.Action) is { HasParam: true } pa && b.Command.Trim().Length == 0)
            Warning($"No {pa.Param!.ToLowerInvariant()}: edit it to pick one.", w);
        else if (!b.IsCommand && HotkeyActions.Find(b.Action) == null)
            Warning($"This engine doesn't know the action '{b.Action}'.", w);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
    }

    // ── Editor ─────────────────────────────────────────────────────────────

    private void OpenEditor(int index, HotkeyBinding? b)
    {
        if (_waitingForKey) HotkeyService.CancelCapture();
        _editorOpen = true;
        _editIndex = index;
        _editCombo = b?.Combo ?? default;
        _editAction = b?.Action ?? "macro";
        bool hasParam = b != null && HotkeyActions.Find(b.Action)?.HasParam == true;
        _editCommand = b != null && !hasParam ? b.Command : string.Empty;
        _editParam = hasParam ? b!.Command : string.Empty;
        _editEnabled = b?.Enabled ?? true;
        _waitingForKey = b == null;   // a new hotkey starts by asking for its key
        if (_waitingForKey) HotkeyService.BeginCapture();
    }

    private void CloseEditor()
    {
        if (_waitingForKey) HotkeyService.CancelCapture();
        _waitingForKey = false;
        _editorOpen = false;
        _editIndex = -1;
        WatchMetaStates(false);
    }

    private void PollCapture()
    {
        if (!_waitingForKey) return;
        if (HotkeyService.TryTakeCapture(out KeyCombo? combo))
        {
            _waitingForKey = false;
            if (combo is { } c) _editCombo = c;
        }
    }

    private bool Editor(IReadOnlyList<HotkeyBinding> list, float w)
    {
        bool typing = false;
        Section(_editIndex < 0 ? "New Hotkey" : "Edit Hotkey", w);

        // Key: [Ctrl+F1] (click, then press the combo).
        Label("Key", "Click, then press the key with any of Ctrl, Alt and Shift. Esc cancels.");
        ImGuiNET.ImGui.SameLine(70);
        string keyText = _waitingForKey ? "Press a key... (Esc cancels)" : _editCombo.IsEmpty ? "Click to set" : _editCombo.ToString();
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, _waitingForKey ? Teal : Amber);
        if (ImGuiNET.ImGui.Button(keyText + "##key", new Vector2(Math.Max(80, w - 74), 0)))
        {
            if (_waitingForKey) { HotkeyService.CancelCapture(); _waitingForKey = false; }
            else { HotkeyService.BeginCapture(); _waitingForKey = true; }
        }
        ImGuiNET.ImGui.PopStyleColor();
        SafeImGui.SetItemTooltip(_waitingForKey ? "Waiting for a key. Click again or press Esc to stop." : "Click, then press the key combination.");
        if (_waitingForKey && HotkeyService.CaptureNote.Length > 0)
            Warning(HotkeyService.CaptureNote, w);

        // Action: dropdown of the built-ins and "Chat command...".
        ImGuiNET.ImGui.Dummy(new Vector2(0, 3));
        Label("Action", "What the key does: a built-in, or any chat command (e.g. /ra start, /rn bc /ra stop).");
        ImGuiNET.ImGui.SameLine(70);
        bool isCommand = string.Equals(_editAction, HotkeyActions.Command, StringComparison.OrdinalIgnoreCase);
        HotkeyAction? picked = isCommand ? null : HotkeyActions.Find(_editAction);
        string current = isCommand ? ChatCommandItem
            : picked == null ? _editAction
            : picked.Group.Length > 0 ? $"{picked.Name}  ({picked.Group})" : picked.Name;
        ImGuiNET.ImGui.SetNextItemWidth(Math.Max(80, w - 74));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, BtnFill);
        bool open = ImGuiNET.ImGui.BeginCombo("##action", current, ImGuiComboFlags.HeightLarge);
        ImGuiNET.ImGui.PopStyleColor(3);
        if (!open && picked is { Tip.Length: > 0 }) SafeImGui.SetItemTooltip(picked.Tip);
        if (open)
        {
            if (ImGuiNET.ImGui.Selectable(ChatCommandItem, isCommand)) _editAction = HotkeyActions.Command;
            ImGuiNET.ImGui.SetItemTooltip("Run a chat command, as if typed: /ra ..., /rc ..., /lua ..., /rn bc ...");
            string group = string.Empty;
            foreach (HotkeyAction a in HotkeyActions.BuiltIns)
            {
                if (!HotkeyActions.Offered(a)) continue;   // the ILT panels: only on Infinite Leaftide with RynthILT
                if (a.Group != group || group.Length == 0 && a == HotkeyActions.BuiltIns[0])
                {
                    ImGuiNET.ImGui.Separator();
                    if (a.Group.Length > 0)
                    {
                        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Amber);
                        ImGuiNET.ImGui.TextUnformatted(a.Group);
                        ImGuiNET.ImGui.PopStyleColor();
                        if (a.Group == HotkeyActions.VTankGroup)
                            ImGuiNET.ImGui.SetItemTooltip("VirindiTank's hotkey actions, by VTank's names.");
                    }
                    group = a.Group;
                }
                if (ImGuiNET.ImGui.Selectable((a.Group.Length > 0 ? "  " : "") + a.Name + "##" + a.Id,
                        string.Equals(a.Id, _editAction, StringComparison.OrdinalIgnoreCase)))
                    _editAction = a.Id;
                if (a.Tip.Length > 0) SafeImGui.SetItemTooltip(a.Tip);
            }
            ImGuiNET.ImGui.EndCombo();
        }

        bool hasParam = picked is { HasParam: true };
        WatchMetaStates(hasParam && string.Equals(picked!.Id, HotkeyActions.MetaStateId, StringComparison.OrdinalIgnoreCase));
        if (hasParam)
            typing |= ParamRow(picked!, w);

        if (isCommand)
        {
            ImGuiNET.ImGui.Dummy(new Vector2(0, 3));
            Label("Command", "The chat line to run, starting with /.");
            ImGuiNET.ImGui.SameLine(70);
            ImGuiNET.ImGui.SetNextItemWidth(Math.Max(80, w - 74));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, PanelBg);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Amber);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            ImGuiNET.ImGui.InputTextWithHint("##cmd", "/ra start", ref _editCommand, 400u);
            typing = ImGuiNET.ImGui.IsItemActive();
            ImGuiNET.ImGui.PopStyleVar();
            ImGuiNET.ImGui.PopStyleColor(3);
            ImGuiNET.ImGui.SetItemTooltip("Any chat command: /ra start, /rc reload, /lua myscript go, /rn bc /ra stop.");
        }

        // Warnings for this key (allowed anyway).
        foreach (string warning in HotkeyConflicts.Warnings(list, _editCombo, _editIndex))
            Warning(warning, w);

        string cmd = _editCommand.Trim();
        string param = _editParam.Trim();
        bool commandOk = !isCommand || cmd.StartsWith('/');
        bool paramOk = !hasParam || param.Length > 0;
        bool canSave = !_editCombo.IsEmpty && !_waitingForKey && commandOk && paramOk;
        if (isCommand && cmd.Length > 0 && !cmd.StartsWith('/'))
            Warning("A chat command starts with / (e.g. /ra start).", w);

        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
        ImGuiNET.ImGui.BeginDisabled(!canSave);
        if (ImGuiNET.ImGui.Button(PhosphorIcons.FloppyDisk + "  Save"))
        {
            var b = new HotkeyBinding(_editCombo, isCommand ? HotkeyActions.Command : _editAction,
                isCommand ? cmd : hasParam ? param : string.Empty, _editEnabled);
            if (_editIndex < 0 || !HotkeyService.Replace(_editIndex, b))
                HotkeyService.Add(b);
            CloseEditor();
        }
        ImGuiNET.ImGui.EndDisabled();
        if (ImGuiNET.ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            SafeImGui.SetTooltip(!canSave
                ? (_editCombo.IsEmpty || _waitingForKey ? "Pick a key first."
                    : !paramOk ? $"Pick the {picked?.Param?.ToLowerInvariant() ?? "value"} first."
                    : "Type the chat command (starting with /).")
                : "Save this hotkey (it works right away, in every client on this PC).");
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.Button("Cancel"))
            CloseEditor();
        ImGuiNET.ImGui.SetItemTooltip("Close without saving.");
        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
        return typing;
    }

    /// <summary>
    /// The parameter row (the meta state): a text box, and a list of the loaded meta's states when
    /// they are known. True while the text box has the keyboard.
    /// </summary>
    private bool ParamRow(HotkeyAction a, float w)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 3));
        bool meta = string.Equals(a.Id, HotkeyActions.MetaStateId, StringComparison.OrdinalIgnoreCase);
        Label(a.Param!, meta ? "The meta state to switch to. Pick one of the loaded meta's states, or type a name." : a.Tip);
        ImGuiNET.ImGui.SameLine(70);

        List<string> states = meta ? UiSources.Meta.Current?.Value.States ?? new List<string>() : new List<string>();
        float listW = states.Count > 0 ? 26 : 0;
        ImGuiNET.ImGui.SetNextItemWidth(Math.Max(80, w - 74 - listW));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, PanelBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Amber);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGuiNET.ImGui.InputTextWithHint("##param", meta ? "state name" : a.Param!.ToLowerInvariant(), ref _editParam, 200u);
        bool typing = ImGuiNET.ImGui.IsItemActive();
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(3);
        SafeImGui.SetItemTooltip(meta ? "The state's name, as in the meta (the case doesn't matter)." : a.Tip);

        if (states.Count > 0)
        {
            ImGuiNET.ImGui.SameLine();
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, BtnFill);
            ImGuiNET.ImGui.SetNextItemWidth(22);
            bool open = ImGuiNET.ImGui.BeginCombo("##states", string.Empty, ImGuiComboFlags.NoPreview | ImGuiComboFlags.HeightLarge);
            ImGuiNET.ImGui.PopStyleColor(3);
            if (open)
            {
                foreach (string st in states)
                    if (ImGuiNET.ImGui.Selectable(st + "##st", string.Equals(st, _editParam.Trim(), StringComparison.OrdinalIgnoreCase)))
                        _editParam = st;
                ImGuiNET.ImGui.EndCombo();
            }
            ImGuiNET.ImGui.SetItemTooltip("The loaded meta's states.");
        }
        else if (meta)
            Note("(No meta loaded, or RynthAi isn't running: type the state's name.)", w);

        if (meta && states.Count > 0 && _editParam.Trim().Length > 0
            && !states.Exists(st => string.Equals(st, _editParam.Trim(), StringComparison.OrdinalIgnoreCase)))
            Warning($"The loaded meta has no state '{_editParam.Trim()}' (it may be in another meta).", w);
        return typing;
    }

    // ── Pieces in Settings' look ───────────────────────────────────────────

    private static void Label(string text, string tip)
    {
        ImGuiNET.ImGui.AlignTextToFramePadding();
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, TextDim);
        ImGuiNET.ImGui.TextUnformatted(text);
        ImGuiNET.ImGui.PopStyleColor();
        SafeImGui.SetItemTooltip(tip);
    }

    private static void Section(string text, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        float h = bold.FontSize + 4;
        dl.AddRectFilled(p, p + new Vector2(w, h), TabActive);
        dl.AddText(bold, bold.FontSize, p + new Vector2(4, 2), Amber, text);
        ImGuiNET.ImGui.Dummy(new Vector2(w, h));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
    }

    private static void Note(string text, float w) => Wrapped(text, w, Mute);

    private static void Warning(string text, float w) => Wrapped(PhosphorIcons.Warning + " " + text, w, Warn);

    private static void Wrapped(string text, float w, uint color)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 1));
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui10));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + w);
        ImGuiNET.ImGui.TextUnformatted(text);
        ImGuiNET.ImGui.PopTextWrapPos();
        ImGuiNET.ImGui.PopStyleColor();
        ImGuiNET.ImGui.PopFont();
    }

    private static bool IconButton(string id, Vector2 pos, string glyph, bool enabled)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, new Vector2(18, 18)) && enabled;
        bool hot = enabled && ImGuiNET.ImGui.IsItemHovered();
        dl.AddRectFilled(pos, pos + new Vector2(18, 18), hot ? Lighten(BtnFill) : BtnFill);
        dl.AddRect(pos, pos + new Vector2(18, 18), BtnBord);
        PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), glyph, pos, new Vector2(18, 18), enabled ? TextDim : ToggleOff);
        return clicked;
    }

    private static uint Lighten(uint abgr)
    {
        uint a = abgr & 0xFF000000;
        uint r = (uint)Math.Min(255, (int)((abgr & 0xFF) * 1.25f) + 12);
        uint g = (uint)Math.Min(255, (int)(((abgr >> 8) & 0xFF) * 1.25f) + 12);
        uint b = (uint)Math.Min(255, (int)(((abgr >> 16) & 0xFF) * 1.25f) + 12);
        return a | (b << 16) | (g << 8) | r;
    }
}

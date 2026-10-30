// ============================================================================
//  RynthCore.Engine - ImGui/Panels/LuaFace.cs
//  ImGui face of the Lua panel. Two layouts, picked by the payload:
//
//  RynthLua v2 (ScriptInfos present), tabs "Scripts" | "Editor", and on the
//  right "N running · RynthAi: loaded / not loaded":
//    Scripts  one row per script: running dot, name, description, autostart
//             (Off / global / this character), Run|Stop, Restart. Click a row
//             to select it; below, the selected script's state, "Open in
//             editor", its console (Copy, Clear) and a line that runs Lua
//             inside it (execin).
//    Editor   status + Run/Stop for the editor's script (the name box, else
//             "editor"), name + Save/New/Delete, the coloured editor
//             (CodeEditor, Lua) and the selected script's console.
//  Old single-script payload (RynthAi's exports) or no plugin: the v1 face,
//  status + Run/Stop, script buttons (click to open), name row, editor,
//  console; "Lua: RynthLua not connected" until a plugin answers.
//
//  Data: UiSources.Lua (hub) and LuaCommands. The editor takes a script when
//  the plugin reports a new load (LoadSeq); on first open it restores the
//  plugin's last script only if the editor is empty. Text leaves the editor
//  only on Run/Save. The payload's Console is the selected script's.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class LuaFace : IImGuiPanel
{
    public const string Title = "Lua";

    public static void Register() => ImGuiPanelHost.Register(Title,
        new PanelSpec(new Vector2(420, 560), new Vector2(380, 360), EdgeToEdge: true, GripInBody: true),
        () => new LuaFace());

    private static readonly uint ConsoleBg = RynthTheme.Argb(0xFF05090D), DotOff = RynthTheme.Argb(0xFF4D5A66);

    private const string NotConnected = "Lua: RynthLua not connected";
    /// <summary>RynthLua's description for a running script that has no file (the editor, /lua exec).</summary>
    private const string NotSaved = "(not saved)";
    private const float RowH = 26;

    private static readonly string[] AutoCodes = { "none", "global", "character" };
    private static readonly string[] AutoItems = { "Off", "Every character (global)", "This character" };

    private LuaPayload? _data;
    private long _seenVersion = -1;
    private int _lastLoadSeq = -1, _lastConsoleSeq = -1;
    private string _lastSelected = string.Empty;
    private bool _firstFetch = true, _scrollConsole;
    private string _status = NotConnected;
    private bool _running;
    private string _note = string.Empty;   // local hint ("Type a script name first.")
    private bool _editorTab;
    private string _editorSelectSent = string.Empty;   // Editor tab: last "select" sent for its script
    private bool _focusExec;

    private readonly byte[] _name = new byte[128];
    private readonly byte[] _exec = new byte[1024];
    private readonly Picker _picker = new("##lua_pick");
    private CodeEditor? _editor;

    public void OnShown()
    {
        UiSources.Lua.Subscribe();
        UiSources.Lua.RequestRefresh();
    }

    public void OnHidden()
    {
        UiSources.Lua.Unsubscribe();
        _editor?.Dispose();
        _editor = null;
    }

    public void Draw()
    {
        _editor ??= new CodeEditor("##lua_source", CodeLanguage.Lua, CodeEditor.RynthPalette());
        TakeSnapshot();

        float w = Begin(out Vector2 origin, out Vector2 size);
        LuaPayload? d = _data;
        if (d?.ScriptInfos is { } infos)
        {
            Header(w, d);
            if (_editorTab) EditorView(w, origin, size, d, infos);
            else ScriptsView(w, origin, size, d, infos);
        }
        else
            LegacyView(w, origin, size);
        End(origin, size);
        _picker.Draw();
    }

    private void TakeSnapshot()
    {
        var snap = UiSources.Lua.Current;
        if (snap == null)
        {
            // Plugins unloaded (RL): back to "not connected" until one answers again.
            if (_data != null)
            {
                _data = null;
                _seenVersion = -1;
                _running = false;
                _status = NotConnected;
            }
            return;
        }
        if (snap.Version == _seenVersion) return;
        _seenVersion = snap.Version;
        LuaPayload d = _data = snap.Value;

        _running = d.Running;
        _status = d.Running
            ? $"Running: {(d.ScriptName.Length > 0 ? d.ScriptName : "(editor)")}"
            : d.Status.Length > 0 ? d.Status : "Lua: idle";

        // A newly loaded script goes into the editor; on first open only if it's empty.
        if (d.LoadSeq != _lastLoadSeq)
        {
            bool take = !_firstFetch || _editor!.GetText().Length == 0;
            _lastLoadSeq = d.LoadSeq;
            if (take)
            {
                _editor!.SetText(d.LoadedText);
                WriteUtf8(_name, d.LoadedName);
            }
        }
        string selected = d.Selected ?? string.Empty;
        if (d.ConsoleSeq != _lastConsoleSeq || selected != _lastSelected)
        {
            _lastConsoleSeq = d.ConsoleSeq;
            _lastSelected = selected;
            _scrollConsole = true;
            _note = string.Empty;
        }
        _firstFetch = false;
    }

    private const string ScriptsTab = PhosphorIcons.Files + " Scripts", EditorTab = PhosphorIcons.PencilSimple + " Editor";
    private const string RunLabel = PhosphorIcons.Play + " Run", StopLabel = PhosphorIcons.Stop + " Stop",
        RestartLabel = PhosphorIcons.ArrowCounterClockwise + " Restart";

    // ── v2: header ───────────────────────────────────────────────────────

    // [Scripts][Editor]            N running · RynthAi: loaded
    private void Header(float w, LuaPayload d)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        const float h = 24;
        float sw = ButtonWidth(ScriptsTab) + 4, ew = ButtonWidth(EditorTab) + 4;
        if (Button("##tab_scripts", ScriptsTab, p, new Vector2(sw, h), _editorTab ? Text : Teal, _editorTab ? BtnFill : Selected))
            _editorTab = false;
        if (Button("##tab_editor", EditorTab, new Vector2(p.X + sw + 2, p.Y), new Vector2(ew, h), _editorTab ? Teal : Text, _editorTab ? Selected : BtnFill))
            _editorTab = true;

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        bool ai = d.RynthAi == true;
        string run = d.RunningCount == 1 ? "1 running" : $"{d.RunningCount} running";
        const string sep = "  ·  ";
        string aiText = ai ? "RynthAi: loaded" : "RynthAi: not loaded";
        float runW = ImGuiNET.ImGui.CalcTextSize(run).X, sepW = ImGuiNET.ImGui.CalcTextSize(sep).X, aiW = ImGuiNET.ImGui.CalcTextSize(aiText).X;
        float left = p.X + sw + ew + 10, right = p.X + w;
        float x = Math.Max(left, right - runW - sepW - aiW);
        float ty = p.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
        dl.PushClipRect(new Vector2(left, p.Y), new Vector2(right, p.Y + h), true);
        dl.AddText(new Vector2(x, ty), d.RunningCount > 0 ? Green : Mute, run);
        dl.AddText(new Vector2(x + runW, ty), Mute, sep);
        dl.AddText(new Vector2(x + runW + sepW, ty), ai ? Green : Amber, aiText);
        dl.PopClipRect();
        if (ImGuiNET.ImGui.IsMouseHoveringRect(new Vector2(x + runW + sepW, p.Y), new Vector2(right, p.Y + h)))
            SafeImGui.SetTooltip(ai
                ? "RynthAi's script interface is there: scripts can use its macro, nav and meta features."
                : "RynthAi isn't loaded (or is too old): scripts' RynthAi calls won't work.");
        NextLine(p, h + 2);
    }

    // ── v2: Scripts tab ──────────────────────────────────────────────────

    private void ScriptsView(float w, Vector2 origin, Vector2 size, LuaPayload d, List<LuaScriptInfo> infos)
    {
        string sel = d.Selected ?? string.Empty;
        ScriptList(w, Remaining(origin, size), infos, sel);
        LuaScriptInfo? si = Find(infos, sel);
        SelectedRow(w, d, sel, si);

        // Console, then the exec line: Dummy+header (30) + child + spacing (4) + line (22).
        float consoleH = Math.Max(40, Remaining(origin, size) - 56);
        Console(w, consoleH, sel.Length > 0 ? "Console · " + sel : "Console");
        ExecRow(w, sel, si?.Running == true);
    }

    private void ScriptList(float w, float avail, List<LuaScriptInfo> infos, string selected)
    {
        int n = infos.Count;
        float listMax = Math.Max(RowH * 2 + 4, avail * 0.42f);
        float listH = Math.Clamp(Math.Max(1, n) * RowH + 4, RowH + 4, listMax);

        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2, 2));
        ImGuiNET.ImGui.BeginChild("##lua_list", new Vector2(w, listH), ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(2);
        float rw = ImGuiNET.ImGui.GetContentRegionAvail().X;
        if (n == 0)
        {
            Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.GetWindowDrawList().AddText(p + new Vector2(8, (RowH - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Mute,
                "No scripts yet: write one in the Editor tab and Save.");
            ImGuiNET.ImGui.Dummy(new Vector2(rw, RowH));
        }
        for (int i = 0; i < n; i++)
        {
            ImGuiNET.ImGui.PushID(i);
            ScriptRow(infos[i], i, rw, selected);
            ImGuiNET.ImGui.PopID();
        }
        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopStyleVar();
    }

    // (dot) Name  description ........ [Auto: off] [Run] [Restart]
    private void ScriptRow(LuaScriptInfo info, int index, float w, string selected)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        bool isSel = info.Name.Equals(selected, StringComparison.OrdinalIgnoreCase);
        bool notSaved = IsNotSaved(info);
        const float autoW = 74, runW = 50, restartW = 64, gap = 3;
        float nameW = Math.Max(40, w - (autoW + runW + restartW + gap * 3 + 2));

        dl.AddRectFilled(p, p + new Vector2(w, RowH), isSel ? Selected : index % 2 == 0 ? RowAlt : PanelBg);
        ImGuiNET.ImGui.SetCursorScreenPos(p);
        if (ImGuiNET.ImGui.InvisibleButton("##row", new Vector2(nameW, RowH)) && !isSel)
            LuaCommands.Send("select", info.Name);
        if (ImGuiNET.ImGui.IsItemHovered())
        {
            if (!isSel) dl.AddRectFilled(p, p + new Vector2(nameW, RowH), BtnFill);
            SafeImGui.SetTooltip(RowTooltip(info, notSaved));
        }
        if (isSel) dl.AddRectFilled(p, p + new Vector2(2, RowH), Teal);

        float ty = p.Y + (RowH - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
        dl.AddCircleFilled(new Vector2(p.X + 11, p.Y + RowH * 0.5f), 4, info.Running ? Green : DotOff);
        dl.PushClipRect(p, p + new Vector2(nameW - 4, RowH), true);
        dl.AddText(new Vector2(p.X + 21, ty), isSel ? Teal : Text, info.Name);
        if (info.Description.Length > 0)
            dl.AddText(new Vector2(p.X + 21 + ImGuiNET.ImGui.CalcTextSize(info.Name).X + 8, ty), Faded(Mute), info.Description);
        dl.PopClipRect();

        float x = p.X + nameW, by = p.Y + 3, bh = RowH - 6;
        bool auto = info.Autostart is "global" or "character";
        if (Button("##auto", AutoLabel(info.Autostart), new Vector2(x, by), new Vector2(autoW, bh), auto ? Teal : Mute, BtnFill, enabled: !notSaved))
        {
            string name = info.Name;
            // Before login there's no character to autostart for: only off / global.
            string[] items = _data?.LoggedIn == false ? AutoItems[..2] : AutoItems;
            _picker.Open(new Vector2(x, by + bh + 2), items, Array.IndexOf(AutoCodes, info.Autostart),
                k => LuaCommands.Send("autostart", name, AutoCodes[k]), 190);
        }
        SafeImGui.SetItemTooltip(notSaved
            ? "Save it to a file to autostart it."
            : "Start at login: off, on every character (global), or on this character only.");
        x += autoW + gap;
        if (info.Running)
        {
            if (Button("##run", StopLabel, new Vector2(x, by), new Vector2(runW, bh), Text, StopBg))
                LuaCommands.Send("stopScript", info.Name);
        }
        else if (Button("##run", RunLabel, new Vector2(x, by), new Vector2(runW, bh), Text, StartBg, enabled: !notSaved))
            LuaCommands.Send("start", info.Name);
        x += runW + gap;
        if (Button("##restart", RestartLabel, new Vector2(x, by), new Vector2(restartW, bh), Text, BtnFill, enabled: info.Running && !notSaved))
            LuaCommands.Send("restart", info.Name);
        SafeImGui.SetItemTooltip(notSaved ? "Not saved: run it again from the Editor." : "Stop it and start it again from its file.");
        NextLine(p, RowH);
    }

    private static bool IsNotSaved(LuaScriptInfo info) => info.Transient || info.Description == NotSaved;

    private static string RowTooltip(LuaScriptInfo info, bool notSaved)
    {
        string kind = notSaved ? "Not saved (the editor or /lua exec)"
            : info.IsFolder ? $"Folder script ({info.Name}\\index.lua)" : $"Single file ({info.Name}.lua)";
        string desc = notSaved || info.Description.Length == 0 ? string.Empty : info.Description + "\n";
        return $"{info.Name}\n{desc}{kind}\nClick to show its console.";
    }

    private static string AutoLabel(string autostart) => autostart switch
    {
        "global" => "Auto: all",
        "character" => "Auto: char",
        _ => "Auto: off",
    };

    // name  running | stopped (status)                     [Open in editor]
    private void SelectedRow(float w, LuaPayload d, string sel, LuaScriptInfo? si)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 1));
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        const float h = 22;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        if (sel.Length == 0)
        {
            dl.AddText(p + new Vector2(0, (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Mute, "Click a script to see its console.");
            NextLine(p, h);
            return;
        }
        const string open = PhosphorIcons.PencilSimple + " Open in editor";
        float bw = ButtonWidth(open);
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        ImGuiNET.ImGui.PushFont(bold);
        float nameW = ImGuiNET.ImGui.CalcTextSize(sel).X;
        ImGuiNET.ImGui.PopFont();
        bool running = si?.Running == true;
        string state = running ? "running"
            : d.ScriptName.Equals(sel, StringComparison.OrdinalIgnoreCase) && d.Status.Length > 0 ? d.Status : "stopped";
        dl.PushClipRect(p, p + new Vector2(w - bw - 6, h), true);
        dl.AddText(bold, bold.FontSize, p + new Vector2(0, (h - bold.FontSize) * 0.5f), Teal, sel);
        dl.AddText(p + new Vector2(nameW + 10, (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), running ? Green : Amber, state);
        dl.PopClipRect();
        bool canOpen = si != null && !IsNotSaved(si);
        if (Button("##open", open, new Vector2(p.X + w - bw, p.Y), new Vector2(bw, h), Text, BtnFill, enabled: canOpen))
        {
            LuaCommands.Send("load", sel);
            _editorTab = true;
        }
        SafeImGui.SetItemTooltip(canOpen
            ? (si!.IsFolder ? "Edit its index.lua in the Editor tab." : "Edit it in the Editor tab.")
            : "It has no file to open.");
        NextLine(p, h);
    }

    // [Lua to run inside <script>...] [Exec]
    private void ExecRow(float w, string sel, bool canExec)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        const float bw = 50;
        string hint = sel.Length == 0 ? "Select a script to run code in it"
            : canExec ? $"Lua to run inside {sel} (Enter)" : $"Start {sel} to run code in it";
        if (_focusExec)
        {
            ImGuiNET.ImGui.SetKeyboardFocusHere();
            _focusExec = false;
        }
        // The row sits on the body's bottom edge: it stops short of the resize grip (GripInBody).
        float grip = ImGuiPanelHost.BodyGripReserve;
        bool enter = TextBox("##lua_exec", _exec, p, w - bw - 4 - grip, hint, out _, ImGuiInputTextFlags.EnterReturnsTrue);
        bool click = Button("##exec", "Exec", new Vector2(p.X + w - bw - grip, p.Y), new Vector2(bw, 22), Text, BtnFill, enabled: canExec);
        ImGuiNET.ImGui.SetItemTooltip("Runs the line inside the selected script: its globals, its state.");
        if ((enter || click) && canExec)
        {
            string code = Utf8(_exec).Trim();
            if (code.Length > 0)
            {
                LuaCommands.Send("execin", sel, code);
                _exec[0] = 0;
            }
            _focusExec = enter;   // Enter leaves the box; keep typing in it
        }
        NextLine(p, 22);
    }

    // ── v2: Editor tab ───────────────────────────────────────────────────

    private void EditorView(float w, Vector2 origin, Vector2 size, LuaPayload d, List<LuaScriptInfo> infos)
    {
        // RynthLua runs the editor text as a script named by the name box, else "editor".
        string edName = Utf8(_name).Trim();
        string runName = edName.Length > 0 ? edName : "editor";
        LuaScriptInfo? ei = Find(infos, runName);
        bool running = ei?.Running == true;
        // Always say which script the editor holds: the Scripts tab's selection doesn't change it
        // (only "Open in editor" does), so the name here is the one Run/Save act on.
        string editing = "Editing: " + runName + (_editor?.IsDirty == true ? " (unsaved)" : "");
        string status = running ? editing + " · running"
            : d.ScriptName.Equals(runName, StringComparison.OrdinalIgnoreCase) && d.Status.Length > 0 ? editing + " · " + d.Status
            : editing;

        // The console below follows the editor's script, not whatever was last clicked on the
        // Scripts tab. Select it once per change of name (selecting also moves the Scripts tab).
        if (ei != null && !runName.Equals(d.Selected ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && !runName.Equals(_editorSelectSent, StringComparison.OrdinalIgnoreCase))
        {
            LuaCommands.Send("select", ei.Name);
            _editorSelectSent = runName;
        }
        else if (runName.Equals(d.Selected ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            _editorSelectSent = string.Empty;

        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        const float bw = 64;
        float statusW = w - 2 * (bw + 4);
        dl.PushClipRect(p, p + new Vector2(statusW, 24), true);
        dl.AddText(bold, bold.FontSize, p + new Vector2(0, (24 - bold.FontSize) * 0.5f), running ? Green : Amber, status);
        dl.PopClipRect();
        if (Button("##run", RunLabel, new Vector2(p.X + statusW + 4, p.Y), new Vector2(bw, 24), Text, StartBg))
            LuaCommands.Send("run", edName, _editor?.GetText() ?? string.Empty);
        SafeImGui.SetItemTooltip("Run the editor's text as " + runName + " (replaces a running copy).");
        if (Button("##stop", StopLabel, new Vector2(p.X + statusW + bw + 8, p.Y), new Vector2(bw, 24), Text, StopBg, enabled: running))
            LuaCommands.Send("stopScript", runName);
        NextLine(p, 24);

        bool folder = ei?.IsFolder == true;
        NameRow(w, canDelete: !folder, saveTip: folder ? "Saves its index.lua." : null,
            deleteTip: folder ? "Folder scripts are deleted in Explorer." : "Renames it to .lua.bak.");
        EditorAndConsole(w, origin, size, 120, ei != null ? "Console · " + ei.Name
            : d.Selected is { Length: > 0 } s ? "Console · " + s : "Console");
    }

    // ── v1: old single-script payload / not connected ────────────────────

    private void LegacyView(float w, Vector2 origin, Vector2 size)
    {
        TopRow(w);
        Scripts(w);
        NameRow(w, canDelete: true, saveTip: null, deleteTip: null);
        EditorAndConsole(w, origin, size, 150, "Console");
    }

    // Status | Run  Stop
    private void TopRow(float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        const float bw = 64;
        float statusW = w - 2 * (bw + 4);
        dl.PushClipRect(p, p + new Vector2(statusW, 24), true);
        dl.AddText(bold, bold.FontSize, p + new Vector2(0, (24 - bold.FontSize) * 0.5f), _running ? Green : Amber, _status);
        dl.PopClipRect();
        if (Button("##run", RunLabel, new Vector2(p.X + statusW + 4, p.Y), new Vector2(bw, 24), Text, StartBg))
            LuaCommands.Send("run", Utf8(_name).Trim(), _editor?.GetText() ?? string.Empty);
        if (Button("##stop", StopLabel, new Vector2(p.X + statusW + bw + 8, p.Y), new Vector2(bw, 24), Text, StopBg))
            LuaCommands.Send("stop");
        NextLine(p, 24);
    }

    // "Scripts (click to open):" then one button per script, wrapping.
    private void Scripts(float w)
    {
        Label("Scripts (click to open):", Mute);
        var scripts = _data?.Scripts;
        if (scripts == null || scripts.Count == 0)
        {
            Label("(none yet: write one and Save)", Mute);
            return;
        }
        Vector2 start = ImGuiNET.ImGui.GetCursorScreenPos();
        float x = start.X, y = start.Y;
        for (int i = 0; i < scripts.Count; i++)
        {
            string s = scripts[i];
            float bw = ButtonWidth(s);
            if (x > start.X && x + bw > start.X + w) { x = start.X; y += 26; }
            ImGuiNET.ImGui.PushID(i);
            if (Button("##s", s, new Vector2(x, y), new Vector2(bw, 22), Text, BtnFill))
                LuaCommands.Send("load", s);
            ImGuiNET.ImGui.PopID();
            x += bw + 4;
        }
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(start.X, y + 22));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
    }

    // ── Shared pieces ────────────────────────────────────────────────────

    // [name] Save New Delete
    private void NameRow(float w, bool canDelete, string? saveTip, string? deleteTip)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        const float bw = 58;
        float boxW = w - 3 * (bw + 4);
        TextBox("##lua_name", _name, p, boxW, "Script name...", out _);

        float x = p.X + boxW + 4;
        if (Button("##save", PhosphorIcons.FloppyDisk + " Save", new Vector2(x, p.Y), new Vector2(bw, 22), Text, BtnFill))
        {
            string name = Utf8(_name).Trim();
            if (name.Length == 0) _note = "Type a script name first.";
            else
            {
                LuaCommands.Send("save", name, _editor?.GetText() ?? string.Empty);
                _editor?.MarkClean();
            }
        }
        if (saveTip != null) SafeImGui.SetItemTooltip(saveTip);
        x += bw + 4;
        if (Button("##new", PhosphorIcons.FilePlus + " New", new Vector2(x, p.Y), new Vector2(bw, 22), Text, BtnFill))
        {
            _editor?.SetText(string.Empty);
            Array.Clear(_name);
        }
        x += bw + 4;
        if (Button("##delete", PhosphorIcons.Trash + " Delete", new Vector2(x, p.Y), new Vector2(bw, 22), Text, BtnFill, enabled: canDelete))
        {
            string name = Utf8(_name).Trim();
            if (name.Length > 0) LuaCommands.Send("delete", name);
        }
        if (deleteTip != null) SafeImGui.SetItemTooltip(deleteTip);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + 22));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
    }

    // The editor takes what the console leaves.
    private void EditorAndConsole(float w, Vector2 origin, Vector2 size, float consoleH, string consoleTitle)
    {
        // The console below scrolls: the pair ends above the resize grip (GripInBody).
        float editorH = Math.Max(80, Remaining(origin, size) - consoleH - 34 - GripOverlap());
        if (!CodeEditor.Available)
            ImGuiNET.ImGui.TextColored(RynthTheme.Vec(0xFFE04848), "The script editor needs the new cimgui.dll (a full RynthCore deploy).");
        else
        {
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Code12));
            _editor!.Render(new Vector2(w, editorH));
            ImGuiNET.ImGui.PopFont();
        }
        Console(w, consoleH, consoleTitle);
    }

    // Console [Copy] [Clear], then the output (green, wrapped, follows new lines).
    private void Console(float w, float height, string title)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.PushClipRect(p, p + new Vector2(w - 122, 22), true);
        dl.AddText(p + new Vector2(0, (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Mute, title);
        dl.PopClipRect();
        if (Button("##copy", PhosphorIcons.Copy + " Copy", new Vector2(p.X + w - 116, p.Y), new Vector2(56, 22), Text, BtnFill))
            ImGuiNET.ImGui.SetClipboardText(_data?.Console ?? string.Empty);
        if (Button("##clear", PhosphorIcons.Eraser + " Clear", new Vector2(p.X + w - 56, p.Y), new Vector2(56, 22), Text, BtnFill))
            LuaCommands.Send("clearConsole");
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + 24));

        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, ConsoleBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4, 4));
        ImGuiNET.ImGui.BeginChild("##lua_console", new Vector2(w, height), ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(2);
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Mono10));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Green);
        ImGuiNET.ImGui.PushTextWrapPos(0);
        string text = _data?.Console ?? string.Empty;
        if (text.Length > 0) ImGuiNET.ImGui.TextUnformatted(text);
        if (_note.Length > 0) SafeImGui.TextColored(RynthTheme.VecOf(RynthTheme.Highlight), "[panel] " + _note);
        ImGuiNET.ImGui.PopTextWrapPos();
        ImGuiNET.ImGui.PopStyleColor();
        ImGuiNET.ImGui.PopFont();
        if (_scrollConsole)
        {
            ImGuiNET.ImGui.SetScrollHereY(1f);
            _scrollConsole = false;
        }
        ImGuiNET.ImGui.EndChild();
    }

    private static LuaScriptInfo? Find(List<LuaScriptInfo> infos, string name)
    {
        if (name.Length == 0) return null;
        foreach (LuaScriptInfo i in infos)
            if (i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }
}

// ============================================================================
//  RynthCore.Engine - ImGui/Panels/P1Faces.cs
//  ImGui faces of the Status, Log and Tracker panels
//  (docs/IMGUI_PARITY_PLAN.md §2.11, §2.12). Each mirrors its Avalonia face in
//  UI/Panels and reads the same UiDataHub snapshot. AC thread only; strings
//  are built when a snapshot changes, not per frame.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.Plugins;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal static class P1Faces
{
    /// <summary>Registers the P1 faces next to their Avalonia faces (EntryPoint).</summary>
    public static void Register(bool trackerPresent)
    {
        // Status and Log end in a footer of left-aligned buttons: the resize grip
        // sits beside it, over the body (GripInBody), instead of in a strip below.
        var standard = new PanelSpec(new Vector2(400, 500), new Vector2(320, 240), GripInBody: true);
        ImGuiPanelHost.Register("Status", standard, () => new StatusFace());
        ImGuiPanelHost.Register("Log", standard, () => new LogFace());
        if (trackerPresent)
            ImGuiPanelHost.Register("Tracker",
                new PanelSpec(new Vector2(165, 190), new Vector2(60, 20), Background: 0, GripInBody: true),
                () => new TrackerFace());
        // The Skills panel: an engine panel with only an ImGui face, needing no plugin.
        // Registered here so engine init needs no new line in EntryPoint; ImGuiBar gives
        // it a bar button (ImGuiOnlyPanels).
        SkillsFace.Register();
        // Sense (watch list of names, anywhere the client knows): ImGui only, like Skills.
        SenseFace.Register();
        // Item Count and Mini Remote (items by name with their carried count; a grid of
        // items to use): ImGui only, saved per character in item_huds.txt.
        ItemCountFace.Register();
        MiniRemoteFace.Register();
        // Report a problem (Report/ProblemReport.cs): ImGui only, opened from the bar's grip
        // menu, Settings and /rc report (no bar button of its own).
        ReportFace.Register();
    }
}

/// <summary>Status: engine facts, plugin rows, and each panel's face.</summary>
internal sealed class StatusFace : IImGuiPanel
{
    private static readonly Vector4 HeaderColor = RynthTheme.Vec(0xC88CC8FF);
    private static readonly Vector4 RowColor = RynthTheme.Vec(0xC8D2D2D2);
    private static readonly Vector4 ErrorColor = RynthTheme.Vec(0xDCFF6464);

    private string[] _engineRows = Array.Empty<string>();
    private long _pluginVersion = -1;
    private string _pluginHeader = "Plugins";
    private readonly List<(string Text, bool Error)> _pluginRows = new();
    private List<string> _faceRows = new();
    private long _faceRowsTick;

    public void OnShown()
    {
        UiSources.PluginStatus.Subscribe();
        UiSources.PluginStatus.RequestRefresh();
        BuildEngineRows();
    }

    private void BuildEngineRows()
    {
        _engineRows = new[]
        {
            $"Version: {EntryPoint.BuildStamp}",
            $"Game HWND: 0x{EntryPoint.GameHwnd:X8}",
            $"Plugin dir: {PluginManager.PluginDirectory}",
        };
    }

    public void OnHidden() => UiSources.PluginStatus.Unsubscribe();

    public void Draw()
    {
        RefreshCaches();
        float footer = ImGuiNET.ImGui.GetFrameHeightWithSpacing();
        ImGuiNET.ImGui.BeginChild("##status", new Vector2(0, -footer));
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));

        Header("Engine");
        foreach (string row in _engineRows) Row(row, false);
        Separator();
        Header(_pluginHeader);
        if (_pluginRows.Count == 0) Row("None loaded", false);
        foreach (var (text, error) in _pluginRows) Row(text, error);
        Separator();
        Header("UI faces");
        foreach (string row in _faceRows) Row(row, false);

        ImGuiNET.ImGui.PopFont();
        ImGuiNET.ImGui.EndChild();

        if (ImGuiNET.ImGui.Button(PhosphorIcons.ArrowsClockwise + " Refresh"))
        {
            UiSources.PluginStatus.RequestRefresh();
            _faceRowsTick = 0;
            BuildEngineRows();
        }
    }

    private void RefreshCaches()
    {
        var snap = UiSources.PluginStatus.Current;
        if (snap != null && snap.Version != _pluginVersion)
        {
            _pluginVersion = snap.Version;
            _pluginHeader = $"Plugins ({snap.Value.Length})";
            _pluginRows.Clear();
            foreach (PluginStatusRow r in snap.Value)
                _pluginRows.Add(($"{r.Label}: {r.State}", r.IsError));
        }

        long now = Environment.TickCount64;
        if (now - _faceRowsTick > 2000)
        {
            _faceRowsTick = now;
            _faceRows = PanelRouter.Describe();
        }
    }

    private static void Header(string text)
    {
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));
        SafeImGui.TextColored(HeaderColor, text);
        ImGuiNET.ImGui.PopFont();
    }

    private static void Row(string text, bool error)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, error ? ErrorColor : RowColor);
        SafeImGui.TextWrapped(text);
        ImGuiNET.ImGui.PopStyleColor();
    }

    private static void Separator()
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float w = ImGuiNET.ImGui.GetContentRegionAvail().X;
        ImGuiNET.ImGui.GetWindowDrawList().AddLine(p, p + new Vector2(w, 0), RynthTheme.Argb(0x3CFFFFFF));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
    }
}

/// <summary>Log: the engine's recent-log ring, monospace, with Auto-scroll and Clear.</summary>
internal sealed unsafe class LogFace : IImGuiPanel
{
    private ImGuiListClipper* _clipper;
    private bool _autoScroll = true;
    private long _clearedSeq;
    private long _seenVersion = -1;

    public void OnShown()
    {
        UiSources.Log.Subscribe();
        UiSources.Log.RequestRefresh();
        _clipper = ImGuiNative.ImGuiListClipper_ImGuiListClipper();
    }

    public void OnHidden()
    {
        UiSources.Log.Unsubscribe();
        if (_clipper != null) ImGuiNative.ImGuiListClipper_destroy(_clipper);
        _clipper = null;
    }

    public void Draw()
    {
        var snap = UiSources.Log.Current;
        bool changed = snap != null && snap.Version != _seenVersion;
        if (snap != null) _seenVersion = snap.Version;

        float footer = ImGuiNET.ImGui.GetFrameHeightWithSpacing();
        ImGuiNET.ImGui.BeginChild("##lines", new Vector2(0, -footer));
        if (snap != null && _clipper != null)
        {
            LogSnapshot log = snap.Value;
            // Clear hides everything up to the sequence number it saw, so it keeps
            // working after the 256-line ring wraps (Avalonia bug A6).
            int start = (int)Math.Clamp(_clearedSeq - log.FirstSeq + 1, 0, log.Lines.Length);
            int count = log.Lines.Length - start;

            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Mono10));
            var clipper = new ImGuiListClipperPtr(_clipper);
            clipper.Begin(count);
            while (clipper.Step())
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    ImGuiNET.ImGui.TextUnformatted(log.Lines[start + i]);
            clipper.End();
            ImGuiNET.ImGui.PopFont();

            if (changed && _autoScroll)
                ImGuiNET.ImGui.SetScrollHereY(1f);
        }
        ImGuiNET.ImGui.EndChild();

        ImGuiNET.ImGui.Checkbox("Auto-scroll", ref _autoScroll);
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.Button(PhosphorIcons.Eraser + " Clear"))
            _clearedSeq = snap?.Value.LastSeq ?? EntryPoint.RecentLogSeq;
    }
}

/// <summary>
/// Tracker: the session-stat rows (TrackerSource) over an adjustable-opacity background, or text only
/// (TrackerSettings.TextOnly: bare, the Ledger, clicks pass through except over it, or everywhere with
/// TrackerSettings.ClickThrough unless Ctrl is held). Rows can be hidden (the gear menu, TrackerSettings.Stats).
/// </summary>
internal sealed class TrackerFace : IImGuiPanel
{
    private static readonly Vector4 TitleColor = RynthTheme.Vec(0xFFE6B450);
    private static readonly Vector4 LabelColor = RynthTheme.Vec(0xFF999999);
    private static readonly uint ResetBg = RynthTheme.Argb(0xFF1A2E40);
    private static uint ResetBorder => RynthTheme.AccentFill;

    // Text only, the Ledger (2026-11-12, the owner: "something similar to how our Lua script works for
    // item tracking ... no windows or background. Just text"; picked "Ledger", tinted, from the
    // artifact "Bare Tracker"). Each label right-aligned beside its number, the rates, a gap, then the
    // totals; numbers of a thousand and up shortened (3.12M, 148k); the rates tinted by what they
    // count, deaths green at 0 and red above; every letter in a dark halo so it reads on any ground.
    private static readonly uint LedgerLabel = RynthTheme.Argb(0xFFD8D8D8);
    private static readonly uint LedgerValue = RynthTheme.Argb(0xFFFFFFFF);
    private static readonly uint LedgerDim = RynthTheme.Argb(0xFFBDBDBD);
    private static readonly uint LedgerXp = RynthTheme.Argb(0xFFEFC054);
    private static readonly uint LedgerLum = RynthTheme.Argb(0xFF7FB9F9);
    private static readonly uint LedgerKill = RynthTheme.Argb(0xFFF98075);
    private static readonly uint LedgerPass = RynthTheme.Argb(0xFFC5ABF4);
    private static readonly uint LedgerRad = RynthTheme.Argb(0xFF62D5C8);
    private static readonly uint LedgerOk = RynthTheme.Argb(0xFF73D885);
    // The halo: the four sides firm, the corners soft.
    private static readonly uint HaloNear = RynthTheme.Argb(0xE6000000);
    private static readonly uint HaloFar = RynthTheme.Argb(0x8C000000);
    private static readonly Vector2[] HaloSides = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };
    private static readonly Vector2[] HaloCorners = { new(1, 1), new(-1, -1), new(1, -1), new(-1, 1) };
    private const float LedgerGap = 8f, LedgerGroupGap = 5f, ToolsGap = 10f;

    // The Ledger's rows, worked out when the snapshot or the hidden set changes (not per frame).
    private TrackerSnapshot? _ledgerFor;
    private bool _ledgerBuilt;
    private int _ledgerHiddenVersion;
    private string[] _ledgerLabels = Array.Empty<string>(), _ledgerValues = Array.Empty<string>();
    private uint[] _ledgerColors = Array.Empty<uint>();
    private int _ledgerTotalsAt = -1;   // the first shown total's row (the gap goes above it)
    private int _opacity;
    private const string StatsMenuId = "##tracker_stats";
    private static readonly string AllHiddenText = "All stats hidden (" + PhosphorIcons.Gear + ")";

    // Text only: the mouse is over what the last frame drew, so the window takes input (to drag it, or
    // for the buttons); elsewhere clicks reach the game. A press that began while it took input keeps it.
    // With full click-through (TrackerSettings.ClickThrough) it takes input only while Ctrl is held.
    private bool _hot, _pressHot;

    /// <summary>Text only is bare: docked, no frame, title, background or grip (popped out it is framed as usual).</summary>
    public bool Bare => TrackerSettings.TextOnly;

    public bool ClickThrough
    {
        get
        {
            if (!Bare) return false;
            if (_pressHot) return false;   // a press of ours: keep the mouse until the button is up
            if (!TrackerSettings.ClickThrough) return !_hot;
            // Full click-through (2026-11-12): only Ctrl over the Ledger takes the mouse, and not in the
            // middle of a press the game already has (Ctrl pressed while holding a game click).
            if (!_hot || !ImGuiNET.ImGui.GetIO().KeyCtrl) return true;
            return ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left) && !ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        }
    }

    public void OnShown()
    {
        UiSources.Tracker.Subscribe();
        UiSources.Tracker.RequestRefresh();
        _opacity = (int)Math.Round(TrackerSettings.BgAlpha / 2.55);
    }

    public void OnHidden() => UiSources.Tracker.Unsubscribe();

    public void Draw()
    {
        if (TrackerSettings.TextOnly && !ImGuiPanelHost.DrawingEmbedded && !ImGuiPopOuts.InPopOutFrame)
        {
            DrawTextOnly();
            return;
        }
        _hot = _pressHot = false;
        DrawFramed();
    }

    /// <summary>
    /// Text only, the Ledger: each row's label right-aligned beside its number, in a dark halo, nothing
    /// behind them. The rows are a drag handle. While the mouse is on them four small buttons show at
    /// the end of the first row, Reset, click-through, the stats menu and the frame; the rest of the time
    /// it is only the text. With full click-through the rows and buttons need Ctrl held.
    /// </summary>
    private void DrawTextOnly()
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 origin = ImGuiNET.ImGui.GetCursorScreenPos();
        float width = Math.Max(60, ImGuiNET.ImGui.GetContentRegionAvail().X);
        Vector2 mouse = ImGuiNET.ImGui.GetIO().MousePos;
        bool locked = TrackerSettings.ClickThrough;
        // Whether the host gave this window the mouse this frame (the same test it made before Begin).
        bool takingInput = !ClickThrough;
        bool showTools = locked ? takingInput : _hot || _pressHot;

        TrackerSnapshot? snap = UiSources.Tracker.Current?.Value;
        BuildLedger(snap);

        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Narrow10));
        ImFontPtr labelFont = ImGuiNET.ImGui.GetFont();
        float labelSize = ImGuiNET.ImGui.GetFontSize();
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Mono10));
        ImFontPtr valueFont = ImGuiNET.ImGui.GetFont();
        float valueSize = ImGuiNET.ImGui.GetFontSize();
        float rowH = Math.Max(labelSize, valueSize) + 1;
        float labelDrop = MathF.Round((valueSize - labelSize) * 0.6f);   // the labels sit on the numbers' baseline

        float labelW = 0, valueW = 0;
        for (int i = 0; i < _ledgerLabels.Length; i++)
        {
            labelW = Math.Max(labelW, labelFont.CalcTextSizeA(labelSize, float.MaxValue, 0f, _ledgerLabels[i]).X);
            valueW = Math.Max(valueW, valueFont.CalcTextSizeA(valueSize, float.MaxValue, 0f, _ledgerValues[i]).X);
        }
        float valueX = origin.X + MathF.Ceiling(labelW) + LedgerGap;
        float y = origin.Y;
        for (int i = 0; i < _ledgerLabels.Length; i++)
        {
            if (i == _ledgerTotalsAt && i > 0) y += LedgerGroupGap;
            float lw = labelFont.CalcTextSizeA(labelSize, float.MaxValue, 0f, _ledgerLabels[i]).X;
            Haloed(dl, labelFont, labelSize, new Vector2(MathF.Round(origin.X + labelW - lw), y + labelDrop), LedgerLabel, _ledgerLabels[i]);
            Haloed(dl, valueFont, valueSize, new Vector2(valueX, y), _ledgerColors[i], _ledgerValues[i]);
            y += rowH;
        }
        ImGuiNET.ImGui.PopFont();
        ImGuiNET.ImGui.PopFont();
        float blockW = Math.Max(1, valueX + valueW - origin.X);
        float height = Math.Max(rowH, y - origin.Y);

        // The rows: a drag handle (the window has no title bar to hold).
        ImGuiNET.ImGui.SetCursorScreenPos(origin);
        ImGuiNET.ImGui.InvisibleButton("##tracker_textonly_drag", new Vector2(blockW, height));
        ImGuiPanelHost.DragWindowWithLastItem();
        if (ImGuiNET.ImGui.IsItemHovered() && !ImGuiNET.ImGui.IsItemActive())
            ImGuiNET.ImGui.SetTooltip(locked ? "Ctrl-drag to move the Tracker (click-through is on)." : "Drag to move the Tracker.");

        // Reset, click-through, the stats menu and the frame, only while the mouse is on the Tracker
        // (with click-through, while Ctrl is held there): at the end of the first row (inside the
        // window when it is narrower than that).
        float buttonW = rowH + 4;
        const int ToolCount = 4;
        float toolsW = ToolCount * buttonW + (ToolCount - 1) * 2;
        float toolsX = Math.Max(origin.X, Math.Min(origin.X + blockW + ToolsGap, origin.X + width - toolsW));
        if (showTools)
        {
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));   // the icons' font, as the framed look's buttons
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(1, 0));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Button, ResetBg);
            var size = new Vector2(buttonW, rowH);
            float x = toolsX;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, origin.Y));
            if (ImGuiNET.ImGui.Button(PhosphorIcons.ArrowCounterClockwise + "##tracker_reset", size))
                UiSources.Tracker.RequestReset();
            ImGuiNET.ImGui.SetItemTooltip("Reset the session stats");
            x += buttonW + 2;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, origin.Y));
            if (locked) ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Button, RynthTheme.AccentFill);
            bool lockClicked = ImGuiNET.ImGui.Button((locked ? PhosphorIcons.Lock : PhosphorIcons.LockOpen) + "##tracker_clickthrough", size);
            if (locked) ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.SetItemTooltip(locked ? ClickThroughOnTip : ClickThroughOffTip);
            if (lockClicked) TrackerSettings.SetClickThrough(!locked);
            x += buttonW + 2;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, origin.Y));
            if (ImGuiNET.ImGui.Button(PhosphorIcons.Gear + "##tracker_statsbtn", size))
                ImGuiNET.ImGui.OpenPopup(StatsMenuId);
            ImGuiNET.ImGui.SetItemTooltip("Choose which stats the Tracker shows");
            x += buttonW + 2;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, origin.Y));
            if (ImGuiNET.ImGui.Button(PhosphorIcons.SquaresFour + "##tracker_frame", size))
                TrackerSettings.SetTextOnly(false);
            ImGuiNET.ImGui.SetItemTooltip("Show the Tracker's frame again (its background and opacity).");
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.PopStyleVar();
            ImGuiNET.ImGui.PopFont();
        }
        DrawStatsMenu();

        // Hot for the next frame: the mouse over the rows, or over the buttons while they show.
        bool overRows = mouse.X >= origin.X && mouse.X <= origin.X + blockW && mouse.Y >= origin.Y && mouse.Y <= origin.Y + height;
        bool overTools = showTools && mouse.X >= origin.X + blockW && mouse.X <= toolsX + toolsW && mouse.Y >= origin.Y && mouse.Y <= origin.Y + rowH;
        bool over = overRows || overTools;
        _hot = over;
        bool down = ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left);
        if (!down) _pressHot = false;
        // Only a press this window took (a game click over the text never grabs the mouse mid-press).
        else if (over && takingInput && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left)) _pressHot = true;
    }

    private const string ClickThroughOffTip =
        "Click-through: every click reaches the game, even over the numbers.\nHold Ctrl to move it or reach these buttons.";
    private const string ClickThroughOnTip =
        "Click-through is on: every click reaches the game, even over the numbers.\nHold Ctrl to move it or reach these buttons. Click to turn it off.";

    /// <summary>
    /// The stats menu (2026-11-12, the owner: "a small settings menu to show/hide some of the stats"): a
    /// box per row the Tracker can show, Show all, and click-through. Opened by the gear in either look.
    /// </summary>
    private static void DrawStatsMenu()
    {
        if (!ImGuiNET.ImGui.BeginPopup(StatsMenuId)) return;
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
        ImGuiNET.ImGui.TextColored(TitleColor, "Show on the Tracker");
        foreach (TrackerStat s in TrackerSettings.Stats)
        {
            bool shown = !TrackerSettings.IsHidden(s.Id);
            ImGuiNET.ImGui.PushID(s.Id);
            if (ImGuiNET.ImGui.Checkbox(s.Name, ref shown))
                TrackerSettings.SetHidden(s.Id, !shown);
            if (s.Note != null)
                ImGuiNET.ImGui.SetItemTooltip(s.Note);
            ImGuiNET.ImGui.PopID();
        }
        if (ImGuiNET.ImGui.Button("Show all"))
            TrackerSettings.ShowAll();
        ImGuiNET.ImGui.Separator();
        bool clickThrough = TrackerSettings.ClickThrough;
        if (ImGuiNET.ImGui.Checkbox("Click-through (text only)", ref clickThrough))
            TrackerSettings.SetClickThrough(clickThrough);
        ImGuiNET.ImGui.SetItemTooltip("Text only, every click reaches the game, even over the numbers.\nHold Ctrl to move it or reach its buttons.");
        ImGuiNET.ImGui.PopFont();
        ImGuiNET.ImGui.EndPopup();
    }

    /// <summary>
    /// The Ledger's labels, numbers and colours for <paramref name="snap"/> without the hidden rows;
    /// only when the snapshot or the hidden set changed. Every row hidden leaves one dim line saying so.
    /// </summary>
    private void BuildLedger(TrackerSnapshot? snap)
    {
        int hiddenVersion = TrackerSettings.HiddenVersion;
        if (_ledgerBuilt && ReferenceEquals(snap, _ledgerFor) && hiddenVersion == _ledgerHiddenVersion) return;
        _ledgerBuilt = true;
        _ledgerFor = snap;
        _ledgerHiddenVersion = hiddenVersion;
        string[] labels = snap?.Labels ?? TrackerSnapshot.DefaultLabels;
        string[]? values = snap?.Values;
        var names = new List<string>(labels.Length);
        var shownValues = new List<string>(labels.Length);
        var colors = new List<uint>(labels.Length);
        int totalsFrom = Array.IndexOf(labels, "XP");   // the rates come before XP, the totals from it
        _ledgerTotalsAt = -1;
        for (int i = 0; i < labels.Length; i++)
        {
            if (TrackerSettings.IsLabelHidden(labels[i])) continue;
            string value = values != null && i < values.Length ? values[i] : "—";
            // The gap goes above the first total shown, and only with a rate shown above it.
            if (_ledgerTotalsAt < 0 && totalsFrom >= 0 && i >= totalsFrom) _ledgerTotalsAt = names.Count;
            names.Add(LedgerName(labels[i]));
            shownValues.Add(Shorten(value));
            colors.Add(LedgerColor(labels[i], value));
        }
        if (names.Count == 0)
        {
            names.Add("TRACKER");
            shownValues.Add("all stats hidden");
            colors.Add(LedgerDim);
        }
        _ledgerLabels = names.ToArray();
        _ledgerValues = shownValues.ToArray();
        _ledgerColors = colors.ToArray();
    }

    /// <summary>The Ledger's name for a row: in capitals, with the framed look's abbreviations spelled out.</summary>
    internal static string LedgerName(string label) => label switch
    {
        "Kls/hr" => "KILLS/HR",
        "XP/kl" => "XP/KILL",
        _ => label.ToUpperInvariant(),
    };

    /// <summary>
    /// A number of a thousand and up as three figures and a unit (1.23k, 12.3k, 148k, 3.12M, 1.05B);
    /// anything else (the session clock, a rate with a decimal, "max level", "—") as it is.
    /// </summary>
    internal static string Shorten(string value)
    {
        if (value.IndexOf(',') < 0 || !long.TryParse(value.Replace(",", ""), System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out long n))
            return value;
        string[] units = { "", "k", "M", "B", "T" };
        double x = n;
        int u = 0;
        while (Math.Abs(x) >= 1000 && u < units.Length - 1) { x /= 1000; u++; }
        string s = Three(x);
        if (Math.Abs(double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)) >= 1000 && u < units.Length - 1)
        {
            x /= 1000;   // 999,999 rounds to 1000k: one unit up instead
            u++;
            s = Three(x);
        }
        return s + units[u];

        static string Three(double v)
        {
            double a = Math.Abs(v);
            string format = a < 10 ? "0.##" : a < 100 ? "0.#" : "0";
            return v.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>A row's colour: the rates by what they count, deaths green at 0 and red above, the clock dim, totals white.</summary>
    private static uint LedgerColor(string label, string value) => label switch
    {
        "Session" => LedgerDim,
        "XP/hr" => LedgerXp,
        "Lum/hr" or "Lum/5m" => LedgerLum,
        "Kls/hr" => LedgerKill,
        "Pass/hr" => LedgerPass,
        "Rad/hr" => LedgerRad,
        "Deaths" => value == "0" ? LedgerOk : LedgerKill,
        _ => LedgerValue,
    };

    /// <summary>Text in a dark halo: the four sides firm, the corners soft, then the text.</summary>
    private static void Haloed(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 p, uint color, string text)
    {
        foreach (Vector2 o in HaloCorners) dl.AddText(font, size, p + o, HaloFar, text);
        foreach (Vector2 o in HaloSides) dl.AddText(font, size, p + o, HaloNear, text);
        dl.AddText(font, size, p, color, text);
    }

    private void DrawFramed()
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        // Background behind the content only (like the Avalonia StackPanel), so
        // content goes on channel 1 and the rect, sized afterwards, on channel 0.
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        Vector2 top = ImGuiNET.ImGui.GetCursorScreenPos() - ImGuiNET.ImGui.GetStyle().WindowPadding;
        float width = ImGuiNET.ImGui.GetWindowWidth();

        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));

        // Title row: [Tracker] [===slider===] [text only] [stats menu] [reset]
        ImGuiNET.ImGui.TextColored(TitleColor, "Tracker");
        ImGuiNET.ImGui.SameLine();
        float resetW = ImGuiNET.ImGui.CalcTextSize(PhosphorIcons.ArrowCounterClockwise).X + 8;
        float textOnlyW = ImGuiNET.ImGui.CalcTextSize(PhosphorIcons.TextAa).X + 8;
        float gearW = ImGuiNET.ImGui.CalcTextSize(PhosphorIcons.Gear).X + 8;
        float spacing = ImGuiNET.ImGui.GetStyle().ItemSpacing.X;
        ImGuiNET.ImGui.SetNextItemWidth(Math.Max(10, ImGuiNET.ImGui.GetContentRegionAvail().X - resetW - textOnlyW - gearW - 8 - spacing));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 0));
        if (ImGuiNET.ImGui.SliderInt("##opacity", ref _opacity, 0, 100, ""))
            TrackerSettings.SetBgAlpha((byte)Math.Round(_opacity * 2.55), save: false);
        if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit())
            TrackerSettings.SetBgAlpha((byte)Math.Round(_opacity * 2.55), save: true);
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Button, ResetBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, ResetBorder);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 1));
        bool canBare = !ImGuiPanelHost.DrawingEmbedded && !ImGuiPopOuts.InPopOutFrame;
        ImGuiNET.ImGui.BeginDisabled(!canBare);
        if (ImGuiNET.ImGui.Button(PhosphorIcons.TextAa + "##textonly"))
            TrackerSettings.SetTextOnly(true);
        ImGuiNET.ImGui.EndDisabled();
        ImGuiNET.ImGui.SetItemTooltip(canBare
            ? (TrackerSettings.ClickThrough
                ? "Text only: just the numbers, no frame or background. Click-through is on: every click reaches the game,\neven over the numbers; hold Ctrl over them to move it or for its buttons (Reset, click-through, stats, frame)."
                : "Text only: just the numbers, no frame or background (clicks pass through to the game except over the numbers).\nHover them for Reset, click-through, the stats menu and the button that brings the frame back.")
            : "Text only works while the Tracker is docked (not popped out).");
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.Button(PhosphorIcons.Gear + "##stats"))
            ImGuiNET.ImGui.OpenPopup(StatsMenuId);
        ImGuiNET.ImGui.SetItemTooltip("Choose which stats the Tracker shows, and click-through for text only");
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.Button(PhosphorIcons.ArrowCounterClockwise + "##reset"))
            UiSources.Tracker.RequestReset();
        ImGuiNET.ImGui.SetItemTooltip("Reset the session stats");
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(2);

        TrackerSnapshot? snap = UiSources.Tracker.Current?.Value;
        string[]? values = snap?.Values;
        string[] labels = snap?.Labels ?? TrackerSnapshot.DefaultLabels;
        // The resize grip sits over the body's bottom-right corner (GripInBody): a
        // row that reaches down into it right-aligns its value short of it.
        float grip = ImGuiPanelHost.BodyGripReserve;
        float gripTop = grip > 0 ? ImGuiNET.ImGui.GetWindowPos().Y + ImGuiNET.ImGui.GetWindowHeight() - grip : float.MaxValue;
        float rowH = ImGuiNET.ImGui.GetTextLineHeight();
        int drawn = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            if (TrackerSettings.IsLabelHidden(labels[i])) continue;
            drawn++;
            float rowRight = ImGuiNET.ImGui.GetCursorPosX() + ImGuiNET.ImGui.GetContentRegionAvail().X;
            if (ImGuiNET.ImGui.GetCursorScreenPos().Y + rowH > gripTop) rowRight -= grip;
            SafeImGui.TextColored(LabelColor, labels[i]);
            if (labels[i].StartsWith("Pass", StringComparison.Ordinal))
                ImGuiNET.ImGui.SetItemTooltip("Luminance that reached your bank without a \"You've banked\" line: your vassals' pass-up.\nFellowship and Zone Share splits arrive the same silent way and are included.");
            else if (labels[i] == "Lum/5m")
                ImGuiNET.ImGui.SetItemTooltip("Luminance earned in the last five minutes.");
            string value = values != null && i < values.Length ? values[i] : "—";
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Mono9));
            float w = ImGuiNET.ImGui.CalcTextSize(value).X;
            ImGuiNET.ImGui.SameLine(Math.Max(0, rowRight - w));
            ImGuiNET.ImGui.TextUnformatted(value);
            ImGuiNET.ImGui.PopFont();
        }
        if (drawn == 0)
            ImGuiNET.ImGui.TextDisabled(AllHiddenText);
        ImGuiNET.ImGui.PopFont();

        float bottom = ImGuiNET.ImGui.GetCursorScreenPos().Y;
        dl.ChannelsSetCurrent(0);
        uint bg = RynthTheme.Argb(((uint)TrackerSettings.BgAlpha << 24) | 0x0A121A);
        dl.AddRectFilled(top, new Vector2(top.X + width, bottom), bg);
        dl.ChannelsMerge();
        DrawStatsMenu();
    }
}

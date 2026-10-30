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

/// <summary>Tracker: the session-stat rows (TrackerSource) over an adjustable-opacity background.</summary>
internal sealed class TrackerFace : IImGuiPanel
{
    private static readonly Vector4 TitleColor = RynthTheme.Vec(0xFFE6B450);
    private static readonly Vector4 LabelColor = RynthTheme.Vec(0xFF999999);
    private static readonly uint ResetBg = RynthTheme.Argb(0xFF1A2E40);
    private static uint ResetBorder => RynthTheme.AccentFill;
    private int _opacity;

    public void OnShown()
    {
        UiSources.Tracker.Subscribe();
        UiSources.Tracker.RequestRefresh();
        _opacity = (int)Math.Round(TrackerSettings.BgAlpha / 2.55);
    }

    public void OnHidden() => UiSources.Tracker.Unsubscribe();

    public void Draw()
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        // Background behind the content only (like the Avalonia StackPanel), so
        // content goes on channel 1 and the rect, sized afterwards, on channel 0.
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        Vector2 top = ImGuiNET.ImGui.GetCursorScreenPos() - ImGuiNET.ImGui.GetStyle().WindowPadding;
        float width = ImGuiNET.ImGui.GetWindowWidth();

        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));

        // Title row: [Tracker] [===slider===] [reset]
        ImGuiNET.ImGui.TextColored(TitleColor, "Tracker");
        ImGuiNET.ImGui.SameLine();
        float resetW = ImGuiNET.ImGui.CalcTextSize(PhosphorIcons.ArrowCounterClockwise).X + 8;
        ImGuiNET.ImGui.SetNextItemWidth(Math.Max(10, ImGuiNET.ImGui.GetContentRegionAvail().X - resetW - 4));
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
        for (int i = 0; i < labels.Length; i++)
        {
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
        ImGuiNET.ImGui.PopFont();

        float bottom = ImGuiNET.ImGui.GetCursorScreenPos().Y;
        dl.ChannelsSetCurrent(0);
        uint bg = RynthTheme.Argb(((uint)TrackerSettings.BgAlpha << 24) | 0x0A121A);
        dl.AddRectFilled(top, new Vector2(top.X + width, bottom), bg);
        dl.ChannelsMerge();
    }
}

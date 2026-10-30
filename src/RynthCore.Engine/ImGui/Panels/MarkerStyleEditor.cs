// ============================================================================
//  RynthCore.Engine - ImGui/Panels/MarkerStyleEditor.cs
//  The per-kind "Name / Size / Text" controls of MarkerStyleSettings, shared by the
//  Radar's settings window (one row per kind, after its show box and colour) and
//  the Dungeon Map's Markers window. Sliders report a change once the drag ends
//  (so the settings file is written once, not per step); the name box at once.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI.Panels;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal static class MarkerStyleEditor
{
    public const float SizeSliderWidth = 74f;

    /// <summary>"Name  Size  Text" column titles, at the given offsets from the window's left edge.</summary>
    public static void HeaderRow(float xName, float xSize, float xText)
    {
        ImGuiNET.ImGui.SetCursorPosX(xName);
        ImGuiNET.ImGui.TextDisabled("Name");
        if (ImGuiNET.ImGui.IsItemHovered())
            ImGuiNET.ImGui.SetTooltip("Draw the object's name beside its marker.");
        ImGuiNET.ImGui.SameLine(xSize);
        ImGuiNET.ImGui.TextDisabled("Size");
        if (ImGuiNET.ImGui.IsItemHovered())
            ImGuiNET.ImGui.SetTooltip("How big the marker is drawn. Ctrl+click a slider to type a value.");
        ImGuiNET.ImGui.SameLine(xText);
        ImGuiNET.ImGui.TextDisabled("Text");
        if (ImGuiNET.ImGui.IsItemHovered())
            ImGuiNET.ImGui.SetTooltip("How big its name is drawn, in pixels.");
    }

    /// <summary>
    /// One kind's name box, marker size and name size on the current row (the caller has pushed an id
    /// for the kind). True when something was changed and should be saved.
    /// </summary>
    public static bool KindCells(MarkerStyleSettings st, byte kind, float xName, float xSize, float xText)
    {
        bool changed = false;
        ImGuiNET.ImGui.SameLine(xName);
        changed |= ImGuiNET.ImGui.Checkbox("##name", ref st.ShowName[kind]);
        ImGuiNET.ImGui.SameLine(xSize);
        changed |= Slider("##size", ref st.Size[kind], MarkerStyleSettings.MinSize, MarkerStyleSettings.MaxSize, "%.1fx", SizeSliderWidth);
        ImGuiNET.ImGui.SameLine(xText);
        // Never greyed out: set the name's size first, switch the name on after (or the other way round).
        changed |= Slider("##text", ref st.NameSize[kind], MarkerStyleSettings.MinName, MarkerStyleSettings.MaxName, "%.0f px", SizeSliderWidth);
        return changed;
    }

    /// <summary>A labelled slider on its own row: the label, then the slider at <paramref name="x"/>.</summary>
    public static bool SliderRow(string label, ref float value, float min, float max, string fmt, float x)
    {
        ImGuiNET.ImGui.AlignTextToFramePadding();
        ImGuiNET.ImGui.TextUnformatted(label);
        ImGuiNET.ImGui.SameLine(x);
        ImGuiNET.ImGui.PushID(label);
        bool changed = Slider("##v", ref value, min, max, fmt, SizeSliderWidth);
        ImGuiNET.ImGui.PopID();
        return changed;
    }

    private static bool Slider(string id, ref float value, float min, float max, string fmt, float width)
    {
        ImGuiNET.ImGui.SetNextItemWidth(width);
        bool moved = ImGuiNET.ImGui.SliderFloat(id, ref value, min, max, fmt, ImGuiSliderFlags.AlwaysClamp);
        if (!float.IsFinite(value)) value = min;
        // Save once the drag ends, not on every step (a typed value commits when it is accepted).
        return moved && !ImGuiNET.ImGui.IsItemActive() || ImGuiNET.ImGui.IsItemDeactivatedAfterEdit();
    }
}

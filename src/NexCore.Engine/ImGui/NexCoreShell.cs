using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace NexCore.Engine.ImGuiBackend;

internal static class NexCoreShell
{
    private static readonly string[] PluginNames =
    [
        "NexAi",
        "NexTank Compatibility",
        "Core Hooks",
        "Telemetry"
    ];

    private static readonly string[] PluginStatuses =
    [
        "Planned adoption target",
        "Legacy source reference",
        "Internal service layer",
        "Host diagnostics stream"
    ];

    private static readonly Vector4 Accent = new(0.12f, 0.78f, 0.67f, 1.00f);
    private static readonly Vector4 AccentSoft = new(0.12f, 0.78f, 0.67f, 0.20f);
    private static readonly Vector4 Gold = new(0.88f, 0.69f, 0.28f, 1.00f);
    private static readonly Vector4 Panel = new(0.09f, 0.12f, 0.16f, 0.96f);
    private static readonly Vector4 PanelAlt = new(0.06f, 0.08f, 0.11f, 0.96f);
    private static readonly Vector4 TextDim = new(0.68f, 0.75f, 0.82f, 1.00f);
    private static readonly Vector4 TextMute = new(0.49f, 0.56f, 0.63f, 1.00f);
    private static readonly Vector4 Good = new(0.33f, 0.82f, 0.48f, 1.00f);
    private static readonly Vector4 Warn = new(0.93f, 0.62f, 0.26f, 1.00f);

    private static bool _showShellWindow;
    private static bool _showDemoWindow;
    private static bool _showLogWindow;
    private static bool _showRoadmapWindow;
    private static bool _autoScrollLogs = true;
    private static int _selectedPlugin = 0;
    private static readonly bool[] PluginBarButtonsVisible = [true, false, false, false];
    private static Vector2 _barPosition = new(12f, 12f);
    private static bool _barPositionInitialized;
    private static bool _barResetRequested = true;

    private readonly struct ShellMetric
    {
        public readonly string Label;
        public readonly string Value;
        public readonly Vector4 Color;

        public ShellMetric(string label, string value, Vector4 color)
        {
            Label = label;
            Value = value;
            Color = color;
        }
    }

    public static void Render(int frameCount)
    {
        ApplyTheme();
        RenderControlBar(frameCount);

        if (_showDemoWindow)
            ImGui.ShowDemoWindow(ref _showDemoWindow);

        if (_showShellWindow)
            RenderHostWindow(frameCount);

        if (_showLogWindow)
            RenderLogWindow();

        if (_showRoadmapWindow)
            RenderRoadmapWindow();
    }

    private static void ApplyTheme()
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        style.WindowRounding = 12f;
        style.ChildRounding = 10f;
        style.FrameRounding = 8f;
        style.PopupRounding = 10f;
        style.ScrollbarRounding = 10f;
        style.GrabRounding = 8f;
        style.TabRounding = 8f;
        style.WindowBorderSize = 1f;
        style.FrameBorderSize = 0f;
        style.ItemSpacing = new Vector2(10f, 10f);
        style.ItemInnerSpacing = new Vector2(8f, 6f);
        style.WindowPadding = new Vector2(14f, 14f);

        var colors = style.Colors;
        colors[(int)ImGuiCol.WindowBg] = new Vector4(0.04f, 0.06f, 0.08f, 0.97f);
        colors[(int)ImGuiCol.ChildBg] = Panel;
        colors[(int)ImGuiCol.PopupBg] = new Vector4(0.05f, 0.07f, 0.10f, 0.98f);
        colors[(int)ImGuiCol.Border] = new Vector4(0.16f, 0.23f, 0.28f, 1.00f);
        colors[(int)ImGuiCol.FrameBg] = new Vector4(0.08f, 0.11f, 0.15f, 1.00f);
        colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.11f, 0.16f, 0.22f, 1.00f);
        colors[(int)ImGuiCol.FrameBgActive] = new Vector4(0.14f, 0.20f, 0.26f, 1.00f);
        colors[(int)ImGuiCol.TitleBg] = new Vector4(0.05f, 0.07f, 0.10f, 1.00f);
        colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.07f, 0.10f, 0.14f, 1.00f);
        colors[(int)ImGuiCol.Button] = new Vector4(0.10f, 0.14f, 0.18f, 1.00f);
        colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.14f, 0.21f, 0.27f, 1.00f);
        colors[(int)ImGuiCol.ButtonActive] = new Vector4(0.11f, 0.26f, 0.24f, 1.00f);
        colors[(int)ImGuiCol.Header] = new Vector4(0.10f, 0.14f, 0.18f, 1.00f);
        colors[(int)ImGuiCol.HeaderHovered] = new Vector4(0.13f, 0.20f, 0.26f, 1.00f);
        colors[(int)ImGuiCol.HeaderActive] = new Vector4(0.12f, 0.24f, 0.22f, 1.00f);
        colors[(int)ImGuiCol.CheckMark] = Accent;
        colors[(int)ImGuiCol.SliderGrab] = Accent;
        colors[(int)ImGuiCol.SliderGrabActive] = Gold;
        colors[(int)ImGuiCol.Tab] = new Vector4(0.08f, 0.11f, 0.15f, 1.00f);
        colors[(int)ImGuiCol.TabHovered] = new Vector4(0.13f, 0.20f, 0.26f, 1.00f);
        colors[(int)ImGuiCol.TabSelected] = new Vector4(0.11f, 0.25f, 0.22f, 1.00f);
        colors[(int)ImGuiCol.Separator] = new Vector4(0.16f, 0.23f, 0.28f, 1.00f);
        colors[(int)ImGuiCol.ResizeGrip] = AccentSoft;
        colors[(int)ImGuiCol.ResizeGripHovered] = Accent;
        colors[(int)ImGuiCol.ResizeGripActive] = Gold;
    }

    private static void RenderControlBar(int frameCount)
    {
        if (!_barPositionInitialized)
        {
            _barPosition = new Vector2(12f, 12f);
            _barPositionInitialized = true;
        }

        if (_barResetRequested)
        {
            ImGui.SetNextWindowPos(_barPosition, ImGuiCond.Always);
            _barResetRequested = false;
        }
        else
        {
            ImGui.SetNextWindowPos(_barPosition, ImGuiCond.FirstUseEver);
        }

        ImGui.SetNextWindowBgAlpha(0.96f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6f, 4f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4f, 3f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(5f, 2f));

        ImGuiWindowFlags flags =
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;

        if (!ImGui.Begin("NX##NexCoreBar", flags))
        {
            ImGui.End();
            ImGui.PopStyleVar(3);
            return;
        }

        UpdateBarPositionFromWindow();
        bool auxReady = frameCount >= 120;

        ImGui.TextColored(Win32Backend.IsUiCaptureEnabled() ? Good : TextMute, Win32Backend.IsUiCaptureEnabled() ? "UI" : "GAME");
        ShowTooltip("Press Insert to toggle UI capture.");

        ImGui.SameLine();
        ImGui.BeginDisabled(!auxReady);
        if (ImGui.SmallButton(_showShellWindow ? "Hd" : "Op"))
            _showShellWindow = !_showShellWindow;
        ShowTooltip(auxReady
            ? (_showShellWindow ? "Hide NexCore shell." : "Open NexCore shell.")
            : "Shell unlocks after the client render pipeline has stabilized.");
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(!auxReady);
        DrawCompactToggle("Lg", ref _showLogWindow);
        ShowTooltip(auxReady
            ? "Open or close the diagnostics window."
            : "Diagnostics unlock after the client render pipeline has stabilized.");
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(!auxReady);
        DrawCompactToggle("Mp", ref _showRoadmapWindow);
        ShowTooltip(auxReady
            ? "Open or close the roadmap window."
            : "Roadmap unlocks after the client render pipeline has stabilized.");
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(!auxReady);
        DrawCompactToggle("Dm", ref _showDemoWindow);
        ShowTooltip(auxReady
            ? "Open or close the ImGui demo window."
            : "Demo unlocks after the client render pipeline has stabilized.");
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(!auxReady);
        if (ImGui.SmallButton("+"))
            OpenAllWindows();
        ShowTooltip(auxReady
            ? "Open the shell, logs, roadmap, and demo windows."
            : "Bulk window controls unlock after the client render pipeline has stabilized.");
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(!auxReady);
        if (ImGui.SmallButton("-"))
            CloseAllWindows();
        ShowTooltip(auxReady
            ? "Close the shell, logs, roadmap, and demo windows."
            : "Bulk window controls unlock after the client render pipeline has stabilized.");
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(!auxReady);
        if (ImGui.SmallButton("Plg"))
            ImGui.OpenPopup("PluginBarPopup");
        ShowTooltip(auxReady
            ? "Choose which plugin shortcuts appear on the bar."
            : "Plugin shortcuts unlock after the client render pipeline has stabilized.");
        ImGui.EndDisabled();

        if (ImGui.BeginPopup("PluginBarPopup"))
        {
            ImGui.TextColored(TextDim, "Plugin Shortcuts");
            ImGui.Separator();

            for (int i = 0; i < PluginNames.Length; i++)
            {
                bool visible = PluginBarButtonsVisible[i];
                if (ImGui.Checkbox(PluginNames[i], ref visible))
                    PluginBarButtonsVisible[i] = visible;

                ImGui.SameLine(220f);
                ImGui.TextColored(TextMute, PluginStatuses[i]);
            }

            ImGui.EndPopup();
        }

        bool anyPluginShortcut = false;
        for (int i = 0; i < PluginNames.Length; i++)
        {
            if (!PluginBarButtonsVisible[i])
                continue;

            if (!anyPluginShortcut)
            {
                ImGui.SameLine();
                ImGui.TextColored(TextMute, "|");
                anyPluginShortcut = true;
            }
            else
            {
                ImGui.SameLine();
            }

            ImGui.BeginDisabled(!auxReady);
            if (ImGui.SmallButton(PluginNames[i]))
            {
                _selectedPlugin = i;
                _showShellWindow = true;
            }
            ShowTooltip(auxReady
                ? $"{PluginNames[i]}: focus this plugin in the NexCore shell."
                : "Plugin shortcuts unlock after the client render pipeline has stabilized.");
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Rs"))
            ResetBarPosition();
        ShowTooltip("Reset the bar to the top-left corner.");

        ImGui.End();
        ImGui.PopStyleVar(3);
    }

    private static void EnsureBarVisible()
    {
        ImGuiIOPtr io = ImGui.GetIO();
        Vector2 clamped = ClampToDisplay(_barPosition, ImGui.GetWindowSize(), io.DisplaySize);
        if (clamped == _barPosition)
            return;

        _barPosition = clamped;
        ImGui.SetWindowPos(_barPosition);
    }

    private static Vector2 ClampToDisplay(Vector2 position, Vector2 windowSize, Vector2 displaySize)
    {
        if (displaySize.X <= 0f || displaySize.Y <= 0f)
            return position;

        const float margin = 4f;
        float maxX = MathF.Max(margin, displaySize.X - windowSize.X - margin);
        float maxY = MathF.Max(margin, displaySize.Y - windowSize.Y - margin);

        return new Vector2(
            Math.Clamp(position.X, margin, maxX),
            Math.Clamp(position.Y, margin, maxY));
    }

    private static void ResetBarPosition()
    {
        _barPosition = new Vector2(12f, 12f);
        _barResetRequested = true;
    }

    private static void UpdateBarPositionFromWindow()
    {
        _barPosition = ImGui.GetWindowPos();
        EnsureBarVisible();
    }

    private static void DrawCompactToggle(string label, ref bool value)
    {
        bool wasActive = value;
        if (wasActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.32f, 0.28f, 1.00f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.15f, 0.40f, 0.34f, 1.00f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.18f, 0.46f, 0.39f, 1.00f));
        }

        if (ImGui.SmallButton(label))
            value = !value;

        if (wasActive)
            ImGui.PopStyleColor(3);
    }

    private static void ShowTooltip(string text)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(text);
        ImGui.EndTooltip();
    }

    private static void RenderHostWindow(int frameCount)
    {
        ImGui.SetNextWindowPos(new Vector2(28f, 72f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(880f, 560f), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("NexCore Shell", ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        RenderShellToolbar();
        DrawHero();
        ImGui.Spacing();

        if (ImGui.BeginTable("ShellMain", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Left", ImGuiTableColumnFlags.WidthStretch, 0.60f);
            ImGui.TableSetupColumn("Right", ImGuiTableColumnFlags.WidthStretch, 0.40f);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            RenderControlDeck(frameCount);

            ImGui.TableNextColumn();
            RenderPluginDeck();

            ImGui.EndTable();
        }

        ImGui.End();
    }

    private static void RenderShellToolbar()
    {
        if (ImGui.Button("Minimize To Bar"))
            _showShellWindow = false;

        ImGui.SameLine();
        if (ImGui.Button("Open Everything"))
            OpenAllWindows();

        ImGui.SameLine();
        if (ImGui.Button("Close Aux Windows"))
        {
            _showLogWindow = false;
            _showRoadmapWindow = false;
            _showDemoWindow = false;
        }

        ImGui.SameLine();
        ImGui.TextColored(TextMute, "Use the NexCore bar to reopen or focus windows.");
        ImGui.Separator();
    }

    private static void DrawHero()
    {
        ImGui.BeginChild("Hero", new Vector2(0f, 112f), ImGuiChildFlags.Borders);
        var drawList = ImGui.GetWindowDrawList();
        Vector2 min = ImGui.GetWindowPos();
        Vector2 max = min + ImGui.GetWindowSize();
        uint accent = ImGui.ColorConvertFloat4ToU32(AccentSoft);
        uint accentStrong = ImGui.ColorConvertFloat4ToU32(new Vector4(0.10f, 0.35f, 0.30f, 0.55f));
        drawList.AddRectFilledMultiColor(min, max, accentStrong, accent, accent, accentStrong);

        ImGui.TextColored(Accent, "NEXCORE");
        ImGui.SameLine();
        ImGui.TextColored(TextDim, "HOST SHELL");
        ImGui.TextColored(TextDim, "A native in-process home for adopted plugins, overlays, and client hooks.");
        ImGui.Spacing();

        ImGui.TextColored(TextMute, "Intent");
        ImGui.SameLine();
        ImGui.TextColored(TextDim, "Build the shell first so NexAi has a clean landing zone inside NexCore.");

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 6f);
        DrawTag("Overlay online", Good);
        ImGui.SameLine();
        DrawTag("Plugin host staging", Gold);
        ImGui.SameLine();
        DrawTag("Adoption-ready UI", Accent);
        ImGui.EndChild();
    }

    private static void RenderControlDeck(int frameCount)
    {
        ImGui.BeginChild("ControlDeck", new Vector2(0f, 0f), ImGuiChildFlags.Borders);
        ImGui.TextColored(TextDim, "Control Deck");
        ImGui.Separator();

        ShellMetric[] metrics =
        [
            new ShellMetric("Overlay", "ACTIVE", Good),
            new ShellMetric("Input Capture", Win32Backend.IsUiCaptureEnabled() ? "ENGAGED" : "STANDBY", Win32Backend.IsUiCaptureEnabled() ? Accent : Warn),
            new ShellMetric("Render Frames", frameCount.ToString(), Gold),
            new ShellMetric("Host Mode", "NexAi Adoption", Accent)
        ];

        if (ImGui.BeginTable("Metrics", 2, ImGuiTableFlags.SizingStretchSame))
        {
            for (int i = 0; i < metrics.Length; i++)
            {
                ImGui.TableNextColumn();
                DrawMetric(metrics[i]);
            }
            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextColored(TextDim, "Session");
        ImGui.TextWrapped("The shell is now the operational surface for injection work: status, plugin loading, diagnostics, and eventually adopted NexAi panels.");
        ImGui.Spacing();

        bool localDemo = _showDemoWindow;
        if (ImGui.Checkbox("Show ImGui Demo Window", ref localDemo))
            _showDemoWindow = localDemo;

        bool localLogs = _showLogWindow;
        if (ImGui.Checkbox("Open Diagnostics Window", ref localLogs))
            _showLogWindow = localLogs;

        bool localRoadmap = _showRoadmapWindow;
        if (ImGui.Checkbox("Open Adoption Roadmap", ref localRoadmap))
            _showRoadmapWindow = localRoadmap;

        ImGui.Spacing();
        ImGui.TextColored(TextMute, "Hotkey");
        ImGui.SameLine();
        ImGui.TextColored(TextDim, "Press Insert to release or reclaim UI capture.");

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 6f);
        ImGui.BeginChild("Launchpad", new Vector2(0f, 146f), ImGuiChildFlags.Borders);
        ImGui.TextColored(TextDim, "Launchpad");
        DrawFeatureLine("Host dashboard", "Done", Good);
        DrawFeatureLine("Plugin registry", "Next scaffold", Gold);
        DrawFeatureLine("NexAi settings pane", "Adopt from NexSuite", Accent);
        DrawFeatureLine("Client action hooks", "After host APIs", Warn);
        ImGui.EndChild();

        ImGui.EndChild();
    }

    private static void RenderPluginDeck()
    {
        ImGui.BeginChild("PluginDeck", new Vector2(0f, 0f), ImGuiChildFlags.Borders);
        ImGui.TextColored(TextDim, "Plugin Deck");
        ImGui.Separator();

        for (int i = 0; i < PluginNames.Length; i++)
        {
            ImGui.PushID(i);

            bool visible = PluginBarButtonsVisible[i];
            if (ImGui.Checkbox("##ShortcutVisible", ref visible))
                PluginBarButtonsVisible[i] = visible;

            ImGui.SameLine();
            bool selected = _selectedPlugin == i;
            if (ImGui.Selectable($"{PluginNames[i]}##Plugin{i}", selected, ImGuiSelectableFlags.None, new Vector2(180f, 34f)))
                _selectedPlugin = i;

            ImGui.SameLine();
            ImGui.TextColored(PluginBarButtonsVisible[i] ? Good : TextMute, PluginBarButtonsVisible[i] ? "Bar shortcut enabled" : "Hidden from bar");
            ImGui.SameLine(360f);
            ImGui.TextColored(i == 0 ? Accent : TextMute, PluginStatuses[i]);
            ImGui.PopID();
        }

        ImGui.Spacing();
        ImGui.BeginChild("PluginInspector", new Vector2(0f, 260f), ImGuiChildFlags.Borders);

        if (_selectedPlugin == 0)
        {
            ImGui.TextColored(Accent, "NexAi");
            ImGui.TextWrapped("Primary adoption candidate. The goal is to lift NexAi out of the Utility Belt host model and give it a native NexCore shell, state bridge, and event surface.");
            ImGui.Spacing();
            DrawBulletRow("Current source", "NexSuite / Utility Belt plugin");
            DrawBulletRow("UI maturity", "High");
            DrawBulletRow("Migration strategy", "Adopt panels first, then events and actions");
            DrawBulletRow("Best immediate step", "Host shell + plugin contract");
            DrawBulletRow("Bar shortcut", PluginBarButtonsVisible[0] ? "Enabled" : "Hidden");
        }
        else if (_selectedPlugin == 1)
        {
            ImGui.TextColored(Gold, "NexTank Compatibility");
            ImGui.TextWrapped("Useful as a behavior and data reference while the NexCore host contracts are still young.");
            ImGui.Spacing();
            DrawBulletRow("Role", "Reference implementation");
            DrawBulletRow("Port approach", "Selective reuse, not hard dependency");
            DrawBulletRow("Bar shortcut", PluginBarButtonsVisible[1] ? "Enabled" : "Hidden");
        }
        else if (_selectedPlugin == 2)
        {
            ImGui.TextColored(Gold, "Core Hooks");
            ImGui.TextWrapped("The service layer that will expose world state, actions, and message hooks to adopted plugins.");
            ImGui.Spacing();
            DrawBulletRow("Status", "Not started");
            DrawBulletRow("Dependency", "Needs host-facing API design");
            DrawBulletRow("Bar shortcut", PluginBarButtonsVisible[2] ? "Enabled" : "Hidden");
        }
        else
        {
            ImGui.TextColored(Gold, "Telemetry");
            ImGui.TextWrapped("Small but important: operational visibility for load state, hook health, and plugin diagnostics.");
            ImGui.Spacing();
            DrawBulletRow("Status", "Shell-ready");
            DrawBulletRow("Surface", "Diagnostics window and host metrics");
            DrawBulletRow("Bar shortcut", PluginBarButtonsVisible[3] ? "Enabled" : "Hidden");
        }

        ImGui.EndChild();
        ImGui.EndChild();
    }

    private static void RenderLogWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(760f, 280f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("NexCore Diagnostics", ref _showLogWindow))
        {
            ImGui.End();
            return;
        }

        bool localAutoScroll = _autoScrollLogs;
        if (ImGui.Checkbox("Auto-scroll", ref localAutoScroll))
            _autoScrollLogs = localAutoScroll;

        ImGui.SameLine();
        ImGui.TextColored(TextMute, "Recent in-process log lines");
        ImGui.Separator();

        ImGui.BeginChild("LogLines", new Vector2(0f, 0f), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar);
        foreach (string line in EntryPoint.GetRecentLogLines())
            ImGui.TextUnformatted(line);

        if (_autoScrollLogs && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 40f)
            ImGui.SetScrollHereY(1f);

        ImGui.EndChild();
        ImGui.End();
    }

    private static void RenderRoadmapWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(520f, 360f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("NexCore Adoption Roadmap", ref _showRoadmapWindow))
        {
            ImGui.End();
            return;
        }

        DrawRoadmapStep("1", "Shell window", "Give NexCore a polished host surface inside the client.", Good);
        DrawRoadmapStep("2", "Plugin contract", "Define how adopted plugins register panes, services, and lifecycle hooks.", Accent);
        DrawRoadmapStep("3", "NexAi pane adoption", "Move the first NexAi dashboard slices into native NexCore UI.", Gold);
        DrawRoadmapStep("4", "Hook expansion", "Add network, world-state, and action surfaces after the host is stable.", Warn);

        ImGui.End();
    }

    private static void DrawMetric(in ShellMetric metric)
    {
        ImGui.BeginChild($"{metric.Label}Card", new Vector2(0f, 76f), ImGuiChildFlags.Borders);
        ImGui.TextColored(TextMute, metric.Label);
        ImGui.SetWindowFontScale(1.2f);
        ImGui.TextColored(metric.Color, metric.Value);
        ImGui.SetWindowFontScale(1.0f);
        ImGui.EndChild();
    }

    private static void DrawTag(string text, Vector4 color)
    {
        Vector2 size = ImGui.CalcTextSize(text);
        Vector2 pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        uint fill = ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 0.18f));
        uint stroke = ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 0.65f));
        drawList.AddRectFilled(pos, pos + new Vector2(size.X + 16f, size.Y + 8f), fill, 6f);
        drawList.AddRect(pos, pos + new Vector2(size.X + 16f, size.Y + 8f), stroke, 6f);
        ImGui.SetCursorScreenPos(pos + new Vector2(8f, 4f));
        ImGui.TextColored(color, text);
    }

    private static void DrawFeatureLine(string name, string state, Vector4 color)
    {
        ImGui.TextColored(TextDim, name);
        ImGui.SameLine(190f);
        ImGui.TextColored(color, state);
    }

    private static void DrawBulletRow(string label, string value)
    {
        ImGui.BulletText($"{label}: {value}");
    }

    private static void DrawRoadmapStep(string index, string title, string body, Vector4 color)
    {
        ImGui.BeginChild($"Roadmap{index}", new Vector2(0f, 68f), ImGuiChildFlags.Borders);
        ImGui.TextColored(color, $"Step {index}");
        ImGui.SameLine();
        ImGui.TextColored(TextDim, title);
        ImGui.TextWrapped(body);
        ImGui.EndChild();
    }

    private static void OpenAllWindows()
    {
        _showShellWindow = true;
        _showLogWindow = true;
        _showRoadmapWindow = true;
        _showDemoWindow = true;
    }

    private static void CloseAllWindows()
    {
        _showShellWindow = false;
        _showLogWindow = false;
        _showRoadmapWindow = false;
        _showDemoWindow = false;
    }
}

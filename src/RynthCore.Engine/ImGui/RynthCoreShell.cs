using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.ImGuiBackend;

internal static class RynthCoreShell
{
    private static readonly string[] PluginNames =
    [
        "RynthAi",
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
    private static bool _showPacketSnifferWindow;
    private static bool _autoScrollLogs = true;
    private static string[] _cachedLogLines = Array.Empty<string>();
    private static long _nextLogRefreshTick;
    private static int _selectedPlugin = 0;
    private static bool _showPluginTips = true;
    private static string _newPluginPathInput = "";
    private static readonly bool[] PluginBarButtonsVisible = [true, false, false, false];
    private static Vector2 _barPosition = new(12f, 12f);
    private static bool _barPositionInitialized;
    private static bool _barResetRequested = true;
    private static long _lastBarSaveTick;
    private static Vector2 _lastSavedBarPosition;

    private static readonly string BarPositionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "RynthCore.bar.cfg");

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

        if (_showPacketSnifferWindow)
            RenderPacketSnifferWindow();
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
        style.FrameBorderSize = 1f;
        style.ItemSpacing = new Vector2(10f, 10f);
        style.ItemInnerSpacing = new Vector2(8f, 6f);
        style.WindowPadding = new Vector2(14f, 14f);

        var colors = style.Colors;
        colors[(int)ImGuiCol.WindowBg] = new Vector4(0.04f, 0.06f, 0.08f, 0.97f);
        colors[(int)ImGuiCol.ChildBg] = Panel;
        colors[(int)ImGuiCol.PopupBg] = new Vector4(0.06f, 0.12f, 0.18f, 1.00f);
        colors[(int)ImGuiCol.Border] = new Vector4(0.16f, 0.23f, 0.28f, 1.00f);
        colors[(int)ImGuiCol.FrameBg] = new Vector4(0.08f, 0.11f, 0.15f, 1.00f);
        colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.11f, 0.16f, 0.22f, 1.00f);
        colors[(int)ImGuiCol.FrameBgActive] = new Vector4(0.14f, 0.20f, 0.26f, 1.00f);
        colors[(int)ImGuiCol.TitleBg] = new Vector4(0.05f, 0.07f, 0.10f, 1.00f);
        colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.07f, 0.10f, 0.14f, 1.00f);
        colors[(int)ImGuiCol.Button] = new Vector4(0.06f, 0.12f, 0.18f, 1.00f);
        colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.10f, 0.18f, 0.25f, 1.00f);
        colors[(int)ImGuiCol.ButtonActive] = new Vector4(0.08f, 0.15f, 0.22f, 1.00f);
        colors[(int)ImGuiCol.Header] = new Vector4(0.06f, 0.12f, 0.18f, 1.00f);
        colors[(int)ImGuiCol.HeaderHovered] = new Vector4(0.10f, 0.18f, 0.25f, 1.00f);
        colors[(int)ImGuiCol.HeaderActive] = new Vector4(0.08f, 0.15f, 0.22f, 1.00f);
        colors[(int)ImGuiCol.Border] = new Vector4(0.15f, 0.25f, 0.35f, 1.00f);
        colors[(int)ImGuiCol.CheckMark] = Accent;
        colors[(int)ImGuiCol.SliderGrab] = new Vector4(0.15f, 0.30f, 0.45f, 1.00f);
        colors[(int)ImGuiCol.SliderGrabActive] = new Vector4(0.20f, 0.40f, 0.60f, 1.00f);
        colors[(int)ImGuiCol.Tab] = new Vector4(0.06f, 0.12f, 0.18f, 1.00f);
        colors[(int)ImGuiCol.TabHovered] = new Vector4(0.10f, 0.18f, 0.25f, 1.00f);
        colors[(int)ImGuiCol.TabSelected] = new Vector4(0.15f, 0.25f, 0.35f, 1.00f);
        colors[(int)ImGuiCol.Separator] = new Vector4(0.15f, 0.25f, 0.35f, 1.00f);
        colors[(int)ImGuiCol.ResizeGrip] = new Vector4(0.06f, 0.12f, 0.18f, 0.40f);
        colors[(int)ImGuiCol.ResizeGripHovered] = new Vector4(0.10f, 0.18f, 0.25f, 1.00f);
        colors[(int)ImGuiCol.ResizeGripActive] = new Vector4(0.15f, 0.25f, 0.35f, 1.00f);
        colors[(int)ImGuiCol.ModalWindowDimBg] = new Vector4(0, 0, 0, 0);
    }

    private static void RenderControlBar(int frameCount)
    {
        if (!_barPositionInitialized)
        {
            _barPosition = LoadBarPosition();
            _lastSavedBarPosition = _barPosition;
            _barPositionInitialized = true;
        }

        // With ViewportsEnable, ImGui expects absolute screen coords. We store
        // the bar position as client-relative, so offset by the main viewport's
        // origin (which ImGuiController sets to ClientToScreen(0,0) each frame).
        Vector2 viewportOrigin = ImGui.GetMainViewport().Pos;
        Vector2 screenPos = _barPosition + viewportOrigin;

        if (_barResetRequested)
        {
            ImGui.SetNextWindowPos(screenPos, ImGuiCond.Always);
            _barResetRequested = false;
        }
        else
        {
            ImGui.SetNextWindowPos(screenPos, ImGuiCond.FirstUseEver);
        }

        ImGui.SetNextWindowBgAlpha(1.0f);
        // Pin the bar to the main viewport so it stays anchored inside the game
        // client even with ViewportsEnable on. Plugin windows can still pop out
        // into their own OS windows — this pin only applies to the bar.
        ImGui.SetNextWindowViewport(ImGui.GetMainViewport().ID);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6f, 4f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4f, 3f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(5f, 2f));

        ImGuiWindowFlags flags =
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;

        if (!ImGui.Begin("RC##RynthCoreBar", flags))
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
        DrawCompactToggle("Lg", ref _showLogWindow);
        ShowTooltip(auxReady
            ? "Open or close the diagnostics window."
            : "Diagnostics unlock after the client render pipeline has stabilized.");
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(!auxReady);
        DrawCompactToggle("Pk", ref _showPacketSnifferWindow);
        ShowTooltip(auxReady
            ? "Open or close the raw packet sniffer."
            : "Packet sniffer unlocks after the client render pipeline has stabilized.");
        ImGui.EndDisabled();

        var loadedPlugins = PluginManager.Plugins;
        bool anyLoadedPluginShortcut = false;
        for (int i = 0; i < loadedPlugins.Count; i++)
        {
            var plugin = loadedPlugins[i];
            if (string.IsNullOrWhiteSpace(plugin.DisplayName))
                continue;

            bool loginGated = plugin.OnLoginComplete != null;
            if (loginGated && !plugin.LoginCompleteDispatched)
                continue;

            if (!anyLoadedPluginShortcut)
            {
                ImGui.SameLine();
                ImGui.TextColored(TextMute, "|");
                anyLoadedPluginShortcut = true;
            }
            else
            {
                ImGui.SameLine();
            }

            ImGui.BeginDisabled(!auxReady);
            if (ImGui.SmallButton($"{BuildLoadedPluginBarLabel(plugin.DisplayName)}##LoadedPlugin{i}"))
            {
                if (plugin.OnBarAction != null)
                {
                    try
                    {
                        plugin.OnBarAction();
                    }
                    catch (Exception ex)
                    {
                        RynthLog.Render($"RynthCoreShell: {plugin.DisplayName} bar action threw {ex.GetType().Name}: {ex.Message}");
                    }
                }
                else
                {
                    _showShellWindow = true;
                }
            }
            ShowTooltip(auxReady
                ? plugin.OnBarAction != null
                    ? $"{plugin.DisplayName}: trigger the plugin's bar action."
                    : $"{plugin.DisplayName}: open the shell while this plugin is loaded."
                : "Loaded plugin shortcuts unlock after the client render pipeline has stabilized.");
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!auxReady);
        if (ImGui.SmallButton("RL"))
            PluginManager.RequestRescan();
        ShowTooltip(auxReady
            ? "Hot-reload all plugins: unloads, copies new DLLs, reloads."
            : "Hot-reload unlocks after the client render pipeline has stabilized.");
        ImGui.EndDisabled();

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
        // _barPosition is client-relative; translate to screen coords for ImGui.
        ImGui.SetWindowPos(_barPosition + ImGui.GetMainViewport().Pos);
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
        SaveBarPosition();
    }

    private static Vector2 LoadBarPosition()
    {
        try
        {
            if (!File.Exists(BarPositionPath))
                return new Vector2(12f, 12f);

            string text = File.ReadAllText(BarPositionPath).Trim();
            string[] parts = text.Split(',');
            if (parts.Length == 2 &&
                float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            {
                return new Vector2(x, y);
            }
        }
        catch { }
        return new Vector2(12f, 12f);
    }

    private static void SaveBarPositionThrottled()
    {
        long now = Environment.TickCount64;
        if (now - _lastBarSaveTick < 2000) return;
        if (_barPosition.X == _lastSavedBarPosition.X && _barPosition.Y == _lastSavedBarPosition.Y) return;
        SaveBarPosition();
    }

    private static void SaveBarPosition()
    {
        try
        {
            _lastBarSaveTick = Environment.TickCount64;
            _lastSavedBarPosition = _barPosition;
            File.WriteAllText(BarPositionPath,
                $"{_barPosition.X.ToString(CultureInfo.InvariantCulture)},{_barPosition.Y.ToString(CultureInfo.InvariantCulture)}");
        }
        catch { }
    }

    private static void UpdateBarPositionFromWindow()
    {
        // GetWindowPos returns screen coords with ViewportsEnable — convert back
        // to client-relative so the stored position is viewport-independent.
        _barPosition = ImGui.GetWindowPos() - ImGui.GetMainViewport().Pos;
        EnsureBarVisible();
        SaveBarPositionThrottled();
    }

    private static void DrawCompactToggle(string label, ref bool value)
    {
        bool wasActive = value;
        if (wasActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.30f, 0.35f, 1.00f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.36f, 0.42f, 1.00f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.20f, 0.40f, 0.46f, 1.00f));
        }

        if (ImGui.SmallButton(label))
            value = !value;

        if (wasActive)
            ImGui.PopStyleColor(3);
    }

    private static readonly Vector4 TooltipBg = new(0.06f, 0.12f, 0.18f, 1.0f);

    private static void ShowTooltip(string text)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            return;

        ImGui.PushStyleColor(ImGuiCol.PopupBg, TooltipBg);
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(text);
        ImGui.EndTooltip();
        ImGui.PopStyleColor();
    }

    private static void RenderHostWindow(int frameCount)
    {
        ImGui.SetNextWindowPos(new Vector2(28f, 72f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(880f, 560f), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("RynthCore Shell", ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        RenderShellToolbar();

        if (ImGui.BeginTabBar("##ShellTabs"))
        {
            if (ImGui.BeginTabItem("Dashboard"))
            {
                DrawHero();
                ImGui.Spacing();

                if (ImGui.BeginTable("ShellMain", 2, ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Left", ImGuiTableColumnFlags.WidthStretch, 0.60f);
                    ImGui.TableSetupColumn("Right", ImGuiTableColumnFlags.WidthStretch, 0.40f);
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    RenderControlDeck();

                    ImGui.TableNextColumn();
                    RenderPluginDeck();

                    ImGui.EndTable();
                }
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Plugins"))
            {
                RenderPluginsTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
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
        if (ImGui.Button("Rescan Plugins"))
            PluginManager.RequestRescan();

        ShowTooltip("Reload plugin DLLs from disk without restarting the client.");

        ImGui.SameLine();
        ImGui.TextColored(TextMute, "Use the RynthCore bar to reopen or focus windows.");
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

        ImGui.TextColored(Accent, "RYNTHCORE");
        ImGui.SameLine();
        ImGui.TextColored(TextDim, "HOST SHELL");
        ImGui.TextColored(TextDim, "A native in-process home for adopted plugins, overlays, and client hooks.");
        ImGui.Spacing();

        ImGui.TextColored(TextMute, "Intent");
        ImGui.SameLine();
        ImGui.TextColored(TextDim, "Build the shell first so RynthAi has a clean landing zone inside RynthCore.");

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 6f);
        DrawTag("Overlay online", Good);
        ImGui.SameLine();
        DrawTag("Plugin host staging", Gold);
        ImGui.SameLine();
        DrawTag("Adoption-ready UI", Accent);
        ImGui.EndChild();
    }

    private static void RenderControlDeck()
    {
        ImGui.BeginChild("ControlDeck", new Vector2(0f, 0f), ImGuiChildFlags.Borders);
        ImGui.TextColored(TextDim, "Control Deck");
        ImGui.Separator();

        ShellMetric[] metrics =
        [
            new ShellMetric("Overlay", "ACTIVE", Good),
            new ShellMetric("Input Capture", Win32Backend.IsUiCaptureEnabled() ? "ENGAGED" : "STANDBY", Win32Backend.IsUiCaptureEnabled() ? Accent : Warn),
            new ShellMetric("Render Loop", "LIVE", Gold),
            new ShellMetric("Host Mode", "RynthAi Adoption", Accent)
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
        ImGui.TextWrapped("The shell is now the operational surface for injection work: status, plugin loading, diagnostics, and eventually adopted RynthAi panels.");
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
        DrawFeatureLine("RynthAi settings pane", "Adopt from RynthSuite", Accent);
        DrawFeatureLine("Client action hooks", "Probe live in shell", Accent);
        ImGui.EndChild();

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 6f);
        RenderCompatibilityDeck();

        ImGui.EndChild();
    }

    private static void RenderPluginDeck()
    {
        ImGui.BeginChild("PluginDeck", new Vector2(0f, 0f), ImGuiChildFlags.Borders);
        ImGui.TextColored(TextDim, "Plugin Deck");
        ImGui.Separator();

        bool rescanQueued = PluginManager.IsRescanQueued;
        if (rescanQueued)
        {
            ImGui.TextColored(Gold, "Rescan queued for this frame.");
        }
        else if (PluginManager.HasObservedLoginComplete)
        {
            ImGui.TextColored(TextMute, "OnLoginComplete has fired. Rebuild a plugin DLL, replace it in the Plugins folder, then click Rescan Plugins.");
        }
        else
        {
            ImGui.TextColored(Gold, "Loaded plugins that opt into OnLoginComplete will stay dormant until login finishes.");
        }

        string pluginDir = PluginManager.PluginDirectory;
        if (!string.IsNullOrEmpty(pluginDir))
        {
            ImGui.TextColored(TextMute, pluginDir);
        }

        if (_showPluginTips)
        {
            ImGui.Spacing();
            ImGui.BeginChild("PluginTips", new Vector2(0f, 76f), ImGuiChildFlags.Borders);
            ImGui.TextColored(TextDim, "Live Dev Loop");
            ImGui.BulletText("Publish the plugin so it has native exports.");
            ImGui.BulletText("Copy the new DLL into the Plugins folder.");
            ImGui.BulletText("Click Rescan Plugins to unload the old copy and load the new one.");
            ImGui.EndChild();
        }

        ImGui.Spacing();

        // Show dynamically loaded plugins first
        var loadedPlugins = PluginManager.Plugins;
        if (loadedPlugins.Count > 0)
        {
            ImGui.TextColored(Accent, "Loaded Plugins");
            for (int i = 0; i < loadedPlugins.Count; i++)
            {
                var p = loadedPlugins[i];
                ImGui.PushID($"loaded_{i}");

                bool waitingForLogin = p.Initialized && !p.Failed && p.OnLoginComplete != null && !p.LoginCompleteDispatched;
                Vector4 statusColor = p.Failed ? Warn : waitingForLogin ? Gold : p.Initialized ? Good : TextMute;
                string statusText = p.Failed ? "FAILED" : waitingForLogin ? "WAIT LOGIN" : p.Initialized ? "ACTIVE" : "PENDING";

                string ver = string.IsNullOrEmpty(p.VersionString) ? "" : $"  v{p.VersionString}";
                ImGui.BulletText("");
                ImGui.SameLine();
                ImGui.TextColored(Accent, p.DisplayName);
                ImGui.SameLine();
                ImGui.TextColored(TextMute, ver);
                ImGui.SameLine(300f);
                ImGui.TextColored(statusColor, statusText);

                if (!string.IsNullOrEmpty(p.SourceFilePath))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(TextMute, $"<{p.FileName}>");
                }

                string caps = "";
                if (p.OnLoginComplete != null) caps += " login";
                if (p.OnBarAction != null) caps += " bar";
                if (p.Tick != null) caps += " tick";
                if (p.Render != null) caps += " render";
                if (caps.Length > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(TextMute, $"[{caps.Trim()}]");
                }

                ImGui.PopID();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }
        else
        {
            ImGui.TextColored(TextMute, "No plugins loaded.");
            ImGui.TextColored(TextMute, "Drop DLLs into the Plugins folder.");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        // Show static roadmap entries
        ImGui.TextColored(TextDim, "Roadmap");
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
            ImGui.TextColored(TextMute, PluginStatuses[i]);
            ImGui.PopID();
        }

        ImGui.Spacing();
        ImGui.BeginChild("PluginInspector", new Vector2(0f, 200f), ImGuiChildFlags.Borders);

        if (_selectedPlugin >= 0 && _selectedPlugin < PluginNames.Length)
        {
            ImGui.TextColored(Gold, PluginNames[_selectedPlugin]);
            ImGui.TextColored(TextMute, PluginStatuses[_selectedPlugin]);
        }

        ImGui.EndChild();
        ImGui.EndChild();
    }

    private static void RenderPluginsTab()
    {
        ImGui.Spacing();

        // ── Loaded plugins ──────────────────────────────────────────────────
        var loadedPlugins = PluginManager.Plugins;
        ImGui.TextColored(Accent, $"Loaded Plugins ({loadedPlugins.Count})");
        ImGui.Separator();

        if (loadedPlugins.Count == 0)
        {
            ImGui.TextColored(TextMute, "No plugins loaded.");
        }
        else
        {
            for (int i = 0; i < loadedPlugins.Count; i++)
            {
                var p = loadedPlugins[i];
                bool waitingForLogin = p.Initialized && !p.Failed && p.OnLoginComplete != null && !p.LoginCompleteDispatched;
                Vector4 statusColor = p.Failed ? Warn : waitingForLogin ? Gold : p.Initialized ? Good : TextMute;
                string statusText = p.Failed ? "FAILED" : waitingForLogin ? "WAIT LOGIN" : p.Initialized ? "ACTIVE" : "PENDING";

                string ver = string.IsNullOrEmpty(p.VersionString) ? "" : $" v{p.VersionString}";
                ImGui.BulletText("");
                ImGui.SameLine();
                ImGui.TextColored(Accent, p.DisplayName);
                ImGui.SameLine();
                ImGui.TextColored(TextMute, ver);
                ImGui.SameLine(280f);
                ImGui.TextColored(statusColor, statusText);
                ImGui.TextColored(TextMute, $"  {p.SourceFilePath}");
            }
        }

        ImGui.Spacing();
        ImGui.Spacing();

        // ── Plugin sources ──────────────────────────────────────────────────
        ImGui.TextColored(Accent, "Plugin Sources");
        ImGui.Separator();

        string builtInDir = PluginManager.PluginDirectory;
        if (!string.IsNullOrEmpty(builtInDir))
        {
            ImGui.TextColored(TextMute, "Built-in directory:");
            ImGui.SameLine();
            ImGui.TextColored(TextDim, builtInDir);
        }

        ImGui.Spacing();
        ImGui.TextColored(TextMute, "Additional plugin DLLs:");

        var extraPaths = PluginManager.ExtraPluginPaths;
        int removeIdx = -1;
        if (extraPaths.Count == 0)
        {
            ImGui.TextColored(TextMute, "  (none)");
        }
        else
        {
            for (int i = 0; i < extraPaths.Count; i++)
            {
                ImGui.PushID($"pluginpath_{i}");
                bool exists = File.Exists(extraPaths[i]);
                ImGui.TextColored(exists ? Good : Warn, exists ? "  OK" : "  ??");
                ImGui.SameLine();
                ImGui.TextColored(TextDim, extraPaths[i]);
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
                    removeIdx = i;
                ImGui.PopID();
            }
        }
        if (removeIdx >= 0)
        {
            EngineSettings.RemovePluginPath(removeIdx);
            PluginManager.RequestRescan();
        }

        ImGui.Spacing();
        ImGui.TextColored(TextMute, "Add plugin DLL path:");
        ImGui.SetNextItemWidth(-80f);
        ImGui.InputText("##newpath", ref _newPluginPathInput, 512);
        ImGui.SameLine();
        bool canAdd = !string.IsNullOrWhiteSpace(_newPluginPathInput);
        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Add"))
        {
            EngineSettings.AddPluginPath(_newPluginPathInput.Trim());
            _newPluginPathInput = "";
            PluginManager.RequestRescan();
        }
        if (!canAdd) ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Rescan Plugins"))
            PluginManager.RequestRescan();
        ShowTooltip("Unload all plugins, re-scan built-in directory and all extra paths, reload.");
    }

    private static void RenderCompatibilityDeck()
    {
        ClientActionHookStatus status = ClientActionHooks.GetStatus();
        bool uiHookInstalled = UiLifecycleHooks.IsInstalled;
        bool uiObserved = UiLifecycleHooks.HasObservedUiInitialized;
        bool chatHooksInstalled = ChatCallbackHooks.IsInstalled;
        bool loginHookInstalled = LoginLifecycleHooks.IsInstalled;
        bool loginObserved = LoginLifecycleHooks.HasObservedLoginComplete;
        bool multiClientEnabled = MultiClientHooks.IsEnabled;
        bool multiClientInstalled = MultiClientHooks.IsInstalled;

        ImGui.BeginChild("CompatibilityDeck", new Vector2(0f, 192f), ImGuiChildFlags.Borders);
        ImGui.TextColored(TextDim, "RynthAi Compatibility Hooks");

        ImGui.SameLine();
        if (ImGui.SmallButton("Probe Hooks"))
            ClientActionHooks.Probe();
        ShowTooltip("Rescan acclient.exe for the combat and movement helper entrypoints RynthAi uses.");

        ImGui.Separator();
        DrawFeatureLine("Combat surface", status.CombatInitialized ? "READY" : "OFF", status.CombatInitialized ? Good : Warn);
        DrawFeatureLine("Movement surface", status.MovementInitialized ? "READY" : "OFF", status.MovementInitialized ? Good : Warn);
        DrawFeatureLine("Local motion", status.CommandInterpreterInitialized ? "READY" : "OFF", status.CommandInterpreterInitialized ? Good : Warn);
        DrawFeatureLine("UI lifecycle", uiObserved ? "FIRED" : uiHookInstalled ? "HOOKED" : "OFF", uiObserved ? Good : uiHookInstalled ? Gold : Warn);
        DrawFeatureLine("Chat callbacks", chatHooksInstalled ? "READY" : "OFF", chatHooksInstalled ? Good : Warn);
        DrawFeatureLine("Multi-client gate", multiClientInstalled ? "BYPASS" : multiClientEnabled ? "FAILED" : "OFF", multiClientInstalled ? Good : multiClientEnabled ? Warn : TextMute);
        DrawFeatureLine("Login lifecycle", loginObserved ? "FIRED" : loginHookInstalled ? "HOOKED" : "OFF", loginObserved ? Good : loginHookInstalled ? Gold : Warn);

        ImGui.Spacing();
        ImGui.TextColored(TextMute, "Combat");
        ImGui.TextWrapped($"Melee {OnOff(status.MeleeAvailable)}  Missile {OnOff(status.MissileAvailable)}  Mode {OnOff(status.ChangeCombatModeAvailable)}  Cancel {OnOff(status.CancelAttackAvailable)}  Health {OnOff(status.QueryHealthAvailable)}");
        ImGui.TextColored(TextMute, status.CombatStatus);

        ImGui.Spacing();
        ImGui.TextColored(TextMute, "Movement");
        ImGui.TextWrapped($"Move {OnOff(status.DoMovementAvailable)}  Stop {OnOff(status.StopMovementAvailable)}  Jump {OnOff(status.JumpNonAutonomousAvailable)}  Autonomy {OnOff(status.AutonomyLevelAvailable)}");
        ImGui.TextColored(TextMute, status.MovementStatus);

        ImGui.Spacing();
        ImGui.TextColored(TextMute, "Local");
        ImGui.TextWrapped($"Autorun {OnOff(status.SetAutoRunAvailable)}  TapJump {OnOff(status.TapJumpAvailable)}");
        ImGui.TextColored(TextMute, status.CommandInterpreterStatus);

        ImGui.Spacing();
        ImGui.TextColored(TextMute, "UI");
        ImGui.TextColored(TextMute, UiLifecycleHooks.StatusMessage);

        ImGui.Spacing();
        ImGui.TextColored(TextMute, "Chat");
        ImGui.TextColored(TextMute, ChatCallbackHooks.StatusMessage);

        ImGui.Spacing();
        ImGui.TextColored(TextMute, "Multi-client");
        ImGui.TextColored(TextMute, MultiClientHooks.StatusMessage);

        ImGui.Spacing();
        ImGui.TextColored(TextMute, "Lifecycle");
        ImGui.TextColored(TextMute, LoginLifecycleHooks.StatusMessage);
        ImGui.EndChild();
    }

    private static void RenderLogWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(760f, 280f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("RynthCore Diagnostics", ref _showLogWindow))
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

        long now = Environment.TickCount64;
        if (_cachedLogLines.Length == 0 || now >= _nextLogRefreshTick)
        {
            _cachedLogLines = EntryPoint.GetRecentLogLines();
            _nextLogRefreshTick = now + 250;
        }

        ImGui.BeginChild("LogLines", new Vector2(0f, 0f), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar);
        foreach (string line in _cachedLogLines)
            ImGui.TextUnformatted(line);

        if (_autoScrollLogs && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 40f)
            ImGui.SetScrollHereY(1f);

        ImGui.EndChild();
        ImGui.End();
    }

    private static void RenderRoadmapWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(520f, 360f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("RynthCore Adoption Roadmap", ref _showRoadmapWindow))
        {
            ImGui.End();
            return;
        }

        DrawRoadmapStep("1", "Shell window", "Give RynthCore a polished host surface inside the client.", Good);
        DrawRoadmapStep("2", "Plugin contract", "Define how adopted plugins register panes, services, and lifecycle hooks.", Accent);
        DrawRoadmapStep("3", "RynthAi pane adoption", "Move the first RynthAi dashboard slices into native RynthCore UI.", Gold);
        DrawRoadmapStep("4", "Hook expansion", "Add network, world-state, and action surfaces after the host is stable.", Warn);

        ImGui.End();
    }

    private static void DrawMetric(in ShellMetric metric)
    {
        ImGui.BeginChild($"{metric.Label}Card", new Vector2(0f, 76f), ImGuiChildFlags.Borders);
        ImGui.TextColored(TextMute, metric.Label);
        ImGui.TextColored(metric.Color, metric.Value);
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

    private static string OnOff(bool value)
    {
        return value ? "ON" : "OFF";
    }

    private static string BuildLoadedPluginBarLabel(string displayName)
    {
        string label = displayName.Replace("RynthCore ", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (label.Length <= 10)
            return label;

        return label[..10];
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

    private static void RenderPacketSnifferWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(740f, 420f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Packet Sniffer##RynthCorePk", ref _showPacketSnifferWindow))
        {
            ImGui.End();
            return;
        }

        // Controls row
        bool frozen = RawOpcodeTracker.Frozen;
        if (ImGui.Checkbox("Freeze", ref frozen))
            RawOpcodeTracker.Frozen = frozen;
        ShowTooltip("Pause capture. Existing entries stay visible.");

        ImGui.SameLine();
        bool showKnown = RawOpcodeTracker.ShowKnown;
        if (ImGui.Checkbox("Show Known", ref showKnown))
            RawOpcodeTracker.ShowKnown = showKnown;
        ShowTooltip("Show opcodes already handled by SmartBoxHooks (0xF74B, 0xF74C, etc.).");

        ImGui.SameLine();
        bool showUnknown = RawOpcodeTracker.ShowUnknown;
        if (ImGui.Checkbox("Show Unknown", ref showUnknown))
            RawOpcodeTracker.ShowUnknown = showUnknown;
        ShowTooltip("Show opcodes not in the known set.");

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
            RawOpcodeTracker.Clear();
        ShowTooltip("Reset all counters.");

        if (!RawPacketHooks.IsInstalled)
        {
            ImGui.Spacing();
            ImGui.TextColored(Warn, "Hook not installed — RecvFrom pointer not found at 0x007935AC.");
            ImGui.End();
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var snapshot = RawOpcodeTracker.GetSnapshot();
        int visibleCount = 0;

        ImGuiTableFlags tableFlags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.SizingFixedFit;

        if (ImGui.BeginTable("##PkTable", 4, tableFlags, new Vector2(0f, -1f)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Opcode",  ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("Count",   ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Size",    ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("Last Payload (first 16 bytes)", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            foreach (var (opcode, entry) in snapshot)
            {
                bool isKnown = RawOpcodeTracker.IsKnown(opcode);
                if (isKnown && !RawOpcodeTracker.ShowKnown)   continue;
                if (!isKnown && !RawOpcodeTracker.ShowUnknown) continue;

                visibleCount++;
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextColored(isKnown ? TextMute : Accent, $"0x{opcode:X4}");

                ImGui.TableSetColumnIndex(1);
                ImGui.TextColored(TextDim, entry.Count.ToString());

                ImGui.TableSetColumnIndex(2);
                ImGui.TextColored(TextDim, entry.LastPayloadLen.ToString());

                ImGui.TableSetColumnIndex(3);
                if (entry.LastSample is { Length: > 0 })
                {
                    // Format hex bytes inline
                    var sb = new System.Text.StringBuilder(entry.LastSample.Length * 3);
                    foreach (byte b in entry.LastSample)
                    {
                        sb.Append(b.ToString("X2"));
                        sb.Append(' ');
                    }
                    ImGui.TextColored(TextMute, sb.ToString().TrimEnd());
                }
            }

            ImGui.EndTable();
        }

        if (visibleCount == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(TextMute, snapshot.Length == 0
                ? "No packets captured yet — join a server and traffic will appear here."
                : "All captured opcodes filtered. Toggle 'Show Known' / 'Show Unknown' to see them.");
        }

        ImGui.End();
    }

    private static void OpenAllWindows()
    {
        _showLogWindow = true;
    }

    private static void CloseAllWindows()
    {
        _showLogWindow = false;
    }
}

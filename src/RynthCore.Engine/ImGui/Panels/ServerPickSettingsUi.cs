// ============================================================================
//  RynthCore.Engine - ImGui/Panels/ServerPickSettingsUi.cs
//  Settings > Diagnostics > Server: which server this character counts as.
//
//  A picker (Auto, or one of the servers the engine knows: the built-in ones
//  and every server a loaded plugin declares), what is active and why, and the
//  full list: verdict, detection, this character's override, and the plugin
//  the other plugins ask for that server's settings. Picking a server forces it
//  on and every other server off for this character (ServerSelectionStore);
//  the menus, the "servermenu" push and plugin API v85 follow on the next frame.
//  Drawn by SettingsFace for the Diagnostics tab's ServerPick row, in its look.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI.Sections;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal static class ServerPickSettingsUi
{
    private static readonly Vector4 Teal = RynthTheme.Vec(0xFF26D9E6), Amber = RynthTheme.Vec(0xFFE8B333),
        Mute = RynthTheme.Vec(0xFFB8C8D8), Good = RynthTheme.Vec(0xFF33FF33);

    // The last pick's outcome, shown under the picker until the next one.
    private static string _message = string.Empty;

    /// <summary>The row: picker, active server, server table. Render thread.</summary>
    public static void Draw(float width, string? tooltip)
    {
        SectionRegistry.Snapshot snap = SectionRegistry.Current;
        IReadOnlyList<ServerState> servers = snap.Servers;
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        if (servers.Count == 0)
        {
            SafeImGui.TextDisabled("No servers known yet (the list fills in once a character is in game).");
            return;
        }

        string? pick = ServerSelection.CurrentPick(servers);
        DrawPicker(servers, pick, width, tooltip);

        ServerState? active = ServerSelection.ActiveServer(servers);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        if (active == null)
            SafeImGui.TextColored(Mute, "Active: none (no custom server matched; retail rules).");
        else
            SafeImGui.TextColored(Teal, $"Active: {active.Name} ({active.Id}) - {active.Reason}");
        if (_message.Length > 0) SafeImGui.TextColored(Amber, _message);

        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
        DrawTable(servers, snap.Manifests);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
    }

    private static void DrawPicker(IReadOnlyList<ServerState> servers, string? pick, float width, string? tooltip)
    {
        string preview = pick == null ? "Mixed (set per server with /rc server <id> on|off)"
            : pick == ServerSelection.Auto ? "Auto (detect)"
            : Label(servers, pick);

        ImGuiNET.ImGui.AlignTextToFramePadding();
        SafeImGui.TextColored(Mute, "Server");
        if (tooltip != null) SafeImGui.SetItemTooltip(tooltip);
        ImGuiNET.ImGui.SameLine(90);
        ImGuiNET.ImGui.SetNextItemWidth(Math.Max(160, width - 96));
        if (ImGuiNET.ImGui.BeginCombo("##serverpick", preview))
        {
            if (ImGuiNET.ImGui.Selectable("Auto (detect)##pick_auto", pick == ServerSelection.Auto))
                Pick(ServerSelection.Auto);
            foreach (ServerState s in servers)
            {
                string marks = (s.Active ? "  [active]" : string.Empty) + (s.Detected ? "  [detected]" : string.Empty);
                if (ImGuiNET.ImGui.Selectable(s.Name + " (" + s.Id + ")" + marks + "##pick_" + s.Id,
                        pick != null && pick.Equals(s.Id, StringComparison.OrdinalIgnoreCase)))
                    Pick(s.Id);
            }
            ImGuiNET.ImGui.EndCombo();
        }
        if (tooltip != null) SafeImGui.SetItemTooltip(tooltip);
    }

    private static void Pick(string id)
    {
        ServerSelectionStore.Apply(id, out string message);
        _message = message;
    }

    private static void DrawTable(IReadOnlyList<ServerState> servers, IReadOnlyList<SectionManifest> manifests)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (!ImGuiNET.ImGui.BeginTable("##servers", 5, flags)) return;
        ImGuiNET.ImGui.TableSetupColumn("Server", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGuiNET.ImGui.TableSetupColumn("Active", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGuiNET.ImGui.TableSetupColumn("Detected", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGuiNET.ImGui.TableSetupColumn("Override", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGuiNET.ImGui.TableSetupColumn("Settings from", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGuiNET.ImGui.TableHeadersRow();
        foreach (ServerState s in servers)
        {
            ImGuiNET.ImGui.TableNextRow();
            ImGuiNET.ImGui.TableNextColumn();
            SafeImGui.TextColored(s.Active ? Teal : Mute, s.Name + " (" + s.Id + ")");
            if (!string.IsNullOrEmpty(s.Reason)) SafeImGui.SetItemTooltip(s.Reason);
            ImGuiNET.ImGui.TableNextColumn();
            SafeImGui.TextColored(s.Active ? Good : Mute, s.Active ? "yes" : "no");
            ImGuiNET.ImGui.TableNextColumn();
            SafeImGui.TextColored(Mute, s.Detected ? "yes" : "no");
            ImGuiNET.ImGui.TableNextColumn();
            SafeImGui.TextColored(s.Force == ServerForce.Auto ? Mute : Amber, ServerForceText.Name(s.Force));
            ImGuiNET.ImGui.TableNextColumn();
            string owner = ServerSelection.OwnerPlugin(manifests, s.Id, SectionRegistry.EnginePlugin);
            SafeImGui.TextColored(owner.Length > 0 ? Teal : Mute, owner.Length > 0 ? owner : "(no plugin loaded)");
        }
        ImGuiNET.ImGui.EndTable();
    }

    private static string Label(IReadOnlyList<ServerState> servers, string id)
    {
        foreach (ServerState s in servers)
            if (s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return s.Name + " (" + s.Id + ")";
        return id;
    }
}

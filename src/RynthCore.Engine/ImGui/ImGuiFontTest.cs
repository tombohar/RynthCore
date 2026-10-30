// ============================================================================
//  RynthCore.Engine - ImGui/ImGuiFontTest.cs
//  Developer check for the atlas and theme (docs/IMGUI_PARITY_PLAN.md P0
//  acceptance): every baked font with sample text, every merged symbol and
//  icons inline with text (to judge their size and baseline), then every
//  baked Phosphor icon, plus the theme swatches. Only with RYNTHCORE_IMGUI_FONTTEST=1 in the
//  client's environment or after /rc imgui fonttest; never shown otherwise.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;

namespace RynthCore.Engine.ImGuiBackend;

internal static class ImGuiFontTest
{
    /// <summary>Shown when RYNTHCORE_IMGUI_FONTTEST=1, or toggled with /rc imgui fonttest.</summary>
    public static volatile bool Enabled =
        Environment.GetEnvironmentVariable("RYNTHCORE_IMGUI_FONTTEST") == "1";

    private const string Sample = "The quick brown fox 0123456789 — …";
    private const string Symbols = "● • → ←";
    private const string Icons = PhosphorIcons.Gear + " Settings  " + PhosphorIcons.Play + " Run  " + PhosphorIcons.Trash
        + " Delete  " + PhosphorIcons.Sword + PhosphorIcons.Sparkle + PhosphorIcons.SneakerMove + PhosphorIcons.Bag + " Hxg";

    public static void Draw(float scale)
    {
        ImGuiNET.ImGui.SetNextWindowSize(new Vector2(560, 520) * scale, ImGuiCond.FirstUseEver);
        if (ImGuiNET.ImGui.Begin("RynthCore font test##fonttest"))
        {
            SafeImGui.TextDisabled($"scale {scale:0.##}  atlas {ImGuiNET.ImGui.GetIO().Fonts.TexWidth}x{ImGuiNET.ImGui.GetIO().Fonts.TexHeight}");
            for (UiFont f = 0; f < UiFont.Count; f++)
            {
                ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(f));
                ImGuiNET.ImGui.TextUnformatted($"{f,-9} {Sample}  {Symbols}  {Icons}");
                ImGuiNET.ImGui.PopFont();
            }

            ImGuiNET.ImGui.Separator();
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui14));
            ImGuiNET.ImGui.PushTextWrapPos(0);
            ImGuiNET.ImGui.TextUnformatted(PhosphorIcons.Baked);
            ImGuiNET.ImGui.PopTextWrapPos();
            ImGuiNET.ImGui.PopFont();

            ImGuiNET.ImGui.Separator();
            Swatch("bar", RynthTheme.BarBackground); Swatch("button", RynthTheme.BarButton);
            Swatch("glass", RynthTheme.PanelGlass); Swatch("teal", RynthTheme.PanelBorder);
            Swatch("header", RynthTheme.PanelHeader); Swatch("rynthai", RynthTheme.RynthAiBackground);
            Swatch("gold", RynthTheme.RadarGold);
            ImGuiNET.ImGui.NewLine();
            ImGuiNET.ImGui.TextColored(ImGuiNET.ImGui.ColorConvertU32ToFloat4(RynthTheme.RcLabel), "RC");
            ImGuiNET.ImGui.SameLine();
            ImGuiNET.ImGui.TextColored(ImGuiNET.ImGui.ColorConvertU32ToFloat4(RynthTheme.ReloadLabel), "RL");
            ImGuiNET.ImGui.SameLine();
            SafeImGui.TextColored(ImGuiNET.ImGui.ColorConvertU32ToFloat4(RynthTheme.PopOutLabel), PhosphorIcons.ArrowSquareOut);
        }
        ImGuiNET.ImGui.End();
    }

    private static void Swatch(string label, uint color)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float h = ImGuiNET.ImGui.GetTextLineHeight();
        ImGuiNET.ImGui.GetWindowDrawList().AddRectFilled(p, p + new Vector2(h * 2, h), color);
        ImGuiNET.ImGui.Dummy(new Vector2(h * 2, h));
        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.TextUnformatted(label);
        ImGuiNET.ImGui.SameLine();
    }
}

// ============================================================================
//  RynthCore.Engine - ImGui/VideoPatchBanner.cs
//  The "rendering paused" banner of the video patch's "all" mode
//  (D3D9/VideoPatch.cs): AC draws nothing then (the back buffer is only
//  cleared to black), so the window says why and how to get it back. Drawn on
//  ImGui's background list, under every panel. AC's render thread, inside the
//  main ImGui frame.
// ============================================================================

using System.Numerics;

namespace RynthCore.Engine.ImGuiBackend;

internal static class VideoPatchBanner
{
    private const string Line1 = "Rendering paused (video patch: all)";
    private const string Line2 = "Type /rc video off, or Settings > Misc > Video patch";

    private static readonly uint Back = RynthTheme.Argb(0xC0101820);
    private static uint Border => RynthTheme.Border;
    private static uint Title => RynthTheme.Accent;
    private static uint Text => RynthTheme.TextMuted;

    public static void Draw()
    {
        if (!D3D9.VideoPatch.WantsBanner) return;
        ImGuiNET.ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();
        Vector2 display = io.DisplaySize;
        if (display.X <= 1 || display.Y <= 1) return;

        var dl = ImGuiNET.ImGui.GetBackgroundDrawList();
        Vector2 s1 = ImGuiNET.ImGui.CalcTextSize(Line1);
        Vector2 s2 = ImGuiNET.ImGui.CalcTextSize(Line2);
        float w = System.MathF.Max(s1.X, s2.X) + 32f;
        float h = s1.Y + s2.Y + 28f;
        Vector2 min = new((display.X - w) * 0.5f, (display.Y - h) * 0.5f);
        Vector2 max = min + new Vector2(w, h);
        dl.AddRectFilled(min, max, Back, 6f);
        dl.AddRect(min, max, Border, 6f);
        dl.AddText(new Vector2((display.X - s1.X) * 0.5f, min.Y + 10f), Title, Line1);
        dl.AddText(new Vector2((display.X - s2.X) * 0.5f, min.Y + 18f + s1.Y), Text, Line2);
    }
}

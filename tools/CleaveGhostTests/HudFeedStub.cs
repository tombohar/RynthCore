// A stand-in for src\RynthCore.Engine\ImGui\MonsterHud\HudFeed.cs, the combat text's feed, so
// MonsterHudData.cs compiles here without the ImGui HUD. It only counts what it is told.
namespace RynthCore.Engine.ImGuiBackend.Hud;

internal static class HudFeed
{
    public static int HealthCalls;

    public static void OnHealth(uint id, float prev, float ratio, uint max) => HealthCalls++;
}

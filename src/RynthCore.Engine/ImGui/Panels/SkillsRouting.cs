// ============================================================================
//  RynthCore.Engine - ImGui/Panels/SkillsRouting.cs
//  Where "open Skills" goes. Skills / Attributes / Progress live under RynthOracle
//  when its tabs can be reached; otherwise the standalone Skills panel stays the
//  way in, so nobody loses Skills (bar icon, /rc skills, the Character menu).
//
//  "Can be reached" = the Oracle script window is registered AND Oracle has
//  announced the bridge with `/rc oracle-skills` (OracleSkillsEmbed.TabsAvailable);
//  an Oracle without the tabs never announces, so it counts as absent.
// ============================================================================

using RynthCore.Engine.UI;
using RynthCore.Engine.UI.ScriptWindows;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal static class SkillsRouting
{
    /// <summary>Oracle's main script window (UiWindow "RynthOracle/Oracle").</summary>
    public const string OracleKey = OracleSkillsEmbed.OraclePanelKeyFragment;

    /// <summary>Oracle provides the Skills / Attributes / Progress tabs now. Any thread.</summary>
    public static bool OracleTabsAvailable => OracleSkillsEmbed.TabsAvailable(ScriptWindowRegistry.Has(OracleKey));

    /// <summary>The bar's Skills icon shows exactly when Oracle can't provide the tabs. Any thread.</summary>
    public static bool ShowSkillsBarIcon => !OracleTabsAvailable;

    /// <summary>The Oracle command that selects a tab: skills, attrs or progress.</summary>
    public static string OracleCommand(int tab) => tab switch
    {
        1 => "/ro attrs",
        2 => "/ro progress",
        _ => "/ro skills",
    };

    /// <summary>
    /// Opens Skills (0), Attributes (1) or Progress (2): in Oracle when its tabs are there, else the
    /// standalone Skills panel on that tab. Returns false when neither exists (no ImGui layer).
    /// </summary>
    public static bool Open(int tab)
    {
        if (OracleTabsAvailable)
        {
            ServerMenus.Run(OracleCommand(tab));
            return true;
        }
        if (!ImGuiPanelHost.HasFace(SkillsFace.Title)) return false;
        if (tab == 2)
        {
            SkillsFace.ShowProgression();
            return true;
        }
        SkillsFace.RequestTab(tab);
        if (!ImGuiPanelHost.IsOpen(SkillsFace.Title)) PanelRouter.Toggle(SkillsFace.Title);
        return true;
    }

    /// <summary>
    /// "/rc skills show|hide|toggle". With Oracle: show opens Oracle on Skills, hide closes Oracle only while
    /// it is showing a Skills-family tab, toggle closes it when it already shows Skills. Without: the panel.
    /// </summary>
    public static bool SetOpen(bool? show)
    {
        if (!OracleTabsAvailable)
        {
            if (!ImGuiPanelHost.HasFace(SkillsFace.Title)) return false;
            if (show == null || show.Value != ImGuiPanelHost.IsOpen(SkillsFace.Title))
                PanelRouter.Toggle(SkillsFace.Title);
            return true;
        }

        bool oracleOpen = ScriptWindowRegistry.IsOpen(OracleKey);
        int tab = OracleSkillsEmbed.ActiveTab;
        bool onSkills = oracleOpen && tab == 0;
        if (show == false || (show == null && onSkills))
        {
            if (oracleOpen && tab >= 0) ScriptWindowRegistry.PlayerSetVisible(OracleKey, false);
            return true;
        }
        if (show == true && onSkills) return true;
        ServerMenus.Run(OracleCommand(0));
        return true;
    }
}

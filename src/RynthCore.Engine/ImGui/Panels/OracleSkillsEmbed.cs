// ============================================================================
//  RynthCore.Engine - ImGui/Panels/OracleSkillsEmbed.cs
//  Bridges RynthOracle's Skills / Attributes / Progress tabs to the engine
//  SkillsFace body so raise/train/progression stay in one place under Oracle.
//  Oracle sets ActiveTab (`/rc oracle-skills <skills|attrs|progress|off>`) while
//  those tabs are selected; ScriptWindowFace for the Oracle window draws the
//  embed after the script tab strip.
//
//  Idea and first version: Silentkelpie (RynthCore PR 6).
//
//  Lifecycle (the part that must not leak): the embedded SkillsFace is "shown"
//  exactly once when it first draws, and "hidden" exactly once when it stops:
//    - Clear() (Oracle left the tabs: `/rc oracle-skills off`),
//    - the Oracle window closed / removed / its plugin unloaded (ScriptWindowFace
//      OnHidden, ScriptWindowRegistry removal and Reset on engine shutdown),
//    - a safety sweep: no draw for IdleMs (window collapsed, owner crashed).
//  Redraws while shown only refresh the idle clock (NoteDrawn). SkillsFace binds
//  its OnShown / OnHidden here; the callbacks go through a ShownLatch, so they
//  alternate no matter how often or from which thread these are called.
//
//  Skills stays reachable without Oracle: TabsAvailable is true only while the
//  Oracle window is registered AND Oracle has announced itself by sending any
//  `/rc oracle-skills` (an older Oracle never does), so the standalone Skills bar
//  icon / panel stays for everyone who cannot reach the Oracle tabs.
//
//  Pure of ImGui and the plugin registry, so tools/BarDeclutterTests compiles it.
// ============================================================================

using System;
using System.Threading;

namespace RynthCore.Engine.ImGuiBackend.Panels;

/// <summary>Which SkillsFace body Oracle wants drawn inside its window (-1 = none).</summary>
internal static class OracleSkillsEmbed
{
    /// <summary>0 = Skills, 1 = Attributes, 2 = Progress (ILT Progression). -1 = off.</summary>
    private static int _activeTab = -1;

    /// <summary>1 once Oracle has sent a `/rc oracle-skills` (any argument) in this engine generation.</summary>
    private static int _announced;

    private static long _lastDrawnMs;

    private static Action? _onShown, _onHidden;

    private static readonly ShownLatch Latch = new(
        static () => _onShown?.Invoke(),
        static () => _onHidden?.Invoke());

    /// <summary>No draw for this long and the embed counts as hidden (Oracle collapsed, closed unseen, crashed).</summary>
    public const long IdleMs = 1000;

    /// <summary>Oracle panel key prefix used by ScriptWindowRegistry ("script:…").</summary>
    public const string OraclePanelKeyFragment = "RynthOracle/Oracle";

    public static int ActiveTab => Volatile.Read(ref _activeTab);

    /// <summary>True between the first embed draw and the hide (Clear, window gone, idle).</summary>
    public static bool IsShown => Latch.IsShown;

    /// <summary>SkillsFace hands over what to run on the shown / hidden edges. Once per engine generation.</summary>
    public static void Bind(Action shown, Action hidden)
    {
        _onShown = shown;
        _onHidden = hidden;
    }

    /// <summary>The command saw `/rc oracle-skills` from the Oracle plugin: it has the tabs. Any thread.</summary>
    public static void Announce() => Volatile.Write(ref _announced, 1);

    /// <summary>
    /// Whether the Oracle Skills / Attributes / Progress tabs can be reached: the Oracle script window is
    /// registered and Oracle has announced the bridge. False for an Oracle without the tabs, or none.
    /// </summary>
    public static bool TabsAvailable(bool oracleWindowRegistered) =>
        oracleWindowRegistered && Volatile.Read(ref _announced) != 0;

    /// <summary>Called from the Oracle plugin (pump/render) when a Skills-family tab is shown. Out of range = off.</summary>
    public static void SetActiveTab(int tab)
    {
        if (tab is < 0 or > 2) { Clear(); return; }
        Volatile.Write(ref _activeTab, tab);
    }

    /// <summary>Clears the embed when Oracle leaves Skills / Attributes / Progress, and hides the face. Any thread.</summary>
    public static void Clear()
    {
        Volatile.Write(ref _activeTab, -1);
        Latch.Hide();
    }

    /// <summary>
    /// The embed drew this frame (AC thread): keeps it shown. The face's OnShown runs on the first
    /// frame only. Ignored when the embed was cleared meanwhile.
    /// </summary>
    public static void NoteDrawn(long nowMs)
    {
        if (ActiveTab < 0) return;
        Volatile.Write(ref _lastDrawnMs, nowMs);
        Latch.Show();
    }

    /// <summary>Once a frame (AC thread): hides an embed that stopped drawing for IdleMs.</summary>
    public static void Sweep(long nowMs)
    {
        if (!Latch.IsShown) return;
        if (nowMs - Volatile.Read(ref _lastDrawnMs) > IdleMs) Latch.Hide();
    }

    /// <summary>Engine shutdown / reload: hide the face and forget the announcement.</summary>
    public static void Shutdown()
    {
        Clear();
        Volatile.Write(ref _announced, 0);
    }

    public static bool IsOraclePanel(string panelKey) =>
        panelKey.Contains(OraclePanelKeyFragment, StringComparison.OrdinalIgnoreCase);
}

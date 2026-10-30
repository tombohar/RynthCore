// ============================================================================
//  RynthCore.Engine - UI/PanelRouter.cs
//  The one place that decides which face shows a panel.
//
//  Model (Tom, 2026-09-27): a panel docked in the client is drawn by its
//  ImGui face; a panel popped out of the client is an Avalonia window. While
//  the ImGui faces are being built, each panel's docked face is chosen per
//  panel (PanelFaceStore; code default Avalonia) and falls back to Avalonia
//  whenever ImGui can't show it.
//
//  Every "open/close this panel" request goes through Toggle: bar buttons
//  (docked and floating bar), the RynthAi launcher, Chat's Filters button and
//  chat commands. Bar actions that aren't panels (RL, bar pop-out) pass
//  straight through.
//
//  Pop-out/redock: the ImGui face's ↗ and ↙ go through ImGuiPanelHost.PopOut
//  and Redock; a popped-out panel is the same ImGui face in its own window
//  (ImGuiPopOuts; Tom, 2026-09-29). The Avalonia face's ↙ (a panel still popped
//  out in Avalonia from before) asks RedockToImGui first.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using RynthCore.Engine.ImGuiBackend;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI;

internal static class PanelRouter
{
    /// <summary>The face that shows <paramref name="title"/> while it is docked in the client.</summary>
    public static PanelFace ResolveDockedFace(string title)
    {
        title = Canonical(title);
        if (!EngineSettings.EnableImGuiBackend || !ImGuiPanelHost.HasFace(title))
            return PanelFace.Avalonia;
        // Tom, 2026-09-28: in the client it's always ImGui; Avalonia only when
        // popped out. A per-panel face saved while the faces were being built
        // (PanelFaceStore) no longer puts an Avalonia panel back in the client.
        return PanelFace.ImGui;
    }

    /// <summary>True when panels can pop out of the client (into ImGui windows, ImGuiPopOuts).</summary>
    public static bool CanPopOut => EngineSettings.EnableImGuiBackend;

    /// <summary>Opens the panel if it is closed, closes it if it is open. Any thread.</summary>
    public static void Toggle(string title)
    {
        title = Canonical(title);
        PanelFace face = ResolveDockedFace(title);
        if (face == PanelFace.Avalonia)
        {
            AvaloniaOverlay.ActivateBarButton(title);
            return;
        }

        if (ImGuiPanelHost.IsOpen(title))
        {
            ImGuiPanelHost.Close(title);
            if (face == PanelFace.Both) AvaloniaOverlay.ActivateBarButton(title);
            return;
        }

        // Open (popped out or in the client, the button closes it) is handled above;
        // a panel last left popped out reopens popped out (ImGuiPanelHost.Apply).
        ImGuiPanelHost.Open(title);
        if (face == PanelFace.Both) AvaloniaOverlay.ActivateBarButton(title);
    }

    // The basic Monsters panel was retired (2026-10-01): its rule editing moved into
    // the Damage panel and Monster Detail. "Monsters" stays a known title (EntryPoint
    // still registers its Avalonia view, and saved layouts and commands name it), and
    // every request for it goes to Damage.
    private const string RetiredMonstersTitle = "Monsters", MonstersPanelTitle = "Damage";

    /// <summary>The panel that shows <paramref name="title"/>: "Monsters" is the Damage panel now.</summary>
    public static string Canonical(string title) =>
        title.Equals(RetiredMonstersTitle, StringComparison.OrdinalIgnoreCase) ? MonstersPanelTitle : title;

    /// <summary>The dashboard's Monsters button: the Damage panel.</summary>
    public static void ToggleMonsters() => Toggle(MonstersPanelTitle);

    /// <summary>
    /// Engine init, before any panel restores: a "Monsters" panel saved open is saved
    /// closed, and Damage open in its place (where Monsters was if Damage has no spot yet).
    /// </summary>
    public static void MoveRetiredMonstersPanel()
    {
        if (!PanelStateStore.TryGetPanel(RetiredMonstersTitle, out PanelStateStore.PanelEntry mon) || !mon.Open) return;
        PanelStateStore.MarkPanelClosed(RetiredMonstersTitle);
        if (PanelStateStore.TryGetPanel(MonstersPanelTitle, out PanelStateStore.PanelEntry dmg))
        {
            if (!dmg.Open) PanelStateStore.SetPanel(MonstersPanelTitle, dmg with { Open = true });
        }
        else PanelStateStore.SetPanel(MonstersPanelTitle, mon);
        RynthLog.UI("PanelRouter: the Monsters panel was saved open; opening Damage in its place (Monsters is retired).");
    }

    /// <summary>
    /// Asked by the Avalonia face's redock (↙) before it docks itself. True
    /// when the docked face is ImGui: the caller closes its floating window and
    /// calls <see cref="CompleteRedockToImGui"/> instead of docking.
    /// </summary>
    public static bool RedockGoesToImGui(string title) => ResolveDockedFace(title) == PanelFace.ImGui;

    /// <summary>
    /// After the Avalonia floating window closed (its close saved "popped out,
    /// closed"): mark the panel docked and open, then open the ImGui face at
    /// the docked placement. Ordered with the other UI writes.
    /// </summary>
    public static void CompleteRedockToImGui(string title)
    {
        title = Canonical(title);
        UiBackgroundWriter.Enqueue($"redock {title}", () =>
        {
            PanelStateStore.TryGetPanel(title, out PanelStateStore.PanelEntry prior);
            PanelStateStore.SetPanel(title, prior with { Open = true, Floating = false });
            ImGuiPanelHost.Open(title);
        });
    }

    /// <summary>
    /// Stores the docked face for a panel ("bar" for the overlay bar) and, if
    /// the panel is open docked, moves it to the new face now. Returns a
    /// one-line result for chat. Game thread or AC thread.
    /// </summary>
    public static string SetDockedFace(string title, PanelFace face)
    {
        if (title.Equals("bar", StringComparison.OrdinalIgnoreCase) || title == ImGuiBar.FaceKey)
        {
            PanelFaceStore.Set(ImGuiBar.FaceKey, face == PanelFace.ImGui ? null : face);
            ImGuiBar.ApplyFace();
            PanelFace barNow = ImGuiBar.ResolveFace();
            return $"Bar: docked face = {PanelFaceStore.FaceName(face)}{Note(barNow, face)}.";
        }

        string? known = FindRegisteredTitle(Canonical(title));
        if (known == null)
            return $"No panel named '{title}'. Try /rc ui list.";
        if (face != PanelFace.Avalonia && !ImGuiPanelHost.HasFace(known))
            return $"{known} has no ImGui version yet; it stays Avalonia.";

        PanelFace before = ResolveDockedFace(known);
        PanelFaceStore.Set(known, face == ImGuiPanelHost.CodeDefault(known) ? null : face);
        PanelFace now = ResolveDockedFace(known);
        if (now != before)
            MoveOpenPanel(known, before, now);
        // Last closed while popped out: Toggle would reopen the Avalonia
        // window. The player just picked the docked face, so the next open
        // shows that instead. (An open pop-out is left alone.)
        if (now != PanelFace.Avalonia && PanelStateStore.TryGetPanel(known, out PanelStateStore.PanelEntry saved)
            && saved.Floating && !saved.Open)
            PanelStateStore.SetPanel(known, saved with { Floating = false });
        return $"{known}: docked face = {PanelFaceStore.FaceName(face)}{Note(now, face)}.";
    }

    private static string Note(PanelFace now, PanelFace asked) =>
        now == asked ? "" : $" (showing {PanelFaceStore.FaceName(now)}: the ImGui layer is off or Avalonia is disabled)";

    /// <summary>
    /// "Switching while the panel is open: close in the old face, open in the
    /// new one at the same place" (plan §3.4). Popped-out panels aren't
    /// affected - they are always the Avalonia window. Each hand-off runs its
    /// steps in one ordered sequence so the last panel_state write wins.
    /// </summary>
    private static void MoveOpenPanel(string title, PanelFace before, PanelFace now)
    {
        if (!PanelStateStore.TryGetPanel(title, out PanelStateStore.PanelEntry saved) || saved.Floating)
            return;
        bool imguiOpen = ImGuiPanelHost.IsOpen(title);
        bool avaloniaOpen = before != PanelFace.ImGui && saved.Open; // docked Avalonia face showing

        switch (before, now)
        {
            case (PanelFace.Avalonia, PanelFace.ImGui):
                if (avaloniaOpen)
                    AvaloniaOverlay.PostToUi(() =>
                    {
                        AvaloniaOverlay.TogglePanelOnUiThread(title); // closes it, saving its placement
                        ImGuiPanelHost.Open(title);                   // then opens there (and saves open)
                    });
                break;
            case (PanelFace.Avalonia, PanelFace.Both):
                if (avaloniaOpen) ImGuiPanelHost.Open(title);
                break;
            case (PanelFace.ImGui, PanelFace.Avalonia):
                if (imguiOpen) ImGuiPanelHost.HandOffToAvalonia(title);
                break;
            case (PanelFace.ImGui, PanelFace.Both):
                if (imguiOpen) AvaloniaOverlay.ActivateBarButton(title);
                break;
            case (PanelFace.Both, PanelFace.ImGui):
                if (avaloniaOpen) AvaloniaOverlay.ActivateBarButton(title); // the ImGui copy stays
                break;
            case (PanelFace.Both, PanelFace.Avalonia):
                if (imguiOpen) ImGuiPanelHost.Close(title);                 // the Avalonia copy stays
                break;
        }
    }

    /// <summary>One line per registered panel for /rc ui list and the Status panel.</summary>
    public static List<string> Describe()
    {
        var lines = new List<string>
        {
            $"Bar: docked={PanelFaceStore.FaceName(ImGuiBar.ResolveFace())}{(PanelStateStore.BarFloating ? ", popped out" : ", docked")}",
        };
        foreach (var (title, _) in OverlayHost.GetPanels())
        {
            var sb = new StringBuilder(title).Append(": docked=").Append(PanelFaceStore.FaceName(ResolveDockedFace(title)));
            sb.Append(ImGuiPanelHost.HasFace(title) ? "" : " (no ImGui version yet)");
            if (PanelStateStore.TryGetPanel(title, out var entry))
                sb.Append(entry.Floating ? ", popped out" : ", docked").Append(entry.Open ? ", open" : ", closed");
            lines.Add(sb.ToString());
        }
        return lines;
    }

    private static string? FindRegisteredTitle(string title)
    {
        foreach (var (registered, _) in OverlayHost.GetPanels())
            if (string.Equals(registered, title, StringComparison.OrdinalIgnoreCase))
                return registered;
        return null;
    }
}

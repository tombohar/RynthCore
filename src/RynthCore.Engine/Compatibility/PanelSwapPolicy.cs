// ============================================================================
//  RynthCore.Engine - Compatibility/PanelSwapPolicy.cs
//
//  Pure decisions for AcPanels' repair of AC's panel container after a close
//  (no AC memory here; tools/PanelSwapPolicyTests runs them offline).
//
//  What goes wrong in AC (acclient 4,841,472 bytes; Ghidra + disassembly):
//    gmPanelUI::RecvNotice_SetPanelVisibility (0x004BD380), closing the panel
//    the container shows (+0x10 of its notice handler, element +0x608) that
//    was opened over another one (+0x14, element +0x60C: "previous", set when
//    the new panel has attribute 0x10000049 and the old one hasn't; vitae,
//    book, effects, character information... in layout 0x2100006E):
//      0x004BD4A3  current.SetVisible(0)
//      0x004BD4A6  re-reads previous; if set: open it again (notice), clear it
//      0x004BD4DA  else: current = 0, the container is hidden.
//    UIElement::SetVisible (0x00462390) tells the panel's parents at once
//    (element message 0x18, UIElementManager::BroadcastElementMessage
//    0x0045AC50, synchronous unless a UI message is already being handled),
//    and the container's gmPanelUI::ListenToElementMessage (0x004BD300) sends
//    the same close notice again from inside the first. That inner close
//    reopens the previous panel and clears "previous"; the outer one then
//    reads "no previous", clears current and hides the container. Left: the
//    container hidden with no current panel and the previous panel (the pack)
//    still shown inside it. The next panel AC opens there (gmVitaeUI pops the
//    vitae panel when vitae drops, 0x004A7B48) finds no current panel to
//    close, shows itself and the container - and the pack shows with it,
//    under it, its paper doll's 3D figure drawn over both (the doll is its own
//    render object, linked last: RenderUI::RenderObjects 0x004488A0).
//    A close from a panel's own button runs inside a UI message, so the
//    inner notice waits and AC's swap is right; a close from outside one
//    (RynthCore's bar and /rc panels on the game tick, keys) hits it.
//
//  The repair: before AC handles a close notice, note each container whose
//  current panel is the one closing and which has a previous panel; after,
//  if AC left that container with no current panel while the previous panel
//  still shows, send the open notice for the previous panel (what AC meant):
//  the container shows it, as when the panel's own button closes it.
//  Unknown inputs (0 pointers) never act.
// ============================================================================

namespace RynthCore.Engine.Compatibility;

internal static class PanelSwapPolicy
{
    /// <summary>
    /// Before AC handles a panel notice for one container: the panel AC should bring back when it
    /// closes <paramref name="closing"/> (the container's previous panel), or 0 when there is none to watch.
    /// </summary>
    public static nint PreviousToWatch(bool open, nint closing, nint current, nint previous) =>
        !open && closing != 0 && closing == current && previous != 0 && previous != closing ? previous : 0;

    /// <summary>
    /// After AC handled the close: whether to open <paramref name="watched"/> again, because AC left its
    /// container with no current panel while it still shows (the inner/outer close race). Never a panel a
    /// plugin claimed (its window opens in place of AC's; AC's would pop up).
    /// </summary>
    public static bool ShouldReopen(nint watched, nint currentAfter, bool watchedShows, bool claimed) =>
        watched != 0 && currentAfter == 0 && watchedShows && !claimed;
}

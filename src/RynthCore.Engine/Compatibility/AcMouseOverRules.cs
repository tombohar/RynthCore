// ============================================================================
//  RynthCore.Engine - Compatibility/AcMouseOverRules.cs
//
//  The pure half of AcMouseOver: given what was read out of AC's UI manager,
//  is the cursor on the 3D world or on one of AC's own windows, and may a world
//  overlay (a RynthVision nameplate, drawn under AC's windows) take a click
//  there? No AC memory, no engine state: tools/AcMouseOverTests compiles this
//  file in and checks every case offline.
//
//  Evidence for the constants is in AcMouseOver.cs.
// ============================================================================

namespace RynthCore.Engine.Compatibility;

/// <summary>What AC's own UI has under the mouse.</summary>
internal enum AcMouseTarget
{
    /// <summary>AC's UI state couldn't be read (no manager yet, unreadable memory, not AC's thread).</summary>
    Unknown,
    /// <summary>AC reports nothing under the mouse (cursor outside the client, or no element took it).</summary>
    Nothing,
    /// <summary>The 3D view: AC's gmSmartBoxUI panel (type 0x10000014) or its SmartBox wrapper (0x10000030).</summary>
    World,
    /// <summary>Any other AC element: a window (inventory, chat, radar, vitals, the selected-object bar...).</summary>
    AcWindow,
}

internal static class AcMouseOverRules
{
    /// <summary>UIElement_SmartBoxWrapper::GetUIElementType (0x004E5E80) returns this; it is the 3D view.</summary>
    public const int UIElementTypeSmartBoxWrapper = 0x10000030;

    /// <summary>
    /// gmSmartBoxUI::Register (0x004D71E0) registers this type: the game-play 3D view panel,
    /// element 0x1000049A (gmGamePlayUI::SetupChildren 0x004EAB50; SaveScreenLayout 0x004EB9E0
    /// writes its rect as "&lt;SBOX&gt;"). In game it is what AC reports under the cursor over open
    /// ground (2026-10-10 client logs), not the wrapper: missing it here sent every plate's
    /// click to AC.
    /// </summary>
    public const int UIElementTypeGmSmartBoxUI = 0x10000014;

    /// <summary>
    /// The portal-space view: gmSmartBoxUI's child element 0x10000436, a 3D viewport
    /// (gmSmartBoxUI PostInit 0x004D77C7: GetChildRecursive(0x10000436), DynamicCast 0xD, kept at
    /// +0x678, with the portal swirl object and its light and camera added). gmSmartBoxUI::UseTime
    /// (0x004D7A90) shows it (vtable +0x18 with 1) and hides the SmartBox while a teleport is in
    /// progress (its states 2..5) and hides it again after. It covers the 3D view then, so in
    /// portal space it is what AC has under the cursor where the world normally is: it stands in
    /// for the world, not for one of AC's windows. Counted as an AC window, every window drawn
    /// under AC's UI went click-through for the whole portal (2026-11-20: the DreamWeave Skills
    /// window took no clicks, scrolls or tab changes in portal space).
    /// </summary>
    public const uint ElementIdPortalSpaceView = 0x10000436;

    /// <summary>One of the 3D view's element types.</summary>
    public static bool IsWorldType(int elementType) =>
        elementType == UIElementTypeSmartBoxWrapper || elementType == UIElementTypeGmSmartBoxUI;

    /// <summary>
    /// The 3D view, or what stands in for it: one of its element types, or the portal-space view
    /// (by its element id, <see cref="ElementIdPortalSpaceView"/>).
    /// </summary>
    public static bool IsWorldElement(int elementType, uint elementId) =>
        IsWorldType(elementType) || elementId == ElementIdPortalSpaceView;

    /// <summary>
    /// Classifies one read of UIElementManager. <paramref name="stateReadable"/>: the manager
    /// was known and its fields readable. <paramref name="mouseOutside"/>: its +0x2D4 flag (the
    /// cursor left the client; AC then hit-tests nothing). <paramref name="element"/>: +0x244,
    /// the raw element under the mouse. <paramref name="typeKnown"/>/<paramref name="elementType"/>:
    /// that element's GetUIElementType, when it could be read safely. <paramref name="elementId"/>:
    /// its element id (+0x2E4; 0 when it couldn't be read).
    /// </summary>
    public static AcMouseTarget Classify(bool stateReadable, bool mouseOutside, uint element, bool typeKnown, int elementType,
        uint elementId = 0)
    {
        if (!stateReadable) return AcMouseTarget.Unknown;
        if (mouseOutside || element == 0) return AcMouseTarget.Nothing;
        if (!typeKnown) return AcMouseTarget.Unknown;
        return IsWorldElement(elementType, elementId) ? AcMouseTarget.World : AcMouseTarget.AcWindow;
    }

    /// <summary>
    /// May an overlay drawn under AC's windows take the click? Only an AC window under the
    /// mouse says no: the world says yes, and so does anything AC can't tell us (fail open:
    /// the plates keep today's behaviour when AC's UI can't be read).
    /// </summary>
    public static bool UnderUiOverlayMayTakeClick(AcMouseTarget target) => target != AcMouseTarget.AcWindow;

    /// <summary>
    /// Should a panel drawn under AC's windows (PanelSpec.UnderUi) let the mouse through this frame?
    /// Where an AC window covers the cursor, yes, unless the left button is held on a press that
    /// began on the panel itself (its own drag carries on over the AC window). A press that began on
    /// the game (an item dragged in AC's inventory) is AC's: the panel behind must not catch its
    /// drop (problem report R-20261126-194457-f7d8: every inventory move snapped back while a
    /// DreamWeave panel sat behind AC's inventory).
    /// </summary>
    public static bool UnderUiPanelClickThrough(AcMouseTarget target, bool leftDown, bool pressBelongsToGame) =>
        target == AcMouseTarget.AcWindow && (!leftDown || pressBelongsToGame);
}

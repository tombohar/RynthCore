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

    /// <summary>One of the 3D view's element types.</summary>
    public static bool IsWorldType(int elementType) =>
        elementType == UIElementTypeSmartBoxWrapper || elementType == UIElementTypeGmSmartBoxUI;

    /// <summary>
    /// Classifies one read of UIElementManager. <paramref name="stateReadable"/>: the manager
    /// was known and its fields readable. <paramref name="mouseOutside"/>: its +0x2D4 flag (the
    /// cursor left the client; AC then hit-tests nothing). <paramref name="element"/>: +0x244,
    /// the raw element under the mouse. <paramref name="typeKnown"/>/<paramref name="elementType"/>:
    /// that element's GetUIElementType, when it could be read safely.
    /// </summary>
    public static AcMouseTarget Classify(bool stateReadable, bool mouseOutside, uint element, bool typeKnown, int elementType)
    {
        if (!stateReadable) return AcMouseTarget.Unknown;
        if (mouseOutside || element == 0) return AcMouseTarget.Nothing;
        if (!typeKnown) return AcMouseTarget.Unknown;
        return IsWorldType(elementType) ? AcMouseTarget.World : AcMouseTarget.AcWindow;
    }

    /// <summary>
    /// May an overlay drawn under AC's windows take the click? Only an AC window under the
    /// mouse says no: the world says yes, and so does anything AC can't tell us (fail open:
    /// the plates keep today's behaviour when AC's UI can't be read).
    /// </summary>
    public static bool UnderUiOverlayMayTakeClick(AcMouseTarget target) => target != AcMouseTarget.AcWindow;
}

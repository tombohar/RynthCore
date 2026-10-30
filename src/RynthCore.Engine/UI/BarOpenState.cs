// ============================================================================
//  RynthCore.Engine - UI/BarOpenState.cs
//  Which toolbar buttons (ImGuiBar) draw as open: a button is highlighted in
//  the theme's accent while a window it stands for is open, docked or popped
//  out (a popped-out panel stays open in ImGuiPanelHost).
//
//  What a button stands for: an engine panel's or a plugin window's button its
//  title (a plugin window that took over an engine panel has that panel's
//  title), a plugin script window's button that window's panel key ("script:
//  ...", not its title); a button that opens a menu of several (ILT, Scripts)
//  every window it lists, open while any of them is.
//
//  Pure (no ImGui, no engine state): the caller passes the open check
//  (ImGuiPanelHost.IsOpen, one set lookup a key). Tests: tools/BarLayoutTests.
//  Any thread.
// ============================================================================

using System;

namespace RynthCore.Engine.UI;

internal static class BarOpenState
{
    /// <summary>
    /// The window a one-window button stands for: a script window's panel key when it is one,
    /// else the title (engine panels, ImGui-only panels, plugin windows).
    /// </summary>
    public static string KeyFor(string title, string? scriptPanelKey) =>
        string.IsNullOrEmpty(scriptPanelKey) ? title : scriptPanelKey;

    /// <summary>A one-window button is open while its window is (an empty key never is).</summary>
    public static bool IsOpen(string key, Func<string, bool> isOpen) =>
        !string.IsNullOrEmpty(key) && isOpen(key);

    /// <summary>A button for several windows (ILT, Scripts) is open while any of them is; none listed = closed.</summary>
    public static bool AnyOpen(ReadOnlySpan<string> keys, Func<string, bool> isOpen)
    {
        foreach (string key in keys)
            if (IsOpen(key, isOpen)) return true;
        return false;
    }
}

// ============================================================================
//  RynthCore.Engine - ImGui/ImGuiTextFocus.cs
//  Which RynthCore ImGui text box holds the keyboard, per ImGui context (the
//  main frame and each pop-out), seen from the box itself: FaceKit.TextBox and
//  the script-window replay call NoteActive while their InputText is the
//  active item. That is the same frame the click lands, one frame ahead of
//  io.WantTextInput (which ImGui publishes from the previous frame's widgets).
//
//  Used two ways by Win32Backend's WndProc:
//    * keyboard capture: a box active in the last frame keeps keys from AC
//      even before io.WantTextInput catches up (the click-then-type frame);
//    * the leak diagnostic: a key-down or char that is about to reach AC while
//      a box was active logs one "[Input] key reached AC ..." line per burst.
//
//  Threads: AC's main thread only (ImGui frames and the game WndProc both run
//  there). No locks.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Engine.ImGuiBackend;

internal static class ImGuiTextFocus
{
    private static string? _frameOwner;   // the frame being built
    private static string? _mainOwner;    // the last main frame's
    private static readonly Dictionary<string, string> PopOwners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A text box is the active item in the frame being built. AC thread, in the draw.</summary>
    public static void NoteActive(string id)
    {
        if (_frameOwner != null) return;
        string? panel = ImGuiPanelHost.DrawingTitle;
        _frameOwner = string.IsNullOrEmpty(panel) ? id : panel + " " + id;
        if (ImGuiPopOuts.InPopOutFrame) _frameOwner += " (popped out)";
    }

    /// <summary>A frame (main or pop-out) is about to be built.</summary>
    public static void BeginFrame() => _frameOwner = null;

    /// <summary>The main frame was built. <paramref name="wantTextInput"/> is its io.WantTextInput.</summary>
    public static void EndMainFrame(bool wantTextInput)
    {
        _mainOwner = _frameOwner ?? (wantTextInput ? "a text field in the client" : null);
        _frameOwner = null;
    }

    /// <summary>No main frame this tick (nothing ImGui open, or the frame failed): no box.</summary>
    public static void ClearMain()
    {
        _mainOwner = null;
        _frameOwner = null;
    }

    /// <summary>A box was active in the last main frame (FaceKit / script windows only).</summary>
    public static bool MainBoxActive => _mainOwner != null;

    /// <summary>A pop-out's frame was built. Returns true when one of its boxes was active.</summary>
    public static bool EndPopOutFrame(string title, bool wantTextInput)
    {
        string? owner = _frameOwner ?? (wantTextInput ? title + " (popped out)" : null);
        _frameOwner = null;
        if (owner != null) PopOwners[title] = owner;
        else PopOwners.Remove(title);
        return owner != null;
    }

    public static void ForgetPopOut(string title) => PopOwners.Remove(title);

    /// <summary>The box holding the keyboard (main frame first), or null.</summary>
    public static string? ActiveOwner
    {
        get
        {
            if (_mainOwner != null) return _mainOwner;
            foreach (string owner in PopOwners.Values) return owner;
            return null;
        }
    }
}

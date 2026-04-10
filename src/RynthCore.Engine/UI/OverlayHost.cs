// ============================================================================
//  RynthCore.Engine - UI/OverlayHost.cs
//  Static registry for overlay panels. Engine code registers panels before
//  AvaloniaOverlay.Start() is called; the window reads them at creation time.
// ============================================================================

using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace RynthCore.Engine.UI;

internal static class OverlayHost
{
    private static readonly List<(string Title, Func<Control> Factory)> _panels = new();

    /// <summary>
    /// Register a panel tab. Call before AvaloniaOverlay.Start().
    /// Factory is invoked on the Avalonia UI thread when the window is created.
    /// </summary>
    internal static void RegisterPanel(string title, Func<Control> factory)
        => _panels.Add((title, factory));

    internal static IReadOnlyList<(string Title, Func<Control> Factory)> GetPanels()
        => _panels;
}

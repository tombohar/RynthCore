// ============================================================================
//  RynthCore.Engine - ImGui/Panels/ShownLatch.cs
//  A shown / hidden pair that fires its callbacks once per transition.
//
//  Show() runs the "shown" callback only when the latch is off; Hide() runs the
//  "hidden" callback only when it is on. Calling either twice in a row is a no-op,
//  so a face that wants to be "kept shown" every frame (Show) cannot re-run its
//  setup, and a hide that never had a show (or a second hide) cannot unbalance a
//  refcount like UiSource.Subscribe / Unsubscribe.
//
//  Pure (no ImGui, no engine state). Any thread: the callbacks run under the
//  latch's own lock, so a Show and a Hide from two threads still alternate.
//  If the shown callback throws, the latch stays on, so the matching Hide runs.
//  Tests: tools/BarDeclutterTests.
// ============================================================================

using System;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class ShownLatch
{
    private readonly object _gate = new();
    private readonly Action _shown;
    private readonly Action _hidden;
    private volatile bool _on;

    public ShownLatch(Action shown, Action hidden)
    {
        _shown = shown;
        _hidden = hidden;
    }

    /// <summary>True between a Show that ran its callback and the Hide that ends it.</summary>
    public bool IsShown => _on;

    /// <summary>Turns the latch on. Returns true (and runs the callback) only on the off to on edge.</summary>
    public bool Show()
    {
        if (_on) return false;
        lock (_gate)
        {
            if (_on) return false;
            _on = true;
            _shown();
            return true;
        }
    }

    /// <summary>Turns the latch off. Returns true (and runs the callback) only on the on to off edge.</summary>
    public bool Hide()
    {
        if (!_on) return false;
        lock (_gate)
        {
            if (!_on) return false;
            _on = false;
            _hidden();
            return true;
        }
    }
}

// =============================================================================
//  RynthCore.Engine - UI/PanelDestroyFallback.cs
//  What LayeredWindow.Dispose does when it could NOT post the self-destroy to a
//  panel window (the window is already gone, or its thread's queue is full).
//
//  Win32 only lets the thread that created a window destroy it. The normal path
//  (LayeredWindow.Retire) posts WM_RYNTH_SELF_DESTROY to the panel itself, so the
//  panel's own WndProc destroys it on its owner thread. This is only the backup,
//  and it never sends the destroy to another thread: a panel created on the
//  Avalonia UI thread could not be destroyed by the game thread (ACCESS_DENIED).
//
//  Pure, so tools\PanelWindowTests can check it. Idea from Silentkelpie
//  (rynth PR #1, LayeredWindow.DestroyOnOwnerThread); re-implemented here.
// =============================================================================

namespace RynthCore.Engine.UI;

internal enum PanelDestroyFallback
{
    /// <summary>No owner thread: the window no longer exists. Nothing to do.</summary>
    AlreadyGone,

    /// <summary>The calling thread owns the window: DestroyWindow right here.</summary>
    DestroyHere,

    /// <summary>Another thread owns it: leave it hidden (Retire already hid it). Never cross-thread DestroyWindow.</summary>
    LeaveHidden,
}

internal static class PanelDestroyPlan
{
    /// <param name="ownerThread">GetWindowThreadProcessId(hwnd); 0 when the window is gone.</param>
    /// <param name="currentThread">GetCurrentThreadId().</param>
    /// <param name="ourPanel">The window's class is one of our panel classes. A handle can be reused
    /// once the window is gone; never destroy a window that isn't a panel.</param>
    internal static PanelDestroyFallback Choose(uint ownerThread, uint currentThread, bool ourPanel)
    {
        if (ownerThread == 0) return PanelDestroyFallback.AlreadyGone;
        if (!ourPanel) return PanelDestroyFallback.AlreadyGone; // the handle now belongs to someone else
        return ownerThread == currentThread ? PanelDestroyFallback.DestroyHere : PanelDestroyFallback.LeaveHidden;
    }
}

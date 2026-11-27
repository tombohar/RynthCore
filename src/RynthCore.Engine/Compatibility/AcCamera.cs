// ============================================================================
//  RynthCore.Engine - Compatibility/AcCamera.cs
//  Plugin API v99 (2026-11-24): AC's camera from a plugin, for the phone's Live
//  view (RynthRemote's remote play): turn, tilt and zoom while held, and the
//  views (the default one behind the character - "home" - and first person).
//
//  It goes through AC's own camera key handler, CameraManager::OnAction(InputEvent
//  const&) (0x00456060), with a made-up event: the action at +0x00, pressed at
//  +0x20 - all that handler reads for its actions 0x33-0x3C. So a press does
//  exactly what AC's camera key does, held until its release:
//    0x33 Closer      CameraSet::Closer(1, 1.0f)        / StopCloser
//    0x34 Farther     CameraSet::Farther(1, 1.0f)       / StopFarther
//    0x35/0x36 turn   CameraSet::Rotate(a==0x35, 1, 1.0f, 1) / StopRotating
//    0x37 Raise       CameraSet::Raise(1, 1.0f)         / StopRaising
//    0x38 Lower       CameraSet::Lower(1, 1.0f)         / StopLowering
//    0x39 default     CameraSet::SetDefaultOffsets(1) (on the press only)
//    0x3A first person CameraSet::SetInHead()
//  The handler's this is the CameraManager's InputEventHandler part, the manager
//  + 4 (it reads its CameraSet at [this + 0xB8] = manager + 0xBC). The manager is
//  SmartBox + 0xA0 (CameraSet::CameraSet: [smartbox + 0xA0]); its CameraSet at
//  + 0xBC points back at both (+0x08 the manager, +0x0C the SmartBox), which is
//  checked before every call. Pattern only: a guessed address is never called.
//  Queued from any thread, done on AC's game thread before its tick, like the
//  shortcut slots. Nothing here throws across the boundary.
// ============================================================================

using System;
using System.Collections.Concurrent;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static unsafe class AcCamera
{
    /// <summary>The plugin API's camera actions (the SDK's RynthCameraAction).</summary>
    public enum Action
    {
        Home = 0,         // the default view behind the character (press only)
        RotateLeft = 1,   // held: the view turns left (the camera swings round to her right)
        RotateRight = 2,  // held: the view turns right
        Raise = 3,        // held: the camera swings up (it looks down at her)
        Lower = 4,        // held: the camera swings down (it looks up)
        Closer = 5,       // held: zoom in
        Farther = 6,      // held: zoom out
        FirstPerson = 7,  // press only
    }

    // AC's InputAction ids for them (CameraManager::OnAction's jump table, 0x33-0x3E).
    private static int AcActionOf(Action a) => a switch
    {
        Action.Home => 0x39,
        Action.RotateLeft => RotateLeftId,
        Action.RotateRight => RotateRightId,
        Action.Raise => 0x37,
        Action.Lower => 0x38,
        Action.Closer => 0x33,
        Action.Farther => 0x34,
        Action.FirstPerson => 0x3A,
        _ => 0,
    };
    // 0x35 calls Rotate(1, ...), 0x36 Rotate(0, ...). On the test client (2026-11-24) an 80 ms 0x35 slid the houses
    // ahead of her to the left of the picture - the view turned right - and 0x36 turned it left.
    private const int RotateLeftId = 0x36, RotateRightId = 0x35;

    private const int OnActionFallbackVa = 0x00456060;
    // CameraManager::OnAction: esi = this, edi = the event; [edi] - 0x33 against 0x0B, the jump table, then the
    // CameraSet at [esi + 0xB8].
    private static readonly byte?[] OnActionPattern =
    [
        0x56, 0x57, 0x8B, 0x7C, 0x24, 0x0C, 0x8B, 0x17, 0x8D, 0x42, 0xCD, 0x83, 0xF8, 0x0B, 0x8B, 0xF1,
        0x0F, 0x87, null, null, null, null, 0xFF, 0x24, 0x85, null, null, null, null,
        0x8B, 0x8E, 0xB8, 0x00, 0x00, 0x00
    ];

    private const int SmartBoxCameraManagerOffset = 0xA0;
    private const int ManagerCameraSetOffset = 0xBC;
    private const int ManagerHandlerOffset = 0x04;
    private const int SetManagerOffset = 0x08, SetSmartBoxOffset = 0x0C;
    private const int EventActionOffset = 0x00, EventPressedOffset = 0x20, EventSize = 0x40;

    private static IntPtr _onAction;
    private static bool _initialized;
    private static string _status = "Not initialized.";
    public static string Status => _status;
    public static bool Available => _onAction != IntPtr.Zero;

    private readonly record struct Request(Action Action, bool Pressed, long QueuedMs);
    private static readonly ConcurrentQueue<Request> Queue = new();
    private const int MaxQueued = 32;
    private const long GiveUpMs = 2000;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
        {
            _status = "acclient.exe not available.";
            return;
        }
        var r = HookResolver.Resolve(text, "AcCamera.CameraManager::OnAction", OnActionPattern, OnActionFallbackVa);
        _onAction = r.Success && r.Source == HookResolver.ResolveSource.PatternScan ? r.Address : IntPtr.Zero;
        _status = _onAction != IntPtr.Zero ? $"CameraManager::OnAction at 0x{_onAction.ToInt64():X8}." : "CameraManager::OnAction not found.";
        RynthLog.Compat("AcCamera: " + _status);
    }

    /// <summary>
    /// Queues a camera key: <paramref name="pressed"/> true presses it (a held one goes on until its release),
    /// false lets go. Home and FirstPerson act on the press. False when it can't be done here (not found, a
    /// bad action, a full queue). Any thread.
    /// </summary>
    public static bool Enqueue(Action action, bool pressed)
    {
        Initialize();
        if (!Available || AcActionOf(action) == 0) return false;
        if (Queue.Count >= MaxQueued) return false;
        Queue.Enqueue(new Request(action, pressed, Environment.TickCount64));
        return true;
    }

    /// <summary>The CameraSet's manager, checked: its CameraSet points back at it and at the SmartBox.</summary>
    private static IntPtr Manager()
    {
        if (!SmartBoxLocator.TryGetSmartBox(out IntPtr sb, out _, out _) || sb == IntPtr.Zero) return IntPtr.Zero;
        if (!ClientObjectHooks.IsReadableSpan(sb + SmartBoxCameraManagerOffset, 4)) return IntPtr.Zero;
        IntPtr mgr = *(IntPtr*)(sb + SmartBoxCameraManagerOffset);
        if (mgr == IntPtr.Zero || !ClientObjectHooks.IsReadableSpan(mgr, ManagerCameraSetOffset + 4)) return IntPtr.Zero;
        IntPtr set = *(IntPtr*)(mgr + ManagerCameraSetOffset);
        if (set == IntPtr.Zero || !ClientObjectHooks.IsReadableSpan(set, SetSmartBoxOffset + 4)) return IntPtr.Zero;
        if (*(IntPtr*)(set + SetManagerOffset) != mgr || *(IntPtr*)(set + SetSmartBoxOffset) != sb) return IntPtr.Zero;
        return mgr;
    }

    /// <summary>Game thread, before AC's tick. Alloc-free when nothing is queued; never throws.</summary>
    public static void Drain()
    {
        if (Queue.IsEmpty) return;
        long now = Environment.TickCount64;
        IntPtr mgr = IntPtr.Zero;
        bool looked = false;
        byte* ev = stackalloc byte[EventSize];
        while (Queue.TryDequeue(out Request r))
        {
            // A press that waited too long is dropped; a release never is (it must not leave the camera turning).
            if (r.Pressed && now - r.QueuedMs > GiveUpMs) continue;
            if (!looked) { mgr = Manager(); looked = true; }
            if (mgr == IntPtr.Zero) continue;   // out of the world: there is no camera to move
            try
            {
                new Span<byte>(ev, EventSize).Clear();
                *(int*)(ev + EventActionOffset) = AcActionOf(r.Action);
                ev[EventPressedOffset] = (byte)(r.Pressed ? 1 : 0);
                ((delegate* unmanaged[Thiscall]<IntPtr, byte*, byte>)_onAction)(mgr + ManagerHandlerOffset, ev);
            }
            catch (Exception ex)
            {
                RynthLog.Compat($"AcCamera: {r.Action} {(r.Pressed ? "press" : "release")} threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Logout / relog: nothing queued for the old character.</summary>
    public static void Reset()
    {
        while (Queue.TryDequeue(out _)) { }
    }
}

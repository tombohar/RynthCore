// ============================================================================
//  RynthCore.Engine - ImGui/ItemDragBridge.cs
//  Carries an item drag into the MAIN ImGui context when it didn't start there,
//  so drop targets in the client (RynthAi's Mini Remote slots) can take it:
//
//    • A popped-out Inventory: the pop-out has its own ImGui context and holds
//      mouse capture for the whole drag, so the main context never sees it.
//      The pop-out face reports the dragged item (NotePopOutDrag); the main
//      frame republishes it as an extern payload. The main context sees no
//      button press, so the payload uses PayloadAutoExpire: it is delivered to
//      the hovered target on the first frame it is no longer submitted.
//    • AC's own inventory: a press that began on the game belongs to the game,
//      and ImGui ignores hovering for it. DragDropHooks reports the item AC
//      starts dragging (OnNativeDragStart); a carried one is republished the
//      same way, under PayloadType, and delivered when ImGui sees the button go
//      up. AC does not select the dragged item, so without that hook the bridge
//      can only guess from the selection: the guess goes out under
//      NativePayloadType, which targets treat as unreliable.
//
//  A native drag released over an ImGui window would otherwise complete in AC
//  as a drop on the 3D view (the item lands on the ground). TryRedirectNativeDrop
//  moves that button-up back to where the drag began, which AC treats as
//  putting the item back.
//
//  Threads: AC's main thread only (game WndProc, EndScene, pop-out frames).
// ============================================================================

using System;
using System.Diagnostics;
using ImGuiNET;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.ImGuiBackend;

internal static unsafe class ItemDragBridge
{
    /// <summary>Payload type for one item (uint object id). Must match RynthAi's MiniRemoteHud.ItemPayloadType.</summary>
    public const string PayloadType = "RYNTH_INV_ITEM";

    /// <summary>
    /// Payload type for a game drag whose item was guessed from the selection (DragDropHooks not
    /// installed). Any game left-drag with a carried item selected looks like this (moving an AC
    /// panel too), so targets should take it only where a wrong item does no harm (RynthAi: empty
    /// slots). Must match MiniRemoteHud.GameItemPayloadType.
    /// </summary>
    public const string NativePayloadType = "RYNTH_GAME_ITEM";

    private const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    private const int MK_LBUTTON = 0x0001, MK_OTHER = 0x0002 | 0x0010 | 0x0020 | 0x0040;
    /// <summary>Movement (px, squared) before a game press counts as a drag.</summary>
    private const int DragThresholdSq = 5 * 5;
    /// <summary>AC may select the pressed item a few messages after the press: keep re-reading it this long.</summary>
    private static readonly long SelectionSettleTicks = Stopwatch.Frequency / 2;
    /// <summary>A pop-out drag with no report for this long is over (the pop-out closed mid-drag).</summary>
    private static readonly long PopOutStaleTicks = Stopwatch.Frequency;

    // Popped-out Inventory drag.
    private static uint _popOutItem;
    private static long _popOutSeenAt;

    // Native (AC inventory) drag.
    private static bool _pressPending;
    private static IntPtr _pressLParam;
    private static int _pressX, _pressY;
    private static long _pressAt;
    private static uint _nativeItem;
    /// <summary>_nativeItem came from AC's drag start (DragDropHooks), not from the selection.</summary>
    private static bool _nativeFromHook;
    /// <summary>The left button is down on a press that belongs to the game.</summary>
    private static bool _gamePressHeld;
    private static bool _nativeReleased;
    /// <summary>The cursor was over an ImGui window in the last main frame (only tracked during a native drag).</summary>
    private static bool _overImGui;

    // ── Popped-out Inventory ────────────────────────────────────────────────

    /// <summary>A popped-out Inventory is dragging <paramref name="itemId"/> (call every pop-out frame of the drag).</summary>
    public static void NotePopOutDrag(uint itemId)
    {
        if (itemId == 0) return;
        if (_popOutItem != itemId)
            RynthLog.UI($"ItemDragBridge: pop-out drag 0x{itemId:X8} published to the client.");
        _popOutItem = itemId;
        _popOutSeenAt = Stopwatch.GetTimestamp();
    }

    /// <summary>The popped-out Inventory's drag ended (dropped or cancelled).</summary>
    public static void EndPopOutDrag() => _popOutItem = 0;

    // ── Native drag (game WndProc) ──────────────────────────────────────────

    /// <summary>
    /// Every game-window mouse message, after the backend decided who owns the held buttons.
    /// <paramref name="gameOwned"/>: the current press began on the game, not on ImGui.
    /// </summary>
    public static void OnGameMouse(uint msg, IntPtr wParam, IntPtr lParam, bool gameOwned)
    {
        int keys = (int)((long)wParam & 0xFFFF);
        switch (msg)
        {
            case WM_LBUTTONDOWN:
                // Only a plain left press that belongs to the game can be an inventory drag.
                _pressPending = gameOwned && (keys & MK_OTHER) == 0;
                _pressLParam = lParam;
                _pressX = LoWord(lParam);
                _pressY = HiWord(lParam);
                _pressAt = Stopwatch.GetTimestamp();
                _gamePressHeld = _pressPending;
                if (_pressPending) { _nativeItem = 0; _nativeFromHook = false; _nativeReleased = false; }
                break;

            case WM_MOUSEMOVE:
                if ((keys & MK_LBUTTON) == 0)
                {
                    // A release we never saw (outside the window): finish the drag.
                    _pressPending = false;
                    _gamePressHeld = false;
                    if (_nativeItem != 0) _nativeReleased = true;
                    break;
                }
                if (_pressPending)
                {
                    int dx = LoWord(lParam) - _pressX, dy = HiWord(lParam) - _pressY;
                    if (dx * dx + dy * dy >= DragThresholdSq)
                    {
                        _pressPending = false;
                        // With the drag-start hook the real item arrives through OnNativeDragStart.
                        if (_nativeItem == 0 && !DragDropHooks.IsInstalled)
                        {
                            _nativeItem = CarriedSelection();
                            if (_nativeItem != 0)
                                RynthLog.UI($"ItemDragBridge: native inventory drag 0x{_nativeItem:X8} (from the selection) published to ImGui.");
                        }
                    }
                }
                else if (_nativeItem != 0 && !_nativeFromHook && !_nativeReleased && Stopwatch.GetTimestamp() - _pressAt < SelectionSettleTicks)
                {
                    // The selection can trail the press by a few messages: follow it while it settles.
                    uint sel = CarriedSelection();
                    if (sel != 0) _nativeItem = sel;
                }
                break;

            case WM_LBUTTONUP:
                _pressPending = false;
                _gamePressHeld = false;
                if (_nativeItem != 0) _nativeReleased = true;
                break;
        }
    }

    /// <summary>
    /// AC started dragging <paramref name="itemId"/> (DragDropHooks, AC's main thread). Taken while
    /// a game-owned left press is held and only for items the player carries.
    /// </summary>
    public static void OnNativeDragStart(uint itemId)
    {
        if (!_gamePressHeld || itemId == 0 || !IsCarried(itemId)) return;
        _pressPending = false;
        _nativeItem = itemId;
        _nativeFromHook = true;
        _nativeReleased = false;
        RynthLog.UI($"ItemDragBridge: native inventory drag 0x{itemId:X8} published to ImGui.");
    }

    /// <summary>
    /// A native item drag is being released over an ImGui window: returns the press position
    /// to send AC instead, so AC puts the item back rather than dropping it on the ground.
    /// </summary>
    public static bool TryRedirectNativeDrop(uint msg, out IntPtr pressLParam)
    {
        pressLParam = _pressLParam;
        if (msg != WM_LBUTTONUP || _nativeItem == 0 || !_overImGui) return false;
        RynthLog.UI($"ItemDragBridge: native drag 0x{_nativeItem:X8} released over an ImGui window; AC gets the drop back at its start ({_pressX},{_pressY}).");
        return true;
    }

    // ── Main frame (after ImGui.NewFrame, before any window) ────────────────

    /// <summary>Republishes a pop-out or native drag in the main context for this frame.</summary>
    public static void SubmitMainFrame(ImGuiIOPtr io)
    {
        uint id = 0;
        string type = PayloadType;
        if (_popOutItem != 0)
        {
            if (Stopwatch.GetTimestamp() - _popOutSeenAt > PopOutStaleTicks) _popOutItem = 0;
            else id = _popOutItem;
        }

        if (id == 0 && _nativeItem != 0)
        {
            type = _nativeFromHook ? PayloadType : NativePayloadType;
            // After the button-up, stop once ImGui itself sees the button up: that frame
            // delivers the drop (the payload outlives its last submission by one frame).
            if (_nativeReleased && !io.MouseDown[0])
            {
                _nativeItem = 0;
                _nativeFromHook = false;
                _nativeReleased = false;
                _overImGui = false;
                return;
            }
            id = _nativeItem;
            _overImGui = ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        }

        if (id == 0) return;
        const ImGuiDragDropFlags flags = ImGuiDragDropFlags.SourceExtern | ImGuiDragDropFlags.SourceNoPreviewTooltip
                                         | ImGuiDragDropFlags.PayloadAutoExpire;
        if (ImGuiNET.ImGui.BeginDragDropSource(flags))
        {
            ImGuiNET.ImGui.SetDragDropPayload(type, (IntPtr)(&id), sizeof(uint));
            ImGuiNET.ImGui.EndDragDropSource();
        }
    }

    /// <summary>Forget every drag (focus lost, logout, engine shutdown).</summary>
    public static void Reset()
    {
        _popOutItem = 0;
        _pressPending = false;
        _gamePressHeld = false;
        _nativeItem = 0;
        _nativeFromHook = false;
        _nativeReleased = false;
        _overImGui = false;
    }

    /// <summary>The game's selected object when the player carries it, else 0.</summary>
    private static uint CarriedSelection()
    {
        uint id = ClientHelperHooks.GetSelectedItemId();
        return IsCarried(id) ? id : 0;
    }

    /// <summary><paramref name="id"/> is in the player's pack, a side pack, or worn.</summary>
    private static bool IsCarried(uint id)
    {
        if (id == 0 || !ClientHelperHooks.HasGetPlayerId) return false;
        uint player = ClientHelperHooks.GetPlayerId();
        if (player == 0 || id == player) return false;
        if (!ClientObjectHooks.TryGetObjectOwnershipInfo(id, out uint container, out uint wielder, out _)) return false;
        if (wielder == player || container == player) return true;
        // Side pack in the main pack.
        return container != 0 && ClientObjectHooks.TryGetObjectOwnershipInfo(container, out uint outer, out _, out _) && outer == player;
    }

    private static int LoWord(IntPtr l) => unchecked((short)((long)l & 0xFFFF));
    private static int HiWord(IntPtr l) => unchecked((short)(((long)l >> 16) & 0xFFFF));
}

// ============================================================================
//  RynthCore.Engine - Compatibility/AcMouseOver.cs
//
//  "Is the cursor on the 3D world, or on one of AC's own windows?" - AC's own
//  answer, read out of its UI manager. The world overlays (RynthVision's
//  nameplates) draw under AC's windows (ImGui/UnderUiLayer); a plate behind
//  the inventory must not take the click meant for the inventory (2026-10-08
//  report: "it's behind the UI, but I can't select anything that has the hud
//  behind it"). MonsterHud asks here before a plate keeps a click from AC.
//
//  Evidence (AC client decompile, C:\Projects\GhidraTools\decompiled):
//    UIElementManager::MouseMoveHandler (0x0045E7F0), ::MouseDownEvent
//      (0x0045DC40), ::UpdateMouseOver (0x0045C390) and ::DoMouseUpdate
//      (0x0045CDE0, run from UseTime when +0x240 is set) all do the same:
//      unless +0x2D4 is set (the cursor left the client: MouseLeaveEvent
//      0x0045C380 sets it), MouseOverCriteria::Recurse (0x00459490) walks the
//      tree from the root (+0x9C), topmost child first, and returns the deepest
//      visible element under the point that takes the mouse (its +0x545 byte or
//      +0xA4 bit 6); SwitchMouseOver (0x0045B640) stores it at +0x244.
//      MouseDownEvent then routes the press by it (+0x248, the hover element,
//      gets the capture), so +0x244 is exactly what AC will send a click to.
//    DeletingElement (0x0045E600) clears +0x244 (and sets +0x240) when that
//      element goes, so on AC's main thread the pointer is live or zero.
//    The 3D view is UIElement_SmartBoxWrapper: GetUIElementType (0x004E5E80)
//      returns 0x10000030, Register (0x004E60A0) registers that type. Its
//      MouseDown (0x004E6390) is AC's click-in-the-world (FindObject); for a
//      world click to select anything it must be the element under the mouse.
//    UIElement vtable +0x98 = GetUIElementType, +0xA0 = GetParent, +0x2E4 the
//      element id: as verified for UiElementHooks (tools/ui_hooks_check.py).
//  The manager pointer is `this` of UiElementHooks' detours (CheckTooltip runs
//  every frame from UIElementManager::UseTime 0x0045D0B0); the singleton global
//  (0x0083E03C, CreateUIElementManager 0x0045F900) is not used, to ship no fixed
//  address. No manager (tooltip hooks off or not found) = Unknown = fail open.
//
//  In game (2026-10-10 logs) the element under the mouse over open ground is
//  NOT the wrapper but gmSmartBoxUI, element 0x1000049A, type 0x10000014
//  (gmSmartBoxUI::Register 0x004D71E0; gmGamePlayUI's "<SBOX>"). Both types
//  count as the world (AcMouseOverRules.IsWorldType). `/rc acmouse`
//  (or `/rc acmouse watch` for 20 s of changes in chat) names what AC has
//  under the cursor; Identify is the one place that decides.
//
//  Threads: AC's main thread only (EndScene and the UI detours run there);
//  anything else gets Unknown. Every read goes through the memory probe first.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal static unsafe class AcMouseOver
{
    private const int MgrMouseOver = 0x244;     // raw element under the mouse (SwitchMouseOver)
    private const int MgrMouseOutside = 0x2D4;  // byte: cursor left the client (MouseLeaveEvent)
    private const int ElementIdOffset = 0x2E4;
    private const int VtGetUIElementType = 0x98;
    private const int VtGetParent = 0xA0;

    /// <summary>One read of AC's mouse-over state (diagnostics carry the ids).</summary>
    public struct Sample
    {
        public AcMouseTarget Target;
        public IntPtr Element;
        public uint ElementId;
        public bool TypeKnown;
        public int Type;
    }

    // Main thread only. Element type per vtable: GetUIElementType returns a constant per
    // class, so each class is asked once (113 UIElement classes in the client).
    private static readonly Dictionary<IntPtr, int> TypeByVtable = new();
    private static Sample _last;

    private static long _watchUntil;
    private static IntPtr _watchLastElement = new(-1);
    private static int _watchLines;

    /// <summary>AC's main thread: what AC's UI has under the mouse right now.</summary>
    public static AcMouseTarget Current() => Read().Target;

    /// <summary>AC's main thread: AC's mouse-over state, with the element's id and type.</summary>
    public static Sample Read()
    {
        var s = new Sample { Target = AcMouseTarget.Unknown };
        try
        {
            if (!MainThreadGuard.IsOnMainThread())
                return s;
            IntPtr mgr = UiElementHooks.Manager;
            bool readable = mgr != IntPtr.Zero
                && ClientObjectHooks.IsReadableSpan(mgr + MgrMouseOver, 4)
                && ClientObjectHooks.IsReadableSpan(mgr + MgrMouseOutside, 1);
            if (!readable)
                return _last = s;
            bool outside = *(byte*)(mgr + MgrMouseOutside) != 0;
            s.Element = *(IntPtr*)(mgr + MgrMouseOver);
            if (!outside && s.Element != IntPtr.Zero)
            {
                s.TypeKnown = TryGetType(s.Element, out s.Type);
                if (ClientObjectHooks.IsReadableSpan(s.Element + ElementIdOffset, 4))
                    s.ElementId = *(uint*)(s.Element + ElementIdOffset);
            }
            s.Target = Identify(readable, outside, s.Element, s.TypeKnown, s.Type);
        }
        catch
        {
            s.Target = AcMouseTarget.Unknown;
        }
        return _last = s;
    }

    /// <summary>
    /// The one decision: the 3D view is gmSmartBoxUI (type 0x10000014) or its SmartBox wrapper (0x10000030);
    /// any other element AC reports is one of its windows; nothing readable is Unknown.
    /// </summary>
    private static AcMouseTarget Identify(bool readable, bool outside, IntPtr element, bool typeKnown, int type)
        => AcMouseOverRules.Classify(readable, outside, (uint)element.ToInt32(), typeKnown, type);

    /// <summary>The element's UIElement type (vtable +0x98), asked once per class. Main thread.</summary>
    private static bool TryGetType(IntPtr element, out int type)
    {
        type = 0;
        if (!ClientObjectHooks.TryReadAcObjectVtable(element, out IntPtr vtable))
            return false;
        if (TypeByVtable.TryGetValue(vtable, out type))
            return true;
        if (!ClientObjectHooks.IsReadableSpan(vtable + VtGetUIElementType, 4))
            return false;
        IntPtr fn = *(IntPtr*)(vtable + VtGetUIElementType);
        if (!SmartBoxLocator.IsPointerInModule(fn))
            return false;
        type = ((delegate* unmanaged[Thiscall]<IntPtr, int>)fn)(element);
        if (TypeByVtable.Count < 512)
            TypeByVtable[vtable] = type;
        return true;
    }

    /// <summary>The parent's id and type, for the diagnostics only. Main thread.</summary>
    private static string DescribeParent(IntPtr element)
    {
        if (!ClientObjectHooks.TryReadAcObjectVtable(element, out IntPtr vtable)
            || !ClientObjectHooks.IsReadableSpan(vtable + VtGetParent, 4))
            return "parent ?";
        IntPtr fn = *(IntPtr*)(vtable + VtGetParent);
        if (!SmartBoxLocator.IsPointerInModule(fn))
            return "parent ?";
        IntPtr parent = ((delegate* unmanaged[Thiscall]<IntPtr, IntPtr>)fn)(element);
        if (parent == IntPtr.Zero)
            return "no parent";
        uint id = ClientObjectHooks.IsReadableSpan(parent + ElementIdOffset, 4) ? *(uint*)(parent + ElementIdOffset) : 0;
        return TryGetType(parent, out int t) ? $"parent id 0x{id:X8} type 0x{t:X8}" : $"parent id 0x{id:X8} type ?";
    }

    private static string Format(in Sample s, bool withParent)
    {
        string what = s.Target switch
        {
            AcMouseTarget.World => "the 3D world (plates may take the click)",
            AcMouseTarget.AcWindow => "an AC window (plates leave the click to AC)",
            AcMouseTarget.Nothing => "nothing (plates may take the click)",
            _ => "unknown - AC's UI state not readable (plates may take the click)",
        };
        if (s.Element == IntPtr.Zero)
            return $"AC mouse-over: {what}.";
        string type = s.TypeKnown ? $"0x{s.Type:X8}" : "?";
        string parent = "";
        if (withParent)
        {
            try { parent = ", " + DescribeParent(s.Element); } catch { parent = ", parent ?"; }
        }
        return $"AC mouse-over: {what}; element id 0x{s.ElementId:X8} type {type}{parent}.";
    }

    /// <summary>/rc acmouse: the state now (main thread: read live; elsewhere the last read).</summary>
    public static string Describe()
    {
        bool live = MainThreadGuard.IsOnMainThread();
        Sample s = live ? Read() : _last;
        return Format(s, live) + (UiElementHooks.Manager == IntPtr.Zero ? " (UI manager not seen yet: the tooltip hooks are off or haven't run.)" : "");
    }

    /// <summary>/rc acmouse watch: every change of the element under the mouse, for <paramref name="seconds"/>.</summary>
    public static void Watch(int seconds)
    {
        _watchLastElement = new IntPtr(-1);
        _watchLines = 0;
        Volatile.Write(ref _watchUntil, Stopwatch.GetTimestamp() + Stopwatch.Frequency * Math.Clamp(seconds, 1, 120));
    }

    /// <summary>Once a frame on AC's main thread (EngineFrameController): the watch, when on.</summary>
    public static void Tick()
    {
        long until = Volatile.Read(ref _watchUntil);
        if (until == 0)
            return;
        if (Stopwatch.GetTimestamp() > until || _watchLines >= 60)
        {
            _watchUntil = 0;
            Say("AC mouse-over watch ended.");
            return;
        }
        Sample s = Read();
        if (s.Element == _watchLastElement)
            return;
        _watchLastElement = s.Element;
        _watchLines++;
        Say(Format(s, true));
    }

    private static void Say(string text)
    {
        RynthLog.Compat(text);
        AcMainThreadQueue.EnqueueWriteToChat("[RynthCore] " + text, 1);
    }
}

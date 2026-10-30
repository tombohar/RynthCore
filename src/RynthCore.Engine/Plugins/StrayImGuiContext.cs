// ============================================================================
//  RynthCore.Engine - Plugins/StrayImGuiContext.cs
//
//  A plugin whose ImGui.NET isn't bound to the engine's cimgui (RynthCore.cimgui.dll) calls a second
//  copy, cimgui.dll, with an ImGui of its own and so its own "current context". Plugins set that to the
//  engine's context before drawing, which works in the main frame. A popped-out window draws in a
//  context of its own, and the engine makes it current only in ITS cimgui: the stray copy still names
//  the main context, which is not in a frame then, and the plugin's first call faults in native code.
//  No try/catch stops an access violation there: the client dies (RynthDreamWeave's server card in a
//  popped-out Mini Remote, 2026-10-19).
//
//  So around a plugin call made inside a pop-out, the pop-out's context is made current in the stray
//  copy too, and the copy's own current context is put back afterwards. Nothing happens when no second
//  cimgui is loaded (ManagedPlugins binds managed plugins to the engine's). Render thread only.
//
//  Found and fixed by SilentKelpie (rynth PR #6); re-implemented here.
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Plugins;

internal static unsafe class StrayImGuiContext
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, EntryPoint = "GetProcAddress")]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    // The stray module these belong to (it can load after the first pop-out frame).
    private static IntPtr _module;
    private static delegate* unmanaged[Cdecl]<IntPtr> _get;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _set;

    /// <summary>What <see cref="Enter"/> changed, for <see cref="Restore"/>.</summary>
    public readonly struct Scope
    {
        public readonly bool Entered;
        public readonly IntPtr Previous;
        public Scope(bool entered, IntPtr previous) { Entered = entered; Previous = previous; }
    }

    /// <summary>Makes <paramref name="context"/> current in the stray cimgui, if one is loaded.</summary>
    public static Scope Enter(IntPtr context)
    {
        if (context == IntPtr.Zero || !Resolve()) return default;
        IntPtr previous = _get();
        _set(context);
        return new Scope(true, previous);
    }

    /// <summary>Puts back the stray copy's own current context (zero included: a pop-out's must not linger there).</summary>
    public static void Restore(Scope scope)
    {
        if (!scope.Entered || _set == null) return;
        _set(scope.Previous);
    }

    /// <summary>True with the stray copy's two calls ready: a cimgui.dll loaded that isn't the engine's.</summary>
    private static bool Resolve()
    {
        IntPtr module = GetModuleHandleW("cimgui.dll");
        if (module == IntPtr.Zero || module == EntryPoint.ImGuiNativeHandle) return false;
        if (module == _module && _get != null && _set != null) return true;
        IntPtr get = GetProcAddress(module, "igGetCurrentContext");
        IntPtr set = GetProcAddress(module, "igSetCurrentContext");
        if (get == IntPtr.Zero || set == IntPtr.Zero) return false;
        _get = (delegate* unmanaged[Cdecl]<IntPtr>)get;
        _set = (delegate* unmanaged[Cdecl]<IntPtr, void>)set;
        _module = module;
        RynthLog.Plugin($"PluginManager: a second cimgui.dll is loaded (0x{module:X8}); popped-out plugin surfaces get the pop-out's context in it too.");
        return true;
    }
}

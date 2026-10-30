// ============================================================================
//  RynthCore.Engine - Hooking/LoaderServices.cs
//
//  The native loader (native/Loader/Services.c, loader 2.0+) owns the permanent native
//  entry points the engine used to hand out itself: the game window's subclass proc and
//  the plugin API table's function pointers. With them, nothing native ever points into
//  an engine generation after its shutdown, so the CoreCLR host can unload it
//  (docs/UNLOADABLE_ENGINE_PLAN.md). Under an older loader (or none) Available is false
//  and callers keep their old behaviour.
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Hooking;

internal static unsafe class LoaderServices
{
    private static readonly delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int> s_subclassInstall;
    private static readonly delegate* unmanaged[Stdcall]<IntPtr, int> s_subclassRelease;
    private static readonly delegate* unmanaged[Stdcall]<int, IntPtr, IntPtr> s_apiStub;

    static LoaderServices()
    {
        IntPtr loader = GetModuleHandleW("RynthCore.Loader.dll");
        if (loader == IntPtr.Zero || GetProcAddress(loader, "RcServices_Version") == IntPtr.Zero)
            return;
        s_subclassInstall = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)GetProcAddress(loader, "RcSubclass_Install");
        s_subclassRelease = (delegate* unmanaged[Stdcall]<IntPtr, int>)GetProcAddress(loader, "RcSubclass_Release");
        s_apiStub = (delegate* unmanaged[Stdcall]<int, IntPtr, IntPtr>)GetProcAddress(loader, "RcApiStub");
    }

    public static bool Available => s_subclassInstall != null && s_subclassRelease != null && s_apiStub != null;

    /// <summary>
    /// Routes <paramref name="hwnd"/>'s messages to <paramref name="handler"/> through the
    /// loader's permanent subclass. <paramref name="previous"/> is the proc to forward to.
    /// </summary>
    public static bool SubclassInstall(IntPtr hwnd, IntPtr handler, out IntPtr previous)
    {
        IntPtr prev = IntPtr.Zero;
        bool ok = s_subclassInstall(hwnd, handler, &prev) != 0;
        previous = prev;
        return ok;
    }

    /// <summary>The loader's subclass passes everything straight to the previous proc again.</summary>
    public static void SubclassRelease(IntPtr hwnd) => s_subclassRelease(hwnd);

    /// <summary>A permanent stub for plugin API slot <paramref name="index"/> that calls <paramref name="fn"/>.</summary>
    public static IntPtr ApiStub(int index, IntPtr fn) => s_apiStub(index, fn);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
}

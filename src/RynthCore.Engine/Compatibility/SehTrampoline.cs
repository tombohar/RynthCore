// ============================================================================
//  RynthCore.Engine - SehTrampoline.cs
//  Managed P/Invoke bridge to RynthCore.SehTrampoline.dll.
//
//  NativeAOT managed try/catch cannot catch access violations (hardware
//  exceptions / corrupted-state exceptions on .NET 5+).  When AC's native
//  code AVs while we call an AC API during object teardown, the managed
//  catch { } is a no-op and the process dies.
//
//  The trampoline DLL provides __try/__except wrappers compiled with MSVC.
//  Each export wraps exactly one native call.  If the call AVs, the SEH
//  handler catches it, sets the out-parameter to a safe default, and
//  returns 0.  Return 1 means the call completed normally.
//
//  This is per-callsite SEH, NOT process-wide (unlike VEH/SUEF which are
//  proven fatal in NativeAOT-injected acclient — see CrashLogger.cs).
//
//  Usage pattern at each call site:
//    if (SehTrampoline.IsAvailable) {
//        IntPtr fnPtr = Marshal.GetFunctionPointerForDelegate(_delegate!);
//        T result = SehTrampoline.SomeWrapper(fnPtr, args, out bool avCaught);
//        if (avCaught) { log; return safeDefault; }
//        // use result normally
//    } else {
//        // direct delegate call (same as before this fix)
//    }
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

internal static class SehTrampoline
{
    private static volatile bool _available;

    /// <summary>Called by EntryPoint after RynthCore.SehTrampoline.dll loads successfully.</summary>
    public static void MarkAvailable() => _available = true;

    /// <summary>
    /// True when the trampoline DLL is loaded.  Call sites should check this
    /// and fall back to direct delegate calls when false.
    /// </summary>
    public static bool IsAvailable => _available;

    // ── P/Invoke declarations ──────────────────────────────────────────────

    [DllImport("RynthCore.SehTrampoline.dll", EntryPoint = "SEH_CdeclPtrUint",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int _CdeclPtrUint(IntPtr fn, uint arg, void** outResult);

    [DllImport("RynthCore.SehTrampoline.dll", EntryPoint = "SEH_CdeclPtrVoid",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int _CdeclPtrVoid(IntPtr fn, void** outResult);

    [DllImport("RynthCore.SehTrampoline.dll", EntryPoint = "SEH_CdeclVoidUintByte",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern int _CdeclVoidUintByte(IntPtr fn, uint arg1, byte arg2);

    [DllImport("RynthCore.SehTrampoline.dll", EntryPoint = "SEH_ThiscallByteUint",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int _ThiscallByteUint(IntPtr fn, IntPtr thisPtr, uint arg, byte* outResult);

    [DllImport("RynthCore.SehTrampoline.dll", EntryPoint = "SEH_ThiscallUintNoArg",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int _ThiscallUintNoArg(IntPtr fn, IntPtr thisPtr, uint* outResult);

    // ── Typed call-through wrappers ────────────────────────────────────────
    // Each returns the native result; sets avCaught=true if AV was intercepted.
    // Callers MUST check IsAvailable before calling these.

    /// <summary>void* __cdecl fn(uint)  — e.g. GetWeenieObject(objectId)</summary>
    public static IntPtr CdeclPtrUint(IntPtr fn, uint arg, out bool avCaught)
    {
        unsafe
        {
            void* result;
            avCaught = _CdeclPtrUint(fn, arg, &result) == 0;
            return (IntPtr)result;
        }
    }

    /// <summary>void* __cdecl fn()  — e.g. GetCombatSystem()</summary>
    public static IntPtr CdeclPtrVoid(IntPtr fn, out bool avCaught)
    {
        unsafe
        {
            void* result;
            avCaught = _CdeclPtrVoid(fn, &result) == 0;
            return (IntPtr)result;
        }
    }

    /// <summary>
    /// void __cdecl fn(uint, byte)  — e.g. CastSpell(spellId, targetIsSelected).
    /// Returns false if AV was caught.
    /// </summary>
    public static bool CdeclVoidUintByte(IntPtr fn, uint arg1, byte arg2)
        => _CdeclVoidUintByte(fn, arg1, arg2) != 0;

    /// <summary>byte __thiscall fn(uint)  — e.g. ObjectIsAttackable(objectId)</summary>
    public static byte ThiscallByteUint(IntPtr fn, IntPtr thisPtr, uint arg, out bool avCaught)
    {
        unsafe
        {
            byte result;
            avCaught = _ThiscallByteUint(fn, thisPtr, arg, &result) == 0;
            return result;
        }
    }

    /// <summary>uint __thiscall fn()  — e.g. InqType()</summary>
    public static uint ThiscallUintNoArg(IntPtr fn, IntPtr thisPtr, out bool avCaught)
    {
        unsafe
        {
            uint result;
            avCaught = _ThiscallUintNoArg(fn, thisPtr, &result) == 0;
            return result;
        }
    }
}

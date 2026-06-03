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
    /// Installs the native VEH crash logger inside the trampoline DLL. PURE NATIVE —
    /// no managed callback / reverse-P/Invoke transition — so unlike the removed
    /// managed CrashLogger VEH it cannot itself re-trigger a NativeAOT fail-fast on an
    /// AC-owned thread. Writes the faulting 32-bit context + a module-resolved stack
    /// sweep to <paramref name="logPath"/> for the 0xC0000005 (AV/CSE) and 0xC0000602
    /// (RaiseFailFast) classes that bypass managed handlers. Observes only.
    /// </summary>
    public static void InstallCrashLogger(string logPath)
    {
        try { _InstallCrashLogger(logPath); }
        catch (Exception ex) { RynthLog.Compat($"SehTrampoline: InstallCrashLogger failed - {ex.GetType().Name}: {ex.Message}"); }
    }

    // ── Tag-parser guard (native detour) ───────────────────────────────────
    /// <summary>Address of the native tag-parser null-guard detour (RC_TagParserGuard),
    /// for the engine to pass to MinHook as the detour target.</summary>
    public static IntPtr GetTagParserGuardAddress() => _GetTagParserGuardAddress();

    /// <summary>Hands the native guard the MinHook trampoline (the original
    /// FUN_0067D3C0). MUST be called BEFORE the hook is enabled.</summary>
    public static void SetTagParserOriginal(IntPtr original) => _SetTagParserOriginal(original);

    /// <summary>Count of null-context parse runs the native guard has dropped (diag).</summary>
    public static uint TagParserSkipCount { get { try { return _TagParserSkipCount(); } catch { return 0; } } }

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

    [DllImport("RynthCore.SehTrampoline.dll", EntryPoint = "SEH_FreeHandsCast",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern int _FreeHandsCast(IntPtr fn, IntPtr thisPtr, uint spellId, uint targetId);

    [DllImport("RynthCore.SehTrampoline.dll", EntryPoint = "SEH_ThiscallByteUint",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int _ThiscallByteUint(IntPtr fn, IntPtr thisPtr, uint arg, byte* outResult);

    [DllImport("RynthCore.SehTrampoline.dll", EntryPoint = "SEH_ThiscallUintNoArg",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int _ThiscallUintNoArg(IntPtr fn, IntPtr thisPtr, uint* outResult);

    [DllImport("RynthCore.SehTrampoline.dll", EntryPoint = "RC_InstallCrashLogger",
               CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern void _InstallCrashLogger([MarshalAs(UnmanagedType.LPWStr)] string logPath);

    [DllImport("RynthCore.SehTrampoline.dll", EntryPoint = "RC_GetTagParserGuardAddress",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr _GetTagParserGuardAddress();

    [DllImport("RynthCore.SehTrampoline.dll", EntryPoint = "RC_SetTagParserOriginal",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern void _SetTagParserOriginal(IntPtr original);

    [DllImport("RynthCore.SehTrampoline.dll", EntryPoint = "RC_TagParserSkipCount",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern uint _TagParserSkipCount();

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

    /// <summary>
    /// void __thiscall fn(this, uint spellId, uint targetId) — ClientMagicSystem::
    /// FreeHandsAndCastSpell. Explicit-target cast (no global selection / UI state).
    /// 'this' is the GetMagicSystem() singleton. Returns false if an AV was caught.
    /// </summary>
    public static bool FreeHandsCast(IntPtr fn, IntPtr thisPtr, uint spellId, uint targetId)
        => _FreeHandsCast(fn, thisPtr, spellId, targetId) != 0;

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

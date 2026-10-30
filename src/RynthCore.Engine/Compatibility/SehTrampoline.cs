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
using System.Threading;

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
    /// sweep to <paramref name="logPath"/>. v6 (PR #8): each faulting address once, repeats
    /// at power-of-two counts or after 30 s, .NET/CoreCLR noise not charged to the cap, the
    /// unhandled-exception entry always; a 16-record ring of the rest is flushed on a crash
    /// exit (RC_FlushCrashLog), from the last-chance filter, and at process exit (last 30 s).
    /// AVs caught by the SEH_* wrappers only go to that ring. Observes only.
    /// Caveat: "pure native" held only while nothing it calls had a managed detour. Before v5
    /// it called CreateFileW from the handlers, and DatFileShareHooks detours CreateFileW in
    /// managed code, so older DLLs are not installed while that hook is on (see below).
    /// </summary>
    public static void InstallCrashLogger(string logPath)
    {
        try
        {
            // v2+ logs fatal crashes only (unhandled-exception filter + NativeAOT-module AVs);
            // v1 (no version export) logged every first-chance AV.
            uint version = NativeVersion;
            if (version < SafeCrashLoggerVersion && DatFileShareHooks.IsInstalled)
            {
                // v1-v4 open native-crash.log with CreateFileW from inside the VEH, which runs
                // first, on the faulting thread. DatFileShareHooks detours CreateFileW with a
                // managed delegate, so for a fault in managed code (any NullReferenceException:
                // the thread is still in cooperative mode) that is a reverse P/Invoke from
                // cooperative mode, and CoreCLR ends the process: ReversePInvokeBadTransition,
                // event 1023 "internal error in the .NET Runtime at IP coreclr.dll+0x256E19",
                // exit 0x80131506. Seven client deaths 2026-09-29..10-12 were this. v5 opens the
                // file here instead (a normal P/Invoke), so the handlers never call CreateFileW.
                RynthLog.Warn($"SehTrampoline: native crash logger NOT installed - the loaded RynthCore.SehTrampoline.dll is v{version}, " +
                    $"whose logger calls CreateFileW inside exception handling; with the DAT share hook's managed CreateFileW detour " +
                    $"that turns any NullReferenceException into a fatal CLR error. Needs v{SafeCrashLoggerVersion}+ " +
                    "(rebuild native\\SehTrampoline, then restart the client). A logger an earlier engine generation installed in " +
                    "this client stays active until the client restarts.");
                return;
            }

            _InstallCrashLogger(logPath);
            string mode = version >= 6 ? "once per address + repeats, flush ring"
                        : version >= 2 ? "fatal-only" : "logs every first-chance AV";
            RynthLog.Compat($"SehTrampoline: native crash logger installed (v{version}, {mode}) -> {logPath}");
        }
        catch (Exception ex) { RynthLog.Compat($"SehTrampoline: InstallCrashLogger failed - {ex.GetType().Name}: {ex.Message}"); }
        ResolveFlushCrashLog();
    }

    // RC_FlushCrashLog, resolved once here (0 = a DLL older than the export). Called through a
    // raw unmanaged function pointer, so the crash path binds and compiles nothing: the call
    // is primed below with code 0, which the native side ignores.
    private static nint _flushCrashLog;

    private static unsafe void ResolveFlushCrashLog()
    {
        try
        {
            nint module = GetModuleHandleW("RynthCore.SehTrampoline.dll");
            nint fn = module == 0 ? 0 : GetProcAddress(module, "RC_FlushCrashLog");
            if (fn == 0)
            {
                RynthLog.Compat("SehTrampoline: RC_FlushCrashLog not exported (older DLL); crash exits will not flush the crash log's recent records.");
                return;
            }
            ((delegate* unmanaged[Cdecl]<uint, void>)fn)(0);   // prime: code 0 is a no-op
            _flushCrashLog = fn;
        }
        catch (Exception ex) { RynthLog.Compat($"SehTrampoline: RC_FlushCrashLog unavailable - {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>
    /// A crash exit is under way (ProcessExitHooks): write the crash logger's recent first-chance
    /// records that were not written one by one, such as the fatal repeat of an address seen
    /// before. Native and allocation-free; writes each record once, so a second call (one exit
    /// path calling another) writes nothing.
    /// </summary>
    public static unsafe void FlushCrashLog(uint exitCode)
    {
        nint fn = _flushCrashLog;
        if (fn == 0 || exitCode == 0) return;
        ((delegate* unmanaged[Cdecl]<uint, void>)fn)(exitCode);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern nint GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, EntryPoint = "GetProcAddress", ExactSpelling = true)]
    private static extern nint GetProcAddress(nint module, string name);

    private static int _moduleRefreshUnavailable;

    /// <summary>
    /// Rebuilds the crash logger's module cache (module names at exception time come from it,
    /// so the handler never calls the loader). Heartbeat thread, every few seconds. A DLL
    /// older than the export is tolerated: the first failure stops the calls.
    /// </summary>
    public static void RefreshCrashLoggerModules()
    {
        if (!_available || Volatile.Read(ref _moduleRefreshUnavailable) != 0) return;
        try { _RefreshCrashLoggerModules(); }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _moduleRefreshUnavailable, 1);
            RynthLog.Compat($"SehTrampoline: crash-logger module refresh unavailable - {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// First native version whose crash logger opens its file at install time and never calls
    /// CreateFileW while an exception is being dispatched (see SehTrampoline.c, cl_append_file).
    /// </summary>
    public const uint SafeCrashLoggerVersion = 5;

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

    private static int _nativeVersion = -1;

    /// <summary>
    /// Version of the loaded native DLL (0 when unavailable, 1 when it predates the version
    /// export). A running client keeps the DLL it started with, so a newer engine can meet
    /// an older trampoline; gate newer exports on this.
    /// </summary>
    public static uint NativeVersion
    {
        get
        {
            int cached = System.Threading.Volatile.Read(ref _nativeVersion);
            if (cached >= 0)
                return (uint)cached;
            uint version = 0;
            if (_available)
            {
                try { version = _SehTrampolineVersion(); }
                catch (EntryPointNotFoundException) { version = 1; }
                catch { version = 0; }
            }
            System.Threading.Volatile.Write(ref _nativeVersion, (int)version);
            return version;
        }
    }

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

    [DllImport("RynthCore.SehTrampoline.dll", EntryPoint = "SEH_ThiscallIntUintPtr",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int _ThiscallIntUintPtr(IntPtr fn, IntPtr thisPtr, uint arg1, void* arg2, int* outResult);

    [DllImport("RynthCore.SehTrampoline.dll", EntryPoint = "SEH_GameTickThiscall",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int _GameTickThiscall(IntPtr fn, IntPtr thisPtr, int* outResult,
        uint* outExAddr, uint* outDataAddr, byte* module, int moduleCap);

    [DllImport("RynthCore.SehTrampoline.dll", EntryPoint = "RC_InstallCrashLogger",
               CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern void _InstallCrashLogger([MarshalAs(UnmanagedType.LPWStr)] string logPath);

    [DllImport("RynthCore.SehTrampoline.dll", EntryPoint = "RC_SehTrampolineVersion",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern uint _SehTrampolineVersion();

    [DllImport("RynthCore.SehTrampoline.dll", EntryPoint = "RC_RefreshCrashLoggerModules",
               CallingConvention = CallingConvention.Cdecl)]
    private static extern void _RefreshCrashLoggerModules();

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

    /// <summary>
    /// int __thiscall fn(this, uint arg1, void* outStruct) — e.g.
    /// InqAttribute2nd(stype, &SecondaryAttribute). Returns the native int result
    /// (nonzero = success); avCaught=true (and result 0) if an AV was intercepted.
    /// outStruct points at caller-owned memory the callee fills on success.
    /// </summary>
    public static unsafe int ThiscallIntUintPtr(IntPtr fn, IntPtr thisPtr, uint arg1, void* outStruct, out bool avCaught)
    {
        int result;
        avCaught = _ThiscallIntUintPtr(fn, thisPtr, arg1, outStruct, &result) == 0;
        return result;
    }

    /// <summary>
    /// int __thiscall fn(this) — Client::UseTime. Requires <see cref="NativeVersion"/> &gt;= 4.
    /// On a caught AV: avCaught=true, result 0, and the fault location; <paramref name="exAddr"/>
    /// is relative to <paramref name="module"/> when that is non-empty, else absolute.
    /// The native side also writes the full record to native-crash.log.
    /// </summary>
    public static unsafe int GameTickThiscall(IntPtr fn, IntPtr thisPtr, out bool avCaught,
        out uint exAddr, out uint dataAddr, out string module)
    {
        const int ModuleCap = 64;
        byte* name = stackalloc byte[ModuleCap];
        int result;
        uint ex, data;
        avCaught = _GameTickThiscall(fn, thisPtr, &result, &ex, &data, name, ModuleCap) == 0;
        exAddr = ex;
        dataAddr = data;
        module = avCaught ? (Marshal.PtrToStringAnsi((IntPtr)name) ?? string.Empty) : string.Empty;
        return result;
    }
}

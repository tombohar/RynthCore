using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// MinHook detours on <c>kernel32!ExitProcess</c> and
/// <c>kernel32!TerminateProcess</c> — the two paths every process-shutdown
/// in Windows funnels through (managed FailFast, native AC self-exit on
/// disconnect, runtime AV-induced __fastfail, etc.).
///
/// Purpose: when the AC client dies silently — no popup, no engine VEH
/// banner, no FirstChanceException — neither managed nor SEH machinery
/// catches it. The only chance to see who killed the process is to
/// intercept the kill primitive itself. We capture a native stack
/// backtrace at detour entry, identify the calling module, log it, then
/// forward to the original (process still dies, but we know why).
///
/// Logged information per call:
///   - Function (ExitProcess vs TerminateProcess)
///   - Exit code
///   - Calling thread ID
///   - Top N native return addresses, each annotated with module + RVA
///     so we can pinpoint the exact instruction that decided to kill us.
/// </summary>
internal static class ProcessExitHooks
{
    private const int CapturedFrames = 12;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ExitProcessDelegate(uint exitCode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
    private delegate bool TerminateProcessDelegate(IntPtr hProcess, uint exitCode);

    // ntdll!RtlExitUserProcess(NTSTATUS) — the actual implementation that
    // kernel32!ExitProcess wraps. Modern code (and NativeAOT runtime) can
    // call this directly, bypassing kernel32.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RtlExitUserProcessDelegate(uint exitStatus);

    // ntdll!NtTerminateProcess(HANDLE, NTSTATUS) — the syscall stub. Anything
    // that wants to bypass kernel32!TerminateProcess goes through here.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint NtTerminateProcessDelegate(IntPtr processHandle, uint exitStatus);

    // kernel32!RaiseFailFastException — what __fastfail() / abort() / FailFast
    // route through. Doesn't take an exit code; takes EXCEPTION_RECORD + CONTEXT.
    // We don't need the params, just the call-site capture.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RaiseFailFastExceptionDelegate(IntPtr pExceptionRecord, IntPtr pContext, uint dwFlags);

    // ntdll!RtlFailFast(NTSTATUS) — internally executes `int 0x29` (the
    // __fastfail intrinsic). The kernel handles 0x29 directly and terminates
    // the process WITHOUT re-entering user mode through ExitProcess /
    // TerminateProcess / RaiseFailFastException. Hooking RtlFailFast itself
    // is the only way to capture this path before the kernel takes over.
    // Used by: stack-cookie failures (/GS), heap corruption checks, CRT
    // abort paths on newer toolchains, NTDLL's own internal asserts.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RtlFailFastDelegate(uint code);

    // ntdll!RtlFailFast2(NTSTATUS, EXCEPTION_RECORD*) — newer variant that
    // also passes an exception record. Same semantics: the kernel terminates
    // the process via int 0x29 without revisiting user mode.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RtlFailFast2Delegate(uint code, IntPtr pExceptionRecord);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ushort RtlCaptureStackBackTrace(
        uint FramesToSkip, uint FramesToCapture, IntPtr[] BackTrace, out uint BackTraceHash);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleHandleExW(uint dwFlags, IntPtr lpModuleName, out IntPtr phModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(IntPtr hModule, [Out] char[] lpFilename, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetCurrentThreadId();

    private const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004;
    private const uint GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x00000002;
    private static readonly uint ModuleFromAddrFlags =
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT;

    private static ExitProcessDelegate? _exitDetour;
    private static ExitProcessDelegate? _exitOriginal;
    private static TerminateProcessDelegate? _terminateDetour;
    private static TerminateProcessDelegate? _terminateOriginal;

    private static RtlExitUserProcessDelegate? _rtlExitDetour;
    private static RtlExitUserProcessDelegate? _rtlExitOriginal;
    private static NtTerminateProcessDelegate? _ntTermDetour;
    private static NtTerminateProcessDelegate? _ntTermOriginal;
    private static RaiseFailFastExceptionDelegate? _failFastDetour;
    private static RaiseFailFastExceptionDelegate? _failFastOriginal;
    private static RtlFailFastDelegate? _rtlFailFastDetour;
    private static RtlFailFastDelegate? _rtlFailFastOriginal;
    private static RtlFailFast2Delegate? _rtlFailFast2Detour;
    private static RtlFailFast2Delegate? _rtlFailFast2Original;

    private static int _installed;
    private static int _logged; // ensure we log at most once even if both fire

    // Re-entrancy latch for the fail-fast family (RaiseFailFastException,
    // RtlFailFast, RtlFailFast2). In the NativeAOT-inside-acclient environment
    // a fail-fast does NOT terminate the way it does in a normal process: the
    // runtime/AC exception machinery swallows or continues the fail-fast
    // exception, so kernel32!RaiseFailFastException RETURNS instead of killing
    // the process. Its caller then re-invokes it, hitting our MinHook again →
    // detour re-enters → calls the original again → ~190K-deep recursion that
    // exhausts the 8 MB stack and AVs in ntdll (the recursing code is this
    // exit logger itself, so nothing downstream can ever log it). Latch is
    // process-wide and never reset — a fail-fast is terminal by contract;
    // there is no "recovery", we only need to die cleanly and once.
    private static int _inFailFast;

    /// <summary>
    /// Terminate without any possibility of re-entering a ProcessExitHooks
    /// detour. Each call target is a MinHook *trampoline* (relocated original
    /// prologue + jump PAST our hook), so none of them route back through our
    /// detours. Used to break the fail-fast bounce.
    /// </summary>
    private static void HardTerminateNoReentry(uint code)
    {
        try { _ntTermOriginal?.Invoke(new IntPtr(-1), code); } catch { }
        try { _rtlExitOriginal?.Invoke(code); } catch { }
        try { _exitOriginal?.Invoke(code); } catch { }
        try { Environment.Exit(unchecked((int)code)); } catch { }
    }

    public static void Initialize()
    {
        if (Interlocked.CompareExchange(ref _installed, 1, 0) != 0)
            return;

        try
        {
            IntPtr kernel32 = GetModuleHandleW("kernel32.dll");
            if (kernel32 == IntPtr.Zero)
            {
                RynthLog.Compat("ProcessExitHooks: kernel32 not found.");
                return;
            }

            HookExport(kernel32, "ExitProcess", out IntPtr exitTarget);
            if (exitTarget != IntPtr.Zero)
            {
                _exitDetour = ExitProcessDetour;
                IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_exitDetour);
                IntPtr trampoline = MinHook.HookCreate(exitTarget, detourPtr);
                _exitOriginal = Marshal.GetDelegateForFunctionPointer<ExitProcessDelegate>(trampoline);
                Thread.MemoryBarrier();
                MinHook.Enable(exitTarget);
                RynthLog.Info($"ProcessExitHooks: hooked kernel32!ExitProcess @ 0x{exitTarget.ToInt32():X8}.");
            }

            HookExport(kernel32, "TerminateProcess", out IntPtr terminateTarget);
            if (terminateTarget != IntPtr.Zero)
            {
                _terminateDetour = TerminateProcessDetour;
                IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_terminateDetour);
                IntPtr trampoline = MinHook.HookCreate(terminateTarget, detourPtr);
                _terminateOriginal = Marshal.GetDelegateForFunctionPointer<TerminateProcessDelegate>(trampoline);
                Thread.MemoryBarrier();
                MinHook.Enable(terminateTarget);
                RynthLog.Info($"ProcessExitHooks: hooked kernel32!TerminateProcess @ 0x{terminateTarget.ToInt32():X8}.");
            }

            // ── DO NOT HOOK RaiseFailFastException (proven fatal 2026-05-16) ──
            // Dump analysis of the every-few-minutes silent crash showed a
            // 190K-deep stack overflow whose repeating frame pair is
            // <ReverseOpenStaticDelegateStub>...RaiseFailFastExceptionDelegate
            // ↔ RhpReversePInvokeAttachOrTrapThread2. The NativeAOT reverse-
            // P/Invoke transition that enters a managed detour for this export
            // itself re-triggers RaiseFailFastException, so the detour recurses
            // through the transition shim BEFORE any managed code (incl. any
            // re-entrancy guard) can run. Unfixable from the managed side — the
            // only correct action is to not hook it. A real fail-fast then
            // terminates cleanly (as designed) instead of our hook converting
            // it into the guaranteed stack-overflow crash. Same applies to
            // ntdll!RtlFailFast / RtlFailFast2 below. The other exit hooks
            // (ExitProcess/TerminateProcess/RtlExitUserProcess/NtTerminate) are
            // safe — exiting the process does not re-enter those from within
            // the transition, and they log clean interceptions.

            // ntdll-level hooks — catch paths that bypass kernel32 entirely.
            IntPtr ntdll = GetModuleHandleW("ntdll.dll");
            if (ntdll != IntPtr.Zero)
            {
                HookExport(ntdll, "RtlExitUserProcess", out IntPtr rtlExitTarget);
                if (rtlExitTarget != IntPtr.Zero)
                {
                    _rtlExitDetour = RtlExitUserProcessDetour;
                    IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_rtlExitDetour);
                    IntPtr trampoline = MinHook.HookCreate(rtlExitTarget, detourPtr);
                    _rtlExitOriginal = Marshal.GetDelegateForFunctionPointer<RtlExitUserProcessDelegate>(trampoline);
                    Thread.MemoryBarrier();
                    MinHook.Enable(rtlExitTarget);
                    RynthLog.Info($"ProcessExitHooks: hooked ntdll!RtlExitUserProcess @ 0x{rtlExitTarget.ToInt32():X8}.");
                }

                HookExport(ntdll, "NtTerminateProcess", out IntPtr ntTermTarget);
                if (ntTermTarget != IntPtr.Zero)
                {
                    _ntTermDetour = NtTerminateProcessDetour;
                    IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_ntTermDetour);
                    IntPtr trampoline = MinHook.HookCreate(ntTermTarget, detourPtr);
                    _ntTermOriginal = Marshal.GetDelegateForFunctionPointer<NtTerminateProcessDelegate>(trampoline);
                    Thread.MemoryBarrier();
                    MinHook.Enable(ntTermTarget);
                    RynthLog.Info($"ProcessExitHooks: hooked ntdll!NtTerminateProcess @ 0x{ntTermTarget.ToInt32():X8}.");
                }

                // RtlFailFast / RtlFailFast2 — NOT HOOKED. Same NativeAOT
                // reverse-P/Invoke re-entry recursion as RaiseFailFastException
                // (see the block above). Hooking these with a managed detour
                // is the every-few-minutes crash. Leave them alone; a genuine
                // fail-fast terminates cleanly. (On this Windows build the
                // GetProcAddress for these also failed historically anyway.)
                if (false && ntdll != IntPtr.Zero)
                {
                    HookExport(ntdll, "RtlFailFast", out IntPtr rtlFailFastTarget);
                    if (rtlFailFastTarget != IntPtr.Zero)
                    {
                        _rtlFailFastDetour = RtlFailFastDetour;
                        IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_rtlFailFastDetour);
                        IntPtr trampoline = MinHook.HookCreate(rtlFailFastTarget, detourPtr);
                        _rtlFailFastOriginal = Marshal.GetDelegateForFunctionPointer<RtlFailFastDelegate>(trampoline);
                        Thread.MemoryBarrier();
                        MinHook.Enable(rtlFailFastTarget);
                        RynthLog.Info($"ProcessExitHooks: hooked ntdll!RtlFailFast @ 0x{rtlFailFastTarget.ToInt32():X8}.");
                    }

                    HookExport(ntdll, "RtlFailFast2", out IntPtr rtlFailFast2Target);
                    if (rtlFailFast2Target != IntPtr.Zero)
                    {
                        _rtlFailFast2Detour = RtlFailFast2Detour;
                        IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_rtlFailFast2Detour);
                        IntPtr trampoline = MinHook.HookCreate(rtlFailFast2Target, detourPtr);
                        _rtlFailFast2Original = Marshal.GetDelegateForFunctionPointer<RtlFailFast2Delegate>(trampoline);
                        Thread.MemoryBarrier();
                        MinHook.Enable(rtlFailFast2Target);
                        RynthLog.Info($"ProcessExitHooks: hooked ntdll!RtlFailFast2 @ 0x{rtlFailFast2Target.ToInt32():X8}.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"ProcessExitHooks: install threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void HookExport(IntPtr kernel32, string name, out IntPtr target)
    {
        target = GetProcAddress(kernel32, name);
        if (target == IntPtr.Zero)
            RynthLog.Compat($"ProcessExitHooks: GetProcAddress({name}) failed.");
    }

    private static void ExitProcessDetour(uint exitCode)
    {
        LogTermination("ExitProcess", IntPtr.Zero, exitCode);
        // Run our orderly shutdown BEFORE AC's CRT atexit chain. Without this,
        // hooks stay installed, our threads (TickPump, Avalonia STA) keep
        // running, and AC's static-destructor LongHash teardown at 0x416B10
        // races them on a hash chain → the popup AV at 0x00416C86.
        try { EngineLifecycle.Shutdown(); }
        catch (Exception ex) { RynthLog.Info($"ProcessExit: EngineLifecycle.Shutdown threw {ex.GetType().Name}: {ex.Message}"); }
        try { _exitOriginal!(exitCode); }
        catch { /* original never returns; swallow if it somehow does */ }
    }

    private static bool TerminateProcessDetour(IntPtr hProcess, uint exitCode)
    {
        // Only log when terminating ourselves — TerminateProcess on other
        // process handles is a normal Process.Kill and not what we're hunting.
        // hProcess == -1 (pseudo-handle for current process) or == GetCurrentProcess() handle.
        bool selfKill = hProcess == new IntPtr(-1) || hProcess == GetCurrentProcessHandle();
        if (selfKill)
            LogTermination("TerminateProcess", hProcess, exitCode);
        try { return _terminateOriginal!(hProcess, exitCode); }
        catch { return false; }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    private static IntPtr GetCurrentProcessHandle() => GetCurrentProcess();

    private static void RtlExitUserProcessDetour(uint exitStatus)
    {
        LogTermination("RtlExitUserProcess", IntPtr.Zero, exitStatus);
        try { EngineLifecycle.Shutdown(); }
        catch (Exception ex) { RynthLog.Info($"ProcessExit: EngineLifecycle.Shutdown threw {ex.GetType().Name}: {ex.Message}"); }
        try { _rtlExitOriginal!(exitStatus); }
        catch { }
    }

    private static uint NtTerminateProcessDetour(IntPtr processHandle, uint exitStatus)
    {
        bool selfKill = processHandle == new IntPtr(-1) || processHandle == GetCurrentProcessHandle();
        if (selfKill)
            LogTermination("NtTerminateProcess", processHandle, exitStatus);
        try { return _ntTermOriginal!(processHandle, exitStatus); }
        catch { return 0xC0000001; /* STATUS_UNSUCCESSFUL */ }
    }

    private static void RaiseFailFastExceptionDetour(IntPtr pExceptionRecord, IntPtr pContext, uint dwFlags)
    {
        if (Interlocked.Exchange(ref _inFailFast, 1) != 0)
        {
            // Re-entered: the original bounced back through our hook instead
            // of terminating. Forwarding again is the 190K-deep stack-overflow
            // recursion. Break it — die now, non-recursively.
            HardTerminateNoReentry(dwFlags);
            return;
        }
        LogTermination("RaiseFailFastException", IntPtr.Zero, dwFlags);
        try { _failFastOriginal!(pExceptionRecord, pContext, dwFlags); }
        catch { }
        // A real fail-fast never returns. In this environment it can — don't
        // let the caller spin re-raising it; terminate deterministically.
        HardTerminateNoReentry(dwFlags);
    }

    private static void RtlFailFastDetour(uint code)
    {
        if (Interlocked.Exchange(ref _inFailFast, 1) != 0)
        {
            HardTerminateNoReentry(code);
            return;
        }
        LogTermination("RtlFailFast", IntPtr.Zero, code);
        try { _rtlFailFastOriginal!(code); }
        catch { /* original never returns */ }
        HardTerminateNoReentry(code);
    }

    private static void RtlFailFast2Detour(uint code, IntPtr pExceptionRecord)
    {
        if (Interlocked.Exchange(ref _inFailFast, 1) != 0)
        {
            HardTerminateNoReentry(code);
            return;
        }
        LogTermination("RtlFailFast2", IntPtr.Zero, code);
        try { _rtlFailFast2Original!(code, pExceptionRecord); }
        catch { /* original never returns */ }
        HardTerminateNoReentry(code);
    }

    private static void LogTermination(string func, IntPtr hProcess, uint exitCode)
    {
        if (Interlocked.Exchange(ref _logged, 1) != 0)
            return; // don't double-log if both ExitProcess + TerminateProcess fire

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== PROCESS TERMINATION INTERCEPTED ====");
            sb.AppendLine($"  function:  kernel32!{func}");
            sb.AppendLine($"  exit code: 0x{exitCode:X8} ({(int)exitCode})");
            sb.AppendLine($"  thread:    {GetCurrentThreadId()}  (managed name='{Thread.CurrentThread.Name ?? "<unnamed>"}', managed id={Thread.CurrentThread.ManagedThreadId})");
            if (hProcess != IntPtr.Zero)
                sb.AppendLine($"  hProcess:  0x{hProcess.ToInt32():X8}");

            var frames = new IntPtr[CapturedFrames];
            // FramesToSkip=2 to step past this method + the detour wrapper.
            ushort captured = RtlCaptureStackBackTrace(2, (uint)CapturedFrames, frames, out _);
            sb.AppendLine($"  native back-trace ({captured} frame(s)) — note: x86 EBP-walk, deep frames may be unreliable under FPO:");
            for (int i = 0; i < captured; i++)
            {
                IntPtr addr = frames[i];
                if (addr == IntPtr.Zero) break;
                string module = ResolveModule(addr, out int rva);
                sb.AppendLine($"    [{i:00}] 0x{addr.ToInt32():X8}  {module}+0x{rva:X}");
            }

            // Managed stack — the actually useful one. NativeAOT supports
            // Environment.StackTrace; this gives us the C# call chain that
            // led to ExitProcess being called from inside our engine.
            sb.AppendLine("  managed stack (Environment.StackTrace):");
            try
            {
                string mst = Environment.StackTrace ?? "<null>";
                foreach (string raw in mst.Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Length > 0) sb.AppendLine($"    {line}");
                }
            }
            catch (Exception stEx)
            {
                sb.AppendLine($"    <stack capture failed: {stEx.GetType().Name}: {stEx.Message}>");
            }

            sb.Append("==========================================");

            RynthLog.Info(sb.ToString());
        }
        catch
        {
            // Logging path must not throw — process is about to die anyway,
            // and our detour MUST forward to the original so termination
            // actually completes.
        }
    }

    private static string ResolveModule(IntPtr address, out int rva)
    {
        rva = 0;
        try
        {
            if (!GetModuleHandleExW(ModuleFromAddrFlags, address, out IntPtr hMod) || hMod == IntPtr.Zero)
                return "<unknown>";

            rva = (int)(address.ToInt64() - hMod.ToInt64());

            char[] buf = new char[260];
            uint n = GetModuleFileNameW(hMod, buf, (uint)buf.Length);
            if (n == 0)
                return $"<mod 0x{hMod.ToInt32():X8}>";

            string full = new string(buf, 0, (int)n);
            int slash = full.LastIndexOfAny(new[] { '\\', '/' });
            return slash >= 0 ? full.Substring(slash + 1) : full;
        }
        catch
        {
            return "<resolve-failed>";
        }
    }
}

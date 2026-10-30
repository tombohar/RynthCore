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
    private static int _installed;
    private static int _logged; // ensure we log at most once even if both fire

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

            // DO NOT HOOK RaiseFailFastException, RtlFailFast or RtlFailFast2 (proven fatal
            // in practice): the reverse-P/Invoke transition into a managed detour for those
            // exports re-triggered the fail-fast itself (190K-deep recursion through the
            // transition shim, before any managed guard could run). Their detour bodies were
            // dead code and are gone, so nothing can switch them back on by
            // accident: a managed body on the fail-fast path turns a crash that would leave
            // a dump into a silent one.
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
        // A crash exit goes straight to the original, before any managed work (IsCrashExit).
        // The reverse-P/Invoke transition into this detour is the only managed step left; a
        // native pass-through would remove that too; it is possible but not done here.
        if (IsCrashExit(exitCode))
        {
            SehTrampoline.FlushCrashLog(exitCode);   // native, allocation-free
            _exitOriginal!(exitCode);
            return;
        }
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

    // A crash exit: an NTSTATUS error or exception code (0xC0000000 and up: 0xC0000005 an
    // access violation, 0xE0434352 a CLR exception). The process is dying of a fault, often
    // from inside the runtime's own fatal-error path; managed work here (logging, a
    // MiniDumpWriteDump) can deadlock or fault again there and leave nothing at all. So a crash
    // exit is passed straight to the original. The evidence comes from places that do not
    // need managed code on the way out: the native crash logger (SehTrampoline.c, its vectored
    // handler sees the fault first) and CoreCLR's own createdump (native\Loader\Loader.c, ConfigureCrashDumps). This
    // replaces the managed crash dump written here before, which left nothing for real crashes.
    // The one call made first, SehTrampoline.FlushCrashLog, is a raw native call: it writes
    // the crash logger's recent first-chance records that were not written one by one (the
    // fatal hit of an address seen before is usually among them; a terminate from a handler
    // never reaches the last-chance filter). Which codes count: CrashExitRule.
    private static bool IsCrashExit(uint code) => CrashExitRule.IsCrashExit(code);

    // Review fix (2026-10-18): the managed dump this file used to write on TerminateProcess /
    // NtTerminateProcess (f9c62ed, for Lucy's Town) was removed above on the grounds that
    // createdump covers it. It covers only the paths the CLR itself ends: an AV that unwinds
    // through one of our managed detours, or reaches the CLR's unhandled-exception filter (both
    // checked offline with a small CoreCLR host: createdump runs). It does NOT cover a native AV
    // that AC's own handler catches and answers with TerminateProcess: no CLR frame, no CLR
    // filter, and nothing else here writes a dump. So the dump is back on that path, with what
    // made the old one risky taken out:
    //   - after the native log flush, so the crash log is on disk whatever happens next;
    //   - on its own thread with a bounded wait (CrashDumpWaitMs): if the dying process is stuck
    //     on a lock, the exit goes ahead after that, never hangs; a 0-byte file is deleted by
    //     the next client's log housekeeping;
    //   - once per process, and not at all when createdump already wrote clr_<pid>_*.dmp;
    //   - small type only (CrashDump.WriteSelfDump), which also needs free space
    //     (DumpBudget.SmallDumpMinFree), and only the newest few are kept.
    private static int _crashDumped;
    private const int CrashDumpWaitMs = 20_000;

    private static void DumpCrashOnce(string via, uint code)
    {
        if (Interlocked.Exchange(ref _crashDumped, 1) != 0) return;
        try
        {
            var worker = new Thread(() =>
            {
                try { CrashDump.WriteCrashDumpOnce(via, code); }
                catch { }
            }) { IsBackground = true, Name = "RynthCore.CrashDump" };
            worker.Start();
            worker.Join(CrashDumpWaitMs);
        }
        catch { /* never block or break the exit */ }
    }

    private static bool TerminateProcessDetour(IntPtr hProcess, uint exitCode)
    {
        // Only log when terminating ourselves: TerminateProcess on other process handles is a
        // normal Process.Kill and not what we're hunting. hProcess == -1 is the pseudo-handle.
        bool selfKill = hProcess == new IntPtr(-1) || hProcess == GetCurrentProcessHandle();
        if (selfKill)
        {
            if (IsCrashExit(exitCode))
            {
                SehTrampoline.FlushCrashLog(exitCode);
                DumpCrashOnce("TerminateProcess", exitCode);
            }
            else LogTermination("TerminateProcess", hProcess, exitCode);
        }
        try { return _terminateOriginal!(hProcess, exitCode); }
        catch { return false; }
    }
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    private static IntPtr GetCurrentProcessHandle() => GetCurrentProcess();

    private static void RtlExitUserProcessDetour(uint exitStatus)
    {
        if (IsCrashExit(exitStatus))   // see ExitProcessDetour
        {
            SehTrampoline.FlushCrashLog(exitStatus);
            _rtlExitOriginal!(exitStatus);
            return;
        }
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
        {
            // A second flush (TerminateProcess calls this) writes nothing: records are written once.
            if (IsCrashExit(exitStatus))
            {
                SehTrampoline.FlushCrashLog(exitStatus);
                DumpCrashOnce("NtTerminateProcess", exitStatus);
            }
            else LogTermination("NtTerminateProcess", processHandle, exitStatus);
        }
        try { return _ntTermOriginal!(processHandle, exitStatus); }
        catch { return 0xC0000001; /* STATUS_UNSUCCESSFUL */ }
    }

    /// <summary>ExitProcess/TerminateProcess was called: the process is closing.</summary>
    public static bool TerminationIntercepted => Volatile.Read(ref _logged) != 0;

    private static void LogTermination(string func, IntPtr hProcess, uint exitCode)
    {
        if (Interlocked.Exchange(ref _logged, 1) != 0)
            return; // don't double-log if both ExitProcess + TerminateProcess fire

        try
        {
            // Dump RynthCore->AC action trace BEFORE building the termination
            // banner so the trace is in the log even if the banner build throws.
            // Captures the last ~256 mutating calls — the "too many switches?"
            // ring buffer for Class B AVs (AC UIElement faults, no RC stack).
            AcActionTrace.DumpToLog();

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

    internal static string ResolveModule(IntPtr address, out int rva)
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

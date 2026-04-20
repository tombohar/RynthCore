// ============================================================================
//  RynthCore.Engine - CrashLogger.cs
//  Installs a Win32 Vectored Exception Handler so that an AV (uncatchable in
//  NativeAOT) is logged with the faulting address + module BEFORE the process
//  dies. Without this, silent AVs leave us guessing which call crashed.
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine;

internal static class CrashLogger
{
    [StructLayout(LayoutKind.Sequential)]
    private struct EXCEPTION_RECORD
    {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public IntPtr ExceptionRecord;
        public IntPtr ExceptionAddress;
        public uint NumberParameters;
        // ExceptionInformation[15] follows — not needed for our logging
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EXCEPTION_POINTERS
    {
        public IntPtr ExceptionRecord;   // EXCEPTION_RECORD*
        public IntPtr ContextRecord;     // CONTEXT*
    }

    private delegate int VectoredHandler(IntPtr exceptionInfo);

    [DllImport("kernel32.dll")]
    private static extern IntPtr AddVectoredExceptionHandler(uint first, VectoredHandler handler);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(IntPtr hModule, char[] lpFilename, uint nSize);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetModuleInformation(IntPtr hProcess, IntPtr hModule, out MODULEINFO lpmodinfo, uint cb);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetModuleHandleExW(uint dwFlags, IntPtr lpModuleName, out IntPtr phModule);

    [StructLayout(LayoutKind.Sequential)]
    private struct MODULEINFO
    {
        public IntPtr lpBaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    // GetModuleHandleEx flags
    private const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS  = 0x00000004;
    private const uint GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x00000002;

    // Exception codes worth logging — skip the noisy breakpoints / single-steps.
    private const uint EXCEPTION_ACCESS_VIOLATION         = 0xC0000005;
    private const uint EXCEPTION_ILLEGAL_INSTRUCTION      = 0xC000001D;
    private const uint EXCEPTION_PRIV_INSTRUCTION         = 0xC0000096;
    private const uint EXCEPTION_INT_DIVIDE_BY_ZERO       = 0xC0000094;
    private const uint EXCEPTION_STACK_OVERFLOW           = 0xC00000FD;

    // Return values for VectoredHandler
    private const int EXCEPTION_CONTINUE_SEARCH   = 0;

    // Pin the delegate so it isn't GC'd while Windows holds the function pointer.
    private static VectoredHandler? _handler;
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;

        try
        {
            _handler = OnVectoredException;
            IntPtr cookie = AddVectoredExceptionHandler(1 /*CALL_FIRST*/, _handler);
            if (cookie == IntPtr.Zero)
            {
                RynthLog.Info($"CrashLogger: AddVectoredExceptionHandler failed (err {Marshal.GetLastWin32Error()})");
                return;
            }
            _installed = true;
            RynthLog.Info("CrashLogger: VEH installed.");
        }
        catch (Exception ex)
        {
            RynthLog.Info($"CrashLogger: install failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int OnVectoredException(IntPtr pExceptionInfo)
    {
        try
        {
            EXCEPTION_POINTERS ep = Marshal.PtrToStructure<EXCEPTION_POINTERS>(pExceptionInfo);
            EXCEPTION_RECORD er = Marshal.PtrToStructure<EXCEPTION_RECORD>(ep.ExceptionRecord);

            // Only log genuinely fatal categories. Normal operation throws SEH
            // exceptions (C++ 0xE06D7363, CLR 0xE0434352, etc.) we should ignore.
            bool interesting =
                er.ExceptionCode == EXCEPTION_ACCESS_VIOLATION ||
                er.ExceptionCode == EXCEPTION_ILLEGAL_INSTRUCTION ||
                er.ExceptionCode == EXCEPTION_PRIV_INSTRUCTION ||
                er.ExceptionCode == EXCEPTION_INT_DIVIDE_BY_ZERO ||
                er.ExceptionCode == EXCEPTION_STACK_OVERFLOW;

            if (!interesting)
                return EXCEPTION_CONTINUE_SEARCH;

            string module = ResolveModule(er.ExceptionAddress, out IntPtr moduleBase);
            long rva = moduleBase != IntPtr.Zero
                ? er.ExceptionAddress.ToInt64() - moduleBase.ToInt64()
                : 0;

            RynthLog.Info(
                $"CRASH: code=0x{er.ExceptionCode:X8} addr=0x{er.ExceptionAddress.ToInt64():X8} " +
                $"module={module} base=0x{moduleBase.ToInt64():X8} rva=0x{rva:X}");
        }
        catch
        {
            // Logging must not throw — we're in an exception handler.
        }

        return EXCEPTION_CONTINUE_SEARCH;
    }

    private static string ResolveModule(IntPtr address, out IntPtr moduleBase)
    {
        moduleBase = IntPtr.Zero;
        try
        {
            if (!GetModuleHandleExW(
                    GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                    address, out IntPtr hModule) || hModule == IntPtr.Zero)
            {
                return "<unknown>";
            }

            moduleBase = hModule;
            char[] buf = new char[512];
            uint len = GetModuleFileNameW(hModule, buf, (uint)buf.Length);
            if (len == 0) return "<noname>";
            return new string(buf, 0, (int)len);
        }
        catch
        {
            return "<resolve-failed>";
        }
    }
}

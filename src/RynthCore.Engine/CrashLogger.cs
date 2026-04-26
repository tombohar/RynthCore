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

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MODULEINFO
    {
        public IntPtr lpBaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private const uint MEM_COMMIT    = 0x00001000;
    private const uint PAGE_NOACCESS = 0x00000001;
    private const uint PAGE_GUARD    = 0x00000100;

    // x86 CONTEXT field offsets (verified against winnt.h _CONTEXT layout).
    // FloatSave occupies 112 bytes between the Dr* registers and SegGs.
    private const int CTX_OFF_EDI = 156;
    private const int CTX_OFF_ESI = 160;
    private const int CTX_OFF_EBX = 164;
    private const int CTX_OFF_EDX = 168;
    private const int CTX_OFF_ECX = 172;
    private const int CTX_OFF_EAX = 176;
    private const int CTX_OFF_EBP = 180;
    private const int CTX_OFF_EIP = 184;
    private const int CTX_OFF_ESP = 196;

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
                $"module={ShortModule(module)} base=0x{moduleBase.ToInt64():X8} rva=0x{rva:X}");

            // Decode AV operation from ExceptionInformation[0..1] (first param =
            // 0 read / 1 write / 8 DEP, second param = faulting data address).
            // Only available when NumberParameters >= 2; safe to read the two
            // dwords directly past the fixed EXCEPTION_RECORD header.
            if (er.ExceptionCode == EXCEPTION_ACCESS_VIOLATION && er.NumberParameters >= 2)
            {
                int infoOffset = Marshal.SizeOf<EXCEPTION_RECORD>();
                uint avKind = (uint)Marshal.ReadInt32(ep.ExceptionRecord, infoOffset);
                IntPtr avAddr = (IntPtr)Marshal.ReadInt32(ep.ExceptionRecord, infoOffset + 4);
                string kind = avKind switch { 0 => "read", 1 => "write", 8 => "DEP", _ => $"?{avKind}" };
                RynthLog.Info($"CRASH AV: {kind} faultAddr=0x{avAddr.ToInt64():X8}");
            }

            DumpContext(ep.ContextRecord);
        }
        catch
        {
            // Logging must not throw — we're in an exception handler.
        }

        return EXCEPTION_CONTINUE_SEARCH;
    }

    /// <summary>
    /// Reads the x86 CONTEXT* and emits register dump + EBP-frame walk + ESP
    /// sweep. EBP frames cover compilers that preserve frame pointers (most
    /// managed/CLR/NativeAOT frames); the ESP sweep is the fallback for
    /// FPO-optimized native code (cimgui, acclient) where return addresses
    /// still sit on the stack but EBP is repurposed.
    /// </summary>
    private static void DumpContext(IntPtr ctx)
    {
        if (ctx == IntPtr.Zero) return;

        uint eip, eax, ebx, ecx, edx, esi, edi, ebp, esp;
        try
        {
            eip = (uint)Marshal.ReadInt32(ctx, CTX_OFF_EIP);
            eax = (uint)Marshal.ReadInt32(ctx, CTX_OFF_EAX);
            ebx = (uint)Marshal.ReadInt32(ctx, CTX_OFF_EBX);
            ecx = (uint)Marshal.ReadInt32(ctx, CTX_OFF_ECX);
            edx = (uint)Marshal.ReadInt32(ctx, CTX_OFF_EDX);
            esi = (uint)Marshal.ReadInt32(ctx, CTX_OFF_ESI);
            edi = (uint)Marshal.ReadInt32(ctx, CTX_OFF_EDI);
            ebp = (uint)Marshal.ReadInt32(ctx, CTX_OFF_EBP);
            esp = (uint)Marshal.ReadInt32(ctx, CTX_OFF_ESP);
        }
        catch
        {
            return;
        }

        RynthLog.Info(
            $"CRASH regs: eip=0x{eip:X8} eax=0x{eax:X8} ebx=0x{ebx:X8} ecx=0x{ecx:X8} " +
            $"edx=0x{edx:X8} esi=0x{esi:X8} edi=0x{edi:X8}");
        RynthLog.Info($"CRASH regs: ebp=0x{ebp:X8} esp=0x{esp:X8}");

        WalkEbpFrames((IntPtr)ebp);
        SweepStack((IntPtr)esp);
    }

    private static void WalkEbpFrames(IntPtr startEbp)
    {
        try
        {
            IntPtr cur = startEbp;
            for (int frame = 0; frame < 16; frame++)
            {
                if (!IsReadable(cur, 8)) break;
                IntPtr nextEbp = (IntPtr)Marshal.ReadInt32(cur);
                IntPtr ret    = (IntPtr)Marshal.ReadInt32(cur, 4);
                if (ret == IntPtr.Zero) break;

                string mod = ResolveModule(ret, out IntPtr modBase);
                if (modBase != IntPtr.Zero)
                {
                    long fr = ret.ToInt64() - modBase.ToInt64();
                    RynthLog.Info($"  ebp[{frame:D2}] ret=0x{ret.ToInt64():X8} {ShortModule(mod)}+0x{fr:X}");
                }
                else
                {
                    RynthLog.Info($"  ebp[{frame:D2}] ret=0x{ret.ToInt64():X8} <unknown>");
                }

                // Stop if next frame pointer doesn't look like a higher stack address.
                long delta = nextEbp.ToInt64() - cur.ToInt64();
                if (delta <= 0 || delta > 0x100000) break;
                cur = nextEbp;
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Reads up to 96 dwords from ESP and logs each value that lands inside a
    /// loaded module. For an indirect-call AV, [esp] is the return address into
    /// the caller — usually the first hit identifies the bad call site.
    /// </summary>
    private static void SweepStack(IntPtr startEsp)
    {
        const int sweepDwords = 96;
        try
        {
            int hits = 0;
            for (int i = 0; i < sweepDwords; i++)
            {
                IntPtr addr = (IntPtr)(startEsp.ToInt64() + i * 4);
                if (!IsReadable(addr, 4)) break;
                IntPtr v = (IntPtr)Marshal.ReadInt32(addr);
                if (v == IntPtr.Zero) continue;

                string mod = ResolveModule(v, out IntPtr modBase);
                if (modBase == IntPtr.Zero) continue;

                long fr = v.ToInt64() - modBase.ToInt64();
                RynthLog.Info($"  esp+0x{i*4:X3}: 0x{v.ToInt64():X8} {ShortModule(mod)}+0x{fr:X}");

                if (++hits >= 24) break;  // cap to keep log readable
            }
        }
        catch
        {
        }
    }

    private static bool IsReadable(IntPtr addr, int bytes)
    {
        try
        {
            if (VirtualQuery(addr, out MEMORY_BASIC_INFORMATION mbi, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == IntPtr.Zero)
                return false;
            if (mbi.State != MEM_COMMIT) return false;
            if ((mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0) return false;

            // Make sure the requested range doesn't cross out of the committed region.
            long endRequested = addr.ToInt64() + bytes;
            long endRegion = mbi.BaseAddress.ToInt64() + mbi.RegionSize.ToInt64();
            return endRequested <= endRegion;
        }
        catch
        {
            return false;
        }
    }

    private static string ShortModule(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return "<?>";
        int slash = fullPath.LastIndexOfAny(new[] { '\\', '/' });
        return slash >= 0 ? fullPath.Substring(slash + 1) : fullPath;
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

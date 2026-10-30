using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RynthCore.App.Avalonia;

/// <summary>
/// Finds the running crash-dump writers (createdump.exe, WerFault.exe) and the client each one
/// is dumping, for the launcher's kill holds (CrashDumpWriterRule). The client's pid comes from
/// the writer's command line; when that can't be read, from its parent (CoreCLR starts
/// createdump from the dying client). Read-only: opens each writer for query only.
/// </summary>
internal static class CrashDumpWriters
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessBasicInformation = 0;
    private const int ProcessCommandLineInformation = 60;

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Client pid -> the writer dumping it ("createdump.exe pid 1234"). Never throws.</summary>
    public static Dictionary<int, string> Find()
    {
        var result = new Dictionary<int, string>();
        foreach (string name in CrashDumpWriterRule.WriterNames)
        {
            Process[] found;
            try { found = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (Process p in found)
            {
                try
                {
                    int writerPid = p.Id;
                    string? cmd = TryGetCommandLine(writerPid);
                    int? target = cmd != null ? CrashDumpWriterRule.TargetPid(name, cmd) : TryGetParentPid(writerPid);
                    if (target is int pid && pid > 0)
                        result[pid] = $"{name}.exe pid {writerPid}";
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        return result;
    }

    private static string? TryGetCommandLine(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        IntPtr buf = IntPtr.Zero;
        try
        {
            int len = 4096;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                buf = Marshal.AllocHGlobal(len);
                int status = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, len, out int needed);
                if (status == 0)
                {
                    var us = Marshal.PtrToStructure<UNICODE_STRING>(buf);
                    return us.Buffer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
                }
                Marshal.FreeHGlobal(buf);
                buf = IntPtr.Zero;
                if (needed <= len || needed > 1 << 20) return null;   // a size mismatch says how much it needs
                len = needed;
            }
            return null;
        }
        catch { return null; }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            CloseHandle(h);
        }
    }

    private static int? TryGetParentPid(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        IntPtr buf = IntPtr.Zero;
        try
        {
            int size = Marshal.SizeOf<PROCESS_BASIC_INFORMATION>();
            buf = Marshal.AllocHGlobal(size);
            if (NtQueryInformationProcess(h, ProcessBasicInformation, buf, size, out _) != 0) return null;
            var pbi = Marshal.PtrToStructure<PROCESS_BASIC_INFORMATION>(buf);
            return (int)pbi.InheritedFromUniqueProcessId.ToInt64();
        }
        catch { return null; }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            CloseHandle(h);
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NexCore.Injector;

public sealed class EngineInjectionService
{
    public const string DefaultAcProcessName = "acclient";
    public const string EngineDllName = "NexCore.Engine.dll";
    private const string InitExport = "NexCoreInit";

    private const uint ProcessAllAccess = 0x001F0FFF;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(
        IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out uint lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(
        IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize,
        IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetModuleHandleA(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    public string? TryResolveEnginePath(string? explicitEnginePath)
    {
        foreach (string path in GetEngineCandidatePaths(explicitEnginePath))
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    public Process[] FindTargetProcesses(string processName = DefaultAcProcessName)
    {
        return Process.GetProcessesByName(processName);
    }

    public InjectionResult InjectFirstRunning(string enginePath, Action<string>? log = null, string processName = DefaultAcProcessName)
    {
        Process[] processes = FindTargetProcesses(processName);
        if (processes.Length == 0)
            return InjectionResult.Failure(1, $"{processName}.exe is not running.", enginePath);

        return InjectIntoProcess(processes[0], enginePath, log);
    }

    public async Task<bool> WaitForGraphicsReadyAsync(
        Process targetProcess,
        Action<string>? log = null,
        int timeoutMs = 120000,
        int stableReadyMs = 5000,
        int pollIntervalMs = 1000,
        CancellationToken cancellationToken = default)
    {
        log ??= _ => { };

        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        DateTime? readySince = null;
        string? lastStatus = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool isReady = TryDescribeGraphicsReadiness(targetProcess, out string status);
            if (!string.Equals(lastStatus, status, StringComparison.Ordinal))
            {
                log(status);
                lastStatus = status;
            }

            if (isReady)
            {
                readySince ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - readySince.Value).TotalMilliseconds >= stableReadyMs)
                {
                    log("Auto-inject wait: graphics-ready state is stable.");
                    return true;
                }
            }
            else
            {
                readySince = null;
            }

            await Task.Delay(pollIntervalMs, cancellationToken);
        }

        log("Auto-inject wait: timed out before the AC client reached a graphics-ready state.");
        return false;
    }

    public InjectionResult InjectIntoProcess(Process targetProcess, string enginePath, Action<string>? log = null)
    {
        log ??= _ => { };

        if (string.IsNullOrWhiteSpace(enginePath))
            return InjectionResult.Failure(1, $"Engine DLL not found: {enginePath}", enginePath, targetProcess.Id);

        if (!TryResolveInjectableEnginePath(enginePath, log, out string resolvedEnginePath, out uint initRva, out string resolveError))
            return InjectionResult.Failure(1, resolveError, enginePath, targetProcess.Id);

        log($"Engine DLL: {resolvedEnginePath}");

        string minhookPath = Path.Combine(Path.GetDirectoryName(resolvedEnginePath)!, "minhook.x86.dll");
        if (!File.Exists(minhookPath))
            log("WARNING: minhook.x86.dll not found next to engine DLL.");
        else
            log("MinHook found beside engine DLL.");

        log($"{InitExport} RVA: 0x{initRva:X8}");

        log($"Target PID: {targetProcess.Id} ({targetProcess.ProcessName})");

        IntPtr hProcess = OpenProcess(ProcessAllAccess, false, targetProcess.Id);
        if (hProcess == IntPtr.Zero)
        {
            return InjectionResult.Failure(
                1,
                $"OpenProcess failed (error {Marshal.GetLastWin32Error()}). Try running as Administrator.",
                resolvedEnginePath,
                targetProcess.Id);
        }

        try
        {
            IntPtr hKernel32 = GetModuleHandleA("kernel32.dll");
            IntPtr loadLibAddr = GetProcAddress(hKernel32, "LoadLibraryA");
            if (loadLibAddr == IntPtr.Zero)
                return InjectionResult.Failure(1, "Could not locate LoadLibraryA.", resolvedEnginePath, targetProcess.Id);

            byte[] dllPathBytes = Encoding.ASCII.GetBytes(resolvedEnginePath + "\0");
            IntPtr remoteStr = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)dllPathBytes.Length, MemCommit | MemReserve, PageReadWrite);
            if (remoteStr == IntPtr.Zero)
                return InjectionResult.Failure(1, "VirtualAllocEx failed for remote DLL path.", resolvedEnginePath, targetProcess.Id);

            try
            {
                if (!WriteProcessMemory(hProcess, remoteStr, dllPathBytes, (uint)dllPathBytes.Length, out _))
                {
                    return InjectionResult.Failure(
                        1,
                        $"WriteProcessMemory failed (error {Marshal.GetLastWin32Error()}).",
                        resolvedEnginePath,
                        targetProcess.Id);
                }

                log("[1/2] Injecting engine DLL...");
                IntPtr loadThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, loadLibAddr, remoteStr, 0, out _);
                if (loadThread == IntPtr.Zero)
                {
                    return InjectionResult.Failure(
                        1,
                        $"CreateRemoteThread for LoadLibraryA failed (error {Marshal.GetLastWin32Error()}).",
                        resolvedEnginePath,
                        targetProcess.Id);
                }

                uint loadLibResult;
                try
                {
                    WaitForSingleObject(loadThread, 10000);
                    GetExitCodeThread(loadThread, out loadLibResult);
                }
                finally
                {
                    CloseHandle(loadThread);
                }

                if (loadLibResult == 0)
                {
                    return InjectionResult.Failure(
                        1,
                        "LoadLibrary returned NULL. Check engine dependencies beside the DLL.",
                        resolvedEnginePath,
                        targetProcess.Id);
                }

                IntPtr remoteBase = (IntPtr)loadLibResult;
                log($"[1/2] Engine mapped at 0x{remoteBase:X8}");

                log("[2/2] Calling NexCoreInit...");
                IntPtr remoteInitAddr = IntPtr.Add(remoteBase, (int)initRva);
                IntPtr initThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, remoteInitAddr, IntPtr.Zero, 0, out _);
                if (initThread == IntPtr.Zero)
                {
                    return InjectionResult.Failure(
                        1,
                        $"CreateRemoteThread for {InitExport} failed (error {Marshal.GetLastWin32Error()}).",
                        resolvedEnginePath,
                        targetProcess.Id);
                }

                uint initResult;
                try
                {
                    WaitForSingleObject(initThread, 10000);
                    GetExitCodeThread(initThread, out initResult);
                }
                finally
                {
                    CloseHandle(initThread);
                }

                log(initResult == 0 ? "NexCoreInit returned success." : $"NexCoreInit returned {initResult}.");
                return InjectionResult.SuccessResult("NexCore injected successfully.", resolvedEnginePath, targetProcess.Id, initResult);
            }
            finally
            {
                VirtualFreeEx(hProcess, remoteStr, 0, MemRelease);
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private IEnumerable<string> GetEngineCandidatePaths(string? explicitEnginePath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(List<string> candidates, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string fullPath = Path.GetFullPath(path);
            if (seen.Add(fullPath))
                candidates.Add(fullPath);
        }

        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(explicitEnginePath))
        {
            string explicitFullPath = Path.GetFullPath(explicitEnginePath);
            AddCandidate(candidates, explicitFullPath);

            string? explicitDir = Path.GetDirectoryName(explicitFullPath);
            if (!string.IsNullOrWhiteSpace(explicitDir))
            {
                AddCandidate(candidates, Path.Combine(explicitDir, "publish", EngineDllName));
                AddCandidate(candidates, Path.Combine(explicitDir, "native", EngineDllName));
                AddCandidate(candidates, Path.Combine(explicitDir, "Native", EngineDllName));

                string? parentDir = Directory.GetParent(explicitDir)?.FullName;
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    AddCandidate(candidates, Path.Combine(parentDir, "publish", EngineDllName));
                    AddCandidate(candidates, Path.Combine(parentDir, "native", EngineDllName));
                    AddCandidate(candidates, Path.Combine(parentDir, "Native", EngineDllName));
                }
            }
        }

        string cwd = Environment.CurrentDirectory;
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;

        AddCandidate(candidates, Path.Combine(baseDir, EngineDllName));
        AddCandidate(candidates, Path.Combine(exeDir, EngineDllName));
        AddCandidate(candidates, Path.Combine(cwd, "src", "NexCore.Engine", "bin", "x86", "Release", "net9.0-windows", "win-x86", "publish", EngineDllName));
        AddCandidate(candidates, Path.Combine(cwd, "src", "NexCore.Engine", "bin", "x86", "Release", "net9.0-windows", "win-x86", "native", EngineDllName));
        AddCandidate(candidates, Path.Combine(cwd, "src", "NexCore.Engine", "bin", "x86", "Release", "net9.0-windows", "win-x86", EngineDllName));
        AddCandidate(candidates, Path.Combine(cwd, "src", "NexCore.Engine", "bin", "Release", "net9.0-windows", "win-x86", "publish", EngineDllName));
        AddCandidate(candidates, Path.Combine(cwd, "src", "NexCore.Engine", "bin", "Release", "net9.0-windows", "win-x86", "native", EngineDllName));
        AddCandidate(candidates, Path.Combine(cwd, "src", "NexCore.Engine", "bin", "Release", "net9.0-windows", "win-x86", EngineDllName));
        AddCandidate(candidates, Path.Combine(baseDir, "..", "..", "..", "..", "NexCore.Engine", "bin", "x86", "Release", "net9.0-windows", "win-x86", "publish", EngineDllName));
        AddCandidate(candidates, Path.Combine(baseDir, "..", "..", "..", "..", "NexCore.Engine", "bin", "x86", "Release", "net9.0-windows", "win-x86", "native", EngineDllName));
        AddCandidate(candidates, Path.Combine(baseDir, "..", "..", "..", "..", "NexCore.Engine", "bin", "x86", "Release", "net9.0-windows", "win-x86", EngineDllName));
        AddCandidate(candidates, Path.Combine(baseDir, "..", "..", "..", "..", "NexCore.Engine", "bin", "Release", "net9.0-windows", "win-x86", "publish", EngineDllName));
        AddCandidate(candidates, Path.Combine(baseDir, "..", "..", "..", "..", "NexCore.Engine", "bin", "Release", "net9.0-windows", "win-x86", "native", EngineDllName));
        AddCandidate(candidates, Path.Combine(baseDir, "..", "..", "..", "..", "NexCore.Engine", "bin", "Release", "net9.0-windows", "win-x86", EngineDllName));
        AddCandidate(candidates, Path.Combine(baseDir, "..", "NexCore.Engine", "bin", "Release", "net9.0-windows", "win-x86", "publish", EngineDllName));
        AddCandidate(candidates, Path.Combine(baseDir, "..", "NexCore.Engine", "bin", "Release", "net9.0-windows", "win-x86", "native", EngineDllName));
        AddCandidate(candidates, Path.Combine(baseDir, "..", "NexCore.Engine", "bin", "Release", "net9.0-windows", "win-x86", EngineDllName));
        AddCandidate(candidates, Path.Combine(cwd, EngineDllName));

        return candidates;
    }

    private bool TryResolveInjectableEnginePath(
        string requestedEnginePath,
        Action<string> log,
        out string resolvedEnginePath,
        out uint initRva,
        out string error)
    {
        string? lastError = null;

        foreach (string candidate in GetEngineCandidatePaths(requestedEnginePath))
        {
            if (!File.Exists(candidate))
                continue;

            try
            {
                uint candidateRva = GetExportRva(candidate, InitExport);
                resolvedEnginePath = candidate;
                initRva = candidateRva;
                error = string.Empty;

                if (!string.Equals(Path.GetFullPath(requestedEnginePath), candidate, StringComparison.OrdinalIgnoreCase))
                    log($"Using native engine candidate: {candidate}");

                return true;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        resolvedEnginePath = Path.GetFullPath(requestedEnginePath);
        initRva = 0;
        error = lastError ?? $"Engine DLL not found: {requestedEnginePath}";
        return false;
    }

    public bool TryDescribeGraphicsReadiness(Process targetProcess, out string status)
    {
        try
        {
            targetProcess.Refresh();

            if (targetProcess.HasExited)
            {
                status = "Auto-inject wait: AC process exited before injection.";
                return false;
            }

            if (targetProcess.MainWindowHandle == IntPtr.Zero)
            {
                status = "Auto-inject wait: waiting for the AC game window.";
                return false;
            }

            if (!targetProcess.Responding)
            {
                status = "Auto-inject wait: waiting for the AC window to respond.";
                return false;
            }

            if (!IsModuleLoaded(targetProcess, "d3d9.dll"))
            {
                status = "Auto-inject wait: waiting for Direct3D 9 to initialize.";
                return false;
            }

            status = "Auto-inject wait: AC window and Direct3D 9 are ready.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Auto-inject wait: readiness probe failed ({ex.Message}).";
            return false;
        }
    }

    private static uint GetExportRva(string dllPath, string exportName)
    {
        byte[] pe = File.ReadAllBytes(dllPath);
        int peOffset = BitConverter.ToInt32(pe, 0x3C);

        if (pe[peOffset] != 'P' || pe[peOffset + 1] != 'E')
            throw new InvalidOperationException("Invalid PE signature.");

        int coffHeader = peOffset + 4;
        ushort numSections = BitConverter.ToUInt16(pe, coffHeader + 2);
        ushort optionalHeaderSize = BitConverter.ToUInt16(pe, coffHeader + 16);
        int optionalHeader = coffHeader + 20;

        ushort magic = BitConverter.ToUInt16(pe, optionalHeader);
        if (magic != 0x10B)
            throw new InvalidOperationException($"Expected x86 PE32 image, got 0x{magic:X4}.");

        int dataDirectoryOffset = optionalHeader + 96;
        uint exportDirRva = BitConverter.ToUInt32(pe, dataDirectoryOffset);
        if (exportDirRva == 0)
            throw new InvalidOperationException("No export directory found.");

        int sectionHeaders = optionalHeader + optionalHeaderSize;
        uint exportDirFileOffset = RvaToFileOffset(pe, sectionHeaders, numSections, exportDirRva);

        uint numberOfNames = BitConverter.ToUInt32(pe, (int)exportDirFileOffset + 24);
        uint addressOfFunctions = BitConverter.ToUInt32(pe, (int)exportDirFileOffset + 28);
        uint addressOfNames = BitConverter.ToUInt32(pe, (int)exportDirFileOffset + 32);
        uint addressOfNameOrdinals = BitConverter.ToUInt32(pe, (int)exportDirFileOffset + 36);

        uint namesFileOffset = RvaToFileOffset(pe, sectionHeaders, numSections, addressOfNames);
        uint ordinalsFileOffset = RvaToFileOffset(pe, sectionHeaders, numSections, addressOfNameOrdinals);
        uint functionsFileOffset = RvaToFileOffset(pe, sectionHeaders, numSections, addressOfFunctions);

        for (uint i = 0; i < numberOfNames; i++)
        {
            uint nameRva = BitConverter.ToUInt32(pe, (int)namesFileOffset + (int)(i * 4));
            uint nameOffset = RvaToFileOffset(pe, sectionHeaders, numSections, nameRva);

            int end = (int)nameOffset;
            while (end < pe.Length && pe[end] != 0)
                end++;

            string name = Encoding.ASCII.GetString(pe, (int)nameOffset, end - (int)nameOffset);
            if (!string.Equals(name, exportName, StringComparison.Ordinal))
                continue;

            ushort ordinalIndex = BitConverter.ToUInt16(pe, (int)ordinalsFileOffset + (int)(i * 2));
            return BitConverter.ToUInt32(pe, (int)functionsFileOffset + ordinalIndex * 4);
        }

        throw new InvalidOperationException($"Export '{exportName}' not found in {Path.GetFileName(dllPath)}.");
    }

    private static uint RvaToFileOffset(byte[] pe, int sectionHeaders, ushort numSections, uint rva)
    {
        for (int i = 0; i < numSections; i++)
        {
            int sectionHeader = sectionHeaders + (i * 40);
            uint virtualAddress = BitConverter.ToUInt32(pe, sectionHeader + 12);
            uint virtualSize = BitConverter.ToUInt32(pe, sectionHeader + 8);
            uint rawDataPointer = BitConverter.ToUInt32(pe, sectionHeader + 20);

            if (rva >= virtualAddress && rva < virtualAddress + virtualSize)
                return rva - virtualAddress + rawDataPointer;
        }

        throw new InvalidOperationException($"Cannot map RVA 0x{rva:X8} to file offset.");
    }

    private static bool IsModuleLoaded(Process targetProcess, string moduleName)
    {
        try
        {
            foreach (ProcessModule module in targetProcess.Modules)
            {
                if (string.Equals(module.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }
}

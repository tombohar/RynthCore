using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RynthCore.Injector;

public sealed class EngineInjectionService
{
    public const string DefaultAcProcessName = "acclient";
    /// <summary>The DLL we actually inject — RynthCore.Loader.dll, which then
    /// loads RynthCore.Engine.dll and can later FreeLibrary + reload it.</summary>
    public const string EngineDllName = "RynthCore.Loader.dll";
    /// <summary>Legacy filename — if the user's saved path points to this, we
    /// transparently redirect to the Loader sibling in the same directory.</summary>
    private const string LegacyEngineDllName = "RynthCore.Engine.dll";
    private const string InitExport = "RynthCoreInit";
    private const string DecalInitExport = "DecalStartup";
    private const string RuntimeDirectoryName = "Runtime";

    private const uint ProcessAllAccess = 0x001F0FFF;
    private const uint CreateSuspended = 0x00000004;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const uint StillActive = 259;

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        System.Text.StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetModuleHandleA(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

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

        return InjectDllAndCallExport(
            targetProcess,
            resolvedEnginePath,
            initRva,
            InitExport,
            stackLabel: "RynthCore",
            successMessage: "RynthCore injected successfully.",
            log);
    }

    /// <summary>
    /// Injects a generic DLL into <paramref name="targetProcess"/> via remote
    /// LoadLibraryA, then calls the export at <paramref name="exportRva"/> as a
    /// new thread (entry signature: <c>uint __stdcall(void)</c> — matches
    /// LPTHREAD_START_ROUTINE). Used by both the engine path and the Decal path.
    /// </summary>
    private InjectionResult InjectDllAndCallExport(
        Process targetProcess,
        string dllPath,
        uint exportRva,
        string exportName,
        string stackLabel,
        string successMessage,
        Action<string> log)
    {
        log($"{exportName} RVA: 0x{exportRva:X8}");
        log($"Target PID: {targetProcess.Id} ({targetProcess.ProcessName})");

        IntPtr hProcess = OpenProcess(ProcessAllAccess, false, targetProcess.Id);
        if (hProcess == IntPtr.Zero)
        {
            return InjectionResult.Failure(
                1,
                $"OpenProcess failed (error {Marshal.GetLastWin32Error()}). Try running as Administrator.",
                dllPath,
                targetProcess.Id);
        }

        try
        {
            IntPtr hKernel32 = GetModuleHandleA("kernel32.dll");
            IntPtr loadLibAddr = GetProcAddress(hKernel32, "LoadLibraryA");
            if (loadLibAddr == IntPtr.Zero)
                return InjectionResult.Failure(1, "Could not locate LoadLibraryA.", dllPath, targetProcess.Id);

            byte[] dllPathBytes = Encoding.ASCII.GetBytes(dllPath + "\0");
            IntPtr remoteStr = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)dllPathBytes.Length, MemCommit | MemReserve, PageReadWrite);
            if (remoteStr == IntPtr.Zero)
                return InjectionResult.Failure(1, "VirtualAllocEx failed for remote DLL path.", dllPath, targetProcess.Id);

            try
            {
                if (!WriteProcessMemory(hProcess, remoteStr, dllPathBytes, (uint)dllPathBytes.Length, out _))
                {
                    return InjectionResult.Failure(
                        1,
                        $"WriteProcessMemory failed (error {Marshal.GetLastWin32Error()}).",
                        dllPath,
                        targetProcess.Id);
                }

                log($"[1/2] Injecting {stackLabel} DLL...");
                IntPtr loadThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, loadLibAddr, remoteStr, 0, out _);
                if (loadThread == IntPtr.Zero)
                {
                    return InjectionResult.Failure(
                        1,
                        $"CreateRemoteThread for LoadLibraryA failed (error {Marshal.GetLastWin32Error()}).",
                        dllPath,
                        targetProcess.Id);
                }

                uint loadLibResult;
                try
                {
                    // Deep-audit finding #6 (2026-06-18): the wait's return value was
                    // discarded. On a timeout (WAIT_TIMEOUT, the thread still running
                    // LoadLibraryA) GetExitCodeThread returns STILL_ACTIVE (259) — that
                    // was then cast straight to remoteBase and handed to a second
                    // CreateRemoteThread as a call target, i.e. jumping into address
                    // 0x103 + rva inside the target process. Fail closed on anything
                    // but a real WAIT_OBJECT_0 signal instead of falling through.
                    uint waitResult = WaitForSingleObject(loadThread, 10000);
                    if (waitResult != WaitObject0)
                    {
                        string reason = waitResult == WaitTimeout
                            ? "timed out after 10s"
                            : $"wait failed (result 0x{waitResult:X8}, error {Marshal.GetLastWin32Error()})";
                        return InjectionResult.Failure(
                            1,
                            $"LoadLibraryA remote thread {reason} — aborting rather than treating an unfinished call as success.",
                            dllPath,
                            targetProcess.Id);
                    }
                    GetExitCodeThread(loadThread, out loadLibResult);
                    if (loadLibResult == StillActive)
                    {
                        // Belt-and-suspenders: even a WAIT_OBJECT_0 signal on a handle
                        // that GetExitCodeThread still reports as STILL_ACTIVE must not
                        // be treated as a valid module base.
                        return InjectionResult.Failure(
                            1,
                            "LoadLibraryA remote thread signaled but exit code is STILL_ACTIVE — refusing to use it as a module base.",
                            dllPath,
                            targetProcess.Id);
                    }
                }
                finally
                {
                    CloseHandle(loadThread);
                }

                if (loadLibResult == 0)
                {
                    return InjectionResult.Failure(
                        1,
                        $"LoadLibrary returned NULL for {Path.GetFileName(dllPath)}. Check its dependencies beside the DLL.",
                        dllPath,
                        targetProcess.Id);
                }

                IntPtr remoteBase = (IntPtr)loadLibResult;
                log($"[1/2] {stackLabel} mapped at 0x{remoteBase:X8}");

                log($"[2/2] Calling {exportName}...");
                IntPtr remoteInitAddr = IntPtr.Add(remoteBase, (int)exportRva);
                IntPtr initThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, remoteInitAddr, IntPtr.Zero, 0, out _);
                if (initThread == IntPtr.Zero)
                {
                    return InjectionResult.Failure(
                        1,
                        $"CreateRemoteThread for {exportName} failed (error {Marshal.GetLastWin32Error()}).",
                        dllPath,
                        targetProcess.Id);
                }

                uint initResult;
                try
                {
                    uint waitResult = WaitForSingleObject(initThread, 10000);
                    if (waitResult != WaitObject0)
                    {
                        string reason = waitResult == WaitTimeout
                            ? "timed out after 10s"
                            : $"wait failed (result 0x{waitResult:X8}, error {Marshal.GetLastWin32Error()})";
                        return InjectionResult.Failure(
                            1,
                            $"{exportName} remote thread {reason} — the DLL is loaded but init did not confirm completion.",
                            dllPath,
                            targetProcess.Id);
                    }
                    GetExitCodeThread(initThread, out initResult);
                    if (initResult == StillActive)
                    {
                        return InjectionResult.Failure(
                            1,
                            $"{exportName} remote thread signaled but exit code is STILL_ACTIVE — refusing to trust the result.",
                            dllPath,
                            targetProcess.Id);
                    }
                }
                finally
                {
                    CloseHandle(initThread);
                }

                log(initResult == 0 ? $"{exportName} returned success." : $"{exportName} returned {initResult}.");
                return InjectionResult.SuccessResult(successMessage, dllPath, targetProcess.Id, initResult);
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

    /// <summary>
    /// Injects Decal's <c>Inject.dll</c> into the target and invokes its
    /// <c>DecalStartup</c> export — the same recipe ThwargLauncher's bundled
    /// injector.dll uses. Resolves the DecalStartup RVA from the on-disk PE.
    /// </summary>
    public InjectionResult InjectDecalIntoProcess(Process targetProcess, string decalInjectDllPath, Action<string>? log = null)
    {
        log ??= _ => { };

        if (string.IsNullOrWhiteSpace(decalInjectDllPath) || !File.Exists(decalInjectDllPath))
            return InjectionResult.Failure(1, $"Decal Inject.dll not found: {decalInjectDllPath}", decalInjectDllPath, targetProcess.Id);

        log($"Decal DLL: {decalInjectDllPath}");

        uint decalStartupRva;
        try
        {
            decalStartupRva = GetExportRva(decalInjectDllPath, DecalInitExport);
        }
        catch (Exception ex)
        {
            return InjectionResult.Failure(
                1,
                $"Could not locate {DecalInitExport} export in Decal Inject.dll: {ex.Message}",
                decalInjectDllPath,
                targetProcess.Id);
        }

        return InjectDllAndCallExport(
            targetProcess,
            decalInjectDllPath,
            decalStartupRva,
            DecalInitExport,
            stackLabel: "Decal",
            successMessage: "Decal injected successfully.",
            log);
    }

    public InjectionResult LaunchSuspendedAndInject(
        string clientPath,
        string arguments,
        string enginePath,
        Action<string>? log = null,
        Action<int>? onProcessCreated = null)
    {
        return LaunchSuspendedAndInvoke(
            clientPath,
            arguments,
            stackLabel: "RynthCore",
            failureContextPath: enginePath,
            successMessage: "Launched AC and injected RynthCore successfully.",
            inject: (proc, perCallLog) => InjectIntoProcess(proc, enginePath, perCallLog),
            log,
            onProcessCreated);
    }

    /// <summary>
    /// Launches AC suspended and injects Decal's <c>Inject.dll</c> + invokes
    /// <c>DecalStartup</c>. The RynthCore engine is NOT loaded into the
    /// process — this account runs against Decal's plugin stack alone, the
    /// same way ThwargLauncher launches Decal-using accounts.
    /// </summary>
    public InjectionResult LaunchSuspendedAndInjectDecal(
        string clientPath,
        string arguments,
        string decalInjectDllPath,
        Action<string>? log = null,
        Action<int>? onProcessCreated = null)
    {
        return LaunchSuspendedAndInvoke(
            clientPath,
            arguments,
            stackLabel: "Decal",
            failureContextPath: decalInjectDllPath,
            successMessage: "Launched AC and injected Decal successfully.",
            inject: (proc, perCallLog) => InjectDecalIntoProcess(proc, decalInjectDllPath, perCallLog),
            log,
            onProcessCreated);
    }

    private InjectionResult LaunchSuspendedAndInvoke(
        string clientPath,
        string arguments,
        string stackLabel,
        string failureContextPath,
        string successMessage,
        Func<Process, Action<string>, InjectionResult> inject,
        Action<string>? log,
        Action<int>? onProcessCreated)
    {
        log ??= _ => { };

        if (string.IsNullOrWhiteSpace(clientPath) || !File.Exists(clientPath))
            return InjectionResult.Failure(1, $"AC client not found: {clientPath}", failureContextPath);

        string workingDirectory = Path.GetDirectoryName(clientPath) ?? Environment.CurrentDirectory;
        string commandLine = string.IsNullOrWhiteSpace(arguments)
            ? $"\"{clientPath}\""
            : $"\"{clientPath}\" {arguments}";

        var startupInfo = new STARTUPINFO
        {
            cb = (uint)Marshal.SizeOf<STARTUPINFO>()
        };

        if (!CreateProcessW(
                clientPath,
                new System.Text.StringBuilder(commandLine),
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CreateSuspended,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out PROCESS_INFORMATION processInfo))
        {
            return InjectionResult.Failure(
                1,
                $"CreateProcessW failed (error {Marshal.GetLastWin32Error()}).",
                failureContextPath);
        }

        bool resumed = false;

        void ResumeMainThread()
        {
            if (resumed)
                return;

            uint resumeResult = ResumeThread(processInfo.hThread);
            if (resumeResult == 0xFFFFFFFF)
                throw new InvalidOperationException($"ResumeThread failed (error {Marshal.GetLastWin32Error()}).");

            resumed = true;
            log($"Resumed AC main thread (PID {processInfo.dwProcessId}).");
        }

        log($"Launched AC suspended (PID {processInfo.dwProcessId}).");
        log($"Launching suspended so {stackLabel} can install before AC's single-instance gate runs.");
        onProcessCreated?.Invoke((int)processInfo.dwProcessId);

        try
        {
            using var launchedProcess = Process.GetProcessById((int)processInfo.dwProcessId);
            InjectionResult injectResult = inject(launchedProcess, log);
            ResumeMainThread();

            if (!injectResult.Success)
                return InjectionResult.Failure(injectResult.ExitCode, injectResult.Summary, injectResult.EnginePath, (int)processInfo.dwProcessId);

            return InjectionResult.SuccessResult(
                successMessage,
                injectResult.EnginePath,
                (int)processInfo.dwProcessId,
                injectResult.InitResult ?? 0);
        }
        catch (Exception ex)
        {
            try
            {
                ResumeMainThread();
            }
            catch (Exception resumeEx)
            {
                return InjectionResult.Failure(
                    1,
                    $"Launch + inject failed: {ex.Message} ResumeThread also failed: {resumeEx.Message}",
                    failureContextPath,
                    (int)processInfo.dwProcessId);
            }

            return InjectionResult.Failure(
                1,
                $"Launch + inject failed: {ex.Message}",
                failureContextPath,
                (int)processInfo.dwProcessId);
        }
        finally
        {
            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
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

        void AddEngineDirectoryCandidates(List<string> candidates, string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return;

            AddCandidate(candidates, Path.Combine(directory, EngineDllName));
            AddCandidate(candidates, Path.Combine(directory, "publish", EngineDllName));
            AddCandidate(candidates, Path.Combine(directory, "native", EngineDllName));
            AddCandidate(candidates, Path.Combine(directory, "Native", EngineDllName));
        }

        void AddLauncherLayoutCandidates(List<string> candidates, string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return;

            AddEngineDirectoryCandidates(candidates, directory);
            AddEngineDirectoryCandidates(candidates, Path.Combine(directory, RuntimeDirectoryName));
            AddEngineDirectoryCandidates(candidates, Path.Combine(directory, RuntimeDirectoryName, "Engine"));
        }

        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(explicitEnginePath))
        {
            string explicitFullPath = Path.GetFullPath(explicitEnginePath);

            // If the user's saved path points to the legacy engine DLL, prefer
            // the Loader sibling so we get the hot-reload-capable injection
            // path. Falls through to the explicit path if the Loader isn't
            // beside it.
            if (string.Equals(Path.GetFileName(explicitFullPath), LegacyEngineDllName, StringComparison.OrdinalIgnoreCase))
            {
                string? legacyDir = Path.GetDirectoryName(explicitFullPath);
                if (!string.IsNullOrWhiteSpace(legacyDir))
                    AddCandidate(candidates, Path.Combine(legacyDir, EngineDllName));
            }

            string? runtimeSibling = TryGetRuntimeSiblingEnginePath(explicitFullPath);
            AddCandidate(candidates, runtimeSibling);
            AddCandidate(candidates, explicitFullPath);

            string? explicitDir = Path.GetDirectoryName(explicitFullPath);
            if (!string.IsNullOrWhiteSpace(explicitDir))
            {
                AddLauncherLayoutCandidates(candidates, explicitDir);

                string? parentDir = Directory.GetParent(explicitDir)?.FullName;
                if (!string.IsNullOrWhiteSpace(parentDir))
                    AddLauncherLayoutCandidates(candidates, parentDir);
            }
        }

        string cwd = Environment.CurrentDirectory;
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;

        AddLauncherLayoutCandidates(candidates, baseDir);
        AddLauncherLayoutCandidates(candidates, exeDir);
        AddLauncherLayoutCandidates(candidates, cwd);
        AddEngineDirectoryCandidates(candidates, Path.Combine(cwd, "src", "RynthCore.Engine", "bin", "x86", "Release", "net9.0-windows", "win-x86"));
        AddEngineDirectoryCandidates(candidates, Path.Combine(cwd, "src", "RynthCore.Engine", "bin", "Release", "net9.0-windows", "win-x86"));
        AddEngineDirectoryCandidates(candidates, Path.Combine(baseDir, "..", "..", "..", "..", "RynthCore.Engine", "bin", "x86", "Release", "net9.0-windows", "win-x86"));
        AddEngineDirectoryCandidates(candidates, Path.Combine(baseDir, "..", "..", "..", "..", "RynthCore.Engine", "bin", "Release", "net9.0-windows", "win-x86"));
        AddEngineDirectoryCandidates(candidates, Path.Combine(baseDir, "..", "RynthCore.Engine", "bin", "Release", "net9.0-windows", "win-x86"));

        return candidates;
    }

    private static string? TryGetRuntimeSiblingEnginePath(string enginePath)
    {
        if (!string.Equals(Path.GetFileName(enginePath), EngineDllName, StringComparison.OrdinalIgnoreCase))
            return null;

        string? engineDir = Path.GetDirectoryName(enginePath);
        if (string.IsNullOrWhiteSpace(engineDir))
            return null;

        if (string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(engineDir)), RuntimeDirectoryName, StringComparison.OrdinalIgnoreCase))
            return null;

        string runtimeSibling = Path.Combine(engineDir, RuntimeDirectoryName, EngineDllName);
        return File.Exists(runtimeSibling) ? runtimeSibling : null;
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

            if (!IsWindowResponding(targetProcess.MainWindowHandle))
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

    // Bounded replacement for Process.Responding, which is
    // SendMessageTimeout(WM_NULL, ..., SMTO_ABORTIFHUNG, 5000) under the hood —
    // up to a 5s stall per probe on a busy-but-not-yet-ghosted client. Callers
    // poll readiness on the launcher UI thread every 2s per client, so that 5s
    // window can wedge the whole launcher behind one slow AC client. 250ms
    // bounds it; a client that can't answer WM_NULL in 250ms isn't ready yet.
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    private static bool IsWindowResponding(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        return SendMessageTimeoutW(hwnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 250, out _) != IntPtr.Zero;
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

    /// <summary>Names that signal a Decal-style coexistence stack is loaded
    /// inside the target acclient.exe. Match is case-insensitive.</summary>
    private static readonly string[] DecalStackModuleNames =
    {
        "decal.dll",
        "UBLoader.dll",
        "Decal.Adapter.dll",
        "phatacd.dll",
    };

    /// <summary>Names that signal RynthCore is already mapped into the target,
    /// so the watcher should treat the process as already-injected.</summary>
    private static readonly string[] RynthCoreModuleNames =
    {
        "RynthCore.Loader.dll",
        "RynthCore.Engine.dll",
    };

    public bool IsRynthCoreLoaded(Process targetProcess)
    {
        foreach (string name in RynthCoreModuleNames)
        {
            if (IsModuleLoaded(targetProcess, name))
                return true;
        }

        return false;
    }

    /// <summary>Inspects loaded modules in the target acclient.exe and returns
    /// the first Decal-stack module name found, or null if none are present.
    /// Informational only — used to log when we're attaching alongside Decal.</summary>
    public string? TryDetectDecalStack(Process targetProcess)
    {
        foreach (string name in DecalStackModuleNames)
        {
            if (IsModuleLoaded(targetProcess, name))
                return name;
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }
}

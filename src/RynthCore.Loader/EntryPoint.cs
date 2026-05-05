// ============================================================================
//  RynthCore.Loader - EntryPoint.cs
//  Tiny shim DLL injected into acclient.exe instead of RynthCore.Engine.
//  Owns the engine module handle so we can FreeLibrary + swap files + reload.
//
//  Phase 1: pure passthrough — load engine, call its init.                ✅
//  Phase 2: engine RynthCoreShutdown export.                              ✅
//  Phase 3: reload command — shutdown engine, FreeLibrary, LoadLibrary,
//           re-init. Triggered by signaling a named event the loader
//           waits on in a dedicated thread.                               ✅
//  Phase 4: file-watcher auto-reload — poll the canonical engine DLL's
//           last-write timestamp and signal reload when it changes. The
//           canonical file is never LoadLibrary'd directly (we always
//           stage to .engine_loads/), so the developer can `cp` over it
//           without locking errors.
// ============================================================================

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Loader;

public static class EntryPoint
{
    private const string EngineDllName = "RynthCore.Engine.dll";
    private const string EngineInitExport = "RynthCoreEngineInit";
    private const string EngineShutdownExport = "RynthCoreShutdown";

    // Unified log path — must stay in sync with RynthCore.Engine.LogPaths.
    // The loader cannot reference the engine assembly (it loads it
    // dynamically), so the constants are mirrored here.
    private const string UnifiedLogDirectory = @"C:\Games\RynthCore\Logs";
    private const string UnifiedLogFileName = "RynthCore.log";
    /// <summary>
    /// Subdirectory beside the loader where reload-generation copies of the
    /// engine DLL live. Each reload copies the canonical engine DLL to a
    /// unique filename here so Windows treats it as a fresh module — the
    /// `FreeLibrary` + `LoadLibrary` path leaves stale static state in place
    /// (NativeAOT keeps the DLL pinned via lingering threads), so we sidestep
    /// the OS module cache by varying the filename.
    /// </summary>
    private const string ReloadStagingSubdir = ".engine_loads";

    /// <summary>
    /// Named auto-reset event the engine signals to request a full reload.
    /// "Local\\" prefix scopes it to the current logon session — exactly the
    /// AC process — so cross-process collisions can't occur.
    /// </summary>
    private const string ReloadEventName = "Local\\RynthCore.Engine.RequestReload";

    private static int _initialized;
    private static IntPtr _engineModule;
    private static string? _enginePath;
    private static readonly object Sync = new();
    private static Thread? _reloadWatcher;
    private static Thread? _fileWatcher;
    private static IntPtr _reloadEvent = IntPtr.Zero;
    private static volatile bool _reloadWatcherShouldRun = true;
    /// <summary>1 on first init, 2+ on each reload. Passed to the engine's
    /// init export as lpParam so it can take the "skip the LoginComplete-gated
    /// D3D9 deferral" path on reloads.</summary>
    private static int _engineInitCount;
    /// <summary>Last-write timestamp the file watcher saw. New value triggers reload.</summary>
    private static DateTime _canonicalLastWriteUtc;

    [UnmanagedCallersOnly(EntryPoint = "RynthCoreInit")]
    public static uint Initialize(IntPtr lpParam)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
        {
            Log("RynthCoreInit called twice — ignoring second call.");
            return 1;
        }

        try
        {
            Log("RynthCoreInit (loader) starting.");

            string? loaderDir = GetLoaderDirectory();
            if (loaderDir == null)
            {
                Log("FATAL: Could not determine loader directory.");
                return 2;
            }

            Log($"Loader directory: {loaderDir}");

            string enginePath = Path.Combine(loaderDir, EngineDllName);
            if (!File.Exists(enginePath))
            {
                Log($"FATAL: Engine DLL not found at {enginePath}");
                return 3;
            }

            _enginePath = enginePath;

            uint rc = LoadAndInitEngine();
            if (rc != 0)
                return rc;

            StartReloadWatcher();
            StartFileWatcher();
            return 0;
        }
        catch (Exception ex)
        {
            Log($"FATAL in loader RynthCoreInit: {ex}");
            return 99;
        }
    }

    private static uint LoadAndInitEngine()
    {
        if (string.IsNullOrEmpty(_enginePath))
        {
            Log("FATAL: engine path not resolved.");
            return 10;
        }

        _engineInitCount++;

        // Always stage to a unique filename. Two reasons:
        //   1) On reloads, Windows otherwise reuses the still-pinned module
        //      image and stale statics survive across FreeLibrary.
        //   2) Even on first load, leaving the canonical file unlocked lets
        //      the developer `cp newengine.dll .../Runtime/RynthCore.Engine.dll`
        //      without sharing-violation errors — the file watcher then auto
        //      triggers a reload from the new bytes.
        string? loadPath = StageReloadCopy(_engineInitCount);
        if (string.IsNullOrEmpty(loadPath))
        {
            Log("FATAL: failed to stage engine reload copy.");
            return 11;
        }

        // Snapshot the canonical's last-write so the watcher only fires on
        // a *new* copy after init, not on the file we just staged from.
        try
        {
            _canonicalLastWriteUtc = File.GetLastWriteTimeUtc(_enginePath);
        }
        catch
        {
            _canonicalLastWriteUtc = DateTime.UtcNow;
        }

        IntPtr engineModule;
        lock (Sync)
        {
            engineModule = LoadLibraryW(loadPath);
        }

        if (engineModule == IntPtr.Zero)
        {
            Log($"FATAL: LoadLibraryW failed for engine (path={loadPath}, error {Marshal.GetLastWin32Error()})");
            return 4;
        }

        Log($"Engine module loaded at 0x{engineModule:X8} from {loadPath}");

        IntPtr initAddr = GetProcAddress(engineModule, EngineInitExport);
        if (initAddr == IntPtr.Zero)
        {
            Log($"FATAL: Engine missing export '{EngineInitExport}' (error {Marshal.GetLastWin32Error()})");
            return 5;
        }

        _engineModule = engineModule;

        IntPtr initParam = (IntPtr)_engineInitCount;

        var init = Marshal.GetDelegateForFunctionPointer<EngineInitFn>(initAddr);
        uint rc = init(initParam);
        Log($"{EngineInitExport}(initCount={_engineInitCount}) returned {rc}.");
        return rc;
    }

    private static string? StageReloadCopy(int generation)
    {
        try
        {
            string canonicalPath = _enginePath!;
            string canonicalDir = Path.GetDirectoryName(canonicalPath)!;
            string stagingDir = Path.Combine(canonicalDir, ReloadStagingSubdir);
            Directory.CreateDirectory(stagingDir);

            string stagedPath = Path.Combine(stagingDir, $"RynthCore.Engine.gen{generation}.dll");
            File.Copy(canonicalPath, stagedPath, overwrite: true);

            // Skia/HarfBuzz/cimgui are loaded from the canonical Runtime
            // directory via the engine's PreloadNativeDll resolver and stay
            // resident across reloads, so we don't need to copy them too.

            Log($"Staged engine for gen {generation} at {stagedPath}");
            return stagedPath;
        }
        catch (Exception ex)
        {
            Log($"StageReloadCopy: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static void StartFileWatcher()
    {
        if (_fileWatcher != null) return;

        _fileWatcher = new Thread(FileWatcherLoop)
        {
            Name = "RynthCore.Loader.FileWatcher",
            IsBackground = true
        };
        _fileWatcher.Start();
        Log($"File watcher armed on '{_enginePath}' (poll 1s).");
    }

    private static void FileWatcherLoop()
    {
        const int PollIntervalMs = 1000;
        // Briefly settle after a detected change so the writer (cp/dotnet
        // publish) has finished flushing. Otherwise we'd race the copy and
        // either read a partial DLL or hit a sharing violation when staging.
        const int SettleMs = 500;

        while (_reloadWatcherShouldRun)
        {
            Thread.Sleep(PollIntervalMs);
            if (!_reloadWatcherShouldRun) return;

            try
            {
                DateTime current = File.GetLastWriteTimeUtc(_enginePath!);
                if (current <= _canonicalLastWriteUtc)
                    continue;

                // Wait for the timestamp to stabilize for one poll interval —
                // means the writer has finished copying.
                Thread.Sleep(SettleMs);
                DateTime stable = File.GetLastWriteTimeUtc(_enginePath!);
                if (stable != current)
                    continue; // still being written, retry next poll

                Log($"FileWatcher: canonical engine DLL changed ({_canonicalLastWriteUtc:o} → {stable:o}) — signaling reload.");
                _canonicalLastWriteUtc = stable;

                if (_reloadEvent != IntPtr.Zero)
                    SetEvent(_reloadEvent);
            }
            catch (Exception ex)
            {
                Log($"FileWatcher: poll threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(IntPtr hEvent);

    private static void StartReloadWatcher()
    {
        if (_reloadWatcher != null) return;

        _reloadEvent = CreateEventW(IntPtr.Zero, /*bManualReset*/ false, /*bInitialState*/ false, ReloadEventName);
        if (_reloadEvent == IntPtr.Zero)
        {
            Log($"WARN: CreateEventW failed (error {Marshal.GetLastWin32Error()}); reload disabled.");
            return;
        }

        _reloadWatcher = new Thread(ReloadWatcherLoop)
        {
            Name = "RynthCore.Loader.ReloadWatcher",
            IsBackground = true
        };
        _reloadWatcher.Start();
        Log($"Reload watcher armed on '{ReloadEventName}'.");
    }

    private static void ReloadWatcherLoop()
    {
        while (_reloadWatcherShouldRun)
        {
            uint result;
            try
            {
                result = WaitForSingleObject(_reloadEvent, INFINITE);
            }
            catch (Exception ex)
            {
                Log($"ReloadWatcher: wait threw {ex.GetType().Name}: {ex.Message} — exiting.");
                return;
            }

            if (!_reloadWatcherShouldRun)
                return;

            if (result != WAIT_OBJECT_0)
            {
                Log($"ReloadWatcher: unexpected wait result 0x{result:X} — looping.");
                continue;
            }

            try
            {
                Reload();
            }
            catch (Exception ex)
            {
                Log($"ReloadWatcher: Reload threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void Reload()
    {
        Log("Reload requested.");

        if (_engineModule == IntPtr.Zero)
        {
            Log("Reload: no engine module currently loaded — re-initializing.");
            uint rc = LoadAndInitEngine();
            Log(rc == 0 ? "Reload: re-init succeeded." : $"Reload: re-init failed rc={rc}");
            return;
        }

        // Step 1: ask the engine to tear itself down.
        IntPtr shutdownAddr = GetProcAddress(_engineModule, EngineShutdownExport);
        if (shutdownAddr != IntPtr.Zero)
        {
            try
            {
                var shutdown = Marshal.GetDelegateForFunctionPointer<EngineShutdownFn>(shutdownAddr);
                uint rc = shutdown(IntPtr.Zero);
                Log($"{EngineShutdownExport} returned {rc}.");
            }
            catch (Exception ex)
            {
                Log($"Reload: engine shutdown threw {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            Log($"Reload: engine has no '{EngineShutdownExport}' export — skipping graceful shutdown.");
        }

        // Step 2: brief drain so any post-shutdown threads finish exiting
        // before we yank the address space out from under them.
        Thread.Sleep(150);

        // Step 3: FreeLibrary the current engine.
        IntPtr oldModule;
        lock (Sync)
        {
            oldModule = _engineModule;
            _engineModule = IntPtr.Zero;
        }

        if (oldModule != IntPtr.Zero)
        {
            bool freed = FreeLibrary(oldModule);
            Log($"FreeLibrary(0x{oldModule:X8}) = {freed}");
        }

        // Step 4: re-load the engine from disk and call init.
        uint reinitRc = LoadAndInitEngine();
        Log(reinitRc == 0 ? "Reload: complete." : $"Reload: re-init failed rc={reinitRc}");
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint EngineInitFn(IntPtr lpParam);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint EngineShutdownFn(IntPtr lpParam);

    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_OBJECT_0 = 0x00000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetModuleHandleA(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(IntPtr hModule, char[] lpFilename, uint nSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEventW(
        IntPtr lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
        string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    private static string? GetLoaderDirectory()
    {
        IntPtr hLoader = GetModuleHandleA("RynthCore.Loader.dll");
        if (hLoader == IntPtr.Zero)
            return null;

        var buffer = new char[512];
        uint length = GetModuleFileNameW(hLoader, buffer, (uint)buffer.Length);
        if (length == 0)
            return null;

        return Path.GetDirectoryName(new string(buffer, 0, (int)length));
    }

    private static readonly object LogLock = new();

    private static void Log(string message)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [pid:{Environment.ProcessId}] [loader] {message}\r\n";
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line);

            lock (LogLock)
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    try
                    {
                        Directory.CreateDirectory(UnifiedLogDirectory);
                        using var fs = new FileStream(
                            Path.Combine(UnifiedLogDirectory, UnifiedLogFileName),
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite);
                        fs.Write(bytes, 0, bytes.Length);
                        return;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(5);
                    }
                    catch
                    {
                        return;
                    }
                }
            }
        }
        catch
        {
        }
    }
}

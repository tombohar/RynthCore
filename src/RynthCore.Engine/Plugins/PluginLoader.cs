// ============================================================================
//  RynthCore.Engine - Plugins/PluginLoader.cs
//  Scans the Plugins directory, loads each DLL, and resolves exports.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Plugins;

internal static class PluginLoader
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    /// <summary>
    /// Scans <paramref name="pluginsDir"/> for *.dll files, loads each one,
    /// and resolves the plugin contract exports.
    /// Returns only plugins that have the two required exports (Init + Shutdown).
    /// </summary>
    public static List<LoadedPlugin> LoadAll(string pluginsDir, string shadowRootDir, int generation)
    {
        var plugins = new List<LoadedPlugin>();

        if (!Directory.Exists(pluginsDir))
        {
            RynthLog.Verbose($"PluginLoader: Creating plugins directory: {pluginsDir}");
            try { Directory.CreateDirectory(pluginsDir); }
            catch (Exception ex)
            {
                RynthLog.Plugin($"PluginLoader: Failed to create directory: {ex.Message}");
                return plugins;
            }
            return plugins;
        }

        string sessionShadowDir = PrepareShadowDirectory(shadowRootDir, generation);

        string[] dllFiles;
        try { dllFiles = Directory.GetFiles(pluginsDir, "*.dll"); }
        catch (Exception ex)
        {
            RynthLog.Plugin($"PluginLoader: Failed to enumerate {pluginsDir}: {ex.Message}");
            return plugins;
        }

        Array.Sort(dllFiles, StringComparer.OrdinalIgnoreCase);

        if (dllFiles.Length == 0)
        {
            RynthLog.Verbose("PluginLoader: No plugin DLLs found.");
            return plugins;
        }

        RynthLog.Verbose($"PluginLoader: Found {dllFiles.Length} DLL(s) in {pluginsDir}");

        foreach (string dllPath in dllFiles)
        {
            var plugin = TryLoad(dllPath, sessionShadowDir);
            if (plugin != null)
                plugins.Add(plugin);
        }

        RynthLog.Verbose($"PluginLoader: {plugins.Count} plugin(s) loaded successfully.");
        return plugins;
    }

    /// <summary>
    /// Unloads a plugin. <b>Does NOT call FreeLibrary</b> — NativeAOT plugin DLLs
    /// share the .NET runtime with the host engine, and their <c>DLL_PROCESS_DETACH</c>
    /// path tears down NativeAOT runtime state in ways that race with our other
    /// threads (TickPump, Avalonia STA, hooks) and produce a deterministic
    /// <c>ntdll!RtlpHeap</c> AV at addr ending in <c>0xFFC</c> on hot-reload + manual
    /// exit. The module handle is intentionally leaked; on next reload, a new
    /// shadow copy at a different path is loaded so the new plugin code runs
    /// cleanly. The OS reclaims the leaked module pages when the process exits.
    /// </summary>
    public static void Unload(LoadedPlugin plugin)
    {
        if (plugin.ModuleHandle != IntPtr.Zero)
        {
            RynthLog.Verbose($"PluginLoader: Skipping FreeLibrary on {plugin.FileName} (NativeAOT — module pages leaked intentionally).");
        }

        // Don't try to delete the .dll/.pdb either — they're still mapped.
        // Old shadow copies get cleaned up on next launch via CleanupShadowCopies.
        TryDeleteEmptyDirectory(Path.GetDirectoryName(plugin.FilePath));
    }

    public static void CleanupShadowCopies(string shadowRootDir)
    {
        if (!Directory.Exists(shadowRootDir))
            return;

        try
        {
            foreach (string dir in Directory.GetDirectories(shadowRootDir))
                TryDeleteDirectory(dir);
        }
        catch (Exception ex)
        {
            RynthLog.Verbose($"PluginLoader: Shadow cleanup skipped ({ex.Message})");
        }
    }

    /// <summary>
    /// One-shot startup sweep over sibling per-PID shadow roots
    /// (<c>&lt;baseShadowRootDir&gt;/p&lt;PID&gt;/</c>). Deletes dirs whose
    /// owning PID is dead — i.e. orphans from previous client runs that
    /// crashed or were killed before tearing down their shadow tree.
    ///
    /// Mutex-serialized across processes: two clients launching concurrently
    /// must not race on the same dead-PID dirs, because Directory.Delete
    /// failures (already-deleted-by-other-client → DirectoryNotFoundException)
    /// generate exception heap pressure, which is exactly the failure mode
    /// the per-PID shadow-root design is meant to eliminate.
    /// </summary>
    public static void CleanupOrphanShadowRoots(string baseShadowRootDir, int currentPid)
    {
        RynthLog.Plugin($"PluginLoader: orphan sweep starting (root={baseShadowRootDir}, currentPid={currentPid}).");

        if (!Directory.Exists(baseShadowRootDir))
        {
            RynthLog.Plugin("PluginLoader: orphan sweep — base dir does not exist, nothing to do.");
            return;
        }

        RynthLog.Plugin("PluginLoader: orphan sweep DIAG-1 — base dir exists, creating mutex.");

        System.Threading.Mutex? mutex = null;
        bool acquired = false;
        int deleted = 0;
        int skippedAlive = 0;
        try
        {
            try
            {
                mutex = new System.Threading.Mutex(false, "Local\\RynthCore.PluginShadow.OrphanSweep");
            }
            catch (Exception ex)
            {
                RynthLog.Plugin($"PluginLoader: orphan sweep — mutex create threw {ex.GetType().Name}: {ex.Message}. Proceeding without serialization.");
            }

            RynthLog.Plugin($"PluginLoader: orphan sweep DIAG-2 — mutex created (null={mutex == null}).");

            if (mutex != null)
            {
                RynthLog.Plugin("PluginLoader: orphan sweep DIAG-3 — calling mutex.WaitOne(5s).");
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (System.Threading.AbandonedMutexException) { acquired = true; }
                catch (Exception ex)
                {
                    RynthLog.Plugin($"PluginLoader: orphan sweep — mutex wait threw {ex.GetType().Name}: {ex.Message}. Proceeding without serialization.");
                }
                RynthLog.Plugin($"PluginLoader: orphan sweep DIAG-4 — mutex.WaitOne returned, acquired={acquired}.");
                if (!acquired)
                    RynthLog.Plugin("PluginLoader: orphan sweep — mutex not acquired within 5s; proceeding without serialization.");
            }

            RynthLog.Plugin("PluginLoader: orphan sweep DIAG-5 — calling Directory.GetDirectories.");
            string[] dirs = Directory.GetDirectories(baseShadowRootDir);
            RynthLog.Plugin($"PluginLoader: orphan sweep DIAG-6 — got {dirs.Length} dirs.");

            for (int i = 0; i < dirs.Length; i++)
            {
                string dir = dirs[i];
                string name = Path.GetFileName(dir);
                RynthLog.Plugin($"PluginLoader: orphan sweep DIAG-7.{i} — examining '{name}'.");

                if (name.Length < 2 || name[0] != 'p') continue;
                if (!int.TryParse(name.AsSpan(1), out int ownerPid)) continue;
                if (ownerPid == currentPid)
                {
                    RynthLog.Plugin($"PluginLoader: orphan sweep DIAG-7.{i} — skipping {name} (own PID).");
                    continue;
                }

                // Liveness check via Win32 OpenProcess — bypasses
                // System.Diagnostics.Process which was crashing this thread
                // (and by extension the process) under NativeAOT in a
                // heavily-hooked acclient.exe. We need this gate because
                // Directory.Delete over a still-mapped DLL — which is what
                // a LIVE owner's shadow dir contains — also crashes silently
                // here, bypassing the managed try/catch in
                // TryDeleteDirectoryReporting. So delete is unsafe without
                // knowing the owner is dead.
                RynthLog.Plugin($"PluginLoader: orphan sweep DIAG-7.{i} — checking IsPidAliveWin32({ownerPid}).");
                bool alive = IsPidAliveWin32(ownerPid);
                RynthLog.Plugin($"PluginLoader: orphan sweep DIAG-7.{i} — IsPidAliveWin32({ownerPid})={alive}.");

                if (alive)
                {
                    skippedAlive++;
                    RynthLog.Plugin($"PluginLoader: orphan sweep skipping {name} — PID {ownerPid} still running.");
                    continue;
                }

                RynthLog.Plugin($"PluginLoader: orphan sweep DIAG-7.{i} — calling TryDeleteDirectoryReporting('{name}').");
                bool ok = TryDeleteDirectoryReporting(dir);
                RynthLog.Plugin($"PluginLoader: orphan sweep DIAG-7.{i} — delete returned ok={ok}.");
                if (ok) deleted++;
            }

            RynthLog.Plugin($"PluginLoader: orphan sweep complete (deleted={deleted}, skippedAlive={skippedAlive}).");
        }
        catch (Exception ex)
        {
            RynthLog.Plugin($"PluginLoader: orphan sweep threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            RynthLog.Plugin($"PluginLoader: orphan sweep DIAG-FIN — releasing mutex (acquired={acquired}).");
            if (acquired && mutex != null)
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
            mutex?.Dispose();
            RynthLog.Plugin("PluginLoader: orphan sweep DIAG-FIN — done.");
        }
    }

    private static bool TryDeleteDirectoryReporting(string path)
    {
        try
        {
            Directory.Delete(path, true);
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Plugin($"PluginLoader: orphan sweep delete failed for {Path.GetFileName(path)} — {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint STILL_ACTIVE = 259;

    /// <summary>
    /// Win32-native liveness check. Replaces the original
    /// System.Diagnostics.Process.GetProcessById path which AVs the host
    /// process under NativeAOT in injected acclient.exe — see the comment in
    /// CleanupOrphanShadowRoots. Trade-off vs. the original: no ProcessName
    /// verification, so a recycled PID held by an unrelated process keeps the
    /// orphan dir alive. The cost is a stale shadow tree, not a crash.
    /// </summary>
    private static bool IsPidAliveWin32(int pid)
    {
        if (pid <= 0) return false;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            if (!GetExitCodeProcess(h, out uint exitCode)) return false;
            return exitCode == STILL_ACTIVE;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [Obsolete("Use IsPidAliveWin32 — Process.GetProcessById crashes under NativeAOT in injected x86 acclient.exe.")]
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            if (p.HasExited) return false;
            return string.Equals(p.ProcessName, "acclient", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static LoadedPlugin? LoadSingle(string sourcePath, string shadowRootDir, int generation)
    {
        string sessionShadowDir = PrepareShadowDirectory(shadowRootDir, generation);
        return TryLoad(sourcePath, sessionShadowDir);
    }

    private static LoadedPlugin? TryLoad(string sourcePath, string sessionShadowDir)
    {
        string fileName = Path.GetFileName(sourcePath);
        RynthLog.Verbose($"PluginLoader: Loading {fileName}...");

        string shadowPath;
        try
        {
            shadowPath = CreateShadowCopy(sourcePath, sessionShadowDir);
        }
        catch (Exception ex)
        {
            RynthLog.Plugin($"PluginLoader: FAILED to stage {fileName} ({ex.Message})");
            return null;
        }

        IntPtr handle = LoadLibraryW(shadowPath);
        if (handle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            RynthLog.Plugin($"PluginLoader: FAILED to load {fileName} (Win32 error {err})");
            return null;
        }

        // Resolve required exports
        IntPtr initPtr = GetProcAddress(handle, "RynthPluginInit");
        IntPtr shutdownPtr = GetProcAddress(handle, "RynthPluginShutdown");

        if (initPtr == IntPtr.Zero || shutdownPtr == IntPtr.Zero)
        {
            RynthLog.Plugin($"PluginLoader: {fileName} missing required exports (RynthPluginInit/RynthPluginShutdown) — skipping.");
            FreeLibrary(handle);
            return null;
        }

        var plugin = new LoadedPlugin
        {
            SourceFilePath = sourcePath,
            FilePath = shadowPath,
            FileName = fileName,
            ModuleHandle = handle,
            Init = Marshal.GetDelegateForFunctionPointer<PluginInitDelegate>(initPtr),
            Shutdown = Marshal.GetDelegateForFunctionPointer<PluginShutdownDelegate>(shutdownPtr),
            DisplayName = fileName
        };

        // Resolve optional exports
        IntPtr namePtr = GetProcAddress(handle, "RynthPluginName");
        if (namePtr != IntPtr.Zero)
            plugin.GetName = Marshal.GetDelegateForFunctionPointer<PluginNameDelegate>(namePtr);

        IntPtr versionPtr = GetProcAddress(handle, "RynthPluginVersion");
        if (versionPtr != IntPtr.Zero)
            plugin.GetVersion = Marshal.GetDelegateForFunctionPointer<PluginVersionDelegate>(versionPtr);

        IntPtr onLoginCompletePtr = GetProcAddress(handle, "RynthPluginOnLoginComplete");
        if (onLoginCompletePtr != IntPtr.Zero)
            plugin.OnLoginComplete = Marshal.GetDelegateForFunctionPointer<PluginOnLoginCompleteDelegate>(onLoginCompletePtr);

        IntPtr onLogoutPtr = GetProcAddress(handle, "RynthPluginOnLogout");
        if (onLogoutPtr != IntPtr.Zero)
            plugin.OnLogout = Marshal.GetDelegateForFunctionPointer<PluginOnLogoutDelegate>(onLogoutPtr);

        IntPtr onUiInitializedPtr = GetProcAddress(handle, "RynthPluginOnUIInitialized");
        if (onUiInitializedPtr != IntPtr.Zero)
            plugin.OnUIInitialized = Marshal.GetDelegateForFunctionPointer<PluginOnUIInitializedDelegate>(onUiInitializedPtr);

        IntPtr onBusyCountIncrementedPtr = GetProcAddress(handle, "RynthPluginOnBusyCountIncremented");
        if (onBusyCountIncrementedPtr != IntPtr.Zero)
            plugin.OnBusyCountIncremented = Marshal.GetDelegateForFunctionPointer<PluginOnBusyCountIncrementedDelegate>(onBusyCountIncrementedPtr);

        IntPtr onBusyCountDecrementedPtr = GetProcAddress(handle, "RynthPluginOnBusyCountDecremented");
        if (onBusyCountDecrementedPtr != IntPtr.Zero)
            plugin.OnBusyCountDecremented = Marshal.GetDelegateForFunctionPointer<PluginOnBusyCountDecrementedDelegate>(onBusyCountDecrementedPtr);

        IntPtr onSelectedTargetChangePtr = GetProcAddress(handle, "RynthPluginOnSelectedTargetChange");
        if (onSelectedTargetChangePtr != IntPtr.Zero)
            plugin.OnSelectedTargetChange = Marshal.GetDelegateForFunctionPointer<PluginOnSelectedTargetChangeDelegate>(onSelectedTargetChangePtr);

        IntPtr onCombatModeChangePtr = GetProcAddress(handle, "RynthPluginOnCombatModeChange");
        if (onCombatModeChangePtr != IntPtr.Zero)
            plugin.OnCombatModeChange = Marshal.GetDelegateForFunctionPointer<PluginOnCombatModeChangeDelegate>(onCombatModeChangePtr);

        IntPtr onSmartBoxEventPtr = GetProcAddress(handle, "RynthPluginOnSmartBoxEvent");
        if (onSmartBoxEventPtr != IntPtr.Zero)
        {
            plugin.OnSmartBoxEventPtr = onSmartBoxEventPtr;
            plugin.OnSmartBoxEvent = Marshal.GetDelegateForFunctionPointer<PluginOnSmartBoxEventDelegate>(onSmartBoxEventPtr);
        }

        IntPtr onDeleteObjectPtr = GetProcAddress(handle, "RynthPluginOnDeleteObject");
        if (onDeleteObjectPtr != IntPtr.Zero)
            plugin.OnDeleteObject = Marshal.GetDelegateForFunctionPointer<PluginOnDeleteObjectDelegate>(onDeleteObjectPtr);

        IntPtr onCreateObjectPtr = GetProcAddress(handle, "RynthPluginOnCreateObject");
        if (onCreateObjectPtr != IntPtr.Zero)
            plugin.OnCreateObject = Marshal.GetDelegateForFunctionPointer<PluginOnCreateObjectDelegate>(onCreateObjectPtr);

        IntPtr onUpdateObjectPtr = GetProcAddress(handle, "RynthPluginOnUpdateObject");
        if (onUpdateObjectPtr != IntPtr.Zero)
        {
            plugin.OnUpdateObjectPtr = onUpdateObjectPtr;
            plugin.OnUpdateObject = Marshal.GetDelegateForFunctionPointer<PluginOnUpdateObjectDelegate>(onUpdateObjectPtr);
        }

        IntPtr onUpdateObjectInventoryPtr = GetProcAddress(handle, "RynthPluginOnUpdateObjectInventory");
        if (onUpdateObjectInventoryPtr != IntPtr.Zero)
            plugin.OnUpdateObjectInventory = Marshal.GetDelegateForFunctionPointer<PluginOnUpdateObjectInventoryDelegate>(onUpdateObjectInventoryPtr);

        IntPtr onViewObjectContentsPtr = GetProcAddress(handle, "RynthPluginOnViewObjectContents");
        if (onViewObjectContentsPtr != IntPtr.Zero)
            plugin.OnViewObjectContents = Marshal.GetDelegateForFunctionPointer<PluginOnViewObjectContentsDelegate>(onViewObjectContentsPtr);

        IntPtr onStopViewingObjectContentsPtr = GetProcAddress(handle, "RynthPluginOnStopViewingObjectContents");
        if (onStopViewingObjectContentsPtr != IntPtr.Zero)
            plugin.OnStopViewingObjectContents = Marshal.GetDelegateForFunctionPointer<PluginOnStopViewingObjectContentsDelegate>(onStopViewingObjectContentsPtr);

        IntPtr onVendorOpenPtr = GetProcAddress(handle, "RynthPluginOnVendorOpen");
        if (onVendorOpenPtr != IntPtr.Zero)
            plugin.OnVendorOpen = Marshal.GetDelegateForFunctionPointer<PluginOnVendorOpenDelegate>(onVendorOpenPtr);

        IntPtr onVendorClosePtr = GetProcAddress(handle, "RynthPluginOnVendorClose");
        if (onVendorClosePtr != IntPtr.Zero)
            plugin.OnVendorClose = Marshal.GetDelegateForFunctionPointer<PluginOnVendorCloseDelegate>(onVendorClosePtr);

        IntPtr onUpdateHealthPtr = GetProcAddress(handle, "RynthPluginOnUpdateHealth");
        if (onUpdateHealthPtr != IntPtr.Zero)
        {
            plugin.OnUpdateHealthPtr = onUpdateHealthPtr;
            plugin.OnUpdateHealth = Marshal.GetDelegateForFunctionPointer<PluginOnUpdateHealthDelegate>(onUpdateHealthPtr);
        }

        IntPtr onCombatDamagePtr = GetProcAddress(handle, "RynthPluginOnCombatDamage");
        if (onCombatDamagePtr != IntPtr.Zero)
        {
            plugin.OnCombatDamagePtr = onCombatDamagePtr;
            plugin.OnCombatDamage = Marshal.GetDelegateForFunctionPointer<PluginOnCombatDamageDelegate>(onCombatDamagePtr);
        }

        IntPtr onKillNotificationPtr = GetProcAddress(handle, "RynthPluginOnKillNotification");
        if (onKillNotificationPtr != IntPtr.Zero)
        {
            plugin.OnKillNotificationPtr = onKillNotificationPtr;
            plugin.OnKillNotification = Marshal.GetDelegateForFunctionPointer<PluginOnKillNotificationDelegate>(onKillNotificationPtr);
        }

        IntPtr onChatWindowTextPtr = GetProcAddress(handle, "RynthPluginOnChatWindowText");
        if (onChatWindowTextPtr != IntPtr.Zero)
        {
            plugin.OnChatWindowTextPtr = onChatWindowTextPtr;
            plugin.OnChatWindowText = Marshal.GetDelegateForFunctionPointer<PluginOnChatWindowTextDelegate>(onChatWindowTextPtr);
        }

        IntPtr onChatBarEnterPtr = GetProcAddress(handle, "RynthPluginOnChatBarEnter");
        if (onChatBarEnterPtr != IntPtr.Zero)
            plugin.OnChatBarEnter = Marshal.GetDelegateForFunctionPointer<PluginOnChatBarEnterDelegate>(onChatBarEnterPtr);

        IntPtr onBarActionPtr = GetProcAddress(handle, "RynthPluginOnBarAction");
        if (onBarActionPtr != IntPtr.Zero)
            plugin.OnBarAction = Marshal.GetDelegateForFunctionPointer<PluginBarActionDelegate>(onBarActionPtr);

        IntPtr onEnchantmentAddedPtr = GetProcAddress(handle, "RynthPluginOnEnchantmentAdded");
        if (onEnchantmentAddedPtr != IntPtr.Zero)
            plugin.OnEnchantmentAdded = Marshal.GetDelegateForFunctionPointer<PluginOnEnchantmentAddedDelegate>(onEnchantmentAddedPtr);

        IntPtr onEnchantmentRemovedPtr = GetProcAddress(handle, "RynthPluginOnEnchantmentRemoved");
        if (onEnchantmentRemovedPtr != IntPtr.Zero)
            plugin.OnEnchantmentRemoved = Marshal.GetDelegateForFunctionPointer<PluginOnEnchantmentRemovedDelegate>(onEnchantmentRemovedPtr);

        IntPtr tickPtr = GetProcAddress(handle, "RynthPluginTick");
        if (tickPtr != IntPtr.Zero)
            plugin.Tick = Marshal.GetDelegateForFunctionPointer<PluginTickDelegate>(tickPtr);

        IntPtr renderPtr = GetProcAddress(handle, "RynthPluginRender");
        if (renderPtr != IntPtr.Zero)
            plugin.Render = Marshal.GetDelegateForFunctionPointer<PluginRenderDelegate>(renderPtr);

        // Read name/version from the plugin if available
        if (plugin.GetName != null)
        {
            IntPtr strPtr = plugin.GetName();
            if (strPtr != IntPtr.Zero)
                plugin.DisplayName = Marshal.PtrToStringAnsi(strPtr) ?? fileName;
        }

        if (plugin.GetVersion != null)
        {
            IntPtr strPtr = plugin.GetVersion();
            if (strPtr != IntPtr.Zero)
                plugin.VersionString = Marshal.PtrToStringAnsi(strPtr) ?? "";
        }

        string ver = string.IsNullOrEmpty(plugin.VersionString) ? "" : $" v{plugin.VersionString}";
        string caps = BuildCapsList(plugin);
        RynthLog.Verbose($"PluginLoader: {plugin.DisplayName}{ver} loaded ({caps})");

        return plugin;
    }

    private static string PrepareShadowDirectory(string shadowRootDir, int generation)
    {
        // Include EntryPoint.InitCount so each engine instance gets its own
        // shadow root. After an engine hot-reload the previous engine's
        // plugin DLL stays pinned (FreeLibrary doesn't actually unmap), so
        // overwriting its shadow path would fail with a sharing violation.
        int engineInit = EntryPoint.InitCount;
        string sessionDir = Path.Combine(
            shadowRootDir,
            $"session-{generation:D4}-pid{Environment.ProcessId}-eng{engineInit:D2}");
        Directory.CreateDirectory(sessionDir);
        return sessionDir;
    }

    private static string CreateShadowCopy(string sourcePath, string sessionShadowDir)
    {
        string fileName = Path.GetFileName(sourcePath);
        string shadowPath = Path.Combine(sessionShadowDir, fileName);
        File.Copy(sourcePath, shadowPath, true);

        string sourcePdb = Path.ChangeExtension(sourcePath, ".pdb");
        string shadowPdb = Path.ChangeExtension(shadowPath, ".pdb");
        if (File.Exists(sourcePdb))
            File.Copy(sourcePdb, shadowPdb, true);

        return shadowPath;
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        try
        {
            if (Directory.GetFileSystemEntries(path).Length == 0)
                Directory.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
        }
    }

    private static string BuildCapsList(LoadedPlugin p)
    {
        var parts = new List<string>(4) { "init", "shutdown" };
        if (p.OnLoginComplete != null) parts.Add("login");
        if (p.OnLogout != null) parts.Add("logout");
        if (p.OnUIInitialized != null) parts.Add("ui");
        if (p.OnBusyCountIncremented != null) parts.Add("busy+");
        if (p.OnBusyCountDecremented != null) parts.Add("busy-");
        if (p.OnSelectedTargetChange != null) parts.Add("target");
        if (p.OnCombatModeChange != null) parts.Add("combat-mode");
        if (p.OnSmartBoxEvent != null) parts.Add("smartbox");
        if (p.OnCreateObject != null) parts.Add("create");
        if (p.OnDeleteObject != null) parts.Add("delete");
        if (p.OnUpdateObject != null) parts.Add("update-object");
        if (p.OnUpdateObjectInventory != null) parts.Add("inventory");
        if (p.OnViewObjectContents != null) parts.Add("contents-open");
        if (p.OnStopViewingObjectContents != null) parts.Add("contents-close");
        if (p.OnVendorOpen != null) parts.Add("vendor-open");
        if (p.OnVendorClose != null) parts.Add("vendor-close");
        if (p.OnUpdateHealth != null || p.OnUpdateHealthPtr != IntPtr.Zero) parts.Add("health");
        if (p.OnCombatDamage != null || p.OnCombatDamagePtr != IntPtr.Zero) parts.Add("combatdmg");
        if (p.OnKillNotification != null || p.OnKillNotificationPtr != IntPtr.Zero) parts.Add("kill");
        if (p.OnChatWindowText != null) parts.Add("chat-in");
        if (p.OnChatBarEnter != null) parts.Add("chat-out");
        if (p.OnBarAction != null) parts.Add("bar");
        if (p.Tick != null) parts.Add("tick");
        if (p.Render != null) parts.Add("render");
        return string.Join(", ", parts);
    }
}

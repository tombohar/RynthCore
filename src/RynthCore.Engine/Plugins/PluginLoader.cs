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
    /// Unloads a plugin by freeing its DLL.
    /// </summary>
    public static void Unload(LoadedPlugin plugin)
    {
        if (plugin.ModuleHandle != IntPtr.Zero)
        {
            FreeLibrary(plugin.ModuleHandle);
            RynthLog.Verbose($"PluginLoader: Unloaded {plugin.FileName}");
        }

        TryDeleteFile(plugin.FilePath);
        TryDeleteFile(Path.ChangeExtension(plugin.FilePath, ".pdb"));
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
        string sessionDir = Path.Combine(shadowRootDir, $"session-{generation:D4}-pid{Environment.ProcessId}");
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
        if (p.OnChatWindowText != null) parts.Add("chat-in");
        if (p.OnChatBarEnter != null) parts.Add("chat-out");
        if (p.OnBarAction != null) parts.Add("bar");
        if (p.Tick != null) parts.Add("tick");
        if (p.Render != null) parts.Add("render");
        return string.Join(", ", parts);
    }
}

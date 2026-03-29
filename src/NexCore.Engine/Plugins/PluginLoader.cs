// ============================================================================
//  NexCore.Engine - Plugins/PluginLoader.cs
//  Scans the Plugins directory, loads each DLL, and resolves exports.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace NexCore.Engine.Plugins;

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
            EntryPoint.Log($"PluginLoader: Creating plugins directory: {pluginsDir}");
            try { Directory.CreateDirectory(pluginsDir); }
            catch (Exception ex)
            {
                EntryPoint.Log($"PluginLoader: Failed to create directory: {ex.Message}");
                return plugins;
            }
            return plugins;
        }

        string sessionShadowDir = PrepareShadowDirectory(shadowRootDir, generation);

        string[] dllFiles;
        try { dllFiles = Directory.GetFiles(pluginsDir, "*.dll"); }
        catch (Exception ex)
        {
            EntryPoint.Log($"PluginLoader: Failed to enumerate {pluginsDir}: {ex.Message}");
            return plugins;
        }

        Array.Sort(dllFiles, StringComparer.OrdinalIgnoreCase);

        if (dllFiles.Length == 0)
        {
            EntryPoint.Log("PluginLoader: No plugin DLLs found.");
            return plugins;
        }

        EntryPoint.Log($"PluginLoader: Found {dllFiles.Length} DLL(s) in {pluginsDir}");

        foreach (string dllPath in dllFiles)
        {
            var plugin = TryLoad(dllPath, sessionShadowDir);
            if (plugin != null)
                plugins.Add(plugin);
        }

        EntryPoint.Log($"PluginLoader: {plugins.Count} plugin(s) loaded successfully.");
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
            EntryPoint.Log($"PluginLoader: Unloaded {plugin.FileName}");
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
            EntryPoint.Log($"PluginLoader: Shadow cleanup skipped ({ex.Message})");
        }
    }

    private static LoadedPlugin? TryLoad(string sourcePath, string sessionShadowDir)
    {
        string fileName = Path.GetFileName(sourcePath);
        EntryPoint.Log($"PluginLoader: Loading {fileName}...");

        string shadowPath;
        try
        {
            shadowPath = CreateShadowCopy(sourcePath, sessionShadowDir);
        }
        catch (Exception ex)
        {
            EntryPoint.Log($"PluginLoader: FAILED to stage {fileName} ({ex.Message})");
            return null;
        }

        IntPtr handle = LoadLibraryW(shadowPath);
        if (handle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            EntryPoint.Log($"PluginLoader: FAILED to load {fileName} (Win32 error {err})");
            return null;
        }

        // Resolve required exports
        IntPtr initPtr = GetProcAddress(handle, "NexPluginInit");
        IntPtr shutdownPtr = GetProcAddress(handle, "NexPluginShutdown");

        if (initPtr == IntPtr.Zero || shutdownPtr == IntPtr.Zero)
        {
            EntryPoint.Log($"PluginLoader: {fileName} missing required exports (NexPluginInit/NexPluginShutdown) — skipping.");
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
        IntPtr namePtr = GetProcAddress(handle, "NexPluginName");
        if (namePtr != IntPtr.Zero)
            plugin.GetName = Marshal.GetDelegateForFunctionPointer<PluginNameDelegate>(namePtr);

        IntPtr versionPtr = GetProcAddress(handle, "NexPluginVersion");
        if (versionPtr != IntPtr.Zero)
            plugin.GetVersion = Marshal.GetDelegateForFunctionPointer<PluginVersionDelegate>(versionPtr);

        IntPtr onLoginCompletePtr = GetProcAddress(handle, "NexPluginOnLoginComplete");
        if (onLoginCompletePtr != IntPtr.Zero)
            plugin.OnLoginComplete = Marshal.GetDelegateForFunctionPointer<PluginOnLoginCompleteDelegate>(onLoginCompletePtr);

        IntPtr onUiInitializedPtr = GetProcAddress(handle, "NexPluginOnUIInitialized");
        if (onUiInitializedPtr != IntPtr.Zero)
            plugin.OnUIInitialized = Marshal.GetDelegateForFunctionPointer<PluginOnUIInitializedDelegate>(onUiInitializedPtr);

        IntPtr onSelectedTargetChangePtr = GetProcAddress(handle, "NexPluginOnSelectedTargetChange");
        if (onSelectedTargetChangePtr != IntPtr.Zero)
            plugin.OnSelectedTargetChange = Marshal.GetDelegateForFunctionPointer<PluginOnSelectedTargetChangeDelegate>(onSelectedTargetChangePtr);

        IntPtr onSmartBoxEventPtr = GetProcAddress(handle, "NexPluginOnSmartBoxEvent");
        if (onSmartBoxEventPtr != IntPtr.Zero)
        {
            plugin.OnSmartBoxEventPtr = onSmartBoxEventPtr;
            plugin.OnSmartBoxEvent = Marshal.GetDelegateForFunctionPointer<PluginOnSmartBoxEventDelegate>(onSmartBoxEventPtr);
        }

        IntPtr onDeleteObjectPtr = GetProcAddress(handle, "NexPluginOnDeleteObject");
        if (onDeleteObjectPtr != IntPtr.Zero)
            plugin.OnDeleteObject = Marshal.GetDelegateForFunctionPointer<PluginOnDeleteObjectDelegate>(onDeleteObjectPtr);

        IntPtr onCreateObjectPtr = GetProcAddress(handle, "NexPluginOnCreateObject");
        if (onCreateObjectPtr != IntPtr.Zero)
            plugin.OnCreateObject = Marshal.GetDelegateForFunctionPointer<PluginOnCreateObjectDelegate>(onCreateObjectPtr);

        IntPtr onUpdateObjectPtr = GetProcAddress(handle, "NexPluginOnUpdateObject");
        if (onUpdateObjectPtr != IntPtr.Zero)
        {
            plugin.OnUpdateObjectPtr = onUpdateObjectPtr;
            plugin.OnUpdateObject = Marshal.GetDelegateForFunctionPointer<PluginOnUpdateObjectDelegate>(onUpdateObjectPtr);
        }

        IntPtr onUpdateObjectInventoryPtr = GetProcAddress(handle, "NexPluginOnUpdateObjectInventory");
        if (onUpdateObjectInventoryPtr != IntPtr.Zero)
            plugin.OnUpdateObjectInventory = Marshal.GetDelegateForFunctionPointer<PluginOnUpdateObjectInventoryDelegate>(onUpdateObjectInventoryPtr);

        IntPtr onViewObjectContentsPtr = GetProcAddress(handle, "NexPluginOnViewObjectContents");
        if (onViewObjectContentsPtr != IntPtr.Zero)
            plugin.OnViewObjectContents = Marshal.GetDelegateForFunctionPointer<PluginOnViewObjectContentsDelegate>(onViewObjectContentsPtr);

        IntPtr onStopViewingObjectContentsPtr = GetProcAddress(handle, "NexPluginOnStopViewingObjectContents");
        if (onStopViewingObjectContentsPtr != IntPtr.Zero)
            plugin.OnStopViewingObjectContents = Marshal.GetDelegateForFunctionPointer<PluginOnStopViewingObjectContentsDelegate>(onStopViewingObjectContentsPtr);

        IntPtr onChatWindowTextPtr = GetProcAddress(handle, "NexPluginOnChatWindowText");
        if (onChatWindowTextPtr != IntPtr.Zero)
        {
            plugin.OnChatWindowTextPtr = onChatWindowTextPtr;
            plugin.OnChatWindowText = Marshal.GetDelegateForFunctionPointer<PluginOnChatWindowTextDelegate>(onChatWindowTextPtr);
        }

        IntPtr onChatBarEnterPtr = GetProcAddress(handle, "NexPluginOnChatBarEnter");
        if (onChatBarEnterPtr != IntPtr.Zero)
            plugin.OnChatBarEnter = Marshal.GetDelegateForFunctionPointer<PluginOnChatBarEnterDelegate>(onChatBarEnterPtr);

        IntPtr onBarActionPtr = GetProcAddress(handle, "NexPluginOnBarAction");
        if (onBarActionPtr != IntPtr.Zero)
            plugin.OnBarAction = Marshal.GetDelegateForFunctionPointer<PluginBarActionDelegate>(onBarActionPtr);

        IntPtr tickPtr = GetProcAddress(handle, "NexPluginTick");
        if (tickPtr != IntPtr.Zero)
            plugin.Tick = Marshal.GetDelegateForFunctionPointer<PluginTickDelegate>(tickPtr);

        IntPtr renderPtr = GetProcAddress(handle, "NexPluginRender");
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
        EntryPoint.Log($"PluginLoader: {plugin.DisplayName}{ver} loaded ({caps})");

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
        if (p.OnSelectedTargetChange != null) parts.Add("target");
        if (p.OnSmartBoxEvent != null) parts.Add("smartbox");
        if (p.OnCreateObject != null) parts.Add("create");
        if (p.OnDeleteObject != null) parts.Add("delete");
        if (p.OnUpdateObject != null) parts.Add("update-object");
        if (p.OnUpdateObjectInventory != null) parts.Add("inventory");
        if (p.OnViewObjectContents != null) parts.Add("contents-open");
        if (p.OnStopViewingObjectContents != null) parts.Add("contents-close");
        if (p.OnChatWindowText != null) parts.Add("chat-in");
        if (p.OnChatBarEnter != null) parts.Add("chat-out");
        if (p.OnBarAction != null) parts.Add("bar");
        if (p.Tick != null) parts.Add("tick");
        if (p.Render != null) parts.Add("render");
        return string.Join(", ", parts);
    }
}

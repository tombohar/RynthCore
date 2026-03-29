// ============================================================================
//  NexCore.Engine - EntryPoint.cs
//  NativeAOT exported function. Called by the injector after LoadLibrary.
//  Spawns init on a background thread to avoid loader-lock issues.
// ============================================================================

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Generic;
using ImGuiNET;
using NexCore.Engine.Compatibility;
using NexCore.Engine.D3D9;
using NexCore.Engine.Plugins;

namespace NexCore.Engine;

public static class EntryPoint
{
    private const string BuildStamp = "2026-03-29 ui-flicker-fix-v25";
    private const int MaxRecentLogLines = 256;
    private static int _initialized;
    private static bool _imGuiResolverConfigured;
    private static IntPtr _imGuiNativeHandle;
    private static readonly object LogLock = new();
    private static readonly Queue<string> RecentLogLines = new();

    [UnmanagedCallersOnly(EntryPoint = "NexCoreInit")]
    public static uint Initialize(IntPtr lpParam)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            return 1;

        try
        {
            Log($"NexCoreInit called (build {BuildStamp}) - spawning init thread...");
            RunInitStep("early multi-client hooks", () => MultiClientHooks.Initialize(Log));

            var thread = new Thread(InitWorker)
            {
                Name = "NexCore.Init",
                IsBackground = true
            };
            thread.Start();

            return 0;
        }
        catch (Exception ex)
        {
            Log($"FATAL in NexCoreInit: {ex}");
            return 2;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetModuleHandleA(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(IntPtr hModule, char[] lpFilename, uint nSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    private static string? GetEngineDirectory()
    {
        IntPtr hEngine = GetModuleHandleA("NexCore.Engine.dll");
        if (hEngine == IntPtr.Zero)
            return null;

        var buffer = new char[512];
        uint length = GetModuleFileNameW(hEngine, buffer, (uint)buffer.Length);
        if (length == 0)
            return null;

        return Path.GetDirectoryName(new string(buffer, 0, (int)length));
    }

    private static bool PreloadNativeDll(string engineDir, string dllName)
    {
        foreach (string path in GetNativeDllCandidates(engineDir, dllName))
        {
            if (!File.Exists(path))
                continue;

            long fileSize = TryGetFileSize(path);
            Log(fileSize > 0
                ? $"Preload: Loading {path} ({fileSize} bytes)"
                : $"Preload: Loading {path}");

            IntPtr handle = LoadLibraryW(path);
            if (handle != IntPtr.Zero)
            {
                Log($"Preload: {dllName} OK (0x{handle:X8})");
                return true;
            }

            Log($"Preload: FAILED to load {path} (error {Marshal.GetLastWin32Error()})");
        }

        Log($"Preload: FAILED to find/load {dllName} from NexCore directories.");
        return false;
    }

    private static bool ConfigureImGuiNativeLibrary(string engineDir)
    {
        if (_imGuiResolverConfigured)
            return _imGuiNativeHandle != IntPtr.Zero;

        foreach (string path in GetImGuiNativeCandidates(engineDir))
        {
            if (!File.Exists(path))
                continue;

            long fileSize = TryGetFileSize(path);
            Log(fileSize > 0
                ? $"ImGuiNative: Loading {path} ({fileSize} bytes)"
                : $"ImGuiNative: Loading {path}");

            _imGuiNativeHandle = LoadLibraryW(path);
            if (_imGuiNativeHandle == IntPtr.Zero)
            {
                Log($"ImGuiNative: FAILED to load {path} (error {Marshal.GetLastWin32Error()})");
                continue;
            }

            try
            {
                NativeLibrary.SetDllImportResolver(typeof(ImGui).Assembly, ResolveImGuiNativeLibrary);
                Log($"ImGuiNative: Resolver configured (0x{_imGuiNativeHandle:X8})");
            }
            catch (InvalidOperationException ex)
            {
                Log($"ImGuiNative: Resolver already set - {ex.Message}");
            }

            _imGuiResolverConfigured = true;
            return true;
        }

        Log("ImGuiNative: FAILED to find/load a private cimgui runtime.");
        return false;
    }

    private static IntPtr ResolveImGuiNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "cimgui", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(libraryName, "cimgui.dll", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        return _imGuiNativeHandle;
    }

    private static string[] GetNativeDllCandidates(string engineDir, string dllName)
    {
        return
        [
            Path.Combine(engineDir, dllName),
            Path.Combine(engineDir, "Native", dllName)
        ];
    }

    private static string[] GetImGuiNativeCandidates(string engineDir)
    {
        return
        [
            Path.Combine(engineDir, "Native", "NexCore.cimgui.dll"),
            Path.Combine(engineDir, "NexCore.cimgui.dll"),
            Path.Combine(engineDir, "Native", "cimgui.dll"),
            Path.Combine(engineDir, "cimgui.dll")
        ];
    }

    private static long TryGetFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static void InitWorker()
    {
        try
        {
            Log("Init thread started.");

            string? engineDir = GetEngineDirectory();
            if (engineDir == null)
            {
                Log("FATAL: Could not determine engine directory.");
                return;
            }

            Log($"Engine directory: {engineDir}");

            if (!PreloadNativeDll(engineDir, "minhook.x86.dll"))
            {
                Log("FATAL: minhook.x86.dll required - aborting.");
                return;
            }

            if (!ConfigureImGuiNativeLibrary(engineDir))
            {
                Log("WARNING: cimgui runtime not found - ImGui will not be available.");
                Log("  Ship NexCore.cimgui.dll (or cimgui.dll) alongside NexCore.Engine.dll");
            }

            RunInitStep("NexAi action hooks", ClientActionHooks.Initialize);
            RunInitStep("login lifecycle hooks", () => LoginLifecycleHooks.Initialize(Log));
            RunInitStep("UI lifecycle hooks", () => UiLifecycleHooks.Initialize(Log));
            RunInitStep("selected-target hooks", () => SelectedTargetHooks.Initialize(Log));
            RunInitStep("smartbox hooks", () => SmartBoxHooks.Initialize(Log));
            RunInitStep("create-object hooks", () => CreateObjectHooks.Initialize(Log));
            RunInitStep("delete-object hooks", () => DeleteObjectHooks.Initialize(Log));
            RunInitStep("update-object hooks", () => UpdateObjectServerDispatchHooks.Initialize(Log));
            RunInitStep("vector-update hooks", () => VectorUpdateServerDispatchHooks.Initialize(Log));
            RunInitStep("update-object-inventory hooks", () => UpdateObjectInventoryHooks.Initialize(Log));
            RunInitStep("view-object-contents hooks", () => ViewObjectContentsHooks.Initialize(Log));
            RunInitStep("chat callback hooks", () => ChatCallbackHooks.Initialize(Log));
            PluginManager.LoadPlugins(engineDir);

            D3D9Bootstrapper.Start();
            Log("NexCore bootstrap initialized.");
        }
        catch (Exception ex)
        {
            Log($"FATAL in InitWorker: {ex}");
        }
    }

    private static void RunInitStep(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log($"Compat: {name} failed during init - {ex}");
        }
    }

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "NexCore.log");

    internal static void Log(string message)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";

            lock (LogLock)
            {
                RecentLogLines.Enqueue(line);
                while (RecentLogLines.Count > MaxRecentLogLines)
                    RecentLogLines.Dequeue();
            }

            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch
        {
        }
    }

    internal static string[] GetRecentLogLines()
    {
        lock (LogLock)
            return RecentLogLines.ToArray();
    }
}

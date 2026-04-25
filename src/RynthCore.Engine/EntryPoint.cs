// ============================================================================
//  RynthCore.Engine - EntryPoint.cs
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
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.D3D9;
using RynthCore.Engine.Plugins;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Panels;

namespace RynthCore.Engine;

public static class EntryPoint
{
    internal const string BuildStamp = "2026-03-30-v54-patternscan";
    private const int MaxRecentLogLines = 256;
    private static int _initialized;
    private static bool _imGuiResolverConfigured;
    private static IntPtr _imGuiNativeHandle;
    private static readonly object LogLock = new();
    private static readonly Queue<string> RecentLogLines = new();

    /// <summary>Set true to enable verbose startup logging (hook ready messages, plugin lifecycle, etc.).</summary>
    internal static bool VerboseLogging = false;

    /// <summary>Set by ImGuiController once the game window is confirmed. Read by AvaloniaOverlay.</summary>
    internal static volatile IntPtr GameHwnd;

    [UnmanagedCallersOnly(EntryPoint = "RynthCoreInit")]
    public static uint Initialize(IntPtr lpParam)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            return 1;

        try
        {
            RynthLog.Info($"RynthCoreInit called (build {BuildStamp}) - spawning init thread...");
            CrashLogger.Install();
            RunInitStep("early multi-client hooks", MultiClientHooks.Initialize);

            var thread = new Thread(InitWorker)
            {
                Name = "RynthCore.Init",
                IsBackground = true
            };
            thread.Start();

            return 0;
        }
        catch (Exception ex)
        {
            RynthLog.Info($"FATAL in RynthCoreInit: {ex}");
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
        IntPtr hEngine = GetModuleHandleA("RynthCore.Engine.dll");
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
            RynthLog.Verbose(fileSize > 0
                ? $"Preload: Loading {path} ({fileSize} bytes)"
                : $"Preload: Loading {path}");

            IntPtr handle = LoadLibraryW(path);
            if (handle != IntPtr.Zero)
            {
                RynthLog.Verbose($"Preload: {dllName} OK (0x{handle:X8})");
                return true;
            }

            RynthLog.Info($"Preload: FAILED to load {path} (error {Marshal.GetLastWin32Error()})");
        }

        RynthLog.Info($"Preload: FAILED to find/load {dllName} from RynthCore directories.");
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
            RynthLog.Verbose(fileSize > 0
                ? $"ImGuiNative: Loading {path} ({fileSize} bytes)"
                : $"ImGuiNative: Loading {path}");

            _imGuiNativeHandle = LoadLibraryW(path);
            if (_imGuiNativeHandle == IntPtr.Zero)
            {
                RynthLog.Info($"ImGuiNative: FAILED to load {path} (error {Marshal.GetLastWin32Error()})");
                continue;
            }

            try
            {
                NativeLibrary.SetDllImportResolver(typeof(ImGui).Assembly, ResolveImGuiNativeLibrary);
                RynthLog.Verbose($"ImGuiNative: Resolver configured (0x{_imGuiNativeHandle:X8})");
            }
            catch (InvalidOperationException ex)
            {
                RynthLog.Info($"ImGuiNative: Resolver already set - {ex.Message}");
            }

            _imGuiResolverConfigured = true;
            return true;
        }

        RynthLog.Info("ImGuiNative: FAILED to find/load a private cimgui runtime.");
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
        var candidates = new List<string>();
        foreach (string directory in GetEngineSearchDirectories(engineDir))
            AddCandidate(candidates, Path.Combine(directory, dllName));

        return candidates.ToArray();
    }

    private static string[] GetImGuiNativeCandidates(string engineDir)
    {
        var candidates = new List<string>();
        foreach (string directory in GetEngineSearchDirectories(engineDir))
        {
            AddCandidate(candidates, Path.Combine(directory, "RynthCore.cimgui.dll"));
            AddCandidate(candidates, Path.Combine(directory, "cimgui.dll"));
        }

        return candidates.ToArray();
    }

    private static IEnumerable<string> GetEngineSearchDirectories(string engineDir)
    {
        var directories = new List<string>();
        AddCandidate(directories, engineDir);

        string normalizedEngineDir = Path.GetFullPath(engineDir);
        bool engineDirIsRuntime = string.Equals(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(normalizedEngineDir)),
            "Runtime",
            StringComparison.OrdinalIgnoreCase);

        if (engineDirIsRuntime)
        {
            string? rootDir = Directory.GetParent(normalizedEngineDir)?.FullName;
            AddCandidate(directories, Path.Combine(normalizedEngineDir, "Native"));
            AddCandidate(directories, rootDir);
            if (!string.IsNullOrWhiteSpace(rootDir))
                AddCandidate(directories, Path.Combine(rootDir, "Native"));
        }
        else
        {
            string runtimeDir = Path.Combine(normalizedEngineDir, "Runtime");
            AddCandidate(directories, runtimeDir);
            AddCandidate(directories, Path.Combine(normalizedEngineDir, "Native"));
            AddCandidate(directories, Path.Combine(runtimeDir, "Native"));
        }

        return directories;
    }

    private static void AddCandidate(List<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        string fullPath = Path.GetFullPath(path);
        if (!candidates.Exists(existing => string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase)))
            candidates.Add(fullPath);
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
            RynthLog.Verbose("Init thread started.");

            string? engineDir = GetEngineDirectory();
            if (engineDir == null)
            {
                RynthLog.Info("FATAL: Could not determine engine directory.");
                return;
            }

            RynthLog.Verbose($"Engine directory: {engineDir}");

            if (!PreloadNativeDll(engineDir, "minhook.x86.dll"))
            {
                RynthLog.Info("FATAL: minhook.x86.dll required - aborting.");
                return;
            }

            if (!ConfigureImGuiNativeLibrary(engineDir))
            {
                RynthLog.Info("WARNING: cimgui runtime not found - ImGui will not be available.");
                RynthLog.Info("  Ship RynthCore.cimgui.dll (or cimgui.dll) alongside RynthCore.Engine.dll");
            }

            RunInitStep("RynthAi action hooks", ClientActionHooks.Initialize);
            RunInitStep("client helper hooks", () => ClientHelperHooks.Probe());
            RunInitStep("login lifecycle hooks", LoginLifecycleHooks.Initialize);
            RunInitStep("session state registry", SessionStateRegistry.Initialize);
            RunInitStep("UI lifecycle hooks", UiLifecycleHooks.Initialize);
            RunInitStep("logo bypass", LogoBypassHooks.Start);
            RunInitStep("busy-count hooks", BusyCountHooks.Initialize);
            RunInitStep("combat-mode hooks", CombatModeHooks.Initialize);
            RunInitStep("teleport-state hooks", TeleportStateHooks.Initialize);
            RunInitStep("salvage hooks", SalvageHooks.Initialize);
            RunInitStep("radar hooks", RadarHooks.Initialize);
            RunInitStep("chat hooks", ChatHooks.Initialize);
            RunInitStep("powerbar hooks", PowerbarHooks.Initialize);
            RunInitStep("do-motion hooks", DoMotionHooks.Initialize);
            RunInitStep("smartbox-setstate hooks", SmartBoxSetStateHooks.Initialize);
            RunInitStep("appraisal hooks", AppraisalHooks.Initialize);
            RunInitStep("account hooks", AccountHooks.Initialize);
            RunInitStep("client combat hooks", () => ClientCombatHooks.Probe());
            RunInitStep("selected-target hooks", SelectedTargetHooks.Initialize);
            RunInitStep("smartbox hooks", SmartBoxHooks.Initialize);
            RunInitStep("player vitals hooks", PlayerVitalsHooks.Initialize);
            RunInitStep("enchantment hooks", () => EnchantmentHooks.Initialize());
            RunInitStep("time-sync hook", TimeSyncHooks.Initialize);
            RunInitStep("create-object hooks", CreateObjectHooks.Initialize);
            RunInitStep("delete-object hooks", DeleteObjectHooks.Initialize);
            RunInitStep("update-object hooks", UpdateObjectServerDispatchHooks.Initialize);
            RunInitStep("vector-update hooks", VectorUpdateServerDispatchHooks.Initialize);
            RunInitStep("update-object-inventory hooks", UpdateObjectInventoryHooks.Initialize);
            RunInitStep("view-object-contents hooks", ViewObjectContentsHooks.Initialize);
            RunInitStep("vendor hooks", VendorHooks.Initialize);
            RunInitStep("chat callback hooks", ChatCallbackHooks.Initialize);
            RunInitStep("raw packet hooks", RawPacketHooks.Initialize);
            RunInitStep("property-update hooks", PropertyUpdateHooks.Initialize);
            RunInitStep("auto-id service", AutoIdService.Start);
            PluginManager.LoadPlugins(engineDir);

            // Defer D3D9 hooking until after character login. By that point
            // the game's device is fully initialized and stable, avoiding the
            // race condition that intermittently crashes NULLREF device creation
            // on Win11's d3d9-on-d3d12 wrapper.
            LoginLifecycleHooks.LoginComplete += () =>
            {
                RynthLog.D3D9("D3D9: Login complete — starting D3D9 bootstrapper.");
                D3D9Bootstrapper.Start();
            };

            // Avalonia overlay and its native dependencies (Skia, HarfBuzz, ANGLE)
            // are disabled. The ImGui shell handles all UI. Re-enable when needed.
            // PreloadNativeDll(engineDir, "libSkiaSharp.dll");
            // PreloadNativeDll(engineDir, "libHarfBuzzSharp.dll");
            // PreloadNativeDll(engineDir, "av_libglesv2.dll");
            // OverlayHost.RegisterPanel("Status",    StatusPanel.Create);
            // OverlayHost.RegisterPanel("Log",       LogPanel.Create);
            // OverlayHost.RegisterPanel("Hello Box", HelloBoxPanel.Create);
            // AvaloniaOverlay.Start();
            RynthLog.Info("RynthCore bootstrap initialized.");
        }
        catch (Exception ex)
        {
            RynthLog.Info($"FATAL in InitWorker: {ex}");
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
            RynthLog.Compat($"Compat: {name} failed during init - {ex}");
        }
    }

    internal static void Log(string message)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [pid:{Environment.ProcessId}] {message}";

            lock (LogLock)
            {
                RecentLogLines.Enqueue(line);
                while (RecentLogLines.Count > MaxRecentLogLines)
                    RecentLogLines.Dequeue();

                string logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "RynthCore.log");
                File.AppendAllText(logPath, line + "\n");
            }
        }
        catch
        {
        }
    }

    internal static void LogVerbose(string message)
    {
        if (VerboseLogging)
            Log(message);
    }

    internal static string[] GetRecentLogLines()
    {
        lock (LogLock)
            return RecentLogLines.ToArray();
    }
}

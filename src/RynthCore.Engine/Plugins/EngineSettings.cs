using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RynthCore.Engine.Plugins;

internal static class EngineSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RynthCore",
        "engine.json");

    private static List<string> _pluginPaths = new();
    private static bool _enableImGuiShell = true;
    private static bool _enablePlugins = true;
    private static bool _enableDatShareHook = true;
    private static bool _enableAvaloniaOverlay = true;
    private static bool _enableD3D9Hook = true;
    private static bool _enableEngine = true;
    private static int _engineHookCount = int.MaxValue;
    private static bool _enableImGuiBackend = true;
    private static bool _enableHangMinidump = true;
    private static bool _drawCustomVitalBars = true;
    private static bool _loaded;

    public static IReadOnlyList<string> PluginPaths
    {
        get
        {
            EnsureLoaded();
            return _pluginPaths;
        }
    }

    /// <summary>Force-disabled in code: RynthCore's in-AC UI is the Avalonia overlay, so the
    /// ImGui shell (RynthCoreShell bar + plugin ImGui windows) never renders. engine.json and
    /// the launcher Plugins tab can no longer toggle this; the persisted field is now a no-op.
    /// A developer can opt back in for debugging by setting env var RYNTHCORE_FORCE_IMGUI=1
    /// before launching AC — normal users never set this.</summary>
    public static bool EnableImGuiShell
        => string.Equals(
               System.Environment.GetEnvironmentVariable("RYNTHCORE_FORCE_IMGUI"),
               "1", System.StringComparison.Ordinal);

    /// <summary>When false, PluginManager.LoadPlugins is skipped so no plugin DLLs load,
    /// init, tick, or render. Decal-coexistence plugin pump is also skipped. Default true.</summary>
    public static bool EnablePlugins
    {
        get
        {
            EnsureLoaded();
            return _enablePlugins;
        }
    }

    /// <summary>When false, DatFileShareHooks.Initialize is skipped so kernel32!CreateFileA/W
    /// are NOT detoured and no DAT-extension access/share rewriting happens. Multi-client DAT
    /// coexistence with Decal-injected clients sharing the same install will fall back to the
    /// stricter share semantics those clients negotiated. Default true. Use to isolate the
    /// hook for crash diagnostics.</summary>
    public static bool EnableDatShareHook
    {
        get
        {
            EnsureLoaded();
            return _enableDatShareHook;
        }
    }

    /// <summary>When false, AvaloniaOverlay.Start() is skipped so the Avalonia STA thread,
    /// the offscreen overlay window, and all OverlayHost-registered panels never instantiate.
    /// Eliminates the entire Avalonia/Skia surface from the process. Compatibility hooks and
    /// (when enabled) plugins still load. Default true. Use to confirm whether the engine
    /// runs cleanly without Avalonia.</summary>
    public static bool EnableAvaloniaOverlay
    {
        get
        {
            EnsureLoaded();
            return _enableAvaloniaOverlay;
        }
    }

    /// <summary>When false, D3D9Bootstrapper.Start() is skipped at LoginComplete so neither
    /// Direct3DCreate9/CreateDevice nor IDirect3DDevice9::EndScene get hooked. Eliminates the
    /// entire D3D9 detour path, ImGui per-frame work, and the in-AC overlay bar. Compatibility
    /// hooks and Avalonia (when enabled) still run. Default true. Use to confirm whether the
    /// engine runs cleanly without the EndScene detour.</summary>
    public static bool EnableD3D9Hook
    {
        get
        {
            EnsureLoaded();
            return _enableD3D9Hook;
        }
    }

    /// <summary>When false, InitWorker installs no Compatibility/* hooks, no
    /// LoginLifecycleHooks subscriber chain, no OverlayHost panels, no Avalonia,
    /// no D3D9. Only the early items (CrashLogger, ProcessExitHooks,
    /// MultiClientHooks, DatFileShareHooks, HeartbeatLogger) that fire before
    /// InitWorker stay active. Use to confirm whether the recursive AV is in
    /// the engine's main hook path or in something earlier (loader, NativeAOT
    /// runtime, MultiClientHooks/DatFileShareHooks).</summary>
    public static bool EnableEngine
    {
        get
        {
            EnsureLoaded();
            return _enableEngine;
        }
    }

    /// <summary>Cap on how many of InitWorker's RunInitStep calls actually fire.
    /// Default = int.MaxValue (= install all 36). Set to 18 to install only the
    /// first half, 9 for the first quarter, etc — use for binary-search
    /// bisection of which Compatibility hook is causing the recursive AV.
    /// Has no effect if EnableEngine is false.</summary>
    public static int EngineHookCount
    {
        get
        {
            EnsureLoaded();
            return _engineHookCount;
        }
    }

    /// <summary>When false, EngineFrameController.OnEndScene skips its
    /// ImGui-specific block (Win32Backend.NewFrame, DX9Backend.NewFrame,
    /// ImGui.NewFrame/EndFrame/Render, RynthCoreShell + plugin RenderAll,
    /// DX9Backend.RenderDrawData, viewport pump). The always-on engine work
    /// (GameMatrixCapture, Nav3DRenderInjector, PluginManager init/tick/
    /// pending-action drains, DX9Backend.RenderNav3D fallback) still runs
    /// every frame. Diagnostic for whether the per-frame ImGui path is the
    /// recurser.</summary>
    public static bool EnableImGuiBackend
    {
        get
        {
            EnsureLoaded();
            return _enableImGuiBackend;
        }
    }

    /// <summary>When true, MainThreadHangWatchdog writes a minidump to
    /// Logs\dumps\ the first time AC's main thread is confirmed permanently
    /// wedged — a targeted post-mortem to replace always-on procdump (which
    /// dumped on every clean exit too). Default true.</summary>
    public static bool EnableHangMinidump
    {
        get
        {
            EnsureLoaded();
            return _enableHangMinidump;
        }
    }

    /// <summary>When true, the engine draws the custom D3D9 Health/Stamina/Mana HUD
    /// (<see cref="D3D9.VitalHud"/>) in EndScene. Toggleable live via "/rc vitals";
    /// the setter persists to engine.json so it survives relaunch. Default true.
    /// Only takes visible effect on a clean (no-Decal) client where the
    /// D3D9/EndScene path runs.</summary>
    public static bool DrawCustomVitalBars
    {
        get
        {
            EnsureLoaded();
            return _drawCustomVitalBars;
        }
        set
        {
            EnsureLoaded();   // populate _pluginPaths before Save() rewrites the file
            if (_drawCustomVitalBars == value) return;
            _drawCustomVitalBars = value;
            Save();
        }
    }

    public static void AddPluginPath(string path)
    {
        EnsureLoaded();
        string full = Path.GetFullPath(path);
        for (int i = 0; i < _pluginPaths.Count; i++)
        {
            if (string.Equals(_pluginPaths[i], full, StringComparison.OrdinalIgnoreCase))
                return;
        }
        _pluginPaths.Add(full);
        Save();
    }

    public static void RemovePluginPath(int index)
    {
        EnsureLoaded();
        if (index >= 0 && index < _pluginPaths.Count)
        {
            _pluginPaths.RemoveAt(index);
            Save();
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (!File.Exists(SettingsPath)) return;
            // ReadAllText detects + strips the UTF-8/UTF-16 BOM that some editors
            // (PowerShell 5.1 Set-Content -Encoding utf8, Notepad) silently prepend.
            // ReadAllBytes + JsonDocument.Parse trips on a leading BOM with
            // "'0xEF' is an invalid start of a value", which then sends every
            // EngineSettings flag back to its default — silently re-enables
            // EnableImGuiBackend etc. with no surfaced error.
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("PluginPaths", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    string? val = el.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        _pluginPaths.Add(val);
                }
            }

            if (doc.RootElement.TryGetProperty("EnableImGuiShell", out var shellEl) &&
                (shellEl.ValueKind == JsonValueKind.True || shellEl.ValueKind == JsonValueKind.False))
            {
                _enableImGuiShell = shellEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("EnablePlugins", out var pluginsEl) &&
                (pluginsEl.ValueKind == JsonValueKind.True || pluginsEl.ValueKind == JsonValueKind.False))
            {
                _enablePlugins = pluginsEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("EnableDatShareHook", out var datEl) &&
                (datEl.ValueKind == JsonValueKind.True || datEl.ValueKind == JsonValueKind.False))
            {
                _enableDatShareHook = datEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("EnableAvaloniaOverlay", out var avEl) &&
                (avEl.ValueKind == JsonValueKind.True || avEl.ValueKind == JsonValueKind.False))
            {
                _enableAvaloniaOverlay = avEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("EnableD3D9Hook", out var d3dEl) &&
                (d3dEl.ValueKind == JsonValueKind.True || d3dEl.ValueKind == JsonValueKind.False))
            {
                _enableD3D9Hook = d3dEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("EnableEngine", out var engEl) &&
                (engEl.ValueKind == JsonValueKind.True || engEl.ValueKind == JsonValueKind.False))
            {
                _enableEngine = engEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("EngineHookCount", out var ehcEl) &&
                ehcEl.ValueKind == JsonValueKind.Number &&
                ehcEl.TryGetInt32(out int ehc))
            {
                _engineHookCount = ehc;
            }

            if (doc.RootElement.TryGetProperty("EnableImGuiBackend", out var ibEl) &&
                (ibEl.ValueKind == JsonValueKind.True || ibEl.ValueKind == JsonValueKind.False))
            {
                _enableImGuiBackend = ibEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("EnableHangMinidump", out var hmEl) &&
                (hmEl.ValueKind == JsonValueKind.True || hmEl.ValueKind == JsonValueKind.False))
            {
                _enableHangMinidump = hmEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("DrawCustomVitalBars", out var cvbEl) &&
                (cvbEl.ValueKind == JsonValueKind.True || cvbEl.ValueKind == JsonValueKind.False))
            {
                _drawCustomVitalBars = cvbEl.GetBoolean();
            }
        }
        catch (Exception ex)
        {
            RynthLog.Plugin($"EngineSettings: Failed to load {SettingsPath}: {ex.Message}");
        }
    }

    private static void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (dir != null) Directory.CreateDirectory(dir);

            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteStartArray("PluginPaths");
                foreach (string p in _pluginPaths)
                    w.WriteStringValue(p);
                w.WriteEndArray();
                w.WriteBoolean("EnableImGuiShell", _enableImGuiShell);
                w.WriteBoolean("EnablePlugins", _enablePlugins);
                w.WriteBoolean("EnableDatShareHook", _enableDatShareHook);
                w.WriteBoolean("EnableAvaloniaOverlay", _enableAvaloniaOverlay);
                w.WriteBoolean("EnableD3D9Hook", _enableD3D9Hook);
                w.WriteBoolean("EnableEngine", _enableEngine);
                w.WriteNumber("EngineHookCount", _engineHookCount);
                w.WriteBoolean("EnableImGuiBackend", _enableImGuiBackend);
                w.WriteBoolean("EnableHangMinidump", _enableHangMinidump);
                w.WriteBoolean("DrawCustomVitalBars", _drawCustomVitalBars);
                w.WriteEndObject();
            }
            File.WriteAllBytes(SettingsPath, ms.ToArray());
        }
        catch (Exception ex)
        {
            RynthLog.Plugin($"EngineSettings: Failed to save: {ex.Message}");
        }
    }
}

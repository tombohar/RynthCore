using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using RynthCore.Plugin.RynthAi.Nav;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

public sealed unsafe class RynthAiPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer = Marshal.StringToHGlobalAnsi("RynthAi");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.1.0");

    private const float NavPointElevationOffset = 0.5f;
    private const float UnitsPerCoord = 240.0f;
    private const int RingSegments = 32;
    private const float RingRadius = 3.0f;   // game units (~1.25 yards)

    // ImGui colors (ABGR format for ImGui native)
    private const uint ColorCyan = 0xFFFFFF00;       // nav point rings
    private const uint ColorRed = 0xFF2222FF;         // active waypoint ring
    private const uint ColorBlueLine = 0xFFFF8800;    // lines between points

    private static RynthCoreApiNative _api;
    private static RynthCoreHost _host;
    private static bool _initialized;
    private static bool _loginComplete;
    private static bool _windowVisible;
    private static bool _showRoute = true;

    // Route state
    private static VTankNavParser? _currentRoute;
    private static string _currentNavPath = "";
    private static int _activeNavIndex;
    private static readonly string _navFolder = @"C:\Games\RynthSuite\RynthAi\NavProfiles";

    // Nav file browser state
    private static string[] _navFiles = Array.Empty<string>();

    // Cached cimgui function pointers
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _igSetCurrentContext;
    private static delegate* unmanaged[Cdecl]<ImVec2, int, ImVec2, void> _igSetNextWindowPos;
    private static delegate* unmanaged[Cdecl]<ImVec2, int, void> _igSetNextWindowSize;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, byte> _igBegin;
    private static delegate* unmanaged[Cdecl]<void> _igEnd;
    private static delegate* unmanaged[Cdecl]<IntPtr, ImVec2, byte> _igButton;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> _igTextUnformatted;
    private static delegate* unmanaged[Cdecl]<void> _igSeparator;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, byte> _igCheckbox;
    private static delegate* unmanaged[Cdecl]<IntPtr> _igGetForegroundDrawList;
    // ImDrawList_AddCircle(self, center, radius, col, num_segments, thickness)
    private static delegate* unmanaged[Cdecl]<IntPtr, ImVec2, float, uint, int, float, void> _igDrawListAddCircle;
    // ImDrawList_AddLine(self, p1, p2, col, thickness)
    private static delegate* unmanaged[Cdecl]<IntPtr, ImVec2, ImVec2, uint, float, void> _igDrawListAddLine;
    // ImDrawList_AddCircleFilled(self, center, radius, col, num_segments)
    private static delegate* unmanaged[Cdecl]<IntPtr, ImVec2, float, uint, int, void> _igDrawListAddCircleFilled;
    private static bool _imguiBound;

    public override int Initialize()
    {
        _api = Api;
        _host = Host;

        if (_api.ImGuiContext == IntPtr.Zero)
            return 11;

        if (!TryBindImGui(out string error))
        {
            Log($"RynthAi: failed to bind cimgui ({error})");
            return 12;
        }

        _initialized = true;
        _loginComplete = false;
        _windowVisible = false;
        _showRoute = true;
        _currentRoute = null;
        _currentNavPath = "";
        _activeNavIndex = 0;

        RefreshNavFiles();
        Log("RynthAi: initialized.");
        return 0;
    }

    public override void Shutdown()
    {
        Log("RynthAi: shutting down.");
        _initialized = false;
        _loginComplete = false;
        _currentRoute = null;
    }

    public override void OnLoginComplete()
    {
        if (!_initialized) return;
        _loginComplete = true;
    }

    public override void OnBarAction()
    {
        if (!_initialized || !_loginComplete) return;
        _windowVisible = !_windowVisible;
    }

    public override void OnRender()
    {
        if (!_initialized || !_loginComplete || !_imguiBound || _api.ImGuiContext == IntPtr.Zero)
            return;

        _igSetCurrentContext(_api.ImGuiContext);

        // Draw the 3D nav route overlay (always, even if UI window is hidden)
        if (_showRoute && _currentRoute != null && _currentRoute.Points.Count > 0)
            RenderNavRoute();

        // Draw the UI panel
        if (_windowVisible)
            RenderNavPanel();
    }

    // ─── Precomputed ring geometry (unit circle on ground plane) ────────

    private static readonly float[] _cosTable = new float[RingSegments];
    private static readonly float[] _sinTable = new float[RingSegments];
    private static bool _trigReady;

    private static void EnsureTrigTables()
    {
        if (_trigReady) return;
        for (int i = 0; i < RingSegments; i++)
        {
            double angle = 2.0 * Math.PI * i / RingSegments;
            _cosTable[i] = (float)Math.Cos(angle);
            _sinTable[i] = (float)Math.Sin(angle);
        }
        _trigReady = true;
    }

    // ─── 3D Nav Route Rendering ──────────────────────────────────────────

    private static void RenderNavRoute()
    {
        if (!_host.HasWorldToScreen || !_host.HasGetPlayerPose || !_host.HasGetCurCoords)
            return;

        if (!_host.TryGetPlayerPose(out uint cell, out float px, out float py, out float pz,
                out float _, out float _, out float _, out float _))
            return;

        if (!_host.TryGetCurCoords(out double playerNS, out double playerEW))
            return;

        IntPtr drawList = _igGetForegroundDrawList();
        if (drawList == IntPtr.Zero)
            return;

        EnsureTrigTables();

        var route = _currentRoute!;
        int count = route.Points.Count;

        // Project center of each nav point for line drawing
        Span<float> centerSx = stackalloc float[count];
        Span<float> centerSy = stackalloc float[count];
        Span<bool> centerOnScreen = stackalloc bool[count];

        for (int i = 0; i < count; i++)
        {
            var pt = route.Points[i];
            float wx = px + (float)((pt.EW - playerEW) * UnitsPerCoord);
            float wy = py + (float)((pt.NS - playerNS) * UnitsPerCoord);
            float wz = (float)pt.Z + NavPointElevationOffset;

            centerOnScreen[i] = _host.WorldToScreen(wx, wy, wz, out centerSx[i], out centerSy[i]);
        }

        // Draw lines between consecutive waypoints
        for (int i = 0; i < count; i++)
        {
            int nextIdx = i + 1;
            if (nextIdx >= count)
            {
                if (route.RouteType == NavRouteType.Circular)
                    nextIdx = 0;
                else
                    continue;
            }

            if (!centerOnScreen[i] && !centerOnScreen[nextIdx])
                continue;

            _igDrawListAddLine(
                drawList,
                new ImVec2(centerSx[i], centerSy[i]),
                new ImVec2(centerSx[nextIdx], centerSy[nextIdx]),
                ColorBlueLine,
                2.0f);
        }

        // Draw 3D rings on the ground at each waypoint
        // Each ring is a circle of world-space points projected to screen space
        Span<float> ringSx = stackalloc float[RingSegments];
        Span<float> ringSy = stackalloc float[RingSegments];
        Span<bool> ringVis = stackalloc bool[RingSegments];

        for (int i = 0; i < count; i++)
        {
            var pt = route.Points[i];
            float cx = px + (float)((pt.EW - playerEW) * UnitsPerCoord);
            float cy = py + (float)((pt.NS - playerNS) * UnitsPerCoord);
            float cz = (float)pt.Z + NavPointElevationOffset;

            // Project each vertex of the ring (lying flat on the ground plane)
            int visCount = 0;
            for (int s = 0; s < RingSegments; s++)
            {
                float wx = cx + _cosTable[s] * RingRadius;
                float wy = cy + _sinTable[s] * RingRadius;

                ringVis[s] = _host.WorldToScreen(wx, wy, cz, out ringSx[s], out ringSy[s]);
                if (ringVis[s]) visCount++;
            }

            // Skip if no part of the ring is visible
            if (visCount == 0)
                continue;

            uint color = (i == _activeNavIndex) ? ColorRed : ColorCyan;
            float thickness = (i == _activeNavIndex) ? 3.0f : 2.0f;

            // Draw the ring as connected line segments
            for (int s = 0; s < RingSegments; s++)
            {
                int next = (s + 1) % RingSegments;

                // Draw segment if at least one end projected successfully
                if (ringVis[s] || ringVis[next])
                {
                    _igDrawListAddLine(
                        drawList,
                        new ImVec2(ringSx[s], ringSy[s]),
                        new ImVec2(ringSx[next], ringSy[next]),
                        color,
                        thickness);
                }
            }
        }
    }

    // ─── Nav Control Panel ───────────────────────────────────────────────

    private static void RenderNavPanel()
    {
        _igSetNextWindowPos(new ImVec2(48f, 140f), 1 << 2 /* ImGuiCond_FirstUseEver */, default);
        _igSetNextWindowSize(new ImVec2(380f, 320f), 1 << 2);

        bool open = _igBegin(
            Marshal.StringToHGlobalAnsi("RynthAi Nav##RynthAiNav"),
            IntPtr.Zero,
            0) != 0;

        if (open)
        {
            // Show route toggle
            byte showByte = _showRoute ? (byte)1 : (byte)0;
            if (_igCheckbox(Marshal.StringToHGlobalAnsi("Show Route"), &showByte) != 0)
                _showRoute = showByte != 0;

            _igSeparator();

            // Current route info
            string info = _currentRoute != null
                ? $"Route: {Path.GetFileName(_currentNavPath)} ({_currentRoute.Points.Count} pts, {_currentRoute.RouteType})"
                : "No route loaded";
            TextTemp(info);

            if (_currentRoute != null)
                TextTemp($"Active waypoint: {_activeNavIndex}");

            _igSeparator();

            // Nav file list
            TextTemp("Nav Files:");

            for (int i = 0; i < _navFiles.Length; i++)
            {
                string fileName = Path.GetFileName(_navFiles[i]);
                bool isCurrent = string.Equals(_navFiles[i], _currentNavPath, StringComparison.OrdinalIgnoreCase);
                string label = isCurrent ? $">> {fileName}" : $"   {fileName}";

                IntPtr labelPtr = Marshal.StringToHGlobalAnsi($"{label}##{i}");
                try
                {
                    if (_igButton(labelPtr, new ImVec2(340f, 0f)) != 0)
                    {
                        LoadNavFile(_navFiles[i]);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(labelPtr);
                }
            }

            _igSeparator();

            if (_igButton(Marshal.StringToHGlobalAnsi("Refresh Files"), default) != 0)
                RefreshNavFiles();
        }

        _igEnd();
    }

    // ─── Nav File Management ─────────────────────────────────────────────

    private static void RefreshNavFiles()
    {
        try
        {
            if (Directory.Exists(_navFolder))
                _navFiles = Directory.GetFiles(_navFolder, "*.nav");
            else
                _navFiles = Array.Empty<string>();
        }
        catch
        {
            _navFiles = Array.Empty<string>();
        }
    }

    private static void LoadNavFile(string path)
    {
        try
        {
            _currentRoute = VTankNavParser.Load(path);
            _currentNavPath = path;
            _activeNavIndex = 0;
            _host.Log($"RynthAi: Loaded route '{Path.GetFileName(path)}' with {_currentRoute.Points.Count} points");
        }
        catch (Exception ex)
        {
            _host.Log($"RynthAi: Failed to load nav file: {ex.Message}");
        }
    }

    // ─── ImGui Helpers ───────────────────────────────────────────────────

    private static void TextTemp(string text)
    {
        IntPtr ptr = Marshal.StringToHGlobalAnsi(text);
        try { _igTextUnformatted(ptr, IntPtr.Zero); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static bool TryBindImGui(out string error)
    {
        if (_imguiBound) { error = ""; return true; }

        IntPtr mod = GetModuleHandleA("RynthCore.cimgui.dll");
        if (mod == IntPtr.Zero) mod = GetModuleHandleA("cimgui.dll");
        if (mod == IntPtr.Zero) { error = "no cimgui module"; return false; }

        if (!TryGetProc(mod, "igSetCurrentContext", out IntPtr p)) { error = "missing igSetCurrentContext"; return false; }
        _igSetCurrentContext = (delegate* unmanaged[Cdecl]<IntPtr, void>)p;

        if (!TryGetProc(mod, "igSetNextWindowPos", out p)) { error = "missing igSetNextWindowPos"; return false; }
        _igSetNextWindowPos = (delegate* unmanaged[Cdecl]<ImVec2, int, ImVec2, void>)p;

        if (!TryGetProc(mod, "igSetNextWindowSize", out p)) { error = "missing igSetNextWindowSize"; return false; }
        _igSetNextWindowSize = (delegate* unmanaged[Cdecl]<ImVec2, int, void>)p;

        if (!TryGetProc(mod, "igBegin", out p)) { error = "missing igBegin"; return false; }
        _igBegin = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, byte>)p;

        if (!TryGetProc(mod, "igEnd", out p)) { error = "missing igEnd"; return false; }
        _igEnd = (delegate* unmanaged[Cdecl]<void>)p;

        if (!TryGetProc(mod, "igButton", out p)) { error = "missing igButton"; return false; }
        _igButton = (delegate* unmanaged[Cdecl]<IntPtr, ImVec2, byte>)p;

        if (!TryGetProc(mod, "igTextUnformatted", out p)) { error = "missing igTextUnformatted"; return false; }
        _igTextUnformatted = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)p;

        if (!TryGetProc(mod, "igSeparator", out p)) { error = "missing igSeparator"; return false; }
        _igSeparator = (delegate* unmanaged[Cdecl]<void>)p;

        if (!TryGetProc(mod, "igCheckbox", out p)) { error = "missing igCheckbox"; return false; }
        _igCheckbox = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte>)p;

        if (!TryGetProc(mod, "igGetForegroundDrawList_Nil", out p)) { error = "missing igGetForegroundDrawList"; return false; }
        _igGetForegroundDrawList = (delegate* unmanaged[Cdecl]<IntPtr>)p;

        if (!TryGetProc(mod, "ImDrawList_AddCircle", out p)) { error = "missing ImDrawList_AddCircle"; return false; }
        _igDrawListAddCircle = (delegate* unmanaged[Cdecl]<IntPtr, ImVec2, float, uint, int, float, void>)p;

        if (!TryGetProc(mod, "ImDrawList_AddLine", out p)) { error = "missing ImDrawList_AddLine"; return false; }
        _igDrawListAddLine = (delegate* unmanaged[Cdecl]<IntPtr, ImVec2, ImVec2, uint, float, void>)p;

        if (!TryGetProc(mod, "ImDrawList_AddCircleFilled", out p)) { error = "missing ImDrawList_AddCircleFilled"; return false; }
        _igDrawListAddCircleFilled = (delegate* unmanaged[Cdecl]<IntPtr, ImVec2, float, uint, int, void>)p;

        _imguiBound = true;
        error = "";
        return true;
    }

    private static bool TryGetProc(IntPtr module, string exportName, out IntPtr address)
    {
        address = GetProcAddress(module, exportName);
        return address != IntPtr.Zero;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleA(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ImVec2
    {
        public readonly float X;
        public readonly float Y;
        public ImVec2(float x, float y) { X = x; Y = y; }
    }
}

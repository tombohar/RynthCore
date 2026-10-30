// ============================================================================
//  RynthCore.Engine - ImGui/ImGuiAssertLog.cs
//  Surfaces Dear ImGui's soft asserts in the engine log.
//
//  Our cimgui build routes IM_ASSERT into a native ring buffer instead of
//  aborting (native/cimgui/rynth_imconfig.h). Poll() runs on AC's thread after
//  each ImGui frame: it reads one int, and only formats and logs when new
//  failures appeared. The first MaxLoggedInFull are logged individually; after
//  that a count is logged at most once a minute so a per-frame assert can't
//  flood the log.
// ============================================================================

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.ImGuiBackend;

internal static unsafe class ImGuiAssertLog
{
    private const int MaxLoggedInFull = 50;
    private const int RingSlots = 16; // kAssertSlots in rynth_cimgui_ext.cpp

    private static delegate* unmanaged[Cdecl]<int> _getCount;
    private static delegate* unmanaged[Cdecl]<int, byte*, int, int> _getText;
    private static int _seen;
    private static int _loggedInFull;
    private static int _suppressed;
    private static long _lastSummaryTicks;

    /// <summary>Resolves the ring-buffer exports. Called by ImGuiSelfTest once the build is verified.</summary>
    public static void Bind(IntPtr cimguiModule)
    {
        if (NativeLibrary.TryGetExport(cimguiModule, "RynthImGui_GetAssertCount", out IntPtr count) &&
            NativeLibrary.TryGetExport(cimguiModule, "RynthImGui_GetAssertText", out IntPtr text))
        {
            _getCount = (delegate* unmanaged[Cdecl]<int>)count;
            _getText = (delegate* unmanaged[Cdecl]<int, byte*, int, int>)text;
            // Asserts from an earlier engine generation (the DLL outlives RL) are not ours to report.
            _seen = _getCount();
        }
    }

    public static void Poll()
    {
        if (_getCount == null) return;
        int count = _getCount();
        if (count == _seen) return;

        int first = Math.Max(_seen, count - RingSlots);
        _suppressed += first - _seen; // overwritten before we could read them
        byte* buf = stackalloc byte[256];
        for (int i = first; i < count; i++)
        {
            if (_loggedInFull >= MaxLoggedInFull) { _suppressed++; continue; }
            int len = _getText(i, buf, 256);
            if (len < 0) { _suppressed++; continue; }
            _loggedInFull++;
            RynthLog.UI($"ImGui assert #{i + 1}: {Marshal.PtrToStringAnsi((IntPtr)buf, len)}");
        }
        _seen = count;

        if (_suppressed > 0)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _lastSummaryTicks >= Stopwatch.Frequency * 60)
            {
                RynthLog.UI($"ImGui asserts: {_suppressed} more not logged individually ({count} total this load).");
                _suppressed = 0;
                _lastSummaryTicks = now;
            }
        }
    }
}

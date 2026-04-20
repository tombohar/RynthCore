// ═══════════════════════════════════════════════════════════════════════════
//  RynthCore.Engine — ImGui/ViewportProbe.cs
//  One-shot diagnostic run at startup. Determines the byte offsets of each
//  ImGuiPlatformIO callback field in the running cimgui.dll, so that we can
//  install Win32/DX9 platform callbacks by writing IntPtrs directly into
//  the native struct (ImGui.NET exposes callback fields as get-only).
//
//  Remove/gate this once offsets are documented and wired into a static table.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace RynthCore.Engine.ImGuiBackend;

internal static class ViewportProbe
{
    // Every Platform_* and Renderer_* callback field ImGui.NET exposes.
    // Probing strategy: write a unique sentinel at each 4-byte slot, read
    // every getter, record which getter sees the sentinel, restore the slot.
    private static readonly string[] FieldsToProbe =
    {
        "Platform_CreateWindow",
        "Platform_DestroyWindow",
        "Platform_ShowWindow",
        "Platform_SetWindowPos",
        "Platform_GetWindowPos",
        "Platform_SetWindowSize",
        "Platform_GetWindowSize",
        "Platform_SetWindowFocus",
        "Platform_GetWindowFocus",
        "Platform_GetWindowMinimized",
        "Platform_SetWindowTitle",
        "Platform_SetWindowAlpha",
        "Platform_UpdateWindow",
        "Platform_RenderWindow",
        "Platform_SwapBuffers",
        "Platform_GetWindowDpiScale",
        "Platform_OnChangedViewport",
        "Platform_GetWindowWorkAreaInsets",
        "Platform_CreateVkSurface",
        "Renderer_CreateWindow",
        "Renderer_DestroyWindow",
        "Renderer_SetWindowSize",
        "Renderer_RenderWindow",
        "Renderer_SwapBuffers",
    };

    // ImGuiViewport fields worth locating so we know where ImGui.NET reads from
    // vs the authoritative bytes. These are read-back tests — no writes.
    private static readonly string[] ViewportFieldsToLog =
    {
        "ID",
        "Flags",
        "Pos",
        "Size",
        "WorkPos",
        "WorkSize",
        "DpiScale",
        "ParentViewportId",
        "DrawData",
        "RendererUserData",
        "PlatformUserData",
        "PlatformHandle",
        "PlatformHandleRaw",
        "PlatformWindowCreated",
        "PlatformRequestMove",
        "PlatformRequestResize",
        "PlatformRequestClose",
    };

    private const int ScanBytes = 768;

    public static void Run()
    {
        try
        {
            RynthLog.Info("ViewportProbe: ---- begin ----");
            RunPlatformIOProbe();
            RunMainViewportDump();
            RynthLog.Info("ViewportProbe: ---- end ----");
        }
        catch (Exception ex)
        {
            RynthLog.Info($"ViewportProbe: FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static unsafe void RunPlatformIOProbe()
    {
        ImGuiPlatformIOPtr pio = ImGuiNET.ImGui.GetPlatformIO();
        IntPtr pioNative = (IntPtr)pio.NativePtr;
        RynthLog.Info($"ViewportProbe: PlatformIO native = 0x{pioNative.ToInt64():X8}");

        // Log what each getter returns BEFORE probing, so we can see the stock zero state.
        var propertyCache = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
        Type ptrType = typeof(ImGuiPlatformIOPtr);
        foreach (string name in FieldsToProbe)
        {
            PropertyInfo? prop = ptrType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            bool intptrLike = prop != null && (
                prop.PropertyType == typeof(IntPtr) ||
                (prop.PropertyType.IsByRef && prop.PropertyType.GetElementType() == typeof(IntPtr)));
            if (intptrLike)
            {
                propertyCache[name] = prop!;
            }
            else
            {
                RynthLog.Info($"ViewportProbe:   {name}: NOT FOUND on ImGuiPlatformIOPtr (type={prop?.PropertyType.Name ?? "null"})");
            }
        }

        var discovered = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int off = 0; off < ScanBytes; off += 4)
        {
            IntPtr orig = Marshal.ReadIntPtr(pioNative, off);
            IntPtr sentinel = new IntPtr(unchecked((int)0xAB000000) | off);
            Marshal.WriteIntPtr(pioNative, off, sentinel);

            foreach (var kv in propertyCache)
            {
                IntPtr read;
                try
                {
                    read = (IntPtr)kv.Value.GetValue(pio)!;
                }
                catch
                {
                    continue;
                }

                if (read == sentinel && !discovered.ContainsKey(kv.Key))
                {
                    discovered[kv.Key] = off;
                }
            }

            Marshal.WriteIntPtr(pioNative, off, orig);
        }

        // Emit in ascending offset order so the mapping is obvious in the log.
        var sorted = new List<KeyValuePair<string, int>>(discovered);
        sorted.Sort((a, b) => a.Value.CompareTo(b.Value));
        RynthLog.Info($"ViewportProbe: discovered {sorted.Count}/{propertyCache.Count} offsets:");
        foreach (var kv in sorted)
            RynthLog.Info($"ViewportProbe:   +{kv.Value,4}  {kv.Key}");

        foreach (string name in FieldsToProbe)
        {
            if (propertyCache.ContainsKey(name) && !discovered.ContainsKey(name))
                RynthLog.Info($"ViewportProbe:   MISSING {name} (getter exists but sentinel never matched)");
        }

        // Hex dump of the first chunk so we can visually scan for anomalies.
        DumpRange(pioNative, 0, 256, "PlatformIO");
    }

    private static unsafe void RunMainViewportDump()
    {
        ImGuiViewportPtr main = ImGuiNET.ImGui.GetMainViewport();
        IntPtr vp = (IntPtr)main.NativePtr;
        RynthLog.Info($"ViewportProbe: MainViewport native = 0x{vp.ToInt64():X8}");

        Type vptrType = typeof(ImGuiViewportPtr);
        foreach (string name in ViewportFieldsToLog)
        {
            PropertyInfo? prop = vptrType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null)
            {
                RynthLog.Info($"ViewportProbe:   MainViewport.{name}: (no such property)");
                continue;
            }

            object? val;
            try { val = prop.GetValue(main); }
            catch (Exception ex) { val = $"<throw {ex.GetType().Name}>"; continue; }

            RynthLog.Info($"ViewportProbe:   MainViewport.{name} ({prop.PropertyType.Name}) = {val}");
        }

        DumpRange(vp, 0, 160, "MainViewport");
    }

    private static void DumpRange(IntPtr basePtr, int startOffset, int length, string label)
    {
        for (int off = startOffset; off < startOffset + length; off += 16)
        {
            uint w0 = unchecked((uint)Marshal.ReadInt32(basePtr, off + 0));
            uint w1 = unchecked((uint)Marshal.ReadInt32(basePtr, off + 4));
            uint w2 = unchecked((uint)Marshal.ReadInt32(basePtr, off + 8));
            uint w3 = unchecked((uint)Marshal.ReadInt32(basePtr, off + 12));
            RynthLog.Info($"ViewportProbe:   {label} +{off,4}  {w0:X8} {w1:X8} {w2:X8} {w3:X8}");
        }
    }
}

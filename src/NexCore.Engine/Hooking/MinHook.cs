// ═══════════════════════════════════════════════════════════════════════════
//  NexCore.Engine — Hooking/MinHook.cs
//  Minimal P/Invoke wrapper for MinHook (minhook.x86.dll).
//
//  Get the DLL from:
//    https://github.com/TsudaKageworthy/minhook/releases
//    Download minhook.x86.dll → place next to NexCore.Engine.dll
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Runtime.InteropServices;

namespace NexCore.Engine.Hooking;

internal static class MinHook
{
    private const string DLL = "minhook.x86.dll";

    // ─── Status codes (must match MinHook's MH_STATUS enum) ────────────
    public const int MH_OK                       = 0;
    public const int MH_ERROR_ALREADY_INITIALIZED = 1;
    public const int MH_ERROR_NOT_INITIALIZED     = 2;

    // ─── Core API ─────────────────────────────────────────────────────

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
    public static extern int MH_Initialize();

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
    public static extern int MH_Uninitialize();

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
    public static extern int MH_CreateHook(
        IntPtr pTarget,
        IntPtr pDetour,
        out IntPtr ppOriginal);

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
    public static extern int MH_RemoveHook(IntPtr pTarget);

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
    public static extern int MH_EnableHook(IntPtr pTarget);

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
    public static extern int MH_DisableHook(IntPtr pTarget);

    /// <summary>Enable/disable all hooks at once.</summary>
    public static readonly IntPtr MH_ALL_HOOKS = IntPtr.Zero;

    // ─── Status to string ─────────────────────────────────────────────

    [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr MH_StatusToString(int status);

    public static string StatusString(int status)
    {
        IntPtr ptr = MH_StatusToString(status);
        return ptr != IntPtr.Zero
            ? Marshal.PtrToStringAnsi(ptr) ?? $"Unknown({status})"
            : $"Unknown({status})";
    }

    /// <summary>
    /// Convenience: Initialize + CreateHook + EnableHook in one call.
    /// Returns the trampoline pointer to the original function.
    /// Throws on failure.
    /// </summary>
    public static IntPtr Hook(IntPtr target, IntPtr detour)
    {
        int status = MH_Initialize();
        if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED)
            throw new InvalidOperationException($"MH_Initialize failed: {StatusString(status)}");

        status = MH_CreateHook(target, detour, out IntPtr original);
        if (status != MH_OK)
            throw new InvalidOperationException($"MH_CreateHook failed: {StatusString(status)}");

        status = MH_EnableHook(target);
        if (status != MH_OK)
            throw new InvalidOperationException($"MH_EnableHook failed: {StatusString(status)}");

        return original;
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  RynthCore.Engine — Hooking/MinHook.cs
//  Minimal P/Invoke wrapper for MinHook (minhook.x86.dll).
//
//  Get the DLL from:
//    https://github.com/TsudaKageworthy/minhook/releases
//    Download minhook.x86.dll → place next to RynthCore.Engine.dll
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.Hooking;

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
    /// Race-safe hook install for <c>[UnmanagedCallersOnly]</c> detours that store the
    /// original as an <c>IntPtr</c> field.
    ///
    /// Writes <paramref name="original"/> (the trampoline pointer) BEFORE enabling the
    /// hook so a detour that fires immediately on the game thread never reads a zero
    /// original pointer.  A memory barrier separates the write from MH_EnableHook.
    /// Throws on failure.
    /// </summary>
    public static void Hook(IntPtr target, IntPtr detour, out IntPtr original)
    {
        EnsureInitialized();

        // MH_CreateHook writes the trampoline through the ref — the caller's field
        // is populated here, before the hook goes live.
        int status = MH_CreateHook(target, detour, out original);
        if (status != MH_OK)
            throw new InvalidOperationException($"MH_CreateHook failed: {StatusString(status)}");

        // Full fence: ensure the write above is globally visible before
        // MH_EnableHook patches the target instruction.
        Thread.MemoryBarrier();

        status = MH_EnableHook(target);
        if (status != MH_OK)
            throw new InvalidOperationException($"MH_EnableHook failed: {StatusString(status)}");
    }

    /// <summary>
    /// Race-safe hook install for delegate-based detours (two-step pattern).
    /// Call this first, then build and store the original delegate, add a
    /// <c>Thread.MemoryBarrier()</c>, then call <see cref="Enable"/>.
    ///
    /// <code>
    /// IntPtr trampoline = MinHook.HookCreate(target, detourPtr);
    /// _originalDelegate = Marshal.GetDelegateForFunctionPointer&lt;T&gt;(trampoline);
    /// Thread.MemoryBarrier();
    /// MinHook.Enable(target);
    /// </code>
    /// </summary>
    public static IntPtr HookCreate(IntPtr target, IntPtr detour)
    {
        EnsureInitialized();

        int status = MH_CreateHook(target, detour, out IntPtr original);
        if (status != MH_OK)
            throw new InvalidOperationException($"MH_CreateHook failed: {StatusString(status)}");

        return original;
    }

    /// <summary>Enables a hook previously created with <see cref="HookCreate"/>.</summary>
    public static void Enable(IntPtr target)
    {
        int status = MH_EnableHook(target);
        if (status != MH_OK)
            throw new InvalidOperationException($"MH_EnableHook failed: {StatusString(status)}");
    }

    private static void EnsureInitialized()
    {
        int status = MH_Initialize();
        if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED)
            throw new InvalidOperationException($"MH_Initialize failed: {StatusString(status)}");
    }
}

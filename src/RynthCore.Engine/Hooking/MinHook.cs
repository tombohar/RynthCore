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

internal static unsafe class MinHook
{
    private const string DLL = "minhook.x86.dll";
    private const string LoaderDll = "RynthCore.Loader.dll";

    // ─── Status codes (must match MinHook's MH_STATUS enum) ────────────
    public const int MH_OK                       = 0;
    public const int MH_ERROR_ALREADY_INITIALIZED = 1;
    public const int MH_ERROR_NOT_INITIALIZED     = 2;
    public const int MH_ERROR_ALREADY_CREATED    = 3;
    public const int MH_ERROR_NOT_CREATED        = 4;
    public const int MH_ERROR_ENABLED            = 5;
    public const int MH_ERROR_DISABLED           = 6;

    // ─── Core API ─────────────────────────────────────────────────────
    //
    // The native loader (native/Loader, 2.0+) exports MinHook's API itself: every
    // target gets a permanent stub and a reload only re-points it, so no hook ever
    // points into an unloaded engine (docs/UNLOADABLE_ENGINE_PLAN.md). The engine uses
    // that facade when the loader has it (RcHooks_Version), else minhook.x86.dll as
    // before (the NativeAOT loader, or the engine loaded directly).

    private static readonly Api s_api = ResolveApi();

    /// <summary>True when hooks go through the native loader's facade.</summary>
    public static bool UsesLoaderFacade => s_api.IsFacade;

    public static int MH_Initialize() => s_api.Initialize();

    public static int MH_Uninitialize() => s_api.Uninitialize();

    public static int MH_CreateHook(IntPtr pTarget, IntPtr pDetour, out IntPtr ppOriginal)
    {
        IntPtr original = IntPtr.Zero;
        int status = s_api.Create(pTarget, pDetour, &original);
        ppOriginal = original;
        return status;
    }

    public static int MH_RemoveHook(IntPtr pTarget) => s_api.Remove(pTarget);

    public static int MH_EnableHook(IntPtr pTarget) => s_api.Enable(pTarget);

    public static int MH_DisableHook(IntPtr pTarget) => s_api.Disable(pTarget);

    /// <summary>Enable/disable all hooks at once.</summary>
    public static readonly IntPtr MH_ALL_HOOKS = IntPtr.Zero;

    private static IntPtr MH_StatusToString(int status) => s_api.StatusToString(status);

    private sealed class Api
    {
        public bool IsFacade;
        public delegate* unmanaged[Stdcall]<int> Initialize;
        public delegate* unmanaged[Stdcall]<int> Uninitialize;
        public delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int> Create;
        public delegate* unmanaged[Stdcall]<IntPtr, int> Remove;
        public delegate* unmanaged[Stdcall]<IntPtr, int> Enable;
        public delegate* unmanaged[Stdcall]<IntPtr, int> Disable;
        public delegate* unmanaged[Stdcall]<int, IntPtr> StatusToString;
    }

    private static Api ResolveApi()
    {
        IntPtr module = GetModuleHandleW(LoaderDll);
        bool facade = module != IntPtr.Zero && GetProcAddress(module, "RcHooks_Version") != IntPtr.Zero;
        if (!facade)
            module = LoadMinHookFromEngineFolders();

        IntPtr Get(string name)
        {
            IntPtr fn = GetProcAddress(module, name);
            if (fn == IntPtr.Zero)
                throw new EntryPointNotFoundException($"{(facade ? LoaderDll : DLL)} has no export {name}");
            return fn;
        }

        return new Api
        {
            IsFacade = facade,
            Initialize = (delegate* unmanaged[Stdcall]<int>)Get("MH_Initialize"),
            Uninitialize = (delegate* unmanaged[Stdcall]<int>)Get("MH_Uninitialize"),
            Create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Get("MH_CreateHook"),
            Remove = (delegate* unmanaged[Stdcall]<IntPtr, int>)Get("MH_RemoveHook"),
            Enable = (delegate* unmanaged[Stdcall]<IntPtr, int>)Get("MH_EnableHook"),
            Disable = (delegate* unmanaged[Stdcall]<IntPtr, int>)Get("MH_DisableHook"),
            StatusToString = (delegate* unmanaged[Stdcall]<int, IntPtr>)Get("MH_StatusToString"),
        };
    }

    /// <summary>
    /// minhook.x86.dll by full path from the engine's folders (where the early preload found it).
    /// Never by bare name: Windows' search order (the AC client folder, the current directory,
    /// PATH) would load a planted minhook.x86.dll into the client when ours is missing.
    /// </summary>
    private static IntPtr LoadMinHookFromEngineFolders()
    {
        string? path = EntryPoint.FindNativeDll(DLL);
        if (path == null)
            throw new DllNotFoundException($"{DLL} was not found beside the engine (other folders are not searched).");
        return NativeLibrary.Load(path);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

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
        int status = CreateHookWithReloadRecovery(target, detour, out original);
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

        int status = CreateHookWithReloadRecovery(target, detour, out IntPtr original);
        if (status != MH_OK)
            throw new InvalidOperationException($"MH_CreateHook failed: {StatusString(status)}");

        return original;
    }

    /// <summary>
    /// Wraps <see cref="MH_CreateHook"/> with hot-reload recovery: when the
    /// process has been hot-reloaded (gen2 engine init), the previous engine's
    /// trampolines for the same target are still in MinHook's pool and a fresh
    /// CreateHook returns <see cref="MH_ERROR_ALREADY_CREATED"/>. We disable +
    /// remove the stale hook and retry.
    ///
    /// Safety contract: caller (gen2 init) must run AFTER gen1 has gone through
    /// EngineLifecycle.Shutdown — which calls MH_DisableHook(MH_ALL_HOOKS) and
    /// joins all known engine threads (TickPump, HeartbeatLogger, plugin
    /// shutdowns, AvaloniaOverlay.Stop). After that point no live caller is in
    /// flight through the dangling trampoline, so MH_RemoveHook is safe.
    /// </summary>
    private static int CreateHookWithReloadRecovery(IntPtr target, IntPtr detour, out IntPtr original)
    {
        int status = MH_CreateHook(target, detour, out original);
        if (status != MH_ERROR_ALREADY_CREATED)
            return status;

        // Stale hook from a previous engine generation. Disable (idempotent if
        // already disabled), remove (frees the trampoline), then retry.
        MH_DisableHook(target);
        int rmStatus = MH_RemoveHook(target);
        if (rmStatus != MH_OK)
            return rmStatus; // surface as failure — caller throws

        return MH_CreateHook(target, detour, out original);
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

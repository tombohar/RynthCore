// ============================================================================
//  RynthCore.Engine - Compatibility/UiFlowHooks.cs
//
//  Screen-mode and teardown hooks (Chorizite comparison gap #4, 2026-10-05):
//
//    UIFlow::UseNewMode   (fallback 0x00479AA0)  void __thiscall(UIFlow*)
//      AC switches screens here: intro, character select, the world, char gen,
//      disconnected... It copies the pending mode (+0x90) into the current mode
//      (+0x8C), destroys the old screen's UI object (+0x94) and builds the new
//      one. Read offline from our client: 0x00479B13..0x00479B35 (mov [esi+8C],ecx
//      from [esi+90]; old UI at +94 released with vfunc +0x24/+0x00).
//    Client::Cleanup      (fallback 0x004118D0)  void __thiscall(Client*)
//      The client's shutdown: tears down UI, net, database and preferences
//      (virtual calls at its top). Called once, from gmClient::Cleanup.
//
//  Events (all raised on AC's main thread):
//    ScreenLeaving(old, new)  - inside UseNewMode, BEFORE AC destroys the old
//                               screen's UI. Leaving the world, the engine drops
//                               every cached gameplay-UI pointer right here.
//    ScreenChanged(old, new)  - after AC built the new screen (plugins get
//                               RynthPluginOnScreenChanged on the next pump frame).
//    ClientCleanup            - before AC tears anything down; plugins get
//                               RynthPluginOnClientCleanup (bounded wait), then the
//                               plugin pump stops ticking and the panels stop drawing.
//
//  Fallback: until the hook is live (failed to resolve, switched off), the UIFlow
//  mode the engine already polls at +0x8C (CharacterManagementHooks.MainThreadTick)
//  drives the same events, one tick late and without the "before" guarantee.
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal static unsafe class UiFlowHooks
{
    // UIFlow modes (UIFlow+0x8C), the client's own data ids.
    public const int ModeNone = 0;
    public const int ModeIntro = 0x10000001;
    public const int ModeDisconnected = 0x10000002;
    public const int ModeDataPatch = 0x10000003;
    public const int ModeCredits = 0x10000005;
    public const int ModeGamePlay = 0x10000008;
    public const int ModeEpilogue = 0x10000009;
    public const int ModeCharacterManagement = 0x1000000A;
    public const int ModeCharGen = 0x1000000B;

    private const int UIFlowCurModeOffset = 0x8C;
    private const int UIFlowNewModeOffset = 0x90;

    // Fallback VAs (4,841,472-byte client). Pattern-scan is the source of truth.
    private const int UseNewModeFallbackVa = 0x00479AA0;
    private const int ClientCleanupFallbackVa = 0x004118D0;

    // UIFlow::UseNewMode: sub esp,0x90 / push esi / mov esi,ecx / mov eax,[esi+0x90] /
    // test eax,eax / jz rel32 / mov ecx,eax / xor edx,edx / div [imm32] / mov eax,[imm32].
    // The +0x90 pending-mode read and the hash-bucket div are the anchor. Unique, lands
    // at 0x00479AA0 (tools/pe_pattern.py CHECK, tools/ui_hooks_check.py).
    private static readonly byte?[] UseNewModePattern =
    [
        0x81, 0xEC, 0x90, 0x00, 0x00, 0x00, 0x56, 0x8B, 0xF1, 0x8B, 0x86, 0x90, 0x00, 0x00, 0x00,
        0x85, 0xC0, 0x0F, 0x84, null, null, null, null,
        0x8B, 0xC8, 0x33, 0xD2, 0xF7, 0x35, null, null, null, null,
        0xA1, null, null, null, null
    ];

    // Client::Cleanup: push esi / mov esi,ecx / mov eax,[esi] / call [eax+0x28] / call rel32 /
    // mov edx,[esi] / mov ecx,esi / call [edx+0x8C] / call rel32 / mov eax,[esi] / mov ecx,esi /
    // call [eax+0x84]: its run of virtual cleanups. Unique, lands at 0x004118D0.
    private static readonly byte?[] ClientCleanupPattern =
    [
        0x56, 0x8B, 0xF1, 0x8B, 0x06, 0xFF, 0x50, 0x28, 0xE8, null, null, null, null,
        0x8B, 0x16, 0x8B, 0xCE, 0xFF, 0x92, 0x8C, 0x00, 0x00, 0x00, 0xE8, null, null, null, null,
        0x8B, 0x06, 0x8B, 0xCE, 0xFF, 0x90, 0x84, 0x00, 0x00, 0x00
    ];

    private static readonly UiHookSlot UseNewModeSlot =
        UiHookRegistry.Register("UseNewMode", "UIFlow::UseNewMode - screen changes", UseNewModeFallbackVa);
    private static readonly UiHookSlot CleanupSlot =
        UiHookRegistry.Register("ClientCleanup", "Client::Cleanup - client teardown", ClientCleanupFallbackVa);

    private static IntPtr _originalUseNewMode;
    private static IntPtr _originalCleanup;
    private static int _initialized;

    private static int _mode;           // current UIFlow mode as last observed (0 = not known yet)
    private static int _previousMode;
    private static int _changes;
    private static string _lastSource = "none";
    private static int _cleanupStarted;
    private static int _useNewModeDepth;

    /// <summary>AC's main thread, inside UseNewMode before the old screen's UI is destroyed (hook only).</summary>
    public static event Action<int, int>? ScreenLeaving;
    /// <summary>AC's main thread, after the new screen was built (hook) or on the next tick (poll fallback).</summary>
    public static event Action<int, int>? ScreenChanged;
    /// <summary>AC's main thread, before Client::Cleanup runs. Once.</summary>
    public static event Action? ClientCleanup;

    /// <summary>The UIFlow mode as last observed; 0 until the first observation.</summary>
    public static int CurrentMode => Volatile.Read(ref _mode);
    public static int PreviousMode => Volatile.Read(ref _previousMode);

    /// <summary>True while AC shows the world, or while the mode isn't known yet (no change
    /// from the old behaviour then). Gate cached-AC-UI-pointer use on this.</summary>
    public static bool InWorldOrUnknown
    {
        get
        {
            int m = Volatile.Read(ref _mode);
            return m == ModeNone || m == ModeGamePlay;
        }
    }

    /// <summary>Client::Cleanup has started: AC is tearing its UI down. Never cleared.</summary>
    public static bool ClientCleanupStarted => Volatile.Read(ref _cleanupStarted) != 0;

    /// <summary>The UseNewMode detour is installed and switched on (else the poll drives the events).</summary>
    public static bool ScreenHookLive => UseNewModeSlot.Live;
    public static bool CleanupHookLive => CleanupSlot.Live;

    public static void Initialize()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
        {
            UseNewModeSlot.Reason = CleanupSlot.Reason = "acclient.exe not available";
            RynthLog.Compat("UiFlowHooks: acclient.exe not available - screen-mode and cleanup hooks left out (the mode poll stays).");
            return;
        }

        if (UiHookRegistry.SwitchedOn(UseNewModeSlot))
        {
            HookResolver.ResolveResult r = HookResolver.Resolve(text, "UiFlow.UseNewMode", UseNewModePattern, UseNewModeFallbackVa);
            delegate* unmanaged[Thiscall]<IntPtr, void> detour = &UseNewModeDetour;
            if (UiHookRegistry.TryInstall(UseNewModeSlot, r, (IntPtr)detour, out IntPtr tramp))
            {
                _originalUseNewMode = tramp;
                Thread.MemoryBarrier();
                UiHookRegistry.Enable(UseNewModeSlot);
            }
        }

        if (UiHookRegistry.SwitchedOn(CleanupSlot))
        {
            HookResolver.ResolveResult r = HookResolver.Resolve(text, "UiFlow.ClientCleanup", ClientCleanupPattern, ClientCleanupFallbackVa);
            delegate* unmanaged[Thiscall]<IntPtr, void> detour = &ClientCleanupDetour;
            if (UiHookRegistry.TryInstall(CleanupSlot, r, (IntPtr)detour, out IntPtr tramp))
            {
                _originalCleanup = tramp;
                Thread.MemoryBarrier();
                UiHookRegistry.Enable(CleanupSlot);
            }
        }

        RynthLog.Compat($"UiFlowHooks: UseNewMode {(UseNewModeSlot.Installed ? "hooked" : "not hooked (" + UseNewModeSlot.Reason + ") - the +0x8C poll drives screen events")}, " +
                        $"Client::Cleanup {(CleanupSlot.Installed ? "hooked" : "not hooked (" + CleanupSlot.Reason + ")")}.");
    }

    // ── Detours ──────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static void UseNewModeDetour(IntPtr uiFlow)
    {
        var original = (delegate* unmanaged[Thiscall]<IntPtr, void>)_originalUseNewMode;
        if (!UseNewModeSlot.Enabled || uiFlow == IntPtr.Zero || _useNewModeDepth > 0)
        {
            original(uiFlow);
            return;
        }

        int oldMode = 0, pending = 0;
        bool read = false;
        try
        {
            MainThreadGuard.RecordIfFirst();
            oldMode = *(int*)(uiFlow + UIFlowCurModeOffset);
            pending = *(int*)(uiFlow + UIFlowNewModeOffset);
            read = true;
            // AC destroys the old screen's UI inside the original: drop what points into it first.
            if (pending != 0 && pending != oldMode)
                RaiseLeaving(oldMode, pending, "hook");
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"UiFlowHooks: UseNewMode pre-step threw {ex.GetType().Name}: {ex.Message}"); } catch { }
        }

        _useNewModeDepth++;
        try { original(uiFlow); }
        finally { _useNewModeDepth--; }

        if (!read)
            return;
        try
        {
            int newMode = *(int*)(uiFlow + UIFlowCurModeOffset);
            if (newMode != oldMode)
                Observe(oldMode, newMode, "hook");
            else if (Volatile.Read(ref _mode) == ModeNone && newMode != ModeNone)
                Volatile.Write(ref _mode, newMode);
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"UiFlowHooks: UseNewMode post-step threw {ex.GetType().Name}: {ex.Message}"); } catch { }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static void ClientCleanupDetour(IntPtr client)
    {
        var original = (delegate* unmanaged[Thiscall]<IntPtr, void>)_originalCleanup;
        if (CleanupSlot.Enabled)
        {
            try { RaiseCleanup(); }
            catch (Exception ex)
            {
                try { RynthLog.Compat($"UiFlowHooks: Client::Cleanup pre-step threw {ex.GetType().Name}: {ex.Message}"); } catch { }
            }
        }
        original(client);
    }

    // ── Poll fallback ────────────────────────────────────────────────────

    /// <summary>
    /// AC's main thread (CharacterManagementHooks.MainThreadTick): the UIFlow mode it just
    /// read at +0x8C. Seeds the mode; when the UseNewMode hook isn't live, a change here
    /// raises the screen events instead (after the fact: the old screen is already gone).
    /// </summary>
    public static void OnModePolled(int mode)
    {
        int known = Volatile.Read(ref _mode);
        if (mode == known)
            return;
        if (known == ModeNone || UseNewModeSlot.Live)
        {
            // Seed (late attach, hot reload). With the hook live a difference here means the
            // hook missed a change (it was switched off for a while): correct it quietly.
            Volatile.Write(ref _mode, mode);
            return;
        }
        try
        {
            RaiseLeaving(known, mode, "poll");
            Observe(known, mode, "poll");
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"UiFlowHooks: poll fallback threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Raising ──────────────────────────────────────────────────────────

    private static void RaiseLeaving(int oldMode, int newMode, string source)
    {
        if (oldMode == ModeGamePlay)
            OnLeavingWorld();
        try { ScreenLeaving?.Invoke(oldMode, newMode); }
        catch (Exception ex) { RynthLog.Compat($"UiFlowHooks: ScreenLeaving handler threw {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>
    /// Leaving the world: AC is about to free gmGamePlayUI and every panel in it. The
    /// engine's per-frame users of cached panel pointers (EndScene TickHide for chat,
    /// radar, powerbar, retail vitals) run on this same thread, so clearing them here
    /// closes the window the pump-thread logout dispatch left open (it clears them a
    /// frame or more later). Idempotent: the logout dispatch clears them again.
    /// </summary>
    private static void OnLeavingWorld()
    {
        try { ChatHooks.ResetCachedInstance(); } catch { }
        try { RadarHooks.ResetCachedInstance(); } catch { }
        try { PowerbarHooks.ResetCachedInstance(); } catch { }
        try { RetailVitalsHooks.ResetCachedInstance(); } catch { }
        try { RetailToolbarHooks.ResetCachedInstance(); } catch { }
        try { AcShortcuts.Reset(); } catch { }
        try { AcKeymapLive.Reset(); } catch { }
        try { AcPanels.Reset(); } catch { }
        try { UiElementHooks.OnScreenLeaving(); } catch { }
    }

    private static void Observe(int oldMode, int newMode, string source)
    {
        Volatile.Write(ref _previousMode, oldMode);
        Volatile.Write(ref _mode, newMode);
        Interlocked.Increment(ref _changes);
        _lastSource = source;
        string note = $"{ModeName(oldMode)} -> {ModeName(newMode)} ({source})";
        UseNewModeSlot.LastNote = note;
        if (source == "hook") Interlocked.Increment(ref UseNewModeSlot.Fired);
        RynthLog.Compat($"UiFlowHooks: screen {note}.");

        try { ScreenChanged?.Invoke(oldMode, newMode); }
        catch (Exception ex) { RynthLog.Compat($"UiFlowHooks: ScreenChanged handler threw {ex.GetType().Name}: {ex.Message}"); }
        try { Plugins.PluginManager.QueueScreenChanged(oldMode, newMode); }
        catch (Exception ex) { RynthLog.Compat($"UiFlowHooks: queueing OnScreenChanged threw {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void RaiseCleanup()
    {
        if (Interlocked.Exchange(ref _cleanupStarted, 1) != 0)
            return;
        CleanupSlot.Note("Client::Cleanup entered");
        RynthLog.Compat("UiFlowHooks: Client::Cleanup - the client is tearing down; telling plugins, then stopping plugin ticks and panels.");
        try { UiElementHooks.OnClientCleanup(); } catch { }
        try { ClientCleanup?.Invoke(); }
        catch (Exception ex) { RynthLog.Compat($"UiFlowHooks: ClientCleanup handler threw {ex.GetType().Name}: {ex.Message}"); }
        try { Plugins.PluginManager.DispatchClientCleanup(500); }
        catch (Exception ex) { RynthLog.Compat($"UiFlowHooks: plugin cleanup dispatch threw {ex.GetType().Name}: {ex.Message}"); }
    }

    // ── Status ───────────────────────────────────────────────────────────

    public static string ModeName(int mode) => mode switch
    {
        ModeNone => "unknown",
        ModeIntro => "Intro",
        ModeDisconnected => "Disconnected",
        ModeDataPatch => "DataPatch",
        ModeCredits => "Credits",
        ModeGamePlay => "World",
        ModeEpilogue => "Epilogue",
        ModeCharacterManagement => "CharacterSelect",
        ModeCharGen => "CharGen",
        _ => $"0x{mode:X8}",
    };

    public static string DescribeState()
        => $"Screen: {ModeName(CurrentMode)} (before: {ModeName(PreviousMode)}), {Volatile.Read(ref _changes)} change(s), last via {_lastSource}; " +
           $"driven by {(UseNewModeSlot.Live ? "the UseNewMode hook" : "the +0x8C poll (fallback)")}; " +
           $"client cleanup {(ClientCleanupStarted ? "STARTED" : "not started")}.";
}

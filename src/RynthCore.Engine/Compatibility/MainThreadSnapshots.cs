namespace RynthCore.Engine.Compatibility;

/// <summary>
/// One main-thread refresh point for the small per-frame snapshots that the
/// plugin pump (and other engine threads) read instead of touching AC.
///
/// AC's client is single-threaded. The plugin pump ("RynthCore.NormalPluginPump")
/// and the helper threads must not call AC functions or read AC memory that the
/// main thread mutates or frees; they read values sampled here instead. Called
/// from two places that both run on AC's main thread:
///   * GameTickHooks.UseTimeDetour, after AC's own tick and the queue drain. It
///     fires in every mode, before login too.
///   * EngineFrameController.OnEndScene, next to the other prefetches (EndScene
///     is only hooked after login).
/// Each step throttles itself, so running from both is cheap. Nothing here may
/// allocate per call or wait on another thread.
/// </summary>
internal static class MainThreadSnapshots
{
    // Host ProbeClientHooksFn from the plugin pump: re-probe on the main thread.
    private static int _clientHookProbeRequested;
    private static long _nextSmartBoxProbeMs;

    public static void RequestClientHookProbe() =>
        System.Threading.Volatile.Write(ref _clientHookProbeRequested, 1);

    public static void Tick()
    {
        // Both call sites are AC's main thread by construction (same reasoning as
        // AcMainThreadQueue.Drain, which latches here too).
        MainThreadGuard.RecordIfFirst();
        if (!MainThreadGuard.IsOnMainThread())
            return;
        // Teardown: the detours stay live until MH_DisableHook(ALL) and Shutdown is
        // resetting hook state; nobody reads these snapshots any more (the pump is
        // stopped first), so stop touching AC here, like AcMainThreadQueue.Disarm.
        if (EngineLifecycle.IsShuttingDown)
            return;

        if (System.Threading.Volatile.Read(ref _clientHookProbeRequested) != 0
            && System.Threading.Interlocked.Exchange(ref _clientHookProbeRequested, 0) != 0)
        {
            try
            {
                ClientActionHooks.Probe();
                RynthLog.Compat("ProbeClientHooks: re-probed on AC's main thread (plugin request).");
            }
            catch { }
        }

        // Player id cache (off-thread GetPlayerId callers). Two loads, unthrottled.
        try { ClientHelperHooks.GetPlayerId(); } catch { }

        // The two SmartBox-based samples below must not trigger SmartBoxLocator's
        // lazy re-probe (a .text pattern scan) on every frame if the bootstrap probe
        // failed: retry it here at most every 10 s instead.
        bool smartBoxReady = SmartBoxLocator.IsInitialized;
        if (!smartBoxReady)
        {
            long now = System.Environment.TickCount64;
            if (now >= _nextSmartBoxProbeMs)
            {
                _nextSmartBoxProbeMs = now + 10_000;
                try { smartBoxReady = SmartBoxLocator.Probe(); } catch { }
            }
        }

        // Cast gate: CMotionInterp's pending-motion head, read here instead of on
        // the pump (the SmartBox player -> MovementManager -> CMotionInterp chain
        // dies with the player's physics object at logout / char switch).
        if (smartBoxReady)
        {
            try { CastGate.Sample(); } catch { }
        }

        // Player pose + heading (seqlock copy): nav, radar, Jumper, Nav3D, GetCurCoords.
        // Publishes "no pose" while SmartBox is not located.
        try { PlayerPhysicsHooks.PublishPoseSnapshot(smartBoxReady); } catch { }

        // The qualities walks below (base vitals, enchantment registries) need a
        // live player object. Between gmGamePlayUI::RecvNotice_Logoff and the pump's
        // logout dispatch (which zeroes KnownPlayerQualitiesPtr) the cached pointer
        // can outlive the object, and during a DB-cache teardown AC is freeing them.
        bool playerLive = !LogoutLifecycleHooks.HasObservedLogout
                          && !DbCacheTeardownHooks.TeardownActive
                          && ClientHelperHooks.GetPlayerId() != 0;

        // Base (unbuffed) max vitals for host GetPlayerBaseVitalsFn (~1 Hz).
        if (playerLive)
        {
            try { PlayerVitalsHooks.PrefetchBaseVitals(); } catch { }
        }

        // PlayerInReadyPosition for host IsPlayerReadyFn (~5 Hz).
        try { ClientCombatHooks.SampleReady(); } catch { }

        // Account name cache (host GetAccountNameFn): only while still cold.
        try { AccountHooks.WarmFromMainThread(); } catch { }

        // Enchantment registries: the player's (250 ms, or next tick after an
        // enchantment event) and any objects plugins asked about (500 ms).
        try { EnchantmentHooks.PrefetchPlayerEnchantments(playerLive); } catch { }
        if (playerLive)
        {
            try { EnchantmentHooks.PrefetchWatchedObjectEnchantments(); } catch { }
            // Inventory panel: sends the one requested item action, watches it, and
            // captures the packs (throttled; returns at once while the panel is closed
            // and nothing is in flight). Only while the player object is live, and held
            // through a world transition (dead / portal / landblock jump): it SENDS item
            // actions, and those wait like the AcMainThreadQueue's (WorldTransitionState).
            if (!PlayerLifecycleLog.InWorldTransition)
            {
                try { InventoryModel.MainThreadTick(); } catch { }
            }
            // Skills panel: ranks, XP spent and buffs (500 ms, only while the panel is open).
            // After the enchantment snapshot so its buffs are this tick's.
            try { PlayerProgressHooks.Prefetch(); } catch { }
        }

        // Both send server commands (a mastery raise, a vault withdraw), so like the
        // Inventory panel they wait out a world transition; their requests stay pending.
        if (!PlayerLifecycleLog.InWorldTransition)
        {
            // Aelrynth skill mastery: the /mastery-data request after login, on panel open
            // and after a raise (only on Aelrynth; a few compares when nothing is wanted).
            try { MasteryFeed.MainThreadTick(playerLive); } catch { }
            // Aelrynth account vault (the Inventory panel's Vault view): reads on request only.
            try { VaultFeed.MainThreadTick(playerLive); } catch { }
        }
    }
}

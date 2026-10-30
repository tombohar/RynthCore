using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class CombatModeHooks
{
    private const int SetCombatModeFallbackVa = 0x0056CB80;

    // ClientCombatSystem::SetCombatMode(int newMode, int playerRequested)
    // Loads current mode from [esi+0x1C], compares with new arg from [esp+0x14],
    // jumps if equal. Distinctive 0x0F 0x84 EA 01 00 00 (je rel32 +0x1EA).
    private static readonly byte?[] SetCombatModePattern =
    [
        0x51, 0x53, 0x56, 0x8B, 0xF1, 0x8B, 0x46, 0x1C,
        0x57, 0x8B, 0x7C, 0x24, 0x14, 0x3B, 0xF8, 0x8B,
        0xD8, 0x0F, 0x84, 0xEA, 0x01, 0x00, 0x00, 0x8A,
        0x44, 0x24, 0x18, 0x84, 0xC0
    ];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetCombatModeDelegate(IntPtr thisPtr, int newCombatMode, int playerRequested);

    private static SetCombatModeDelegate? _originalSetCombatMode;
    private static SetCombatModeDelegate? _setCombatModeDetour;
    private static IntPtr _targetAddress;
    private static string _statusMessage = "Not probed yet.";
    // Defaults to NonCombat (not 0) so an off-thread reader that arrives before
    // the first SetCombatMode detour fire still gets a sane value instead of an
    // unnormalized 0.
    private static int _lastObservedCombatMode = CombatActionHooks.CombatModeNonCombat;

    // ClientCombatSystem::s_pCombatSystem — pointer to the singleton in .data.
    // This is a DATA address (not code), so it can't be pattern-scanned in .text;
    // it's left as a fallback constant. If the binary's .data layout shifted, the
    // first dereference would AV and CrashLogger would log it as
    // ACCESS_VIOLATION inside the engine's thiscall thunk.
    private const uint CombatSystemPtrVa = 0x0087166C;
    private const int CombatModeOffset = 28;

    // Phase B: resolve s_pCombatSystem's address by code-xref ("mov [s_pCombatSystem],esi" =
    // 89 35 <addr>), operand at offset 2; the VA stays as fallback. Resolved in Initialize.
    private static readonly byte?[] PatXrefCombatSystemPtr = [ 0x89, 0x35, null, null, null, null, 0x8B, 0x06 ];
    private static uint _combatSystemPtrAddr = CombatSystemPtrVa;

    public static bool IsInstalled { get; private set; }
    public static string StatusMessage => _statusMessage;

    /// <summary>The ClientCombatSystem singleton, or Zero. Read-only (diagnostics).</summary>
    internal static IntPtr ReadCombatSystemPtr()
    {
        IntPtr slot = (IntPtr)_combatSystemPtrAddr;
        return ClientObjectHooks.IsReadablePointer(slot) ? Marshal.ReadIntPtr(slot) : IntPtr.Zero;
    }

    // Deep-audit finding #4 (2026-06-18): this used to raw-dereference the
    // ClientCombatSystem singleton unconditionally, including off AC's main
    // thread — a teardown-window read (relog/portal, singleton being torn
    // down) can land on a freed/mid-reassignment pointer, an uncatchable AV
    // under NativeAOT. SetCombatModeDetour already runs ON the main thread
    // (it's the inbound-echo hook) and maintains _lastObservedCombatMode as a
    // main-thread-verified cache — off-thread callers get that instead of
    // touching AC memory at all.
    public static unsafe int ReadCurrentCombatMode()
    {
        if (!MainThreadGuard.IsOnMainThread())
            return Volatile.Read(ref _lastObservedCombatMode);

        try
        {
            IntPtr combatSystemPtrAddr = (IntPtr)_combatSystemPtrAddr;
            if (!ClientObjectHooks.IsReadablePointer(combatSystemPtrAddr))
                return Volatile.Read(ref _lastObservedCombatMode);
            IntPtr combatSystem = *(IntPtr*)_combatSystemPtrAddr;
            if (combatSystem == IntPtr.Zero || !ClientObjectHooks.IsReadablePointer(combatSystem + CombatModeOffset))
                return CombatActionHooks.CombatModeNonCombat;
            int raw = NormalizeCombatMode(*(int*)(combatSystem + CombatModeOffset));

            // Heal the off-thread cache from this live main-thread read.
            //
            // _lastObservedCombatMode is only advanced by the detour, which fires
            // on a mode CHANGE. After a hot reload or a fresh client it starts at
            // its NonCombat default, so if the character is already in Magic and
            // stays there, no change ever occurs, the detour never fires, and the
            // cache stays wrong forever. Off-thread callers then read NonCombat
            // and request Magic in a loop that cannot succeed — nothing needs
            // changing — until EnsureMagicMode's deadlock recovery re-equips the
            // wand and forces a real transition. That is the 8s stance stall seen
            // at 01:49 (after a client restart) and 10:36 (after a reload) on
            // 2026-09-03, both visible as an unnecessary unwield/re-wield.
            //
            // Safe for the change-detection above: the detour compares against
            // this value, so healing it to the truth only suppresses events that
            // would have reported a change that did not happen.
            Volatile.Write(ref _lastObservedCombatMode, raw);
            return raw;
        }
        catch
        {
            return Volatile.Read(ref _lastObservedCombatMode);
        }
    }

    /// <summary>
    /// Request a combat-mode change the way the CLIENT does it, by calling
    /// ClientCombatSystem::SetCombatMode(newMode, playerRequested: 1).
    ///
    /// Why this exists: CombatActionHooks.ChangeCombatMode binds the function that
    /// writes network opcode 0x53 (CM_Combat::Event_ChangeCombatMode) and calls it
    /// directly. That is the bottom of the stack — it tells the SERVER, and nothing
    /// else. SetCombatMode is the layer above it, and it does three things:
    ///   * sets the client's own mode field,
    ///   * calls CM_Combat::Event_ChangeCombatMode  → sends 0x53 to the server,
    ///   * calls CM_Combat::SendNotice_SetCombatMode → notifies the UI widgets
    ///     (gmCombatUI / gmToolbarUI / gmSpellcastingUI each have a
    ///     RecvNotice_SetCombatMode handler).
    /// Going in below it meant the stance widgets were never told, so the toolbar
    /// icon kept showing Peace while the character was genuinely in Magic and
    /// casting — reported from live play 2026-09-02.
    ///
    /// playerRequested: 1 marks this as player-initiated, which is the path the
    /// toolbar button itself takes. The inbound server echo arrives as 0, and the
    /// UI-refresh branch is skipped there because a real click has already updated
    /// the widget optimistically — which is exactly why the echo alone never fixed
    /// the icon for us.
    ///
    /// Calls the trampoline, so our own detour does not re-enter. _lastObservedCombatMode
    /// is deliberately NOT updated here: it must keep meaning "what the server
    /// decided", or a server that refuses the stance (the documented wand-stance
    /// wedge) would look like it had accepted it. The echo updates it.
    ///
    /// Main thread only — the caller marshals. Returns false if the hook isn't
    /// installed or the singleton isn't readable, so the caller can fall back.
    /// </summary>
    public static unsafe bool RequestCombatMode(int combatMode)
    {
        if (!IsInstalled || _originalSetCombatMode == null)
        { LogRequestPath(combatMode, -1, "FALLBACK: hook not installed"); return false; }
        if (!MainThreadGuard.IsOnMainThread())
        { LogRequestPath(combatMode, -1, "FALLBACK: off main thread"); return false; }

        try
        {
            IntPtr combatSystemPtrAddr = (IntPtr)_combatSystemPtrAddr;
            if (!ClientObjectHooks.IsReadablePointer(combatSystemPtrAddr))
            { LogRequestPath(combatMode, -1, "FALLBACK: s_pCombatSystem address unreadable"); return false; }
            IntPtr combatSystem = *(IntPtr*)_combatSystemPtrAddr;
            if (combatSystem == IntPtr.Zero || !ClientObjectHooks.IsReadablePointer(combatSystem + CombatModeOffset))
            { LogRequestPath(combatMode, -1, "FALLBACK: combat system singleton unreadable"); return false; }

            // Read the field SetCombatMode itself compares against ([esi+0x1C]).
            // Its prologue jumps past the whole body when the request equals the
            // current mode — so if these are equal, the call sends NO packet and
            // notifies nothing, and the stance silently will not change. That is
            // the leading suspect for the 8s stance stall seen on force rebuff
            // (2026-09-03 01:49), so record it explicitly rather than guess.
            int cur = *(int*)(combatSystem + CombatModeOffset);

            // Same healing as ReadCurrentCombatMode: this is a live main-thread
            // read, so it is strictly better than a stale cached default.
            Volatile.Write(ref _lastObservedCombatMode, NormalizeCombatMode(cur));

            // The client already believes it is in this mode, so SetCombatMode
            // would jump straight past its body: no 0x53 to the server and no UI
            // notice. But the SERVER can disagree with the client's field — that
            // is exactly the case worth sending — so decline here and let the
            // caller fall through to the raw opcode-0x53 sender, which is what
            // this code did unconditionally before the client-level path existed.
            // Confirmed live 2026-09-03 01:57: req=8 against clientField=8
            // swallowed every retry for ~8s until EnsureMagicMode's deadlock
            // recovery re-equipped the wand and forced a real transition.
            //
            // Nothing is lost on the UI side by taking the raw path here: the
            // widgets read the same field, and it already holds the requested
            // mode, so there is no stale icon to correct.
            if (cur == combatMode)
            {
                LogRequestPath(combatMode, cur, "declining — client field already equals request (SetCombatMode would no-op); using raw 0x53 sender so the server is still told");
                return false;
            }

            _originalSetCombatMode(combatSystem, combatMode, 1);

            LogRequestPath(combatMode, cur, "sent via ClientCombatSystem::SetCombatMode(playerRequested=1)");
            return true;
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"CombatModeHooks: RequestCombatMode({combatMode}) threw {ex.GetType().Name}: {ex.Message}"); } catch { }
            return false;
        }
    }

    private static string _lastRequestLog = "";
    private static long _lastRequestLogTicks;

    /// <summary>One line per combat-mode request telling us which path ran.
    /// Stance flips are rare, but a retry loop would repeat, so identical
    /// messages are throttled to 1/s.</summary>
    private static void LogRequestPath(int requested, int current, string outcome)
    {
        try
        {
            string msg = $"CombatModeHooks: RequestCombatMode(req={requested}, clientField={current}) — {outcome}";
            long now = Environment.TickCount64;
            if (msg == _lastRequestLog && (now - _lastRequestLogTicks) < 1000) return;
            _lastRequestLog = msg;
            _lastRequestLogTicks = now;
            RynthLog.Compat(msg);
        }
        catch { }
    }

    public static void Initialize()
    {
        if (IsInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        var resolved = HookResolver.Resolve(textSection, "CombatModeHooks.SetCombatMode",
            SetCombatModePattern, SetCombatModeFallbackVa);
        if (!resolved.Success)
        {
            _statusMessage = $"Resolve failed ({resolved.Detail}).";
            return;
        }

        _combatSystemPtrAddr = (uint)HookResolver.ResolveData(textSection, "CombatMode.s_pCombatSystem", PatXrefCombatSystemPtr, 2, (int)CombatSystemPtrVa).Address.ToInt32();

        try
        {
            _targetAddress = resolved.Address;
            _setCombatModeDetour = SetCombatModeDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_setCombatModeDetour);
            _originalSetCombatMode = Marshal.GetDelegateForFunctionPointer<SetCombatModeDelegate>(
                MinHook.HookCreate(_targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(_targetAddress);

            IsInstalled = true;
            _statusMessage = $"Hooked ClientCombatSystem::SetCombatMode @ 0x{_targetAddress.ToInt32():X8}.";
            RynthLog.Compat($"CombatModeHooks: install ok.");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"CombatModeHooks: install threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int _fires;
    private static int _lastLoggedMode = int.MinValue;
    private static long _lastLogTicks;

    private static void SetCombatModeDetour(IntPtr thisPtr, int newCombatMode, int playerRequested)
    {
        // First detour to fire latches AC's main thread ID so background
        // threads (DecalCoexistence plugin-tick, panel snapshot, etc.) can
        // refuse to call into AC's non-thread-safe internals.
        MainThreadGuard.RecordIfFirst();

        RecursionGuard.Tick("CombatModeHooks.SetCombatMode");
        // This detour fires on the INBOUND server echo (RecvNotice_SetCombatMode ->
        // ClientCombatSystem::SetCombatMode), so it reflects what the SERVER decided,
        // not what the plugin requested. The old `_fires <= 3` lifetime cap went dark
        // hours before any wedge, hiding the diagnosis. Log every distinct mode plus
        // repeats throttled to 1/s, so a wand-stance wedge (server re-echoing
        // NonCombat=1 against repeated Magic requests) is visible instead of silent.
        // See rynthai_combat_busy_wedge ADDENDUM 5.
        long nowTicks = Environment.TickCount64;
        if (++_fires <= 3 || newCombatMode != _lastLoggedMode || (nowTicks - _lastLogTicks) >= 1000)
        {
            _lastLoggedMode = newCombatMode;
            _lastLogTicks = nowTicks;
            RynthLog.Compat($"CombatModeHooks: SetCombatMode fired #{_fires} mode={newCombatMode} requested={playerRequested} (main TID = {MainThreadGuard.MainThreadId})");
        }

        try
        {
            _originalSetCombatMode!(thisPtr, newCombatMode, playerRequested);
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"CombatModeHooks: original threw {ex.GetType().Name}: {ex.Message}"); } catch { }
            throw;
        }

        int currentCombatMode = NormalizeCombatMode(newCombatMode);
        int previousCombatMode = Interlocked.Exchange(ref _lastObservedCombatMode, currentCombatMode);
        if (currentCombatMode == previousCombatMode)
            return;

        PluginManager.QueueCombatModeChange(currentCombatMode, previousCombatMode);
    }

    // Main-thread refresh of the off-thread cache (2026-10-05). After the engine hot reload at
    // 13:42:55 Drakkon was in Magic (he had just cast Heal Self), but _lastObservedCombatMode
    // starts at NonCombat in a new engine and only the SetCombatMode detour (a mode CHANGE) or a
    // main-thread ReadCurrentCombatMode moved it. The plugin pump reads off the main thread, so
    // the engine synced "combat mode 1" to the plugins at 13:43:03 and RynthAi's heal at 13:43:12
    // asked for a Magic stance the client already had (RequestCombatMode req=8 clientField=8),
    // then waited out the stance settle. Drain calls this every frame; it reads the client's field
    // at most every MainThreadRefreshMs and, when the cache was wrong, heals it and tells the
    // plugins the way the detour does.
    private static long _lastMainThreadRefreshMs;
    private const long MainThreadRefreshMs = 250;

    /// <summary>AC's main thread only (AcMainThreadQueue.Drain).</summary>
    internal static unsafe void MainThreadRefresh()
    {
        if (!IsInstalled) return;
        long now = Environment.TickCount64;
        if (now - _lastMainThreadRefreshMs < MainThreadRefreshMs) return;
        _lastMainThreadRefreshMs = now;
        try
        {
            IntPtr slot = (IntPtr)_combatSystemPtrAddr;
            if (!ClientObjectHooks.IsReadablePointer(slot)) return;
            IntPtr combatSystem = *(IntPtr*)slot;
            if (combatSystem == IntPtr.Zero || !ClientObjectHooks.IsReadablePointer(combatSystem + CombatModeOffset)) return;
            int raw = NormalizeCombatMode(*(int*)(combatSystem + CombatModeOffset));
            if (raw is not (CombatActionHooks.CombatModeNonCombat or CombatActionHooks.CombatModeMelee
                            or CombatActionHooks.CombatModeMissile or CombatActionHooks.CombatModeMagic))
                return;   // not a mode (a singleton being torn down): leave the cache alone
            int previous = Interlocked.Exchange(ref _lastObservedCombatMode, raw);
            if (raw == previous) return;
            RynthLog.Compat($"CombatModeHooks: the cached combat mode was {previous} but the client reads {raw} " +
                            "(no SetCombatMode seen since this engine started, e.g. after a hot reload): cache healed, plugins told.");
            PluginManager.QueueCombatModeChange(raw, previous);
        }
        catch { }
    }

    private static int NormalizeCombatMode(int combatMode)
    {
        return combatMode switch
        {
            CombatActionHooks.CombatModeNonCombat => CombatActionHooks.CombatModeNonCombat,
            CombatActionHooks.CombatModeMelee => CombatActionHooks.CombatModeMelee,
            CombatActionHooks.CombatModeMissile => CombatActionHooks.CombatModeMissile,
            CombatActionHooks.CombatModeMagic => CombatActionHooks.CombatModeMagic,
            _ => combatMode
        };
    }
}

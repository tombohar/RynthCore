using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class BusyCountHooks
{
    // Fallback VAs (4,841,472-byte client). Pattern-scan is now the source of truth.
    private const int IncrementBusyCountFallbackVa = 0x00565610;
    private const int DecrementBusyCountFallbackVa = 0x00565630;
    private const int UpdateCursorStateFallbackVa  = 0x005653D0;

    // ClientUISystem struct field offset for m_cBusy (confirmed via runtime dump)
    private const int OffsetMCBusy = 0x14;

    // ClientUISystem::IncrementBusyCount — entire function is short:
    //   mov edx,[ecx+14]; inc edx; mov eax,edx; cmp eax,1; mov [ecx+14],edx;
    //   jne +5; jmp UpdateCursorState; ret
    private static readonly byte?[] IncrementBusyCountPattern =
    [
        0x8B, 0x51, 0x14, 0x42, 0x8B, 0xC2, 0x83, 0xF8,
        0x01, 0x89, 0x51, 0x14, 0x75, 0x05,
        0xE9, null, null, null, null,        // jmp rel32 -> UpdateCursorState
        0xC3
    ];

    // ClientUISystem::DecrementBusyCount — also short:
    //   dec [ecx+14]; jne +5; jmp UpdateCursorState; ret
    private static readonly byte?[] DecrementBusyCountPattern =
    [
        0xFF, 0x49, 0x14, 0x75, 0x05,
        0xE9, null, null, null, null,        // jmp rel32 -> UpdateCursorState
        0xC3
    ];

    // ClientUISystem::UpdateCursorState — large prologue with cmp/setcc pair.
    private static readonly byte?[] UpdateCursorStatePattern =
    [
        0x83, 0xEC, 0x08, 0x53, 0x55, 0x56, 0x57, 0x89,
        0x4C, 0x24, 0x10,
        0xE8, null, null, null, null,        // call rel32
        0x85, 0xC0, 0x0F, 0x95, 0xC3, 0x33, 0xC0, 0x84,
        0xDB
    ];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void BusyCountDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void UpdateCursorStateDelegate(IntPtr thisPtr);

    private static BusyCountDelegate? _originalIncrementBusyCount;
    private static BusyCountDelegate? _incrementBusyCountDetour;
    private static BusyCountDelegate? _originalDecrementBusyCount;
    private static BusyCountDelegate? _decrementBusyCountDetour;
    private static IntPtr _incrementTargetAddress;
    private static IntPtr _decrementTargetAddress;
    private static IntPtr _updateCursorStateAddress;
    private static string _statusMessage = "Not probed yet.";
    private static int _incrementDispatchCount;
    private static int _decrementDispatchCount;
    private static int _netBusyCount;
    private static IntPtr _lastThisPtr;

    public static bool IsInstalled { get; private set; }
    public static string StatusMessage => _statusMessage;

    /// <summary>Returns 0 if the character is idle, positive if a UI action is in progress.</summary>
    public static int GetBusyState() => Math.Max(0, _netBusyCount);

    /// <summary>Force-reset the client's busy count to zero and re-evaluate the cursor.</summary>
    public static void ForceResetBusyCount()
    {
        if (!IsInstalled || _lastThisPtr == IntPtr.Zero)
            return;

        int was = _netBusyCount;

        if (_originalDecrementBusyCount != null)
        {
            int calls = Math.Max(was, 3);
            for (int i = 0; i < calls && i < 20; i++)
                _originalDecrementBusyCount(_lastThisPtr);
        }

        Interlocked.Exchange(ref _netBusyCount, 0);

        try { Marshal.WriteInt32(_lastThisPtr + OffsetMCBusy, 0); }
        catch { /* non-fatal */ }

        CommandInterpreterHooks.ClearAllCommands();
        CommandInterpreterHooks.TakeControlFromServer();
        CommandInterpreterHooks.PlayerTeleported();

        if (_updateCursorStateAddress != IntPtr.Zero)
        {
            try
            {
                var updateCursor = Marshal.GetDelegateForFunctionPointer<UpdateCursorStateDelegate>(_updateCursorStateAddress);
                updateCursor(_lastThisPtr);
            }
            catch { /* non-fatal */ }
        }

        RynthLog.Verbose($"Compat: force-reset busy count (was {was})");
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

        var inc = HookResolver.Resolve(textSection, "BusyCountHooks.IncrementBusyCount",
            IncrementBusyCountPattern, IncrementBusyCountFallbackVa);
        var dec = HookResolver.Resolve(textSection, "BusyCountHooks.DecrementBusyCount",
            DecrementBusyCountPattern, DecrementBusyCountFallbackVa);
        var cursor = HookResolver.Resolve(textSection, "BusyCountHooks.UpdateCursorState",
            UpdateCursorStatePattern, UpdateCursorStateFallbackVa);

        if (cursor.Success) _updateCursorStateAddress = cursor.Address;

        if (!inc.Success || !dec.Success)
        {
            _statusMessage = $"Resolve failed — increment={inc.Detail}, decrement={dec.Detail}.";
            RynthLog.Compat($"BusyCountHooks: {_statusMessage}");
            return;
        }

        try
        {
            _incrementTargetAddress = inc.Address;
            _incrementBusyCountDetour = IncrementBusyCountDetour;
            IntPtr incrementDetourPtr = Marshal.GetFunctionPointerForDelegate(_incrementBusyCountDetour);
            _originalIncrementBusyCount = Marshal.GetDelegateForFunctionPointer<BusyCountDelegate>(
                MinHook.HookCreate(_incrementTargetAddress, incrementDetourPtr));

            _decrementTargetAddress = dec.Address;
            _decrementBusyCountDetour = DecrementBusyCountDetour;
            IntPtr decrementDetourPtr = Marshal.GetFunctionPointerForDelegate(_decrementBusyCountDetour);
            _originalDecrementBusyCount = Marshal.GetDelegateForFunctionPointer<BusyCountDelegate>(
                MinHook.HookCreate(_decrementTargetAddress, decrementDetourPtr));

            Thread.MemoryBarrier();
            MinHook.Enable(_incrementTargetAddress);
            MinHook.Enable(_decrementTargetAddress);

            IsInstalled = true;
            _statusMessage = $"Hooked busy-count seams (inc=0x{_incrementTargetAddress.ToInt32():X8}, dec=0x{_decrementTargetAddress.ToInt32():X8}).";
            RynthLog.Compat($"BusyCountHooks: hooks installed.");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"BusyCountHooks: install threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int _incFires, _decFires;

    // ── Auto-watchdog: force-clear busy if it stays >0 too long ──────────
    // Engine-side safety net so the bot self-heals when its busy tracking
    // desyncs from AC's m_cBusy (e.g., a hook miss, RDP-eaten chat input
    // preventing the user from running /ra clearbusy manually, or AC's
    // own busy state going stuck after a server-rejected action).
    //
    // Trigger: any of our main-thread detours can call CheckWatchdog().
    // SmartBoxHooks.DispatchGameEventDetour wires this in — it fires
    // frequently on AC's main thread while in-world, so the watchdog
    // gets a tick at least every server event.
    //
    // Heuristic: if busy has been positive for > BusyWatchdogTimeoutMs
    // with no transitions back to zero, fire ForceResetBusyCount. We
    // do this regardless of why it got stuck — combat-urgent or not —
    // because the alternative is an inert bot.
    private static long _busyBecamePositiveTickMs;
    private static long _lastWatchdogFireTickMs;
    private const long BusyWatchdogTimeoutMs = 5_000;
    private const long BusyWatchdogCooldownMs = 2_000;

    // Real-field watchdog. The shadow counter (_netBusyCount) DESYNCS from AC's
    // real m_cBusy whenever AC mutates the field through a path we don't hook (or
    // a detour is missed): the counter drifts to ~0 while the real field stays
    // pinned > 0. A pinned real m_cBusy makes AC LOCALLY refuse combat-mode
    // changes (its SetCombatMode never even fires) and casts ("You're too
    // busy!") — wedging the bot in NonCombat (can't re-enter Magic to buff/fight)
    // with NEITHER counter-based watchdog firing, because the counter says idle.
    // So the watchdog must also trust the REAL field, read straight from
    // [ClientUISystem + OffsetMCBusy]. (Diagnosed 2026-06-09: SetCombatMode
    // stopped firing for 30+ min while ChangeCombatMode was re-sent and 191
    // "too busy" refusals piled up, yet zero force-clears — classic desync.)
    private static long _realBusyPositiveTickMs;
    private const long RealBusyWatchdogTimeoutMs = 4_000;

    private static int ReadRealBusy()
    {
        IntPtr p = _lastThisPtr;
        if (p == IntPtr.Zero) return -1;
        try { return Marshal.ReadInt32(p + OffsetMCBusy); }
        catch { return -1; }
    }

    /// <summary>
    /// Auto-clear busy state if it has been positive for too long.
    /// Safe to call from any thread; only performs work on AC's main
    /// thread (via MainThreadGuard) since ForceResetBusyCount writes
    /// AC memory and calls AC functions that aren't thread-safe.
    /// </summary>
    public static void CheckWatchdog()
    {
        long now = Environment.TickCount64;

        // ── Real-field watchdog (catches counter desync) ─────────────────────
        // Trust AC's REAL m_cBusy over our shadow counter: when they diverge a
        // pinned real field is what actually wedges the client (mode-change /
        // cast refusal) while _netBusyCount reads ~0 and the counter watchdog
        // below never fires. A legit action returns the field to 0 within ~2-3s
        // (resetting the timer); only a genuinely stuck field reaches the 4s
        // timeout. Field read is harmless off-thread, but ForceResetBusyCount
        // mutates AC, so only clear on the main thread.
        if (MainThreadGuard.IsOnMainThread())
        {
            int realBusy = ReadRealBusy();
            if (realBusy > 0)
            {
                long sinceR = Volatile.Read(ref _realBusyPositiveTickMs);
                if (sinceR == 0)
                {
                    Volatile.Write(ref _realBusyPositiveTickMs, now);
                }
                else if (now - sinceR > RealBusyWatchdogTimeoutMs
                         && now - Volatile.Read(ref _lastWatchdogFireTickMs) > BusyWatchdogCooldownMs)
                {
                    Volatile.Write(ref _lastWatchdogFireTickMs, now);
                    RynthLog.Compat($"BusyCountHooks: REAL m_cBusy stuck at {realBusy} for {now - sinceR}ms (counter={_netBusyCount}) — force-clearing desync.");
                    ForceResetBusyCount();
                    Volatile.Write(ref _realBusyPositiveTickMs, 0);
                    Volatile.Write(ref _busyBecamePositiveTickMs, 0);
                    return;
                }
            }
            else if (realBusy == 0)
            {
                Volatile.Write(ref _realBusyPositiveTickMs, 0);
            }
        }

        // ── Counter-based watchdog (original) ────────────────────────────────
        long stuckSince = Volatile.Read(ref _busyBecamePositiveTickMs);
        if (stuckSince == 0)
            return;

        long elapsed = now - stuckSince;
        if (elapsed < BusyWatchdogTimeoutMs)
            return;

        if (!MainThreadGuard.IsOnMainThread())
            return;

        long lastFire = Volatile.Read(ref _lastWatchdogFireTickMs);
        if (now - lastFire < BusyWatchdogCooldownMs)
            return;

        Volatile.Write(ref _lastWatchdogFireTickMs, now);
        RynthLog.Compat($"BusyCountHooks: watchdog auto-resetting — busy stuck at {_netBusyCount} for {elapsed}ms.");
        ForceResetBusyCount();
        Volatile.Write(ref _busyBecamePositiveTickMs, 0);
    }

    private static void IncrementBusyCountDetour(IntPtr thisPtr)
    {
        RecursionGuard.Tick("BusyCountHooks.Increment");
        _lastThisPtr = thisPtr;
        if (++_incFires <= 3)
            RynthLog.Compat($"BusyCountHooks: Increment fired #{_incFires} this=0x{thisPtr.ToInt32():X8}");
        try { _originalIncrementBusyCount!(thisPtr); }
        catch (Exception ex) { try { RynthLog.Compat($"BusyCountHooks: Increment original threw {ex.GetType().Name}: {ex.Message}"); } catch { } throw; }
        Interlocked.Increment(ref _incrementDispatchCount);
        int after = Interlocked.Increment(ref _netBusyCount);
        if (after == 1)
            Volatile.Write(ref _busyBecamePositiveTickMs, Environment.TickCount64);
        PluginManager.QueueBusyCountIncremented();
    }

    private static void DecrementBusyCountDetour(IntPtr thisPtr)
    {
        RecursionGuard.Tick("BusyCountHooks.Decrement");
        if (_lastThisPtr == IntPtr.Zero)
            _lastThisPtr = thisPtr;
        if (++_decFires <= 3)
            RynthLog.Compat($"BusyCountHooks: Decrement fired #{_decFires} this=0x{thisPtr.ToInt32():X8}");
        try { _originalDecrementBusyCount!(thisPtr); }
        catch (Exception ex) { try { RynthLog.Compat($"BusyCountHooks: Decrement original threw {ex.GetType().Name}: {ex.Message}"); } catch { } throw; }
        Interlocked.Increment(ref _decrementDispatchCount);
        int after = Interlocked.Decrement(ref _netBusyCount);
        if (after <= 0)
            Volatile.Write(ref _busyBecamePositiveTickMs, 0);
        PluginManager.QueueBusyCountDecremented();
    }
}

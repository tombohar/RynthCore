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
    private static int _lastObservedCombatMode;

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

    public static unsafe int ReadCurrentCombatMode()
    {
        try
        {
            IntPtr combatSystem = *(IntPtr*)_combatSystemPtrAddr;
            if (combatSystem == IntPtr.Zero)
                return CombatActionHooks.CombatModeNonCombat;
            int raw = *(int*)(combatSystem + CombatModeOffset);
            return NormalizeCombatMode(raw);
        }
        catch
        {
            return CombatActionHooks.CombatModeNonCombat;
        }
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

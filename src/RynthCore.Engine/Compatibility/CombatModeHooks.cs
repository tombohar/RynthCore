using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class CombatModeHooks
{
    private const int SetCombatModeVa = 0x0056CB80;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetCombatModeDelegate(IntPtr thisPtr, int newCombatMode, int playerRequested);

    private static SetCombatModeDelegate? _originalSetCombatMode;
    private static SetCombatModeDelegate? _setCombatModeDetour;
    private static IntPtr _targetAddress;
    private static string _statusMessage = "Not probed yet.";
    private static int _lastObservedCombatMode;

    // ClientCombatSystem::s_pCombatSystem — pointer to the singleton in .data
    private const uint CombatSystemPtrVa = 0x0087166C;
    // Offset of combatMode (COMBAT_MODE) within ClientCombatSystem:
    //   ClientSystem(8) + IInputActionCallback(4) + QualityChangeHandler(4)
    //   + Turbine_RefCount(8) + jump_pending(1) + m_bTrackingTarget(1) + pad(2) = 28
    private const int CombatModeOffset = 28;

    public static bool IsInstalled { get; private set; }
    public static string StatusMessage => _statusMessage;

    /// <summary>
    /// Reads the current combat mode directly from ClientCombatSystem::combatMode.
    /// Returns NonCombat if the pointer is null or unreadable.
    /// </summary>
    public static unsafe int ReadCurrentCombatMode()
    {
        try
        {
            IntPtr combatSystem = *(IntPtr*)CombatSystemPtrVa;
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

        int funcOff = SetCombatModeVa - textSection.TextBaseVa;
        if (funcOff < 0 || funcOff >= textSection.Bytes.Length)
        {
            _statusMessage = $"ClientCombatSystem::SetCombatMode VA out of range @ 0x{SetCombatModeVa:X8}.";
            RynthLog.Compat($"Compat: combat-mode hook failed - {_statusMessage}");
            return;
        }

        byte firstByte = textSection.Bytes[funcOff];
        if (firstByte is 0x00 or 0xCC or 0xC3)
        {
            _statusMessage = $"ClientCombatSystem::SetCombatMode looks invalid @ 0x{SetCombatModeVa:X8} (opcode 0x{firstByte:X2}).";
            RynthLog.Compat($"Compat: combat-mode hook failed - {_statusMessage}");
            return;
        }

        try
        {
            _targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _setCombatModeDetour = SetCombatModeDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_setCombatModeDetour);
            _originalSetCombatMode = Marshal.GetDelegateForFunctionPointer<SetCombatModeDelegate>(MinHook.HookCreate(_targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(_targetAddress);

            IsInstalled = true;
            _statusMessage = $"Hooked ClientCombatSystem::SetCombatMode @ 0x{_targetAddress.ToInt32():X8}.";
            RynthLog.Compat($"Compat: combat-mode hook ready - SetCombatMode=0x{_targetAddress.ToInt32():X8}, firstByte=0x{firstByte:X2}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: combat-mode hook failed - {ex.Message}");
        }
    }

    private static void SetCombatModeDetour(IntPtr thisPtr, int newCombatMode, int playerRequested)
    {
        try
        {
            _originalSetCombatMode!(thisPtr, newCombatMode, playerRequested);
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"Compat: combat-mode detour error - {ex.GetType().Name}: {ex.Message}"); } catch { }
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

    private static string FormatCombatMode(int combatMode)
    {
        return combatMode switch
        {
            CombatActionHooks.CombatModeNonCombat => "noncombat(1)",
            CombatActionHooks.CombatModeMelee => "melee(2)",
            CombatActionHooks.CombatModeMissile => "missile(4)",
            CombatActionHooks.CombatModeMagic => "magic(8)",
            _ => combatMode.ToString()
        };
    }
}

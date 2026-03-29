using System;
using System.Runtime.InteropServices;

namespace NexCore.Engine.Compatibility;

internal static class CombatActionHooks
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool TargetedMeleeAttackDelegate(uint targetId, int attackHeight, float powerLevel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool TargetedMissileAttackDelegate(uint targetId, int attackHeight, float accuracyLevel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool ChangeCombatModeDelegate(int combatMode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool CancelAttackDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool QueryHealthDelegate(uint targetId);

    public const int AttackHeightHigh = 1;
    public const int AttackHeightMedium = 2;
    public const int AttackHeightLow = 3;

    public const int CombatModeNonCombat = 1;
    public const int CombatModeMelee = 2;
    public const int CombatModeMissile = 4;
    public const int CombatModeMagic = 8;

    private static readonly byte[] CombatPrologue = [0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8];

    private static TargetedMeleeAttackDelegate? _meleeAttack;
    private static TargetedMissileAttackDelegate? _missileAttack;
    private static ChangeCombatModeDelegate? _changeCombatMode;
    private static CancelAttackDelegate? _cancelAttack;
    private static QueryHealthDelegate? _queryHealth;
    private static string _statusMessage = "Not probed yet.";

    public static bool IsInitialized { get; private set; }
    public static bool HasMeleeAttack => _meleeAttack != null;
    public static bool HasMissileAttack => _missileAttack != null;
    public static bool HasChangeCombatMode => _changeCombatMode != null;
    public static bool HasCancelAttack => _cancelAttack != null;
    public static bool HasQueryHealth => _queryHealth != null;
    public static string StatusMessage => _statusMessage;

    public static bool Probe(Action<string>? log = null)
    {
        Reset();

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection, log))
        {
            _statusMessage = "acclient.exe not available.";
            return false;
        }

        try
        {
            byte[] text = textSection.Bytes;

            byte[] cancelOpcode = [0xC7, 0x02, 0xB7, 0x01, 0x00, 0x00];
            int cancelOpcodeOff = PatternScanner.FindPattern(text, cancelOpcode);
            if (cancelOpcodeOff < 0)
            {
                _statusMessage = "CancelAttack opcode 0x1B7 not found.";
                log?.Invoke("Compat: combat probe failed - CancelAttack opcode 0x1B7 not found.");
                return false;
            }

            int cancelFuncOff = PatternScanner.FindPrologueBefore(text, cancelOpcodeOff, CombatPrologue);
            if (cancelFuncOff < 0)
            {
                _statusMessage = "CancelAttack prologue not found.";
                log?.Invoke("Compat: combat probe failed - CancelAttack prologue not found.");
                return false;
            }

            int regionStart = Math.Max(0, cancelOpcodeOff - 0x500);
            int regionEnd = Math.Min(text.Length, cancelOpcodeOff + 0x500);

            byte[] changeModeOpcode = [0xC7, 0x02, 0x53, 0x00, 0x00, 0x00];
            int changeModeOff = PatternScanner.FindPatternInRegion(text, changeModeOpcode, regionStart, regionEnd);
            int changeModeFuncOff = changeModeOff >= 0
                ? PatternScanner.FindPrologueBefore(text, changeModeOff, CombatPrologue)
                : -1;

            byte[] queryHealthOpcode = [0xC7, 0x02, 0xBF, 0x01, 0x00, 0x00];
            int queryHealthOff = PatternScanner.FindPatternInRegion(text, queryHealthOpcode, regionStart, regionEnd);
            int queryHealthFuncOff = queryHealthOff >= 0
                ? PatternScanner.FindPrologueBefore(text, queryHealthOff, CombatPrologue)
                : -1;

            byte[] meleeOpcode = [0xC7, 0x02, 0x08, 0x00, 0x00, 0x00];
            int meleeOff = PatternScanner.FindPatternInRegion(text, meleeOpcode, regionStart, regionEnd);
            int meleeFuncOff = meleeOff >= 0
                ? PatternScanner.FindPrologueBefore(text, meleeOff, CombatPrologue)
                : -1;

            byte[] missileOpcode = [0xC7, 0x02, 0x0A, 0x00, 0x00, 0x00];
            int missileOff = PatternScanner.FindPatternInRegion(text, missileOpcode, regionStart, regionEnd);
            int missileFuncOff = missileOff >= 0
                ? PatternScanner.FindPrologueBefore(text, missileOff, CombatPrologue)
                : -1;

            if (changeModeFuncOff < 0 || queryHealthFuncOff < 0 || meleeFuncOff < 0 || missileFuncOff < 0)
            {
                _statusMessage =
                    $"Incomplete. mode={changeModeFuncOff >= 0}, health={queryHealthFuncOff >= 0}, melee={meleeFuncOff >= 0}, missile={missileFuncOff >= 0}.";
                log?.Invoke($"Compat: combat probe incomplete - {_statusMessage}");
                return false;
            }

            if (!PatternScanner.VerifyBytes(text, cancelFuncOff, CombatPrologue) ||
                !PatternScanner.VerifyBytes(text, changeModeFuncOff, CombatPrologue) ||
                !PatternScanner.VerifyBytes(text, queryHealthFuncOff, CombatPrologue) ||
                !PatternScanner.VerifyBytes(text, meleeFuncOff, CombatPrologue) ||
                !PatternScanner.VerifyBytes(text, missileFuncOff, CombatPrologue))
            {
                _statusMessage = "Prologue verification failed.";
                log?.Invoke("Compat: combat probe failed - prologue verification failed.");
                return false;
            }

            int cancelVa = textSection.TextBaseVa + cancelFuncOff;
            int changeModeVa = textSection.TextBaseVa + changeModeFuncOff;
            int queryHealthVa = textSection.TextBaseVa + queryHealthFuncOff;
            int meleeVa = textSection.TextBaseVa + meleeFuncOff;
            int missileVa = textSection.TextBaseVa + missileFuncOff;

            _cancelAttack = Marshal.GetDelegateForFunctionPointer<CancelAttackDelegate>(new IntPtr(cancelVa));
            _changeCombatMode = Marshal.GetDelegateForFunctionPointer<ChangeCombatModeDelegate>(new IntPtr(changeModeVa));
            _queryHealth = Marshal.GetDelegateForFunctionPointer<QueryHealthDelegate>(new IntPtr(queryHealthVa));
            _meleeAttack = Marshal.GetDelegateForFunctionPointer<TargetedMeleeAttackDelegate>(new IntPtr(meleeVa));
            _missileAttack = Marshal.GetDelegateForFunctionPointer<TargetedMissileAttackDelegate>(new IntPtr(missileVa));

            IsInitialized = true;
            _statusMessage = "Ready.";

            log?.Invoke($"Compat: combat hooks ready - cancel=0x{cancelVa:X8}, mode=0x{changeModeVa:X8}, health=0x{queryHealthVa:X8}, melee=0x{meleeVa:X8}, missile=0x{missileVa:X8}");
            return true;
        }
        catch (Exception ex)
        {
            Reset();
            _statusMessage = ex.Message;
            log?.Invoke($"Compat: combat probe failed - {ex.Message}");
            return false;
        }
    }

    public static bool MeleeAttack(uint targetId, int attackHeight, float powerLevel)
    {
        if (_meleeAttack == null)
            return false;

        try
        {
            return _meleeAttack(targetId, attackHeight, Math.Clamp(powerLevel, 0f, 1f));
        }
        catch
        {
            return false;
        }
    }

    public static bool MissileAttack(uint targetId, int attackHeight, float accuracyLevel)
    {
        if (_missileAttack == null)
            return false;

        try
        {
            return _missileAttack(targetId, attackHeight, Math.Clamp(accuracyLevel, 0f, 1f));
        }
        catch
        {
            return false;
        }
    }

    public static bool ChangeCombatMode(int combatMode)
    {
        if (_changeCombatMode == null)
            return false;

        try
        {
            return _changeCombatMode(combatMode);
        }
        catch
        {
            return false;
        }
    }

    public static bool CancelAttack()
    {
        if (_cancelAttack == null)
            return false;

        try
        {
            return _cancelAttack();
        }
        catch
        {
            return false;
        }
    }

    public static bool QueryHealth(uint targetId)
    {
        if (_queryHealth == null)
            return false;

        try
        {
            return _queryHealth(targetId);
        }
        catch
        {
            return false;
        }
    }

    public static int MapAttackHeight(int uiHeight)
    {
        return uiHeight switch
        {
            0 => AttackHeightLow,
            2 => AttackHeightHigh,
            _ => AttackHeightMedium
        };
    }

    private static void Reset()
    {
        _meleeAttack = null;
        _missileAttack = null;
        _changeCombatMode = null;
        _cancelAttack = null;
        _queryHealth = null;
        IsInitialized = false;
    }
}

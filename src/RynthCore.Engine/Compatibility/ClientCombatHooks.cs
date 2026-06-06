using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Hooks into the AC client's ClientCombatSystem — the high-level combat pipeline
/// that handles turn-to-face, power bar build, and attack execution naturally.
/// Contrast with CombatActionHooks which sends raw game actions (bypassing facing).
/// </summary>
internal static class ClientCombatHooks
{
    // ── VAs from Chorizite ClientCombatSystem (confirmed from IDA .text comments) ──

    /// <summary>Static cdecl: ClientCombatSystem* GetCombatSystem()</summary>
    private const int GetCombatSystemVa = 0x0056B210;

    /// <summary>Global pointer: ClientCombatSystem* s_pCombatSystem</summary>
    private const int CombatSystemPtrVa = 0x0087166C;

    // Thiscall instance methods on ClientCombatSystem:
    private const int SetRequestedAttackHeightVa = 0x0056D640;
    private const int StartAttackRequestVa       = 0x0056CD90;
    private const int EndAttackRequestVa         = 0x0056CE30;
    private const int PlayerInReadyPositionVa    = 0x0056C570;
    private const int AutoTargetVa               = 0x0056C9D0;

    // Static cdecl CM_Combat function — notifies the client of height change (same as pressing Del/End/PgDn)
    private const int SendAttackHeightChangedVa  = 0x006AAE10;

    // ── Delegates ───────────────────────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetCombatSystemDelegate();

    // Thiscall: first param = this pointer (passed via ECX on x86)
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetRequestedAttackHeightDelegate(IntPtr thisPtr, int attackHeight);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void StartAttackRequestDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void EndAttackRequestDelegate(IntPtr thisPtr, int attackHeight, float power);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte PlayerInReadyPositionDelegate(IntPtr thisPtr, byte considerAttackingReady);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void AutoTargetDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SendAttackHeightChangedDelegate(int attackHeight);

    // ── Function pointers ───────────────────────────────────────────────

    private static GetCombatSystemDelegate? _getCombatSystem;
    private static SetRequestedAttackHeightDelegate? _setAttackHeight;
    private static StartAttackRequestDelegate? _startAttackRequest;
    private static EndAttackRequestDelegate? _endAttackRequest;
    private static PlayerInReadyPositionDelegate? _playerInReadyPosition;
    private static AutoTargetDelegate? _autoTarget;
    private static SendAttackHeightChangedDelegate? _sendAttackHeightChanged;

    // ── Pattern-resolved binding (1a hardening, 2026-06-05) ─────────────
    // The *Va consts above are now FALLBACKs. These signatures (verified unique + landing
    // exactly at the VA offline against this client via tools/pe_pattern.py) are the source
    // of truth, so binding survives AC-patch / ACE-rebuild drift. null = wildcard (rel32).
    private static readonly byte?[] PatGetCombatSystem = [ 0xA1, 0x6C, 0x16, 0x87, 0x00, 0xC3, 0x90, 0x90 ];
    private static readonly byte?[] PatSetRequestedAttackHeight = [ 0x8B, 0x51, 0x24, 0x8B, 0x44, 0x24, 0x04, 0x3B ];
    private static readonly byte?[] PatStartAttackRequest = [ 0x51, 0x56, 0x8B, 0xF1, 0xE8, null, null, null, null, 0x8A ];
    private static readonly byte?[] PatEndAttackRequest = [ 0x51, 0x56, 0x8B, 0xF1, 0x8A, 0x46, 0x3E, 0x84, 0xC0, 0x0F ];
    private static readonly byte?[] PatPlayerInReadyPosition = [ 0xA1, 0x58, 0xDA, 0x83, 0x00, 0x83, 0xEC, 0x18 ];
    private static readonly byte?[] PatAutoTarget = [ 0x83, 0xEC, 0x18, 0x53, 0x56, 0x57, 0x8D, 0x44, 0x24, 0x10 ];
    private static readonly byte?[] PatSendAttackHeightChanged = [ 0xE8, null, null, null, null, 0x8B, 0x10, 0x68, 0xFC ];

    private static T? Bind<T>(AcClientTextSection text, string name, byte?[] pattern, int fallbackVa) where T : Delegate
    {
        HookResolver.ResolveResult r = HookResolver.Resolve(text, name, pattern, fallbackVa);
        return r.Success ? Marshal.GetDelegateForFunctionPointer<T>(r.Address) : null;
    }

    private static bool _initialized;
    private static string _statusMessage = "Not probed yet.";

    public static bool IsInitialized => _initialized;
    public static string StatusMessage => _statusMessage;
    public static bool HasClientCombat => _startAttackRequest != null;

    // ── Probe ───────────────────────────────────────────────────────────

    public static bool Probe()
    {
        try
        {
            if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
            {
                _statusMessage = "acclient .text not readable for pattern resolve.";
                RynthLog.Compat($"Compat: ClientCombat probe failed - {_statusMessage}");
                return false;
            }

            _getCombatSystem = Bind<GetCombatSystemDelegate>(text, "ClientCombat.GetCombatSystem", PatGetCombatSystem, GetCombatSystemVa);
            _setAttackHeight = Bind<SetRequestedAttackHeightDelegate>(text, "ClientCombat.SetRequestedAttackHeight", PatSetRequestedAttackHeight, SetRequestedAttackHeightVa);
            _startAttackRequest = Bind<StartAttackRequestDelegate>(text, "ClientCombat.StartAttackRequest", PatStartAttackRequest, StartAttackRequestVa);
            _endAttackRequest = Bind<EndAttackRequestDelegate>(text, "ClientCombat.EndAttackRequest", PatEndAttackRequest, EndAttackRequestVa);
            _playerInReadyPosition = Bind<PlayerInReadyPositionDelegate>(text, "ClientCombat.PlayerInReadyPosition", PatPlayerInReadyPosition, PlayerInReadyPositionVa);
            _autoTarget = Bind<AutoTargetDelegate>(text, "ClientCombat.AutoTarget", PatAutoTarget, AutoTargetVa);
            _sendAttackHeightChanged = Bind<SendAttackHeightChangedDelegate>(text, "ClientCombat.SendAttackHeightChanged", PatSendAttackHeightChanged, SendAttackHeightChangedVa);

            // Verify the combat system singleton is accessible
            IntPtr cs = _getCombatSystem?.Invoke() ?? IntPtr.Zero;
            IntPtr globalPtr = Marshal.ReadIntPtr(new IntPtr(CombatSystemPtrVa));
            RynthLog.Verbose($"Compat: ClientCombat GetCombatSystem()=0x{cs:X8}, global=0x{globalPtr:X8}");
            if (cs == IntPtr.Zero && globalPtr == IntPtr.Zero)
            {
                _statusMessage = "GetCombatSystem returned null — client not ready.";
                RynthLog.Verbose($"Compat: ClientCombat probe - {_statusMessage}");
                // Don't fail — the pointer may be valid after login
            }
            else
            {
                IntPtr used = cs != IntPtr.Zero ? cs : globalPtr;
                RynthLog.Verbose($"Compat: ClientCombatSystem at 0x{used:X8}");
            }

            _initialized = true;
            _statusMessage = "Ready.";
            RynthLog.Verbose("Compat: ClientCombat hooks ready — native attack pipeline available.");
            return true;
        }
        catch (Exception ex)
        {
            Reset();
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: ClientCombat probe failed - {ex.Message}");
            return false;
        }
    }

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>
    /// Initiate a native attack using the client's combat pipeline.
    /// Sets attack height then calls StartAttackRequest which begins
    /// auto-repeating attacks. The client handles turn-to-face naturally.
    /// Target must be selected via SelectItem first.
    /// </summary>
    /// <summary>
    /// Initiate a native attack using the client's combat pipeline.
    /// Calls StartAttackRequest to begin the power bar, then EndAttackRequest
    /// to fire at the specified height and power. The client handles turn-to-face.
    /// Target must be selected via SelectItem first.
    /// </summary>
    public static bool NativeAttack(int attackHeight, float power)
    {
        if (_startAttackRequest == null || _endAttackRequest == null || _getCombatSystem == null)
        {
            RynthLog.Compat($"ClientCombat: NativeAttack — delegates null (start={_startAttackRequest != null}, end={_endAttackRequest != null}, getCS={_getCombatSystem != null})");
            return false;
        }

        try
        {
            IntPtr cs = _getCombatSystem();
            if (cs == IntPtr.Zero)
            {
                IntPtr globalPtr = Marshal.ReadIntPtr(new IntPtr(CombatSystemPtrVa));
                if (globalPtr == IntPtr.Zero) return false;
                cs = globalPtr;
            }

            // Notify client of height change via CM_Combat (same path as keyboard Del/End/PgDn)
            _sendAttackHeightChanged?.Invoke(attackHeight);
            _setAttackHeight!(cs, attackHeight);
            _startAttackRequest(cs);
            _endAttackRequest(cs, attackHeight, power);
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"ClientCombat: NativeAttack EXCEPTION - {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Check if the player is in a ready position to begin an attack.
    /// </summary>
    public static bool IsPlayerReady()
    {
        if (_playerInReadyPosition == null || _getCombatSystem == null)
            return false;

        try
        {
            IntPtr cs = _getCombatSystem();
            if (cs == IntPtr.Zero) return false;
            return _playerInReadyPosition(cs, 1) != 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Auto-target the nearest enemy using the client's targeting system.
    /// </summary>
    public static bool AutoTarget()
    {
        if (_autoTarget == null || _getCombatSystem == null)
            return false;

        try
        {
            IntPtr cs = _getCombatSystem();
            if (cs == IntPtr.Zero) return false;
            _autoTarget(cs);
            return true;
        }
        catch { return false; }
    }

    public static void Reset()
    {
        _getCombatSystem = null;
        _setAttackHeight = null;
        _startAttackRequest = null;
        _endAttackRequest = null;
        _playerInReadyPosition = null;
        _autoTarget = null;
        _sendAttackHeightChanged = null;
        _initialized = false;
    }
}

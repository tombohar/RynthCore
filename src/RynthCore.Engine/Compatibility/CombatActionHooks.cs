using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class CombatActionHooks
{
    private const int QueryHealthResponseVa = 0x006AA900;

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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool RequestIdDelegate(uint objectId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool CastSpellDelegate(uint targetId, int spellId);

    // Chorizite-style: ClientMagicSystem::CastSpell(uint spellId, byte targetIsSelected)
    // VA 0x00568DE0 — static Cdecl. The client uses the currently selected target.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CastSpellClientDelegate(uint spellId, byte targetIsSelected);

    private const int ClientMagicSystemCastSpellVa = 0x00568DE0;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint QueryHealthResponseDelegate(IntPtr uiQueueManager, IntPtr buffer, uint size);

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
    private static RequestIdDelegate? _requestId;
    private static CastSpellDelegate? _castSpell;
    private static CastSpellClientDelegate? _castSpellClient;
    private static IntPtr _castSpellClientPtr;

    // RC2-proven explicit-target cast (no SelectItem / global selection). VAs verified
    // against acclient.map. GetMagicSystem returns the ClientMagicSystem singleton ('this');
    // FreeHandsAndCastSpell(this, spellId, targetId) takes the target as an EXPLICIT arg, so
    // it never reads AC's selected-object global ([singleton+0xF4]) the way CastSpell(...,1)
    // @ 0x00568DE0 does — which is why it survives being marshalled onto AC's game tick.
    private const int GetMagicSystemVa = 0x00567C00;          // void*  __cdecl   GetMagicSystem()
    private const int FreeHandsAndCastSpellVa = 0x00567C90;   // void   __thiscall FreeHandsAndCastSpell(this, uint spellId, uint targetId)
    private static IntPtr _getMagicSystemPtr;
    private static IntPtr _freeHandsCastPtr;
    private static int _sehAvLogCount;
    private static QueryHealthResponseDelegate? _queryHealthResponseDetour;
    private static QueryHealthResponseDelegate? _originalQueryHealthResponse;
    private static QueryHealthResponseDelegate? _identifyObjectDetour;
    private static QueryHealthResponseDelegate? _originalIdentifyObject;
    private static string _statusMessage = "Not probed yet.";

    public static bool IsInitialized { get; private set; }
    public static bool HasMeleeAttack => _meleeAttack != null;
    public static bool HasMissileAttack => _missileAttack != null;
    public static bool HasChangeCombatMode => _changeCombatMode != null;
    public static bool HasCancelAttack => _cancelAttack != null;
    public static bool HasQueryHealth => _queryHealth != null;
    public static bool HasRequestId => _requestId != null;
    public static bool HasCastSpell => _castSpell != null;
    public static string StatusMessage => _statusMessage;

    public static bool Probe()
    {
        Reset();

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
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
                RynthLog.Compat("Compat: combat probe failed - CancelAttack opcode 0x1B7 not found.");
                return false;
            }

            int cancelFuncOff = PatternScanner.FindPrologueBefore(text, cancelOpcodeOff, CombatPrologue);
            if (cancelFuncOff < 0)
            {
                _statusMessage = "CancelAttack prologue not found.";
                RynthLog.Compat("Compat: combat probe failed - CancelAttack prologue not found.");
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

            // RequestId / IdentifyObject — game action 0xC8
            // Search wider region since it may not be near the combat actions
            int wideRegionStart = Math.Max(0, cancelOpcodeOff - 0x2000);
            int wideRegionEnd = Math.Min(text.Length, cancelOpcodeOff + 0x2000);
            byte[] requestIdOpcode = [0xC7, 0x02, 0xC8, 0x00, 0x00, 0x00];
            int requestIdOff = PatternScanner.FindPatternInRegion(text, requestIdOpcode, wideRegionStart, wideRegionEnd);
            int requestIdFuncOff = requestIdOff >= 0
                ? PatternScanner.FindPrologueBefore(text, requestIdOff, CombatPrologue)
                : -1;

            // CastSpell — search full text section for known candidate opcodes
            // Candidates: 0x4A (cast targeted), 0x48, 0x55, 0x5D
            int castSpellFuncOff = -1;
            int castSpellOff = -1;
            byte[][] castSpellCandidates = [
                [0xC7, 0x02, 0x4A, 0x00, 0x00, 0x00],
                [0xC7, 0x02, 0x48, 0x00, 0x00, 0x00],
                [0xC7, 0x02, 0x55, 0x00, 0x00, 0x00],
                [0xC7, 0x02, 0x5D, 0x00, 0x00, 0x00],
            ];
            foreach (byte[] candidate in castSpellCandidates)
            {
                int off = PatternScanner.FindPattern(text, candidate);
                if (off >= 0)
                {
                    int funcOff = PatternScanner.FindPrologueBefore(text, off, CombatPrologue);
                    if (funcOff >= 0)
                    {
                        castSpellOff = off;
                        castSpellFuncOff = funcOff;
                        RynthLog.Verbose($"Compat: CastSpell candidate opcode=0x{candidate[2]:X2} matched at text+0x{off:X}");
                        break;
                    }
                }
            }

            if (changeModeFuncOff < 0 || queryHealthFuncOff < 0 || meleeFuncOff < 0 || missileFuncOff < 0)
            {
                _statusMessage =
                    $"Incomplete. mode={changeModeFuncOff >= 0}, health={queryHealthFuncOff >= 0}, melee={meleeFuncOff >= 0}, missile={missileFuncOff >= 0}.";
                RynthLog.Compat($"Compat: combat probe incomplete - {_statusMessage}");
                return false;
            }

            if (!PatternScanner.VerifyBytes(text, cancelFuncOff, CombatPrologue) ||
                !PatternScanner.VerifyBytes(text, changeModeFuncOff, CombatPrologue) ||
                !PatternScanner.VerifyBytes(text, queryHealthFuncOff, CombatPrologue) ||
                !PatternScanner.VerifyBytes(text, meleeFuncOff, CombatPrologue) ||
                !PatternScanner.VerifyBytes(text, missileFuncOff, CombatPrologue))
            {
                _statusMessage = "Prologue verification failed.";
                RynthLog.Compat("Compat: combat probe failed - prologue verification failed.");
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

            if (requestIdFuncOff >= 0)
            {
                int requestIdVa = textSection.TextBaseVa + requestIdFuncOff;
                _requestId = Marshal.GetDelegateForFunctionPointer<RequestIdDelegate>(new IntPtr(requestIdVa));
                RynthLog.Compat($"Compat: RequestId (IdentifyObject 0xC8) found at 0x{requestIdVa:X8}");
            }
            else
            {
                RynthLog.Compat("Compat: RequestId (IdentifyObject 0xC8) not found — target appraisal unavailable.");
            }

            if (castSpellFuncOff >= 0)
            {
                int castSpellVa = textSection.TextBaseVa + castSpellFuncOff;
                _castSpell = Marshal.GetDelegateForFunctionPointer<CastSpellDelegate>(new IntPtr(castSpellVa));
                RynthLog.Compat($"Compat: CastSpell (0x4A) found at 0x{castSpellVa:X8}");
            }
            else
            {
                RynthLog.Compat("Compat: CastSpell (0x4A) not found — magic combat unavailable.");
            }

            // ClientMagicSystem::CastSpell — static Cdecl:
            //   void CastSpell(uint spellId, byte targetIsSelected)
            // Uses the currently selected target (from SelectItem).
            //
            // The in-module check uses `textSection` (already read above), NOT
            // SmartBoxLocator.IsPointerInModule. SmartBoxLocator's bounds are
            // populated only by its own Probe(), which runs lazily at the first
            // TryGetSmartBox (around login) — long AFTER this early init step
            // (ClientActionHooks.Initialize). IsPointerInModule therefore always
            // saw _moduleBase==0 here and fail-closed for EVERY pointer, so this
            // correct VA was rejected on essentially every launch and casting
            // was forced onto the mis-bound _castSpell pattern fallback — the
            // root cause of the AC-side near-null write AV (0x00568DE0 logged
            // "outside module"; AC crashed at 0x00416C86 [null+0x28] /
            // 0x0055FA24 [null+0x40]). acclient.exe is byte-identical to retail
            // (only PE-header metadata differs) so this retail VA is valid here;
            // it is validated live below per the no-blind-decompile-RVA rule.
            int csModStart = textSection.ModuleBase.ToInt32();
            int csModEnd = csModStart + textSection.ImageSize;
            int csTextOff = ClientMagicSystemCastSpellVa - textSection.TextBaseVa;
            bool csInModule = ClientMagicSystemCastSpellVa >= csModStart &&
                              ClientMagicSystemCastSpellVa < csModEnd;
            bool csInTextWindow = csTextOff >= 0 && csTextOff + 16 <= text.Length;
            if (csInModule && csInTextWindow)
            {
                // Liveness evidence (CLAUDE.md: validate decompile RVAs against
                // the live binary; don't blindly trust a copy-pasted address).
                // Warn-only — never block the bind on the heuristic, or an
                // atypical-but-real prologue would re-break casting exactly the
                // way the SmartBoxLocator order bug did.
                byte b0 = text[csTextOff];
                bool plausiblePrologue =
                    b0 == 0x55 ||                                  // push ebp
                    b0 == 0x53 || b0 == 0x56 || b0 == 0x57 ||      // push ebx/esi/edi
                    b0 == 0x6A || b0 == 0x68 ||                    // push imm
                    b0 == 0xA1 || b0 == 0xB8 || b0 == 0xE9 ||      // mov eax,[m] / mov eax,imm / jmp thunk
                    (b0 == 0x83 && text[csTextOff + 1] == 0xEC) || // sub esp, imm8
                    (b0 == 0x81 && text[csTextOff + 1] == 0xEC) || // sub esp, imm32
                    (b0 == 0x8B && text[csTextOff + 1] == 0xFF) || // mov edi,edi (hotpatch pad)
                    (b0 == 0x8B && text[csTextOff + 1] == 0x44);   // mov eax,[esp+x] (frameless leaf)
                string prologueHex = Convert.ToHexString(text, csTextOff, 16);
                _castSpellClient = Marshal.GetDelegateForFunctionPointer<CastSpellClientDelegate>(
                    new IntPtr(ClientMagicSystemCastSpellVa));
                _castSpellClientPtr = new IntPtr(ClientMagicSystemCastSpellVa);
                RynthLog.Compat(
                    $"Compat: ClientMagicSystem::CastSpell bound at 0x{ClientMagicSystemCastSpellVa:X8} " +
                    $"(prologue {prologueHex}, plausible={plausiblePrologue})");
                if (!plausiblePrologue)
                    RynthLog.Compat("Compat: WARNING ClientMagicSystem::CastSpell prologue is atypical — " +
                                    "verify the VA against this acclient.exe build before trusting casts.");
            }
            else
            {
                RynthLog.Compat(
                    $"Compat: ClientMagicSystem::CastSpell VA 0x{ClientMagicSystemCastSpellVa:X8} " +
                    $"out of range (inModule={csInModule}, inText={csInTextWindow}) — unavailable.");
            }

            // Explicit-target cast pair (RC2-proven): GetMagicSystem @ 0x00567C00 +
            // FreeHandsAndCastSpell @ 0x00567C90. Same byte-identical-retail VA rule as
            // CastSpell above — validated in-module here, invoked via the SEH trampoline on
            // the main thread. Replaces the SelectItem + CastSpell(targetIsSelected=1) two-step
            // for TARGETED casts (the global-selection read that didn't survive the tick).
            int gmsTextOff = GetMagicSystemVa - textSection.TextBaseVa;
            int fhcTextOff = FreeHandsAndCastSpellVa - textSection.TextBaseVa;
            bool gmsOk = GetMagicSystemVa >= csModStart && GetMagicSystemVa < csModEnd &&
                         gmsTextOff >= 0 && gmsTextOff + 16 <= text.Length;
            bool fhcOk = FreeHandsAndCastSpellVa >= csModStart && FreeHandsAndCastSpellVa < csModEnd &&
                         fhcTextOff >= 0 && fhcTextOff + 16 <= text.Length;
            if (gmsOk && fhcOk)
            {
                _getMagicSystemPtr = new IntPtr(GetMagicSystemVa);
                _freeHandsCastPtr = new IntPtr(FreeHandsAndCastSpellVa);
                RynthLog.Compat(
                    $"Compat: explicit-target cast bound — GetMagicSystem=0x{GetMagicSystemVa:X8} " +
                    $"(prologue {Convert.ToHexString(text, gmsTextOff, 8)}), FreeHandsAndCastSpell=0x{FreeHandsAndCastSpellVa:X8} " +
                    $"(prologue {Convert.ToHexString(text, fhcTextOff, 8)}).");
            }
            else
            {
                RynthLog.Compat(
                    $"Compat: explicit-target cast VAs out of range (gms={gmsOk}, fhc={fhcOk}) — " +
                    "targeted casts fail closed.");
            }

            InstallQueryHealthResponseHook();
            // InstallInnerDispatcherHook intentionally disabled 2026-05-14.
            // Its anchor-then-scan-backward heuristic (find CALL to
            // QueryHealthResponseVa, then walk back ≤0x1000 bytes looking for
            // `SUB ESP, 0x1C0`) lands on a function that — on this ACE
            // build at least — AC traverses during world-entry, and the
            // MinHook trampoline at that entry point hangs the load (player
            // stuck on "Entering World", no SendLoginCompleteNotification).
            // The 0xF658 character-list and 0xC9 IdentifyObject packets we
            // were collecting here are redundantly captured by SmartBoxHooks'
            // DispatchSmartBoxEvent / DispatchGameEvent hooks (see
            // SmartBoxHooks.DispatchSmartBoxEventDetour → ProcessPotentialCharacterMessage
            // at line ~117). Re-enabling requires a tighter pattern that
            // *uniquely* identifies the intended inner dispatcher, plus a
            // verification that AC's world-load path is unaffected.
            // InstallInnerDispatcherHook();

            IsInitialized = true;
            _statusMessage = "Ready.";

            RynthLog.Compat($"Compat: combat hooks ready - cancel=0x{cancelVa:X8}, mode=0x{changeModeVa:X8}, health=0x{queryHealthVa:X8}, melee=0x{meleeVa:X8}, missile=0x{missileVa:X8}");
            return true;
        }
        catch (Exception ex)
        {
            Reset();
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: combat probe failed - {ex.Message}");
            return false;
        }
    }

    public static bool MeleeAttack(uint targetId, int attackHeight, float powerLevel)
    {
        if (!MainThreadGuard.IsOnMainThread())
            return AcMainThreadQueue.EnqueueMeleeAttack(targetId, attackHeight, powerLevel);

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
        if (!MainThreadGuard.IsOnMainThread())
            return AcMainThreadQueue.EnqueueMissileAttack(targetId, attackHeight, accuracyLevel);

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
        // P1 marshalling: off-thread mutators run on AC's main thread (via the
        // OnEndScene drain) so AC mutates its single-threaded object/animation
        // (CSequence) state on the thread that updates it — eliminating the
        // off-thread-mutation corruption class (e.g. the CSequence::update_internal
        // AV). On the main thread (incl. the drain) this executes directly.
        if (!MainThreadGuard.IsOnMainThread())
            return AcMainThreadQueue.EnqueueChangeCombatMode(combatMode);

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

    public static bool RequestId(uint objectId)
    {
        if (_requestId == null || objectId == 0)
            return false;

        try
        {
            return _requestId(objectId);
        }
        catch
        {
            return false;
        }
    }

    public static bool CastSpell(uint targetId, int spellId)
    {
        if (spellId <= 0) return false;
        if (_castSpellClient == null) return false;

        // P2 cast marshalling — drain-BEFORE-tick variant (2026-06-03, attempt 2).
        // Off the main thread, enqueue the cast; GameTickHooks drains it on AC's
        // game-logic tick (Client::UseTime) BEFORE _originalUseTime runs, so AC's SAME
        // tick processes the freshly-set selection + cast (mimics AC's natural
        // input -> UseTime flow). Attempt 1 (drain AFTER the tick) landed self-buffs but
        // NOT targeted casts: SelectItem + cast initiated at the END of tick N weren't
        // processed until tick N+1, by which point the selection was clobbered (target
        // held hp=50/50, zero damage).
        //
        // Off-thread casting is NOT a safe fallback: it races AC's cast/UI state and
        // AVs UNCAUGHT (0xC0000005 WRITE in UIElement::GetAttribute_Bool, off the main
        // thread, tid != main) — it killed a 37-min P1 soak. So the cast MUST run on the
        // main thread; the only open question is landing a targeted cast from the tick,
        // which the drain-before order is intended to fix. On the main thread (the
        // UseTime drain, or any main-thread caller) this falls through to the direct
        // cast below.
        if (!MainThreadGuard.IsOnMainThread())
            return AcMainThreadQueue.EnqueueCast(targetId, (uint)spellId);

        // Call ClientMagicSystem::CastSpell directly off the plugin pump thread.
        // This is the proven-working casting path (buffs + combat resolve in
        // chat). The earlier EndScene-marshalled variant (AcMainThreadQueue,
        // drained from OnEndScene) was REVERTED 2026-05-19: EndScene fires AC's
        // IncrementBusyCount but is not the context that drives the cast state
        // machine to completion, so combat casts never produced a projectile /
        // damage / "You cast" chat — busy leaked, AC's single cast slot jammed
        // "Casting <spell>" client-wide, and even manual casting died. Do NOT
        // re-introduce queue/EndScene marshalling for casts.
        // The documented object-teardown AV (0x0055FA24 [null+0x40] /
        // 0x00416C86 [null+0x28]) is contained per-callsite by the SEH
        // trampoline below (__try/__except in RynthCore.SehTrampoline.dll),
        // which is the correct fix for that AV. AC's CastSpell reads its
        // "currently selected target" when targetIsSelected=1; SelectItem
        // first so AC's selection matches the caller's intent.
        if (_castSpellClient != null)
        {
            try
            {
                // TARGETED cast (targetId != 0): explicit-target path — RC2-proven, NO
                // SelectItem. GetMagicSystem() -> FreeHandsAndCastSpell(mgr, spellId, targetId).
                // The old SelectItem + CastSpell(targetIsSelected=1) two-step relied on AC's
                // global selection ([magicSysSingleton+0xF4], read inside 0x00568DE0) surviving
                // the marshal onto AC's tick — it didn't (targets held hp=50/50, zero damage).
                // FreeHandsAndCastSpell takes the target as an explicit arg and auto-readies the
                // wand, so there is nothing to clobber.
                if (targetId != 0)
                {
                    if (SehTrampoline.IsAvailable && _freeHandsCastPtr != IntPtr.Zero && _getMagicSystemPtr != IntPtr.Zero)
                    {
                        IntPtr mgr = SehTrampoline.CdeclPtrVoid(_getMagicSystemPtr, out bool avMgr);
                        if (avMgr || mgr == IntPtr.Zero)
                        {
                            if (System.Threading.Interlocked.Increment(ref _sehAvLogCount) <= 20)
                                RynthLog.Compat($"[SEH] GetMagicSystem null/AV — targeted cast skipped spell={spellId} target=0x{targetId:X8}");
                            return false;
                        }
                        bool okCast = SehTrampoline.FreeHandsCast(_freeHandsCastPtr, mgr, (uint)spellId, targetId);
                        if (!okCast && System.Threading.Interlocked.Increment(ref _sehAvLogCount) <= 20)
                            RynthLog.Compat($"[SEH] AV in FreeHandsAndCastSpell spell={spellId} target=0x{targetId:X8} — caught");
                        return okCast;
                    }
                    return false;   // trampoline / VAs unavailable — fail closed (never the off-thread SelectItem two-step)
                }

                // SELF cast (targetId == 0): the proven CastSpell(spellId, targetIsSelected=0)
                // path — no selection dependency; lands buffs reliably.
                if (SehTrampoline.IsAvailable && _castSpellClientPtr != IntPtr.Zero)
                {
                    bool ok = SehTrampoline.CdeclVoidUintByte(_castSpellClientPtr, (uint)spellId, 0);
                    if (!ok && System.Threading.Interlocked.Increment(ref _sehAvLogCount) <= 20)
                        RynthLog.Compat($"[SEH] AV in self-CastSpell spell={spellId} — caught");
                    return ok;
                }

                _castSpellClient((uint)spellId, 0);
                return true;
            }
            catch
            {
                // Fall through to "refuse" rather than the pattern-scanned
                // _castSpell — see the comment block on that fallback.
            }
        }

        // No safe fallback — fail closed. The pattern-scanned game-action-0x4A
        // wrapper (_castSpell) resolves to the WRONG function on this
        // acclient.exe: CombatPrologue (83 EC 0C 53 56 57 E8) is shared by many
        // AC functions and FindPrologueBefore takes the FIRST match, which is
        // not the cast wrapper, so calling it would crash AC. The 2026-05-14
        // "binaries are byte-identical so the pattern is fine" reasoning was
        // wrong: identity makes the pattern consistently wrong on BOTH retail
        // and ACE, not correct.
        // ⚠ 2026-05-18, DUMP-VERIFIED: the AVs at 0x00416C86 [null+0x28] and
        // 0x0055FA24 [null+0x40] that older notes/comments blamed on this
        // fallback are NOT cast-related. Resolved via acclient.map they are
        // DBOCache::DestroyObj and CPlayerSystem::CalculateObjectRangeChecks →
        // List<ObjectRangeInfo>::remove — AC OBJECT-TEARDOWN code tripping over
        // a corrupted object/range list (async lifecycle corruption class, not
        // spellcasting). Do NOT chase CastSpell for these. See
        // rynthcore_castspell_fallback_pitfall.md / rynthcore_crash_investigation.md.
        // _castSpellClient (the verified ClientMagicSystem::CastSpell VA bound
        // in Probe) is the only safe path; if it is unavailable we refuse the
        // cast. BuffManager.CastSpellInner and CombatManager both handle a
        // false / no-op return without wedging (skip the spell, retry next
        // cycle), so refusing is strictly safer than crashing the client.
        return false;
    }

    // Called by AcMainThreadQueue.Drain on AC's main thread (EndScene path).
    // The preceding queued SelectItem entry already set AC's selection, so
    // cast with targetIsSelected=1. Defensive: never throw into the drain loop.
    internal static void ExecuteQueuedCast(uint spellId)
    {
        if (_castSpellClient == null) return;
        if (SehTrampoline.IsAvailable && _castSpellClientPtr != IntPtr.Zero)
        {
            bool ok = SehTrampoline.CdeclVoidUintByte(_castSpellClientPtr, spellId, 1);
            if (!ok && System.Threading.Interlocked.Increment(ref _sehAvLogCount) <= 20)
                RynthLog.Compat($"[SEH] AV in CastSpell spell={spellId} — caught, cast skipped");
            return;
        }
        try { _castSpellClient.Invoke(spellId, 1); } catch { }
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
        _requestId = null;
        _castSpell = null;
        _castSpellClient = null;
        _castSpellClientPtr = IntPtr.Zero;
        _getMagicSystemPtr = IntPtr.Zero;
        _freeHandsCastPtr = IntPtr.Zero;
        _queryHealthResponseDetour = null;
        _originalQueryHealthResponse = null;
        _identifyObjectDetour = null;
        _originalIdentifyObject = null;
        IsInitialized = false;
    }

    /// Hooks the inner game-event dispatcher at its entry point. The dispatcher is thiscall
    /// with RET 8: uint __thiscall Dispatch(void* buffer, uint size). We intercept 0xC9
    /// (IdentifyObject response) events and parse the CreatureProfile for health/maxHealth.
    /// </summary>
    private static IntPtr _originalInnerDispatcherPtr;

    private static void InstallInnerDispatcherHook()
    {
        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
            return;

        byte[] text = textSection.Bytes;
        int textBase = textSection.TextBaseVa;

        // Find the call to QueryHealthResponse to anchor the inner dispatcher region
        int anchorOff = -1;
        for (int i = 0; i < text.Length - 5; i++)
        {
            if (text[i] != 0xE8) continue;
            int rel = BitConverter.ToInt32(text, i + 1);
            if (textBase + i + 5 + rel == QueryHealthResponseVa)
            {
                anchorOff = i;
                break;
            }
        }

        if (anchorOff < 0)
        {
            RynthLog.Compat("Compat: dispatch scan - no CALL to QueryHealthResponse found.");
            return;
        }

        // Find the inner dispatcher function entry by searching backward for SUB ESP, 0x1C0
        int subEspOff = -1;
        for (int i = anchorOff; i >= Math.Max(0, anchorOff - 0x1000); i--)
        {
            if (text[i] == 0x81 && text[i + 1] == 0xEC &&
                text[i + 2] == 0xC0 && text[i + 3] == 0x01 &&
                text[i + 4] == 0x00 && text[i + 5] == 0x00)
            {
                subEspOff = i;
                break;
            }
        }

        if (subEspOff < 0)
        {
            RynthLog.Compat("Compat: inner dispatcher SUB ESP not found.");
            return;
        }

        int funcEntryVa = textBase + subEspOff;
        RynthLog.Verbose($"Compat: inner dispatcher entry @ 0x{funcEntryVa:X8}");

        try
        {
            unsafe
            {
                delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint, uint> pDetour = &InnerDispatcherDetour;
                MinHook.Hook(new IntPtr(funcEntryVa), (IntPtr)pDetour, out _originalInnerDispatcherPtr);
                RynthLog.Verbose($"Compat: inner dispatcher hook installed @ 0x{funcEntryVa:X8}");
            }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: inner dispatcher hook failed - {ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static unsafe uint InnerDispatcherDetour(IntPtr thisPtr, IntPtr buffer, uint size)
    {
        var pOriginal = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint, uint>)_originalInnerDispatcherPtr;

        try
        {
            if (buffer != IntPtr.Zero && size >= 4)
            {
                uint eventType = unchecked((uint)Marshal.ReadInt32(buffer));

                // Intercept IdentifyObject response (0xC9)
                if (eventType == 0xC9 && size >= 16)
                    TryParseIdentifyResponse(buffer, size);

                // Intercept character list packet (0xF658) — login screen
                if (eventType == 0xF7EA || eventType == 0xF658)
                    CharacterCaptureHooks.ProcessRawCharacterMessage(buffer, size);
            }
        }
        catch
        {
        }

        return pOriginal(thisPtr, buffer, size);
    }

    private static int _identifyWireLogCount;

    /// <summary>
    /// Parses the IdentifyObject response (game event 0xC9) to extract
    /// health/maxHealth from the CreatureProfile section. Wire format is
    /// authoritative per ACE AppraiseInfoExtensions.Write / CreatureProfile:
    ///
    ///   header: [_(4)][objectId(4)][Flags(4)][Success(4)]  (parser base+0..+15)
    ///   then sections, in ACE *write* order (NOT flag-bit order):
    ///     0x0001 IntStatsTable     hashtbl, entry 8  (u32 key + i32)
    ///     0x2000 Int64StatsTable   hashtbl, entry 12 (u32 key + i64)
    ///     0x0002 BoolStatsTable    hashtbl, entry 8  (u32 key + u32)
    ///     0x0004 FloatStatsTable   hashtbl, entry 12 (u32 key + f64)
    ///     0x0008 StringStatsTable  hashtbl, string entries (u32 key + str16L)
    ///     0x1000 DidStatsTable     hashtbl, entry 8  (u32 key + u32)
    ///     0x0010 SpellBook         list: i32 count + count*u32
    ///     0x0080 ArmorProfile      fixed 32 bytes (8 floats)
    ///     0x0100 CreatureProfile   &lt;-- target
    ///   hashtbl header = ushort count + ushort numBuckets (4 bytes).
    ///
    ///   CreatureProfile: cpFlags(4), Health(4), HealthMax(4),
    ///     if cpFlags&amp;0x8: Str,End,Quick,Coord,Focus,Self,
    ///                       Stamina,Mana,StaminaMax,ManaMax (10*4),
    ///     if cpFlags&amp;0x1: AttrHighlights(2)+AttrColors(2).
    /// </summary>
    internal static void TryParseIdentifyResponse(IntPtr buffer, uint size)
    {
        try
        {
            // Header: _(4) + objectId(4) + flags(4) + success(4) = 16 bytes
            uint objectId = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, 4)));
            uint flags = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, 8)));
            uint success = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, 12)));

            if (success == 0 || objectId == 0)
                return;

            const uint CreatureProfile = 0x0100;
            if ((flags & CreatureProfile) == 0)
                return;

            int offset = 16; // Past the header
            int iSize = (int)size;

            // Skip sections written before CreatureProfile, in ACE write order.
            if ((flags & 0x0001) != 0 && !SkipPackedHashTable(buffer, iSize, ref offset, 8))
                return; // IntStatsTable
            if ((flags & 0x2000) != 0 && !SkipPackedHashTable(buffer, iSize, ref offset, 12))
                return; // Int64StatsTable
            if ((flags & 0x0002) != 0 && !SkipPackedHashTable(buffer, iSize, ref offset, 8))
                return; // BoolStatsTable
            if ((flags & 0x0004) != 0 && !SkipPackedHashTable(buffer, iSize, ref offset, 12))
                return; // FloatStatsTable
            if ((flags & 0x0008) != 0 && !SkipStringHashTable(buffer, iSize, ref offset))
                return; // StringStatsTable
            if ((flags & 0x1000) != 0 && !SkipPackedHashTable(buffer, iSize, ref offset, 8))
                return; // DidStatsTable
            if ((flags & 0x0010) != 0 && !SkipPackableListUInt(buffer, iSize, ref offset))
                return; // SpellBook
            if ((flags & 0x0080) != 0)
            {
                offset += 32; // ArmorProfile = 8 floats, fixed
                if (offset > iSize)
                    return;
            }

            // CreatureProfile: cpFlags(4), Health(4), HealthMax(4), ...
            if (offset + 12 > iSize)
                return;

            uint cpFlags = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, offset)));
            uint health = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, offset + 4)));
            uint maxHealth = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, offset + 8)));

            uint stamina = 0, maxStamina = 0, mana = 0, maxMana = 0;
            if ((cpFlags & 0x8) != 0 && offset + 52 <= iSize)
            {
                // Str(+12) End(+16) Quick(+20) Coord(+24) Focus(+28) Self(+32)
                // Stamina(+36) Mana(+40) StaminaMax(+44) ManaMax(+48)
                stamina = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, offset + 36)));
                mana = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, offset + 40)));
                maxStamina = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, offset + 44)));
                maxMana = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, offset + 48)));
            }

            int log = Interlocked.Increment(ref _identifyWireLogCount);
            if (log <= 20)
                RynthLog.Compat($"Compat: identify-wire obj=0x{objectId:X8} flags=0x{flags:X4} cp=0x{cpFlags:X} hp={health}/{maxHealth}");

            if (maxHealth > 0 && maxHealth < 1_000_000)
            {
                var vitals = new CreatureVitals(health, maxHealth, stamina, maxStamina, mana, maxMana);
                ObjectQualityCache.SetCreatureVitals(objectId, vitals);

                if (ClientObjectHooks.TryGetWeenieObjectPtr(objectId, out IntPtr pWeenie) && pWeenie != IntPtr.Zero)
                    ObjectQualityCache.SetMaxHealth(pWeenie, maxHealth);

                float ratio = (float)health / maxHealth;
                PluginManager.QueueUpdateHealth(objectId, ratio, health, maxHealth);

                uint playerId = ClientHelperHooks.GetPlayerId();
                if (playerId != 0 && objectId == playerId)
                    PlayerVitalsHooks.SeedMaxVitalsFromIdentify(maxHealth, maxStamina, maxMana);
            }
        }
        catch
        {
        }
    }

    /// <summary>Skips a packed hash table with fixed-size entries.</summary>
    private static bool SkipPackedHashTable(IntPtr buffer, int size, ref int offset, int entrySize)
    {
        if (offset + 4 > size) return false;
        ushort entryCount = unchecked((ushort)Marshal.ReadInt16(IntPtr.Add(buffer, offset)));
        offset += 4; // header: count(2) + buckets(2)
        int dataSize = entryCount * entrySize;
        offset += dataSize;
        return offset <= size;
    }

    /// <summary>Skips a packed hash table with string values (variable length).</summary>
    private static bool SkipStringHashTable(IntPtr buffer, int size, ref int offset)
    {
        if (offset + 4 > size) return false;
        ushort entryCount = unchecked((ushort)Marshal.ReadInt16(IntPtr.Add(buffer, offset)));
        offset += 4; // header
        for (int i = 0; i < entryCount; i++)
        {
            if (offset + 6 > size) return false;
            offset += 4; // key (uint32)
            // AC packed string: ushort length, then chars, then padding to 4-byte boundary
            ushort strLen = unchecked((ushort)Marshal.ReadInt16(IntPtr.Add(buffer, offset)));
            offset += 2;
            offset += strLen;
            // Align to 4-byte boundary (from the start of the string field, which is offset-2-strLen)
            int totalStringField = 2 + strLen;
            int padding = (4 - (totalStringField % 4)) % 4;
            offset += padding;
        }
        return offset <= size;
    }

    /// <summary>Skips a PackableList&lt;uint&gt;: int32 count + count*uint32.</summary>
    private static bool SkipPackableListUInt(IntPtr buffer, int size, ref int offset)
    {
        if (offset + 4 > size) return false;
        int count = Marshal.ReadInt32(IntPtr.Add(buffer, offset));
        offset += 4;
        if (count < 0 || count > 100_000) return false;
        offset += count * 4;
        return offset <= size;
    }

    private static void InstallQueryHealthResponseHook()
    {
        if (_originalQueryHealthResponse != null)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
            return;

        IntPtr responsePtr = new(QueryHealthResponseVa);
        int textStart = textSection.TextBaseVa;
        int textEnd = textStart + textSection.Bytes.Length;
        int responseVa = responsePtr.ToInt32();
        if (responseVa < textStart || responseVa >= textEnd)
        {
            RynthLog.Compat($"Compat: query-health-response hook skipped - invalid address 0x{QueryHealthResponseVa:X8}");
            return;
        }

        _queryHealthResponseDetour = QueryHealthResponseDetour;
        IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_queryHealthResponseDetour);
        IntPtr originalPtr = MinHook.HookCreate(responsePtr, detourPtr);
        _originalQueryHealthResponse = Marshal.GetDelegateForFunctionPointer<QueryHealthResponseDelegate>(originalPtr);
        Thread.MemoryBarrier();
        MinHook.Enable(responsePtr);
        RynthLog.Verbose($"Compat: query-health-response hook ready - Handle_Combat__QueryHealthResponse=0x{QueryHealthResponseVa:X8}");
    }

    private static uint QueryHealthResponseDetour(IntPtr uiQueueManager, IntPtr buffer, uint size)
    {
        try
        {
            if (buffer != IntPtr.Zero && size >= 12)
            {
                uint opcode = unchecked((uint)Marshal.ReadInt32(buffer));
                uint targetId = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, 4)));
                float healthRatio = Marshal.PtrToStructure<float>(IntPtr.Add(buffer, 8));

                // Resolve absolute health: first check the cache, then try a direct
                // read via InqAttribute2ndStruct (works after an IdentifyObject response
                // has populated the weenie's qualities). This detour runs on the game
                // thread so the direct read is safe.
                uint maxHealth = 0;
                uint currentHealth = 0;
                // Appraisal-only (see SmartBoxHooks.TryQueueHealthUpdate):
                // emit absolute ONLY from a real appraisal's wire-parsed
                // CreatureProfile; otherwise max=0 → UI shows %. ACE creature
                // Inq / pointer-cache maxes are unreliable.
                if (ObjectQualityCache.TryGetCreatureVitals(targetId, out CreatureVitals exact) && exact.MaxHealth > 0)
                {
                    maxHealth = exact.MaxHealth;
                    currentHealth = (uint)Math.Round(maxHealth * Math.Clamp(healthRatio, 0f, 1f));
                }

                // If this response is for the player, derive true MaxHealth from the ratio.
                // e.g. health=92541, ratio=0.925 → trueMax ≈ 99999
                uint playerId = ClientHelperHooks.GetPlayerId();
                if (playerId != 0 && targetId == playerId && healthRatio > 0f && healthRatio <= 1f)
                    PlayerVitalsHooks.UpdateMaxFromHealthRatio(healthRatio);

                PluginManager.QueueUpdateHealth(targetId, healthRatio, currentHealth, maxHealth);
            }
        }
        catch
        {
        }

        return _originalQueryHealthResponse != null
            ? _originalQueryHealthResponse(uiQueueManager, buffer, size)
            : 0;
    }

}

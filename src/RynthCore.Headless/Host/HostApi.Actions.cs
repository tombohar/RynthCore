// ============================================================================
//  RynthCore.Headless - Host/HostApi.Actions.cs
//  The ACTIONS area's API slots: combat, casts, busy / use-done / weenie-error
//  tracking, uses and inventory sends, selection, chat, movement and jumps,
//  confirmations. Thin unmanaged wrappers over HostContext.Actions.cs; every one
//  swallows its exceptions (a plugin call must never see one) and answers as the
//  engine does: 1 sent / queued, 0 refused.
// ============================================================================

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.PluginSdk;

namespace RynthCore.Headless.Host;

internal static unsafe partial class HostApi
{
    static partial void FillActions(RynthCoreApiNative* api)
    {
        // combat + casts
        api->ChangeCombatModeFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, int>)&ActChangeCombatMode;
        api->CancelAttackFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&ActCancelAttack;
        api->QueryHealthFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&ActQueryHealth;
        api->MeleeAttackFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int, float, int>)&ActMeleeAttack;
        api->MissileAttackFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int, float, int>)&ActMissileAttack;
        api->CastSpellFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int, int>)&ActCastSpell;
        api->NativeAttackFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, float, int>)&ActNativeAttack;
        api->IsPlayerReadyFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&ActIsPlayerReady;
        api->GetCurrentCombatModeFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&ActGetCurrentCombatMode;

        // busy, use-done, errors
        api->GetCastBusyStateFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&ActGetCastBusyState;
        api->GetBusyStateFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&ActGetBusyState;
        api->ForceResetBusyCountFn = (IntPtr)(delegate* unmanaged[Cdecl]<void>)&ActForceResetBusyCount;
        api->GetUseDoneSeqFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&ActGetUseDoneSeq;
        api->GetLastUseDoneFn = (IntPtr)(delegate* unmanaged[Cdecl]<int*, uint*, int>)&ActGetLastUseDone;
        api->GetLastWeenieErrorFn = (IntPtr)(delegate* unmanaged[Cdecl]<int*, uint*, uint*, uint*, int>)&ActGetLastWeenieError;

        // uses + inventory
        api->UseObjectFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&ActUseObject;
        api->UseObjectOnFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int>)&ActUseObjectOn;
        api->UseEquippedItemFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int>)&ActUseEquippedItem;
        api->MoveItemExternalFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int>)&ActMoveItemExternal;
        api->MoveItemInternalFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int, int>)&ActMoveItemInternal;
        api->PutItemInContainerFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int>)&ActPutItemInContainer;
        api->SalvageDirectFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int, int>)&ActSalvageDirect;
        api->SplitStackInternalFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int, int>)&ActSplitStackInternal;
        api->MergeStackInternalFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int>)&ActMergeStackInternal;
        api->GetMergeStackResultFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int*, int*, int>)&ActGetMergeStackResult;
        api->GiveObjectToFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int, int>)&ActGiveObjectTo;
        api->WieldItemFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int>)&ActWieldItem;
        api->CloseContainerFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&ActCloseContainer;

        // selection
        api->SelectItemFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&ActSelectItem;
        api->SetSelectedObjectIdFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&ActSetSelectedObjectId;
        api->GetSelectedItemIdFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint>)&ActGetSelectedItemId;
        api->GetPreviousSelectedItemIdFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint>)&ActGetPreviousSelectedItemId;
        api->GetGroundContainerIdFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint>)&ActGetGroundContainerId;

        // chat
        api->WriteToChatFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int>)&ActWriteToChat;
        api->InvokeChatParserFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&ActInvokeChatParser;
        api->SetIncomingChatSuppressionFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, void>)&ActSetIncomingChatSuppression;

        // movement
        api->DoMovementFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, float, int, int>)&ActDoMovement;
        api->StopMovementFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int, int>)&ActStopMovement;
        api->SetMotionFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int, int>)&ActSetMotion;
        api->SetAutoRunFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, int>)&ActSetAutoRun;
        api->StopCompletelyFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&ActStopCompletely;
        api->TurnToHeadingFn = (IntPtr)(delegate* unmanaged[Cdecl]<float, int>)&ActTurnToHeading;
        api->SetAutonomyLevelFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&ActSetAutonomyLevel;

        // jumps
        api->JumpNonAutonomousFn = (IntPtr)(delegate* unmanaged[Cdecl]<float, int>)&ActJumpNonAutonomous;
        api->TapJumpFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&ActTapJump;
        api->CommenceJumpFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&ActCommenceJump;
        api->DoJumpFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, int>)&ActDoJump;
        api->LaunchJumpWithMotionFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, int, int, int, int>)&ActLaunchJumpWithMotion;

        // confirmations
        api->ArmConfirmationFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int, int, int>)&ActArmConfirmation;
        api->GetLastConfirmationFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, uint*, int*, int*, int*, int>)&ActGetLastConfirmation;
    }

    /// <summary>Runs <paramref name="f"/> against the current context; <paramref name="fallback"/> without one or when it throws.</summary>
    private static T RunAct<T>(Func<HostContext, T> f, T fallback)
    {
        try
        {
            HostContext? c = Ctx;
            return c == null ? fallback : f(c);
        }
        catch (Exception ex)
        {
            try { Ctx?.Log($"[host] action threw {ex.GetType().Name}: {ex.Message}"); } catch { }
            return fallback;
        }
    }

    private static int RunAct(Func<HostContext, bool> f) => RunAct(c => f(c) ? 1 : 0, 0);

    // ── combat + casts ──────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActChangeCombatMode(int mode) => RunAct(c => c.ActChangeCombatMode(mode));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActCancelAttack() => RunAct(c => c.ActCancelAttack());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActQueryHealth(uint target) => RunAct(c => c.ActQueryHealth(target));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActMeleeAttack(uint target, int height, float power) => RunAct(c => c.ActMelee(target, height, power));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActMissileAttack(uint target, int height, float accuracy) => RunAct(c => c.ActMissile(target, height, accuracy));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActCastSpell(uint target, int spellId) => RunAct(c => c.ActCast(target, spellId));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActNativeAttack(int height, float power) => RunAct(c => c.ActNativeAttack(height, power));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActIsPlayerReady() => RunAct(c => c.PlayerReady);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActGetCurrentCombatMode() => RunAct(c => c.CurrentCombatMode, 1);

    // ── busy, use-done, errors ──────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActGetCastBusyState() => RunAct(c => c.CastBusyState, 0);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActGetBusyState() => RunAct(c => Math.Max(0, c.BusyCount), 0);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ActForceResetBusyCount() => RunAct(c => { c.ForceResetBusy(); return true; });

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActGetUseDoneSeq() => RunAct(c => c.UseDoneSeq, 0);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActGetLastUseDone(int* seq, uint* error)
    {
        try
        {
            HostContext? c = Ctx;
            if (c == null) return 0;
            (int s, uint e) = c.LastUseDone;
            if (seq != null) *seq = s;
            if (error != null) *error = e;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActGetLastWeenieError(int* seq, uint* error, uint* eventType, uint* objectId)
    {
        try
        {
            HostContext? c = Ctx;
            if (c == null) return 0;
            (int s, uint e, uint t, uint o) = c.LastWeenieError;
            if (seq != null) *seq = s;
            if (error != null) *error = e;
            if (eventType != null) *eventType = t;
            if (objectId != null) *objectId = o;
            return 1;
        }
        catch { return 0; }
    }

    // ── uses + inventory ────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActUseObject(uint obj) => RunAct(c => c.ActUse(obj));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActUseObjectOn(uint source, uint target) => RunAct(c => c.ActUseOn(source, target));

    /// <summary>An equipped item used on a target (a wand on a creature, ...): the same UseWithTarget the client sends.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActUseEquippedItem(uint source, uint target) => RunAct(c => c.ActUseOn(source, target));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActMoveItemExternal(uint obj, uint container, int amount) => RunAct(c => c.ActMoveItemExternal(obj, container, amount));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActMoveItemInternal(uint obj, uint container, int slot, int amount) => RunAct(c => c.ActMoveItemInternal(obj, container, slot, amount));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActPutItemInContainer(uint item, uint container, int placement) => RunAct(c => c.ActPutItemInContainer(item, container, placement), 0);

    /// <summary>SalvageDirect: the ids are copied out first (a lambda can't hold the pointer); count sent, 0 refused.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActSalvageDirect(uint tool, uint* ids, int count)
    {
        if (ids == null || count <= 0 || count > RynthCoreHost.MaxSalvageDirectItems) return 0;
        var list = new uint[count];
        for (int i = 0; i < count; i++) list[i] = ids[i];
        return RunAct(c => c.ActSalvageDirect(tool, list), 0);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActSplitStackInternal(uint obj, uint container, int slot, int amount) => RunAct(c => c.ActSplitStack(obj, container, slot, amount));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActMergeStackInternal(uint source, uint target) => RunAct(c => c.ActMergeStack(source, target));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActGetMergeStackResult(uint source, uint target, int* amount, int* ageMs)
    {
        try
        {
            HostContext? c = Ctx;
            if (c == null) return HostContext.MergeNone;
            int status = c.MergeResult(source, target, out int a, out int age);
            if (amount != null) *amount = a;
            if (ageMs != null) *ageMs = age;
            return status;
        }
        catch { return HostContext.MergeNone; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActGiveObjectTo(uint obj, uint target, int amount) => RunAct(c => c.ActGive(obj, target, amount));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActWieldItem(uint obj, uint equipMask) => RunAct(c => c.ActWield(obj, equipMask));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActCloseContainer(uint container) => RunAct(c => c.ActCloseContainer(container));

    // ── selection ───────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActSelectItem(uint obj) => RunAct(c => c.ActSelectItem(obj));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActSetSelectedObjectId(uint obj) => RunAct(c => c.ActSetSelected(obj));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint ActGetSelectedItemId() => RunAct(c => c.SelectedId, 0u);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint ActGetPreviousSelectedItemId() => RunAct(c => c.PreviousSelectedId, 0u);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint ActGetGroundContainerId() => RunAct(c => c.GroundContainer, 0u);

    // ── chat ────────────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActWriteToChat(IntPtr textUtf16, int chatType)
    {
        string? text = textUtf16 != IntPtr.Zero ? Marshal.PtrToStringUni(textUtf16) : null;
        return RunAct(c => c.ActWriteToChat(text, chatType));
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActInvokeChatParser(IntPtr textUtf16)
    {
        string? text = textUtf16 != IntPtr.Zero ? Marshal.PtrToStringUni(textUtf16) : null;
        return RunAct(c => c.ActInvokeChatParser(text));
    }

    /// <summary>No chat window to keep lines out of: a no-op.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ActSetIncomingChatSuppression(int enabled) { }

    // ── movement ────────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActDoMovement(uint motion, float speed, int holdKey) => RunAct(c => c.ActSetMotion(motion, true, holdKey != 0));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActStopMovement(uint motion, int holdKey) => RunAct(c => c.ActSetMotion(motion, false, holdKey != 0));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActSetMotion(uint motion, int enabled) => RunAct(c => c.ActSetMotion(motion, enabled != 0));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActSetAutoRun(int enabled) => RunAct(c => c.ActSetAutoRun(enabled != 0));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActStopCompletely() => RunAct(c => c.ActStopCompletely());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActTurnToHeading(float headingDegrees) => RunAct(c => c.ActTurnToHeading(headingDegrees));

    /// <summary>The client's autonomy level has no counterpart here: accepted.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActSetAutonomyLevel(uint level) => 1;

    // ── jumps ───────────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActJumpNonAutonomous(float extent) => RunAct(c => c.ActJump(extent));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActTapJump() => RunAct(c => c.ActJump(0f));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActCommenceJump() => RunAct(c => c.ActCommenceJump());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActDoJump(int autonomous) => RunAct(c => c.ActJump(c.JumpCharge));

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActLaunchJumpWithMotion(int shift, int w, int x, int z, int cKey)
        => RunAct(c => c.ActLaunchJump(shift != 0, w != 0, x != 0, z != 0, cKey != 0));

    // ── confirmations ───────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActArmConfirmation(uint type, int minPercent, int timeoutMs) => RunAct(c => c.ActArmConfirmation(type, minPercent, timeoutMs), 0);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ActGetLastConfirmation(uint* type, uint* contextId, int* percent, int* outcome, int* ageMs)
    {
        try
        {
            HostContext? c = Ctx;
            if (c == null) return 0;
            int seq = c.LastConfirmation(out uint t, out uint ctx, out int pct, out int oc, out int age);
            if (type != null) *type = t;
            if (contextId != null) *contextId = ctx;
            if (percent != null) *percent = pct;
            if (outcome != null) *outcome = oc;
            if (ageMs != null) *ageMs = age;
            return seq;
        }
        catch { return 0; }
    }
}

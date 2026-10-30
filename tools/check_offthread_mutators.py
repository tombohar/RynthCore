#!/usr/bin/env python3
"""
check_offthread_mutators.py - static guard against the off-thread AC-mutation
AV class (the root of the entire 2026-06-11..13 crash week).

INVARIANT (hard-won): any engine code that mutates AC's internal state must run
on AC's main thread. The established pattern in Compatibility/*Hooks.cs is:

    public static bool DoThing(...) {
        if (!MainThreadGuard.IsOnMainThread())
            return AcMainThreadQueue.EnqueueDoThing(...);   // marshal
        ... call the native AC delegate directly ...         // on main thread
    }

Every off-thread AC mutator that slipped past this gate produced a corruption
AV: SetSelectedObject (0x460D1D UIElement UAF), ForceResetBusyCount, the
CommandInterpreter movement calls, UseObject (CSequence), etc. SelectItem was
the last holdout and it shipped because nothing CHECKED the invariant.

This tool checks it statically (read-only, stdlib-only, out-of-process):

  CHECK A (precise, curation-free): every method that CALLS
    `AcMainThreadQueue.Enqueue...` must also contain
    `MainThreadGuard.IsOnMainThread` in the same method body. Catches a
    marshalled helper whose gate was removed/botched.

  CHECK B (curated): every invocation of a known AC-STATE-MUTATOR native
    delegate (the hand-listed set below) must sit in a method that contains the
    gate - OR in an allow-listed context (the detour bodies and the
    AcMainThreadQueue drain executor, which run on the main thread by
    construction). Catches a NEW mutator added with no marshal at all (the
    SelectItem-before-74f6d8c situation).

  CHECK C (raw writes): every method that stores into memory with
    `Marshal.Write*(...)` or the `WriteFloat(...)` helper must contain the gate,
    or sit in an allow-listed context, or be on RAW_WRITE_ALLOW. CHECK B only
    sees native delegate calls, so a raw store into a live AC object (the
    SetPlayerHeadingDirect quaternion write, 2026-09-29) slipped past it.

Exit 0 = invariant holds, 1 = a violation (an AC mutator reachable off-thread),
2 = could not read the source tree. Wire into CI next to the golden tests /
pe_pattern gate.
"""
from __future__ import annotations
import os, re, sys

# Resolve the engine Compatibility dir relative to this script so it works both
# locally (C:\Projects\RynthCore\tools\..) and in CI ($GITHUB_WORKSPACE\tools\..).
# Override order: argv[1] > $RC_HOOKS_DIR > derived-from-script-location.
_DERIVED_HOOKS = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "src", "RynthCore.Engine", "Compatibility"))
HOOKS_DIR = (sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith("-")
             else os.environ.get("RC_HOOKS_DIR") or _DERIVED_HOOKS)

# Curated AC-STATE-MUTATOR native delegate fields. Hand-maintained: add a field
# here when a new hook calls an AC function that WRITES client state (selection,
# item move/use, motion, combat-mode, busy count). Readers (Inq*/Get*) are NOT
# listed - they have their own pointer-validation, not a thread gate.
MUTATOR_DELEGATES = {
    "_setSelectedObject", "_useObject", "_useObjectOn", "_useWithTargetEvent",
    "_useEquippedItem", "_moveItemExternal", "_moveItemInternal",
    "_eventStackableMerge", "_addTextToScroll",
    # CM_Inventory sends (inventory game actions): give, wield, drop (Inventory panel)
    "_eventGiveObjectRequest", "_eventGetAndWieldItem", "_eventDropItem",
    # CM_Inventory::Event_NoLongerViewingContents (0x0195, close a corpse/chest; v76 CloseContainer)
    "_eventNoLongerViewingContents",
    "_setAutoRun", "_turnToHeading", "_stopCompletely", "_setMotion",
    "_changeCombatMode",
    "_sendShopEvent",   # VendorTrade: gmVendorUI::SendShopEvent (vendor buy/sell + busy count)
    # PlayerTrade (v72): CM_Trade trade actions + ClientTradeSystem accept/decline (Trade object)
    "_eventOpenTradeNegotiations", "_eventAddToTrade", "_eventResetTrade",
    "_eventCloseTradeNegotiations", "_tradeSystemAcceptTrade", "_tradeSystemDeclineTrade",
    # Salvage panel: open notice + gmSalvageUI AddNewItem/Salvage (UIElement tree)
    "_sendNoticeOpenSalvagePanel", "_gmSalvageUIAddNewItem", "_gmSalvageUISalvage",
    # CM_Inventory::Event_CreateTinkeringTool (0x027D salvage without the panel; v82 SalvageDirect)
    "_eventCreateTinkeringTool",
    # CombatActionHooks game-action sends (AC heap alloc + send queue; 0xC8 also bumps busy count)
    "_requestId", "_queryHealth", "_cancelAttack",
    # MovementActionHooks: CM_Movement::Event_AutonomyLevel (0xF752 game-action send)
    "_autonomyLevel",
    # CharacterManagementHooks: CPlayerSystem::LogOnCharacter (login state + login send);
    # the auto-login poll threads marshal it through MainThreadTick's request slot
    "_logOnCharacter",
    # PlayerTraining: CM_Train raise senders (RaiseAttribute / RaiseVital / RaiseSkill game actions)
    "_eventTrainAttribute", "_eventTrainAttribute2nd", "_eventTrainSkill",
    "_eventTrainSkillAdvancementClass",   # 0x0047 TrainSkill (untrained -> trained with credits)
}
GATE = "MainThreadGuard.IsOnMainThread"
# AcMainThreadQueue.MustQueue(teardown: ...) is the shared mutator gate (MutatorGate.cs): it
# asks MainThreadGuard.IsOnMainThread() itself and returns true (queue) off the main thread.
# Counted only in the gate's own shape, an if that opens with it (optionally after one
# 'flag ||'), searched with // comments stripped, so a commented-out gate is not a gate.
# The check is textual: a string literal that spells the shape would still count.
GATE_SHAPE = re.compile(r"if\s*\((?:\w+\s*\|\|\s*)?AcMainThreadQueue\.MustQueue\(")
ENQUEUE = "AcMainThreadQueue.Enqueue"

# Pure off-thread PRODUCERS: Timer/pump callbacks that ONLY enqueue (never call an
# AC mutator directly), so they legitimately lack the dual-mode IsOnMainThread gate.
# The marshalled AC call always runs on the main thread in AcMainThreadQueue.Drain*.
# (file, method) pairs, each verified to do no off-thread AC mutation.
ENQUEUE_PRODUCER_ALLOW = {
    ("AutoIdService.cs", "DrainTick"),   # appraisal: enqueues 0xC8, sent on main thread in DrainRequestIds
    ("AutoIdService.cs", "QueueIdentify"),   # LiveWorld.QueueIdentify, AutoIdDrain's send on the drain timer: only EnqueueRequestId
    ("RynthCoreChatCommands.cs", "Reply"),   # /rc and /rv plates replies: only EnqueueWriteToChat
    ("AcMouseOver.cs", "Say"),   # /rc acmouse watch lines: only EnqueueWriteToChat (and the log)
}

# CHECK C sinks: raw stores into memory. Comments are stripped before matching.
RAW_WRITE = re.compile(r"\bMarshal\.Write(?:Byte|Int16|Int32|Int64|IntPtr)\s*\(|(?<![\w.])WriteFloat\s*\(")

# Raw writers that do NOT touch live AC object state, so they need no gate.
# (file, method) pairs, each verified by reading the body.
RAW_WRITE_ALLOW = {
    ("PlayerPhysicsHooks.cs", "WriteFloat"),     # the helper itself; its callers are checked
    ("ThreadStackSampler.cs", "SampleAll"),      # writes ContextFlags into its OWN CONTEXT buffer
    ("DatFileShareHooks.cs", "WriteIatPointer"), # IAT slot patch under VirtualProtect (hook plumbing, no callers today)
}

# Methods that run on AC's main thread BY CONSTRUCTION and may call mutators
# without a gate: the [UnmanagedCallersOnly] detour bodies (invoked by AC on its
# own thread) and the hook install/probe/resolve scaffolding. Matched by name
# substring or by an [UnmanagedCallersOnly] attribute on the method.
ALLOW_METHOD_SUBSTR = ("Detour", "Install", "Initialize", "Probe", "Resolve",
                       "Shutdown", "Uninstall")


def split_methods(src):
    """Yield (name, start_line, body_text, attrs_above) for each method via
    brace counting. attrs_above = the few lines immediately preceding the
    signature (to catch [UnmanagedCallersOnly])."""
    lines = src.splitlines()
    sig = re.compile(
        r"^\s*(?:\[[^\]]*\]\s*)*(?:public|private|internal|protected)\b[^;=]*\b([A-Za-z_]\w*)\s*\([^;]*\)\s*$")
    i = 0
    n = len(lines)
    while i < n:
        m = sig.match(lines[i])
        # signature may end with the opening brace on the next non-blank line
        if m:
            name = m.group(1)
            # find opening brace
            j = i + 1
            while j < n and lines[j].strip() == "":
                j += 1
            if j < n and lines[j].strip().startswith("{"):
                depth = 0
                body = []
                k = j
                started = False
                while k < n:
                    depth += lines[k].count("{") - lines[k].count("}")
                    body.append(lines[k])
                    if lines[k].count("{") > 0:
                        started = True
                    if started and depth <= 0:
                        break
                    k += 1
                attrs = "\n".join(lines[max(0, i - 4):i])
                yield name, i + 1, "\n".join(body), attrs
                i = k + 1
                continue
        i += 1


def is_allowed(name, attrs):
    if "UnmanagedCallersOnly" in attrs:
        return True
    return any(s in name for s in ALLOW_METHOD_SUBSTR)


def main():
    print(f"=== off-thread AC-mutator gate check ===\nscanning: {HOOKS_DIR}")
    if not os.path.isdir(HOOKS_DIR):
        print(f"cannot read {HOOKS_DIR}")
        return 2

    violations = []
    checked_a = checked_b = checked_c = 0

    deleg_call = re.compile(r"(" + "|".join(re.escape(d) for d in MUTATOR_DELEGATES) + r")\s*\(")

    for fn in sorted(os.listdir(HOOKS_DIR)):
        if not fn.endswith(".cs"):
            continue
        path = os.path.join(HOOKS_DIR, fn)
        src = open(path, encoding="utf-8", errors="replace").read()
        # AcMainThreadQueue.cs defines Enqueue* and IS the main-thread drain
        # executor - exempt it from both checks.
        is_queue = fn == "AcMainThreadQueue.cs"
        for name, line, body, attrs in split_methods(src):
            gate_code = "\n".join(l.split("//", 1)[0] for l in body.splitlines())
            gated = GATE in body or GATE_SHAPE.search(gate_code) is not None
            allowed = is_allowed(name, attrs) or is_queue

            # CHECK A: calls Enqueue but lacks the gate (unless it's a known pure producer).
            if (ENQUEUE in body) and not gated and not is_queue \
               and (fn, name) not in ENQUEUE_PRODUCER_ALLOW:
                checked_a += 1
                violations.append((fn, line, name, "calls AcMainThreadQueue.Enqueue* without a MainThreadGuard gate"))

            # CHECK B: invokes a curated mutator delegate without the gate.
            if not allowed and deleg_call.search(body) and not gated:
                hit = deleg_call.search(body).group(1)
                checked_b += 1
                violations.append((fn, line, name, f"invokes AC-mutator delegate {hit}() without a MainThreadGuard gate"))

            # CHECK C: raw memory store without the gate.
            if not allowed and not gated and (fn, name) not in RAW_WRITE_ALLOW:
                code = "\n".join(l.split("//", 1)[0] for l in body.splitlines())
                m = RAW_WRITE.search(code)
                if m:
                    checked_c += 1
                    violations.append((fn, line, name, f"raw memory write {m.group(0).rstrip('( ')}() without a MainThreadGuard gate"))

    if violations:
        for fn, line, name, why in violations:
            print(f"  [VIOLATION] {fn}:{line} {name}() - {why}")
        print(f"\nFAIL: {len(violations)} AC mutator(s) reachable off-thread. "
              f"Gate them on MainThreadGuard.IsOnMainThread() + marshal via AcMainThreadQueue, "
              f"or add the method to the allow-list if it is a main-thread-only detour.")
        return 1

    print(f"PASS: every marshalled helper is gated and every curated AC-mutator "
          f"delegate is invoked and every raw memory write sits behind the main-thread gate.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

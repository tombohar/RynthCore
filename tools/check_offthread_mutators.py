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
    "_setAutoRun", "_turnToHeading", "_stopCompletely", "_setMotion",
    "_changeCombatMode",
}
GATE = "MainThreadGuard.IsOnMainThread"
ENQUEUE = "AcMainThreadQueue.Enqueue"

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
    checked_a = checked_b = 0

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
            gated = GATE in body
            allowed = is_allowed(name, attrs) or is_queue

            # CHECK A: calls Enqueue but lacks the gate.
            if (ENQUEUE in body) and not gated and not is_queue:
                checked_a += 1
                violations.append((fn, line, name, "calls AcMainThreadQueue.Enqueue* without a MainThreadGuard gate"))

            # CHECK B: invokes a curated mutator delegate without the gate.
            if not allowed and deleg_call.search(body) and not gated:
                hit = deleg_call.search(body).group(1)
                checked_b += 1
                violations.append((fn, line, name, f"invokes AC-mutator delegate {hit}() without a MainThreadGuard gate"))

    if violations:
        for fn, line, name, why in violations:
            print(f"  [VIOLATION] {fn}:{line} {name}() - {why}")
        print(f"\nFAIL: {len(violations)} AC mutator(s) reachable off-thread. "
              f"Gate them on MainThreadGuard.IsOnMainThread() + marshal via AcMainThreadQueue, "
              f"or add the method to the allow-list if it is a main-thread-only detour.")
        return 1

    print(f"PASS: every marshalled helper is gated and every curated AC-mutator "
          f"delegate is invoked behind the main-thread gate.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

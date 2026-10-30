#!/usr/bin/env python3
"""
Offline check of the raise / train senders (Compatibility/PlayerTraining.cs, plugin API v79
Raise / TrainSkill) against an acclient.exe. Read-only: maps the file, runs nothing.

The engine binds the client's own CM_Train game-action senders, the functions the retail
character window calls for "+" (gmAttributeUI / gmSkillUI RaiseSelection and
Raise10Selection) and for the train dialog (gmSkillUI::TrainSkillDialogCallback). This
proves, on the given client build, that:

  * each embedded pattern is unique and lands on its VA (as pe_pattern.py CHECK does);
  * each target starts a function and is the generated two-argument sender: it reads its
    first argument at [esp+1Ch] and its second at [esp+20h] (after sub esp,0Ch and three
    pushes), writes the expected game-action opcode (0x45 RaiseAttribute, 0x44 RaiseVital,
    0x46 RaiseSkill, 0x47 TrainSkill) and returns with a plain ret (cdecl: the caller pops);
  * the retail character window's own raise/train handlers call exactly these functions
    (direct E8 calls at the Chorizite-map call sites) with two pushed arguments and pop
    8 bytes after: the engine calls them the same way (bool __cdecl (uint, uint)).

Usage:  python tools/train_senders_check.py ["<acclient.exe>" ...]
Default: both client copies (C:\\Turbine\\Asheron's Call and C:\\Games\\RynthCore\\AcClient).
Exit 0 = every check passed on every client given.
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pe_pattern import read_window, matches, parse_engine_patterns  # noqa: E402

DEFAULT_CLIENTS = [r"C:\Turbine\Asheron's Call\acclient.exe", r"C:\Games\RynthCore\AcClient\acclient.exe"]

# name in PlayerTraining.cs -> (opcode, the character window's call sites from the Chorizite
# acclient map: (call address, the function it sits in))
SENDERS = {
    "PlayerTraining.Event_TrainAttribute": (0x45, [
        (0x0049D361, "gmAttributeUI::RaiseSelection"), (0x0049D491, "gmAttributeUI::Raise10Selection")]),
    "PlayerTraining.Event_TrainAttribute2nd": (0x44, [
        (0x0049D38A, "gmAttributeUI::RaiseSelection"), (0x0049D4BA, "gmAttributeUI::Raise10Selection")]),
    "PlayerTraining.Event_TrainSkill": (0x46, [
        (0x0049CBF7, "gmSkillUI::RaiseSelection"), (0x0049BA97, "gmSkillUI::Raise10Selection")]),
    "PlayerTraining.Event_TrainSkillAdvancementClass": (0x47, [
        (0x0049C5B1, "gmSkillUI::TrainSkillDialogCallback")]),
}

fails = 0


def check(ok, msg):
    global fails
    print(("ok   " if ok else "FAIL ") + msg)
    if not ok:
        fails += 1


def run(path):
    print(f"\n=== {path}")
    if not os.path.exists(path):
        check(False, "acclient.exe not found")
        return
    W, BASE = read_window(path)

    def at(va, n):
        o = va - BASE
        return W[o:o + n]

    def rel_callers(target):
        out, i = [], 0
        while True:
            i = W.find(b"\xE8", i)
            if i < 0 or i + 5 > len(W):
                break
            if BASE + i + 5 + struct.unpack_from("<i", W, i + 1)[0] == target:
                out.append(BASE + i)
            i += 1
        return out

    def pops_8_after(call_va):
        after = at(call_va + 5, 16)
        if after.find(b"\x83\xC4\x08") >= 0:
            return True
        if after[0] == 0xEB:   # jmp short to a join that pops
            dest = call_va + 5 + 2 + struct.unpack_from("<b", after, 1)[0]
            return at(dest, 12).find(b"\x83\xC4\x08") >= 0
        return False

    items = {it["name"]: it for it in parse_engine_patterns() if it["file"] == "PlayerTraining.cs" and it["kind"] == "fn"}
    for name, (opcode, sites) in SENDERS.items():
        it = items.get(name)
        check(it is not None, f"{name}: pattern present in PlayerTraining.cs")
        if it is None:
            continue
        ms = matches(W, it["pattern"], limit=4)
        va = BASE + ms[0] if len(ms) == 1 else None
        check(va is not None, f"{name}: pattern unique ({len(ms)} match{'es' if len(ms) != 1 else ''}) at 0x{(va or 0):08X}"
              + (" = the map's VA" if va == it["va"] else f" (map VA 0x{it['va']:08X})"))
        if va is None:
            continue
        check(it["pattern"][-1] == opcode and at(va + 90, 7) == bytes([0xC7, 0x02, opcode, 0, 0, 0, 0x8B]),
              f"{name}: writes game action 0x{opcode:04X}")
        prev = at(va - 4, 4)
        check(prev[-1] in (0x90, 0xCC, 0xC3) or (prev[-3] == 0xC2 and prev[-1] == 0x00), f"{name}: starts a function (before: {prev.hex(' ')})")
        check(at(va, 6) == bytes([0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57]), f"{name}: sub esp,0Ch / push ebx,esi,edi")
        body = at(va, 0x180)
        check(body.find(bytes([0x8B, 0x4C, 0x24, 0x1C])) >= 0 and body.find(bytes([0x8B, 0x54, 0x24, 0x20])) >= 0,
              f"{name}: reads two stack arguments ([esp+1Ch], [esp+20h])")
        end = body.find(bytes([0x83, 0xC4, 0x0C, 0xC3]))
        check(end > 0 and body[:end].find(bytes([0xC2, 0x08, 0x00])) < 0 and body[:end].find(bytes([0xC2, 0x04, 0x00])) < 0,
              f"{name}: add esp,0Ch / ret at +0x{end:X} (cdecl, no ret N)")
        check(body[:end].find(bytes([0x8A, 0xD8])) >= 0, f"{name}: returns the send's bool in al (mov bl,al ... )")
        callers = rel_callers(va)
        for call, owner in sites:
            check(call in callers, f"{name}: called by the character window ({owner}, call at 0x{call:08X})")
            if call in callers:
                check(pops_8_after(call), f"{name}: {owner} pops 8 bytes after the call (two cdecl arguments)")
        print(f"     {len(callers)} direct call site(s): " + ", ".join(f"0x{c:08X}" for c in callers))


def main():
    paths = sys.argv[1:] or DEFAULT_CLIENTS
    for p in paths:
        run(p)
    print()
    print("PASS" if fails == 0 else f"FAIL ({fails})")
    return 0 if fails == 0 else 1


if __name__ == "__main__":
    sys.exit(main())

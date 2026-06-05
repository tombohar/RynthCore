#!/usr/bin/env python3
"""
Offline byte-pattern extractor / uniqueness verifier for acclient.exe hook hardening.

Verifies over the EXACT window the engine's AcClientModule.TryReadTextSection builds at
runtime: the live virtual image from RVA 0x1000, length min(SizeOfImage-0x1000, 0x400000).
That window spans .text AND the start of .rdata/.data, so "unique here" == "unique at runtime"
(stronger than checking .text alone). For each (symbol, VA) it:
  1. maps VA -> window offset,
  2. cuts a prologue window, wildcarding rel32 operands of E8/E9 (call/jmp) and 0F 8x (jcc),
  3. finds the SHORTEST prefix that occurs exactly once in the window,
  4. confirms that single match lands at the requested VA,
  5. prints a ready-to-paste C# byte?[] literal.

Usage:  python pe_pattern.py "<path-to-acclient.exe>" [group]
"""
import sys, struct

DEFAULT_TEXT_RVA = 0x1000
MAX_TEXT_BYTES = 0x400000
SUSPICIOUS = 0x500000

def read_window(path):
    with open(path, "rb") as f:
        data = f.read()
    e = struct.unpack_from("<I", data, 0x3C)[0]
    coff = e + 4
    num_sec = struct.unpack_from("<H", data, coff + 2)[0]
    size_opt = struct.unpack_from("<H", data, coff + 16)[0]
    opt = coff + 20
    if struct.unpack_from("<H", data, opt)[0] != 0x10B:
        raise SystemExit("expected PE32 (x86)")
    image_base = struct.unpack_from("<I", data, opt + 28)[0]
    size_of_image = struct.unpack_from("<I", data, opt + 56)[0]
    sec_tbl = opt + size_opt
    # Reconstruct the live virtual image (sections placed at their RVAs, gaps zero-filled).
    virt = bytearray(size_of_image)
    for i in range(num_sec):
        b = sec_tbl + i * 40
        vaddr = struct.unpack_from("<I", data, b + 12)[0]
        rawsize = struct.unpack_from("<I", data, b + 16)[0]
        rawptr = struct.unpack_from("<I", data, b + 20)[0]
        end = min(vaddr + rawsize, size_of_image)
        chunk = data[rawptr:rawptr + (end - vaddr)]
        virt[vaddr:vaddr + len(chunk)] = chunk
    # Mirror AcClientModule's window-sizing exactly.
    raw_text = size_of_image - DEFAULT_TEXT_RVA
    if raw_text <= 0 or raw_text > SUSPICIOUS:
        textsize = min(max(0, raw_text), MAX_TEXT_BYTES)
    else:
        textsize = min(raw_text, MAX_TEXT_BYTES)
    window = bytes(virt[DEFAULT_TEXT_RVA:DEFAULT_TEXT_RVA + textsize])
    return window, image_base + DEFAULT_TEXT_RVA

def wildcard_window(buf, off, n=56):
    win = list(buf[off:off + n])
    pat = list(win)
    i = 0
    while i < len(win):
        b = win[i]
        if b in (0xE8, 0xE9):
            for k in range(1, 5):
                if i + k < len(pat): pat[i + k] = None
            i += 5
        elif b == 0x0F and i + 1 < len(win) and 0x80 <= win[i + 1] <= 0x8F:
            for k in range(2, 6):
                if i + k < len(pat): pat[i + k] = None
            i += 6
        else:
            i += 1
    return pat

def matches(text_b, pat, limit=6):
    first_wc = next((k for k, p in enumerate(pat) if p is None), len(pat))
    if first_wc == 0:
        return [-2]
    prefix = bytes(pat[:first_wc])
    out, start = [], 0
    while True:
        idx = text_b.find(prefix, start)
        if idx < 0: break
        ok = all(pat[j] is None or text_b[idx + j] == pat[j] for j in range(first_wc, len(pat)))
        if ok:
            out.append(idx)
            if len(out) > limit: break
        start = idx + 1
    return out

def shortest_unique(text_b, full_pat, text_base, lo=8):
    for L in range(lo, len(full_pat) + 1):
        pat = full_pat[:L]
        while pat and pat[-1] is None:
            pat = pat[:-1]
        if not pat: continue
        ms = matches(text_b, pat)
        if len(ms) == 1:
            return pat, text_base + ms[0]
    ms = matches(text_b, full_pat)
    return full_pat, (text_base + ms[0] if ms else -1)

def cs(pat):
    return "[ " + ", ".join("null" if p is None else f"0x{p:02X}" for p in pat) + " ]"

GROUPS = {
    "ClientObjectHooks": [
        ("GetWeenieObject", 0x005583F0), ("GetNumContainedItems", 0x0058CCE0),
        ("GetNumContainedContainers", 0x0058CCF0), ("GetObjectNameStatic", 0x0058F840),
        ("GetObjectNameInstance", 0x0058F510), ("InqType", 0x0058D700),
        ("InqInt", 0x00590C20), ("InqFloat", 0x00590CD0), ("InqInt64", 0x00590C70),
        ("InqAttribute2ndBaseLevel", 0x00592D20), ("InqBool", 0x00590CA0),
        ("InqString", 0x005919F0), ("GetCombatSystem", 0x0056B210),
        ("ObjectIsAttackable", 0x0056B340), ("IsSpellKnown", 0x0058FCF0),
        ("InqSkillLevel", 0x00593380), ("InqSkillAdvancementClass", 0x00592B70),
        ("InqAttribute", 0x00592700), ("GetVitaeValue", 0x0058FE80),
    ],
}

def main():
    if len(sys.argv) < 2:
        raise SystemExit("usage: pe_pattern.py <acclient.exe> [group]")
    text_b, text_base = read_window(sys.argv[1])
    group = sys.argv[2] if len(sys.argv) > 2 else "ClientObjectHooks"
    print(f"window base VA = 0x{text_base:08X}, window size = {len(text_b)} bytes\n")
    ok = bad = 0
    for name, va in GROUPS[group]:
        off = va - text_base
        if off < 0 or off >= len(text_b):
            print(f"[SKIP] {name:30} 0x{va:08X} not in window"); bad += 1; continue
        full = wildcard_window(text_b, off)
        pat, mva = shortest_unique(text_b, full, text_base)
        n = len(matches(text_b, pat))
        good = (n == 1 and mva == va)
        ok += good; bad += (not good)
        print(f"[{'OK ' if good else '!! '}] {name:30} VA=0x{va:08X} matches={n} at=0x{mva:08X} len={len(pat)}")
        print(f"        {cs(pat)}")
    print(f"\nUnique+correct: {ok}/{ok+bad}")

if __name__ == "__main__":
    main()

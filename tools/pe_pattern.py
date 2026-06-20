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

Usage:  python pe_pattern.py "<path-to-acclient.exe>" [group|ALL]
Only FUNCTION (.text) VAs belong here. Data/global addresses (.data/.rdata singletons,
RecvFrom slot, s_NullBuffer, selection globals) are Phase B — string/vtable discovery, not
.text pattern scan.
"""
import sys, struct, re, os, glob

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
    virt = bytearray(size_of_image)
    for i in range(num_sec):
        b = sec_tbl + i * 40
        vaddr = struct.unpack_from("<I", data, b + 12)[0]
        rawsize = struct.unpack_from("<I", data, b + 16)[0]
        rawptr = struct.unpack_from("<I", data, b + 20)[0]
        end = min(vaddr + rawsize, size_of_image)
        chunk = data[rawptr:rawptr + (end - vaddr)]
        virt[vaddr:vaddr + len(chunk)] = chunk
    raw_text = size_of_image - DEFAULT_TEXT_RVA
    if raw_text <= 0 or raw_text > SUSPICIOUS:
        textsize = min(max(0, raw_text), MAX_TEXT_BYTES)
    else:
        textsize = min(raw_text, MAX_TEXT_BYTES)
    window = bytes(virt[DEFAULT_TEXT_RVA:DEFAULT_TEXT_RVA + textsize])
    return window, image_base + DEFAULT_TEXT_RVA

def wildcard_window(buf, off, n=160):
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

# FUNCTION (.text) VAs only, grouped by the engine hook file that binds them.
# Dups across files (same VA) are noted; resolve once in the catalog.
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
    "ClientHelperHooks": [
        ("SetSelectedObject", 0x0058D110), ("GetAcPlugin", 0x0055A740),
        ("UseObjectOn", 0x0055A8C0), ("UseEquippedItem", 0x0055A910),
        ("MoveItemExternal", 0x0055A9E0), ("MoveItemInternal", 0x0055AA00),
        ("Event_StackableMerge", 0x006ACDD0), ("Event_GiveObjectRequest", 0x006ACA60),
        ("ClientUISystem_UseObject", 0x00565750),
        ("InqPlayerCoords", 0x00560E00), ("GetPlayerId", 0x0048E5F0),
        ("AddTextToScroll", 0x005649F0), ("UseWithTargetEvent", 0x006AD3E0),
        ("SendNotice_OpenSalvagePanel", 0x006AD4F0), ("gmSalvageUI_AddNewItem", 0x004CC020),
        ("gmSalvageUI_Salvage", 0x004CC430), ("PStringBaseW_ctor", 0x00402730),
        ("PStringBaseW_dtor", 0x004011B0),
    ],
    "PlayerVitalsHooks": [
        ("UpdateAttribute2nd", 0x00559900), ("UpdateAttribute2ndLevel", 0x00559920),
        ("PrivateUpdateAttribute2nd", 0x00559B20), ("PrivateUpdateAttribute2ndLevel", 0x00559B50),
        ("OnStatUpdatedInt", 0x0058ED50), ("InqAttribute2nd_struct", 0x005927F0),
        ("SendNotice_PlayerDescReceived", 0x0047A200), ("InqAttribute2nd_uint", 0x00592D20),
    ],
    "ClientCombatHooks": [
        ("GetCombatSystem", 0x0056B210), ("SetRequestedAttackHeight", 0x0056D640),
        ("StartAttackRequest", 0x0056CD90), ("EndAttackRequest", 0x0056CE30),
        ("PlayerInReadyPosition", 0x0056C570), ("AutoTarget", 0x0056C9D0),
        ("SendAttackHeightChanged", 0x006AAE10),
    ],
    "CharacterManagementHooks": [
        ("UIFlow_GetPersistantData", 0x0051DFB0), ("GetPlayerSystem", 0x0055E1D0),
        ("LogOnCharacter", 0x00560600), ("CharacterSet_GetIdentity", 0x004E8B20),
        ("CharacterSet_GetName", 0x004FE980), ("CharacterSet_GetGid", 0x004FE9B0),
    ],
    "ChatCommandDispatcher": [
        ("Event_Talk", 0x006A53E0), ("Event_Emote", 0x006A4F40),
        ("Event_SoulEmote", 0x006A5320), ("Event_TalkDirectByName", 0x006A55A0),
        ("Event_ChannelBroadcast", 0x006A4E50), ("PStringBaseC_ctor", 0x0048C3E0),
        ("PStringBaseC_Clear", 0x004AB990),
    ],
    "CombatActionHooks": [
        ("Handle_Combat_QueryHealthResponse", 0x006AA900), ("CastSpell", 0x00568DE0),
        ("GetMagicSystem", 0x00567C00), ("FreeHandsAndCastSpell", 0x00567C90),
    ],
    "PlayerPhysicsHooks": [
        ("CPhysicsObj_get_heading", 0x00512010), ("CPhysicsObj_set_heading", 0x00514C60),
        ("CMotionInterp_get_max_speed", 0x005288C0),
    ],
    "GameTickHooks": [
        ("Client_UseTime", 0x00411FA0),
    ],
    "TimeSyncHooks": [
        ("ClientNet_HandleTimeSynch", 0x005448F0),
    ],
}

def best_pattern(text_b, text_base, va, off):
    """Prefer a drift-tolerant wildcarded pattern; if template-siblings share it (the rel32
    call target being the only differentiator), fall back to a literal pattern. Returns
    (mode, pat) where mode is 'wild' | 'lit', or (None, None) if no unique prologue exists."""
    for mode in ("wild", "lit"):
        full = wildcard_window(text_b, off) if mode == "wild" else list(text_b[off:off + 160])
        pat, mva = shortest_unique(text_b, full, text_base)
        if len(matches(text_b, pat)) == 1 and mva == va:
            return mode, pat
    return None, None

# DATA/global addresses (.data/.rdata) — resolved by code-xref: find a unique .text site
# that references the address as an absolute operand, wildcard the 4 operand bytes, and read
# the address back from the matched operand at runtime. Robust if the global moves.
GROUPS_DATA = [
    ("CPlayerSystem", 0x0087119C), ("ClientUISystem", 0x00871354),
    ("CommunicationSystem", 0x00870BE4), ("s_pCombatSystem", 0x0087166C),
    ("CObjectMaint_s_pcInstance", 0x00842ADC), ("UIFlow", 0x0083E72C),
    ("s_selected_id", 0x00871E54), ("s_previous_selected_id", 0x00871E58),
    ("split_amount", 0x0081D7EC), ("total_stack", 0x0081D7F0),
    ("PStringChar_s_NullBuffer", 0x008EF11C), ("PStringWide_s_NullBuffer", 0x00818340),
    ("RecvFrom_slot", 0x007935AC),
]

def _find_all(text_b, needle, limit=80):
    out, start = [], 0
    while True:
        i = text_b.find(needle, start)
        if i < 0: break
        out.append(i); start = i + 1
        if len(out) > limit: break
    return out

def data_xref(text_b, va):
    """Unique .text code site referencing data VA as an absolute operand.
    Returns (pattern with the 4 operand bytes wildcarded, operand_offset) or None."""
    needle = struct.pack("<I", va & 0xFFFFFFFF)
    best = None
    for idx in _find_all(text_b, needle):
        for lead in (2, 1, 3, 5, 8, 12):
            if idx - lead < 0: continue
            done = False
            for tail in range(0, 26, 2):
                pat = list(text_b[idx - lead:idx]) + [None] * 4 + list(text_b[idx + 4:idx + 4 + tail])
                ms = matches(text_b, pat)
                if len(ms) == 1:
                    if struct.unpack_from("<I", text_b, ms[0] + lead)[0] == (va & 0xFFFFFFFF):
                        if best is None or len(pat) < best[0]:
                            best = (len(pat), pat, lead)
                    done = True
                    break
            if done: break
    return (best[1], best[2]) if best else None

def process_data(text_b):
    print("=== DATA (.data/.rdata globals via code-xref) ===")
    ok = bad = 0
    for name, va in GROUPS_DATA:
        res = data_xref(text_b, va)
        if res is None:
            bad += 1
            print(f"[!! ] {name:28} 0x{va:08X}  no unique code-xref found"); continue
        pat, off = res
        ok += 1
        print(f"[OK ] {name:28} 0x{va:08X}  operandOffset={off} len={len(pat)}")
        print(f"        {cs(pat)}")
    print(f"\nData xrefs found: {ok}/{ok + bad}")
    return ok, bad

def _parse_vas(args):
    out = []
    for a in args:
        a = a.strip().rstrip(",")
        if a:
            out.append(int(a, 16))
    return out

def process_gen(text_b, text_base, vas):
    """Ad-hoc: cut a unique FUNCTION pattern for each VA passed on the command line
    (read-only; does NOT touch GROUPS). For converting a fixed-VA+VerifyBytes hook to
    HookResolver.Resolve without first editing this file. Prints a paste-ready byte?[]."""
    print("=== GEN (ad-hoc function patterns) ===")
    for va in vas:
        off = va - text_base
        if off < 0 or off >= len(text_b):
            print(f"[SKIP] 0x{va:08X} not in window"); continue
        mode, pat = best_pattern(text_b, text_base, va, off)
        if pat is None:
            print(f"[TB!] 0x{va:08X} no unique prologue in 160B -> TIER-B (keep fixed VA + VerifyBytes)")
            continue
        print(f"[{'OK ' if mode == 'wild' else 'LIT'}] 0x{va:08X} unique len={len(pat)} mode={mode}")
        print(f"        {cs(pat)}")

def process_gendata(text_b, vas):
    """Ad-hoc: cut a code-xref pattern for each DATA/global VA on the command line."""
    print("=== GENDATA (ad-hoc data globals via code-xref) ===")
    for va in vas:
        res = data_xref(text_b, va)
        if res is None:
            print(f"[!! ] 0x{va:08X} no unique code-xref found"); continue
        pat, off = res
        print(f"[OK ] 0x{va:08X} operandOffset={off} len={len(pat)}")
        print(f"        {cs(pat)}")

def process(text_b, text_base, group):
    print(f"=== {group} ===")
    ok = bad = 0
    for name, va in GROUPS[group]:
        off = va - text_base
        if off < 0 or off >= len(text_b):
            print(f"[SKIP] {name:34} 0x{va:08X} not in window"); bad += 1; continue
        mode, pat = best_pattern(text_b, text_base, va, off)
        if pat is None:
            bad += 1
            print(f"[TB!] {name:34} VA=0x{va:08X} no unique prologue in 160B -> TIER-B (fixed VA + VerifyBytes)")
            continue
        ok += 1
        print(f"[{'OK ' if mode == 'wild' else 'LIT'}] {name:34} VA=0x{va:08X} unique len={len(pat)} mode={mode}")
        print(f"        {cs(pat)}")
    return ok, bad

# ── CHECK mode: verify the patterns ACTUALLY EMBEDDED in the engine source still resolve
# uniquely + correctly against a given acclient.exe. Catches binary drift AND transcription
# typos before deploy. Exits non-zero on any failure (usable as a CI / pre-deploy gate).
def _compat_dir():
    return os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                        "src", "RynthCore.Engine", "Compatibility")

def parse_engine_patterns():
    srcs = {}
    for f in glob.glob(os.path.join(_compat_dir(), "*.cs")):
        with open(f, "r", encoding="utf-8") as fh:
            # Strip // line comments so commas / brackets inside an inline disasm
            # comment (e.g. `0x84, 0xC0  // MOV AL,[ESP+4]; TEST AL,AL`) can't corrupt
            # the byte?[] split or truncate the array match at a `]` inside the comment.
            srcs[f] = re.sub(r'//[^\n]*', '', fh.read())
    consts = {}
    for s in srcs.values():
        for m in re.finditer(r'const\s+u?int\s+(\w+)\s*=\s*[^;]*?0x([0-9A-Fa-f]+)', s):
            consts[m.group(1)] = int(m.group(2), 16)
    patterns = {}
    for s in srcs.values():
        for m in re.finditer(r'byte\?\[\]\s+(\w+)\s*=\s*\[(.*?)\]\s*;', s, re.DOTALL):
            pat, ok = [], True
            for t in m.group(2).split(','):
                t = t.strip()
                if not t:
                    continue
                if t == 'null':
                    pat.append(None)
                elif re.fullmatch(r'0x[0-9A-Fa-f]+', t):
                    pat.append(int(t, 16))
                else:
                    ok = False; break
            if ok and pat:
                patterns[m.group(1)] = pat
    def resolve_va(tail):
        for name, val in consts.items():
            if re.search(r'\b' + re.escape(name) + r'\b', tail):
                return val
        m = re.search(r'0x([0-9A-Fa-f]+)', tail)
        return int(m.group(1), 16) if m else None
    items = []
    seen = set()
    def add(kind, name, patvar, va, off, fn):
        key = (name, patvar, fn)
        if patvar in patterns and va is not None and key not in seen:
            seen.add(key)
            items.append({'kind': kind, 'name': name, 'pattern': patterns[patvar], 'va': va, 'off': off, 'file': fn})
    for f, s in srcs.items():
        fn = os.path.basename(f)
        for m in re.finditer(r'ResolveData(?:Va)?\(\s*(?:\w+,\s*)?"([^"]+)",\s*(\w+)\s*,\s*(\d+)\s*,\s*([^;]+?)[;\n]', s):
            add('data', m.group(1), m.group(2), resolve_va(m.group(4)), int(m.group(3)), fn)
        for m in re.finditer(r'(?:HookResolver\.Resolve|Bind(?:Resolved)?<[^>]+>|ResolveAddr)\(\s*\w+,\s*"([^"]+)",\s*(\w+)\s*,\s*([^;]+?)[;\n]', s):
            add('fn', m.group(1), m.group(2), resolve_va(m.group(3)), 0, fn)
        for m in re.finditer(r'Resolve(?:Va|FnVa)\(\s*"([^"]+)",\s*(\w+)\s*,\s*([^;]+?)[;\n]', s):
            add('fn', m.group(1), m.group(2), resolve_va(m.group(3)), 0, fn)
    return items

# Fail CLOSED: if the source-parsing regexes in parse_engine_patterns() ever
# stop matching after an engine refactor (renamed Resolve, reordered args,
# new pattern syntax), items silently shrinks toward 0 and CHECK would print
# "PASS - all 0 embedded patterns" with exit 0 - a green gate verifying
# nothing. As of 2026-06-11 the engine embeds 120 patterns; a parse count
# below this floor means the PARSER broke, not that patterns were removed.
# Raise/lower deliberately when the real pattern count changes.
CHECK_MIN_PATTERNS = 100

def process_check(text_b, text_base):
    items = parse_engine_patterns()
    if len(items) < CHECK_MIN_PATTERNS:
        print(f"FAIL - only {len(items)} embedded patterns parsed from the engine source "
              f"(floor: {CHECK_MIN_PATTERNS}). The CHECK parser has likely drifted from the "
              f"engine's Resolve-site syntax - fix parse_engine_patterns() before trusting this gate.")
        return 1
    print(f"Verifying {len(items)} embedded engine patterns against this acclient.exe...\n")
    fails = 0
    for it in sorted(items, key=lambda i: (i['file'], i['name'])):
        ms = matches(text_b, it['pattern'], limit=16)
        if it['kind'] == 'fn':
            ok = (it['va'] - text_base) in set(ms)
            why = "" if ok else ("pattern NOT FOUND" if not ms else f"matches {len(ms)} site(s), none at VA")
        else:
            if len(ms) == 1 and ms[0] + it['off'] + 4 <= len(text_b):
                operand = struct.unpack_from('<I', text_b, ms[0] + it['off'])[0]
                ok = operand == (it['va'] & 0xFFFFFFFF)
                why = "" if ok else f"operand 0x{operand:08X} != expected 0x{it['va']:08X}"
            else:
                ok = False; why = f"{len(ms)} matches (data-xref needs exactly 1)"
        if not ok:
            fails += 1
            print(f"[FAIL] {it['file']:30} {it['name']:38} VA=0x{it['va']:08X}  {why}")
    print("=" * 60)
    if fails == 0:
        print(f"PASS - all {len(items)} embedded patterns unique & correct on this acclient.exe.")
        return 0
    print(f"FAIL - {fails}/{len(items)} embedded patterns did NOT verify. Re-cut with pe_pattern.py ALL/DATA, fix the source, rebuild.")
    return 1

def main():
    if len(sys.argv) < 2:
        raise SystemExit("usage: pe_pattern.py <acclient.exe> [group|ALL|DATA|CHECK|GEN <va...>|GENDATA <va...>]")
    text_b, text_base = read_window(sys.argv[1])
    sel = sys.argv[2] if len(sys.argv) > 2 else "ALL"
    print(f"window base VA = 0x{text_base:08X}, window size = {len(text_b)} bytes\n")
    if sel.upper() == "CHECK":
        sys.exit(process_check(text_b, text_base))
    if sel.upper() == "GEN":
        process_gen(text_b, text_base, _parse_vas(sys.argv[3:])); return
    if sel.upper() == "GENDATA":
        process_gendata(text_b, _parse_vas(sys.argv[3:])); return
    if sel.upper() == "DATA":
        process_data(text_b); return
    groups = list(GROUPS) if sel.upper() == "ALL" else [sel]
    tok = tbad = 0
    for g in groups:
        ok, bad = process(text_b, text_base, g)
        tok += ok; tbad += bad
        print()
    print(f"TOTAL unique+correct: {tok}/{tok + tbad}")

if __name__ == "__main__":
    main()

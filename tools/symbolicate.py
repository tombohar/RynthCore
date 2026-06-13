#!/usr/bin/env python3
# ============================================================================
#  symbolicate.py - turn RynthCore crash/hang logs into readable stacks.
#
#  RynthCore's CrashLogger / MainThreadHangWatchdog emit native frames as
#  "<module>+0xRVA" (e.g. "acclient.exe+0x15FA24") because NativeAOT has no
#  symbols for acclient.exe at runtime. We DO have a full Ghidra decompile of
#  the same binary (GhidraTools/symbols.csv, ~48k named functions), so this
#  offline tool post-processes a log and rewrites every acclient frame into
#     "ClientCombatSystem::Foo+0x12 [acclient.exe+0x15FA24]"
#  It also flags addresses that match the catalogue of known recurring AVs.
#
#  Pure post-processing of a text file vs a CSV. Out-of-process, zero runtime
#  risk to the injected engine. Re-runnable.
#
#  Usage:
#     python symbolicate.py [LOGFILE] [-o OUT] [--symbols PATH] [--base 0x400000]
#
#  LOGFILE defaults to the newest C:\Games\RynthCore\Logs\RynthCore*.log.
#  With no -o, annotated output goes to stdout.
# ============================================================================

import argparse
import bisect
import csv
import glob
import os
import re
import sys

# Logs contain non-ASCII (em-dashes, etc). Force UTF-8 on the streams so piping
# a large annotated log to a Windows console/pipe doesn't UnicodeEncodeError.
for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

DEFAULT_SYMBOLS = r"C:\Projects\GhidraTools\symbols.csv"
DEFAULT_LOGDIR = r"C:\Games\RynthCore\Logs"
DEFAULT_BASE = 0x400000          # Ghidra image base for acclient.exe (no-ASLR)
SYMBOL_MODULE = "acclient.exe"   # the only module we have symbols for

# module+0xRVA tokens, e.g. "acclient.exe+0x15FA24" or "  ebp[03] ret=... acclient.exe+0x1234"
FRAME_RE = re.compile(r"\b([A-Za-z0-9_.\-]+\.(?:exe|dll))\+0x([0-9A-Fa-f]+)\b")

# Catalogue of known recurring faults, keyed by absolute VA (base + rva).
# Sourced from the RynthCore crash-investigation notes; extend as new ones land.
KNOWN_CRASHES = {
    0x0055FA24: "object-teardown corruption (DBOCache::DestroyObj / "
                "List<ObjectRangeInfo>::remove) from OFF-THREAD AC mutation "
                "-> marshal mutations onto the AC tick. [rynthcore_crash_investigation]",
    0x00416C86: "object-teardown class (NOT a CastSpell fault, despite "
                "appearances). [rynthcore_castspell_fallback_pitfall]",
    0x0056547B: "ClientUISystem::UpdateCursorState reading the freed combat "
                "singleton on a trailing mouse/cursor msg after WM_CLOSE -> "
                "swallow mouse input once WM_CLOSE seen. [rynthcore_freeze_on_close_fix]",
    0x00460D1D: "UIElement::GetAttribute_Bool (0x460CC0+0x5D) smart-ptr refcount "
                "writeback into read-only .text: AC's selection/targeting UIElement "
                "subtree was corrupted by an OFF-THREAD SetSelectedObject (0x58D110) "
                "racing AC's main-thread UI walk during corpse-loot SelectItem. "
                "regs eax/dataAddr=0x472175 ecx=0x472171 edi=0xD; often two threads "
                "faulting at once. FIXED 74f6d8c (main-thread marshal). NOT "
                "AddTextToScroll / NOT the 'too busy' chat spam -- that was a stale "
                "misattribution, corrected 2026-06-13. [rynthcore_offthread_av_class]",
}


def load_symbols(path):
    """Return (sorted_vas, names_by_index) for nearest-preceding lookup."""
    pairs = []
    with open(path, "r", encoding="utf-8-sig", newline="") as f:
        reader = csv.DictReader(f)
        for row in reader:
            addr = (row.get("address") or "").strip().strip('"')
            name = (row.get("name") or "").strip().strip('"')
            if not addr or not name:
                continue
            try:
                va = int(addr, 16) if addr.lower().startswith("0x") else int(addr, 16)
            except ValueError:
                continue
            pairs.append((va, name))
    pairs.sort(key=lambda p: p[0])
    vas = [p[0] for p in pairs]
    names = [p[1] for p in pairs]
    return vas, names


def resolve(va, vas, names):
    """Nearest preceding symbol for an absolute VA -> 'name+0xNN' or None."""
    i = bisect.bisect_right(vas, va) - 1
    if i < 0:
        return None
    base = vas[i]
    off = va - base
    # Guard against absurd offsets (address past the last symbol / in another section)
    if off > 0x20000:
        return None
    return f"{names[i]}+0x{off:X}" if off else names[i]


def newest_log(logdir):
    cands = glob.glob(os.path.join(logdir, "RynthCore*.log"))
    cands = [c for c in cands if not c.endswith(".old")]
    if not cands:
        cands = glob.glob(os.path.join(logdir, "RynthCore*.log*"))
    if not cands:
        return None
    return max(cands, key=os.path.getmtime)


def main():
    ap = argparse.ArgumentParser(description="Symbolicate RynthCore crash/hang logs.")
    ap.add_argument("logfile", nargs="?", help="log to annotate (default: newest in %s)" % DEFAULT_LOGDIR)
    ap.add_argument("-o", "--out", help="write annotated log here (default: stdout)")
    ap.add_argument("--symbols", default=DEFAULT_SYMBOLS, help="Ghidra symbols.csv (default: %(default)s)")
    ap.add_argument("--base", default=hex(DEFAULT_BASE), help="acclient.exe image base (default: %(default)s)")
    ap.add_argument("--only-crash", action="store_true",
                    help="emit only lines belonging to a CRASH/HANG/EXCEPTION block")
    args = ap.parse_args()

    base = int(args.base, 16) if args.base.lower().startswith("0x") else int(args.base, 16)

    logfile = args.logfile or newest_log(DEFAULT_LOGDIR)
    if not logfile or not os.path.exists(logfile):
        sys.exit(f"error: log file not found ({logfile!r}). Pass one explicitly.")

    if not os.path.exists(args.symbols):
        sys.exit(f"error: symbols file not found: {args.symbols}")

    vas, names = load_symbols(args.symbols)
    sys.stderr.write(f"[symbolicate] {len(vas)} symbols from {args.symbols}\n")
    sys.stderr.write(f"[symbolicate] annotating {logfile} (base=0x{base:X})\n")

    out_lines = []
    hits = 0
    known_hits = []
    block_markers = ("CRASH", "HANG", "EXCEPTION", "PROCESS-KILLER", "SUEF", "VEH")
    in_block = False

    with open(logfile, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.rstrip("\n")

            # Track crash/hang blocks for --only-crash filtering.
            upper = line.upper()
            if any(m in upper for m in block_markers):
                in_block = True
            elif line.startswith("====") and "====" in line[4:]:
                in_block = False

            def repl(m):
                nonlocal hits
                module, rva_hex = m.group(1), m.group(2)
                if module.lower() != SYMBOL_MODULE:
                    return m.group(0)        # we only have acclient symbols
                rva = int(rva_hex, 16)
                va = base + rva
                name = resolve(va, vas, names)
                if name is None:
                    return m.group(0)
                hits += 1
                tag = ""
                if va in KNOWN_CRASHES:
                    tag = "  <<< KNOWN"
                    known_hits.append((va, name, KNOWN_CRASHES[va]))
                return f"{name} [{m.group(0)}]{tag}"

            annotated = FRAME_RE.sub(repl, line)

            if not args.only_crash or in_block:
                out_lines.append(annotated)

    body = "\n".join(out_lines) + "\n"

    # Footer summary of known-crash matches.
    footer = []
    if known_hits:
        footer.append("")
        footer.append("================ KNOWN-CRASH MATCHES ================")
        seen = set()
        for va, name, note in known_hits:
            if va in seen:
                continue
            seen.add(va)
            footer.append(f"  0x{va:08X}  {name}")
            footer.append(f"            -> {note}")
        footer.append("====================================================")
    footer.append(f"[symbolicate] resolved {hits} acclient frame(s); "
                  f"{len(set(v for v, _, _ in known_hits))} known-crash address(es).")

    text = body + "\n".join(footer) + "\n"

    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(text)
        sys.stderr.write(f"[symbolicate] wrote {args.out}\n")
    else:
        sys.stdout.write(text)


if __name__ == "__main__":
    main()

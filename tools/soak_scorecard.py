#!/usr/bin/env python3
"""
soak_scorecard.py - out-of-process soak verdict for RynthCore clients.

WHY THIS EXISTS
---------------
A 12-hour soak on 2026-06-12 was declared "solid" because the client never
crashed - but combat was 100% broken the entire time (a predicted-kill-swap
aborted every cast; nothing died for 12h). "Alive" is not "healthy." This tool
reads the per-PID logs + native-crash.log and produces a verdict across THREE
axes so that blind spot can't recur:

  1. LIVENESS   - heartbeat continuity, fps, plugin-pump rate, wedge signature.
  2. STABILITY  - crash blocks, hang dumps, force-clear / reconcile counters.
  3. PRODUCTIVITY - kills, casts that LANDED ("You cast"), loot completed.
                    A client that is up but killing nothing FAILS here.

Read-only. No deps beyond the stdlib. Run after (or during) a soak:

    python tools/soak_scorecard.py                 # newest per-PID log
    python tools/soak_scorecard.py --pid 6560      # a specific client
    python tools/soak_scorecard.py --all           # every client in THIS soak
    python tools/soak_scorecard.py --since 14:46    # window starts at 14:46 today
    python tools/soak_scorecard.py --max-age-h 8    # window = last 8h
    python tools/soak_scorecard.py --watch 60       # re-verdict every 60s

SOAK WINDOW (why this matters): the log dir accumulates per-PID logs across
DAYS, and native-crash.log is shared and has no per-block timestamps. Scoring
the whole pile makes a clean current run FAIL on yesterday's pre-fix crashes
(observed 2026-06-13: a healthy 7h client failed because morning pre-fix logs
and a 10h-stale crash block were counted). So by default the window starts at
the CURRENT soak's start (first heartbeat of the newest live log); older logs
are excluded as previous soaks, and crash blocks count only if native-crash.log
was written inside the window. Override with --since / --max-age-h.

Exit code 0 = PASS, 1 = FAIL, 2 = no log found. So it can gate CI / a
launcher hook later.
"""
from __future__ import annotations
import argparse, glob, os, re, sys, time
from datetime import datetime

LOG_DIR = r"C:\Games\RynthCore\Logs"
DUMP_DIR = os.path.join(LOG_DIR, "dumps")
CRASH_LOG = os.path.join(LOG_DIR, "native-crash.log")

# hb #N up=Ss fps=F plug=P/s ws=MMB login=1 qd=Q rec=R fcl=C
HB = re.compile(
    r"hb #(?P<n>\d+) up=(?P<up>\d+)s fps=(?P<fps>\d+) plug=(?P<plug>\d+)/s "
    r"ws=(?P<ws>\d+)MB login=(?P<login>\d)(?: qd=(?P<qd>\d+))?(?: rec=(?P<rec>\d+))?(?: fcl=(?P<fcl>\d+))?"
)
TS = re.compile(r"^\[(\d\d):(\d\d):(\d\d)\.\d+\]")


def _secs(line: str):
    m = TS.match(line)
    if not m:
        return None
    h, mn, s = (int(x) for x in m.groups())
    return h * 3600 + mn * 60 + s


def pid_log_groups():
    """Map pid -> [its log files sorted oldest-first], covering rotations
    (RynthCore.<pid>.log plus .log.old / .log.N). The engine rotates at ~5 MB:
    right after a rotation the live .log is a tiny fragment and the soak's
    history is in .log.old, so scoring only .log would miss the whole run.
    Each pid is analyzed over its concatenated files in chronological order."""
    groups = {}
    for f in glob.glob(os.path.join(LOG_DIR, "RynthCore.*.log*")):
        m = re.search(r"RynthCore\.(\d+)\.log", os.path.basename(f))
        if not m:
            continue
        groups.setdefault(m.group(1), []).append(f)
    for pid in groups:
        groups[pid].sort(key=os.path.getmtime)   # oldest first = chronological
    return groups


def count(path, pat):
    c = 0
    rx = re.compile(re.escape(pat))
    with open(path, encoding="utf-8", errors="replace") as fh:
        for ln in fh:
            if rx.search(ln):
                c += 1
    return c


def analyze(paths):
    if isinstance(paths, str):
        paths = [paths]
    pid = re.search(r"RynthCore\.(\d+)\.log", os.path.basename(paths[0])).group(1)
    last_hb = None
    hb_count = 0
    fps0_inworld = 0          # wedge signature: fps=0 with login=1
    max_hb_gap = 0.0
    prev_t = None
    kills = casts_landed = loot_done = force_clears = 0
    combat_casts = 0
    buffstance = coma = stance_stuck = 0
    first_t = last_t = None

    for path in paths:
      with open(path, encoding="utf-8", errors="replace") as fh:
        for ln in fh:
            t = _secs(ln)
            if t is not None:
                if first_t is None:
                    first_t = t
                last_t = t
            m = HB.search(ln)
            if m:
                last_hb = m.groupdict()
                hb_count += 1
                if m["fps"] == "0" and m["login"] == "1":
                    fps0_inworld += 1
                if t is not None and prev_t is not None:
                    gap = t - prev_t
                    if gap < 0:
                        gap += 86400  # midnight wrap
                    max_hb_gap = max(max_hb_gap, gap)
                if t is not None:
                    prev_t = t
            # productivity / health signals
            if "KillerNotification" in ln:
                kills += 1
            if "[CombatCast]" in ln:
                combat_casts += 1
            if "You cast" in ln:            # buffs/life only; war magic emits none
                casts_landed += 1
            if "Corpse loot: completed" in ln:
                loot_done += 1
            if "force-clearing" in ln:
                force_clears += 1
            if "BuffStance" in ln:
                buffstance += 1
            if "BUFFING COMA" in ln:
                coma += 1
            if "STANCE STUCK" in ln:
                stance_stuck += 1

    up_h = (last_t - first_t) / 3600.0 if (first_t is not None and last_t is not None and last_t >= first_t) else 0.0
    return dict(pid=pid, path=path, last_hb=last_hb, hb_count=hb_count,
                fps0_inworld=fps0_inworld, max_hb_gap=max_hb_gap, up_h=up_h,
                kills=kills, combat_casts=combat_casts, casts_landed=casts_landed,
                loot_done=loot_done, force_clears=force_clears, buffstance=buffstance,
                coma=coma, stance_stuck=stance_stuck)


def crash_blocks_for_pid(pid):
    """Attribute native-crash.log blocks to a specific client by the embedded
    `RynthCore.Engine.p<PID>.genN.dll` stack frame each block carries. The
    crash log is SHARED across all multi-boxed clients, so file mtime can't
    pin a crash to one PID — but the engine module frame names the PID exactly.
    Returns (mine, unattributed): `mine` = block-header lines whose engine
    frame matches `pid` (a real crash in THIS client, even if first-chance and
    survived); `unattributed` = blocks with no engine frame (pure acclient/
    system stack) which can't be tied to a PID from a shared log."""
    if not os.path.exists(CRASH_LOG):
        return [], []
    txt = open(CRASH_LOG, encoding="utf-8", errors="replace").read()
    mine, unattr = [], []
    for raw in txt.split("NATIVE CRASH LOGGER")[1:]:
        header = next((l.strip() for l in raw.splitlines() if "exAddr=" in l), None)
        if header is None:
            continue
        m = re.search(r"RynthCore\.Engine\.p(\d+)\.", raw)
        if m:
            if m.group(1) == str(pid):
                mine.append(header)
        else:
            unattr.append(header)
    return mine[-5:], unattr[-3:]


def since_epoch(hhmm):
    """Parse an HH:MM soak-window start (today; yesterday if it's still future)."""
    hh, mm = (int(x) for x in hhmm.split(":"))
    now = datetime.now()
    ws = now.replace(hour=hh, minute=mm, second=0, microsecond=0)
    return (ws.timestamp() - 86400) if ws > now else ws.timestamp()


def session_start_epoch(path):
    """Current-soak start = first heartbeat time of the newest (active) log,
    dated by that log's mtime. Logs/crashes before this belong to a PREVIOUS
    soak. Returns None if unparseable (caller falls back to a fixed max-age)."""
    try:
        mt = os.path.getmtime(path)
        mdt = datetime.fromtimestamp(mt)
        first = None
        with open(path, encoding="utf-8", errors="replace") as fh:
            for ln in fh:
                s = _secs(ln)
                if s is not None:
                    first = s
                    break
        if first is None:
            return None
        start = mdt.replace(hour=first // 3600, minute=(first % 3600) // 60,
                            second=first % 60, microsecond=0).timestamp()
        return start - 86400 if start > mt else start
    except Exception:
        return None


def verdict(a):
    fails, warns = [], []
    # LIVENESS
    if a["last_hb"] is None:
        fails.append("no heartbeat lines (engine never ran / log empty)")
    else:
        if a["fps0_inworld"] > 5:
            fails.append(f"WEDGE: {a['fps0_inworld']} heartbeats at fps=0 while login=1")
        if a["max_hb_gap"] > 15:
            warns.append(f"heartbeat gap up to {a['max_hb_gap']:.0f}s (stall or rotation)")
    # STABILITY
    if a["coma"]:
        fails.append(f"{a['coma']} BUFFING COMA event(s)")
    if a["stance_stuck"]:
        warns.append(f"{a['stance_stuck']} STANCE STUCK recovery(ies)")
    fcl_per_h = a["force_clears"] / a["up_h"] if a["up_h"] > 0.1 else a["force_clears"]
    if fcl_per_h > 60:
        warns.append(f"{fcl_per_h:.0f} force-clears/hr (busy still leaking - should be near-flat)")
    # PRODUCTIVITY - the axis that today's 12h soak would have FAILED.
    # KILLS (KillerNotification) are the reliable truth signal. "You cast" is
    # NOT logged for war-magic combat (and only logged for buffs during a
    # buff-pending window), so it is informational only - never a FAIL.
    # The real "casting but not landing" regression (2026-06-13) shows as
    # many CombatCast ATTEMPTS with ZERO kills.
    if a["up_h"] >= 0.25:  # only judge productivity once it's run >15 min
        kph = a["kills"] / a["up_h"]
        if a["kills"] == 0:
            if a["combat_casts"] >= 15:
                fails.append(f"{a['combat_casts']} cast attempts but ZERO kills - casts not landing "
                             f"(the 2026-06-13 swap-aborts-cast regression)")
            elif a["loot_done"] > 0:
                warns.append("kills=0 but looting - passive/escort mode? verify it should be fighting")
            else:
                warns.append("kills=0 and almost no combat - idle, no spawns, or not engaging (check the bot)")
        elif kph < 10:
            warns.append(f"only {kph:.1f} kills/hr (low for a grind)")
    return fails, warns


def report(a):
    f, w = verdict(a)
    status = "FAIL" if f else ("WARN" if w else "PASS")
    hb = a["last_hb"] or {}
    print(f"\n=== pid {a['pid']} - {status} ===")
    print(f"  uptime ~{a['up_h']:.1f}h | {a['hb_count']} heartbeats | last fps={hb.get('fps','?')} "
          f"plug={hb.get('plug','?')}/s qd={hb.get('qd','?')} rec={hb.get('rec','?')} fcl={hb.get('fcl','?')}")
    print(f"  PRODUCTIVITY: {a['kills']} kills | {a['combat_casts']} cast attempts | {a['loot_done']} corpses looted "
          f"| {a['casts_landed']} 'You cast' (buff-window log only; not a combat signal)")
    print(f"  STABILITY:    {a['force_clears']} force-clears | {a['stance_stuck']} stance-stuck | {a['coma']} coma "
          f"| max hb gap {a['max_hb_gap']:.0f}s | {a['fps0_inworld']} fps0-inworld")
    for x in f:
        print(f"  [FAIL] {x}")
    for x in w:
        print(f"  [warn] {x}")
    return status


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pid")
    ap.add_argument("--all", action="store_true")
    ap.add_argument("--since", help="soak-window start as HH:MM (today). Logs/crashes before this are a previous soak")
    ap.add_argument("--max-age-h", type=float, help="window = last N hours (overrides the auto current-soak window)")
    ap.add_argument("--watch", type=int, default=0, help="re-verdict every N seconds")
    args = ap.parse_args()

    def run_once():
        groups = pid_log_groups()                        # pid -> [files oldest-first]
        if args.pid:
            files = groups.get(args.pid) or [os.path.join(LOG_DIR, f"RynthCore.{args.pid}.log")]
            groups = {args.pid: [f for f in files if os.path.exists(f)]}
        if not groups or all(not v for v in groups.values()):
            print("no client logs found in", LOG_DIR)
            return 2
        last_seen = {pid: max(os.path.getmtime(f) for f in fs) for pid, fs in groups.items() if fs}
        anchor = max(last_seen, key=last_seen.get)        # most recently active client

        # --- soak window start ---
        if args.since:
            ws = since_epoch(args.since)
        elif args.max_age_h:
            ws = time.time() - args.max_age_h * 3600.0
        else:                                             # auto: start of the active soak
            ws = session_start_epoch(groups[anchor][0]) or (time.time() - 12 * 3600.0)
        print(f"soak window: since {datetime.fromtimestamp(ws):%Y-%m-%d %H:%M}"
              f"  (older logs/crashes = previous soak, excluded)")

        # --- which clients fall in the window ---
        in_window = sorted([p for p in last_seen if last_seen[p] >= ws], key=lambda p: last_seen[p], reverse=True)
        stale = sorted([p for p in last_seen if last_seen[p] < ws], key=lambda p: last_seen[p], reverse=True)
        if args.pid:
            chosen = [args.pid]
        elif in_window:
            chosen = in_window if args.all else in_window[:1]
        else:                                             # nothing fresh: show newest, flagged
            age_h = (time.time() - last_seen[anchor]) / 3600.0
            print(f"  ! no logs within the window; newest (pid {anchor}) is {age_h:.1f}h old - PREVIOUS soak.")
            chosen, stale = [anchor], [p for p in stale if p != anchor]
        if stale:
            print(f"  excluded {len(stale)} stale client(s) from prior soaks: "
                  + ", ".join(stale[:12]) + (" ..." if len(stale) > 12 else ""))
        if not args.all and not args.pid and len(in_window) > 1:
            print(f"  ({len(in_window) - 1} other in-window client(s) not shown; use --all)")

        # --- crash-log freshness: file mtime is the only time signal it has ---
        crash_fresh = os.path.exists(CRASH_LOG) and os.path.getmtime(CRASH_LOG) >= ws
        if os.path.exists(CRASH_LOG):
            cm = f"{datetime.fromtimestamp(os.path.getmtime(CRASH_LOG)):%Y-%m-%d %H:%M}"
            print(f"  native-crash.log last written {cm} "
                  + ("(IN window - blocks below are this soak)" if crash_fresh
                     else "(BEFORE window - 0 crashes this soak)"))

        worst = "PASS"
        for pid in chosen:
            fs = groups[pid]
            a = analyze(fs)
            if len(fs) > 1:
                print(f"\n(aggregating {len(fs)} rotated files for pid {pid})", end="")
            st = report(a)
            if crash_fresh:
                mine, _unattr = crash_blocks_for_pid(a["pid"])
                if mine:
                    print(f"  [FAIL] {len(mine)} crash block(s) attributed to THIS client (pid {a['pid']}) "
                          f"in-window - corruption:")
                    for c in mine:
                        print(f"         {c}")
                    st = "FAIL"
            if st == "FAIL" or (st == "WARN" and worst == "PASS"):
                worst = st
        print(f"\nVERDICT: {worst}")
        return 0 if worst != "FAIL" else 1

    if args.watch:
        while True:
            print("\n" + "=" * 60 + f"  {datetime.now():%H:%M:%S}")
            run_once()
            time.sleep(max(15, args.watch))
    else:
        sys.exit(run_once())


if __name__ == "__main__":
    main()

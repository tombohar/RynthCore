"""WatchPatch - timeline of who owns the first bytes of some functions in a running process.

Polls the addresses every few ms and prints a line whenever the bytes change, with the
owner of the jump (see ProcPatchDiff.owner_of). Read-only. Spike tool (Decal bridge).

    python tools/WatchPatch.py <pid> <seconds> 0x005649F0 0x004CBF70 ...
"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
import ProcPatchDiff as P  # noqa: E402


def main():
    pid, secs = int(sys.argv[1]), float(sys.argv[2])
    addrs = [int(a, 16) for a in sys.argv[3:]]
    p = P.Proc(pid)
    last = {}
    t0 = time.time()
    refresh = 0.0
    while time.time() - t0 < secs:
        now = time.time()
        if now - refresh > 1.0:          # modules come and go (Decal loads late)
            try:
                p.mods = p._modules()
            except OSError:
                break
            refresh = now
        for a in addrs:
            b = p.read(a, 6)
            if b is None:
                continue
            if last.get(a) != b:
                j = P.decode_jump(p, a)
                own = P.owner_of(p, j[0])[0] if j else "original bytes" if a not in last else "?"
                stamp = time.strftime("%H:%M:%S", time.localtime(now)) + f".{int(now * 1000) % 1000:03d}"
                print(f"{stamp} 0x{a:08X} {b.hex()} -> {own}", flush=True)
                last[a] = b
        time.sleep(0.005)


if __name__ == "__main__":
    main()

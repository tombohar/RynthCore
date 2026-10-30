"""
clr_fatal_triage.py - first look at an x86 acclient crash dump (no debugger needed).

Written for the CoreCLR fatal errors of 2026-09-29..10-12 (.NET Runtime event 1023,
"internal error in the .NET Runtime at IP coreclr.dll+0x256E19", exit 0x80131506),
where dotnet-dump only showed an ExecutionEngineException with no stack.

For every thread it finds an exception being dispatched on (the EXCEPTION_RECORD /
CONTEXT pair KiUserExceptionDispatcher leaves on the stack), it prints the exception,
the faulting registers, and the return addresses on that thread's stack as
module+offset. coreclr.dll offsets are named when you pass its public PDB (--pdb,
fetched once from https://msdl.microsoft.com/download/symbols/coreclr.pdb/<GUID><age>/coreclr.pdb;
the GUID/age are printed below). Known fatal sites are named even without it.

    python -I tools\\ClrFatalTriage\\clr_fatal_triage.py <dump.dmp> [--pdb coreclr.pdb] [--all-threads]
               [--image Name.dll=C:\\path\\to\\matching\\Name.dll]

Needs Python 3 and pefile (only to read code bytes from the module files when the dump
has none, which is what tells real return addresses from stale stack values). Read-only.
JIT'd code has no module; those addresses print as raw hex (SOS `ip2md` resolves them
when the dump holds the code heap: `dotnet-dump analyze` under the x86 dotnet host).
"""
import argparse
import bisect
import mmap
import os
import pickle
import struct
import sys

# Fatal sites by coreclr.dll file version -> offset -> meaning.
KNOWN_SITES = {
    "10.0.1226.42308": {
        0x256E19: "ReversePInvokeBadTransition: a reverse P/Invoke (a managed hook/callback) was entered "
                  "on a thread already in cooperative mode, i.e. native code running inside managed "
                  "code's context (an exception handler / VEH on a managed fault) called a function "
                  "with a managed detour. In the listing, the first module frame after "
                  "JIT_ReversePInvokeEnter* (next higher address, past the stub's raw frame) is the "
                  "native code that called the hooked function.",
    },
}


class Dump:
    def __init__(self, path, images):
        f = open(path, "rb")
        self.m = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        sig, _ver, nstreams, dirrva = struct.unpack_from("<IIII", self.m, 0)
        if sig != 0x504D444D:
            raise SystemExit("not a minidump")
        self.streams = {}
        for i in range(nstreams):
            st, size, rva = struct.unpack_from("<III", self.m, dirrva + 12 * i)
            self.streams.setdefault(st, []).append((size, rva))
        self.images = images
        self.imgcache = {}
        self.mods, self.mem, self.threads = [], [], []
        self._parse()

    def _str(self, rva):
        n = struct.unpack_from("<I", self.m, rva)[0]
        return self.m[rva + 4:rva + 4 + n].decode("utf-16le", "replace")

    def _parse(self):
        m = self.m
        for _size, rva in self.streams.get(4, []):  # ModuleListStream
            for i in range(struct.unpack_from("<I", m, rva)[0]):
                o = rva + 4 + 108 * i
                base, sz, _cs, ts, namerva = struct.unpack_from("<QIIII", m, o)
                ms, ls = struct.unpack_from("<II", m, o + 32)
                ver = "%d.%d.%d.%d" % (ms >> 16, ms & 0xFFFF, ls >> 16, ls & 0xFFFF)
                self.mods.append((base, sz, self._str(namerva), ts, ver))
        self.mods.sort()
        for _size, rva in self.streams.get(5, []):  # MemoryListStream
            for i in range(struct.unpack_from("<I", m, rva)[0]):
                start, dsz, drva = struct.unpack_from("<QII", m, rva + 4 + 16 * i)
                self.mem.append((start, dsz, drva))
        for _size, rva in self.streams.get(9, []):  # Memory64ListStream
            n, off = struct.unpack_from("<QQ", m, rva)
            for i in range(n):
                start, dsz = struct.unpack_from("<QQ", m, rva + 16 + 16 * i)
                self.mem.append((start, dsz, off))
                off += dsz
        self.mem.sort()
        self.memstarts = [x[0] for x in self.mem]
        for _size, rva in self.streams.get(3, []):  # ThreadListStream
            for i in range(struct.unpack_from("<I", m, rva)[0]):
                (tid, _s, _pc, _p, _teb, sstart, ssize, _srva, _csz,
                 crva) = struct.unpack_from("<IIIIQQIIII", m, rva + 4 + 48 * i)
                sstart &= 0xFFFFFFFF  # WOW64 dumps can sign-extend 32-bit stack addresses
                self.threads.append((tid, sstart, sstart + ssize, crva))

    def read(self, addr, size):
        i = bisect.bisect_right(self.memstarts, addr) - 1
        if i < 0:
            return None
        start, dsz, off = self.mem[i]
        if addr + size > start + dsz:
            return None
        return self.m[off + addr - start:off + addr - start + size]

    def u32(self, addr):
        b = self.read(addr, 4)
        return None if b is None else struct.unpack("<I", b)[0]

    def module(self, addr):
        for mod in self.mods:
            if mod[0] <= addr < mod[0] + mod[1]:
                return mod
        return None

    def code(self, addr, n):
        b = self.read(addr, n)
        if b is not None:
            return b
        mod = self.module(addr)
        if mod is None:
            return None
        name = mod[2]
        if name not in self.imgcache:
            leaf = name.split("\\")[-1].lower()
            path = self.images.get(leaf, name)
            if path.lower().startswith("c:\\windows\\system32\\"):
                path = "C:\\Windows\\SysWOW64\\" + path[20:]
            try:
                import pefile
                pe = pefile.PE(path, fast_load=True)
                # An --image the user named is trusted; otherwise the file must match the dump's
                # timestamp. (The dump's module list can show the file that was on disk at dump
                # time when a loaded DLL was replaced by rename-aside: on 10-12 a v4 trampoline
                # was listed while the client still ran the v1 it started with.)
                trusted = leaf in self.images or pe.FILE_HEADER.TimeDateStamp == mod[3]
                self.imgcache[name] = pe.get_memory_mapped_image() if trusted else None
            except Exception:
                self.imgcache[name] = None
        img = self.imgcache[name]
        o = addr - mod[0]
        return None if img is None else img[o:o + n]

    def after_call(self, ra):
        b = self.code(ra - 7, 7)
        if b is None or len(b) < 7:
            return None  # unknown: no code bytes
        return (b[2] == 0xE8 or (b[5] == 0xFF and (b[6] >> 3) & 7 == 2) or
                (b[4] == 0xFF and (b[5] >> 3) & 7 == 2) or (b[1] == 0xFF and (b[2] >> 3) & 7 == 2) or
                (b[0] == 0xFF and (b[1] >> 3) & 7 == 2))


def load_pdb_publics(path):
    """S_PUB32 records of an MSF 7.0 PDB as a sorted [(rva, name)]; cached next to the PDB."""
    cache = path + ".publics.pkl"
    if os.path.exists(cache):
        return pickle.load(open(cache, "rb"))
    data = open(path, "rb").read()
    bs, _fbm, _nb, ndir, _u, bmaddr = struct.unpack_from("<IIIIII", data, 32)
    dirblocks = struct.unpack_from("<%dI" % ((ndir + bs - 1) // bs), data, bmaddr * bs)
    d = b"".join(data[b * bs:(b + 1) * bs] for b in dirblocks)[:ndir]
    nstreams = struct.unpack_from("<I", d, 0)[0]
    sizes = struct.unpack_from("<%dI" % nstreams, d, 4)
    off, streams = 4 + 4 * nstreams, []
    for s in sizes:
        s = 0 if s == 0xFFFFFFFF else s
        nb = (s + bs - 1) // bs
        streams.append((s, struct.unpack_from("<%dI" % nb, d, off)))
        off += 4 * nb

    def stream(i):
        s, blks = streams[i]
        return b"".join(data[b * bs:(b + 1) * bs] for b in blks)[:s]

    dbi = stream(3)
    f = struct.unpack_from("<iIIHHHHHHiiiiiIiiHHI", dbi, 0)
    symrec, sub = f[7], f[9:14] + (f[16],)
    optdbg = f[15]
    o = 64 + sum(sub)
    sechdr = stream(struct.unpack_from("<%dH" % (optdbg // 2), dbi, o)[5])
    secs = [struct.unpack_from("<I", sechdr, i * 40 + 12)[0] for i in range(len(sechdr) // 40)]
    sr, p, out = stream(symrec), 0, []
    while p + 4 <= len(sr):
        reclen, kind = struct.unpack_from("<HH", sr, p)
        if kind == 0x110E:  # S_PUB32
            _fl, offs, seg = struct.unpack_from("<IIH", sr, p + 4)
            if 1 <= seg <= len(secs):
                out.append((secs[seg - 1] + offs, sr[p + 14:sr.index(b"\0", p + 14)].decode("latin1")))
        p += reclen + 2
    out.sort()
    pickle.dump(out, open(cache, "wb"))
    return out


def pdb_identity(path):
    try:
        import pefile
        pe = pefile.PE(path, fast_load=True)
        pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_DEBUG"]])
        for e in getattr(pe, "DIRECTORY_ENTRY_DEBUG", []):
            if hasattr(e.entry, "Signature_String"):
                return "%s%X" % (e.entry.Signature_String, e.entry.Age)
    except Exception:
        pass
    return None


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("dump")
    ap.add_argument("--pdb", help="coreclr.pdb matching the dump's coreclr.dll")
    ap.add_argument("--all-threads", action="store_true", help="scan every thread, not just ones dispatching an exception")
    ap.add_argument("--image", action="append", default=[], help="Name.dll=path: module file to read code bytes from")
    a = ap.parse_args()
    images = {k.lower(): v for k, v in (x.split("=", 1) for x in a.image)}
    d = Dump(a.dump, images)

    clr = next((mod for mod in d.mods if mod[2].lower().endswith("\\coreclr.dll")), None)
    pubs = load_pdb_publics(a.pdb) if a.pdb else None
    known = KNOWN_SITES.get(clr[4], {}) if clr else {}
    if clr:
        print("coreclr.dll %s at 0x%08X (%s); PDB id %s" % (clr[4], clr[0], clr[2], pdb_identity(clr[2]) or "?"))

    seen_known = set()

    def sym(v):
        mod = d.module(v)
        if mod is None:
            return None
        leaf, off = mod[2].split("\\")[-1], v - mod[0]
        s = "%s+0x%X" % (leaf, off)
        if mod is clr:
            if pubs:
                i = bisect.bisect_right(pubs, (off, "\uffff")) - 1
                if i >= 0:
                    s += "  %s+0x%X" % (pubs[i][1], off - pubs[i][0])
            if off in known:
                s += "  <== KNOWN FATAL SITE"
                seen_known.add(off)
        return s

    for tid, lo, hi, crva in d.threads:
        esp = struct.unpack_from("<I", d.m, crva + 0xC4)[0]
        records = []
        a_ = esp
        while a_ + 8 <= hi:
            per, pctx = d.u32(a_), d.u32(a_ + 4)
            if per is None:
                break
            if per and pctx and lo <= per < hi and pctx == per + 0x50 and all(r[1] != per for r in records):
                code = d.u32(per)
                if code is not None and code & 0xC0000000 == 0xC0000000:
                    records.append((a_, per, pctx))
            a_ += 4
        if not records and not a.all_threads:
            continue
        print("\n=== thread 0x%X  stack 0x%08X..0x%08X" % (tid, lo, hi))
        for at, per, pctx in records:
            code, flags, _rec, addr, n = struct.unpack("<IIIII", d.read(per, 20))
            info = struct.unpack("<%dI" % min(n, 15), d.read(per + 20, 4 * min(n, 15))) if n else ()
            regs = dict(zip(["edi", "esi", "ebx", "edx", "ecx", "eax", "ebp", "eip", "cs", "efl", "esp"],
                            struct.unpack("<11I", d.read(pctx + 0x9C, 44))))
            print("  exception at stack 0x%08X: code=0x%08X flags=%X address=0x%08X %s info=%s" % (
                at, code, flags, addr, sym(addr) or "(no module: JIT'd code or a stub)",
                " ".join("0x%X" % x for x in info)))
            if code == 0xC0000005 and len(info) >= 2:
                print("    %s of 0x%08X%s" % ({0: "read", 1: "write", 8: "execute"}.get(info[0], "?"), info[1],
                                               "  (null page: a NullReferenceException if this is managed code)" if info[1] < 0x10000 else ""))
            print("    " + " ".join("%s=%08X" % (k, regs[k]) for k in ["eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp", "eip"]))
        print("  return addresses (esp -> stack base):")
        a_ = esp
        while a_ + 4 <= hi:
            v = d.u32(a_)
            if v is None:
                print("    <stack unreadable at 0x%08X>" % a_)
                break
            s = sym(v)
            if s:
                call = d.after_call(v)
                if call or call is None:
                    print("    0x%08X: 0x%08X %s%s" % (a_, v, s, "" if call else "  (no code bytes to check)"))
            a_ += 4

    for off in sorted(seen_known):
        print("\nKNOWN FATAL SITE coreclr.dll+0x%X: %s" % (off, known[off]))
    if seen_known:
        print("The 2026-10-12 cause: RynthCore.SehTrampoline.dll's VEH (v1-v4) called CreateFileW while a managed\n"
              "NullReferenceException was being dispatched; DatFileShareHooks detours CreateFileW in managed code.")


if __name__ == "__main__":
    main()

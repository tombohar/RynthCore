"""ProcPatchDiff - which code in a running 32-bit process was patched, and by whom.

Read-only. For each module it compares the executable sections in memory with the
on-disk image (relocated to the module's actual base) and lists every changed byte run.
A run that starts with a jump (E9 rel32, EB rel8 -> E9, FF25 [abs], 68 imm32 C3) is
followed - through simple jump stubs only - to the module that owns the destination,
which names the patcher. It also checks acclient.exe's import table (IAT hooks) and the
game window's WndProc. It never disassembles anything beyond reading one jump at a time.

Spike tool for the Decal-bridge analysis (docs: spikes/decal-bridge). Point it at TEST
clients only.

    python tools/ProcPatchDiff.py <pid> [--all] [--names <RynthCore.<pid>.log>] [--json out.json]

--all      diff every module with a file on disk (default: acclient.exe + the system DLLs
           AC and its hookers use)
--names    annotate acclient.exe addresses with HookResolver names from an engine log
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import re
import struct
import sys

import pefile

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
LIST_MODULES_32BIT = 0x01
IMAGE_SCN_MEM_EXECUTE = 0x20000000

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)
user32 = ctypes.WinDLL("user32", use_last_error=True)

k32.OpenProcess.restype = wt.HANDLE
k32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
k32.VirtualQueryEx.restype = ctypes.c_size_t
k32.VirtualQueryEx.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t]
psapi.EnumProcessModulesEx.argtypes = [wt.HANDLE, ctypes.POINTER(ctypes.c_void_p), wt.DWORD, ctypes.POINTER(wt.DWORD), wt.DWORD]
psapi.GetModuleFileNameExW.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.LPWSTR, wt.DWORD]


class MODULEINFO(ctypes.Structure):
    _fields_ = [("lpBaseOfDll", ctypes.c_void_p), ("SizeOfImage", wt.DWORD), ("EntryPoint", ctypes.c_void_p)]


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_ulonglong), ("AllocationBase", ctypes.c_ulonglong),
                ("AllocationProtect", wt.DWORD), ("__a1", wt.DWORD), ("RegionSize", ctypes.c_ulonglong),
                ("State", wt.DWORD), ("Protect", wt.DWORD), ("Type", wt.DWORD), ("__a2", wt.DWORD)]


psapi.GetModuleInformation.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.POINTER(MODULEINFO), wt.DWORD]

DEFAULT_MODULES = {
    "acclient.exe", "kernel32.dll", "kernelbase.dll", "ntdll.dll", "user32.dll", "win32u.dll",
    "gdi32.dll", "d3d9.dll", "dinput8.dll", "dinput.dll", "ws2_32.dll", "wsock32.dll",
    "mswsock.dll", "winmm.dll", "ole32.dll", "combase.dll", "imm32.dll", "dsound.dll",
}


class Proc:
    def __init__(self, pid):
        self.pid = pid
        self.h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
        if not self.h:
            raise OSError(f"OpenProcess({pid}) failed: {ctypes.get_last_error()}")
        self.mods = self._modules()

    def read(self, addr, size):
        buf = ctypes.create_string_buffer(size)
        got = ctypes.c_size_t(0)
        if not k32.ReadProcessMemory(self.h, ctypes.c_void_p(addr), buf, size, ctypes.byref(got)):
            return None
        return buf.raw[: got.value]

    def dword(self, addr):
        b = self.read(addr, 4)
        return struct.unpack("<I", b)[0] if b and len(b) == 4 else None

    def _modules(self):
        arr = (ctypes.c_void_p * 1024)()
        needed = wt.DWORD(0)
        if not psapi.EnumProcessModulesEx(self.h, arr, ctypes.sizeof(arr), ctypes.byref(needed), LIST_MODULES_32BIT):
            raise OSError(f"EnumProcessModulesEx failed: {ctypes.get_last_error()}")
        mods = []
        for i in range(needed.value // ctypes.sizeof(ctypes.c_void_p)):
            hm = arr[i]
            name = ctypes.create_unicode_buffer(520)
            psapi.GetModuleFileNameExW(self.h, hm, name, 520)
            mi = MODULEINFO()
            psapi.GetModuleInformation(self.h, hm, ctypes.byref(mi), ctypes.sizeof(mi))
            path = name.value
            # A 64-bit reader is told System32 for a WOW64 process's DLLs; the 32-bit
            # files it actually loaded live in SysWOW64.
            sys32 = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32") + "\\"
            if path.lower().startswith(sys32.lower()) and (mi.lpBaseOfDll or 0) < 0x100000000:
                wow = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "SysWOW64", path[len(sys32):])
                if os.path.exists(wow):
                    path = wow
            mods.append({"base": mi.lpBaseOfDll or 0, "size": mi.SizeOfImage, "path": path,
                         "name": os.path.basename(path)})
        return sorted(mods, key=lambda m: m["base"])

    def module_at(self, addr):
        for m in self.mods:
            if m["base"] <= addr < m["base"] + m["size"]:
                return m
        return None

    def alloc_base(self, addr):
        mbi = MBI()
        if k32.VirtualQueryEx(self.h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)) == 0:
            return None
        return mbi.AllocationBase


def decode_jump(p, addr):
    """Destination of a simple jump at addr, or None. Reads only the jump itself."""
    b = p.read(addr, 8)
    if not b or len(b) < 6:
        return None
    if b[0] == 0xE9:
        return (addr + 5 + struct.unpack("<i", b[1:5])[0]) & 0xFFFFFFFF, "jmp rel32"
    if b[0] == 0xEB:
        return (addr + 2 + struct.unpack("<b", b[1:2])[0]) & 0xFFFFFFFF, "jmp rel8"
    if b[0] == 0xFF and b[1] == 0x25:
        slot = struct.unpack("<I", b[2:6])[0]
        dst = p.dword(slot)
        return (dst, f"jmp [0x{slot:08X}]") if dst is not None else None
    if b[0] == 0x68 and b[5] == 0xC3:
        return struct.unpack("<I", b[1:5])[0], "push/ret"
    return None


def owner_of(p, addr, hops=5):
    """Follow simple jump stubs from addr; return (owner description, chain)."""
    chain = []
    cur = addr
    for _ in range(hops):
        m = p.module_at(cur)
        if m is not None:
            chain.append(f"{m['name']}+0x{cur - m['base']:X}")
            return m["name"], chain
        j = decode_jump(p, cur)
        ab = p.alloc_base(cur)
        chain.append(f"private@0x{cur:08X}(alloc 0x{(ab or 0):08X})")
        head = p.read(cur, 10) or b""
        if len(head) == 10 and head[0] == 0xB8 and head[5] == 0xE9:
            # mov eax, <entry>; jmp <stub>: the .NET Framework reverse-P/Invoke thunk shape,
            # i.e. a delegate from managed code (a .NET extension) is the hook.
            return "managed thunk (.NET delegate)", chain
        if j is None:
            return f"private(alloc 0x{(ab or 0):08X})", chain
        dst, kind = j
        if kind.startswith("jmp [") :
            slot = int(re.search(r"0x([0-9A-Fa-f]+)", kind).group(1), 16)
            sm = p.module_at(slot)
            if sm is not None:
                # A stub whose slot lives in a module (e.g. the RynthCore loader's facade).
                chain.append(f"slot in {sm['name']}")
                dm = p.module_at(dst)
                return (f"{sm['name']} stub -> {dm['name'] if dm else 'private'}"), chain
        cur = dst
    return "unresolved", chain


def export_names(pe, base):
    names = {}
    if hasattr(pe, "DIRECTORY_ENTRY_EXPORT"):
        for e in pe.DIRECTORY_ENTRY_EXPORT.symbols:
            if e.name:
                names[base + e.address] = e.name.decode(errors="replace")
    return names


def nearest_name(addr, names):
    best = None
    for a, n in names.items():
        if a <= addr and (best is None or a > best[0]):
            best = (a, n)
    if best is None or addr - best[0] > 0x40:
        return None
    return best[1] if best[0] == addr else f"{best[1]}+0x{addr - best[0]:X}"


def diff_module(p, m, names_extra=None):
    try:
        pe = pefile.PE(m["path"], fast_load=True)
    except Exception as ex:
        return {"module": m["name"], "error": str(ex)}
    pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_BASERELOC"],
                                           pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXPORT"],
                                           pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"]])
    # System DLLs keep their IAT inside .text: name the slots so an IAT hook reads as one.
    iat_names = {}
    for ent in getattr(pe, "DIRECTORY_ENTRY_IMPORT", []):
        for imp in ent.imports:
            nm = (imp.name or b"#" + str(imp.ordinal).encode()).decode(errors="replace")
            iat_names[imp.address - pe.OPTIONAL_HEADER.ImageBase + m["base"]] = f"{ent.dll.decode()}!{nm}"
    if pe.OPTIONAL_HEADER.ImageBase != m["base"]:
        pe.relocate_image(m["base"])
    names = export_names(pe, m["base"])
    if names_extra:
        names.update(names_extra)
    patches = []
    for s in pe.sections:
        if not (s.Characteristics & IMAGE_SCN_MEM_EXECUTE):
            continue
        size = min(s.Misc_VirtualSize or s.SizeOfRawData, s.SizeOfRawData)
        disk = pe.get_data(s.VirtualAddress, size)
        mem = p.read(m["base"] + s.VirtualAddress, size)
        if mem is None or len(mem) != len(disk):
            patches.append({"section": s.Name.rstrip(b"\0").decode(), "error": "unreadable"})
            continue
        runs = []
        CH = 4096
        for off in range(0, size, CH):
            if disk[off:off + CH] == mem[off:off + CH]:
                continue
            for i in range(off, min(off + CH, size)):
                if disk[i] != mem[i]:
                    if runs and i - runs[-1][1] <= 4:
                        runs[-1][1] = i + 1
                    else:
                        runs.append([i, i + 1])
        for a, b in runs:
            va = m["base"] + s.VirtualAddress + a
            # A MinHook-style patch can keep its first byte (rare); start the decode a byte early
            # when the run doesn't begin with a known jump opcode.
            start = va
            j = decode_jump(p, start)
            if j is None and a > 0:
                j2 = decode_jump(p, va - 1)
                if j2 is not None:
                    start, j = va - 1, j2
            entry = {
                "va": f"0x{start:08X}", "rva": f"0x{start - m['base']:X}", "len": b - a,
                "name": nearest_name(start, names),
                "disk": disk[a:b][:16].hex(), "mem": mem[a:b][:16].hex(),
            }
            if j is not None:
                dst, kind = j
                own, chain = owner_of(p, dst)
                entry.update({"kind": kind, "dest": f"0x{dst:08X}", "owner": own, "chain": chain})
            elif b - a == 4 and a > 0 and mem[a - 1] in (0xE8, 0xE9) and disk[a - 1] == mem[a - 1]:
                # The rel32 of an existing call/jmp was re-pointed: a call-site hook.
                site = va - 1
                dst = (va + 4 + struct.unpack("<i", mem[a:b])[0]) & 0xFFFFFFFF
                orig = (va + 4 + struct.unpack("<i", disk[a:b])[0]) & 0xFFFFFFFF
                own, chain = owner_of(p, dst)
                entry.update({"va": f"0x{site:08X}", "kind": "call-site" if mem[a - 1] == 0xE8 else "jmp-site",
                              "dest": f"0x{dst:08X}", "orig_dest": f"0x{orig:08X}",
                              "orig_name": nearest_name(orig, names), "owner": own, "chain": chain})
            elif b - a == 4 and iat_names and va in iat_names:
                val = struct.unpack("<I", mem[a:b])[0]
                own, chain = owner_of(p, val)
                entry.update({"kind": f"IAT slot {iat_names[va]}", "dest": f"0x{val:08X}", "owner": own, "chain": chain})
            elif b - a == 4:
                val = struct.unpack("<I", mem[a:b])[0]
                own, chain = owner_of(p, val)
                entry.update({"kind": "pointer?", "dest": f"0x{val:08X}", "owner": own, "chain": chain})
            else:
                entry.update({"kind": "data/other", "owner": "?"})
            patches.append(entry)
    return {"module": m["name"], "base": f"0x{m['base']:08X}", "patches": patches}


def check_iat(p, m):
    pe = pefile.PE(m["path"], fast_load=True)
    pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"]])
    delta = m["base"] - pe.OPTIONAL_HEADER.ImageBase
    out = []
    for entry in getattr(pe, "DIRECTORY_ENTRY_IMPORT", []):
        dll = entry.dll.decode().lower()
        for imp in entry.imports:
            slot = imp.address + delta
            val = p.dword(slot)
            if val is None:
                continue
            owner = p.module_at(val)
            oname = owner["name"].lower() if owner else "private"
            system = owner is not None and owner["path"].lower().startswith(r"c:\windows")
            if not system and oname != dll:
                own, chain = owner_of(p, val)
                out.append({"import": f"{dll}!{(imp.name or b'#'+str(imp.ordinal).encode()).decode()}",
                            "slot": f"0x{slot:08X}", "value": f"0x{val:08X}", "owner": own, "chain": chain})
    return out


def check_wndproc(p):
    out = []
    GWL_WNDPROC = -4
    EnumWindowsProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    hwnds = []

    def cb(hwnd, _):
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value == p.pid:
            hwnds.append(hwnd)
        return True

    user32.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.POINTER(wt.DWORD)]
    user32.GetWindowTextW.argtypes = [ctypes.c_void_p, wt.LPWSTR, ctypes.c_int]
    user32.IsWindowVisible.argtypes = [ctypes.c_void_p]
    user32.EnumWindows(EnumWindowsProc(cb), 0)
    user32.GetClassNameW.argtypes = [ctypes.c_void_p, wt.LPWSTR, ctypes.c_int]
    for hw in hwnds:
        cls = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hw, cls, 256)
        title = ctypes.create_unicode_buffer(256)
        user32.GetWindowTextW(hw, title, 256)
        wp = 0  # GWL_WNDPROC is not answered across processes; the engine logs its chain in-process
        own, chain = owner_of(p, wp) if wp else ("?", [])
        out.append({"hwnd": f"0x{hw:X}", "class": cls.value, "title": title.value,
                    "wndproc": f"0x{wp:08X}", "owner": own, "chain": chain,
                    "visible": bool(user32.IsWindowVisible(hw))})
    return out


def names_from_log(path):
    names = {}
    if not path:
        return names
    rx = re.compile(r"HookResolver\[([^\]]+)\]: RESOLVED via [\w-]+ @ 0x([0-9A-Fa-f]{8})")
    rx2 = re.compile(r"(\w+Hooks?): .*?(?:hooked|target=|ready @) ?.*?0x([0-9A-Fa-f]{8})")
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            mm = rx.search(line)
            if mm:
                names.setdefault(int(mm.group(2), 16), mm.group(1))
    return names


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    pid = int(sys.argv[1])
    args = sys.argv[2:]
    all_mods = "--all" in args
    log = args[args.index("--names") + 1] if "--names" in args else None
    js = args[args.index("--json") + 1] if "--json" in args else None
    p = Proc(pid)
    extra = names_from_log(log)
    result = {"pid": pid, "modules": [], "diffs": [], "iat": [], "wndproc": []}
    for m in p.mods:
        result["modules"].append({"name": m["name"], "base": f"0x{m['base']:08X}", "size": m["size"], "path": m["path"]})
    for m in p.mods:
        if not (all_mods or m["name"].lower() in DEFAULT_MODULES):
            continue
        if not os.path.exists(m["path"]):
            continue
        d = diff_module(p, m, extra if m["name"].lower() == "acclient.exe" else None)
        if d.get("patches") or d.get("error"):
            result["diffs"].append(d)
    for m in p.mods:
        if m["name"].lower() == "acclient.exe":
            result["iat"] = check_iat(p, m)
    result["wndproc"] = check_wndproc(p)

    print(f"pid {pid}: {len(p.mods)} modules")
    interesting = [m["name"] for m in p.mods if not m["path"].lower().startswith(r"c:\windows")]
    print("non-system modules: " + ", ".join(interesting))
    for d in result["diffs"]:
        if d.get("error"):
            print(f"== {d['module']}: {d['error']}")
            continue
        print(f"== {d['module']} @ {d['base']}: {len(d['patches'])} patched run(s)")
        for e in d["patches"]:
            if "error" in e:
                print(f"   {e}")
                continue
            od = f" (was -> {e['orig_dest']} {e.get('orig_name') or ''})" if e.get('orig_dest') else ''
            print(f"   {e['va']} {e.get('name') or ''} len={e['len']} {e.get('kind','')} -> {e.get('dest','')}{od} "
                  f"owner={e.get('owner')}  [{' > '.join(e.get('chain', []))}]  disk={e['disk']} mem={e['mem']}")
    print(f"== IAT (acclient.exe) entries pointing outside system DLLs: {len(result['iat'])}")
    for e in result["iat"]:
        print(f"   {e['import']} slot={e['slot']} -> {e['value']} owner={e['owner']}")
    print("== windows")
    for w in result["wndproc"]:
        print(f"   {w['hwnd']} class={w['class']!r} title={w['title']!r} visible={w['visible']} wndproc={w['wndproc']} owner={w['owner']} [{' > '.join(w['chain'])}]")
    if js:
        with open(js, "w", encoding="utf-8") as f:
            json.dump(result, f, indent=1)
    return 0


if __name__ == "__main__":
    sys.exit(main())

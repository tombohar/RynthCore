"""AcWindow.py - look at and click ONE test client's game window, by its process id.

Spike helper (Decal bridge): used once, to drive the retail character-creation screen of a
test client. Everything goes to the HWND of the given pid (PostMessage), so no other window
- and no other client - is touched, and nothing moves the real mouse or keyboard.

    python AcWindow.py shot  <pid> <out.png>
    python AcWindow.py click <pid> <x> <y>          client coordinates of the game window
    python AcWindow.py type  <pid> <text>
    python AcWindow.py key   <pid> <vk>             e.g. 0x0D (Enter), 0x08 (Backspace)
"""
import ctypes
import ctypes.wintypes as wt
import sys
import time

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
user32.PostMessageW.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_void_p, ctypes.c_void_p]
user32.GetClientRect.argtypes = [ctypes.c_void_p, ctypes.POINTER(wt.RECT)]
user32.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.POINTER(wt.DWORD)]
user32.GetClassNameW.argtypes = [ctypes.c_void_p, wt.LPWSTR, ctypes.c_int]
user32.IsWindowVisible.argtypes = [ctypes.c_void_p]
user32.PrintWindow.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint]
user32.GetDC.argtypes = [ctypes.c_void_p]
user32.GetDC.restype = ctypes.c_void_p
user32.ReleaseDC.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
gdi32.CreateCompatibleDC.argtypes = [ctypes.c_void_p]
gdi32.CreateCompatibleDC.restype = ctypes.c_void_p
gdi32.CreateCompatibleBitmap.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
gdi32.CreateCompatibleBitmap.restype = ctypes.c_void_p
gdi32.SelectObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
gdi32.GetDIBits.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint, ctypes.c_uint, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint]
gdi32.DeleteObject.argtypes = [ctypes.c_void_p]
gdi32.DeleteDC.argtypes = [ctypes.c_void_p]

WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_LBUTTONUP = 0x0200, 0x0201, 0x0202
WM_KEYDOWN, WM_KEYUP, WM_CHAR = 0x0100, 0x0101, 0x0102


def game_window(pid):
    found = []
    proto = ctypes.WINFUNCTYPE(wt.BOOL, ctypes.c_void_p, ctypes.c_void_p)

    def cb(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            cls = ctypes.create_unicode_buffer(128)
            user32.GetClassNameW(hwnd, cls, 128)
            if cls.value == "Turbine Device Class":
                found.append(hwnd)
        return True

    user32.EnumWindows(proto(cb), None)
    if not found:
        raise SystemExit(f"no game window for pid {pid}")
    return found[0]


def dialogs(pid):
    """Message boxes (#32770) the process has open, with their static text."""
    proto = ctypes.WINFUNCTYPE(wt.BOOL, ctypes.c_void_p, ctypes.c_void_p)
    user32.GetWindowTextW.argtypes = [ctypes.c_void_p, wt.LPWSTR, ctypes.c_int]
    out = []

    def top(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        cls = ctypes.create_unicode_buffer(64)
        user32.GetClassNameW(hwnd, cls, 64)
        if owner.value == pid and cls.value == "#32770" and user32.IsWindowVisible(hwnd):
            texts = []

            def child(ch, _):
                c = ctypes.create_unicode_buffer(64)
                user32.GetClassNameW(ch, c, 64)
                t = ctypes.create_unicode_buffer(1024)
                user32.GetWindowTextW(ch, t, 1024)
                if t.value:
                    texts.append(f"{c.value}: {t.value}")
                return True

            user32.EnumChildWindows(ctypes.c_void_p(hwnd), proto(child), None)
            out.append((hwnd, texts))
        return True

    user32.EnumWindows(proto(top), None)
    for hwnd, texts in out:
        print(f"dialog 0x{hwnd:X}: " + " | ".join(texts))
    if not out:
        print("no dialogs")


def shot(pid, out):
    from PIL import Image
    hwnd = game_window(pid)
    r = wt.RECT()
    user32.GetClientRect(hwnd, ctypes.byref(r))
    w, h = r.right, r.bottom
    hdc = user32.GetDC(hwnd)
    mdc = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mdc, bmp)
    user32.PrintWindow(hwnd, mdc, 3)   # PW_CLIENTONLY | PW_RENDERFULLCONTENT
    bmi = (ctypes.c_byte * 40)()
    ctypes.memmove(bmi, (ctypes.c_int * 3)(40, w, -h), 12)
    ctypes.memmove(ctypes.byref(bmi, 12), (ctypes.c_short * 2)(1, 32), 4)
    buf = (ctypes.c_byte * (w * h * 4))()
    gdi32.GetDIBits(mdc, bmp, 0, h, buf, bmi, 0)
    Image.frombuffer("RGB", (w, h), bytes(buf), "raw", "BGRX", 0, 1).save(out)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mdc)
    user32.ReleaseDC(hwnd, hdc)
    print(f"{w}x{h} -> {out}")


def click(pid, x, y):
    hwnd = game_window(pid)
    lp = (y << 16) | (x & 0xFFFF)
    user32.PostMessageW(hwnd, WM_MOUSEMOVE, 0, lp)
    time.sleep(0.05)
    user32.PostMessageW(hwnd, WM_LBUTTONDOWN, 1, lp)
    time.sleep(0.08)
    user32.PostMessageW(hwnd, WM_LBUTTONUP, 0, lp)


def drag(pid, x0, y0, x1, y1):
    hwnd = game_window(pid)
    user32.PostMessageW(hwnd, WM_MOUSEMOVE, 0, (y0 << 16) | x0)
    time.sleep(0.05)
    user32.PostMessageW(hwnd, WM_LBUTTONDOWN, 1, (y0 << 16) | x0)
    for i in range(1, 11):
        x = x0 + (x1 - x0) * i // 10
        y = y0 + (y1 - y0) * i // 10
        time.sleep(0.03)
        user32.PostMessageW(hwnd, WM_MOUSEMOVE, 1, (y << 16) | x)
    time.sleep(0.05)
    user32.PostMessageW(hwnd, WM_LBUTTONUP, 0, (y1 << 16) | x1)


def type_text(pid, text):
    hwnd = game_window(pid)
    for ch in text:
        user32.PostMessageW(hwnd, WM_CHAR, ord(ch), 1)
        time.sleep(0.04)


def key(pid, vk):
    hwnd = game_window(pid)
    user32.PostMessageW(hwnd, WM_KEYDOWN, vk, 1)
    time.sleep(0.05)
    user32.PostMessageW(hwnd, WM_KEYUP, vk, 0xC0000001)


if __name__ == "__main__":
    cmd, pid = sys.argv[1], int(sys.argv[2])
    if cmd == "shot":
        shot(pid, sys.argv[3])
    elif cmd == "click":
        click(pid, int(sys.argv[3]), int(sys.argv[4]))
    elif cmd == "dialogs":
        dialogs(pid)
    elif cmd == "drag":
        drag(pid, *(int(v) for v in sys.argv[3:7]))
    elif cmd == "type":
        type_text(pid, sys.argv[3])
    elif cmd == "key":
        key(pid, int(sys.argv[3], 0))

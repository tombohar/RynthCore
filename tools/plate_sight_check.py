#!/usr/bin/env python3
"""
Offline check of the AC facts behind the nameplates' "behind walls" rule
(src/RynthCore.Engine/Compatibility/AcCellSight.cs, 2026-11-04, problem report
R-20261105-020337-67aa) against an acclient.exe. Read-only: maps the file, runs nothing.

pe_pattern.py CHECK already proves the embedded CEnvCell::GetDrawnThisFrame pattern is
unique and lands on its VA. This checks the layout the engine reads, so a different
client build fails here instead of hiding plates in game:

  * CEnvCell::GetDrawnThisFrame compares cell+0x130 with [device global]+0xB0, and
    SetDrawnThisFrame writes that stamp to cell+0x130 (the same global);
  * PView::DrawCells adds 1 to [global]+0xB0 before it draws, RenderDeviceD3D::Flip
    adds 1 to its own +0xB0, and DrawEnvCell marks the cell before drawing it;
  * CPhysicsObj::enter_cell stores the cell at +0x90 and the cell's +0x28 id at +0x4C;
  * SmartBox::RenderNormalMode reads the viewer cell id at +0x0C and the viewer cell's
    seen_outside through [+0x50]+0xE8;
  * PView::DrawInside hands the cell's +0xE0 / +0xE4 (visible-cell list) to add_views;
  * PView::ClipPortals walks the portal count at +0x108 and the array at +0x10C;
  * D3DPolyRender::DrawPortalPolyInternal reads the polygon's point count at +0x0E;
  * CCellPortal::UnPack stores 0xFFFFFFFF as the other cell of a doorway to outside;
  * CEnvCell::UnPack stores seen_outside (flags & 1) at +0xE8 (this-0x30 frame: +0xB8).

Usage:  python tools/plate_sight_check.py ["<path-to-acclient.exe>"]
Exit 0 = every check passed.
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pe_pattern import read_window, matches  # noqa: E402

DEFAULT_CLIENT = r"C:\Turbine\Asheron's Call\acclient.exe"
N = None

fails = 0


def check(ok, msg):
    global fails
    print(("ok   " if ok else "FAIL ") + msg)
    if not ok:
        fails += 1


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_CLIENT
    if not os.path.exists(path):
        print(f"acclient.exe not found: {path}")
        return 2
    W, BASE = read_window(path)

    def at(va, n):
        o = va - BASE
        return W[o:o + n]

    def body_has(va, needle, span=0x200):
        return W.find(bytes(needle), va - BASE, va - BASE + span) >= 0

    # CEnvCell::GetDrawnThisFrame: mov eax,[ecx+130h]; mov ecx,[global]; cmp eax,[ecx+0B0h]; sete al; ret
    pat = [0x8B, 0x81, 0x30, 0x01, 0x00, 0x00, 0x8B, 0x0D, N, N, N, N,
           0x3B, 0x81, 0xB0, 0x00, 0x00, 0x00, 0x0F, 0x94, 0xC0, 0xC3]
    ms = matches(W, pat, limit=4)
    check(len(ms) == 1, f"GetDrawnThisFrame pattern unique ({len(ms)} match)")
    if len(ms) != 1:
        return 1
    get_drawn = BASE + ms[0]
    g = struct.unpack_from("<I", W, ms[0] + 8)[0]
    print(f"     GetDrawnThisFrame @ 0x{get_drawn:08X}, render device global 0x{g:08X}")
    gb = list(struct.pack("<I", g))

    # SetDrawnThisFrame (right after it): mov eax,[global]; mov edx,[eax+0B0h]; mov [ecx+130h],edx; ret
    set_drawn = get_drawn + 0x20
    check(at(set_drawn, 18) == bytes([0xA1] + gb + [0x8B, 0x90, 0xB0, 0, 0, 0, 0x89, 0x91, 0x30, 0x01, 0, 0, 0xC3]),
          f"SetDrawnThisFrame @ 0x{set_drawn:08X} writes [global]+0xB0 to cell+0x130")

    # CPhysicsPart's twin reads +0DCh from the same global (proves the stamp is the device's, shared).
    twin = [0x8B, 0x81, 0xDC, 0x00, 0x00, 0x00, 0x8B, 0x0D] + gb + [0x3B, 0x81, 0xB0, 0, 0, 0, 0x0F, 0x94, 0xC0, 0xC3]
    check(len(matches(W, twin, limit=4)) == 1, "CPhysicsPart::GetDrawnThisFrame compares the same device stamp")

    # PView::DrawCells: mov eax,[global]; mov ecx,[eax+0B0h]; add esp,4; inc ecx; mov [eax+0B0h],ecx
    draw_cells = 0x005A5950
    check(body_has(draw_cells, [0xA1] + gb + [0x8B, 0x88, 0xB0, 0, 0, 0, 0x83, 0xC4, 0x04, 0x41, 0x89, 0x88, 0xB0, 0, 0, 0], 0x80),
          "PView::DrawCells adds 1 to the device stamp before drawing")
    # DrawEnvCell: mov esi,[esp+8]; mov ecx,esi; call GetDrawnThisFrame; test al,al; jnz; mov ecx,esi; call SetDrawnThisFrame
    dec = 0x005A0280
    ok = at(dec, 7) == bytes([0x56, 0x8B, 0x74, 0x24, 0x08, 0x8B, 0xCE]) and at(dec + 7, 1) == b"\xE8"
    rel1 = struct.unpack_from("<i", W, dec + 8 - BASE)[0]
    rel2 = struct.unpack_from("<i", W, dec + 0x17 - BASE)[0]
    check(ok and dec + 12 + rel1 == get_drawn and dec + 0x1B + rel2 == set_drawn,
          "DrawEnvCell tests GetDrawnThisFrame and calls SetDrawnThisFrame before drawing")
    # Flip, after Present: mov eax,[esi+0B0h]; inc eax; pop edi; mov [esi+0B0h],eax (the device's own stamp)
    flip = 0x005A0F60
    check(body_has(flip, [0x8B, 0x86, 0xB0, 0, 0, 0, 0x40, 0x5F, 0x89, 0x86, 0xB0, 0, 0, 0], 0x100),
          "RenderDeviceD3D::Flip adds 1 to +0xB0")

    # CPhysicsObj::enter_cell: mov eax,[ebx+28h]; mov [esi+4Ch],eax ... mov [esi+90h],ebx
    ec = 0x005119A0
    check(body_has(ec, [0x8B, 0x43, 0x28, 0x89, 0x46, 0x4C], 0x80) and body_has(ec, [0x89, 0x9E, 0x90, 0x00, 0x00, 0x00], 0x90),
          "CPhysicsObj::enter_cell: cell id from cell+0x28 to +0x4C, cell pointer to +0x90")

    # SmartBox::RenderNormalMode: mov ecx,[esi+0Ch] (viewer cell id) ... mov edx,[esi+50h]; mov ecx,[edx+0E8h]
    rnm = 0x00453B40
    check(body_has(rnm, [0x8B, 0x4E, 0x0C], 0x40) and body_has(rnm, [0x8B, 0x56, 0x50, 0x8B, 0x8A, 0xE8, 0, 0, 0], 0x60),
          "SmartBox::RenderNormalMode: viewer cell id +0x0C, viewer cell +0x50, its seen_outside +0xE8")

    # PView::DrawInside: mov eax,[esi+0E4h]; xor ecx,ecx; mov cx,[esi+0E0h] (add_views' arguments)
    di = 0x005A6970
    check(body_has(di, [0x8B, 0x86, 0xE4, 0, 0, 0, 0x33, 0xC9, 0x66, 0x8B, 0x8E, 0xE0, 0, 0, 0], 0x80),
          "PView::DrawInside hands the visible-cell list (+0xE0 count, +0xE4 ids) to add_views")

    # PView::ClipPortals: mov ecx,[ebp+108h] (portal count) ... mov esi,[ebp+10Ch] (portal array)
    cp = 0x005A6630
    check(body_has(cp, [0x8B, 0x8D, 0x08, 0x01, 0, 0], 0x40) and body_has(cp, [0x8B, 0xB5, 0x0C, 0x01, 0, 0], 0x80),
          "PView::ClipPortals: portal count +0x108, CCellPortal array +0x10C")

    # D3DPolyRender::DrawPortalPolyInternal: mov al,[ebp+0Eh] (point count); mov eax,[ebp+0]; mov ecx,[eax+esi*4]
    dp = 0x0059CCA0
    check(body_has(dp, [0x8A, 0x45, 0x0E], 0x40) and body_has(dp, [0x8B, 0x45, 0x00, 0x8B, 0x0C, 0xB0, 0xD9, 0x01], 0x60),
          "DrawPortalPolyInternal: CPolygon point count byte +0x0E, CVertex* array at +0, x first")

    # CCellPortal::UnPack: test dl,4; ... mov dword [esi],0FFFFFFFFh (a doorway to the outside)
    cu = 0x0053C810
    check(body_has(cu, [0xF6, 0xC2, 0x04], 0x60) and body_has(cu, [0xC7, 0x06, 0xFF, 0xFF, 0xFF, 0xFF], 0x60),
          "CCellPortal::UnPack: a doorway to outside has other cell 0xFFFFFFFF")

    # CEnvCell::UnPack: and ecx,1; mov [esi+0B8h],ecx (this-0x30: +0xE8 seen_outside)
    eu = 0x0052E0D0
    check(body_has(eu, [0x83, 0xE1, 0x01, 0x89, 0x8E, 0xB8, 0, 0, 0], 0x40),
          "CEnvCell::UnPack stores seen_outside (flags & 1) at +0xE8")

    print()
    print("PASS" if fails == 0 else f"{fails} FAILED")
    return 0 if fails == 0 else 1


if __name__ == "__main__":
    sys.exit(main())

"""Inject-BridgeRecords.py <pid> - append SYNTHETIC records to a test client's Decal-bridge ring.

For testing the engine half of the bridge while no server is available: it writes the same
records the bridge would (BridgeProtocol.cs layout) into Local\\RynthCore.DecalBridge.p<pid>.
Only use it on a test client whose bridge is idle (pre-login), or the two writers race.
"""
import mmap
import struct
import sys

# BridgeProtocol.cs version 2: header, 64 KB command ring, then the event ring.
HEADER, CMD, RING = 4096, 64 * 1024, 16 * 1024 * 1024
RING_BASE = HEADER + CMD
OFF_WRITE, OFF_READ, OFF_RECORDS = 16, 24, 40
K_CHAT, K_CHATBAR, K_CREATE, K_RELEASE, K_CHANGE, K_SERVER = 2, 3, 4, 5, 6, 7


def main():
    pid = int(sys.argv[1])
    m = mmap.mmap(-1, RING_BASE + RING, tagname=f"Local\\RynthCore.DecalBridge.p{pid}")
    magic, version = struct.unpack_from("<II", m, 0)
    assert magic == 0x42444352 and version == 2, (hex(magic), version)

    def write(kind, a0=0, a1=0, payload=b""):
        w, r = struct.unpack_from("<qq", m, OFF_WRITE)
        rec = 16 + len(payload)
        rec = (rec + 3) & ~3
        pad = rec - 16 - len(payload)
        pos = w % RING
        assert RING - pos >= rec and RING - (w - r) > rec
        base = RING_BASE + pos
        struct.pack_into("<iHHII", m, base, rec, kind, pad, a0, a1)
        m[base + 16: base + 16 + len(payload)] = payload
        struct.pack_into("<q", m, OFF_WRITE, w + rec)   # publish
        struct.pack_into("<q", m, OFF_RECORDS, struct.unpack_from("<q", m, OFF_RECORDS)[0] + 1)

    def game_event(ev, body):
        return struct.pack("<IIII", 0xF7B0, 0x50000001, 7, ev) + body

    write(K_CHAT, 0x03, 0, "[synthetic] You say, \"hello from the bridge test\"\n".encode("utf-16-le"))
    write(K_CHAT, 0x17, 0, "[synthetic] Welcome to Asheron's Call\n".encode("utf-16-le"))
    write(K_CHATBAR, 0, 0, "[synthetic] hello world".encode("utf-16-le"))
    for oid in (0x80000101, 0x80000102, 0x80000103):
        write(K_CREATE, oid)
    write(K_CHANGE, 0x80000102, 2)
    write(K_RELEASE, 0x80000103)
    # Game events, as Decal's Message.RawData: [F7B0][objectId][sequence][eventType][payload]
    write(K_SERVER, 0xF7B0, 0, game_event(0x01C7, struct.pack("<I", 0)))              # UseDone, no error
    write(K_SERVER, 0xF7B0, 0, game_event(0x028A, struct.pack("<I", 0x0402)))          # WeenieError
    write(K_SERVER, 0xF7B0, 0, game_event(0x01C0, struct.pack("<If", 0x80000101, 0.5)))  # UpdateHealth 50 %
    # spellId, layer, category, hasSpellSet, powerLevel, startTime, duration (+20), then 32 bytes
    ench = struct.pack("<HHHHI", 2, 1, 0, 0, 1) + struct.pack("<dd", 0.0, 1800.0) + b"\0" * 32
    assert len(ench) == 60
    write(K_SERVER, 0xF7B0, 0, game_event(0x02C2, ench))                                # UpdateEnchantment spell 2
    write(K_SERVER, 0xF7B0, 0, game_event(0x02C3, struct.pack("<HH", 2, 1)))            # RemoveEnchantment spell 2
    name = b"Drudge"
    s16 = struct.pack("<H", len(name)) + name
    s16 += b"\0" * (((len(s16) + 3) // 4) * 4 - len(s16))
    write(K_SERVER, 0xF7B0, 0, game_event(0x01B1, s16 + struct.pack("<IdII", 4, 0.3, 57, 1)))  # we hit: 57, crit
    write(K_SERVER, 0xF745, 0, struct.pack("<II", 0xF745, 0x80000104))                # some other message
    print("wrote synthetic records; ring now", struct.unpack_from("<qq", m, OFF_WRITE))


if __name__ == "__main__":
    main()

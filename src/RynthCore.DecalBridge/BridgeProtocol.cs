// ============================================================================
//  RynthCore.DecalBridge - BridgeProtocol.cs
//
//  The in-process channel between the Decal bridge filter (.NET Framework, Decal's
//  desktop CLR) and the RynthCore engine (CoreCLR, unloadable). Compiled into BOTH
//  assemblies (the engine links this file), so it stays C# 9 / net48-compatible.
//  docs/DECAL_BRIDGE_PLAN.md has the design.
//
//  A named, pagefile-backed file mapping holds a header and two single-producer /
//  single-consumer byte rings:
//    event ring   (bridge -> engine): producer = the bridge, on AC's main thread (Decal
//                 raises its events there); consumer = the engine's plugin pump thread.
//    command ring (engine -> bridge): producer = the engine (any thread, serialised by
//                 the engine); consumer = the bridge, on AC's main thread (RenderFrame),
//                 which runs each command through Decal's public API.
//  Nothing crosses the runtimes but bytes: no function pointer, delegate or COM object
//  is ever handed from one runtime to the other. That is what makes it safe with the
//  unloadable engine - a generation that goes away leaves nothing behind that the
//  bridge could call into, and neither side ever blocks on the other (a full ring
//  drops the record and counts it). The mapping lives as long as either side holds it,
//  so the read positions survive an engine hot reload.
//
//  The engine publishes which event kinds it wants (OffWantMask); the bridge writes
//  only those, so a stream the engine takes from its own hooks costs Decal nothing.
//
//  Record: [int length (multiple of 4, header included)][ushort kind][ushort pad bytes]
//          [uint arg0][uint arg1][payload...]
//  Write/read positions are monotonic byte counts (64-bit, Interlocked on x86).
//
//  Version 2 (2026-09-30): want mask, command ring, bridge hello/state fields.
// ============================================================================
#nullable disable
using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace RynthCore.DecalBridge
{
    internal enum BridgeRecordKind : ushort
    {
        Pad = 0,
        // ── events, bridge -> engine ──
        Hello = 1,          // arg0 = protocol version, payload = UTF-16 bridge description (always written)
        ChatText = 2,       // arg0 = Decal ChatBoxMessage.Color (AC chat type), arg1 = Target, payload = UTF-16 text
        ChatBarEnter = 3,   // arg0 = 1 when the bridge ate the line, payload = UTF-16 text
        CreateObject = 4,   // arg0 = object id, arg1 = 1 when part of a replay (WorldFilter.GetAll)
        ReleaseObject = 5,  // arg0 = object id
        ChangeObject = 6,   // arg0 = object id, arg1 = WorldChangeType
        ServerMessage = 7,  // arg0 = message type (opcode), payload = Message.RawData
        LoginComplete = 8,  // arg0 = character id
        Logoff = 9,         // arg0 = LogoffEventType
        ItemSelected = 10,  // arg0 = object id
        CommandResult = 11, // arg0 = command kind, arg1 = 1 ok / 0 failed, payload = UTF-16 detail (failures only)

        // ── commands, engine -> bridge (run on AC's main thread through Decal's public API) ──
        CmdInvokeChatParser = 100,   // payload = UTF-16 line   -> Actions.InvokeChatParser
        CmdSalvagePanelAdd = 101,    // arg0 = item id          -> Actions.SalvagePanelAdd
        CmdSalvagePanelSalvage = 102,//                          -> Actions.SalvagePanelSalvage
    }

    internal static unsafe class BridgeLayout
    {
        public const uint Magic = 0x42444352;   // "RCDB"
        public const int Version = 2;
        public const int HeaderSize = 4096;
        public const int CommandRingSize = 64 * 1024;
        public const int EventRingSize = 16 * 1024 * 1024;
        public const int TotalSize = HeaderSize + CommandRingSize + EventRingSize;
        public const int RecordHeaderSize = 16;
        public const int MaxRecord = 256 * 1024;

        // Header offsets.
        public const int OffMagic = 0;
        public const int OffVersion = 4;
        public const int OffBridgeState = 8;      // 1 while a bridge is started (its "hello")
        public const int OffBridgeBeats = 12;     // bridge: bumped per record written
        public const int OffWritePos = 16;        // long, event ring, producer-owned
        public const int OffReadPos = 24;         // long, event ring, consumer-owned
        public const int OffDropped = 32;         // long, event records the bridge dropped (ring full)
        public const int OffRecords = 40;         // long, event records written
        public const int OffEngineAttached = 48;  // 1 while an engine generation drains
        public const int OffEngineGeneration = 52;
        public const int OffBridgePid = 56;
        public const int OffReplayRequest = 60;   // engine -> bridge: bump to get WorldFilter.GetAll() as CreateObject records
        public const int OffReplayDone = 64;      // bridge -> engine: the request value last served
        public const int OffWantMask = 68;        // engine -> bridge: bit (1 << kind) for each event kind to forward
        public const int OffCmdWritePos = 72;     // long, command ring, engine-owned
        public const int OffCmdReadPos = 80;      // long, command ring, bridge-owned
        public const int OffCmdDropped = 88;      // long, commands the engine dropped (ring full)
        public const int OffCmdDone = 96;         // long, commands the bridge ran
        // Added in v2 without a version bump (zero from an older bridge = unknown):
        public const int OffD3DDevice = 104;      // bridge -> engine: IDirect3DDevice9* Decal renders with (IDecalCore.GetD3DDevice), 0 = unknown
        public const int OffDecalHwnd = 108;      // bridge -> engine: the game window as Decal knows it
        public const int OffPrefixes = 256;       // engine -> bridge: ';'-separated chat prefixes to eat, UTF-16, 0-terminated
        public const int PrefixChars = 1024;

        public static string MappingName(int pid) { return "Local\\RynthCore.DecalBridge.p" + pid; }

        public static int Align4(int n) { return (n + 3) & ~3; }

        public static uint Bit(BridgeRecordKind kind) { return (uint)kind < 32 ? 1u << (int)kind : 0u; }
    }

    /// <summary>One side's view of the mapping (the bridge and the engine each open their own).</summary>
    internal sealed unsafe class BridgeChannel : IDisposable
    {
        private MemoryMappedFile _file;
        private MemoryMappedViewAccessor _view;
        private byte* _base;

        public byte* Header { get { return _base; } }
        private byte* CommandRing { get { return _base + BridgeLayout.HeaderSize; } }
        private byte* EventRing { get { return _base + BridgeLayout.HeaderSize + BridgeLayout.CommandRingSize; } }

        public static BridgeChannel OpenOrCreate(int pid)
        {
            var c = new BridgeChannel();
            c._file = MemoryMappedFile.CreateOrOpen(BridgeLayout.MappingName(pid), BridgeLayout.TotalSize);
            c._view = c._file.CreateViewAccessor(0, BridgeLayout.TotalSize);
            byte* p = null;
            c._view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            c._base = p + c._view.PointerOffset;
            // The first opener stamps the header; the pages start zeroed either way.
            Interlocked.CompareExchange(ref *(int*)(c._base + BridgeLayout.OffVersion), BridgeLayout.Version, 0);
            Interlocked.CompareExchange(ref *(int*)(c._base + BridgeLayout.OffMagic), unchecked((int)BridgeLayout.Magic), 0);
            return c;
        }

        /// <summary>True when some side already created the mapping for <paramref name="pid"/>.</summary>
        public static bool Exists(int pid)
        {
            try
            {
                using (MemoryMappedFile f = MemoryMappedFile.OpenExisting(BridgeLayout.MappingName(pid)))
                    return true;
            }
            catch
            {
                return false;
            }
        }

        public int ReadInt(int off) { return Volatile.Read(ref *(int*)(_base + off)); }
        public void WriteInt(int off, int v) { Volatile.Write(ref *(int*)(_base + off), v); }
        public long ReadLong(int off) { return Interlocked.Read(ref *(long*)(_base + off)); }
        public void WriteLong(int off, long v) { Interlocked.Exchange(ref *(long*)(_base + off), v); }
        public void AddLong(int off, long v) { Interlocked.Add(ref *(long*)(_base + off), v); }

        public bool Valid { get { return (uint)ReadInt(BridgeLayout.OffMagic) == BridgeLayout.Magic && ReadInt(BridgeLayout.OffVersion) == BridgeLayout.Version; } }

        /// <summary>Whether the engine asked for <paramref name="kind"/> (Hello always goes).</summary>
        public bool Wants(BridgeRecordKind kind)
        {
            return kind == BridgeRecordKind.Hello || (unchecked((uint)ReadInt(BridgeLayout.OffWantMask)) & BridgeLayout.Bit(kind)) != 0;
        }

        // ── event ring (bridge -> engine) ─────────────────────────────────────
        public bool Write(BridgeRecordKind kind, uint arg0, uint arg1, byte* payload, int payloadLen)
        {
            if (!Wants(kind))
                return false;
            bool ok = Put(EventRing, BridgeLayout.EventRingSize, BridgeLayout.OffWritePos, BridgeLayout.OffReadPos,
                BridgeLayout.OffDropped, kind, arg0, arg1, payload, payloadLen);
            if (ok)
            {
                AddLong(BridgeLayout.OffRecords, 1);
                Interlocked.Increment(ref *(int*)(_base + BridgeLayout.OffBridgeBeats));
            }
            return ok;
        }

        public bool WriteString(BridgeRecordKind kind, uint arg0, uint arg1, string text)
        {
            if (text == null) text = string.Empty;
            fixed (char* c = text)
                return Write(kind, arg0, arg1, (byte*)c, text.Length * 2);
        }

        public bool WriteBytes(BridgeRecordKind kind, uint arg0, uint arg1, byte[] data)
        {
            if (data == null || data.Length == 0)
                return Write(kind, arg0, arg1, null, 0);
            fixed (byte* b = data)
                return Write(kind, arg0, arg1, b, data.Length);
        }

        public delegate void RecordHandler(BridgeRecordKind kind, uint arg0, uint arg1, byte* payload, int payloadLen);

        /// <summary>Hands every event written so far to <paramref name="handler"/> (payload valid only during the call).</summary>
        public int Drain(RecordHandler handler, int maxRecords)
        {
            return Take(EventRing, BridgeLayout.EventRingSize, BridgeLayout.OffWritePos, BridgeLayout.OffReadPos, handler, maxRecords);
        }

        // ── command ring (engine -> bridge) ───────────────────────────────────
        /// <summary>Queues a command for the bridge. One producer at a time (the engine serialises).</summary>
        public bool WriteCommand(BridgeRecordKind kind, uint arg0, uint arg1, string text)
        {
            if (text == null)
                return Put(CommandRing, BridgeLayout.CommandRingSize, BridgeLayout.OffCmdWritePos, BridgeLayout.OffCmdReadPos,
                    BridgeLayout.OffCmdDropped, kind, arg0, arg1, null, 0);
            fixed (char* c = text)
                return Put(CommandRing, BridgeLayout.CommandRingSize, BridgeLayout.OffCmdWritePos, BridgeLayout.OffCmdReadPos,
                    BridgeLayout.OffCmdDropped, kind, arg0, arg1, (byte*)c, text.Length * 2);
        }

        /// <summary>Bridge: runs every queued command through <paramref name="handler"/>.</summary>
        public int DrainCommands(RecordHandler handler, int maxRecords)
        {
            int n = Take(CommandRing, BridgeLayout.CommandRingSize, BridgeLayout.OffCmdWritePos, BridgeLayout.OffCmdReadPos, handler, maxRecords);
            if (n > 0) AddLong(BridgeLayout.OffCmdDone, n);
            return n;
        }

        public bool HasCommands { get { return ReadLong(BridgeLayout.OffCmdWritePos) != ReadLong(BridgeLayout.OffCmdReadPos); } }

        // ── ring primitives ───────────────────────────────────────────────────
        private bool Put(byte* ring, int size, int offWrite, int offRead, int offDropped,
            BridgeRecordKind kind, uint arg0, uint arg1, byte* payload, int payloadLen)
        {
            if (payloadLen < 0 || payloadLen > BridgeLayout.MaxRecord || payloadLen > size / 4)
            {
                AddLong(offDropped, 1);
                return false;
            }
            int recLen = BridgeLayout.Align4(BridgeLayout.RecordHeaderSize + payloadLen);
            long w = ReadLong(offWrite);
            long r = ReadLong(offRead);
            int pos = (int)(w % size);
            int tail = size - pos;
            int need = recLen + (tail < recLen ? tail : 0);
            if (need > size - (w - r))
            {
                AddLong(offDropped, 1);
                return false;
            }
            if (tail < recLen)
            {
                // Doesn't fit before the end: pad to the end, start again at 0. A tail shorter
                // than a record header is skipped by the reader without being read.
                if (tail >= BridgeLayout.RecordHeaderSize)
                {
                    *(int*)(ring + pos) = tail;
                    *(ushort*)(ring + pos + 4) = (ushort)BridgeRecordKind.Pad;
                }
                pos = 0;
            }
            byte* rec = ring + pos;
            *(int*)rec = recLen;
            *(ushort*)(rec + 4) = (ushort)kind;
            *(ushort*)(rec + 6) = (ushort)(recLen - BridgeLayout.RecordHeaderSize - payloadLen);   // pad bytes
            *(uint*)(rec + 8) = arg0;
            *(uint*)(rec + 12) = arg1;
            for (int i = 0; i < payloadLen; i++)
                rec[BridgeLayout.RecordHeaderSize + i] = payload[i];
            WriteLong(offWrite, w + need);   // publish (full fence)
            return true;
        }

        private int Take(byte* ring, int size, int offWrite, int offRead, RecordHandler handler, int maxRecords)
        {
            long r = ReadLong(offRead);
            long w = ReadLong(offWrite);
            int n = 0;
            while (r < w && n < maxRecords)
            {
                int pos = (int)(r % size);
                int tail = size - pos;
                if (tail < BridgeLayout.RecordHeaderSize) { r += tail; continue; }
                int len = *(int*)(ring + pos);
                if (len < BridgeLayout.RecordHeaderSize || len > tail || (len & 3) != 0)
                {
                    r = w;   // corrupt: resynchronise at the writer
                    break;
                }
                var kind = (BridgeRecordKind)(*(ushort*)(ring + pos + 4));
                if (kind != BridgeRecordKind.Pad)
                {
                    int pad = *(ushort*)(ring + pos + 6) & 3;
                    handler(kind, *(uint*)(ring + pos + 8), *(uint*)(ring + pos + 12),
                        ring + pos + BridgeLayout.RecordHeaderSize, len - BridgeLayout.RecordHeaderSize - pad);
                    n++;
                }
                r += len;
            }
            WriteLong(offRead, r);
            return n;
        }

        public void Dispose()
        {
            if (_view != null)
            {
                if (_base != null) _view.SafeMemoryMappedViewHandle.ReleasePointer();
                _view.Dispose();
                _view = null;
            }
            _base = null;
            if (_file != null) { _file.Dispose(); _file = null; }
        }
    }
}

// ============================================================================
//  RynthCore.StatusAgent - Dat/DatDatabase.cs
//  Read-only access to Asheron's Call .dat archives (the agent reads item
//  icons from client_portal.dat). Written for RynthCore (MIT) on 2026-10-05;
//  the same design as RynthSuite's Shared/RynthCore.TerrainData/DatDatabase.cs.
//
//  Written from the dat container format as it is publicly documented, with
//  Chorizite's DatReaderWriter (MIT, notice below) used as the format reference:
//    - file header at byte 0x140: magic, block size, file size, data set,
//      subset, free head / tail / count, then the byte offset of the root
//      B-tree node (all little-endian uint32);
//    - storage is fixed-size blocks; a block's first uint32 is the byte offset
//      of the next block of the same chain (0 ends the chain), the remaining
//      BlockSize - 4 bytes are payload;
//    - the directory is a B-tree whose nodes are stored as block chains: 62
//      uint32 child offsets, a uint32 entry count (at most 61), then the
//      entries, 24 bytes each: flags/version, id, byte offset of the file's
//      first block, size, timestamp, iteration. Entries are sorted by id,
//      child i holds the ids between entry i-1 and entry i, and a node whose
//      first child offset is 0 is a leaf.
//
//  Lookups descend the tree from the root; interior nodes are kept once read
//  and leaves go through a small LRU. Every call takes one lock, so a database
//  can be shared between threads.
//
//  Chorizite DatReaderWriter notice (format reference):
//    Copyright 2024 ACClientLib
//    Permission is hereby granted, free of charge, to any person obtaining a
//    copy of this software and associated documentation files (the
//    "Software"), to deal in the Software without restriction, including
//    without limitation the rights to use, copy, modify, merge, publish,
//    distribute, sublicense, and/or sell copies of the Software, and to permit
//    persons to whom the Software is furnished to do so, subject to the
//    following conditions: The above copyright notice and this permission
//    notice shall be included in all copies or substantial portions of the
//    Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
//    EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
//    MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN
//    NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
//    DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
//    OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE
//    USE OR OTHER DEALINGS IN THE SOFTWARE.
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace RynthCore2.TerrainData;

/// <summary>
/// A read-only .dat archive. <see cref="Open"/> reads the header, checks the
/// root directory node and counts the files; files are then found by id by
/// searching the on-disk B-tree. Methods return null / empty when the database
/// isn't open or the file isn't there; nothing throws for a damaged file.
/// </summary>
public class DatDatabase : IDisposable
{
    private const long HeaderOffset = 0x140;
    private const int HeaderBytes = 9 * 4;            // magic .. root offset
    private const int ChildSlots = 62;
    private const int MaxEntriesPerNode = 61;
    private const int EntryBytes = 24;
    private const int CountOffset = ChildSlots * 4;                               // 248
    private const int EntriesOffset = CountOffset + 4;                            // 252
    private const int NodeBytes = EntriesOffset + MaxEntriesPerNode * EntryBytes; // 1716
    private const int DepthLimit = 16;   // real trees are 4 deep; stops a looping child pointer
    private const int LeafLruSize = 128;
    private const int InteriorCap = 4096;

    private readonly object _gate = new();
    private FileStream? _file;
    private long _length;
    private readonly byte[] _nodeBuf = new byte[NodeBytes];
    private readonly Dictionary<uint, Node> _interior = new();
    private readonly Dictionary<uint, LinkedListNode<(uint At, Node Node)>> _leafMap = new();
    private readonly LinkedList<(uint At, Node Node)> _leafOrder = new();

    /// <summary>Header word at 0x140 (the dat magic).</summary>
    public uint FileType { get; private set; }
    /// <summary>Block size in bytes (typical: 1024).</summary>
    public uint BlockSize { get; private set; }
    /// <summary>File size recorded in the header.</summary>
    public uint FileSize { get; private set; }
    /// <summary>The header's data set (database type) word.</summary>
    public uint DataSet { get; private set; }
    /// <summary>Byte offset of the root B-tree node.</summary>
    public uint BTreeRoot { get; private set; }

    /// <summary>True once <see cref="Open"/> completes successfully.</summary>
    public bool IsLoaded { get; private set; }
    /// <summary>Source file path passed to <see cref="Open"/>.</summary>
    public string? FilePath { get; private set; }
    /// <summary>Number of files in the dat (counted at <see cref="Open"/>).</summary>
    public int RecordCount { get; private set; }

    /// <summary>Most-recent diagnostic messages (capped at 100).</summary>
    public List<string> DiagLog { get; } = new();

    private sealed class Node
    {
        public uint[]? Children;              // entry count + 1 offsets; null for a leaf
        public DatBTreeEntry[] Entries = []; // ascending ObjectId
    }

    // ---------------------------------------------------------------- open / close

    /// <summary>
    /// Opens the .dat file at <paramref name="path"/> for read-only random access.
    /// Returns false on any failure.
    /// </summary>
    public bool Open(string path)
    {
        lock (_gate)
        {
            CloseLocked();
            FilePath = path;
            try
            {
                if (!File.Exists(path)) { Log($"Not found: {path}"); return false; }

                // AC keeps its own handles on its dats: share everything,
                // including delete (the client's own open asks for it).
                _file = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
                _length = _file.Length;

                Span<byte> h = stackalloc byte[HeaderBytes];
                if (!ReadAt(HeaderOffset, h)) { Log("Header is short"); CloseLocked(); return false; }
                FileType = BinaryPrimitives.ReadUInt32LittleEndian(h);
                BlockSize = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(4));
                FileSize = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(8));
                DataSet = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(12));
                BTreeRoot = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(32));

                if (BlockSize <= 4 || BlockSize > 0x10000) { Log($"Bad block size {BlockSize}"); CloseLocked(); return false; }
                if (BTreeRoot == 0 || BTreeRoot >= _length) { Log($"Bad root offset 0x{BTreeRoot:X8}"); CloseLocked(); return false; }

                IsLoaded = true;
                if (GetNode(BTreeRoot) == null)
                {
                    Log($"Root node at 0x{BTreeRoot:X8} doesn't parse");
                    CloseLocked();
                    return false;
                }
                RecordCount = CountUnder(BTreeRoot, 0);
                Log($"Opened {Path.GetFileName(path)}: {RecordCount} files, block size {BlockSize}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Open failed: {ex.Message}");
                CloseLocked();
                return false;
            }
        }
    }

    /// <summary>Closes the underlying file handle and drops the cached directory nodes.</summary>
    public void Close()
    {
        lock (_gate) CloseLocked();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    private void CloseLocked()
    {
        IsLoaded = false;
        _file?.Dispose();
        _file = null;
        _length = 0;
        RecordCount = 0;
        _interior.Clear();
        _leafMap.Clear();
        _leafOrder.Clear();
    }

    // ---------------------------------------------------------------- lookups

    /// <summary>The directory entry for <paramref name="fileId"/>, or null.</summary>
    public DatBTreeEntry? FindFile(uint fileId)
    {
        lock (_gate)
        {
            if (!IsLoaded) return null;
            uint at = BTreeRoot;
            for (int depth = 0; depth < DepthLimit && at != 0; depth++)
            {
                Node? n = GetNode(at);
                if (n == null) return null;
                int i = LowerBound(n.Entries, fileId);
                if (i < n.Entries.Length && n.Entries[i].ObjectId == fileId) return n.Entries[i];
                if (n.Children == null) return null;
                at = n.Children[i];
            }
            return null;
        }
    }

    /// <summary>The bytes of the file <paramref name="entry"/> describes, or null.</summary>
    public byte[]? ReadFileData(DatBTreeEntry? entry)
    {
        if (entry == null) return null;
        lock (_gate)
        {
            if (!IsLoaded) return null;
            if (entry.FileSize > _length) { Log($"0x{entry.ObjectId:X8}: size {entry.FileSize} exceeds the file"); return null; }
            var data = new byte[entry.FileSize];
            if (ReadChain(entry.FileOffset, data) != data.Length)
            {
                Log($"0x{entry.ObjectId:X8}: block chain at 0x{entry.FileOffset:X8} is broken");
                return null;
            }
            return data;
        }
    }

    /// <summary>Convenience: find by id then read.</summary>
    public byte[]? GetFileData(uint fileId) => ReadFileData(FindFile(fileId));

    /// <summary>
    /// (fileId, storedSize) for every file whose id is in [lo, hi], largest first
    /// (ties by ascending id).
    /// </summary>
    public List<(uint id, uint size)> EntriesInRange(uint lo, uint hi)
    {
        var found = new List<DatBTreeEntry>();
        lock (_gate)
        {
            if (IsLoaded && lo <= hi) Collect(BTreeRoot, lo, hi, found, 0);
        }
        var result = new List<(uint id, uint size)>(found.Count);
        foreach (var e in found) result.Add((e.ObjectId, e.FileSize));
        result.Sort((a, b) => a.size != b.size ? b.size.CompareTo(a.size) : a.id.CompareTo(b.id));
        return result;
    }

    /// <summary>
    /// The interior (EnvCell) ids 0xXXYY0100-0xXXYYFFFD of landblock
    /// <paramref name="landblockKey"/> (0xXXYY), ascending.
    /// </summary>
    public List<uint> GetLandblockCellIds(uint landblockKey)
    {
        var found = new List<DatBTreeEntry>();
        lock (_gate)
        {
            if (IsLoaded) Collect(BTreeRoot, (landblockKey << 16) | 0x0100, (landblockKey << 16) | 0xFFFD, found, 0);
        }
        var ids = new List<uint>(found.Count);
        foreach (var e in found) ids.Add(e.ObjectId);
        return ids;
    }

    /// <summary>The lowest file ids in the dat (up to <paramref name="maxCount"/>), for diagnostics.</summary>
    public List<uint> GetSampleIds(int maxCount = 20)
    {
        var ids = new List<uint>();
        if (maxCount <= 0) return ids;
        var found = new List<DatBTreeEntry>();
        lock (_gate)
        {
            if (IsLoaded) Collect(BTreeRoot, 0, uint.MaxValue, found, 0, maxCount);
        }
        foreach (var e in found) ids.Add(e.ObjectId);
        return ids;
    }

    // In-order walk of the subtree at `at`, adding the entries with ids in
    // [lo, hi] (stops once `into` holds `limit`). Caller holds _gate.
    private void Collect(uint at, uint lo, uint hi, List<DatBTreeEntry> into, int depth, int limit = int.MaxValue)
    {
        if (at == 0 || depth >= DepthLimit || into.Count >= limit) return;
        Node? n = GetNode(at);
        if (n == null) return;
        var e = n.Entries;
        for (int i = 0; i <= e.Length; i++)
        {
            if (n.Children != null)
            {
                // Child i holds the ids between e[i-1] and e[i].
                bool aboveLo = i == e.Length || e[i].ObjectId > lo;
                bool belowHi = i == 0 || e[i - 1].ObjectId < hi;
                if (aboveLo && belowHi) Collect(n.Children[i], lo, hi, into, depth + 1, limit);
            }
            if (i == e.Length || into.Count >= limit) break;
            uint id = e[i].ObjectId;
            if (id > hi) break;
            if (id >= lo) into.Add(e[i]);
        }
    }

    // ---------------------------------------------------------------- nodes

    private static int LowerBound(DatBTreeEntry[] e, uint id)
    {
        int a = 0, b = e.Length;
        while (a < b)
        {
            int m = (a + b) >> 1;
            if (e[m].ObjectId < id) a = m + 1; else b = m;
        }
        return a;
    }

    // Cached node: interior nodes kept (capped), leaves in a small LRU. Caller holds _gate.
    private Node? GetNode(uint at)
    {
        if (_interior.TryGetValue(at, out Node? n)) return n;
        if (_leafMap.TryGetValue(at, out var hit))
        {
            _leafOrder.Remove(hit);
            _leafOrder.AddFirst(hit);
            return hit.Value.Node;
        }
        n = ParseNode(at);
        if (n == null) return null;
        if (n.Children != null)
        {
            if (_interior.Count < InteriorCap) _interior[at] = n;
        }
        else
        {
            if (_leafMap.Count >= LeafLruSize && _leafOrder.Last != null)
            {
                _leafMap.Remove(_leafOrder.Last.Value.At);
                _leafOrder.RemoveLast();
            }
            _leafMap[at] = _leafOrder.AddFirst((at, n));
        }
        return n;
    }

    private Node? ParseNode(uint at)
    {
        int got = ReadChain(at, _nodeBuf);
        if (got < EntriesOffset) return null;
        ReadOnlySpan<byte> s = _nodeBuf;
        int count = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(CountOffset));
        if (count < 0 || count > MaxEntriesPerNode || got < EntriesOffset + count * EntryBytes) return null;

        var entries = new DatBTreeEntry[count];
        for (int i = 0; i < count; i++)
        {
            var r = s.Slice(EntriesOffset + i * EntryBytes, EntryBytes);
            entries[i] = new DatBTreeEntry
            {
                BitFlags = BinaryPrimitives.ReadUInt32LittleEndian(r),
                ObjectId = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(4)),
                FileOffset = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(8)),
                FileSize = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(12)),
            };
        }
        uint[]? children = null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(s) != 0)
        {
            children = new uint[count + 1];
            for (int i = 0; i <= count; i++) children[i] = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(i * 4));
        }
        return new Node { Children = children, Entries = entries };
    }

    // Entry counts under `at`, reading only each node's child table and count. Caller holds _gate.
    private int CountUnder(uint at, int depth)
    {
        if (at == 0 || depth >= DepthLimit) return 0;
        Span<byte> head = stackalloc byte[EntriesOffset];
        if (ReadChain(at, head) != EntriesOffset) return 0;
        int count = BinaryPrimitives.ReadInt32LittleEndian(head.Slice(CountOffset));
        if (count < 0 || count > MaxEntriesPerNode) return 0;
        int total = count;
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != 0)
        {
            Span<uint> kids = stackalloc uint[count + 1];
            for (int i = 0; i <= count; i++) kids[i] = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(i * 4));
            for (int i = 0; i <= count; i++) total += CountUnder(kids[i], depth + 1);
        }
        return total;
    }

    // ---------------------------------------------------------------- blocks

    // Copies the block chain starting at byte offset `first` into `dest` and
    // returns how many bytes it filled: dest.Length, or less when the chain
    // ends (a 0 link) first; -1 when a link points outside the file.
    private int ReadChain(uint first, Span<byte> dest)
    {
        int payload = (int)BlockSize - 4;
        long block = first;
        int done = 0;
        Span<byte> link = stackalloc byte[4];
        while (done < dest.Length)
        {
            if (block == 0) return done;
            if (block + 4 > _length) return -1;
            int take = Math.Min(payload, dest.Length - done);
            if (!ReadAt(block, link) || !ReadAt(block + 4, dest.Slice(done, take))) return -1;
            done += take;
            block = BinaryPrimitives.ReadUInt32LittleEndian(link);
        }
        return done;
    }

    private bool ReadAt(long offset, Span<byte> into)
    {
        if (_file == null || offset < 0 || offset + into.Length > _length) return false;
        _file.Position = offset;
        int got = 0;
        while (got < into.Length)
        {
            int n = _file.Read(into.Slice(got));
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    private void Log(string msg)
    {
        string line = "[DatDB] " + msg;
        System.Diagnostics.Debug.WriteLine(line);
        if (DiagLog.Count < 100) DiagLog.Add(line);
    }
}

/// <summary>One directory entry of a .dat archive.</summary>
public class DatBTreeEntry
{
    /// <summary>Raw flag bits from the B-tree.</summary>
    public uint BitFlags { get; set; }
    /// <summary>Object ID (the file's key in the B-tree).</summary>
    public uint ObjectId { get; set; }
    /// <summary>Byte offset of the first block of the file's chain.</summary>
    public uint FileOffset { get; set; }
    /// <summary>Logical file size in bytes.</summary>
    public uint FileSize { get; set; }
}

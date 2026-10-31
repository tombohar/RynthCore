// ============================================================================
//  RynthCore.Headless - Dat/DatFile.cs
//  Reads one file by id from an AC dat (header at 0x140, B-tree search by id, block
//  chains whose first dword is the next block). Adapted from the engine's minimal
//  reader in ImGui/MonsterHud/PortalSpellTable.cs (itself a port of RynthSuite's
//  TerrainData/DatDatabase.cs). Read-only, shares the file with running clients.
//  P2 replaces this with the shared dat library.
// ============================================================================

namespace RynthCore.Headless.Dat;

public static class DatFile
{
    private const int HeaderOffset = 0x140;
    private const int Branches = 62, MaxEntries = 61, EntrySize = 24;
    private const int NodeSize = 4 * Branches + 4 + EntrySize * MaxEntries;   // 1716
    private const int MaxDepth = 16;

    /// <summary>
    /// The folder with the dats: <paramref name="preferred"/> (the launcher's acclient.exe folder) when it
    /// has them, else the usual install folder, else the current directory.
    /// </summary>
    public static string? FindAcFolder(string? preferred = null)
    {
        foreach (string dir in new[] { preferred ?? "", @"C:\Turbine\Asheron's Call", Environment.CurrentDirectory })
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, "client_portal.dat"))) return dir;
        return null;
    }

    public static byte[]? Read(string path, uint fileId, out string why)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        Span<byte> hdr = stackalloc byte[36];
        fs.Seek(HeaderOffset, SeekOrigin.Begin);
        if (!ReadFully(fs, hdr)) { why = "short header"; return null; }
        uint blockSize = BitConverter.ToUInt32(hdr.Slice(4, 4));
        uint root = BitConverter.ToUInt32(hdr.Slice(32, 4));
        if (blockSize <= 4 || blockSize > 65536 || root == 0 || root >= fs.Length) { why = "bad header"; return null; }

        uint offset = root;
        for (int depth = 0; depth < MaxDepth && offset != 0; depth++)
        {
            byte[]? node = ReadChain(fs, blockSize, offset, NodeSize);
            if (node == null) { why = "unreadable B-tree node"; return null; }
            uint count = BitConverter.ToUInt32(node, 4 * Branches);
            if (count > MaxEntries) { why = "corrupt B-tree node"; return null; }
            int i = 0;
            while (i < count && BitConverter.ToUInt32(node, 4 * Branches + 4 + i * EntrySize + 4) < fileId) i++;
            if (i < count && BitConverter.ToUInt32(node, 4 * Branches + 4 + i * EntrySize + 4) == fileId)
            {
                int e = 4 * Branches + 4 + i * EntrySize;
                uint fileOffset = BitConverter.ToUInt32(node, e + 8);
                uint fileSize = BitConverter.ToUInt32(node, e + 12);
                if (fileSize == 0 || fileSize > 64 * 1024 * 1024) { why = "implausible file size"; return null; }
                byte[]? data = ReadChain(fs, blockSize, fileOffset, (int)fileSize);
                why = data == null ? "unreadable file blocks" : "";
                return data;
            }
            bool leaf = BitConverter.ToUInt32(node, 0) == 0;
            if (leaf || i >= Branches) break;
            offset = BitConverter.ToUInt32(node, i * 4);
        }
        why = $"file 0x{fileId:X8} not in the dat";
        return null;
    }

    private static byte[]? ReadChain(FileStream fs, uint blockSize, uint offset, int size)
    {
        if ((long)offset + 4 > fs.Length) return null;
        var buffer = new byte[size];
        Span<byte> next = stackalloc byte[4];
        fs.Seek(offset, SeekOrigin.Begin);
        if (!ReadFully(fs, next)) return null;
        uint nextAddr = BitConverter.ToUInt32(next);
        int done = 0, guard = 0;
        while (done < size)
        {
            int take = Math.Min((int)blockSize - 4, size - done);
            if (!ReadFully(fs, buffer.AsSpan(done, take))) return null;
            done += take;
            if (done >= size) break;
            if (nextAddr == 0 || nextAddr >= fs.Length || ++guard > 1_000_000) return null;
            fs.Seek(nextAddr, SeekOrigin.Begin);
            if (!ReadFully(fs, next)) return null;
            nextAddr = BitConverter.ToUInt32(next);
        }
        return buffer;
    }

    private static bool ReadFully(FileStream fs, Span<byte> dest)
    {
        while (dest.Length > 0)
        {
            int n = fs.Read(dest);
            if (n <= 0) return false;
            dest = dest.Slice(n);
        }
        return true;
    }
}

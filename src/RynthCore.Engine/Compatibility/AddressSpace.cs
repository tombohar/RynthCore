// ============================================================================
//  RynthCore.Engine - Compatibility/AddressSpace.cs
//
//  Free user address space and the LARGEST free block, in MB. acclient is 32-bit, so
//  "out of memory" usually means fragmentation, not exhaustion: 300 MB may be free in
//  total while no single block can take a 16 MB allocation. vafree alone (the
//  heartbeat's GlobalMemoryStatusEx total) cannot show that; valargest can.
//
//  Walks the regions with VirtualQuery (a few thousand calls, no allocation). Any
//  thread; the heartbeat thread calls it once a second and PluginManager around each
//  plugin load and unload during a reload. Compiled into tools\EngineRobustnessTests.
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

internal static class AddressSpace
{
    private const uint MEM_FREE = 0x10000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nuint BaseAddress;
        public nuint AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;        // sequential layout pads after the uint on x64
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(nuint lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, nuint dwLength);

    /// <summary>
    /// Total free and largest free block of this process's address space, in MB, or
    /// (-1, -1) on failure. Never throws.
    /// </summary>
    public static (long FreeMb, long LargestFreeMb) FreeAndLargestMb()
    {
        try
        {
            ulong free = 0, largest = 0;
            nuint addr = 0;
            nuint size = (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
            int guard = 0;
            while (guard++ < 1_000_000)
            {
                if (VirtualQuery(addr, out MEMORY_BASIC_INFORMATION mbi, size) == 0)
                    break;
                ulong region = mbi.RegionSize;
                if (region == 0)
                    break;
                Accumulate(mbi.State == MEM_FREE, region, ref free, ref largest);
                nuint next = mbi.BaseAddress + mbi.RegionSize;
                if (next <= addr)
                    break;               // wrapped past the top
                addr = next;
            }
            if (guard <= 1)
                return (-1, -1);
            return ((long)(free >> 20), (long)(largest >> 20));
        }
        catch
        {
            return (-1, -1);
        }
    }

    /// <summary>Pure: fold one region into the running totals.</summary>
    public static void Accumulate(bool isFree, ulong regionBytes, ref ulong freeBytes, ref ulong largestBytes)
    {
        if (!isFree) return;
        freeBytes += regionBytes;
        if (regionBytes > largestBytes) largestBytes = regionBytes;
    }

    /// <summary>"free=NNNMB largest=NNNMB", for the reload log lines.</summary>
    public static string Describe()
    {
        var (free, largest) = FreeAndLargestMb();
        return free < 0 ? "free=? largest=?" : $"free={free}MB largest={largest}MB";
    }
}

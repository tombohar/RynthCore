using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

internal static class SmartBoxLocator
{
    public const int SmartBoxCmdInterpOffset = 0xB8;
    public const int SmartBoxPlayerIdOffset = 0xF4;
    public const int SmartBoxPlayerOffset = 0xF8;

    private static IntPtr _smartboxStaticAddr;
    private static IntPtr _moduleBase;
    private static int _imageSize;
    private static readonly List<IntPtr> _smartboxStaticCandidates = [];
    private static string _statusMessage = "Not probed yet.";

    // ─── VirtualQuery-based memory validation ────────────────────────────
    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern int VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, int dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;
    private const uint READABLE_MASK = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;

    /// <summary>
    /// Checks whether the given memory range is committed and readable without triggering an AV.
    /// Uses VirtualQuery — safe to call on any address including unmapped pages.
    /// Critical for NativeAOT where AccessViolationException cannot be caught.
    /// </summary>
    public static bool IsMemoryReadable(IntPtr address, int size)
    {
        if (address == IntPtr.Zero || size <= 0)
            return false;

        if (VirtualQuery(address, out MEMORY_BASIC_INFORMATION mbi,
                Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0)
            return false;

        if (mbi.State != MEM_COMMIT)
            return false;
        if ((mbi.Protect & PAGE_NOACCESS) != 0)
            return false;
        if ((mbi.Protect & PAGE_GUARD) != 0)
            return false;
        if ((mbi.Protect & READABLE_MASK) == 0)
            return false;

        // Ensure the committed region covers the full requested range
        int regionEnd = mbi.BaseAddress.ToInt32() + mbi.RegionSize.ToInt32();
        int requestEnd = address.ToInt32() + size;
        return requestEnd <= regionEnd;
    }

    public static bool IsInitialized { get; private set; }
    public static int CandidateCount => _smartboxStaticCandidates.Count;
    public static string StatusMessage => _statusMessage;

    public static bool Probe()
    {
        Reset();

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return false;
        }

        try
        {
            _moduleBase = textSection.ModuleBase;
            _imageSize = textSection.ImageSize;

            CollectSmartBoxCandidates(textSection.Bytes, textSection.TextBaseVa, textSection.ModuleBase.ToInt32());
            if (_smartboxStaticCandidates.Count == 0)
            {
                _statusMessage = "SmartBox::smartbox static not found.";
                RynthLog.Compat("Compat: smartbox locator probe failed - SmartBox::smartbox static not found.");
                return false;
            }

            _smartboxStaticAddr = _smartboxStaticCandidates[0];
            IsInitialized = true;
            _statusMessage = "Ready.";
            return true;
        }
        catch (Exception ex)
        {
            Reset();
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: smartbox locator probe failed - {ex.Message}");
            return false;
        }
    }

    public static bool TryGetSmartBox(out IntPtr smartBox, out IntPtr smartboxStaticAddr, out string failure)
    {
        smartBox = IntPtr.Zero;
        smartboxStaticAddr = IntPtr.Zero;
        failure = string.Empty;

        if ((!IsInitialized || _smartboxStaticCandidates.Count == 0) && !Probe())
        {
            failure = _statusMessage;
            return false;
        }

        foreach (IntPtr candidate in EnumerateCandidates())
        {
            try
            {
                IntPtr resolved = Marshal.ReadIntPtr(candidate);
                if (resolved == IntPtr.Zero)
                {
                    failure = "SmartBox instance not ready.";
                    continue;
                }

                IntPtr vtable = Marshal.ReadIntPtr(resolved);
                if (!IsPointerInModule(vtable))
                {
                    failure = $"SmartBox vtable looks invalid (0x{vtable.ToInt32():X8}).";
                    continue;
                }

                smartBox = resolved;
                smartboxStaticAddr = candidate;
                _smartboxStaticAddr = candidate;
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
        }

        if (string.IsNullOrEmpty(failure))
            failure = "SmartBox instance not ready.";

        return false;
    }

    public static bool TryGetPlayer(out IntPtr player, out uint playerId, out string failure)
    {
        player = IntPtr.Zero;
        playerId = 0;

        if (!TryGetSmartBox(out IntPtr smartBox, out _, out failure))
            return false;

        try
        {
            player = Marshal.ReadIntPtr(smartBox + SmartBoxPlayerOffset);
            playerId = unchecked((uint)Marshal.ReadInt32(smartBox + SmartBoxPlayerIdOffset));

            if (player == IntPtr.Zero)
            {
                failure = playerId != 0
                    ? $"Player pointer not ready (playerId=0x{playerId:X8})."
                    : "Player pointer not ready.";
                return false;
            }

            IntPtr vtable = Marshal.ReadIntPtr(player);
            if (!IsPointerInModule(vtable))
            {
                failure = $"Player vtable looks invalid (0x{vtable.ToInt32():X8}).";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return false;
        }
    }

    public static bool IsPointerInModule(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero || _moduleBase == IntPtr.Zero || _imageSize <= 0)
            return false;

        int value = ptr.ToInt32();
        int start = _moduleBase.ToInt32();
        int end = start + _imageSize;
        return value >= start && value < end;
    }

    private static IEnumerable<IntPtr> EnumerateCandidates()
    {
        if (_smartboxStaticAddr != IntPtr.Zero)
            yield return _smartboxStaticAddr;

        foreach (IntPtr candidate in _smartboxStaticCandidates)
        {
            if (candidate == _smartboxStaticAddr)
                continue;

            yield return candidate;
        }
    }

    private static void CollectSmartBoxCandidates(byte[] text, int textBaseVa, int imageBase)
    {
        _smartboxStaticCandidates.Clear();

        HashSet<int> seen = [];
        AddCandidates(FindCmdInterpReferenceCandidates(text, imageBase), seen);
        AddCandidates(FindCleanupPatternCandidates(text, textBaseVa, imageBase), seen);
    }

    private static void AddCandidates(IEnumerable<IntPtr> candidates, HashSet<int> seen)
    {
        foreach (IntPtr candidate in candidates)
        {
            int value = candidate.ToInt32();
            if (value == 0 || !seen.Add(value))
                continue;

            _smartboxStaticCandidates.Add(candidate);
        }
    }

    private static IEnumerable<IntPtr> FindCleanupPatternCandidates(byte[] text, int textBaseVa, int imageBase)
    {
        int dataLo = imageBase + 0x400000;
        int dataHi = imageBase + 0x510000;

        for (int i = 0; i < text.Length - 12; i++)
        {
            if (text[i] != 0xC7 || text[i + 1] != 0x05)
                continue;

            int addr = BitConverter.ToInt32(text, i + 2);
            if (addr < dataLo || addr > dataHi)
                continue;

            if (BitConverter.ToInt32(text, i + 6) != 0 || text[i + 10] != 0xC3)
                continue;

            byte[] loadPat = new byte[5];
            loadPat[0] = 0xA1;
            BitConverter.GetBytes(addr).CopyTo(loadPat, 1);

            for (int back = 5; back < 40; back++)
            {
                int j = i - back;
                if (j < 0)
                    break;

                if (!PatternScanner.VerifyBytes(text, j, loadPat))
                    continue;

                for (int k = j + 5; k < j + 10 && k < text.Length - 1; k++)
                {
                    if (text[k] != 0x85 || text[k + 1] != 0xC0)
                        continue;

                    bool hasDtorCall = false;
                    for (int m = k; m < i - 2 && m < text.Length - 3; m++)
                    {
                        if (text[m] == 0x6A && text[m + 1] == 0x01 &&
                            text[m + 2] == 0xFF && text[m + 3] == 0x10)
                        {
                            hasDtorCall = true;
                            break;
                        }
                    }

                    if (hasDtorCall)
                    {
                        yield return new IntPtr(addr);
                        break;
                    }
                }
            }
        }
    }

    private static IEnumerable<IntPtr> FindCmdInterpReferenceCandidates(byte[] text, int imageBase)
    {
        int dataLo = imageBase + 0x400000;
        int dataHi = imageBase + 0x510000;

        for (int i = 0; i < text.Length - 24; i++)
        {
            if (text[i] != 0xA1)
                continue;

            int addr = BitConverter.ToInt32(text, i + 1);
            if (addr < dataLo || addr > dataHi)
                continue;

            bool hasNullCheck = false;
            for (int k = i + 5; k < i + 10 && k < text.Length - 1; k++)
            {
                if (text[k] == 0x85 && text[k + 1] == 0xC0)
                {
                    hasNullCheck = true;
                    break;
                }
            }

            if (!hasNullCheck)
                continue;

            for (int k = i + 5; k < i + 24 && k < text.Length - 6; k++)
            {
                if (text[k] != 0x8B)
                    continue;

                byte modrm = text[k + 1];
                if ((modrm & 0xC7) != 0x80)
                    continue;

                if (BitConverter.ToInt32(text, k + 2) != SmartBoxCmdInterpOffset)
                    continue;

                yield return new IntPtr(addr);
                break;
            }
        }
    }

    private static void Reset()
    {
        _smartboxStaticAddr = IntPtr.Zero;
        _moduleBase = IntPtr.Zero;
        _imageSize = 0;
        _smartboxStaticCandidates.Clear();
        IsInitialized = false;
        _statusMessage = "Not probed yet.";
    }
}

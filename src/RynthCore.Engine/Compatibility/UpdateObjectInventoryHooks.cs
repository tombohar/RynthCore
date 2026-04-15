using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class UpdateObjectInventoryHooks
{
    private const int UpdateObjectInventoryExpectedVa = 0x0055A190;
    private static readonly byte[] UpdateObjectInventorySignature =
    [
        0x8B, 0x44, 0x24, 0x04, 0x50, 0xE8, 0x96, 0xE7,
        0xFA, 0xFF, 0x8B, 0x4C, 0x24, 0x08, 0x51, 0x8D,
        0x48, 0x3C, 0xE8, 0x69, 0xFF, 0xFF, 0xFF, 0xC2,
        0x08, 0x00
    ];

    // Derived from UpdateObjectInventory disasm: lea ecx, [eax+0x3C]
    // The instruction at sig offset 15 (8D 48 3C) encodes the inventory offset.
    private const int WeenieObjectInventoryOffset = 0x3C;

    // CObjectInventory layout from Chorizite:
    //   LongHashData (12) + IDList _itemsList + IDList _containersList + ...
    private const int ObjectInventoryItemsListOffset = 0x0C;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void UpdateObjectInventoryDelegate(IntPtr thisPtr, uint objectId, IntPtr newInventory);

    private static UpdateObjectInventoryDelegate? _originalUpdateObjectInventory;
    private static UpdateObjectInventoryDelegate? _updateObjectInventoryDetour;
    private static IntPtr _targetAddress;
    private static string _statusMessage = "Not probed yet.";

    public static bool IsInstalled { get; private set; }
    public static string StatusMessage => _statusMessage;

    public static void Initialize()
    {
        if (IsInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        try
        {
            int updateFuncOff = ResolveUpdateObjectInventoryOffset(textSection.Bytes, textSection.TextBaseVa, out bool updateUsedScan);
            if (updateFuncOff < 0)
            {
                _statusMessage = "ACCObjectMaint::UpdateObjectInventory signature not found.";
                RynthLog.Compat($"Compat: update-object-inventory hook failed - {_statusMessage}");
                return;
            }

            _targetAddress = new IntPtr(textSection.TextBaseVa + updateFuncOff);
            _updateObjectInventoryDetour = UpdateObjectInventoryDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_updateObjectInventoryDetour);
            _originalUpdateObjectInventory = Marshal.GetDelegateForFunctionPointer<UpdateObjectInventoryDelegate>(
                MinHook.HookCreate(_targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(_targetAddress);

            IsInstalled = true;
            _statusMessage =
                $"Hooked ACCObjectMaint::UpdateObjectInventory @ 0x{_targetAddress.ToInt32():X8}" +
                $"{(updateUsedScan ? " (pattern)" : string.Empty)}.";
            RynthLog.Compat($"Compat: update-object-inventory hook ready - {_statusMessage}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: update-object-inventory hook failed - {ex.Message}");
        }
    }

    private static int ResolveUpdateObjectInventoryOffset(byte[] text, int textBaseVa, out bool usedPatternScan)
    {
        usedPatternScan = false;

        int expectedOff = UpdateObjectInventoryExpectedVa - textBaseVa;
        if (PatternScanner.VerifyBytes(text, expectedOff, UpdateObjectInventorySignature))
            return expectedOff;

        int scannedOff = PatternScanner.FindPattern(text, UpdateObjectInventorySignature);
        if (scannedOff >= 0)
        {
            usedPatternScan = true;
            return scannedOff;
        }

        return -1;
    }

    private static void UpdateObjectInventoryDetour(IntPtr thisPtr, uint objectId, IntPtr newInventory)
    {
        _originalUpdateObjectInventory!(thisPtr, objectId, newInventory);
        if (objectId == 0)
            return;

        PluginManager.QueueUpdateObjectInventory(objectId);
    }

    public static unsafe int GetContainerContents(uint containerId, uint* itemIds, int maxCount)
    {
        if (itemIds == null || maxCount <= 0)
            return 0;

        return GetContainerContents(containerId, new Span<uint>(itemIds, maxCount));
    }

    private static bool _hasLoggedVtableScan;

    /// <summary>
    /// Scan weenie object memory in 4-byte steps, logging every in-module pointer (vtable).
    /// This reveals the sub-object layout — each vtable pointer marks a base class or embedded object.
    /// </summary>
    private static void LogWeenieVtableScan(uint containerId, IntPtr weeniePtr)
    {
        if (_hasLoggedVtableScan) return;
        _hasLoggedVtableScan = true;

        RynthLog.Compat($"Compat: === Vtable scan of weenie 0x{containerId:X8} at 0x{weeniePtr.ToInt32():X8} ===");
        for (int off = 0; off < 0x80; off += 4)
        {
            try
            {
                IntPtr addr = weeniePtr + off;
                if (!IsReadablePointer(addr))
                {
                    RynthLog.Compat($"Compat:   +0x{off:X2} UNREADABLE");
                    continue;
                }

                IntPtr val = Marshal.ReadIntPtr(addr);
                bool inModule = SmartBoxLocator.IsPointerInModule(val);
                bool readable = val != IntPtr.Zero && IsReadablePointer(val);

                if (inModule)
                    RynthLog.Compat($"Compat:   +0x{off:X2} = 0x{val.ToInt32():X8} [VTABLE - in module]");
                else if (readable && val.ToInt32() > 0x10000)
                    RynthLog.Compat($"Compat:   +0x{off:X2} = 0x{val.ToInt32():X8} [heap ptr]");
                else
                    RynthLog.Compat($"Compat:   +0x{off:X2} = 0x{val.ToInt32():X8}");
            }
            catch
            {
                RynthLog.Compat($"Compat:   +0x{off:X2} ACCESS VIOLATION");
                break;
            }
        }

        // Also log what's at the current offset we're using
        try
        {
            IntPtr invPtr = Marshal.ReadIntPtr(weeniePtr + WeenieObjectInventoryOffset);
            RynthLog.Compat($"Compat: Current WeenieObjectInventoryOffset=0x{WeenieObjectInventoryOffset:X2} -> 0x{invPtr.ToInt32():X8} readable={IsReadablePointer(invPtr)}");
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: Failed to read at inventory offset: {ex.Message}");
        }
    }

    private static int GetContainerContents(uint containerId, Span<uint> itemIds)
    {
        if (containerId == 0 || itemIds.Length == 0)
            return 0;

        // Brute-force scan: iterate known object IDs and check containerID ownership.
        // The embedded CObjectInventory at weenie+0x3C is often empty (especially for
        // the player object), so we scan all objects and match by ownership instead.
        return ScanByContainerId(containerId, itemIds);
    }

    /// <summary>
    /// Scan a range of object IDs, calling GetWeenieObject for each, and return
    /// those whose containerID matches the target. This discovers items that were
    /// created before the plugin loaded (e.g. pack contents at login).
    /// </summary>
    private static int ScanByContainerId(uint containerId, Span<uint> itemIds)
    {
        int written = 0;

        // Scan dynamic object range (0x80000001 – 0x8000FFFF)
        for (uint id = 0x80000001; id <= 0x8000FFFF && written < itemIds.Length; id++)
        {
            if (id == containerId) continue;
            if (!ClientObjectHooks.TryGetObjectOwnershipInfo(id, out uint cid, out _, out _))
                continue;
            if (cid == containerId)
                itemIds[written++] = id;
        }

        return written;
    }

    private static int WalkIdList(uint containerId, IntPtr idList, Span<uint> dest, int expectedCount, string label)
    {
        IntPtr node = Marshal.ReadIntPtr(idList + 0x08); // first node
        int written = 0;
        int walked = 0;

        while (node != IntPtr.Zero && written < expectedCount && written < dest.Length && walked < 2048)
        {
            walked++;
            uint itemId = unchecked((uint)Marshal.ReadInt32(node));
            if (itemId != 0)
            {
                dest[written++] = itemId;
            }

            node = Marshal.ReadIntPtr(node + 0x08); // next node
        }

        return written;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
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

    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;

    private static bool IsReadablePointer(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return false;

        if (VirtualQuery(ptr, out MEMORY_BASIC_INFORMATION mbi, Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0)
            return false;

        if (mbi.State != MemCommit)
            return false;

        return (mbi.Protect & (PageNoAccess | PageGuard)) == 0;
    }
}

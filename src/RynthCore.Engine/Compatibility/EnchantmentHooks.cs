using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Reads the player's active spell enchantments from the CEnchantmentRegistry.
///
/// Hookless approach: reads the registry directly from the player's CACQualities struct.
///
/// Path: PlayerVitalsHooks.KnownPlayerQualitiesPtr → CACQualities+0x70 → CEnchantmentRegistry*
/// Offset 0x70 confirmed by disassembling CACQualities::EnchantAttribute2nd (0x0058FEC0):
///   8B 49 70 = mov ecx, [ecx+0x70]  ; reads _enchantment_reg from this (CACQualities*)
///
/// CEnchantmentRegistry layout (from Chorizite + PDB):
///   +0  PackObj (vtable, 4 bytes)
///   +4  PackableList&lt;Enchantment&gt;* _mult_list
///   +8  PackableList&lt;Enchantment&gt;* _add_list
///   +12 PackableList&lt;Enchantment&gt;* _cooldown_list
///   +16 Enchantment* _vitae
///   +20 uint m_cHelpfulEnchantments
///   +24 uint m_cHarmfulEnchantments
///
/// Enchantment struct (sizeof=72, pack 4):
///   +0  PackObj (vtable)     +4  _id (uint)
///   +8  m_SpellSetID         +12 _spell_category
///   +16 _power_level         +20 _start_time (double)
///   +28 _duration (double)   +36 _caster (uint)
///   +40 _degrade_modifier    +44 _degrade_limit
///   +48 _last_time_degraded  +56 StatMod(16 bytes)
///
/// PackableLLNode&lt;Enchantment&gt;: data(72) + next*(4) + prev*(4)
/// </summary>
internal static class EnchantmentHooks
{
    // Offset of _enchantment_reg within CACQualities
    // Confirmed by disassembly: mov ecx, [ecx+0x70] at 0x0058FEC0
    private const int QualitiesRegistryOffset = 0x70;

    // CEnchantmentRegistry offsets
    private const int RegistryMultListOffset     = 4;
    private const int RegistryAddListOffset      = 8;
    private const int RegistryCooldownListOffset = 12;

    // PackableList<Enchantment>: +0=vtable, +4=head, +8=tail, +12=curNum
    private const int ListHeadOffset = 4;

    // Enchantment fields within each PackableLLNode
    // MSVC x86 pack(8): doubles are 8-byte aligned, inserting 4 bytes padding after _power_level
    private const int EnchantmentIdOffset        = 4;   // _id (uint)
    private const int EnchantmentStartTimeOffset = 24;  // _start_time (double) — padded from +20 to +24
    private const int EnchantmentDurationOffset  = 32;  // _duration (double)
    // sizeof(Enchantment) = 80 with MSVC padding; PackableLLNode has next* and prev* after data
    private const int NodeNextOffset             = 80;  // next*

    private static bool _initialized;
    private static bool _loggedFirstRead;

    public static bool IsInitialized => _initialized;

    public static bool Initialize()
    {
        if (_initialized) return true;

        _initialized = true;
        RynthLog.Compat("Compat: enchantment reader ready (hookless, CACQualities+0x70)");
        return true;
    }

    /// <summary>
    /// Reads active enchantments from the player's CEnchantmentRegistry.
    /// Returns count written, 0 if no enchantments, -1 if player qualities not available.
    /// Expiry times are server-time seconds (subtract current server time for remaining).
    /// </summary>
    public static unsafe int ReadPlayerEnchantments(uint* spellIds, double* expiryTimes, int maxCount)
    {
        if (maxCount <= 0) return 0;

        // Get player's CACQualities pointer (set by login hooks)
        IntPtr qualPtr = PlayerVitalsHooks.KnownPlayerQualitiesPtr;
        if (qualPtr == IntPtr.Zero) return -1;

        int count = ReadEnchantmentsFromQualities(qualPtr, spellIds, expiryTimes, maxCount);

        if (!_loggedFirstRead && count > 0)
        {
            _loggedFirstRead = true;
            RynthLog.Compat($"[EnchRead] first player read: {count} enchantments");
        }

        return count;
    }

    /// <summary>
    /// Reads active enchantments from any game object's CEnchantmentRegistry.
    /// Path: GetWeenieObject(objectId) → weenie+qualitiesOffset → CACQualities+0x70.
    /// Returns count written, 0 if no enchantments, -1 if object not found or has no registry.
    ///
    /// Uses VirtualQuery to validate every pointer before dereferencing — critical because
    /// AccessViolationException cannot be caught in NativeAOT/.NET 5+.
    /// </summary>
    public static unsafe int ReadObjectEnchantments(uint objectId, uint* spellIds, double* expiryTimes, int maxCount)
    {
        if (maxCount <= 0) return 0;

        if (!ClientObjectHooks.TryGetWeenieObjectPtr(objectId, out IntPtr weeniePtr))
            return -1;

        // Navigate weenie → CACQualities via the probed/fallback offset
        IntPtr qualAddr = weeniePtr + ClientObjectHooks.WeenieQualitiesOffset;
        if (!SmartBoxLocator.IsMemoryReadable(qualAddr, 4))
            return -1;
        IntPtr qualPtr = Marshal.ReadIntPtr(qualAddr);
        if (qualPtr == IntPtr.Zero) return -1;

        // Validate: CACQualities inherits PackObj → vtable must point into acclient.exe.
        if (!SmartBoxLocator.IsMemoryReadable(qualPtr, 4))
            return -1;
        IntPtr vtable = Marshal.ReadIntPtr(qualPtr);
        if (!SmartBoxLocator.IsPointerInModule(vtable))
            return -1;

        // Validate the enchantment registry pointer
        IntPtr regAddr = qualPtr + QualitiesRegistryOffset;
        if (!SmartBoxLocator.IsMemoryReadable(regAddr, 4))
            return -1;
        IntPtr regPtr = Marshal.ReadIntPtr(regAddr);
        if (regPtr == IntPtr.Zero) return 0;

        // Validate registry vtable
        if (!SmartBoxLocator.IsMemoryReadable(regPtr, 4))
            return -1;
        IntPtr regVtable = Marshal.ReadIntPtr(regPtr);
        if (!SmartBoxLocator.IsPointerInModule(regVtable))
            return -1;

        return ReadEnchantmentsFromQualities(qualPtr, spellIds, expiryTimes, maxCount);
    }

    /// <summary>
    /// Core: reads enchantments from a CACQualities pointer's enchantment registry.
    /// </summary>
    private static unsafe int ReadEnchantmentsFromQualities(IntPtr qualPtr, uint* spellIds, double* expiryTimes, int maxCount)
    {
        IntPtr regAddr = qualPtr + QualitiesRegistryOffset;
        if (!SmartBoxLocator.IsMemoryReadable(regAddr, 4))
            return -1;
        IntPtr registryPtr = Marshal.ReadIntPtr(regAddr);

        if (registryPtr == IntPtr.Zero) return 0;

        int count = 0;
        count = WalkEnchantList(registryPtr + RegistryMultListOffset,     spellIds, expiryTimes, maxCount, count);
        count = WalkEnchantList(registryPtr + RegistryAddListOffset,      spellIds, expiryTimes, maxCount, count);
        count = WalkEnchantList(registryPtr + RegistryCooldownListOffset, spellIds, expiryTimes, maxCount, count);
        return count;
    }

    private static unsafe int WalkEnchantList(IntPtr listPtrAddress, uint* spellIds, double* expiryTimes, int maxCount, int count)
    {
        if (!SmartBoxLocator.IsMemoryReadable(listPtrAddress, 4))
            return count;
        IntPtr listPtr = Marshal.ReadIntPtr(listPtrAddress);
        if (listPtr == IntPtr.Zero) return count;

        if (!SmartBoxLocator.IsMemoryReadable(listPtr + ListHeadOffset, 4))
            return count;
        IntPtr nodePtr = Marshal.ReadIntPtr(listPtr + ListHeadOffset);

        int guard = 0;
        while (nodePtr != IntPtr.Zero && guard++ < 512 && count < maxCount)
        {
            // Validate entire node is readable before accessing any field
            if (!SmartBoxLocator.IsMemoryReadable(nodePtr, NodeNextOffset + 4))
                break;

            // _id field is enchantment ID: (layer << 16) | spellId — mask to get spell ID
            uint spellId    = unchecked((uint)Marshal.ReadInt32(nodePtr + EnchantmentIdOffset)) & 0xFFFF;
            long startBits  = Marshal.ReadInt64(nodePtr + EnchantmentStartTimeOffset);
            long durBits    = Marshal.ReadInt64(nodePtr + EnchantmentDurationOffset);
            double start    = BitConverter.Int64BitsToDouble(startBits);
            double duration = BitConverter.Int64BitsToDouble(durBits);

            if (spellId != 0)
            {
                spellIds[count]    = spellId;
                expiryTimes[count] = duration > 0 ? start + duration : double.MaxValue;
                count++;
            }

            nodePtr = Marshal.ReadIntPtr(nodePtr + NodeNextOffset);
        }

        return count;
    }
}

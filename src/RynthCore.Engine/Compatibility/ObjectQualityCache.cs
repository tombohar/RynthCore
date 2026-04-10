using System;
using System.Collections.Generic;

namespace RynthCore.Engine.Compatibility;

internal readonly record struct CreatureVitals(
    uint Health, uint MaxHealth,
    uint Stamina, uint MaxStamina,
    uint Mana, uint MaxMana);

/// <summary>
/// Lightweight cache for creature vitals keyed by CWeenieObject pointer.
/// Populated from IdentifyObject (0xC9) CreatureProfile responses so that
/// QueryHealthResponse can resolve absolute health values, and plugins can
/// query target stamina/mana.
/// </summary>
internal static class ObjectQualityCache
{
    private static readonly Dictionary<IntPtr, uint> _maxHealthByPtr = new();
    private static readonly Dictionary<uint, CreatureVitals> _vitalsByObjectId = new();
    private static readonly object _lock = new();

    public static void SetMaxHealth(IntPtr objectPtr, uint maxHealth)
    {
        if (objectPtr == IntPtr.Zero)
            return;
        lock (_lock)
            _maxHealthByPtr[objectPtr] = maxHealth;
    }

    public static bool TryGetMaxHealth(IntPtr objectPtr, out uint maxHealth)
    {
        if (objectPtr == IntPtr.Zero)
        {
            maxHealth = 0;
            return false;
        }
        lock (_lock)
            return _maxHealthByPtr.TryGetValue(objectPtr, out maxHealth);
    }

    public static void SetCreatureVitals(uint objectId, CreatureVitals vitals)
    {
        if (objectId == 0)
            return;
        lock (_lock)
            _vitalsByObjectId[objectId] = vitals;
    }

    public static bool TryGetCreatureVitals(uint objectId, out CreatureVitals vitals)
    {
        if (objectId == 0)
        {
            vitals = default;
            return false;
        }
        lock (_lock)
            return _vitalsByObjectId.TryGetValue(objectId, out vitals);
    }
}

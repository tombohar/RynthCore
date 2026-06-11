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
    // NOTE: a former _maxHealthByPtr (keyed by reusable CWeenieObject heap
    // pointers, populated per stat-update from an UnmanagedCallersOnly detour)
    // was removed 2026-06-11: it was write-only — zero readers — pure
    // allocation churn in a detour plus an unbounded pointer-keyed dictionary.
    private static readonly Dictionary<uint, CreatureVitals> _vitalsByObjectId = new();
    private static readonly object _lock = new();

    // Bound the cache: ids accumulate for the whole session otherwise. The
    // cache is advisory (resolvers fall back to ratio-only); a rare wholesale
    // clear is cheaper and safer than LRU bookkeeping inside detour paths.
    private const int MaxEntries = 4096;

    public static void SetCreatureVitals(uint objectId, CreatureVitals vitals)
    {
        if (objectId == 0)
            return;
        lock (_lock)
        {
            if (_vitalsByObjectId.Count >= MaxEntries && !_vitalsByObjectId.ContainsKey(objectId))
                _vitalsByObjectId.Clear();
            _vitalsByObjectId[objectId] = vitals;
        }
    }

    /// <summary>Drop all cached vitals at logout — object ids don't survive the session.</summary>
    public static void ClearSession()
    {
        lock (_lock)
            _vitalsByObjectId.Clear();
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

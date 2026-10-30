// ============================================================================
//  RynthCore.Engine - Compatibility/PropertyCaches.cs
//
//  One lookup over the engine's property caches, in the order every
//  TryGetObject*Property uses them (2026-09-30). Pure dictionary reads under
//  their owners' locks: safe on any thread, never touches AC.
//
//    the player:  PlayerDescription + private updates (PropertyUpdateHooks self
//                 record), then public updates about the player, then the
//                 client's own qualities tables (ClientObjectHooks' 1 s
//                 main-thread snapshot), then the player's last identify;
//    any other:   its last identify (with later updates folded in), then the
//                 public updates the server sent for it.
//
//  What the client knows beyond these (PublicWeenieDesc fields, AC's live
//  qualities on the main thread) stays with ClientObjectHooks.
// ============================================================================

namespace RynthCore.Engine.Compatibility;

internal static class PropertyCaches
{
    private static bool IsPlayer(uint objectId)
    {
        if (objectId == 0) return false;
        uint me = ClientHelperHooks.GetPlayerId();
        return me != 0 && me == objectId;
    }

    public static bool TryGetInt(uint objectId, uint stype, out int value)
    {
        if (IsPlayer(objectId))
        {
            if (PropertyUpdateHooks.TryGetSelfInt(stype, out value)) return true;
            if (PropertyUpdateHooks.TryGetCachedIntProperty(objectId, stype, out value)) return true;
            if (ClientObjectHooks.PlayerQualitiesSnapshot(objectId)?.Ints.TryGetValue(stype, out value) == true) return true;
            return AppraisalHooks.TryGetCachedIntProperty(objectId, stype, out value);
        }
        return AppraisalHooks.TryGetCachedIntProperty(objectId, stype, out value)
            || PropertyUpdateHooks.TryGetCachedIntProperty(objectId, stype, out value);
    }

    public static bool TryGetInt64(uint objectId, uint stype, out long value)
    {
        if (IsPlayer(objectId))
        {
            if (PropertyUpdateHooks.TryGetSelfInt64(stype, out value)) return true;
            if (PropertyUpdateHooks.TryGetCachedInt64Property(objectId, stype, out value)) return true;
            if (ClientObjectHooks.PlayerQualitiesSnapshot(objectId)?.Int64s.TryGetValue(stype, out value) == true) return true;
            return AppraisalHooks.TryGetCachedInt64Property(objectId, stype, out value);
        }
        return AppraisalHooks.TryGetCachedInt64Property(objectId, stype, out value)
            || PropertyUpdateHooks.TryGetCachedInt64Property(objectId, stype, out value);
    }

    public static bool TryGetBool(uint objectId, uint stype, out bool value)
    {
        if (IsPlayer(objectId))
        {
            if (PropertyUpdateHooks.TryGetSelfBool(stype, out value)) return true;
            if (PropertyUpdateHooks.TryGetCachedBoolProperty(objectId, stype, out value)) return true;
            if (ClientObjectHooks.PlayerQualitiesSnapshot(objectId)?.Bools.TryGetValue(stype, out value) == true) return true;
            return AppraisalHooks.TryGetCachedBoolProperty(objectId, stype, out value);
        }
        return AppraisalHooks.TryGetCachedBoolProperty(objectId, stype, out value)
            || PropertyUpdateHooks.TryGetCachedBoolProperty(objectId, stype, out value);
    }

    public static bool TryGetFloat(uint objectId, uint stype, out double value)
    {
        if (IsPlayer(objectId))
        {
            if (PropertyUpdateHooks.TryGetSelfFloat(stype, out value)) return true;
            if (PropertyUpdateHooks.TryGetCachedDoubleProperty(objectId, stype, out value)) return true;
            if (ClientObjectHooks.PlayerQualitiesSnapshot(objectId)?.Floats.TryGetValue(stype, out value) == true) return true;
            return AppraisalHooks.TryGetCachedDoubleProperty(objectId, stype, out value);
        }
        return AppraisalHooks.TryGetCachedDoubleProperty(objectId, stype, out value)
            || PropertyUpdateHooks.TryGetCachedDoubleProperty(objectId, stype, out value);
    }

    public static bool TryGetString(uint objectId, uint stype, out string value)
    {
        if (IsPlayer(objectId))
        {
            if (PropertyUpdateHooks.TryGetSelfString(stype, out value)) return true;
            if (PropertyUpdateHooks.TryGetCachedStringProperty(objectId, stype, out value)) return true;
            if (ClientObjectHooks.PlayerQualitiesSnapshot(objectId) is { } q && q.Strings.TryGetValue(stype, out string? qs)) { value = qs; return true; }
            return AppraisalHooks.TryGetCachedStringProperty(objectId, stype, out value);
        }
        return AppraisalHooks.TryGetCachedStringProperty(objectId, stype, out value)
            || PropertyUpdateHooks.TryGetCachedStringProperty(objectId, stype, out value);
    }

    public static bool TryGetDataId(uint objectId, uint stype, out uint value)
    {
        if (IsPlayer(objectId))
        {
            if (PropertyUpdateHooks.TryGetSelfDataId(stype, out value)) return true;
            if (PropertyUpdateHooks.TryGetCachedDataIdProperty(objectId, stype, out value)) return true;
            if (ClientObjectHooks.PlayerQualitiesSnapshot(objectId)?.DataIds.TryGetValue(stype, out value) == true) return true;
            return AppraisalHooks.TryGetCachedDataIdProperty(objectId, stype, out value);
        }
        return AppraisalHooks.TryGetCachedDataIdProperty(objectId, stype, out value)
            || PropertyUpdateHooks.TryGetCachedDataIdProperty(objectId, stype, out value);
    }

    public static bool TryGetInstanceId(uint objectId, uint stype, out uint value)
    {
        if (IsPlayer(objectId))
        {
            if (PropertyUpdateHooks.TryGetSelfInstanceId(stype, out value)) return true;
            if (PropertyUpdateHooks.TryGetCachedInstanceIdProperty(objectId, stype, out value)) return true;
            if (ClientObjectHooks.PlayerQualitiesSnapshot(objectId)?.InstanceIds.TryGetValue(stype, out value) == true) return true;
            return AppraisalHooks.TryGetCachedInstanceIdProperty(objectId, stype, out value);
        }
        return PropertyUpdateHooks.TryGetCachedInstanceIdProperty(objectId, stype, out value)
            || AppraisalHooks.TryGetCachedInstanceIdProperty(objectId, stype, out value);
    }
}

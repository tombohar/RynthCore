// ============================================================================
//  RynthCore.Engine - Compatibility/ObjectPropertyDump.cs
//
//  Plugin API v84 (GetObjectProperties): gathers every property the engine
//  knows for one object and encodes it (ObjectPropertyRecord). Sources:
//    - the CreateObject copy (PublicWeenieDesc snapshot, ~0.1 s old);
//    - the client's own qualities tables (CBaseQualities), walked on AC's main
//      thread (live there; off it, requested for the next main-thread tick);
//    - the last identify (all six tables + profile values, spell book);
//    - the server's public property updates, and for the player its own record
//      (PlayerDescription + private updates) and the 1 s qualities snapshot.
//  Off the main thread nothing here touches AC memory: every source is a copy.
// ============================================================================

using System;

namespace RynthCore.Engine.Compatibility;

internal static class ObjectPropertyDump
{
    /// <summary>The record for <paramref name="objectId"/>, or null when the engine knows nothing about it.</summary>
    public static byte[]? Collect(uint objectId)
    {
        if (objectId == 0)
            return null;

        uint me = ClientHelperHooks.GetPlayerId();
        bool isPlayer = me != 0 && me == objectId;
        ObjectPropertyFlags flags = isPlayer ? ObjectPropertyFlags.IsPlayer : ObjectPropertyFlags.None;

        var createData = new PropertyBag();
        bool hasCreate = ClientObjectHooks.CopyCreateDataProperties(objectId, createData);
        if (hasCreate) flags |= ObjectPropertyFlags.HasCreateData;

        PropertyBag? qualities = ClientObjectHooks.GetObjectQualitiesForDump(objectId, out bool pending, out bool read);
        if (read) flags |= ObjectPropertyFlags.QualitiesRead;
        if (pending) flags |= ObjectPropertyFlags.QualitiesPending;
        if (isPlayer && qualities == null)
            qualities = ClientObjectHooks.PlayerQualitiesSnapshot(objectId);   // never changed once published

        AppraisalHooks.TryCopyIdentify(objectId, out PropertyBag? identify, out uint[]? spells);
        PropertyBag? updates = PropertyUpdateHooks.CopyPublicUpdates(objectId);
        PropertyBag? self = isPlayer ? PropertyUpdateHooks.CopySelf() : null;

        if (AppraisalHooks.HasAppraisalData(objectId)) flags |= ObjectPropertyFlags.HasIdData;
        long lastId = AppraisalHooks.GetLastIdTime(objectId);

        PropertyBag all = ObjectPropertyRecord.Combine(isPlayer, hasCreate ? createData : null, qualities, identify, updates, self);
        if (all.Count == 0 && (spells == null || spells.Length == 0) && !hasCreate && !pending)
            return null;
        return ObjectPropertyRecord.Encode(all, spells, flags, lastId);
    }
}

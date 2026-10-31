// ============================================================================
//  RynthCore.Headless - Host/HostApi.Objects.cs
//  The objects / properties slots of the API table: names, positions, types, flags,
//  every property kind (Host/Objects/ObjectProps.cs has the lookup order and the
//  CreateObject header fallbacks), ownership and containers, the live id list,
//  identify (request, appraisal data, spell book, target vitals), heading / physics
//  state / door motion, server age and ghost removal, the v84 property record,
//  container order and what the player wields.
//
//  Engine conventions (its off-thread answers): 1/0, out-pointers written only on
//  success, 0 / -1 for "no context" or "unknown", nothing thrown back at a plugin.
// ============================================================================

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Headless.Host.Objects;
using RynthCore.Headless.Messages;
using RynthCore.Headless.World;
using RynthCore.PluginSdk;

namespace RynthCore.Headless.Host;

internal static unsafe partial class HostApi
{
    // ANSI slots (HostContext.Ansi) owned by this area.
    private const int AnsiObjectName = 100, AnsiStringProperty = 101;

    static partial void FillObjects(RynthCoreApiNative* api)
    {
        api->GetObjectNameFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, IntPtr>)&ObjGetName;
        api->GetObjectPositionFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, float*, float*, float*, int>)&ObjGetPosition;
        api->GetItemTypeFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int>)&ObjGetItemType;
        api->ObjectIsAttackableFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&ObjIsAttackable;
        api->GetObjectBitfieldFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint>)&ObjGetBitfield;
        api->GetObjectWcidFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint>)&ObjGetWcid;

        api->GetObjectIntPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int*, int>)&ObjGetInt;
        api->GetObjectBoolPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, int*, int>)&ObjGetBool;
        api->GetObjectDoublePropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, double*, int>)&ObjGetDouble;
        api->GetObjectQuadPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, long*, int>)&ObjGetQuad;
        api->GetObjectStringPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, IntPtr>)&ObjGetString;
        api->GetObjectDataIdPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, uint*, int>)&ObjGetDataId;
        api->GetObjectInstanceIdPropertyFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, uint*, int>)&ObjGetInstanceId;

        api->GetObjectWielderInfoFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, uint*, int>)&ObjGetWielderInfo;
        api->GetObjectOwnershipInfoFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, uint*, uint*, int>)&ObjGetOwnershipInfo;
        api->GetContainerContentsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int, int>)&ObjGetContainerContents;
        api->GetNumContainedItemsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&ObjNumContainedItems;
        api->GetNumContainedContainersFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&ObjNumContainedContainers;
        api->GetLiveObjectIdsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, int, int>)&ObjGetLiveIds;

        api->RequestIdFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&ObjRequestId;
        api->HasAppraisalDataFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&ObjHasAppraisalData;
        api->GetLastIdTimeFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, long>)&ObjGetLastIdTime;
        api->GetObjectSpellIdsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int, int>)&ObjGetSpellIds;
        api->GetTargetVitalsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, uint*, uint*, uint*, uint*, uint*, int>)&ObjGetTargetVitals;

        api->GetObjectHeadingFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, float*, int>)&ObjGetHeading;
        api->GetObjectStateFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int>)&ObjGetState;
        api->GetObjectMotionOnFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int*, int>)&ObjGetMotionOn;
        api->GetObjectServerAgeFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&ObjGetServerAge;
        api->RemoveClientObjectFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int>)&ObjRemoveClientObject;
        api->GetObjectPropertiesFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, byte*, int, int>)&ObjGetProperties;
        api->GetObjectPalettesFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, uint*, int, int>)&ObjGetPalettes;
        api->GetContainerOrderFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int, uint*, int, int>)&ObjGetContainerOrder;
        api->GetWieldedItemsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, uint*, int, int>)&ObjGetWieldedItems;
    }

    /// <summary>The object, when there is a context and the world model has it.</summary>
    private static WorldObject? ObjFind(uint id, out HostContext? c)
    {
        c = Ctx;
        return c == null || id == 0 ? null : c.World.Get(id);
    }

    /// <summary>The object, only once the server created it (not a stub an inventory event made).</summary>
    private static WorldObject? ObjCreated(uint id, out HostContext? c)
    {
        WorldObject? o = ObjFind(id, out c);
        return o != null && ObjectProps.Created(o) ? o : null;
    }

    // ── identity ────────────────────────────────────────────────────────────

    /// <summary>The object's name (host-owned ANSI, valid until the next call on this thread); 0 when unknown.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr ObjGetName(uint id)
    {
        try
        {
            WorldObject? o = ObjFind(id, out _);
            if (o == null || !ObjectProps.TryGetString(o, 1, out string name)) return IntPtr.Zero;
            return HostContext.Ansi(AnsiObjectName, name);
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>The landcell and cell-local x, y, z; 0 for an object with no place in the world (in a pack, wielded).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetPosition(uint id, uint* cell, float* x, float* y, float* z)
    {
        try
        {
            if (cell == null || x == null || y == null || z == null) return 0;
            if (ObjFind(id, out _)?.Position is not { } p) return 0;
            *cell = p.Cell; *x = p.X; *y = p.Y; *z = p.Z;
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>ItemType flags: the header's, else an identify's (int 1); an object with neither answers 0.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetItemType(uint id, uint* flags)
    {
        try
        {
            if (flags == null) return 0;
            WorldObject? o = ObjFind(id, out _);
            if (o == null) return 0;
            uint t = o.Desc.ItemType;
            if (t == 0 && o.TryGetInt(1, out int it)) t = (uint)it;
            if (t == 0 && !ObjectProps.Created(o)) return 0;
            *flags = t;
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>See <see cref="ObjectProps.IsAttackable"/>: the Attackable bit, never the player, other players only PK vs PK.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjIsAttackable(uint id)
    {
        try
        {
            WorldObject? o = ObjCreated(id, out HostContext? c);
            return o != null && ObjectProps.IsAttackable(o, c!.World.PlayerObject) ? 1 : 0;
        }
        catch { return 0; }
    }

    /// <summary>ObjectDescriptionFlags from the header; 0 when unknown.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint ObjGetBitfield(uint id)
    {
        try { return ObjCreated(id, out _)?.Desc.DescriptionFlags ?? 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint ObjGetWcid(uint id)
    {
        try { return ObjCreated(id, out _)?.Desc.WeenieClassId ?? 0; }
        catch { return 0; }
    }

    // ── properties ──────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetInt(uint id, uint stype, int* value)
    {
        try
        {
            if (value == null) return 0;
            WorldObject? o = ObjFind(id, out _);
            if (o == null || !ObjectProps.TryGetInt(o, stype, out int v)) return 0;
            *value = v;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetBool(uint id, uint stype, int* value)
    {
        try
        {
            if (value == null) return 0;
            WorldObject? o = ObjFind(id, out _);
            if (o == null || !ObjectProps.TryGetBool(o, stype, out bool v)) return 0;
            *value = v ? 1 : 0;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetDouble(uint id, uint stype, double* value)
    {
        try
        {
            if (value == null) return 0;
            WorldObject? o = ObjFind(id, out _);
            if (o == null || !ObjectProps.TryGetFloat(o, stype, out double v)) return 0;
            *value = v;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetQuad(uint id, uint stype, long* value)
    {
        try
        {
            if (value == null) return 0;
            WorldObject? o = ObjFind(id, out _);
            if (o == null || !ObjectProps.TryGetInt64(o, stype, out long v)) return 0;
            *value = v;
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>Host-owned ANSI (valid until the next call on this thread); 0 when unknown or empty.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr ObjGetString(uint id, uint stype)
    {
        try
        {
            WorldObject? o = ObjFind(id, out _);
            if (o == null || !ObjectProps.TryGetString(o, stype, out string v)) return IntPtr.Zero;
            return HostContext.Ansi(AnsiStringProperty, v);
        }
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetDataId(uint id, uint stype, uint* value)
    {
        try
        {
            if (value == null) return 0;
            WorldObject? o = ObjFind(id, out _);
            if (o == null || !ObjectProps.TryGetDataId(o, stype, out uint v)) return 0;
            *value = v;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetInstanceId(uint id, uint stype, uint* value)
    {
        try
        {
            if (value == null) return 0;
            WorldObject? o = ObjFind(id, out _);
            if (o == null || !ObjectProps.TryGetInstanceId(o, stype, out uint v)) return 0;
            *value = v;
            return 1;
        }
        catch { return 0; }
    }

    // ── ownership and containers ────────────────────────────────────────────

    /// <summary>Wielder and EquipMask location (both 0 when not wielded); 0 for an unknown object.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetWielderInfo(uint id, uint* wielder, uint* location)
    {
        try
        {
            if (wielder == null || location == null) return 0;
            WorldObject? o = ObjFind(id, out _);
            if (o == null) return 0;
            *wielder = o.WielderId;
            *location = o.WieldedLocation;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetOwnershipInfo(uint id, uint* container, uint* wielder, uint* location)
    {
        try
        {
            if (container == null || wielder == null || location == null) return 0;
            WorldObject? o = ObjFind(id, out _);
            if (o == null) return 0;
            *container = o.ContainerId;
            *wielder = o.WielderId;
            *location = o.WieldedLocation;
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>
    /// The ids whose container OR wielder is <paramref name="containerId"/> (a pack's items, a creature's
    /// gear), ascending; writes at most maxCount (the lowest ids) and returns how many it wrote.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetContainerContents(uint containerId, uint* ids, int maxCount)
    {
        try
        {
            if (ids == null || maxCount <= 0 || containerId == 0) return 0;
            HostContext? c = Ctx;
            if (c == null) return 0;
            var list = new List<uint>();
            foreach (WorldObject o in c.World.Objects.Values)
                if (o.Id != containerId && (o.ContainerId == containerId || o.WielderId == containerId)) list.Add(o.Id);
            list.Sort();
            int n = Math.Min(list.Count, maxCount);
            for (int i = 0; i < n; i++) ids[i] = list[i];
            return n;
        }
        catch { return 0; }
    }

    /// <summary>Ordinary items directly inside (the player: the main pack's item list); -1 for an unknown object.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjNumContainedItems(uint id)
    {
        try { return ObjContainedCount(id, containers: false); }
        catch { return -1; }
    }

    /// <summary>Packs and foci directly inside (the player: the pack column); -1 for an unknown object.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjNumContainedContainers(uint id)
    {
        try { return ObjContainedCount(id, containers: true); }
        catch { return -1; }
    }

    private static int ObjContainedCount(uint id, bool containers)
    {
        WorldObject? o = ObjFind(id, out HostContext? c);
        if (o == null) return -1;
        PlayerState p = c!.World.Player;
        if (id == p.Id && p.DescriptionReceived)
            return containers ? p.SidePackOrder.Count : p.MainPackOrder.Count;
        // Packs and foci take a pack slot (foci are no containers: RequiresPackSlot 0x800000 marks them).
        int n = 0;
        foreach (WorldObject x in c.World.Objects.Values)
            if (x.Id != id && x.ContainerId == id && (x.IsContainer || (x.Desc.DescriptionFlags & 0x800000) != 0) == containers) n++;
        return n;
    }

    /// <summary>v69: every object the server created (ascending); returns the total, copying up to capacity.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetLiveIds(uint* buffer, int capacity)
    {
        try
        {
            HostContext? c = Ctx;
            if (c == null) return 0;
            var list = new List<uint>(c.World.Objects.Count);
            foreach (WorldObject o in c.World.Objects.Values)
                if (ObjectProps.Created(o)) list.Add(o.Id);
            list.Sort();
            if (buffer != null && capacity > 0)
            {
                int n = Math.Min(capacity, list.Count);
                for (int i = 0; i < n; i++) buffer[i] = list[i];
            }
            return list.Count;
        }
        catch { return 0; }
    }

    // ── identify ────────────────────────────────────────────────────────────

    /// <summary>Sends an identify (game action 0xC8) while in the world; 1 sent.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjRequestId(uint id)
    {
        try
        {
            HostContext? c = Ctx;
            if (c == null || id == 0 || c.Session.State != Session.SessionState.InWorld) return 0;
            c.Session.Identify(id);
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>1 once an identify reply (successful or not) came for the object this session.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjHasAppraisalData(uint id)
    {
        try { return Ctx?.HasAppraisalData(id) == true ? 1 : 0; }
        catch { return 0; }
    }

    /// <summary>Unix seconds of the last identify reply; 0 never.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static long ObjGetLastIdTime(uint id)
    {
        try { return Ctx?.LastIdTime(id) ?? 0; }
        catch { return 0; }
    }

    /// <summary>The last identify's spell book: the total count (copying up to maxCount), -1 when there is none.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetSpellIds(uint id, uint* spellIds, int maxCount)
    {
        try
        {
            if (spellIds == null || maxCount <= 0) return -1;
            uint[]? spells = Ctx?.IdentifySpells(id);
            if (spells == null || spells.Length == 0) return -1;
            int n = Math.Min(spells.Length, maxCount);
            for (int i = 0; i < n; i++) spellIds[i] = spells[i];
            return spells.Length;
        }
        catch { return -1; }
    }

    /// <summary>A creature's vitals from its identify's creature profile (health kept current by UpdateHealth); 0 when none.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetTargetVitals(uint id, uint* health, uint* maxHealth, uint* stamina, uint* maxStamina, uint* mana, uint* maxMana)
    {
        try
        {
            if (health == null || maxHealth == null || stamina == null || maxStamina == null || mana == null || maxMana == null) return 0;
            HostContext? c = Ctx;
            if (c == null || id == 0 || !c.TryGetTargetVitals(id, out HostContext.TargetVitals v)) return 0;
            *health = v.Health; *maxHealth = v.MaxHealth;
            *stamina = v.Stamina; *maxStamina = v.MaxStamina;
            *mana = v.Mana; *maxMana = v.MaxMana;
            return 1;
        }
        catch { return 0; }
    }

    // ── physics ─────────────────────────────────────────────────────────────

    /// <summary>Compass heading 0..360 (0 north, clockwise) from the object's rotation; 0 with no position.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetHeading(uint id, float* heading)
    {
        try
        {
            if (heading == null) return 0;
            if (ObjFind(id, out _)?.Position is not { } p) return 0;
            *heading = ObjectProps.Heading(p.Qw, p.Qz);
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>PhysicsState (CreateObject, then SetState: Ethereal 0x4 = an open door).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetState(uint id, uint* state)
    {
        try
        {
            if (state == null) return 0;
            WorldObject? o = ObjCreated(id, out _);
            if (o == null) return 0;
            *state = o.PhysicsState;
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>A door's / lever's last On / Off motion from UpdateMotion; 0 when none was seen.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetMotionOn(uint id, int* isOn)
    {
        try
        {
            if (isOn == null) return 0;
            HostContext? c = Ctx;
            if (c == null || !c.TryGetMotionOn(id, out bool on)) return 0;
            *isOn = on ? 1 : 0;
            return 1;
        }
        catch { return 0; }
    }

    /// <summary>v83: ms since the server's last message about the object (capped); -1 none seen.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetServerAge(uint id)
    {
        try
        {
            HostContext? c = Ctx;
            if (c == null || id == 0) return -1;
            long age = c.ServerAgeMs(id);
            return age < 0 ? -1 : (int)Math.Min(age, int.MaxValue);
        }
        catch { return -1; }
    }

    /// <summary>v83: drops a ghost creature (1 removed, 0 refused; -1 without a context).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjRemoveClientObject(uint id)
    {
        try
        {
            HostContext? c = Ctx;
            return c == null ? -1 : c.RemoveClientObject(id);
        }
        catch { return 0; }
    }

    /// <summary>v84: the property record's size (copied when it fits); 0 unknown object; -1 bad arguments.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetProperties(uint id, byte* buffer, int size)
    {
        try
        {
            if (id == 0 || size < 0 || (buffer == null && size > 0)) return -1;
            WorldObject? o = ObjFind(id, out HostContext? c);
            if (o == null) return 0;
            byte[]? rec = ObjectProps.Record(o, id == c!.World.Player.Id, c.HasAppraisalData(id), c.LastIdTime(id), c.IdentifySpells(id));
            if (rec == null) return 0;
            if (rec.Length <= size) rec.AsSpan().CopyTo(new Span<byte>(buffer, size));
            return rec.Length;
        }
        catch { return -1; }
    }

    /// <summary>No ObjDesc palettes are kept (CreateObject's model data is skipped): always -1, "no data".</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetPalettes(uint id, uint* subIds, uint* offsets, int maxCount) => -1;

    /// <summary>
    /// v80: the player's main pack in server slot order (slotKind 0: ordinary items; 1: side packs
    /// and foci). -1 for any other container (no full list kept) or bad arguments.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetContainerOrder(uint containerId, int slotKind, uint* ids, int maxIds)
    {
        try
        {
            if (containerId == 0 || slotKind is < 0 or > 1 || maxIds < 0 || (ids == null && maxIds > 0)) return -1;
            HostContext? c = Ctx;
            if (c == null) return -1;
            PlayerState p = c.World.Player;
            if (containerId != p.Id || !p.DescriptionReceived) return -1;
            IReadOnlyList<uint> order = slotKind == 1 ? p.SidePackOrder : p.MainPackOrder;
            int n = Math.Min(order.Count, maxIds);
            for (int i = 0; i < n; i++) ids[i] = order[i];
            return order.Count;
        }
        catch { return -1; }
    }

    /// <summary>
    /// v87: what the player wields and where (EquipMask): PlayerDescription's equipped list kept by
    /// WieldObject / PutObjInContainer / PutObjectIn3D, plus anything the world model shows wielded by
    /// the player in no container; ascending by id. -1 with no player yet or bad arguments.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ObjGetWieldedItems(uint* ids, uint* locations, int max)
    {
        try
        {
            if (ids == null || locations == null || max <= 0) return -1;
            HostContext? c = Ctx;
            if (c == null) return -1;
            PlayerState p = c.World.Player;
            if (p.Id == 0 || !p.DescriptionReceived) return -1;
            var all = new SortedDictionary<uint, uint>();
            foreach (var kv in p.EquippedItems) all[kv.Key] = kv.Value;
            foreach (WorldObject o in c.World.Objects.Values)
                if (o.WielderId == p.Id && o.ContainerId == 0 && o.WieldedLocation != 0 && o.Id != p.Id)
                    all.TryAdd(o.Id, o.WieldedLocation);
            int n = 0;
            foreach (var kv in all)
            {
                if (n >= max) break;
                ids[n] = kv.Key;
                locations[n] = kv.Value;
                n++;
            }
            return n;
        }
        catch { return -1; }
    }
}

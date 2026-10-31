// ============================================================================
//  RynthCore.Headless - World/WorldState.cs
//  The client's picture of the world, kept from the server's messages: the object
//  table, the player's state, and the results of the player's actions (use done,
//  errors, attack done, combat notices). ClientSession hands every message to Apply.
//  Single-threaded (the session's tick).
// ============================================================================

using System.Buffers.Binary;
using RynthCore.Engine.Compatibility;
using RynthCore.Headless.Messages;
using RynthCore.PluginSdk.Net;

namespace RynthCore.Headless.World;

public sealed class WorldState
{
    private readonly Dictionary<uint, WorldObject> _objects = new();
    private readonly Func<long> _now;

    internal WorldState(Func<long> nowMs) => _now = nowMs;

    public PlayerState Player { get; } = new();
    public IReadOnlyDictionary<uint, WorldObject> Objects => _objects;

    /// <summary>
    /// The player's run rate as the server uses it (the RunForward speed in its echo of our
    /// own motion); ground speed is 4 m/s times this. Null until the server has echoed a run.
    /// </summary>
    public float? PlayerRunRate { get; private set; }

    /// <summary>The container the player last opened (ViewContents), 0 if none.</summary>
    public uint OpenContainer { get; internal set; }

    public event Action<WorldObject>? ObjectCreated;
    public event Action<WorldObject>? ObjectUpdated;
    /// <summary>SetState changed an object's physics state (doors opening/closing, ...).</summary>
    public event Action<WorldObject>? ObjectStateChanged;
    public event Action<uint>? ObjectDeleted;
    /// <summary>UseDone: 0 = success, else a WeenieError code.</summary>
    public event Action<uint>? UseDone;
    public event Action<uint>? AttackDone;
    /// <summary>WeenieError / WeenieErrorWithString (code, text or "").</summary>
    public event Action<uint, string>? Error;
    /// <summary>InventoryServerSaveFailed (item, error).</summary>
    public event Action<uint, uint>? InventoryFailed;
    public event Action<CombatNotice>? Combat;
    /// <summary>An identify reply arrived (object, success).</summary>
    public event Action<uint, bool>? Identified;
    public event Action<Enchantment>? EnchantmentAdded;
    public event Action<ushort>? EnchantmentRemoved;
    public event Action<string>? TransientMessage;
    public event Action? PlayerDescribed;
    /// <summary>The server started moving the player to a target (MoveToObject / MoveToPosition motion).</summary>
    public event Action<MotionUpdate>? PlayerMoveTo;
    public event Action<uint>? ContainerOpened;
    /// <summary>CloseGroundContainer: the server stopped showing us a container.</summary>
    public event Action<uint>? ContainerClosed;
    /// <summary>Motion / vector / position news about an object (not the player's own echoes).</summary>
    public event Action<uint>? ObjectMoved;
    /// <summary>UpdateHealth (game event 0x01C0): object, health fraction.</summary>
    public event Action<uint, float>? HealthUpdated;
    /// <summary>The server teleported the player (PlayerTeleport 0xF751).</summary>
    public event Action? PlayerTeleported;
    /// <summary>The server moved the player (teleport or a position correction).</summary>
    public event Action<WorldPosition>? PlayerCorrected;
    /// <summary>An UpdateMotion's interpreted state (movement type 0) for any object, the player's own included.</summary>
    public event Action<uint, InterpretedMotion>? ObjectMotion;
    /// <summary>An identify reply's spell book and creature profile; raised just before <see cref="Identified"/>.</summary>
    public event Action<IdentifyDetails>? IdentifyDetailed;

    public WorldObject? Get(uint id) => _objects.TryGetValue(id, out WorldObject? o) ? o : null;
    public WorldObject? PlayerObject => Get(Player.Id);

    /// <summary>Items inside <paramref name="container"/> (direct children).</summary>
    public IEnumerable<WorldObject> Contents(uint container) => _objects.Values.Where(o => o.ContainerId == container);

    /// <summary>Everything the player carries: the main pack, side packs and their contents, and wielded items.</summary>
    public IEnumerable<WorldObject> Inventory()
    {
        uint me = Player.Id;
        var packs = new HashSet<uint> { me };
        foreach (WorldObject o in _objects.Values)
            if (o.ContainerId == me && o.IsContainer) packs.Add(o.Id);
        return _objects.Values.Where(o => o.Id != me && (packs.Contains(o.ContainerId) || o.WielderId == me));
    }

    /// <summary>Straight-line distance in metres between two objects in the same landblock (null otherwise).</summary>
    public float? Distance(uint a, uint b)
    {
        WorldPosition? pa = Get(a)?.Position, pb = Get(b)?.Position;
        if (pa is not { } x || pb is not { } y) return null;
        return Geometry.Distance(x, y);
    }

    internal void Apply(ServerMessage msg)
    {
        ReadOnlySpan<byte> b = msg.Body;
        long now = _now();
        switch (msg.Opcode)
        {
            case Opcode.ObjectCreate:
            case WorldOpcode.UpdateObject:
                if (!ObjectCreate.TryParse(b, out ObjectCreateData d)) return;
                // New if never seen, or only known as a placeholder (from a ViewContents / inventory
                // event that arrived before the object's own CreateObject, as ACE sends corpse contents).
                bool isNew = !_objects.TryGetValue(d.Id, out WorldObject? o) || !o.Described;
                o ??= new WorldObject(d.Id);
                o.Apply(d, now);
                _objects[d.Id] = o;
                if (d.Id == Player.Id) break;
                if (isNew) ObjectCreated?.Invoke(o); else ObjectUpdated?.Invoke(o);
                break;

            case Opcode.ObjectDelete:
                if (WorldMessages.TryU32(b, out uint del) && _objects.Remove(del))
                {
                    Player.RemoveFromPack(del);
                    Player.Equipped.Remove(del);
                    ObjectDeleted?.Invoke(del);
                }
                break;

            case Opcode.PlayerCreate:
                if (WorldMessages.TryU32(b, out uint pid))
                {
                    Player.Id = pid;
                    OnPlayerKnown();
                }
                break;

            case WorldOpcode.UpdatePosition:
                if (WorldMessages.TryUpdatePosition(b, out PositionUpdate pu) && _objects.TryGetValue(pu.Id, out WorldObject? po))
                {
                    // Our own position: the server echoes what we reported (already behind us
                    // while we walk). Take it only when it is a teleport or a correction (the
                    // teleport / force-position sequence moved), or when we have none yet.
                    bool mine = pu.Id == Player.Id;
                    bool correction = pu.TeleportSeq != po.TeleportSeq || pu.ForcePositionSeq != po.ForcePositionSeq;
                    if (!mine || correction || po.Position == null)
                    {
                        po.Position = pu.Position;
                        if (mine && correction) PlayerCorrected?.Invoke(pu.Position);
                    }
                    po.Vx = pu.Vx; po.Vy = pu.Vy; po.Vz = pu.Vz;
                    po.InstanceSeq = pu.InstanceSeq;
                    po.PositionSeq = pu.PositionSeq;
                    po.TeleportSeq = pu.TeleportSeq;
                    po.ForcePositionSeq = pu.ForcePositionSeq;
                    po.UpdatedMs = now;
                    if (!mine) ObjectMoved?.Invoke(pu.Id);
                }
                break;

            case WorldOpcode.UpdateMotion:
                if (WorldMessages.TryUpdateMotion(b, out MotionUpdate mu) && _objects.TryGetValue(mu.Id, out WorldObject? mo))
                {
                    mo.Stance = mu.Stance;
                    mo.ServerControlSeq = mu.ServerControlSeq;
                    if (mu.Id != Player.Id) ObjectMoved?.Invoke(mu.Id);
                    if (mu.Id == Player.Id && !mu.Autonomous && mu.MovementType is 6 or 7)
                        PlayerMoveTo?.Invoke(mu);
                    if (mu.Id == Player.Id && MotionMessages.TryInterpreted(b, out InterpretedMotion im)
                        && im.ForwardCommand == MotionCommand.RunForward && im.ForwardSpeed > 0.5f && im.ForwardSpeed < 10f)
                        PlayerRunRate = im.ForwardSpeed;
                    if (ObjectMotion != null && MotionMessages.TryInterpreted(b, out InterpretedMotion om)) ObjectMotion(mu.Id, om);
                }
                break;

            case Opcode.PlayerTeleport:
                if (ForgetOnTeleport) ForgetSurroundings();
                PlayerTeleported?.Invoke();
                break;

            case WorldOpcode.VectorUpdate:
                if (WorldMessages.TryU32(b, out uint vid) && vid != Player.Id && _objects.ContainsKey(vid)) ObjectMoved?.Invoke(vid);
                break;

            case WorldOpcode.SetState:
                if (WorldMessages.TryU32U32(b, out uint sid, out uint state) && _objects.TryGetValue(sid, out WorldObject? so))
                {
                    so.PhysicsState = state;
                    ObjectStateChanged?.Invoke(so);
                }
                break;

            case WorldOpcode.PickupEvent:
                // The object left the 3D world (picked up by someone).
                if (WorldMessages.TryU32(b, out uint picked) && _objects.TryGetValue(picked, out WorldObject? pk))
                    pk.Position = null;
                break;

            case WorldOpcode.ParentEvent:
                if (WorldMessages.TryU32U32(b, out uint parent, out uint child) && _objects.TryGetValue(child, out WorldObject? co))
                {
                    co.WielderId = parent;
                    co.Position = null;
                }
                break;

            case WorldOpcode.SetStackSize:
                if (WorldMessages.TrySetStackSize(b, out uint stackId, out uint size, out uint value) && _objects.TryGetValue(stackId, out WorldObject? st))
                {
                    st.StackSize = size;
                    st.Value = (int)value;
                    ObjectUpdated?.Invoke(st);
                }
                break;

            case WorldOpcode.InventoryRemoveObject:
                if (WorldMessages.TryU32(b, out uint gone))
                {
                    _objects.Remove(gone);
                    Player.RemoveFromPack(gone);
                    Player.Equipped.Remove(gone);
                    ObjectDeleted?.Invoke(gone);
                }
                break;

            case WorldOpcode.PrivateUpdateAttribute:
                if (WorldMessages.TryPrivateAttribute(b, out uint attr, out AttributeInfo ai)) Player.Attributes[attr] = ai;
                break;

            case WorldOpcode.PrivateUpdateVital:
                if (WorldMessages.TryPrivateVital(b, out uint vital, out VitalInfo vi)) Player.Vitals[vital] = vi;
                break;

            case WorldOpcode.PrivateUpdateVitalCurrent:
                if (WorldMessages.TryPrivateVitalCurrent(b, out uint vc, out uint cur) && vc is >= 1 and <= 3)
                {
                    Player.Vitals.TryGetValue(vc, out VitalInfo old);
                    Player.Vitals[vc] = old with { Current = cur };
                }
                break;

            case WorldOpcode.PrivateUpdateSkill:
                if (WorldMessages.TryPrivateSkill(b, out SkillInfo si)) Player.Skills[si.Id] = si;
                break;

            case Opcode.GameEvent:
                ApplyEvent(msg, now);
                break;

            default:
                if (PropertyWire.IsPropertyUpdateOpcode(msg.Opcode))
                    ApplyProperty(msg);
                break;
        }
    }

    private void ApplyProperty(ServerMessage msg)
    {
        // PropertyWire reads from the opcode on.
        Span<byte> whole = new byte[msg.Body.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(whole, msg.Opcode);
        msg.Body.CopyTo(whole.Slice(4));
        if (!PropertyWire.TryParsePropertyUpdate(whole, out PropertyUpdate u)) return;
        uint id = u.IsPrivate ? Player.Id : u.ObjectId;
        if (!_objects.TryGetValue(id, out WorldObject? o)) return;
        switch (u.Kind)
        {
            case PropertyKind.Int: o.Ints[u.Key] = (int)u.Integer; break;
            case PropertyKind.Int64: o.Int64s[u.Key] = u.Integer; break;
            case PropertyKind.Bool: o.Bools[u.Key] = u.Integer != 0; break;
            case PropertyKind.Float: o.Floats[u.Key] = u.Real; break;
            case PropertyKind.String: o.Strings[u.Key] = u.Text ?? ""; break;
            case PropertyKind.DataId: o.DataIds[u.Key] = (uint)u.Integer; break;
            case PropertyKind.InstanceId:
                o.InstanceIds[u.Key] = (uint)u.Integer;
                if (u.Key == 2) o.ContainerId = (uint)u.Integer;      // Container
                if (u.Key == 3) o.WielderId = (uint)u.Integer;        // Wielder
                break;
        }
        if (id != Player.Id) ObjectUpdated?.Invoke(o);
    }

    private void ApplyEvent(ServerMessage msg, long now)
    {
        ReadOnlySpan<byte> e = msg.EventBody;
        var r = new NetReader(e);
        switch (msg.EventType)
        {
            case PlayerDescription.EventType:
                if (!PlayerDescription.TryParse(e, out PlayerDescriptionData pd)) return;
                ApplyDescription(pd);
                break;

            case WorldEvent.PutObjInContainer:
                if (r.U32(out uint item) && r.U32(out uint container) && r.I32(out int slot) && r.U32(out uint ctype))
                {
                    WorldObject o = GetOrStub(item);
                    o.ContainerId = container;
                    o.WielderId = 0;
                    o.WieldedLocation = 0;
                    o.Position = null;
                    Player.Equipped.Remove(item);
                    if (container == Player.Id)
                    {
                        Player.RemoveFromPack(item);
                        List<uint> list = ctype == 0 ? Player.MainPack : Player.SidePacks;
                        list.Insert(Math.Clamp(slot, 0, list.Count), item);
                    }
                    else
                    {
                        Player.RemoveFromPack(item);
                    }
                    ObjectUpdated?.Invoke(o);
                }
                break;

            case WorldEvent.WieldObject:
                if (r.U32(out uint wItem) && r.I32(out int loc))
                {
                    WorldObject o = GetOrStub(wItem);
                    o.WielderId = Player.Id;
                    o.ContainerId = 0;
                    o.WieldedLocation = (uint)loc;
                    Player.RemoveFromPack(wItem);
                    Player.Equipped[wItem] = (uint)loc;
                    ObjectUpdated?.Invoke(o);
                }
                break;

            case WorldEvent.PutObjectIn3D:
                if (r.U32(out uint dropped) && _objects.TryGetValue(dropped, out WorldObject? dObj))
                {
                    dObj.ContainerId = 0;
                    dObj.WielderId = 0;
                    Player.RemoveFromPack(dropped);
                    Player.Equipped.Remove(dropped);
                    ObjectUpdated?.Invoke(dObj);
                }
                break;

            case WorldEvent.ViewContents:
                if (r.U32(out uint vc) && r.ListCount(out int n, 8))
                {
                    for (int i = 0; i < n; i++)
                        if (r.U32(out uint cid) && r.U32(out _)) GetOrStub(cid).ContainerId = vc;
                    OpenContainer = vc;
                    ContainerOpened?.Invoke(vc);
                }
                break;

            case WorldEvent.CloseGroundContainer:
                if (r.U32(out uint closed))
                {
                    if (closed == OpenContainer) OpenContainer = 0;
                    ContainerClosed?.Invoke(closed);
                }
                break;

            case WorldEvent.IdentifyObjectResponse:
            {
                Span<byte> whole = new byte[e.Length + 4];
                BinaryPrimitives.WriteUInt32LittleEndian(whole, msg.EventType);
                e.CopyTo(whole.Slice(4));
                if (!PropertyWire.TryParseIdentify(whole, out IdentifyRecord rec)) return;
                if (_objects.TryGetValue(rec.ObjectId, out WorldObject? io) && rec.Success)
                {
                    PropertyBag bag = rec.Properties;
                    foreach (var kv in bag.Ints) io.Ints[kv.Key] = kv.Value;
                    foreach (var kv in bag.Int64s) io.Int64s[kv.Key] = kv.Value;
                    foreach (var kv in bag.Bools) io.Bools[kv.Key] = kv.Value;
                    foreach (var kv in bag.Floats) io.Floats[kv.Key] = kv.Value;
                    foreach (var kv in bag.Strings) io.Strings[kv.Key] = kv.Value;
                    foreach (var kv in bag.DataIds) io.DataIds[kv.Key] = kv.Value;
                    foreach (var kv in bag.InstanceIds) io.InstanceIds[kv.Key] = kv.Value;
                    io.Identified = true;
                }
                if (IdentifyDetailsReader.TryParse(whole, out IdentifyDetails det))
                {
                    if (io != null && rec.Success && det.HasSpellBook) io.SpellBook = det.Spells;
                    IdentifyDetailed?.Invoke(det);
                }
                Identified?.Invoke(rec.ObjectId, rec.Success);
                break;
            }

            case WorldEvent.UpdateHealth:
                if (r.U32(out uint hid) && r.F32(out float ratio))
                {
                    if (_objects.TryGetValue(hid, out WorldObject? ho)) ho.HealthRatio = ratio;
                    HealthUpdated?.Invoke(hid, ratio);
                }
                break;

            case WorldEvent.UseDone:
                if (r.U32(out uint ud)) UseDone?.Invoke(ud);
                break;

            case WorldEvent.AttackDone:
                if (r.U32(out uint ad)) AttackDone?.Invoke(ad);
                break;

            case WorldEvent.WeenieError:
                if (r.U32(out uint we)) Error?.Invoke(we, "");
                break;

            case WorldEvent.WeenieErrorWithString:
                if (r.U32(out uint ws) && r.String16L(out string wtext)) Error?.Invoke(ws, wtext);
                break;

            case WorldEvent.InventoryServerSaveFailed:
                if (r.U32(out uint fi) && r.U32(out uint ferr)) InventoryFailed?.Invoke(fi, ferr);
                break;

            case WorldEvent.TransientString:
                if (r.String16L(out string ts)) TransientMessage?.Invoke(ts);
                break;

            case WorldEvent.AttackerNotification:
                if (r.String16L(out string dn) && r.U32(out uint dt) && r.F64(out double hp) && r.U32(out uint dmg) && r.U32(out uint crit))
                    Combat?.Invoke(new CombatNotice(msg.EventType, dn, dt, hp, dmg, crit != 0));
                break;

            case WorldEvent.DefenderNotification:
                if (r.String16L(out string an) && r.U32(out uint adt) && r.F64(out double ahp) && r.U32(out uint admg) && r.U32(out _) && r.U32(out uint acrit))
                    Combat?.Invoke(new CombatNotice(msg.EventType, an, adt, ahp, admg, acrit != 0));
                break;

            case WorldEvent.EvasionAttacker:
            case WorldEvent.EvasionDefender:
            case WorldEvent.VictimNotification:
            case WorldEvent.KillerNotification:
                if (r.String16L(out string en)) Combat?.Invoke(new CombatNotice(msg.EventType, en, 0, 0, 0, false));
                break;

            case WorldEvent.MagicUpdateSpell:
                if (r.U16(out ushort learned)) Player.Spells.Add(learned);
                break;

            case WorldEvent.MagicRemoveSpell:
                if (r.U16(out ushort forgot)) Player.Spells.Remove(forgot);
                break;

            case WorldEvent.MagicUpdateEnchantment:
                if (PlayerDescription.ReadEnchantment(ref r, out Enchantment en1))
                {
                    Player.SetEnchantment(en1);
                    EnchantmentAdded?.Invoke(en1);
                }
                break;

            case WorldEvent.MagicUpdateMultipleEnchantments:
            {
                var list = new List<Enchantment>();
                if (!PlayerDescription.ReadEnchantmentList(ref r, list)) return;
                foreach (Enchantment x in list) { Player.SetEnchantment(x); EnchantmentAdded?.Invoke(x); }
                break;
            }

            case WorldEvent.MagicRemoveEnchantment:
            case WorldEvent.MagicDispelEnchantment:
                if (WorldMessages.TryLayeredSpell(ref r, out ushort sp, out ushort layer) && Player.RemoveEnchantment(sp, layer))
                    EnchantmentRemoved?.Invoke(sp);
                break;

            case WorldEvent.MagicRemoveMultipleEnchantments:
            case WorldEvent.MagicDispelMultipleEnchantments:
                if (r.ListCount(out int rn, 4))
                    for (int i = 0; i < rn; i++)
                        if (WorldMessages.TryLayeredSpell(ref r, out ushort s2, out ushort l2) && Player.RemoveEnchantment(s2, l2))
                            EnchantmentRemoved?.Invoke(s2);
                break;

            case WorldEvent.MagicPurgeEnchantments:
                foreach (Enchantment x in Player.Enchantments.ToList())
                    if (x.SpellId != 666) { Player.RemoveEnchantment(x.SpellId, x.Layer); EnchantmentRemoved?.Invoke(x.SpellId); }
                break;

            case WorldEvent.MagicPurgeBadEnchantments:
                // Harmful = not flagged Beneficial (0x2000000).
                foreach (Enchantment x in Player.Enchantments.ToList())
                    if ((x.StatModType & 0x2000000) == 0 && x.SpellId != 666) { Player.RemoveEnchantment(x.SpellId, x.Layer); EnchantmentRemoved?.Invoke(x.SpellId); }
                break;
        }
    }

    private void ApplyDescription(PlayerDescriptionData pd)
    {
        PlayerState p = Player;
        p.Description = pd;
        foreach (var kv in pd.Attributes) p.Attributes[kv.Key] = kv.Value;
        foreach (var kv in pd.Vitals) p.Vitals[kv.Key] = kv.Value;
        foreach (var kv in pd.Skills) p.Skills[kv.Key] = kv.Value;
        p.Spells.Clear();
        p.Spells.UnionWith(pd.Spells);
        p.Enchantments.Clear();
        p.Enchantments.AddRange(pd.Enchantments);
        if (pd.InventoryFound)
        {
            p.MainPack.Clear();
            p.SidePacks.Clear();
            foreach (PackEntry it in pd.Inventory)
                (it.ContainerType == 0 ? p.MainPack : p.SidePacks).Add(it.Id);
            p.Equipped.Clear();
            foreach (EquippedEntry eq in pd.Equipped) p.Equipped[eq.Id] = eq.Location;
        }
        p.DescriptionReceived = true;

        // The player's own object gets the property tables (it may not exist yet: PlayerDescription comes first).
        if (p.Id != 0) CopyProperties(pd, GetOrStub(p.Id));
        else _pendingDescription = pd;
        PlayerDescribed?.Invoke();
    }

    private PlayerDescriptionData? _pendingDescription;

    internal void OnPlayerKnown()
    {
        if (_pendingDescription != null && Player.Id != 0)
        {
            CopyProperties(_pendingDescription, GetOrStub(Player.Id));
            _pendingDescription = null;
        }
    }

    private static void CopyProperties(PlayerDescriptionData pd, WorldObject o)
    {
        foreach (var kv in pd.Ints) o.Ints[kv.Key] = kv.Value;
        foreach (var kv in pd.Int64s) o.Int64s[kv.Key] = kv.Value;
        foreach (var kv in pd.Bools) o.Bools[kv.Key] = kv.Value;
        foreach (var kv in pd.Floats) o.Floats[kv.Key] = kv.Value;
        foreach (var kv in pd.Strings) o.Strings[kv.Key] = kv.Value;
        foreach (var kv in pd.DataIds) o.DataIds[kv.Key] = kv.Value;
        foreach (var kv in pd.InstanceIds) o.InstanceIds[kv.Key] = kv.Value;
    }

    /// <summary>
    /// On a teleport (portal, recall) the retail client drops everything around it; ACE sends no
    /// deletes for what was left behind, only creates for the new surroundings. Keeping them left
    /// ghosts: a headless bot on a 4Hives route "used" Hive 3's Brood Matron corpse from Hive 4 for half an
    /// hour (2026-10-29). Kept: the player and what it carries (packs, their contents, worn items).
    /// </summary>
    /// <summary>Drop the surroundings on a teleport, as the retail client does (on unless a test replay needs its ghosts).</summary>
    public bool ForgetOnTeleport { get; set; } = true;

    private void ForgetSurroundings()
    {
        uint me = Player.Id;
        if (me == 0) return;
        var keep = new HashSet<uint>(Inventory().Select(o => o.Id)) { me };
        foreach (uint id in _objects.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            _objects.Remove(id);
            ObjectDeleted?.Invoke(id);
        }
        OpenContainer = 0;
    }

    /// <summary>
    /// Drops an object on the client's own initiative (a ghost the server stopped talking about),
    /// as an ObjectDelete would but without raising <see cref="ObjectDeleted"/>: the caller reports
    /// it. If the server still has it, its next CreateObject brings it back.
    /// </summary>
    internal bool ForgetObject(uint id)
    {
        if (!_objects.Remove(id)) return false;
        Player.RemoveFromPack(id);
        Player.Equipped.Remove(id);
        return true;
    }

    private WorldObject GetOrStub(uint id)
    {
        if (!_objects.TryGetValue(id, out WorldObject? o))
        {
            o = new WorldObject(id);
            _objects[id] = o;
        }
        return o;
    }
}

public static class Geometry
{
    /// <summary>
    /// Metres between two positions. Outdoor cells: landblock x/y are 192 m apart, so positions
    /// in neighbouring landblocks compare correctly. Indoor cells use their landblock frame.
    /// </summary>
    public static float Distance(WorldPosition a, WorldPosition b)
    {
        float ax = a.X + (a.Cell >> 24) * 192f, ay = a.Y + ((a.Cell >> 16) & 0xFF) * 192f;
        float bx = b.X + (b.Cell >> 24) * 192f, by = b.Y + ((b.Cell >> 16) & 0xFF) * 192f;
        float dx = ax - bx, dy = ay - by, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Distance on the ground plane (ignores height).</summary>
    public static float Distance2D(WorldPosition a, WorldPosition b)
    {
        float ax = a.X + (a.Cell >> 24) * 192f, ay = a.Y + ((a.Cell >> 16) & 0xFF) * 192f;
        float bx = b.X + (b.Cell >> 24) * 192f, by = b.Y + ((b.Cell >> 16) & 0xFF) * 192f;
        return MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
    }
}

// ============================================================================
//  RynthCore.Headless - Host/HostContext.Actions.cs
//  The ACTIONS area's state and logic: what the plugin API's action slots do
//  (HostApi.Actions.cs is the thin unmanaged layer over this).
//
//  Busy count (the engine's BusyCountHooks): +1 for every use (UseObject, UseObjectOn,
//  UseEquippedItem) and cast the host sends, the acclient's m_cBusy actions, -1 for
//  every server UseDone (0x01C7), each raising OnBusyCountIncremented / Decremented.
//  Inventory sends (moves, splits, merges, gives, wields, closes) are not counted: the
//  server answers them with the item's new place or InventoryServerSaveFailed, never a
//  UseDone (the engine's raw sends don't count them either). ForceResetBusyCount drops
//  the count to 0 with one decrement event per unit; a count nobody answers for
//  BusyStaleMs is reset the same way (the engine's watchdog).
//
//  Cast gate (GetCastBusyState): 1 from a cast until its UseDone (a fizzle ends with one
//  too), at most CastGateMaxMs; there is no animation to watch here.
//
//  Use-done / weenie-error sequences: as SmartBoxHooks counts them (UseDone; WeenieError
//  0x028A, WeenieErrorWithString 0x028B, InventoryServerSaveFailed 0x00A0 with its item).
//
//  Selection: SelectItem (150 ms dedupe of the same target, like the engine) and
//  SetSelectedObjectId; a change raises OnSelectedTargetChange(current, previous); the
//  selected object leaving the world clears it.
//
//  Chat: WriteToChat prints to the log and echoes the line to the plugins as chat-window
//  text (as AC's chat window raises it), at most one line per WriteToChatMinMs like the
//  engine's off-thread path. InvokeChatParser offers the line to every plugin's
//  OnChatBarEnter (not again for a line a plugin sends from inside its own handler),
//  then sends it on (Host/Actions/ChatLineRouter.cs).
//
//  Confirmations: ArmConfirmation answers the next matching server prompt (0x0274) Yes
//  or No by its "N percent" (the engine's ConfirmationResponder); an unarmed prompt is
//  recorded as shown and left unanswered (nobody is there to click).
//
//  Every clock here is the session's (Session.NowMs).
// ============================================================================

using RynthCore.Headless.Host.Actions;
using RynthCore.Headless.Messages;
using RynthCore.Headless.Session;
using RynthCore.Headless.World;
using RynthCore.PluginSdk.Net;

namespace RynthCore.Headless.Host;

public sealed partial class HostContext
{
    public const long SelectDedupeMs = 150;
    public const long WriteToChatMinMs = 100;
    public const long CastGateMaxMs = 4000;
    public const long BusyStaleMs = 20_000;
    public const long ConfirmationMaxTimeoutMs = 120_000;

    // MergeStackStatus codes (never renumber: ABI).
    public const int MergeNone = 0, MergeQueued = 1, MergeSent = 2, MergeTargetFull = 3, MergeFailed = 4, MergeQueueFull = 5;
    // ConfirmationOutcome codes (ABI).
    public const int ConfirmNone = 0, ConfirmShown = 1, ConfirmAccepted = 2, ConfirmDeclined = 3, ConfirmSendFailed = 4;

    private int _actBusy;
    private long _actBusyChangedMs;
    private int _actUseDoneSeq;
    private uint _actUseDoneError;
    private int _actErrorSeq;
    private uint _actErrorCode, _actErrorEvent, _actErrorObject;
    private long _actCastSentMs = -1;
    private uint _actSelected, _actPrevSelected, _actLastSelectTarget;
    private long _actLastSelectMs = long.MinValue / 2;
    private long _actLastChatMs = long.MinValue / 2;
    private bool _actInChatPreDispatch;
    private readonly Dictionary<ulong, (int Status, int Amount, long At)> _actMerges = new();
    private long _actJumpChargeMs = -1;

    private bool _actArmed;
    private uint _actArmedType;
    private int _actArmedMinPercent;
    private long _actArmedUntil;
    private int _actConfirmSeq, _actConfirmPercent = -1, _actConfirmOutcome;
    private uint _actConfirmType, _actConfirmContext;
    private long _actConfirmAt;

    private long ActNow => Session.NowMs;
    private bool ActInWorld => Session.State == SessionState.InWorld && World.Player.Id != 0;

    partial void InitActions()
    {
        World.UseDone += OnActUseDone;
        World.Error += (code, text) => RecordWeenieError(text.Length > 0 ? WorldEvent.WeenieErrorWithString : WorldEvent.WeenieError, code, 0);
        World.InventoryFailed += (item, err) => RecordWeenieError(WorldEvent.InventoryServerSaveFailed, err, item);
        World.ObjectDeleted += id => { if (id != 0 && id == _actSelected) SetSelection(0); };
        Session.RawMessage += OnActRawMessage;
        Session.StateChanged += s =>
        {
            // Out of the world (logout, failure): nothing is owed any more (the engine's ResetSession).
            if (s == SessionState.InWorld) return;
            _actBusy = 0;
            _actCastSentMs = -1;
            _actSelected = _actPrevSelected = _actLastSelectTarget = 0;
            _actArmed = false;
            _actJumpChargeMs = -1;
        };
    }

    partial void TickActions()
    {
        TickPendingWield();
        if (_actBusy > 0 && ActNow - _actBusyChangedMs > BusyStaleMs)
        {
            Log($"[host] busy count {_actBusy} unanswered for {BusyStaleMs / 1000}s: reset");
            ForceResetBusy();
        }
    }

    // ── busy count, use-done, errors ────────────────────────────────────────

    public int BusyCount => _actBusy;
    public int UseDoneSeq => _actUseDoneSeq;
    public (int Seq, uint Error) LastUseDone => (_actUseDoneSeq, _actUseDoneError);
    public (int Seq, uint Error, uint EventType, uint ObjectId) LastWeenieError => (_actErrorSeq, _actErrorCode, _actErrorEvent, _actErrorObject);

    /// <summary>1 while a cast waits for its UseDone (at most CastGateMaxMs), else 0.</summary>
    public int CastBusyState => _actCastSentMs >= 0 && ActNow - _actCastSentMs < CastGateMaxMs ? 1 : 0;

    private void BusyUp()
    {
        if (TraceActions) Log($"[act] busy {_actBusy} -> {_actBusy + 1}");
        _actBusy++;
        _actBusyChangedMs = ActNow;
        Events.Enqueue(new PluginEvent.BusyIncremented());
    }

    private void OnActUseDone(uint error)
    {
        if (TraceActions) Log($"[act] UseDone 0x{error:X} (busy {_actBusy})");
        _actUseDoneSeq++;
        _actUseDoneError = error;
        _actCastSentMs = -1;
        if (_actBusy <= 0) return;   // nothing of ours to pair (a use we never counted, or reset already)
        _actBusy--;
        _actBusyChangedMs = ActNow;
        Events.Enqueue(new PluginEvent.BusyDecremented());
    }

    public void ForceResetBusy()
    {
        int was = _actBusy;
        _actBusy = 0;
        _actBusyChangedMs = ActNow;
        for (int i = 0; i < was && i < 64; i++)
            Events.Enqueue(new PluginEvent.BusyDecremented());
    }

    private void RecordWeenieError(uint eventType, uint code, uint objectId)
    {
        _actErrorSeq++;
        _actErrorCode = code;
        _actErrorEvent = eventType;
        _actErrorObject = objectId;
    }

    // ── uses, casts, combat ─────────────────────────────────────────────────

    /// <summary>Logs every action sent and every busy change (host diagnostics).</summary>
    public bool TraceActions { get; set; }

    public bool ActUse(uint obj)
    {
        if (!ActInWorld || obj == 0) return false;
        // The retail client turns "use" on equipment it carries into a wield (GetAndWieldItem) or,
        // for something already wielded, an unwield into the pack; the server never sees a Use
        // for those (and ACE would ignore one). Plugins swap weapons and wands this way.
        if (RetailUseOfEquipment(obj)) return true;
        if (RetailUseInOpenContainer(obj)) return true;
        Session.Use(obj);
        BusyUp();
        return true;
    }

    // "Use" on an item inside the open corpse or chest (or a pack in it) is a pick-up into the main pack in
    // the retail client; ACE has no use for it ("couldn't find object", and a meta's /mt loot never got
    // the item: a headless bot's 4Hives route, 2026-10-29).
    private bool RetailUseInOpenContainer(uint obj)
    {
        uint open = World.OpenContainer, me = World.Player.Id;
        if (open == 0 || me == 0 || World.Get(obj) is not { } o) return false;
        uint holder = o.ContainerId;
        if (holder != open && (holder == 0 || World.Get(holder)?.ContainerId != open)) return false;
        Session.MoveItem(obj, me, 0);
        return true;
    }

    private bool RetailUseOfEquipment(uint obj)
    {
        WorldObject? o = World.Get(obj);
        uint me = World.Player.Id;
        if (o == null || me == 0 || o.Desc.ValidLocations is not { } valid || valid == 0) return false;
        if (o.WielderId == me)
        {
            Session.MoveItem(obj, me, 0);
            return true;
        }
        uint holder = o.ContainerId;
        bool carried = holder == me || (holder != 0 && World.Get(holder)?.ContainerId == me);
        if (!carried) return false;
        // A swap: ACE refuses a weapon while another is in hand (CheckWeaponCollision), so the client
        // first puts away whatever holds the hands the new item needs, then wields it.
        uint slot = WieldSlot(valid);
        uint hands = HandsFor(slot);
        var inTheWay = new List<uint>();
        if (hands != 0)
            foreach (var (id, loc) in World.Player.EquippedItems.ToList())
                if (id != obj && (loc & hands) != 0)
                {
                    Session.MoveItem(id, me, 0);
                    inTheWay.Add(id);
                }
        if (inTheWay.Count == 0)
        {
            Session.Wield(obj, slot);
            return true;
        }
        // The wield goes out once the server has put those away (it checks the hands when the
        // wield arrives); TickActions sends it, or gives up after a few seconds.
        _actPendingWield = (obj, slot, inTheWay, ActNow + 4000);
        return true;
    }

    private (uint Item, uint Slot, List<uint> Waiting, long Deadline)? _actPendingWield;

    private void TickPendingWield()
    {
        if (_actPendingWield is not { } w) return;
        uint me = World.Player.Id;
        bool clear = w.Waiting.All(id => World.Get(id)?.WielderId != me);
        if (clear) Session.Wield(w.Item, w.Slot);
        if (clear || ActNow > w.Deadline) _actPendingWield = null;
    }

    private const uint MeleeWeapon = 0x100000, Shield = 0x200000, MissileWeapon = 0x400000, Held = 0x1000000, TwoHanded = 0x2000000;
    private const uint Weapons = MeleeWeapon | MissileWeapon | Held | TwoHanded;

    /// <summary>The wielded slots that must be emptied first: any weapon for a weapon; a shield or two-hander for a shield.</summary>
    private static uint HandsFor(uint slot)
        => (slot & Weapons) != 0 ? Weapons | ((slot & TwoHanded) != 0 ? Shield : 0)
         : (slot & Shield) != 0 ? Shield | TwoHanded
         : 0;

    /// <summary>Where a wield goes: one free side of a left/right pair (rings, bracelets), else the item's whole mask.</summary>
    private uint WieldSlot(uint valid)
    {
        uint used = 0;
        foreach (uint loc in World.Player.EquippedItems.Values) used |= loc;
        foreach (uint pair in new[] { 0xC0000u /* fingers */, 0x30000u /* wrists */ })
        {
            if ((valid & pair) != pair) continue;
            uint low = pair & ~(pair - 1), high = pair & ~low;
            return (used & low) == 0 ? low : (used & high) == 0 ? high : low;
        }
        return valid;
    }

    public bool ActUseOn(uint source, uint target)
    {
        if (!ActInWorld || source == 0 || target == 0) return false;
        Session.UseOn(source, target);
        BusyUp();
        return true;
    }

    public bool ActCast(uint target, int spellId)
    {
        if (!ActInWorld || spellId <= 0) return false;
        // A self-targeted spell (SpellFlags 0x8) goes out untargeted, as the client's
        // FreeHandsAndCastSpell sends it; anything else on its target.
        bool self = target == 0 || (target == World.Player.Id && Spells?.Get((uint)spellId) is { SelfTargeted: true });
        Session.Cast((uint)spellId, self ? 0 : target);
        _actCastSentMs = ActNow;
        BusyUp();
        return true;
    }

    public bool ActChangeCombatMode(int mode)
    {
        if (!ActInWorld || mode is not (1 or 2 or 4 or 8)) return false;
        Session.SetCombatMode((uint)mode);
        return true;
    }

    public bool ActMelee(uint target, int height, float power)
    {
        if (!ActInWorld || target == 0 || height is < 1 or > 3 || !float.IsFinite(power)) return false;
        Session.MeleeAttack(target, (uint)height, power);
        return true;
    }

    public bool ActMissile(uint target, int height, float accuracy)
    {
        if (!ActInWorld || target == 0 || height is < 1 or > 3 || !float.IsFinite(accuracy)) return false;
        Session.MissileAttack(target, (uint)height, accuracy);
        return true;
    }

    /// <summary>The client's own attack on the selected target, by combat mode (melee / missile).</summary>
    public bool ActNativeAttack(int height, float power)
    {
        if (!ActInWorld || _actSelected == 0) return false;
        if (height is < 1 or > 3) height = (int)AttackHeight.Medium;
        return CurrentCombatMode switch
        {
            2 => ActMelee(_actSelected, height, power),
            4 => ActMissile(_actSelected, height, power),
            _ => false,
        };
    }

    public bool ActCancelAttack()
    {
        if (!ActInWorld) return false;
        Session.CancelAttack();
        return true;
    }

    public bool ActQueryHealth(uint target)
    {
        if (!ActInWorld || target == 0) return false;
        Session.QueryHealth(target);
        return true;
    }

    /// <summary>In the world, not portaling and no cast in flight.</summary>
    public bool PlayerReady => ActInWorld && World.PlayerObject != null && !Portaling && CastBusyState == 0;

    // ── inventory ───────────────────────────────────────────────────────────

    /// <summary>True when <paramref name="container"/> is one of the player's own packs and it is full (the engine fails these closed).</summary>
    private bool IsFullOwnedContainer(uint container, uint item)
    {
        uint me = World.Player.Id;
        WorldObject? c = World.Get(container);
        bool ours = container == me || (c != null && c.ContainerId == me);
        if (!ours || c == null) return false;
        if (World.Get(item)?.ContainerId == container) return false;   // a move inside it
        int capacity = c.Desc.ItemsCapacity ?? 0;
        if (capacity <= 0) return false;
        int count = World.Contents(container).Count(o => !o.IsContainer);
        return count >= capacity;
    }

    private uint StackOf(uint id) => World.Get(id)?.StackSize is { } s && s > 0 ? s : 1;

    /// <summary>Whole item, or <paramref name="amount"/> of a stack split off, into a container (slot 0).</summary>
    public bool ActMoveItemExternal(uint obj, uint container, int amount)
    {
        if (!ActInWorld || obj == 0 || container == 0 || amount < 0) return false;
        if (IsFullOwnedContainer(container, obj)) return false;
        if (amount > 0 && amount < StackOf(obj)) Session.Split(obj, container, 0, amount);
        else Session.MoveItem(obj, container, 0);
        return true;
    }

    /// <summary>Whole move (0x0019) into a container at a slot.</summary>
    public bool ActMoveItemInternal(uint obj, uint container, int slot, int amount)
    {
        if (!ActInWorld || obj == 0 || container == 0 || slot < 0 || amount <= 0) return false;
        if (IsFullOwnedContainer(container, obj)) return false;
        Session.MoveItem(obj, container, slot);
        return true;
    }

    /// <summary>StackableSplitToContainer (0x0055): amount onto a slot.</summary>
    public bool ActSplitStack(uint obj, uint container, int slot, int amount)
    {
        if (!ActInWorld || obj == 0 || container == 0 || slot < 0 || amount <= 0) return false;
        Session.Split(obj, container, slot, amount);
        return true;
    }

    /// <summary>
    /// Salvage (0x027D) with a tool: the items the player carries, at most 100 (the engine's limit);
    /// the count sent, 0 refused. The server answers with the salvage it made, as for the retail panel.
    /// </summary>
    public int ActSalvageDirect(uint tool, IReadOnlyList<uint> items)
    {
        if (!ActInWorld || tool == 0 || items.Count == 0 || items.Count > 100) return 0;
        var carried = new HashSet<uint>(World.Inventory().Select(o => o.Id));
        if (!carried.Contains(tool) || items.Any(id => id == 0 || id == tool || !carried.Contains(id))) return 0;
        Session.Salvage(tool, items);
        return items.Count;
    }

    /// <summary>PutItemInContainer (0x0019) at a placement: 1 sent, 0 refused.</summary>
    public int ActPutItemInContainer(uint item, uint container, int placement)
    {
        if (!ActInWorld || item == 0 || container == 0 || placement < 0) return 0;
        Session.MoveItem(item, container, placement);
        return 1;
    }

    /// <summary>StackableMerge (0x0054) of as much of the source as fits the target; the outcome is kept for GetMergeStackResult.</summary>
    public bool ActMergeStack(uint source, uint target)
    {
        if (!ActInWorld || source == 0 || target == 0 || source == target)
        {
            RecordMerge(source, target, MergeFailed, 0);
            return false;
        }
        int sourceCount = (int)StackOf(source);
        int targetCount = (int)(World.Get(target)?.StackSize ?? 0);
        int max = MaxStackOf(source);
        if (max <= 0) max = MaxStackOf(target);
        int amount = sourceCount;
        if (max > 0)
        {
            int room = max - targetCount;
            if (room <= 0)
            {
                RecordMerge(source, target, MergeTargetFull, 0);
                return false;
            }
            amount = Math.Min(sourceCount, room);
        }
        Session.Merge(source, target, amount);
        RecordMerge(source, target, MergeSent, amount);
        return true;
    }

    private int MaxStackOf(uint id)
    {
        WorldObject? o = World.Get(id);
        if (o == null) return 0;
        if (o.Desc.MaxStackSize is { } m && m > 0) return m;
        return o.TryGetInt(11, out int v) ? v : 0;
    }

    private void RecordMerge(uint source, uint target, int status, int amount)
    {
        if (_actMerges.Count >= 512) _actMerges.Clear();
        _actMerges[((ulong)source << 32) | target] = (status, amount, ActNow);
    }

    public int MergeResult(uint source, uint target, out int amount, out int ageMs)
    {
        amount = ageMs = 0;
        if (!_actMerges.TryGetValue(((ulong)source << 32) | target, out var e)) return MergeNone;
        amount = e.Amount;
        ageMs = (int)Math.Clamp(ActNow - e.At, 0, int.MaxValue);
        return e.Status;
    }

    /// <summary>GiveObjectRequest (0x00CD); amount 0 = the whole stack.</summary>
    public bool ActGive(uint obj, uint target, int amount)
    {
        if (!ActInWorld || obj == 0 || target == 0 || amount < 0) return false;
        Session.Give(target, obj, amount == 0 ? (int)StackOf(obj) : amount);
        return true;
    }

    public bool ActWield(uint obj, uint equipMask)
    {
        if (!ActInWorld || obj == 0 || equipMask == 0) return false;
        Session.Wield(obj, equipMask);
        return true;
    }

    public bool ActCloseContainer(uint container)
    {
        if (!ActInWorld || container == 0) return false;
        Session.StopViewing(container);
        return true;
    }

    // ── selection ───────────────────────────────────────────────────────────

    public uint SelectedId => _actSelected;
    public uint PreviousSelectedId => _actPrevSelected;

    /// <summary>The engine's SelectItem: the same target again within SelectDedupeMs is a no-op (still 1).</summary>
    public bool ActSelectItem(uint obj)
    {
        if (!ActInWorld) return false;
        long now = ActNow;
        if (obj != 0 && obj == _actLastSelectTarget && now - _actLastSelectMs < SelectDedupeMs) return true;
        _actLastSelectTarget = obj;
        _actLastSelectMs = now;
        SetSelection(obj);
        return true;
    }

    public bool ActSetSelected(uint obj)
    {
        if (!ActInWorld) return false;
        SetSelection(obj);
        return true;
    }

    private void SetSelection(uint obj)
    {
        if (obj == _actSelected) return;
        _actPrevSelected = _actSelected;
        _actSelected = obj;
        Events.Enqueue(new PluginEvent.SelectedTargetChange(_actSelected, _actPrevSelected));
    }

    // ── chat ────────────────────────────────────────────────────────────────

    public bool ActWriteToChat(string? text, int chatType)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string line = text.TrimEnd('\r', '\n');
        if (line.Length == 0) return false;
        foreach (char ch in line)
            if (ch < 0x20 && ch != '\t') return false;
        long now = ActNow;
        if (now - _actLastChatMs < WriteToChatMinMs) return false;
        _actLastChatMs = now;
        Log($"[chat] {line}");
        Events.Enqueue(new PluginEvent.ChatWindowText(line, chatType));
        return true;
    }

    public bool ActInvokeChatParser(string? text)
    {
        string line = (text ?? "").TrimEnd('\r', '\n').Trim();
        if (line.Length == 0) return false;
        if (!_actInChatPreDispatch && Plugins != null)
        {
            _actInChatPreDispatch = true;
            try
            {
                if (Plugins.ChatBarEnter(line)) return true;
            }
            finally { _actInChatPreDispatch = false; }
        }
        if (!ActInWorld) return false;
        ChatRoute r = ChatLineRouter.Route(line);
        switch (r.Kind)
        {
            case ChatRouteKind.Say: Session.Say(r.Text); return true;
            case ChatRouteKind.Emote: Session.Emote(r.Text); return true;
            case ChatRouteKind.SoulEmote: Session.SoulEmote(r.Text); return true;
            case ChatRouteKind.Tell: Session.Tell(r.Target, r.Text); return true;
            case ChatRouteKind.Unsupported: Log($"[host] chat '{line}' not sent: {r.Why}"); return false;
            default: return false;
        }
    }

    // ── movement ────────────────────────────────────────────────────────────

    public bool ActSetAutoRun(bool on)
    {
        if (!ActInWorld) return false;
        Session.SetAutoRun(on);
        return true;
    }

    public bool ActSetMotion(uint motion, bool on, bool run = true)
    {
        if (!ActInWorld) return false;
        return Session.SetDriveMotion(motion, on, run);
    }

    public bool ActStopCompletely()
    {
        if (!ActInWorld) return false;
        Session.StopCompletely();
        return true;
    }

    public bool ActTurnToHeading(float degrees)
    {
        if (!ActInWorld || !float.IsFinite(degrees) || World.PlayerObject?.Position == null) return false;
        Session.FaceHeading(degrees);
        return true;
    }

    // ── jumps ───────────────────────────────────────────────────────────────

    /// <summary>A jump of <paramref name="extent"/> (0..1) with a horizontal velocity; the height follows ACE's formula from the Jump skill.</summary>
    public bool ActJump(float extent, float vx = 0, float vy = 0)
    {
        if (!ActInWorld || World.PlayerObject?.Position == null || !float.IsFinite(extent)) return false;
        extent = Math.Clamp(extent, 0f, 1f);
        uint skill = World.Player.SkillTable.TryGetValue(22, out SkillInfo s) ? s.Init + s.Ranks : 0;
        if (skill == 0) skill = 100;
        float height = Math.Max(0.35f, (skill / (skill + 1300f) * 22.2f + 0.05f) * extent);
        Session.Jump(extent, vx, vy, MathF.Sqrt(2f * 9.8f * height));
        _actJumpChargeMs = -1;
        return true;
    }

    public bool ActCommenceJump()
    {
        if (!ActInWorld) return false;
        _actJumpChargeMs = ActNow;
        return true;
    }

    /// <summary>The charge since CommenceJump (a full charge takes a second), 0 without one.</summary>
    public float JumpCharge => _actJumpChargeMs < 0 ? 0f : Math.Clamp((ActNow - _actJumpChargeMs) / 1000f, 0f, 1f);

    /// <summary>The charged jump with momentum from the keys: W forward, X back, Z / C strafe left / right (shift walks).</summary>
    public bool ActLaunchJump(bool shift, bool w, bool x, bool z, bool c)
    {
        if (World.PlayerObject?.Position is not { } p) return false;
        float h = ClientSession.HeadingDegrees(p) * MathF.PI / 180f;
        float speed = shift ? MotionCommand.WalkSpeed : Session.MoveSpeed;
        float fwd = (w ? speed : 0) - (x ? speed * ClientSession.BackwardsFactor : 0);
        float side = (c ? MotionCommand.SidestepSpeed : 0) - (z ? MotionCommand.SidestepSpeed : 0);
        float sin = MathF.Sin(h), cos = MathF.Cos(h);
        return ActJump(JumpCharge, sin * fwd + cos * side, cos * fwd - sin * side);
    }

    // ── confirmations ───────────────────────────────────────────────────────

    public int ActArmConfirmation(uint type, int minPercent, int timeoutMs)
    {
        if (timeoutMs <= 0) { _actArmed = false; return 1; }
        if (type == 0 || minPercent < 0 || minPercent > 100 || timeoutMs > ConfirmationMaxTimeoutMs) return -3;
        _actArmed = true;
        _actArmedType = type;
        _actArmedMinPercent = minPercent;
        _actArmedUntil = ActNow + timeoutMs;
        return 1;
    }

    public int LastConfirmation(out uint type, out uint context, out int percent, out int outcome, out int ageMs)
    {
        type = _actConfirmType;
        context = _actConfirmContext;
        percent = _actConfirmPercent;
        outcome = _actConfirmOutcome;
        ageMs = _actConfirmSeq == 0 ? 0 : (int)Math.Clamp(ActNow - _actConfirmAt, 0, int.MaxValue);
        return _actConfirmSeq;
    }

    private void OnActRawMessage(uint opcode, ReadOnlyMemory<byte> message)
    {
        if (opcode != ServerOpcode.GameEvent) return;
        try
        {
            if (!ServerMessage.TryFromWire(message.Span, out ServerMessage msg) || msg.EventType != GameEventType.ConfirmationRequest) return;
            if (ConfirmationRequest.TryParse(msg.EventBody, out ConfirmationRequest req))
                OnConfirmationRequest(req.ConfirmationType, req.ContextId, req.Text);
        }
        catch (Exception ex)
        {
            Log($"[host] confirmation request: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>A server yes/no prompt: answered when armed for its type, else recorded as shown.</summary>
    internal void OnConfirmationRequest(uint type, uint context, string text)
    {
        _actConfirmSeq++;
        _actConfirmType = type;
        _actConfirmContext = context;
        _actConfirmPercent = ParseConfirmPercent(text);
        _actConfirmAt = ActNow;
        _actConfirmOutcome = ConfirmShown;
        if (!_actArmed || _actArmedType != type || ActNow > _actArmedUntil) return;
        _actArmed = false;
        bool accept = _actArmedMinPercent <= 0 || (_actConfirmPercent >= 0 && _actConfirmPercent >= _actArmedMinPercent);
        if (!ActInWorld) { _actConfirmOutcome = ConfirmSendFailed; return; }
        Session.ConfirmationResponse(type, context, accept);
        _actConfirmOutcome = accept ? ConfirmAccepted : ConfirmDeclined;
        Log($"[host] confirmation type {type} ctx 0x{context:X8} percent {_actConfirmPercent}: answered {(accept ? "yes" : "no")}");
    }

    /// <summary>"... a 38 percent chance ..." -> 38; -1 when the text has no "N percent".</summary>
    public static int ParseConfirmPercent(string text)
    {
        int at = text.IndexOf(" percent", StringComparison.Ordinal);
        if (at <= 0) return -1;
        int start = at;
        while (start > 0 && char.IsAsciiDigit(text[start - 1])) start--;
        if (start == at || at - start > 3) return -1;
        return Math.Min(100, int.Parse(text.AsSpan(start, at - start)));
    }
}

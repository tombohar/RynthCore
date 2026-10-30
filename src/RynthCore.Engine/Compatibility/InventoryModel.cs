// ============================================================================
//  RynthCore.Engine - Compatibility/InventoryModel.cs
//  The data and the item actions behind the ImGui Inventory panel
//  (docs/IMGUI_INVENTORY.md). Engine only; no plugin involved.
//
//  Threads:
//    MainThreadTick  AC's main thread only (MainThreadSnapshots.Tick, from
//                    Client::UseTime and EndScene). Every AC read and every item
//                    action happens here. Idle cost when the panel is closed and
//                    nothing is in flight: one volatile read.
//    Subscribe / Unsubscribe / Hover / Request / Current / Tooltip / Pending /
//    LastResult / Target
//                    any thread (the panel calls them from its Draw). They only
//                    swap references or small values; the published objects are
//                    immutable.
//
//  One item action at a time: AC keeps one pending inventory request and refuses
//  a second ("You can only move or use one item at a time"; ClientActionGates).
//  Request() refuses while an action is waiting; nothing is ever queued behind it.
//  Several items (the panel's multi-selection) go through RequestBatch
//  (InventoryModel.Batch.cs), which feeds them to this same path one at a time,
//  each only after the one before it has ended.
//
//  The client's own gate (ClientActionGates, read on the main thread here):
//    Before the send  while AC's pending-request slot holds anyone's request (the
//                     bot looting, a retail drag), or (use/move/split, which AC
//                     gates itself) the attacking flag is set, the action is held:
//                     "Waiting for the game", sent the first tick the gate is open,
//                     dropped after GateWaitTicks.
//    After the send   move and split go through ItemHolder::AttemptToPlaceInContainer
//                     -> UIAttemptPutInContainer / SplitToContainer, which record the
//                     request in that slot. "Slot set for our object, then emptied" is
//                     the server's answer (ServerSaysMoveItem / SetStackSize /
//                     AttemptFailed all empty it), so the result is decided right
//                     then from the effect checks and any refusal since the send. A
//                     move/split that left the slot empty was not sent by AC (it
//                     refused locally and said why in chat).
//    Use, wield, drop and give don't touch the slot (use is a UseEvent; the others
//    are raw CM_Inventory sends), so they end as before: the effect is seen, the
//    server refuses it (InventoryServerSaveFailed for the item, or a use's
//    WeenieError / UseDone), the item is gone, or ActionTimeoutTicks pass.
//    Without the gate (its patterns unresolved) every action behaves that way and
//    nothing is held.
//
//  Vanishing items: every read resolves the object again on the main thread and
//  fails closed (ClientObjectHooks.TryReadItemFields), and the item and target are
//  re-checked in the same tick, right before the send. An item that disappears
//  (corpse decay, stack merge) is just absent from the next view; an action on it
//  ends as "gone".
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using RynthCore.Engine.ImGuiBackend.Hud;

namespace RynthCore.Engine.Compatibility;

/// <summary>Filter groups for the panel.</summary>
internal enum InventoryCategory : byte { Misc, Weapons, Armor, Jewelry, Usable, Components, Salvage }

/// <summary>One item as the panel shows it. Immutable; shared between views while unchanged.</summary>
internal sealed class InventoryItem
{
    public uint Id, Container, Wielder, Location, ValidLocations, Type, Value, Icon, Wcid, SpellId, Useability;
    /// <summary>The PublicWeenieDesc's object-description flags (ObjectDescriptionFlag).</summary>
    public uint Bitfield;
    public int Burden, Stack, MaxStack, ItemsCapacity, MaterialType;
    public float Workmanship;
    public string Name = string.Empty;
    /// <summary>Lower-case name, for the search box.</summary>
    public string SearchName = string.Empty;
    /// <summary>The count drawn on the icon ("" for a single item).</summary>
    public string StackText = string.Empty;
    public InventoryCategory Category;
    public bool IsPack => ItemsCapacity > 0;

    /// <summary>ObjectDescriptionFlag.RequiresPackSlot: the item takes one of the main pack's pack slots.</summary>
    public const uint RequiresPackSlotFlag = 0x00800000u;

    /// <summary>
    /// A foci-style item (Foci of Enchantment and the like, ACE's ContainerType.Foci): it
    /// sits in one of the main pack's pack slots (the side column) next to the side packs,
    /// and doesn't count against the main pack's 102 item slots. The server keeps it in the
    /// main pack only (a side pack has no pack slots).
    /// </summary>
    public bool RequiresPackSlot => (Bitfield & RequiresPackSlotFlag) != 0 && !IsPack;

    public bool SameFields(in ClientObjectHooks.ItemPwdFields f) =>
        Container == f.Container && Wielder == f.Wielder && Location == f.Location && ValidLocations == f.ValidLocations
        && Type == f.Type && Value == f.Value && Icon == f.Icon && Wcid == f.Wcid && SpellId == f.SpellId
        && Useability == f.Useability && Burden == f.Burden && Stack == (int)f.StackSize && MaxStack == (int)f.MaxStackSize
        && ItemsCapacity == f.ItemsCapacity && MaterialType == f.MaterialType && Workmanship == f.Workmanship
        && Bitfield == f.Bitfield;
}

/// <summary>The main pack or one side pack.</summary>
internal sealed class InventoryPack
{
    public uint Id;
    public string Name = string.Empty;
    public int Capacity;
    public bool IsMain;
    /// <summary>Everything in the pack, pack-slot items (foci) included, in captured order.</summary>
    public InventoryItem[] Items = Array.Empty<InventoryItem>();
    /// <summary>Items that count against <see cref="Capacity"/>: pack-slot items don't (ACE's CountPackItems).</summary>
    public int Used;
    /// <summary>"12/24" (items that count / capacity).</summary>
    public string FillText = string.Empty;
}

/// <summary>Everything the panel draws, captured at once. Immutable.</summary>
internal sealed class InventoryView
{
    public static readonly InventoryView Empty = new();

    public uint PlayerId;
    public InventoryPack[] Packs = Array.Empty<InventoryPack>();
    /// <summary>
    /// The main pack's pack-slot items (foci), in captured order: retail's side column shows
    /// the side packs first, then these. They are also in Packs[0].Items.
    /// </summary>
    public InventoryItem[] SlotItems = Array.Empty<InventoryItem>();
    /// <summary>The main pack's pack slots (side packs + pack-slot items): 7, +1 with the extra-pack-slot augmentation.</summary>
    public int ContainerSlots = 7;
    public InventoryItem[] Worn = Array.Empty<InventoryItem>();
    public int ItemCount;
    public int Burden, BurdenCapacity;
    public long Pyreals;
    public string BurdenText = string.Empty;
    public string BurdenTip = string.Empty;
    public string PyrealText = string.Empty;
    /// <summary>ItemCount and Worn.Length as text (the pack column).</summary>
    public string ItemCountText = "0", WornCountText = "0";
    /// <summary>OR of the worn items' locations (which slots are taken).</summary>
    public uint WornMask;
    /// <summary>The character's name ("" until known): the Classic view's "Inventory of Name".</summary>
    public string PlayerName = string.Empty;
    /// <summary>Aetheria sigil slots unlocked (PropertyInt 322: 1 blue, 2 yellow, 4 red).</summary>
    public uint AetheriaMask;

    public bool Owns(uint containerId)
    {
        foreach (InventoryPack p in Packs)
            if (p.Id == containerId) return true;
        return false;
    }

    public InventoryPack? PackOf(uint containerId)
    {
        foreach (InventoryPack p in Packs)
            if (p.Id == containerId) return p;
        return null;
    }
}

/// <summary>The hovered item's extra tooltip lines (appraisal, spells). Immutable.</summary>
internal sealed class InventoryTooltip
{
    public uint Id;
    public string[] Lines = Array.Empty<string>();
    public bool Identifying;
}

/// <summary>The creature or player selected in the game (the Give target). Immutable.</summary>
internal sealed class InventoryTarget
{
    public uint Id;
    public string Name = string.Empty;
    /// <summary>"Give: Name", "Give to Name", and the Give zone's tooltip, built once.</summary>
    public string ZoneLabel = string.Empty, MenuLabel = string.Empty, ZoneTip = string.Empty;
}

internal enum InventoryActionKind : byte { Use, Move, Split, Wield, Drop, Give }

/// <summary>An item action the panel asks for.</summary>
internal sealed class InventoryRequest
{
    public InventoryActionKind Kind;
    public uint ItemId;
    /// <summary>
    /// Move/Split: the pack (player id = main pack). Give: the creature. Wield: the
    /// paperdoll slot it was dropped on (an equip mask; 0 = pick one). Else 0.
    /// </summary>
    public uint TargetId;
    /// <summary>Split: how many to split off. Give: 0 = the whole stack.</summary>
    public int Amount;
    /// <summary>Move: the slot in the target pack the item should end at (the server's order), or -1 for the first empty one.</summary>
    public int Slot = -1;
    public string ItemName = string.Empty;
    public string TargetName = string.Empty;
}

/// <summary>The action in flight, or held until the game is free, for the status row. Immutable.</summary>
internal sealed class InventoryPending
{
    public InventoryActionKind Kind;
    public uint ItemId;
    public string Label = string.Empty;
    public long StartedTicks;
    /// <summary>Not sent yet: the client is busy with another item action (or an attack).</summary>
    public bool WaitingForGame;
    /// <summary>While <see cref="WaitingForGame"/>: what the client is busy with ("pick up of Pyreal").</summary>
    public string BusyWith = string.Empty;
}

/// <summary>How the last action ended. Immutable.</summary>
internal sealed class InventoryResult
{
    public string Text = string.Empty;
    public bool Ok;
    public long AtTicks;
}

internal static partial class InventoryModel
{
    // ── Cadence ─────────────────────────────────────────────────────────
    private static readonly long Freq = Stopwatch.Frequency;
    private static readonly long CaptureIdleTicks = Freq;               // 1 s while open
    private static readonly long CaptureMinTicks = Freq * 15 / 100;     // at most ~6/s after changes
    private static readonly long WatchTicks = Freq / 10;                // action watch: 10 Hz
    private static readonly long ActionTimeoutTicks = Freq * 6;         // no effect seen: stop waiting
    private static readonly long GateWaitTicks = Freq * 10;             // held for the game's gate at most this long
    private static readonly long SlotTimeoutTicks = Freq * 15;          // our request still in AC's slot: stop waiting
    private static readonly long AnsweredGraceTicks = Freq / 2;         // slot emptied, no effect, no refusal yet
    private static readonly long NotSentGraceTicks = Freq * 3 / 2;      // move/split that AC didn't record
    private static readonly long TargetTicks = Freq / 4;                // selection check: 4 Hz
    private static readonly long TooltipRefreshTicks = Freq;            // tooltip rebuild while hovered
    private static readonly long IdentifyHoverTicks = Freq * 3 / 10;    // hover this long before an ID
    private static readonly long IdentifyGapTicks = Freq;               // at most one ID a second
    private static readonly long IdentifyRepeatTicks = Freq * 60;       // never the same item within a minute

    private const uint PyrealWcid = 273;
    private const uint StypeEncumbVal = 5, StypeCoinValue = 20, StypeItemCurMana = 107, StypeItemMaxMana = 108,
        StypeAugCarry = 230, StypeAetheriaBitfield = 322, AttrStrength = 1,
        StypeContainersCapacity = 7, StypeAugExtraPackSlot = 229;
    private const int MaxIds = 1024;

    // ── Published (any thread reads) ────────────────────────────────────
    private static InventoryView _current = InventoryView.Empty;
    private static long _version;
    private static InventoryTooltip? _tooltip;
    private static InventoryPending? _pending;
    private static InventoryResult? _result;
    private static InventoryTarget? _target;

    public static InventoryView Current => Volatile.Read(ref _current);
    /// <summary>Changes whenever a new view is published.</summary>
    public static long Version => Interlocked.Read(ref _version);
    public static InventoryTooltip? Tooltip => Volatile.Read(ref _tooltip);
    public static InventoryPending? Pending => Volatile.Read(ref _pending);
    public static InventoryResult? LastResult => Volatile.Read(ref _result);
    public static InventoryTarget? Target => Volatile.Read(ref _target);
    /// <summary>The object selected in the game when it is one of the character's items, else 0 (4 Hz while subscribed).</summary>
    public static uint SelectedItem => Volatile.Read(ref _selectedItem);
    private static uint _selectedItem;
    public static bool DropAvailable => ClientHelperHooks.HasDropItem;
    public static bool GiveAvailable => ClientHelperHooks.HasGiveObjectTo;
    public static bool WieldAvailable => ClientHelperHooks.HasWieldItem;

    // ── Inputs (any thread) ─────────────────────────────────────────────
    private static int _subscribers;
    private static int _dirty = 1;
    private static InventoryRequest? _requested;
    private static uint _hoverId;
    private static long _hoverSince;

    public static void Subscribe()
    {
        Interlocked.Increment(ref _subscribers);
        MarkDirty();
    }

    public static void Unsubscribe()
    {
        if (Interlocked.Decrement(ref _subscribers) < 0)
            Interlocked.Exchange(ref _subscribers, 0);
    }

    /// <summary>Something in the inventory changed: capture again soon. Any thread; cheap.</summary>
    public static void MarkDirty() => Volatile.Write(ref _dirty, 1);

    /// <summary>The item under the mouse (0 = none). Any thread.</summary>
    public static void Hover(uint itemId)
    {
        if (Volatile.Read(ref _hoverId) == itemId) return;
        Volatile.Write(ref _hoverSince, Stopwatch.GetTimestamp());
        Volatile.Write(ref _hoverId, itemId);
    }

    /// <summary>
    /// Asks for one item action. Refused (false, with the reason) while another one is
    /// waiting for the server or already requested: nothing is queued. It is sent on AC's
    /// main thread at its next tick, after the item and target are checked again. Any thread.
    /// </summary>
    public static bool Request(InventoryRequest request, out string why)
    {
        if (BatchRunning)
        {
            why = "Several items are being sent; wait, or press Stop.";
            return false;
        }
        InventoryPending? busy = Pending;
        if (busy != null)
        {
            why = (busy.WaitingForGame ? "Still waiting for the game: " : "Still waiting for the server: ") + busy.Label;
            return false;
        }
        if (Interlocked.CompareExchange(ref _requested, request, null) != null)
        {
            why = "Another item action is about to be sent.";
            return false;
        }
        why = string.Empty;
        return true;
    }

    // ── Main-thread state ───────────────────────────────────────────────
    private static readonly uint[] _ids = new uint[MaxIds];
    private static readonly uint[] _packIds = new uint[64];
    private static readonly Dictionary<uint, InventoryItem> _items = new(512);
    private static readonly List<InventoryItem> _scratchItems = new(512);
    private static readonly HashSet<uint> _seen = new();
    private static readonly HashSet<uint> _kept = new();
    private static readonly List<uint> _gone = new();
    private static long _lastCapture, _lastWatch, _lastTarget, _lastTooltip, _lastIdentify;
    private static ulong _lastSignature;
    private static uint _lastPlayer;
    private static string _playerName = string.Empty;
    private static readonly Dictionary<uint, long> _identified = new();
    private static readonly uint[] _spellBuf = new uint[64];

    // The action in flight (main thread only).
    private sealed class InFlight
    {
        public InventoryRequest Request = null!;
        public long Started;
        public int UseDoneSeq, ErrorSeq;
        public uint Container, Wielder;
        public int Stack;
        public uint Player;
        /// <summary>AC recorded this send in its pending-request slot (type, object).</summary>
        public bool SlotTracked;
        public uint SlotType, SlotObject;
        /// <summary>A move/split: AC records it in the slot when it actually sends it.</summary>
        public bool ExpectSlot;
        /// <summary>When the slot stopped holding our request (the server answered); 0 = not yet.</summary>
        public long AnsweredAt;
    }
    private static InFlight? _inFlight;

    // The action held until the client's gate opens (main thread only).
    private static InventoryRequest? _held;
    private static long _heldSince;
    private static long _heldBusyKey;

    /// <summary>
    /// AC's main thread (MainThreadSnapshots.Tick). Sends a requested action, watches the
    /// one in flight, captures the inventory and builds the hovered item's tooltip, each
    /// throttled. Returns at once while the panel is closed and nothing is in flight.
    /// </summary>
    public static void MainThreadTick()
    {
        bool open = Volatile.Read(ref _subscribers) > 0;
        if (!open && _inFlight == null && _held == null && Volatile.Read(ref _requested) == null
            && Volatile.Read(ref _batchActive) == 0)
            return;
        if (!MainThreadGuard.IsOnMainThread())
            return;

        long now = Stopwatch.GetTimestamp();
        try
        {
            InventoryRequest? req = Interlocked.Exchange(ref _requested, null);
            if (req != null) Begin(req, now);
            // Every tick, unthrottled: the gate's open moments between the bot's
            // requests can be short.
            if (_held != null) TryReleaseHeld(now);
            if (_inFlight != null && now - _lastWatch >= WatchTicks)
            {
                _lastWatch = now;
                Watch(now);
            }
            // After the watch, so an item that just ended is counted before the next is sent.
            if (Volatile.Read(ref _batchActive) != 0) BatchTick(now);
            if (!open) return;

            bool dirty = Volatile.Read(ref _dirty) != 0;
            long since = now - _lastCapture;
            if ((dirty && since >= CaptureMinTicks) || since >= CaptureIdleTicks
                || (_inFlight != null && since >= CaptureMinTicks * 2))
            {
                Volatile.Write(ref _dirty, 0);
                _lastCapture = now;
                Capture();
            }
            if (now - _lastTarget >= TargetTicks)
            {
                _lastTarget = now;
                SampleTarget();
            }
            HoverTick(now);
            IdentifyQueueTick(now);
        }
        catch (Exception ex)
        {
            // Never into AC: a managed failure here only costs this tick.
            RynthLog.Compat($"InventoryModel: tick failed - {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Capture ─────────────────────────────────────────────────────────

    private static void Capture()
    {
        uint player = ClientHelperHooks.GetPlayerId();
        if (player == 0)
        {
            if (_lastPlayer != 0 || !ReferenceEquals(Current, InventoryView.Empty))
            {
                _lastPlayer = 0;
                _items.Clear();
                _lastSignature = 0;
                Publish(InventoryView.Empty);
            }
            return;
        }
        if (player != _lastPlayer)
        {
            _items.Clear();       // another character: nothing carries over
            _identified.Clear();
            ClearIdentifyQueue();
            _lastPlayer = player;
            _lastSignature = 0;
            _playerName = string.Empty;
        }

        // Candidates: the PWD snapshot's children of the player and of each of its packs
        // (<= ~100 ms old); every candidate's container and wielder are then read live,
        // and an item is placed by its live container.
        int n = ClientObjectHooks.CollectOwnedIdsFromSnapshot(player, _ids);
        int packCount = 0;
        for (int i = 0; i < n && packCount < _packIds.Length; i++)
        {
            if (ClientObjectHooks.TryReadItemFields(_ids[i], out ClientObjectHooks.ItemPwdFields pf)
                && pf.Container == player && pf.Wielder == 0 && pf.ItemsCapacity > 0)
                _packIds[packCount++] = _ids[i];
        }
        Array.Sort(_packIds, 0, packCount);
        for (int p = 0; p < packCount && n < MaxIds; p++)
            n += ClientObjectHooks.CollectOwnedIdsFromSnapshot(_packIds[p], _ids.AsSpan(n));
        Array.Sort(_ids, 0, n);

        _seen.Clear();
        _kept.Clear();
        _scratchItems.Clear();
        ulong sig = 1469598103934665603UL ^ player;
        for (int i = 0; i < n; i++)
        {
            uint id = _ids[i];
            if (!_seen.Add(id)) continue;
            if (!ClientObjectHooks.TryReadItemFields(id, out ClientObjectHooks.ItemPwdFields f)) continue;
            bool worn = f.Wielder == player;
            bool inMain = f.Container == player && f.Wielder == 0;
            bool inPack = !worn && !inMain && f.Wielder == 0 && Array.BinarySearch(_packIds, 0, packCount, f.Container) >= 0;
            if (!worn && !inMain && !inPack) continue;   // moved away since the snapshot

            if (!_items.TryGetValue(id, out InventoryItem? item) || !item.SameFields(f))
            {
                item = MakeItem(id, f, item);
                _items[id] = item;
            }
            _scratchItems.Add(item);
            _kept.Add(id);
            sig = Mix(sig, id, f.Container, f.Wielder, f.Location, f.StackSize, f.Value, (uint)f.Burden, f.Icon,
                (uint)item.Name.Length ^ (f.Bitfield & InventoryItem.RequiresPackSlotFlag));
        }

        // Forget items no longer carried.
        if (_items.Count > _kept.Count)
        {
            _gone.Clear();
            foreach (uint id in _items.Keys)
                if (!_kept.Contains(id)) _gone.Add(id);
            foreach (uint id in _gone) _items.Remove(id);
        }

        // Player totals.
        int burden = ClientObjectHooks.TryGetObjectIntProperty(player, StypeEncumbVal, out int b) ? b : -1;
        long pyreals = ClientObjectHooks.TryGetObjectIntProperty(player, StypeCoinValue, out int coins) ? coins : -1;
        int capacity = 0;
        if (ClientObjectHooks.TryGetObjectAttribute(player, AttrStrength, 0, out uint strength) && strength > 0)
        {
            int augs = ClientObjectHooks.TryGetObjectIntProperty(player, StypeAugCarry, out int a) ? Math.Max(0, a) : 0;
            int bonus = Math.Min(150, 30 * augs);
            capacity = (int)(150 * strength + strength * bonus);
        }
        uint aetheria = ClientObjectHooks.TryGetObjectIntProperty(player, StypeAetheriaBitfield, out int ae) ? (uint)ae & 7u : 0u;
        // The main pack's pack slots: the player's ContainersCapacity when the client has it,
        // else ACE's rule (Player.cs: 7 + AugmentationExtraPackSlot).
        int containerSlots = ClientObjectHooks.TryGetObjectIntProperty(player, StypeContainersCapacity, out int cc) && cc > 0
            ? cc
            : 7 + (ClientObjectHooks.TryGetObjectIntProperty(player, StypeAugExtraPackSlot, out int xp) ? Math.Clamp(xp, 0, 1) : 0);
        containerSlots = Math.Clamp(containerSlots, 1, 32);
        // The name is read once per character (it doesn't change), then kept.
        if (_playerName.Length == 0)
        {
            if (!ClientObjectHooks.TryGetSnapshotName(player, out string name) || name.Length == 0)
                ClientObjectHooks.TryGetObjectName(player, out name);
            _playerName = name ?? string.Empty;
        }
        sig = Mix(sig, (uint)burden, (uint)pyreals, (uint)capacity, (uint)(pyreals >> 32), (uint)_scratchItems.Count,
            aetheria, (uint)_playerName.Length, (uint)containerSlots, 0);

        if (sig == _lastSignature && _current.PlayerId == player) return;
        _lastSignature = sig;
        Publish(BuildView(player, packCount, burden, pyreals, capacity, aetheria, containerSlots));
    }

    private static InventoryView BuildView(uint player, int packCount, int burden, long pyreals, int capacity, uint aetheria,
        int containerSlots)
    {
        var view = new InventoryView
        {
            PlayerId = player, Burden = burden, BurdenCapacity = capacity, PlayerName = _playerName, AetheriaMask = aetheria,
            ContainerSlots = containerSlots,
        };

        // Main pack first, then the side packs in id order. The main pack's pack-slot items
        // (foci) are also listed on their own, for the Classic side column (after the packs).
        var main = new List<InventoryItem>();
        var slotItems = new List<InventoryItem>();
        var packLists = new List<InventoryItem>[packCount];
        for (int p = 0; p < packCount; p++) packLists[p] = new List<InventoryItem>();
        var worn = new List<InventoryItem>();
        long pyrealStacks = 0;
        int count = 0;
        foreach (InventoryItem it in _scratchItems)
        {
            if (it.Wielder == player) { worn.Add(it); view.WornMask |= it.Location; continue; }
            if (it.Wcid == PyrealWcid) pyrealStacks += Math.Max(1, it.Stack);
            if (it.Container == player)
            {
                if (!it.IsPack)
                {
                    main.Add(it);
                    if (it.RequiresPackSlot) slotItems.Add(it);
                    count++;
                }
                continue;
            }
            int idx = Array.BinarySearch(_packIds, 0, packCount, it.Container);
            if (idx >= 0) { packLists[idx].Add(it); count++; }
        }

        var packs = new InventoryPack[packCount + 1];
        packs[0] = new InventoryPack
        {
            Id = player, Name = "Main pack", Capacity = 102, IsMain = true, Items = Ordered(main),
        };
        packs[0].Used = CountTowardCapacity(packs[0].Items);
        packs[0].FillText = Fill(packs[0].Used, 102);
        for (int p = 0; p < packCount; p++)
        {
            _items.TryGetValue(_packIds[p], out InventoryItem? packItem);
            int cap = packItem?.ItemsCapacity ?? 24;
            packs[p + 1] = new InventoryPack
            {
                Id = _packIds[p],
                Name = packItem?.Name ?? "Pack",
                Capacity = cap,
                Items = Ordered(packLists[p]),
            };
            packs[p + 1].Used = CountTowardCapacity(packs[p + 1].Items);
            packs[p + 1].FillText = Fill(packs[p + 1].Used, cap);
        }
        view.Packs = packs;
        view.SlotItems = slotItems.ToArray();
        view.Worn = worn.ToArray();
        view.ItemCount = count;
        view.Pyreals = pyreals >= 0 ? pyreals : pyrealStacks;

        view.PyrealText = view.Pyreals.ToString("N0", CultureInfo.InvariantCulture);
        view.ItemCountText = count.ToString(CultureInfo.InvariantCulture);
        view.WornCountText = view.Worn.Length.ToString(CultureInfo.InvariantCulture);
        if (burden < 0) { view.BurdenText = "?"; view.BurdenTip = "Burden not known yet."; }
        else if (capacity > 0)
        {
            // Retail's own arithmetic (OpenAC BurdenMath.LoadToPercent, checked against the retail panel): the
            // load ratio in single precision, the meter's fill (0-300%), and the percent shown is that rounded
            // DOWN. Rounding to nearest showed 36 where retail showed 35.
            float load = (float)burden / capacity;
            float fill = Math.Clamp(load / 3f, 0f, 1f);
            int pct = (int)MathF.Floor(fill * 300f);
            view.BurdenText = pct.ToString(CultureInfo.InvariantCulture) + "%";
            view.BurdenTip = $"Burden {burden:N0} of {capacity:N0} ({pct}%). Over 100% you move slower; 300% is the limit.";
        }
        else
        {
            view.BurdenText = burden.ToString("N0", CultureInfo.InvariantCulture);
            view.BurdenTip = $"Burden {burden:N0}.";
        }
        return view;
    }

    // Already in ascending id order (the capture sorts ids): that is "pack order".
    private static InventoryItem[] Ordered(List<InventoryItem> list) => list.ToArray();

    /// <summary>ACE's Container.CountPackItems: everything but packs and pack-slot items.</summary>
    private static int CountTowardCapacity(InventoryItem[] items)
    {
        int n = 0;
        foreach (InventoryItem it in items)
            if (!it.RequiresPackSlot) n++;
        return n;
    }

    private static string Fill(int items, int capacity) =>
        items.ToString(CultureInfo.InvariantCulture) + "/" + capacity.ToString(CultureInfo.InvariantCulture);

    private static InventoryItem MakeItem(uint id, in ClientObjectHooks.ItemPwdFields f, InventoryItem? old)
    {
        string name = old?.Name ?? string.Empty;
        if (name.Length == 0 || old!.Wcid != f.Wcid || old.Stack != (int)f.StackSize)
        {
            if (!ClientObjectHooks.TryGetSnapshotName(id, out name) || name.Length == 0)
                if (!ClientObjectHooks.TryGetObjectName(id, out name) || name.Length == 0)
                    name = old?.Name is { Length: > 0 } prev ? prev : "(unknown item)";
        }
        int stack = (int)f.StackSize;
        return new InventoryItem
        {
            Id = id, Container = f.Container, Wielder = f.Wielder, Location = f.Location, ValidLocations = f.ValidLocations,
            Type = f.Type, Value = f.Value, Icon = f.Icon, Wcid = f.Wcid, SpellId = f.SpellId, Useability = f.Useability,
            Bitfield = f.Bitfield,
            Burden = f.Burden, Stack = stack, MaxStack = (int)f.MaxStackSize, ItemsCapacity = f.ItemsCapacity,
            MaterialType = f.MaterialType, Workmanship = f.Workmanship,
            Name = name,
            SearchName = name.ToLowerInvariant(),
            StackText = stack > 1 ? ShortCount(stack) : string.Empty,
            Category = CategoryOf(f.Type),
        };
    }

    /// <summary>"25", "1.2k", "25k".</summary>
    internal static string ShortCount(int n)
    {
        if (n < 1000) return n.ToString(CultureInfo.InvariantCulture);
        if (n < 10000) return (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        if (n < 1000000) return (n / 1000).ToString(CultureInfo.InvariantCulture) + "k";
        return (n / 1000000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
    }

    internal static InventoryCategory CategoryOf(uint type)
    {
        if ((type & (0x1u | 0x100u | 0x8000u)) != 0) return InventoryCategory.Weapons;        // melee, missile, caster
        if ((type & (0x2u | 0x4u)) != 0) return InventoryCategory.Armor;                        // armor, clothing
        if ((type & 0x8u) != 0) return InventoryCategory.Jewelry;
        if ((type & 0x1000u) != 0) return InventoryCategory.Components;
        if ((type & (0x20000000u | 0x40000000u)) != 0) return InventoryCategory.Salvage;        // tinkering tool / material
        if ((type & (0x20u | 0x800u | 0x80000u | 0x4000u | 0x2000u)) != 0) return InventoryCategory.Usable; // food, gem, mana stone, key, writable
        return InventoryCategory.Misc;
    }

    private static ulong Mix(ulong h, uint a, uint b, uint c, uint d, uint e, uint f, uint g, uint i, uint j)
    {
        const ulong P = 1099511628211UL;
        h = (h ^ a) * P; h = (h ^ b) * P; h = (h ^ c) * P; h = (h ^ d) * P; h = (h ^ e) * P;
        h = (h ^ f) * P; h = (h ^ g) * P; h = (h ^ i) * P; h = (h ^ j) * P;
        return h;
    }

    private static void Publish(InventoryView view)
    {
        Volatile.Write(ref _current, view);
        Interlocked.Increment(ref _version);
    }

    // ── Give target ─────────────────────────────────────────────────────

    private static void SampleTarget()
    {
        uint sel = ClientHelperHooks.GetSelectedItemId();
        uint player = ClientHelperHooks.GetPlayerId();
        Volatile.Write(ref _selectedItem, sel != 0 && _items.ContainsKey(sel) ? sel : 0u);
        InventoryTarget? cur = _target;
        if (sel == 0 || sel == player || _items.ContainsKey(sel)
            || !ClientObjectHooks.TryGetItemType(sel, out uint type) || (type & 0x10u) == 0)   // creatures and players only
        {
            if (cur != null) Volatile.Write(ref _target, null);
            return;
        }
        if (cur != null && cur.Id == sel) return;
        if (!ClientObjectHooks.TryGetSnapshotName(sel, out string name) || name.Length == 0)
            if (!ClientObjectHooks.TryGetObjectName(sel, out name) || name.Length == 0) name = "the selected creature";
        Volatile.Write(ref _target, new InventoryTarget
        {
            Id = sel, Name = name, ZoneLabel = "Give: " + name, MenuLabel = "Give to " + name,
            ZoneTip = "Give to " + name + ": drag an item here, or click to give the selected item.",
        });
    }

    // ── Actions ─────────────────────────────────────────────────────────

    /// <summary>What the status row says for an action ("Moving X to Y").</summary>
    private static string ActionLabel(InventoryRequest r) => r.Kind switch
    {
        InventoryActionKind.Use => "Using " + r.ItemName,
        InventoryActionKind.Move => $"Moving {r.ItemName} to {r.TargetName}",
        InventoryActionKind.Split => $"Splitting {r.Amount} {r.ItemName} into {r.TargetName}",
        InventoryActionKind.Wield => "Equipping " + r.ItemName,
        InventoryActionKind.Drop => "Dropping " + r.ItemName,
        InventoryActionKind.Give => $"Giving {r.ItemName} to {r.TargetName}",
        _ => r.ItemName,
    };

    /// <summary>
    /// Use, move and split go through ACCWeenieObject::IsPlayerReadyToMakeInventoryRequest,
    /// which AC refuses while attacking as well as while a request is pending. Wield, drop
    /// and give are raw CM_Inventory sends that AC doesn't gate.
    /// </summary>
    private static bool ClientGated(InventoryActionKind kind) =>
        kind is InventoryActionKind.Use or InventoryActionKind.Move or InventoryActionKind.Split;

    /// <summary>Move and split: AC records them in its pending-request slot when it sends them.</summary>
    private static bool RecordsSlot(InventoryActionKind kind) =>
        kind is InventoryActionKind.Move or InventoryActionKind.Split;

    /// <summary>
    /// Is the client's item-action gate closed for this kind? Key 0 = open (or the gate
    /// isn't available: today's behaviour). Main thread (called from MainThreadTick only).
    /// </summary>
    private static long GateBusyKey(InventoryActionKind kind)
    {
        if (!ClientActionGates.IsRequestGateAvailable)
            return 0;
        if (ClientActionGates.TryReadPending(out uint type, out uint obj, out _) && type != 0)
            return ((long)type << 32) | obj;
        if (ClientGated(kind) && ClientActionGates.TryReadAttacking(out bool attacking) && attacking)
            return -1;
        return 0;
    }

    private static string BusyText(long key)
    {
        if (key == -1) return "an attack";
        uint type = (uint)(key >> 32), obj = (uint)key;
        string what = ClientActionGates.RequestTypeName(type);
        if (obj != 0 && (ClientObjectHooks.TryGetSnapshotName(obj, out string name) && name.Length > 0
                         || ClientObjectHooks.TryGetObjectName(obj, out name) && name.Length > 0))
            return what + " of " + name;
        return "another item action (" + what + ")";
    }

    /// <summary>Send now if the client's gate is open, else hold the action until it is. Main thread.</summary>
    private static void Begin(InventoryRequest r, long now)
    {
        if (_inFlight != null || _held != null) { Finish("Still waiting for the server.", false, now); return; }
        long key = GateBusyKey(r.Kind);
        if (key == 0)
        {
            Send(r, now);
            return;
        }
        _held = r;
        _heldSince = now;
        _heldBusyKey = key;
        PublishHeld(r, key);
    }

    private static void PublishHeld(InventoryRequest r, long key) =>
        Volatile.Write(ref _pending, new InventoryPending
        {
            Kind = r.Kind, ItemId = r.ItemId, Label = ActionLabel(r), StartedTicks = _heldSince,
            WaitingForGame = true, BusyWith = BusyText(key),
        });

    /// <summary>The held action: sent the first tick the gate is open, or dropped after GateWaitTicks. Main thread.</summary>
    private static void TryReleaseHeld(long now)
    {
        InventoryRequest r = _held!;
        long key = GateBusyKey(r.Kind);
        if (key == 0)
        {
            _held = null;
            Volatile.Write(ref _pending, null);
            Send(r, now);
            return;
        }
        if (now - _heldSince >= GateWaitTicks)
        {
            _held = null;
            Volatile.Write(ref _pending, null);
            string busy = BusyText(key);
            Finish($"Not sent: the game stayed busy with {busy}. Try again.", false, now);
            RynthLog.Compat($"InventoryModel: {ActionLabel(r)} not sent - the client's item gate stayed closed {GateWaitTicks / Freq}s ({busy}).");
            return;
        }
        if (key != _heldBusyKey)
        {
            _heldBusyKey = key;
            PublishHeld(r, key);
        }
    }

    private static void Send(InventoryRequest r, long now)
    {
        if (_inFlight != null) { Finish("Still waiting for the server.", false, now); return; }
        uint player = ClientHelperHooks.GetPlayerId();
        if (player == 0) { Finish("Not in the world.", false, now); return; }
        if (!ClientObjectHooks.TryReadItemFields(r.ItemId, out ClientObjectHooks.ItemPwdFields f))
        {
            Finish($"{r.ItemName} is gone.", false, now);
            MarkDirty();
            return;
        }
        bool worn = f.Wielder == player;
        bool carried = worn || (f.Wielder == 0 && (f.Container == player || OwnedPack(f.Container, player)));
        if (!carried)
        {
            Finish($"{r.ItemName} is no longer in your packs.", false, now);
            MarkDirty();
            return;
        }

        bool ok;
        string label = ActionLabel(r);
        switch (r.Kind)
        {
            case InventoryActionKind.Use:
                ok = ClientHelperHooks.UseObject(r.ItemId);
                break;

            case InventoryActionKind.Move:
            {
                if (r.TargetId != player && !OwnedPack(r.TargetId, player))
                {
                    Finish("That pack is gone.", false, now);
                    return;
                }
                if (f.Container == r.TargetId && !worn && r.Slot < 0) { Finish($"{r.ItemName} is already there.", true, now); return; }
                int amount = Math.Max(1, (int)f.StackSize);
                // A slot (a drop on a cell of the grid, or a reorder) goes out as the packet itself; the
                // server answers a full pack or a wrong kind of item. Without one the helper picks the
                // first empty slot, and refuses (false) a full pack of the player's own before sending.
                ok = r.Slot >= 0 && ClientHelperHooks.HasPutItemInContainer
                    ? ClientHelperHooks.PutItemInContainerAt(r.ItemId, r.TargetId, r.Slot)
                    : ClientHelperHooks.MoveItemInternal(r.ItemId, r.TargetId, 0, amount);
                if (!ok) { Finish($"{r.TargetName} is full, or the move isn't possible.", false, now); return; }
                break;
            }

            case InventoryActionKind.Split:
            {
                int stack = (int)f.StackSize;
                if (stack < 2 || r.Amount < 1 || r.Amount >= stack)
                {
                    Finish($"Can't split {r.Amount} from a stack of {stack}.", false, now);
                    return;
                }
                if (r.TargetId != player && !OwnedPack(r.TargetId, player))
                {
                    Finish("That pack is gone.", false, now);
                    return;
                }
                // The new stack needs a free slot. SplitStackInternal goes through the same
                // native slot-table walk as a move (FUN_00588f70) but, unlike MoveItemInternal,
                // has no full-pack guard of its own, so apply it here before sending.
                if (ClientHelperHooks.IsOwnedContainerFull(r.TargetId))
                {
                    Finish($"{r.TargetName} is full, or the split isn't possible.", false, now);
                    return;
                }
                ok = ClientHelperHooks.SplitStackInternal(r.ItemId, r.TargetId, 0, r.Amount);
                break;
            }

            case InventoryActionKind.Wield:
            {
                if (worn) { Finish($"{r.ItemName} is already worn.", true, now); return; }
                uint mask = r.TargetId != 0
                    ? SlotWieldMask(f.ValidLocations, r.TargetId)
                    : WieldMask(f.ValidLocations, Current.WornMask);
                if (mask == 0)
                {
                    Finish(r.TargetId != 0 && f.ValidLocations != 0
                        ? $"{r.ItemName} doesn't go in that slot."
                        : $"{r.ItemName} can't be worn or wielded.", false, now);
                    return;
                }
                ok = ClientHelperHooks.WieldItem(r.ItemId, mask);
                break;
            }

            case InventoryActionKind.Drop:
                if (worn) { Finish($"Take {r.ItemName} off before dropping it.", false, now); return; }
                ok = ClientHelperHooks.DropItem(r.ItemId);
                break;

            case InventoryActionKind.Give:
            {
                if (worn) { Finish($"Take {r.ItemName} off before giving it.", false, now); return; }
                if (r.TargetId == 0 || r.TargetId == player || !ClientObjectHooks.TryGetItemType(r.TargetId, out _))
                {
                    Finish("The give target is gone.", false, now);
                    return;
                }
                ok = ClientHelperHooks.GiveObjectTo(r.ItemId, r.TargetId, Math.Max(0, r.Amount));
                break;
            }

            default:
                return;
        }

        if (!ok)
        {
            Finish($"Couldn't send: {label}.", false, now);
            return;
        }

        SmartBoxHooks.TryGetLastWeenieError(out int errSeq, out _, out _, out _);
        _inFlight = new InFlight
        {
            Request = r, Started = now, UseDoneSeq = SmartBoxHooks.GetUseDoneSeq(), ErrorSeq = errSeq,
            Container = f.Container, Wielder = f.Wielder, Stack = (int)f.StackSize, Player = player,
        };

        // The send ran synchronously on this thread and the slot was empty just before it
        // (Begin/TryReleaseHeld only send with the gate open), so a request in the slot now
        // is ours: whatever object AC recorded (the item; an auto-merge may record another).
        if (ClientActionGates.IsRequestGateAvailable && RecordsSlot(r.Kind))
        {
            _inFlight.ExpectSlot = true;
            if (ClientActionGates.TryReadPending(out uint slotType, out uint slotObj, out _) && slotType != 0)
            {
                _inFlight.SlotTracked = true;
                _inFlight.SlotType = slotType;
                _inFlight.SlotObject = slotObj;
            }
        }
        Volatile.Write(ref _pending, new InventoryPending { Kind = r.Kind, ItemId = r.ItemId, Label = label, StartedTicks = now });
        _lastWatch = now;
        MarkDirty();
    }

    /// <summary>Looks at the item again: done, refused, gone, or still waiting. Main thread.</summary>
    private static void Watch(long now)
    {
        InFlight a = _inFlight!;
        InventoryRequest r = a.Request;
        bool exists = ClientObjectHooks.TryReadItemFields(r.ItemId, out ClientObjectHooks.ItemPwdFields f);

        // The server refused it: InventoryServerSaveFailed (0x00A0) for this item, or (a use)
        // any refusal / an error UseDone.
        SmartBoxHooks.TryGetLastWeenieError(out int errSeq, out uint err, out uint evt, out uint errObj);
        bool refused = errSeq != a.ErrorSeq && (evt == 0x00A0 ? errObj == r.ItemId : r.Kind == InventoryActionKind.Use);
        bool useDone = SmartBoxHooks.GetUseDoneSeq() != a.UseDoneSeq;
        uint useErr = 0;
        if (useDone) SmartBoxHooks.TryGetLastUseDone(out _, out useErr);

        // AC's slot no longer holds our request: the server has answered (or the client
        // dropped it). Read on this (main) thread, like every other read here.
        if (a.SlotTracked && a.AnsweredAt == 0)
        {
            ClientActionGates.TryReadPending(out uint slotType, out uint slotObj, out _);
            if (slotType != a.SlotType || slotObj != a.SlotObject)
                a.AnsweredAt = now;
        }

        string? done = null;
        bool ok = true;
        if (refused)
        {
            done = $"Refused: {PendingLabel(r)} (error 0x{err:X}).";
            ok = false;
        }
        else if (!exists)
        {
            done = r.Kind switch
            {
                InventoryActionKind.Drop => $"Dropped {r.ItemName}.",
                InventoryActionKind.Give => $"Gave {r.ItemName} to {r.TargetName}.",
                InventoryActionKind.Use => $"Used {r.ItemName}.",
                _ => $"{r.ItemName} is gone (merged or removed).",
            };
        }
        else
        {
            switch (r.Kind)
            {
                case InventoryActionKind.Use:
                    if (useDone)
                    {
                        ok = useErr == 0;
                        done = ok ? $"Used {r.ItemName}." : $"Couldn't use {r.ItemName} (error 0x{useErr:X}).";
                    }
                    else if (f.Wielder != a.Wielder || f.Container != a.Container || (int)f.StackSize != a.Stack)
                        done = $"Used {r.ItemName}.";
                    break;
                case InventoryActionKind.Move:
                    if (f.Container == r.TargetId && f.Wielder == 0) done = $"Moved {r.ItemName} to {r.TargetName}.";
                    else if (a.AnsweredAt != 0 && (int)f.StackSize < a.Stack)   // part merged into a stack there
                        done = $"Moved {a.Stack - (int)f.StackSize} {r.ItemName} to {r.TargetName}.";
                    break;
                case InventoryActionKind.Split:
                    if ((int)f.StackSize < a.Stack) done = $"Split {r.Amount} {r.ItemName} into {r.TargetName}.";
                    break;
                case InventoryActionKind.Wield:
                    if (f.Wielder == a.Player) done = $"Equipped {r.ItemName}.";
                    break;
                case InventoryActionKind.Drop:
                    if (f.Wielder != a.Player && f.Container != a.Player && !OwnedPack(f.Container, a.Player))
                        done = $"Dropped {r.ItemName}.";
                    break;
                case InventoryActionKind.Give:
                    if ((int)f.StackSize < a.Stack
                        || (f.Wielder != a.Player && f.Container != a.Player && !OwnedPack(f.Container, a.Player)))
                        done = $"Gave {r.ItemName} to {r.TargetName}.";
                    break;
            }
        }

        if (done == null && a.AnsweredAt != 0)
        {
            // The server answered and nothing we watch for happened: a refusal. Any
            // WeenieError since the send is its reason (the last one kept).
            if (errSeq != a.ErrorSeq)
            {
                done = $"Refused: {PendingLabel(r)} (error 0x{err:X}).";
                ok = false;
            }
            else if (now - a.AnsweredAt >= AnsweredGraceTicks)
            {
                done = $"Refused: {PendingLabel(r)} (the server answered; nothing changed).";
                ok = false;
            }
        }
        else if (done == null && a.ExpectSlot && !a.SlotTracked && now - a.Started >= NotSentGraceTicks)
        {
            // AC didn't record a move/split: it refused it before sending (it says why in chat).
            done = $"The game didn't send: {PendingLabel(r)} (see chat for why).";
            ok = false;
        }
        else if (done == null && now - a.Started >= (a.SlotTracked ? SlotTimeoutTicks : ActionTimeoutTicks))
        {
            // Slot-tracked: AC is still holding our request, so it will refuse other item
            // actions until the server answers (or ClientActionGates frees a stuck one).
            done = a.SlotTracked
                ? $"No answer from the server yet for: {PendingLabel(r)}. The game is still waiting on it (/rc actionstate)."
                : $"No answer seen for: {PendingLabel(r)}. You can act again.";
            ok = false;
        }
        if (done == null) return;
        _inFlight = null;
        Volatile.Write(ref _pending, null);
        Finish(done, ok, now);
        MarkDirty();
    }

    private static string PendingLabel(InventoryRequest r) => Pending?.Label ?? r.ItemName;

    private static void Finish(string text, bool ok, long now) =>
        Volatile.Write(ref _result, new InventoryResult { Text = text, Ok = ok, AtTicks = now });

    /// <summary>A side pack the player carries (its container is the player). Main thread.</summary>
    private static bool OwnedPack(uint containerId, uint player)
    {
        if (containerId == 0 || containerId == player) return false;
        return ClientObjectHooks.TryReadItemFields(containerId, out ClientObjectHooks.ItemPwdFields p)
            && p.Container == player && p.Wielder == 0 && p.ItemsCapacity > 0;
    }

    /// <summary>
    /// The slot(s) to wield into. Armour and clothing: their whole coverage (the server
    /// uses that anyway). Anything else (weapons, shields, rings, bracelets, trinkets,
    /// sigils): the first slot it fits that nothing is in, else its first slot.
    /// </summary>
    internal static uint WieldMask(uint valid, uint occupied)
    {
        const uint ClothingAndArmor = 0x7FFF;
        if (valid == 0) return 0;
        if ((valid & ClothingAndArmor) != 0) return valid;
        uint first = 0;
        for (int bit = 0; bit < 32; bit++)
        {
            uint m = 1u << bit;
            if ((valid & m) == 0) continue;
            if (first == 0) first = m;
            if ((occupied & m) == 0) return m;
        }
        return first;
    }

    /// <summary>
    /// The slot(s) to wield into when the item was dropped on one paperdoll slot
    /// (<paramref name="slot"/>, an equip mask), or 0 when it can't go there. Armour,
    /// clothing and cloaks keep their whole coverage (the server puts them there anyway);
    /// anything else goes in that slot only (a ring on the chosen hand, a weapon in the
    /// weapon slot). Ported from OpenAC's ItemEquipRules.ResolvePaperdollDropWieldMask
    /// (Tom's OpenAC client, src/AcDream.Runtime/Gameplay/ItemEquipRules.cs).
    /// </summary>
    internal static uint SlotWieldMask(uint valid, uint slot)
    {
        const uint AutoWear = 0x7FFFu | 0x08000000u;   // clothing and armour (head..lower legs), cloak
        if ((valid & slot) == 0) return 0;
        return (valid & AutoWear) != 0 ? valid : valid & slot;
    }

    // ── Tooltip and identify ────────────────────────────────────────────

    private static void HoverTick(long now)
    {
        uint id = Volatile.Read(ref _hoverId);
        InventoryTooltip? tip = _tooltip;
        if (id == 0)
        {
            if (tip != null) Volatile.Write(ref _tooltip, null);
            return;
        }

        bool appraised = AppraisalHooks.HasAppraisalData(id);
        bool sentRecently = _identified.TryGetValue(id, out long sentAt) && now - sentAt < IdentifyRepeatTicks;
        if (!appraised && !sentRecently && now - Volatile.Read(ref _hoverSince) >= IdentifyHoverTicks
            && now - _lastIdentify >= IdentifyGapTicks && _items.ContainsKey(id))
        {
            if (_identified.Count > 512) _identified.Clear();
            _identified[id] = now;
            _lastIdentify = now;
            sentRecently = true;
            using (Net.EngineSendMarker.Enter(Net.IdRequester.Ui))
                ClientActionHooks.RequestId(id);   // main thread: the 0xC8 is sent now
            tip = null;                        // rebuild below with "identifying"
        }

        if (tip != null && tip.Id == id && now - _lastTooltip < TooltipRefreshTicks) return;
        _lastTooltip = now;
        Volatile.Write(ref _tooltip, BuildTooltip(id, appraised, !appraised && sentRecently));
    }

    // ── Identify queue (the attribute filter's "Identify N") ────────────

    private static readonly long QueuedIdentifyGapTicks = Freq / 4;   // 4 a second
    private const int MaxQueuedIdentifies = 1024;
    private static readonly object IdQueueSync = new();
    private static readonly Queue<uint> _idQueue = new();               // guarded by IdQueueSync
    private static readonly HashSet<uint> _idQueued = new();            // guarded by IdQueueSync
    private static long _lastQueuedIdentify;

    /// <summary>Items still waiting in the identify queue. Any thread.</summary>
    public static int IdentifyQueueCount
    {
        get { lock (IdQueueSync) return _idQueue.Count; }
    }

    /// <summary>
    /// Asks the server to identify <paramref name="ids"/>, a few a second on AC's main thread
    /// while the panel is open. Items already appraised, sent in the last minute, already
    /// queued or no longer carried are skipped. Any thread.
    /// </summary>
    public static void QueueIdentify(IReadOnlyList<uint> ids)
    {
        lock (IdQueueSync)
        {
            foreach (uint id in ids)
            {
                if (id == 0 || _idQueue.Count >= MaxQueuedIdentifies) continue;
                if (_idQueued.Add(id)) _idQueue.Enqueue(id);
            }
        }
    }

    /// <summary>Drops every queued identify. Any thread.</summary>
    public static void ClearIdentifyQueue()
    {
        lock (IdQueueSync)
        {
            _idQueue.Clear();
            _idQueued.Clear();
        }
    }

    /// <summary>Main thread: sends the next queued identify that still needs sending.</summary>
    private static void IdentifyQueueTick(long now)
    {
        if (now - _lastQueuedIdentify < QueuedIdentifyGapTicks) return;
        while (true)
        {
            uint id;
            lock (IdQueueSync)
            {
                if (!_idQueue.TryDequeue(out id)) return;
                _idQueued.Remove(id);
            }
            if (AppraisalHooks.HasAppraisalData(id) || !_items.ContainsKey(id)) continue;
            if (_identified.TryGetValue(id, out long sentAt) && now - sentAt < IdentifyRepeatTicks) continue;
            if (_identified.Count > 2048) _identified.Clear();
            _identified[id] = now;
            _lastQueuedIdentify = now;
            ClientActionHooks.RequestId(id);   // main thread: the 0xC8 is sent now
            return;
        }
    }

    private static InventoryTooltip BuildTooltip(uint id, bool appraised, bool identifying)
    {
        var lines = new List<string>(8);
        _items.TryGetValue(id, out InventoryItem? item);
        if (appraised)
        {
            bool hasCur = ClientObjectHooks.TryGetObjectIntProperty(id, StypeItemCurMana, out int cur);
            bool hasMax = ClientObjectHooks.TryGetObjectIntProperty(id, StypeItemMaxMana, out int max);
            if (hasMax && max > 0)
                lines.Add($"Mana {(hasCur ? cur : 0):N0} / {max:N0}");
            int n = AppraisalHooks.GetObjectSpellIds(id, _spellBuf, _spellBuf.Length);
            if (n > 0)
            {
                if (!PortalSpellTable.Ready) PortalSpellTable.EnsureLoadQueued();
                var sb = new StringBuilder("Spells: ");
                int shown = Math.Min(n, _spellBuf.Length);
                for (int i = 0; i < shown; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(SpellName(_spellBuf[i]));
                }
                lines.Add(sb.ToString());
            }
        }
        if (item != null && item.SpellId != 0 && (lines.Count == 0 || !appraised))
        {
            if (!PortalSpellTable.Ready) PortalSpellTable.EnsureLoadQueued();
            lines.Add("Casts " + SpellName(item.SpellId));
        }
        if (identifying) lines.Add("Identifying...");
        else if (!appraised) lines.Add("Not identified yet.");
        return new InventoryTooltip { Id = id, Lines = lines.ToArray(), Identifying = identifying };
    }

    private static string SpellName(uint spellId)
    {
        // Spell ids in an appraisal can carry the layer in the high word.
        uint sid = spellId & 0xFFFF;
        return PortalSpellTable.TryGetName(sid, out string name) ? name : "spell " + sid.ToString(CultureInfo.InvariantCulture);
    }
}

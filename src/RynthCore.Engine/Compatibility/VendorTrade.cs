// ============================================================================
//  RynthCore.Engine - Compatibility/VendorTrade.cs
//
//  Engine side of vendor trading: read the open vendor, buy from it, sell to it.
//  Plugins reach this through the v67 host calls (GetVendorInfo / GetVendorItems /
//  VendorBuy / VendorSell / GetVendorTradeStatus); Tom can drive it by hand with
//  /rc vendor ... (RynthCoreChatCommands).
//
//  How it works (all offsets checked against the live acclient.exe):
//
//  READ   The server's vendor list (GameEvent ApproachVendor 0x0062) reaches
//         gmVendorUI::OpenVendor(this, vendorId, VendorProfile const&,
//         PackableList<ItemProfile> const&, ShopMode), which VendorHooks already
//         detours. Inside that detour (AC's main thread, arguments alive) we copy
//         the VendorProfile and every ItemProfile + its PublicWeenieDesc into a
//         managed, immutable VendorSnapshot. Every read is page-probed first, so a
//         layout surprise fails closed (no snapshot) instead of faulting. Plugin
//         reads are served from that snapshot on any thread.
//
//  BUY /  The retail "Buy all"/"Sell all" buttons (gmVendorUI::HandleButtonClicks)
//  SELL   fill gmVendorUI.m_buyList / m_sellList (PackableList<ItemProfile>, amount
//         + object id, pwd null) and call gmVendorUI::SendShopEvent(vendorId, list,
//         altCurrencyId, 0=buy/1=sell), which calls CM_Vendor::Event_Buy (0x005F) /
//         Event_Sell (0x0060), then ACCWeenieObject::RecordRequest(vendor, 10) and
//         ClientUISystem::IncrementBusyCount (the server's UseDone takes it back
//         off). We do exactly that, with our own short-lived list: a real
//         PackableList<ItemProfile> header + ItemProfile nodes in unmanaged memory
//         whose vtables are the client's own (found by code xref, then confirmed
//         against the live vendor list before we ever send). Event_Buy only walks
//         the list to pack it into the outgoing blob, so the memory is freed as soon
//         as the call returns. altCurrencyId is VendorProfile.trade_id, as retail.
//
//  THREAD Requests are checked against the snapshot on the caller's thread, then
//         queued. MainThreadTick (called from AcMainThreadQueue.Drain, the EndScene
//         drain on AC's main thread) re-checks against live client state (vendor
//         still open, pyreals / alt currency, pack slots, burden, ownership) and only
//         then calls SendShopEvent. One transaction is in flight at a time; it
//         completes when the server's UseDone (0x01C7) arrives, the vendor list is
//         re-sent, the vendor closes, or 10 s pass.
//
//  FAIL   No vendor, unknown item, bad amount, insufficient funds, an item the player
//  CLOSED doesn't own or is wearing, unresolved client function or unverified layout:
//         refuse, log, and report through GetVendorTradeStatus. Pack slots and burden
//         are refused only when the numbers are known and say "won't fit"; when they
//         can't be read the server (which checks the same limits) has the final say.
//
//  Server rules mirrored from ACE (Player_Commerce.cs / Vendor.cs / ItemsToReceive.cs):
//  player pays max(1, ceil(value * SellRate - 0.1)) per created stack (promissory
//  notes at 1.15), vendor pays max(1, floor(value * BuyRate + 0.1)) per item (notes
//  at 1.0), burden limit = 3 x capacity, stackables take ceil(amount / maxStack) slots.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

/// <summary>One item on the vendor's list, copied out of its ItemProfile + PublicWeenieDesc.</summary>
internal sealed class VendorItemInfo
{
    public uint ObjectId;
    public uint Wcid;
    public uint ItemType;
    public uint IconId;
    /// <summary>ItemProfile.amount: how many the vendor has, negative = unlimited.</summary>
    public int Amount;
    public int StackSize;
    public int MaxStackSize;
    public int Value;
    public int Burden;
    public uint Bitfield;
    public int ItemsCapacity;
    public int ContainersCapacity;
    public string Name = string.Empty;

    public bool Unlimited => Amount < 0;
    /// <summary>ACE treats MaxStackSize &gt; 0 as stackable (ItemProfileToWorldObjects).</summary>
    public bool Stackable => MaxStackSize > 0;
    /// <summary>Packs and foci take a container slot (BF_REQUIRES_PACKSLOT 0x800000).</summary>
    public bool NeedsContainerSlot => (Bitfield & 0x800000u) != 0 || ItemsCapacity > 0 || ContainersCapacity > 0;
    public int UnitValue => StackSize > 1 ? Value / StackSize : Value;
    public int UnitBurden => StackSize > 1 ? Burden / StackSize : Burden;
}

/// <summary>Immutable copy of the open vendor, published on AC's main thread.</summary>
internal sealed class VendorSnapshot
{
    public uint VendorId;
    public uint Generation;
    public int ShopMode;
    public uint ItemTypes;
    public int MinValue;
    public int MaxValue;
    public int DealsMagic;
    public float BuyRate;
    public float SellRate;
    public uint AltCurrencyWcid;
    public int AltCurrencyServerCount = -1;
    public string AltCurrencyName = string.Empty;
    public string Name = string.Empty;
    public bool ProfileRead;
    public bool ItemsComplete;
    /// <summary>gmVendorUI field offsets and the list/ItemProfile vtables matched the live objects.</summary>
    public bool LayoutVerified;
    public IntPtr Ui;
    public IntPtr ListVtableSeen;
    public IntPtr ItemVtableSeen;
    public VendorItemInfo[] Items = Array.Empty<VendorItemInfo>();

    public VendorItemInfo? Find(uint objectId)
    {
        foreach (VendorItemInfo it in Items)
            if (it.ObjectId == objectId)
                return it;
        return null;
    }
}

internal static class VendorTrade
{
    // ── Client addresses (pattern-resolved; the VAs are fallbacks the gate checks) ──
    // gmVendorUI::SendShopEvent(vendorId, PackableList<ItemProfile> const&, altCurrencyId,
    // ShopEvent) — thiscall, `this` unused, ret 0x10. Literal prologue:
    //   mov eax,[esp+10]; sub eax,0; push esi; jz buy; dec eax; jnz out
    private const int VendorSendShopEventVa = 0x004C0EA0;
    private static readonly byte?[] PatVendorSendShopEvent =
        [ 0x8B, 0x44, 0x24, 0x10, 0x83, 0xE8, 0x00, 0x56, 0x74, 0x17, 0x48, 0x75, 0x42 ];

    // PackableList<ItemProfile> vtable — code xref in gmVendorUI::OpenVendor where it
    // news the shop list: jz +11; mov [eax],<vtbl>; mov [eax+4],ebp; mov [eax+8],ebp;
    // mov [eax+C],ebp; jmp +2  (operand at +4).
    private const int VendorItemProfileListVtableVa = 0x007B6110;
    private static readonly byte?[] PatXrefItemProfileListVtable =
        [ 0x74, 0x11, 0xC7, 0x00, null, null, null, null, 0x89, 0x68, 0x04, 0x89, 0x68, 0x08, 0x89, 0x68, 0x0C, 0xEB, 0x02 ];

    // ItemProfile vtable — code xref in ItemProfile::~ItemProfile:
    // mov [esi],<vtbl>; jz; mov eax,[ecx]; push 1; call [eax]; mov [esi+C],0; mov [esi],<PackObj>
    private const int VendorItemProfileVtableVa = 0x007E9A34;
    private static readonly byte?[] PatXrefItemProfileVtable =
        [ 0xC7, 0x06, null, null, null, null, 0x74, 0x0D, 0x8B, 0x01, 0x6A, 0x01, 0xFF, 0x10, 0xC7, 0x46, 0x0C, 0x00, 0x00, 0x00, 0x00, 0xC7, 0x06 ];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SendShopEventDelegate(IntPtr gmVendorUi, uint vendorId, IntPtr itemList, uint altCurrencyId, int shopEvent);

    private const int ShopEventBuy = 0;
    private const int ShopEventSell = 1;

    // ── Layouts (from the live binary: gmVendorUI ctor 0x004C3060, OpenVendor 0x004C5790,
    //    ResetShopState 0x004C32C0, ItemProfile::Pack 0x005D2890, PackableList::Pack
    //    0x0055F420, VendorProfile::GetTradeID 0x005D2BD0) ─────────────────────────
    private const int UiShopVendorIdOffset = 0x608;   // 0 once ResetShopState runs (every close path)
    private const int UiShopItemListOffset = 0x610;   // PackableList<ItemProfile>* (UI's own copy)

    private const int ProfileSize = 0x28;
    private const int ProfileItemTypes = 0x04, ProfileMinValue = 0x08, ProfileMaxValue = 0x0C, ProfileMagic = 0x10;
    private const int ProfileBuyRate = 0x14, ProfileSellRate = 0x18, ProfileTradeId = 0x1C, ProfileTradeNum = 0x20, ProfileTradeName = 0x24;

    private const int ListSize = 0x10;                // vfptr, head, tail, curNum
    private const int ListHead = 0x04, ListTail = 0x08, ListCount = 0x0C;
    private const int NodeSize = 0x18;                // ItemProfile(16) + next + prev
    private const int NodeAmount = 0x04, NodeIid = 0x08, NodePwd = 0x0C, NodeNext = 0x10, NodePrev = 0x14;

    private const int PwdSize = 0xB0;
    private const int PwdName = 4, PwdWcid = 12, PwdIcon = 16, PwdContainer = 28, PwdWielder = 32;
    private const int PwdItemsCapacity = 48, PwdContainersCapacity = 52, PwdType = 56, PwdValue = 60;
    private const int PwdStackSize = 96, PwdMaxStackSize = 100, PwdBitfield = 104, PwdBurden = 116;

    private const uint ItemTypePromissoryNote = 0x00040000;
    private const int PyrealMaxStack = 25000;          // coinstack MaxStackSize (payout slots)
    private const int MaxVendorItems = 1024;
    private const int MaxTradeEntries = 100;
    private const int MaxTradeAmount = 0x7FFFFF;       // ItemProfile::Pack keeps 24 bits; stay positive
    private const int MaxPendingTxns = 4;
    private const long InFlightTimeoutMs = 10_000;

    // ── Resolved state ──────────────────────────────────────────────────────────
    private static SendShopEventDelegate? _sendShopEvent;
    private static IntPtr _listVtable;
    private static IntPtr _itemProfileVtable;
    private static IntPtr _moduleBase;
    private static int _moduleSize;
    private static bool _resolved;
    private static volatile bool _vtablesVerified;
    private static string _statusMessage = "Not initialized.";

    // ── Snapshot + live polling (snapshot written on AC's main thread only) ─────
    private static volatile VendorSnapshot? _snapshot;
    private static uint _generation;
    private static long _lastPollTick;
    private static long _lastCoinsTick;
    private static volatile int _playerCoins = -1;
    private static volatile int _altHave = -1;
    private static volatile bool _needAltScan;

    // ── Transactions ────────────────────────────────────────────────────────────
    private sealed class Txn
    {
        public uint Seq;
        public bool IsBuy;
        public uint VendorId;
        public uint[] Ids = Array.Empty<uint>();
        public int[] Amounts = Array.Empty<int>();
        public bool Echo;
    }

    private static readonly object _txnLock = new();
    private static readonly Queue<Txn> _pending = new();
    private static volatile int _pendingCount;
    private static int _seq;

    // In-flight transaction (main thread only).
    private static Txn? _inFlight;
    private static long _inFlightSinceTick;
    private static int _inFlightUseDoneBaseline;
    private static long _inFlightUseDoneSeenTick;
    private static bool _inFlightRefreshed;

    // Last-request status (any thread, under _statusLock).
    private static readonly object _statusLock = new();
    private static uint _stSeq;
    private static int _stState;       // 1 queued, 2 sent, 3 done, 4 refused
    private static int _stResult;      // with 3: 1 accepted, 2 answered w/o refresh, 3 timeout
    private static bool _stIsBuy;
    private static uint _stVendor;
    private static int _stCount;
    private static long _stEstimate;
    private static string _stMessage = string.Empty;

    // Chat echo for /rc vendor (drained on the main thread; written from the drain,
    // never from inside the outgoing-chat detour).
    private static readonly object _echoLock = new();
    private static readonly Queue<string> _echo = new();
    private static volatile int _echoCount;
    private const int MaxEcho = 48;

    public static string StatusMessage => _statusMessage;
    /// <summary>Client functions + vtables resolved by pattern (not fallback).</summary>
    public static bool IsResolved => _resolved;
    public static VendorSnapshot? Snapshot => _snapshot;

    // ════════════════════════════════════════════════════════════════════════════
    //  Init
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolve SendShopEvent and the two vtables. Strict: a fallback-VA resolve is NOT
    /// accepted for anything we call or build objects from — trading just stays off.
    /// Called from VendorHooks.Initialize once the open/close detours are in.
    /// </summary>
    public static void Initialize(AcClientTextSection text)
    {
        _moduleBase = text.ModuleBase;
        _moduleSize = text.ImageSize;

        HookResolver.ResolveResult send = HookResolver.Resolve(text, "Vendor.SendShopEvent", PatVendorSendShopEvent, VendorSendShopEventVa);
        HookResolver.ResolveResult listVt = HookResolver.ResolveData(text, "Vendor.ItemProfileListVtable", PatXrefItemProfileListVtable, 4, VendorItemProfileListVtableVa);
        HookResolver.ResolveResult itemVt = HookResolver.ResolveData(text, "Vendor.ItemProfileVtable", PatXrefItemProfileVtable, 2, VendorItemProfileVtableVa);

        if (send.Source != HookResolver.ResolveSource.PatternScan
            || listVt.Source != HookResolver.ResolveSource.PatternScan
            || itemVt.Source != HookResolver.ResolveSource.PatternScan)
        {
            _statusMessage = $"trading OFF - SendShopEvent={send.Detail}, listVtbl={listVt.Detail}, itemVtbl={itemVt.Detail}";
            RynthLog.Warn($"VendorTrade: {_statusMessage}");
            return;
        }

        if (!InModule(listVt.Address) || !InModule(itemVt.Address))
        {
            _statusMessage = $"trading OFF - vtables outside acclient (list=0x{listVt.Address.ToInt32():X8} item=0x{itemVt.Address.ToInt32():X8})";
            RynthLog.Warn($"VendorTrade: {_statusMessage}");
            return;
        }

        _sendShopEvent = Marshal.GetDelegateForFunctionPointer<SendShopEventDelegate>(send.Address);
        _listVtable = listVt.Address;
        _itemProfileVtable = itemVt.Address;
        _resolved = true;
        _statusMessage = $"ready (SendShopEvent=0x{send.Address.ToInt32():X8}, listVtbl=0x{_listVtable.ToInt32():X8}, itemVtbl=0x{_itemProfileVtable.ToInt32():X8}); waiting for a vendor to confirm the layout";
        RynthLog.Compat($"VendorTrade: {_statusMessage}");
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Snapshot (called from the VendorHooks detours — AC's main thread)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Copy the vendor out of the OpenVendor arguments. Called BEFORE the original runs,
    /// while the caller's VendorProfile / ItemProfile list (and each item's PWD) are
    /// intact — OpenVendor clears the PWDs on its own copy as it builds weenie objects.
    /// Returns null if the arguments don't look like what we expect.
    /// </summary>
    public static VendorSnapshot? CaptureFromNotice(uint vendorId, IntPtr profile, IntPtr list, int shopMode)
    {
        if (vendorId == 0)
            return null;

        try
        {
            var snap = new VendorSnapshot { VendorId = vendorId, ShopMode = shopMode };

            if (profile != IntPtr.Zero && Readable(profile, ProfileSize) && InModule(Marshal.ReadIntPtr(profile)))
            {
                snap.ItemTypes = ReadU32(profile + ProfileItemTypes);
                snap.MinValue = Marshal.ReadInt32(profile + ProfileMinValue);
                snap.MaxValue = Marshal.ReadInt32(profile + ProfileMaxValue);
                snap.DealsMagic = Marshal.ReadInt32(profile + ProfileMagic);
                snap.BuyRate = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(profile + ProfileBuyRate));
                snap.SellRate = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(profile + ProfileSellRate));
                snap.AltCurrencyWcid = ReadU32(profile + ProfileTradeId);
                snap.AltCurrencyServerCount = snap.AltCurrencyWcid != 0 ? Marshal.ReadInt32(profile + ProfileTradeNum) : -1;
                TryReadLegacyString(profile + ProfileTradeName, 128, out snap.AltCurrencyName);
                snap.ProfileRead = RateLooksSane(snap.BuyRate) && RateLooksSane(snap.SellRate);
                if (!snap.ProfileRead)
                    RynthLog.Warn($"VendorTrade: vendor 0x{vendorId:X8} profile rates look wrong (buy={snap.BuyRate} sell={snap.SellRate}); trading with it is off.");
            }

            if (list != IntPtr.Zero && Readable(list, ListSize))
            {
                snap.ListVtableSeen = Marshal.ReadIntPtr(list);
                if (InModule(snap.ListVtableSeen))
                    snap.Items = ReadItemList(list, snap);
            }

            return snap;
        }
        catch (Exception ex)
        {
            RynthLog.Warn($"VendorTrade: capture failed for vendor 0x{vendorId:X8} - {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static VendorItemInfo[] ReadItemList(IntPtr list, VendorSnapshot snap)
    {
        int declared = Marshal.ReadInt32(list + ListCount);
        int capacity = declared > 0 && declared <= MaxVendorItems ? declared : 16;
        var items = new List<VendorItemInfo>(capacity);
        IntPtr node = Marshal.ReadIntPtr(list + ListHead);
        int walked = 0;
        bool complete = true;

        while (node != IntPtr.Zero)
        {
            if (walked >= MaxVendorItems || !Readable(node, NodeSize))
            {
                complete = false;
                break;
            }
            walked++;

            IntPtr itemVt = Marshal.ReadIntPtr(node);
            if (snap.ItemVtableSeen == IntPtr.Zero)
                snap.ItemVtableSeen = itemVt;
            else if (itemVt != snap.ItemVtableSeen)
            {
                complete = false;   // mixed vtables = not the list we think it is
                break;
            }

            var it = new VendorItemInfo
            {
                Amount = Marshal.ReadInt32(node + NodeAmount),
                ObjectId = ReadU32(node + NodeIid),
            };

            IntPtr pwd = Marshal.ReadIntPtr(node + NodePwd);
            if (pwd != IntPtr.Zero && Readable(pwd, PwdSize) && InModule(Marshal.ReadIntPtr(pwd)))
            {
                it.Wcid = ReadU32(pwd + PwdWcid);
                it.IconId = ReadU32(pwd + PwdIcon);
                it.ItemsCapacity = Marshal.ReadInt32(pwd + PwdItemsCapacity);
                it.ContainersCapacity = Marshal.ReadInt32(pwd + PwdContainersCapacity);
                it.ItemType = ReadU32(pwd + PwdType);
                it.Value = Marshal.ReadInt32(pwd + PwdValue);
                it.StackSize = Marshal.ReadInt32(pwd + PwdStackSize);
                it.MaxStackSize = Marshal.ReadInt32(pwd + PwdMaxStackSize);
                it.Bitfield = ReadU32(pwd + PwdBitfield);
                it.Burden = Marshal.ReadInt32(pwd + PwdBurden);
                TryReadLegacyString(pwd + PwdName, 256, out it.Name);
            }

            if (it.ObjectId != 0)
                items.Add(it);

            node = Marshal.ReadIntPtr(node + NodeNext);
        }

        snap.ItemsComplete = complete && (declared < 0 || walked == declared);
        if (!snap.ItemsComplete)
            RynthLog.Warn($"VendorTrade: vendor 0x{snap.VendorId:X8} item list read stopped early ({walked} walked, {declared} declared).");
        return items.ToArray();
    }

    /// <summary>
    /// After the original OpenVendor ran: confirm the gmVendorUI layout against the live
    /// object, fill names from the client, publish the snapshot. Main thread.
    /// </summary>
    public static void OnVendorOpened(IntPtr gmVendorUi, uint vendorId, VendorSnapshot? snap)
    {
        try
        {
            if (snap == null)
            {
                _snapshot = null;
                RynthLog.Warn($"VendorTrade: vendor 0x{vendorId:X8} opened but could not be read; vendor calls will refuse.");
                return;
            }

            snap.Ui = gmVendorUi;

            // gmVendorUI.shopVendorID (+0x608) must now hold this vendor, and +0x610 must be
            // the UI's own PackableList<ItemProfile> copy (vtable = the one we resolved).
            bool uiIdOk = gmVendorUi != IntPtr.Zero
                          && Readable(gmVendorUi + UiShopVendorIdOffset, 12)
                          && ReadU32(gmVendorUi + UiShopVendorIdOffset) == vendorId;
            IntPtr uiList = uiIdOk ? Marshal.ReadIntPtr(gmVendorUi + UiShopItemListOffset) : IntPtr.Zero;
            bool uiListOk = _resolved && uiList != IntPtr.Zero && Readable(uiList, ListSize) && Marshal.ReadIntPtr(uiList) == _listVtable;
            IntPtr uiFirstItemVt = IntPtr.Zero;
            if (uiListOk)
            {
                IntPtr head = Marshal.ReadIntPtr(uiList + ListHead);
                if (head != IntPtr.Zero && Readable(head, NodeSize))
                    uiFirstItemVt = Marshal.ReadIntPtr(head);
            }

            bool listVtOk = uiListOk || (_resolved && snap.ListVtableSeen == _listVtable);
            bool itemVtOk = _resolved && (snap.ItemVtableSeen == _itemProfileVtable || uiFirstItemVt == _itemProfileVtable);
            if (listVtOk && itemVtOk && !_vtablesVerified)
            {
                _vtablesVerified = true;
                RynthLog.Compat("VendorTrade: live vendor list matches the resolved PackableList<ItemProfile>/ItemProfile vtables - trading enabled.");
            }
            snap.LayoutVerified = uiIdOk && uiListOk
                                  && (snap.Items.Length == 0 || snap.ItemVtableSeen == _itemProfileVtable || uiFirstItemVt == _itemProfileVtable);
            if (!snap.LayoutVerified)
                RynthLog.Warn($"VendorTrade: vendor 0x{vendorId:X8} layout check failed (uiId={uiIdOk}, uiList={uiListOk}, resolved={_resolved}, " +
                              $"argListVt=0x{snap.ListVtableSeen.ToInt32():X8} argItemVt=0x{snap.ItemVtableSeen.ToInt32():X8} uiItemVt=0x{uiFirstItemVt.ToInt32():X8} " +
                              $"expected list=0x{_listVtable.ToInt32():X8} item=0x{_itemProfileVtable.ToInt32():X8}); trading with it is off.");

            if (ClientObjectHooks.TryGetObjectName(vendorId, out string vendorName))
                snap.Name = vendorName;
            foreach (VendorItemInfo it in snap.Items)
            {
                if (it.Name.Length == 0 && ClientObjectHooks.TryGetObjectName(it.ObjectId, out string n))
                    it.Name = n;
                if (it.Wcid == 0 && ClientObjectHooks.TryGetObjectWcid(it.ObjectId, out uint w))
                    it.Wcid = w;
            }

            snap.Generation = unchecked(++_generation);
            if (_inFlight != null && _inFlight.VendorId == vendorId)
                _inFlightRefreshed = true;

            _altHave = -1;
            _needAltScan = snap.AltCurrencyWcid != 0;
            _snapshot = snap;
            RefreshCoins();
            _lastPollTick = Environment.TickCount64;

            RynthLog.Compat($"VendorTrade: vendor 0x{vendorId:X8} '{snap.Name}' open gen={snap.Generation} items={snap.Items.Length} " +
                            $"buy={snap.BuyRate:0.###} sell={snap.SellRate:0.###} types=0x{snap.ItemTypes:X8} " +
                            $"currency={(snap.AltCurrencyWcid == 0 ? "pyreals" : $"{snap.AltCurrencyName} (wcid {snap.AltCurrencyWcid})")} layout={(snap.LayoutVerified ? "ok" : "BAD")}");
        }
        catch (Exception ex)
        {
            _snapshot = null;
            RynthLog.Warn($"VendorTrade: OnVendorOpened failed - {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The vendor is gone (close notice, window closed, walked away). Main thread.</summary>
    public static void OnVendorClosed(string reason)
    {
        VendorSnapshot? snap = _snapshot;
        _snapshot = null;
        _needAltScan = false;
        _altHave = -1;

        List<Txn>? dropped = null;
        lock (_txnLock)
        {
            if (_pending.Count > 0)
            {
                dropped = new List<Txn>(_pending);
                _pending.Clear();
                _pendingCount = 0;
            }
        }
        if (dropped != null)
            foreach (Txn t in dropped)
                Refuse(t, "vendor closed before it was sent");

        // A transaction already sent stays in flight: the server still answers it (UseDone,
        // and a fresh vendor list if it went through). The client can also close and
        // re-open the vendor inside one ApproachVendor dispatch (when a ground container
        // was open - ClientUISystem 0x005652B0), so a close here is not a refusal.

        if (snap != null)
            RynthLog.Compat($"VendorTrade: vendor 0x{snap.VendorId:X8} closed ({reason}).");
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Main-thread tick (AcMainThreadQueue.Drain)
    // ════════════════════════════════════════════════════════════════════════════

    public static void MainThreadTick()
    {
        if (_echoCount > 0)
            DrainEcho();

        VendorSnapshot? snap = _snapshot;
        if (snap == null && _inFlight == null && _pendingCount == 0)
            return;   // idle fast path

        if (!MainThreadGuard.IsOnMainThread())
            return;

        long now = Environment.TickCount64;
        if (snap != null)
        {
            if (now - _lastPollTick >= 200)
            {
                _lastPollTick = now;
                if (!VendorStillOpen(snap))
                {
                    VendorHooks.MarkVendorClosed("vendor window closed");
                    snap = null;
                }
            }
            if (snap != null && now - _lastCoinsTick >= 250)
                RefreshCoins();
            if (snap != null && _needAltScan)
            {
                _needAltScan = false;
                _altHave = CountOwnedWcid(snap.AltCurrencyWcid);
            }
        }

        if (_inFlight != null)
            CheckInFlight(now);

        if (_inFlight == null && _pendingCount > 0)
        {
            Txn? next = null;
            lock (_txnLock)
            {
                if (_pending.Count > 0)
                {
                    next = _pending.Dequeue();
                    _pendingCount = _pending.Count;
                }
            }
            if (next != null)
                Execute(next);
        }
    }

    private static bool VendorStillOpen(VendorSnapshot snap)
    {
        if (!snap.LayoutVerified || snap.Ui == IntPtr.Zero)
            return true;   // can't poll this one; rely on the close notice
        IntPtr field = snap.Ui + UiShopVendorIdOffset;
        return Readable(field, 4) && ReadU32(field) == snap.VendorId;
    }

    private static void RefreshCoins()
    {
        _lastCoinsTick = Environment.TickCount64;
        uint player = ClientHelperHooks.GetPlayerId();
        _playerCoins = player != 0 && ClientObjectHooks.TryGetObjectIntProperty(player, 20u /* COIN_VALUE */, out int coins) ? coins : -1;
    }

    private static void CheckInFlight(long now)
    {
        int useDone = SmartBoxHooks.GetUseDoneSeq();
        if (useDone != _inFlightUseDoneBaseline)
        {
            if (_inFlightRefreshed)
            {
                CompleteInFlight(1, "server accepted (vendor list re-sent)");
                return;
            }
            // ACE sends the fresh vendor list before UseDone, but give a late list (or a
            // UseDone that belonged to some other action) a moment before calling it a refusal.
            if (_inFlightUseDoneSeenTick == 0)
                _inFlightUseDoneSeenTick = now;
            else if (now - _inFlightUseDoneSeenTick >= 500)
            {
                CompleteInFlight(2, "server answered without re-sending the list (refused - see chat)");
                return;
            }
        }
        if (now - _inFlightSinceTick >= InFlightTimeoutMs)
            CompleteInFlight(_inFlightRefreshed ? 1 : 3, _inFlightRefreshed ? "server accepted (no UseDone seen)" : "no answer from the server within 10 s");
    }

    private static void CompleteInFlight(int result, string message)
    {
        Txn? t = _inFlight;
        _inFlight = null;
        _inFlightRefreshed = false;
        if (t == null)
            return;
        SetStatus(t, 3, result, message);
        RynthLog.Compat($"VendorTrade: #{t.Seq} {(t.IsBuy ? "buy" : "sell")} done - {message}");
        if (t.Echo)
            Echo($"[RC] vendor #{t.Seq}: {message}");
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Requests (any thread)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Queue a buy. Returns the request id, or 0 if refused (status says why).</summary>
    public static uint RequestBuy(uint vendorId, ReadOnlySpan<uint> ids, ReadOnlySpan<int> amounts, bool echo = false)
    {
        var t = new Txn { Seq = NextSeq(), IsBuy = true, Echo = echo };
        if (ids.Length != amounts.Length)
            return RefuseNew(t, vendorId, ids.Length, "ids/amounts length mismatch");

        VendorSnapshot? snap = _snapshot;
        string? err = PreCheck(snap, vendorId, ids.Length, out uint vid);
        if (err != null)
            return RefuseNew(t, vendorId, ids.Length, err);

        long estimate = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            uint id = ids[i];
            int amount = amounts[i];
            for (int j = 0; j < i; j++)
                if (ids[j] == id)
                    return RefuseNew(t, vid, ids.Length, $"0x{id:X8} listed twice");
            VendorItemInfo? item = snap!.Find(id);
            if (item == null)
                return RefuseNew(t, vid, ids.Length, $"0x{id:X8} is not on this vendor's list");
            if (amount < 1 || amount > MaxTradeAmount)
                return RefuseNew(t, vid, ids.Length, $"bad amount {amount} for 0x{id:X8}");
            if (!item.Unlimited && amount > item.Amount)
                return RefuseNew(t, vid, ids.Length, $"vendor only has {item.Amount} of 0x{id:X8}, asked for {amount}");
            EstimateBuy(item, amount, snap.SellRate, out long cost, out _, out _, out _);
            estimate += cost;
        }

        t.VendorId = vid;
        t.Ids = ids.ToArray();
        t.Amounts = amounts.ToArray();
        return Enqueue(t, estimate);
    }

    /// <summary>Queue a sell of whole stacks. Returns the request id, or 0 if refused.</summary>
    public static uint RequestSell(uint vendorId, ReadOnlySpan<uint> ids, bool echo = false)
    {
        var t = new Txn { Seq = NextSeq(), IsBuy = false, Echo = echo };
        VendorSnapshot? snap = _snapshot;
        string? err = PreCheck(snap, vendorId, ids.Length, out uint vid);
        if (err != null)
            return RefuseNew(t, vendorId, ids.Length, err);

        for (int i = 0; i < ids.Length; i++)
        {
            uint id = ids[i];
            if (id == 0)
                return RefuseNew(t, vid, ids.Length, "item id 0");
            if (id == vid || snap!.Find(id) != null)
                return RefuseNew(t, vid, ids.Length, $"0x{id:X8} belongs to the vendor");
            for (int j = 0; j < i; j++)
                if (ids[j] == id)
                    return RefuseNew(t, vid, ids.Length, $"0x{id:X8} listed twice");
        }

        t.VendorId = vid;
        t.Ids = ids.ToArray();
        t.Amounts = new int[ids.Length];   // filled with stack sizes on the main thread
        return Enqueue(t, 0);
    }

    /// <summary>Record a refusal for a request that never got as far as RequestBuy/RequestSell (bad host-call arguments). Returns 0.</summary>
    public static uint RefuseRequest(bool isBuy, uint vendorId, int count, string reason)
    {
        var t = new Txn { Seq = NextSeq(), IsBuy = isBuy };
        return RefuseNew(t, vendorId, Math.Clamp(count, 0, MaxTradeEntries), reason);
    }

    private static string? PreCheck(VendorSnapshot? snap, uint vendorId, int count, out uint vid)
    {
        vid = vendorId;
        if (!_resolved)
            return $"vendor trading unavailable ({_statusMessage})";
        if (snap == null)
            return "no vendor open";
        vid = snap.VendorId;
        if (vendorId != 0 && vendorId != snap.VendorId)
            return $"vendor 0x{vendorId:X8} is not the open vendor (0x{snap.VendorId:X8})";
        if (!snap.LayoutVerified || !_vtablesVerified)
            return "vendor layout not verified; trading is off for this vendor";
        if (!snap.ProfileRead)
            return "vendor profile unreadable";
        if (count < 1)
            return "nothing to trade";
        if (count > MaxTradeEntries)
            return $"too many lines ({count} > {MaxTradeEntries}); split into batches";
        return null;
    }

    private static uint Enqueue(Txn t, long estimate)
    {
        lock (_txnLock)
        {
            if (_pending.Count >= MaxPendingTxns)
                return RefuseNew(t, t.VendorId, t.Ids.Length, $"{MaxPendingTxns} requests already waiting");
            _pending.Enqueue(t);
            _pendingCount = _pending.Count;
        }
        SetStatus(t, 1, 0, "queued", estimate);
        RynthLog.Compat($"VendorTrade: #{t.Seq} {(t.IsBuy ? "buy" : "sell")} queued - {t.Ids.Length} line(s) for vendor 0x{t.VendorId:X8}" +
                        (t.IsBuy ? $", est. cost {estimate}" : string.Empty));
        return t.Seq;
    }

    private static uint NextSeq()
    {
        uint s = unchecked((uint)Interlocked.Increment(ref _seq));
        return s == 0 ? unchecked((uint)Interlocked.Increment(ref _seq)) : s;
    }

    private static uint RefuseNew(Txn t, uint vendorId, int count, string reason)
    {
        t.VendorId = vendorId;
        t.Ids = new uint[Math.Max(0, count)];
        Refuse(t, reason);
        return 0;
    }

    private static void Refuse(Txn t, string reason)
    {
        SetStatus(t, 4, 0, reason);
        RynthLog.Compat($"VendorTrade: #{t.Seq} {(t.IsBuy ? "buy" : "sell")} refused - {reason}");
        if (t.Echo)
            Echo($"[RC] vendor #{t.Seq} refused: {reason}");
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Execute (main thread)
    // ════════════════════════════════════════════════════════════════════════════

    private static void Execute(Txn t)
    {
        try
        {
            VendorSnapshot? snap = _snapshot;
            string? err = PreCheck(snap, t.VendorId, t.Ids.Length, out _);
            if (err != null) { Refuse(t, err); return; }
            if (!VendorStillOpen(snap!)) { Refuse(t, "vendor window is closed"); return; }

            uint player = ClientHelperHooks.GetPlayerId();
            if (player == 0) { Refuse(t, "player id unavailable"); return; }

            long estimate;
            uint altCurrency = 0;
            if (t.IsBuy)
            {
                err = ValidateBuy(t, snap!, player, out estimate);
                altCurrency = snap!.AltCurrencyWcid;
            }
            else
            {
                err = ValidateSell(t, snap!, player, out estimate);
            }
            if (err != null) { Refuse(t, err); return; }

            if (!SendShopEvent(snap!, t, altCurrency, out err))
            {
                Refuse(t, err ?? "send failed");
                return;
            }

            _inFlight = t;
            _inFlightSinceTick = Environment.TickCount64;
            _inFlightUseDoneSeenTick = 0;
            _inFlightRefreshed = false;
            SetStatus(t, 2, 0, "sent, waiting for the server", estimate);
            RynthLog.Compat($"VendorTrade: #{t.Seq} {(t.IsBuy ? "buy" : "sell")} SENT to 0x{t.VendorId:X8}: {DescribeLines(t, snap!)}" +
                            (t.IsBuy ? $" cost~{estimate}{(altCurrency != 0 ? " " + snap!.AltCurrencyName : "p")}" : $" payout~{estimate}p"));
            if (t.Echo)
                Echo($"[RC] vendor #{t.Seq} sent: {(t.IsBuy ? "cost" : "payout")} ~{estimate}");
        }
        catch (Exception ex)
        {
            Refuse(t, $"internal error {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? ValidateBuy(Txn t, VendorSnapshot snap, uint player, out long totalCost)
    {
        totalCost = 0;
        long needItemSlots = 0, needContainerSlots = 0, needBurden = 0;
        for (int i = 0; i < t.Ids.Length; i++)
        {
            VendorItemInfo? item = snap.Find(t.Ids[i]);
            if (item == null)
                return $"0x{t.Ids[i]:X8} is no longer on the vendor's list";
            int amount = t.Amounts[i];
            if (amount < 1 || amount > MaxTradeAmount || (!item.Unlimited && amount > item.Amount))
                return $"bad amount {amount} for 0x{t.Ids[i]:X8} (vendor has {(item.Unlimited ? "unlimited" : item.Amount.ToString())})";
            EstimateBuy(item, amount, snap.SellRate, out long cost, out long slots, out bool containerSlot, out long burden);
            totalCost += cost;
            if (containerSlot) needContainerSlots += slots; else needItemSlots += slots;
            needBurden += burden;
        }
        if (totalCost > int.MaxValue)
            return "total cost overflows";

        // Funds — fail closed if we can't read them.
        if (snap.AltCurrencyWcid == 0)
        {
            if (!ClientObjectHooks.TryGetObjectIntProperty(player, 20u /* COIN_VALUE */, out int coins))
                return "could not read your pyreals";
            _playerCoins = coins;
            if (totalCost > coins)
                return $"costs {totalCost} pyreals, you have {coins}";
        }
        else
        {
            int have = CountOwnedWcid(snap.AltCurrencyWcid);
            _altHave = have;
            if (have < 0)
                return $"could not count your {Currency(snap)}";
            if (totalCost > have)
                return $"costs {totalCost} {Currency(snap)}, you have {have}";
        }

        // Pack space + burden — refuse only when known not to fit.
        if (TryGetFreeSlots(player, out int freeItems, out int freeContainers))
        {
            if (needItemSlots > freeItems)
                return $"needs {needItemSlots} free pack slots, you have {freeItems}";
            if (needContainerSlots > freeContainers)
                return $"needs {needContainerSlots} free container slots, you have {freeContainers}";
        }
        else
        {
            RynthLog.Compat($"VendorTrade: #{t.Seq} pack slots unreadable - leaving the space check to the server.");
        }

        if (needBurden > 0 && TryGetAvailableBurden(player, out long availableBurden) && needBurden > availableBurden)
            return $"adds {needBurden} burden, you can carry {availableBurden} more";

        return null;
    }

    private static string? ValidateSell(Txn t, VendorSnapshot snap, uint player, out long payout)
    {
        payout = 0;
        for (int i = 0; i < t.Ids.Length; i++)
        {
            uint id = t.Ids[i];
            if (!ClientObjectHooks.TryGetObjectOwnershipInfo(id, out uint container, out uint wielder, out _))
                return $"0x{id:X8} not found";
            if (wielder != 0)
                return $"0x{id:X8} is equipped - unequip it first";
            bool owned = container == player;
            if (!owned && container != 0
                && ClientObjectHooks.TryGetObjectOwnershipInfo(container, out uint outer, out uint outerWielder, out _))
                owned = outer == player && outerWielder == 0;
            if (!owned)
                return $"0x{id:X8} is not in your packs";

            if (!ClientObjectHooks.TryGetItemType(id, out uint type) || type == 0)
                return $"could not read the type of 0x{id:X8}";
            if ((snap.ItemTypes & type) == 0)
                return $"this vendor does not buy {DescribeObject(id)} (type 0x{type:X8})";

            if (!ClientObjectHooks.TryReadPwdInt32(id, PwdValue, out int value))
                return $"could not read the value of 0x{id:X8}";
            if (value < 1)
                return $"{DescribeObject(id)} has no value";

            if (ClientObjectHooks.TryGetObjectIntProperty(id, 6u /* ITEMS_CAPACITY */, out int cap) && cap > 0)
            {
                int inside = ClientObjectHooks.GetNumContainedItems(id);
                if (inside != 0)
                    return $"{DescribeObject(id)} is not empty";
            }

            int stack = ClientObjectHooks.TryGetObjectIntProperty(id, 12u /* STACK_SIZE */, out int s) && s > 0 ? s : 1;
            if (stack > MaxTradeAmount)
                return $"stack of 0x{id:X8} too large ({stack})";
            t.Amounts[i] = stack;
            payout += VendorPaysForStack(snap.BuyRate, type, value);
        }
        if (payout > int.MaxValue)
            return "payout overflows";

        // The server puts the pyreals in your packs before it takes the sold items out.
        long coinStacks = (payout + PyrealMaxStack - 1) / PyrealMaxStack;
        if (coinStacks > 0 && TryGetFreeSlots(player, out int freeItems, out _) && coinStacks > freeItems)
            return $"the {payout} pyreal payout needs {coinStacks} free pack slot(s), you have {freeItems}";

        return null;
    }

    /// <summary>
    /// Build a PackableList&lt;ItemProfile&gt; in unmanaged memory and hand it to the
    /// client's gmVendorUI::SendShopEvent. Main thread only.
    /// </summary>
    private static bool SendShopEvent(VendorSnapshot snap, Txn t, uint altCurrency, out string? error)
    {
        error = null;
        if (!MainThreadGuard.IsOnMainThread()) { error = "not on AC's main thread"; return false; }
        if (_sendShopEvent == null || !_resolved || !_vtablesVerified) { error = "SendShopEvent unavailable"; return false; }
        int n = t.Ids.Length;
        if (n < 1 || n > MaxTradeEntries) { error = "bad line count"; return false; }

        int bytes = ListSize + n * NodeSize;
        IntPtr mem = Marshal.AllocHGlobal(bytes);
        try
        {
            for (int off = 0; off < bytes; off += 4)
                Marshal.WriteInt32(mem + off, 0);

            IntPtr first = mem + ListSize;
            Marshal.WriteIntPtr(mem, _listVtable);
            Marshal.WriteIntPtr(mem + ListHead, first);
            Marshal.WriteIntPtr(mem + ListTail, first + (n - 1) * NodeSize);
            Marshal.WriteInt32(mem + ListCount, n);

            for (int i = 0; i < n; i++)
            {
                int amount = t.Amounts[i];
                if (amount < 1 || amount > MaxTradeAmount) { error = $"bad amount {amount}"; return false; }
                IntPtr node = first + i * NodeSize;
                Marshal.WriteIntPtr(node, _itemProfileVtable);
                Marshal.WriteInt32(node + NodeAmount, amount);
                Marshal.WriteInt32(node + NodeIid, unchecked((int)t.Ids[i]));
                Marshal.WriteIntPtr(node + NodePwd, IntPtr.Zero);          // pwd null -> 8 bytes on the wire
                Marshal.WriteIntPtr(node + NodeNext, i < n - 1 ? node + NodeSize : IntPtr.Zero);
                Marshal.WriteIntPtr(node + NodePrev, i > 0 ? node - NodeSize : IntPtr.Zero);
            }

            _inFlightUseDoneBaseline = SmartBoxHooks.GetUseDoneSeq();
            _sendShopEvent(snap.Ui, t.VendorId, mem, altCurrency, t.IsBuy ? ShopEventBuy : ShopEventSell);
            return true;
        }
        catch (Exception ex)
        {
            error = $"SendShopEvent threw {ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Prices, slots, burden (ACE rules)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>What the player pays for one created stack worth <paramref name="stackValue"/> (ACE Vendor.GetSellCost).</summary>
    internal static long PlayerPaysForStack(float sellRate, uint itemType, long stackValue)
    {
        float rate = itemType == ItemTypePromissoryNote ? 1.15f : sellRate;
        float product = rate * stackValue;
        return Math.Max(1L, (long)Math.Ceiling(product - 0.1));
    }

    /// <summary>What the vendor pays for one item worth <paramref name="value"/> (ACE Vendor.GetBuyCost).</summary>
    internal static long VendorPaysForStack(float buyRate, uint itemType, long value)
    {
        float rate = itemType == ItemTypePromissoryNote ? 1.0f : buyRate;
        float product = rate * value;
        return Math.Max(1L, (long)Math.Floor(product + 0.1));
    }

    /// <summary>
    /// Cost / slots / burden for buying <paramref name="amount"/> of <paramref name="item"/>,
    /// the way ACE creates it: stackables in stacks of MaxStackSize, others one each.
    /// A limited listing (usually an item another player sold, which ACE hands over whole)
    /// is costed at the larger of the per-unit price and the whole listing.
    /// </summary>
    internal static void EstimateBuy(VendorItemInfo item, int amount, float sellRate,
        out long cost, out long slots, out bool containerSlot, out long burden)
    {
        long unitValue = Math.Max(0, item.UnitValue);
        long unitBurden = Math.Max(0, item.UnitBurden);
        containerSlot = item.NeedsContainerSlot;
        if (item.Stackable)
        {
            long max = item.MaxStackSize;
            long full = amount / max;
            long rem = amount % max;
            cost = full * PlayerPaysForStack(sellRate, item.ItemType, unitValue * max)
                   + (rem > 0 ? PlayerPaysForStack(sellRate, item.ItemType, unitValue * rem) : 0);
            slots = full + (rem > 0 ? 1 : 0);
        }
        else
        {
            cost = amount * PlayerPaysForStack(sellRate, item.ItemType, unitValue);
            slots = amount;
        }
        burden = amount * unitBurden;

        if (!item.Unlimited)
        {
            cost = Math.Max(cost, PlayerPaysForStack(sellRate, item.ItemType, Math.Max(0, item.Value)));
            slots = Math.Max(slots, 1);
            burden = Math.Max(burden, Math.Max(0, item.Burden));
        }
    }

    internal static int UnitPrice(VendorItemInfo item, float sellRate) =>
        (int)Math.Min(int.MaxValue, PlayerPaysForStack(sellRate, item.ItemType, Math.Max(0, item.UnitValue)));

    /// <summary>
    /// Free item slots (main pack + side packs) and container slots, like ACE's
    /// GetFreeInventorySlots / GetFreeContainerSlots, from the client's own counts.
    /// Main thread. False if the numbers can't be read.
    /// </summary>
    private static bool TryGetFreeSlots(uint player, out int freeItems, out int freeContainers)
    {
        freeItems = 0;
        freeContainers = 0;
        if (!ClientObjectHooks.TryGetObjectIntProperty(player, 6u, out int mainCap) || mainCap <= 0)
            mainCap = 102;   // same default IsFullOwnedContainer uses for the main pack
        int mainCount = ClientObjectHooks.GetNumContainedItems(player);
        if (mainCount < 0)
            return false;
        freeItems = Math.Max(0, mainCap - mainCount);

        if (!ClientObjectHooks.TryGetObjectIntProperty(player, 7u, out int containerCap) || containerCap <= 0)
            containerCap = 7;
        int containers = ClientObjectHooks.GetNumContainedContainers(player);
        if (containers < 0)
            return false;
        freeContainers = Math.Max(0, containerCap - containers);

        foreach (uint pack in OwnedDirectIds(player))
        {
            if (!ClientObjectHooks.TryGetObjectIntProperty(pack, 6u, out int cap) || cap <= 0)
                continue;   // not a pack (or a focus)
            int inside = ClientObjectHooks.GetNumContainedItems(pack);
            if (inside < 0)
                return false;
            freeItems += Math.Max(0, cap - inside);
        }
        return true;
    }

    /// <summary>ACE: burden limit is 3 x capacity; capacity = 150*Str + 30*Str per carrying aug.</summary>
    private static bool TryGetAvailableBurden(uint player, out long available)
    {
        available = 0;
        if (!ClientObjectHooks.TryGetObjectIntProperty(player, 5u /* ENCUMB_VAL */, out int carried))
            return false;
        long capacity;
        if (ClientObjectHooks.TryGetObjectIntProperty(player, 96u /* ENCUMB_CAPACITY */, out int cap) && cap > 0)
            capacity = cap;
        else if (ClientObjectHooks.TryGetObjectAttribute(player, 1u /* Strength */, 0, out uint str) && str > 0)
        {
            int augs = ClientObjectHooks.TryGetObjectIntProperty(player, 230u, out int a) && a > 0 ? a : 0;
            capacity = 150L * str + 30L * augs * str;
        }
        else
            return false;
        available = capacity * 3 - carried;
        return true;
    }

    /// <summary>Ids whose container is the player (main-pack items and side packs). Main thread.</summary>
    private static List<uint> OwnedDirectIds(uint player)
    {
        var result = new List<uint>();
        foreach (uint id in ClientObjectHooks.LiveObjectIds)
        {
            if (id == player)
                continue;
            if (ClientObjectHooks.TryGetObjectOwnershipInfo(id, out uint c, out uint w, out _) && c == player && w == 0)
                result.Add(id);
        }
        return result;
    }

    /// <summary>How many of <paramref name="wcid"/> the player carries (packs included). -1 if unreadable. Main thread.</summary>
    private static int CountOwnedWcid(uint wcid)
    {
        if (wcid == 0 || !MainThreadGuard.IsOnMainThread())
            return -1;
        uint player = ClientHelperHooks.GetPlayerId();
        uint[] live = ClientObjectHooks.LiveObjectIds;
        if (player == 0 || live.Length == 0)
            return -1;

        var packs = new HashSet<uint>();
        foreach (uint id in OwnedDirectIds(player))
            if (ClientObjectHooks.TryGetObjectIntProperty(id, 6u, out int cap) && cap > 0)
                packs.Add(id);

        long total = 0;
        foreach (uint id in live)
        {
            if (id == player || !ClientObjectHooks.TryGetObjectOwnershipInfo(id, out uint c, out uint w, out _))
                continue;
            if (w != 0 || (c != player && !packs.Contains(c)))
                continue;
            if (!ClientObjectHooks.TryGetObjectWcid(id, out uint itemWcid) || itemWcid != wcid)
                continue;
            total += ClientObjectHooks.TryGetObjectIntProperty(id, 12u, out int s) && s > 0 ? s : 1;
        }
        return (int)Math.Min(int.MaxValue, total);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Host-API fills (any thread)
    // ════════════════════════════════════════════════════════════════════════════

    public static unsafe int FillInfo(VendorInfoNative* info)
    {
        if (info == null)
            return 0;
        *info = default;
        VendorSnapshot? snap = _snapshot;
        if (snap == null)
            return 0;

        info->VendorId = snap.VendorId;
        info->Generation = snap.Generation;
        info->ShopMode = snap.ShopMode;
        info->ItemTypes = snap.ItemTypes;
        info->MinValue = snap.MinValue;
        info->MaxValue = snap.MaxValue;
        info->DealsMagic = snap.DealsMagic;
        info->BuyRate = snap.BuyRate;
        info->SellRate = snap.SellRate;
        info->AltCurrencyWcid = snap.AltCurrencyWcid;
        info->AltCurrencyServerCount = snap.AltCurrencyServerCount;
        info->AltCurrencyHave = snap.AltCurrencyWcid != 0 ? _altHave : -1;
        info->PlayerCoins = _playerCoins;
        info->ItemCount = snap.Items.Length;
        info->Flags = (TradeAvailable(snap) ? 1u : 0u) | (_inFlight != null || _pendingCount > 0 ? 2u : 0u);
        WriteAnsi(info->Name, 64, snap.Name);
        WriteAnsi(info->AltCurrencyName, 64, snap.AltCurrencyName);
        return 1;
    }

    public static unsafe int FillItems(VendorItemNative* items, int maxCount)
    {
        VendorSnapshot? snap = _snapshot;
        if (snap == null)
            return -1;
        VendorItemInfo[] src = snap.Items;
        if (items != null && maxCount > 0)
        {
            int n = Math.Min(maxCount, src.Length);
            for (int i = 0; i < n; i++)
            {
                VendorItemInfo it = src[i];
                VendorItemNative* d = items + i;
                *d = default;
                d->ObjectId = it.ObjectId;
                d->Wcid = it.Wcid;
                d->ItemType = it.ItemType;
                d->IconId = it.IconId;
                d->Amount = it.Unlimited ? -1 : it.Amount;
                d->StackSize = it.StackSize;
                d->MaxStackSize = it.MaxStackSize;
                d->Value = it.Value;
                d->UnitValue = it.UnitValue;
                d->UnitPrice = UnitPrice(it, snap.SellRate);
                d->Burden = it.Burden;
                d->Flags = (it.Unlimited ? 1u : 0u) | (it.Stackable ? 2u : 0u) | (it.NeedsContainerSlot ? 4u : 0u);
                WriteAnsi(d->Name, 64, it.Name);
            }
        }
        return src.Length;
    }

    public static unsafe int FillStatus(VendorTradeStatusNative* status)
    {
        if (status == null)
            return 0;
        *status = default;
        lock (_statusLock)
        {
            if (_stSeq == 0 && _stState == 0)
                return 0;
            status->RequestId = _stSeq;
            status->State = _stState;
            status->Result = _stResult;
            status->IsBuy = _stIsBuy ? 1 : 0;
            status->VendorId = _stVendor;
            status->EntryCount = _stCount;
            status->Estimate = (int)Math.Clamp(_stEstimate, int.MinValue, int.MaxValue);
            WriteAnsi(status->Message, 128, _stMessage);
        }
        return 1;
    }

    private static bool TradeAvailable(VendorSnapshot snap) =>
        _resolved && _vtablesVerified && snap.LayoutVerified && snap.ProfileRead;

    private static void SetStatus(Txn t, int state, int result, string message, long estimate = long.MinValue)
    {
        // Always the most recently UPDATED request; RequestId says which one. Plugins
        // trade one request at a time (wait for State 3/4 before the next), so this is
        // the request they are waiting on.
        lock (_statusLock)
        {
            if (t.Seq != _stSeq && estimate == long.MinValue)
                _stEstimate = 0;
            _stSeq = t.Seq;
            _stState = state;
            _stResult = result;
            _stIsBuy = t.IsBuy;
            _stVendor = t.VendorId;
            _stCount = t.Ids.Length;
            if (estimate != long.MinValue)
                _stEstimate = estimate;
            _stMessage = message;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  /rc vendor ...
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// /rc vendor [list|info|status|buy &lt;id|sel&gt; [n]|sell &lt;id|sel&gt; [id ...]].
    /// Replies go to the log and (capped) to chat from the main-thread drain.
    /// </summary>
    public static void HandleChatCommand(string args)
    {
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "list";
        switch (verb)
        {
            case "list":
            case "ls":
                PrintVendor(listItems: true);
                break;
            case "info":
                PrintVendor(listItems: false);
                break;
            case "status":
            case "st":
                PrintStatus();
                break;
            case "buy":
            {
                if (parts.Length < 2 || !TryParseId(parts[1], out uint id))
                {
                    Echo("[RC] usage: /rc vendor buy <id|sel> [amount]");
                    break;
                }
                int amount = 1;
                if (parts.Length >= 3 && (!int.TryParse(parts[2], out amount) || amount < 1))
                {
                    Echo("[RC] amount must be a positive number");
                    break;
                }
                uint seq = RequestBuy(0, [id], [amount], echo: true);
                if (seq != 0)
                    Echo($"[RC] vendor #{seq}: buy {amount} x {DescribeVendorItem(id)} queued");
                break;
            }
            case "sell":
            {
                if (parts.Length < 2)
                {
                    Echo("[RC] usage: /rc vendor sell <id|sel> [id ...]");
                    break;
                }
                var ids = new List<uint>();
                for (int i = 1; i < parts.Length; i++)
                {
                    if (!TryParseId(parts[i], out uint id))
                    {
                        Echo($"[RC] not an object id: {parts[i]}");
                        return;
                    }
                    ids.Add(id);
                }
                uint seq = RequestSell(0, ids.ToArray(), echo: true);
                if (seq != 0)
                    Echo($"[RC] vendor #{seq}: sell {ids.Count} item(s) queued");
                break;
            }
            default:
                Echo("[RC] /rc vendor list | info | status | buy <id|sel> [n] | sell <id|sel> [id ...]");
                break;
        }
    }

    private static void PrintVendor(bool listItems)
    {
        VendorSnapshot? snap = _snapshot;
        if (snap == null)
        {
            Echo($"[RC] no vendor open. ({_statusMessage})");
            return;
        }
        string currency = snap.AltCurrencyWcid == 0
            ? $"pyreals (you have {(_playerCoins >= 0 ? _playerCoins.ToString() : "?")})"
            : $"{Currency(snap)} wcid {snap.AltCurrencyWcid} (server says {snap.AltCurrencyServerCount}, counted {(_altHave >= 0 ? _altHave.ToString() : "?")})";
        Echo($"[RC] vendor 0x{snap.VendorId:X8} {snap.Name}: {snap.Items.Length} items, sells at {snap.SellRate:0.###}x, buys at {snap.BuyRate:0.###}x, " +
             $"types 0x{snap.ItemTypes:X8}, value {snap.MinValue}-{snap.MaxValue}, magic {snap.DealsMagic}, currency {currency}, " +
             $"trading {(TradeAvailable(snap) ? "ON" : "OFF")}, gen {snap.Generation}");
        if (!listItems)
            return;

        const int chatCap = 30;
        int shown = 0;
        foreach (VendorItemInfo it in snap.Items)
        {
            string line = $"[RC]  0x{it.ObjectId:X8} {it.Name} x{(it.Unlimited ? "inf" : it.Amount.ToString())} " +
                          $"@{UnitPrice(it, snap.SellRate)} (wcid {it.Wcid}, value {it.Value}, stack {it.StackSize}/{it.MaxStackSize}, burden {it.Burden}, type 0x{it.ItemType:X})";
            if (shown < chatCap)
            {
                Echo(line);
                shown++;
            }
            else
            {
                RynthLog.Compat(line);
            }
        }
        if (snap.Items.Length > chatCap)
            Echo($"[RC]  ...{snap.Items.Length - chatCap} more in the RynthCore log");
    }

    private static void PrintStatus()
    {
        string line;
        lock (_statusLock)
        {
            if (_stSeq == 0 && _stState == 0)
                line = "[RC] no vendor trade requested yet";
            else
            {
                string state = _stState switch { 1 => "queued", 2 => "sent", 3 => "done", 4 => "refused", _ => "?" };
                line = $"[RC] vendor #{_stSeq} {(_stIsBuy ? "buy" : "sell")} {state}" +
                       (_stState == 3 ? $" (result {_stResult})" : string.Empty) +
                       $", {_stCount} line(s), est {_stEstimate}: {_stMessage}";
            }
        }
        Echo(line);
    }

    private static bool TryParseId(string token, out uint id)
    {
        id = 0;
        if (token.Equals("sel", StringComparison.OrdinalIgnoreCase) || token.Equals("selected", StringComparison.OrdinalIgnoreCase))
        {
            id = ClientHelperHooks.GetSelectedItemId();
            return id != 0;
        }
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(token.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out id) && id != 0;
        if (uint.TryParse(token, out id) && id != 0)
            return true;
        return uint.TryParse(token, System.Globalization.NumberStyles.HexNumber, null, out id) && id != 0;
    }

    /// <summary>Queue a chat line (also logged). Written by DrainEcho on the main thread.</summary>
    private static void Echo(string line)
    {
        RynthLog.Compat(line);
        lock (_echoLock)
        {
            if (_echo.Count >= MaxEcho)
                return;
            _echo.Enqueue(line);
            _echoCount = _echo.Count;
        }
    }

    private static void DrainEcho()
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;
        while (true)
        {
            string line;
            lock (_echoLock)
            {
                if (_echo.Count == 0) { _echoCount = 0; return; }
                line = _echo.Dequeue();
                _echoCount = _echo.Count;
            }
            try { ClientHelperHooks.WriteToChat(line, 1); } catch { }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════════════════

    private static string Currency(VendorSnapshot snap) =>
        snap.AltCurrencyWcid == 0 ? "pyreals" : (snap.AltCurrencyName.Length > 0 ? snap.AltCurrencyName : $"wcid {snap.AltCurrencyWcid}");

    private static string DescribeVendorItem(uint id)
    {
        VendorItemInfo? it = _snapshot?.Find(id);
        return it != null && it.Name.Length > 0 ? $"{it.Name} (0x{id:X8})" : $"0x{id:X8}";
    }

    private static string DescribeObject(uint id) =>
        ClientObjectHooks.TryGetObjectName(id, out string n) && n.Length > 0 ? $"{n} (0x{id:X8})" : $"0x{id:X8}";

    private static string DescribeLines(Txn t, VendorSnapshot snap)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < t.Ids.Length && i < 12; i++)
        {
            if (i > 0) sb.Append(", ");
            string name = t.IsBuy ? (snap.Find(t.Ids[i])?.Name ?? string.Empty) : string.Empty;
            sb.Append(t.Amounts[i]).Append("x 0x").Append(t.Ids[i].ToString("X8"));
            if (name.Length > 0) sb.Append(' ').Append(name);
        }
        if (t.Ids.Length > 12) sb.Append(", ...");
        return sb.ToString();
    }

    private static bool RateLooksSane(float rate) => float.IsFinite(rate) && rate > 0f && rate < 1000f;

    private static bool InModule(IntPtr p)
    {
        if (p == IntPtr.Zero || _moduleBase == IntPtr.Zero || _moduleSize <= 0)
            return false;
        long v = (uint)p.ToInt32();
        long start = (uint)_moduleBase.ToInt32();
        return v >= start && v < start + _moduleSize;
    }

    /// <summary>Both ends of [p, p+size) committed and readable (spans here are &lt; one page).</summary>
    private static bool Readable(IntPtr p, int size) =>
        p != IntPtr.Zero && ClientObjectHooks.IsReadablePointer(p) && ClientObjectHooks.IsReadablePointer(p + (size - 1));

    private static uint ReadU32(IntPtr p) => unchecked((uint)Marshal.ReadInt32(p));

    /// <summary>
    /// AC1Legacy::PStringBase&lt;char&gt; at <paramref name="field"/>: pointer to PSRefBuffer
    /// { vfptr, refcount, m_len (incl. NUL), m_size, m_hash, m_data[] } — same reader as
    /// ClientObjectHooks.TryReadPwdString.
    /// </summary>
    private static bool TryReadLegacyString(IntPtr field, int maxLen, out string value)
    {
        value = string.Empty;
        try
        {
            if (!Readable(field, 4)) return false;
            IntPtr buffer = Marshal.ReadIntPtr(field);
            if (buffer == IntPtr.Zero || !Readable(buffer, 20)) return false;
            int rawLen = Marshal.ReadInt32(buffer + 8);
            if (rawLen <= 1 || rawLen > maxLen) return false;
            IntPtr data = buffer + 20;
            if (!Readable(data, rawLen - 1)) return false;
            value = Marshal.PtrToStringAnsi(data, rawLen - 1) ?? string.Empty;
            int nul = value.IndexOf('\0');
            if (nul >= 0) value = value.Substring(0, nul);
            return value.Length > 0;
        }
        catch
        {
            value = string.Empty;
            return false;
        }
    }

    private static unsafe void WriteAnsi(byte* dest, int capacity, string? text)
    {
        int n = 0;
        if (!string.IsNullOrEmpty(text))
        {
            for (; n < text.Length && n < capacity - 1; n++)
            {
                char c = text[n];
                dest[n] = c < 256 ? (byte)c : (byte)'?';
            }
        }
        for (int i = n; i < capacity; i++)
            dest[i] = 0;
    }
}

// ============================================================================
//  RynthCore.Engine - Compatibility/AcShortcuts.cs
//
//  The player's shortcut slots, AC's own (Tom 2026-10-22: the keys a plugin's
//  item bar shows must be the ones the player had in retail, work the same,
//  and change together with the retail toolbar). Plugin API v90:
//  GetShortcuts (what is in the 18 slots) and ShortcutAction (use a slot, put
//  an item in one, empty one), for RynthDefective's bar.
//
//  Where they live (Ghidra symbols + disassembly of the 4,841,472-byte client):
//    CPlayerSystem (CharacterManagementHooks) + 0x34 is the PlayerModule
//    (gmToolbarUI::AddShortcut: lea ecx,[eax+0x34]; PlayerModule::AddShortCut);
//    its +0x04 is the ShortCutManager (0x4C bytes: a vtable, then 18 slot
//    pointers, ShortCutManager::AddShortCut keeps the index in 0..0x11); a slot
//    is a ShortCutData (0x10 bytes: vtable, +4 its index, +8 the object id,
//    +0xC a spell id). That list is what the server saved and what the retail
//    toolbar shows.
//
//  Doing, through the retail toolbar's own functions on the gmToolbarUI that
//  holds the slots (RetailToolbarHooks.ShortcutToolbar), shown or hidden:
//    use    gmToolbarUI::UseShortcut(slot, 1)         0x004BDFE0, ret 8: what
//           AC's UseQuickSlot_N key does (ListenToGlobalMessage).
//    set    gmToolbarUI::CreateShortcutToItem(id, slot, 0, 0)  0x004BE750,
//           ret 0x10: what retail's own "make a shortcut" does: AC's checks
//           (shortcut eligible, owned by the player; it says why not, in AC's
//           words), the item out of any slot it is in already (RemoveShortcut:
//           it moves, as dragging it does), then AddShortcut(id, slot, 1) at
//           0x004BE630: the slot's list, the server (Event_AddShortCut) and the
//           PlayerModule's list. The third argument 0: an item not carried is
//           never picked up for it (retail's key passes 1).
//    clear  gmToolbarUI::RemoveShortcutInSlotNum(slot, 1)  0x004BE180, ret 8.
//  Queued from any thread and run on AC's game thread just before its tick
//  (GameTickHooks, beside the cast drain: AC's own input -> UseTime order),
//  dropped after 6 s. Not held between worlds: the keys aren't either. The slots are read on the game thread after the tick, 4 times a second,
//  into a snapshot GetShortcuts returns from any thread.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static unsafe class AcShortcuts
{
    public const int SlotCount = 18;

    private const int PlayerModuleOffset = 0x34, ShortCutManagerOffset = 0x04, SlotArrayOffset = 0x04;
    private const int SlotObjectOffset = 0x08, SlotSpellOffset = 0x0C, SlotDataSize = 0x10;

    private const int UseFallbackVa = 0x004BDFE0, CreateFallbackVa = 0x004BE750, RemoveFallbackVa = 0x004BE180;

    // gmToolbarUI::UseShortcut(int slot, bool): range checks against +0x63C, the slot's list at +0x634.
    private static readonly byte?[] UsePattern =
    [
        0x8B, 0x44, 0x24, 0x04, 0x85, 0xC0, 0x0F, 0x8C, null, null, null, null,
        0x3B, 0x81, 0x3C, 0x06, 0x00, 0x00, 0x0F, 0x8D, null, null, null, null,
        0x8B, 0x89, 0x34, 0x06, 0x00, 0x00, 0x8B, 0x0C, 0x81
    ];

    // gmToolbarUI::CreateShortcutToItem(uint id, int slot, bool pickUp, bool quiet): a 0x9C frame, ebp = the
    // object (0 = nothing to do), esi = this, then GetWeenieObject(id); ret 0x10.
    private static readonly byte?[] CreatePattern =
    [
        0x81, 0xEC, 0x9C, 0x00, 0x00, 0x00, 0x55, 0x8B, 0xAC, 0x24, 0xA4, 0x00, 0x00, 0x00, 0x85, 0xED,
        0x56, 0x8B, 0xF1, 0x0F, 0x84, null, null, null, null, 0x53, 0x57, 0x55, 0xE8
    ];

    // gmToolbarUI::RemoveShortcutInSlotNum(int slot, bool save): edi = this, against +0x63C.
    private static readonly byte?[] RemovePattern =
    [
        0x8B, 0x44, 0x24, 0x04, 0x85, 0xC0, 0x56, 0x57, 0x8B, 0xF9, 0x7C, null,
        0x3B, 0x87, 0x3C, 0x06, 0x00, 0x00, 0x7D, null
    ];

    private static IntPtr _use, _create, _remove;
    private static bool _initialized;
    private static string _status = "Not initialized.";
    public static string Status => _status;
    public static bool CanUse => _use != IntPtr.Zero;
    public static bool CanEdit => _create != IntPtr.Zero && _remove != IntPtr.Zero;

    /// <summary>Resolves the three toolbar functions (pattern only: a guessed address is never called).</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
        {
            _status = "acclient.exe not available.";
            return;
        }
        IntPtr Resolve(string name, byte?[] pattern, int fallback)
        {
            var r = HookResolver.Resolve(text, "AcShortcuts." + name, pattern, fallback);
            return r.Success && r.Source == HookResolver.ResolveSource.PatternScan ? r.Address : IntPtr.Zero;
        }
        _use = Resolve("gmToolbarUI::UseShortcut", UsePattern, UseFallbackVa);
        _create = Resolve("gmToolbarUI::CreateShortcutToItem", CreatePattern, CreateFallbackVa);
        _remove = Resolve("gmToolbarUI::RemoveShortcutInSlotNum", RemovePattern, RemoveFallbackVa);
        _status = $"use {(_use != IntPtr.Zero ? "ok" : "missing")}, set {(_create != IntPtr.Zero ? "ok" : "missing")}, clear {(_remove != IntPtr.Zero ? "ok" : "missing")}.";
        RynthLog.Compat("AcShortcuts: " + _status);
    }

    // ── The slots (snapshot, read on the game thread after its tick) ─────────

    /// <summary>One slot: the object in it (0 = empty) and a spell id (0 for an item).</summary>
    public readonly record struct Slot(uint ObjectId, uint SpellId);

    private static volatile Slot[] _slots = Array.Empty<Slot>();
    private static long _nextReadMs;

    /// <summary>The 18 slots as last read (empty out of the world). Any thread.</summary>
    public static Slot[] Slots => _slots;

    /// <summary>Game thread, after AC's tick: the slots again, 4 times a second.</summary>
    public static void ReadTick()
    {
        long now = Environment.TickCount64;
        if (now < _nextReadMs) return;
        _nextReadMs = now + 250;
        if (!LoginLifecycleHooks.HasObservedLoginComplete) { _slots = Array.Empty<Slot>(); return; }
        Slot[]? read = ReadSlots();
        if (read == null) return;
        Slot[] old = _slots;
        if (old.Length == read.Length)
        {
            bool same = true;
            for (int i = 0; i < read.Length && same; i++) same = old[i] == read[i];
            if (same) return;
        }
        _slots = read;
    }

    private static Slot[]? ReadSlots()
    {
        IntPtr ps = CharacterManagementHooks.PlayerSystemOnMainThread();
        if (ps == IntPtr.Zero) return null;
        IntPtr module = ps + PlayerModuleOffset;
        if (!ClientObjectHooks.IsReadableSpan(module, ShortCutManagerOffset + 4)) return null;
        IntPtr mgr = Marshal.ReadIntPtr(module, ShortCutManagerOffset);
        var slots = new Slot[SlotCount];
        if (mgr == IntPtr.Zero) return slots;   // no shortcuts made yet: all empty
        if (!ClientObjectHooks.IsReadableSpan(mgr, SlotArrayOffset + SlotCount * 4)) return null;
        for (int i = 0; i < SlotCount; i++)
        {
            IntPtr entry = Marshal.ReadIntPtr(mgr, SlotArrayOffset + i * 4);
            if (entry == IntPtr.Zero || !ClientObjectHooks.IsReadableSpan(entry, SlotDataSize)) continue;
            slots[i] = new Slot(unchecked((uint)Marshal.ReadInt32(entry, SlotObjectOffset)),
                                unchecked((uint)Marshal.ReadInt32(entry, SlotSpellOffset)));
        }
        return slots;
    }

    // ── Doing (queued; run on the game thread before its tick) ───────────────

    public enum Op { Use = 0, Set = 1, Clear = 2 }

    private readonly record struct Request(Op Op, int Slot, uint ObjectId, long QueuedMs);

    private static readonly ConcurrentQueue<Request> Queue = new();
    private const int MaxQueued = 16;
    private const long GiveUpMs = 6000;

    /// <summary>
    /// Queues <paramref name="op"/> on slot <paramref name="slot"/> (0-17); Set needs the object.
    /// False when it can't be done here (out of range, the function wasn't found, a full queue).
    /// Any thread.
    /// </summary>
    public static bool Enqueue(Op op, int slot, uint objectId)
    {
        if (slot < 0 || slot >= SlotCount) return false;
        if (op == Op.Use ? !CanUse : !CanEdit) return false;
        if (op == Op.Set && objectId == 0) return false;
        if (Queue.Count >= MaxQueued) return false;
        Queue.Enqueue(new Request(op, slot, objectId, Environment.TickCount64));
        return true;
    }

    /// <summary>Game thread, before AC's tick. Alloc-free when nothing is queued; never throws.</summary>
    public static void Drain()
    {
        if (Queue.IsEmpty) return;
        long now = Environment.TickCount64;
        // Not held between worlds (portal space, the seconds after landing): a slot pressed then does
        // what its key does then - AC's own toolbar call, which AC answers or refuses itself. Held,
        // a click in portal space did nothing (Tom 2026-10-23). Logout empties the queue (Reset).
        IntPtr toolbar = RetailToolbarHooks.ShortcutToolbar();
        while (Queue.TryDequeue(out Request r))
        {
            if (now - r.QueuedMs > GiveUpMs) continue;
            if (toolbar == IntPtr.Zero)
            {
                RynthLog.Compat($"AcShortcuts: {r.Op} slot {r.Slot + 1} dropped: the shortcut toolbar isn't found.");
                continue;
            }
            try
            {
                switch (r.Op)
                {
                    case Op.Use:
                        ((delegate* unmanaged[Thiscall]<IntPtr, int, int, void>)_use)(toolbar, r.Slot, 1);
                        break;
                    case Op.Set:
                        ((delegate* unmanaged[Thiscall]<IntPtr, uint, int, int, int, void>)_create)(toolbar, r.ObjectId, r.Slot, 0, 0);
                        RynthLog.Compat($"AcShortcuts: slot {r.Slot + 1} = 0x{r.ObjectId:X8}.");
                        break;
                    case Op.Clear:
                        ((delegate* unmanaged[Thiscall]<IntPtr, int, int, void>)_remove)(toolbar, r.Slot, 1);
                        RynthLog.Compat($"AcShortcuts: slot {r.Slot + 1} emptied.");
                        break;
                }
            }
            catch (Exception ex)
            {
                RynthLog.Compat($"AcShortcuts: {r.Op} slot {r.Slot + 1} threw {ex.GetType().Name}: {ex.Message}");
            }
            _nextReadMs = 0;   // show the change on the next read
        }
    }

    /// <summary>Logout / relog: nothing queued for the old character, nothing shown.</summary>
    public static void Reset()
    {
        while (Queue.TryDequeue(out _)) { }
        _slots = Array.Empty<Slot>();
    }
}

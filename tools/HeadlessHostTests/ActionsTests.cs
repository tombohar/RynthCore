// ============================================================================
//  ActionsTests - the ACTIONS area: each action's game action (type + payload), the
//  busy count with its events, use-done / weenie-error sequences, merge results,
//  selection, chat (InvokeChatParser through a plugin's OnChatBarEnter, WriteToChat
//  echo), confirmations, jumps and manual driving (autorun + heading, turning, stops,
//  living with a planned walk and the server's move-to).
// ============================================================================

using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RynthCore.Headless.Host;
using RynthCore.Headless.Messages;
using RynthCore.Headless.Session;
using RynthCore.Headless.World;
using RynthCore.PluginSdk;

namespace HeadlessHostTests;

internal static unsafe class ActionsTests
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() => Suites.Add("actions", Run, order: 30);

    private const uint TurnRight = 0x6500000D, WalkBackwards = 0x45000006;

    private static Fixture F => Fixture.Shared;
    private static RynthCoreApiNative* Api => F.Api;
    private static uint Me => F.Session.World.Player.Id;

    private static void Run()
    {
        WieldOnUse();
        PickUpOnUseInCorpse();
        SalvageDirect();
        Fixture f = F;
        using var _ = f.Context.Enter();
        T.Check(f.Session.State == SessionState.InWorld, "session in world");

        Combat();
        BusyAndUseDone();
        Errors();
        Inventory();
        Merge();
        Selection();
        Chat();
        Confirmation();
        Jumps();
        Drive();
        DriveAndWalks();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static int I32(byte[] b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
    private static float F32(byte[] b, int at) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at));

    private static string S16(byte[] b, ref int at)
    {
        int n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
        string s = Encoding.Latin1.GetString(b, at + 2, n);
        at += 2 + n;
        at = (at + 3) & ~3;
        return s;
    }

    /// <summary>Exactly one action of <paramref name="type"/> sent since the mark (and nothing else): its payload.</summary>
    private static byte[] One(int mark, uint type, string what)
    {
        var sent = F.ActionsSince(mark);
        T.Check(sent.Count == 1 && sent[0].Type == type,
            $"{what}: one 0x{type:X4} sent (got {string.Join(", ", sent.Select(s => $"0x{s.Type:X4}"))})");
        return sent.Count > 0 ? sent[^1].Payload : Array.Empty<byte>();
    }

    private static byte[] GameEvent(uint type, byte[] body)
    {
        var b = new byte[16 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0xF7B0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), Me);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), type);
        body.CopyTo(b, 16);
        return b;
    }

    private static byte[] U32s(params uint[] v)
    {
        var b = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), v[i]);
        return b;
    }

    private static byte[] UseDone(uint error) => GameEvent(0x01C7, U32s(error));

    private static List<PluginEvent> Drain() => F.Context.Events.Drain();

    /// <summary>A stub object (no ObjectCreate) in a made-up container, so the real world model is untouched.</summary>
    private static WorldObject Stub(uint id)
    {
        F.Receive(GameEvent(0x0022, U32s(id, 0x7FFF00FF, 0, 0)));
        return F.Session.World.Get(id)!;
    }

    private static void Advance(long ms, long step = 50)
    {
        for (long t = 0; t < ms; t += step)
        {
            F.Now += step;
            F.Session.Tick();
        }
    }

    // Not equipment: "use" on equipment is a wield, as in the retail client (checked in WieldOnUse).

    /// <summary>"Use" on carried equipment wields it (the retail client's behaviour); on wielded equipment it unwields.</summary>
    private static void WieldOnUse()
    {
        using var _ = F.Context.Enter();
        var w = F.Session.World;
        var gear = w.Inventory().FirstOrDefault(o => o.WielderId == 0 && (o.Desc.ValidLocations ?? 0) != 0 && (o.Desc.ValidLocations & 0xC0000) != 0xC0000);
        var worn = w.Inventory().FirstOrDefault(o => o.WielderId == Me);
        T.Check(gear != null && worn != null, "the replay has pack equipment and worn equipment");
        if (gear == null || worn == null) return;
        int m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, int>)Api->UseObjectFn)(gear.Id), 1, "UseObject on pack equipment");
        var acts = F.ActionsSince(m);
        T.Check(acts.Count == 1 && acts[0].Type == 0x001A && U32(acts[0].Payload, 0) == gear.Id && U32(acts[0].Payload, 4) == gear.Desc.ValidLocations,
            "it goes out as GetAndWieldItem with the item's locations");
        m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, int>)Api->UseObjectFn)(worn.Id), 1, "UseObject on worn equipment");
        acts = F.ActionsSince(m);
        T.Check(acts.Count == 1 && acts[0].Type == 0x0019 && U32(acts[0].Payload, 0) == worn.Id && U32(acts[0].Payload, 4) == Me,
            "it goes out as PutItemInContainer into the pack");
    }

    /// <summary>SalvageDirect: one 0x027D action (tool, count, ids) for carried items; anything else is refused.</summary>
    private static void SalvageDirect()
    {
        using var _ = F.Context.Enter();
        var carried = F.Session.World.Inventory().Where(o => !o.IsContainer && o.WielderId == 0).Select(o => o.Id).Take(3).ToArray();
        T.Check(carried.Length == 3, "three carried items to stand in for the tool and the salvage");
        if (carried.Length < 3) return;
        T.Check(Api->SalvageDirectFn != IntPtr.Zero, "SalvageDirect is filled (RynthAi's HasSalvageDirect)");
        var salvage = (delegate* unmanaged[Cdecl]<uint, uint*, int, int>)Api->SalvageDirectFn;
        uint tool = carried[0];
        uint* ids = stackalloc uint[] { carried[1], carried[2] };
        int m = F.Mark;
        T.Eq(salvage(tool, ids, 2), 2, "two items sent");
        var acts = F.ActionsSince(m);
        T.Check(acts.Count == 1 && acts[0].Type == 0x027D && U32(acts[0].Payload, 0) == tool && U32(acts[0].Payload, 4) == 2
                && U32(acts[0].Payload, 8) == carried[1] && U32(acts[0].Payload, 12) == carried[2],
            "it goes out as 0x027D: tool, count, ids");
        uint* stranger = stackalloc uint[] { carried[1], 0x80FFFFFE };
        m = F.Mark;
        T.Eq(salvage(tool, stranger, 2), 0, "an item not carried: refused");
        T.Eq(salvage(tool, ids, 0), 0, "no items: refused");
        T.Check(F.ActionsSince(m).Count == 0, "nothing sent for the refusals");
    }

    /// <summary>"Use" on an item in the open corpse picks it up into the main pack (the retail client's behaviour).</summary>
    private static void PickUpOnUseInCorpse()
    {
        using var _ = F.Context.Enter();
        var w = F.Session.World;
        var item = w.Get(FirstPackItem());
        T.Check(item != null, "a pack item to stand in for the loot");
        if (item == null) return;
        uint wasIn = item.ContainerId, wasOpen = w.OpenContainer;
        const uint corpse = 0x80004D5B;
        try
        {
            item.ContainerId = corpse;
            w.OpenContainer = corpse;
            int m = F.Mark;
            T.Eq(((delegate* unmanaged[Cdecl]<uint, int>)Api->UseObjectFn)(item.Id), 1, "UseObject on an item in the open corpse");
            var acts = F.ActionsSince(m);
            T.Check(acts.Count == 1 && acts[0].Type == 0x0019 && U32(acts[0].Payload, 0) == item.Id && U32(acts[0].Payload, 4) == Me,
                "it goes out as PutItemInContainer into the main pack, not a Use (and the busy count is untouched)");
        }
        finally
        {
            item.ContainerId = wasIn;
            w.OpenContainer = wasOpen;
        }
    }

    private static uint FirstPackItem() => F.Session.World.Inventory().FirstOrDefault(o => !o.IsContainer && o.WielderId == 0 && (o.Desc.ValidLocations ?? 0) == 0)?.Id ?? 0;

    private static uint SomeCreature() => F.Session.World.Objects.Values.FirstOrDefault(o => o.IsCreature && o.Id != Me)?.Id ?? 0x80001234;

    // ── combat + casts ──────────────────────────────────────────────────────

    private static void Combat()
    {
        uint mob = SomeCreature();
        int m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<int, int>)Api->ChangeCombatModeFn)(2), 1, "ChangeCombatMode(Melee)");
        T.Eq(U32(One(m, 0x0053, "ChangeCombatMode"), 0), 2u, "ChangeCombatMode payload");
        T.Eq(((delegate* unmanaged[Cdecl]<int, int>)Api->ChangeCombatModeFn)(3), 0, "ChangeCombatMode(3) refused");

        m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, int, float, int>)Api->MeleeAttackFn)(mob, 1, 0.75f), 1, "MeleeAttack");
        byte[] p = One(m, 0x0008, "MeleeAttack");
        T.Check(p.Length == 12 && U32(p, 0) == mob && U32(p, 4) == 1 && Math.Abs(F32(p, 8) - 0.75f) < 1e-6, "MeleeAttack payload (target, height, power)");
        T.Eq(((delegate* unmanaged[Cdecl]<uint, int, float, int>)Api->MeleeAttackFn)(0, 1, 0.5f), 0, "MeleeAttack(0) refused");

        m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, int, float, int>)Api->MissileAttackFn)(mob, 3, 0.25f), 1, "MissileAttack");
        p = One(m, 0x000A, "MissileAttack");
        T.Check(p.Length == 12 && U32(p, 0) == mob && U32(p, 4) == 3 && Math.Abs(F32(p, 8) - 0.25f) < 1e-6, "MissileAttack payload");

        m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<int>)Api->CancelAttackFn)(), 1, "CancelAttack");
        T.Eq(One(m, 0x01B7, "CancelAttack").Length, 0, "CancelAttack has no payload");

        m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, int>)Api->QueryHealthFn)(mob), 1, "QueryHealth");
        T.Eq(U32(One(m, 0x01BF, "QueryHealth"), 0), mob, "QueryHealth payload");

        // NativeAttack: the selected target, by combat mode.
        WorldObject me = F.Session.World.PlayerObject!;
        bool had = me.Ints.TryGetValue(40, out int oldMode);
        try
        {
            ((delegate* unmanaged[Cdecl]<uint, int>)Api->SetSelectedObjectIdFn)(mob);
            me.Ints[40] = 1;
            m = F.Mark;
            T.Eq(((delegate* unmanaged[Cdecl]<int, float, int>)Api->NativeAttackFn)(2, 0.5f), 0, "NativeAttack refused in NonCombat");
            T.Eq(((delegate* unmanaged[Cdecl]<int>)Api->GetCurrentCombatModeFn)(), 1, "GetCurrentCombatMode = NonCombat");
            me.Ints[40] = 2;
            T.Eq(((delegate* unmanaged[Cdecl]<int, float, int>)Api->NativeAttackFn)(2, 0.5f), 1, "NativeAttack in Melee");
            T.Eq(U32(One(m, 0x0008, "NativeAttack (melee)"), 0), mob, "NativeAttack hits the selection");
            me.Ints[40] = 4;
            m = F.Mark;
            T.Eq(((delegate* unmanaged[Cdecl]<int, float, int>)Api->NativeAttackFn)(1, 0.5f), 1, "NativeAttack in Missile");
            One(m, 0x000A, "NativeAttack (missile)");
        }
        finally
        {
            if (had) me.Ints[40] = oldMode; else me.Ints.Remove(40);
            ((delegate* unmanaged[Cdecl]<uint, int>)Api->SetSelectedObjectIdFn)(0);
        }

        // Casts: targeted 0x4A (target, spell); untargeted 0x48 (spell).
        Drain();
        m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, int, int>)Api->CastSpellFn)(mob, 27), 1, "CastSpell on a target");
        p = One(m, 0x004A, "CastSpell targeted");
        T.Check(p.Length == 8 && U32(p, 0) == mob && U32(p, 4) == 27, "CastTargeted payload (target, spell)");
        T.Eq(((delegate* unmanaged[Cdecl]<int>)Api->GetCastBusyStateFn)(), 1, "cast gate closed while the cast is out");
        T.Eq(((delegate* unmanaged[Cdecl]<int>)Api->IsPlayerReadyFn)(), 0, "not ready while casting");
        F.Receive(UseDone(0));
        T.Eq(((delegate* unmanaged[Cdecl]<int>)Api->GetCastBusyStateFn)(), 0, "cast gate open after UseDone");
        T.Eq(((delegate* unmanaged[Cdecl]<int>)Api->IsPlayerReadyFn)(), 1, "ready again");
        m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, int, int>)Api->CastSpellFn)(0, 2), 1, "CastSpell untargeted");
        T.Eq(U32(One(m, 0x0048, "CastSpell untargeted"), 0), 2u, "CastUntargeted payload");
        F.Receive(UseDone(0));
        T.Eq(((delegate* unmanaged[Cdecl]<uint, int, int>)Api->CastSpellFn)(mob, 0), 0, "CastSpell(spell 0) refused");
        var ev = Drain();
        T.Eq(ev.Count(e => e is PluginEvent.BusyIncremented), 2, "a cast counts busy");
        T.Eq(ev.Count(e => e is PluginEvent.BusyDecremented), 2, "its UseDone uncounts it");
    }

    // ── busy count, use-done ────────────────────────────────────────────────

    private static void BusyAndUseDone()
    {
        uint item = FirstPackItem();
        T.Check(item != 0, "a pack item to use");
        var getBusy = (delegate* unmanaged[Cdecl]<int>)Api->GetBusyStateFn;
        var seqFn = (delegate* unmanaged[Cdecl]<int>)Api->GetUseDoneSeqFn;
        var lastFn = (delegate* unmanaged[Cdecl]<int*, uint*, int>)Api->GetLastUseDoneFn;

        Drain();
        T.Eq(getBusy(), 0, "busy 0 at rest");
        int seq0 = seqFn();
        int m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, int>)Api->UseObjectFn)(item), 1, "UseObject");
        T.Eq(U32(One(m, 0x0036, "UseObject"), 0), item, "UseObject payload");
        T.Eq(getBusy(), 1, "busy 1 after a use");
        m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, int>)Api->UseObjectOnFn)(item, Me), 1, "UseObjectOn");
        byte[] p = One(m, 0x0035, "UseObjectOn");
        T.Check(U32(p, 0) == item && U32(p, 4) == Me, "UseObjectOn payload (source, target)");
        T.Eq(getBusy(), 2, "busy 2 after two uses");
        T.Eq(((delegate* unmanaged[Cdecl]<uint, int>)Api->UseObjectFn)(0), 0, "UseObject(0) refused");

        F.Receive(UseDone(0));
        T.Eq(getBusy(), 1, "UseDone takes one off");
        T.Eq(seqFn(), seq0 + 1, "use-done seq +1");
        F.Receive(UseDone(0x1D));
        T.Eq(getBusy(), 0, "second UseDone: busy 0");
        int s; uint e;
        T.Eq(lastFn(&s, &e), 1, "GetLastUseDone");
        T.Check(s == seq0 + 2 && e == 0x1D, $"last use-done (seq {s}, err 0x{e:X}) = (seq0+2, 0x1D)");
        F.Receive(UseDone(0));
        T.Eq(getBusy(), 0, "an extra UseDone doesn't go negative");

        var ev = Drain();
        T.Eq(ev.Count(x => x is PluginEvent.BusyIncremented), 2, "two busy-incremented events");
        T.Eq(ev.Count(x => x is PluginEvent.BusyDecremented), 2, "two busy-decremented events (none for the unpaired UseDone)");

        // Force reset: one decrement per unit.
        ((delegate* unmanaged[Cdecl]<uint, int>)Api->UseObjectFn)(item);
        ((delegate* unmanaged[Cdecl]<uint, int>)Api->UseObjectFn)(item);
        ((delegate* unmanaged[Cdecl]<uint, int>)Api->UseObjectFn)(item);
        Drain();
        ((delegate* unmanaged[Cdecl]<void>)Api->ForceResetBusyCountFn)();
        T.Eq(getBusy(), 0, "ForceResetBusyCount -> 0");
        T.Eq(Drain().Count(x => x is PluginEvent.BusyDecremented), 3, "three decrements for the three units dropped");

        // UseEquippedItem is a UseWithTarget too, and counts.
        m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, int>)Api->UseEquippedItemFn)(item, Me), 1, "UseEquippedItem");
        One(m, 0x0035, "UseEquippedItem");
        T.Eq(getBusy(), 1, "UseEquippedItem counts busy");
        F.Receive(UseDone(0));
        Drain();
    }

    private static void Errors()
    {
        var fn = (delegate* unmanaged[Cdecl]<int*, uint*, uint*, uint*, int>)Api->GetLastWeenieErrorFn;
        int s0; uint e, t, o;
        fn(&s0, &e, &t, &o);

        F.Receive(GameEvent(0x028A, U32s(0x0402)));
        int s;
        T.Eq(fn(&s, &e, &t, &o), 1, "GetLastWeenieError");
        T.Check(s == s0 + 1 && e == 0x402 && t == 0x028A && o == 0, $"WeenieError recorded (seq {s}, 0x{e:X}, evt 0x{t:X}, obj 0x{o:X})");

        var body = new List<byte>(U32s(0x051F));
        body.AddRange(new byte[] { 4, 0, (byte)'f', (byte)'u', (byte)'l', (byte)'l', 0, 0 });
        F.Receive(GameEvent(0x028B, body.ToArray()));
        fn(&s, &e, &t, &o);
        T.Check(s == s0 + 2 && e == 0x51F && t == 0x028B, "WeenieErrorWithString recorded");

        F.Receive(GameEvent(0x00A0, U32s(0x7FFF0010, 0x0029)));
        fn(&s, &e, &t, &o);
        T.Check(s == s0 + 3 && e == 0x29 && t == 0x00A0 && o == 0x7FFF0010, "InventoryServerSaveFailed recorded with its item");
    }

    // ── inventory ───────────────────────────────────────────────────────────

    private static void Inventory()
    {
        uint item = FirstPackItem();
        uint pack = 0x7FFF0020;   // a container id the server would judge; not one of ours to the client
        int m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, int, int, int>)Api->MoveItemInternalFn)(item, pack, 3, 1), 1, "MoveItemInternal");
        byte[] p = One(m, 0x0019, "MoveItemInternal");
        T.Check(U32(p, 0) == item && U32(p, 4) == pack && I32(p, 8) == 3, "MoveItemInternal payload (item, container, slot)");
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, int, int, int>)Api->MoveItemInternalFn)(item, pack, 0, 0), 0, "MoveItemInternal amount 0 refused");

        m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, int, int>)Api->PutItemInContainerFn)(item, Me, 5), 1, "PutItemInContainer");
        p = One(m, 0x0019, "PutItemInContainer");
        T.Check(U32(p, 0) == item && U32(p, 4) == Me && I32(p, 8) == 5, "PutItemInContainer payload");

        WorldObject stack = Stub(0x7FFF0030);
        stack.StackSize = 25;
        m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, int, int>)Api->MoveItemExternalFn)(stack.Id, Me, 10), 1, "MoveItemExternal part of a stack");
        p = One(m, 0x0055, "MoveItemExternal (split)");
        T.Check(U32(p, 0) == stack.Id && U32(p, 4) == Me && I32(p, 8) == 0 && I32(p, 12) == 10, "split payload (stack, container, 0, amount)");
        m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, int, int>)Api->MoveItemExternalFn)(stack.Id, Me, 0), 1, "MoveItemExternal whole");
        p = One(m, 0x0019, "MoveItemExternal (whole)");
        T.Check(U32(p, 0) == stack.Id && U32(p, 4) == Me, "whole move payload");

        m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, int, int, int>)Api->SplitStackInternalFn)(stack.Id, Me, 2, 7), 1, "SplitStackInternal");
        p = One(m, 0x0055, "SplitStackInternal");
        T.Check(U32(p, 0) == stack.Id && U32(p, 4) == Me && I32(p, 8) == 2 && I32(p, 12) == 7, "SplitStackInternal payload");

        uint npc = SomeCreature();
        m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, int, int>)Api->GiveObjectToFn)(stack.Id, npc, 0), 1, "GiveObjectTo whole stack");
        p = One(m, 0x00CD, "GiveObjectTo");
        T.Check(U32(p, 0) == npc && U32(p, 4) == stack.Id && I32(p, 8) == 25, "give payload (target, item, whole stack = 25)");

        m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, int>)Api->WieldItemFn)(item, 0x00100000), 1, "WieldItem");
        p = One(m, 0x001A, "WieldItem");
        T.Check(U32(p, 0) == item && U32(p, 4) == 0x00100000, "wield payload (item, mask)");
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, int>)Api->WieldItemFn)(item, 0), 0, "WieldItem mask 0 refused");

        m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<uint, int>)Api->CloseContainerFn)(0x7FFF0040), 1, "CloseContainer");
        T.Eq(U32(One(m, 0x0195, "CloseContainer"), 0), 0x7FFF0040u, "close payload");

        T.Eq(((delegate* unmanaged[Cdecl]<int>)Api->GetBusyStateFn)(), 0, "inventory sends don't count busy");
        T.Eq(((delegate* unmanaged[Cdecl]<uint>)Api->GetGroundContainerIdFn)(), F.Context.GroundContainer, "GetGroundContainerId");
    }

    private static void Merge()
    {
        WorldObject a = Stub(0x7FFF0050), b = Stub(0x7FFF0051);
        a.StackSize = 30; b.StackSize = 80;
        a.Desc.MaxStackSize = 100; b.Desc.MaxStackSize = 100;
        var merge = (delegate* unmanaged[Cdecl]<uint, uint, int>)Api->MergeStackInternalFn;
        var result = (delegate* unmanaged[Cdecl]<uint, uint, int*, int*, int>)Api->GetMergeStackResultFn;
        int amount, age;
        T.Eq(result(a.Id, b.Id, &amount, &age), 0, "no merge result before a merge");
        int m = F.Mark;
        T.Eq(merge(a.Id, b.Id), 1, "MergeStackInternal");
        byte[] p = One(m, 0x0054, "MergeStackInternal");
        T.Check(U32(p, 0) == a.Id && U32(p, 4) == b.Id && I32(p, 8) == 20, "merge payload (from, to, what fits = 20)");
        T.Eq(result(a.Id, b.Id, &amount, &age), 2, "merge result Sent");
        T.Eq(amount, 20, "merge result amount");
        b.StackSize = 100;
        m = F.Mark;
        T.Eq(merge(a.Id, b.Id), 0, "merge onto a full stack refused");
        T.Eq(F.ActionsSince(m).Count, 0, "nothing sent for a full target");
        T.Eq(result(a.Id, b.Id, &amount, &age), 3, "merge result TargetFull");
    }

    // ── selection ───────────────────────────────────────────────────────────

    private static void Selection()
    {
        var select = (delegate* unmanaged[Cdecl]<uint, int>)Api->SelectItemFn;
        var get = (delegate* unmanaged[Cdecl]<uint>)Api->GetSelectedItemIdFn;
        var prev = (delegate* unmanaged[Cdecl]<uint>)Api->GetPreviousSelectedItemIdFn;
        uint a = SomeCreature();
        WorldObject b = Stub(0x7FFF0060);

        F.Now += 1000;
        Drain();
        uint before = get();
        T.Eq(select(a), 1, "SelectItem");
        T.Eq(get(), a, "selected");
        var ev = Drain().OfType<PluginEvent.SelectedTargetChange>().ToList();
        T.Check(ev.Count == 1 && ev[0].Current == a && ev[0].Previous == before, "SelectedTargetChange(a, previous)");

        T.Eq(select(a), 1, "the same target again within 150 ms: still 1");
        T.Eq(Drain().OfType<PluginEvent.SelectedTargetChange>().Count(), 0, "no event for a repeat");

        T.Eq(select(b.Id), 1, "select another");
        T.Eq(get(), b.Id, "selected b");
        T.Eq(prev(), a, "previous = a");
        ev = Drain().OfType<PluginEvent.SelectedTargetChange>().ToList();
        T.Check(ev.Count == 1 && ev[0].Current == b.Id && ev[0].Previous == a, "SelectedTargetChange(b, a)");

        // The selected object leaving the world clears the selection.
        var del = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(del, 0xF747);
        BinaryPrimitives.WriteUInt32LittleEndian(del.AsSpan(4), b.Id);
        F.Receive(del);
        T.Eq(get(), 0u, "selection cleared when its object is deleted");
        ev = Drain().OfType<PluginEvent.SelectedTargetChange>().ToList();
        T.Check(ev.Count == 1 && ev[0].Current == 0 && ev[0].Previous == b.Id, "SelectedTargetChange(0, b)");

        T.Eq(((delegate* unmanaged[Cdecl]<uint, int>)Api->SetSelectedObjectIdFn)(a), 1, "SetSelectedObjectId");
        T.Eq(get(), a, "set selected");
        ((delegate* unmanaged[Cdecl]<uint, int>)Api->SetSelectedObjectIdFn)(0);
        Drain();
    }

    // ── chat ────────────────────────────────────────────────────────────────

    private static readonly List<string> ChatBarLines = new();

    /// <summary>A plugin's OnChatBarEnter: eats "/ra ..."; "/chain" sends "/ra chained" back through InvokeChatParser.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void FakeChatBarEnter(IntPtr text, int* eat)
    {
        string s = Marshal.PtrToStringUni(text) ?? "";
        ChatBarLines.Add(s);
        if (s.StartsWith("/ra ", StringComparison.Ordinal)) *eat = 1;
        if (s == "/chain")
        {
            IntPtr t = Marshal.StringToHGlobalUni("/ra chained");
            try { ((delegate* unmanaged[Cdecl]<IntPtr, int>)Fixture.Shared.Api->InvokeChatParserFn)(t); }
            finally { Marshal.FreeHGlobal(t); }
            *eat = 1;
        }
    }

    private static int Parse(string line)
    {
        IntPtr t = Marshal.StringToHGlobalUni(line);
        try { return ((delegate* unmanaged[Cdecl]<IntPtr, int>)Api->InvokeChatParserFn)(t); }
        finally { Marshal.FreeHGlobal(t); }
    }

    private static int Write(string line, int type)
    {
        IntPtr t = Marshal.StringToHGlobalUni(line);
        try { return ((delegate* unmanaged[Cdecl]<IntPtr, int, int>)Api->WriteToChatFn)(t, type); }
        finally { Marshal.FreeHGlobal(t); }
    }

    private static void Chat()
    {
        // Without plugins: straight to the server.
        int m = F.Mark;
        T.Eq(Parse("hello there"), 1, "InvokeChatParser bare text");
        byte[] p = One(m, 0x0015, "bare text -> Talk");
        int at = 0;
        T.Eq(S16(p, ref at), "hello there", "Talk text");

        m = F.Mark;
        T.Eq(Parse("/tell Bob Smith, meet at the lifestone"), 1, "/tell");
        p = One(m, 0x005D, "/tell -> Tell");
        at = 0;
        string msg = S16(p, ref at), target = S16(p, ref at);
        T.Check(msg == "meet at the lifestone" && target == "Bob Smith", $"Tell payload (message '{msg}', target '{target}')");

        m = F.Mark;
        T.Eq(Parse("/myquests"), 1, "an unknown slash command");
        at = 0;
        T.Eq(S16(One(m, 0x0015, "/myquests -> Talk"), ref at), "@myquests", "sent as an @ server command");

        m = F.Mark;
        T.Eq(Parse("/f anyone?"), 0, "/f refused headless");
        T.Eq(F.ActionsSince(m).Count, 0, "nothing sent for /f");
        m = F.Mark;
        T.Eq(Parse("/e waves"), 1, "/e");
        One(m, 0x01DF, "/e -> Emote");

        // With a plugin: offered first, eaten lines stay home.
        var host = new PluginHost(F.Context);
        try
        {
            var plugin = new PluginHost.Plugin
            {
                Path = "fake",
                Name = "FakeChat",
                Exports = new Dictionary<string, IntPtr>
                {
                    ["RynthPluginOnChatBarEnter"] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int*, void>)&FakeChatBarEnter,
                },
                Initialized = true,
            };
            var list = (List<PluginHost.Plugin>)typeof(PluginHost).GetField("_plugins", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;
            list.Add(plugin);
            ChatBarLines.Clear();

            m = F.Mark;
            T.Eq(Parse("/ra start"), 1, "a line a plugin eats");
            T.Check(ChatBarLines.SequenceEqual(new[] { "/ra start" }), "offered to the plugin's OnChatBarEnter");
            T.Eq(F.ActionsSince(m).Count, 0, "an eaten line isn't sent");

            m = F.Mark;
            T.Eq(Parse("good morning"), 1, "a line no plugin eats");
            One(m, 0x0015, "not eaten -> Talk");

            ChatBarLines.Clear();
            m = F.Mark;
            T.Eq(Parse("/chain"), 1, "a plugin chaining InvokeChatParser from its handler");
            T.Check(ChatBarLines.SequenceEqual(new[] { "/chain" }), "the chained line isn't offered again (no loop)");
            at = 0;
            T.Eq(S16(One(m, 0x0015, "chained line -> Talk"), ref at), "@ra chained", "the chained line goes to the server");
        }
        finally
        {
            F.Context.Plugins = null;
        }

        // WriteToChat: logged and echoed as chat-window text, one line per 100 ms.
        F.Now += 1000;
        Drain();
        var logged = new List<string>();
        Action<string> sink = F.Context.LogSink;
        F.Context.LogSink = logged.Add;
        try
        {
            T.Eq(Write("RynthAi: buffing", 5), 1, "WriteToChat");
            T.Eq(Write("too soon", 5), 0, "a second line within 100 ms is dropped");
            F.Now += 100;
            T.Eq(Write("bad\u0001line", 5), 0, "control characters refused");
            T.Eq(Write("later", 0x16), 1, "after 100 ms");
        }
        finally { F.Context.LogSink = sink; }
        T.Check(logged.Any(l => l.Contains("RynthAi: buffing")), "WriteToChat logged");
        var echo = Drain().OfType<PluginEvent.ChatWindowText>().ToList();
        T.Check(echo.Count == 2 && echo[0].Text == "RynthAi: buffing" && echo[0].ChatType == 5 && echo[1].ChatType == 0x16,
            "echoed to the plugins as chat-window text");
    }

    // ── confirmations, jumps ────────────────────────────────────────────────

    private static void Confirmation()
    {
        var arm = (delegate* unmanaged[Cdecl]<uint, int, int, int>)Api->ArmConfirmationFn;
        var last = (delegate* unmanaged[Cdecl]<uint*, uint*, int*, int*, int*, int>)Api->GetLastConfirmationFn;
        T.Eq(arm(0, 50, 1000), -3, "ArmConfirmation bad type");
        T.Eq(arm(5, 50, 10_000), 1, "ArmConfirmation(craft, 50%)");
        int m = F.Mark;
        F.Context.OnConfirmationRequest(5, 0x1234, "You determine that you have a 38 percent chance to succeed.");
        byte[] p = One(m, 0x0275, "declined prompt answered");
        T.Check(U32(p, 0) == 5 && U32(p, 4) == 0x1234 && U32(p, 8) == 0, "ConfirmationResponse (type, context, no)");
        uint type, ctx; int pct, outcome, age;
        int seq = last(&type, &ctx, &pct, &outcome, &age);
        T.Check(seq >= 1 && type == 5 && ctx == 0x1234 && pct == 38 && outcome == 3, $"last confirmation (seq {seq}, pct {pct}, outcome {outcome})");

        m = F.Mark;
        F.Context.OnConfirmationRequest(5, 0x1235, "You determine that you have a 90 percent chance to succeed.");
        T.Eq(F.ActionsSince(m).Count, 0, "one-shot: an unarmed prompt isn't answered");
        last(&type, &ctx, &pct, &outcome, &age);
        T.Eq(outcome, 1, "unarmed prompt: Shown");

        arm(5, 50, 10_000);
        m = F.Mark;
        F.Context.OnConfirmationRequest(5, 0x1236, "You determine that you have a 90 percent chance to succeed.");
        T.Eq(U32(One(m, 0x0275, "accepted prompt answered"), 8), 1u, "answered yes");
    }

    private static void Jumps()
    {
        int m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<int>)Api->TapJumpFn)(), 1, "TapJump");
        byte[] p = One(m, 0xF61B, "TapJump -> Jump");
        T.Check(p.Length >= 16 && F32(p, 0) == 0f && F32(p, 12) > 0f, "tap jump: extent 0, upward velocity");

        T.Eq(((delegate* unmanaged[Cdecl]<int>)Api->CommenceJumpFn)(), 1, "CommenceJump");
        F.Now += 500;
        m = F.Mark;
        T.Eq(((delegate* unmanaged[Cdecl]<int, int>)Api->DoJumpFn)(1), 1, "DoJump");
        p = One(m, 0xF61B, "DoJump -> Jump");
        T.Check(Math.Abs(F32(p, 0) - 0.5f) < 0.01f, $"half a second's charge = extent 0.5 (got {F32(p, 0)})");
    }

    // ── driving ─────────────────────────────────────────────────────────────

    private static WorldPosition Pos => F.Session.World.PlayerObject!.Position!.Value;

    private static (float X, float Y) Global(WorldPosition p) => (p.X + (p.Cell >> 24) * 192f, p.Y + ((p.Cell >> 16) & 0xFF) * 192f);

    private static List<uint> Types(int mark) => F.ActionsSince(mark).Select(a => a.Type).ToList();

    private static void Drive()
    {
        var autorun = (delegate* unmanaged[Cdecl]<int, int>)Api->SetAutoRunFn;
        var turnTo = (delegate* unmanaged[Cdecl]<float, int>)Api->TurnToHeadingFn;
        var stop = (delegate* unmanaged[Cdecl]<int>)Api->StopCompletelyFn;
        var setMotion = (delegate* unmanaged[Cdecl]<uint, int, int>)Api->SetMotionFn;
        T.Check(F.Session.World.PlayerObject?.Position != null, "player has a position");
        if (F.Session.World.PlayerObject?.Position == null) return;
        if (F.Session.Moving) F.Session.StopMoving();

        // Autorun east for 3 s.
        var start = Global(Pos);
        int m = F.Mark;
        T.Eq(autorun(1), 1, "SetAutoRun(1)");
        T.Eq(turnTo(90f), 1, "TurnToHeading(90)");
        var sent = F.ActionsSince(m);
        T.Check(sent.Count >= 1 && sent[0].Type == 0xF61C, "autorun: MoveToState first");
        if (sent.Count >= 1 && sent[0].Type == 0xF61C)
        {
            byte[] mts = sent[0].Payload;
            T.Check(U32(mts, 0) == 0x0D && U32(mts, 4) == 2 && U32(mts, 8) == MotionCommand.WalkForward && U32(mts, 12) == 2,
                $"MoveToState = run forward (flags 0x{U32(mts, 0):X})");
        }
        Advance(3000);
        var end = Global(Pos);
        float dx = end.X - start.X, dy = end.Y - start.Y;
        float speed = F.Session.MoveSpeed;
        T.Info($"3 s of autorun at {speed:F2} m/s moved ({dx:F2}, {dy:F2}); heading {ClientSession.HeadingDegrees(Pos):F1}");
        T.Check(dx > speed * 2.5f && dx < speed * 3.3f, $"moved east about 3 s x {speed:F2} m/s (dx {dx:F2})");
        T.Check(Math.Abs(dy) < 0.5f, $"not north/south (dy {dy:F2})");
        T.Check(Math.Abs(ClientSession.HeadingDegrees(Pos) - 90f) < 0.5f, "facing east");
        var types = Types(m);
        int reports = types.Count(t => t == 0xF753);
        T.Check(reports >= 5 && reports <= 9, $"AutonomousPosition about every 0.5 s ({reports} in 3 s)");
        T.Eq(types.Count(t => t == 0xF61C), 1, "one MoveToState while the keys didn't change");
        var lastReport = F.ActionsSince(m).Last(a => a.Type == 0xF753).Payload;
        T.Check(U32(lastReport, 0) == Pos.Cell && Math.Abs(F32(lastReport, 4) - Pos.X) <= speed * 0.6f, "the reports carry the new position");

        // Stop.
        m = F.Mark;
        T.Eq(stop(), 1, "StopCompletely");
        sent = F.ActionsSince(m);
        T.Check(sent.Count == 2 && sent[0].Type == 0xF61C && U32(sent[0].Payload, 0) == 0 && sent[1].Type == 0xF753,
            $"stop: MoveToState (no keys) + position (got {string.Join(", ", sent.Select(s => $"0x{s.Type:X4}"))})");
        var stopped = Global(Pos);
        m = F.Mark;
        Advance(1000);
        var after = Global(Pos);
        T.Check(Math.Abs(after.X - stopped.X) < 0.01f && Math.Abs(after.Y - stopped.Y) < 0.01f, "stays put after the stop");
        T.Eq(Types(m).Count(t => t is 0xF61C or 0xF753), 0, "nothing sent standing still");
        T.Eq(autorun(0), 1, "SetAutoRun(0) when already off");
        T.Eq(F.ActionsSince(m).Count, 0, "an unchanged autorun sends nothing");

        // Turning in place: SetMotion(TurnRight) for 1 s turns about 135 degrees.
        float h0 = ClientSession.HeadingDegrees(Pos);
        m = F.Mark;
        T.Eq(setMotion(TurnRight, 1), 1, "SetMotion(TurnRight, on)");
        Advance(1000);
        T.Eq(setMotion(TurnRight, 0), 1, "SetMotion(TurnRight, off)");
        float turned = (ClientSession.HeadingDegrees(Pos) - h0 + 360f) % 360f;
        T.Check(turned > 110f && turned < 160f, $"turned right about 135 deg in 1 s (got {turned:F1})");
        var turnSent = F.ActionsSince(m);
        T.Check(turnSent.Count >= 2 && turnSent[0].Type == 0xF61C && (U32(turnSent[0].Payload, 0) & 0x100) != 0, "turn: MoveToState with the turn command");
        T.Check(turnSent.Last(a => a.Type == 0xF61C).Payload is var lp && U32(lp, 0) == 0, "turn released: MoveToState stop");
        var turnedAt = Global(Pos);
        T.Check(Math.Abs(turnedAt.X - stopped.X) < 0.01f, "turning in place doesn't move");

        // TurnToHeading standing still: the heading goes out with the next step.
        m = F.Mark;
        turnTo(180f);
        T.Eq(F.ActionsSince(m).Count, 0, "TurnToHeading standing still: nothing yet");
        Advance(200);
        T.Check(Types(m).Contains(0xF753u), "the new heading is reported within a step");
        T.Check(Math.Abs(ClientSession.HeadingDegrees(Pos) - 180f) < 0.5f, "facing south");

        // Backwards (DoMovement / StopMovement) moves north while facing south, slower.
        var b0 = Global(Pos);
        T.Eq(((delegate* unmanaged[Cdecl]<uint, float, int, int>)Api->DoMovementFn)(WalkBackwards, 1f, 1), 1, "DoMovement(WalkBackwards)");
        Advance(1000);
        T.Eq(((delegate* unmanaged[Cdecl]<uint, int, int>)Api->StopMovementFn)(WalkBackwards, 1), 1, "StopMovement(WalkBackwards)");
        var b1 = Global(Pos);
        T.Check(b1.Y - b0.Y > speed * 0.4f && b1.Y - b0.Y < speed * 0.8f, $"backwards: north at ~0.65 x speed (dy {b1.Y - b0.Y:F2})");
        T.Check(!F.Session.Driving, "no keys held after the stop");
        T.Eq(((delegate* unmanaged[Cdecl]<uint, int, int>)Api->SetMotionFn)(0x10000057, 1), 0, "an unsupported motion (an emote) refused");
    }

    private static void DriveAndWalks()
    {
        var autorun = (delegate* unmanaged[Cdecl]<int, int>)Api->SetAutoRunFn;
        var stop = (delegate* unmanaged[Cdecl]<int>)Api->StopCompletelyFn;
        ClientSession s = F.Session;
        if (s.World.PlayerObject?.Position == null) return;

        // A planned walk that starts while driving ends the drive (its stop first, then the walk's run).
        autorun(1);
        Advance(300);
        WorldPosition here = Pos;
        WorldPosition goal = here with { X = here.X + (here.X < 96 ? 20 : -20) };
        int m = F.Mark;
        s.WalkTo(goal);
        Advance(200);
        T.Check(!s.Driving && s.Moving, "WalkTo takes over from the drive");
        var mts = F.ActionsSince(m).Where(a => a.Type == 0xF61C).Select(a => U32(a.Payload, 0)).ToList();
        T.Check(mts.Count == 2 && mts[0] == 0 && mts[1] == 0x0D, $"drive stop, then the walk's run ({string.Join(", ", mts.Select(x => $"0x{x:X}"))})");

        // A drive key cancels the planned walk.
        m = F.Mark;
        T.Eq(autorun(1), 1, "SetAutoRun(1) during a walk");
        T.Check(!s.Moving && s.Driving, "the drive cancels the walk");
        mts = F.ActionsSince(m).Where(a => a.Type == 0xF61C).Select(a => U32(a.Payload, 0)).ToList();
        T.Check(mts.Count == 1 && mts[0] == 0x0D, $"just the drive's run goes out ({string.Join(", ", mts.Select(x => $"0x{x:X}"))})");
        stop();

        // The server's move-to pauses the drive: no MoveToState while it lasts, the keys go out once it ends.
        MethodInfo begin = typeof(ClientSession).GetMethod("BeginMove", BindingFlags.NonPublic | BindingFlags.Instance)!;
        begin.Invoke(s, new object[] { 0u, new[] { Pos with { X = Pos.X + (Pos.X < 96 ? 30 : -30) } }, 0.3f, 15_000L, true });
        T.Check(s.ServerMoving, "server move-to in progress");
        m = F.Mark;
        T.Eq(autorun(1), 1, "SetAutoRun(1) during the server's move-to");
        Advance(600);
        var during = Types(m);
        T.Eq(during.Count(t => t == 0xF61C), 0, "no MoveToState during the server's move-to");
        T.Check(during.Contains(0xF753u), "the server's move-to still reports positions");
        m = F.Mark;
        s.StopMoving();
        Advance(200);
        T.Check(!s.Moving && s.Driving, "move-to over, the drive resumes");
        mts = F.ActionsSince(m).Where(a => a.Type == 0xF61C).Select(a => U32(a.Payload, 0)).ToList();
        T.Check(mts.Count == 1 && mts[0] == 0x0D, "the held keys go out once the move-to ends");
        stop();
        Advance(100);
        T.Check(!s.Driving && !s.Moving, "all stopped");
    }
}

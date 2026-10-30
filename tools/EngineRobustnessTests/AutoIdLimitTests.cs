using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Net;
using V = RynthCore.Engine.Compatibility.AutoIdPolicy.Verdict;
using C = RynthCore.Engine.Compatibility.AutoIdPolicy.Class;

namespace EngineRobustnessTests;

/// <summary>
/// AutoId limiting: the mode decision table, the post-transition defer, where an
/// object is, and the engine-send marker that tags every engine 0xC8.
/// </summary>
internal static partial class Program
{
    private static void RunAutoIdLimitTests()
    {
        AutoIdDecisionTable();
        AutoIdDecisionSpotCases();
        AutoIdDeferWindow();
        AutoIdLoginDefer();
        AutoIdInventoryRate();
        AutoIdClassify();
        AutoIdModeParse();
        IdMarkerScopes();
        IdMarkerRidesTheQueue();
        IdHeartbeatNames();
    }

    // ---- the decision table ---------------------------------------------------------

    private static readonly long[] Ages = { 0, AutoIdPolicy.MinAgeMs - 1, AutoIdPolicy.MinAgeMs, 60_000, AutoIdPolicy.UnknownMaxWaitMs - 1, AutoIdPolicy.UnknownMaxWaitMs };

    /// <summary>The rule as the spec states it, written out independently of AutoIdPolicy.Decide.</summary>
    private static V Expected(AutoIdMode mode, C cls, long age, bool defer, bool openedAfter, bool fresh, bool cached, bool rateOk, bool warm, bool combat)
    {
        if (mode == AutoIdMode.Off) return V.SkipMode;
        if (mode == AutoIdMode.All) return cls == C.Target && fresh ? V.SkipFresh : V.Send;
        bool young = age < AutoIdPolicy.MinAgeMs;
        return cls switch
        {
            C.Target => fresh ? V.SkipFresh : V.Send,
            C.Container => fresh ? V.SkipFresh : young ? V.WaitAge : defer && !openedAfter ? V.WaitDefer : V.Send,
            C.Inventory => cached ? V.SkipFresh : young ? V.WaitAge : defer ? V.WaitDefer : combat ? V.WaitCombat : !rateOk ? V.WaitRate : V.Send,
            C.Unknown => young ? V.WaitAge : !warm && age < AutoIdPolicy.UnknownMaxWaitMs ? V.WaitSnapshot : V.SkipMode,
            _ => V.SkipMode,
        };
    }

    private static void AutoIdDecisionTable()
    {
        _case = "autoid decision table";
        int rows = 0, wrong = 0;
        foreach (AutoIdMode mode in Enum.GetValues<AutoIdMode>())
        foreach (C cls in Enum.GetValues<C>())
        foreach (long age in Ages)
        foreach (bool defer in new[] { false, true })
        foreach (bool opened in new[] { false, true })
        foreach (bool cached in new[] { false, true })
        foreach (bool fresh in new[] { false, true })
        foreach (bool rateOk in new[] { false, true })
        foreach (bool warm in new[] { false, true })
        foreach (bool combat in new[] { false, true })
        {
            if (fresh && !cached) continue;   // fresh implies cached
            rows++;
            V got = AutoIdPolicy.Decide(mode, cls, age, defer, opened, fresh, cached, rateOk, warm, combat);
            V want = Expected(mode, cls, age, defer, opened, fresh, cached, rateOk, warm, combat);
            if (got != want && wrong++ < 10)
                Check(false, $"{mode} {cls} age={age} defer={defer} opened={opened} cached={cached} fresh={fresh} rateOk={rateOk} warm={warm} combat={combat}: want {want}, got {got}");
        }
        Check(wrong == 0, $"{wrong} of {rows} rows wrong");
        Check(rows == 3 * 5 * 6 * 2 * 2 * 3 * 2 * 2 * 2, $"row count {rows}");
    }

    /// <summary>The same rules as named cases, so a mutation is easy to read off a failure.</summary>
    private static void AutoIdDecisionSpotCases()
    {
        _case = "autoid spot cases";
        const long Old = 10_000;
        // Off: nothing at all, the selected target included.
        Check(AutoIdPolicy.Decide(AutoIdMode.Off, C.Target, 0, false, false, false, false) == V.SkipMode, "off skips the target");
        Check(AutoIdPolicy.Decide(AutoIdMode.Off, C.Container, Old, false, true, false, false) == V.SkipMode, "off skips an open container");
        // All: the old behaviour.
        Check(AutoIdPolicy.Decide(AutoIdMode.All, C.World, 0, true, false, false, false) == V.Send, "all sends a fresh-born world object, deferred or not");
        Check(AutoIdPolicy.Decide(AutoIdMode.All, C.Target, 0, false, false, true, true) == V.SkipFresh, "all keeps the target fresh skip");
        Check(AutoIdPolicy.Decide(AutoIdMode.All, C.Inventory, 0, false, false, false, true) == V.Send, "all sends a cached pack item (old behaviour)");
        // Limited: target.
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Target, 0, true, false, false, false) == V.Send, "target exempt from age and defer");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Target, 0, false, false, true, true) == V.SkipFresh, "target fresh skip");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Target, 0, false, false, false, true) == V.Send, "target cached but stale is sent");
        // Limited: world never.
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.World, Old, false, false, false, false) == V.SkipMode, "creature / ground item skipped");
        // Limited: container.
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Container, AutoIdPolicy.MinAgeMs - 1, false, true, false, false) == V.WaitAge, "container item waits for the minimum age");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Container, AutoIdPolicy.MinAgeMs, false, false, false, false) == V.Send, "container item at the minimum age is sent");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Container, Old, true, false, false, false) == V.WaitDefer, "container opened before the settle ended waits out the defer");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Container, Old, true, true, false, false) == V.Send, "container opened after the settle is not deferred");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Container, Old, false, false, true, true) == V.SkipFresh, "fresh container item skipped");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Container, Old, false, false, false, true) == V.Send, "stale cached container item re-appraised");
        // Limited: inventory.
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Inventory, Old, false, false, false, true) == V.SkipFresh, "pack item with any cached appraisal skipped");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Inventory, AutoIdPolicy.MinAgeMs - 1, false, false, false, false) == V.WaitAge, "pack item waits for the minimum age");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Inventory, Old, true, true, false, false) == V.WaitDefer, "pack item deferred (the opened-container exemption does not apply)");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Inventory, Old, false, false, false, false) == V.Send, "uncached pack item sent");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Inventory, Old, false, false, false, false, inventoryRateOk: false) == V.WaitRate, "pack item over the rate waits");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Container, Old, false, true, false, false, inventoryRateOk: false) == V.Send, "the rate cap is the inventory's only (container)");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Target, 0, true, false, false, false, inventoryRateOk: false) == V.Send, "the rate cap never holds the selected target");
        Check(AutoIdPolicy.Decide(AutoIdMode.All, C.Inventory, 0, false, false, false, false, inventoryRateOk: false) == V.Send, "All mode keeps the old behaviour (no cap)");
        // Combat: only the pack waits; the target and an open container do not.
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Inventory, Old, false, false, false, false, combatBusy: true) == V.WaitCombat, "pack item waits while in combat");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Target, 0, false, false, false, false, combatBusy: true) == V.Send, "combat never holds the selected target");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Container, Old, false, true, false, false, combatBusy: true) == V.Send, "combat never holds an open container");
        Check(AutoIdPolicy.Decide(AutoIdMode.All, C.Inventory, Old, false, false, false, false, combatBusy: true) == V.Send, "All mode: no combat hold (old behaviour)");
        // What counts as combat: a cast or an attack, never a selection alone (a hunting bot
        // nearly always has one, and the pack would never be appraised).
        Check(!AutoIdPolicy.InCombat(0x80001234, 0, false), "target selected, no cast, no attack: not combat");
        bool selectedOnly = AutoIdPolicy.InCombat(0x80001234, 0, false);
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Inventory, Old, false, false, false, false, combatBusy: selectedOnly) == V.Send, "target selected, no cast, no attack: the pack sends");
        Check(AutoIdPolicy.InCombat(0, 1, false), "a cast in flight is combat");
        Check(AutoIdPolicy.InCombat(0, 0, true), "an attack running is combat");
        Check(!AutoIdPolicy.InCombat(0, 0, false), "nothing at all: not combat");
        // Limited: unknown place.
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Unknown, 100, false, false, false, false) == V.WaitAge, "unknown place waits while young");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Unknown, Old, false, false, false, false) == V.SkipMode, "still unknown when old: skipped");
        Check(AutoIdPolicy.DefaultMode == AutoIdMode.Limited, "default mode is Limited");
    }

    // ---- the post-transition defer -----------------------------------------------------

    private static void AutoIdDeferWindow()
    {
        _case = "autoid defer window";
        const long D = AutoIdPolicy.DeferMs;
        const long S = WorldTransitionState.SettleMs;
        // Login completed long before (at 1); every time below is shifted past its defer.
        const long B = 1_000_000;
        static bool Def(long now, long settle, bool inTransition) =>
            AutoIdPolicy.InDefer(now + B, settle == 0 ? 0 : settle + B, 1, inTransition);

        // No transition yet: nothing is deferred.
        var w = new WorldTransitionState();
        w.Sample(1000, dead: false, portaling: false, landblock: 0xA9B4);
        Check(!Def(1000, w.SettleUntilMs, w.InTransition(1000)), "no transition, no defer");

        // Walking into the next landblock never defers.
        w.Sample(2000, false, false, 0xAAB4);
        w.Sample(3000, false, false, 0xAAB5);
        Check(w.SettleUntilMs == 0, "an adjacent walk sets no settle window");
        Check(!Def(3000, w.SettleUntilMs, w.InTransition(3000)), "adjacent walk: no defer");

        // A portal: deferred through portal space, the settle window, and DeferMs after it.
        w.Sample(10_000, false, true, 0xAAB5);
        Check(Def(10_000, w.SettleUntilMs, w.InTransition(10_000)), "in portal space: deferred");
        w.Sample(12_000, false, false, 0x0007);
        long settleEnd = w.SettleUntilMs;
        Check(settleEnd == 12_000 + S, $"settle ends {S} ms after the portal exit, got {settleEnd}");
        Check(Def(settleEnd + D - 1, settleEnd, w.InTransition(settleEnd + D - 1)), "deferred until DeferMs after the settle");
        Check(!Def(settleEnd + D, settleEnd, w.InTransition(settleEnd + D)), "not deferred at DeferMs after the settle");

        // A container opened after the settle ended is exempt; one opened before is not.
        Check(AutoIdPolicy.OpenedAfterSettle(settleEnd + 1, settleEnd), "opened after the settle: exempt");
        Check(!AutoIdPolicy.OpenedAfterSettle(settleEnd - 1, settleEnd), "opened during the settle: not exempt");
        Check(!AutoIdPolicy.OpenedAfterSettle(0, settleEnd), "never opened: not exempt");

        // Death defers too.
        var d = new WorldTransitionState();
        d.Sample(1000, false, false, 0x1234);
        d.Sample(2000, true, false, 0x1234);
        d.Sample(3000, false, false, 0x1234);
        Check(Def(3000 + S + D - 1, d.SettleUntilMs, false), "death: deferred");
        Check(!Def(3000 + S + D, d.SettleUntilMs, false), "death: defer ends");

        // A landblock jump without portal space defers.
        var j = new WorldTransitionState();
        j.Sample(1000, false, false, 0x00AE);
        j.Sample(1100, false, false, 0xA9B4);
        Check(Def(1100 + S + D - 1, j.SettleUntilMs, false), "landblock jump: deferred");

        // A PlayerTeleport message (a recall into the next landblock) defers.
        var t = new WorldTransitionState();
        t.Sample(1000, false, false, 0x1234);
        t.OnTeleportMessage(5000);
        Check(Def(5000 + S + D - 1, t.SettleUntilMs, false), "teleport message: deferred");
        Check(!Def(5000 + S + D, t.SettleUntilMs, false), "teleport message: defer ends");

        // In a transition right now: deferred whatever the settle time says.
        Check(Def(1, 0, inTransition: true), "in a transition: deferred");
    }

    // ---- login is a transition (a client crashed in the login create burst) ----------------

    private static void AutoIdLoginDefer()
    {
        _case = "autoid login defer";
        const long D = AutoIdPolicy.DeferMs;
        const long Login = 500_000;

        // Before login completes (the create burst): everything background is deferred.
        Check(AutoIdPolicy.InDefer(Login - 1, 0, 0, false), "login not complete: deferred");
        Check(AutoIdPolicy.InDefer(10_000_000, 0, 0, false), "login never completed: still deferred");
        // After: DeferMs past login-complete, exactly like the post-teleport defer.
        Check(AutoIdPolicy.InDefer(Login, 0, Login, false), "at login-complete: deferred");
        Check(AutoIdPolicy.InDefer(Login + D - 1, 0, Login, false), "deferred until DeferMs after login");
        Check(!AutoIdPolicy.InDefer(Login + D, 0, Login, false), "not deferred at DeferMs after login");
        // The later of login and the last settle window decides.
        Check(AutoIdPolicy.TransitionEndMs(0, Login) == Login, "no transition yet: the login is the last one");
        Check(AutoIdPolicy.TransitionEndMs(Login + 9_000, Login) == Login + 9_000, "a portal after login: the portal's settle");
        Check(AutoIdPolicy.TransitionEndMs(Login - 9_000, Login) == Login, "a portal before login: the login");
        Check(AutoIdPolicy.InDefer(Login + 9_000 + D - 1, Login + 9_000, Login, false), "portal after login: its own defer");
        Check(AutoIdPolicy.InDefer(Login + D - 1, Login - 9_000, Login, false), "a settle before login does not shorten the login defer");
        // A container the player opens after login-complete is exempt; one opened before it is not.
        Check(AutoIdPolicy.OpenedAfterSettle(Login + 1, AutoIdPolicy.TransitionEndMs(0, Login)), "opened after login: exempt");
        Check(!AutoIdPolicy.OpenedAfterSettle(Login - 1, AutoIdPolicy.TransitionEndMs(0, Login)), "opened before login completed: not exempt");
        // The decision during the login defer: pack and container items wait, the target does not.
        bool inDefer = AutoIdPolicy.InDefer(Login + 1_000, 0, Login, false);
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Inventory, 60_000, inDefer, false, false, false) == V.WaitDefer, "login: pack item waits");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Container, 60_000, inDefer, false, false, false) == V.WaitDefer, "login: container item waits");
        Check(AutoIdPolicy.Decide(AutoIdMode.Limited, C.Target, 0, inDefer, false, false, false) == V.Send, "login: the selected target is exempt");
    }

    private static void AutoIdInventoryRate()
    {
        _case = "autoid inventory rate";
        const long I = AutoIdPolicy.InventoryMinIntervalMs;
        Check(I == 200, "5 a second");
        Check(AutoIdPolicy.InventoryRateAllows(1_000, 0), "the first send is allowed");
        Check(!AutoIdPolicy.InventoryRateAllows(1_000 + I - 1, 1_000), "not again inside the interval");
        Check(AutoIdPolicy.InventoryRateAllows(1_000 + I, 1_000), "again at the interval");

        // Simulate the drain: a 100 ms timer, 3 sends a tick at most, 50 uncached pack items
        // past their age and the defer. Sends per second never pass 5.
        long last = 0;
        int pending = 50;
        var sendTimes = new List<long>();
        for (long now = 100; now <= 20_000 && pending > 0; now += 100)
        {
            int sentThisTick = 0;
            for (int k = 0; k < pending && sentThisTick < 3; k++)
            {
                bool ok = AutoIdPolicy.InventoryRateAllows(now, last);
                V v = AutoIdPolicy.Decide(AutoIdMode.Limited, C.Inventory, 60_000, false, false, false, false, ok);
                if (v != V.Send) continue;
                last = now;
                sentThisTick++;
                sendTimes.Add(now);
            }
            pending -= sentThisTick;
        }
        Check(pending == 0, $"all 50 sent eventually, {pending} left");
        int worst = 0;
        foreach (long t0 in sendTimes)
            worst = Math.Max(worst, sendTimes.Count(t => t >= t0 && t < t0 + 1_000));
        Check(worst <= 5, $"at most 5 inventory sends in any second, got {worst}");
        Check(worst == 5, $"and the cap is reached (5), got {worst}");
    }

    // ---- where an object is ------------------------------------------------------------

    private static void AutoIdClassify()
    {
        _case = "autoid classify";
        const uint Me = 0x50000001, Corpse = 0x80000100, Pack = 0x80000200, Monster = 0x80000300, Chest = 0x80000400;
        Check(AutoIdPolicy.Classify(false, Me, Corpse, Corpse, 0, 0) == C.Unknown, "not in the snapshot: unknown");
        Check(AutoIdPolicy.Classify(true, Me, 0, Me, 0, 0) == C.Inventory, "in the main pack");
        Check(AutoIdPolicy.Classify(true, Me, 0, 0, Me, 0) == C.Inventory, "worn");
        Check(AutoIdPolicy.Classify(true, Me, 0, Pack, 0, Me) == C.Inventory, "in a side pack");
        Check(AutoIdPolicy.Classify(true, Me, Pack, Pack, 0, Me) == C.Inventory, "in an opened own side pack: inventory");
        Check(AutoIdPolicy.Classify(true, Me, Corpse, Corpse, 0, 0) == C.Container, "in the open corpse");
        Check(AutoIdPolicy.Classify(true, Me, Chest, Pack, 0, Chest) == C.Container, "in a pack inside the open chest");
        Check(AutoIdPolicy.Classify(true, Me, Chest, Corpse, 0, 0) == C.World, "in a corpse that is not open");
        Check(AutoIdPolicy.Classify(true, Me, 0, 0, 0, 0) == C.World, "on the ground, nothing open");
        Check(AutoIdPolicy.Classify(true, Me, Corpse, 0, 0, 0) == C.World, "on the ground while a corpse is open (container 0 is not the open one)");
        Check(AutoIdPolicy.Classify(true, Me, Corpse, 0, Monster, 0) == C.World, "wielded by a monster");
        Check(AutoIdPolicy.Classify(true, 0, 0, 0, 0, 0) == C.World, "player unknown: nothing is inventory");
    }

    private static void AutoIdModeParse()
    {
        _case = "autoid mode parse";
        Check(AutoIdPolicy.TryParseMode("ALL", out AutoIdMode m) && m == AutoIdMode.All, "all");
        Check(AutoIdPolicy.TryParseMode(" limited ", out m) && m == AutoIdMode.Limited, "limited");
        Check(AutoIdPolicy.TryParseMode("off", out m) && m == AutoIdMode.Off, "off");
        Check(!AutoIdPolicy.TryParseMode("sometimes", out m) && m == AutoIdPolicy.DefaultMode, "unknown: false, default");
        foreach (AutoIdMode mode in Enum.GetValues<AutoIdMode>())
            Check(AutoIdPolicy.TryParseMode(AutoIdPolicy.Name(mode), out m) && m == mode, $"{mode} round-trips by name");
        foreach (AutoIdMode mode in Enum.GetValues<AutoIdMode>())
            Check(AutoIdPolicy.TryParseMode(mode.ToString(), out m) && m == mode, $"{mode} round-trips as engine.json writes it");
    }

    // ---- the engine-send marker --------------------------------------------------------

    private static void IdMarkerScopes()
    {
        _case = "id marker";
        Check(EngineSendMarker.Current == IdRequester.None, "no marker outside a scope");
        using (EngineSendMarker.Enter(IdRequester.Plugin))
        {
            Check(EngineSendMarker.Current == IdRequester.Plugin, "marked inside");
            using (EngineSendMarker.Enter(IdRequester.AutoIdContainer))
                Check(EngineSendMarker.Current == IdRequester.AutoIdContainer, "nested scope");
            Check(EngineSendMarker.Current == IdRequester.Plugin, "outer marker restored");

            IdRequester seenElsewhere = IdRequester.Self;
            var th = new Thread(() => seenElsewhere = EngineSendMarker.Current);
            th.Start();
            th.Join();
            Check(seenElsewhere == IdRequester.None, "another thread is not marked (thread-local)");
        }
        Check(EngineSendMarker.Current == IdRequester.None, "cleared after the scope");
        try
        {
            using (EngineSendMarker.Enter(IdRequester.Ui))
                throw new InvalidOperationException();
        }
        catch (InvalidOperationException) { }
        Check(EngineSendMarker.Current == IdRequester.None, "cleared after an exception");
    }

    private static void IdMarkerRidesTheQueue()
    {
        _case = "id marker queue";
        var q = new PendingIdQueue(8);
        q.Enqueue(1, 1000, 1, MergeMax, 0, (byte)IdRequester.AutoIdContainer);
        q.Enqueue(2, 1000, 1, MergeMax, 0, (byte)IdRequester.Plugin);
        Check(q.Enqueue(1, 1100, 1, MergeMax, 0, (byte)IdRequester.Plugin) == PendingIdQueue.Result.Merged, "merged");
        Check(q.TryDequeue(out var a) && a.Source == (byte)IdRequester.AutoIdContainer, "the requester rides with the entry; a merge keeps the first");
        Check(q.TryDequeue(out var b) && b.Source == (byte)IdRequester.Plugin, "second entry's requester");
        q.Enqueue(3, 1200, 1, MergeMax);
        Check(q.TryDequeue(out var c) && c.Source == 0, "unmarked stays None");
    }

    private static void IdHeartbeatNames()
    {
        _case = "id heartbeat names";
        foreach (IdRequester who in Enum.GetValues<IdRequester>())
        {
            string s = IdRequesters.Short(who) + "=";
            foreach (string bad in new[] { "fps=", "plug=", "login=", "qd=" })
                Check(!s.Contains(bad) && !bad.Contains(s), $"{who}: \"{s}\" vs {bad}");
            if (who != IdRequester.None)
                Check(IdRequesters.Label((uint)who).Length > 0, $"{who} has a flight label");
        }
        Check(IdRequesters.Label(0) == "" && IdRequesters.Label(99) == "", "no label for none or an unknown code");
        var regex = new System.Text.RegularExpressions.Regex(@"hb #\d+ .*?fps=(\d+) .*?plug=(\d+)/s .*?login=(\d)(?:.*?qd=(\d+))?");
        string sample = "autoid 60s: mode=limited sent tgt=1 ctr=2 inv=0 wld=0 plg=5 slf=0 ui=0 cli=3 tot=11 skip mode=900 age=4 defer=0 fresh=1 pend=2";
        Check(!regex.IsMatch(sample), "the minute line does not match the launcher's heartbeat regex");
    }
}

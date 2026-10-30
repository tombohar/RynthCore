// ============================================================================
//  RynthCore.Engine - Compatibility/TrainingApi.cs
//
//  Plugin API v79: spending experience the way the retail character window's
//  "+" does (docs/IMGUI_SKILLS.md "Raising"), for plugins (the RynthRemote
//  plugin, so the DrakRemote phone app can raise from the Skills tab).
//
//    GetTrainingInfo  the numbers a raise needs and what it costs: unassigned XP,
//                     skill credits, and per attribute / vital / skill the ranks,
//                     XP spent, base and buffed value, the XP for +1 and +10, how
//                     many ranks the unassigned XP buys, the credits to train.
//    Raise            n ranks of one attribute, vital or trained skill. The engine
//                     works the XP out from the same numbers (portal XpTable) and
//                     refuses unless it equals what the caller confirmed.
//    TrainSkill       an untrained skill -> trained, for exactly the SkillTable's
//                     credit price.
//
//  Sources: PlayerProgressHooks (the player's qualities, read on AC's main thread;
//  WantSlow keeps them coming every 2 s while a plugin asks) and SkillDat (the
//  portal SkillTable and XpTable, read once in the background). The sends are
//  PlayerTraining's: the client's own CM_Train senders, on AC's main thread
//  (queued in AcMainThreadQueue from any other thread).
//
//  Guards: in the world only (login complete, the world screen, no logout, no
//  teardown, the snapshot is this character's); numbers at most 3 s old (else
//  a read is asked for and Stale comes back: try again a moment later); one
//  raise at a time (Busy until the unassigned XP or the credits change, or 5 s).
//  Any thread.
// ============================================================================

using System;
using RynthCore.Engine.ImGuiBackend.Panels;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static unsafe class TrainingApi
{
    /// <summary>Numbers older than this are not raised against.</summary>
    internal const int StaleAfterMs = 3000;
    private const int BusyMs = 5000;

    public const uint KindAttribute = 1, KindVital = 2, KindSkill = 3;

    private static readonly object Gate = new();
    private static long _busyUntilMs;
    private static long _busyUnassigned;
    private static int _busyCredits;

    /// <summary>The player is in the world and its objects are live (same test as the
    /// main-thread snapshots, plus the world screen and no client cleanup). Any thread.</summary>
    public static bool InWorld =>
        LoginLifecycleHooks.HasObservedLoginComplete
        && UiFlowHooks.InWorldOrUnknown
        && !UiFlowHooks.ClientCleanupStarted
        && !LogoutLifecycleHooks.HasObservedLogout
        && !DbCacheTeardownHooks.TeardownActive
        && ClientHelperHooks.GetPlayerId() != 0;

    /// <summary>v79 GetTrainingInfo. Returns the number of entries there are (entries may be null to
    /// ask; at most maxEntries are written). 0 while there is nothing to report.</summary>
    public static int GetInfo(TrainingInfoNative* info, TrainingEntryNative* entries, int maxEntries)
    {
        PlayerProgressHooks.WantSlow();
        SkillDat.EnsureLoadQueued();

        bool inWorld = InWorld;
        uint playerId = inWorld ? ClientHelperHooks.GetPlayerId() : 0;
        PlayerProgress? s = PlayerProgressHooks.Current;
        if (s != null && (s.PlayerId != playerId || !inWorld)) s = null;
        SkillDatTables? dat = SkillDat.Tables;
        long now = Environment.TickCount64;
        long readAt = PlayerProgressHooks.LastReadMs;

        int count = 0;
        if (s != null && dat != null)
        {
            count = 6 + 3;
            for (uint id = 1; id <= PlayerProgress.MaxSkill; id++)
                if (dat.Skills.ContainsKey(id)) count++;
        }

        if (info != null && info->Size >= 8)
        {
            TrainingInfoNative o = default;
            uint flags = 0;
            if (inWorld) flags |= TrainingInfoFlags.InWorld;
            if (PlayerTraining.Available) flags |= TrainingInfoFlags.RaiseBound;
            if (PlayerTraining.TrainAvailable) flags |= TrainingInfoFlags.TrainBound;
            if (s != null) flags |= TrainingInfoFlags.HaveNumbers;
            if (dat != null) flags |= TrainingInfoFlags.HaveTables;
            if (IsBusy(s, now)) flags |= TrainingInfoFlags.Busy;
            o.Flags = flags;
            o.SnapshotVersion = s != null ? (uint)s.Version : 0;
            o.AgeMs = s != null && readAt > 0 ? (uint)Math.Clamp(now - readAt, 0, uint.MaxValue - 1) : uint.MaxValue;
            o.UnassignedXp = s?.UnassignedXp ?? -1;
            o.TotalXp = s?.TotalXp ?? -1;
            o.Level = s?.Level ?? 0;
            o.SkillCredits = s?.SkillCredits ?? -1;
            o.EntryCount = count;
            o.StaleAfterMs = StaleAfterMs;
            uint n = Math.Min(info->Size, (uint)sizeof(TrainingInfoNative));
            Buffer.MemoryCopy(&o, info, n, n);
            info->Size = n;
        }

        if (entries == null || maxEntries <= 0 || s == null || dat == null)
            return count;

        int w = 0;
        for (uint a = 1; a <= 6 && w < maxEntries; a++)
            entries[w++] = Entry(KindAttribute, a, 0, s.AttrRanks[a], s.AttrXpSpent[a], s.AttrInit[a],
                (int)s.AttrBase[a], (int)s.AttrBuffed[a], dat.Xp.AttributeXp, s.UnassignedXp);
        for (uint v = 1; v <= 3 && w < maxEntries; v++)
            entries[w++] = Entry(KindVital, v * 2 - 1, 0, s.VitalRanks[v], s.VitalXpSpent[v], s.VitalInit[v],
                (int)s.VitalBase[v], (int)s.VitalBuffed[v], dat.Xp.VitalXp, s.UnassignedXp);
        for (uint id = 1; id <= PlayerProgress.MaxSkill && w < maxEntries; id++)
        {
            if (!dat.Skills.TryGetValue(id, out SkillDatEntry? e)) continue;
            uint cls = s.SkillClass[id];
            TrainingEntryNative x = Entry(KindSkill, id, cls, s.SkillRanks[id], s.SkillXpSpent[id], s.SkillInit[id],
                s.SkillBase[id], s.SkillBuffed[id], TrainingCosts.TableFor(dat.Xp, KindSkill, cls), s.UnassignedXp);
            if (e.UsableUntrained) x.Flags |= TrainingEntryFlags.UsableUntrained;
            if (cls < 2)
            {
                x.TrainCredits = Math.Max(0, e.TrainedCost);
                if (x.TrainCredits > 0) x.Flags |= TrainingEntryFlags.Trainable;
            }
            entries[w++] = x;
        }
        return count;
    }

    private static TrainingEntryNative Entry(uint kind, uint stype, uint cls, uint ranks, uint spent, uint init,
                                             int baseValue, int buffed, uint[]? table, long unassigned)
    {
        var x = new TrainingEntryNative
        {
            Kind = kind, Stype = stype, Class = cls, Ranks = ranks, XpSpent = spent, Innate = init,
            Base = baseValue, Buffed = buffed, CostOne = -1, CostTen = -1,
        };
        if (table != null && table.Length > 0)
        {
            x.RanksLeft = TrainingCosts.RanksLeft(table, ranks);
            if (x.RanksLeft == 0) x.Flags |= TrainingEntryFlags.AtTop;
            else x.Flags |= TrainingEntryFlags.Raisable;
            x.CostOne = TrainingCosts.CostOf(table, ranks, spent, 1);
            x.CostTen = TrainingCosts.CostOf(table, ranks, spent, 10);
            x.Affordable = TrainingCosts.Affordable(table, ranks, spent, Math.Max(0, unassigned));
        }
        return x;
    }

    private static bool IsBusy(PlayerProgress? s, long now)
    {
        lock (Gate)
        {
            if (now >= _busyUntilMs) return false;
            // The server answered: the unassigned XP or the credits moved.
            if (s != null && (s.UnassignedXp != _busyUnassigned || s.SkillCredits != _busyCredits))
            {
                _busyUntilMs = 0;
                return false;
            }
            return true;
        }
    }

    /// <summary>The snapshot to act on, or why not.</summary>
    private static RaiseStatus Ready(out PlayerProgress? s, out SkillDatTables? dat)
    {
        PlayerProgressHooks.WantSlow();
        SkillDat.EnsureLoadQueued();
        s = null;
        dat = SkillDat.Tables;
        if (!InWorld) return RaiseStatus.NotInWorld;
        uint playerId = ClientHelperHooks.GetPlayerId();
        s = PlayerProgressHooks.Current;
        if (s == null || s.PlayerId != playerId || dat == null || !s.AttributeCacheRead || !s.SkillTableRead)
        {
            PlayerProgressHooks.RefreshSoon();
            return RaiseStatus.NotReady;
        }
        long read = PlayerProgressHooks.LastReadMs;
        if (read <= 0 || Environment.TickCount64 - read > StaleAfterMs)
        {
            PlayerProgressHooks.RefreshSoon();
            return RaiseStatus.Stale;
        }
        if (IsBusy(s, Environment.TickCount64)) return RaiseStatus.Busy;
        return RaiseStatus.Sent;
    }

    /// <summary>v79 Raise: <paramref name="ranks"/> ranks of one attribute (1..6), vital (1 health,
    /// 3 stamina, 5 mana) or trained/specialized skill. <paramref name="xpSent"/> = the XP sent.</summary>
    public static RaiseStatus Raise(uint kind, uint stype, uint ranks, long expectedXp, out long xpSent)
    {
        xpSent = 0;
        if (kind is < KindAttribute or > KindSkill || ranks < 1 || ranks > TrainingCosts.MaxRanksPerRaise || expectedXp < 0
            || !ValidTarget(kind, stype))
            return RaiseStatus.BadArguments;
        if (!PlayerTraining.Available) return RaiseStatus.Unavailable;

        RaiseStatus ready = Ready(out PlayerProgress? s, out SkillDatTables? dat);
        if (ready != RaiseStatus.Sent) return ready;

        uint[]? table;
        uint have, spent;
        switch (kind)
        {
            case KindAttribute:
                table = dat!.Xp.AttributeXp; have = s!.AttrRanks[stype]; spent = s.AttrXpSpent[stype];
                break;
            case KindVital:
                uint v = (stype + 1) / 2;
                table = dat!.Xp.VitalXp; have = s!.VitalRanks[v]; spent = s.VitalXpSpent[v];
                break;
            default:
                table = TrainingCosts.TableFor(dat!.Xp, KindSkill, s!.SkillClass[stype]); have = s.SkillRanks[stype]; spent = s.SkillXpSpent[stype];
                break;
        }

        RaiseStatus check = TrainingCosts.CheckRaise(table, have, spent, s.UnassignedXp, (int)ranks, expectedXp, out long cost);
        if (check != RaiseStatus.Sent) return check;

        var trainKind = kind switch
        {
            KindAttribute => PlayerTraining.TrainKind.Attribute,
            KindVital => PlayerTraining.TrainKind.Vital,
            _ => PlayerTraining.TrainKind.Skill,
        };
        AcActionTrace.Record("Raise", kind << 16 | stype, (uint)cost);
        if (!PlayerTraining.Raise(trainKind, stype, (uint)cost)) return RaiseStatus.SendFailed;
        MarkBusy(s);
        xpSent = cost;
        RynthLog.Compat($"TrainingApi: raise {trainKind} {stype} +{ranks} for {cost:N0} XP (plugin)");
        return RaiseStatus.Sent;
    }

    /// <summary>v79 TrainSkill: untrained -> trained for exactly the SkillTable's credit price.</summary>
    public static RaiseStatus TrainSkill(uint stype, int expectedCredits)
    {
        if (stype < 1 || stype > PlayerProgress.MaxSkill || expectedCredits < 0) return RaiseStatus.BadArguments;
        if (!PlayerTraining.TrainAvailable) return RaiseStatus.Unavailable;

        RaiseStatus ready = Ready(out PlayerProgress? s, out SkillDatTables? dat);
        if (ready != RaiseStatus.Sent) return ready;
        if (!dat!.Skills.TryGetValue(stype, out SkillDatEntry? e)) return RaiseStatus.NotRaisable;

        RaiseStatus check = TrainingCosts.CheckTrain(s!.SkillClass[stype], e.TrainedCost, s.SkillCredits, expectedCredits);
        if (check != RaiseStatus.Sent) return check;

        AcActionTrace.Record("TrainSkill", stype, (uint)e.TrainedCost);
        if (!PlayerTraining.TrainSkill(stype, (uint)e.TrainedCost)) return RaiseStatus.SendFailed;
        MarkBusy(s);
        RynthLog.Compat($"TrainingApi: train skill {stype} for {e.TrainedCost} credits (plugin)");
        return RaiseStatus.Sent;
    }

    private static bool ValidTarget(uint kind, uint stype) => kind switch
    {
        KindAttribute => stype >= 1 && stype <= 6,
        KindVital => stype is 1 or 3 or 5,
        KindSkill => stype >= 1 && stype <= PlayerProgress.MaxSkill,
        _ => false,
    };

    private static void MarkBusy(PlayerProgress s)
    {
        lock (Gate)
        {
            _busyUntilMs = Environment.TickCount64 + BusyMs;
            _busyUnassigned = s.UnassignedXp;
            _busyCredits = s.SkillCredits;
        }
        PlayerProgressHooks.RefreshSoon();
    }
}

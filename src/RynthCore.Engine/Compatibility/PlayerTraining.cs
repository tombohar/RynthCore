// ============================================================================
//  RynthCore.Engine - Compatibility/PlayerTraining.cs
//
//  Spending unassigned XP on an attribute, a vital or a skill (the Skills panel,
//  docs/IMGUI_SKILLS.md), through the retail client's own senders, bound the
//  way PlayerTrade binds CM_Trade:
//
//    CM_Train::Event_TrainAttribute(atype, xp)      0x006A8E90  0x0045 RaiseAttribute
//    CM_Train::Event_TrainAttribute2nd(vtype, xp)   0x006A8FA0  0x0044 RaiseVital
//    CM_Train::Event_TrainSkill(stype, xp)          0x006A90B0  0x0046 RaiseSkill
//    CM_Train::Event_TrainSkillAdvancementClass(stype, credits)
//                                                   0x006A91C0  0x0047 TrainSkill
//
//  The last is what the retail skills window's train dialog sends
//  (gmSkillUI::TrainSkillDialogCallback 0x0049C480 calls it with the skill and
//  the credits). ACE's handler (Player.HandleActionTrainSkill) only trains an
//  untrained skill, and only when the credits sent equal the portal SkillTable's
//  TrainedCost and you have them. Specializing has no game action on ACE: it
//  takes the skill's Gem of Enlightenment (SkillAlterationDevice).
//
//  bool __cdecl (Chorizite acclient map); each only builds and sends the game
//  action, in the generated CM_* sender shape Event_AddToTrade has (two
//  arguments: arg block +0x0C), so the patterns run to the opcode immediate.
//  Cut with tools/pe_pattern.py GEN and unique on both acclient copies
//  (C:\Turbine and C:\Games\RynthCore\AcClient). Bound only by a unique pattern
//  match: a miss leaves raising unavailable, never a fixed-VA call.
//
//  The server (ACE Player.HandleActionRaise*) checks the XP against unassigned XP
//  and the XP left to the top rank, and raises as many ranks as it covers.
//  Vitals are addressed by their maximum: 1 health, 3 stamina, 5 mana.
//
//  Every call runs on AC's main thread: off it (or with defer) the request goes
//  through AcMainThreadQueue (ActionKind.Train) and the drain calls back in.
//  Only in the world (TrainingApi.InWorld), checked again when the drain sends:
//  a raise queued just before a logout is dropped, not sent to the next screen.
//  The plugin API (v79) reaches these through TrainingApi, which also works the
//  XP out and checks it (TrainingCosts).
// ============================================================================

using System;

namespace RynthCore.Engine.Compatibility;

internal static unsafe class PlayerTraining
{
    internal enum TrainKind : uint
    {
        Attribute = 1,
        Vital = 2,
        Skill = 3,
        // Untrained -> trained with skill credits (the amount is credits, not XP).
        TrainWithCredits = 4,
    }

    private const int EventTrainAttributeVa = 0x006A8E90;
    private const int EventTrainAttribute2ndVa = 0x006A8FA0;
    private const int EventTrainSkillVa = 0x006A90B0;
    private const int EventTrainSkillAdvancementClassVa = 0x006A91C0;

    // The CM_* generated sender template (see PlayerTrade); the last byte is the opcode.
    private static readonly byte?[] PatEventTrainAttribute = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC6, 0x0C, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x54, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x1C, 0xC7, 0x02, 0x45 ];
    private static readonly byte?[] PatEventTrainAttribute2nd = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC6, 0x0C, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x54, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x1C, 0xC7, 0x02, 0x44 ];
    private static readonly byte?[] PatEventTrainSkill = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC6, 0x0C, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x54, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x1C, 0xC7, 0x02, 0x46 ];
    private static readonly byte?[] PatEventTrainSkillAdvancementClass = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC6, 0x0C, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x54, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x1C, 0xC7, 0x02, 0x47 ];

    private static delegate* unmanaged[Cdecl]<uint, uint, byte> _eventTrainAttribute;
    private static delegate* unmanaged[Cdecl]<uint, uint, byte> _eventTrainAttribute2nd;
    private static delegate* unmanaged[Cdecl]<uint, uint, byte> _eventTrainSkill;
    private static delegate* unmanaged[Cdecl]<uint, uint, byte> _eventTrainSkillAdvancementClass;

    /// <summary>True when all three raise senders are bound. Any thread.</summary>
    public static bool Available => _eventTrainAttribute != null && _eventTrainAttribute2nd != null && _eventTrainSkill != null;

    /// <summary>True when the train-with-credits sender is bound. Any thread.</summary>
    public static bool TrainAvailable => _eventTrainSkillAdvancementClass != null;

    /// <summary>Bind the senders. Called from ClientHelperHooks.Probe (engine init).</summary>
    public static void Probe(AcClientTextSection text)
    {
        _eventTrainAttribute = (delegate* unmanaged[Cdecl]<uint, uint, byte>)ResolveFn(HookResolver.Resolve(text, "PlayerTraining.Event_TrainAttribute", PatEventTrainAttribute, EventTrainAttributeVa));
        _eventTrainAttribute2nd = (delegate* unmanaged[Cdecl]<uint, uint, byte>)ResolveFn(HookResolver.Resolve(text, "PlayerTraining.Event_TrainAttribute2nd", PatEventTrainAttribute2nd, EventTrainAttribute2ndVa));
        _eventTrainSkill = (delegate* unmanaged[Cdecl]<uint, uint, byte>)ResolveFn(HookResolver.Resolve(text, "PlayerTraining.Event_TrainSkill", PatEventTrainSkill, EventTrainSkillVa));
        _eventTrainSkillAdvancementClass = (delegate* unmanaged[Cdecl]<uint, uint, byte>)ResolveFn(HookResolver.Resolve(text, "PlayerTraining.Event_TrainSkillAdvancementClass", PatEventTrainSkillAdvancementClass, EventTrainSkillAdvancementClassVa));
        RynthLog.Verbose($"Compat: raise senders {(Available ? "ready" : "partly bound")} (attribute={_eventTrainAttribute != null} vital={_eventTrainAttribute2nd != null} skill={_eventTrainSkill != null} train={_eventTrainSkillAdvancementClass != null}).");
    }

    // Fail closed: these send game actions, so only a unique pattern match binds.
    private static void* ResolveFn(HookResolver.ResolveResult r) =>
        r.Success && r.Source == HookResolver.ResolveSource.PatternScan ? (void*)r.Address : null;

    private static bool Bound(TrainKind kind) => kind switch
    {
        TrainKind.Attribute => _eventTrainAttribute != null,
        TrainKind.Vital => _eventTrainAttribute2nd != null,
        TrainKind.Skill => _eventTrainSkill != null,
        TrainKind.TrainWithCredits => _eventTrainSkillAdvancementClass != null,
        _ => false,
    };

    private static bool ValidTarget(TrainKind kind, uint stype) => kind switch
    {
        TrainKind.Attribute => stype >= 1 && stype <= 6,
        TrainKind.Vital => stype is 1 or 3 or 5,
        TrainKind.Skill or TrainKind.TrainWithCredits => stype >= 1 && stype <= 54,
        _ => false,
    };

    /// <summary>
    /// Spend <paramref name="xp"/> unassigned XP on one attribute (1..6), vital maximum
    /// (1 health, 3 stamina, 5 mana) or skill. Off AC's main thread, or with
    /// <paramref name="defer"/> (a UI click inside the ImGui frame), it is queued and sent
    /// from the drain. Returns false when unbound, the target is invalid or the queue is full.
    /// With TrainKind.TrainWithCredits the amount is skill credits (see <see cref="TrainSkill"/>).
    /// </summary>
    public static bool Raise(TrainKind kind, uint stype, uint xp, bool defer = false)
    {
        if (!Bound(kind) || !ValidTarget(kind, stype) || xp == 0)
            return false;
        if (!TrainingApi.InWorld)
        {
            RynthLog.Compat($"PlayerTraining: {kind} {stype} not sent - not in the world");
            return false;
        }
        if (defer || AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueTrain((uint)kind, stype, xp);

        byte rv = kind switch
        {
            TrainKind.Attribute => _eventTrainAttribute(stype, xp),
            TrainKind.Vital => _eventTrainAttribute2nd(stype, xp),
            TrainKind.TrainWithCredits => _eventTrainSkillAdvancementClass(stype, xp),
            _ => _eventTrainSkill(stype, xp),
        };
        RynthLog.Compat(kind == TrainKind.TrainWithCredits
            ? $"PlayerTraining: train skill {stype} with {xp} credits rv={rv}"
            : $"PlayerTraining: raise {kind} {stype} with {xp:N0} XP rv={rv}");
        PlayerProgressHooks.RefreshSoon();
        return rv != 0;
    }

    /// <summary>
    /// Train an untrained skill with <paramref name="credits"/> skill credits (0x0047 TrainSkill).
    /// The server wants exactly the portal SkillTable's TrainedCost. Same threading as Raise.
    /// </summary>
    public static bool TrainSkill(uint stype, uint credits, bool defer = false) =>
        Raise(TrainKind.TrainWithCredits, stype, credits, defer);

    /// <summary>AcMainThreadQueue drain (AC's main thread): A = TrainKind, B = stype, C = XP.</summary>
    internal static void RunQueued(uint kind, uint stype, uint xp)
    {
        try { Raise((TrainKind)kind, stype, xp); }
        catch (Exception ex) { RynthLog.Compat($"PlayerTraining: queued raise failed - {ex.Message}"); }
    }
}

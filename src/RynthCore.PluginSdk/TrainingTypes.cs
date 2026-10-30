using System;
using System.Runtime.InteropServices;

namespace RynthCore.PluginSdk;

// ─── Spending experience (engine API v79) ──────────────────────────────────
// What the retail character window's "+" does: raise an attribute, a vital or a
// trained skill with unassigned XP, or train an untrained skill with credits.
// RynthCoreHost.TryGetTrainingInfo reports the numbers and costs; Raise / TrainSkill
// send the client's own CM_Train game actions on AC's main thread.
//
//   if (Host.HasTraining && Host.TryGetTrainingInfo(out var info, entries, out int n))
//   {
//       var focus = Array.Find(entries, e => e.Kind == TrainingKind.Attribute && e.Stype == 5);
//       if (focus.CostOne > 0 && focus.CostOne <= info.UnassignedXp)
//           Host.Raise(TrainingKind.Attribute, 5, 1, focus.CostOne, out long sent);
//   }
//
// Pass the cost the user saw as expectedXp: the engine works the XP out again from
// fresh numbers and refuses (CostChanged) when they differ, so a stale screen never
// spends a different amount. Spent XP can't be taken back.

/// <summary>What a raise targets (the API's kind numbers).</summary>
public enum TrainingKind : uint
{
    /// <summary>Strength 1, Endurance 2, Quickness 3, Coordination 4, Focus 5, Self 6.</summary>
    Attribute = 1,
    /// <summary>A vital's maximum: 1 health, 3 stamina, 5 mana.</summary>
    Vital = 2,
    /// <summary>A skill by its id (trained or specialized to raise; untrained to train).</summary>
    Skill = 3,
}

/// <summary>The engine's answer to <see cref="RynthCoreHost.Raise"/> / <see cref="RynthCoreHost.TrainSkill"/>.</summary>
public enum RaiseResult
{
    /// <summary>Sent, or queued for AC's main thread. The new numbers follow within a second or two.</summary>
    Sent = 1,
    /// <summary>The client's raise (or train) sender isn't bound on this client build.</summary>
    Unavailable = 0,
    NotInWorld = -1,
    BadArguments = -2,
    /// <summary>No numbers yet (the engine is reading them); try again in a second.</summary>
    NotReady = -3,
    /// <summary>The numbers were too old; a fresh read was asked for. Try again in a moment.</summary>
    Stale = -4,
    /// <summary>The cost isn't the one the caller passed: something changed. Refresh and confirm again.</summary>
    CostChanged = -5,
    /// <summary>Not enough unassigned XP (or skill credits).</summary>
    NotEnough = -6,
    /// <summary>At the top rank, an untrained skill (raise), an already trained skill (train), or no price.</summary>
    NotRaisable = -7,
    /// <summary>An earlier raise or train hasn't shown up in the numbers yet (up to 5 s).</summary>
    Busy = -8,
    /// <summary>The send failed or the engine's action queue was full.</summary>
    SendFailed = -9,
    /// <summary>The engine predates API v79 (SDK-side; the engine never returns it).</summary>
    EngineTooOld = -100,
}

/// <summary>v79 <see cref="TrainingInfoNative.Flags"/> bits.</summary>
public static class TrainingInfoFlags
{
    public const uint InWorld = 1u << 0;
    /// <summary>The attribute, vital and skill raise senders are bound.</summary>
    public const uint RaiseBound = 1u << 1;
    /// <summary>The train-with-credits sender is bound.</summary>
    public const uint TrainBound = 1u << 2;
    /// <summary>The player's numbers have been read (entries follow).</summary>
    public const uint HaveNumbers = 1u << 3;
    /// <summary>The portal SkillTable and XpTable are loaded.</summary>
    public const uint HaveTables = 1u << 4;
    /// <summary>A raise or train from the API hasn't landed yet.</summary>
    public const uint Busy = 1u << 5;
}

/// <summary>v79 <see cref="TrainingEntryNative.Flags"/> bits.</summary>
public static class TrainingEntryFlags
{
    /// <summary>Can be raised with XP (attribute, vital, trained or specialized skill below the top).</summary>
    public const uint Raisable = 1u << 0;
    public const uint AtTop = 1u << 1;
    /// <summary>Skills: usable untrained.</summary>
    public const uint UsableUntrained = 1u << 2;
    /// <summary>Skills: untrained and trainable with <see cref="TrainingEntryNative.TrainCredits"/> credits.</summary>
    public const uint Trainable = 1u << 3;
}

/// <summary>
/// v79 GetTrainingInfo header (Pack 4, 64 bytes; later versions only append).
/// Mirrors RynthCore.Engine/Plugins/PluginContract.cs TrainingInfoNative exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct TrainingInfoNative
{
    /// <summary>In: sizeof(TrainingInfoNative). Out: bytes the engine wrote.</summary>
    public uint Size;
    /// <summary><see cref="TrainingInfoFlags"/>.</summary>
    public uint Flags;
    /// <summary>Changes whenever any number changes.</summary>
    public uint SnapshotVersion;
    /// <summary>Milliseconds since the numbers were read; 0xFFFFFFFF = never.</summary>
    public uint AgeMs;
    /// <summary>-1 when unknown.</summary>
    public long UnassignedXp;
    /// <summary>-1 when unknown.</summary>
    public long TotalXp;
    public int Level;
    /// <summary>-1 when unknown.</summary>
    public int SkillCredits;
    public int EntryCount;
    /// <summary>Raise refuses numbers older than this (<see cref="RaiseResult.Stale"/>).</summary>
    public int StaleAfterMs;
    public fixed uint Reserved[4];

    public readonly bool InWorld => (Flags & TrainingInfoFlags.InWorld) != 0;
    public readonly bool RaiseBound => (Flags & TrainingInfoFlags.RaiseBound) != 0;
    public readonly bool TrainBound => (Flags & TrainingInfoFlags.TrainBound) != 0;
    public readonly bool HaveNumbers => (Flags & TrainingInfoFlags.HaveNumbers) != 0;
    public readonly bool Busy => (Flags & TrainingInfoFlags.Busy) != 0;
}

/// <summary>
/// v79 GetTrainingInfo entry: one attribute, vital or skill (Pack 4, 64 bytes; later
/// versions only append). Mirrors the engine's TrainingEntryNative exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct TrainingEntryNative
{
    /// <summary>1 attribute, 2 vital, 3 skill (<see cref="TrainingKind"/>).</summary>
    public TrainingKind Kind;
    /// <summary>Attribute 1..6, vital maximum 1 / 3 / 5, skill id.</summary>
    public uint Stype;
    /// <summary>Skills: 0 not in the table, 1 untrained, 2 trained, 3 specialized. 0 otherwise.</summary>
    public uint Class;
    public uint Ranks;
    public uint XpSpent;
    /// <summary>The innate (starting) level.</summary>
    public uint Innate;
    /// <summary>Without enchantments (a vital's maximum).</summary>
    public int Base;
    public int Buffed;
    /// <summary>XP for one more rank; -1 at the top or not raisable.</summary>
    public long CostOne;
    /// <summary>XP for ten more ranks; -1 when fewer than ten are left or not raisable.</summary>
    public long CostTen;
    public int RanksLeft;
    /// <summary>Ranks in a row the unassigned XP buys now.</summary>
    public int Affordable;
    /// <summary>Untrained skills: the credits to train it (0 = not trainable from the client).</summary>
    public int TrainCredits;
    /// <summary><see cref="TrainingEntryFlags"/>.</summary>
    public uint Flags;

    public readonly bool Raisable => (Flags & TrainingEntryFlags.Raisable) != 0;
    public readonly bool AtTop => (Flags & TrainingEntryFlags.AtTop) != 0;
    public readonly bool Trainable => (Flags & TrainingEntryFlags.Trainable) != 0;
}

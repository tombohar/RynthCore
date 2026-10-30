// ============================================================================
//  RynthCore.Engine - Net/FlightFormat.cs
//  Turns flight recorder entries into one text line each, and writes a dump.
//
//  Runs only when a dump is written (never on the game's hot path). Message layouts are
//  the AC wire format as the Aelrynth/ACE server writes it: little-endian, strings are a
//  u16 length + Latin-1 bytes padded to 4, "align" pads to 4 from the start of the
//  message. An entry holds only the first FlightEntry.DataCap bytes of a message, so
//  every read is bounds-checked and a cut-off message ends with "...".
//
//  Chat the player says or reads (talk, tells, channels) is never written: only its length.
//
//  Pure code (no engine state); compiled into tools/FlightRecorderTests.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RynthCore.Engine.Net;

/// <summary>Bounds-checked little-endian reader over a kept message.</summary>
internal ref struct FlightReader
{
    private readonly ReadOnlySpan<byte> _b;
    public int Pos;
    public bool Ok;

    public FlightReader(ReadOnlySpan<byte> b, int pos = 0) { _b = b; Pos = pos; Ok = true; }

    public int Remaining => Math.Max(0, _b.Length - Pos);
    private bool Need(int n)
    {
        if (Ok && Pos + n <= _b.Length) return true;
        Ok = false;
        Pos = _b.Length;
        return false;
    }

    public byte U8() => Need(1) ? _b[Pos++] : (byte)0;
    public ushort U16() { if (!Need(2)) return 0; ushort v = (ushort)(_b[Pos] | (_b[Pos + 1] << 8)); Pos += 2; return v; }
    public uint U32() { if (!Need(4)) return 0; uint v = (uint)(_b[Pos] | (_b[Pos + 1] << 8) | (_b[Pos + 2] << 16) | (_b[Pos + 3] << 24)); Pos += 4; return v; }
    public int I32() => unchecked((int)U32());
    public float F32() => BitConverter.UInt32BitsToSingle(U32());
    public double F64() { uint lo = U32(); uint hi = U32(); return BitConverter.UInt64BitsToDouble(((ulong)hi << 32) | lo); }
    public void Skip(int n) { if (Need(n)) Pos += n; }
    public void Align() { int pad = (4 - (Pos & 3)) & 3; if (pad > 0 && Pos + pad <= _b.Length) Pos += pad; else if (pad > 0) Pos = _b.Length; }

    /// <summary>u16 length, Latin-1 bytes, padding to 4 from the string's start. Long text is cut to <paramref name="max"/>.</summary>
    public string Str(int max = 96)
    {
        int start = Pos;
        ushort n = U16();
        if (!Ok) return "";
        int have = Math.Min(n, Remaining);
        int show = Math.Min(have, max);
        var sb = new StringBuilder(show + 3);
        for (int i = 0; i < show; i++)
        {
            byte c = _b[Pos + i];
            sb.Append(c < 32 ? ' ' : (char)c);
        }
        if (show < n) sb.Append("...");
        if (have < n) { Ok = false; Pos = _b.Length; return sb.ToString(); }
        Pos += n;
        int pad = (4 - ((Pos - start) & 3)) & 3;
        if (Pos + pad <= _b.Length) Pos += pad; else Pos = _b.Length;
        return sb.ToString();
    }
}

internal static class FlightFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ── names ──────────────────────────────────────────────────────────────

    public static string ActionName(uint a) => a switch
    {
        0x0005 => "SetSingleCharacterOption",
        FlightOp.AcTargetedMeleeAttack => "MeleeAttack",
        FlightOp.AcTargetedMissileAttack => "MissileAttack",
        FlightOp.AcTalk => "Talk",
        FlightOp.AcPutItemInContainer => "PutItemInContainer",
        FlightOp.AcGetAndWieldItem => "GetAndWieldItem",
        FlightOp.AcDropItem => "DropItem",
        FlightOp.AcTalkDirect => "TalkDirect",
        FlightOp.AcUseWithTarget => "UseWithTarget",
        FlightOp.AcUse => "Use",
        FlightOp.AcRaiseVital => "RaiseVital",
        FlightOp.AcRaiseAttribute => "RaiseAttribute",
        FlightOp.AcRaiseSkill => "RaiseSkill",
        FlightOp.AcTrainSkill => "TrainSkill",
        FlightOp.AcCastUntargetedSpell => "CastUntargetedSpell",
        FlightOp.AcCastTargetedSpell => "CastTargetedSpell",
        FlightOp.AcChangeCombatMode => "ChangeCombatMode",
        FlightOp.AcStackableMerge => "StackableMerge",
        FlightOp.AcStackableSplitToContainer => "StackableSplitToContainer",
        FlightOp.AcStackableSplitTo3D => "StackableSplitTo3D",
        FlightOp.AcTell => "Tell",
        0x005F => "Buy",
        0x0060 => "Sell",
        0x0063 => "TeleToLifestone",
        FlightOp.AcLoginComplete => "LoginComplete",
        FlightOp.AcIdentifyObject => "IdentifyObject",
        FlightOp.AcGiveObjectRequest => "GiveObjectRequest",
        FlightOp.AcChatChannel => "ChatChannel",
        FlightOp.AcNoLongerViewingContents => "NoLongerViewingContents",
        FlightOp.AcStackableSplitToWield => "StackableSplitToWield",
        0x01A1 => "SetCharacterOptions",
        FlightOp.AcCancelAttack => "CancelAttack",
        FlightOp.AcQueryHealth => "QueryHealth",
        0x01DF => "Emote",
        0x01E1 => "SoulEmote",
        0x01E9 => "PingRequest",
        0x01F6 => "OpenTradeNegotiations",
        0x01F7 => "CloseTradeNegotiations",
        0x01F8 => "AddToTrade",
        0x01FA => "AcceptTrade",
        0x01FB => "DeclineTrade",
        0x0204 => "ResetTrade",
        0x0263 => "QueryItemMana",
        0x0275 => "ConfirmationResponse",
        0x0279 => "Suicide",
        FlightOp.AcJump => "Jump",
        FlightOp.AcMoveToState => "MoveToState",
        FlightOp.AcDoMovementCommand => "DoMovementCommand",
        FlightOp.AcTurnTo => "TurnTo",
        FlightOp.AcStopMovementCommand => "StopMovementCommand",
        0xF6EA => "ForceObjectDescSend",
        FlightOp.AcAutonomyLevel => "AutonomyLevel",
        FlightOp.AcAutonomousPosition => "AutonomousPosition",
        FlightOp.AcJumpNonAutonomous => "JumpNonAutonomous",
        _ => $"action 0x{a:X4}",
    };

    public static string EventName(uint e) => e switch
    {
        FlightOp.EvPopupString => "PopupString",
        FlightOp.EvInventoryPutObjInContainer => "InventoryPutObjInContainer",
        FlightOp.EvWieldObject => "WieldObject",
        FlightOp.EvCloseGroundContainer => "CloseGroundContainer",
        FlightOp.EvInventoryServerSaveFailed => "InventoryServerSaveFailed",
        FlightOp.EvViewContents => "ViewContents",
        FlightOp.EvInventoryPutObjectIn3D => "InventoryPutObjectIn3D",
        FlightOp.EvAttackDone => "AttackDone",
        FlightOp.EvMagicRemoveSpell => "MagicRemoveSpell",
        FlightOp.EvVictimNotification => "VictimNotification",
        FlightOp.EvKillerNotification => "KillerNotification",
        FlightOp.EvAttackerNotification => "AttackerNotification",
        FlightOp.EvDefenderNotification => "DefenderNotification",
        FlightOp.EvEvasionAttacker => "EvasionAttackerNotification",
        FlightOp.EvEvasionDefender => "EvasionDefenderNotification",
        FlightOp.EvCombatCommenceAttack => "CombatCommenceAttack",
        FlightOp.EvUseDone => "UseDone",
        FlightOp.EvConfirmationRequest => "ConfirmationRequest",
        FlightOp.EvWeenieError => "WeenieError",
        FlightOp.EvWeenieErrorWithString => "WeenieErrorWithString",
        FlightOp.EvMagicUpdateSpell => "MagicUpdateSpell",
        FlightOp.EvMagicUpdateEnchantment => "MagicUpdateEnchantment",
        FlightOp.EvMagicRemoveEnchantment => "MagicRemoveEnchantment",
        FlightOp.EvMagicUpdateMultipleEnchantments => "MagicUpdateMultipleEnchantments",
        FlightOp.EvMagicRemoveMultipleEnchantments => "MagicRemoveMultipleEnchantments",
        FlightOp.EvMagicPurgeEnchantments => "MagicPurgeEnchantments",
        FlightOp.EvMagicDispelEnchantment => "MagicDispelEnchantment",
        FlightOp.EvMagicDispelMultipleEnchantments => "MagicDispelMultipleEnchantments",
        FlightOp.EvTransientString => "TransientString",
        FlightOp.EvMagicPurgeBadEnchantments => "MagicPurgeBadEnchantments",
        _ => $"event 0x{e:X4}",
    };

    public static string OpcodeName(uint op) => op switch
    {
        FlightOp.GameEvent => "GameEvent",
        FlightOp.GameAction => "GameAction",
        FlightOp.UpdateMotion => "UpdateMotion",
        FlightOp.UpdatePosition => "UpdatePosition",
        FlightOp.PlayerTeleport => "PlayerTeleport",
        FlightOp.VectorUpdate => "VectorUpdate",
        FlightOp.PlayEffect => "PlayEffect",
        FlightOp.PlayScriptId => "PlayScriptId",
        FlightOp.ObjectDelete => "ObjectDelete",
        FlightOp.PickupEvent => "PickupEvent",
        FlightOp.SetState => "SetState",
        FlightOp.ParentEvent => "ParentEvent",
        FlightOp.ServerMessage => "ServerMessage",
        FlightOp.PlayerCreate => "PlayerCreate",
        FlightOp.CharacterLogOff => "CharacterLogOff",
        FlightOp.InventoryRemoveObject => "InventoryRemoveObject",
        FlightOp.SetStackSize => "SetStackSize",
        FlightOp.PlayerKilled => "PlayerKilled",
        0xF7C8 => "CharacterEnterWorldRequest",
        0xF657 => "CharacterEnterWorld",
        0xF7DE => "TurbineChat",
        0xF7E6 => "DDD_InterrogationResponse",
        0xF7EA => "DDD_EndDDD",
        _ => $"msg 0x{op:X4}",
    };

    /// <summary>The WeenieError codes worth naming here (others print as hex).</summary>
    public static string ErrorName(uint e) => e switch
    {
        0x0000 => "none",
        0x001D => "YoureTooBusy",
        0x001B => "PhysicsCantTransition",
        0x001C => "PhysicsMaxDistanceExceeded",
        0x0020 => "IllegalInventoryTransaction",
        0x0023 => "MotionFailure",
        0x0024 => "YouCantJumpWhileInTheAir",
        0x0027 => "CarryingItem",
        0x0028 => "Frozen",
        0x0029 => "Stuck",
        0x002A => "YouAreTooEncumbered",
        0x0036 => "ActionCancelled",
        0x0037 => "ObjectGone",
        0x0038 => "NoObject",
        0x003D => "YouChargedTooFar",
        0x0400 => "YouDontHaveAllTheComponents",
        0x0401 => "YouDontHaveEnoughManaToCast",
        0x0402 => "YourSpellFizzled",
        0x0403 => "YourSpellTargetIsMissing",
        0x0404 => "YourProjectileSpellMislaunched",
        0x0406 => "MagicTargetOutOfRange",
        0x0409 => "MagicGeneralFailure",
        0x040A => "YouAreUnpreparedToCastASpell",
        0x042A => "CurrentlyAttacking",
        0x042B => "MissileAttackNotOk",
        0x042C => "TargetNotAcquired",
        0x043C => "YourHandsMustBeFree",
        0x0498 => "YouHaveMovedTooFar",
        _ => "",
    };

    private static string Err(uint e)
    {
        string n = ErrorName(e);
        return n.Length > 0 ? $"0x{e:X4} {n}" : $"0x{e:X4}";
    }

    /// <summary>Motion commands and stances by their low 16 bits (the interpreted state sends only those).</summary>
    public static string MotionName(uint cmd)
    {
        uint k = cmd & 0xFFFF;
        string? n = k switch
        {
            0x0000 => "Invalid",
            0x0001 => "HoldRun", 0x0002 => "HoldSidestep", 0x0003 => "Ready", 0x0004 => "Stop",
            0x0005 => "WalkForward", 0x0006 => "WalkBackwards", 0x0007 => "RunForward", 0x0008 => "Fallen",
            0x0009 => "Interpolating", 0x000D => "TurnRight", 0x000E => "TurnLeft", 0x000F => "SideStepRight",
            0x0010 => "SideStepLeft", 0x0011 => "Dead", 0x0012 => "Crouch", 0x0013 => "Sitting", 0x0014 => "Sleeping",
            0x0015 => "Falling", 0x0016 => "Reload", 0x0017 => "Unload", 0x0018 => "Pickup", 0x0019 => "StoreInBackpack",
            0x001A => "Eat", 0x001B => "Drink", 0x001C => "Reading", 0x001D => "JumpCharging",
            0x002B => "MagicBlast", 0x002C => "MagicSelfHead", 0x002D => "MagicSelfHeart", 0x002E => "MagicBonus",
            0x002F => "MagicClap", 0x0030 => "MagicHarm", 0x0031 => "MagicHeal", 0x0032 => "MagicThrowMissile",
            0x0033 => "MagicRecoilMissile", 0x0034 => "MagicPenalty", 0x0035 => "MagicTransfer", 0x0036 => "MagicVision",
            0x0037 => "MagicEnchantItem", 0x0038 => "MagicPortal", 0x0039 => "MagicPray", 0x003A => "StopTurning",
            0x003B => "Jump", 0x003C => "HandCombat", 0x003D => "NonCombat", 0x003E => "SwordCombat", 0x003F => "BowCombat",
            0x0040 => "SwordShieldCombat", 0x0041 => "CrossbowCombat", 0x0043 => "SlingCombat",
            0x0044 => "TwoHandedSwordCombat", 0x0045 => "TwoHandedStaffCombat", 0x0046 => "DualWieldCombat",
            0x0047 => "ThrownWeaponCombat", 0x0049 => "Magic", 0x0050 => "FallDown", 0x0055 => "StaggerBackward",
            0x0056 => "StaggerForward", 0x0057 => "Sanctuary",
            0x0058 => "ThrustMed", 0x0059 => "ThrustLow", 0x005A => "ThrustHigh", 0x005B => "SlashHigh", 0x005C => "SlashMed",
            0x005D => "SlashLow", 0x005E => "BackhandHigh", 0x005F => "BackhandMed", 0x0060 => "BackhandLow", 0x0061 => "Shoot",
            0x0062 => "AttackHigh1", 0x0063 => "AttackMed1", 0x0064 => "AttackLow1", 0x0065 => "AttackHigh2",
            0x0066 => "AttackMed2", 0x0067 => "AttackLow2", 0x0068 => "AttackHigh3", 0x0069 => "AttackMed3", 0x006A => "AttackLow3",
            0x009C => "EnterGame", 0x009D => "ExitGame", 0x00A0 => "EnterPortal", 0x00A1 => "ExitPortal",
            0x00D3 => "CastSpell", 0x00E0 => "UseMagicStaff", 0x00E8 => "BowNoAmmo", 0x00E9 => "CrossBowNoAmmo",
            0x013B => "AtlatlCombat", 0x013C => "ThrownShieldCombat",
            _ => null,
        };
        if (n == null && k >= 0x006F && k <= 0x0078) n = $"MagicPowerUp{k - 0x006E:00}";
        return n ?? $"0x{cmd:X}";
    }

    private static string CombatMode(uint m) => m switch
    {
        1 => "NonCombat", 2 => "Melee", 4 => "Missile", 8 => "Magic", _ => $"mode {m}",
    };

    private static string HoldKey(uint h) => h switch { 0 => "Invalid", 1 => "None", 2 => "Run", _ => $"hold{h}" };

    private static string MovementType(byte t) => t switch
    {
        0 => "State", 1 => "RawCommand", 2 => "InterpretedCommand", 3 => "StopRawCommand", 4 => "StopInterpretedCommand",
        5 => "StopCompletely", 6 => "MoveToObject", 7 => "MoveToPosition", 8 => "TurnToObject", 9 => "TurnToHeading",
        _ => $"type{t}",
    };

    private static string F(float v) => float.IsFinite(v) ? v.ToString("0.##", Inv) : "nan";
    private static string Id(uint id) => $"0x{id:X8}";

    /// <summary>Yaw in degrees from the (w, x, y, z) rotation (rotation about the vertical axis).</summary>
    private static string Yaw(float w, float z)
    {
        double deg = 2 * Math.Atan2(z, w) * 180 / Math.PI;
        deg %= 360; if (deg < 0) deg += 360;
        return deg.ToString("0", Inv);
    }

    // ── entries ────────────────────────────────────────────────────────────

    /// <summary>The text after the direction arrow for one entry.</summary>
    public static string Describe(in FlightRecord r)
    {
        try
        {
            FlightEntry e = r.Entry;
            ReadOnlySpan<byte> data;
            unsafe { data = new ReadOnlySpan<byte>(e.Data, e.Len).ToArray(); }
            string s = e.Dir switch
            {
                FlightDir.Out => DescribeOut(data, e),
                FlightDir.In => DescribeIn(data, e),
                FlightDir.Mark => DescribeMark(data, e, r.Name),
                _ => "?",
            };
            return s;
        }
        catch (Exception ex)
        {
            return $"(undecodable: {ex.GetType().Name})";
        }
    }

    private static string Tail(in FlightReader rd, FlightEntry e)
    {
        var sb = new StringBuilder();
        if (!rd.Ok) sb.Append(" ...");
        if ((e.Flags & FlightFlags.MultiFragment) != 0) sb.Append(" [multi-fragment]");
        if ((e.Flags & FlightFlags.Retransmit) != 0) sb.Append(" [retransmitted]");
        if ((e.Flags & FlightFlags.SendFailed) != 0) sb.Append(" [SEND FAILED]");
        if (e.Dir != FlightDir.Mark && e.B > 0) sb.Append($" (+{e.B} like it not kept)");
        return sb.ToString();
    }

    public static string DescribeOut(ReadOnlySpan<byte> d, FlightEntry e)
    {
        var rd = new FlightReader(d);
        uint op = rd.U32();
        if (op != FlightOp.GameAction)
            return $"{OpcodeName(op)} len={e.FullLen}" + Tail(rd, e);

        uint seq = rd.U32();
        uint action = rd.U32();
        string who = IdRequesters.Label(e.C);
        string tag = (e.Flags & FlightFlags.Engine) != 0 ? (who.Length > 0 ? $"[eng:{who}] " : "[eng] ")
                   : (e.Flags & FlightFlags.Client) != 0 ? "[cli] " : "";
        string body = action switch
        {
            FlightOp.AcUse => $"{Id(rd.U32())}",
            FlightOp.AcUseWithTarget => $"{Id(rd.U32())} on {Id(rd.U32())}",
            FlightOp.AcCastUntargetedSpell => $"spell {rd.U32()}",
            FlightOp.AcCastTargetedSpell => Cast(ref rd),
            FlightOp.AcChangeCombatMode => CombatMode(rd.U32()),
            FlightOp.AcTargetedMeleeAttack => $"{Id(rd.U32())} height {rd.U32()} power {F(rd.F32())}",
            FlightOp.AcTargetedMissileAttack => $"{Id(rd.U32())} height {rd.U32()} accuracy {F(rd.F32())}",
            FlightOp.AcPutItemInContainer => $"{Id(rd.U32())} into {Id(rd.U32())} slot {rd.I32()}",
            FlightOp.AcGetAndWieldItem => $"{Id(rd.U32())} mask 0x{rd.U32():X}",
            FlightOp.AcDropItem => Id(rd.U32()),
            FlightOp.AcGiveObjectRequest => Give(ref rd),
            FlightOp.AcStackableMerge => $"{Id(rd.U32())} into {Id(rd.U32())} amount {rd.I32()}",
            FlightOp.AcStackableSplitToContainer => $"{Id(rd.U32())} to {Id(rd.U32())} slot {rd.I32()} amount {rd.I32()}",
            FlightOp.AcStackableSplitTo3D => $"{Id(rd.U32())} amount {rd.I32()}",
            FlightOp.AcStackableSplitToWield => $"{Id(rd.U32())} mask 0x{rd.U32():X} amount {rd.I32()}",
            FlightOp.AcNoLongerViewingContents => Id(rd.U32()),
            FlightOp.AcIdentifyObject => Id(rd.U32()),
            FlightOp.AcQueryHealth => Id(rd.U32()),
            FlightOp.AcCancelAttack => "",
            FlightOp.AcLoginComplete => "",
            FlightOp.AcAutonomyLevel => $"level {rd.U32()}",
            FlightOp.AcTalk or FlightOp.AcTell or FlightOp.AcTalkDirect or FlightOp.AcChatChannel or 0x01DF or 0x01E1
                => $"({e.FullLen} bytes, text not recorded)",
            FlightOp.AcMoveToState => MoveToState(ref rd),
            FlightOp.AcAutonomousPosition => AutonomousPosition(ref rd),
            FlightOp.AcJump => Jump(ref rd),
            FlightOp.AcJumpNonAutonomous => $"extent {F(rd.F32())}",
            FlightOp.AcDoMovementCommand => $"{MotionName(rd.U32())} speed {F(rd.F32())} hold {HoldKey(rd.U32())}",
            FlightOp.AcStopMovementCommand => $"{MotionName(rd.U32())} hold {HoldKey(rd.U32())}",
            _ => RawArgs(ref rd),
        };
        if (action is FlightOp.AcTalk or FlightOp.AcTell or FlightOp.AcTalkDirect or FlightOp.AcChatChannel or 0x01DF or 0x01E1)
            rd.Ok = true;   // the text is skipped on purpose
        return $"{tag}{ActionName(action)} {body}".TrimEnd() + $" #{seq}" + Tail(rd, e);
    }

    private static string Cast(ref FlightReader rd) { uint t = rd.U32(); uint s = rd.U32(); return $"spell {s} on {Id(t)}"; }
    private static string Give(ref FlightReader rd) { uint t = rd.U32(); uint o = rd.U32(); int n = rd.I32(); return $"{Id(o)} x{n} to {Id(t)}"; }

    private static string RawArgs(ref FlightReader rd)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 4 && rd.Remaining >= 4; i++)
            sb.Append(i == 0 ? "args " : " ").Append($"0x{rd.U32():X}");
        return sb.ToString();
    }

    private static string Position(ref FlightReader rd, bool rotation)
    {
        uint cell = rd.U32();
        float x = rd.F32(), y = rd.F32(), z = rd.F32();
        string s = $"@{Id(cell)} ({F(x)},{F(y)},{F(z)})";
        if (rotation)
        {
            float w = rd.F32(); rd.F32(); rd.F32(); float qz = rd.F32();
            s += $" yaw {Yaw(w, qz)}";
        }
        return s;
    }

    private static string Seqs(ref FlightReader rd) => $"seq i{rd.U16()}/s{rd.U16()}/t{rd.U16()}/f{rd.U16()}";

    private static string MoveToState(ref FlightReader rd)
    {
        uint packed = rd.U32();
        uint flags = packed & 0x7FF;
        uint ncmd = packed >> 11;
        var sb = new StringBuilder();
        if ((flags & 0x001) != 0) sb.Append($"hold={HoldKey(rd.U32())} ");
        if ((flags & 0x002) != 0) sb.Append($"style={MotionName(rd.U32())} ");
        string fwd = (flags & 0x004) != 0 ? MotionName(rd.U32()) : "";
        string fwdHold = (flags & 0x008) != 0 ? HoldKey(rd.U32()) : "";
        string fwdSpeed = (flags & 0x010) != 0 ? F(rd.F32()) : "";
        if (fwd.Length > 0 || fwdHold.Length > 0 || fwdSpeed.Length > 0)
            sb.Append($"fwd={(fwd.Length > 0 ? fwd : "-")}{(fwdHold.Length > 0 ? "(" + fwdHold + ")" : "")}{(fwdSpeed.Length > 0 ? "@" + fwdSpeed : "")} ");
        string side = (flags & 0x020) != 0 ? MotionName(rd.U32()) : "";
        string sideHold = (flags & 0x040) != 0 ? HoldKey(rd.U32()) : "";
        string sideSpeed = (flags & 0x080) != 0 ? F(rd.F32()) : "";
        if (side.Length > 0 || sideHold.Length > 0 || sideSpeed.Length > 0)
            sb.Append($"side={(side.Length > 0 ? side : "-")}{(sideHold.Length > 0 ? "(" + sideHold + ")" : "")}{(sideSpeed.Length > 0 ? "@" + sideSpeed : "")} ");
        string turn = (flags & 0x100) != 0 ? MotionName(rd.U32()) : "";
        string turnHold = (flags & 0x200) != 0 ? HoldKey(rd.U32()) : "";
        string turnSpeed = (flags & 0x400) != 0 ? F(rd.F32()) : "";
        if (turn.Length > 0 || turnHold.Length > 0 || turnSpeed.Length > 0)
            sb.Append($"turn={(turn.Length > 0 ? turn : "-")}{(turnHold.Length > 0 ? "(" + turnHold + ")" : "")}{(turnSpeed.Length > 0 ? "@" + turnSpeed : "")} ");
        if (flags == 0) sb.Append("(no motion: stand still) ");
        if (ncmd > 0)
        {
            sb.Append("cmds=[");
            for (uint i = 0; i < ncmd && i < 8 && rd.Ok; i++)
            {
                ushort c = rd.U16(); ushort ps = rd.U16(); float sp = rd.F32();
                sb.Append(i > 0 ? " " : "").Append($"{MotionName(c)}@{F(sp)}#{ps & 0x7FFF}{((ps & 0x8000) != 0 ? "a" : "")}");
            }
            if (ncmd > 8) { rd.Skip((int)(ncmd - 8) * 8); sb.Append(" ..."); }
            sb.Append("] ");
        }
        sb.Append(Position(ref rd, rotation: true)).Append(' ');
        sb.Append(Seqs(ref rd));
        byte contact = rd.U8();
        sb.Append($" contact={contact & 1}{((contact & 2) != 0 ? " longjump" : "")}");
        return sb.ToString();
    }

    private static string AutonomousPosition(ref FlightReader rd)
    {
        string pos = Position(ref rd, rotation: true);
        string seqs = Seqs(ref rd);
        byte contact = rd.U8();
        return $"{pos} {seqs} contact={contact & 1}";
    }

    private static string Jump(ref FlightReader rd)
    {
        float extent = rd.F32();
        float vx = rd.F32(), vy = rd.F32(), vz = rd.F32();
        return $"extent {F(extent)} velocity ({F(vx)},{F(vy)},{F(vz)}) {Seqs(ref rd)}";
    }

    public static string DescribeIn(ReadOnlySpan<byte> d, FlightEntry e)
    {
        var rd = new FlightReader(d);
        uint op = rd.U32();
        string s;
        switch (op)
        {
            case FlightOp.GameEvent:
            {
                rd.U32();                 // player guid
                uint seq = rd.U32();
                uint ev = rd.U32();
                s = $"{EventName(ev)} {DescribeEvent(ev, ref rd, e)}".TrimEnd() + $" #{seq}";
                break;
            }
            case FlightOp.UpdateMotion: s = "UpdateMotion " + UpdateMotion(ref rd); break;
            case FlightOp.UpdatePosition: s = "UpdatePosition " + UpdatePosition(ref rd); break;
            case FlightOp.PlayerTeleport: s = $"PlayerTeleport seq t{rd.U16()}"; break;
            case FlightOp.VectorUpdate:
            {
                uint id = rd.U32();
                float vx = rd.F32(), vy = rd.F32(), vz = rd.F32(), ox = rd.F32(), oy = rd.F32(), oz = rd.F32();
                s = $"VectorUpdate {Id(id)} velocity ({F(vx)},{F(vy)},{F(vz)}) omega ({F(ox)},{F(oy)},{F(oz)})";
                break;
            }
            case FlightOp.PlayEffect: { uint id = rd.U32(); uint sc = rd.U32(); float sp = rd.F32(); s = $"PlayEffect {Id(id)} script 0x{sc:X} speed {F(sp)}"; break; }
            case FlightOp.PlayScriptId: { uint id = rd.U32(); uint sc = rd.U32(); s = $"PlayScriptId {Id(id)} script 0x{sc:X}"; break; }
            case FlightOp.ObjectDelete: { uint id = rd.U32(); s = $"ObjectDelete {Id(id)} i{rd.U16()}"; break; }
            case FlightOp.PickupEvent: { uint id = rd.U32(); s = $"PickupEvent {Id(id)} i{rd.U16()} p{rd.U16()}"; break; }
            case FlightOp.SetState: { uint id = rd.U32(); uint st = rd.U32(); s = $"SetState {Id(id)} state 0x{st:X}"; break; }
            case FlightOp.ParentEvent: { uint p = rd.U32(); uint c = rd.U32(); s = $"ParentEvent {Id(c)} onto {Id(p)} loc {rd.I32()}"; break; }
            case FlightOp.InventoryRemoveObject: s = $"InventoryRemoveObject {Id(rd.U32())}"; break;
            case FlightOp.SetStackSize: { rd.U8(); uint id = rd.U32(); uint n = rd.U32(); s = $"SetStackSize {Id(id)} size {n}"; break; }
            case FlightOp.ServerMessage: { string t = rd.Str(); uint ct = rd.U32(); s = $"ServerMessage \"{t}\" (chat type {ct})"; break; }
            case FlightOp.PlayerCreate: s = $"PlayerCreate {Id(rd.U32())}"; break;
            case FlightOp.CharacterLogOff: s = "CharacterLogOff"; break;
            case FlightOp.PlayerKilled: { string t = rd.Str(); uint v = rd.U32(); uint k = rd.U32(); s = $"PlayerKilled \"{t}\" victim {Id(v)} killer {Id(k)}"; break; }
            default: s = $"{OpcodeName(op)} len={e.FullLen}"; break;
        }
        return s + Tail(rd, e);
    }

    private static string DescribeEvent(uint ev, ref FlightReader rd, FlightEntry e)
    {
        switch (ev)
        {
            case FlightOp.EvUseDone: return $"err {Err(rd.U32())}";
            case FlightOp.EvWeenieError: return Err(rd.U32());
            case FlightOp.EvWeenieErrorWithString: { uint c = rd.U32(); return $"{Err(c)} \"{rd.Str()}\""; }
            case FlightOp.EvViewContents:
            {
                uint c = rd.U32();
                uint n = rd.U32();
                var sb = new StringBuilder($"{Id(c)} items {n}");
                for (uint i = 0; i < n && i < 6 && rd.Remaining >= 8; i++)
                {
                    uint id = rd.U32(); rd.U32();
                    sb.Append(i == 0 ? ": " : " ").Append(Id(id));
                }
                if (n > 6) sb.Append(" ...");
                rd.Ok = true;     // the item list is cut on purpose
                return sb.ToString();
            }
            case FlightOp.EvCloseGroundContainer: return Id(rd.U32());
            case FlightOp.EvInventoryServerSaveFailed: { uint i = rd.U32(); return $"{Id(i)} err {Err(rd.U32())}"; }
            case FlightOp.EvInventoryPutObjInContainer: { uint i = rd.U32(); uint c = rd.U32(); int p = rd.I32(); uint t = rd.U32(); return $"{Id(i)} in {Id(c)} slot {p} type {t}"; }
            case FlightOp.EvWieldObject: { uint i = rd.U32(); return $"{Id(i)} mask 0x{rd.U32():X}"; }
            case FlightOp.EvInventoryPutObjectIn3D: return Id(rd.U32());
            case FlightOp.EvAttackDone: return $"err {Err(rd.U32())}";
            case FlightOp.EvCombatCommenceAttack: return "";
            case FlightOp.EvAttackerNotification:
            {
                string who = rd.Str(40); rd.U32(); double pct = rd.F64(); uint dmg = rd.U32(); uint crit = rd.U32();
                return $"hit '{who}' for {dmg}{(crit != 0 ? " (crit)" : "")} ({pct * 100:0}% of its max health)";   // the share is damage / max, not what is left
            }
            case FlightOp.EvDefenderNotification:
            {
                string who = rd.Str(40); rd.U32(); double pct = rd.F64(); uint dmg = rd.U32();
                return $"'{who}' hit you for {dmg} ({pct * 100:0}% of your max health)";
            }
            case FlightOp.EvEvasionAttacker: return $"'{rd.Str(40)}' evaded";
            case FlightOp.EvEvasionDefender: return $"evaded '{rd.Str(40)}'";
            case FlightOp.EvKillerNotification:
            case FlightOp.EvVictimNotification:
            case FlightOp.EvPopupString:
            case FlightOp.EvTransientString:
                return $"\"{rd.Str()}\"";
            case FlightOp.EvConfirmationRequest: { uint t = rd.U32(); uint ctx = rd.U32(); return $"type {t} context {ctx} \"{rd.Str(60)}\""; }
            case FlightOp.EvMagicUpdateEnchantment:
            {
                ushort spell = rd.U16(); ushort layer = rd.U16(); rd.U16(); rd.U16(); rd.U32(); rd.F64();
                double dur = rd.F64(); uint caster = rd.U32();
                return $"spell {spell} layer {layer} duration {dur.ToString("0", Inv)} s caster {Id(caster)}";
            }
            case FlightOp.EvMagicRemoveEnchantment:
            case FlightOp.EvMagicDispelEnchantment:
            case FlightOp.EvMagicUpdateSpell:
            case FlightOp.EvMagicRemoveSpell:
            {
                ushort spell = rd.U16(); ushort layer = rd.U16();
                return $"spell {spell} layer {layer}";
            }
            case FlightOp.EvMagicUpdateMultipleEnchantments:
            {
                uint n = rd.U32(); ushort first = rd.U16();
                rd.Ok = true;
                return $"{n} enchantments (first spell {first})";
            }
            case FlightOp.EvMagicRemoveMultipleEnchantments:
            case FlightOp.EvMagicDispelMultipleEnchantments:
            {
                uint n = rd.U32();
                var sb = new StringBuilder($"{n}:");
                for (uint i = 0; i < n && i < 6 && rd.Remaining >= 4; i++) { ushort sp = rd.U16(); rd.U16(); sb.Append($" {sp}"); }
                rd.Ok = true;
                return sb.ToString();
            }
            case FlightOp.EvMagicPurgeEnchantments:
            case FlightOp.EvMagicPurgeBadEnchantments:
                return "";
            default:
                return RawArgs(ref rd);
        }
    }

    private static string UpdateMotion(ref FlightReader rd)
    {
        uint id = rd.U32();
        ushort inst = rd.U16(), mov = rd.U16(), srv = rd.U16();
        byte auton = rd.U8();
        rd.Align();
        byte type = rd.U8();
        byte mflags = rd.U8();
        ushort style = rd.U16();
        var sb = new StringBuilder();
        sb.Append($"{Id(id)} {(auton != 0 ? "autonomous " : "server ")}{MovementType(type)} style={MotionName(style)}");
        switch (type)
        {
            case 0:
            {
                uint packed = rd.U32();
                uint fl = packed & 0x7F;
                uint ncmd = packed >> 7;
                string cur = (fl & 0x01) != 0 ? MotionName(rd.U16()) : "";
                string fwd = (fl & 0x02) != 0 ? MotionName(rd.U16()) : "";
                string side = (fl & 0x08) != 0 ? MotionName(rd.U16()) : "";
                string turn = (fl & 0x20) != 0 ? MotionName(rd.U16()) : "";
                string fs = (fl & 0x04) != 0 ? F(rd.F32()) : "";
                string ss = (fl & 0x10) != 0 ? F(rd.F32()) : "";
                string ts = (fl & 0x40) != 0 ? F(rd.F32()) : "";
                if (cur.Length > 0 && cur != MotionName(style)) sb.Append($" state.style={cur}");
                sb.Append($" fwd={(fwd.Length > 0 ? fwd : "Ready")}{(fs.Length > 0 ? "@" + fs : "")}");
                if (side.Length > 0) sb.Append($" side={side}{(ss.Length > 0 ? "@" + ss : "")}");
                if (turn.Length > 0) sb.Append($" turn={turn}{(ts.Length > 0 ? "@" + ts : "")}");
                if (ncmd > 0)
                {
                    sb.Append(" cmds=[");
                    for (uint i = 0; i < ncmd && i < 8 && rd.Ok; i++)
                    {
                        ushort c = rd.U16(); ushort ps = rd.U16(); float sp = rd.F32();
                        sb.Append(i > 0 ? " " : "").Append($"{MotionName(c)}@{F(sp)}#{ps & 0x7FFF}{((ps & 0x8000) != 0 ? "a" : "")}");
                    }
                    sb.Append(']');
                }
                rd.Align();
                if ((mflags & 1) != 0) sb.Append($" stick to {Id(rd.U32())}");
                break;
            }
            case 6:
            case 7:
            {
                if (type == 6) sb.Append($" target {Id(rd.U32())}");
                uint cell = rd.U32(); float x = rd.F32(), y = rd.F32(), z = rd.F32();
                uint pf = rd.U32(); float dist = rd.F32(), min = rd.F32(), fail = rd.F32(), speed = rd.F32(), wr = rd.F32(), hd = rd.F32();
                float run = rd.F32();
                sb.Append($" origin @{Id(cell)} ({F(x)},{F(y)},{F(z)}) params 0x{pf:X} dist {F(dist)} min {F(min)} fail {F(fail)} speed {F(speed)} walkrun {F(wr)} heading {F(hd)} runrate {F(run)}");
                break;
            }
            case 8:
            {
                uint target = rd.U32(); float want = rd.F32();
                uint pf = rd.U32(); float speed = rd.F32(); float hd = rd.F32();
                sb.Append($" target {Id(target)} heading {F(want)} params 0x{pf:X} speed {F(speed)} ({F(hd)})");
                break;
            }
            case 9:
            {
                uint pf = rd.U32(); float speed = rd.F32(); float hd = rd.F32();
                sb.Append($" heading {F(hd)} params 0x{pf:X} speed {F(speed)}");
                break;
            }
        }
        sb.Append($" seq i{inst}/m{mov}/c{srv}");
        return sb.ToString();
    }

    private static string UpdatePosition(ref FlightReader rd)
    {
        uint id = rd.U32();
        uint flags = rd.U32();
        uint cell = rd.U32();
        float x = rd.F32(), y = rd.F32(), z = rd.F32();
        float w = (flags & 0x08) == 0 ? rd.F32() : 0;
        if ((flags & 0x10) == 0) rd.F32();
        if ((flags & 0x20) == 0) rd.F32();
        float qz = (flags & 0x40) == 0 ? rd.F32() : 0;
        string vel = "";
        if ((flags & 0x01) != 0) { float vx = rd.F32(), vy = rd.F32(), vz = rd.F32(); vel = $" velocity ({F(vx)},{F(vy)},{F(vz)})"; }
        if ((flags & 0x02) != 0) rd.U32();
        string seqs = $"seq i{rd.U16()}/p{rd.U16()}/t{rd.U16()}/f{rd.U16()}";
        return $"{Id(id)} @{Id(cell)} ({F(x)},{F(y)},{F(z)}) yaw {Yaw(w, qz)}{vel}{((flags & 0x04) != 0 ? " grounded" : "")} {seqs}";
    }

    public static string DescribeMark(ReadOnlySpan<byte> d, FlightEntry e, string? name)
    {
        string text = Latin1(d);
        if ((e.Flags & FlightFlags.Truncated) != 0) text += "...";
        switch ((FlightMark)e.Kind)
        {
            case FlightMark.Note: return $"note: {text}";
            case FlightMark.EngineCall:
                return e.C > 0
                    ? $"engine {name}({Hex(e.A)}, {Hex(e.B)}) repeated {e.C} more time(s), last here"
                    : $"engine {name}({Hex(e.A)}, {Hex(e.B)})";
            case FlightMark.BusyIncrement: return $"m_cBusy +1 -> real {Busy(e.A)} (engine count {(int)e.B})";
            case FlightMark.BusyDecrement: return $"m_cBusy -1 -> real {Busy(e.A)} (engine count {(int)e.B})";
            case FlightMark.BusyForceClear: return $"m_cBusy force-cleared (was real {Busy(e.A)}, engine count {(int)e.B}){(text.Length > 0 ? " - " + text : "")}";
            case FlightMark.BusyForceClearAsk: return $"m_cBusy force-clear asked: {text}";
            case FlightMark.BusyReconcile: return $"m_cBusy reconcile: {e.A} decrement(s) for the engine's own action, real now {Busy(e.B)}";
            case FlightMark.BusyNegativeRepair: return $"m_cBusy was negative ({(int)e.A}) - reset to 0";
            case FlightMark.BusyWatchdog: return $"busy watchdog ({text}): real {Busy(e.A)}, engine count {(int)e.B}, stuck {e.C} ms";
            case FlightMark.GateCleared:
                return e.A == 0
                    ? $"action gate cleared: attacking flag ({text})"
                    : $"action gate cleared: pending item request type {e.A} on {Id(e.B)} ({text})";
            case FlightMark.EngineInit: return $"engine generation {e.A} started{(e.A >= 2 ? " (hot reload: everything before this was in the previous generation's buffer)" : "")}";
            case FlightMark.EngineShutdown: return $"engine shutting down{(text.Length > 0 ? " (" + text + ")" : "")}";
            case FlightMark.PluginReload: return $"plugin reload: {text}";
            case FlightMark.Trigger: return $"AUTO DUMP TRIGGER: {text}";
            case FlightMark.Dump: return $"DUMP REQUESTED{(text.Length > 0 ? ": " + text : "")}";
            case FlightMark.Summary: return Summary(d);
            case FlightMark.NetControl:
            {
                string who = d.Length > 0 && d[0] == (byte)FlightDir.Out ? "client" : "server";
                ushort more = d.Length >= 4 ? (ushort)(d[2] | (d[3] << 8)) : (ushort)0;
                string extra = more > 0 ? $" (+{more} more not marked)" : "";
                if (e.A == PacketFlags.Disconnect) return $"{who} sent DISCONNECT";
                return $"{who} asks for {e.B} lost packet(s) again (first seq {e.C}){extra}";
            }
            default: return $"marker {e.Kind} {Hex(e.A)} {Hex(e.B)} {Hex(e.C)} {text}".TrimEnd();
        }
    }

    private static string Summary(ReadOnlySpan<byte> d)
    {
        FlightSummary s = FlightSummary.Read(d);
        string busy = s.RealBusy == FlightSummary.Unknown ? "?" : s.RealBusy.ToString(Inv);
        string req = s.PendingType == 0xFFFFFFFF ? "?" : s.PendingType == 0 ? "none" : $"type {s.PendingType} on {Id(s.PendingObject)}";
        string atk = s.Attacking == 255 ? "?" : s.Attacking.ToString(Inv);
        string ges = s.Gesture == 255 ? "?" : s.Gesture.ToString(Inv);
        string loss = "";
        if (s.InRetransmits + s.OutRetransmits + s.InResendRequests + s.OutResendRequests + s.SendErrors > 0)
            loss = $" | resent in {s.InRetransmits} out {s.OutRetransmits}, resend asks server {s.InResendRequests} client {s.OutResendRequests}" +
                   (s.SendErrors > 0 ? $", SEND ERRORS {s.SendErrors}" : "");
        return $"-- 1s: in {s.InPackets} pk/{s.InMessages} msg ({s.InKept} kept), out {s.OutPackets} pk/{s.OutMessages} msg ({s.OutKept} kept){loss}" +
               $" | m_cBusy {busy} (engine {s.ShadowBusy}) | item request {req} | attacking {atk} | gesture {ges}";
    }

    private static string Busy(uint v) => unchecked((int)v) == FlightSummary.Unknown ? "?" : unchecked((int)v).ToString(Inv);
    private static string Hex(uint v) => v == 0 ? "0" : $"0x{v:X}";

    private static string Latin1(ReadOnlySpan<byte> d)
    {
        var sb = new StringBuilder(d.Length);
        foreach (byte b in d) sb.Append(b < 32 ? ' ' : (char)b);
        return sb.ToString();
    }

    // ── dump ───────────────────────────────────────────────────────────────

    public static string Arrow(FlightDir dir) => dir switch
    {
        FlightDir.Out => "->",
        FlightDir.In => "<-",
        _ => "**",
    };

    /// <summary>
    /// Writes the dump: a header, a legend, then one line per entry with its time relative to
    /// <paramref name="zeroStamp"/> (the moment the dump was asked for), the wall clock, the
    /// arrow and the text.
    /// </summary>
    public static void WriteDump(TextWriter w, FlightRecord[] records, long zeroStamp, DateTime zeroLocal,
                                 long ticksPerSecond, IEnumerable<string> header)
    {
        w.WriteLine("RynthCore flight recorder dump");
        foreach (string h in header) w.WriteLine(h);
        if (records.Length > 0)
        {
            double first = (records[0].Entry.Stamp - zeroStamp) * 1000.0 / ticksPerSecond;
            double last = (records[^1].Entry.Stamp - zeroStamp) * 1000.0 / ticksPerSecond;
            w.WriteLine($"Entries: {records.Length}, from {first / 1000.0:0.0} s to {last / 1000.0:+0.0;-0.0;0.0} s around the request.");
        }
        else
        {
            w.WriteLine("Entries: none.");
        }
        w.WriteLine();
        foreach (string l in Legend) w.WriteLine(l);
        w.WriteLine();
        foreach (FlightRecord r in records)
            w.WriteLine(Line(r, zeroStamp, zeroLocal, ticksPerSecond));
    }

    public static string Line(in FlightRecord r, long zeroStamp, DateTime zeroLocal, long ticksPerSecond)
    {
        double ms = (r.Entry.Stamp - zeroStamp) * 1000.0 / ticksPerSecond;
        DateTime at = zeroLocal.AddMilliseconds(ms);
        return $"{ms.ToString("+0;-0;0", Inv),9} ms  {at.ToString("HH:mm:ss.fff", Inv)}  {Arrow(r.Entry.Dir)} {Describe(r)}";
    }

    public static readonly string[] Legend =
    {
        "How to read: one line per message or marker, oldest first.",
        "  column 1: milliseconds relative to the dump request (negative = before it); column 2: local time.",
        "  ->  client to server (what this client sent).  [eng] = matches an engine direct-action call just before",
        "      (the 'engine ...' marker above it); [cli] = no engine call to match: the client's own path",
        "      (keyboard/mouse, or a plugin calling AC some other way). Movement matches are by time only.",
        "      IdentifyObject: [eng:autoid-target|autoid-container|autoid-inventory|autoid-world|plugin|self|ui]",
        "      = an engine appraisal and who asked for it; [cli] = AC itself or Decal.",
        "  <-  server to client: game events (UseDone, ViewContents, errors, magic, combat), the player's own",
        "      motion/position/teleport, system text, and messages about objects the character just used.",
        "  **  engine markers: engine calls, m_cBusy changes and resets, action-gate clears, a per-second link",
        "      and client-state summary ('-- 1s'), hot reloads, notes and dump triggers.",
        "  '#n' is the action/event sequence; 'seq i/s/t/f' are the instance/server-control/teleport/force-position",
        "  sequences; '(+N like it not kept)' counts position updates skipped by the rate limit; '...' = cut off.",
        "  Chat text the player says or reads is not recorded.",
    };
}

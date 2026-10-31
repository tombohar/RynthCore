// ============================================================================
//  RynthCore.Headless - Messages/MotionMessages.cs
//  The player's movement messages and the interpreted motion state the server echoes.
//
//  MoveToState (game action 0xF61C): RawMotionState, the 32-byte position (cell, x y z,
//  rotation w x y z), the instance / server-control / teleport / force-position
//  sequences, a contact byte (0x1 on the ground, 0x2 standing long jump), pad to 4.
//  RawMotionState: u32 = flags (low 11 bits) | command count << 11, then the fields whose
//  flag is set, in bit order: current hold key 0x1, current style 0x2, forward command
//  0x4, forward hold key 0x8, forward speed 0x10, sidestep command 0x20, sidestep hold
//  key 0x40, sidestep speed 0x80, turn command 0x100, turn hold key 0x200, turn speed
//  0x400. Omitted fields mean: hold key None, style NonCombat, forward Ready, speed 1.
//
//  The server answers a MoveToState with an UpdateMotion for us too, with its own speeds:
//  holding Run turns WalkForward into RunForward at the player's run rate, which is how
//  the client learns how fast the server thinks it runs (4 m/s x run rate).
//
//  Jump (0xF61B): extent, velocity x y z, the four sequences, then two u32 (object, spell)
//  that ACE reads before acting; they are sent as 0.
// ============================================================================

using RynthCore.Headless.Net;
using RynthCore.PluginSdk.Net;

namespace RynthCore.Headless.Messages;

public static class MotionCommand
{
    public const uint Ready = 0x41000003, WalkForward = 0x45000005, WalkBackwards = 0x45000006, RunForward = 0x44000007,
        TurnRight = 0x6500000D, TurnLeft = 0x6500000E, SideStepRight = 0x6500000F, SideStepLeft = 0x65000010;
    public const uint HoldNone = 1, HoldRun = 2;
    public const uint NonCombat = 0x8000003D;

    /// <summary>Base ground speeds (m/s at speed 1): walk, run, sidestep.</summary>
    public const float WalkSpeed = 3.12f, RunSpeed = 4.0f, SidestepSpeed = 1.25f;
}

/// <summary>What the player is pressing, as RawMotionState carries it.</summary>
public readonly record struct RawMotion(uint ForwardCommand = 0, bool Run = false, uint TurnCommand = 0, float TurnSpeed = 1f,
    uint SidestepCommand = 0)
{
    public static RawMotion Stop => new();
    public static RawMotion RunForward => new(MotionCommand.WalkForward, Run: true);
    public static RawMotion WalkForward => new(MotionCommand.WalkForward);
    public bool Moving => ForwardCommand != 0 || TurnCommand != 0 || SidestepCommand != 0;
}

/// <summary>The interpreted motion state of an UpdateMotion (movement type 0).</summary>
public readonly record struct InterpretedMotion(uint Style, uint ForwardCommand, float ForwardSpeed, uint SidestepCommand,
    float SidestepSpeed, uint TurnCommand, float TurnSpeed);

internal static class MotionMessages
{
    private const uint FHoldKey = 0x1, FStyle = 0x2, FForward = 0x4, FForwardHold = 0x8, FForwardSpeed = 0x10,
        FSidestep = 0x20, FSidestepHold = 0x40, FSidestepSpeed = 0x80, FTurn = 0x100, FTurnHold = 0x200, FTurnSpeed = 0x400;

    public static void WriteRawMotion(WireWriter w, RawMotion m)
    {
        uint flags = 0;
        if (m.Run) flags |= FHoldKey;
        if (m.ForwardCommand != 0) flags |= FForward | FForwardHold;
        if (m.SidestepCommand != 0) flags |= FSidestep | FSidestepHold;
        if (m.TurnCommand != 0) flags |= FTurn | FTurnHold;
        if (m.TurnCommand != 0 && m.TurnSpeed != 1f) flags |= FTurnSpeed;
        uint hold = m.Run ? MotionCommand.HoldRun : MotionCommand.HoldNone;
        w.U32(flags);
        if ((flags & FHoldKey) != 0) w.U32(hold);
        if ((flags & FForward) != 0) w.U32(m.ForwardCommand);
        if ((flags & FForwardHold) != 0) w.U32(hold);
        if ((flags & FSidestep) != 0) w.U32(m.SidestepCommand);
        if ((flags & FSidestepHold) != 0) w.U32(hold);
        if ((flags & FTurn) != 0) w.U32(m.TurnCommand);
        if ((flags & FTurnHold) != 0) w.U32(hold);
        if ((flags & FTurnSpeed) != 0) w.F32(m.TurnSpeed);
    }

    public static byte[] MoveToState(RawMotion m, WorldPosition p, ushort instance, ushort serverControl, ushort teleport,
        ushort forcePosition, bool onGround)
    {
        var w = new WireWriter(80);
        WriteRawMotion(w, m);
        w.U32(p.Cell).F32(p.X).F32(p.Y).F32(p.Z).F32(p.Qw).F32(p.Qx).F32(p.Qy).F32(p.Qz);
        w.U16(instance).U16(serverControl).U16(teleport).U16(forcePosition);
        w.U8(onGround ? (byte)1 : (byte)0);
        return w.Align4().ToArray();
    }

    public static byte[] Jump(float extent, float vx, float vy, float vz, ushort instance, ushort serverControl, ushort teleport, ushort forcePosition)
        => new WireWriter(32).F32(extent).F32(vx).F32(vy).F32(vz).U16(instance).U16(serverControl).U16(teleport).U16(forcePosition)
            .U32(0).U32(0).ToArray();

    /// <summary>
    /// The interpreted state in an UpdateMotion body of movement type 0: u32 flags (bits 0-6) |
    /// command count &lt;&lt; 7; then u16 style, u16 forward, u16 sidestep, u16 turn (each if
    /// flagged; commands are their low 16 bits), then f32 forward / sidestep / turn speeds.
    /// </summary>
    public static bool TryInterpreted(ReadOnlySpan<byte> updateMotionBody, out InterpretedMotion m)
    {
        m = default;
        var r = new NetReader(updateMotionBody);
        if (!r.Skip(4 + 2 + 2 + 2 + 1 + 1) || !r.U8(out byte type) || !r.U8(out _) || !r.U16(out ushort stance) || type != 0)
            return false;
        if (!r.U32(out uint f)) return false;
        uint style = 0x80000000u | stance, fwd = MotionCommand.Ready, side = 0, turn = 0;
        float fs = 1, ss = 1, ts = 1;
        ushort u16;
        if ((f & 0x01) != 0) { if (!r.U16(out u16)) return false; style = 0x80000000u | u16; }
        if ((f & 0x02) != 0) { if (!r.U16(out u16)) return false; fwd = Full(u16); }
        if ((f & 0x08) != 0) { if (!r.U16(out u16)) return false; side = Full(u16); }
        if ((f & 0x20) != 0) { if (!r.U16(out u16)) return false; turn = Full(u16); }
        if ((f & 0x04) != 0 && !r.F32(out fs)) return false;
        if ((f & 0x10) != 0 && !r.F32(out ss)) return false;
        if ((f & 0x40) != 0 && !r.F32(out ts)) return false;
        m = new InterpretedMotion(style, fwd, fs, side, ss, turn, ts);
        return true;
    }

    /// <summary>The full motion id for the low 16 bits the server sends (the ones a player's state uses).</summary>
    private static uint Full(ushort low) => low switch
    {
        0x0003 => MotionCommand.Ready,
        0x0005 => MotionCommand.WalkForward,
        0x0006 => MotionCommand.WalkBackwards,
        0x0007 => MotionCommand.RunForward,
        0x000D => MotionCommand.TurnRight,
        0x000E => MotionCommand.TurnLeft,
        0x000F => MotionCommand.SideStepRight,
        0x0010 => MotionCommand.SideStepLeft,
        _ => low,
    };
}

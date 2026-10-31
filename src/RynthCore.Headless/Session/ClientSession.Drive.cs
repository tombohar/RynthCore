// ============================================================================
//  RynthCore.Headless - Session/ClientSession.Drive.cs
//  Manual driving: what a plugin does with the movement keys (the host's SetAutoRun,
//  SetMotion, DoMovement / StopMovement, StopCompletely, TurnToHeading), as opposed to
//  a planned walk to a point (ClientSession.Movement.cs).
//
//  The drive state is the keys held: autorun, a forward/backward command, a turn and a
//  sidestep. While any of them is held, every StepMs the player moves along its heading
//  (forward at MoveSpeed when running, sidestep and backwards slower) through the same
//  Walker as a planned walk, and turns at TurnRateDegPerSec. What the server hears is
//  the same as for a walk: a MoveToState with the keys whenever they change (the first
//  one is the "run forward"), an AutonomousPosition every ReportMs while moving, and a
//  MoveToState "stop" plus a final position when the keys are all released.
//  TurnToHeading turns at once (like the engine's direct heading write); standing
//  still, the new heading goes out with the next step's position report.
//
//  Living with the other movers:
//    - a drive command that moves cancels our own planned walk (quietly: the drive's
//      MoveToState replaces the walk's run on the server);
//    - a planned walk that starts while driving ends the drive (its stop goes out
//      first, then the walk's own run);
//    - the server's move-to (a use or pickup out of reach) pauses the drive: nothing is
//      stepped and no MoveToState is sent while it lasts (one would cancel the server's
//      pending action); the keys stay held and the drive resumes when the move-to ends;
//    - a teleport releases every key (nothing is sent: we are in portal space).
//
//  Also here: the few raw game actions the host's chat parser and jumps need.
// ============================================================================

using RynthCore.Headless.Messages;
using RynthCore.Headless.Movement;
using RynthCore.Headless.Net;
using RynthCore.Headless.World;

namespace RynthCore.Headless.Session;

public sealed partial class ClientSession
{
    /// <summary>Turn rate while a turn key is held (AC: 90 deg/s at turn speed 1, x1.5 running).</summary>
    public const float TurnRateDegPerSec = 135f;

    /// <summary>Backwards runs at this fraction of the forward speed (AC's backwards factor).</summary>
    public const float BackwardsFactor = 0.65f;

    private bool _driveAutoRun, _driveRun = true;
    private uint _driveForward, _driveTurn, _driveSide;
    private RawMotion _driveSent;              // the keys the server last heard from the drive (Stop = none)
    private long _driveLastStepMs;
    private bool _driveHeadingDirty, _driveHooked;

    /// <summary>The session clock (ms), for the host's timers.</summary>
    internal long NowMs => _now();

    /// <summary>True while the server is moving the player to something (a use / pickup out of reach).</summary>
    public bool ServerMoving => Moving && _serverMove;

    /// <summary>True while autorun is on.</summary>
    public bool AutoRun => _driveAutoRun;

    /// <summary>The movement keys held, as a MoveToState carries them.</summary>
    public RawMotion DriveMotion
    {
        get
        {
            uint fwd = _driveAutoRun ? MotionCommand.WalkForward : _driveForward;
            bool run = _driveAutoRun || _driveRun;
            var m = new RawMotion(fwd, run, _driveTurn, 1f, _driveSide);
            return m.Moving ? m : RawMotion.Stop;
        }
    }

    /// <summary>True while a movement key is held (whether or not a server move-to has paused it).</summary>
    public bool Driving => DriveMotion.Moving;

    /// <summary>Autorun on / off (the engine's SetAutoRun).</summary>
    public void SetAutoRun(bool on)
    {
        if (_driveAutoRun == on) return;
        ApplyDrive(() => _driveAutoRun = on);
    }

    /// <summary>
    /// Holds or releases one movement key (the engine's SetMotion / DoMovement / StopMovement):
    /// WalkForward / RunForward, WalkBackwards, TurnRight / TurnLeft, SideStepRight / SideStepLeft.
    /// <paramref name="run"/> is the run hold key. False for any other motion.
    /// </summary>
    public bool SetDriveMotion(uint motion, bool on, bool run = true)
    {
        switch (motion)
        {
            case MotionCommand.WalkForward:
            case MotionCommand.RunForward:
                if (on) ApplyDrive(() => { _driveForward = MotionCommand.WalkForward; _driveRun = run || motion == MotionCommand.RunForward; });
                else if (_driveForward == MotionCommand.WalkForward) ApplyDrive(() => _driveForward = 0);
                return true;
            case MotionCommand.WalkBackwards:
                if (on) ApplyDrive(() => { _driveForward = MotionCommand.WalkBackwards; _driveRun = run; _driveAutoRun = false; });
                else if (_driveForward == MotionCommand.WalkBackwards) ApplyDrive(() => _driveForward = 0);
                return true;
            case MotionCommand.TurnRight:
            case MotionCommand.TurnLeft:
                if (on) ApplyDrive(() => _driveTurn = motion);
                else if (_driveTurn == motion) ApplyDrive(() => _driveTurn = 0);
                return true;
            case MotionCommand.SideStepRight:
            case MotionCommand.SideStepLeft:
                if (on) ApplyDrive(() => _driveSide = motion);
                else if (_driveSide == motion) ApplyDrive(() => _driveSide = 0);
                return true;
            case MotionCommand.Ready:
                if (on) ApplyDrive(() => { _driveForward = 0; _driveAutoRun = false; });
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Releases every key and stops (the engine's StopCompletely): the drive's stop goes out,
    /// and our own planned walk ends too. The server's move-to is left alone.
    /// </summary>
    public void StopCompletely()
    {
        if (Moving && !_serverMove) StopMoving();
        ApplyDrive(() => { _driveAutoRun = false; _driveForward = 0; _driveTurn = 0; _driveSide = 0; });
    }

    /// <summary>
    /// Faces a compass heading (degrees, 0 = north, clockwise) at once. While driving it rides
    /// the next position report; standing still it goes out with the next step (at most StepMs).
    /// </summary>
    public void FaceHeading(float headingDegrees)
    {
        if (State != SessionState.InWorld || World.PlayerObject is not { } me || me.Position is not { } p) return;
        if (!float.IsFinite(headingDegrees)) return;
        me.Position = WithHeading(p, headingDegrees * MathF.PI / 180f);
        _driveHeadingDirty = true;
    }

    /// <summary>The player's compass heading in degrees (0 = north, clockwise), from its rotation.</summary>
    public static float HeadingDegrees(WorldPosition p)
    {
        float deg = -2f * MathF.Atan2(p.Qz, p.Qw) * 180f / MathF.PI;
        deg %= 360f;
        return deg < 0 ? deg + 360f : deg;
    }

    // ── internals ───────────────────────────────────────────────────────────

    private void ApplyDrive(Action change)
    {
        HookDrive();
        long now = _now();
        // Catch the position up to now under the old keys, then change them.
        if (CanDrive) AdvanceDrive(now);
        change();
        if (State != SessionState.InWorld) return;
        RawMotion m = DriveMotion;
        if (m.Moving && Moving && !_serverMove)
        {
            // A drive key cancels our planned walk; the drive's MoveToState replaces its run.
            _announced = false;
            EndMove(false, "manual drive");
        }
        if (Moving && _serverMove) return;   // paused: TickDrive sends it once the server's move-to ends
        SendDrive(m, now);
    }

    /// <summary>The same keys held (no keys at all is one state, whatever the hold key or turn speed).</summary>
    private static bool SameKeys(RawMotion a, RawMotion b)
        => a.Moving == b.Moving && (!a.Moving || (a.ForwardCommand == b.ForwardCommand && a.TurnCommand == b.TurnCommand
            && a.SidestepCommand == b.SidestepCommand && a.Run == b.Run));

    private bool CanDrive => State == SessionState.InWorld && !Moving && _driveSent.Moving;

    private void SendDrive(RawMotion m, long now)
    {
        if (SameKeys(m, _driveSent)) return;
        WorldObject? me = World.PlayerObject;
        if (me?.Position is not { } p) return;
        bool starting = !_driveSent.Moving;
        _driveSent = m;
        SendMotion(m);
        if (starting) _driveLastStepMs = now;
        if (!m.Moving || starting) SendPosition(p);
        _driveHeadingDirty = false;
    }

    private void HookDrive()
    {
        if (_driveHooked) return;
        _driveHooked = true;
        World.PlayerTeleported += ReleaseDriveQuietly;
        StateChanged += s => { if (s != SessionState.InWorld) ReleaseDriveQuietly(); };
    }

    /// <summary>Every key released, nothing sent (portal space, or a walk that took over and says so itself).</summary>
    private void ReleaseDriveQuietly()
    {
        _driveAutoRun = false;
        _driveForward = _driveTurn = _driveSide = 0;
        _driveSent = RawMotion.Stop;
        _driveHeadingDirty = false;
    }

    private void TickDrive()
    {
        if (State != SessionState.InWorld) return;
        long now = _now();
        if (Moving)
        {
            if (!_serverMove && (_driveSent.Moving || Driving))
            {
                // Our planned walk took over: the drive stops (its stop first, then the walk's run).
                if (_driveSent.Moving) SendMotion(RawMotion.Stop);
                ReleaseDriveQuietly();
            }
            else if (_serverMove)
            {
                // Paused: the server's move-to replaces our motion on its side, so the keys
                // still held go out afresh (with a position) once it ends.
                _driveSent = RawMotion.Stop;
            }
            _driveLastStepMs = now;
            return;
        }

        RawMotion m = DriveMotion;
        if (!SameKeys(m, _driveSent)) SendDrive(m, now);   // resuming after a server move-to
        if (!m.Moving)
        {
            if (_driveHeadingDirty && now - _lastReportMs >= StepMs && World.PlayerObject?.Position is { } p)
            {
                SendPosition(p);
                _driveHeadingDirty = false;
            }
            return;
        }
        if (now - _driveLastStepMs < StepMs) return;
        AdvanceDrive(now);
        if (now - _lastReportMs >= ReportMs && World.PlayerObject?.Position is { } q)
        {
            SendPosition(q);
            _driveHeadingDirty = false;
        }
    }

    /// <summary>Moves the player under the keys the server last heard, from the last step to now.</summary>
    private void AdvanceDrive(long now)
    {
        RawMotion m = _driveSent;
        long elapsed = now - _driveLastStepMs;
        if (elapsed <= 0) return;
        _driveLastStepMs = now;
        if (!m.Moving || World.PlayerObject is not { } me || me.Position is not { } from) return;
        float dt = Math.Min(elapsed, 3 * StepMs) / 1000f;

        float heading = HeadingDegrees(from) * MathF.PI / 180f;
        float turn = TurnRateDegPerSec * MathF.PI / 180f * dt;
        if (m.TurnCommand == MotionCommand.TurnRight) heading += turn;
        else if (m.TurnCommand == MotionCommand.TurnLeft) heading -= turn;

        float speed = m.Run ? MoveSpeed : MotionCommand.WalkSpeed;
        float forward = m.ForwardCommand switch
        {
            MotionCommand.WalkForward or MotionCommand.RunForward => speed,
            MotionCommand.WalkBackwards => -speed * BackwardsFactor,
            _ => 0f,
        };
        float side = m.SidestepCommand switch
        {
            MotionCommand.SideStepRight => MotionCommand.SidestepSpeed * (m.Run ? 1.5f : 1f),
            MotionCommand.SideStepLeft => -MotionCommand.SidestepSpeed * (m.Run ? 1.5f : 1f),
            _ => 0f,
        };
        float sin = MathF.Sin(heading), cos = MathF.Cos(heading);
        float dx = (sin * forward + cos * side) * dt;
        float dy = (cos * forward - sin * side) * dt;
        WorldPosition turned = WithHeading(from, heading);
        me.Position = dx != 0 || dy != 0 ? Walker.Step(turned, dx, dy).Position : turned;
    }

    // ── raw actions for the host (chat parser, jumps) ─────────────────────

    /// <summary>/tell: game action 0x005D (message, then the target's name).</summary>
    public void Tell(string target, string message)
        => SendAction(0x005D, new WireWriter(16 + message.Length + target.Length).String16L(message).String16L(target).ToArray());

    /// <summary>/emote: game action 0x01DF.</summary>
    public void Emote(string text) => SendAction(0x01DF, new WireWriter(8 + text.Length).String16L(text).ToArray());

    /// <summary>/me (soul emote): game action 0x01E1.</summary>
    public void SoulEmote(string text) => SendAction(0x01E1, new WireWriter(8 + text.Length).String16L(text).ToArray());

    /// <summary>A jump (game action 0xF61B): extent 0..1 and the launch velocity (world frame, m/s).</summary>
    public void Jump(float extent, float vx, float vy, float vz)
    {
        WorldObject? me = World.PlayerObject;
        if (me == null) return;
        SendAction(ActionType.Jump, MotionMessages.Jump(Math.Clamp(extent, 0f, 1f), vx, vy, vz,
            me.InstanceSeq, me.ServerControlSeq, me.TeleportSeq, me.ForcePositionSeq));
    }
}

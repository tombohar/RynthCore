using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

internal static class PlayerPhysicsHooks
{
    private const int PhysicsPositionOffset = 0x48;
    private const int PositionObjCellIdOffset = 0x04;
    private const int PositionQwOffset = 0x08;
    private const int PositionQxOffset = 0x0C;
    private const int PositionQyOffset = 0x10;
    private const int PositionQzOffset = 0x14;
    private const int PositionOriginXOffset = 0x3C;
    private const int PositionOriginYOffset = 0x40;
    private const int PositionOriginZOffset = 0x44;
    private const int ReferencePhysicsGetHeading = 0x00512010;
    private const int ReferencePhysicsSetHeading = 0x00514C60;

    // CPhysicsObj::m_pMovementManager lives at +0xC4 (same offset UB's Jumper uses).
    // The CMotionInterp pointer is the first field of MovementManager.
    private const int MovementManagerOffset = 0xC4;

    // CMotionInterp::get_max_speed — Chorizite RVA 0x001278C0, our client is +0x1000
    // from Chorizite (confirmed via set_heading/get_heading/CommenceJump addresses).
    private const int ReferenceGetMaxSpeed = 0x005288C0;

    // Fields in CMotionInterp holding the "current motion command" for each
    // channel. Writing these right before DoJump tells the physics simulation
    // to launch the jump with that velocity baked in.
    private const int CMotionForwardIdOffset = 76;   // 0x4C
    private const int CMotionForwardSpeedOffset = 80; // 0x50
    private const int CMotionStrafeIdOffset = 84;    // 0x54
    private const int CMotionStrafeSpeedOffset = 88; // 0x58
    private const int CMotionTurnOffset = 92;        // 0x5C

    // Raw motion-command IDs the physics simulation reads from CMotionInterp.
    // Sourced from UB's UBHelper.Jumper — these are *not* the SetMotion IDs.
    private const int MotionIdForward = 0x44000007;
    private const int MotionIdBackward = 0x45000005;
    private const int MotionIdIdle = 0x41000003;
    private const int MotionIdSidestep = 0x6500000F;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate float PhysicsGetHeadingDelegate(IntPtr physicsObjPtr);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void PhysicsSetHeadingDelegate(IntPtr physicsObjPtr, float headingDegrees, int sendEvent);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate double GetMaxSpeedDelegate(IntPtr cmotionInterpPtr);

    private static string _statusMessage = "Not probed yet.";
    private static PhysicsGetHeadingDelegate? _getHeading;
    private static PhysicsSetHeadingDelegate? _setHeading;
    private static GetMaxSpeedDelegate? _getMaxSpeed;

    public static bool IsInitialized { get; private set; }
    public static bool HasGetHeading => _getHeading != null;
    public static bool HasSetHeading => _setHeading != null;
    public static string StatusMessage => _statusMessage;

    public static bool Probe()
    {
        bool ready = SmartBoxLocator.Probe();
        IsInitialized = ready;
        _statusMessage = ready ? "Ready." : SmartBoxLocator.StatusMessage;

        if (ready)
        {
            BindPhysicsDelegates();
            RynthLog.Verbose($"Compat: player physics ready - smartbox candidates={SmartBoxLocator.CandidateCount}");
        }

        return ready;
    }

    public static bool TryGetPlayerPose(
        out uint objCellId,
        out float x,
        out float y,
        out float z,
        out float qw,
        out float qx,
        out float qy,
        out float qz)
    {
        objCellId = 0;
        x = y = z = 0;
        qw = 1f;
        qx = qy = qz = 0;

        if (!SmartBoxLocator.TryGetPlayer(out IntPtr player, out _, out string failure))
        {
            _statusMessage = failure;
            return false;
        }

        try
        {
            IntPtr pos = player + PhysicsPositionOffset;
            objCellId = unchecked((uint)Marshal.ReadInt32(pos + PositionObjCellIdOffset));
            qw = ReadFloat(pos + PositionQwOffset);
            qx = ReadFloat(pos + PositionQxOffset);
            qy = ReadFloat(pos + PositionQyOffset);
            qz = ReadFloat(pos + PositionQzOffset);
            x = ReadFloat(pos + PositionOriginXOffset);
            y = ReadFloat(pos + PositionOriginYOffset);
            z = ReadFloat(pos + PositionOriginZOffset);

            IsInitialized = true;
            _statusMessage = "Ready.";
            return true;
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            return false;
        }
    }

    public static bool TryGetPlayerHeading(out float headingDegrees)
    {
        headingDegrees = 0;

        if (!EnsurePhysicsDelegates())
            return false;

        if (!SmartBoxLocator.TryGetPlayer(out IntPtr player, out _, out string failure))
        {
            _statusMessage = failure;
            return false;
        }

        try
        {
            headingDegrees = NormalizeDecalHeading(_getHeading!(player));
            IsInitialized = true;
            _statusMessage = "Ready.";
            return true;
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            return false;
        }
    }

    public static bool SetPlayerHeading(float headingDegrees)
    {
        if (!EnsurePhysicsDelegates() || _setHeading == null)
            return false;

        if (!SmartBoxLocator.TryGetPlayer(out IntPtr player, out _, out string failure))
        {
            _statusMessage = failure;
            return false;
        }

        try
        {
            _setHeading(player, NormalizePhysicsHeading(headingDegrees), 1);
            IsInitialized = true;
            _statusMessage = "Ready.";
            return true;
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Sets the player heading by directly writing the orientation quaternion
    /// in the player's CPhysicsObj position struct. This is an instant snap —
    /// equivalent to old Decal <c>Actions.Heading = value</c>.
    ///
    /// Uses the same SmartBox memory offsets we read from in TryGetPlayerPose,
    /// so it's reliable whenever pose reads work. Does not depend on any
    /// hardcoded function VAs.
    /// </summary>
    public static bool SetPlayerHeadingDirect(float decalHeadingDeg)
    {
        if (!SmartBoxLocator.TryGetPlayer(out IntPtr player, out _, out string failure))
        {
            _statusMessage = failure;
            return false;
        }

        try
        {
            // Convert Decal heading (0°=N, clockwise) to physics yaw (0°=N, counter-clockwise)
            double physYawDeg = -decalHeadingDeg;
            double physYawRad = physYawDeg * (Math.PI / 180.0);

            // Yaw-only quaternion: q = (cos(yaw/2), 0, 0, sin(yaw/2))
            float newQw = (float)Math.Cos(physYawRad * 0.5);
            float newQz = (float)Math.Sin(physYawRad * 0.5);

            IntPtr pos = player + PhysicsPositionOffset;
            WriteFloat(pos + PositionQwOffset, newQw);
            WriteFloat(pos + PositionQzOffset, newQz);

            IsInitialized = true;
            _statusMessage = "Ready.";
            return true;
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            return false;
        }
    }

    public static bool HasLaunchJumpWithMotion => SmartBoxLocator.IsPointerInModule(new(ReferenceGetMaxSpeed));

    /// <summary>
    /// Writes motion-vector fields directly into CMotionInterp, calls DoJump(1),
    /// then clears them back out. Mirrors UB's UBHelper.Jumper algorithm, minus
    /// the power-bar hook (the plugin drives timing with a ms delay).
    ///
    /// This is the only reliable way to jump *with forward/back/strafe momentum*
    /// in AC — SetMotion routes through the command interpreter, which doesn't
    /// bake velocity into the physics simulation in time for DoJump.
    /// </summary>
    public static bool LaunchJumpWithMotion(bool shift, bool holdW, bool holdX, bool holdZ, bool holdC)
    {
        if (!TryGetCMotionInterp(out IntPtr cmi))
        {
            RynthLog.Compat("LaunchJumpWithMotion: CMotionInterp unavailable");
            return false;
        }

        if (!TryGetMaxSpeed(cmi, out double maxSpeed))
        {
            RynthLog.Compat("LaunchJumpWithMotion: get_max_speed failed");
            return false;
        }

        double num = maxSpeed / 4.0;

        IntPtr pForwardId = cmi + CMotionForwardIdOffset;
        IntPtr pForwardSpeed = cmi + CMotionForwardSpeedOffset;
        IntPtr pStrafeId = cmi + CMotionStrafeIdOffset;
        IntPtr pStrafeSpeed = cmi + CMotionStrafeSpeedOffset;
        IntPtr pTurn = cmi + CMotionTurnOffset;

        try
        {
            // Forward/back channel
            if (holdW || holdX)
            {
                if (holdW)
                {
                    Marshal.WriteInt32(pForwardId, MotionIdForward);
                    WriteFloat(pForwardSpeed, shift ? 1f : (float)num);
                }
                else
                {
                    Marshal.WriteInt32(pForwardId, MotionIdBackward);
                    WriteFloat(pForwardSpeed, shift ? -1f : (float)(-0.65 * num));
                }
            }
            else
            {
                Marshal.WriteInt32(pForwardId, MotionIdIdle);
            }

            // Strafe channel
            if (holdZ || holdC)
            {
                Marshal.WriteInt32(pStrafeId, MotionIdSidestep);
                double s = num * 1.248;
                if (shift) s *= 0.5;
                if (s > 3.0) s = 3.0;
                WriteFloat(pStrafeSpeed, holdC ? (float)s : (float)(-s));
            }
            else
            {
                Marshal.WriteInt32(pStrafeId, 0);
            }

            Marshal.WriteInt32(pTurn, 0);

            bool jumped = CommandInterpreterHooks.DoJump(true);

            // Clear immediately so the character doesn't keep walking on landing.
            if (holdZ || holdC) Marshal.WriteInt32(pStrafeId, 0);
            if (holdW || holdX) Marshal.WriteInt32(pForwardId, MotionIdIdle);

            return jumped;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"LaunchJumpWithMotion: write failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryGetCMotionInterp(out IntPtr cmotionInterp)
    {
        cmotionInterp = IntPtr.Zero;

        if (!SmartBoxLocator.TryGetPlayer(out IntPtr player, out _, out string failure))
        {
            _statusMessage = failure;
            return false;
        }

        try
        {
            IntPtr movementManager = Marshal.ReadIntPtr(player + MovementManagerOffset);
            if (movementManager == IntPtr.Zero) return false;
            cmotionInterp = Marshal.ReadIntPtr(movementManager);
            return cmotionInterp != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetMaxSpeed(IntPtr cmotionInterp, out double maxSpeed)
    {
        maxSpeed = 0;

        if (_getMaxSpeed == null)
        {
            IntPtr ptr = new(ReferenceGetMaxSpeed);
            if (!SmartBoxLocator.IsPointerInModule(ptr))
                return false;
            _getMaxSpeed = Marshal.GetDelegateForFunctionPointer<GetMaxSpeedDelegate>(ptr);
        }

        try
        {
            maxSpeed = _getMaxSpeed(cmotionInterp);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteFloat(IntPtr address, float value)
    {
        Marshal.WriteInt32(address, BitConverter.SingleToInt32Bits(value));
    }

    private static float ReadFloat(IntPtr address)
    {
        int bits = Marshal.ReadInt32(address);
        return BitConverter.Int32BitsToSingle(bits);
    }

    // CPhysicsObj heading is rotated 90 degrees from the Decal/VTank basis
    // used by old RynthAi Actions.Heading. Normalize here so plugins can stay
    // on the original T1 heading math.
    private static float NormalizeDecalHeading(float physicsHeading)
    {
        return NormalizeDegrees(90f - physicsHeading);
    }

    private static float NormalizePhysicsHeading(float decalHeading)
    {
        return NormalizeDegrees(90f - decalHeading);
    }

    private static float NormalizeDegrees(float heading)
    {
        while (heading < 0f)
            heading += 360f;

        while (heading >= 360f)
            heading -= 360f;

        return heading;
    }

    /// <summary>
    /// Computes live NS/EW coordinates from the player's physics pose
    /// (objCellId + local x,y) using the classic in-game coordinate basis.
    /// This keeps steering and overlays on the same source of truth and
    /// avoids stale cached coordinate reads while running.
    /// </summary>
    public static bool TryGetLiveCoords(out double northSouth, out double eastWest)
    {
        northSouth = 0;
        eastWest = 0;

        if (!TryGetPlayerPose(out uint objCellId, out float x, out float y, out _, out _, out _, out _, out _))
            return false;

        int lbX = (int)((objCellId >> 24) & 0xFF);
        int lbY = (int)((objCellId >> 16) & 0xFF);

        // Match the classic radar/coordinates basis exactly:
        //   EW = ((Landcell >> 24) * 8 + X / 24 - 1019.5) / 10
        //   NS = (((Landcell >> 16) & 0xFF) * 8 + Y / 24 - 1019.5) / 10
        eastWest = (lbX * 8.0 + x / 24.0 - 1019.5) / 10.0;
        northSouth = (lbY * 8.0 + y / 24.0 - 1019.5) / 10.0;
        return true;
    }

    private static bool EnsurePhysicsDelegates()
    {
        if (_getHeading != null && _setHeading != null)
            return true;

        if (!SmartBoxLocator.Probe())
        {
            _statusMessage = SmartBoxLocator.StatusMessage;
            return false;
        }

        return BindPhysicsDelegates();
    }

    private static bool BindPhysicsDelegates()
    {
        if (_getHeading != null && _setHeading != null)
            return true;

        IntPtr getHeadingPtr = new(ReferencePhysicsGetHeading);
        IntPtr setHeadingPtr = new(ReferencePhysicsSetHeading);
        if (!SmartBoxLocator.IsPointerInModule(getHeadingPtr) || !SmartBoxLocator.IsPointerInModule(setHeadingPtr))
        {
            _statusMessage =
                $"Physics heading pointers look invalid (get=0x{getHeadingPtr.ToInt32():X8}, set=0x{setHeadingPtr.ToInt32():X8}).";
            RynthLog.Compat($"Compat: player physics heading bind failed - {_statusMessage}");
            return false;
        }

        _getHeading = Marshal.GetDelegateForFunctionPointer<PhysicsGetHeadingDelegate>(getHeadingPtr);
        _setHeading = Marshal.GetDelegateForFunctionPointer<PhysicsSetHeadingDelegate>(setHeadingPtr);
        RynthLog.Verbose(
            $"Compat: player heading hooks ready - get=0x{getHeadingPtr.ToInt32():X8}, set=0x{setHeadingPtr.ToInt32():X8}");
        return true;
    }
}

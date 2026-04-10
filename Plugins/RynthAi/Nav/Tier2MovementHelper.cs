using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;

namespace NexSuite.Plugins.RynthAi
{
    /// <summary>
    /// Tier 2 movement: calls MovementManager::PerformMovement directly on the
    /// player's CPhysicsObj.  The client's own pathfinding handles interpolation,
    /// heading, speed — producing smooth arcs instead of autorun + PD-controller
    /// correction jitter.
    ///
    /// Pointer chain (all from acclient.exe decompilation + binary verification):
    ///   [SmartBox::smartbox] → SmartBox*
    ///     [SmartBox+0xF8]   → CPhysicsObj*   (player)
    ///       [player+0xC4]   → MovementManager*
    ///         PerformMovement(__thiscall, const MovementStruct*)
    ///
    /// MovementTypes (from MovementStruct.type):
    ///   6 = MoveToObject      — approach a world object
    ///   7 = MoveToPosition    — walk/run to an (x,y,z) in a landblock cell
    ///   8 = TurnToObject      — face a world object
    ///   9 = TurnToHeading     — face a compass heading (degrees)
    ///
    /// All structs are stack-allocated in unmanaged memory, filled, passed to the
    /// native function, then freed.  No persistent allocations.
    ///
    /// Pattern scans (version-independent):
    ///   PerformMovement — anchored by its unique return-71 epilogue
    ///   CancelMoveTo    — immediately follows PerformMovement (reads [ecx+4])
    ///   Position vtable — found in CPhysicsObj::MoveToObject's stack init
    ///   MovParams vtable — found in MovementParameters constructor (writes speed=1.0)
    /// </summary>
    internal static class Tier2MovementHelper
    {
        // ═══════════════════════════════════════════════════════════════════════
        //  MOVEMENT TYPE CONSTANTS
        // ═══════════════════════════════════════════════════════════════════════

        public const uint TYPE_MOVE_TO_OBJECT   = 6;
        public const uint TYPE_MOVE_TO_POSITION = 7;
        public const uint TYPE_TURN_TO_OBJECT   = 8;
        public const uint TYPE_TURN_TO_HEADING  = 9;

        // ═══════════════════════════════════════════════════════════════════════
        //  STRUCT SIZES & OFFSETS
        // ═══════════════════════════════════════════════════════════════════════

        // ── SmartBox ──────────────────────────────────────────────────────────
        private const int SB_PLAYER_ID     = 0xF4;
        private const int SB_PLAYER        = 0xF8;
        private const int SB_CMDINTERP     = 0xB8;

        // ── CPhysicsObj ───────────────────────────────────────────────────────
        private const int PO_POSITION      = 0x48;   // Position (embedded, 72 bytes — starts at vtable)
        private const int PO_MOVEMENT_MGR  = 0xC4;   // MovementManager*

        // ── MovementStruct (0x64 = 100 bytes) ────────────────────────────────
        private const int MS_SIZE          = 0x64;
        private const int MS_TYPE          = 0x00;    // uint  MovementTypes::Type
        private const int MS_MOTION        = 0x04;    // uint  motion
        private const int MS_OBJECT_ID     = 0x08;    // uint  object_id
        private const int MS_TOP_LEVEL_ID  = 0x0C;    // uint  top_level_id
        private const int MS_POS           = 0x10;    // Position (72 bytes, embedded)
        private const int MS_RADIUS        = 0x58;    // float radius
        private const int MS_HEIGHT        = 0x5C;    // float height
        private const int MS_PARAMS        = 0x60;    // MovementParameters* (pointer)

        // ── Position (0x48 = 72 bytes, embedded inside MovementStruct) ───────
        //    Relative to MS_POS:
        //    Frame layout: quaternion(16) → cache/matrix(36) → origin(12)
        //    (verified from binary: origin at Pos+0x3C, not Pos+0x18)
        private const int POS_VTABLE       = 0x00;    // Position_vtbl*
        private const int POS_OBJCELL_ID   = 0x04;    // uint32 landblock cell
        private const int POS_QW           = 0x08;    // float  Frame.qw (quaternion w)
        private const int POS_QX           = 0x0C;    // float  Frame.qx
        private const int POS_QY           = 0x10;    // float  Frame.qy
        private const int POS_QZ           = 0x14;    // float  Frame.qz
        private const int POS_CACHE        = 0x18;    // float[9] (3×3 rotation matrix, 36 bytes)
        private const int POS_ORIGIN_X     = 0x3C;    // float  Frame.m_fOrigin.x
        private const int POS_ORIGIN_Y     = 0x40;    // float  Frame.m_fOrigin.y
        private const int POS_ORIGIN_Z     = 0x44;    // float  Frame.m_fOrigin.z

        // ── MovementParameters (0x2C = 44 bytes) ────────────────────────────
        private const int MP_SIZE          = 0x2C;
        private const int MP_VTABLE        = 0x00;    // MovementParameters_vtbl*
        private const int MP_BITFIELD      = 0x04;    // uint   flags
        private const int MP_DIST_TO_OBJ   = 0x08;    // float  distance_to_object
        private const int MP_MIN_DIST      = 0x0C;    // float  min_distance
        private const int MP_HEADING       = 0x10;    // float  desired_heading
        private const int MP_SPEED         = 0x14;    // float  speed
        private const int MP_FAIL_DIST     = 0x18;    // float  fail_distance
        private const int MP_WR_THRESH     = 0x1C;    // float  walk_run_threshhold
        private const int MP_CONTEXT_ID    = 0x20;    // uint   context_id
        private const int MP_HOLD_KEY      = 0x24;    // int    hold_key_to_apply
        private const int MP_ACTION_STAMP  = 0x28;    // uint   action_stamp

        // ── MovementParameters defaults (from acclient.exe statics) ──────────
        private const uint   MP_DEFAULT_BITFIELD  = 0x0001EE1F; // 0x10 bit = always autorun speed
        private const float  MP_DEFAULT_DIST_OBJ  = 0.6f;
        private const float  MP_DEFAULT_MIN_DIST  = 0.0f;
        private const float  MP_DEFAULT_FAIL_DIST = float.MaxValue;
        private const float  MP_DEFAULT_WR_THRESH = 0.0f;        // 0 = never walk, always run
        private const float  MP_DEFAULT_SPEED     = 1.0f;

        // ═══════════════════════════════════════════════════════════════════════
        //  NATIVE DELEGATES
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// MovementManager::PerformMovement(const MovementStruct*)
        /// __thiscall — ECX = MovementManager*, stack arg = MovementStruct*
        /// Returns 0 on success, non-zero error code on failure.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate uint DelPerformMovement(IntPtr thisPtr, IntPtr movementStruct);

        /// <summary>
        /// MovementManager::CancelMoveTo(unsigned int err)
        /// __thiscall — ECX = MovementManager*, stack arg = error code
        /// Error 0 = cancelled by user.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void DelCancelMoveTo(IntPtr thisPtr, uint err);

        // ═══════════════════════════════════════════════════════════════════════
        //  CACHED STATE
        // ═══════════════════════════════════════════════════════════════════════

        private static DelPerformMovement _performMovement;
        private static DelCancelMoveTo    _cancelMoveTo;

        private static IntPtr _smartboxStaticAddr;   // Address of SmartBox::smartbox global
        private static IntPtr _positionVtable;       // Position::`vftable' address
        private static IntPtr _movParamsVtable;      // MovementParameters::`vftable' address

        private static bool        _initialized;
        private static Action<string> _log;

        public static bool IsInitialized => _initialized;

        // ═══════════════════════════════════════════════════════════════════════
        //  INITIALIZATION (pattern scan)
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Scans acclient.exe at runtime for MovementManager functions and
        /// struct vtable addresses.  Call once at plugin startup, after
        /// CombatActionHelper and MovementActionHelper have initialized.
        /// 
        /// smartboxAddr: if non-zero, use this as SmartBox::smartbox static
        ///   address instead of pattern scanning (pass UB's SmartBox.smartbox
        ///   pointer address for guaranteed correctness).
        /// </summary>
        public static void Initialize(Action<string> chatLog = null, IntPtr smartboxAddr = default)
        {
            _log = chatLog;

            try
            {
                Process proc = Process.GetCurrentProcess();
                IntPtr baseAddr = IntPtr.Zero;
                int imageSize = 0;

                foreach (ProcessModule mod in proc.Modules)
                {
                    if (mod.ModuleName.Equals("acclient.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        baseAddr = mod.BaseAddress;
                        imageSize = mod.ModuleMemorySize;
                        break;
                    }
                }

                if (baseAddr == IntPtr.Zero)
                {
                    Log("Tier2 scan FAILED: acclient.exe not found");
                    return;
                }

                int textRva  = 0x1000;
                int textSize = Math.Min(imageSize - textRva, 0x400000);
                IntPtr textBase = new IntPtr(baseAddr.ToInt32() + textRva);
                byte[] text = new byte[textSize];
                Marshal.Copy(textBase, text, 0, textSize);
                int textBaseVA = baseAddr.ToInt32() + textRva;

                // ── 1. Find MovementManager::PerformMovement ─────────────────
                // Unique epilogue: pop edi; mov eax, 71; pop esi; ret 4
                // Hex: 5F B8 47 00 00 00 5E C2 04 00
                byte[] pmEpilogue = { 0x5F, 0xB8, 0x47, 0x00, 0x00, 0x00, 0x5E, 0xC2, 0x04, 0x00 };
                int pmEpiOff = -1;

                // PerformMovement is in the MovementManager code region (~0x520000–0x530000)
                // Narrow the search to avoid false positives in unrelated code.
                for (int i = 0; i < text.Length - pmEpilogue.Length; i++)
                {
                    if (!BytesMatch(text, i, pmEpilogue)) continue;

                    // Verify: the function should start with push esi; push edi; mov esi, ecx
                    // (56 57 8B F1) preceded by CC/90/C3 padding.
                    int funcOff = FindFuncStartBefore(text, i,
                        new byte[] { 0x56, 0x57, 0x8B, 0xF1 });
                    if (funcOff >= 0)
                    {
                        // Extra verify: function reads [esi+0x08] (physics_obj) then pushes 1
                        // Pattern at funcOff+4: 8B 4E 08 6A 01
                        if (funcOff + 9 < text.Length &&
                            text[funcOff + 4] == 0x8B &&
                            text[funcOff + 5] == 0x4E &&
                            text[funcOff + 6] == 0x08 &&
                            text[funcOff + 7] == 0x6A &&
                            text[funcOff + 8] == 0x01)
                        {
                            pmEpiOff = funcOff;
                            break;
                        }
                    }
                }

                if (pmEpiOff < 0)
                {
                    Log("Tier2 scan FAILED: PerformMovement not found");
                    return;
                }

                int pmVA = textBaseVA + pmEpiOff;
                Log($"PerformMovement at 0x{pmVA:X8}");
                _performMovement = (DelPerformMovement)Marshal.GetDelegateForFunctionPointer(
                    new IntPtr(pmVA), typeof(DelPerformMovement));

                // ── 2. Find CancelMoveTo ─────────────────────────────────────
                // Immediately after PerformMovement's ret — small function:
                //   8B 49 04   mov ecx, [ecx+4]   (moveto_manager at MM+0x04)
                //   85 C9      test ecx, ecx
                //   74 XX      jz skip
                //   E9 XX      jmp MoveToManager::CancelMoveTo
                //   C2 04 00   ret 4
                // Search forward from the epilogue for this pattern.
                int cancelOff = -1;
                int searchFrom = pmEpiOff + pmEpilogue.Length;
                // Search up to 128 bytes past the epilogue — the switch jump table
                // data sits between PerformMovement's ret and CancelMoveTo's start.
                for (int i = searchFrom; i < Math.Min(searchFrom + 128, text.Length - 8); i++)
                {
                    if (text[i] == 0x8B && text[i + 1] == 0x49 && text[i + 2] == 0x04 &&
                        text[i + 3] == 0x85 && text[i + 4] == 0xC9 && text[i + 5] == 0x74)
                    {
                        cancelOff = i;
                        break;
                    }
                }

                if (cancelOff < 0)
                {
                    Log("Tier2 scan WARNING: CancelMoveTo not found (non-fatal)");
                }
                else
                {
                    int cancelVA = textBaseVA + cancelOff;
                    Log($"CancelMoveTo at 0x{cancelVA:X8}");
                    _cancelMoveTo = (DelCancelMoveTo)Marshal.GetDelegateForFunctionPointer(
                        new IntPtr(cancelVA), typeof(DelCancelMoveTo));
                }

                // ── 3. Find SmartBox::smartbox static address ────────────────
                if (smartboxAddr != IntPtr.Zero)
                {
                    // Caller provided the address (e.g. from UB's AcClient.SmartBox)
                    _smartboxStaticAddr = smartboxAddr;
                    Log($"SmartBox::smartbox at 0x{_smartboxStaticAddr.ToInt32():X8} (provided by caller)");
                }
                else
                {
                    // Fall back to pattern scan
                    _smartboxStaticAddr = FindSmartBoxStatic(text, textBaseVA, baseAddr.ToInt32());
                    if (_smartboxStaticAddr == IntPtr.Zero)
                    {
                        Log("Tier2 scan FAILED: SmartBox::smartbox static not found");
                        return;
                    }
                    Log($"SmartBox::smartbox at 0x{_smartboxStaticAddr.ToInt32():X8} (pattern scan)");
                }

                // ── 4. Find Position vtable ──────────────────────────────────
                // CPhysicsObj movement functions init a stack Position with:
                //   C7 44 24 XX [vtable]      ← .rdata pointer
                //   C7 44 24 XX 00 00 00 00   ← objcell_id = 0
                //   C7 44 24 XX 00 00 80 3F   ← qw = 1.0f
                // Search callers of PerformMovement for this pattern.
                _positionVtable = FindPositionVtable(text, textBaseVA, pmVA);
                if (_positionVtable == IntPtr.Zero)
                {
                    Log("Tier2 scan FAILED: Position vtable not found");
                    return;
                }
                Log($"Position vtable at 0x{_positionVtable.ToInt32():X8}");

                // ── 5. Find MovementParameters vtable ────────────────────────
                // MovementParameters constructor writes vtable then speed = 1.0:
                //   C7 00 [vtable]           ← mov [eax], vtable
                //   ...
                //   C7 40 14 00 00 80 3F     ← mov [eax+0x14], 1.0f (speed)
                _movParamsVtable = FindMovParamsVtable(text, textBaseVA);
                if (_movParamsVtable == IntPtr.Zero)
                {
                    Log("Tier2 scan FAILED: MovementParameters vtable not found");
                    return;
                }
                Log($"MovParams vtable at 0x{_movParamsVtable.ToInt32():X8}");

                _initialized = true;
                Log("Tier2MovementHelper initialized — all addresses found via pattern scan");
                Tier2Log.StartSession();
                Tier2Log.Log($"PerformMovement=0x{pmVA:X8} SmartBox=0x{_smartboxStaticAddr.ToInt32():X8} " +
                             $"PosVT=0x{_positionVtable.ToInt32():X8} MpVT=0x{_movParamsVtable.ToInt32():X8}");
            }
            catch (Exception ex)
            {
                _initialized = false;
                Log("Tier2 init FAILED: " + ex.Message);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  PUBLIC API
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Tells the client's physics engine to walk/run to a world position.
        /// The client handles all interpolation, heading changes, and speed
        /// internally — no frame-by-frame steering required.
        ///
        /// Parameters use AC's internal coordinate system:
        ///   objcell_id — landblock cell (e.g. 0xABCD0012 for outdoor cell)
        ///   x, y, z    — local position within the cell (yards)
        ///   speed      — movement speed multiplier (1.0 = normal)
        ///   distTo     — stop this far from the target (yards, default 0.6)
        ///
        /// Returns 0 on success, non-zero error code on failure.
        /// </summary>
        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        public static uint MoveToPosition(
            uint objcell_id, float x, float y, float z,
            float speed = 1.0f, float distTo = 0.5f, float walkRunThreshold = 15f)
        {
            if (!_initialized || _performMovement == null) return uint.MaxValue;

            LogPointerChain();  // once — logs the full chain to file

            IntPtr mm = GetMovementManager();
            if (mm == IntPtr.Zero)
            {
                Tier2Log.Log("MoveToPosition: MovementManager is NULL — aborting");
                return uint.MaxValue;
            }

            IntPtr msPtr  = IntPtr.Zero;
            IntPtr mpPtr  = IntPtr.Zero;
            try
            {
                msPtr = Marshal.AllocHGlobal(MS_SIZE);
                mpPtr = Marshal.AllocHGlobal(MP_SIZE);
                ZeroMemory(msPtr, MS_SIZE);
                ZeroMemory(mpPtr, MP_SIZE);

                // ── MovementParameters ────────────────────────────────────────
                WriteMovementParams(mpPtr, speed, distTo, 0f, walkRunThreshold);

                // ── MovementStruct ────────────────────────────────────────────
                Marshal.WriteInt32(msPtr + MS_TYPE, (int)TYPE_MOVE_TO_POSITION);
                WritePosition(msPtr + MS_POS, objcell_id, x, y, z, 1f, 0f, 0f, 0f);
                Marshal.WriteIntPtr(msPtr + MS_PARAMS, mpPtr);

                Tier2Log.Log($"MoveToPosition CALL: mm=0x{mm.ToInt32():X8} cell=0x{objcell_id:X8} " +
                             $"x={x:F2} y={y:F2} z={z:F2} speed={speed} distTo={distTo} wrTh={walkRunThreshold}");

                uint result = _performMovement(mm, msPtr);

                Tier2Log.Log($"MoveToPosition RETURNED: {result}");
                return result;
            }
            catch (Exception ex)
            {
                Tier2Log.Log($"MoveToPosition EXCEPTION: {ex}");
                Log("MoveToPosition failed: " + ex.Message);
                return uint.MaxValue;
            }
            finally
            {
                if (msPtr != IntPtr.Zero) Marshal.FreeHGlobal(msPtr);
                if (mpPtr != IntPtr.Zero) Marshal.FreeHGlobal(mpPtr);
            }
        }

        /// <summary>
        /// Tells the client's physics engine to approach a world object by ID.
        /// The client tracks the object's position and adjusts the path.
        /// </summary>
        public static uint MoveToObject(
            uint objectId, float speed = 1.0f, float distTo = 0.5f)
        {
            if (!_initialized || _performMovement == null) return uint.MaxValue;

            IntPtr mm = GetMovementManager();
            if (mm == IntPtr.Zero) return uint.MaxValue;

            IntPtr msPtr = IntPtr.Zero;
            IntPtr mpPtr = IntPtr.Zero;
            try
            {
                msPtr = Marshal.AllocHGlobal(MS_SIZE);
                mpPtr = Marshal.AllocHGlobal(MP_SIZE);
                ZeroMemory(msPtr, MS_SIZE);
                ZeroMemory(mpPtr, MP_SIZE);

                WriteMovementParams(mpPtr, speed, distTo, 0f);

                Marshal.WriteInt32(msPtr + MS_TYPE, (int)TYPE_MOVE_TO_OBJECT);
                Marshal.WriteInt32(msPtr + MS_OBJECT_ID, (int)objectId);
                WriteIdentityPosition(msPtr + MS_POS);
                Marshal.WriteIntPtr(msPtr + MS_PARAMS, mpPtr);

                return _performMovement(mm, msPtr);
            }
            catch (Exception ex)
            {
                Log("MoveToObject failed: " + ex.Message);
                return uint.MaxValue;
            }
            finally
            {
                if (msPtr != IntPtr.Zero) Marshal.FreeHGlobal(msPtr);
                if (mpPtr != IntPtr.Zero) Marshal.FreeHGlobal(mpPtr);
            }
        }

        /// <summary>
        /// Tells the client to turn the player to face a compass heading.
        /// heading: 0 = North, 90 = East, 180 = South, 270 = West (degrees).
        /// </summary>
        public static uint TurnToHeading(float heading, float speed = 1.0f)
        {
            if (!_initialized || _performMovement == null) return uint.MaxValue;

            IntPtr mm = GetMovementManager();
            if (mm == IntPtr.Zero) return uint.MaxValue;

            IntPtr msPtr = IntPtr.Zero;
            IntPtr mpPtr = IntPtr.Zero;
            try
            {
                msPtr = Marshal.AllocHGlobal(MS_SIZE);
                mpPtr = Marshal.AllocHGlobal(MP_SIZE);
                ZeroMemory(msPtr, MS_SIZE);
                ZeroMemory(mpPtr, MP_SIZE);

                // For TurnToHeading, the heading goes in MovementParameters.desired_heading
                WriteMovementParams(mpPtr, speed, 0f, heading);

                Marshal.WriteInt32(msPtr + MS_TYPE, (int)TYPE_TURN_TO_HEADING);
                WriteIdentityPosition(msPtr + MS_POS);
                Marshal.WriteIntPtr(msPtr + MS_PARAMS, mpPtr);

                return _performMovement(mm, msPtr);
            }
            catch (Exception ex)
            {
                Log("TurnToHeading failed: " + ex.Message);
                return uint.MaxValue;
            }
            finally
            {
                if (msPtr != IntPtr.Zero) Marshal.FreeHGlobal(msPtr);
                if (mpPtr != IntPtr.Zero) Marshal.FreeHGlobal(mpPtr);
            }
        }

        /// <summary>
        /// Cancels any in-progress MoveToPosition / MoveToObject / TurnToHeading.
        /// Safe to call if nothing is in progress.
        /// </summary>
        public static void CancelMoveTo()
        {
            if (!_initialized || _cancelMoveTo == null) return;
            try
            {
                IntPtr mm = GetMovementManager();
                if (mm != IntPtr.Zero)
                    _cancelMoveTo(mm, 0);
            }
            catch (Exception ex)
            {
                Log("CancelMoveTo failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Returns true if the player has a MovementManager and its MoveToManager
        /// is non-null (meaning some Tier 2 movement is potentially active).
        /// </summary>
        public static bool IsMovingTo()
        {
            try
            {
                IntPtr mm = GetMovementManager();
                if (mm == IntPtr.Zero) return false;
                // MoveToManager* at MovementManager+0x04
                IntPtr mtm = Marshal.ReadIntPtr(mm + 0x04);
                return mtm != IntPtr.Zero;
            }
            catch { return false; }
        }

        /// <summary>
        /// Reads the player's current internal Position struct.
        /// Uses Marshal.ReadInt32 + BitConverter (safe) instead of unsafe pointers
        /// to avoid SEH crashes that bypass managed try/catch.
        /// </summary>
        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        public static bool GetPlayerPosition(
            out uint objcell_id, out float x, out float y, out float z)
        {
            objcell_id = 0; x = y = z = 0;
            try
            {
                IntPtr player = GetPlayerPtr();
                if (player == IntPtr.Zero) return false;

                IntPtr pos = new IntPtr(player.ToInt32() + PO_POSITION);
                objcell_id = (uint)Marshal.ReadInt32(pos + POS_OBJCELL_ID);
                x = IntToFloat(Marshal.ReadInt32(pos + POS_ORIGIN_X));
                y = IntToFloat(Marshal.ReadInt32(pos + POS_ORIGIN_Y));
                z = IntToFloat(Marshal.ReadInt32(pos + POS_ORIGIN_Z));
                return true;
            }
            catch (Exception ex)
            {
                Tier2Log.Log($"GetPlayerPos EXCEPTION: {ex.Message}");
                return false;
            }
        }

        /// <summary>Reinterpret int32 bits as float (safe alternative to unsafe pointer cast).</summary>
        private static float IntToFloat(int bits)
        {
            byte[] b = BitConverter.GetBytes(bits);
            return BitConverter.ToSingle(b, 0);
        }

        /// <summary>
        /// Returns the player's current landblock cell ID, or 0 if unavailable.
        /// HPCSE attribute required: Marshal.ReadInt32 is unsafe under the hood —
        /// AccessViolationException is a Corrupted State Exception on .NET 4.8
        /// and bypasses normal try/catch without this attribute.
        /// </summary>
        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        public static uint GetPlayerCellId()
        {
            try
            {
                IntPtr sb = Marshal.ReadIntPtr(_smartboxStaticAddr);
                if (sb == IntPtr.Zero) return 0;

                IntPtr player = Marshal.ReadIntPtr(sb + SB_PLAYER);
                if (player == IntPtr.Zero) return 0;

                return (uint)Marshal.ReadInt32(new IntPtr(
                    player.ToInt32() + PO_POSITION + POS_OBJCELL_ID));
            }
            catch (Exception ex)
            {
                Tier2Log.Log($"GetPlayerCellId CRASHED: {ex.GetType().Name}: {ex.Message}");
                return 0;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  POINTER CHAIN HELPERS
        // ═══════════════════════════════════════════════════════════════════════

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private static IntPtr GetSmartBox()
        {
            try
            {
                if (_smartboxStaticAddr == IntPtr.Zero) return IntPtr.Zero;
                return Marshal.ReadIntPtr(_smartboxStaticAddr);
            }
            catch (Exception ex)
            {
                Tier2Log.Log($"GetSmartBox CRASHED: {ex.GetType().Name}");
                return IntPtr.Zero;
            }
        }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private static IntPtr GetPlayerPtr()
        {
            try
            {
                IntPtr sb = GetSmartBox();
                if (sb == IntPtr.Zero) return IntPtr.Zero;
                return Marshal.ReadIntPtr(sb + SB_PLAYER);
            }
            catch (Exception ex)
            {
                Tier2Log.Log($"GetPlayerPtr CRASHED: {ex.GetType().Name}");
                return IntPtr.Zero;
            }
        }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private static IntPtr GetMovementManager()
        {
            try
            {
                IntPtr player = GetPlayerPtr();
                if (player == IntPtr.Zero) return IntPtr.Zero;
                return Marshal.ReadIntPtr(player + PO_MOVEMENT_MGR);
            }
            catch (Exception ex)
            {
                Tier2Log.Log($"GetMovementManager CRASHED: {ex.GetType().Name}");
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Logs the full pointer chain once.  Call before first movement.
        /// </summary>
        private static bool _chainLogged;
        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        public static void LogPointerChain()
        {
            if (_chainLogged) return;
            _chainLogged = true;
            try
            {
                IntPtr sb = Marshal.ReadIntPtr(_smartboxStaticAddr);
                IntPtr player = (sb != IntPtr.Zero) ? Marshal.ReadIntPtr(sb + SB_PLAYER) : IntPtr.Zero;
                IntPtr mm = (player != IntPtr.Zero) ? Marshal.ReadIntPtr(player + PO_MOVEMENT_MGR) : IntPtr.Zero;
                uint cell = 0; float px = 0, py = 0, pz = 0;
                if (player != IntPtr.Zero)
                {
                    IntPtr pos = new IntPtr(player.ToInt32() + PO_POSITION);
                    cell = (uint)Marshal.ReadInt32(pos + POS_OBJCELL_ID);
                    px = IntToFloat(Marshal.ReadInt32(pos + POS_ORIGIN_X));
                    py = IntToFloat(Marshal.ReadInt32(pos + POS_ORIGIN_Y));
                    pz = IntToFloat(Marshal.ReadInt32(pos + POS_ORIGIN_Z));
                }
                Tier2Log.Log($"CHAIN: [0x{_smartboxStaticAddr.ToInt32():X8}]→sb=0x{sb.ToInt32():X8} " +
                             $"[sb+0x{SB_PLAYER:X2}]→player=0x{player.ToInt32():X8} " +
                             $"[player+0x{PO_MOVEMENT_MGR:X2}]→mm=0x{mm.ToInt32():X8}");
                Tier2Log.Log($"CHAIN: playerPos cell=0x{cell:X8} origin=({px:F2},{py:F2},{pz:F2})");
            }
            catch (Exception ex)
            {
                Tier2Log.Log($"CHAIN FAILED: {ex.GetType().Name}: {ex.Message}");
                _chainLogged = false;  // retry next time
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  STRUCT WRITERS
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Fills a 44-byte MovementParameters at the given unmanaged address.
        /// walkRunThreshold: distance in yards at which to switch from run to walk.
        ///   0 = always run. Default AC value is 15.0.
        /// </summary>
        private static void WriteMovementParams(
            IntPtr ptr, float speed, float distTo, float heading, float walkRunThreshold = 15f)
        {
            Marshal.WriteIntPtr(ptr + MP_VTABLE,     _movParamsVtable);
            WriteInt(ptr + MP_BITFIELD,              MP_DEFAULT_BITFIELD);
            WriteFloat(ptr + MP_DIST_TO_OBJ,         distTo);
            WriteFloat(ptr + MP_MIN_DIST,            MP_DEFAULT_MIN_DIST);
            WriteFloat(ptr + MP_HEADING,             heading);
            WriteFloat(ptr + MP_SPEED,               speed);
            WriteFloat(ptr + MP_FAIL_DIST,           MP_DEFAULT_FAIL_DIST);
            WriteFloat(ptr + MP_WR_THRESH,           walkRunThreshold);
            WriteInt(ptr + MP_CONTEXT_ID,            0);
            WriteInt(ptr + MP_HOLD_KEY,              0);  // 0 = let walk_run_threshold decide
            WriteInt(ptr + MP_ACTION_STAMP,          0);
        }

        /// <summary>
        /// Fills a 72-byte Position at the given unmanaged address.
        /// Computes the Frame cache (3×3 rotation matrix from quaternion).
        /// </summary>
        private static void WritePosition(
            IntPtr ptr, uint objcell_id,
            float x, float y, float z,
            float qw, float qx, float qy, float qz)
        {
            Marshal.WriteIntPtr(ptr + POS_VTABLE,  _positionVtable);
            WriteInt(ptr + POS_OBJCELL_ID,         objcell_id);
            WriteFloat(ptr + POS_QW, qw);
            WriteFloat(ptr + POS_QX, qx);
            WriteFloat(ptr + POS_QY, qy);
            WriteFloat(ptr + POS_QZ, qz);
            WriteFloat(ptr + POS_ORIGIN_X, x);
            WriteFloat(ptr + POS_ORIGIN_Y, y);
            WriteFloat(ptr + POS_ORIGIN_Z, z);

            // Frame::cache — quaternion → 3×3 rotation matrix (row-major)
            float xx = qx * qx, yy = qy * qy, zz = qz * qz;
            float xy = qx * qy, xz = qx * qz, yz = qy * qz;
            float wx = qw * qx, wy = qw * qy, wz = qw * qz;

            IntPtr c = ptr + POS_CACHE;
            WriteFloat(c + 0x00, 1f - 2f * (yy + zz));  // m00
            WriteFloat(c + 0x04, 2f * (xy - wz));        // m01
            WriteFloat(c + 0x08, 2f * (xz + wy));        // m02
            WriteFloat(c + 0x0C, 2f * (xy + wz));        // m10
            WriteFloat(c + 0x10, 1f - 2f * (xx + zz));   // m11
            WriteFloat(c + 0x14, 2f * (yz - wx));         // m12
            WriteFloat(c + 0x18, 2f * (xz - wy));        // m20
            WriteFloat(c + 0x1C, 2f * (yz + wx));         // m21
            WriteFloat(c + 0x20, 1f - 2f * (xx + yy));   // m22
        }

        /// <summary>
        /// Writes an identity Position (objcell_id=0, identity quaternion).
        /// </summary>
        private static void WriteIdentityPosition(IntPtr ptr)
        {
            WritePosition(ptr, 0, 0f, 0f, 0f, 1f, 0f, 0f, 0f);
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  PATTERN SCAN HELPERS
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Finds SmartBox::smartbox by looking for the Cleanup pattern:
        ///   A1 [addr] ; load smartbox
        ///   85 C0     ; test eax, eax
        ///   74 XX     ; jz skip
        ///   ...       ; vtable destructor call
        ///   C7 05 [addr] 00 00 00 00  ; store 0 to smartbox
        ///   C3        ; ret
        /// </summary>
        private static IntPtr FindSmartBoxStatic(byte[] text, int textBaseVA, int imageBase)
        {
            int dataLo = imageBase + 0x400000;   // rough .data start
            int dataHi = imageBase + 0x510000;   // rough .data end

            // Search for: C7 05 [addr] 00 00 00 00 C3
            // where [addr] is also loaded with A1 [addr] within 30 bytes before
            for (int i = 0; i < text.Length - 12; i++)
            {
                if (text[i] != 0xC7 || text[i + 1] != 0x05) continue;
                int addr = BitConverter.ToInt32(text, i + 2);
                if (addr < dataLo || addr > dataHi) continue;
                // Check imm32 = 0 and next byte = C3 (ret)
                if (BitConverter.ToInt32(text, i + 6) != 0) continue;
                if (text[i + 10] != 0xC3) continue;

                // Verify: A1 [same addr] appears within 40 bytes before
                byte[] loadPat = new byte[5];
                loadPat[0] = 0xA1;
                BitConverter.GetBytes(addr).CopyTo(loadPat, 1);
                for (int back = 5; back < 40; back++)
                {
                    int j = i - back;
                    if (j < 0) break;
                    if (BytesMatch(text, j, loadPat))
                    {
                        // Also verify: 85 C0 (test eax,eax) appears after the load
                        for (int k = j + 5; k < j + 10 && k < text.Length - 1; k++)
                        {
                            if (text[k] == 0x85 && text[k + 1] == 0xC0)
                            {
                                // Extra verify: the load is near a vtable dtor call
                                // SmartBox::Cleanup does: lea ecx,[eax+4]; mov eax,[ecx];
                                // push 1; call [eax]  — the push 1 is the destructor flag,
                                // distinguishing this from other cleanup patterns.
                                bool hasDtorCall = false;
                                for (int m = k; m < i - 2 && m < text.Length - 3; m++)
                                {
                                    if (text[m] == 0x6A && text[m + 1] == 0x01 &&
                                        text[m + 2] == 0xFF && text[m + 3] == 0x10)
                                    {
                                        hasDtorCall = true;  // push 1; call [eax]
                                        break;
                                    }
                                }
                                if (hasDtorCall)
                                    return new IntPtr(addr);
                            }
                        }
                    }
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Finds Position::`vftable' by scanning PerformMovement callers
        /// for the stack-init pattern that writes a .rdata pointer followed
        /// by objcell_id=0 then qw=1.0f.
        /// </summary>
        private static IntPtr FindPositionVtable(byte[] text, int textBaseVA, int pmVA)
        {
            int rdataLo = textBaseVA + 0x390000;   // .rdata region
            int rdataHi = rdataLo + 0x80000;

            // Find callers of PerformMovement (E8 relative calls)
            for (int i = 0; i < text.Length - 5; i++)
            {
                if (text[i] != 0xE8) continue;
                int rel = BitConverter.ToInt32(text, i + 1);
                int target = textBaseVA + i + 5 + rel;
                if (target != pmVA) continue;

                // Found a caller. Search backwards up to 300 bytes for:
                // C7 44 24 XX [rdata_addr]    (Position vtable)
                // C7 44 24 YY 00 00 00 00     (objcell_id = 0)
                // C7 44 24 ZZ 00 00 80 3F     (qw = 1.0f)
                int searchStart = Math.Max(0, i - 300);
                for (int j = searchStart; j < i - 8; j++)
                {
                    // Look for C7 44 24 XX followed by .rdata address
                    if (text[j] != 0xC7 || text[j + 1] != 0x44 || text[j + 2] != 0x24)
                        continue;
                    int val = BitConverter.ToInt32(text, j + 4);
                    if (val < rdataLo || val > rdataHi) continue;

                    // Check: within 16 bytes, there should be C7 44 24 XX 00 00 80 3F (qw=1.0)
                    for (int k = j + 8; k < Math.Min(j + 40, i); k++)
                    {
                        if (text[k] == 0xC7 && text[k + 1] == 0x44 && text[k + 2] == 0x24 &&
                            text[k + 4] == 0x00 && text[k + 5] == 0x00 &&
                            text[k + 6] == 0x80 && text[k + 7] == 0x3F)
                        {
                            return new IntPtr(val);
                        }
                    }
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Finds MovementParameters::`vftable' by searching for the constructor
        /// that writes a .rdata vtable to [eax+0] then speed=1.0 to [eax+0x14]:
        ///   C7 00 [vtable]            ← mov [eax], vtable
        ///   ... (field inits) ...
        ///   C7 40 14 00 00 80 3F      ← mov [eax+0x14], 1.0f
        /// </summary>
        private static IntPtr FindMovParamsVtable(byte[] text, int textBaseVA)
        {
            int rdataLo = textBaseVA + 0x390000;
            int rdataHi = rdataLo + 0x80000;

            // Search for: C7 40 14 00 00 80 3F  (speed = 1.0f at +0x14)
            byte[] speedPat = { 0xC7, 0x40, 0x14, 0x00, 0x00, 0x80, 0x3F };
            for (int i = 0; i < text.Length - speedPat.Length; i++)
            {
                if (!BytesMatch(text, i, speedPat)) continue;

                // Walk backwards up to 40 bytes for C7 00 [rdata_addr]
                for (int back = 4; back < 40; back++)
                {
                    int j = i - back;
                    if (j < 0) break;
                    if (text[j] == 0xC7 && text[j + 1] == 0x00)
                    {
                        int val = BitConverter.ToInt32(text, j + 2);
                        if (val >= rdataLo && val <= rdataHi)
                            return new IntPtr(val);
                    }
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Walks backwards from an offset to find a function start.
        /// The function must begin with the given prologue bytes, preceded
        /// by CC/90/C3 alignment padding.
        /// </summary>
        private static int FindFuncStartBefore(byte[] data, int fromOffset, byte[] prologue)
        {
            for (int back = 1; back < 400; back++)
            {
                int pos = fromOffset - back;
                if (pos < 1 || pos + prologue.Length > data.Length) continue;

                if (!BytesMatch(data, pos, prologue)) continue;

                byte prev = data[pos - 1];
                if (prev == 0xCC || prev == 0x90 || prev == 0xC3)
                    return pos;
            }
            return -1;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  LOW-LEVEL HELPERS
        // ═══════════════════════════════════════════════════════════════════════

        private static bool BytesMatch(byte[] data, int offset, byte[] pattern)
        {
            if (offset + pattern.Length > data.Length) return false;
            for (int i = 0; i < pattern.Length; i++)
                if (data[offset + i] != pattern[i]) return false;
            return true;
        }

        private static unsafe void WriteFloat(IntPtr addr, float value)
        {
            *(float*)addr.ToPointer() = value;
        }

        private static unsafe float ReadFloat(IntPtr addr)
        {
            return *(float*)addr.ToPointer();
        }

        private static void WriteInt(IntPtr addr, uint value)
        {
            Marshal.WriteInt32(addr, (int)value);
        }

        private static void ZeroMemory(IntPtr addr, int size)
        {
            byte[] zeros = new byte[size];
            Marshal.Copy(zeros, 0, addr, size);
        }

        private static void Log(string msg)
        {
            try { _log?.Invoke($"[RynthAi] {msg}"); } catch { }
        }
    }
}

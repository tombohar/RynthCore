using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NexSuite.Plugins.RynthAi
{
    /// <summary>
    /// Direct calls to acclient.exe CM_Movement functions.
    /// Derived entirely from Turbine's acclient.exe decompilation.
    ///
    /// Uses the same runtime pattern-scan approach as CombatActionHelper.
    ///
    /// ═══ TIER 1: CM_Movement server events (Proto_UI::SendToWeenie) ═══
    /// These send ordered game events to the server. They are __cdecl statics.
    ///
    ///   Event_DoMovementCommand(motion, speed, hold_key)   — opcode 0xF61E
    ///   Event_StopMovementCommand(motion, hold_key)        — opcode 0xF661
    ///   Event_Jump_NonAutonomous(extent)                   — opcode 0xF7C9
    ///   Event_AutonomyLevel(level)                         — opcode 0xF752
    ///
    /// All share the CM prologue: 83 EC 0C 53 56 57 E8
    /// Anchored by Event_StopMovementCommand (opcode 0xF661) which has a
    /// unique-enough value in the CM_Movement cluster.
    ///
    /// ═══ TIER 2: CPhysicsObj::MoveToPosition (planned) ═══
    /// Client-side pathfinding via the player's physics object.
    /// Requires tracing SmartBox::smartbox → player → MovementManager.
    /// Would let the client handle all interpolation, heading, speed natively.
    ///
    /// ═══ TIER 3: MoveToManager queue (planned) ═══  
    /// Direct movement node queue manipulation for maximum control.
    ///
    /// Motion constants (from acclient.exe motion tables):
    ///   0x45000005 = MoveForward (WalkForward)
    ///   0x45000006 = MoveBackward (WalkBackward)
    ///   0x6500000D = TurnRight
    ///   0x6500000E = TurnLeft
    ///   0x41000003 = RunForward
    ///
    /// HoldKey values:
    ///   0 = None
    ///   1 = Run (hold shift)
    ///   2 = AutoRun
    /// </summary>
    internal static class MovementActionHelper
    {
        // ── Motion constants ────────────────────────────────────────────────
        public const uint MOTION_WALK_FORWARD  = 0x45000005;
        public const uint MOTION_WALK_BACKWARD = 0x45000006;
        public const uint MOTION_RUN_FORWARD   = 0x41000003;
        public const uint MOTION_TURN_RIGHT    = 0x6500000D;
        public const uint MOTION_TURN_LEFT     = 0x6500000E;
        public const uint MOTION_SIDESTEP_RIGHT = 0x6500000F;
        public const uint MOTION_SIDESTEP_LEFT  = 0x65000010;

        // ── HoldKey values ──────────────────────────────────────────────────
        public const int HOLDKEY_NONE    = 0;
        public const int HOLDKEY_RUN     = 1;
        public const int HOLDKEY_AUTORUN = 2;

        // ── Delegate types ──────────────────────────────────────────────────
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool DelDoMovementCommand(uint motion, float speed, int holdKey);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool DelStopMovementCommand(uint motion, int holdKey);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool DelJumpNonAutonomous(float extent);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool DelAutonomyLevel(uint level);

        // ── Cached delegates ────────────────────────────────────────────────
        private static DelDoMovementCommand _doMovement;
        private static DelStopMovementCommand _stopMovement;
        private static DelJumpNonAutonomous _jumpNonAutonomous;
        private static DelAutonomyLevel _autonomyLevel;

        private static bool _initialized;
        private static Action<string> _log;

        // ── Shared prologue for CM_Movement functions ───────────────────────
        private static readonly byte[] CM_PROLOGUE = { 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8 };

        /// <summary>
        /// Pattern-scans acclient.exe for CM_Movement functions at runtime.
        /// Call once at plugin startup (after CombatActionHelper.Initialize).
        /// </summary>
        public static void Initialize(Action<string> chatLog = null)
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
                    Log("Movement scan FAILED: acclient.exe not found");
                    return;
                }

                int textRva = 0x1000;
                int textSize = Math.Min(imageSize - textRva, 0x400000);
                IntPtr textBase = new IntPtr(baseAddr.ToInt32() + textRva);
                byte[] text = new byte[textSize];
                Marshal.Copy(textBase, text, 0, textSize);
                int textBaseVA = baseAddr.ToInt32() + textRva;

                // ── Anchor: StopMovementCommand via opcode 0xF661 ──────────
                // This is unique enough in the CM_Movement region.
                byte[] stopOpcode = { 0xC7, 0x02, 0x61, 0xF6, 0x00, 0x00 };
                int stopOpcodeOff = FindPattern(text, stopOpcode);
                if (stopOpcodeOff < 0)
                {
                    Log("Movement scan FAILED: StopMovementCommand opcode 0xF661 not found");
                    return;
                }

                int stopFuncOff = FindPrologueBefore(text, stopOpcodeOff);
                if (stopFuncOff < 0)
                {
                    Log("Movement scan FAILED: StopMovementCommand prologue not found");
                    return;
                }

                int stopVA = textBaseVA + stopFuncOff;
                Log($"StopMovementCommand at 0x{stopVA:X8}");

                // ── Search near StopMovementCommand for the others ─────────
                // CM_Movement functions cluster within ~0x700 bytes
                int regionStart = Math.Max(0, stopOpcodeOff - 0x700);
                int regionEnd = Math.Min(text.Length, stopOpcodeOff + 0x300);

                // DoMovementCommand: opcode 0xF61E (63006 decimal)
                byte[] doOpcode = { 0xC7, 0x02, 0x1E, 0xF6, 0x00, 0x00 };
                int doOpcodeOff = FindPatternInRegion(text, doOpcode, regionStart, regionEnd);
                int doFuncOff = doOpcodeOff >= 0 ? FindPrologueBefore(text, doOpcodeOff) : -1;

                // Jump_NonAutonomous: opcode 0xF7C9 (63433 decimal)
                byte[] jumpNAOpcode = { 0xC7, 0x02, 0xC9, 0xF7, 0x00, 0x00 };
                int jumpNAOff = FindPatternInRegion(text, jumpNAOpcode, regionStart, regionEnd);
                int jumpNAFuncOff = jumpNAOff >= 0 ? FindPrologueBefore(text, jumpNAOff) : -1;

                // AutonomyLevel: opcode 0xF752 (63314 decimal)
                byte[] autoLevelOpcode = { 0xC7, 0x02, 0x52, 0xF7, 0x00, 0x00 };
                int autoLevelOff = FindPatternInRegion(text, autoLevelOpcode, regionStart, regionEnd);
                int autoLevelFuncOff = autoLevelOff >= 0 ? FindPrologueBefore(text, autoLevelOff) : -1;

                // ── Verify and create delegates ────────────────────────────
                if (doFuncOff < 0)
                {
                    Log("Movement scan INCOMPLETE: DoMovementCommand not found");
                    return;
                }

                int doVA = textBaseVA + doFuncOff;
                Log($"DoMovementCommand at 0x{doVA:X8}");

                _stopMovement = (DelStopMovementCommand)Marshal.GetDelegateForFunctionPointer(
                    new IntPtr(stopVA), typeof(DelStopMovementCommand));
                _doMovement = (DelDoMovementCommand)Marshal.GetDelegateForFunctionPointer(
                    new IntPtr(doVA), typeof(DelDoMovementCommand));

                if (jumpNAFuncOff >= 0)
                {
                    int jumpNAVA = textBaseVA + jumpNAFuncOff;
                    _jumpNonAutonomous = (DelJumpNonAutonomous)Marshal.GetDelegateForFunctionPointer(
                        new IntPtr(jumpNAVA), typeof(DelJumpNonAutonomous));
                    Log($"Jump_NonAutonomous at 0x{jumpNAVA:X8}");
                }

                if (autoLevelFuncOff >= 0)
                {
                    int autoLevelVA = textBaseVA + autoLevelFuncOff;
                    _autonomyLevel = (DelAutonomyLevel)Marshal.GetDelegateForFunctionPointer(
                        new IntPtr(autoLevelVA), typeof(DelAutonomyLevel));
                    Log($"AutonomyLevel at 0x{autoLevelVA:X8}");
                }

                _initialized = true;
                Log("MovementActionHelper initialized — CM_Movement functions found via pattern scan");
            }
            catch (Exception ex)
            {
                _initialized = false;
                Log("MovementActionHelper init FAILED: " + ex.Message);
            }
        }

        public static bool IsInitialized => _initialized;

        // ══════════════════════════════════════════════════════════════════════
        //  TIER 1 PUBLIC API — Server-side movement events
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Sends a movement command to the server.
        /// Replaces keyboard-driven movement for server authorization.
        ///   motion: one of MOTION_* constants
        ///   speed: 1.0 = normal, can scale
        ///   holdKey: HOLDKEY_NONE, HOLDKEY_RUN, or HOLDKEY_AUTORUN
        /// </summary>
        public static bool DoMovement(uint motion, float speed = 1.0f, int holdKey = HOLDKEY_RUN)
        {
            if (!_initialized || _doMovement == null) return false;
            try { return _doMovement(motion, speed, holdKey); }
            catch (Exception ex) { Log("DoMovement failed: " + ex.Message); return false; }
        }

        /// <summary>
        /// Stops a specific movement command on the server.
        ///   motion: the motion to stop (same constant used in DoMovement)
        ///   holdKey: must match the holdKey used in the corresponding DoMovement
        /// </summary>
        public static bool StopMovement(uint motion, int holdKey = HOLDKEY_RUN)
        {
            if (!_initialized || _stopMovement == null) return false;
            try { return _stopMovement(motion, holdKey); }
            catch (Exception ex) { Log("StopMovement failed: " + ex.Message); return false; }
        }

        /// <summary>
        /// Non-autonomous jump with specified extent (0.0–1.0).
        /// Replaces keyboard-simulated jump in RynthJumper.
        /// </summary>
        public static bool JumpNonAutonomous(float extent)
        {
            if (!_initialized || _jumpNonAutonomous == null) return false;
            try { return _jumpNonAutonomous(Math.Max(0f, Math.Min(1f, extent))); }
            catch (Exception ex) { Log("JumpNA failed: " + ex.Message); return false; }
        }

        /// <summary>
        /// Sets the autonomy level for the client.
        /// 0 = server controls, 1 = client controls (normal play), 2 = full autonomy.
        /// </summary>
        public static bool SetAutonomyLevel(uint level)
        {
            if (!_initialized || _autonomyLevel == null) return false;
            try { return _autonomyLevel(level); }
            catch (Exception ex) { Log("AutonomyLevel failed: " + ex.Message); return false; }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  PATTERN SCANNER (shared with CombatActionHelper design)
        // ══════════════════════════════════════════════════════════════════════

        private static int FindPattern(byte[] data, byte[] pattern)
        {
            return FindPatternInRegion(data, pattern, 0, data.Length);
        }

        private static int FindPatternInRegion(byte[] data, byte[] pattern, int start, int end)
        {
            int limit = Math.Min(end, data.Length) - pattern.Length;
            for (int i = Math.Max(0, start); i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private static int FindPrologueBefore(byte[] data, int opcodeOffset)
        {
            for (int back = 1; back < 300; back++)
            {
                int pos = opcodeOffset - back;
                if (pos < 1 || pos + CM_PROLOGUE.Length > data.Length) continue;

                bool match = true;
                for (int j = 0; j < CM_PROLOGUE.Length; j++)
                {
                    if (data[pos + j] != CM_PROLOGUE[j]) { match = false; break; }
                }

                if (match)
                {
                    byte prev = data[pos - 1];
                    if (prev == 0xCC || prev == 0x90 || prev == 0xC3)
                        return pos;
                }
            }
            return -1;
        }

        private static void Log(string msg)
        {
            try { _log?.Invoke($"[RynthAi] {msg}"); } catch { }
        }
    }
}

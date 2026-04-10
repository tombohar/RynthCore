using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NexSuite.Plugins.RynthAi
{
    /// <summary>
    /// Direct calls to acclient.exe CM_Combat functions for attack actions.
    /// Derived entirely from Turbine's acclient.exe decompilation — no VTank code involved.
    ///
    /// Uses runtime pattern scanning to find function addresses in any client build.
    /// Each CM_Combat::Event_* function writes a unique opcode DWORD into a packed buffer
    /// before calling Proto_UI::SendToWeenie. We search acclient.exe's .text section for
    /// these opcode write patterns, then walk backwards to find the function prologue.
    ///
    /// Source: acclient.exe CM_Combat namespace (IDA decompilation of Turbine's client)
    ///   Event_TargetedMeleeAttack   — opcode 0x08, 3 args (targetID, height, power)
    ///   Event_TargetedMissileAttack — opcode 0x0A, 3 args (targetID, height, accuracy)
    ///   Event_ChangeCombatMode      — opcode 0x53, 1 arg  (combatMode)
    ///   Event_CancelAttack          — opcode 0x1B7, 0 args
    ///   Event_QueryHealth           — opcode 0x1BF, 1 arg  (targetID)
    ///
    /// All functions are __cdecl with standard prologue: sub esp,0Ch; push ebx; push esi; push edi
    /// Signature bytes: 83 EC 0C 53 56 57 E8
    /// </summary>
    internal static class CombatActionHelper
    {
        // ── Delegate types matching __cdecl signatures ───────────────────────
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool DelTargetedMeleeAttack(uint targetID, int attackHeight, float powerLevel);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool DelTargetedMissileAttack(uint targetID, int attackHeight, float accuracyLevel);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool DelChangeCombatMode(int combatMode);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool DelCancelAttack();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool DelQueryHealth(uint targetID);

        // ── Cached delegates (created once at init) ─────────────────────────
        private static DelTargetedMeleeAttack _meleeAttack;
        private static DelTargetedMissileAttack _missileAttack;
        private static DelChangeCombatMode _changeCombatMode;
        private static DelCancelAttack _cancelAttack;
        private static DelQueryHealth _queryHealth;

        private static bool _initialized;
        private static Action<string> _log;

        // ── ATTACK_HEIGHT enum values (from acclient.exe) ───────────────────
        public const int ATTACK_HEIGHT_HIGH   = 1;
        public const int ATTACK_HEIGHT_MEDIUM = 2;
        public const int ATTACK_HEIGHT_LOW    = 3;

        // ── COMBAT_MODE enum values (from acclient.exe) ─────────────────────
        public const int COMBAT_MODE_NONCOMBAT = 1;
        public const int COMBAT_MODE_MELEE     = 2;
        public const int COMBAT_MODE_MISSILE   = 4;
        public const int COMBAT_MODE_MAGIC     = 8;

        // ── Shared function prologue for all CM_Combat::Event_* functions ───
        // sub esp, 0Ch; push ebx; push esi; push edi; call OrderHdr_ctor
        private static readonly byte[] CM_COMBAT_PROLOGUE = { 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8 };

        /// <summary>
        /// Scans acclient.exe's loaded image at runtime to find CM_Combat functions.
        /// Call once at plugin startup.
        /// </summary>
        public static void Initialize(Action<string> chatLog = null)
        {
            _log = chatLog;

            try
            {
                // Find acclient.exe's base address in memory
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
                    Log("Pattern scan FAILED: acclient.exe module not found");
                    return;
                }

                Log($"acclient.exe base: 0x{baseAddr.ToInt32():X8}, size: 0x{imageSize:X}");

                // Read the .text section into managed memory for scanning
                // .text typically starts at offset 0x1000 from base
                int textRva = 0x1000;
                int textSize = imageSize - textRva;
                if (textSize <= 0 || textSize > 0x500000)
                {
                    Log($"Text section size suspect: 0x{textSize:X}, clamping");
                    textSize = Math.Min(imageSize - textRva, 0x400000);
                }

                IntPtr textBase = new IntPtr(baseAddr.ToInt32() + textRva);
                byte[] text = new byte[textSize];
                Marshal.Copy(textBase, text, 0, textSize);

                int textBaseVA = baseAddr.ToInt32() + textRva;

                // ── Step 1: Find CancelAttack via unique opcode 0x1B7 ──────
                // Pattern: C7 02 B7 01 00 00 (mov dword ptr [edx], 1B7h)
                // This opcode value is unique in the entire binary.
                byte[] cancelOpcode = { 0xC7, 0x02, 0xB7, 0x01, 0x00, 0x00 };
                int cancelOpcodeOff = FindPattern(text, cancelOpcode);
                if (cancelOpcodeOff < 0)
                {
                    Log("Pattern scan FAILED: CancelAttack opcode 0x1B7 not found");
                    return;
                }

                int cancelFuncOff = FindPrologueBefore(text, cancelOpcodeOff);
                if (cancelFuncOff < 0)
                {
                    Log("Pattern scan FAILED: CancelAttack prologue not found");
                    return;
                }

                int cancelVA = textBaseVA + cancelFuncOff;
                Log($"CancelAttack found at 0x{cancelVA:X8}");

                // ── Step 2: Search near CancelAttack for the others ────────
                // All CM_Combat::Event_* functions cluster within ~0x500 bytes
                int regionStart = Math.Max(0, cancelOpcodeOff - 0x500);
                int regionEnd = Math.Min(text.Length, cancelOpcodeOff + 0x500);

                // ChangeCombatMode: opcode 0x53 (unique in CM_Combat region)
                byte[] changeModeOpcode = { 0xC7, 0x02, 0x53, 0x00, 0x00, 0x00 };
                int changeModeOff = FindPatternInRegion(text, changeModeOpcode, regionStart, regionEnd);
                int changeModeFuncOff = changeModeOff >= 0 ? FindPrologueBefore(text, changeModeOff) : -1;

                // QueryHealth: opcode 0x1BF (unique in entire binary)
                byte[] queryHealthOpcode = { 0xC7, 0x02, 0xBF, 0x01, 0x00, 0x00 };
                int queryHealthOff = FindPatternInRegion(text, queryHealthOpcode, regionStart, regionEnd);
                int queryHealthFuncOff = queryHealthOff >= 0 ? FindPrologueBefore(text, queryHealthOff) : -1;

                // MeleeAttack: opcode 0x08 (common globally, but unique in CM_Combat region)
                byte[] meleeOpcode = { 0xC7, 0x02, 0x08, 0x00, 0x00, 0x00 };
                int meleeOff = FindPatternInRegion(text, meleeOpcode, regionStart, regionEnd);
                int meleeFuncOff = meleeOff >= 0 ? FindPrologueBefore(text, meleeOff) : -1;

                // MissileAttack: opcode 0x0A (search in CM_Combat region only)
                byte[] missileOpcode = { 0xC7, 0x02, 0x0A, 0x00, 0x00, 0x00 };
                int missileOff = FindPatternInRegion(text, missileOpcode, regionStart, regionEnd);
                int missileFuncOff = missileOff >= 0 ? FindPrologueBefore(text, missileOff) : -1;

                // ── Step 3: Verify all found ───────────────────────────────
                if (changeModeFuncOff < 0 || queryHealthFuncOff < 0 ||
                    meleeFuncOff < 0 || missileFuncOff < 0)
                {
                    Log($"Pattern scan INCOMPLETE: CancelAttack=OK, " +
                        $"ChangeCombatMode={changeModeFuncOff >= 0}, " +
                        $"QueryHealth={queryHealthFuncOff >= 0}, " +
                        $"MeleeAttack={meleeFuncOff >= 0}, " +
                        $"MissileAttack={missileFuncOff >= 0}");
                    return;
                }

                int changeModeVA  = textBaseVA + changeModeFuncOff;
                int queryHealthVA = textBaseVA + queryHealthFuncOff;
                int meleeVA       = textBaseVA + meleeFuncOff;
                int missileVA     = textBaseVA + missileFuncOff;

                Log($"ChangeCombatMode at 0x{changeModeVA:X8}");
                Log($"QueryHealth at 0x{queryHealthVA:X8}");
                Log($"MeleeAttack at 0x{meleeVA:X8}");
                Log($"MissileAttack at 0x{missileVA:X8}");

                // Verify all have the expected prologue bytes
                if (!VerifyPrologue(text, cancelFuncOff) ||
                    !VerifyPrologue(text, changeModeFuncOff) ||
                    !VerifyPrologue(text, queryHealthFuncOff) ||
                    !VerifyPrologue(text, meleeFuncOff) ||
                    !VerifyPrologue(text, missileFuncOff))
                {
                    Log("Pattern scan FAILED: prologue verification failed");
                    return;
                }

                // ── Step 4: Create delegates ───────────────────────────────
                _cancelAttack = (DelCancelAttack)Marshal.GetDelegateForFunctionPointer(
                    new IntPtr(cancelVA), typeof(DelCancelAttack));
                _changeCombatMode = (DelChangeCombatMode)Marshal.GetDelegateForFunctionPointer(
                    new IntPtr(changeModeVA), typeof(DelChangeCombatMode));
                _queryHealth = (DelQueryHealth)Marshal.GetDelegateForFunctionPointer(
                    new IntPtr(queryHealthVA), typeof(DelQueryHealth));
                _meleeAttack = (DelTargetedMeleeAttack)Marshal.GetDelegateForFunctionPointer(
                    new IntPtr(meleeVA), typeof(DelTargetedMeleeAttack));
                _missileAttack = (DelTargetedMissileAttack)Marshal.GetDelegateForFunctionPointer(
                    new IntPtr(missileVA), typeof(DelTargetedMissileAttack));

                _initialized = true;
                Log("CombatActionHelper initialized — all 5 CM_Combat functions found via pattern scan");
            }
            catch (Exception ex)
            {
                _initialized = false;
                Log("CombatActionHelper init FAILED: " + ex.Message);
            }
        }

        public static bool IsInitialized => _initialized;

        // ══════════════════════════════════════════════════════════════════════
        //  PUBLIC API
        // ══════════════════════════════════════════════════════════════════════

        public static bool MeleeAttack(uint targetId, int attackHeight, float powerLevel)
        {
            if (!_initialized) return false;
            try
            {
                return _meleeAttack(targetId, attackHeight, Math.Max(0f, Math.Min(1f, powerLevel)));
            }
            catch (Exception ex) { Log("MeleeAttack failed: " + ex.Message); return false; }
        }

        public static bool MissileAttack(uint targetId, int attackHeight, float accuracyLevel)
        {
            if (!_initialized) return false;
            try
            {
                return _missileAttack(targetId, attackHeight, Math.Max(0f, Math.Min(1f, accuracyLevel)));
            }
            catch (Exception ex) { Log("MissileAttack failed: " + ex.Message); return false; }
        }

        public static bool ChangeCombatMode(int combatMode)
        {
            if (!_initialized) return false;
            try { return _changeCombatMode(combatMode); }
            catch (Exception ex) { Log("ChangeCombatMode failed: " + ex.Message); return false; }
        }

        public static bool CancelAttack()
        {
            if (!_initialized) return false;
            try { return _cancelAttack(); }
            catch (Exception ex) { Log("CancelAttack failed: " + ex.Message); return false; }
        }

        public static bool QueryHealth(uint targetId)
        {
            if (!_initialized) return false;
            try { return _queryHealth(targetId); }
            catch (Exception ex) { Log("QueryHealth failed: " + ex.Message); return false; }
        }

        /// <summary>
        /// Maps UISettings height (0=Low, 1=Medium, 2=High) to AC ATTACK_HEIGHT enum.
        /// </summary>
        public static int MapAttackHeight(int uiHeight)
        {
            switch (uiHeight)
            {
                case 0:  return ATTACK_HEIGHT_LOW;
                case 2:  return ATTACK_HEIGHT_HIGH;
                default: return ATTACK_HEIGHT_MEDIUM;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  PATTERN SCANNER
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
                    if (data[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        /// <summary>
        /// Walks backwards from an opcode write to find the CM_Combat function prologue.
        /// Must be preceded by CC (int3), 90 (nop), or C3 (ret).
        /// </summary>
        private static int FindPrologueBefore(byte[] data, int opcodeOffset)
        {
            for (int back = 1; back < 300; back++)
            {
                int pos = opcodeOffset - back;
                if (pos < 1 || pos + CM_COMBAT_PROLOGUE.Length > data.Length)
                    continue;

                bool match = true;
                for (int j = 0; j < CM_COMBAT_PROLOGUE.Length; j++)
                {
                    if (data[pos + j] != CM_COMBAT_PROLOGUE[j])
                    {
                        match = false;
                        break;
                    }
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

        private static bool VerifyPrologue(byte[] data, int offset)
        {
            if (offset < 0 || offset + CM_COMBAT_PROLOGUE.Length > data.Length)
                return false;
            for (int i = 0; i < CM_COMBAT_PROLOGUE.Length; i++)
            {
                if (data[offset + i] != CM_COMBAT_PROLOGUE[i])
                    return false;
            }
            return true;
        }

        private static void Log(string msg)
        {
            try { _log?.Invoke($"[RynthAi] {msg}"); } catch { }
        }
    }
}

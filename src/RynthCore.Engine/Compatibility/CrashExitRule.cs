// ============================================================================
//  RynthCore.Engine - Compatibility/CrashExitRule.cs
//
//  Is a process exit code a CRASH exit? ProcessExitHooks passes a crash exit straight to
//  the original (no managed work on a dying process) after flushing the native crash
//  logger's recent first-chance records.
//
//  A crash exit is an error-severity status or exception code (both top bits set,
//  0xC0000000 and up): 0xC0000005 an access violation, 0xC0000409 a stack buffer overrun,
//  0xC0000374 heap corruption, and the software exception codes a runtime exits with on an
//  unhandled exception, 0xE0434352 (CLR) and 0xE06D7363 (C++).
//  EXCEPT 0xFFFFFFFF: ExitProcess(-1) is an ordinary "exit with failure", not a fault. The
//  old rule (code >= 0xC0000000) took it for a crash and skipped the orderly shutdown
//  of the process.
//
//  Pure. Compiled into tools\EngineRobustnessTests.
// ============================================================================

namespace RynthCore.Engine.Compatibility;

internal static class CrashExitRule
{
    public static bool IsCrashExit(uint code) => (code & 0xC0000000u) == 0xC0000000u && code != 0xFFFFFFFFu;
}

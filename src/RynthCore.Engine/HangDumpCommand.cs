// ============================================================================
//  RynthCore.Engine - HangDumpCommand.cs
//  The hang watchdog's dump, written from outside the client (CrashDump.
//  WriteHangDumpOutOfProcess): the 32-bit rundll32 runs RynthCore.SehTrampoline.dll's
//  RC_DumpProcess (native\SehTrampoline\SehTrampoline.c, DumpArgs.h). Its command line
//  and what its exit code means. Pure: tools\EngineRobustnessTests checks both.
// ============================================================================

using System.Globalization;

namespace RynthCore.Engine;

internal static class HangDumpCommand
{
    /// <summary>The rundll32 entry, by its plain name (rundll32 tries RC_DumpProcessW first).</summary>
    public const string Entry = "RC_DumpProcess";

    /// <summary>
    /// rundll32's arguments: <c>"&lt;helper dll&gt;",RC_DumpProcess &lt;pid&gt; &lt;dump path&gt;</c>. The
    /// path is the rest of the line, unquoted (RC_DumpProcess takes it as it is, spaces included).
    /// </summary>
    public static string Arguments(string helperDll, string dumpPath, int pid) =>
        $"\"{helperDll}\",{Entry} {pid.ToString(CultureInfo.InvariantCulture)} {dumpPath}";

    /// <summary>What RC_DumpProcess's exit code means.</summary>
    public static string DescribeExit(int code) => code switch
    {
        0 => "written",
        1 => "bad arguments",
        2 => "could not open the client process",
        3 => "could not create the dump file",
        4 => "no dbghelp MiniDumpWriteDump",
        5 => "MiniDumpWriteDump failed",
        _ => "rundll32 could not run the helper",
    };
}

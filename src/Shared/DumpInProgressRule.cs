// ============================================================================
//  Shared - DumpInProgressRule.cs (compiled into the engine and the launcher)
//
//  The engine's OUT OF MEMORY full-memory dump (CrashDump) suspends every thread in the
//  client while dbghelp writes it, for minutes on a 3 GB process. The log goes silent
//  meanwhile, which the launcher's wedge watch (AutoRestartWedgedClients) reads as a dead
//  engine after 120 s and kills the client: mid-dump, leaving a truncated file. The engine
//  writes MarkerStarted before the dump and MarkerEnded after it; the launcher holds off
//  its kill while a dump is in progress by either sign:
//    - the log tail's last MarkerStarted has no MarkerEnded after it, and the log was
//      written less than MaxMarkerAge ago (a dump never takes that long; a client that
//      died mid-dump is killed after it); or
//    - the client's acclient_oom_<pid>_*.dmp file is still being written (modified within
//      FileQuietMs).
//  The launcher's restart after the dump, for the OOM box that follows, is unchanged.
//  A hold never lasts more than MaxHold per client (Decide, from the launcher's first hold
//  of that pid): a log kept fresh by some other writer while a marker stays open must not
//  shield a dead client forever. Past the cap the kills resume.
//
//  Pure. Compiled into tools\EngineRobustnessTests.
// ============================================================================

using System;

namespace RynthCore.Install;

internal static class DumpInProgressRule
{
    public const string MarkerStarted = "CrashDump: FULL-MEMORY DUMP STARTED";
    public const string MarkerEnded = "CrashDump: FULL-MEMORY DUMP ENDED";
    public const string OomDumpPrefix = "acclient_oom_";

    public static readonly TimeSpan MaxMarkerAge = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan FileQuietMs = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(15);

    public enum Hold
    {
        /// <summary>No dump in progress: the kill checks run.</summary>
        None,
        /// <summary>A dump is in progress: hold off the kills.</summary>
        Holding,
        /// <summary>Signs of a dump, but held for MaxHold already: the kill checks run.</summary>
        Expired,
    }

    /// <summary>
    /// <see cref="IsDumping"/>, capped: <paramref name="holdSinceUtc"/> is when the launcher first
    /// held this client (null = not held yet). The launcher keeps that time while the result is
    /// Holding or Expired and forgets it on None, so an expired hold does not start again.
    /// </summary>
    public static Hold Decide(string? logTail, DateTime logLastWriteUtc, DateTime? newestOomDumpWriteUtc,
                              DateTime? holdSinceUtc, DateTime nowUtc)
    {
        if (!IsDumping(logTail, logLastWriteUtc, newestOomDumpWriteUtc, nowUtc)) return Hold.None;
        if (holdSinceUtc is DateTime since && nowUtc - since >= MaxHold) return Hold.Expired;
        return Hold.Holding;
    }

    /// <summary>
    /// True while the launcher must not kill the client for a silent log.
    /// <paramref name="logTail"/>: the end of RynthCore.&lt;pid&gt;.log;
    /// <paramref name="logLastWriteUtc"/>: its last write; <paramref name="newestOomDumpWriteUtc"/>:
    /// the last write of the newest acclient_oom_&lt;pid&gt;_*.dmp, or null.
    /// </summary>
    public static bool IsDumping(string? logTail, DateTime logLastWriteUtc, DateTime? newestOomDumpWriteUtc, DateTime nowUtc)
    {
        if (newestOomDumpWriteUtc is DateTime dumpWrite && nowUtc - dumpWrite < FileQuietMs)
            return true;
        if (string.IsNullOrEmpty(logTail)) return false;
        int started = logTail.LastIndexOf(MarkerStarted, StringComparison.Ordinal);
        if (started < 0) return false;
        if (logTail.IndexOf(MarkerEnded, started, StringComparison.Ordinal) >= 0) return false;
        return nowUtc - logLastWriteUtc < MaxMarkerAge;
    }
}

using System;

namespace RynthCore.Engine;

/// <summary>
/// Whether a full-memory dump may be written now. Pure arithmetic (the disk and folder are read
/// by <see cref="CrashDump"/>), so tools\EngineRobustnessTests runs it directly.
///
/// A full dump is about the size of the process (a 32-bit client: up to ~4 GB), the client is
/// frozen while it is written, and every client that hits the OUT OF MEMORY box at once writes
/// one. So: the volume must keep a margin free afterwards, and the dumps already in the folder
/// (all clients, live or not) are capped in number and in bytes. When the verdict is not
/// <see cref="DumpVerdict.Full"/> the caller writes the small dump instead.
/// </summary>
internal enum DumpVerdict
{
    Full = 0,
    /// <summary>Free space unknown, or less than the dump plus <see cref="DumpBudget.MinFreeAfterFullDump"/>.</summary>
    LowDisk,
    /// <summary><see cref="DumpBudget.MaxFullDumpsAllPids"/> or more full dumps are already in the folder.</summary>
    TooManyDumps,
    /// <summary>The full dumps in the folder plus this one would pass <see cref="DumpBudget.MaxFullDumpBytesAllPids"/>.</summary>
    TooManyBytes,
}

internal static class DumpBudget
{
    /// <summary>Free space that must remain on the volume after a full dump.</summary>
    internal const long MinFreeAfterFullDump = 2L * 1024 * 1024 * 1024;

    /// <summary>Full dumps in the folder, all clients together, before another is refused.</summary>
    internal const int MaxFullDumpsAllPids = 4;

    /// <summary>Bytes of full dumps in the folder, all clients together, including the one about to be written.</summary>
    internal const long MaxFullDumpBytesAllPids = 14L * 1024 * 1024 * 1024;

    /// <summary>A full dump holds the private memory plus the mapped images and the dump's own tables.</summary>
    internal const long FullDumpOverheadBytes = 512L * 1024 * 1024;

    /// <summary>The small (Normal + handle data) dump is tens of MB; it still needs some room.</summary>
    internal const long SmallDumpMinFree = 200L * 1024 * 1024;

    /// <summary>Largest a 32-bit client can be; the estimate when the process size cannot be read.</summary>
    internal const long FallbackProcessBytes = 4L * 1024 * 1024 * 1024;

    internal static long EstimateFullDumpBytes(long privateBytes)
        => (privateBytes > 0 ? privateBytes : FallbackProcessBytes) + FullDumpOverheadBytes;

    /// <param name="freeBytes">Free bytes available to this user on the dump volume; negative = unknown.</param>
    /// <param name="estimateBytes">The size this dump is expected to have.</param>
    /// <param name="existingCount">Full dumps already in the folder (any client, any state).</param>
    /// <param name="existingBytes">Their combined size.</param>
    internal static DumpVerdict Evaluate(long freeBytes, long estimateBytes, int existingCount, long existingBytes)
    {
        if (freeBytes < 0 || freeBytes - estimateBytes < MinFreeAfterFullDump) return DumpVerdict.LowDisk;
        if (existingCount >= MaxFullDumpsAllPids) return DumpVerdict.TooManyDumps;
        if (existingBytes + estimateBytes > MaxFullDumpBytesAllPids) return DumpVerdict.TooManyBytes;
        return DumpVerdict.Full;
    }

    internal static bool SmallDumpAllowed(long freeBytes) => freeBytes >= SmallDumpMinFree;
}

using System.Diagnostics;

namespace RynthCore.StatusAgent;

/// <summary>
/// Is the client behind a status file still running? A retail client's file is named by its own
/// acclient pid. A headless character's (rynth-headless, several characters per process) is named
/// by a synthetic client id and carries headless / hostPid instead: then the host process must be
/// up and be a rynth-headless. Both checks guard against a recycled pid owned by something else.
/// </summary>
internal static class ClientLiveness
{
    public const string HeadlessProcess = "rynth-headless";

    public static bool IsAlive(int pid, bool headless, int hostPid, Func<int, string?> processName)
    {
        if (headless)
        {
            if (hostPid <= 0) return false;
            string? host = processName(hostPid);
            return host != null && host.Contains(HeadlessProcess, StringComparison.OrdinalIgnoreCase);
        }
        string? name = processName(pid);
        return name != null && name.Contains("acclient", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The process's name, or null when there is no such process.</summary>
    public static string? ProcessName(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch { return null; }   // ArgumentException = no such process
    }
}

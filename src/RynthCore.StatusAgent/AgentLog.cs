namespace RynthCore.StatusAgent;

/// <summary>Tiny console + file logger. Best-effort; never throws.</summary>
internal static class AgentLog
{
    private static string? _logFile;
    private static readonly object Gate = new();
    // StatusAgent.log never rolled over and reached ~700 MB (2026-10-11, disk at 1.7 GB free).
    // Past MaxBytes it becomes StatusAgent.log.old (one old copy) and a fresh file starts.
    private const long MaxBytes = 50L * 1024 * 1024;
    private const int SizeCheckEvery = 200;
    private static int _writesSinceCheck = SizeCheckEvery;
    public static bool Verbose;

    public static void Init(string logDirectory)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            _logFile = Path.Combine(logDirectory, "StatusAgent.log");
        }
        catch { _logFile = null; }
    }

    public static void Info(string msg)  => Write("INF", msg);
    public static void Warn(string msg)  => Write("WRN", msg);
    public static void Error(string msg) => Write("ERR", msg);

    public static void Debug(string msg)
    {
        if (Verbose) Write("DBG", msg);
    }

    /// <summary>Every SizeCheckEvery writes: past MaxBytes, the log moves to .old and starts again.</summary>
    internal static void RollIfBig(string file, long maxBytes = MaxBytes)
    {
        if (++_writesSinceCheck < SizeCheckEvery) return;
        _writesSinceCheck = 0;
        var info = new FileInfo(file);
        if (!info.Exists || info.Length <= maxBytes) return;
        File.Move(file, file + ".old", overwrite: true);
    }

    private static void Write(string level, string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {msg}";
        lock (Gate)
        {
            try { Console.WriteLine(line); } catch { }
            if (_logFile != null)
            {
                try { RollIfBig(_logFile); } catch { }
                try { File.AppendAllText(_logFile, line + Environment.NewLine); } catch { }
            }
        }
    }
}

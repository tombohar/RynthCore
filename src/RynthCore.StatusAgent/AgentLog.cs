namespace RynthCore.StatusAgent;

/// <summary>Tiny console + file logger. Best-effort; never throws.</summary>
internal static class AgentLog
{
    private static string? _logFile;
    private static readonly object Gate = new();
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

    private static void Write(string level, string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {msg}";
        lock (Gate)
        {
            try { Console.WriteLine(line); } catch { }
            if (_logFile != null)
            {
                try { File.AppendAllText(_logFile, line + Environment.NewLine); } catch { }
            }
        }
    }
}

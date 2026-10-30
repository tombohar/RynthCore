using System.Globalization;
using System.Text;

namespace RynthCore;

/// <summary>
/// Desktop text logs with a fixed size cap and per-day roll retention. Active file lives on the user
/// desktop as <c>{fileStem}.log</c>. When the active file reaches <see cref="MaxActiveFileBytes"/>,
/// it is moved into <c>Desktop\RynthLogs\</c> with a time-stamped name, then a new active file is
/// created. For each <paramref name="fileStem"/> and each calendar day, at most
/// <see cref="MaxRolledFilesPerLocalDay"/> archived files are kept (oldest for that day deleted first).
/// </summary>
public static class DesktopRollingLog
{
    // 10 MB active segment, up to 10 rolled segments per local calendar day.
    public const long MaxActiveFileBytes = 10L * 1024 * 1024;
    public const int MaxRolledFilesPerLocalDay = 10;

    private static readonly string DesktopDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    private static readonly string ArchiveDir = Path.Combine(DesktopDir, "RynthLogs");

    /// <summary>Pre-defined stems for the three Rynth products (one file each on the desktop).</summary>
    public const string StemEngine = "RynthCore";
    public const string StemLauncher = "RynthCore-Launcher";
    public const string StemInjector = "RynthCore-Injector";

    private static readonly object Gate = new();

    /// <param name="fileStem">File name without extension, e.g. <see cref="StemEngine"/>.</param>
    public static void AppendLine(string fileStem, string line)
    {
        string stem = SanitizeStem(fileStem);
        string text = line ?? string.Empty;
        if (text.Length > 0 && text[^1] is not '\n' and not '\r')
            text += Environment.NewLine;

        lock (Gate)
        {
            try
            {
                string active = Path.Combine(DesktopDir, stem + ".log");
                Directory.CreateDirectory(ArchiveDir);

                if (File.Exists(active) && new FileInfo(active).Length + Encoding.UTF8.GetByteCount(text) > MaxActiveFileBytes)
                    RotateToArchive(stem, active);

                using var stream = new FileStream(active, FileMode.Append, FileAccess.Write, FileShare.Read);
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
            }
            catch
            {
                // Intentionally silent: logging must not crash host.
            }
        }
    }

    // Moves current active file to RynthLogs; trims same-day archives for this stem, then the move.
    private static void RotateToArchive(string stem, string activePath)
    {
        string ymd = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string prefix = stem + "-roll-" + ymd + "-";

        PruneOldestRolledForPrefix(prefix);

        string destName = prefix + DateTime.Now.ToString("HHmmssfff", CultureInfo.InvariantCulture) + ".log";
        string destPath = Path.Combine(ArchiveDir, destName);
        if (File.Exists(destPath)) File.Delete(destPath);
        try
        {
            File.Move(activePath, destPath, overwrite: false);
        }
        catch
        {
            try
            {
                if (File.Exists(destPath)) File.Delete(destPath);
            }
            catch
            {
                // ignore
            }

            if (File.Exists(activePath)) File.Delete(activePath);
        }
    }

    // Ensures that after a new file with this prefix is created, at most MaxRolledFilesPerLocalDay rolled files for this prefix+day exist.
    private static void PruneOldestRolledForPrefix(string prefix)
    {
        if (!Directory.Exists(ArchiveDir)) return;

        var todaysRolled = new DirectoryInfo(ArchiveDir)
            .GetFiles(prefix + "*.log", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f.CreationTimeUtc)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Delete oldest rolled segments until a new one would not exceed the daily cap.
        while (todaysRolled.Count >= MaxRolledFilesPerLocalDay)
        {
            try
            {
                todaysRolled[0].Delete();
                todaysRolled.RemoveAt(0);
            }
            catch
            {
                break;
            }
        }
    }

    private static string SanitizeStem(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return "RynthLog";
        var invalid = Path.GetInvalidFileNameChars();
        var b = new StringBuilder(stem.Length);
        foreach (var ch in stem.Trim())
        {
            if (!invalid.Contains(ch) && ch != Path.DirectorySeparatorChar && ch != Path.AltDirectorySeparatorChar)
                b.Append(ch);
        }

        string t = b.ToString();
        return t.Length == 0 ? "RynthLog" : t;
    }
}

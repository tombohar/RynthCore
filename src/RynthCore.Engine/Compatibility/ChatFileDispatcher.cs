using System;
using System.IO;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// File-driven chat command dispatcher.
///
/// Watches <c>%APPDATA%\RynthCore\dispatch.txt</c>. Any change to that file
/// is interpreted as a slash command for AC's chat — the file's content
/// (last non-blank line) is sent through <see cref="ChatCommandDispatcher.Dispatch"/>,
/// which routes through any RynthCore-style plugins (RynthAi, VTank, etc.)
/// just like a user-typed command would.
///
/// Use cases:
/// - User is on Remote Desktop and AC's chat input is unresponsive after a
///   hot reload or focus event. Open the dispatch.txt file in Notepad,
///   change the contents, save — command fires.
/// - Scripted automation outside the AC client.
/// - Backup chat path when ImGui/Avalonia keyboard capture is eating input.
///
/// Format: the file is plain text. The dispatcher reads the LAST non-blank
/// non-comment line (lines starting with '#' are ignored). Saving the file
/// triggers a debounced dispatch (~200ms after the last save event, so
/// half-completed edits don't fire).
///
/// Safety:
/// - Empty file or only comments → no dispatch.
/// - Dispatcher does not delete the file. Repeated saves of the same
///   content re-fire the command — by design, so the user can save again
///   to retry. If you want fire-once, write a sentinel line.
/// </summary>
internal static class ChatFileDispatcher
{
    private static FileSystemWatcher? _watcher;
    private static readonly object SyncRoot = new();
    private static long _pendingFireTickMs;
    private static Timer? _debounceTimer;
    private static string _lastDispatchedContent = string.Empty;

    private const int DebounceMs = 200;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RynthCore",
        "dispatch.txt");

    public static void Start()
    {
        if (_watcher != null) return;

        try
        {
            string dir = Path.GetDirectoryName(FilePath) ?? "";
            string name = Path.GetFileName(FilePath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name))
            {
                RynthLog.Compat($"ChatFileDispatcher: invalid FilePath '{FilePath}', not starting.");
                return;
            }

            Directory.CreateDirectory(dir);

            if (!File.Exists(FilePath))
            {
                File.WriteAllText(FilePath,
                    "# RynthCore chat-command dispatch file.\n" +
                    "# Save this file with a slash command on the last line to dispatch it.\n" +
                    "# Example: /ra clearbusy\n" +
                    "# Comments (lines starting with #) and blank lines are ignored.\n" +
                    "# Plugin pre-dispatch (RynthAi /ra, etc.) is honored.\n");
            }

            _watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Renamed += OnRenamed;

            _debounceTimer = new Timer(DispatchOnce, null, Timeout.Infinite, Timeout.Infinite);

            RynthLog.Compat($"ChatFileDispatcher: watching '{FilePath}'.");
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"ChatFileDispatcher: start failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void Stop()
    {
        try
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
        catch { /* shutdown — non-fatal */ }
    }

    private static void OnChanged(object _, FileSystemEventArgs e) => ScheduleDispatch();
    private static void OnRenamed(object _, RenamedEventArgs e) => ScheduleDispatch();

    private static void ScheduleDispatch()
    {
        lock (SyncRoot)
        {
            _pendingFireTickMs = Environment.TickCount64 + DebounceMs;
            try { _debounceTimer?.Change(DebounceMs, Timeout.Infinite); }
            catch { /* timer disposed during shutdown */ }
        }
    }

    private static void DispatchOnce(object? _)
    {
        string? line = null;
        try
        {
            // ReadAllText with a brief retry: the editor may still hold the
            // file briefly after save, FileSystemWatcher fires before the
            // handle is released. Try a few times.
            string? text = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { text = File.ReadAllText(FilePath); break; }
                catch (IOException) { Thread.Sleep(40); }
            }
            if (text == null) return;

            // Find the LAST non-blank non-comment line.
            string[] rawLines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = rawLines.Length - 1; i >= 0; i--)
            {
                string trimmed = rawLines[i].Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith('#')) continue;
                line = trimmed;
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
                return;

            // Dedup adjacent identical content silently. Saving the file
            // re-fires only if content actually differs from last dispatch
            // OR the line is prefixed with "force:" (force-fire even if
            // same content). User can also clear+resave to fire again.
            bool force = line.StartsWith("force:", StringComparison.OrdinalIgnoreCase);
            if (force) line = line.Substring("force:".Length).TrimStart();

            if (!force && line == _lastDispatchedContent)
            {
                RynthLog.Verbose($"ChatFileDispatcher: skipped re-dispatch of unchanged line '{line}'.");
                return;
            }

            _lastDispatchedContent = line;

            // Strip leading slash if present — Dispatch handles both forms.
            string toDispatch = line.StartsWith('/') ? line.Substring(1) : line;
            string forDispatch = '/' + toDispatch;

            RynthLog.Compat($"ChatFileDispatcher: dispatching '{forDispatch}'.");
            try
            {
                bool ok = ChatCommandDispatcher.Dispatch(forDispatch);
                RynthLog.Compat($"ChatFileDispatcher: dispatched ok={ok}.");
            }
            catch (Exception ex)
            {
                RynthLog.Compat($"ChatFileDispatcher: Dispatch threw — {ex.GetType().Name}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"ChatFileDispatcher: read/parse failed — {ex.GetType().Name}: {ex.Message}");
        }
    }
}

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine;

/// <summary>
/// Engine log categories. Each one has a configurable emit level (see <see cref="LogSettings"/>).
/// Names are the keys of engine.json "LogCategories" and must stay stable.
/// </summary>
internal enum LogCategory
{
    /// <summary>Uncategorised RynthLog.Info lines.</summary>
    General = 0,
    /// <summary>D3D9: vtable, EndScene, bootstrapper, matrix capture, nav3D.</summary>
    D3D9,
    /// <summary>Compatibility hooks: client objects, combat, movement, vitals, chat, ...</summary>
    Compat,
    /// <summary>ImGui rendering: context, DX9 backend, Win32 input, shell.</summary>
    Render,
    /// <summary>Plugin system, including every line plugins write through the host Log API.</summary>
    Plugin,
    /// <summary>UI / Avalonia overlay.</summary>
    UI,
    /// <summary>Verbose detail lines (RynthLog.Verbose / RynthLog.Debug).</summary>
    Verbose,
}

/// <summary>
/// Live engine logging configuration, read from %APPDATA%\RynthCore\engine.json:
/// <list type="bullet">
///   <item><c>"LoggingLevel"</c> — global threshold: Off, Error, Warning, Info, Debug, Trace.</item>
///   <item><c>"LogCategories"</c> — per-category emit level: <c>{ "Compat": "Info", "Verbose": "Debug", ... }</c>.
///         The emit level is the level a category's lines are written AT, so a line shows when
///         its emit level is within the global threshold. Setting a Trace/Debug category to
///         Info puts it in the normal (Info) log; Off silences the category entirely.</item>
/// </list>
/// A background thread polls the file's timestamp once a second, so launcher edits apply to a
/// running client without a restart. Errors are always written, even when the level is Off.
/// </summary>
internal static class LogSettings
{
    /// <summary>engine.json property holding the per-category levels.</summary>
    internal const string CategoriesProperty = "LogCategories";

    private const int PollIntervalMs = 1000;

    // Default emit level per LogCategory (index = enum value): categories at Info,
    // Render off, verbose lines at Debug.
    private static readonly EntryPoint.EngineLogLevel[] Defaults =
    {
        EntryPoint.EngineLogLevel.Info,   // General
        EntryPoint.EngineLogLevel.Info,   // D3D9
        EntryPoint.EngineLogLevel.Info,   // Compat
        EntryPoint.EngineLogLevel.Off,    // Render
        EntryPoint.EngineLogLevel.Info,   // Plugin
        EntryPoint.EngineLogLevel.Info,   // UI
        EntryPoint.EngineLogLevel.Debug,  // Verbose
    };

    // Replaced wholesale on reload (never mutated after publish) so hot-path readers on any
    // thread can index it without a lock.
    private static EntryPoint.EngineLogLevel[] _levels = (EntryPoint.EngineLogLevel[])Defaults.Clone();

    private static readonly object WatcherLock = new();
    private static Thread? _watcherThread;
    private static ManualResetEventSlim? _stopSignal;
    private static DateTime _stampUtc = DateTime.MinValue;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "engine.json");

    /// <summary>Current emit level for a category (cheap; safe on hot paths).</summary>
    internal static EntryPoint.EngineLogLevel LevelFor(LogCategory category)
    {
        EntryPoint.EngineLogLevel[] levels = Volatile.Read(ref _levels);
        int i = (int)category;
        return (uint)i < (uint)levels.Length ? levels[i] : EntryPoint.EngineLogLevel.Info;
    }

    /// <summary>True when a line in this category would be written right now.</summary>
    internal static bool IsEnabled(LogCategory category) => EntryPoint.ShouldLog(LevelFor(category));

    /// <summary>3-char severity tag written into each log line.</summary>
    internal static string Tag(EntryPoint.EngineLogLevel level) => level switch
    {
        EntryPoint.EngineLogLevel.Error => "ERR",
        EntryPoint.EngineLogLevel.Warning => "WRN",
        EntryPoint.EngineLogLevel.Debug => "DBG",
        EntryPoint.EngineLogLevel.Trace => "TRC",
        _ => "INF",
    };

    /// <summary>
    /// Reads the global level and category levels from engine.json and publishes them.
    /// Called once at init (before the first log line) and again by the watcher on change.
    /// Missing or unreadable values fall back to their defaults. Returns a one-line summary.
    /// </summary>
    internal static string Reload()
    {
        string? globalText = null;
        var levels = (EntryPoint.EngineLogLevel[])Defaults.Clone();

        try
        {
            string path = SettingsPath;
            if (File.Exists(path))
            {
                _stampUtc = File.GetLastWriteTimeUtc(path);
                // ReadAllText strips a UTF-8/UTF-16 BOM that JsonDocument would choke on.
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("LoggingLevel", out JsonElement levelEl) && levelEl.ValueKind == JsonValueKind.String)
                    globalText = levelEl.GetString();

                if (root.TryGetProperty(CategoriesProperty, out JsonElement catsEl) && catsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in catsEl.EnumerateObject())
                    {
                        if (p.Value.ValueKind != JsonValueKind.String) continue;
                        if (!Enum.TryParse(p.Name, ignoreCase: true, out LogCategory cat) || !Enum.IsDefined(cat)) continue;
                        if (TryParseCategoryLevel(p.Value.GetString(), out EntryPoint.EngineLogLevel lvl))
                            levels[(int)cat] = lvl;
                    }
                }
            }
        }
        catch
        {
            // Half-written / corrupt file: keep defaults for this pass; the next timestamp change retries.
            globalText ??= EngineSettings.LoggingLevel;
        }

        EntryPoint.LoggingLevel = EntryPoint.ParseLoggingLevel(globalText);
        Volatile.Write(ref _levels, levels);
        EntryPoint.VerboseLogging = IsEnabled(LogCategory.Verbose);
        return Describe();
    }

    /// <summary>Human-readable "level=Info D3D9=Info Compat=Info ..." summary for the log.</summary>
    internal static string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("level=").Append(EntryPoint.LoggingLevel);
        EntryPoint.EngineLogLevel[] levels = Volatile.Read(ref _levels);
        for (int i = 0; i < levels.Length; i++)
            sb.Append(' ').Append((LogCategory)i).Append('=').Append(levels[i]);
        return sb.ToString();
    }

    /// <summary>
    /// Categories whose emit level is more verbose than the global level, so none of their lines
    /// are written (e.g. every category at Trace with LoggingLevel Info: an almost empty log).
    /// Empty when nothing is hidden.
    /// </summary>
    internal static string HiddenCategoriesWarning()
    {
        EntryPoint.EngineLogLevel global = EntryPoint.LoggingLevel;
        EntryPoint.EngineLogLevel[] levels = Volatile.Read(ref _levels);
        var hidden = new StringBuilder();
        for (int i = 0; i < levels.Length; i++)
        {
            if (levels[i] == EntryPoint.EngineLogLevel.Off || levels[i] <= global) continue;
            if (hidden.Length > 0) hidden.Append(", ");
            hidden.Append((LogCategory)i).Append('=').Append(levels[i]);
        }
        return hidden.Length == 0 ? ""
            : $"LogSettings: {hidden} write above the global LoggingLevel ({global}), so those lines are NOT logged. " +
              "Raise LoggingLevel to the most verbose category level, or set the categories to Info.";
    }

    /// <summary>Starts the once-a-second engine.json poll. Idempotent.</summary>
    internal static void StartWatcher()
    {
        lock (WatcherLock)
        {
            if (_watcherThread != null) return;
            _stopSignal = new ManualResetEventSlim(false);
            ManualResetEventSlim stop = _stopSignal;
            _watcherThread = new Thread(() => WatchLoop(stop))
            {
                IsBackground = true,
                Name = "RynthCore.LogSettingsWatcher",
            };
            _watcherThread.Start();
        }
    }

    /// <summary>
    /// Stops and joins the poll thread. Called from EngineLifecycle.Shutdown so the thread never
    /// runs inside an old engine generation after a hot reload.
    /// </summary>
    internal static void StopWatcher()
    {
        Thread? thread;
        lock (WatcherLock)
        {
            thread = _watcherThread;
            _stopSignal?.Set();
            _watcherThread = null;
        }
        thread?.Join(2000);
    }

    private static void WatchLoop(ManualResetEventSlim stop)
    {
        while (!stop.Wait(PollIntervalMs))
        {
            try
            {
                string path = SettingsPath;
                if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) == _stampUtc)
                    continue;

                string summary = Reload();
                // Bypasses the category levels so a change is visible whenever Info is in range.
                if (EntryPoint.ShouldLog(EntryPoint.EngineLogLevel.Info))
                    EntryPoint.LogTagged("engine", $"LogSettings: engine.json changed - logging reloaded ({summary}).", "INF");
                string hidden = HiddenCategoriesWarning();
                if (hidden.Length > 0) RynthLog.Warn(hidden);
            }
            catch
            {
                // Logging configuration must never take the client down; retry next poll.
            }
        }
    }

    /// <summary>
    /// Parses a category emit level: Off, Trace, Debug (or Verbose), Info, Warning (or Warn), Error.
    /// </summary>
    private static bool TryParseCategoryLevel(string? text, out EntryPoint.EngineLogLevel level)
    {
        level = EntryPoint.EngineLogLevel.Info;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        if (t.Equals("Verbose", StringComparison.OrdinalIgnoreCase)) { level = EntryPoint.EngineLogLevel.Debug; return true; }
        if (t.Equals("Warn", StringComparison.OrdinalIgnoreCase)) { level = EntryPoint.EngineLogLevel.Warning; return true; }
        return Enum.TryParse(t, ignoreCase: true, out level) && Enum.IsDefined(level);
    }
}

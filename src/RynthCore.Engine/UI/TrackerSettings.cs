// ============================================================================
//  RynthCore.Engine - UI/TrackerSettings.cs
//  The Tracker panel's background opacity and its text-only look, shared by its
//  ImGui and Avalonia faces: %APPDATA%\RynthCore\rynthtracker_settings.json
//  ({"bgAlpha":0-255,"textOnly":false}).
//  Loaded on the engine init worker; saves go through UiBackgroundWriter so
//  the ImGui face never writes a file from AC's thread.
// ============================================================================

using System;
using System.IO;
using System.Text.Json;

namespace RynthCore.Engine.UI;

internal static class TrackerSettings
{
    private static volatile int _bgAlpha = 0xCC; // ~80%
    private static volatile bool _textOnly;
    private static volatile bool _loaded;
    private static readonly object LoadSync = new();

    // RYNTHCORE_TRACKER_SETTINGS: another file (tools\TrackerSettingsTests).
    private static string SettingsPath =>
        Environment.GetEnvironmentVariable("RYNTHCORE_TRACKER_SETTINGS") is { Length: > 0 } over ? over
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "rynthtracker_settings.json");

    /// <summary>Background alpha 0-255.</summary>
    public static byte BgAlpha
    {
        get
        {
            EnsureLoaded();
            return (byte)_bgAlpha;
        }
    }

    /// <summary>
    /// Text only (2026-11-01, the owner: "make the window invisible besides the writing"): docked, the
    /// panel has no frame, title or background, only its rows (shadowed), and clicks pass through
    /// except over them.
    /// </summary>
    public static bool TextOnly
    {
        get
        {
            EnsureLoaded();
            return _textOnly;
        }
    }

    /// <summary>Sets the alpha now; <paramref name="save"/> writes the file in the background.</summary>
    public static void SetBgAlpha(byte alpha, bool save)
    {
        EnsureLoaded();
        _bgAlpha = alpha;
        if (save)
            UiBackgroundWriter.Enqueue("tracker settings", Save);
    }

    /// <summary>Text only on or off, saved in the background.</summary>
    public static void SetTextOnly(bool on)
    {
        EnsureLoaded();
        _textOnly = on;
        UiBackgroundWriter.Enqueue("tracker settings", Save);
    }

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (LoadSync)
        {
            if (_loaded) return;
            try
            {
                string path = SettingsPath;
                if (File.Exists(path))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty("bgAlpha", out var v))
                        _bgAlpha = Math.Clamp(v.GetInt32(), 0, 255);
                    if (doc.RootElement.TryGetProperty("textOnly", out var t) && t.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        _textOnly = t.GetBoolean();
                }
            }
            catch { }
            _loaded = true;
        }
    }

    /// <summary>The settings file's text.</summary>
    internal static string ToJson(int bgAlpha, bool textOnly) =>
        $"{{\"bgAlpha\":{bgAlpha},\"textOnly\":{(textOnly ? "true" : "false")}}}";

    private static void Save()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ToJson(_bgAlpha, _textOnly));
        }
        catch { }
    }
}

// ============================================================================
//  RynthCore.Engine - UI/TrackerSettings.cs
//  The Tracker panel's background opacity, shared by its ImGui and Avalonia
//  faces: %APPDATA%\RynthCore\rynthtracker_settings.json ({"bgAlpha":0-255}).
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
    private static volatile bool _loaded;
    private static readonly object LoadSync = new();

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "rynthtracker_settings.json");

    /// <summary>Background alpha 0-255.</summary>
    public static byte BgAlpha
    {
        get
        {
            EnsureLoaded();
            return (byte)_bgAlpha;
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
                }
            }
            catch { }
            _loaded = true;
        }
    }

    private static void Save()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"{{\"bgAlpha\":{_bgAlpha}}}");
        }
        catch { }
    }
}

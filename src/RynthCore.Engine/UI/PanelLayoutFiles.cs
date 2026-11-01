// ============================================================================
//  RynthCore.Engine - UI/PanelLayoutFiles.cs
//  Panel layout per character: the file operations (PanelLayoutPolicy has the
//  names and decisions). No engine state and no logging, so
//  tools\PanelLayoutTests runs them against a temp folder.
//
//  Every write is atomic (a temp file beside the target, then a replace:
//  EngineSettingsFile.WriteAtomic), so a client that dies mid-save leaves the
//  old file, never half a file. Each client writes only its own character's
//  files; the default pair is written only by an explicit "make this the
//  default" and, as before, by clients that use it.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI;

internal static class PanelLayoutFiles
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>The file's text, or null when there is none.</summary>
    public static string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>Writes <paramref name="text"/> to <paramref name="path"/> atomically (creating its folder).</summary>
    public static void WriteText(string path, string text)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        EngineSettingsFile.WriteAtomic(path, Utf8NoBom.GetBytes(text));
    }

    /// <summary>
    /// A character without a layout yet starts from the default: each of its files that is missing is
    /// copied from the default's (PanelLayoutPolicy.SeedPlan). Files it already has are left alone.
    /// Returns which were copied.
    /// </summary>
    public static (bool PanelState, bool Ini) SeedMissing(LayoutFiles defaults, LayoutFiles character)
    {
        var plan = PanelLayoutPolicy.SeedPlan(File.Exists(character.PanelState), File.Exists(character.ImGuiIni),
            File.Exists(defaults.PanelState), File.Exists(defaults.ImGuiIni));
        if (plan.PanelState) CopyAtomic(defaults.PanelState, character.PanelState);
        if (plan.Ini) CopyAtomic(defaults.ImGuiIni, character.ImGuiIni);
        return plan;
    }

    /// <summary>
    /// /rc layout reset: the character's files become copies of the default's. A file the default
    /// doesn't have makes the character's empty (as a new character with no default would start), so
    /// nothing of the old layout is left. Nothing is deleted.
    /// </summary>
    public static void CopyOver(LayoutFiles defaults, LayoutFiles character)
    {
        if (File.Exists(defaults.PanelState)) CopyAtomic(defaults.PanelState, character.PanelState);
        else WriteText(character.PanelState, string.Empty);
        if (File.Exists(defaults.ImGuiIni)) CopyAtomic(defaults.ImGuiIni, character.ImGuiIni);
        else WriteText(character.ImGuiIni, string.Empty);
    }

    /// <summary>
    /// /rc layout default: the character's layout becomes the default for new characters.
    /// <paramref name="panelState"/> is the panel store's text now; <paramref name="ini"/> the ImGui
    /// windows' (null: the character's imgui.ini as saved, and nothing when it has none).
    /// </summary>
    public static void MakeDefault(LayoutFiles character, LayoutFiles defaults, string panelState, string? ini)
    {
        WriteText(defaults.PanelState, panelState);
        string? text = ini ?? ReadText(character.ImGuiIni);
        if (text != null) WriteText(defaults.ImGuiIni, text);
    }

    private static void CopyAtomic(string from, string to)
    {
        string? dir = Path.GetDirectoryName(to);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        EngineSettingsFile.WriteAtomic(to, File.ReadAllBytes(from));
    }
}

/// <summary>
/// The latest text queued for each file, written by whoever flushes it first (the UI writer, a layout
/// switch, shutdown): an older save can never land after a newer one. Writes run one at a time.
/// </summary>
internal sealed class LatestTextWrites
{
    private readonly object _slots = new();
    private readonly object _writing = new();
    private readonly Dictionary<string, string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string, string> _write;

    public LatestTextWrites(Action<string, string> write) => _write = write;

    /// <summary>Keeps <paramref name="text"/> as what <paramref name="path"/> gets next (replacing an unwritten one). Any thread; never blocks on a write.</summary>
    public void Queue(string path, string text)
    {
        lock (_slots) _pending[path] = text;
    }

    /// <summary>Writes <paramref name="path"/>'s queued text, if any. False when there was nothing to write. Throws what the write throws.</summary>
    public bool Flush(string path)
    {
        lock (_writing)
        {
            string? text;
            lock (_slots)
            {
                if (!_pending.Remove(path, out text)) return false;
            }
            _write(path, text);
            return true;
        }
    }

    /// <summary>True while <paramref name="path"/> has text waiting.</summary>
    public bool HasPending(string path)
    {
        lock (_slots) return _pending.ContainsKey(path);
    }
}

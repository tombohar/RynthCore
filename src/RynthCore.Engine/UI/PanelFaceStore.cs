// ============================================================================
//  RynthCore.Engine - UI/PanelFaceStore.cs
//  Which face shows a panel while it is docked in the client:
//  %LocalAppData%\RynthCore\panel_faces.txt, one "<Title>=avalonia|imgui|both"
//  per line. Popped-out panels are always Avalonia windows, so only the docked
//  face is a choice (docs/IMGUI_PARITY_PLAN.md, "ImGui in game, Avalonia when
//  popped out").
//
//  Its own file rather than a panel_state.txt row: PanelStateStore rewrites
//  that file from memory and drops rows it doesn't recognise, so an older
//  engine would silently erase the choice. Engine-owned; the launcher never
//  writes it. A missing entry means "use the code default".
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RynthCore.Engine.UI;

internal enum PanelFace
{
    Avalonia,
    ImGui,
    /// <summary>Both faces docked at once, the ImGui copy offset, for side-by-side checks.</summary>
    Both,
}

internal static class PanelFaceStore
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, PanelFace> Faces = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "panel_faces.txt");

    public static bool TryGet(string title, out PanelFace face)
    {
        lock (Sync)
        {
            EnsureLoaded();
            return Faces.TryGetValue(title, out face);
        }
    }

    /// <summary>Stores a choice; null clears it back to the code default.</summary>
    public static void Set(string title, PanelFace? face)
    {
        lock (Sync)
        {
            EnsureLoaded();
            if (face is PanelFace f) Faces[title] = f;
            else Faces.Remove(title);
        }
        // May be called from AC's thread (the ImGui bar's menu): write in the background.
        UiBackgroundWriter.Enqueue("panel faces", () => { lock (Sync) Save(); });
    }

    public static string FaceName(PanelFace face) => face switch
    {
        PanelFace.ImGui => "imgui",
        PanelFace.Both => "both",
        _ => "avalonia",
    };

    public static bool TryParseFace(string text, out PanelFace face)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "avalonia": face = PanelFace.Avalonia; return true;
            case "imgui": face = PanelFace.ImGui; return true;
            case "both": face = PanelFace.Both; return true;
            default: face = PanelFace.Avalonia; return false;
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            string path = FilePath;
            if (!File.Exists(path)) return;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (TryParseFace(line.Substring(eq + 1), out PanelFace face))
                    Faces[line.Substring(0, eq).Trim()] = face;
            }
        }
        catch (Exception ex)
        {
            RynthLog.UI($"PanelFaceStore: load failed - {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Save()
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var sb = new StringBuilder();
            sb.AppendLine("# RynthCore docked panel face (avalonia|imgui|both). Set with /rc ui <Panel> <face>.");
            foreach (var (title, face) in Faces)
                sb.Append(title).Append('=').AppendLine(FaceName(face));
            File.WriteAllText(path, sb.ToString());
        }
        catch (Exception ex)
        {
            RynthLog.UI($"PanelFaceStore: save failed - {ex.GetType().Name}: {ex.Message}");
        }
    }
}

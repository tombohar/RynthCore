// ============================================================================
//  RynthCore.Engine - UI/Sections/SectionManifest.cs
//  What a plugin adds to the Mini Remote's sections, read from its optional
//  export  const char* RynthPluginGetSectionsJson(void)  (UTF-8 JSON, the same
//  parameterless JSON-getter convention as RynthPluginGetSnapshotJson).
//
//  Pure (no AC, no engine state): tools/SectionsTests compiles this file as is.
//
//  Schema (version 1; unknown keys are ignored, so later versions only add):
//  {
//    "version": 1,
//    "servers":  [ { "id": "ilt", "name": "Infinite Leaftide",
//                    "worldNames": ["*Leaftide*"], "hosts": ["infiniteleaftide.online"] } ],
//    "sections": [ { "id": "quests", "label": "Quests", "order": 150 } ],
//    "contributions": [
//      { "section": "pets", "server": "", "label": "Pets", "surface": "section:pets",
//        "primary": true, "width": 560, "height": 580 },
//      { "section": "skills", "server": "ilt", "label": "Progression",
//        "command": "/rc progression", "tooltip": "...", "enabled": true, "note": "" } ],
//    "windows": [
//      { "id": "characters", "title": "Characters", "surface": "window:characters",
//        "icon": "\uE68E", "width": 420, "height": 260, "minWidth": 280, "minHeight": 140,
//        "bar": true, "edgeToEdge": true, "server": "", "takeOver": false,
//        "chrome": "", "fitHeight": false } ]
//  }
//  servers       servers the plugin knows. A built-in id (ilt, aelrynth) keeps the
//                engine's own detection; a new id is detected from its lists.
//  sections      extra sections (beyond the core twelve) the plugin adds.
//  contributions what the plugin puts in a section: "server" "" = every world, else
//                only while that server is active (detected or forced). "surface"
//                = drawn inline through RynthPluginRenderEmbed; "command" = a button
//                that runs it. "primary" = the section's main view when the engine
//                has no core panel for it (Pets, Crafting, Spells, ...).
//  windows       (plugin API v89, 2026-10-21) windows the plugin draws and the engine
//                hosts as panels: header, pop-out, resize, remembered place, a bar
//                button when "bar" (the icon: a Phosphor glyph, else the title's
//                letter); the body is "surface" through RynthPluginRenderEmbed.
//                "title" is the panel key (unique). An engine panel's title is refused,
//                unless "takeOver": true (2026-10-21): the window then draws in that
//                panel's place (same key: its saved place, open state and commands)
//                and the engine's face comes back when the plugin goes. Engines
//                before that ignore the key and keep their face (the title refused).
//                "server" set: only while that server is active. docs/PLUGIN_WINDOWS.md.
//                v90 (2026-10-21): "chrome": "none" = no header and no background or
//                border: the plugin draws the whole window, its own title row included
//                (PanelDragWithLastItem moves it), edge to edge. "fitHeight": true = the
//                window's height follows the height the plugin's draw asks for
//                (RynthPluginRenderEmbed's wantHeight) and its width scales with the
//                panel's text size. Engines before v90 ignore both. "corner":
//                "bottomRight" (or topLeft, topRight, bottomLeft, top, bottom: centred) = where it first
//                opens when nothing is saved for it (v90, 2026-10-22). "scale": "draw" =
//                the window draws its content at any size (PanelScale): Ctrl+wheel over
//                it steps that size, 30-250%, not its text size (v90, 2026-10-23).
//
//  Everything from a plugin is checked here: ids are short lower-case words,
//  text is capped, a command must be a single "/..." chat command, and the lists
//  are capped, so a damaged or hostile manifest can't flood the UI.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace RynthCore.Engine.UI.Sections;

/// <summary>A server a plugin declares (detected by the engine from its lists unless the id is built in).</summary>
internal sealed record SectionServerDecl(string Id, string Name, string[] WorldNames, string[] Hosts);

/// <summary>An extra section a plugin adds.</summary>
internal sealed record SectionDecl(string Id, string Label, int Order);

/// <summary>One thing a plugin puts in a section.</summary>
internal sealed record SectionContribution(
    string Plugin,
    string Section,
    string Server,
    string Label,
    string Surface,
    string Command,
    string Tooltip,
    bool Primary,
    bool Enabled,
    string Note,
    float Width,
    float Height,
    int Order)
{
    /// <summary>Drawn inline (a plugin surface), not a button.</summary>
    public bool IsSurface => Surface.Length > 0;
    /// <summary>For every world, not one server.</summary>
    public bool IsUniversal => Server.Length == 0;
}

/// <summary>
/// A window a plugin draws, hosted by the engine (docs/PLUGIN_WINDOWS.md): its panel key and header
/// are <see cref="Title"/>, its body the plugin's <see cref="Surface"/> through RynthPluginRenderEmbed.
/// </summary>
internal sealed record SectionWindowDecl(
    string Plugin,
    string Id,
    string Title,
    string Surface,
    string Icon,
    float Width,
    float Height,
    float MinWidth,
    float MinHeight,
    bool Bar,
    bool EdgeToEdge,
    string Server,
    // Takes the place of the engine panel with this title while hosted ("takeOver": true).
    bool TakeOver = false,
    // v90: no header, background or border; the plugin draws the whole window ("chrome": "none").
    bool Frameless = false,
    // v90: the height follows the plugin's wantHeight; the width scales with the text size ("fitHeight").
    bool FitHeight = false,
    // v90: where it first opens with nothing saved: "", "topLeft", "topRight", "bottomLeft", "bottomRight" ("corner").
    string Corner = "",
    // v90: it draws its own content at any size, so Ctrl+wheel steps its draw size (PanelScale), not its text ("scale": "draw").
    bool DrawScale = false,
    // 2026-11-07 (no version bump: older engines ignore the key): drawn under AC's own UI, like a native
    // AC window ("layer": "underUi"; frameless windows only). AC's windows cover it and take the clicks there.
    bool UnderUi = false)
{
    /// <summary>For every world, not one server.</summary>
    public bool IsUniversal => Server.Length == 0;
}

/// <summary>One plugin's parsed manifest.</summary>
internal sealed record SectionManifest(
    string Plugin,
    SectionServerDecl[] Servers,
    SectionDecl[] Sections,
    SectionContribution[] Contributions)
{
    public static readonly SectionManifest Empty =
        new("", Array.Empty<SectionServerDecl>(), Array.Empty<SectionDecl>(), Array.Empty<SectionContribution>());

    /// <summary>The windows the plugin draws (plugin API v89 engines host them; older ones ignore the key).</summary>
    public SectionWindowDecl[] Windows { get; init; } = Array.Empty<SectionWindowDecl>();
}

internal static class SectionManifestParser
{
    public const int SupportedVersion = 1;
    public const int MaxServers = 16;
    public const int MaxSections = 32;
    public const int MaxContributions = 128;
    public const int MaxPatterns = 32;
    public const int MaxIdLength = 32;
    public const int MaxLabelLength = 48;
    public const int MaxTextLength = 300;
    public const int MaxCommandLength = 200;
    public const int MaxSurfaceLength = 64;
    public const float MaxSurfaceSize = 4096f;
    /// <summary>Largest manifest read at all (a manifest is a few KB).</summary>
    public const int MaxJsonLength = 256 * 1024;
    public const int MaxWindows = 8;
    public const int MaxWindowTitleLength = 32;
    public const float DefaultWindowWidth = 420f, DefaultWindowHeight = 300f, MinWindowSize = 120f, MaxWindowSize = 2000f;

    /// <summary>
    /// Parses <paramref name="json"/> from plugin <paramref name="plugin"/>. Never throws:
    /// a malformed document gives <see cref="SectionManifest.Empty"/> and the reason in
    /// <paramref name="error"/>; a bad entry is skipped.
    /// </summary>
    public static SectionManifest Parse(string plugin, string? json, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json)) { error = "empty"; return SectionManifest.Empty with { Plugin = plugin }; }
        if (json.Length > MaxJsonLength) { error = $"too large ({json.Length} chars)"; return SectionManifest.Empty with { Plugin = plugin }; }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "not a JSON object"; return SectionManifest.Empty with { Plugin = plugin }; }
            int version = root.TryGetProperty("version", out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 1;
            if (version < 1) { error = $"version {version}"; return SectionManifest.Empty with { Plugin = plugin }; }

            var servers = new List<SectionServerDecl>();
            foreach (JsonElement e in Array(root, "servers"))
            {
                if (servers.Count >= MaxServers) break;
                string id = Id(Str(e, "id"));
                if (id.Length == 0 || servers.Exists(s => s.Id == id)) continue;
                string name = Text(Str(e, "name"), MaxLabelLength);
                servers.Add(new SectionServerDecl(id, name.Length > 0 ? name : id, Patterns(e, "worldNames"), Patterns(e, "hosts")));
            }

            var sections = new List<SectionDecl>();
            foreach (JsonElement e in Array(root, "sections"))
            {
                if (sections.Count >= MaxSections) break;
                string id = Id(Str(e, "id"));
                string label = Text(Str(e, "label"), MaxLabelLength);
                if (id.Length == 0 || label.Length == 0 || sections.Exists(s => s.Id == id)) continue;
                sections.Add(new SectionDecl(id, label, Int(e, "order", 1000)));
            }

            var contributions = new List<SectionContribution>();
            foreach (JsonElement e in Array(root, "contributions"))
            {
                if (contributions.Count >= MaxContributions) break;
                string section = Id(Str(e, "section"));
                string label = Text(Str(e, "label"), MaxLabelLength);
                if (section.Length == 0 || label.Length == 0) continue;
                string rawServer = Str(e, "server");
                string server = rawServer.Length == 0 ? string.Empty : Id(rawServer);
                if (rawServer.Length > 0 && server.Length == 0) continue;   // a server id that isn't one
                string surface = Surface(Str(e, "surface"));
                string command = Command(Str(e, "command"));
                if (surface.Length == 0 && command.Length == 0) continue;   // nothing to draw or run
                contributions.Add(new SectionContribution(
                    plugin, section, server, label, surface, command,
                    Text(Str(e, "tooltip"), MaxTextLength, multiline: true),
                    Bool(e, "primary", false),
                    Bool(e, "enabled", true),
                    Text(Str(e, "note"), MaxTextLength, multiline: true),
                    Size(e, "width"), Size(e, "height"),
                    Int(e, "order", 1000)));
            }

            var windows = new List<SectionWindowDecl>();
            foreach (JsonElement e in Array(root, "windows"))
            {
                if (windows.Count >= MaxWindows) break;
                string id = Id(Str(e, "id"));
                string title = Text(Str(e, "title"), MaxWindowTitleLength);
                string surface = Surface(Str(e, "surface"));
                if (id.Length == 0 || title.Length == 0 || surface.Length == 0) continue;
                if (windows.Exists(w => w.Id == id || w.Title.Equals(title, StringComparison.OrdinalIgnoreCase))) continue;
                string rawServer = Str(e, "server");
                string server = rawServer.Length == 0 ? string.Empty : Id(rawServer);
                if (rawServer.Length > 0 && server.Length == 0) continue;
                float width = WindowSize(Size(e, "width"), DefaultWindowWidth);
                float height = WindowSize(Size(e, "height"), DefaultWindowHeight);
                float minWidth = Math.Min(WindowSize(Size(e, "minWidth"), MinWindowSize), width);
                float minHeight = Math.Min(WindowSize(Size(e, "minHeight"), MinWindowSize), height);
                windows.Add(new SectionWindowDecl(plugin, id, title, surface, Icon(Str(e, "icon")),
                    width, height, minWidth, minHeight, Bool(e, "bar", false), Bool(e, "edgeToEdge", false), server,
                    Bool(e, "takeOver", false),
                    Frameless: Str(e, "chrome").Trim().Equals("none", StringComparison.OrdinalIgnoreCase),
                    FitHeight: Bool(e, "fitHeight", false),
                    Corner: Str(e, "corner").Trim() is "topLeft" or "topRight" or "bottomLeft" or "bottomRight" or "top" or "bottom" ? Str(e, "corner").Trim() : "",
                    DrawScale: Str(e, "scale").Trim().Equals("draw", StringComparison.OrdinalIgnoreCase),
                    UnderUi: Str(e, "layer").Trim().Equals("underUi", StringComparison.OrdinalIgnoreCase)));
            }

            return new SectionManifest(plugin, servers.ToArray(), sections.ToArray(), contributions.ToArray())
            {
                Windows = windows.ToArray(),
            };
        }
        catch (JsonException ex)
        {
            error = "bad JSON: " + ex.Message;
            return SectionManifest.Empty with { Plugin = plugin };
        }
    }

    // ── Field rules ─────────────────────────────────────────────────────

    /// <summary>A lower-case id of letters, digits, '-' and '_' (at most MaxIdLength), else "".</summary>
    public static string Id(string? text)
    {
        string t = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (t.Length == 0 || t.Length > MaxIdLength) return string.Empty;
        foreach (char c in t)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return string.Empty;
        return t;
    }

    /// <summary>
    /// Display text: control characters become spaces (line breaks too, unless
    /// <paramref name="multiline"/>: tooltips and notes), trimmed, capped.
    /// </summary>
    public static string Text(string? text, int max, bool multiline = false)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (char.IsControl(chars[i]) && !(multiline && chars[i] == '\n')) chars[i] = ' ';
        string t = new string(chars).Trim();
        return t.Length <= max ? t : t[..max];
    }

    /// <summary>A plugin surface name: letters, digits and ":_.-", not the engine's ":popout" suffix.</summary>
    public static string Surface(string? text)
    {
        string t = (text ?? string.Empty).Trim();
        if (t.Length == 0 || t.Length > MaxSurfaceLength || t.EndsWith(":popout", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        foreach (char c in t)
            if (!(char.IsAsciiLetterOrDigit(c) || c is ':' or '_' or '.' or '-')) return string.Empty;
        return t;
    }

    /// <summary>
    /// A window's bar icon: one or two characters of the icon font's private use area
    /// (U+E000-U+F8FF, where the Phosphor glyphs sit), else "" (the bar shows the title's letter).
    /// </summary>
    public static string Icon(string? text)
    {
        string t = (text ?? string.Empty).Trim();
        if (t.Length is 0 or > 2) return string.Empty;
        foreach (char c in t)
            if (c is < '\uE000' or > '\uF8FF') return string.Empty;
        return t;
    }

    /// <summary>A window dimension: the given size within MinWindowSize..MaxWindowSize, or <paramref name="fallback"/>.</summary>
    private static float WindowSize(float given, float fallback) =>
        given <= 0 ? fallback : Math.Clamp(given, MinWindowSize, MaxWindowSize);

    /// <summary>One chat command ("/rc progression"): starts with '/', one line, capped; else "".</summary>
    public static string Command(string? text)
    {
        string t = (text ?? string.Empty).Trim();
        if (t.Length < 2 || t.Length > MaxCommandLength || t[0] != '/') return string.Empty;
        foreach (char c in t)
            if (char.IsControl(c)) return string.Empty;
        return t;
    }

    private static IEnumerable<JsonElement> Array(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement a) || a.ValueKind != JsonValueKind.Array) yield break;
        foreach (JsonElement e in a.EnumerateArray())
            if (e.ValueKind == JsonValueKind.Object) yield return e;
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static bool Bool(JsonElement e, string name, bool fallback)
        => e.TryGetProperty(name, out JsonElement v) ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        } : fallback;

    private static int Int(JsonElement e, string name, int fallback)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)
            ? Math.Clamp(n, -100_000, 100_000) : fallback;

    /// <summary>A surface's preferred width or height in pixels (0 = not given).</summary>
    private static float Size(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)
           && d > 0 && !double.IsNaN(d) ? (float)Math.Min(d, MaxSurfaceSize) : 0f;

    private static string[] Patterns(JsonElement e, string name)
    {
        var list = new List<string>();
        if (!e.TryGetProperty(name, out JsonElement a) || a.ValueKind != JsonValueKind.Array) return list.ToArray();
        foreach (JsonElement p in a.EnumerateArray())
        {
            if (list.Count >= MaxPatterns) break;
            if (p.ValueKind != JsonValueKind.String) continue;
            string t = Text(p.GetString(), MaxLabelLength);
            if (t.Length > 0 && !list.Contains(t, StringComparer.OrdinalIgnoreCase)) list.Add(t);
        }
        return list.ToArray();
    }
}

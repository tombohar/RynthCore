// ============================================================================
//  RynthCore.Engine - D3D9/VideoPatchPolicy.cs
//  The pure part of the video patch (D3D9/VideoPatch.cs): the modes, what each
//  one skips, the frame cap, and the settings file's text format. No engine,
//  AC or file-system dependencies, so tools/VideoPatchTests compiles it in.
//
//  Modes (also the Settings combo order, so the values are the combo indexes):
//    Off        normal.
//    Unfocused  World, only while the client window isn't the foreground window.
//    World      AC's 3D scene is not drawn; AC's 2D UI and our panels still are.
//    All        AC's 3D scene and AC's 2D UI are not drawn; our panels and a
//               "rendering paused" banner are. Never saved (session only).
//
//  Settings file (%LOCALAPPDATA%\RynthCore\video_patch.txt), one key=value a line:
//    FrameCapFps=15                 PC-wide: the fps cap while the world is skipped (0 = none)
//    Aelrynth/Some Character=world  per character ("<server>/<character>")
//    Default=unfocused              used when the client doesn't know its character
//  Lines it doesn't understand are kept as they are.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;

namespace RynthCore.Engine.D3D9;

internal enum VideoPatchMode
{
    Off = 0,
    Unfocused = 1,
    World = 2,
    All = 3,
}

/// <summary>What one frame skips.</summary>
internal readonly record struct VideoPatchDecision(bool SkipWorld, bool SkipAcUi)
{
    public static readonly VideoPatchDecision None = new(false, false);
    public bool Any => SkipWorld || SkipAcUi;
}

internal static class VideoPatchPolicy
{
    public const int DefaultFrameCapFps = 15;
    public const int MinFrameCapFps = 5;
    public const int MaxFrameCapFps = 60;

    public const string FrameCapKey = "FrameCapFps";
    public const string DefaultCharacterKey = "Default";

    /// <summary>Settings combo labels, in enum order.</summary>
    public static readonly string[] ComboItems =
    {
        "Off",
        "World when unfocused",
        "World (keep AC's UI)",
        "All (session only)",
    };

    /// <summary>The command / file word for a mode.</summary>
    public static string Name(VideoPatchMode mode) => mode switch
    {
        VideoPatchMode.Unfocused => "unfocused",
        VideoPatchMode.World => "world",
        VideoPatchMode.All => "all",
        _ => "off",
    };

    /// <summary>Parses a command / file word. "on" means world (UtilityBelt's habit); "focus" means unfocused.</summary>
    public static bool TryParse(string? text, out VideoPatchMode mode)
    {
        mode = VideoPatchMode.Off;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "off": case "0": case "false": case "none": case "disable": case "disabled": mode = VideoPatchMode.Off; return true;
            case "on": case "1": case "true": case "world": case "3d": case "enable": case "enabled": mode = VideoPatchMode.World; return true;
            case "unfocused": case "focus": case "background": case "bg": mode = VideoPatchMode.Unfocused; return true;
            case "all": case "full": case "everything": mode = VideoPatchMode.All; return true;
            default: return false;
        }
    }

    /// <summary>
    /// What a frame skips. Nothing outside the world (login, character select, portal
    /// out to the character list, client teardown): there AC's screens are all UI, and
    /// a skipped UI there would leave a black window nobody can type into.
    /// <paramref name="uiSkippable"/>: our ImGui layer is on (banner, panels) and AC's UI pass
    /// is hooked. Without it "All" is "World", so AC's chat stays.
    /// </summary>
    public static VideoPatchDecision Decide(VideoPatchMode mode, bool inWorld, bool focused, bool uiSkippable)
    {
        if (!inWorld) return VideoPatchDecision.None;
        return mode switch
        {
            VideoPatchMode.World => new(true, false),
            VideoPatchMode.Unfocused => focused ? VideoPatchDecision.None : new(true, false),
            VideoPatchMode.All => uiSkippable ? new(true, true) : new(true, false),
            _ => VideoPatchDecision.None,
        };
    }

    /// <summary>The fps cap for a frame (0 = none): the configured cap while the world is skipped.</summary>
    public static int FrameCap(VideoPatchDecision decision, int configuredCapFps) =>
        decision.SkipWorld && configuredCapFps > 0 ? ClampCap(configuredCapFps) : 0;

    /// <summary>0 stays 0 (no cap); anything else is held to 5..60.</summary>
    public static int ClampCap(int fps) => fps <= 0 ? 0 : Math.Clamp(fps, MinFrameCapFps, MaxFrameCapFps);

    /// <summary>Only these modes are written to the file ("all" is session only).</summary>
    public static bool IsSaved(VideoPatchMode mode) => mode != VideoPatchMode.All;

    /// <summary>The per-character key: "&lt;server&gt;/&lt;character&gt;", or "Default" without a character name.</summary>
    public static string CharacterKey(string? server, string? character)
    {
        string c = (character ?? string.Empty).Trim();
        if (c.Length == 0) return DefaultCharacterKey;
        string s = (server ?? string.Empty).Trim();
        // '=' would end the key early; neither name can hold one in AC, but be safe.
        return (s.Length > 0 ? s + "/" + c : c).Replace('=', '_');
    }

    // ── The settings file ────────────────────────────────────────────────

    internal sealed class FileModel
    {
        public int FrameCapFps = DefaultFrameCapFps;
        /// <summary>Per-character saved modes (key = CharacterKey), case-insensitive.</summary>
        public readonly Dictionary<string, VideoPatchMode> Modes = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Lines this version doesn't understand, kept in order.</summary>
        public readonly List<string> Other = new();

        /// <summary>The saved mode for a character; Off when none (and no "Default" line).</summary>
        public VideoPatchMode ModeFor(string key)
        {
            if (Modes.TryGetValue(key, out VideoPatchMode m)) return m;
            if (Modes.TryGetValue(DefaultCharacterKey, out m)) return m;
            return VideoPatchMode.Off;
        }

        /// <summary>Stores a character's mode; "all" is never stored, "off" removes the line.</summary>
        public bool Set(string key, VideoPatchMode mode)
        {
            if (!IsSaved(mode)) return false;
            if (mode == VideoPatchMode.Off) Modes.Remove(key);
            else Modes[key] = mode;
            return true;
        }
    }

    public static FileModel Parse(IEnumerable<string>? lines)
    {
        var model = new FileModel();
        if (lines == null) return model;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int eq = line.LastIndexOf('=');
            if (line.StartsWith('#') || eq <= 0)
            {
                model.Other.Add(raw);
                continue;
            }
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if (key.Equals(FrameCapKey, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps))
                    model.FrameCapFps = ClampCap(fps);
                continue;
            }
            if (TryParse(value, out VideoPatchMode mode) && IsSaved(mode))
            {
                if (mode != VideoPatchMode.Off) model.Modes[key] = mode;
                continue;
            }
            model.Other.Add(raw);
        }
        return model;
    }

    public static List<string> Serialize(FileModel model)
    {
        var lines = new List<string>
        {
            "# RynthCore video patch: <server>/<character>=off|unfocused|world, Default=... without a name. /rc video",
            $"{FrameCapKey}={model.FrameCapFps.ToString(CultureInfo.InvariantCulture)}",
        };
        var keys = new List<string>(model.Modes.Keys);
        keys.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string key in keys)
            lines.Add($"{key}={Name(model.Modes[key])}");
        foreach (string other in model.Other)
            if (!other.TrimStart().StartsWith("# RynthCore video patch", StringComparison.Ordinal))
                lines.Add(other);
        return lines;
    }
}

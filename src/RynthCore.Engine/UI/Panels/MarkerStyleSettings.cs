// ============================================================================
//  RynthCore.Engine - UI/Panels/MarkerStyleSettings.cs
//  Per-kind marker size, name label and name size, for one map surface. The Radar
//  and the Dungeon Map each own one (RadarSettingsStore.Markers /
//  DungeonMapSettingsStore.Markers), so a name that suits the big map can stay off
//  the small radar. No ImGui here: tools/RadarSettingsTests compiles this file.
//
//    Size      marker scale per RadarKind (1 = the surface's normal size)
//    ShowName  draw the object's name (the marker's label) beside the marker
//    NameSize  that name's text size, in pixels at 100% UI scale
//    SelfSize  your own dot and heading arrow
//
//  Settings-file keys (written only when they differ from the defaults, so a later
//  default change still lands): size.<kind>, name.<kind>, nameSize.<kind>, size.self,
//  where <kind> is the kind's key (monster, npc, portal, door, player, ...).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.UI.Panels;

internal sealed class MarkerStyleSettings
{
    public const float MinSize = 0.5f, MaxSize = 3.0f, DefaultSize = 1.0f;
    public const float MinName = 7f, MaxName = 24f, DefaultName = 11f;

    /// <summary>Settings-file key per <see cref="RadarKind"/>.</summary>
    public static readonly string[] Keys =
    {
        "monster", "npc", "portal", "door", "player", "fellow",
        "pet", "vendor", "corpse", "ownCorpse", "lifestone", "groundItem",
    };

    /// <summary>Marker scale per kind.</summary>
    public readonly float[] Size = new float[RadarKind.Count];
    /// <summary>Whether a kind's name is drawn.</summary>
    public readonly bool[] ShowName = new bool[RadarKind.Count];
    /// <summary>Name text size per kind (px at 100% UI scale).</summary>
    public readonly float[] NameSize = new float[RadarKind.Count];
    /// <summary>Scale of your own dot and heading arrow.</summary>
    public float SelfSize = DefaultSize;

    private readonly bool[] _defaultNames;

    /// <param name="defaultNames">Which kinds show their name until the user says otherwise (length RadarKind.Count).</param>
    public MarkerStyleSettings(bool[] defaultNames)
    {
        if (defaultNames.Length != RadarKind.Count) throw new ArgumentException("one flag per RadarKind", nameof(defaultNames));
        _defaultNames = (bool[])defaultNames.Clone();
        Reset();
    }

    /// <summary>Every size and name box back to its default.</summary>
    public void Reset()
    {
        for (int k = 0; k < RadarKind.Count; k++)
        {
            Size[k] = DefaultSize;
            ShowName[k] = _defaultNames[k];
            NameSize[k] = DefaultName;
        }
        SelfSize = DefaultSize;
    }

    public bool NameShown(byte kind) => kind < RadarKind.Count && ShowName[kind];

    /// <summary>Marker scale of a kind; 1 for a kind this engine doesn't know.</summary>
    public float SizeOf(byte kind) => kind < RadarKind.Count ? Size[kind] : DefaultSize;

    /// <summary>Name size of a kind (px at 100% UI scale).</summary>
    public float NameSizeOf(byte kind) => kind < RadarKind.Count ? NameSize[kind] : DefaultName;

    /// <summary>Takes a settings-file line if its key is one of ours. True when it was.</summary>
    public bool TryLoad(string key, string val)
    {
        int dot = key.IndexOf('.');
        if (dot <= 0) return false;
        string head = key[..dot], tail = key[(dot + 1)..];
        if (head != "size" && head != "name" && head != "nameSize") return false;

        if (head == "size" && tail == "self")
        {
            if (TryFloat(val, out float self)) SelfSize = Math.Clamp(self, MinSize, MaxSize);
            return true;
        }
        int k = Array.IndexOf(Keys, tail);
        if (k < 0) return false;
        switch (head)
        {
            case "size":     if (TryFloat(val, out float s)) Size[k] = Math.Clamp(s, MinSize, MaxSize); break;
            case "name":     ShowName[k] = val == "1"; break;
            case "nameSize": if (TryFloat(val, out float n)) NameSize[k] = Math.Clamp(n, MinName, MaxName); break;
        }
        return true;
    }

    /// <summary>The settings-file lines for everything that differs from the defaults.</summary>
    public void AppendLines(List<string> lines)
    {
        for (int k = 0; k < RadarKind.Count; k++)
        {
            if (Size[k] != DefaultSize) lines.Add(FormattableString.Invariant($"size.{Keys[k]}={Size[k]:0.##}"));
            if (ShowName[k] != _defaultNames[k]) lines.Add($"name.{Keys[k]}={(ShowName[k] ? "1" : "0")}");
            if (NameSize[k] != DefaultName) lines.Add(FormattableString.Invariant($"nameSize.{Keys[k]}={NameSize[k]:0.##}"));
        }
        if (SelfSize != DefaultSize) lines.Add(FormattableString.Invariant($"size.self={SelfSize:0.##}"));
    }

    private static bool TryFloat(string val, out float f) =>
        float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out f) && float.IsFinite(f);
}

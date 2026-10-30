// ============================================================================
//  RynthCore.Engine - ImGui/Panels/RadarMarkerStyle.cs
//  One shape per radar marker kind (RadarKind), shared by the Radar face, its
//  settings legend and the Dungeon Map. Shapes differ as well as colours, so
//  the kinds stay apart for colour-blind players and when a user picks two
//  similar colours:
//    monster  filled dot            player   dot with a white ring
//    NPC      filled dot            fellow   triangle with a white edge
//    vendor   coin (dot, dark rim)  pet      small dot, dark edge
//    corpse   X                     your corpse  bold X with a dark outline
//    lifestone  ring with a centre  item     small square
//    portal   diamond               door     square with a dark edge
//  Colours come in as ImGui colours (RynthTheme.Argb of the ARGB setting).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI.Data;
using RynthCore.Engine.UI.Panels;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal static class RadarMarkerStyle
{
    private static readonly uint White = RynthTheme.Argb(0xF0FFFFFF);
    private static readonly uint Black = RynthTheme.Argb(0xE6000000);

    /// <summary>
    /// Draws a marker of <paramref name="kind"/> centred on <paramref name="p"/>. <paramref name="s"/>
    /// scales it (1 = the radar's size; the Dungeon Map draws a little bigger).
    /// </summary>
    public static void Draw(ImDrawListPtr dl, byte kind, Vector2 p, uint col, float s = 1f)
    {
        switch (kind)
        {
            case RadarKind.Monster:
            case RadarKind.Npc:
                dl.AddCircleFilled(p, 3f * s, col, 8);
                break;
            case RadarKind.Portal:
            {
                float r = 4f * s;
                dl.AddQuadFilled(p + new Vector2(0, -r), p + new Vector2(r, 0), p + new Vector2(0, r), p + new Vector2(-r, 0), col);
                break;
            }
            case RadarKind.Door:
            {
                var h = new Vector2(3.5f * s, 3.5f * s);
                dl.AddRectFilled(p - h, p + h, col);
                dl.AddRect(p - h, p + h, Darken(col, 0.4f));
                break;
            }
            case RadarKind.Player:
                dl.AddCircleFilled(p, 3.5f * s, col, 10);
                dl.AddCircle(p, 4.5f * s, White, 12, 1.5f);
                break;
            case RadarKind.Fellow:
            {
                float r = 4.8f * s;
                Vector2 a = p + new Vector2(0, -r), b = p + new Vector2(r * 0.9f, r * 0.65f), c = p + new Vector2(-r * 0.9f, r * 0.65f);
                dl.AddTriangleFilled(a, b, c, col);
                dl.AddTriangle(a, b, c, White, 1.3f);
                break;
            }
            case RadarKind.Pet:
                dl.AddCircleFilled(p, 2.3f * s, col, 8);
                dl.AddCircle(p, 2.8f * s, Black, 8, 1f);
                break;
            case RadarKind.Vendor:
                dl.AddCircleFilled(p, 4f * s, col, 12);
                dl.AddCircle(p, 4f * s, Darken(col, 0.45f), 12, 1.2f);
                dl.AddCircle(p, 2f * s, Darken(col, 0.55f), 8, 1f);
                break;
            case RadarKind.Corpse:
            {
                float r = 3f * s;
                dl.AddLine(p + new Vector2(-r, -r), p + new Vector2(r, r), col, 1.5f);
                dl.AddLine(p + new Vector2(-r, r), p + new Vector2(r, -r), col, 1.5f);
                break;
            }
            case RadarKind.OwnCorpse:
            {
                float r = 3.6f * s;
                dl.AddLine(p + new Vector2(-r, -r), p + new Vector2(r, r), Black, 4f);
                dl.AddLine(p + new Vector2(-r, r), p + new Vector2(r, -r), Black, 4f);
                dl.AddLine(p + new Vector2(-r, -r), p + new Vector2(r, r), col, 2.2f);
                dl.AddLine(p + new Vector2(-r, r), p + new Vector2(r, -r), col, 2.2f);
                break;
            }
            case RadarKind.Lifestone:
                dl.AddCircle(p, 3.8f * s, col, 12, 2f);
                dl.AddCircleFilled(p, 1.2f * s, col, 6);
                break;
            case RadarKind.GroundItem:
            {
                var h = new Vector2(2f * s, 2f * s);
                dl.AddRectFilled(p - h, p + h, col);
                dl.AddRect(p - h - Vector2.One, p + h + Vector2.One, Black);
                break;
            }
        }
    }

    /// <summary>Half the marker's width at <c>s</c> = 1, in pixels (where a name label starts, how big a hit is).</summary>
    public static float Radius(byte kind) => kind switch
    {
        RadarKind.Monster or RadarKind.Npc => 3f,
        RadarKind.Portal => 4f,
        RadarKind.Door => 3.5f,
        RadarKind.Player => 4.5f,
        RadarKind.Fellow => 4.8f,
        RadarKind.Pet => 2.8f,
        RadarKind.Vendor => 4f,
        RadarKind.Corpse => 3f,
        RadarKind.OwnCorpse => 3.6f,
        RadarKind.Lifestone => 3.8f,
        _ => 3f,
    };

    private const int MaxNameChars = 24;
    // Clipped names, so a long one costs no string per frame. Cleared when it grows large.
    private static readonly Dictionary<string, string> Clipped = new();
    private static readonly uint NameOutline = RynthTheme.Argb(0xE6000000);

    /// <summary>
    /// Draws <paramref name="name"/> to the right of a marker centred on <paramref name="p"/>
    /// (<paramref name="radius"/> = its drawn half-width), in <paramref name="col"/> with a dark outline so it
    /// reads on floors and walls alike. <paramref name="sizePx"/> is the text size at 100% UI scale:
    /// <paramref name="font"/> (baked at the UI scale) is stretched to it, so it follows the UI scale too.
    /// Names longer than 24 characters are cut with an ellipsis. On the round radar (<paramref name="mask"/>) a name that
    /// runs past the circle is cut off at its edge.
    /// </summary>
    public static void DrawName(ImDrawListPtr dl, ImFontPtr font, Vector2 p, float radius, uint col, string? name, float sizePx,
        RoundMask? mask = null)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (name.Length > MaxNameChars)
        {
            if (!Clipped.TryGetValue(name, out string? cut))
            {
                if (Clipped.Count > 512) Clipped.Clear();
                cut = name[..MaxNameChars] + "…";
                Clipped[name] = cut;
            }
            name = cut;
        }
        // The font is baked for 11 px text at the UI scale: its FontSize / 11 is that scale.
        float fs = sizePx * (font.FontSize / 11f);
        ImGuiNET.ImGui.PushFont(font);
        Vector2 ts = ImGuiNET.ImGui.CalcTextSize(name) * (fs / font.FontSize);
        ImGuiNET.ImGui.PopFont();
        var at = new Vector2(p.X + radius + 3f, p.Y - ts.Y * 0.5f);
        // Round radar, and the name runs past the circle: it is cut off at the circle's edge. ImGui clips to
        // rectangles, so the cut is a vertical edge at the circle's width on the text's farthest row from the
        // centre (the text is a line high, so the edge is within a pixel or two of the circle).
        bool clipped = false;
        if (mask is RoundMask round && !round.ContainsBox(at, at + ts))
        {
            float dy = MathF.Max(MathF.Abs(at.Y - round.Centre.Y), MathF.Abs(at.Y + ts.Y - round.Centre.Y));
            float half = round.Radius * round.Radius - dy * dy;
            if (half <= 0f) return;
            half = MathF.Sqrt(half);
            dl.PushClipRect(new Vector2(round.Centre.X - half, at.Y - 2f), new Vector2(round.Centre.X + half, at.Y + ts.Y + 2f), true);
            clipped = true;
        }
        dl.AddText(font, fs, at + new Vector2(-1, 0), NameOutline, name);
        dl.AddText(font, fs, at + new Vector2(1, 0), NameOutline, name);
        dl.AddText(font, fs, at + new Vector2(0, -1), NameOutline, name);
        dl.AddText(font, fs, at + new Vector2(0, 1), NameOutline, name);
        dl.AddText(font, fs, at, col, name);
        if (clipped) dl.PopClipRect();
    }

    /// <summary>A kind's colour for its name: the marker's colour, fully opaque.</summary>
    public static uint NameColor(uint markerCol) => markerCol | 0xFF000000u;

    /// <summary>An ImGui colour (ABGR) with its RGB scaled by <paramref name="k"/>, alpha kept.</summary>
    public static uint Darken(uint col, float k)
    {
        uint r = (uint)((col & 0xFF) * k), g = (uint)(((col >> 8) & 0xFF) * k), b = (uint)(((col >> 16) & 0xFF) * k);
        return (col & 0xFF000000) | (b << 16) | (g << 8) | r;
    }

    /// <summary>Each kind's ImGui colour from the radar settings, refreshed into <paramref name="into"/> (length RadarKind.Count).</summary>
    public static void FillColors(uint[] into)
    {
        for (int k = 0; k < into.Length; k++)
            into[k] = RynthTheme.Argb(RadarSettingsStore.KindColor((byte)k));
    }
}
